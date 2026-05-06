using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LlamaLibrary.Helpers;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 63: Shisui of the Violet Tides dungeon logic.
/// </summary>
public class ShisuioftheVioletTides : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.ShisuioftheVioletTides;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.SharpStrike,EnemyAction.FoulNail];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {

        // Boss Arenas

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HarutsugeGate,
            () => ArenaCenter.Amikiri,
            outerRadius: 90.0f,
            innerRadius: 19f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AkashioHall,
            () => ArenaCenter.RubyPrincess,
            outerRadius: 90.0f,
            innerRadius: 19f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ShisuiGokagura,
            () => ArenaCenter.ShisuiYohi,
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
            SubZoneId.HarutsugeGate => await Amikiri(),
            SubZoneId.AkashioHall => await RubyPrincess(),
            SubZoneId.ShisuiGokagura => await ShisuiYohi(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: Amikiri.
    /// </summary>
    private static async Task<bool> Amikiri()
    {
        return false;
    }

    /// <summary>
    /// Boss 2: Ruby Princess.
    /// </summary>
    private static async Task<bool> RubyPrincess()
    {
        return false;
    }

    /// <summary>
    /// Boss 3: Shisui Yohi
    /// </summary>
    private async Task<bool> ShisuiYohi()
    {
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Amikiri
        /// </summary>
        public const uint Amikiri = 6237;

        /// <summary>
        /// Second Boss: Ruby Princess
        /// </summary>
        public const uint RubyPrincess = 6241;

        /// <summary>
        /// Second Boss: Ruby Princess > Tamate-bako
        /// </summary>
        public const uint Tamatebako = 6274;

        /// <summary>
        /// Final Boss: Shisui Yohi
        /// </summary>
        public const uint ShisuiYohi = 6243;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.Amikiri"/>.
        /// </summary>
        public static readonly Vector3 Amikiri = new(0f, 18.5f, 70f);

        /// <summary>
        /// Second Boss: <see cref="EnemyNpc.RubyPrincess"/>.
        /// </summary>
        public static readonly Vector3 RubyPrincess = new(0f, 27f, -208f);

        /// <summary>
        /// Third Boss: <see cref="EnemyNpc.Shisui Yohi"/>.
        /// </summary>
        public static readonly Vector3 ShisuiYohi = new(0f, 45.9f, -432.5f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Amikiri
        /// Sharp Strike
        /// Tank Buster
        /// </summary>
        public const uint SharpStrike = 8050;

        /// <summary>
        /// Ruby Princess
        /// Seduce
        /// Change into an old lady by walking over to one of the boxes
        /// </summary>
        public const uint Seduce = 8058;

        /// <summary>
        /// Shisui Yohi
        /// Foul Nail
        /// Tank buster
        /// </summary>
        public const uint FoulNail = 8071;

        /// <summary>
        /// Shisui Yohi
        /// Bite and Run
        ///
        /// </summary>
        public const uint BiteandRun = 8069;
        public const uint BiteandRun2 = 8070;


    }

    private static class PlayerAura
    {

    }
}
