using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Localization;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static DutyMechanic.Dungeons.PortaDecumana;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100.6: Mistwake
/// </summary>
public class Mistwake : AbstractDungeon
{
    private const float StalagmiteRadius = 2.5f;
    private static DateTime BedevilingLightTimestamp = DateTime.MinValue;

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Mistwake;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
    [
        EnemyAction.RayOfLightning,
        EnemyAction.AmdusiasThunderIII, EnemyAction.Rush
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.ThunderIII, EnemyAction.Shockbolt, EnemyAction.GoldenTalons];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        EnemyAction.RayOfLightning,
        EnemyAction.AmdusiasThunderIII
    ];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        _ = await FollowDodgeSpells();
        _ = await TankBusterSpells();
        _ = await DamageMitigationSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (lastSubZoneId != currentSubZoneId)
        {
            Logger.Information(Translations.SUBZONE_CHANGED_CLEARING_AVOIDS, currentSubZoneId);

            SidestepPlugin.Enabled = true;

            AvoidanceManager.RemoveAllAvoids(avoidInfo => true);
            AvoidanceManager.ResetNavigation();
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.ShatteredLair => await HandleTrenoCatoblepas(),
            SubZoneId.OverturePlaza => await HandleAmdusias(),
            SubZoneId.TownRound => await HandleThundergustGriffin(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    private async Task<bool> HandleTrenoCatoblepas()
    {
        SidestepPlugin.Enabled = false;

        if (lastSubZoneId is not SubZoneId.ShatteredLair)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            // Add small static avoids on Stalagmites to help path around them
            AvoidanceManager.AddAvoidObject<GameObject>(
                canRun: () => Core.Player.InCombat,
                radius: StalagmiteRadius,
                unitIds: [.. GameObjectManager.GetObjectsByNPCId(EnemyNpc.Stalagmite).Select(obj => obj.ObjectId)]);

            AvoidanceHelpers.AddAvoidDonut(
                canRun: () => Core.Player.InCombat && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(bc => bc.CastingSpellId == EnemyAction.BedevilingLight),
                locationProducer: () =>
                {
                    Vector3 nearestRock = GameObjectManager
                        .GameObjects.OrderBy(obj => obj.Distance())
                        .FirstOrDefault(obj => obj.NpcId == EnemyNpc.Stalagmite && obj.IsVisible).Location;

                    return Clio.Common.MathEx.CalculatePointFrom(ArenaCenter.TrenoCatoblepas, nearestRock, -3f);
                },
                outerRadius: 90,
                innerRadius: 1);

            // Thunder III for non-tank/non-target: avoid the AoE buster target
            AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId is EnemyAction.ThunderIII && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
                radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.1f,
                locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

            // Thunder III for tank/target: avoid hitting party AND Stalagmites
            AvoidanceManager.AddAvoidObject<GameObject>(
                canRun: () => Core.Player.InCombat && GameObjectManager.Attackers.Any(bc => bc.CastingSpellId is EnemyAction.ThunderIII && bc.SpellCastInfo.TargetId == Core.Player.ObjectId),
                radius: 4f + StalagmiteRadius,
                unitIds:
                [
                    .. PartyManager.VisibleMembers.Select(p => p.BattleCharacter.ObjectId),
                    .. GameObjectManager.GetObjectsByNPCId(EnemyNpc.Stalagmite).Select(obj => obj.ObjectId)
                ]);

            AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId == EnemyAction.Petribreath,
                leashPointProducer: () => ArenaCenter.TrenoCatoblepas,
                leashRadius: 40.0f,
                rotationDegrees: 0.0f,
                radius: 40.0f,
                arcDegrees: 131f);

            // Thunder II: Avoid hitting each other or stacking in static puddles
            AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => (bc.CastingSpellId is EnemyAction.ThunderII_Targeted or EnemyAction.ThunderII_Ground) && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
                radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.1f,
                locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

            // Thunder II: Also avoid hitting Stalagmites
            AvoidanceManager.AddAvoidObject<GameObject>(
                canRun: () => Core.Player.InCombat && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(bc => (bc.CastingSpellId is EnemyAction.ThunderII_Targeted or EnemyAction.ThunderII_Ground)),
                radius: 5f + StalagmiteRadius,
                unitIds: [.. GameObjectManager.GetObjectsByNPCId(EnemyNpc.Stalagmite).Select(obj => obj.ObjectId)]);

            // Boss Arena
            AvoidanceHelpers.AddAvoidSquareDonut(
                () => Core.Player.InCombat,
                innerWidth: 39.0f,
                innerHeight: 39.0f,
                outerWidth: 90.0f,
                outerHeight: 90.0f,
                collectionProducer: () => [ArenaCenter.TrenoCatoblepas],
                priority: AvoidancePriority.High);
        }

        if (Core.Player.InCombat)
        {
            if (BedevilingLightTimestamp < DateTime.Now)
            {
                var trenoCatoblepas = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(bc => bc.CastingSpellId == EnemyAction.BedevilingLight);
                if (trenoCatoblepas != default)
                {
                    BedevilingLightTimestamp = DateTime.Now;
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, trenoCatoblepas.SpellCastInfo.RemainingCastTime, $"Disallow combat routine movement while hiding from ({trenoCatoblepas.SpellCastInfo.ActionId}) {trenoCatoblepas.SpellCastInfo.Name} for {trenoCatoblepas.SpellCastInfo.RemainingCastTime}.");
                }
            }
        }

        return false;
    }

    private async Task<bool> HandleAmdusias()
    {
        SidestepPlugin.Enabled = false;

        BattleCharacter poisonCloud = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.PoisonCloud).OrderBy(bc => bc.Distance2D()).LastOrDefault(bc => bc.IsVisible);

        if (poisonCloud != null)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 27000, $"Avoiding Poison Cloud for 27 seconds");
            await MovementHelpers.GetClosestAlly.Follow();
        }

        if (lastSubZoneId is not SubZoneId.OverturePlaza)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: (bc) => bc.CastingSpellId == EnemyAction.ThunderclapConcertoBehind,
                leashPointProducer: () => ArenaCenter.Amdusias,
                leashRadius: 40.0f,
                rotationDegrees: 0.0f,
                radius: 40.0f,
                arcDegrees: 345f);

            AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: (bc) => bc.CastingSpellId == EnemyAction.ThunderclapConcertoFront,
                leashPointProducer: () => ArenaCenter.Amdusias,
                leashRadius: 40.0f,
                rotationDegrees: 180.0f,
                radius: 40.0f,
                arcDegrees: 345f);

            AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId is EnemyAction.Thunder && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
                radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
                locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.Amdusias,
                outerRadius: 90.0f,
                innerRadius: 19.0f,
                priority: AvoidancePriority.High);
        }

        return false;
    }

    private Task<bool> HandleThundergustGriffin()
    {
        SidestepPlugin.Enabled = true;

        if (lastSubZoneId is not SubZoneId.TownRound)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId is EnemyAction.LightningBolt && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
                radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
                locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.ThundergustGriffin,
                outerRadius: 90.0f,
                innerRadius: 18.5f,
                priority: AvoidancePriority.High);
        }

        return Task.FromResult(false);
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Treno Catoblepas
        /// </summary>
        public const uint TrenoCatoblepas = 14270;

        /// <summary>
        /// First Boss: Treno Catoblepas's Stalagmite
        /// </summary>
        public const uint Stalagmite = 108;

        /// <summary>
        /// Second Boss: Amdusias.
        /// </summary>
        public const uint Amdusias = 14271;

        /// <summary>
        /// Second Boss: Poison Cloud.
        /// </summary>
        public const uint PoisonCloud = 14272;

        /// <summary>
        /// Final Boss: Thundergust Griffin.
        /// </summary>
        public const uint ThundergustGriffin = 14288;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Boss 1: Treno Catoblepas.
        /// </summary>
        public static readonly Vector3 TrenoCatoblepas = new(84f, 48f, 373f);

        /// <summary>
        /// Boss 2: Amdusias.
        /// </summary>
        public static readonly Vector3 Amdusias = new(281f, -98f, -285f);

        /// <summary>
        /// Boss 3: Thundergust Griffin.
        /// </summary>
        public static readonly Vector3 ThundergustGriffin = new(281f, -115f, -620f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Earthquake.
        /// Unavoidable raid-wide AoE damage.
        /// </summary>
        public const uint Earthquake = 43327;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Bedeviling Light.
        /// AoE petrification. Break line of sight with <see cref="EnemyNpc.Stalagmite"/>.
        /// </summary>
        public const uint BedevilingLight = 43330;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder III.
        /// AoE Tank Buster. Avoid hitting other players and <see cref="EnemyNpc.Stalagmite"/>.
        /// </summary>
        public const uint ThunderIII = 43329;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Ray of Lightning.
        /// Shared line AoE targeting a player.
        /// </summary>
        public const uint RayOfLightning = 44825;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Petribreath.
        /// Front-facing cone AoE.
        /// </summary>
        public const uint Petribreath = 43335;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder II, player-targeted.
        /// Circle AoE targeting player. Spread without hitting <see cref="EnemyNpc.Stalagmite"/>.
        /// </summary>
        public const uint ThunderII_Targeted = 43333;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder II, ground-targeted.
        /// Circle AoE targeting static location. Will unavoidably break some <see cref="EnemyNpc.Stalagmite"/>.
        /// </summary>
        public const uint ThunderII_Ground = 43332;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder II, dummy cast.
        /// </summary>
        public const uint ThunderII_Dummy = 43331;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        /// </summary>
        public const uint ThunderclapConcertoBehind = 45336;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        /// </summary>
        /// public const uint ThunderclapConcerto2 = 45337;
        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        /// </summary>
        public const uint ThunderclapConcertoFront = 45341;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        /// </summary>
        /// public const uint ThunderclapConcerto4 = 45342;
        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Burst.
        /// </summary>
        public const uint Burst = 45349;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Shockbolt.
        /// Tank Buster
        /// </summary>
        public const uint Shockbolt = 45356;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunder.
        /// Spread
        /// </summary>
        public const uint Thunder = 45343;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunder III.
        /// Stack
        /// </summary>
        public const uint AmdusiasThunderIII = 45352;

        /// <summary>
        /// <see cref="EnemyNpc.ThundergustGriffin"/>'s Golden Talons.
        /// Tank Buster
        /// </summary>
        public const uint GoldenTalons = 45305;

        /// <summary>
        /// <see cref="EnemyNpc.ThundergustGriffin"/>'s Rush.
        /// Follow
        /// </summary>
        public const uint Rush = 45302;

        /// <summary>
        /// <see cref="EnemyNpc.ThundergustGriffin"/>'s Thundering Roar.
        /// Follow
        /// </summary>
        public const uint ThunderingRoar = 45295;

        /// <summary>
        /// <see cref="EnemyNpc.ThundergustGriffin"/>'s Lightning Bolt.
        /// Spread
        /// </summary>
        public const uint LightningBolt = 46856;

        /// <summary>
        /// <see cref="EnemyNpc.ThundergustGriffin"/>'s Electrogenetic Force.
        /// Spread
        /// </summary>
        public const uint ElectrogeneticForce = 45304;
    }

    private static class PartyAura
    {
        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Bedeviling Light.
        /// </summary>
        public const uint BedevilingLight = 43330;
    }
}
