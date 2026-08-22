using Clio.Utilities;
using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 97: Vanguard dungeon logic.
/// </summary>
public class Vanguard : AbstractDungeon
{
    // Motion Sensor, Search and Destroy, Battery Circuit, Fulminous Fence, Enhanced Mobility, and
    // Zander's arena transition have independent lifecycles. Keeping their state separate prevents
    // a wipe, boss transition, or one mechanic's completion from releasing another mechanic's
    // ownership.
    private readonly CapabilityManagerHandle batteryCircuitMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle motionSensorMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly HashSet<uint> batteryCircuitHelperIds = [];

    private DateTime batteryCircuitFirstActivationAtUtc = DateTime.MinValue;
    private DateTime batteryCircuitEndsAtUtc = DateTime.MinValue;
    private DateTime enhancedMobilityEndsAtUtc = DateTime.MinValue;
    private DateTime protectorFenceCaptureEndsAtUtc = DateTime.MinValue;
    private DateTime protectorFenceCastFinishAtUtc = DateTime.MinValue;
    private DateTime searchAndDestroyPrepositionEndsAtUtc = DateTime.MinValue;
    private DateTime zanderArenaShrinkAtUtc = DateTime.MinValue;
    private Vector3 batteryCircuitDestination = ArenaCenter.Protector;
    private Vector3 enhancedMobilityOrigin = ArenaCenter.VanguardCommanderR8;
    private EnhancedMobilitySafeBand enhancedMobilitySafeBand;
    private ProtectorFenceLayout protectorFenceLayout;
    private uint batteryCircuitAnchorHelperId;
    private uint lastProtectorFenceMapEffectUnknown;
    private ushort lastProtectorFenceMapEffectState;
    private byte lastProtectorFenceMapEffectFlags;
    private float batteryCircuitAnchorInitialHeading;
    private float batteryCircuitDestinationHeading;
    private float lastBatteryCircuitDiagnosticHeading = float.NaN;
    private int batteryCircuitDestinationPulseIndex = -1;
    private int protectorFenceCaptureCheckpoint;
    private int lastBatteryCircuitDiagnosticPulseIndex = -1;
    private string lastProtectorFenceActorFingerprint = string.Empty;
    private string lastProtectorFenceDirectorFingerprint = string.Empty;
    private string lastProtectorFenceMapEffectsFingerprint = string.Empty;
    private bool protectorFenceMapEffectWasObserved;
    private bool protectorFenceFulminousWasCasting;
    private bool protectorFenceParalysisWasPresent;
    private bool batteryCircuitDestinationActive;
    private bool batteryCircuitDestinationUnavailableLogged;
    private bool batteryCircuitMovementOwned;
    private bool batteryCircuitMoving;
    private bool motionSensorMovementOwned;
    private bool zanderArenaShrunk;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Vanguard;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.HeavyBlastCannon];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];
    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Rush is a five-yalm charge from each sentry to its cast location. The previous six-yalm
        // approximation survived the 2026-08-21 capture, but using the measured width preserves
        // the narrow intended gaps when several parallel lanes resolve together.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInCommanderCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Rush,
            width: 5f,
            length: 40f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Enhanced Mobility begins with a 14-yalm-wide side rectangle whose forward length is 10
        // yalms for corner-safe variants and 20 yalms for center-safe variants. The helper action
        // identifies both the lateral offset and the later safe band, so the rectangle must use
        // the helper's position and heading rather than the boss visual's destination.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInCommanderCombat,
            objectSelector: IsEnhancedMobilityOutCaster,
            width: 14f,
            length: 10f,
            rotationProducer: caster => -caster.Heading,
            priority: AvoidancePriority.High,
            locationProducer: GetEnhancedMobilitySideOrigin);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInCommanderCombat,
            objectSelector: IsEnhancedMobilityInCaster,
            width: 14f,
            length: 20f,
            rotationProducer: caster => -caster.Heading,
            priority: AvoidancePriority.High,
            locationProducer: GetEnhancedMobilitySideOrigin);

        // Rapid Rotary follows the rectangle with three 120-degree sectors staggered by roughly
        // 0.3 seconds. Across the complete rotation those sectors cover one stable radial band:
        // corner-safe variants cover the inner 17 yalms, while center-safe variants cover the
        // 11-to-28-yalm ring. Publishing that union as a circle or donut gives RB an early,
        // continuously safe destination and avoids depending on no-cast helper events that generic
        // telegraph decoding removed before the vulnerability hit in the 2026-08-21 capture.
        AvoidanceManager.AddAvoid(new AvoidLocationInfo<Vector3>(
            condition: IsEnhancedMobilityOutAvoidActive,
            locationProducer: location => location,
            radiusProducer: _ => 17f,
            collecionSelection: () => [enhancedMobilityOrigin],
            leashPointSelector: () => ArenaCenter.VanguardCommanderR8,
            leashRadius: 25f,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High));

        AvoidanceHelpers.AddAvoidDonut(
            canRun: IsEnhancedMobilityInAvoidActive,
            locationProducer: () => enhancedMobilityOrigin,
            outerRadius: 28f,
            innerRadius: 11f,
            priority: AvoidancePriority.High);

        // Aerial Offensive's action data is a four-yalm base range plus a ten-yalm expanding
        // effect. SideStep used only the base range and left the player at center inside two
        // overlapping impacts; the four observed casts resolved together and killed from 98%
        // health. Duty Mechanic therefore owns the full 14-yalm cast-location circles.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInCommanderCombat,
            // The late encounter pattern starts a second four-sentry wave before the first has
            // fully resolved. Publishing only the next four preserves the intended wall-safe to
            // center-safe transition instead of combining future and current puddles.
            objectSelector: bc => IsAmongNextCasters(bc, EnemyAction.AerialOffensiveCasts, 4),
            radiusProducer: _ => 14f,
            locationProducer: bc => bc.SpellCastInfo.CastLocation,
            priority: AvoidancePriority.High));

        // Boss 1: Electrosurge
        // Boss 2: Tracking Bolt
        // Boss 3: Soulbane Shock
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.CentralGarage or (uint)SubZoneId.SafetyInspectionChamber or (uint)SubZoneId.VanguardControlRoom,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.Electrosurge or EnemyAction.TrackingBolt or EnemyAction.SoulbaneShock && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Search and Destroy's laser helpers cast 50-yalm lines with a measured total width of two
        // yalms. The navigation width includes a half-yalm player-hitbox margin on each edge: the
        // 2026-08-21 captures showed the player losing health while threading exact-width lanes,
        // and the extra clearance is small enough to preserve the mechanic's intended corridors.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInProtectorCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.HomingCannon,
            width: HomingCannonNavigationWidth,
            length: 50f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInProtectorCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Shock,
            radiusProducer: bc => 3.0f,
            locationProducer: bc => bc.SpellCastInfo.CastLocation,
            priority: AvoidancePriority.High));

        // Blast Cannon resolves as paired 26-by-4-yalm lines. Selecting only the next two helpers
        // preserves later safe lanes and leaves enough time to stop for the overlapping Motion
        // Sensor; publishing every queued line caused repeated forced movement and vulnerabilities.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInProtectorCombat,
            objectSelector: bc => IsAmongNextCasters(bc, EnemyAction.BlastCannonCasts, 2),
            width: 4f,
            length: 26f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Battery Circuit begins with two opposite 30-degree cones. Their helper actors rotate for
        // the subsequent no-cast pulses, so latching the first-cast object IDs keeps the current
        // cones attached to the correct live helpers for all 34 half-second resolutions.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsBatteryCircuitAvoidActive,
            objectSelector: IsTrackedBatteryCircuitHelper,
            leashPointProducer: () => ArenaCenter.Protector,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 30.0f,
            arcDegrees: 30.0f);

        // The helper heading changes at the damage event, which made a current-heading-only avoid
        // reactive: the helper first observed casting action 37351 later applied vulnerability and
        // the following 0.5-second pulse killed the player on 2026-08-21. Each pulse applies a
        // negative 11-degree heading delta, so a second cone publishes the next activation early
        // while the zero-offset cone remains a fallback. Four narrow cones leave most of the arena.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsBatteryCircuitAvoidActive,
            objectSelector: IsTrackedBatteryCircuitHelper,
            leashPointProducer: () => ArenaCenter.Protector,
            leashRadius: 40.0f,
            rotationDegrees: BatteryCircuitRotationIncrementDegrees,
            radius: 30.0f,
            arcDegrees: 30.0f);

        // The center Electrowhirl and placed Bombardment circles are independently cast helpers.
        // Explicit geometry replaces the former ally-follow heuristic that was inside the first
        // Electrowhirl circle for both vulnerability gains in the baseline run.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInProtectorCombat,
            objectSelector: bc => EnemyAction.ElectrowhirlCasts.Contains(bc.CastingSpellId),
            radiusProducer: bc => 6.0f,
            priority: AvoidancePriority.High));

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInProtectorCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Bombardment,
            radiusProducer: bc => 5.0f,
            locationProducer: bc => bc.SpellCastInfo.CastLocation,
            priority: AvoidancePriority.High));

        // Fulminous Fence changes collision without changing the service-navigation mesh. The
        // 2026-08-21 capture showed RB accepting a route across the active (-8,-92)-to-(-4,-96)
        // diagonal and then selecting a destination back across it. Registering every possible
        // segment once and enabling only the live map-effect layout makes RB's avoid-path check see
        // the same barriers as the client while preserving every corridor in the other layouts.
        RegisterProtectorFenceAvoids();

        // Zander is encounter-owned by Duty Mechanic. A 2026-08-21 run showed that disabling
        // SideStep only after Rearguard started was too late: SideStep published two shapes on the
        // same tick, their union removed every navigable destination, and the later Foreguard was
        // left unhandled while SideStep remained disabled. Register both Slitherbane variants and
        // Soulbane Saber here so one owner supplies every narrow line throughout the encounter.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInZanderCombat,
            objectSelector: bc => EnemyAction.ZanderLineCasts.Contains(bc.CastingSpellId),
            width: 4.0f,
            length: 20.0f,
            priority: AvoidancePriority.High);

        // Foreguard's helper heading already describes the damaging forward half. Keeping this
        // separate from Rearguard preserves the action-specific heading correction proven below.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInZanderCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SlitherbaneForeguardAoe &&
                IsSelectedSlitherbaneHazard(bc),
            leashPointProducer: () => ArenaCenter.ZandertheSnakeskinner,
            leashRadius: 19.5f,
            rotationDegrees: 0.0f,
            radius: 20.0f,
            arcDegrees: 180.0f);

        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInZanderCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SlitherbaneRearguardAoe &&
                IsSelectedSlitherbaneHazard(bc),
            leashPointProducer: () => ArenaCenter.ZandertheSnakeskinner,
            // The 2026-08-21 capture produced two vulnerabilities from action 36593 in the half
            // opposite the helper heading while the zero-offset cone considered the player safe.
            // Its damaging cast rotation is therefore 180 degrees from that actor heading.
            leashRadius: 19.5f,
            rotationDegrees: 180.0f,
            radius: 20.0f,
            arcDegrees: 180.0f);

        // Slitherbane cones and delayed Burst rectangles share one activation-ordered sequence.
        // IsSelectedSlitherbaneHazard exposes at most the next two and suppresses an exactly
        // opposite second hazard until the first resolves, preserving an actual safe half.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInZanderCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SlitherbaneBurst &&
                IsSelectedSlitherbaneHazard(bc),
            width: 40.0f,
            length: 20.0f,
            priority: AvoidancePriority.High);

        // Phase-one Soulbane Burst is not part of the ordered Slitherbane queue, but SideStep used
        // to own it. Publish its measured half-arena rectangle now that Duty Mechanic owns Zander.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInZanderCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SoulbaneBurst,
            width: 40.0f,
            length: 20.0f,
            priority: AvoidancePriority.High);

        // Syntheslither helpers resolve 0.6 seconds apart. Publishing only the next two 90-degree
        // cones mirrors the useful look-ahead without combining all four future quadrants into an
        // impossible avoid. Syntheslean is included because SideStep previously owned its opener.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInZanderCombat,
            objectSelector: bc => IsAmongNextCasters(bc, EnemyAction.SyntheslitherCasts, 2),
            leashPointProducer: () => ArenaCenter.ZandertheSnakeskinner,
            leashRadius: 19.5f,
            rotationDegrees: 0.0f,
            radius: 19.0f,
            arcDegrees: 90.0f);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            IsInCommanderCombat,
            // Sentries spawned exactly at X=-117/-83 and Z=190/224 around the (-100, 207)
            // center, corroborating a 17-yalm square half-width. Preserving the complete 34-yalm
            // platform is important because corner-safe Enhanced Mobility and expanding Aerial
            // Offensive patterns consume most of the center; navigation still enforces the wall.
            innerWidth: 34.0f,
            innerHeight: 34.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.VanguardCommanderR8],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SafetyInspectionChamber,
            // The post-Electrowave floor is 24 by 40 yalms. The former 22-by-38 boundary left one
            // yalm of clearance and the 2026-08-21 laser capture still reached X=-11.55. A two-yalm
            // inset accounts for the player capsule and navigation overshoot without consuming the
            // central laser lanes.
            innerWidth: ProtectorNavigationWidth,
            innerHeight: ProtectorNavigationHeight,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Protector],
            priority: AvoidancePriority.High);

        // The first Homing Cannon wave starts about 5.15 seconds after Search and Destroy's visual
        // cast finishes in two captures. The turret rotations are not committed before their own
        // casts, so forecasting line headings would be unsafe; instead, use that warning window to
        // stage within a smaller central rectangle, then release it as soon as any laser begins so
        // the real cast geometry can choose the correct lane.
        AvoidanceHelpers.AddAvoidSquareDonut(
            IsSearchAndDestroyPrepositionActive,
            innerWidth: SearchAndDestroyStagingWidth,
            innerHeight: SearchAndDestroyStagingHeight,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Protector],
            priority: AvoidancePriority.High);

        // The encounter geometry and live arena agree on a 19.5-yalm starting radius that contracts
        // to 17 yalms when Electrothermia resolves. The pending ring proactively moves the player
        // inside the future boundary; the two outer donuts then provide the appropriate physical
        // leash before and after the transition without permanently discarding the opening strip.
        AvoidanceHelpers.AddAvoidDonut(
            () => IsInZanderCombat() && !zanderArenaShrunk,
            () => ArenaCenter.ZandertheSnakeskinner,
            outerRadius: 90.0f,
            innerRadius: 19.5f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            IsZanderArenaShrinkPending,
            () => ArenaCenter.ZandertheSnakeskinner,
            outerRadius: 19.5f,
            innerRadius: 17.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => IsInZanderCombat() && zanderArenaShrunk,
            () => ArenaCenter.ZandertheSnakeskinner,
            outerRadius: 90.0f,
            innerRadius: 17.0f,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseMotionSensorHold("Leaving Vanguard");
        ResetEnhancedMobilityState();
        ResetSearchAndDestroyState();
        ResetBatteryCircuitState();
        ResetProtectorFenceState();
        ResetZanderState();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (currentSubZoneId == SubZoneId.CentralGarage)
        {
            bool commanderOwnershipActive = IsInCommanderCombat();
            if (commanderOwnershipActive)
            {
                UpdateEnhancedMobilityState();
            }
            else
            {
                ResetEnhancedMobilityState();
            }

            // The 2026-08-21 capture proved SideStep under-sized Aerial Offensive and released
            // Enhanced Mobility before Rapid Rotary. Disable it only while the live first boss and
            // player are in combat; the next tick after death, victory, or arena exit restores it.
            SidestepPlugin.Enabled = !commanderOwnershipActive;
            ReleaseMotionSensorHold("Protector arena exited");
            ResetSearchAndDestroyState();
            ResetBatteryCircuitState();
            ResetProtectorFenceState();
            ResetZanderState();
        }
        else if (currentSubZoneId == SubZoneId.SafetyInspectionChamber)
        {
            ResetEnhancedMobilityState();
            UpdateSearchAndDestroyState();
            UpdateBatteryCircuitState();
            UpdateProtectorFenceState();
            SidestepPlugin.Enabled = false;
        }
        else if (currentSubZoneId == SubZoneId.VanguardControlRoom)
        {
            ResetEnhancedMobilityState();
            ReleaseMotionSensorHold("Protector arena exited");
            ResetSearchAndDestroyState();
            ResetBatteryCircuitState();
            ResetProtectorFenceState();
            bool zanderOwnershipActive = IsInZanderCombat();
            if (zanderOwnershipActive)
            {
                UpdateZanderArenaState();
            }
            else
            {
                ResetZanderState();
            }

            // SideStep must be off before phase-one casts begin, not toggled in reaction to
            // Rearguard. This removes the same-tick duplicate-avoid race and leaves one coherent
            // activation queue responsible for every Zander movement mechanic.
            SidestepPlugin.Enabled = !zanderOwnershipActive;
        }
        else
        {
            ResetEnhancedMobilityState();
            ReleaseMotionSensorHold("Protector arena exited");
            ResetSearchAndDestroyState();
            ResetBatteryCircuitState();
            ResetProtectorFenceState();
            ResetZanderState();
            SidestepPlugin.Enabled = true;
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.CentralGarage => await VanguardCommanderR8(),
            SubZoneId.SafetyInspectionChamber => await Protector(),
            SubZoneId.VanguardControlRoom => await ZandertheSnakeskinner(),
            _ => false,
        };

        return result;
    }

    /// <summary>
    /// Boss 1: Vanguard Commander R8.
    /// </summary>
    private static Task<bool> VanguardCommanderR8()
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Latches Enhanced Mobility's helper-authored safe band through the delayed Rapid Rotary hits.
    /// </summary>
    private void UpdateEnhancedMobilityState()
    {
        BattleCharacter helper = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(candidate => candidate.IsValid && candidate.IsCasting &&
                EnemyAction.EnhancedMobilityHelperCasts.Contains(candidate.CastingSpellId))
            .OrderBy(candidate => candidate.SpellCastInfo.RemainingCastTime)
            .ThenBy(candidate => candidate.ObjectId)
            .FirstOrDefault();

        if (helper != null)
        {
            enhancedMobilityOrigin = helper.Location;
            enhancedMobilitySafeBand = EnemyAction.EnhancedMobilityInCasts.Contains(helper.CastingSpellId)
                ? EnhancedMobilitySafeBand.Center
                : EnhancedMobilitySafeBand.Corners;

            // The final sector resolves about 1.9 seconds after the helper cast finishes. A 0.6-second
            // cleanup margin covers RB polling and status-application delay without carrying the
            // radial avoid into the next Dispatch pattern.
            enhancedMobilityEndsAtUtc = DateTime.UtcNow +
                helper.SpellCastInfo.RemainingCastTime + EnhancedMobilityPostCastDuration;
        }
        else if (DateTime.UtcNow >= enhancedMobilityEndsAtUtc)
        {
            ResetEnhancedMobilityState();
        }
    }

    /// <summary>
    /// Computes the world origin of Enhanced Mobility's laterally offset opening rectangle.
    /// </summary>
    /// <param name="caster">Center helper whose action selects the right or left offset.</param>
    /// <returns>The rectangle origin seven yalms to the indicated side of the helper.</returns>
    private static Vector3 GetEnhancedMobilitySideOrigin(BattleCharacter caster)
    {
        float lateralOffset = EnemyAction.EnhancedMobilityRightCasts.Contains(caster.CastingSpellId)
            ? 7f
            : -7f;

        // FFXIV heading zero points along +Z. Its right-hand vector is therefore
        // (cos(heading), -sin(heading)) in the horizontal X/Z plane.
        return new Vector3(
            caster.Location.X + (MathF.Cos(caster.Heading) * lateralOffset),
            caster.Location.Y,
            caster.Location.Z - (MathF.Sin(caster.Heading) * lateralOffset));
    }

    /// <summary>
    /// Selects an Enhanced Mobility helper whose later rotation leaves arena corners safe.
    /// </summary>
    /// <param name="candidate">Current-frame helper candidate.</param>
    /// <returns><see langword="true"/> for a live corner-safe helper cast.</returns>
    private static bool IsEnhancedMobilityOutCaster(BattleCharacter candidate)
    {
        return candidate != null && candidate.IsValid && candidate.IsCasting &&
            EnemyAction.EnhancedMobilityOutCasts.Contains(candidate.CastingSpellId);
    }

    /// <summary>
    /// Selects an Enhanced Mobility helper whose later rotation leaves arena center safe.
    /// </summary>
    /// <param name="candidate">Current-frame helper candidate.</param>
    /// <returns><see langword="true"/> for a live center-safe helper cast.</returns>
    private static bool IsEnhancedMobilityInCaster(BattleCharacter candidate)
    {
        return candidate != null && candidate.IsValid && candidate.IsCasting &&
            EnemyAction.EnhancedMobilityInCasts.Contains(candidate.CastingSpellId);
    }

    /// <summary>
    /// Returns whether Rapid Rotary currently makes the inner 17 yalms unsafe.
    /// </summary>
    /// <returns><see langword="true"/> while the player should remain in a corner-safe region.</returns>
    private bool IsEnhancedMobilityOutAvoidActive()
    {
        return IsInCommanderCombat() &&
            enhancedMobilitySafeBand == EnhancedMobilitySafeBand.Corners &&
            DateTime.UtcNow < enhancedMobilityEndsAtUtc;
    }

    /// <summary>
    /// Returns whether Rapid Rotary currently makes the 11-to-28-yalm ring unsafe.
    /// </summary>
    /// <returns><see langword="true"/> while the player should remain in the center-safe region.</returns>
    private bool IsEnhancedMobilityInAvoidActive()
    {
        return IsInCommanderCombat() &&
            enhancedMobilitySafeBand == EnhancedMobilitySafeBand.Center &&
            DateTime.UtcNow < enhancedMobilityEndsAtUtc;
    }

    /// <summary>
    /// Clears Enhanced Mobility's latched geometry after combat, expiry, or arena transition.
    /// </summary>
    private void ResetEnhancedMobilityState()
    {
        enhancedMobilityEndsAtUtc = DateTime.MinValue;
        enhancedMobilityOrigin = ArenaCenter.VanguardCommanderR8;
        enhancedMobilitySafeBand = EnhancedMobilitySafeBand.None;
    }

    /// <summary>
    /// Boss 2: Protector.
    /// </summary>
    private async Task<bool> Protector()
    {
        // The acceleration-bomb hold must preempt every movement source in its final second. Until
        // then, normal avoidance remains free to resolve the paired Blast Cannon lines.
        if (HandleMotionSensorHold())
        {
            return true;
        }

        if (await HandleBatteryCircuitAsync())
        {
            return true;
        }

        await FollowDodgeSpells();
        return false;
    }

    /// <summary>
    /// Boss 3: Zander the Snakeskinner.
    /// </summary>
    private static Task<bool> ZandertheSnakeskinner()
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Stops movement and actions during Acceleration Bomb's final resolution window.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> while the bot must do nothing; otherwise <see langword="false"/> so
    /// ordinary avoidance, healing, and rotation remain schedulable.
    /// </returns>
    private bool HandleMotionSensorHold()
    {
        Aura accelerationBomb = Core.Player.Auras?.AuraList.FirstOrDefault(aura =>
            PlayerAura.AccelerationBombs.Contains(aura.Id));

        if (accelerationBomb == null || accelerationBomb.TimeLeft > MotionSensorHoldThresholdSeconds)
        {
            ReleaseMotionSensorHold("Acceleration Bomb is not imminent");
            return false;
        }

        CapabilityManager.Update(
            motionSensorMovementHandle,
            CapabilityFlags.Movement,
            accelerationBomb.TimespanLeft.Add(TimeSpan.FromMilliseconds(500)),
            "Holding still for Protector's Acceleration Bomb");
        motionSensorMovementOwned = true;

        // Acceleration Bomb checks both movement and action state at expiry. Clear the target to
        // suppress auto-attacks, cancel a cast that would finish inside the window, and stop every
        // RB movement layer. The target is reacquired normally after the aura disappears.
        ActionManager.StopCasting();
        Core.Me.ClearTarget();
        Navigator.Stop();
        Navigator.PlayerMover.MoveStop();
        MovementManager.MoveStop();
        return true;
    }

    /// <summary>
    /// Releases only the movement lease used by Motion Sensor.
    /// </summary>
    /// <param name="reason">Lifecycle reason recorded by the capability manager.</param>
    private void ReleaseMotionSensorHold(string reason)
    {
        if (!motionSensorMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(motionSensorMovementHandle, CapabilityFlags.Movement, reason);
        motionSensorMovementOwned = false;
    }

    /// <summary>
    /// Uses Search and Destroy's long visual lead to stage centrally until the first committed
    /// Homing Cannon heading becomes available.
    /// </summary>
    private void UpdateSearchAndDestroyState()
    {
        if (!IsInProtectorCombat())
        {
            ResetSearchAndDestroyState();
            return;
        }

        List<BattleCharacter> casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(caster => caster.IsValid && caster.IsCasting)
            .ToList();
        BattleCharacter visualCaster = casters.FirstOrDefault(caster =>
            caster.CastingSpellId == EnemyAction.SearchAndDestroy);

        if (visualCaster != null)
        {
            // Both captures placed the first laser approximately 5.15 seconds after the visual
            // completed. Recomputing from remaining cast time keeps one stable absolute deadline
            // even when RB first observes the cast between bot ticks.
            searchAndDestroyPrepositionEndsAtUtc = DateTime.UtcNow +
                visualCaster.SpellCastInfo.RemainingCastTime +
                SearchAndDestroyPostVisualDelay;
            return;
        }

        // Laser headings become authoritative only when Homing Cannon begins. Releasing the
        // staging rectangle on that same frame prevents it from competing with the real lanes.
        if (casters.Any(caster => caster.CastingSpellId == EnemyAction.HomingCannon) ||
            DateTime.UtcNow >= searchAndDestroyPrepositionEndsAtUtc)
        {
            ResetSearchAndDestroyState();
        }
    }

    /// <summary>
    /// Returns whether Search and Destroy's pre-laser central staging boundary should be active.
    /// </summary>
    /// <returns><see langword="true"/> after the visual begins and before a laser commits.</returns>
    private bool IsSearchAndDestroyPrepositionActive()
    {
        return IsInProtectorCombat() &&
            searchAndDestroyPrepositionEndsAtUtc != DateTime.MinValue &&
            DateTime.UtcNow < searchAndDestroyPrepositionEndsAtUtc;
    }

    /// <summary>
    /// Clears the Search and Destroy pre-position deadline on resolution, death, or arena exit.
    /// </summary>
    private void ResetSearchAndDestroyState()
    {
        searchAndDestroyPrepositionEndsAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Latches the two Battery Circuit helpers that own the rotating cone sequence.
    /// </summary>
    private void UpdateBatteryCircuitState()
    {
        if (!IsInProtectorCombat())
        {
            ResetBatteryCircuitState("Protector combat ended");
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        List<BattleCharacter> firstConeCasters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(caster => caster.IsValid && caster.IsCasting &&
                caster.CastingSpellId == EnemyAction.BatteryCircuitFirst)
            .OrderBy(caster => caster.ObjectId)
            .ToList();

        if (firstConeCasters.Count > 0)
        {
            foreach (BattleCharacter caster in firstConeCasters)
            {
                batteryCircuitHelperIds.Add(caster.ObjectId);
            }

            if (batteryCircuitFirstActivationAtUtc == DateTime.MinValue)
            {
                TimeSpan longestRemainingCast = firstConeCasters
                    .Max(caster => caster.SpellCastInfo.RemainingCastTime);
                batteryCircuitFirstActivationAtUtc = nowUtc + longestRemainingCast;
                batteryCircuitEndsAtUtc = batteryCircuitFirstActivationAtUtc + BatteryCircuitPostCastDuration;

                BattleCharacter anchorHelper = SelectBatteryCircuitAnchorHelper(firstConeCasters);
                batteryCircuitAnchorHelperId = anchorHelper.ObjectId;
                batteryCircuitAnchorInitialHeading = anchorHelper.Heading;

                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    string helpers = string.Join(",", firstConeCasters.Select(caster =>
                        $"0x{caster.ObjectId:X8}@{caster.Heading:F3}"));
                    Logger.Information(
                        $"[MechanicDiag] BATTERY_CIRCUIT_SEQUENCE helpers=[{helpers}] " +
                        $"anchor=0x{batteryCircuitAnchorHelperId:X8} " +
                        $"initialHeading={batteryCircuitAnchorInitialHeading:F3} " +
                        $"firstActivationMs={longestRemainingCast.TotalMilliseconds:F0} " +
                        $"pulses={BatteryCircuitPulseCount} intervalMs={BatteryCircuitPulseInterval.TotalMilliseconds:F0}.");
                }
            }
        }
        else if (nowUtc >= batteryCircuitEndsAtUtc)
        {
            ResetBatteryCircuitState("Battery Circuit sequence expired");
        }
    }

    /// <summary>
    /// Keeps the player in the recently vacated wedge immediately behind Battery Circuit's leading
    /// cone. Registered avoidance remains authoritative for fences, Bombardment, Electrowhirl, and
    /// arena bounds; this handler resumes the same rotating corridor after an emergency dodge.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> while actively traveling or yielding to emergency avoidance;
    /// otherwise <see langword="false"/> so healing and rotation remain schedulable.
    /// </returns>
    private async Task<bool> HandleBatteryCircuitAsync()
    {
        if (!IsBatteryCircuitAvoidActive())
        {
            ReleaseBatteryCircuitMovement("Battery Circuit is not active");
            return false;
        }

        CapabilityManager.Update(
            batteryCircuitMovementHandle,
            CapabilityFlags.Movement,
            BatteryCircuitMovementLeaseMilliseconds,
            "Holding Battery Circuit's trailing safe corridor");
        batteryCircuitMovementOwned = true;

        float anchorHeading = GetBatteryCircuitAnchorHeading(out bool liveHeading);
        int pulseIndex = GetBatteryCircuitPendingPulseIndex(DateTime.UtcNow);

        // AvoidanceManager owns movement until the player has cleared a registered hazard. Do not
        // stop its mover here; the movement-only lease prevents the combat routine from competing,
        // and the trailing anchor is reacquired on the first safe tick.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            bool cachedDestinationCurrent = batteryCircuitDestinationActive &&
                pulseIndex == batteryCircuitDestinationPulseIndex &&
                GetAngularDistance(batteryCircuitDestinationHeading, anchorHeading) <
                DegreesToRadians(BatteryCircuitDestinationReselectionDegrees);
            LogBatteryCircuitAnchorUpdate(
                pulseIndex,
                anchorHeading,
                liveHeading,
                cachedDestinationCurrent,
                batteryCircuitDestination);
            batteryCircuitMoving = false;
            return true;
        }

        // The candidate search samples multiple radii and chords, so retain its result for the
        // current pulse. Re-select only when the helper advances, a live heading resynchronizes, or
        // a newly registered dynamic hazard invalidates the endpoint or approach.
        bool headingChanged = batteryCircuitDestinationActive &&
            GetAngularDistance(batteryCircuitDestinationHeading, anchorHeading) >=
            DegreesToRadians(BatteryCircuitDestinationReselectionDegrees);
        bool destinationBlocked = batteryCircuitDestinationActive &&
            (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(batteryCircuitDestination)) ||
             !IsBatteryCircuitApproachClear(Core.Player.Location, batteryCircuitDestination));
        bool destinationNeedsSelection = !batteryCircuitDestinationActive ||
            pulseIndex != batteryCircuitDestinationPulseIndex ||
            headingChanged ||
            destinationBlocked;

        if (destinationNeedsSelection)
        {
            batteryCircuitDestinationActive = TrySelectBatteryCircuitDestination(
                anchorHeading,
                out batteryCircuitDestination);
            if (batteryCircuitDestinationActive)
            {
                batteryCircuitDestinationHeading = anchorHeading;
                batteryCircuitDestinationPulseIndex = pulseIndex;
            }
        }

        LogBatteryCircuitAnchorUpdate(
            pulseIndex,
            anchorHeading,
            liveHeading,
            batteryCircuitDestinationActive,
            batteryCircuitDestination);

        if (!batteryCircuitDestinationActive)
        {
            StopBatteryCircuitOwnedMovement();
            if (!batteryCircuitDestinationUnavailableLogged)
            {
                batteryCircuitDestinationUnavailableLogged = true;
                Logger.Warning(
                    "[Duty Mechanic] Battery Circuit has no straight-line trailing anchor outside " +
                    "registered hazards; holding combat-routine movement until a safe corridor reopens.");
            }

            return false;
        }

        batteryCircuitDestinationUnavailableLogged = false;
        if (Core.Player.Distance2D(batteryCircuitDestination) <= BatteryCircuitArrivalTolerance)
        {
            StopBatteryCircuitOwnedMovement();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        // Candidate selection samples the complete chord against every registered avoid before
        // direct movement begins. This prevents a short rotating step from cutting across one of
        // Fulminous Fence's off-navmesh walls while avoiding a slow service-navigation detour that
        // cannot keep pace with the half-second pulse interval.
        Navigator.Stop();
        MovementManager.MoveStop();
        Navigator.PlayerMover.MoveTowards(batteryCircuitDestination);
        batteryCircuitMoving = true;
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Chooses and latches the closer of the two opposite rotating corridors at sequence start.
    /// The selected helper is never switched mid-sequence because crossing to its opposite lane
    /// would traverse an active or imminent cone.
    /// </summary>
    /// <param name="helpers">Helpers casting the first synchronized cone.</param>
    /// <returns>The helper whose trailing corridor requires the least safe travel.</returns>
    private static BattleCharacter SelectBatteryCircuitAnchorHelper(IReadOnlyList<BattleCharacter> helpers)
    {
        BattleCharacter bestHelper = helpers[0];
        float bestScore = float.MaxValue;

        foreach (BattleCharacter helper in helpers)
        {
            Vector3 idealDestination = CreateBatteryCircuitAnchorPoint(
                helper.Heading,
                BatteryCircuitIdealTrailingOffsetDegrees,
                BatteryCircuitIdealRadius);
            float score = Core.Player.Distance2D(idealDestination);

            if (TrySelectBatteryCircuitDestination(helper.Heading, out Vector3 safeDestination))
            {
                score = Core.Player.Distance2D(safeDestination);
            }
            else
            {
                // Prefer any helper with an immediately safe candidate over a geometrically closer
                // lane whose fence or overlapping AOE currently blocks every sampled approach.
                score += BatteryCircuitUnavailableLanePenalty;
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestHelper = helper;
            }
        }

        return bestHelper;
    }

    /// <summary>
    /// Resolves the selected helper's current heading, falling back to the captured 34-pulse timing
    /// model if its frame-scoped actor wrapper is temporarily unavailable.
    /// </summary>
    /// <param name="liveHeading">Set when the returned heading came from the current helper actor.</param>
    /// <returns>The center heading of the next cone that owns the trailing corridor.</returns>
    private float GetBatteryCircuitAnchorHeading(out bool liveHeading)
    {
        BattleCharacter helper = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(candidate => candidate.IsValid &&
                candidate.ObjectId == batteryCircuitAnchorHelperId);
        if (helper != null)
        {
            liveHeading = true;
            return helper.Heading;
        }

        liveHeading = false;
        int pulseIndex = GetBatteryCircuitPendingPulseIndex(DateTime.UtcNow);
        float predictedHeading = batteryCircuitAnchorInitialHeading +
            DegreesToRadians(pulseIndex * BatteryCircuitRotationIncrementDegrees);
        return NormalizeRadians(predictedHeading);
    }

    /// <summary>
    /// Computes which cone is pending from the cast-finish time. Index zero is the first cast;
    /// immediately after each half-second activation the pending index advances by one.
    /// </summary>
    /// <param name="nowUtc">Current UTC time from the bot thread.</param>
    /// <returns>A zero-based pending pulse index clamped to the 34-cast sequence.</returns>
    private int GetBatteryCircuitPendingPulseIndex(DateTime nowUtc)
    {
        if (batteryCircuitFirstActivationAtUtc == DateTime.MinValue ||
            nowUtc < batteryCircuitFirstActivationAtUtc)
        {
            return 0;
        }

        double elapsedSeconds = (nowUtc - batteryCircuitFirstActivationAtUtc).TotalSeconds;
        int resolvedPulses = 1 + (int)Math.Floor(elapsedSeconds / BatteryCircuitPulseInterval.TotalSeconds);
        return Math.Min(BatteryCircuitPulseCount - 1, resolvedPulses);
    }

    /// <summary>
    /// Selects the closest candidate within the latched trailing wedge whose endpoint and complete
    /// straight-line approach remain outside every currently registered avoid.
    /// </summary>
    /// <param name="coneHeading">Center heading of the selected helper's pending cone.</param>
    /// <param name="destination">Selected world position when a safe approach exists.</param>
    /// <returns><see langword="true"/> when a fence- and AOE-safe candidate was found.</returns>
    private static bool TrySelectBatteryCircuitDestination(float coneHeading, out Vector3 destination)
    {
        destination = default;
        float bestScore = float.MaxValue;
        bool found = false;

        for (int angleIndex = 0; angleIndex < BatteryCircuitCandidateAngleCount; angleIndex++)
        {
            float trailingOffset = BatteryCircuitIdealTrailingOffsetDegrees +
                (angleIndex * BatteryCircuitCandidateAngleStepDegrees);
            foreach (float radius in BatteryCircuitCandidateRadii)
            {
                Vector3 candidate = CreateBatteryCircuitAnchorPoint(coneHeading, trailingOffset, radius);
                if (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(candidate)) ||
                    !IsBatteryCircuitApproachClear(Core.Player.Location, candidate))
                {
                    continue;
                }

                float score = Core.Player.Distance2D(candidate) +
                    (angleIndex * BatteryCircuitAngularDeviationPenalty) +
                    (Math.Abs(radius - BatteryCircuitIdealRadius) * BatteryCircuitRadialDeviationPenalty);
                if (score < bestScore)
                {
                    bestScore = score;
                    destination = candidate;
                    found = true;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// Creates one trailing-wedge candidate. FFXIV heading zero points along +Z; the negative
    /// rotation increment means the recently vacated side is at a positive angular offset.
    /// </summary>
    /// <param name="coneHeading">Center heading of the pending cone.</param>
    /// <param name="trailingOffsetDegrees">Distance behind its trailing edge in degrees.</param>
    /// <param name="radius">Distance from Protector's arena center.</param>
    /// <returns>A world-space anchor at Protector's fixed elevation.</returns>
    private static Vector3 CreateBatteryCircuitAnchorPoint(
        float coneHeading,
        float trailingOffsetDegrees,
        float radius)
    {
        float trailingDirection = BatteryCircuitRotationIncrementDegrees < 0f ? 1f : -1f;
        float anchorHeading = coneHeading +
            DegreesToRadians(trailingOffsetDegrees * trailingDirection);
        return new Vector3(
            ArenaCenter.Protector.X + (MathF.Sin(anchorHeading) * radius),
            ArenaCenter.Protector.Y,
            ArenaCenter.Protector.Z + (MathF.Cos(anchorHeading) * radius));
    }

    /// <summary>
    /// Samples a direct movement chord densely enough that the one-yalm padded fence polygons and
    /// small Bombardment circles cannot fall between checks.
    /// </summary>
    /// <param name="start">Current player position.</param>
    /// <param name="end">Proposed trailing anchor.</param>
    /// <returns><see langword="true"/> when every sampled point is outside registered avoids.</returns>
    private static bool IsBatteryCircuitApproachClear(Vector3 start, Vector3 end)
    {
        float distance = start.Distance2D(end);
        int sampleCount = Math.Max(1, (int)Math.Ceiling(distance / BatteryCircuitPathSampleSpacing));
        for (int sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
        {
            float progress = (float)sampleIndex / sampleCount;
            Vector3 sample = new(
                start.X + ((end.X - start.X) * progress),
                start.Y + ((end.Y - start.Y) * progress),
                start.Z + ((end.Z - start.Z) * progress));
            if (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(sample)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Emits one diagnostic record per pending pulse or observed heading change so the next live
    /// run can distinguish sequence drift, blocked anchors, and movement latency without per-tick
    /// log spam.
    /// </summary>
    private void LogBatteryCircuitAnchorUpdate(
        int pulseIndex,
        float heading,
        bool liveHeading,
        bool destinationAvailable,
        Vector3 destination)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            return;
        }

        bool headingChanged = float.IsNaN(lastBatteryCircuitDiagnosticHeading) ||
            GetAngularDistance(lastBatteryCircuitDiagnosticHeading, heading) >=
            DegreesToRadians(BatteryCircuitDiagnosticHeadingThresholdDegrees);
        if (pulseIndex == lastBatteryCircuitDiagnosticPulseIndex && !headingChanged)
        {
            return;
        }

        lastBatteryCircuitDiagnosticPulseIndex = pulseIndex;
        lastBatteryCircuitDiagnosticHeading = heading;
        string target = destinationAvailable ? FormatProtectorCaptureLocation(destination) : "none";
        string distance = destinationAvailable
            ? Core.Player.Distance2D(destination).ToString("F3")
            : "none";
        Logger.Information(
            $"[MechanicDiag] BATTERY_CIRCUIT_ANCHOR pulse={pulseIndex + 1}/{BatteryCircuitPulseCount} " +
            $"helper=0x{batteryCircuitAnchorHelperId:X8} heading={heading:F3} " +
            $"headingSource={(liveHeading ? "live" : "predicted")} target={target} distance={distance} " +
            $"player={FormatProtectorCaptureLocation(Core.Player.Location)} " +
            $"avoids={AvoidanceManager.Avoids.Count} escapingAvoid={AvoidanceManager.IsRunningOutOfAvoid}.");
    }

    /// <summary>
    /// Stops only movement issued by the Battery Circuit anchor after arrival or destination loss.
    /// </summary>
    private void StopBatteryCircuitOwnedMovement()
    {
        if (!batteryCircuitMoving || AvoidanceManager.IsRunningOutOfAvoid)
        {
            batteryCircuitMoving = false;
            return;
        }

        Navigator.Stop();
        Navigator.PlayerMover.MoveStop();
        MovementManager.MoveStop();
        batteryCircuitMoving = false;
    }

    /// <summary>
    /// Releases the movement-only lease without disturbing emergency avoidance movement.
    /// </summary>
    /// <param name="reason">Lifecycle reason recorded by the capability manager.</param>
    private void ReleaseBatteryCircuitMovement(string reason)
    {
        StopBatteryCircuitOwnedMovement();
        if (!batteryCircuitMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(batteryCircuitMovementHandle, CapabilityFlags.Movement, reason);
        batteryCircuitMovementOwned = false;
    }

    /// <summary>
    /// Converts degrees to radians for FFXIV heading arithmetic.
    /// </summary>
    private static float DegreesToRadians(float degrees)
    {
        return degrees * (MathF.PI / 180f);
    }

    /// <summary>
    /// Normalizes a heading to the [0, 2π) interval.
    /// </summary>
    private static float NormalizeRadians(float radians)
    {
        float fullTurn = MathF.PI * 2f;
        float normalized = radians % fullTurn;
        return normalized < 0f ? normalized + fullTurn : normalized;
    }

    /// <summary>
    /// Returns the smaller unsigned separation between two circular headings.
    /// </summary>
    private static float GetAngularDistance(float first, float second)
    {
        float difference = Math.Abs(NormalizeRadians(first) - NormalizeRadians(second));
        return Math.Min(difference, (MathF.PI * 2f) - difference);
    }

    /// <summary>
    /// Returns whether the latched Battery Circuit sequence is still within its observed lifetime.
    /// </summary>
    /// <returns><see langword="true"/> while rotating cone geometry should remain active.</returns>
    private bool IsBatteryCircuitAvoidActive()
    {
        return IsInProtectorCombat() &&
            batteryCircuitHelperIds.Count > 0 &&
            batteryCircuitAnchorHelperId != 0 &&
            DateTime.UtcNow < batteryCircuitEndsAtUtc;
    }

    /// <summary>
    /// Selects a live helper whose heading owns one of Battery Circuit's rotating cones.
    /// </summary>
    /// <param name="candidate">Current-frame helper candidate.</param>
    /// <returns><see langword="true"/> when the helper was latched by the first cone cast.</returns>
    private bool IsTrackedBatteryCircuitHelper(BattleCharacter candidate)
    {
        return candidate != null && candidate.IsValid && batteryCircuitHelperIds.Contains(candidate.ObjectId);
    }

    /// <summary>
    /// Clears Battery Circuit helper identity and timeout state.
    /// </summary>
    /// <param name="reason">Lifecycle reason used to release owned movement.</param>
    private void ResetBatteryCircuitState(string reason = "Battery Circuit state reset")
    {
        ReleaseBatteryCircuitMovement(reason);
        batteryCircuitHelperIds.Clear();
        batteryCircuitAnchorHelperId = 0;
        batteryCircuitAnchorInitialHeading = 0f;
        batteryCircuitDestination = ArenaCenter.Protector;
        batteryCircuitDestinationHeading = 0f;
        batteryCircuitDestinationPulseIndex = -1;
        batteryCircuitDestinationActive = false;
        batteryCircuitFirstActivationAtUtc = DateTime.MinValue;
        batteryCircuitEndsAtUtc = DateTime.MinValue;
        lastBatteryCircuitDiagnosticHeading = float.NaN;
        lastBatteryCircuitDiagnosticPulseIndex = -1;
        batteryCircuitDestinationUnavailableLogged = false;
    }

    /// <summary>
    /// Registers the four mutually exclusive Fulminous Fence layouts as persistent avoid
    /// definitions whose conditions are selected from the live instance map-effect state.
    /// </summary>
    private void RegisterProtectorFenceAvoids()
    {
        foreach (ProtectorFenceLayoutDefinition definition in ProtectorFenceLayouts)
        {
            RegisterProtectorFenceLayout(definition);
        }
    }

    /// <summary>
    /// Adds one padded polygon per wall segment plus circular endpoint obstacles for a single
    /// Fulminous Fence preset. Separate polygons allow RB to route around real openings instead of
    /// treating the complete maze as one solid region.
    /// </summary>
    /// <param name="definition">Indexed wall and node selections for one map-effect layout.</param>
    private void RegisterProtectorFenceLayout(ProtectorFenceLayoutDefinition definition)
    {
        ProtectorFenceLayout layout = definition.Layout;
        foreach (int pairIndex in definition.SegmentPairIndices)
        {
            ProtectorFenceSegmentPair pair = ProtectorFenceSegmentPairs[pairIndex];
            Vector2[] polygon = CreateProtectorFenceSegmentPolygon(
                ProtectorFenceNodePositions[pair.StartNodeIndex],
                ProtectorFenceNodePositions[pair.EndNodeIndex]);
            AvoidanceManager.AddAvoidPolygon(
                condition: () => IsProtectorFenceLayoutActive(layout),
                leashPointProducer: () => ArenaCenter.Protector,
                leashRadius: ProtectorFenceLeashRadius,
                rotationProducer: _ => 0.0f,
                scaleProducer: _ => 1.0f,
                heightProducer: _ => 15.0f,
                pointsProducer: _ => polygon,
                locationProducer: location => location,
                collectionProducer: () => ProtectorFenceAvoidOrigin,
                priority: AvoidancePriority.High);
        }

        Vector3[] nodes = definition.NodeIndices
            .Select(index => ProtectorFenceNodePositions[index])
            .ToArray();
        AvoidanceManager.AddAvoid(new AvoidLocationInfo<Vector3>(
            condition: () => IsProtectorFenceLayoutActive(layout),
            locationProducer: location => location,
            radiusProducer: _ => ProtectorFenceNavigationRadius,
            collecionSelection: () => nodes,
            leashPointSelector: () => ArenaCenter.Protector,
            leashRadius: ProtectorFenceLeashRadius,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High));
    }

    /// <summary>
    /// Creates a world-aligned rectangle around one fence segment. The one-yalm half-width combines
    /// the measured 0.51-yalm collision radius with approximately half a yalm of player/navigation
    /// clearance, preventing accepted routes from grazing the physical wall.
    /// RB interprets polygon contours in counter-clockwise order. The 2026-08-21 Layout D capture
    /// showed the previous clockwise contour allowing a route through the middle of an active wall,
    /// even though its endpoint circles remained effective.
    /// </summary>
    /// <param name="start">First wall endpoint in world coordinates.</param>
    /// <param name="end">Second wall endpoint in world coordinates.</param>
    /// <returns>Four polygon points relative to Protector's arena center.</returns>
    private static Vector2[] CreateProtectorFenceSegmentPolygon(Vector3 start, Vector3 end)
    {
        float deltaX = end.X - start.X;
        float deltaZ = end.Z - start.Z;
        float length = (float)Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        float perpendicularX = -deltaZ / length * ProtectorFenceNavigationRadius;
        float perpendicularZ = deltaX / length * ProtectorFenceNavigationRadius;
        float centerX = ArenaCenter.Protector.X;
        float centerZ = ArenaCenter.Protector.Z;

        return
        [
            new(start.X - centerX - perpendicularX, start.Z - centerZ - perpendicularZ),
            new(end.X - centerX - perpendicularX, end.Z - centerZ - perpendicularZ),
            new(end.X - centerX + perpendicularX, end.Z - centerZ + perpendicularZ),
            new(start.X - centerX + perpendicularX, start.Z - centerZ + perpendicularZ),
        ];
    }

    /// <summary>
    /// Reads Protector's current instance map effect and activates the matching collision layout.
    /// RB exposes both a 32-bit unknown field and a 16-bit state; accepting the corroborated full
    /// event value or its unique low word keeps this compatible with how supported RB builds expose
    /// the same map-effect record.
    /// </summary>
    private void UpdateProtectorFenceState()
    {
        if (!IsInProtectorCombat())
        {
            ResetProtectorFenceState();
            return;
        }

        Director activeDirector = DirectorManager.ActiveDirector;
        InstanceContentDirector instanceDirector = activeDirector as InstanceContentDirector;
        MapEffect[] mapEffects = instanceDirector != null && instanceDirector.IsValid
            ? instanceDirector.MapEffects
            : [];

        UpdateProtectorFenceCapture(activeDirector, instanceDirector, mapEffects);
        UpdateProtectorParalysisDiagnostic();

        if (instanceDirector == null || !instanceDirector.IsValid)
        {
            return;
        }

        MapEffect? fenceEffect = null;
        bool arenaResetEffectActive = false;
        foreach (MapEffect effect in mapEffects)
        {
            if (effect.ID == ProtectorFenceMapEffectRecordId)
            {
                fenceEffect = effect;
            }
            else if (effect.ID == ProtectorArenaResetMapEffectRecordId &&
                     MatchesProtectorMapEffectState(effect, ProtectorArenaResetState, 0x0001))
            {
                arenaResetEffectActive = true;
            }
        }

        if (fenceEffect.HasValue)
        {
            MapEffect effect = fenceEffect.Value;
            bool mapEffectChanged = LogProtectorFenceMapEffectChange(effect);
            ProtectorFenceLayout resolvedLayout = ResolveProtectorFenceLayout(effect);
            if (resolvedLayout != ProtectorFenceLayout.Unknown)
            {
                SetProtectorFenceLayout(resolvedLayout, effect);
            }
            else if (mapEffectChanged)
            {
                Logger.Warning(
                    $"[MechanicDiag] PROTECTOR_FENCE_UNKNOWN id=0x{effect.ID:X2} " +
                    $"state=0x{effect.State:X4} flags=0x{effect.Flags:X2} unk=0x{effect.unk:X8}; " +
                    $"retaining={protectorFenceLayout}.");
            }
        }
        else if (arenaResetEffectActive)
        {
            SetProtectorFenceLayout(ProtectorFenceLayout.None, null);
        }
    }

    /// <summary>
    /// Records the player-facing contact result of crossing an active Fulminous Fence. Fence
    /// contact applies dispellable Paralysis rather than Vulnerability Up, so this encounter-local
    /// edge is the acceptance signal that wall-aware navigation must eliminate in the next live
    /// run. It remains behind the shared developer diagnostic switch and stores no aura wrapper.
    /// </summary>
    private void UpdateProtectorParalysisDiagnostic()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            protectorFenceParalysisWasPresent = false;
            return;
        }

        Auras playerAuras = Core.Player.Auras;
        if (playerAuras == null || !playerAuras.IsValid)
        {
            return;
        }

        Aura paralysis = playerAuras.AuraList.FirstOrDefault(aura =>
            aura != null &&
            !string.IsNullOrWhiteSpace(aura.Name) &&
            aura.Name.Equals("Paralysis", StringComparison.OrdinalIgnoreCase));
        bool paralysisIsPresent = paralysis != null;
        if (paralysisIsPresent && !protectorFenceParalysisWasPresent)
        {
            Logger.Warning(
                $"[MechanicDiag] PROTECTOR_FENCE_PARALYSIS_GAIN statusId={paralysis.Id} " +
                $"rawValue={paralysis.Value} source=0x{paralysis.CasterId:X8} " +
                $"layout={protectorFenceLayout} player={FormatProtectorCaptureLocation(Core.Player.Location)} " +
                $"hp={Core.Player.CurrentHealth}/{Core.Player.MaxHealth} avoids={AvoidanceManager.Avoids.Count} " +
                $"escapingAvoid={AvoidanceManager.IsRunningOutOfAvoid}.");
        }

        protectorFenceParalysisWasPresent = paralysisIsPresent;
    }

    /// <summary>
    /// Captures the complete director, map-effect, and nearby-object state around Fulminous Fence.
    /// The earlier packet-index-filtered path produced no record even though the unfiltered
    /// 2026-08-21 snapshot later exposed the scene-record transition. Three timed snapshots plus
    /// change-only records distinguish a transient map effect from an actor-described layout or a
    /// broken director exposure without flooding the rest of the dungeon log.
    /// </summary>
    /// <param name="activeDirector">RB's current director, which may not be an instance director.</param>
    /// <param name="instanceDirector">The current instance director when RB exposes one.</param>
    /// <param name="mapEffects">One stable bot-frame copy of every exposed map-effect record.</param>
    private void UpdateProtectorFenceCapture(
        Director activeDirector,
        InstanceContentDirector instanceDirector,
        MapEffect[] mapEffects)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ResetProtectorFenceCaptureState();
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        BattleCharacter fulminousCaster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor != null && actor.IsValid && actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.FulminousFence);
        bool fulminousIsCasting = fulminousCaster != null;

        if (fulminousIsCasting && !protectorFenceFulminousWasCasting)
        {
            TimeSpan remainingCastTime = fulminousCaster.SpellCastInfo.RemainingCastTime;
            protectorFenceCastFinishAtUtc = nowUtc +
                (remainingCastTime > TimeSpan.Zero ? remainingCastTime : TimeSpan.Zero);
            protectorFenceCaptureEndsAtUtc = protectorFenceCastFinishAtUtc +
                ProtectorFencePostResolutionCaptureDelay + ProtectorFenceCaptureGrace;
            protectorFenceCaptureCheckpoint = 1;
            lastProtectorFenceActorFingerprint = string.Empty;
            lastProtectorFenceDirectorFingerprint = string.Empty;
            lastProtectorFenceMapEffectsFingerprint = string.Empty;
            LogProtectorFenceCaptureSnapshot(
                "cast-observed",
                activeDirector,
                instanceDirector,
                mapEffects,
                nowUtc);
        }

        protectorFenceFulminousWasCasting = fulminousIsCasting;
        if (protectorFenceCaptureEndsAtUtc == DateTime.MinValue)
        {
            return;
        }

        bool checkpointLogged = false;
        if (protectorFenceCaptureCheckpoint == 1 && nowUtc >= protectorFenceCastFinishAtUtc)
        {
            protectorFenceCaptureCheckpoint = 2;
            checkpointLogged = true;
            LogProtectorFenceCaptureSnapshot(
                "cast-finish",
                activeDirector,
                instanceDirector,
                mapEffects,
                nowUtc);
        }
        else if (protectorFenceCaptureCheckpoint == 2 &&
                 nowUtc >= protectorFenceCastFinishAtUtc + ProtectorFencePostResolutionCaptureDelay)
        {
            protectorFenceCaptureCheckpoint = 3;
            checkpointLogged = true;
            LogProtectorFenceCaptureSnapshot(
                "post-fence",
                activeDirector,
                instanceDirector,
                mapEffects,
                nowUtc);
        }

        if (nowUtc <= protectorFenceCaptureEndsAtUtc && !checkpointLogged)
        {
            LogProtectorFenceCaptureChanges(activeDirector, instanceDirector, mapEffects, "state-change", false);
        }
        else if (nowUtc > protectorFenceCaptureEndsAtUtc)
        {
            protectorFenceCaptureEndsAtUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Records one forced checkpoint and an arena-bounded object inventory. The object inventory is
    /// intentionally limited to the three mechanic checkpoints; moving party members would make it
    /// unsuitable for change-based logging while their runtime types and IDs can reveal a fence
    /// representation that the cast-only collector cannot observe.
    /// </summary>
    /// <param name="stage">Capture checkpoint name.</param>
    /// <param name="activeDirector">RB's active director.</param>
    /// <param name="instanceDirector">Active instance director, when available.</param>
    /// <param name="mapEffects">All map effects exposed in the current bot frame.</param>
    /// <param name="nowUtc">Current bot-thread observation time.</param>
    private void LogProtectorFenceCaptureSnapshot(
        string stage,
        Director activeDirector,
        InstanceContentDirector instanceDirector,
        MapEffect[] mapEffects,
        DateTime nowUtc)
    {
        double relativeMilliseconds = protectorFenceCastFinishAtUtc == DateTime.MinValue
            ? 0d
            : (nowUtc - protectorFenceCastFinishAtUtc).TotalMilliseconds;
        Logger.Information(FormattableString.Invariant(
            $"[MechanicDiag] PROTECTOR_FENCE_CAPTURE stage={stage} relativeToCastFinishMs={relativeMilliseconds:F0} player={FormatProtectorCaptureLocation(Core.Player.Location)}."));

        LogProtectorFenceCaptureChanges(activeDirector, instanceDirector, mapEffects, stage, true);

        List<GameObject> nearbyObjects = GetProtectorCaptureObjects();
        string objects = nearbyObjects.Count == 0
            ? "<empty>"
            : string.Join("; ", nearbyObjects.Select(FormatProtectorCaptureObject));
        Logger.Information(
            $"[MechanicDiag] PROTECTOR_FENCE_OBJECTS stage={stage} count={nearbyObjects.Count} " +
            $"objects=[{objects}].");
    }

    /// <summary>
    /// Logs director, full map-effect, and fence-actor fingerprints only when they change between
    /// forced checkpoints. Fence actor base ID 0x4255 is evidence-only in this capture; it must not
    /// select a collision preset until a live run proves which positions and lifecycle are exposed.
    /// </summary>
    /// <param name="activeDirector">RB's active director.</param>
    /// <param name="instanceDirector">Active instance director, when available.</param>
    /// <param name="mapEffects">All map effects exposed in the current bot frame.</param>
    /// <param name="stage">Checkpoint name or state-change label.</param>
    /// <param name="force">Whether to log even when the fingerprint is unchanged.</param>
    private void LogProtectorFenceCaptureChanges(
        Director activeDirector,
        InstanceContentDirector instanceDirector,
        MapEffect[] mapEffects,
        string stage,
        bool force)
    {
        string directorFingerprint = FormatProtectorDirector(activeDirector, instanceDirector);
        if (force || directorFingerprint != lastProtectorFenceDirectorFingerprint)
        {
            lastProtectorFenceDirectorFingerprint = directorFingerprint;
            Logger.Information(
                $"[MechanicDiag] PROTECTOR_FENCE_DIRECTOR stage={stage} {directorFingerprint}.");
        }

        string mapEffectsFingerprint = FormatProtectorMapEffects(mapEffects);
        if (force || mapEffectsFingerprint != lastProtectorFenceMapEffectsFingerprint)
        {
            lastProtectorFenceMapEffectsFingerprint = mapEffectsFingerprint;
            Logger.Information(
                $"[MechanicDiag] PROTECTOR_FENCE_MAP_SNAPSHOT stage={stage} count={mapEffects.Length} " +
                $"effects=[{mapEffectsFingerprint}].");
        }

        List<GameObject> fenceActors = GetProtectorCaptureObjects()
            .Where(actor => actor.BaseId == EnemyNpc.FulminousFenceBaseId)
            .ToList();
        string actorFingerprint = fenceActors.Count == 0
            ? "<empty>"
            : string.Join("; ", fenceActors.Select(FormatProtectorCaptureObject));
        if (force || actorFingerprint != lastProtectorFenceActorFingerprint)
        {
            lastProtectorFenceActorFingerprint = actorFingerprint;
            Logger.Information(
                $"[MechanicDiag] PROTECTOR_FENCE_ACTORS stage={stage} count={fenceActors.Count} " +
                $"actors=[{actorFingerprint}].");
        }
    }

    /// <summary>
    /// Formats the active director without dereferencing instance-only fields on an invalid or
    /// differently typed director. Pointer and map-array address distinguish an absent director
    /// from a valid director whose public map-effect array is empty.
    /// </summary>
    private static string FormatProtectorDirector(
        Director activeDirector,
        InstanceContentDirector instanceDirector)
    {
        if (activeDirector == null)
        {
            return "type=<none> valid=False pointer=<none> dungeonId=<none> mapEffectsAddr=<none>";
        }

        string pointer = FormattableString.Invariant($"0x{activeDirector.Pointer.ToInt64():X}");
        if (instanceDirector == null || !instanceDirector.IsValid)
        {
            return $"type={activeDirector.GetType().FullName} valid={activeDirector.IsValid} " +
                $"pointer={pointer} dungeonId=<none> mapEffectsAddr=<none>";
        }

        string mapEffectsAddress = FormattableString.Invariant(
            $"0x{instanceDirector.MapEffectsAddr.ToInt64():X}");
        return $"type={activeDirector.GetType().FullName} valid=True pointer={pointer} " +
            $"dungeonId={instanceDirector.DungeonId} mapEffectsAddr={mapEffectsAddress}";
    }

    /// <summary>
    /// Formats every map-effect field in deterministic order so a transient non-0x0D record is
    /// preserved and array reordering alone does not produce diagnostic noise.
    /// </summary>
    private static string FormatProtectorMapEffects(MapEffect[] mapEffects)
    {
        if (mapEffects.Length == 0)
        {
            return "<empty>";
        }

        return string.Join("; ", mapEffects
            .OrderBy(effect => effect.ID)
            .ThenBy(effect => effect.unk)
            .ThenBy(effect => effect.State)
            .ThenBy(effect => effect.Flags)
            .Select(effect => FormattableString.Invariant(
                $"id=0x{effect.ID:X2} state=0x{effect.State:X4} flags=0x{effect.Flags:X2} unk=0x{effect.unk:X8}")));
    }

    /// <summary>
    /// Returns valid game objects within Protector's arena capture radius. Reading and formatting
    /// occurs synchronously on the bot thread so no frame-scoped wrapper escapes the current tick.
    /// </summary>
    private static List<GameObject> GetProtectorCaptureObjects()
    {
        float radiusSquared = ProtectorFenceCaptureRadius * ProtectorFenceCaptureRadius;
        return GameObjectManager.GameObjects
            .Where(actor => actor != null && actor.IsValid &&
                ((actor.Location.X - ArenaCenter.Protector.X) *
                 (actor.Location.X - ArenaCenter.Protector.X)) +
                ((actor.Location.Z - ArenaCenter.Protector.Z) *
                 (actor.Location.Z - ArenaCenter.Protector.Z)) <= radiusSquared)
            .OrderBy(actor => actor.ObjectId)
            .ToList();
    }

    /// <summary>
    /// Converts one frame-scoped object into a stable scalar diagnostic record.
    /// </summary>
    private static string FormatProtectorCaptureObject(GameObject actor)
    {
        string name = (actor.Name ?? string.Empty).Replace('"', '\'');
        return FormattableString.Invariant(
            $"type={actor.GetType().Name} objectId=0x{actor.ObjectId:X8} baseId=0x{actor.BaseId:X} npcId={actor.NpcId} name=\"{name}\" location={FormatProtectorCaptureLocation(actor.Location)} heading={actor.Heading:F3} visible={actor.IsVisible} targetable={actor.IsTargetable}");
    }

    /// <summary>
    /// Formats an X/Y/Z position with invariant precision suitable for comparing captured fence
    /// nodes to the four-yalm layout grid.
    /// </summary>
    private static string FormatProtectorCaptureLocation(Vector3 location)
    {
        return FormattableString.Invariant($"({location.X:F3}, {location.Y:F3}, {location.Z:F3})");
    }

    /// <summary>
    /// Converts a live map-effect record into one of the four mutually exclusive wall presets or
    /// the explicit reset state.
    /// </summary>
    /// <param name="effect">Current map-effect ID 0x0D record.</param>
    /// <returns>The resolved preset, or <see cref="ProtectorFenceLayout.Unknown"/>.</returns>
    private static ProtectorFenceLayout ResolveProtectorFenceLayout(MapEffect effect)
    {
        if (ProtectorFenceResetStates.Any(state => MatchesProtectorMapEffectState(effect, state, 0x0004)))
        {
            return ProtectorFenceLayout.None;
        }

        if (MatchesProtectorMapEffectState(effect, ProtectorFenceLayoutAState, 0x0400))
        {
            return ProtectorFenceLayout.LayoutA;
        }

        if (MatchesProtectorMapEffectState(effect, ProtectorFenceLayoutBState, 0x0080))
        {
            return ProtectorFenceLayout.LayoutB;
        }

        if (MatchesProtectorMapEffectState(effect, ProtectorFenceLayoutCState, 0x0001))
        {
            return ProtectorFenceLayout.LayoutC;
        }

        if (MatchesProtectorMapEffectState(effect, ProtectorFenceLayoutDState, 0x0010))
        {
            return ProtectorFenceLayout.LayoutD;
        }

        return ProtectorFenceLayout.Unknown;
    }

    /// <summary>
    /// Matches both known RB representations of an encounter map-effect state: the complete event
    /// value when retained in <see cref="MapEffect.unk"/>, and the unique low word exposed through
    /// <see cref="MapEffect.State"/>.
    /// </summary>
    /// <param name="effect">Live map-effect record.</param>
    /// <param name="fullState">Complete 32-bit encounter event value.</param>
    /// <param name="lowState">Unique low 16 bits of that event value.</param>
    /// <returns><see langword="true"/> when either supported representation matches.</returns>
    private static bool MatchesProtectorMapEffectState(MapEffect effect, uint fullState, ushort lowState)
    {
        return effect.unk == fullState || effect.State == lowState;
    }

    /// <summary>
    /// Records the raw map-effect transition once so the next live run proves which RB fields are
    /// authoritative without producing per-tick diagnostic traffic.
    /// </summary>
    /// <param name="effect">Current map-effect record.</param>
    /// <returns><see langword="true"/> only when a new raw record was logged.</returns>
    private bool LogProtectorFenceMapEffectChange(MapEffect effect)
    {
        if (protectorFenceMapEffectWasObserved &&
            lastProtectorFenceMapEffectUnknown == effect.unk &&
            lastProtectorFenceMapEffectState == effect.State &&
            lastProtectorFenceMapEffectFlags == effect.Flags)
        {
            return false;
        }

        protectorFenceMapEffectWasObserved = true;
        lastProtectorFenceMapEffectUnknown = effect.unk;
        lastProtectorFenceMapEffectState = effect.State;
        lastProtectorFenceMapEffectFlags = effect.Flags;
        Logger.Information(
            $"[MechanicDiag] PROTECTOR_FENCE_MAP_EFFECT id=0x{effect.ID:X2} " +
            $"state=0x{effect.State:X4} flags=0x{effect.Flags:X2} unk=0x{effect.unk:X8}.");
        return true;
    }

    /// <summary>
    /// Applies a newly resolved fence layout and logs only actual layout transitions.
    /// </summary>
    /// <param name="layout">Resolved active layout or reset state.</param>
    /// <param name="effect">Source map-effect record, when one exists.</param>
    private void SetProtectorFenceLayout(ProtectorFenceLayout layout, MapEffect? effect)
    {
        if (protectorFenceLayout == layout)
        {
            return;
        }

        protectorFenceLayout = layout;
        string source = effect.HasValue
            ? $"state=0x{effect.Value.State:X4} unk=0x{effect.Value.unk:X8}"
            : "arena-reset";
        Logger.Information($"[MechanicDiag] PROTECTOR_FENCE_LAYOUT layout={layout} source={source}.");
    }

    /// <summary>
    /// Returns whether one registered wall preset should currently participate in path avoidance.
    /// </summary>
    /// <param name="layout">Preset owned by the registered polygons and nodes.</param>
    /// <returns><see langword="true"/> only while that preset is active in Protector combat.</returns>
    private bool IsProtectorFenceLayoutActive(ProtectorFenceLayout layout)
    {
        return IsInProtectorCombat() && protectorFenceLayout == layout;
    }

    /// <summary>
    /// Clears transient map-effect and layout state on wipe, encounter exit, or duty exit.
    /// </summary>
    private void ResetProtectorFenceState()
    {
        protectorFenceLayout = ProtectorFenceLayout.None;
        protectorFenceMapEffectWasObserved = false;
        lastProtectorFenceMapEffectUnknown = 0;
        lastProtectorFenceMapEffectState = 0;
        lastProtectorFenceMapEffectFlags = 0;
        protectorFenceParalysisWasPresent = false;
        ResetProtectorFenceCaptureState();
    }

    /// <summary>
    /// Clears only the evidence-capture window and immutable fingerprints. Collision layout state
    /// is owned by <see cref="ResetProtectorFenceState"/> and is intentionally not changed here.
    /// </summary>
    private void ResetProtectorFenceCaptureState()
    {
        protectorFenceCaptureEndsAtUtc = DateTime.MinValue;
        protectorFenceCastFinishAtUtc = DateTime.MinValue;
        protectorFenceCaptureCheckpoint = 0;
        lastProtectorFenceActorFingerprint = string.Empty;
        lastProtectorFenceDirectorFingerprint = string.Empty;
        lastProtectorFenceMapEffectsFingerprint = string.Empty;
        protectorFenceFulminousWasCasting = false;
    }

    /// <summary>
    /// Advances the arena transition from the 19.5-yalm opening polygon to the 17-yalm phase-two
    /// polygon when Electrothermia finishes.
    /// </summary>
    private void UpdateZanderArenaState()
    {
        if (!IsInZanderCombat() || zanderArenaShrunk)
        {
            return;
        }

        BattleCharacter electrothermiaCaster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(caster => caster.IsValid && caster.IsCasting &&
                caster.CastingSpellId == EnemyAction.Electrothermia);
        if (electrothermiaCaster != null && zanderArenaShrinkAtUtc == DateTime.MinValue)
        {
            // The outer ring becomes unsafe at cast finish plus 0.5 seconds. RB has no encounter
            // map-effect callback here, so the same cast-relative grace is the most stable
            // transition signal and avoids shrinking early during the raidwide cast.
            zanderArenaShrinkAtUtc = DateTime.UtcNow +
                electrothermiaCaster.SpellCastInfo.RemainingCastTime +
                ZanderArenaTransitionGrace;
        }

        if (zanderArenaShrinkAtUtc != DateTime.MinValue && DateTime.UtcNow >= zanderArenaShrinkAtUtc)
        {
            zanderArenaShrunk = true;
        }
    }

    /// <summary>
    /// Returns whether Electrothermia has announced the future 17-yalm boundary but has not yet
    /// completed its map transition.
    /// </summary>
    /// <returns><see langword="true"/> while the outer 2.5-yalm ring should be vacated.</returns>
    private bool IsZanderArenaShrinkPending()
    {
        return IsInZanderCombat() &&
            !zanderArenaShrunk &&
            zanderArenaShrinkAtUtc != DateTime.MinValue;
    }

    /// <summary>
    /// Clears encounter-local boundary timing after leaving combat, dying, or exiting the duty.
    /// </summary>
    private void ResetZanderState()
    {
        zanderArenaShrinkAtUtc = DateTime.MinValue;
        zanderArenaShrunk = false;
    }

    /// <summary>
    /// Selects the activation-ordered Slitherbane hazards that are safe to publish together.
    /// </summary>
    /// <param name="candidate">Current helper cone or delayed Burst being evaluated.</param>
    /// <returns>
    /// <see langword="true"/> for either of the next two hazards, except for an exactly opposite
    /// second hazard that must remain non-risky until the first one resolves.
    /// </returns>
    private static bool IsSelectedSlitherbaneHazard(BattleCharacter candidate)
    {
        if (candidate == null || !candidate.IsValid || !candidate.IsCasting ||
            !EnemyAction.SlitherbaneQueueCasts.Contains(candidate.CastingSpellId))
        {
            return false;
        }

        List<BattleCharacter> orderedHazards = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(caster => caster.IsValid && caster.IsCasting &&
                EnemyAction.SlitherbaneQueueCasts.Contains(caster.CastingSpellId))
            .OrderBy(caster => caster.SpellCastInfo.RemainingCastTime)
            .ThenBy(caster => caster.ObjectId)
            .Take(2)
            .ToList();

        int candidateIndex = orderedHazards.FindIndex(caster => caster.ObjectId == candidate.ObjectId);
        if (candidateIndex < 0)
        {
            return false;
        }

        // The second AOE is non-risky when it is the first AOE rotated by 180 degrees. Combining
        // both halves in RB would erase every possible destination, exactly matching the path-
        // generation flood captured on 2026-08-21, so publish only the resolving half.
        return candidateIndex == 0 ||
            !AreOppositeHeadings(orderedHazards[0].Heading, orderedHazards[1].Heading);
    }

    /// <summary>
    /// Tests the one-degree, half-turn equivalence used by Slitherbane's activation queue.
    /// </summary>
    /// <param name="firstHeading">Heading of the first resolving hazard, in radians.</param>
    /// <param name="secondHeading">Heading of the second resolving hazard, in radians.</param>
    /// <returns><see langword="true"/> when the headings differ by approximately 180 degrees.</returns>
    private static bool AreOppositeHeadings(float firstHeading, float secondHeading)
    {
        double difference = Math.Abs(firstHeading - secondHeading) % (Math.PI * 2.0d);
        difference = Math.Min(difference, (Math.PI * 2.0d) - difference);
        return Math.Abs(difference - Math.PI) <= SlitherbaneOppositeToleranceRadians;
    }

    /// <summary>
    /// Selects only the next resolving members of a sequential helper-cast family.
    /// </summary>
    /// <param name="candidate">Candidate helper currently being evaluated by avoidance.</param>
    /// <param name="actionIds">Action family whose remaining cast times define resolution order.</param>
    /// <param name="count">Maximum number of same-stage helpers that resolve together.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> is in the next group.</returns>
    private static bool IsAmongNextCasters(BattleCharacter candidate, HashSet<uint> actionIds, int count)
    {
        if (candidate == null || !candidate.IsCasting || !actionIds.Contains(candidate.CastingSpellId))
        {
            return false;
        }

        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(caster => caster.IsCasting && actionIds.Contains(caster.CastingSpellId))
            .OrderBy(caster => caster.SpellCastInfo.RemainingCastTime)
            .ThenBy(caster => caster.ObjectId)
            .Take(count)
            .Any(caster => caster.ObjectId == candidate.ObjectId);
    }

    /// <summary>
    /// Returns whether the live Vanguard Commander and player are fighting in the first arena.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only while encounter-local avoidance should own movement instead of
    /// SideStep; requiring the unique living boss prevents Central Garage trash from matching.
    /// </returns>
    private static bool IsInCommanderCombat()
    {
        return Core.Player != null &&
            Core.Player.IsAlive &&
            Core.Player.InCombat &&
            WorldManager.SubZoneId == (uint)SubZoneId.CentralGarage &&
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Any(actor => actor.IsValid && actor.IsAlive &&
                    actor.BaseId == EnemyNpc.VanguardCommanderR8BaseId);
    }

    /// <summary>
    /// Returns whether the player is in combat inside Protector's dedicated arena.
    /// </summary>
    /// <returns><see langword="true"/> only during the Protector encounter.</returns>
    private static bool IsInProtectorCombat()
    {
        return Core.Player != null &&
            Core.Player.InCombat &&
            WorldManager.SubZoneId == (uint)SubZoneId.SafetyInspectionChamber;
    }

    /// <summary>
    /// Returns whether the player is in combat inside Zander's dedicated arena.
    /// </summary>
    /// <returns><see langword="true"/> only during the Zander encounter.</returns>
    private static bool IsInZanderCombat()
    {
        return Core.Player != null &&
            Core.Player.IsAlive &&
            Core.Player.InCombat &&
            WorldManager.SubZoneId == (uint)SubZoneId.VanguardControlRoom;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Vanguard Commander R8.
        /// </summary>
        public const uint VanguardCommanderR8 = 12750;

        /// <summary>
        /// First boss object base ID. Enhanced Mobility helpers reuse NPC ID 12750 with base ID
        /// 0x233C, so the captured 0x411D boss base is required for unambiguous ownership.
        /// </summary>
        public const uint VanguardCommanderR8BaseId = 0x411D;

        /// <summary>
        /// Second Boss: Protector.
        /// </summary>
        public const uint Protector = 12757;

        /// <summary>
        /// Environmental fence actor base ID corroborated for Fulminous Fence. The live capture
        /// records every instance but does not use it for layout selection until its node positions
        /// and lifecycle are proven on the supported client.
        /// </summary>
        public const uint FulminousFenceBaseId = 0x4255;

        /// <summary>
        /// Second Boss: Fulminous Fence.
        /// </summary>
        public const uint FulminousFence = 13563;

        /// <summary>
        /// Final Boss: Zander the Snakeskinner.
        /// </summary>
        public const uint ZandertheSnakeskinner = 12752;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Vanguard Commander R8.
        /// </summary>
        public static readonly Vector3 VanguardCommanderR8 = new(-100f, 7f, 207f);

        /// <summary>
        /// Second Boss: Protector.
        /// </summary>
        public static readonly Vector3 Protector = new(0f, 7f, -100f);

        /// <summary>
        /// Third Boss: Zander the Snakeskinner.
        /// </summary>
        public static readonly Vector3 ZandertheSnakeskinner = new(90f, 12f, -430f);
    }

    private static class EnemyAction
    {
        /// <summary>Right-side Enhanced Mobility helper whose rotating follow-up leaves corners safe.</summary>
        public const uint EnhancedMobilityRightOut = 36563;

        /// <summary>Left-side Enhanced Mobility helper whose rotating follow-up leaves corners safe.</summary>
        public const uint EnhancedMobilityLeftOut = 36564;

        /// <summary>Right-side Enhanced Mobility helper whose rotating follow-up leaves center safe.</summary>
        public const uint EnhancedMobilityRightIn = 37184;

        /// <summary>Left-side Enhanced Mobility helper whose rotating follow-up leaves center safe.</summary>
        public const uint EnhancedMobilityLeftIn = 37191;

        /// <summary>Helper casts that identify the complete Enhanced Mobility choreography.</summary>
        public static readonly HashSet<uint> EnhancedMobilityHelperCasts =
            [EnhancedMobilityRightOut, EnhancedMobilityLeftOut, EnhancedMobilityRightIn, EnhancedMobilityLeftIn];

        /// <summary>Helper variants whose three rotary sectors cover the inner 17 yalms.</summary>
        public static readonly HashSet<uint> EnhancedMobilityOutCasts =
            [EnhancedMobilityRightOut, EnhancedMobilityLeftOut];

        /// <summary>Helper variants whose three rotary sectors cover the 11-to-28-yalm ring.</summary>
        public static readonly HashSet<uint> EnhancedMobilityInCasts =
            [EnhancedMobilityRightIn, EnhancedMobilityLeftIn];

        /// <summary>Helper variants whose opening rectangle is offset to its right.</summary>
        public static readonly HashSet<uint> EnhancedMobilityRightCasts =
            [EnhancedMobilityRightOut, EnhancedMobilityRightIn];

        /// <summary>
        /// Vanguard Commander R8
        /// Rush
        /// line oE
        /// </summary>
        public const uint Rush = 36569;

        public static readonly HashSet<uint> RushCasting = [36569];

        /// <summary>Vanguard sentry's expanding 14-yalm cast-location circle.</summary>
        public const uint AerialOffensive = 36570;

        /// <summary>All casts participating in Aerial Offensive's sequenced four-sentry waves.</summary>
        public static readonly HashSet<uint> AerialOffensiveCasts = [AerialOffensive];

        /// <summary>Vanguard Commander R8 helper-authored five-yalm spread.</summary>
        public const uint Electrosurge = 36573;

        /// <summary>
        /// Protector
        /// Tracking Bolt
        /// spread
        /// </summary>
        public const uint TrackingBolt = 37349;

        /// <summary>Protector visual whose resolution creates the environmental fence layout.</summary>
        public const uint FulminousFence = 37149;

        /// <summary>Protector visual that provides the pre-position window before laser waves.</summary>
        public const uint SearchAndDestroy = 37154;

        /// <summary>
        /// Protector
        /// Homing Cannon
        /// Straight line AOE
        /// </summary>
        public const uint HomingCannon = 37155;

        /// <summary>
        /// Protector
        /// Shock
        /// Small Circle AoE that drops the same time as HomingCannon
        /// </summary>
        public const uint Shock = 37156;

        /// <summary>Protector helper's placed five-yalm Bombardment circle.</summary>
        public const uint Bombardment = 39016;

        /// <summary>Protector's first helper-authored Battery Circuit cone.</summary>
        public const uint BatteryCircuitFirst = 37351;

        /// <summary>Protector's initial and repeated six-yalm Electrowhirl circles.</summary>
        public static readonly HashSet<uint> ElectrowhirlCasts = [37350, 37160];

        /// <summary>
        /// Protector
        /// Blast Cannon
        /// Sequential 26-by-4-yalm line
        /// </summary>
        public const uint BlastCannon = 37151;

        /// <summary>All helper casts in the sequential Blast Cannon family.</summary>
        public static readonly HashSet<uint> BlastCannonCasts = [BlastCannon];

        /// <summary>
        /// Protector
        /// Heavy Blast Cannon
        /// Stack
        /// </summary>
        public const uint HeavyBlastCannon = 37345;

        /// <summary>
        /// Zander the Snakeskinner helper-authored five-yalm spread.
        /// </summary>
        public const uint SoulbaneShock = 37922;

        /// <summary>Zander phase one's 20-by-4-yalm Soulbane Saber line.</summary>
        public const uint SoulbaneSaber = 36574;

        /// <summary>Zander phase one's delayed 20-by-40-yalm Soulbane Burst rectangle.</summary>
        public const uint SoulbaneBurst = 36575;

        /// <summary>Electrothermia raidwide whose completion shrinks Zander's arena to 17 yalms.</summary>
        public const uint Electrothermia = 36594;

        /// <summary>Zander's narrow line at the start of Slitherbane Foreguard.</summary>
        public const uint SlitherbaneForeguard = 36589;

        /// <summary>Zander's narrow line at the start of Slitherbane Rearguard.</summary>
        public const uint SlitherbaneRearguard = 36590;

        /// <summary>Zander helper's rear-facing 180-degree initial cleave.</summary>
        public const uint SlitherbaneRearguardAoe = 36593;

        /// <summary>Zander helper's forward-facing 180-degree Foreguard cleave.</summary>
        public const uint SlitherbaneForeguardAoe = 36592;

        /// <summary>Zander helper's delayed half-arena burst shared by Slitherbane variants.</summary>
        public const uint SlitherbaneBurst = 36591;

        /// <summary>Zander's opening phase-two 19-yalm, 90-degree Syntheslean cone.</summary>
        public const uint Syntheslean = 37198;

        /// <summary>First cone in the clockwise Syntheslither sequence.</summary>
        public const uint Syntheslither1 = 36580;

        /// <summary>Second cone in the clockwise Syntheslither sequence.</summary>
        public const uint Syntheslither2 = 36581;

        /// <summary>Third cone in the clockwise Syntheslither sequence.</summary>
        public const uint Syntheslither3 = 36582;

        /// <summary>Fourth cone in the clockwise Syntheslither sequence.</summary>
        public const uint Syntheslither4 = 36583;

        /// <summary>First cone in the counterclockwise Syntheslither sequence.</summary>
        public const uint Syntheslither5 = 36585;

        /// <summary>Second cone in the counterclockwise Syntheslither sequence.</summary>
        public const uint Syntheslither6 = 36586;

        /// <summary>Third cone in the counterclockwise Syntheslither sequence.</summary>
        public const uint Syntheslither7 = 36587;

        /// <summary>Fourth cone in the counterclockwise Syntheslither sequence.</summary>
        public const uint Syntheslither8 = 36588;

        /// <summary>Narrow Zander lines that share the measured 20-by-4-yalm geometry.</summary>
        public static readonly HashSet<uint> ZanderLineCasts =
            [SoulbaneSaber, SlitherbaneForeguard, SlitherbaneRearguard];

        /// <summary>
        /// Slitherbane cones and delayed rectangles whose activation order must remain unified.
        /// </summary>
        public static readonly HashSet<uint> SlitherbaneQueueCasts =
            [SlitherbaneForeguardAoe, SlitherbaneRearguardAoe, SlitherbaneBurst];

        /// <summary>
        /// Phase-two cone family. Only its next two activations are exposed to RB navigation so
        /// later quadrants cannot combine into a false whole-arena avoid.
        /// </summary>
        public static readonly HashSet<uint> SyntheslitherCasts =
            [Syntheslean, Syntheslither1, Syntheslither2, Syntheslither3, Syntheslither4,
                Syntheslither5, Syntheslither6, Syntheslither7, Syntheslither8];
    }

    // Fulminous Fence uses one stable 26-node arena blueprint and four map-effect-selected subsets.
    // The 2026-08-21 capture proved that hand-copying complete coordinates per layout had allowed
    // Layout A's first wall and two Layout B walls/nodes to drift from the client. Keeping nodes and
    // all 28 possible connections authoritative here makes every preset a compact index selection
    // and prevents one correction from silently changing another layout.
    private static readonly Vector3[] ProtectorFenceNodePositions =
    [
        ProtectorFenceNode(12f, -88f), ProtectorFenceNode(8f, -92f),
        ProtectorFenceNode(4f, -88f), ProtectorFenceNode(0f, -88f),
        ProtectorFenceNode(-4f, -88f), ProtectorFenceNode(-12f, -88f),
        ProtectorFenceNode(-8f, -92f), ProtectorFenceNode(0f, -92f),
        ProtectorFenceNode(-4f, -96f), ProtectorFenceNode(0f, -96f),
        ProtectorFenceNode(4f, -96f), ProtectorFenceNode(-4f, -104f),
        ProtectorFenceNode(0f, -104f), ProtectorFenceNode(4f, -104f),
        ProtectorFenceNode(-8f, -108f), ProtectorFenceNode(-12f, -112f),
        ProtectorFenceNode(-4f, -112f), ProtectorFenceNode(0f, -108f),
        ProtectorFenceNode(0f, -112f), ProtectorFenceNode(4f, -112f),
        ProtectorFenceNode(8f, -108f), ProtectorFenceNode(12f, -112f),
        ProtectorFenceNode(12f, -104f), ProtectorFenceNode(12f, -96f),
        ProtectorFenceNode(-12f, -96f), ProtectorFenceNode(-12f, -104f),
    ];

    private static readonly ProtectorFenceSegmentPair[] ProtectorFenceSegmentPairs =
    [
        new(0, 1), new(7, 9), new(5, 6), new(13, 20), new(17, 18), new(11, 14),
        new(21, 20), new(14, 15), new(12, 17), new(1, 10), new(3, 7), new(6, 8),
        new(25, 5), new(25, 11), new(2, 5), new(4, 8), new(16, 21), new(21, 23),
        new(23, 10), new(13, 19), new(15, 24), new(15, 19), new(16, 11), new(24, 8),
        new(0, 22), new(0, 4), new(2, 10), new(22, 13),
    ];

    // Segment selections are the energized wall bodies; node selections are the separately
    // collidable round posts. They are intentionally not inferred from one another because several
    // preset endpoints join the arena edge while only interior posts constrain player routing.
    private static readonly ProtectorFenceLayoutDefinition[] ProtectorFenceLayouts =
    [
        new(ProtectorFenceLayout.LayoutA, [6, 7, 8, 9, 10, 11], [21, 20, 14, 15, 12, 17, 1, 10, 3, 7, 6, 8]),
        new(ProtectorFenceLayout.LayoutB, [0, 1, 2, 3, 4, 5], [0, 1, 7, 9, 5, 6, 13, 20, 17, 18, 11, 14]),
        new(ProtectorFenceLayout.LayoutC, [12, 13, 14, 15, 16, 17, 18, 19], [2, 8, 11, 10, 13, 16]),
        new(ProtectorFenceLayout.LayoutD, [20, 21, 22, 23, 24, 25, 26, 27], [4, 8, 11, 19, 13, 10]),
    ];

    // The polygon API requires a collection item to supply a world origin. Every segment polygon
    // is authored relative to this single stable arena-center item.
    private static readonly Vector3[] ProtectorFenceAvoidOrigin = [ArenaCenter.Protector];

    // Director event indices 0x0D and 0x0C select the fence layout and reset the plain rectangle,
    // respectively. RB's public MapEffects collection does not retain those packet indices: the
    // 2026-08-21 live capture exposed the corresponding stable scene records as 0x9F2947 and
    // 0x9F2948. Using the packet indices here left every wall preset disabled and allowed service
    // navigation to route directly through an electrified fence. Full values below document the
    // encounter event while each unique low word supports RB's public MapEffect.State field.
    private const uint ProtectorFenceMapEffectRecordId = 0x9F2947;
    private const uint ProtectorArenaResetMapEffectRecordId = 0x9F2948;
    private const uint ProtectorFenceLayoutAState = 0x08000400;
    private const uint ProtectorFenceLayoutBState = 0x01000080;
    private const uint ProtectorFenceLayoutCState = 0x00020001;
    private const uint ProtectorFenceLayoutDState = 0x00200010;
    private const uint ProtectorArenaResetState = 0x00020001;
    private static readonly uint[] ProtectorFenceResetStates =
        [0x02000004, 0x10000004, 0x00080004, 0x00400004];

    // The physical fence radius is approximately 0.51 yalm. A one-yalm navigation radius includes
    // the player's half-yalm clearance without closing the maze's four-yalm-spaced corridors.
    private const float ProtectorFenceNavigationRadius = 1.0f;
    private const float ProtectorFenceLeashRadius = 40.0f;

    // Capture through three seconds after cast resolution because the environmental walls finish
    // materializing on that delay. A half-second grace ensures the final checkpoint survives normal
    // RB tick jitter without extending diagnostics into unrelated mechanics.
    private static readonly TimeSpan ProtectorFencePostResolutionCaptureDelay = TimeSpan.FromSeconds(3.0);
    private static readonly TimeSpan ProtectorFenceCaptureGrace = TimeSpan.FromSeconds(0.5);

    // Thirty yalms contains the complete 24-by-40-yalm arena plus edge actors while excluding trash
    // elsewhere in the instance, keeping checkpoint object inventories bounded and relevant.
    private const float ProtectorFenceCaptureRadius = 30.0f;

    /// <summary>
    /// Creates a fence-node world position at Protector's fixed arena elevation.
    /// </summary>
    /// <param name="x">World X coordinate.</param>
    /// <param name="z">World Z coordinate.</param>
    /// <returns>World position used by node avoidance.</returns>
    private static Vector3 ProtectorFenceNode(float x, float z)
    {
        return new Vector3(x, ArenaCenter.Protector.Y, z);
    }

    /// <summary>
    /// One indexed connection in Protector's authoritative fence-node blueprint.
    /// </summary>
    private readonly struct ProtectorFenceSegmentPair
    {
        public ProtectorFenceSegmentPair(int startNodeIndex, int endNodeIndex)
        {
            StartNodeIndex = startNodeIndex;
            EndNodeIndex = endNodeIndex;
        }

        public int StartNodeIndex { get; }
        public int EndNodeIndex { get; }
    }

    /// <summary>
    /// The wall and round-post selections activated by one map-effect fence preset.
    /// </summary>
    private readonly struct ProtectorFenceLayoutDefinition
    {
        public ProtectorFenceLayoutDefinition(
            ProtectorFenceLayout layout,
            int[] segmentPairIndices,
            int[] nodeIndices)
        {
            Layout = layout;
            SegmentPairIndices = segmentPairIndices;
            NodeIndices = nodeIndices;
        }

        public ProtectorFenceLayout Layout { get; }
        public int[] SegmentPairIndices { get; }
        public int[] NodeIndices { get; }
    }

    /// <summary>
    /// Current Fulminous Fence collision preset selected by the instance map effect.
    /// </summary>
    private enum ProtectorFenceLayout
    {
        Unknown = -1,
        None = 0,
        LayoutA,
        LayoutB,
        LayoutC,
        LayoutD,
    }

    private static class PlayerAura
    {
        /// <summary>Player and Duty Support variants of Protector's Acceleration Bomb.</summary>
        public static readonly HashSet<uint> AccelerationBombs = [3802, 4144];
    }

    /// <summary>
    /// Stable radial safe region selected by an Enhanced Mobility helper cast.
    /// </summary>
    private enum EnhancedMobilitySafeBand
    {
        /// <summary>No Enhanced Mobility sequence is currently owned.</summary>
        None,

        /// <summary>The 11-yalm center is safe from every rotary sector.</summary>
        Center,

        /// <summary>Square-arena corners beyond the 17-yalm inner circle are safe.</summary>
        Corners,
    }

    // Rapid Rotary's last of three sectors resolves approximately 1.9 seconds after the opening
    // rectangle. Retaining the radial avoid for 2.5 seconds covers that sequence plus RB tick lag.
    private static readonly TimeSpan EnhancedMobilityPostCastDuration = TimeSpan.FromSeconds(2.5);

    // Two independent 2026-08-21 captures placed the first Homing Cannon 5.15 seconds after the
    // Search and Destroy visual completed. This deadline stages early but yields to cast geometry.
    private static readonly TimeSpan SearchAndDestroyPostVisualDelay = TimeSpan.FromSeconds(5.15);

    // The rotating sequence performs its first cone at cast completion and then 33 more pulses at
    // half-second intervals. One second of expiry grace covers normal bot-tick and actor cleanup lag.
    private static readonly TimeSpan BatteryCircuitPostCastDuration = TimeSpan.FromSeconds(17.5);
    private static readonly TimeSpan BatteryCircuitPulseInterval = TimeSpan.FromSeconds(0.5);

    // Candidate radii remain at least 2.5 yalms outside the six-yalm Electrowhirl while offering
    // nearby alternatives when a fence post or Bombardment circle occupies the ideal 9.5-yalm arc.
    private static readonly float[] BatteryCircuitCandidateRadii = [9.5f, 8.5f, 10.5f, 11.5f];

    // The unsafe outer ring resolves 0.5 seconds after Electrothermia's cast finish, matching the
    // observed map transition rather than shrinking the movement boundary prematurely.
    private static readonly TimeSpan ZanderArenaTransitionGrace = TimeSpan.FromSeconds(0.5);

    // The corroborated half-turn comparison uses one degree. A wider tolerance would incorrectly
    // suppress legitimate second hazards; a narrower value would reintroduce whole-arena unions.
    private const double SlitherbaneOppositeToleranceRadians = Math.PI / 180.0d;

    // Homing Cannon's two-yalm damage width needs one additional yalm for the player's radius;
    // otherwise an accepted center path can still overlap the damaging line at resolution.
    private const float HomingCannonNavigationWidth = 3.0f;

    // The physical post-Electrowave floor is 24 by 40 yalms. These dimensions retain a two-yalm
    // navigation inset on each wall, double the margin that still allowed X=-11.55 in live use.
    private const float ProtectorNavigationWidth = 20.0f;
    private const float ProtectorNavigationHeight = 36.0f;

    // Central staging consumes one additional yalm per side and two per end only before the first
    // laser commits; keeping it separate avoids reducing the real Homing Cannon escape lanes.
    private const float SearchAndDestroyStagingWidth = 18.0f;
    private const float SearchAndDestroyStagingHeight = 32.0f;

    // Battery Circuit applies a -11-degree heading delta per 0.5-second event for 34 activations.
    // Publishing the increment as a fallback cone and using the same value for manual heading
    // prediction prevents avoidance and the trailing anchor from disagreeing about rotation.
    private const float BatteryCircuitRotationIncrementDegrees = -11.0f;
    private const int BatteryCircuitPulseCount = 34;

    // The 30-degree cone has a 15-degree half-angle. An eight-degree reserve places the ideal point
    // 23 degrees into the recently vacated side. Eight candidates at six-degree spacing stop at 65
    // degrees, still inside the 15-to-77-degree safe wedge left by nine forecast cone positions.
    private const float BatteryCircuitIdealTrailingOffsetDegrees = 23.0f;
    private const float BatteryCircuitCandidateAngleStepDegrees = 6.0f;
    private const int BatteryCircuitCandidateAngleCount = 8;

    // A 9.5-yalm anchor clears Electrowhirl without pushing unnecessarily close to the rectangular
    // arena wall. Registered avoids reject radii or angles obstructed by the current fence layout.
    private const float BatteryCircuitIdealRadius = 9.5f;
    private const float BatteryCircuitArrivalTolerance = 0.65f;

    // Half-yalm chord samples cannot skip over the one-yalm padded fence body. The movement lease is
    // refreshed every bot tick and expires quickly if the handler or plugin lifecycle is interrupted.
    private const float BatteryCircuitPathSampleSpacing = 0.5f;
    private const int BatteryCircuitMovementLeaseMilliseconds = 1_000;

    // Two yalms per six-degree step makes staying directly behind the leading edge more important
    // than saving the roughly 1.8-yalm travel of one pulse. Without this bias, shortest-path scoring
    // can remain stationary until the opposite forecast cone closes the back of the safe wedge.
    // A blocked lane still receives a dominant penalty so the initial latch chooses the other helper.
    private const float BatteryCircuitAngularDeviationPenalty = 2.0f;
    private const float BatteryCircuitRadialDeviationPenalty = 0.25f;
    private const float BatteryCircuitUnavailableLanePenalty = 1_000.0f;

    // Eleven-degree pulses should each produce one record; five degrees also catches a live helper
    // resynchronization that diverges from the half-second timing fallback and forces a cached
    // destination to be recalculated before the cone completes a full step.
    private const float BatteryCircuitDiagnosticHeadingThresholdDegrees = 5.0f;
    private const float BatteryCircuitDestinationReselectionDegrees = 5.0f;

    // The corroborated encounter planner blocks both actions and movement one second before expiry;
    // stopping earlier would prevent the bot from reaching a safe lane between Blast Cannons.
    private const float MotionSensorHoldThresholdSeconds = 1.0f;

}
