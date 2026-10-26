using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 80.6: Paglth'an dungeon logic.
/// </summary>
public class Paglthan : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Paglthan;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.AkhMorn];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        // Amhuluk
        // Ball of Levin
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GatheringRing, objectSelector: bc => bc.NpcId == EnemyNpc.BallofLevin1 && bc.IsVisible, radiusProducer: bc => 5.8f, priority: AvoidancePriority.High));
        // Supercharged Ball of Levin
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GatheringRing, objectSelector: bc => bc.NpcId == EnemyNpc.SuperchargedBallofLevin1 && bc.IsVisible, radiusProducer: bc => 10.5f, priority: AvoidancePriority.High));
        // Move away from the lamp post after placing lightening rod
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(condition: () => Core.Player.InCombat && !Core.Me.HasAura(PlayerAura.LighteningRod) && WorldManager.SubZoneId == (uint)SubZoneId.GatheringRing, objectSelector: bc => bc.NpcId == EnemyNpc.LampPost && bc.IsVisible, radiusProducer: bc => 10.0f, priority: AvoidancePriority.High));
        // Magitek Fortress
        // Lazer
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ScalekinPen, objectSelector: bc => bc.CastingSpellId == EnemyAction.Lazer, width: 10f, length: 40f, xOffset: 0f, yOffset: 0f, priority: AvoidancePriority.High);
        // Dodge missles
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ScalekinPen, objectSelector: bc => bc.NpcId == EnemyNpc.MagitekMissile && bc.IsVisible, width: 2f, length: 10f, xOffset: 0f, yOffset: 0f, priority: AvoidancePriority.High);
        // Dodge Fire
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ScalekinPen, objectSelector: bc => bc.CastingSpellId == EnemyAction.Lazer, width: 5f, length: 40f, xOffset: 0f, yOffset: 0f, priority: AvoidancePriority.High);
        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(() => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GatheringRing, () => ArenaCenter.Amhuluk, outerRadius: 90.0f, innerRadius: 19.0f, priority: AvoidancePriority.High);
        // Removed the arena donut for second boss as it caused issues when moving to the second level.
        AvoidanceHelpers.AddAvoidDonut(() => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Sunseat, () => ArenaCenter.LunarBahamut, outerRadius: 90.0f, innerRadius: 19.0f, priority: AvoidancePriority.High);
        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (CommonBehaviors.IsLoading || QuestLogManager.InCutscene || WorldManager.ZoneId != 938 || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
        {
            ReleasePositioning();
            return false;
        }

        activePositioning = this;
        // The final boss's existing stack owner has exclusive control there.
        // Release earlier transport/rod input before entering that coroutine.
        if (WorldManager.SubZoneId == (uint)SubZoneId.Sunseat)
        {
            ReleasePositioning();
            await FollowDodgeSpells();
            return false;
        }

        if (WorldManager.SubZoneId == (uint)SubZoneId.GatheringRing && Core.Me.HasAura(PlayerAura.LighteningRod))
        {
            // Keep the existing rod selector. Yield when the actor is missing so recovery and combat remain schedulable.
            var rod = GameObjectManager.GetObjectsByNPCId<GameObject>(EnemyNpc.LampPost).LastOrDefault(a => a.IsValid && a.IsVisible);
            if (rod != null)
            {
                return PositionAt(rod.Location, false, "Lightning rod transfer");
            }
        }
        else if (WorldManager.SubZoneId == (uint)SubZoneId.ScalekinPen)
        {
            var core = GameObjectManager.GetObjectsByNPCId<GameObject>(EnemyNpc.MagitekCore).Where(a => a.IsValid && a.CurrentHealth > 0).OrderBy(a => a.Distance2D()).FirstOrDefault();
            // The existing threshold separates groundY-25 from upperY-18;
            // release during ascent instead of continuing to command the old pad.
            if (core != null && core.IsTargetable && Core.Me.Y < -20)
            {
                // Existing launch point and direct approach are retained. The
                // server owns ascent; this owner releases as soon as Y changes.
                return PositionAt(new Vector3(-175.1252f, -25.00018f, 28.52025f), true, "Magitek core launch");
            }
        }

        ReleasePositioning();
        return false;
    }

    // Replace the two unbounded movement loops with one tick-scoped owner. Both
    // actor disappearance and ordinary death/loading/stop cleanup remain schedulable;
    // arrival holds permit combat/healing while waiting for the game transition.
    private static Paglthan activePositioning;
    private readonly CapabilityManagerHandle positioningHandle = CapabilityManager.CreateNewHandle();
    private bool positioningMoving, positioningLeased;
    /// <summary>Releases Paglthan's rod/lift input before plugin disable, stop or hook replacement.</summary>
    public static void ResetPositioning()
    {
        activePositioning?.ReleasePositioning();
        activePositioning = null;
    }

    /// <summary>Releases stale positive movement when death, loading or departure bypasses combat scheduling.</summary>
    public static void CheckPositioningLifecycle()
    {
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene || WorldManager.ZoneId != 938 || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
        {
            ResetPositioning();
        }
    }

    private bool PositionAt(Vector3 destination, bool direct, string reason)
    {
        CapabilityManager.Update(positioningHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        positioningLeased = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            positioningMoving = false;
            return false;
        }

        // Retain the previous1y contact tolerance; a0.7y mesh goal leaves arrival
        // slack without requiring another movement command once at the trigger.
        if (Core.Me.Location.Distance2D(destination) <= 1)
        {
            StopPositioningMovement();
            return false;
        }

        if (direct)
        {
            Navigator.PlayerMover.MoveTowards(destination);
        }
        else
        {
            Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = .7f, UseMount = false });
        }

        positioningMoving = true;
        return true;
    }

    private void StopPositioningMovement()
    {
        if (positioningMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        positioningMoving = false;
    }

    private void ReleasePositioning()
    {
        StopPositioningMovement();
        if (positioningLeased)
        {
            CapabilityManager.Clear(positioningHandle, CapabilityFlags.Movement, "Rod/lift ownership ended");
        }

        positioningLeased = false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Amhuluk.
        /// </summary>
        public const uint DemonTome = 10075;
        /// <summary>
        /// First Boss: Amhuluk.
        /// </summary>
        public const uint LampPost = 108;
        /// <summary>
        /// First Boss: Ball of Levin.
        /// </summary>
        public const uint BallofLevin1 = 10065;
        /// <summary>
        /// First Boss: Supercharged Ball of Levin.
        /// </summary>
        public const uint SuperchargedBallofLevin1 = 10066;
        /// <summary>
        /// Second Boss: Magitek Core.
        /// </summary>
        public const uint MagitekCore = 10076;
        /// <summary>
        /// Second Boss: Magitek Missile.
        /// </summary>
        public const uint MagitekMissile = 10073;
        /// <summary>
        /// Third Boss: The Everliving Bibliotaph.
        /// </summary>
        public const uint TheEverlivingBibliotaph = 3930;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Amhuluk.
        /// </summary>
        public static readonly Vector3 Amhuluk = new(-520.0377f, -0.02984518f, 144.9744f);
        /// <summary>
        /// Second Boss: Magitek Fortress.
        /// </summary>
        public static readonly Vector3 MagitekFortress = new(-175.324f, -25.00018f, 41.64988f);
        /// <summary>
        /// Second Boss: Magitek Fortress.
        /// </summary>
        public static readonly Vector3 MagitekFortressUpper = new(-175f, -18f, 8.5f);
        /// <summary>
        /// Third Boss: Lunar Bahamut.
        /// </summary>
        public static readonly Vector3 LunarBahamut = new(799f, -57f, -99f);
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Stunned by pseudo-cutscene.
        /// </summary>
        public const uint LighteningRod = 2574;
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Amhuluk
        /// Lightning Bolt
        /// Very wide AOE around target
        /// </summary>
        public const uint LightningBolt = 23627;
        /// <summary>
        /// Magitek Fortress
        /// Lazer
        /// Line AOE
        /// </summary>
        public const uint Lazer = 966;
        /// <summary>
        /// Magitek Fortress
        /// Fire
        /// Line AOE
        /// </summary>
        public const uint Fire = 24846;
        /// <summary>
        /// Lunar Bahamut
        /// Akh Morn
        /// Stack
        /// </summary>
        public const uint AkhMorn = 23381;
    }
}
