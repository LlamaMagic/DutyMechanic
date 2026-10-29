using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns reworked Aery boss geometry and positive-position mechanics. The standalone
/// profile retains route/add targeting; one native-mover adapter arbitrates transfer,
/// knockback staging and stacks without replacing normal combat. docs/Aery.md records
/// implementation rationale and the reviewed coverage limits.
/// </summary>
public partial class Aery : AbstractDungeon
{
    private const uint Rangda = 3452, Gyascutus = 3455, Nidhogg = 3458;
    private static readonly Vector3 RangdaCenter = new(334.9f, 93.996f, -203);
    private static readonly Vector3 GyascutusCenter = new(11.9f, 60, 68);
    private static readonly Vector3 NidhoggCenter = new(34.9f, 148.397f, -267);
    private static Aery active;
    private bool? savedSideStepEnabled;
    private uint encounter;
    private uint ownedBossId;
    private uint stackTarget;
    private DateTime stackUntil;
    private BattleCharacter currentBoss;
    private Vector3? stackDestination;

    // October 14/20 captures distinguish real bosses from same-name 1144/233C helpers.
    // Nidhogg retains ownership during airborne and add phases while still alive.
    private static bool IsBoss(BattleCharacter a) => a.BaseId is 0xEA6 or 0x3970 or 0x39CA;
    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1065 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter a) => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid;
    private Vector3 ArenaCenter => encounter == Rangda ? RangdaCenter : encounter == Gyascutus ? GyascutusCenter : NidhoggCenter;
    // Corroborated floor radii 26/19.5/30, inset half a yalm. Gyascutus knockback
    // landing is checked separately; a boundary alone cannot stage that mechanic.
    private float ArenaRadius => encounter == Rangda ? 25.5f : encounter == Gyascutus ? 19.0f : 29.5f;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheAery;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [30193, 30205]; // Crippling Blow / Scarlet Price.
    /// <inheritdoc/>
    // Both Bellows and Massacre are room-wide damage, not unsafe floor. The routine
    // owns cooldown planning. Instant Touchdown has no invented cast-bar trigger.
    protected override HashSet<uint> SpellsToMitigate { get; } = [31233, 30206, 30207];

    /// <summary>Releases this dungeon's input and capability leases on stop or replacement.</summary>
    public static void ResetActive()
    {
        active?.SetOwnership(false);
        active = null;
    }

    /// <summary>Keeps cleanup and helper discovery active through yielding profile hooks.</summary>
    public static void CheckLifecycle() => active?.Pulse();

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        return Task.FromResult(false);
    }

    private void Pulse()
    {
        if (!Live())
        {
            SetOwnership(false);
            return;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && Math.Abs(a.Y - Core.Me.Y) < 10).ToArray();
        // Nidhogg stages Massacre 57y north of the arena center. A player on the
        // opposite half can be more than 65y from that already-acquired actor, and
        // its rendered visibility may remain false after returning. Retain only
        // that exact living boss within the bounded arena-relative staging region;
        // helpers or a newly discovered hidden boss cannot acquire this lease.
        var boss = actors.FirstOrDefault(a => IsBoss(a) && savedSideStepEnabled != null &&
            a.ObjectId == ownedBossId && (a.Distance2D() < 65 ||
                encounter == Nidhogg && a.BaseId == 0x39CA && a.Location.Distance2D(NidhoggCenter) < 65))
            ?? actors.FirstOrDefault(a => IsBoss(a) && a.Distance2D() < 65 &&
                a.IsVisible && a.IsTargetable && a.CanAttack && a.InCombat);
        if (boss == null)
        {
            SetOwnership(false);
            return;
        }
        if (savedSideStepEnabled != null && encounter != boss.NpcId)
            SetOwnership(false);
        encounter = boss.NpcId;
        ownedBossId = boss.ObjectId;
        currentBoss = boss;
        SetOwnership(true);
        RegisterHazards();
        UpdateInputOwner();
        var now = DateTime.UtcNow;
        UpdateHazards(actors, boss, now);
        if (Casting(boss) && boss.CastingSpellId == ActionId.Stack)
        {
            if (stackTarget != boss.SpellCastInfo.TargetId)
                stackDestination = null;
            stackTarget = boss.SpellCastInfo.TargetId;
            // October20 Horrid Blaze lasts 6.7s and is visible even while the boss's
            // rendered actor is hidden. Use its actual target, with a bounded delivery tail.
            stackUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.35);
        }
        else if (now >= stackUntil || Casting(boss) || stackUntil - now > TimeSpan.FromSeconds(.6))
        {
            stackTarget = 0;
            stackDestination = null;
            if (semanticKind == "stack")
                ReleaseStack();
        }
        if (semanticKind == "transfer" && !HasLightningRod(Core.Me) ||
            semanticKind == "knockback" && (!Casting(boss) || boss.CastingSpellId != ActionId.BodySlam))
            ReleaseStack();
        CaptureUnresolvedSignals(actors, now);
        inputOwner?.CompleteEscape();
    }

    private void SetOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Aery] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled)
        {
            ReleaseStack();
            inputOwner?.Detach();
            inputOwner = null;
            hazards.Clear();
            ReleaseHazardRegistrations();
            stackTarget = 0;
            stackUntil = DateTime.MinValue;
            currentBoss = null;
            ownedBossId = 0;
            transferStatue = 0;
            spreadUntil = DateTime.MinValue;
            spreadCaster = 0;
            stackDestination = null;
            signalState.Clear();
            nextSignalSample = DateTime.MinValue;
            if (savedSideStepEnabled != null)
            {
                if (SidestepPlugin != null)
                    SidestepPlugin.Enabled = savedSideStepEnabled.Value;
                savedSideStepEnabled = null;
                Logger.Information("[Aery] Boss ownership released; SideStep restored.");
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!ReferenceEquals(active, this))
            return false;
        Pulse();
        if (!OwnsHazards())
            return false;
        await TankBusterSpells();
        await DamageMitigationSpells();
        bool positioning = PositionForMechanic();
        // October 29 traces identify both MoveAndStop in-range branches canceling
        // native escape: first during Cauterize, then during Wing/Roar after Massacre.
        // The current input owner therefore retains scheduling priority for the
        // entire native escape lifetime, regardless of the cast or targetability.
        // Native avoidance already pulses independently; do not issue a second
        // destination, duplicate its arrival decision or suppress explicit stops.
        // Its completed escape releases the tree to ordinary healing/combat; prison,
        // forced motion, death and replacement movers cannot inherit this claim.
        return positioning || inputOwner?.HasNativeEscapePriority == true;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetOwnership(false);
        if (ReferenceEquals(active, this))
            active = null;
        return Task.FromResult(false);
    }
}
