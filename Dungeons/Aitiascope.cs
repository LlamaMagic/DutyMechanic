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
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

public class Aitiascope : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheAitiascope;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [
        EnemyAction.IgnisOdi,
        EnemyAction.ShieldSkewer,
        EnemyAction.ThundagaForteCenter
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.AglaeaBite,EnemyAction.AnvilofTartarus,EnemyAction.DarkForte];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [EnemyAction.IgnisOdi];

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
            SubZoneId.CentralObservatory => await HandleLiviatheUndeterred(),
            SubZoneId.SaltcrystalStrings => await HandleRhitahtyntheUnshakable(),
            SubZoneId.MidnightDownwell => await HandleAmontheUndying(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    private async Task<bool> HandleLiviatheUndeterred()
    {

        if (lastSubZoneId is not SubZoneId.CentralObservatory)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.Livia,
                outerRadius: 90.0f,
                innerRadius: 19.0f,
                priority: AvoidancePriority.High);
        }

        return false;
    }

    private async Task<bool> HandleRhitahtyntheUnshakable()
    {

        if (lastSubZoneId is not SubZoneId.SaltcrystalStrings)
        {

            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            Logger.Information("Removing Shield Skewer Avoid to allow for Follow-Dodge behavior.");
            SideStep.Override(25680);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidSquareDonut(
                () => Core.Player.InCombat,
                innerWidth: 39.0f,
                innerHeight: 39.0f,
                outerWidth: 90.0f,
                outerHeight: 90.0f,
                collectionProducer: () => [ArenaCenter.Rhitahty],
                priority: AvoidancePriority.High);
        }

        return false;
    }

    private Task<bool> HandleAmontheUndying()
    {

        if (lastSubZoneId is not SubZoneId.MidnightDownwell)
        {
            uint currentSubZoneId = WorldManager.SubZoneId;
            Logger.Information(Translations.SUBZONE_CHANGED_ADDING_AVOIDS, (SubZoneId)currentSubZoneId);

            Logger.Information("Removing Thundaga Forte Avoid to allow for Follow-Dodge behavior.");
            SideStep.Override(25690);

            AvoidanceHelpers.AddAvoidDonut(
                canRun: () => Core.Player.InCombat && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(bc => bc.CastingSpellId == EnemyAction.CurtainCall && bc.SpellCastInfo.RemainingCastTime.Milliseconds <= 3000),
                locationProducer: () => ArenaCenter.CurtainCallSafeSpot,
                outerRadius: 90,
                innerRadius: 2);

            AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId is EnemyAction.RightFiragaForte,
                width: 20.0f,
                length: 40f,
                xOffset: 10f,
                priority: AvoidancePriority.High);

            AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: bc => bc.CastingSpellId is EnemyAction.LeftFiragaForte,
                width: 20.0f,
                length: 40f,
                xOffset: -10f,
                priority: AvoidancePriority.High);

            AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
                canRun: () => Core.Player.InCombat ,
                objectSelector: bc => bc.CastingSpellId == EnemyAction.Epode,
                width: 12f,
                length: 120f,
                yOffset: -60f,
                priority: AvoidancePriority.High);

            AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
                canRun: () => Core.Player.InCombat,
                objectSelector: (bc) => bc.CastingSpellId == EnemyAction.ThundagaForteTriangle1,
                leashPointProducer: () => ArenaCenter.Amon,
                leashRadius: 40.0f,
                rotationDegrees: 180.0f,
                radius: 40.0f,
                arcDegrees: 45f);

            AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
                canRun: () => Core.Player.InCombat && !EnemyAction.ThundagaForteTriangle1Hash.IsCasting(),
                objectSelector: (bc) => bc.CastingSpellId == EnemyAction.ThundagaForteTriangle2,
                leashPointProducer: () => ArenaCenter.Amon,
                leashRadius: 40.0f,
                rotationDegrees: 180.0f,
                radius: 40.0f,
                arcDegrees: 45f);

            // Boss Arena
            _ = AvoidanceHelpers.AddAvoidDonut(
                () => Core.Player.InCombat,
                () => ArenaCenter.Amon,
                outerRadius: 90.0f,
                innerRadius: 19.0f,
                priority: AvoidancePriority.High);
        }

        return Task.FromResult(false);
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Livia the Undeterred
        /// </summary>
        public const uint Livia = 10290;

        /// <summary>
        /// Second Boss: Rhitahtyn the Unshakable.
        /// </summary>
        public const uint Rhitahtyn = 10292;

        /// <summary>
        /// Final Boss: Amon the Undying.
        /// </summary>
        public const uint Amon = 10293;

        /// <summary>
        /// Final Boss: Amon the Undying.
        /// Shiva's Ice
        /// </summary>
        public const uint ShivasIce = 108;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Boss 1: Livia the Undeterred.
        /// </summary>
        public static readonly Vector3 Livia = new(-6f, 164f, 471f);

        /// <summary>
        /// Boss 2: Rhitahtyn the Unshakable.
        /// </summary>
        public static readonly Vector3 Rhitahty = new(11f, -211.4f, 144f);

        /// <summary>
        /// Boss 3: Amon the Undying.
        /// </summary>
        public static readonly Vector3 Amon = new(11f, -236f, -490f);

        /// <summary>
        /// Boss 3: Thundaga Forte Safe Spot.
        /// </summary>
        public static readonly Vector3 ThundagaForteSafeSpot = new(10.796859f, -236f, -506.8239f);

        /// <summary>
        /// Boss 3: Curtain Call Safe Spot.
        /// </summary>
        public static readonly Vector3 CurtainCallSafeSpot = new(10.692489f, -236f, -480.21402f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// <see cref="EnemyNpc.Livia"/>'s Aglaea Bite.
        /// Tank Buster
        /// </summary>
        public const uint AglaeaBite = 25673;

        /// <summary>
        /// <see cref="EnemyNpc.Livia"/>'s Ignis Odi.
        /// Stack
        /// </summary>
        public const uint IgnisOdi = 25677;

        /// <summary>
        /// <see cref="EnemyNpc.Rhitahtyn"/>'s Shrapnel Shell.
        ///
        /// </summary>
        public const uint ShrapnelShell = 25684;

        /// <summary>
        /// <see cref="EnemyNpc.Rhitahtyn"/>'s Anvil of Tartarus.
        ///
        /// </summary>
        public const uint AnvilofTartarus = 25686;

        /// <summary>
        /// <see cref="EnemyNpc.Rhitahtyn"/>'s Tartarean Spark .
        /// Small straight line AOE
        /// </summary>
        public const uint TartareanSpark = 25687;

        /// <summary>
        /// <see cref="EnemyNpc.Rhitahtyn"/>'s Impact.
        /// Move away from the sides of the arena
        /// </summary>
        public const uint Impact = 25679;

        /// <summary>
        /// <see cref="EnemyNpc.Rhitahtyn"/>'s Shield Skewer .
        /// Follow dodge
        /// </summary>
        public const uint ShieldSkewer = 25680;
        public static readonly HashSet<uint> ShieldSkewerHash = [25680];

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Dark Forte.
        /// Tank Buster
        /// </summary>
        public const uint DarkForte = 25700;

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Thundaga Forte.
        /// Explosive from center of arena
        /// </summary>
        public const uint ThundagaForteCenter = 25690;
        public static readonly HashSet<uint> ThundagaForteCenterHash = [25690];

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Thundaga Forte.
        /// Explosive from center of arena
        /// </summary>
        public const uint ThundagaForteTriangle1 = 25691;
        public static readonly HashSet<uint> ThundagaForteTriangle1Hash = [25691];

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Thundaga Forte.
        /// Explosive from center of arena
        /// </summary>
        public const uint ThundagaForteTriangle2 = 25692;

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Right Firaga Forte.
        /// Takes over the right side of the arena
        /// </summary>
        public const uint RightFiragaForte = 25696;

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Left Firaga Forte.
        /// Takes over the left side of the arena
        /// </summary>
        public const uint LeftFiragaForte  = 25697;

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Epode.
        /// Straight line AOE
        /// </summary>
        public const uint Epode  = 25695;

        /// <summary>
        /// <see cref="EnemyNpc.Amon"/>'s Curtain Call.
        /// Hide behind the ice
        /// </summary>
        public const uint CurtainCall  = 25702;

    }

    private static class PartyAura
    {

    }
}
