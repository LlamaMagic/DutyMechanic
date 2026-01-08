using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
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
/// Lv. 35: The Sunken Temple of Qarn dungeon logic.
/// </summary>
public class SunkenTemplofQarn : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.SunkenTempleofQarn;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.Triclip, EnemyAction.LoomingJudgment];

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheOratoryofTatamefuII,
            innerWidth: 37.0f,
            innerHeight: 29.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Teratotaur],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheAdytumofLalafutoIV,
            innerWidth: 37.0f,
            innerHeight: 29.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.TempleGuardian],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheCameralChamber,
            innerWidth: 35.0f,
            innerHeight: 35.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Adjudicator],
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
            SubZoneId.TheOratoryofTatamefuII => await Teratotaur(),
            SubZoneId.TheAdytumofLalafutoIV => await TempleGuardian(),
            SubZoneId.TheCameralChamber => await Adjudicator(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: Teratotaur.
    /// </summary>
    private async Task<bool> Teratotaur()
    {
        if (Core.Player.HasAura(PlayerAura.Doom))
        {
            // Three Ids of the lit platforms
            var targetNpcIds = new uint[] { 2000866, 2000867, 2000868 };

            var targetObject = GameObjectManager.GetObjectsOfType<EventObject>()
                .Where(eo => targetNpcIds.Contains(eo.NpcId) && eo.IsVisible)
                .FirstOrDefault();

            if (targetObject != null && targetObject.IsValid)
            {
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 10000, "Doom avoid");
                await CommonTasks.MoveTo(targetObject.Location);
                await Coroutine.Sleep(30);
            }

            // follow the NPCs to platform
            // await MovementHelpers.GetClosestAlly.Follow(useMesh: true, followDistance: 1f);
        }

        return false;
    }

    /// <summary>
    /// Boss 2: Temple Guardian.
    /// </summary>
    private static async Task<bool> TempleGuardian()
    {
        return false;
    }

    /// <summary>
    /// Boss 3: Adjudicator.
    /// </summary>
    private static async Task<bool> Adjudicator()
    {
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Teratotaur
        /// </summary>
        public const uint Teratotaur = 1567;

        /// <summary>
        /// Second Boss: Temple Guardian.
        /// </summary>
        public const uint TempleGuardian = 1569;

        /// <summary>
        /// Final Boss: Adjudicator.
        /// </summary>
        public const uint Adjudicator = 1570;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.Teratotaur"/>.
        /// </summary>
        public static readonly Vector3 Teratotaur = new(-72f, -12f, -60f);

        /// <summary>
        /// Second Boss: <see cref="EnemyNpc.TempleGuardian"/>.
        /// </summary>
        public static readonly Vector3 TempleGuardian = new(54f, -50f, -7.5f);

        /// <summary>
        /// Third Boss: <see cref="EnemyNpc.Adjudicator"/>.
        /// </summary>
        public static readonly Vector3 Adjudicator = new(235.5f, -4f, 0f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Teratotaur
        /// Triclip
        /// Tank Buster
        /// </summary>
        public const uint Triclip = 42231;

        /// <summary>
        /// Adjudicator
        /// Looming Judgment
        /// Tank Buster
        /// </summary>
        public const uint LoomingJudgment = 42245;
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Teratotaur
        /// Aura Name: Doom, Aura Id: 1970
        /// Causes instant death if timer reaches 0. Stand on platform to dispel
        /// </summary>
        public const uint Doom = 1970;
    }
}
