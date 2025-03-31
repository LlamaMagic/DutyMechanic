﻿using Buddy.Coroutines;
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
/// Lv. 100: Yuweyawata Field Station dungeon logic.
/// </summary>
public class YuweyawataFieldStation : AbstractDungeon
{
    private readonly Stopwatch BeastlyRoarTimer = new();
    private static readonly int BeastlyRoarDuration = 30_000;

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.YuweyawataFieldStation;

    /// <inheritdoc/>
    public override DungeonId DungeonId => DungeonId.YuweyawataFieldStation;


    private AvoidInfo craterAvoid = default;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new() { EnemyAction.BoulderDance, };
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

        // Boss 1: Lightning Storm
        // Boss 3: Jagged Edge
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.CrystalQuarry or (uint)SubZoneId.SoulCenter or (uint)SubZoneId.TheDustYoke && !EnemyAction.SoulweaveHash.IsCasting(),
            objectSelector: bc => bc.CastingSpellId is EnemyAction.LightningBolt or EnemyAction.TelltaleTears or EnemyAction.JaggedEdge && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss 2: Dark II
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SoulCenter,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.DarkII,
            leashPointProducer: () => ArenaCenter.OverseerKanilokka,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 30.0f);

        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SoulCenter && !EnemyAction.DarkIIHash.IsCasting(),
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.DarkII2,
            leashPointProducer: () => ArenaCenter.OverseerKanilokka,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 30.0f);

        /*
        // Boss 2: Phantom Flood
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SoulCenter,
            objectSelector: c => c.CastingSpellId == EnemyAction.PhantomFlood,
            outerRadius: 40.0f,
            innerRadius: 5.0F,
            priority: AvoidancePriority.Medium);
        */

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CrystalQuarry,
            () => ArenaCenter.LindblumZaghnal,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SoulCenter,
            () => ArenaCenter.OverseerKanilokka,
            outerRadius: 90.0f,
            innerRadius: 15.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheDustYoke,
            () => ArenaCenter.Lunipyati,
            outerRadius: 90.0f,
            innerRadius: 15.0f,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (!Core.Me.InCombat)
        {
            CapabilityManager.Clear();
            BeastlyRoarTimer.Reset();
        }

        if (WorldManager.SubZoneId is (uint)SubZoneId.SoulCenter or (uint)SubZoneId.TheDustYoke)
        {
            SidestepPlugin.Enabled = false;
        }
        else
        {
            SidestepPlugin.Enabled = true;
        }

        if (craterAvoid != default && WorldManager.SubZoneId != (uint)SubZoneId.TheDustYoke)
        {
            AvoidanceManager.RemoveAvoid(craterAvoid);
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.CrystalQuarry => await LindblumZaghnal(),
            SubZoneId.SoulCenter => await OverseerKanilokka(),
            SubZoneId.TheDustYoke => await Lunipyati(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: Lindblum Zaghnal.
    /// </summary>
    private async Task<bool> LindblumZaghnal()
    {
        return false;
    }

    /// <summary>
    /// Boss 2: Overseer Kanilokka.
    /// </summary>
    private async Task<bool> OverseerKanilokka()
    {
        if (EnemyAction.SoulweaveHash.IsCasting())
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 4_500, "Doing boss mechanics");
            await MovementHelpers.GetClosestMelee.Follow(1f);
        }

        if (Core.Me.HasAura(PlayerAura.TemporaryMisdirection) && Core.Me.GetAuraById(PlayerAura.TemporaryMisdirection).TimeLeft < 3)
        {
            if (TankInvul.TryGetValue(Core.Player.CurrentJob, out uint actionId))
            {
                SpellData action = DataManager.GetSpellData(actionId);
                if (ActionManager.CanCast(actionId, Core.Player) && Core.Me.HasAura(PlayerAura.TemporaryMisdirection))
                {
                    Logger.Information($"Casting {action.Name} ({action.Id})");
                    ActionManager.DoAction(action, Core.Player);
                    await Coroutine.Sleep(1_500);
                }
            }
        }

        if (EnemyAction.DarkSouls.IsCasting())
        {
            await CombatHelpers.HandleTankBuster();
        }

        return false;
    }

    /// <summary>
    /// Boss 3: Lunipyati.
    /// </summary>
    private async Task<bool> Lunipyati()
    {
        if (EnemyAction.CraterCarve.IsCasting() && !AvoidanceManager.AvoidInfos.Contains(craterAvoid))
        {
            craterAvoid = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheDustYoke,
                () => ArenaCenter.Lunipyati,
                outerRadius: 12.5f,
                innerRadius: 0.0f,
                priority: AvoidancePriority.High);

            AvoidanceManager.AddAvoid(craterAvoid);
        }

        if (EnemyAction.LeapingEarth.IsCasting() || EnemyAction.RagingClaw.IsCasting() || EnemyAction.TuraliStoneIV.IsCasting() || (EnemyAction.UnnamedLines.IsCasting() && !EnemyAction.JaggedEdgeHash.IsCasting()))
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 4_500, "Doing boss mechanics");
            await MovementHelpers.GetClosestMelee.Follow(useMesh: true, followDistance: 1f);
        }

        if (EnemyAction.BeastlyRoar.IsCasting() || BeastlyRoarTimer.IsRunning)
        {
            if (!BeastlyRoarTimer.IsRunning)
            {
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, BeastlyRoarDuration, "Beastly Roar Avoid");
                BeastlyRoarTimer.Start();
            }

            if (BeastlyRoarTimer.ElapsedMilliseconds < BeastlyRoarDuration)
            {
                await MovementHelpers.GetClosestMelee.FollowTimed(BeastlyRoarTimer, BeastlyRoarDuration, 1f, useMesh: true);
            }

            if (BeastlyRoarTimer.ElapsedMilliseconds >= BeastlyRoarDuration)
            {
                BeastlyRoarTimer.Reset();
            }
        }

        if (EnemyAction.Slabber.IsCasting())
        {
            await CombatHelpers.HandleTankBuster();
        }

        return false;
    }


    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Lindblum Zaghnal
        /// </summary>
        public const uint LindblumZaghnal = 13623;

        /// <summary>
        /// Second Boss: Overseer Kanilokka.
        /// </summary>
        public const uint OverseerKanilokka = 13634;

        /// <summary>
        /// Final Boss: Lunipyati.
        /// </summary>
        public const uint Lunipyati = 13610;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.LindblumZaghnal"/>.
        /// </summary>
        public static readonly Vector3 LindblumZaghnal = new(73f, 0.75f, 277f);

        /// <summary>
        /// Second Boss: Overseer Kanilokka.
        /// </summary>
        public static readonly Vector3 OverseerKanilokka = new(116f, 12.5f, -66f);

        /// <summary>
        /// Second Boss: Overseer Kanilokka.
        /// Safe spot to dodge
        /// </summary>
        public static readonly Vector3 SoulCenterSafeSpot = new(112.520134f, 12.499999f, -47.292393f);

        /// <summary>
        /// Third Boss: Lunipyati.
        /// </summary>
        public static readonly Vector3 Lunipyati = new(34f, -88f, -710f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Lindblum Zaghnal
        /// Lightning Bolt
        /// Spread
        /// </summary>
        public const uint LightningBolt = 40637;

        /// <summary>
        /// Overseer Kanilokka
        /// Telltale Tears
        /// Spread
        /// </summary>
        public const uint TelltaleTears = 40649;

        public static readonly HashSet<uint> TelltaleTearsHash = new() { 40649 };

        /// <summary>
        /// Overseer Kanilokka
        /// Soulweave
        /// Lots of swords everywhere
        /// </summary>
        public const uint Soulweave = 40641;

        /// <summary>
        /// Overseer Kanilokka
        /// Soulweave
        /// Lots of swords everywhere
        /// </summary>
        public const uint Soulweave2 = 40642;

        public static readonly HashSet<uint> SoulweaveHash = new() { 40641, 40642 };

        /// <summary>
        /// Overseer Kanilokka
        /// Dark II
        /// Follow
        /// </summary>
        public const uint DarkII = 40656;

        public static readonly HashSet<uint> DarkIIHash = new() { 40656 };

        /// <summary>
        /// Overseer Kanilokka
        /// Dark II
        /// Follow
        /// </summary>
        public const uint DarkII2 = 40657;

        /// <summary>
        /// Overseer Kanilokka
        /// Phantom Flood
        /// Floods the room with blood
        /// </summary>
        public const uint PhantomFlood = 40643;

        /// <summary>
        /// Overseer Kanilokka
        /// Necrohazard
        /// Plants blood on the ground with a confusion hand over your head
        /// </summary>
        public static readonly HashSet<uint> Necrohazard = new() { 40646 };

        /// <summary>
        /// Overseer Kanilokka
        /// Dark Souls
        /// Tank Buster, use rampart
        /// </summary>
        public static readonly HashSet<uint> DarkSouls = new() { 40658 };

        /// <summary>
        /// Overseer Kanilokka
        /// Soul Douse
        /// Stack
        /// </summary>
        public const uint SoulDouse = 40651;

        /// <summary>
        /// Lunipyati
        /// UnnamedLines
        /// Follow to dodge these
        /// </summary>
        public static readonly HashSet<uint> UnnamedLines = new() { 40661, 40662 };

        /// <summary>
        /// Lunipyati
        /// Leaping Earth
        /// Follow to dodge these
        /// </summary>
        public static readonly HashSet<uint> LeapingEarth = new() { 40606 };

        /// <summary>
        /// Lunipyati
        /// RagingClaw
        /// Straight line aoe
        /// </summary>
        public static readonly HashSet<uint> RagingClaw = new() { 40612 };

        /// <summary>
        /// Lunipyati
        /// Boulder Dance
        /// Boss makes lines where a bolder will fall and then it falls there
        /// </summary>
        public const uint BoulderDance = 40608;

        /// <summary>
        /// Lunipyati
        /// Beastly Roar
        /// Proximity based damage
        /// </summary>
        public static readonly HashSet<uint> BeastlyRoar = new() { 40608, 40610 };

        /// <summary>
        /// Lunipyati
        /// Jagged Edge
        /// Spread
        /// </summary>
        public const uint JaggedEdge = 40615;

        public static readonly HashSet<uint> JaggedEdgeHash = new() { 40615 };

        /// <summary>
        /// Lunipyati
        /// Slabber
        /// Tank buster
        /// </summary>
        public static readonly HashSet<uint> Slabber = new() { 40619 };


        /// <summary>
        /// Lunipyati
        /// Turali Stone IV
        /// Stack
        /// </summary>
        public static readonly HashSet<uint> TuraliStoneIV = new() { 40616 };

        /// <summary>
        /// Lunipyati
        /// Crater Carve
        /// Carves a big hole in the middle of the battlefield
        /// </summary>
        public static readonly HashSet<uint> CraterCarve = new() { 40604, 40605 };
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Overseer Kanilokka
        /// Aura Name: Temporary Misdirection, Aura Id: 3909
        /// Causes loss of control of your character
        /// </summary>
        public const uint TemporaryMisdirection = 3909;

        public const uint Rampart = 1191;
    }
}
