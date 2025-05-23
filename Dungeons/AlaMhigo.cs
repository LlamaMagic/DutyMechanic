﻿using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
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
/// Lv. 70.1: AlaMhigo dungeon logic.
/// </summary>
public class AlaMhigo : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AlaMhigo;

    /// <inheritdoc/>
    public override DungeonId DungeonId => DungeonId.AlaMhigo;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { };
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        // Blue puddles of fire that fall on the player between the first boss and the second
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.RhalgrsGate,
            objectSelector: eo => eo.IsVisible && eo.NpcId == EnemyNpc.FirePuddle,
            radiusProducer: eo => 5.0f,
            priority: AvoidancePriority.High));

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.RhalgrsGate,
            objectSelector: bc => bc.NpcId == EnemyNpc.LockOn && bc.IsVisible,
            radiusProducer: bc => 7.0f,
            priority: AvoidancePriority.High));

        // Boss 2: Demimagicks
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == ((uint)SubZoneId.TheChamberofKnowledge),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Demimagicks,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss Arenas

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.RhalgrsGate,
            () => ArenaCenter.MagitekScorpion,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheChamberofKnowledge,
            () => ArenaCenter.AulusmalAsina,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheHalloftheGriffin,
            () => ArenaCenter.Zenos,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();


        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;
        bool result = false;

        switch (currentSubZoneId)
        {
            case SubZoneId.RhalgrsGate:
                result = await HandleMagitekScorpionAsync();
                break;
            case SubZoneId.TheChamberofKnowledge:
                result = await HandleAulusAsync();
                break;
            case SubZoneId.TheHalloftheGriffin:
                result = await HandleZenosAsync();
                break;
        }

        return false;
    }


    private async Task<bool> HandleMagitekScorpionAsync()
    {
        return false;
    }

    private async Task<bool> HandleAulusAsync()
    {
        // Handle Out of Body experience
        if (WorldManager.SubZoneId == (uint)SubZoneId.TheChamberofKnowledge && Core.Player.InCombat)
        {
            while (Core.Me.HasAura(PlayerAura.OutOfBody) && !Core.Me.HasAura(PlayerAura.Stun))
            {
                var emptyVessle = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.EmptyVessel)
                    .OrderBy(bc => bc.Distance2D()).FirstOrDefault(bc => bc.IsValid && bc.IsTargetable); // our lifeless body

                if (emptyVessle != null)
                {
                    while (Core.Me.HasAura(PlayerAura.OutOfBody) && Core.Me.Location.Distance2D(emptyVessle.Location) > 0.5f)
                    {
                        ff14bot.Helpers.Logging.WriteDiagnostic($"Moving to our lifeless body. Distance: {Core.Me.Location.Distance2D(emptyVessle.Location)}");
                        await LlamaLibrary.Helpers.Navigation.GroundMove(emptyVessle.Location, 0.5f);
                    }

                    await CommonTasks.StopMoving();
                    await Coroutine.Wait(5000, () => Core.Me.Location.Distance2D(emptyVessle.Location) > 0.5 || !Core.Me.HasAura(PlayerAura.OutOfBody));
                }
            }
        }

        return false;
    }

    private async Task<bool> HandleZenosAsync()
    {
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Magitek Scorpion.
        /// </summary>
        public const uint MagitekScorpion = 6037;

        /// <summary>
        /// First Boss: Magitek Scorpion.
        /// </summary>
        public const uint LockOn = 108;

        /// <summary>
        /// First Boss: Fire puddle.
        /// </summary>
        public const uint FirePuddle = 2008685;

        /// <summary>
        /// Second Boss: Empty Vessel.
        /// </summary>
        public const uint EmptyVessel = 6666;

        /// <summary>
        /// Final Boss: Zenos yae Galvus .
        /// </summary>
        public const uint ZenosyaeGalvus = 6039;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Magitek Scorpion.
        /// </summary>
        public static readonly Vector3 MagitekScorpion = new(-191f, 35f, 72f);

        /// <summary>
        /// Second Boss: The Governor.
        /// </summary>
        public static readonly Vector3 AulusmalAsina = new(250f, 106.5f, -70f);

        /// <summary>
        /// Third Boss: Lorelei.
        /// </summary>
        public static readonly Vector3 Zenos = new(250f, 122f, -353f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Magitek Scorpion
        /// Tail Lazer
        /// Stack
        /// </summary>
        public const uint TailLaser1 = 8264;

        public const uint TailLaser2 = 8265;
        public const uint TailLaser3 = 8266;

        /// <summary>
        /// Magitek Scorpion
        /// Target Search
        /// Follow for 10 seconds after cast happens
        /// </summary>
        public static readonly HashSet<uint> TargetSearch = new() { 8262 };

        /// <summary>
        /// Aulus mal Asina
        /// Demimagicks
        /// Spread
        /// </summary>
        public const uint Demimagicks = 8286;

        public static readonly int DemimagicksDuration = 5_000;
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Out Of Body
        /// </summary>
        public const uint OutOfBody = 779;

        /// <summary>
        /// Stun
        /// </summary>
        public const uint Stun = 149;
    }
}
