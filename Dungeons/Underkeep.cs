using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Underkeep dungeon logic.
/// </summary>
public class Underkeep : AbstractDungeon
{
    private readonly Stopwatch CoordinateMarcTimer = new();
    private static readonly int CoordinateMarcDuration = 23_000;

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Underkeep;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.DeterrentPulse];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.ThunderousSlash];

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        // Boss 1: Sedimentary Debris
        // Boss 1: Foundational Debris
        // Boss 2: Electric Excess
        // Boss 3: Hypercharged Light
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.SedimentFunnel or (uint)SubZoneId.ReceivingRoom or (uint)SubZoneId.ChamberofPatience,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.SedimentaryDebris or EnemyAction.FoundationalDebris or EnemyAction.ElectricExcess or EnemyAction.HyperchargedLight && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss 2: Ordered Fire
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ReceivingRoom,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.OrderedFire,
            leashPointProducer: () => ArenaCenter.SoldierS0,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 5.0f);

        // Boss 3: Concurrent Field
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChamberofPatience,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.ConcurrentField,
            leashPointProducer: () => ArenaCenter.ValiaPira,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 45.0f);

        // Boss 3: Electric Field
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChamberofPatience,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.ElectricField,
            leashPointProducer: () => ArenaCenter.ValiaPira,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 45.0f);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SedimentFunnel,
            () => ArenaCenter.Gargant,
            outerRadius: 90.0f,
            innerRadius: 14.2f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ReceivingRoom,
            innerWidth: 37.0f,
            innerHeight: 29.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.SoldierS0],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChamberofPatience,
            innerWidth: 35.0f,
            innerHeight: 35.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.ValiaPira],
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();
        await TankBusterSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        bool result = currentSubZoneId switch
        {
            SubZoneId.SedimentFunnel => await Gargant(),
            SubZoneId.ReceivingRoom => await SoldierS0(),
            SubZoneId.ChamberofPatience => await ValiaPira(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: Gargant.
    /// </summary>
    private static async Task<bool> Gargant()
    {
        return false;
    }

    /// <summary>
    /// Boss 2: Soldier S0.
    /// </summary>
    private static async Task<bool> SoldierS0()
    {
        return false;
    }

    /// <summary>
    /// Boss 3: Valia Pira.
    /// </summary>
    private async Task<bool> ValiaPira()
    {
        if (EnemyAction.CoordinateMarchHash.IsCasting() || CoordinateMarcTimer.IsRunning)
        {
            if (!CoordinateMarcTimer.IsRunning)
            {
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, CoordinateMarcDuration, "Coordinate March Avoid");
                CoordinateMarcTimer.Start();
            }

            if (CoordinateMarcTimer.ElapsedMilliseconds < CoordinateMarcDuration)
            {
                await MovementHelpers.GetClosestMelee.FollowTimed(CoordinateMarcTimer, CoordinateMarcDuration, 0.5f, useMesh: true);
            }

            if (CoordinateMarcTimer.ElapsedMilliseconds >= CoordinateMarcDuration)
            {
                CoordinateMarcTimer.Reset();
            }
        }

        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Gargant
        /// </summary>
        public const uint Gargant = 13753;

        /// <summary>
        /// Second Boss: Soldier S0.
        /// </summary>
        public const uint SoldierS0 = 13757;

        /// <summary>
        /// Final Boss: Valia Pira.
        /// </summary>
        public const uint ValiaPira = 13749;

        /// <summary>
        /// Final Boss: Coordinate Bit.
        /// </summary>
        public const uint CoordinateBit = 13965;

        /// <summary>
        /// Final Boss: Coordinate Turret.
        /// When <see cref="EnemyNpc.CoordinateBit"/> come into contact with Explody Orb it causes an AoE explosion
        /// </summary>
        public const uint CoordinateTurret = 13751;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.Gargant"/>.
        /// </summary>
        public static readonly Vector3 Gargant = new(-248f, -70f, 122f);

        /// <summary>
        /// Second Boss: <see cref="EnemyNpc.SoldierS0"/>.
        /// </summary>
        public static readonly Vector3 SoldierS0 = new(0f, -234f, -182f);

        /// <summary>
        /// Third Boss: <see cref="EnemyNpc.ValiaPira"/>.
        /// </summary>
        public static readonly Vector3 ValiaPira = new(0f, -190f, -330f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Gargant
        /// Sedimentary Debris
        /// Spread
        /// </summary>
        public const uint SedimentaryDebris = 43160;

        /// <summary>
        /// Gargant
        /// Foundational Debris
        /// Spread
        /// </summary>
        public const uint FoundationalDebris = 43161;

        /// <summary>
        /// Soldier S0
        /// Thunderous Slash
        /// Tank Buster
        /// </summary>
        public const uint ThunderousSlash = 43136;

        public static readonly HashSet<uint> ThunderousSlashHash = [43136];

        /// <summary>
        /// Soldier S0
        /// Electric Excess
        /// Spread
        /// </summary>
        public const uint ElectricExcess = 43139;

        /// <summary>
        /// Soldier S0
        /// Ordered Fire
        /// Small AoE cone on target
        /// </summary>
        public const uint OrderedFire = 42573;

        /// <summary>
        /// Valia Pira
        /// Hypercharged Light
        /// Spread
        /// </summary>
        public const uint HyperchargedLight = 42524;

        /// <summary>
        /// Valia Pira
        /// Deterrent Pulse
        /// Stack
        /// </summary>
        public const uint DeterrentPulse = 42540;

        /// <summary>
        /// Valia Pira
        /// Concurrent Field
        /// Small cone avoid
        /// </summary>
        public const uint ConcurrentField = 42521;

        /// <summary>
        /// Valia Pira
        /// Electric Field
        /// Small cone avoid
        /// </summary>
        public const uint ElectricField = 42519;

        /// <summary>
        /// Valia Pira
        /// Coordinate March
        /// Starts the orbs moving
        /// </summary>
        public const uint CoordinateMarch = 42513;
        public static readonly HashSet<uint> CoordinateMarchHash = [42513];

    }

    private static class PlayerAura
    {
    }
}
