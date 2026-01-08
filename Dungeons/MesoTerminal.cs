using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Yuweyawata Field Station dungeon logic.
/// </summary>
public class MesoTerminal : AbstractDungeon
{
    /// <summary>
    /// Spell IDs for Interject and Head Graze.
    /// </summary>
    private readonly uint interject = 7538;

    private readonly uint headGraze = 7551;

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.MesoTerminal;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
    [
        EnemyAction.MemoryOfTheStorm,
        EnemyAction.SensoryDeprivation,
        EnemyAction.SterileSphereLeft,
        EnemyAction.SterileSphereRight,
        EnemyAction.Keraunography,
        EnemyAction.Electray,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.ConcentratedDose, EnemyAction.RelentlessTorment, EnemyAction.MemoryOfThePyre,];

    private static readonly Dictionary<ClassJobType, uint> TankInvul = new()
    {
        { ClassJobType.Warrior, 43 }, // Holmgang
        { ClassJobType.Paladin, 30 }, // Hallowed Ground
        { ClassJobType.DarkKnight, 3638 }, // Living Dead
        { ClassJobType.Gunbreaker, 16152 }, // Superbolide
    };

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.NonvolatileMemory && EnemyAction.Impression1.IsCasting(),
            () => MovementHelpers.GetClosestAlly.Location,
            outerRadius: 40.0f,
            innerRadius: 1.0f,
            priority: AvoidancePriority.High);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TriageModule,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.ChirurgeonGeneral],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PublicForum && !Core.Player.HasAura(PlayerAura.CellBlockβ),
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.HoodedHeadsmen],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PublicForum && Core.Player.HasAura(PlayerAura.CellBlockβ),
            () => ArenaCenter.CellBlockβ,
            outerRadius: 90.0f,
            innerRadius: 7.5f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.NonvolatileMemory,
            innerWidth: 38.0f,
            innerHeight: 38.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.ImmortalRemains],
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
            SubZoneId.TriageModule => await ChirurgeonGeneral(),
            SubZoneId.PublicForum => await HoodedHeadsmen(),
            SubZoneId.NonvolatileMemory => await ImmortalRemains(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: Chirurgeon General.
    /// </summary>
    private static async Task<bool> ChirurgeonGeneral()
    {
        if (Core.Me.HasAura(PlayerAura.Insensible))
        {
            await MovementHelpers.GetClosestAlly.Follow();
        }

        return false;
    }

    /// <summary>
    /// Boss 2: Hooded Headsmen.
    /// </summary>
    private async Task<bool> HoodedHeadsmen()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => bc.CastingSpellId == EnemyAction.WillBreaker && bc.TargetCharacter == Core.Me);

        if (caster != null)
        {
            if (ActionManager.CanCast(interject, caster))
            {
                SpellData action = DataManager.GetSpellData(interject);
                Logger.Information($"Casting {action.Name} ({action.Id}) on {caster.Name}");
                ActionManager.DoAction(action, Core.Me.CurrentTarget);
                await Coroutine.Sleep(1_500);
            }

            if (ActionManager.CanCast(headGraze, caster))
            {
                SpellData action = DataManager.GetSpellData(headGraze);
                Logger.Information($"Casting {action.Name} ({action.Id} on {caster.Name})");
                ActionManager.DoAction(action, caster);
                await Coroutine.Sleep(1_500);
            }
        }

        return false;
    }

    /// <summary>
    /// Boss 3: Immortal Remains.
    /// </summary>
    private static async Task<bool> ImmortalRemains()
    {
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Chirurgeon General
        /// </summary>
        public const uint ChirurgeonGeneral = 13970;

        /// <summary>
        /// Second Boss: Hooded Headsmen
        /// </summary>
        public const uint HoodedHeadsmen = 0;

        /// <summary>
        /// Final Boss: Immortal Remains
        /// </summary>
        public const uint ImmortalRemains = 13974;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Chirurgeon General
        /// </summary>
        public static readonly Vector3 ChirurgeonGeneral = new(270f, -582.5f, 12f);

        /// <summary>
        /// Second Boss: Hooded Headsmen
        /// </summary>
        public static readonly Vector3 HoodedHeadsmen = new(60f, -490f, -258f);

        /// <summary>
        /// Second Boss: Cell Block B
        /// </summary>
        public static readonly Vector3 CellBlockβ = new(60f, -490f, -248f);

        /// <summary>
        /// Final Boss: Immortal Remains
        /// </summary>
        public static readonly Vector3 ImmortalRemains = new(0f, 320f, 0f);

        /// <summary>
        /// Final Boss: Impression Safe Spot
        /// </summary>
        public static readonly Vector3 ImpressionSafeSpot = new(-10.8f, 320f, 7.5f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Chirurgeon General
        /// Sterile Sphere (43805)
        /// </summary>
        public const uint SterileSphereLeft = 43805;

        /// <summary>
        /// Chirurgeon General
        /// Sterile Sphere (43806)
        /// </summary>
        public const uint SterileSphereRight = 43806;

        /// <summary>
        /// Chirurgeon General
        /// Sensory Deprivation (43797)
        /// </summary>
        public const uint SensoryDeprivation = 43797;

        /// <summary>
        /// Chirurgeon General
        /// Concentrated Dose (43799) - Tank Buster
        /// </summary>
        public const uint ConcentratedDose = 43799;

        /// <summary>
        /// Pale Headsman
        /// Relentless Torment (43589) - Tank Buster
        /// </summary>
        public const uint RelentlessTorment = 43589;

        /// <summary>
        /// Pale Headsman
        /// Will Breaker (44856) - Interrupt
        /// </summary>
        public const uint WillBreaker = 44856;

        /// <summary>
        /// Immortal Remains
        /// Impression (43818 / 43819) - Pushback
        /// </summary>
        public static readonly HashSet<uint> Impression1 = [43818];

        public const uint Impression2 = 43819;

        /// <summary>
        /// Immortal Remains
        /// Memory of the Storm (43821) - Stack
        /// </summary>
        public const uint MemoryOfTheStorm = 43821;

        /// <summary>
        /// Immortal Remains
        /// Memory of the Pyre (43823) - Tank Buster
        /// </summary>
        public const uint MemoryOfThePyre = 43823;

        /// <summary>
        /// Immortal Remains
        /// Keraunography (45176)
        /// </summary>
        public const uint Keraunography = 45176;

        /// <summary>
        /// Bygone Aerostat
        /// Electray (43810) - Line AoE
        /// </summary>
        public const uint Electray = 43810;
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Chirurgeon General
        /// Aura Name: Insensible, Aura Id: 4553
        /// Can't see telegraphs of attacks
        /// </summary>
        public const uint Insensible = 4553;

        /// <summary>
        /// Hooded Headsmen
        /// Aura Name: Cell Block β, Aura Id: 4543
        /// Restricts movement to small section of the arena.
        /// </summary>
        public const uint CellBlockβ = 4543;
    }
}
