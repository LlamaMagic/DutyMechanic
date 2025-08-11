using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DutyMechanic.Extensions;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100. The Ageless Necropolis trial logic.
/// </summary>
public class AgelessNecropolis : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AgelessNecropolis;

    /// <inheritdoc/>
    public override DungeonId DungeonId => DungeonId.AgelessNecropolis;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new() { };

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { EnemyAction.BlueShockwave };

    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        // Boss 1: Necron > GrandCross
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.AgelessNecropolis && EnemyAction.GrandCross.IsCasting(),
            () => ArenaCenter.Necron,
            outerRadius: 40.0f,
            innerRadius: 8.0F,
            priority: AvoidancePriority.High);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.AgelessNecropolis,
            innerWidth: 33.0f,
            innerHeight: 28.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => new[] { ArenaCenter.Necron },
            priority: AvoidancePriority.High);


        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();
        await TankBusterSpells();

        if (Core.Me.IsTank() && EnemyAction.BlueShockwaveHash.IsCasting())
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 7000, "Spreading for Tank Buster");
            await MovementHelpers.Spread(7_000, 30f);
        }

        if (!Core.Me.IsTank() && EnemyAction.BlueShockwaveHash.IsCasting())
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 7000, "Stacking for Tank Buster");
            await MovementHelpers.GetClosestAlly.Follow();
        }

        var visibleAzureAethers = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.AzureAether)
            .Where(bc => bc.IsVisible)
            .ToList();

        if (visibleAzureAethers.Count > 1)
        {

            SidestepPlugin.Enabled = false;
            await MovementHelpers.GetClosestAlly.Follow();
        }

        if (visibleAzureAethers.Count < 2)
        {
            SidestepPlugin.Enabled = true;
        }

        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Boss: Necron.
        /// </summary>
        public const uint Necron = 14093;

        /// <summary>
        /// Boss: Azure Aether.
        /// </summary>
        public const uint AzureAether = 14095;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.Necron"/>.
        /// </summary>
        public static readonly Vector3 Necron = new(100f, 0f, 100f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Necron
        /// Blue Shockwave (44546)
        /// Tank buster but you have to spread away from the group
        /// </summary>
        public const uint BlueShockwave = 44546;

        public static readonly HashSet<uint> BlueShockwaveHash = new() { 44546 };

        /// <summary>
        /// Necron
        /// Grand Cross (44533) - AoE
        /// </summary>
        public static readonly HashSet<uint> GrandCross = new() { 44533 };
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Doom
        /// </summary>
        public const uint Doom = 4683;
    }
}
