using Buddy.Coroutines;
using Clio.Common;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DutyMechanic.Extensions;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Eureka Pyros boss logic
/// </summary>
public class EurekaPyros : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.EurekaPyros;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new() { };

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { };

    private static readonly Dictionary<ClassJobType, uint> TankInvul = new()
    {
        { ClassJobType.Warrior, 43 }, // Holmgang
        { ClassJobType.Paladin, 30 }, // Hallowed Ground
        { ClassJobType.DarkKnight, 3638 }, // Living Dead
        { ClassJobType.Gunbreaker, 16152 }, // Superbolide
    };

    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Frozen Void Dragon
        /// Walk around these guys so you don't wake them up
        /// </summary>
        public const uint FrozenVoidDragon = 7473;
    }

    private static class ArenaCenter
    {
    }

    private static class FateIds
    {
    }

    private static class EnemyAction
    {
    }
}
