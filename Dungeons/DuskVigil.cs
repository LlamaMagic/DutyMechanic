using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LlamaLibrary.Helpers;
using System.Linq;
using System.Windows.Media;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 51: The Dusk Vigil dungeon logic.
/// </summary>
public class DuskVigil : AbstractDungeon
{
    private static BattleCharacter whirlingGoal => GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.WhirlingGaol).OrderBy(bc => bc.Distance()).FirstOrDefault(bc => bc.IsVisible);
    private static int whirlingGoalDuration = 25000;
    public static readonly LlamaLibrary.Logging.LLogger Log = new("Dusk Vigil", Colors.LimeGreen);

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.DuskVigil;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.WindsofWinter,EnemyAction.WhirlingGaol];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.TrunkTawse, EnemyAction.Skullsplinter, EnemyAction.GoldenTalons];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        SideStep.Override(EnemyAction.DeathSpiral);

        // Boss 2: Death Spiral
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.LordCommandersSeat,
            objectSelector: c => c.CastingSpellId == EnemyAction.DeathSpiral,
            outerRadius: 40.0f,
            innerRadius: 5.0F,
            priority: AvoidancePriority.Medium);

        // Boss 3: Freefall
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.SaintGuenriolsChapel,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.Freefall && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GatheringHall,
            innerWidth: 38.0f,
            innerHeight: 38.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.ToweringOliphant],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.LordCommandersSeat,
            innerWidth: 38.0f,
            innerHeight: 38.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.SerYuhelmeric],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SaintGuenriolsChapel,
            () => ArenaCenter.Opinicus,
            outerRadius: 90.0f,
            innerRadius: 19f,
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();
        await TankBusterSpells();
        await DamageMitigationSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        bool result = currentSubZoneId switch
        {
            SubZoneId.GatheringHall => await ToweringOliphant(),
            SubZoneId.LordCommandersSeat => await SerYuhelmeric(),
            SubZoneId.SaintGuenriolsChapel => await Opinicus(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: Towering Oliphant.
    /// </summary>
    private static async Task<bool> ToweringOliphant()
    {
        return false;
    }

    /// <summary>
    /// Boss 2: Ser Yuhelmeric.
    /// </summary>
    private static async Task<bool> SerYuhelmeric()
    {
        return false;
    }

    /// <summary>
    /// Boss 3: Opinicus
    /// </summary>
    private async Task<bool> Opinicus()
    {
        if (whirlingGoal != null)
        {
            await MovementHelpers.GetClosestAlly.Follow(3f, whirlingGoalDuration);
        }

        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Towering Oliphant
        /// </summary>
        public const uint ToweringOliphant = 3405;

        /// <summary>
        /// Second Boss: Ser Yuhelmeric
        /// </summary>
        public const uint SerYuhelmeric = 3406;

        /// <summary>
        /// Final Boss: Opinicus
        /// </summary>
        public const uint Opinicus = 3409;

        /// <summary>
        /// Final Boss: WhirlingGaol
        /// </summary>
        public const uint WhirlingGaol = 4381;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.ToweringOliphant"/>.
        /// </summary>
        public static readonly Vector3 ToweringOliphant = new(0f, 0f, 0f);

        /// <summary>
        /// Second Boss: <see cref="EnemyNpc.SerYuhelmeric"/>.
        /// </summary>
        public static readonly Vector3 SerYuhelmeric = new(191f, -8f, -120f);

        /// <summary>
        /// Third Boss: <see cref="EnemyNpc.Opinicus"/>.
        /// </summary>
        public static readonly Vector3 Opinicus = new(-70f, 32f, -388f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Towering Oliphant
        /// Trunk Tawse
        /// Tankbuster
        /// </summary>
        public const uint TrunkTawse = 3670;

        /// <summary>
        /// Ser Yuhelmeric
        /// Skullsplinter
        /// Tankbuster
        /// </summary>
        public const uint Skullsplinter = 3677;

        /// <summary>
        /// Ser Yuhelmeric
        /// Death Spiral
        /// Donut AoE, stand close
        /// </summary>
        public const uint DeathSpiral = 3680;

        /// <summary>
        /// Opinicus
        /// Golden Talons
        /// Tankbuster
        /// </summary>
        public const uint GoldenTalons = 3692;

        /// <summary>
        /// Opinicus
        /// Whirling Gaol
        /// Need to hide behind rock during this phase
        /// </summary>
        public const uint WhirlingGaol = 3695;

        /// <summary>
        /// Opinicus
        /// Winds of Winter
        /// Need to hide behind rock during this phase
        /// </summary>
        public const uint WindsofWinter = 3696;

        /// <summary>
        /// Opinicus
        /// Freefall
        ///
        /// </summary>
        public const uint Freefall = 3693;
    }

    private static class PlayerAura
    {
    }
}
