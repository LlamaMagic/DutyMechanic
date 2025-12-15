using Clio.Utilities;
using DutyMechanic.Data;
using ff14bot.Managers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 60: The Limitless Blue trial logic.
/// </summary>
public class AlexanderA2CuffoftheFather : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AlexanderA2CuffoftheFather;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new() { };
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { };
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

    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Boss: MechanicalBosses.
        /// Square arena, don't really want a circle avoid for this
        /// </summary>
        public static readonly Vector3 MechanicalBosses = new(-0f, -28f, -75f);
    }

    private static class EnemyAction
    {

    }

    private static class AblityTimers
    {
    }
}
