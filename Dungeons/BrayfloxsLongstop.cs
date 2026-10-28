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
/// Supplies Brayflox boss geometry to native avoidance. OrderBot owns route and
/// bubble rescue targeting; the combat routine retains actions and mitigation.
/// </summary>
public class BrayfloxsLongstop : AbstractDungeon
{
    private const uint Pelican = 1280, Inferno = 1284, Hellbender = 1286, Aiatar = 1279;
    private static BrayfloxsLongstop active;
    private bool? savedSideStepEnabled;
    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1041 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter actor, uint action) => actor.IsValid && actor.IsAlive &&
        actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == action;

    /// <summary>Restores the captured SideStep setting on stop, disable or replacement.</summary>
    public static void ResetActive()
    {
        active?.SetBossOwnership(false);
        active = null;
    }

    /// <summary>Maintains boss ownership across yielding profile interactions and loading.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        // Release during unavailable native state but keep this instance through
        // a same-duty load. Explicit stop/disable/replacement resets it separately.
        if (!InDuty()) active.SetBossOwnership(false);
        else active.UpdateOwnership();
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.BrayfloxsLongstop;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [28659];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        // October28 corridor captures exposed SideStep deduplication against an
        // inactive AvoidInfo.Collection. Gate selectors as well as canRun: otherwise
        // boss-only982 suppresses the same trash spell after SideStep is restored.
        // Global2026.10.16 rows506/984 are6y plus caster reach. October27
        // captures identify the first cone and the missing omenless second cone.
        // Fixed radii include their observed model reach and0.5y clearance;
        //130 degrees adds five degrees per edge to the120-degree damage fans.
        AddCone(506, 9.7f);
        AddCone(984, 10.1f);
        // Type13 Stagnant Spray includes its full8y range, unlike type3 above.
        AddCone(448, 8.5f);
        // Hellbender's Aiatar arrival is a minor transition hit/knockback without
        // vulnerability. October28 captures and Domestic's review reject a required
        // dodge: the experimental spawn circle displaced combat without preventing
        // the hit. Preserve normal healing/combat and the boss ownership lease.
        AddCircle(982, 8.5f); // Peculiar Light; avoid the magic-vulnerability circle.
        AddCircle(28657, 2.5f); // Helper-centered landing, not parent visual28656.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && IsEarliestBurst(actor), radiusProducer: _ => 10.5f,
            locationProducer: actor => actor.Location);
        // Both Aiatar appearances use a30x8 type12 forward line. These dimensions
        // already include reach. The half-yalm margin is clearance on each side.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && (Casting(actor, 29618) || Casting(actor, 28660)),
            width: 9f, length: 30.5f);
        // Bubble1383 is deliberately absent: it binds a party member and must be
        // destroyed. A body avoid fights melee rescue and was re-added every tick.
        // The short add cone985 still requires live angle/impact capture; do not claim it verified.
        return Task.FromResult(false);
    }

    private void AddCircle(uint action, float radius) =>
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && Casting(actor, action), radiusProducer: _ => radius,
            locationProducer: actor => actor.Location);

    private void AddCone(uint action, float radius) =>
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && Casting(actor, action), leashPointProducer: () => Core.Me.Location,
            leashRadius: 50f, rotationDegrees: 0f, radius: radius, arcDegrees: 130f);

    private static bool IsEarliestBurst(BattleCharacter actor)
    {
        if (!Casting(actor, 28658)) return false;
        // October27 alternating waves start about5s apart. Avoiding both complete
        //10y unions at once can erase the safe pocket. Keep the earliest cast group,
        // then hand the same native mover the next group when the first finishes.
        //1s tolerates same-wave helper discovery skew while separating that5s gap.
        double remaining = actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds;
        return !GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(other => Casting(other, 28658) &&
                other.SpellCastInfo.RemainingCastTime.TotalMilliseconds < remaining - 1000);
    }

    private void UpdateOwnership()
    {
        if (!Live()) { SetBossOwnership(false); return; }
        // Retain through bubble/add and Aiatar's arrival/departure while combat
        // continues. Acquisition needs an engaged named boss on the current floor,
        // so ordinary corridor mobs retain SideStep's normal avoidance.
        if (savedSideStepEnabled != null) return;
        SetBossOwnership(GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.CanAttack &&
                actor.InCombat && actor.NpcId is Pelican or Inferno or Hellbender or Aiatar &&
                Math.Abs(actor.Y - Core.Me.Y) < 8 && actor.Distance2D() < 40));
    }

    private void SetBossOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Brayflox] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            Logger.Information("[Brayflox] Boss ownership released; SideStep restored.");
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!InDuty()) { SetBossOwnership(false); return false; }
        if (!ReferenceEquals(active, this)) return false;
        UpdateOwnership();
        await TankBusterSpells();
        return false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetBossOwnership(false);
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
