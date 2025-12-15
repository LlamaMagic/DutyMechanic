using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Localization;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100.6: Mistwake
/// </summary>
public class Mistwake : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId { get; }

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; }

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        _ = await FollowDodgeSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (lastSubZoneId != currentSubZoneId)
        {
            Logger.Information(Translations.SUBZONE_CHANGED_CLEARING_AVOIDS, currentSubZoneId);

            AvoidanceManager.RemoveAllAvoids(avoidInfo => true);
            AvoidanceManager.ResetNavigation();
        }

        bool result = currentSubZoneId switch
        {
            //SubZoneId.NONE => await HandlePlaceholder1(),
            //SubZoneId.NONE => await HandlePlaceholder2(),
            //SubZoneId.NONE => await HandlePlaceholder3(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    private Task<bool> HandlePlaceholder1()
    {
        if (lastSubZoneId is not SubZoneId.NONE)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.Placeholder,
                outerRadius: 90.0f,
                innerRadius: 19.0f,
                priority: AvoidancePriority.High);
        }

        return Task.FromResult(false);
    }

    private Task<bool> HandlePlaceholder2()
    {
        if (lastSubZoneId is not SubZoneId.NONE)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.Placeholder,
                outerRadius: 90.0f,
                innerRadius: 19.0f,
                priority: AvoidancePriority.High);
        }

        return Task.FromResult(false);
    }

    private Task<bool> HandlePlaceholder3()
    {
        if (lastSubZoneId is not SubZoneId.NONE)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.Placeholder,
                outerRadius: 90.0f,
                innerRadius: 19.0f,
                priority: AvoidancePriority.High);
        }

        return Task.FromResult(false);
    }

    private static class EnemyNpc
    {
        public const uint Placeholder = uint.MaxValue;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Boss 1: Placeholder.
        /// </summary>
        public static readonly Vector3 Placeholder = new(0f, 0f, 0f);
    }

    private static class MechanicLocation
    {
        public static readonly Vector3 Placeholder = new(0f, 0f, 0f);
    }

    private static class EnemyAura
    {
        /// <summary>
        /// <see cref="EnemyNpc.Placeholder"/>'s Placeholder.
        /// </summary>
        public const uint Placeholder = uint.MaxValue;
    }

    private static class EnemyAction
    {
        /// <summary>
        /// <see cref="EnemyNpc.Placeholder"/>'s Placeholder.
        /// </summary>
        public const uint Placeholder = uint.MaxValue;
    }

    private static class PartyAura
    {
        /// <summary>
        /// <see cref="EnemyNpc.Placeholder"/>'s Placeholder.
        /// </summary>
        public const uint Placeholder = uint.MaxValue;
    }
}
