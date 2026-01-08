using DutyMechanic.Data;
using ff14bot.Enums;
using ff14bot.Managers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Eureka Pyros boss logic
/// </summary>
public class EurekaPyros : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.EurekaPyros;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
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
