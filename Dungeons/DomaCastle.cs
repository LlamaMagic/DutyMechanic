using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 67: Doma Castle dungeon logic.
/// </summary>
public partial class DomaCastle : AbstractDungeon
{
    private static DomaCastle active;
    private bool? savedBossSideStep;
    private bool? boundarySideStep;
    private uint leasedBoss;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.DomaCastle;

    /// <inheritdoc/>
    // Tower occupancy is not equivalent to following the closest ally. The
    // encounter-local positioning handler chooses an unoccupied tower instead.
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        RestoreBoundarySideStep();

        // Rearguard's helper IDs and cast positions were captured on November
        // 11. The encounter reference models 6y circles and 2y rays; retain
        // a 0.5y clearance while native avoidance owns the escape.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory && fireWindows.Count > 0,
            () => ArenaCenter.MagitekRearguard, 60f, _ => 6.5f, point => point,
            () => fireWindows.Values.Select(window => window.Position).ToArray());
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory,
            objectSelector: actor => actor.IsValid && actor.IsCasting &&
                actor.BaseId == 0x1BCE && actor.CastingSpellId == 8352,
            radiusProducer: _ => 6.5f);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.BaseId == 0x1BCF && actor.CastingSpellId == 8353,
            width: 3f, length: 49f, priority: AvoidancePriority.High);

        // Mines also move before their detonation cast. Keep the body and its
        // short forward path separate from the larger cast-time blast; a cast
        // takes over as soon as 8352 begins so the same blast is not duplicated.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory,
            objectSelector: actor => actor.IsValid && actor.IsVisible && actor.BaseId == 0x1BCE && actor.CastingSpellId != 8352,
            radiusProducer: _ => 2.5f);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory,
            objectSelector: actor => actor.IsValid && actor.IsVisible && actor.BaseId == 0x1BCE && actor.CastingSpellId != 8352,
            width: 3f, length: 8.5f, yOffset: 2f, priority: AvoidancePriority.High);

        // The boss-centered Circle of Death is separate from the positive
        // tower response and can overlap it.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TrainingGrounds &&
                hexCirclePosition.HasValue && System.DateTime.UtcNow < hexCircleUntil,
            () => ArenaCenter.MagitekHexadron, 40f, _ => 8.74f, point => point,
            () => hexCirclePosition.HasValue ? new[] { hexCirclePosition.Value } : System.Array.Empty<Vector3>());

        // The November 11 capture exposed tether 60 on one end of each pair.
        // Register only that source, preventing the old opposite-end duplicate
        // from closing otherwise safe lanes near a tower.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TrainingGrounds,
            objectSelector: bc => bc.BaseId == 0x1BD2 && HasChainTether(bc),
            width: 4.6f,
            length: 55f,
            yOffset: -3f,
            priority: AvoidancePriority.High);

        // The final boss has the same tethered lane mechanic, but no legacy
        // coverage. The reference gives a 3y-wide lane; retain 0.5y per side.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HalloftheScarletSwallow,
            objectSelector: bc => bc.BaseId == 0x1BD6 && HasChainTether(bc),
            width: 4f, length: 55f, yOffset: -3f, priority: AvoidancePriority.High);

        // Delay-action Charge marks individual players for a six-yalm spread.
        // The first capture placed Rowan 4.3y from an ally at resolution and
        // recorded two hits. Avoid the marked ally, or every ally when Rowan
        // carries the marker; native escape preempts charge positioning.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HalloftheScarletSwallow,
            objectSelector: actor => actor.IsValid && actor.IsAlive && actor.ObjectId != Core.Me.ObjectId &&
                PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId) &&
                (HasSpreadMarker(Core.Me) || HasSpreadMarker(actor)),
            radiusProducer: _ => 6.5f);

        // Gunsaw re-aimed after each hit in the November 11 capture; its
        // sustained bait-and-move response is owned by UpdatePositioning.

        // Boss 3 Clean Cut
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HalloftheScarletSwallow,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.CleanCut,
            // Source width is 8y; add 0.5y clearance and the full crossing.
            // The baseline's edge damage also overlapped a party spread, so
            // it does not independently establish that this line caused the hit.
            width: 9f,
            length: 52f,
            yOffset: -1f,
            priority: AvoidancePriority.High);

        // Boss Arenas

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory,
            () => ArenaCenter.MagitekRearguard,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        // The second and third arenas are square; a circular leash removes
        // their corners, including useful charge escape positions.
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TrainingGrounds,
            39f, 39f, 180f, 180f,
            () => new[] { ArenaCenter.MagitekHexadron },
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HalloftheScarletSwallow,
            39f, 39f, 180f, 180f,
            () => new[] { ArenaCenter.HypertunedGrynewaht },
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        UpdateBossSideStep();
        // Positioning is pulsed separately so a yielding profile hook cannot
        // prevent native-avoid handoff or stop/death cleanup.
        return Task.FromResult(false);
    }

    private void UpdateBossSideStep()
    {
        uint bossId = WorldManager.SubZoneId switch
        {
            (uint)SubZoneId.TheThirdArmory => EnemyNpc.MagitekRearguardBase,
            (uint)SubZoneId.TrainingGrounds => EnemyNpc.MagitekHexadronBase,
            (uint)SubZoneId.HalloftheScarletSwallow => EnemyNpc.HypertunedGrynewahtBase,
            _ => 0
        };

        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat ||
            bossId == 0 || (leasedBoss != 0 && leasedBoss != bossId))
        {
            ReleaseBossSideStep();
            return;
        }

        // The October 21 capture shows helpers sharing the boss NPC ID and name;
        // the base ID distinguishes the boss before acquiring the lease. Once
        // acquired, keep it across temporary disappearance or untargetability.
        if (leasedBoss == 0 && !GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.BaseId == bossId))
            return;

        if (savedBossSideStep == null && SidestepPlugin != null)
        {
            savedBossSideStep = SidestepPlugin.Enabled;
            leasedBoss = bossId;
            SidestepPlugin.Enabled = false;
            ff14bot.Helpers.Logging.WriteDiagnostic("[Doma] Boss {0}: SideStep suspended.", bossId);
        }
    }

    private static bool HasSpreadMarker(BattleCharacter actor) => actor != null && actor.IsValid &&
        actor.VfxContainer.IsValid && actor.VfxContainer.LockOns.Any(marker =>
            marker != null && marker.IsValid && marker.Id == 99);

    private static bool HasChainTether(BattleCharacter actor) => actor != null && actor.IsValid &&
        actor.VfxContainer.IsValid && (actor.VfxContainer.Tethers?.Any(tether => tether.Id == 60) ?? false);

    private void ReleaseBossSideStep()
    {
        if (savedBossSideStep == null) return;
        // Restore only the state borrowed by this encounter; a later explicit enable
        // must not be overwritten by a stale release.
        if (SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = savedBossSideStep.Value;
        savedBossSideStep = null;
        leasedBoss = 0;
        ff14bot.Helpers.Logging.WriteDiagnostic("[Doma] Boss SideStep lease released.");
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        if (ReferenceEquals(active, this)) ResetActive();
        RestoreBoundarySideStep();
        return Task.FromResult(false);
    }

    // The shared base enables SideStep at both dungeon boundaries. The manager
    // snapshots Doma's current state immediately before that call so entry and
    // exit do not overwrite a user's setting or a change made after a boss.
    internal void PrepareBoundary(bool leaving)
    {
        if (leaving) ReleaseBossSideStep();
        boundarySideStep = SidestepPlugin?.Enabled;
    }

    private void RestoreBoundarySideStep()
    {
        if (boundarySideStep.HasValue && SidestepPlugin != null)
            SidestepPlugin.Enabled = boundarySideStep.Value;
        boundarySideStep = null;
    }

    /// <summary>Releases the boss plugin lease when a stop bypasses dungeon exit.</summary>
    public static void ResetActive()
    {
        if (active == null) return;
        active.ResetPositioning();
        active.ReleaseBossSideStep();
        active = null;
    }

    /// <summary>Releases the lease when a yielded mechanic prevents the dungeon hook from running.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        active.UpdatePositioning();
        if (active.savedBossSideStep == null) return;
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            WorldManager.ZoneId != (uint)Data.ZoneId.DomaCastle || Core.Me == null ||
            !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
            active.ReleaseBossSideStep();
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Magitek Rearguard.
        /// </summary>
        public const uint MagitekRearguardBase = 0x1BCC;

        /// <summary>
        /// Second Boss: Magitek Hexadron.
        /// </summary>
        public const uint MagitekHexadronBase = 0x1BD0;

        /// <summary>
        /// Final Boss: Hypertuned Grynewaht.
        /// </summary>
        public const uint HypertunedGrynewahtBase = 0x1BD4;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Magitek Rearguard.
        /// </summary>
        public static readonly Vector3 MagitekRearguard = new(125f, 40.5f, 17.5f);

        /// <summary>
        /// Second Boss: Magitek Hexadron.
        /// </summary>
        public static readonly Vector3 MagitekHexadron = new(-240f, 45.5f, 130.5f);

        /// <summary>
        /// Third Boss: Hypertuned Grynewaht.
        /// </summary>
        public static readonly Vector3 HypertunedGrynewaht = new(-240f, 67f, -198f);

    }

    private static class EnemyAction
    {
        /// <summary>
        /// Hypertuned Grynewaht
        /// Clean Cut
        /// Straight line avoid
        /// </summary>
        public const uint CleanCut = 8369;
    }

}
