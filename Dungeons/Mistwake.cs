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
using System.Collections.Generic;
using System.Linq;
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
    public override ZoneId ZoneId => Data.ZoneId.Mistwake;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
    [
        EnemyAction.BedevilingLight, EnemyAction.RayofLightning,
        EnemyAction.AmdusiasThunderIII, EnemyAction.Rush
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.ThunderIII, EnemyAction.Shockbolt, EnemyAction.GoldenTalons];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        _ = await FollowDodgeSpells();
        _ = await TankBusterSpells();

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
        SidestepPlugin.Enabled = true;

        // Spread from other Party Members if casting Thunder III and we're tank
        if (Core.Me.IsTank() && EnemyAction.ThunderIIIHash.IsCasting())
        {
            await MovementHelpers.Spread(6000, 5f);
        }

        if (lastSubZoneId is not SubZoneId.ShatteredLair)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId is EnemyAction.ThunderII && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
                radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
                locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

            // Doing Seperate logic here for Thunder III as we don't want it running if we're the tank
            AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => Core.Player.InCombat && !Core.Me.IsTank(),
                objectSelector: bc => bc.CastingSpellId is EnemyAction.ThunderIII && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
                radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.1f,
                locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

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
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Bedeviling Light.
        /// </summary>
        public const uint BedevilingLight = 43330;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder III.
        /// AoE Tank Buster
        /// </summary>
        public const uint ThunderIII = 43329;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder III.
        /// AoE Tank Buster
        /// </summary>
        public static readonly HashSet<uint> ThunderIIIHash = [43329];

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Thunder II.
        /// Spread
        /// </summary>
        public const uint ThunderII = 43333;

        /// <summary>
        /// <see cref="EnemyNpc.TrenoCatoblepas"/>'s Ray of Lightning.
        /// Stack
        /// </summary>
        public const uint RayofLightning = 44825;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        ///
        /// </summary>
        public const uint ThunderclapConcertoBehind = 45336;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        ///
        /// </summary>
        /// public const uint ThunderclapConcerto2 = 45337;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        ///
        /// </summary>
        public const uint ThunderclapConcertoFront = 45341;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Thunderclap Concerto.
        ///
        /// </summary>
        /// public const uint ThunderclapConcerto4 = 45342;

        /// <summary>
        /// <see cref="EnemyNpc.Amdusias"/>'s Burst.
        ///
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
        /// <see cref="EnemyNpc.Treno Catoblepas"/>'s Bedeviling Light.
        /// </summary>
        public const uint BedevilingLight = 43330;
    }
}
