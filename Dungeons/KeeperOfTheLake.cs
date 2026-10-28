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
/// Owns Keeper boss geometry and Akh Morn participation. Native avoidance chooses
/// escape paths; the profile owns objectives and the routine owns ordinary combat.
/// </summary>
public partial class KeeperOfTheLake : AbstractDungeon
{
    private const uint Einhander = 3369, Gunship = 3373, Midgardsormr = 3374;
    private static readonly Vector3 EinhanderCenter = new(18.85f, 26.658f, -17f);
    private static readonly Vector3 GunshipCenter = new(8.5f, 346.024f, -150f);
    private static readonly Vector3 MidgardsormrCenter = new(-41f, 641.041f, -78f);
    private static KeeperOfTheLake active;
    private bool? savedSideStepEnabled;
    private uint encounter;
    private uint stackTarget;
    private DateTime stackUntil;
    private Vector3? stackDestination;

    // Actual boss base IDs distinguish persistent owners from same-name hidden helpers.
    // October 27 capture: Midgardsormr becomes untargetable while dragons/rings remain.
    private static bool IsBoss(BattleCharacter a) => a.BaseId is 0x3927 or 0xE68 or 0x392A;
    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1063 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter a) => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid;
    private Vector3 ArenaCenter => encounter == Einhander ? EinhanderCenter : encounter == Gunship ? GunshipCenter : MidgardsormrCenter;
    // Half-yalm inset from the corroborated 20/19/19.5y rooms preserves wall clearance.
    private float ArenaRadius => encounter == Einhander ? 19.5f : encounter == Gunship ? 18.5f : 19f;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheKeeperOfTheLake;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [29620, 29602];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [29273, 29891, 29281, 29283];

    /// <summary>Releases only this encounter's input, capability and SideStep leases.</summary>
    public static void ResetActive()
    {
        active?.SetOwnership(false);
        active = null;
    }

    /// <summary>Maintains geometry and cleanup during yielding profile/combat hooks.</summary>
    public static void CheckLifecycle() => active?.Pulse();

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        RegisterHazards();
        return Task.FromResult(false);
    }

    private void Pulse()
    {
        if (!Live()) { SetOwnership(false); return; }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && Math.Abs(a.Y - Core.Me.Y) < 10).ToArray();
        var boss = actors.FirstOrDefault(a => IsBoss(a) && a.Distance2D() < 48 &&
            (savedSideStepEnabled != null || a.IsTargetable && a.CanAttack && a.InCombat));
        if (boss == null) { SetOwnership(false); return; }
        encounter = boss.NpcId;
        SetOwnership(true);
        RegisterHazards(); // Reacquire after the authored between-fight cleanup hook.
        UpdateInputOwner();
        var now = DateTime.UtcNow;
        UpdateHazards(actors, now);
        if (Casting(boss) && boss.CastingSpellId == 29283)
        {
            if (stackTarget != boss.SpellCastInfo.TargetId) stackDestination = null;
            stackTarget = boss.SpellCastInfo.TargetId;
            // Three instant follow-up hits outlive the initial cast. Retain participation
            // until the next distinct boss cast, with a bounded 12s post-cast fallback.
            // October 28 captures verified participation through the follow-up hits;
            // the lease does not block rotation, healing or emergency native egress.
            stackUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(12);
        }
        else if ((Casting(boss) && boss.CastingSpellId != 29284) || now >= stackUntil)
        {
            stackTarget = 0;
            stackDestination = null;
            ReleaseStack();
        }
        inputOwner?.CompleteEscape();
    }

    private void SetOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Keeper] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled)
        {
            ReleaseStack();
            inputOwner?.Detach();
            inputOwner = null;
            hazards.Clear();
            fireSideRestricted = false;
            ReleaseHazardRegistrations();
            ResetFireTrail();
            stackTarget = 0;
            stackUntil = DateTime.MinValue;
            stackDestination = null;
            if (savedSideStepEnabled != null)
            {
                if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
                savedSideStepEnabled = null;
                Logger.Information("[Keeper] Boss ownership released; SideStep restored.");
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!ReferenceEquals(active, this)) return false;
        Pulse();
        if (!OwnsHazards()) return false;
        await TankBusterSpells();
        await DamageMitigationSpells();
        return PositionForStack();
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetOwnership(false);
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
