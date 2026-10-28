using DutyMechanic.Data;
using DutyMechanic.Helpers;
using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns Copperbell boss hazards through native avoidance while OrderBot retains
/// lifts, firesand and targeting and the routine retains combat execution.
/// </summary>
public class CopperbellMines : AbstractDungeon
{
    // Global2026.10.16 action rows identify the post-rework encounters. Helper
    // action IDs are distinct from visual jumps/divide, which must add no hazard.
    private const uint Kottos = 548, Ire = 554, Gyges = 101;
    // The circular Kottos floor is centered on the repeated Leap layout and has
    // a15y wall. The2026-10-28 healer capture selected escape goals west of that
    // wall and took two Leap hits. Keep0.5y clearance so native avoidance searches
    // the reachable opposite-side pocket instead of chasing exterior safe points.
    private static readonly Vector3 KottosArenaCenter = new(43f, -9.934f, -89.56f);
    private static CopperbellMines active;
    private bool? savedSideStepEnabled;

    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1038 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter actor, uint action) => actor.IsValid && actor.IsAlive &&
        actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == action;

    /// <summary>Restores the previous SideStep state on stop, disable or hook replacement.</summary>
    public static void ResetActive()
    {
        active?.SetBossOwnership(false);
        active = null;
    }

    /// <summary>Maintains exclusive boss ownership even while profile interaction waits yield.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        if (!InDuty()) ResetActive();
        else active.UpdateOwnership();
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.CopperbellMines;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    // Kottos: Grand Slam; Ichorous Ire: Fluid Spread; Gyges: Grand Slam.
    // These are single-target hits, not telegraphs to escape or party-wide damage.
    protected override HashSet<uint> SpellsToTankBust { get; } = [28545, 28461, 28764];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        SetBossOwnership(false);
        active = this;
        // Helpers can be invisible/untargetable. Select by validated cast ID rather
        // than targetability, and give all simultaneous circles to the same native
        // avoidance owner so Leap, Divide and Smash/Blast cannot issue competing moves.
        AddCircle(28544, 12.5f, false); // Lumbering Leap helper, 12y + 0.5y.
        AddCircle(28462, 4.5f, true);   // Syrup placed circle, 4y + 0.5y.
        AddCircle(28465, 8.5f, false);  // Ichorous Drip Burst, 8y + 0.5y.
        AddCircle(28760, 10.5f, true);  // Gigantic Smash placed center, 10y + 0.5y.
        // Blast resolves three seconds after Smash. The2026-10-28 healer capture
        // showed native union escape crossing the still-live center to flee a
        // later helper, taking Smash damage. Keep only the earlier circle active
        // until its cast ends, then hand the same native owner the helper union.
        // The captured center escape is about11y away (<2s travel), leaving time
        // to enter the now-safe center before Blast; no second mover or NPC follow.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => OwnsHazards() && !GameObjectManager
                .GetObjectsOfType<BattleCharacter>(true, false).Any(actor => Casting(actor, 28760)),
            objectSelector: actor => Casting(actor, 28761),
            radiusProducer: _ => 8.5f,
            locationProducer: actor => actor.Location);
        // Cast-type13 measures these cones without adding combat reach. Preserve
        // 0.5y radial clearance and five degrees per edge on the60-degree damage fan.
        AddCone(28546, 30.5f);
        AddCone(28763, 40.5f);
        // Preserve the previously corrected4y safe center with0.5y clearance.
        // This is an unsafe ring, so native avoidance moves inward without NPC follow.
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: OwnsHazards,
            objectSelector: caster => Casting(caster, 28762),
            outerRadius: 40.5,
            innerRadius: 3.5);

        // This boundary participates in the same native hazard union; it owns no
        // movement. The30y/elevation gate isolates the upper boss room, and the
        // ownership predicate releases it between fights, on stop and on exit.
        // A finite50y exterior covers the failed19–22y escape candidates.
        AvoidanceHelpers.AddAvoidDonut(
            canRun: () => OwnsHazards() && Core.Me.Distance2D(KottosArenaCenter) < 30f &&
                Math.Abs(Core.Me.Y - KottosArenaCenter.Y) < 3f,
            locationProducer: () => KottosArenaCenter,
            outerRadius: 50,
            innerRadius: 14.5);

        return Task.FromResult(false);
    }

    private void AddCircle(uint action, float radius, bool placed)
    {
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => Casting(actor, action), radiusProducer: _ => radius,
            locationProducer: actor => placed ? actor.SpellCastInfo.CastLocation : actor.Location);
    }

    private void AddCone(uint action, float radius)
    {
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => Casting(actor, action), leashPointProducer: () => Core.Me.Location,
            leashRadius: 50f, rotationDegrees: 0f, radius: radius, arcDegrees: 70f);
    }

    private void UpdateOwnership()
    {
        if (!Live()) { SetBossOwnership(false); return; }
        // Retain the lease through Divide/temporary untargetability while combat
        // continues. Corridor trash alone must never suspend SideStep; elevation
        // separates Copperbell's stacked floors and the40y bound covers boss rooms.
        if (savedSideStepEnabled != null) return;
        SetBossOwnership(GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.CanAttack &&
                actor.InCombat && actor.NpcId is Kottos or Ire or Gyges &&
                Math.Abs(actor.Y - Core.Me.Y) < 8 && actor.Distance2D() < 40));
    }

    private void SetBossOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Copperbell] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            Logger.Information("[Copperbell] Boss ownership released; SideStep restored.");
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!InDuty()) { SetBossOwnership(false); return false; }
        active = this;
        UpdateOwnership();
        await TankBusterSpells();
        // Yield to rotation/healing and native avoidance; this handler owns no input.
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
