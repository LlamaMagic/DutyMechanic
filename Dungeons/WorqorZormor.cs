using Buddy.Coroutines;
using Clio.Common;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Helpers;
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
/// Lv. 93: Worqor Zormor dungeon logic.
/// </summary>
public class WorqorZormor : AbstractDungeon
{
    // Ryoqor helper casts define the exact simultaneous hazard shapes.
    private const float RyoqorIceScreamWidth = 20f;
    private const float RyoqorIceScreamLength = 20f;
    private const float RyoqorFrozenSwirlRadius = 15f;
    private const float RyoqorSnowBoulderWidth = 6f;
    private const float RyoqorSnowBoulderLength = 50f;
    private const double RyoqorSnowBoulderWaveToleranceMilliseconds = 500d;
    // Snow Boulder destinations keep wall, lane, and arrival clearance without widening lanes.
    private const float RyoqorArenaSafeRadius = 19f;
    private const float RyoqorArenaMovementRadius = 18f;
    private const float RyoqorSnowBoulderCandidateStep = 0.5f;
    private const float RyoqorSnowBoulderCandidateClearance = 0.75f;
    private const float RyoqorSnowBoulderArrivalDistance = 0.25f;
    private const float RyoqorSpreadRadius = 5.5f;
    private const double RyoqorSpreadResolutionGraceMilliseconds = 500d;
    private static readonly TimeSpan RyoqorFrozenDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RyoqorFluffleResolutionGrace = TimeSpan.FromMilliseconds(750);
    // A one-second cast-time jump identifies a reused helper's next cast.
    private static readonly TimeSpan RyoqorFluffleRecastJump = TimeSpan.FromSeconds(1);

    private static readonly Vector2[] RyoqorIceScreamRectangle =
    [
        new(RyoqorIceScreamWidth / 2f, RyoqorIceScreamLength),
        new(-RyoqorIceScreamWidth / 2f, RyoqorIceScreamLength),
        new(-RyoqorIceScreamWidth / 2f, 0f),
        new(RyoqorIceScreamWidth / 2f, 0f),
    ];

    private static readonly Vector2[] RyoqorSnowBoulderRectangle =
    [
        new(RyoqorSnowBoulderWidth / 2f, RyoqorSnowBoulderLength),
        new(-RyoqorSnowBoulderWidth / 2f, RyoqorSnowBoulderLength),
        new(-RyoqorSnowBoulderWidth / 2f, 0f),
        new(RyoqorSnowBoulderWidth / 2f, 0f),
    ];

    // Cast snapshots keep Cold Feat's delayed wave available after live casts pause.
    private readonly Dictionary<uint, RyoqorFluffleAoe> ryoqorFluffleAoes = [];
    private readonly CapabilityManagerHandle ryoqorSnowBoulderMovementHandle = CapabilityManager.CreateNewHandle();
    private RyoqorSnowBoulderLane[] ryoqorSnowBoulderLanes = [];
    private bool ryoqorSnowBoulderDirectMovementActive;
    private bool ryoqorSnowBoulderMovementOwned;
    private bool ryoqorSnowBoulderDestinationLatched;
    private string ryoqorSnowBoulderWaveKey;
    private Vector3 ryoqorSnowBoulderDestination;

    private const float KahderyorArrivalDistance = 1f;
    private const float KahderyorResponseArrivalDistance = 0.25f;
    private const float KahderyorArenaSafeRadius = 19f;
    private const float KahderyorArenaMovementRadius = 18f;
    // Ranged Earthen Shot assignments reserve an outer pocket away from converging melee actors.
    private const float KahderyorRangedEarthenInnerRadius = 12f;
    private const float KahderyorResponseCandidateStep = 0.5f;
    private const float KahderyorResponseCandidateClearance = 0.75f;
    // Separate acquisition and retention distances prevent spread destination churn.
    private const float KahderyorSpreadAcquisitionDistance = 7.25f;
    private const float KahderyorSpreadRetentionDistance = 6.25f;
    private const float KahderyorWindDonutInnerRadius = 5f;
    private const float KahderyorWindDonutOuterRadius = 10f;
    // Wind Shot margins keep the player outside other targets' five-to-ten-yalm damage bands.
    private const float KahderyorWindDonutSafetyMargin = 0.75f;
    private const float KahderyorWindDonutRetentionMargin = 0.25f;
    private const float KahderyorCrushInRadius = 8f;
    private const float KahderyorStormInHalfWidth = 1f;
    private const float KahderyorWindCrystalSafetyMargin = 0.25f;
    private const float KahderyorCrushOutRadius = 15f;
    private const float KahderyorStormOutHalfWidth = 7f;
    private const float KahderyorStormHalfLength = 25f;
    private const float KahderyorStalagmiteSafeRadius = 16.5f;
    // Cyclonic Ring aims inside its eight-yalm inner edge.
    private const float KahderyorCyclonicSafeRadius = 6f;
    // Confirm the live heading is within fifteen degrees before gaze-safe movement starts.
    private const float KahderyorGazeFacingToleranceRadians = 0.2617994f;
    private static readonly TimeSpan KahderyorGazeImpactGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan KahderyorGazeMovementPulse = TimeSpan.FromMilliseconds(250);
    // Hold response positions briefly after helper casts disappear.
    private static readonly TimeSpan KahderyorResponseImpactGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan KahderyorDestinationRecheckInterval = TimeSpan.FromMilliseconds(250);

    // Crystal helper snapshots provide persistent geometry for Wind Shot and Earthen Shot.
    private readonly Dictionary<uint, KahderyorCrystalSource> kahderyorCrystalSources = [];
    private readonly CapabilityManagerHandle kahderyorCrushMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle kahderyorSeedMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle kahderyorWindMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle kahderyorEarthenMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle kahderyorGazeFacingHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle kahderyorGazeMovementHandle = CapabilityManager.CreateNewHandle();
    private uint kahderyorActiveResponseAction;
    private int kahderyorCompletedResponses;
    private bool kahderyorCrushMovementOwned;
    private bool kahderyorSeedMovementOwned;
    private bool kahderyorWindMovementOwned;
    private bool kahderyorEarthenMovementOwned;
    private bool kahderyorGazeFacingOwned;
    private bool kahderyorGazeMovementOwned;
    private bool kahderyorSeedDestinationLatched;
    private bool kahderyorWindDestinationLatched;
    private bool kahderyorEarthenDestinationLatched;
    private uint kahderyorResponseDirectMovementAction;
    private Vector3 kahderyorSeedDestination;
    private Vector3 kahderyorWindDestination;
    private Vector3 kahderyorEarthenDestination;
    private Vector3[] kahderyorSeedLastOtherTargets = [];
    private DateTime kahderyorSeedHoldUntilUtc;
    private DateTime kahderyorSeedNextRecheckUtc;
    private DateTime kahderyorWindHoldUntilUtc;
    private DateTime kahderyorWindNextRecheckUtc;
    private DateTime kahderyorEarthenHoldUntilUtc;
    private DateTime kahderyorEarthenNextRecheckUtc;
    private Vector3[] kahderyorEarthenLastOtherTargets = [];
    private Vector3 kahderyorGazeOrigin;
    private DateTime kahderyorGazeHoldUntilUtc;

    private static readonly Vector2[] KahderyorStormOutRectangle =
    [
        new(KahderyorStormOutHalfWidth, KahderyorStormHalfLength),
        new(-KahderyorStormOutHalfWidth, KahderyorStormHalfLength),
        new(-KahderyorStormOutHalfWidth, -KahderyorStormHalfLength),
        new(KahderyorStormOutHalfWidth, -KahderyorStormHalfLength),
    ];

    // Gurfurlur destinations stay 1.5 yalms inside the 40-yalm square arena.
    private const float GurfurlurArenaMovementHalfWidth = 18.5f;
    // The arena-centered leash includes every legal corner.
    private const float GurfurlurAvoidanceLeashRadius = 28f;
    private const float GurfurlurLithicImpactWidth = 4f;
    private const float GurfurlurLithicImpactLength = 4f;
    private const float GurfurlurAllfireSize = 10f;
    private const float GurfurlurTileEdgeMargin = 0.5f;
    private const float GurfurlurAllfireWaveToleranceMilliseconds = 500f;
    private const float GurfurlurBitingWindAvoidRadius = 6f;
    // The forward footprint starts tornado avoidance before the six-yalm body arrives.
    private const float GurfurlurBitingWindProjectionLength = 12f;
    private const float GurfurlurBitingWindProjectionWidth = 12f;
    // Long tornado corridors guide destination planning without becoming hard avoids.
    private const float GurfurlurBitingWindForecastLength = 40f;
    private const float GurfurlurBitingWindForecastClearance = 6f;
    private const float GurfurlurBitingWindForecastGridStep = 2f;
    // Forecasts compare four-yalm tornado travel with six-yalm player travel and a lead buffer.
    private const float GurfurlurBitingWindSpeed = 4f;
    private const float GurfurlurPlayerRunSpeed = 6f;
    private const float GurfurlurBitingWindReplanLeadSeconds = 3f;
    // One-yalm route samples reject paths that cross a projected tornado corridor.
    private const float GurfurlurBitingWindForecastPathSampleStep = 1f;
    // Probe periodically for a trajectory-free pocket without searching every tick.
    private static readonly TimeSpan GurfurlurBitingWindClearPocketProbe = TimeSpan.FromMilliseconds(250);
    private const float GurfurlurGreatFloodDistance = 25f;
    private const float GurfurlurGreatFloodFallbackOffset = 17f;
    private const float GurfurlurWindswrathDistance = 15f;
    private const float GurfurlurWindswrathShortArrivalDistance = 2.5f;
    private const float GurfurlurWindswrathResolvedDisplacementDistance = 8f;
    private const float GurfurlurMovementArrivalDistance = 1.25f;
    // Gurfurlur spreads use stable positive destinations with separate retention margins.
    private const float GurfurlurSpreadAcquisitionDistance = 7.25f;
    private const float GurfurlurSpreadRetentionDistance = 6.25f;
    private const float GurfurlurSpreadCandidateStep = 1f;
    // Long Windswrath stages inside eight yalms, then commits to an alternating-row safe wedge.
    private const int GurfurlurLongWindswrathExpectedTornadoCount = 4;
    private const float GurfurlurLongWindswrathEarlyRadius = 8f;
    private const float GurfurlurLongWindswrathEarlyTargetRadius = 7.5f;
    private const float GurfurlurLongWindswrathFinalRadius = 5f;
    private const float GurfurlurLongWindswrathFinalTargetRadius = 3.5f;
    private const float GurfurlurLongWindswrathFinalWindowSeconds = 3f;
    private const float GurfurlurLongWindswrathWedgeHalfAngleDegrees = 15f;
    private const float GurfurlurLongWindswrathPatternRowTolerance = 0.75f;
    private const float GurfurlurLongWindswrathFinalArrivalDistance = 0.35f;
    private const float GurfurlurAuraSphereInterceptOffset = 2f;
    private const float GurfurlurSledgehammerStackDistance = 2f;
    // Retain Sledgehammer's stack point through its two no-cast follow-up hits.
    private static readonly TimeSpan GurfurlurSledgehammerFollowupGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GurfurlurDestinationRecheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan GurfurlurSpreadImpactGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan GurfurlurWindswrathImpactGrace = TimeSpan.FromMilliseconds(750);

    private readonly GurfurlurMovementLease gurfurlurGreatFloodMovement = new();
    private readonly GurfurlurMovementLease gurfurlurVolcanicDropMovement = new();
    private readonly GurfurlurMovementLease gurfurlurWindswrathMovement = new();
    private readonly GurfurlurMovementLease gurfurlurSledgehammerMovement = new();
    private readonly GurfurlurMovementLease gurfurlurAuraSphereMovement = new();
    private readonly GurfurlurMovementLease gurfurlurBitingWindMovement = new();
    private uint gurfurlurGreatFloodCasterId;
    private int gurfurlurGreatFloodAllfireCount;
    private Vector3 gurfurlurGreatFloodDestination;
    private Vector3 gurfurlurVolcanicDropDestination;
    private Vector3[] gurfurlurVolcanicDropLastOtherTargets = [];
    private bool gurfurlurVolcanicDropDestinationLatched;
    private DateTime gurfurlurVolcanicDropHoldUntilUtc;
    private DateTime gurfurlurVolcanicDropNextRecheckUtc;
    private uint gurfurlurWindswrathCasterId;
    private Vector3 gurfurlurWindswrathDestination;
    private bool gurfurlurWindswrathDestinationLatched;
    private DateTime gurfurlurWindswrathHoldUntilUtc;
    private bool gurfurlurLongWindswrathActive;
    private bool gurfurlurWindswrathDestinationReached;
    private bool gurfurlurWindswrathRouteCommitted;
    private GurfurlurWindswrathPattern gurfurlurWindswrathPattern;
    private uint gurfurlurSledgehammerCasterId;
    private uint gurfurlurSledgehammerTargetId;
    private Vector3 gurfurlurSledgehammerFallbackDestination;
    private DateTime gurfurlurSledgehammerHoldUntilUtc;
    private uint gurfurlurAuraSphereId;
    private Vector3 gurfurlurBitingWindDestination;
    private bool gurfurlurBitingWindDestinationLatched;
    private DateTime gurfurlurBitingWindNextClearPocketProbeUtc;

    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.WorqorZormor;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        // Publish only the next Fluffle Up activation wave.
        AvoidanceManager.AddAvoidPolygon<RyoqorFluffleAoe>(
            condition: IsRyoqorTertehCombat,
            leashPointProducer: () => ArenaCenter.RyoqorTerteh,
            leashRadius: 20f,
            rotationProducer: aoe => -aoe.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: _ => RyoqorIceScreamRectangle,
            locationProducer: aoe => aoe.Location,
            collectionProducer: () => GetActiveRyoqorFluffleAoes(RyoqorFluffleShape.IceScream),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // Use the constructor overload that exposes High priority.
        AvoidanceManager.AddAvoid(new AvoidLocationInfo<RyoqorFluffleAoe>(
            condition: IsRyoqorTertehCombat,
            locationProducer: aoe => aoe.Location,
            radiusProducer: _ => RyoqorFrozenSwirlRadius,
            collecionSelection: () => GetActiveRyoqorFluffleAoes(RyoqorFluffleShape.FrozenSwirl),
            leashPointSelector: () => ArenaCenter.RyoqorTerteh,
            leashRadius: 20f,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High));

        // Plan one Snow Boulder wave-level destination; keep polygons only as a fallback.
        AvoidanceManager.AddAvoidPolygon<RyoqorSnowBoulderLane>(
            condition: () => IsRyoqorTertehCombat() && !ryoqorSnowBoulderDestinationLatched,
            leashPointProducer: () => ArenaCenter.RyoqorTerteh,
            leashRadius: 20f,
            rotationProducer: lane => -lane.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: _ => RyoqorSnowBoulderRectangle,
            locationProducer: lane => lane.Location,
            collectionProducer: () => ryoqorSnowBoulderLanes,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // Crystalline Storm helpers define the line geometry reused by Earthen Shot.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsKahderyorCombat,
            objectSelector: bc => IsCastingAction(bc, EnemyAction.CrystallineStormAoe),
            width: 2f,
            length: KahderyorStormHalfLength * 2f,
            yOffset: -KahderyorStormHalfLength,
            priority: AvoidancePriority.High);

        // Earthen Shot uses party-member circles only until its combined planner latches a point.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: IsKahderyorCombat,
            objectSelector: bc =>
                IsCastingAction(bc, EnemyAction.EarthenShotAoe) &&
                !kahderyorEarthenDestinationLatched &&
                bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: _ => 6f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Wind Shot planning combines crystal safe zones with every allied donut.

        // Earthen Shot hard avoids remain active until the combined planner latches a point.
        AvoidanceManager.AddAvoidLocation<KahderyorCrystalSource>(
            canRun: () => IsKahderyorEarthenShotActive() && !kahderyorEarthenDestinationLatched,
            leashPointProducer: () => ArenaCenter.Kahderyor,
            leashRadius: 25f,
            radiusProducer: _ => KahderyorCrushOutRadius,
            locationProducer: source => source.Location,
            collectionProducer: () => kahderyorCrystalSources.Values.Where(source => source.Shape == KahderyorCrystalShape.Circle),
            objectValidator: _ => true,
            ignoreIfBlocking: false);

        AvoidanceManager.AddAvoidPolygon<KahderyorCrystalSource>(
            condition: () => IsKahderyorEarthenShotActive() && !kahderyorEarthenDestinationLatched,
            leashPointProducer: () => ArenaCenter.Kahderyor,
            leashRadius: 25f,
            rotationProducer: source => -source.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: _ => KahderyorStormOutRectangle,
            locationProducer: source => source.Location,
            collectionProducer: () => kahderyorCrystalSources.Values.Where(source => source.Shape == KahderyorCrystalShape.Rectangle),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // The gaze handler owns Stalagmite Circle and Cyclonic Ring movement.

        // Center elemental squares on their cast location and publish only the next Allfire wave.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsGurfurlurCombat,
            objectSelector: actor => IsCastingAction(actor, EnemyAction.LithicImpact),
            width: GurfurlurLithicImpactWidth + GurfurlurTileEdgeMargin * 2f,
            length: GurfurlurLithicImpactLength + GurfurlurTileEdgeMargin * 2f,
            // Center the expanded Lithic Impact square on the helper.
            yOffset: -(GurfurlurLithicImpactLength / 2f + GurfurlurTileEdgeMargin),
            priority: AvoidancePriority.High,
            locationProducer: GetGurfurlurTileImpactLocation);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsGurfurlurAllfireAvoidActive,
            objectSelector: IsActiveGurfurlurAllfireCaster,
            width: GurfurlurAllfireSize + GurfurlurTileEdgeMargin * 2f,
            length: GurfurlurAllfireSize + GurfurlurTileEdgeMargin * 2f,
            yOffset: -(GurfurlurAllfireSize / 2f + GurfurlurTileEdgeMargin),
            priority: AvoidancePriority.High,
            locationProducer: GetGurfurlurTileImpactLocation);

        // Biting Wind publishes its body and short forward path as hard avoids.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsGurfurlurBitingWindBodyAvoidActive,
            objectSelector: IsGurfurlurBitingWind,
            radiusProducer: _ => GurfurlurBitingWindAvoidRadius,
            leashPointSelector: () => ArenaCenter.Gurfurlur,
            leashRadius: GurfurlurAvoidanceLeashRadius,
            priority: AvoidancePriority.High));

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsGurfurlurBitingWindProjectionAvoidActive,
            objectSelector: IsGurfurlurBitingWind,
            width: GurfurlurBitingWindProjectionWidth,
            length: GurfurlurBitingWindProjectionLength,
            priority: AvoidancePriority.High);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Calmgrounds,
            () => ArenaCenter.RyoqorTerteh,
            outerRadius: 90.0f,
            innerRadius: RyoqorArenaSafeRadius,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            // The manual gaze solver stays between six and 16.5 yalms from center, so it remains
            // inside the 19-yalm arena without Navigator's boundary correction stealing facing.
            () => IsKahderyorCombat() && !kahderyorGazeMovementOwned,
            () => ArenaCenter.Kahderyor,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.KarryortheResting,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Gurfurlur],
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        // Capability leases outlive a single behavior-tree tick. Explicit teardown prevents a
        // transition or forced duty exit during a cast from suppressing movement in later content.
        ResetRyoqorState();
        ResetKahderyorState("Worqor Zormor exited");
        ResetGurfurlurState("Worqor Zormor exited");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (WorldManager.SubZoneId is (uint)SubZoneId.KarryortheResting)
        {
            SidestepPlugin.Enabled = false;
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.Calmgrounds => await RyoqorTerteh(),
            SubZoneId.CouncilofMorgar => await Kahderyor(),
            SubZoneId.KarryortheResting => await Gurfurlur(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    private async Task<bool> RyoqorTerteh()
    {
        if (!IsRyoqorTertehCombat())
        {
            ResetRyoqorState();
            return false;
        }

        UpdateRyoqorFluffleState();
        PrioritizeRyoqorSnowballs();

        BattleCharacter[] snowBoulderCasters = GetRyoqorSnowBoulderCasters();
        UpdateRyoqorSnowBoulderMovement(snowBoulderCasters);

        // DutyMechanic owns Snow Boulder explicitly; leaving SideStep active would duplicate the
        // same 50x6 lane and can make Navigator reject legitimate gaps between simultaneous casts.
        SidestepPlugin.Enabled = false;

        if (await MoveToRyoqorSnowBoulderDestination())
        {
            return true;
        }

        // Delay Sparkling Sprinkling spreads until Snow Boulder lanes finish.
        if (snowBoulderCasters.Length == 0)
        {
            BattleCharacter spreadCaster = GetPlayerTargetedCaster(EnemyAction.SparklingSprinklingAoe)
                ?? GetActiveCaster(EnemyAction.SparklingSprinklingAoe);
            if (spreadCaster != null)
            {
                double duration = Math.Max(
                    0d,
                    spreadCaster.SpellCastInfo.RemainingCastTime.TotalMilliseconds + RyoqorSpreadResolutionGraceMilliseconds);
                await MovementHelpers.Spread(duration, RyoqorSpreadRadius);
            }
        }

        return false;
    }

    private static bool IsRyoqorTertehCombat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Calmgrounds;

    private static BattleCharacter[] GetRyoqorSnowBoulderCasters() =>
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.Snowball)
            .Where(actor => IsCastingAction(actor, EnemyAction.SnowBoulder))
            .ToArray();

    private static BattleCharacter[] SelectRyoqorSnowBoulderWave(BattleCharacter[] casters)
    {
        if (casters.Length == 0)
        {
            return [];
        }

        double earliestRemainingMilliseconds = casters.Min(
            actor => actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds);
        return casters
            .Where(actor => actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds <=
                earliestRemainingMilliseconds + RyoqorSnowBoulderWaveToleranceMilliseconds)
            .ToArray();
    }

    private void UpdateRyoqorSnowBoulderMovement(BattleCharacter[] casters)
    {
        if (casters.Length == 0)
        {
            ReleaseRyoqorSnowBoulderMovement("Snow Boulder sequence ended");
            ryoqorSnowBoulderLanes = [];
            ryoqorSnowBoulderDestinationLatched = false;
            ryoqorSnowBoulderWaveKey = null;
            return;
        }

        double leaseMilliseconds = Math.Max(
            500d,
            casters.Max(actor => actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds) + 500d);
        CapabilityManager.Update(
            ryoqorSnowBoulderMovementHandle,
            CapabilityFlags.Movement,
            TimeSpan.FromMilliseconds(leaseMilliseconds),
            "Holding Snow Boulder wave gap");
        ryoqorSnowBoulderMovementOwned = true;

        BattleCharacter[] activeWave = SelectRyoqorSnowBoulderWave(casters);
        string waveKey = string.Join(",", activeWave.Select(actor => actor.ObjectId).OrderBy(id => id));
        if (waveKey == ryoqorSnowBoulderWaveKey)
        {
            return;
        }

        ryoqorSnowBoulderWaveKey = waveKey;
        ryoqorSnowBoulderLanes = activeWave
            .Select(actor => new RyoqorSnowBoulderLane(actor.Location, actor.Heading))
            .ToArray();
        ryoqorSnowBoulderDestinationLatched = TryFindRyoqorSnowBoulderDestination(
            ryoqorSnowBoulderLanes,
            out ryoqorSnowBoulderDestination);
    }

    private static bool TryFindRyoqorSnowBoulderDestination(
        IReadOnlyCollection<RyoqorSnowBoulderLane> lanes,
        out Vector3 destination)
    {
        destination = default;
        double bestScore = double.MaxValue;
        List<Vector3> candidates = [Core.Player.Location];

        for (float xOffset = -RyoqorArenaMovementRadius;
             xOffset <= RyoqorArenaMovementRadius;
             xOffset += RyoqorSnowBoulderCandidateStep)
        {
            for (float zOffset = -RyoqorArenaMovementRadius;
                 zOffset <= RyoqorArenaMovementRadius;
                 zOffset += RyoqorSnowBoulderCandidateStep)
            {
                candidates.Add(new Vector3(
                    ArenaCenter.RyoqorTerteh.X + xOffset,
                    ArenaCenter.RyoqorTerteh.Y,
                    ArenaCenter.RyoqorTerteh.Z + zOffset));
            }
        }

        foreach (Vector3 candidate in candidates)
        {
            float wallClearance = RyoqorArenaSafeRadius - candidate.Distance2D(ArenaCenter.RyoqorTerteh);
            if (wallClearance < RyoqorArenaSafeRadius - RyoqorArenaMovementRadius)
            {
                continue;
            }

            float laneClearance = lanes.Min(lane => DistanceToRyoqorSnowBoulderLane(candidate, lane));
            if (laneClearance < RyoqorSnowBoulderCandidateClearance)
            {
                continue;
            }

            float candidateClearance = Math.Min(wallClearance, laneClearance);
            // Travel remains the dominant cost, while the bounded inverse-clearance term moves a
            // near-edge grid point toward the middle of its pocket when the extra travel is small.
            double score = Core.Player.Distance2D(candidate) +
                2d / Math.Max(RyoqorSnowBoulderCandidateClearance, Math.Min(4f, candidateClearance));
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            destination = candidate;
        }

        return bestScore < double.MaxValue;
    }

    private static float DistanceToRyoqorSnowBoulderLane(Vector3 point, RyoqorSnowBoulderLane lane)
    {
        float deltaX = point.X - lane.Location.X;
        float deltaZ = point.Z - lane.Location.Z;
        float sine = (float)Math.Sin(lane.Heading);
        float cosine = (float)Math.Cos(lane.Heading);
        float localX = deltaX * cosine - deltaZ * sine;
        float localForward = deltaX * sine + deltaZ * cosine;
        float outsideX = Math.Max(Math.Abs(localX) - RyoqorSnowBoulderWidth / 2f, 0f);
        float outsideForward = localForward < 0f
            ? -localForward
            : Math.Max(localForward - RyoqorSnowBoulderLength, 0f);
        return (float)Math.Sqrt(outsideX * outsideX + outsideForward * outsideForward);
    }

    private async Task<bool> MoveToRyoqorSnowBoulderDestination()
    {
        if (!ryoqorSnowBoulderDestinationLatched)
        {
            StopRyoqorSnowBoulderDirectMovement();
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // The avoidance engine now owns the shared mover. Forget our prior command without
            // issuing MoveStop, which could cancel the emergency path during the same frame.
            ryoqorSnowBoulderDirectMovementActive = false;
            return true;
        }

        if (Core.Player.Distance2D(ryoqorSnowBoulderDestination) <= RyoqorSnowBoulderArrivalDistance)
        {
            StopRyoqorSnowBoulderDirectMovement();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(ryoqorSnowBoulderDestination);
        ryoqorSnowBoulderDirectMovementActive = true;
        await Coroutine.Yield();
        return true;
    }

    private void StopRyoqorSnowBoulderDirectMovement()
    {
        if (!ryoqorSnowBoulderDirectMovementActive)
        {
            return;
        }

        Navigator.PlayerMover.MoveStop();
        ryoqorSnowBoulderDirectMovementActive = false;
    }

    private void ReleaseRyoqorSnowBoulderMovement(string reason)
    {
        StopRyoqorSnowBoulderDirectMovement();
        if (!ryoqorSnowBoulderMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(ryoqorSnowBoulderMovementHandle, CapabilityFlags.Movement, reason);
        ryoqorSnowBoulderMovementOwned = false;
    }

    private void UpdateRyoqorFluffleState()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter[] activeCasters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(IsRyoqorFluffleCaster)
            .ToArray();
        HashSet<uint> activeCasterIds = [.. activeCasters.Select(actor => actor.ObjectId)];

        foreach (BattleCharacter caster in activeCasters)
        {
            RyoqorFluffleShape shape = caster.CastingSpellId == EnemyAction.IceScream
                ? RyoqorFluffleShape.IceScream
                : RyoqorFluffleShape.FrozenSwirl;

            TimeSpan remainingCastTime = caster.SpellCastInfo.RemainingCastTime;
            bool hasSnapshot = ryoqorFluffleAoes.TryGetValue(caster.ObjectId, out RyoqorFluffleAoe aoe);
            bool castRestarted = hasSnapshot
                && now > aoe.ActivationUtc + RyoqorFluffleResolutionGrace
                && remainingCastTime > aoe.LastObservedRemainingCastTime + RyoqorFluffleRecastJump;

            // Replace a Fluffle snapshot only when its helper starts a new cast.
            if (!hasSnapshot || castRestarted)
            {
                aoe = new RyoqorFluffleAoe(
                    shape,
                    caster.Location,
                    caster.Heading,
                    now + remainingCastTime,
                    remainingCastTime);
                ryoqorFluffleAoes[caster.ObjectId] = aoe;
            }
            else
            {
                aoe.LastObservedRemainingCastTime = remainingCastTime;
            }

            if (!aoe.Frozen && IsFrozenFluffleHelper(caster))
            {
                aoe.Frozen = true;
                aoe.ActivationUtc += RyoqorFrozenDelay;
            }
        }

        // Keep a frozen snapshot until its delayed impact even if RB clears IsCasting during the
        // pause, but remove completed actors once their cast is no longer the current lifecycle.
        foreach (uint casterId in ryoqorFluffleAoes
                     .Where(pair => !activeCasterIds.Contains(pair.Key)
                         && now > pair.Value.ActivationUtc + RyoqorFluffleResolutionGrace)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            ryoqorFluffleAoes.Remove(casterId);
        }
    }

    private RyoqorFluffleAoe[] GetActiveRyoqorFluffleAoes(RyoqorFluffleShape shape)
    {
        DateTime now = DateTime.UtcNow;
        RyoqorFluffleAoe[] pending = ryoqorFluffleAoes.Values
            .Where(aoe => aoe.Shape == shape && now <= aoe.ActivationUtc + RyoqorFluffleResolutionGrace)
            .ToArray();

        // Cold Feat freezes exactly two helpers of each shape. Withhold the
        // sequence until both tethers are known, preventing a transient all-arena avoid.
        if (pending.Count(aoe => aoe.Frozen) < 2)
        {
            return [];
        }

        // Tether state separates the immediate and Cold Feat-delayed helper waves.
        RyoqorFluffleAoe[] firstWave = pending.Where(aoe => !aoe.Frozen).ToArray();
        if (firstWave.Length > 0)
        {
            return firstWave;
        }

        return pending.Where(aoe => aoe.Frozen).ToArray();
    }

    private static bool IsRyoqorFluffleCaster(BattleCharacter actor) =>
        IsCastingAction(actor, EnemyAction.IceScream) && actor.NpcId == EnemyNpc.RorrlohTeh
        || IsCastingAction(actor, EnemyAction.FrozenSwirlVisual) && actor.NpcId == EnemyNpc.QorrlohTeh;

    private static bool IsFrozenFluffleHelper(BattleCharacter actor) =>
        actor?.VfxContainer?.Tethers?.Any(tether => tether.Id == TetherId.Freeze) == true;

    private void PrioritizeRyoqorSnowballs()
    {
        BattleCharacter snowball = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.Snowball)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.CanAttack)
            .OrderBy(actor => actor.Distance2D())
            .FirstOrDefault();

        if (snowball != null)
        {
            // Use a Kill POI instead of forcing the game's target pointer every tick. This lets the
            // normal combat routine retain movement/rotation ownership while selecting the add.
            if (Poi.Current?.Unit?.ObjectId != snowball.ObjectId)
            {
                Poi.Current = new Poi(snowball, PoiType.Kill);
            }

            return;
        }

        if (Poi.Current?.BattleCharacter?.NpcId == EnemyNpc.Snowball)
        {
            Poi.Clear("Ryoqor Terteh's Snowballs were destroyed");
        }

    }

    private void ResetRyoqorState()
    {
        ryoqorFluffleAoes.Clear();
        ReleaseRyoqorSnowBoulderMovement("Ryoqor Terteh combat ended");
        if (Poi.Current?.BattleCharacter?.NpcId == EnemyNpc.Snowball)
        {
            Poi.Clear("Ryoqor Terteh combat ended");
        }

        ryoqorSnowBoulderLanes = [];
        ryoqorSnowBoulderDestinationLatched = false;
        ryoqorSnowBoulderWaveKey = null;
    }

    private async Task<bool> Kahderyor()
    {
        SidestepPlugin.Enabled = false;

        if (!IsKahderyorCombat())
        {
            ResetKahderyorState("Kahderyor combat ended");
            return false;
        }

        UpdateKahderyorCrystalSources();
        UpdateKahderyorResponseLifecycle();
        PrioritizeKahderyorDebris();

        if (HandleKahderyorGaze())
        {
            return true;
        }

        if (await HandleKahderyorSeedCrystals())
        {
            return true;
        }

        if (await HandleKahderyorCrushTower())
        {
            return true;
        }

        if (await HandleKahderyorWindShot())
        {
            return true;
        }

        if (await HandleKahderyorEarthenShot())
        {
            return true;
        }

        return false;
    }

    private static bool IsKahderyorCombat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CouncilofMorgar;

    private static bool IsKahderyorEarthenShotActive() => IsActionCasting(EnemyAction.EarthenShotAoe);

    private void UpdateKahderyorCrystalSources()
    {
        foreach (BattleCharacter helper in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor => IsCastingAction(actor, EnemyAction.CrystallineCrushAoe, EnemyAction.CrystallineStormAoe)))
        {
            if (kahderyorCrystalSources.ContainsKey(helper.ObjectId))
            {
                continue;
            }

            KahderyorCrystalShape shape = helper.CastingSpellId == EnemyAction.CrystallineCrushAoe
                ? KahderyorCrystalShape.Circle
                : KahderyorCrystalShape.Rectangle;
            kahderyorCrystalSources.Add(helper.ObjectId, new KahderyorCrystalSource(shape, helper.Location, helper.Heading));
        }
    }

    private void UpdateKahderyorResponseLifecycle()
    {
        uint responseAction = IsActionCasting(EnemyAction.WindShotAoe)
            ? EnemyAction.WindShotAoe
            : IsActionCasting(EnemyAction.EarthenShotAoe)
                ? EnemyAction.EarthenShotAoe
                : 0;

        if (responseAction != 0)
        {
            kahderyorActiveResponseAction = responseAction;
            return;
        }

        if (kahderyorActiveResponseAction == 0)
        {
            return;
        }

        kahderyorCompletedResponses++;
        kahderyorActiveResponseAction = 0;

        if (kahderyorCompletedResponses < 2)
        {
            return;
        }

        kahderyorCrystalSources.Clear();
        kahderyorCompletedResponses = 0;
    }

    private void PrioritizeKahderyorDebris()
    {
        BattleCharacter debris = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.CrystallineDebris)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable)
            .OrderBy(actor => actor.Distance2D())
            .FirstOrDefault();

        if (debris != null)
        {
            if (Poi.Current?.Unit?.ObjectId != debris.ObjectId)
            {
                Poi.Current = new Poi(debris, PoiType.Kill);
            }

            return;
        }

        if (Poi.Current?.BattleCharacter?.NpcId == EnemyNpc.CrystallineDebris)
        {
            Poi.Clear("Kahderyor's Crystalline Debris was destroyed");
        }

    }

    private async Task<bool> HandleKahderyorCrushTower()
    {
        BattleCharacter tower = GetActiveCaster(EnemyAction.CrystallineCrushAoe);
        if (tower == null)
        {
            ReleaseKahderyorMovement(kahderyorCrushMovementHandle, ref kahderyorCrushMovementOwned, "Crystalline Crush tower ended");
            return false;
        }

        kahderyorCrushMovementOwned = true;
        CapabilityManager.Update(kahderyorCrushMovementHandle, CapabilityFlags.Movement, tower.SpellCastInfo.RemainingCastTime, "Soaking Kahderyor's Crystalline Crush tower");

        if (AvoidanceManager.IsRunningOutOfAvoid || Core.Player.Location.Distance2D(tower.Location) <= KahderyorArrivalDistance)
        {
            return false;
        }

        await CommonTasks.MoveTo(tower.Location);
        return true;
    }

    private async Task<bool> HandleKahderyorSeedCrystals()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter seedCrystals = GetPlayerTargetedCaster(EnemyAction.SeedCrystalsAoe);
        if (seedCrystals != null)
        {
            kahderyorSeedHoldUntilUtc = now + seedCrystals.SpellCastInfo.RemainingCastTime + KahderyorResponseImpactGrace;
            kahderyorSeedLastOtherTargets = GetOtherTargetLocations(EnemyAction.SeedCrystalsAoe);
        }

        bool sequenceActive = seedCrystals != null ||
            kahderyorSeedDestinationLatched && now <= kahderyorSeedHoldUntilUtc;
        if (!sequenceActive)
        {
            StopKahderyorResponseDirectMovement(EnemyAction.SeedCrystalsAoe);
            ReleaseKahderyorMovement(
                kahderyorSeedMovementHandle,
                ref kahderyorSeedMovementOwned,
                "Seed Crystals impact grace ended");
            kahderyorSeedDestinationLatched = false;
            kahderyorSeedLastOtherTargets = [];
            kahderyorSeedHoldUntilUtc = DateTime.MinValue;
            kahderyorSeedNextRecheckUtc = DateTime.MinValue;
            return false;
        }

        Vector3[] otherTargets = seedCrystals != null
            ? kahderyorSeedLastOtherTargets
            : GetOtherVisiblePartyLocations();

        // Crystal Burden/Crystallized proves that the spread already resolved and movement input is
        // rejected. Do not let the impact grace overwrite the dedicated crystallization lock.
        if (seedCrystals == null && GetKahderyorCrystallizationLock() != null)
        {
            StopKahderyorResponseDirectMovement(EnemyAction.SeedCrystalsAoe);
            ReleaseKahderyorMovement(
                kahderyorSeedMovementHandle,
                ref kahderyorSeedMovementOwned,
                "Seed Crystals resolved into a crystallization lock");
            return false;
        }

        bool shouldRecheck = now >= kahderyorSeedNextRecheckUtc;
        bool destinationStillSafe = kahderyorSeedDestinationLatched;
        if (kahderyorSeedDestinationLatched && shouldRecheck)
        {
            destinationStillSafe = IsStableSpreadDestinationSafe(
                kahderyorSeedDestination,
                ArenaCenter.Kahderyor,
                KahderyorArenaMovementRadius,
                circularArena: true,
                otherTargets,
                KahderyorSpreadRetentionDistance,
                IsOutsideActiveAvoids);
        }

        if (shouldRecheck)
        {
            kahderyorSeedNextRecheckUtc = now + KahderyorDestinationRecheckInterval;
        }

        if (!destinationStillSafe && shouldRecheck)
        {
            if (!TryFindStableSpreadDestination(
                    Core.Player.Location,
                    ArenaCenter.Kahderyor,
                    KahderyorArenaMovementRadius,
                    KahderyorResponseCandidateStep,
                    circularArena: true,
                    otherTargets,
                    KahderyorSpreadAcquisitionDistance,
                    IsOutsideActiveAvoids,
                    out Vector3 destination))
            {
                StopKahderyorResponseDirectMovement(EnemyAction.SeedCrystalsAoe);
                ReleaseKahderyorMovement(
                    kahderyorSeedMovementHandle,
                    ref kahderyorSeedMovementOwned,
                    "Seed Crystals planner found no valid point");
                kahderyorSeedDestinationLatched = false;
                return false;
            }

            kahderyorSeedDestination = destination;
            kahderyorSeedDestinationLatched = true;
        }

        if (!kahderyorSeedDestinationLatched)
        {
            return false;
        }

        kahderyorSeedMovementOwned = true;
        CapabilityManager.Update(
            kahderyorSeedMovementHandle,
            CapabilityFlags.Movement,
            GetKahderyorResponseLease(kahderyorSeedHoldUntilUtc),
            "Holding Kahderyor's planned Seed Crystals spread");
        return await MoveToKahderyorResponseDestination(
            kahderyorSeedDestination,
            EnemyAction.SeedCrystalsAoe);
    }

    private async Task<bool> HandleKahderyorWindShot()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter windShot = GetPlayerTargetedCaster(EnemyAction.WindShotAoe);
        if (windShot != null)
        {
            kahderyorWindHoldUntilUtc = now + windShot.SpellCastInfo.RemainingCastTime + KahderyorResponseImpactGrace;
        }

        if (windShot == null && (!kahderyorWindDestinationLatched || now > kahderyorWindHoldUntilUtc))
        {
            StopKahderyorResponseDirectMovement(EnemyAction.WindShotAoe);
            ReleaseKahderyorMovement(kahderyorWindMovementHandle, ref kahderyorWindMovementOwned, "Wind Shot impact grace ended");
            kahderyorWindDestinationLatched = false;
            kahderyorWindHoldUntilUtc = DateTime.MinValue;
            kahderyorWindNextRecheckUtc = DateTime.MinValue;
            return false;
        }

        Vector3[] otherDonutTargets = GetOtherTargetLocations(EnemyAction.WindShotAoe);
        bool shouldRecheck = now >= kahderyorWindNextRecheckUtc;
        bool destinationStillSafe = kahderyorWindDestinationLatched;
        if (kahderyorWindDestinationLatched && shouldRecheck)
        {
            destinationStillSafe = IsKahderyorWindDestinationSafe(
                kahderyorWindDestination,
                otherDonutTargets,
                kahderyorCrystalSources.Values,
                KahderyorWindDonutRetentionMargin);
        }

        if (shouldRecheck)
        {
            kahderyorWindNextRecheckUtc = now + KahderyorDestinationRecheckInterval;
        }

        if (!destinationStillSafe && shouldRecheck)
        {
            if (kahderyorCrystalSources.Count == 0 || !TryFindKahderyorWindDestination(
                    Core.Player.Location,
                    otherDonutTargets,
                    kahderyorCrystalSources.Values,
                    out Vector3 plannedDestination))
            {
                StopKahderyorResponseDirectMovement(EnemyAction.WindShotAoe);
                ReleaseKahderyorMovement(
                    kahderyorWindMovementHandle,
                    ref kahderyorWindMovementOwned,
                    "Wind Shot planner found no currently valid point");
                kahderyorWindDestinationLatched = false;
                return false;
            }

            kahderyorWindDestination = plannedDestination;
            kahderyorWindDestinationLatched = true;
        }

        if (!kahderyorWindDestinationLatched)
        {
            return false;
        }

        kahderyorWindMovementOwned = true;
        CapabilityManager.Update(
            kahderyorWindMovementHandle,
            CapabilityFlags.Movement,
            GetKahderyorResponseLease(kahderyorWindHoldUntilUtc),
            "Holding Kahderyor's crystal-defined Wind Shot stack");
        return await MoveToKahderyorResponseDestination(kahderyorWindDestination, EnemyAction.WindShotAoe);
    }

    private static bool TryFindKahderyorWindDestination(
        Vector3 playerLocation,
        IReadOnlyCollection<Vector3> otherDonutTargets,
        IReadOnlyCollection<KahderyorCrystalSource> sources,
        out Vector3 destination)
    {
        destination = default;
        if (sources.Count == 0)
        {
            return false;
        }

        double bestScore = double.MaxValue;
        List<Vector3> candidates = [playerLocation];
        for (float xOffset = -KahderyorArenaMovementRadius;
             xOffset <= KahderyorArenaMovementRadius;
             xOffset += KahderyorResponseCandidateStep)
        {
            for (float zOffset = -KahderyorArenaMovementRadius;
                 zOffset <= KahderyorArenaMovementRadius;
                 zOffset += KahderyorResponseCandidateStep)
            {
                candidates.Add(new Vector3(
                    ArenaCenter.Kahderyor.X + xOffset,
                    ArenaCenter.Kahderyor.Y,
                    ArenaCenter.Kahderyor.Z + zOffset));
            }
        }

        foreach (Vector3 candidate in candidates)
        {
            float wallClearance = KahderyorArenaSafeRadius - candidate.Distance2D(ArenaCenter.Kahderyor);
            if (wallClearance < KahderyorArenaSafeRadius - KahderyorArenaMovementRadius)
            {
                continue;
            }

            float crystalClearance = sources.Max(source => DistanceInsideKahderyorWindSafeZone(candidate, source));
            if (crystalClearance < KahderyorWindCrystalSafetyMargin)
            {
                continue;
            }

            float donutClearance = otherDonutTargets.Count == 0
                ? float.MaxValue
                : otherDonutTargets.Min(target => DistanceOutsideKahderyorWindDonutBand(candidate, target));
            if (donutClearance < KahderyorWindDonutSafetyMargin)
            {
                continue;
            }

            float candidateClearance = Math.Min(wallClearance, Math.Min(crystalClearance, donutClearance));
            double score = playerLocation.Distance2D(candidate) +
                3d / Math.Max(KahderyorWindCrystalSafetyMargin, Math.Min(4f, candidateClearance));
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            destination = candidate;
        }

        return bestScore < double.MaxValue;
    }

    private static bool IsKahderyorWindDestinationSafe(
        Vector3 destination,
        IReadOnlyCollection<Vector3> otherDonutTargets,
        IReadOnlyCollection<KahderyorCrystalSource> sources,
        float donutSafetyMargin) =>
        destination.Distance2D(ArenaCenter.Kahderyor) <= KahderyorArenaMovementRadius &&
        sources.Count > 0 &&
        sources.Max(source => DistanceInsideKahderyorWindSafeZone(destination, source)) >= KahderyorWindCrystalSafetyMargin &&
        otherDonutTargets.All(target =>
            DistanceOutsideKahderyorWindDonutBand(destination, target) >= donutSafetyMargin);

    private static float DistanceInsideKahderyorWindSafeZone(Vector3 point, KahderyorCrystalSource source)
    {
        if (source.Shape == KahderyorCrystalShape.Circle)
        {
            return KahderyorCrushInRadius - point.Distance2D(source.Location);
        }

        float deltaX = point.X - source.Location.X;
        float deltaZ = point.Z - source.Location.Z;
        float sine = (float)Math.Sin(source.Heading);
        float cosine = (float)Math.Cos(source.Heading);
        float localX = deltaX * cosine - deltaZ * sine;
        float localForward = deltaX * sine + deltaZ * cosine;
        return Math.Min(
            KahderyorStormInHalfWidth - Math.Abs(localX),
            KahderyorStormHalfLength - Math.Abs(localForward));
    }

    private static float DistanceOutsideKahderyorWindDonutBand(Vector3 point, Vector3 donutCenter)
    {
        float distance = point.Distance2D(donutCenter);
        if (distance <= KahderyorWindDonutInnerRadius)
        {
            return KahderyorWindDonutInnerRadius - distance;
        }

        if (distance >= KahderyorWindDonutOuterRadius)
        {
            return distance - KahderyorWindDonutOuterRadius;
        }

        return -Math.Min(
            distance - KahderyorWindDonutInnerRadius,
            KahderyorWindDonutOuterRadius - distance);
    }

    private async Task<bool> HandleKahderyorEarthenShot()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter earthenShot = GetPlayerTargetedCaster(EnemyAction.EarthenShotAoe);
        if (earthenShot != null)
        {
            kahderyorEarthenHoldUntilUtc = now + earthenShot.SpellCastInfo.RemainingCastTime + KahderyorResponseImpactGrace;
            kahderyorEarthenLastOtherTargets = GetOtherTargetLocations(EnemyAction.EarthenShotAoe);
        }

        bool sequenceActive = earthenShot != null ||
            kahderyorEarthenDestinationLatched && now <= kahderyorEarthenHoldUntilUtc;
        if (!sequenceActive)
        {
            StopKahderyorResponseDirectMovement(EnemyAction.EarthenShotAoe);
            ReleaseKahderyorMovement(kahderyorEarthenMovementHandle, ref kahderyorEarthenMovementOwned, "Earthen Shot impact grace ended");
            kahderyorEarthenDestinationLatched = false;
            kahderyorEarthenHoldUntilUtc = DateTime.MinValue;
            kahderyorEarthenNextRecheckUtc = DateTime.MinValue;
            kahderyorEarthenLastOtherTargets = [];
            return false;
        }

        Vector3[] otherSpreadTargets = earthenShot != null
            ? kahderyorEarthenLastOtherTargets
            : GetOtherVisiblePartyLocations();

        bool shouldRecheck = earthenShot != null && now >= kahderyorEarthenNextRecheckUtc;
        bool destinationStillSafe = kahderyorEarthenDestinationLatched;
        if (kahderyorEarthenDestinationLatched && shouldRecheck)
        {
            destinationStillSafe = IsKahderyorEarthenDestinationSafe(
                kahderyorEarthenDestination,
                otherSpreadTargets,
                kahderyorCrystalSources.Values,
                !Core.Player.IsMelee());
        }

        if (shouldRecheck)
        {
            kahderyorEarthenNextRecheckUtc = now + KahderyorDestinationRecheckInterval;
        }

        if (!destinationStillSafe && shouldRecheck)
        {
            if (kahderyorCrystalSources.Count == 0 || !TryFindKahderyorEarthenDestination(
                    Core.Player.Location,
                    otherSpreadTargets,
                    kahderyorCrystalSources.Values,
                    !Core.Player.IsMelee(),
                    out kahderyorEarthenDestination))
            {
                StopKahderyorResponseDirectMovement(EnemyAction.EarthenShotAoe);
                ReleaseKahderyorMovement(
                    kahderyorEarthenMovementHandle,
                    ref kahderyorEarthenMovementOwned,
                    "Earthen Shot planner found no currently valid point");
                kahderyorEarthenDestinationLatched = false;
                return false;
            }

            kahderyorEarthenDestinationLatched = true;
        }

        if (!kahderyorEarthenDestinationLatched)
        {
            return false;
        }

        kahderyorEarthenMovementOwned = true;
        CapabilityManager.Update(
            kahderyorEarthenMovementHandle,
            CapabilityFlags.Movement,
            GetKahderyorResponseLease(kahderyorEarthenHoldUntilUtc),
            "Holding Kahderyor's planned Earthen Shot pocket");
        return await MoveToKahderyorResponseDestination(kahderyorEarthenDestination, EnemyAction.EarthenShotAoe);
    }

    private static bool IsKahderyorEarthenDestinationSafe(
        Vector3 destination,
        IReadOnlyCollection<Vector3> otherSpreadTargets,
        IReadOnlyCollection<KahderyorCrystalSource> sources,
        bool preferOuterPocket)
    {
        float wallClearance = KahderyorArenaSafeRadius - destination.Distance2D(ArenaCenter.Kahderyor);
        return sources.Count > 0 &&
            wallClearance >= KahderyorArenaSafeRadius - KahderyorArenaMovementRadius &&
            (!preferOuterPocket || destination.Distance2D(ArenaCenter.Kahderyor) >= KahderyorRangedEarthenInnerRadius) &&
            sources.All(source =>
                DistanceOutsideKahderyorEarthenHazard(destination, source) >= KahderyorResponseCandidateClearance) &&
            otherSpreadTargets.All(target =>
                destination.Distance2D(target) >= KahderyorSpreadRetentionDistance);
    }

    private static bool TryFindKahderyorEarthenDestination(
        Vector3 playerLocation,
        IReadOnlyCollection<Vector3> otherSpreadTargets,
        IReadOnlyCollection<KahderyorCrystalSource> sources,
        bool preferOuterPocket,
        out Vector3 destination)
    {
        destination = default;
        if (sources.Count == 0)
        {
            return false;
        }

        double bestScore = double.MaxValue;
        List<Vector3> candidates = [playerLocation];
        for (float xOffset = -KahderyorArenaMovementRadius;
             xOffset <= KahderyorArenaMovementRadius;
             xOffset += KahderyorResponseCandidateStep)
        {
            for (float zOffset = -KahderyorArenaMovementRadius;
                 zOffset <= KahderyorArenaMovementRadius;
                 zOffset += KahderyorResponseCandidateStep)
            {
                candidates.Add(new Vector3(
                    ArenaCenter.Kahderyor.X + xOffset,
                    ArenaCenter.Kahderyor.Y,
                    ArenaCenter.Kahderyor.Z + zOffset));
            }
        }

        foreach (Vector3 candidate in candidates)
        {
            float wallClearance = KahderyorArenaSafeRadius - candidate.Distance2D(ArenaCenter.Kahderyor);
            if (wallClearance < KahderyorArenaSafeRadius - KahderyorArenaMovementRadius)
            {
                continue;
            }

            if (preferOuterPocket && candidate.Distance2D(ArenaCenter.Kahderyor) < KahderyorRangedEarthenInnerRadius)
            {
                continue;
            }

            float crystalClearance = sources.Min(source => DistanceOutsideKahderyorEarthenHazard(candidate, source));
            if (crystalClearance < KahderyorResponseCandidateClearance)
            {
                continue;
            }

            float spreadClearance = otherSpreadTargets.Count == 0
                ? float.MaxValue
                : otherSpreadTargets.Min(target => candidate.Distance2D(target) - 6f);
            if (spreadClearance < KahderyorResponseCandidateClearance)
            {
                continue;
            }

            float candidateClearance = Math.Min(wallClearance, Math.Min(crystalClearance, spreadClearance));
            // Travel dominates so the bot commits quickly. The bounded inverse-clearance term
            // favors the middle of a small pocket when nearby grid points require similar travel.
            double score = playerLocation.Distance2D(candidate) +
                3d / Math.Max(KahderyorResponseCandidateClearance, Math.Min(4f, candidateClearance));
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            destination = candidate;
        }

        return bestScore < double.MaxValue;
    }

    private static float DistanceOutsideKahderyorEarthenHazard(Vector3 point, KahderyorCrystalSource source)
    {
        if (source.Shape == KahderyorCrystalShape.Circle)
        {
            return Math.Max(0f, point.Distance2D(source.Location) - KahderyorCrushOutRadius);
        }

        float deltaX = point.X - source.Location.X;
        float deltaZ = point.Z - source.Location.Z;
        float sine = (float)Math.Sin(source.Heading);
        float cosine = (float)Math.Cos(source.Heading);
        float localX = deltaX * cosine - deltaZ * sine;
        float localForward = deltaX * sine + deltaZ * cosine;
        float outsideX = Math.Max(Math.Abs(localX) - KahderyorStormOutHalfWidth, 0f);
        float outsideForward = Math.Max(Math.Abs(localForward) - KahderyorStormHalfLength, 0f);
        return (float)Math.Sqrt(outsideX * outsideX + outsideForward * outsideForward);
    }

    private static Vector3[] GetOtherTargetLocations(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => IsCastingAction(actor, actionId) && actor.SpellCastInfo.TargetId != Core.Player.ObjectId)
            .Select(actor => GameObjectManager.GetObjectByObjectId(actor.SpellCastInfo.TargetId))
            .Where(target => target != null)
            .GroupBy(target => target.ObjectId)
            .Select(group => group.First().Location)
            .ToArray();

    private static Vector3[] GetOtherVisiblePartyLocations() =>
        PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.ObjectId != Core.Player.ObjectId)
            .GroupBy(actor => actor.ObjectId)
            .Select(group => group.First().Location)
            .ToArray();

    private static bool TryFindStableSpreadDestination(
        Vector3 playerLocation,
        Vector3 arenaCenter,
        float movementExtent,
        float gridStep,
        bool circularArena,
        IReadOnlyCollection<Vector3> otherTargets,
        float requiredSeparation,
        Func<Vector3, bool> additionalSafety,
        out Vector3 destination)
    {
        destination = default;
        double bestScore = double.MaxValue;
        List<Vector3> candidates = [playerLocation];

        for (float xOffset = -movementExtent; xOffset <= movementExtent; xOffset += gridStep)
        {
            for (float zOffset = -movementExtent; zOffset <= movementExtent; zOffset += gridStep)
            {
                candidates.Add(new Vector3(
                    arenaCenter.X + xOffset,
                    arenaCenter.Y,
                    arenaCenter.Z + zOffset));
            }
        }

        foreach (Vector3 candidate in candidates)
        {
            float edgeDistance = circularArena
                ? candidate.Distance2D(arenaCenter)
                : Math.Max(Math.Abs(candidate.X - arenaCenter.X), Math.Abs(candidate.Z - arenaCenter.Z));
            float wallClearance = movementExtent - edgeDistance;
            if (wallClearance < 0f || !additionalSafety(candidate))
            {
                continue;
            }

            float separation = GetMinimumSeparation(candidate, otherTargets);
            if (separation < requiredSeparation)
            {
                continue;
            }

            float spreadReserve = float.IsPositiveInfinity(separation)
                ? wallClearance
                : separation - requiredSeparation;
            float safetyReserve = Math.Min(wallClearance, spreadReserve);
            double score = playerLocation.Distance2D(candidate) +
                3d / Math.Max(0.25f, Math.Min(4f, safetyReserve + 0.25f));
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            destination = candidate;
        }

        return bestScore < double.MaxValue;
    }

    private static bool IsStableSpreadDestinationSafe(
        Vector3 destination,
        Vector3 arenaCenter,
        float movementExtent,
        bool circularArena,
        IReadOnlyCollection<Vector3> otherTargets,
        float requiredSeparation,
        Func<Vector3, bool> additionalSafety)
    {
        float edgeDistance = circularArena
            ? destination.Distance2D(arenaCenter)
            : Math.Max(Math.Abs(destination.X - arenaCenter.X), Math.Abs(destination.Z - arenaCenter.Z));
        return edgeDistance <= movementExtent &&
            additionalSafety(destination) &&
            otherTargets.All(target => destination.Distance2D(target) >= requiredSeparation);
    }

    private static bool IsOutsideActiveAvoids(Vector3 destination) =>
        !AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(destination));

    private static float GetMinimumSeparation(Vector3 location, IEnumerable<Vector3> otherTargets)
    {
        Vector3[] targets = otherTargets.ToArray();
        return targets.Length == 0
            ? float.PositiveInfinity
            : targets.Min(target => location.Distance2D(target));
    }

    private async Task<bool> MoveToKahderyorResponseDestination(Vector3 destination, uint actionId)
    {
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            if (kahderyorResponseDirectMovementAction == actionId)
            {
                kahderyorResponseDirectMovementAction = 0;
            }

            return true;
        }

        if (Core.Player.Distance2D(destination) <= KahderyorResponseArrivalDistance)
        {
            StopKahderyorResponseDirectMovement(actionId);
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(destination);
        kahderyorResponseDirectMovementAction = actionId;
        await Coroutine.Yield();
        return true;
    }

    private void StopKahderyorResponseDirectMovement(uint actionId)
    {
        if (kahderyorResponseDirectMovementAction == 0 ||
            actionId != 0 && kahderyorResponseDirectMovementAction != actionId)
        {
            return;
        }

        Navigator.PlayerMover.MoveStop();
        kahderyorResponseDirectMovementAction = 0;
    }

    private static TimeSpan GetKahderyorResponseLease(DateTime holdUntilUtc) =>
        TimeSpan.FromMilliseconds(Math.Max(250d, (holdUntilUtc - DateTime.UtcNow).TotalMilliseconds));

    private bool HandleKahderyorGaze()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter gaze = GetActiveCaster(EnemyAction.EyeOfTheFierce);
        BattleCharacter stalagmite = GetActiveCaster(EnemyAction.StalagmiteCircleAoe);
        BattleCharacter cyclonic = GetActiveCaster(EnemyAction.CyclonicRingAoe);
        Aura crystallizationLock = GetKahderyorCrystallizationLock();

        if (gaze != null)
        {
            kahderyorGazeOrigin = gaze.Location;
            kahderyorGazeHoldUntilUtc = now + gaze.SpellCastInfo.RemainingCastTime + KahderyorGazeImpactGrace;

            if (!kahderyorGazeFacingOwned)
            {
                kahderyorGazeFacingOwned = true;
            }
        }

        TimeSpan holdRemaining = kahderyorGazeHoldUntilUtc - now;
        Vector3 away = Core.Player.Location - kahderyorGazeOrigin;
        double length = Math.Sqrt(away.X * away.X + away.Z * away.Z);
        if (length < 0.1d)
        {
            away = new Vector3(0f, 0f, 1f);
            length = 1d;
        }

        float desiredHeading = (float)Math.Atan2(away.X, away.Z);

        // Crystallization blocks movement but leaves attacks on Crystalline Debris schedulable.
        if (crystallizationLock != null)
        {
            kahderyorGazeMovementOwned = true;
            TimeSpan crystallizationLease = crystallizationLock.TimespanLeft + KahderyorGazeImpactGrace;
            if (crystallizationLease < TimeSpan.FromMilliseconds(250))
            {
                crystallizationLease = TimeSpan.FromMilliseconds(250);
            }

            CapabilityManager.Update(
                kahderyorGazeMovementHandle,
                CapabilityFlags.Movement,
                crystallizationLease,
                "Pausing Kahderyor movement while crystallization prevents input");
            Navigator.Stop();
            Navigator.PlayerMover.MoveStop();
            MovementManager.MoveStop();

            // Preserve an already-active gaze facing while crystallized.
            if (kahderyorGazeFacingOwned && holdRemaining > TimeSpan.Zero)
            {
                CapabilityManager.Update(
                    kahderyorGazeFacingHandle,
                    CapabilityFlags.Facing,
                    holdRemaining,
                    "Reserving Eye of the Fierce facing while crystallization prevents movement");
                Core.Player.SetFacing(desiredHeading);
            }

            return false;
        }

        if (!kahderyorGazeFacingOwned || holdRemaining <= TimeSpan.Zero)
        {
            ReleaseKahderyorGaze("Eye of the Fierce impact grace ended");
            return false;
        }

        CapabilityManager.Update(
            kahderyorGazeFacingHandle,
            CapabilityFlags.Facing,
            holdRemaining,
            "Reserving Eye of the Fierce facing through its movement and impact phases");

        BattleCharacter geometry = stalagmite ?? cyclonic;
        if (geometry == null)
        {
            if (kahderyorGazeMovementOwned)
            {
                Navigator.PlayerMover.MoveStop();
                MovementManager.MoveStop();
            }

            // Once the helper casts disappear, retain only the gaze-facing input through the
            // impact grace. This prevents a final pathing tick from turning back toward the eye.
            Core.Player.SetFacing(desiredHeading);
            return !IsKahderyorFacingAway(desiredHeading);
        }

        uint geometryAction = geometry.CastingSpellId;
        if (!kahderyorGazeMovementOwned)
        {
            kahderyorGazeMovementOwned = true;
            Navigator.Stop();
            MovementManager.MoveStop();
        }

        CapabilityManager.Update(
            kahderyorGazeMovementHandle,
            CapabilityFlags.Movement,
            holdRemaining,
            "Holding gaze-safe movement for Kahderyor's combined in/out mechanic");

        float geometryDistance = Core.Player.Location.Distance2D(geometry.Location);
        bool needsMovement = geometryAction == EnemyAction.StalagmiteCircleAoe
            ? geometryDistance < KahderyorStalagmiteSafeRadius
            : geometryDistance > KahderyorCyclonicSafeRadius;

        if (geometryAction == EnemyAction.CyclonicRingAoe && needsMovement)
        {
            // Use position-based Cyclonic Ring ingress, then restore the gaze heading.
            MovementManager.MoveStop();
            Navigator.PlayerMover.MoveTowards(geometry.Location);
            return true;
        }

        // Navigator is permitted only for Cyclonic's early ingress because it faces the waypoint.
        // Stop it before applying the gaze heading, including on the first safe-zone tick.
        Navigator.PlayerMover.MoveStop();
        Core.Player.SetFacing(desiredHeading);

        if (!IsKahderyorFacingAway(desiredHeading))
        {
            // Wait for the look-away heading before moving forward out of Stalagmite Circle.
            MovementManager.MoveStop();
            return true;
        }

        if (!needsMovement)
        {
            MovementManager.MoveStop();
            return false;
        }

        // Stalagmite is the one radial transition that can safely share movement and gaze facing:
        // Forward while looking away from the center moves out of its 15-yalm circle.
        MovementManager.Move(MovementDirection.Forward, KahderyorGazeMovementPulse);
        return true;
    }

    private static Aura GetKahderyorCrystallizationLock() =>
        Core.Player.Auras.AuraList.FirstOrDefault(aura =>
            aura.Id == PlayerAura.CrystalBurden ||
            aura.Id == PlayerAura.Crystallized ||
            aura.IsDebuff &&
            ((aura.Name?.IndexOf("Crystal Burden", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
             (aura.LocalizedName?.IndexOf("Crystal Burden", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
             (aura.Name?.IndexOf("Crystallized", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
             (aura.LocalizedName?.IndexOf("Crystallized", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0));

    private static bool IsKahderyorFacingAway(float desiredHeading) =>
        GetKahderyorHeadingErrorRadians(desiredHeading) <= KahderyorGazeFacingToleranceRadians;

    private static double GetKahderyorHeadingErrorRadians(float desiredHeading)
    {
        double difference = Math.Abs(Core.Player.Heading - desiredHeading) % (Math.PI * 2d);
        return Math.Min(difference, Math.PI * 2d - difference);
    }

    private void ReleaseKahderyorGaze(string reason)
    {
        if (kahderyorGazeMovementOwned)
        {
            Navigator.PlayerMover.MoveStop();
            MovementManager.MoveStop();
            CapabilityManager.Clear(kahderyorGazeMovementHandle, CapabilityFlags.Movement, reason);
            kahderyorGazeMovementOwned = false;
        }

        if (kahderyorGazeFacingOwned)
        {
            CapabilityManager.Clear(kahderyorGazeFacingHandle, CapabilityFlags.Facing, reason);
            kahderyorGazeFacingOwned = false;
        }

        kahderyorGazeHoldUntilUtc = DateTime.MinValue;
    }

    private static void ReleaseKahderyorMovement(CapabilityManagerHandle handle, ref bool owned, string reason)
    {
        if (!owned)
        {
            return;
        }

        CapabilityManager.Clear(handle, CapabilityFlags.Movement, reason);
        owned = false;
    }

    private void ResetKahderyorState(string reason)
    {
        kahderyorCrystalSources.Clear();
        kahderyorActiveResponseAction = 0;
        kahderyorCompletedResponses = 0;
        kahderyorSeedDestinationLatched = false;
        kahderyorSeedLastOtherTargets = [];
        kahderyorWindDestinationLatched = false;
        kahderyorEarthenDestinationLatched = false;
        kahderyorEarthenLastOtherTargets = [];
        kahderyorSeedHoldUntilUtc = DateTime.MinValue;
        kahderyorSeedNextRecheckUtc = DateTime.MinValue;
        kahderyorWindHoldUntilUtc = DateTime.MinValue;
        kahderyorWindNextRecheckUtc = DateTime.MinValue;
        kahderyorEarthenHoldUntilUtc = DateTime.MinValue;
        kahderyorEarthenNextRecheckUtc = DateTime.MinValue;
        StopKahderyorResponseDirectMovement(0);
        ReleaseKahderyorMovement(kahderyorCrushMovementHandle, ref kahderyorCrushMovementOwned, reason);
        ReleaseKahderyorMovement(kahderyorSeedMovementHandle, ref kahderyorSeedMovementOwned, reason);
        ReleaseKahderyorMovement(kahderyorWindMovementHandle, ref kahderyorWindMovementOwned, reason);
        ReleaseKahderyorMovement(kahderyorEarthenMovementHandle, ref kahderyorEarthenMovementOwned, reason);
        ReleaseKahderyorGaze(reason);
    }

    private static BattleCharacter GetActiveCaster(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => IsCastingAction(actor, actionId))
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    private static BattleCharacter GetPlayerTargetedCaster(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => IsCastingAction(actor, actionId) && actor.SpellCastInfo.TargetId == Core.Player.ObjectId)
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    private static bool IsActionCasting(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor => IsCastingAction(actor, actionId));

    private static bool IsCastingAction(BattleCharacter actor, params uint[] actionIds) =>
        actor != null && actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid && actionIds.Contains(actor.CastingSpellId);

    private async Task<bool> Gurfurlur()
    {
        if (!IsGurfurlurCombat())
        {
            ResetGurfurlurState("Gurfurlur combat ended");
            return false;
        }

        BattleCharacter volcanicDrop = GetPlayerTargetedCaster(EnemyAction.VolcanicDropAoe);
        BattleCharacter auraSphere = GetGurfurlurAuraSphere();
        BattleCharacter greatFlood = GetActiveCaster(EnemyAction.GreatFlood);
        BattleCharacter windswrath = GetActiveCaster(EnemyAction.WindswrathShort) ?? GetActiveCaster(EnemyAction.WindswrathLong);
        BattleCharacter sledgehammer = GetActiveCaster(EnemyAction.Sledgehammer);
        BattleCharacter[] bitingWinds = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.BitingWind)
            .Where(IsGurfurlurBitingWind)
            .ToArray();

        ReleaseInactiveGurfurlurMechanics(auraSphere, greatFlood, windswrath, sledgehammer);

        if (bitingWinds.Length == 0)
        {
            ReleaseGurfurlurBitingWindForecast("No live Biting Wind remains");
        }

        // Volcanic Drop overlaps the tile sequence, so its stable spread assignment takes
        // precedence over every other positive destination until the impact grace expires.
        if (volcanicDrop != null ||
            gurfurlurVolcanicDropDestinationLatched && DateTime.UtcNow <= gurfurlurVolcanicDropHoldUntilUtc)
        {
            ReleaseGurfurlurBitingWindForecast("Volcanic Drop spread has priority");
            return await HandleGurfurlurVolcanicDrop(volcanicDrop);
        }

        ReleaseGurfurlurVolcanicDrop("Volcanic Drop impact grace ended");

        if (auraSphere != null)
        {
            ReleaseGurfurlurBitingWindForecast("Aura Sphere interception has priority");
            return await HandleGurfurlurAuraSphere(auraSphere);
        }

        if (greatFlood != null)
        {
            ReleaseGurfurlurBitingWindForecast("Great Flood staging has priority");
            return await HandleGurfurlurGreatFlood(greatFlood);
        }

        if (windswrath != null)
        {
            return await HandleGurfurlurWindswrath(windswrath, bitingWinds);
        }

        if (IsGurfurlurLongWindswrathImpactHoldActive())
        {
            ReleaseGurfurlurBitingWindForecast("Long Windswrath impact grace has priority");
            if (!gurfurlurWindswrathDestinationReached)
            {
                // Release an unreached wedge when the knockback helper disappears.
                gurfurlurLongWindswrathActive = false;
                gurfurlurWindswrathHoldUntilUtc = DateTime.MinValue;
                gurfurlurWindswrathDestinationLatched = false;
                gurfurlurWindswrathRouteCommitted = false;
                ReleaseGurfurlurMovement(
                    gurfurlurWindswrathMovement,
                    "Long Windswrath ended before the pattern wedge was reached");
                return false;
            }

            if (gurfurlurWindswrathDestinationReached &&
                Core.Player.Distance2D(gurfurlurWindswrathDestination) >=
                GurfurlurWindswrathResolvedDisplacementDistance)
            {
                // Release the wedge after knockback displacement is observed.
                gurfurlurLongWindswrathActive = false;
                gurfurlurWindswrathHoldUntilUtc = DateTime.MinValue;
                ReleaseGurfurlurMovement(
                    gurfurlurWindswrathMovement,
                    "Long Windswrath knockback displacement observed");
                return false;
            }

            return await MoveToGurfurlurPosition(
                gurfurlurWindswrathMovement,
                gurfurlurWindswrathDestination,
                GurfurlurLongWindswrathFinalArrivalDistance,
                gurfurlurWindswrathHoldUntilUtc - DateTime.UtcNow,
                "Holding the committed Long Windswrath wedge through server resolution",
                yieldToAvoidance: false);
        }

        if (sledgehammer != null || IsGurfurlurSledgehammerFollowupActive())
        {
            ReleaseGurfurlurBitingWindForecast("Sledgehammer stack has priority");
            return await HandleGurfurlurSledgehammer(sledgehammer);
        }

        if (bitingWinds.Length > 0)
        {
            return await HandleGurfurlurBitingWindForecast(bitingWinds, TimeSpan.FromSeconds(2));
        }

        return false;
    }

    private static bool IsGurfurlurCombat() =>
        Core.Player.InCombat &&
        WorldManager.SubZoneId == (uint)SubZoneId.KarryortheResting &&
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.Gurfurlur)
            .Any(actor => actor.IsValid && actor.IsAlive);

    private bool IsGurfurlurBitingWindBodyAvoidActive() =>
        IsGurfurlurCombat() &&
        (!gurfurlurLongWindswrathActive || !gurfurlurWindswrathRouteCommitted);

    private bool IsGurfurlurBitingWindProjectionAvoidActive() =>
        IsGurfurlurCombat() &&
        GetActiveCaster(EnemyAction.WindswrathLong) == null &&
        !IsGurfurlurLongWindswrathImpactHoldActive();

    private bool IsGurfurlurLongWindswrathImpactHoldActive() =>
        gurfurlurLongWindswrathActive &&
        gurfurlurWindswrathDestinationLatched &&
        DateTime.UtcNow <= gurfurlurWindswrathHoldUntilUtc;

    private static bool IsGurfurlurAllfireAvoidActive() =>
        IsGurfurlurCombat() && GetActiveCaster(EnemyAction.GreatFlood) == null;

    private static bool IsActiveGurfurlurAllfireCaster(BattleCharacter candidate)
    {
        if (!IsCastingAction(candidate, EnemyAction.Allfire1, EnemyAction.Allfire2, EnemyAction.Allfire3))
        {
            return false;
        }

        double earliestRemainingMilliseconds = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => IsCastingAction(actor, EnemyAction.Allfire1, EnemyAction.Allfire2, EnemyAction.Allfire3))
            .Min(actor => actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds);

        return candidate.SpellCastInfo.RemainingCastTime.TotalMilliseconds <=
               earliestRemainingMilliseconds + GurfurlurAllfireWaveToleranceMilliseconds;
    }

    private static Vector3 GetGurfurlurTileImpactLocation(BattleCharacter actor)
    {
        if (actor != null && actor.IsValid && actor.SpellCastInfo.IsValid &&
            IsInsideGurfurlurArena(actor.SpellCastInfo.CastLocation))
        {
            return actor.SpellCastInfo.CastLocation;
        }

        return actor?.Location ?? ArenaCenter.Gurfurlur;
    }

    private static bool IsGurfurlurBitingWind(BattleCharacter actor) =>
        actor != null && actor.IsValid && actor.IsVisible && actor.IsAlive && actor.NpcId == EnemyNpc.BitingWind;

    private BattleCharacter GetGurfurlurAuraSphere()
    {
        List<BattleCharacter> spheres = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.AuraSphere)
            .Where(actor => actor.IsValid && actor.IsVisible && actor.IsAlive && actor.CurrentHealth > 0)
            .ToList();

        BattleCharacter latched = spheres.FirstOrDefault(actor => actor.ObjectId == gurfurlurAuraSphereId);
        if (latched != null)
        {
            return latched;
        }

        BattleCharacter selected = spheres
            .OrderBy(actor => actor.Location.Distance2D(ArenaCenter.Gurfurlur))
            .ThenBy(actor => actor.Distance2D())
            .FirstOrDefault();

        if (selected != null)
        {
            gurfurlurAuraSphereId = selected.ObjectId;
        }

        return selected;
    }

    private async Task<bool> HandleGurfurlurAuraSphere(BattleCharacter sphere)
    {
        Vector3 direction = DirectionFromHeading(sphere.Heading);
        Vector3 destination = ClampToGurfurlurArena(sphere.Location + direction * GurfurlurAuraSphereInterceptOffset);
        return await MoveToGurfurlurPosition(
            gurfurlurAuraSphereMovement,
            destination,
            GurfurlurMovementArrivalDistance,
            TimeSpan.FromMilliseconds(1_000),
            $"Intercepting Aura Sphere 0x{sphere.ObjectId:X8}");
    }

    private async Task<bool> HandleGurfurlurGreatFlood(BattleCharacter caster)
    {
        List<BattleCharacter> firstAllfires = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => IsCastingAction(actor, EnemyAction.Allfire1))
            .ToList();

        // Helper cast starts can become visible over adjacent RB frames. Recompute as the first
        // wave fills in instead of permanently latching a one-helper centroid from the first frame.
        if (gurfurlurGreatFloodCasterId != caster.ObjectId ||
            firstAllfires.Count > gurfurlurGreatFloodAllfireCount)
        {
            // Hidden-helper heading provides Great Flood's knockback direction.
            Vector3 direction = DirectionFromHeading(caster.Heading);
            Vector3 landingCenter;
            float stagingOffset;

            if (firstAllfires.Count > 0)
            {
                landingCenter = new Vector3(
                    firstAllfires.Average(actor => actor.Location.X),
                    ArenaCenter.Gurfurlur.Y,
                    firstAllfires.Average(actor => actor.Location.Z));
                stagingOffset = GurfurlurGreatFloodDistance;
            }
            else
            {
                // The no-Allfire fallback keeps the player against the wall opposite the
                // knockback. Seventeen yalms preserves a wall inset and lands eight yalms past center.
                landingCenter = ArenaCenter.Gurfurlur;
                stagingOffset = GurfurlurGreatFloodFallbackOffset;
            }

            gurfurlurGreatFloodDestination = ClampToGurfurlurArena(landingCenter - direction * stagingOffset);
            Vector3 predictedLanding = gurfurlurGreatFloodDestination + direction * GurfurlurGreatFloodDistance;
            gurfurlurGreatFloodCasterId = caster.ObjectId;
            gurfurlurGreatFloodAllfireCount = firstAllfires.Count;
        }

        return await MoveToGurfurlurPosition(
            gurfurlurGreatFloodMovement,
            gurfurlurGreatFloodDestination,
            GurfurlurMovementArrivalDistance,
            caster.SpellCastInfo.RemainingCastTime,
            $"Staging for Great Flood helper 0x{caster.ObjectId:X8}",
            yieldToAvoidance: false);
    }

    private async Task<bool> HandleGurfurlurVolcanicDrop(BattleCharacter caster)
    {
        DateTime now = DateTime.UtcNow;
        if (caster != null)
        {
            gurfurlurVolcanicDropHoldUntilUtc =
                now + caster.SpellCastInfo.RemainingCastTime + GurfurlurSpreadImpactGrace;
            gurfurlurVolcanicDropLastOtherTargets = GetOtherTargetLocations(EnemyAction.VolcanicDropAoe);
        }

        Vector3[] otherTargets = caster != null
            ? gurfurlurVolcanicDropLastOtherTargets
            : GetOtherVisiblePartyLocations();

        bool shouldRecheck = now >= gurfurlurVolcanicDropNextRecheckUtc;
        bool destinationStillSafe = gurfurlurVolcanicDropDestinationLatched;
        if (gurfurlurVolcanicDropDestinationLatched && shouldRecheck)
        {
            destinationStillSafe = IsStableSpreadDestinationSafe(
                gurfurlurVolcanicDropDestination,
                ArenaCenter.Gurfurlur,
                GurfurlurArenaMovementHalfWidth,
                circularArena: false,
                otherTargets,
                GurfurlurSpreadRetentionDistance,
                IsGurfurlurSpreadRouteSafe);
        }

        if (shouldRecheck)
        {
            gurfurlurVolcanicDropNextRecheckUtc = now + GurfurlurDestinationRecheckInterval;
        }

        if (!destinationStillSafe && shouldRecheck)
        {
            if (!TryFindStableSpreadDestination(
                    Core.Player.Location,
                    ArenaCenter.Gurfurlur,
                    GurfurlurArenaMovementHalfWidth,
                    GurfurlurSpreadCandidateStep,
                    circularArena: false,
                    otherTargets,
                    GurfurlurSpreadAcquisitionDistance,
                    IsGurfurlurSpreadRouteSafe,
                    out Vector3 destination))
            {
                ReleaseGurfurlurMovement(
                    gurfurlurVolcanicDropMovement,
                    "Volcanic Drop planner found no valid point");
                gurfurlurVolcanicDropDestinationLatched = false;
                return false;
            }

            gurfurlurVolcanicDropDestination = destination;
            gurfurlurVolcanicDropDestinationLatched = true;
        }

        if (!gurfurlurVolcanicDropDestinationLatched)
        {
            return false;
        }

        return await MoveToGurfurlurPosition(
            gurfurlurVolcanicDropMovement,
            gurfurlurVolcanicDropDestination,
            GurfurlurMovementArrivalDistance,
            gurfurlurVolcanicDropHoldUntilUtc - now,
            "Holding Gurfurlur's planned Volcanic Drop spread");
    }

    private static bool IsGurfurlurSpreadRouteSafe(Vector3 destination)
    {
        Vector3 start = Core.Player.Location;
        float routeDistance = start.Distance2D(destination);
        int sampleCount = Math.Max(1, (int)Math.Ceiling(routeDistance));
        for (int sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
        {
            float progress = sampleIndex / (float)sampleCount;
            Vector3 sample = new(
                start.X + (destination.X - start.X) * progress,
                ArenaCenter.Gurfurlur.Y,
                start.Z + (destination.Z - start.Z) * progress);
            if (!IsOutsideActiveAvoids(sample))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> HandleGurfurlurWindswrath(
        BattleCharacter caster,
        IReadOnlyCollection<BattleCharacter> bitingWinds)
    {
        if (caster.CastingSpellId == EnemyAction.WindswrathShort)
        {
            gurfurlurLongWindswrathActive = false;
            gurfurlurWindswrathHoldUntilUtc = DateTime.MinValue;
            gurfurlurWindswrathDestinationReached = false;
            gurfurlurWindswrathRouteCommitted = false;
            gurfurlurWindswrathPattern = GurfurlurWindswrathPattern.None;
            ReleaseGurfurlurBitingWindForecast("Short Windswrath center staging has priority");

            if (gurfurlurWindswrathCasterId != caster.ObjectId)
            {
                gurfurlurWindswrathCasterId = caster.ObjectId;
                gurfurlurWindswrathDestination = ClampToGurfurlurArena(caster.Location);
                gurfurlurWindswrathDestinationLatched = true;
            }

            return await MoveToGurfurlurPosition(
                gurfurlurWindswrathMovement,
                gurfurlurWindswrathDestination,
                GurfurlurWindswrathShortArrivalDistance,
                caster.SpellCastInfo.RemainingCastTime,
                $"Staging for short Windswrath helper 0x{caster.ObjectId:X8}");
        }

        float remainingSeconds = Math.Max(0f, (float)caster.SpellCastInfo.RemainingCastTime.TotalSeconds);
        gurfurlurLongWindswrathActive = true;
        gurfurlurWindswrathHoldUntilUtc =
            DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime + GurfurlurWindswrathImpactGrace;
        ReleaseGurfurlurBitingWindForecast("Long Windswrath pattern staging has priority");

        if (gurfurlurWindswrathCasterId != caster.ObjectId)
        {
            gurfurlurWindswrathCasterId = caster.ObjectId;
            gurfurlurWindswrathDestinationLatched = false;
            gurfurlurWindswrathDestinationReached = false;
            gurfurlurWindswrathRouteCommitted = false;
            gurfurlurWindswrathPattern = GurfurlurWindswrathPattern.None;
        }

        if (!gurfurlurWindswrathRouteCommitted)
        {
            GurfurlurWindswrathPattern observedPattern =
                DetectGurfurlurLongWindswrathPattern(bitingWinds);
            if (observedPattern != GurfurlurWindswrathPattern.None &&
                observedPattern != gurfurlurWindswrathPattern)
            {
                gurfurlurWindswrathPattern = observedPattern;
            }
        }

        if (remainingSeconds > GurfurlurLongWindswrathFinalWindowSeconds)
        {
            gurfurlurWindswrathDestinationLatched = false;
            gurfurlurWindswrathDestinationReached = false;
            gurfurlurWindswrathRouteCommitted = false;
            gurfurlurWindswrathDestination =
                GetGurfurlurLongWindswrathEarlyDestination(caster.Location);

            return await MoveToGurfurlurPosition(
                gurfurlurWindswrathMovement,
                gurfurlurWindswrathDestination,
                GurfurlurLongWindswrathFinalArrivalDistance,
                caster.SpellCastInfo.RemainingCastTime,
                $"Holding inside Long Windswrath's eight-yalm staging circle for helper 0x{caster.ObjectId:X8}");
        }

        if (!gurfurlurWindswrathDestinationLatched)
        {
            GurfurlurWindswrathPattern selectionPattern = gurfurlurWindswrathPattern;
            if (selectionPattern == GurfurlurWindswrathPattern.None)
            {
                // Use EWEW when the alternating-row pattern was not observed in time.
                selectionPattern = GurfurlurWindswrathPattern.Ewew;
            }

            if (!TrySelectGurfurlurLongWindswrathWedge(
                    caster.Location,
                    selectionPattern,
                    bitingWinds,
                    out Vector3 wedge))
            {
                // Fall back to the first authored in-bounds wedge.
                float fallbackHeading = selectionPattern == GurfurlurWindswrathPattern.Wewe
                    ? DegreesToRadians(GurfurlurLongWindswrathWedgeHalfAngleDegrees)
                    : DegreesToRadians(-GurfurlurLongWindswrathWedgeHalfAngleDegrees);
                Vector3 fallbackDirection = DirectionFromHeading(fallbackHeading);
                wedge = caster.Location + fallbackDirection * GurfurlurLongWindswrathFinalTargetRadius;
            }

            gurfurlurWindswrathPattern = selectionPattern;
            gurfurlurWindswrathDestination = wedge;
            gurfurlurWindswrathDestinationLatched = true;
            gurfurlurWindswrathDestinationReached = false;
            gurfurlurWindswrathRouteCommitted = true;
        }

        if (Core.Player.Distance2D(gurfurlurWindswrathDestination) <=
            GurfurlurLongWindswrathFinalArrivalDistance)
        {
            gurfurlurWindswrathDestinationReached = true;
        }

        return await MoveToGurfurlurPosition(
            gurfurlurWindswrathMovement,
            gurfurlurWindswrathDestination,
            GurfurlurLongWindswrathFinalArrivalDistance,
            caster.SpellCastInfo.RemainingCastTime,
            $"Holding Long Windswrath's pattern-specific knockback wedge for helper 0x{caster.ObjectId:X8}",
            yieldToAvoidance: false);
    }

    private static GurfurlurWindswrathPattern DetectGurfurlurLongWindswrathPattern(
        IEnumerable<BattleCharacter> bitingWinds)
    {
        BattleCharacter[] orderedWinds = bitingWinds.OrderBy(wind => wind.ObjectId).ToArray();
        if (orderedWinds.Length < GurfurlurLongWindswrathExpectedTornadoCount)
        {
            return GurfurlurWindswrathPattern.None;
        }

        GurfurlurWindswrathPattern pattern = GurfurlurWindswrathPattern.None;
        float northPatternRow = ArenaCenter.Gurfurlur.Z + 15f;
        float southPatternRow = ArenaCenter.Gurfurlur.Z - 15f;

        foreach (BattleCharacter wind in orderedWinds)
        {
            if (Math.Abs(wind.Location.Z - northPatternRow) <=
                GurfurlurLongWindswrathPatternRowTolerance)
            {
                pattern = GurfurlurWindswrathPattern.Ewew;
            }
            else if (Math.Abs(wind.Location.Z - southPatternRow) <=
                     GurfurlurLongWindswrathPatternRowTolerance)
            {
                pattern = GurfurlurWindswrathPattern.Wewe;
            }
        }

        return pattern;
    }

    private static Vector3 GetGurfurlurLongWindswrathEarlyDestination(Vector3 source)
    {
        Vector3 current = Core.Player.Location;
        float deltaX = current.X - source.X;
        float deltaZ = current.Z - source.Z;
        double distance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        if (distance <= GurfurlurLongWindswrathEarlyRadius || distance < 0.001d)
        {
            return current;
        }

        return new Vector3(
            source.X + deltaX / (float)distance * GurfurlurLongWindswrathEarlyTargetRadius,
            ArenaCenter.Gurfurlur.Y,
            source.Z + deltaZ / (float)distance * GurfurlurLongWindswrathEarlyTargetRadius);
    }

    private static bool TrySelectGurfurlurLongWindswrathWedge(
        Vector3 source,
        GurfurlurWindswrathPattern pattern,
        IReadOnlyCollection<BattleCharacter> bitingWinds,
        out Vector3 destination)
    {
        destination = default;
        bool foundSafeDestination = false;
        double bestSafeScore = double.MaxValue;
        double bestFallbackScore = double.MinValue;
        Vector3 fallbackDestination = default;
        bool foundStructuralCandidate = false;
        float baseDegrees = pattern == GurfurlurWindswrathPattern.Wewe
            ? GurfurlurLongWindswrathWedgeHalfAngleDegrees
            : -GurfurlurLongWindswrathWedgeHalfAngleDegrees;

        for (int wedgeIndex = 0; wedgeIndex < 4; wedgeIndex++)
        {
            float heading = DegreesToRadians(baseDegrees + wedgeIndex * 90f);
            Vector3 direction = DirectionFromHeading(heading);
            Vector3 candidate =
                source + direction * GurfurlurLongWindswrathFinalTargetRadius;
            Vector3 candidateLanding = PredictRadialKnockbackLanding(
                candidate,
                source,
                GurfurlurWindswrathDistance);
            if (candidate.Distance2D(source) > GurfurlurLongWindswrathFinalRadius ||
                !IsInsideGurfurlurArena(candidate) ||
                !IsInsideGurfurlurArena(candidateLanding))
            {
                continue;
            }

            foundStructuralCandidate = true;
            float clearance = GetGurfurlurLongWindswrathWedgeClearance(
                candidate,
                candidateLanding,
                bitingWinds);
            bool outsideCurrentAvoids =
                !AvoidanceManager.Avoids.Any(avoid =>
                    avoid.IsPointInAvoid(candidate) || avoid.IsPointInAvoid(candidateLanding));
            bool safe = outsideCurrentAvoids && clearance >= 0f;
            double travel = Core.Player.Distance2D(candidate);

            if (safe && travel < bestSafeScore)
            {
                bestSafeScore = travel;
                destination = candidate;
                foundSafeDestination = true;
            }

            double fallbackScore = clearance * 10d - travel;
            if (fallbackScore > bestFallbackScore)
            {
                bestFallbackScore = fallbackScore;
                fallbackDestination = candidate;
            }
        }

        if (foundSafeDestination)
        {
            return true;
        }

        if (foundStructuralCandidate)
        {
            destination = fallbackDestination;
        }

        return foundStructuralCandidate;
    }

    private static float GetGurfurlurLongWindswrathWedgeClearance(
        Vector3 destination,
        Vector3 landing,
        IEnumerable<BattleCharacter> bitingWinds)
    {
        float wallClearance = Math.Min(
            GurfurlurArenaMovementHalfWidth -
            Math.Max(
                Math.Abs(destination.X - ArenaCenter.Gurfurlur.X),
                Math.Abs(destination.Z - ArenaCenter.Gurfurlur.Z)),
            GurfurlurArenaMovementHalfWidth -
            Math.Max(
                Math.Abs(landing.X - ArenaCenter.Gurfurlur.X),
                Math.Abs(landing.Z - ArenaCenter.Gurfurlur.Z)));
        float tornadoClearance = float.PositiveInfinity;

        foreach (BattleCharacter wind in bitingWinds)
        {
            tornadoClearance = Math.Min(
                tornadoClearance,
                Math.Min(
                    destination.Distance2D(wind.Location),
                    landing.Distance2D(wind.Location)) -
                GurfurlurBitingWindAvoidRadius);
        }

        return Math.Min(wallClearance, tornadoClearance);
    }

    private static float DegreesToRadians(float degrees) =>
        degrees * (float)Math.PI / 180f;

    private async Task<bool> HandleGurfurlurBitingWindForecast(
        IReadOnlyCollection<BattleCharacter> bitingWinds,
        TimeSpan remainingLifetime)
    {
        if (bitingWinds.Count == 0)
        {
            ReleaseGurfurlurBitingWindForecast("No live Biting Wind remains");
            return false;
        }

        bool playerIsForecastThreatened = bitingWinds.Any(wind =>
            IsPointInBitingWindForecast(Core.Player.Location, wind));
        bool destinationStillSafe =
            gurfurlurBitingWindDestinationLatched &&
            IsGurfurlurBitingWindForecastCandidateSafe(gurfurlurBitingWindDestination, bitingWinds);

        if (destinationStillSafe &&
            !float.IsPositiveInfinity(GetEarliestBitingWindArrivalSeconds(gurfurlurBitingWindDestination, bitingWinds)) &&
            DateTime.UtcNow >= gurfurlurBitingWindNextClearPocketProbeUtc)
        {
            gurfurlurBitingWindNextClearPocketProbeUtc = DateTime.UtcNow + GurfurlurBitingWindClearPocketProbe;

            if (TryFindGurfurlurBitingWindForecastDestination(
                    bitingWinds,
                    out Vector3 promotedDestination,
                    out float promotedArrivalSeconds) &&
                float.IsPositiveInfinity(promotedArrivalSeconds))
            {
                gurfurlurBitingWindDestination = promotedDestination;
            }
        }

        if (!destinationStillSafe)
        {
            if (!playerIsForecastThreatened)
            {
                ReleaseGurfurlurBitingWindForecast("Player is outside every projected tornado path");
                return false;
            }

            if (!TryFindGurfurlurBitingWindForecastDestination(
                    bitingWinds,
                    out gurfurlurBitingWindDestination,
                    out _))
            {
                ReleaseGurfurlurMovement(
                    gurfurlurBitingWindMovement,
                    "No forecast pocket is clear of the current encounter geometry");
                gurfurlurBitingWindDestinationLatched = false;

                return false;
            }

            gurfurlurBitingWindDestinationLatched = true;
            gurfurlurBitingWindNextClearPocketProbeUtc = DateTime.UtcNow + GurfurlurBitingWindClearPocketProbe;
        }

        return await MoveToGurfurlurPosition(
            gurfurlurBitingWindMovement,
            gurfurlurBitingWindDestination,
            GurfurlurMovementArrivalDistance,
            remainingLifetime,
            $"Pre-positioning ahead of {bitingWinds.Count} Biting Wind paths");
    }

    private bool TryFindGurfurlurBitingWindForecastDestination(
        IReadOnlyCollection<BattleCharacter> bitingWinds,
        out Vector3 destination,
        out float earliestArrivalSeconds)
    {
        destination = default;
        earliestArrivalSeconds = 0f;
        Vector3 bestTimedDestination = default;
        float bestTimedArrivalSeconds = 0f;
        double bestClearScore = double.MinValue;
        double bestTimedScore = double.MinValue;
        for (float xOffset = -GurfurlurArenaMovementHalfWidth;
             xOffset <= GurfurlurArenaMovementHalfWidth;
             xOffset += GurfurlurBitingWindForecastGridStep)
        {
            for (float zOffset = -GurfurlurArenaMovementHalfWidth;
                 zOffset <= GurfurlurArenaMovementHalfWidth;
                 zOffset += GurfurlurBitingWindForecastGridStep)
            {
                Vector3 candidate = new(
                    ArenaCenter.Gurfurlur.X + xOffset,
                    ArenaCenter.Gurfurlur.Y,
                    ArenaCenter.Gurfurlur.Z + zOffset);
                if (!IsInsideGurfurlurArena(candidate))
                {
                    continue;
                }

                if (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(candidate)))
                {
                    continue;
                }

                if (!HasEnoughBitingWindForecastLead(candidate, bitingWinds))
                {
                    continue;
                }

                float candidateArrivalSeconds = GetEarliestBitingWindArrivalSeconds(candidate, bitingWinds);
                float cappedArrivalSeconds = float.IsPositiveInfinity(candidateArrivalSeconds)
                    ? GurfurlurBitingWindForecastLength / GurfurlurBitingWindSpeed
                    : candidateArrivalSeconds;
                float edgeClearance = GurfurlurArenaMovementHalfWidth - Math.Max(Math.Abs(xOffset), Math.Abs(zOffset));
                double travelDistance = Core.Player.Distance2D(candidate);

                if (float.IsPositiveInfinity(candidateArrivalSeconds))
                {
                    // A trajectory-free point outranks every timed pocket.
                    double clearScore = edgeClearance * 4d - travelDistance;
                    if (clearScore > bestClearScore)
                    {
                        bestClearScore = clearScore;
                        destination = candidate;
                    }
                }
                else
                {
                    // When every sampled point is eventually crossed, time until impact dominates;
                    // edge and travel costs only break ties between similarly durable pockets.
                    double timedScore = cappedArrivalSeconds * 12d + edgeClearance * 4d - travelDistance;
                    if (timedScore > bestTimedScore)
                    {
                        bestTimedScore = timedScore;
                        bestTimedDestination = candidate;
                        bestTimedArrivalSeconds = candidateArrivalSeconds;
                    }
                }
            }
        }

        if (bestClearScore > double.MinValue)
        {
            earliestArrivalSeconds = float.PositiveInfinity;
            return true;
        }

        if (bestTimedScore > double.MinValue)
        {
            destination = bestTimedDestination;
            earliestArrivalSeconds = bestTimedArrivalSeconds;
            return true;
        }

        return false;
    }

    private static bool IsGurfurlurBitingWindForecastCandidateSafe(
        Vector3 candidate,
        IEnumerable<BattleCharacter> bitingWinds) =>
        IsInsideGurfurlurArena(candidate) &&
        !AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(candidate)) &&
        HasEnoughBitingWindForecastLead(candidate, bitingWinds);

    private static bool HasEnoughBitingWindForecastLead(
        Vector3 destination,
        IEnumerable<BattleCharacter> bitingWinds)
    {
        Vector3 start = Core.Player.Location;
        float routeDistance = start.Distance2D(destination);
        int sampleCount = Math.Max(1, (int)Math.Ceiling(
            routeDistance / GurfurlurBitingWindForecastPathSampleStep));

        for (int sampleIndex = 1; sampleIndex <= sampleCount; sampleIndex++)
        {
            float progress = sampleIndex / (float)sampleCount;
            Vector3 sample = new(
                start.X + (destination.X - start.X) * progress,
                ArenaCenter.Gurfurlur.Y,
                start.Z + (destination.Z - start.Z) * progress);
            float tornadoArrivalSeconds = GetEarliestBitingWindArrivalSeconds(sample, bitingWinds);
            float playerArrivalSeconds = routeDistance * progress / GurfurlurPlayerRunSpeed;

            if (!float.IsPositiveInfinity(tornadoArrivalSeconds) &&
                tornadoArrivalSeconds < playerArrivalSeconds + GurfurlurBitingWindReplanLeadSeconds)
            {
                return false;
            }
        }

        return true;
    }

    private static float GetEarliestBitingWindArrivalSeconds(
        Vector3 point,
        IEnumerable<BattleCharacter> bitingWinds)
    {
        float earliestSeconds = float.PositiveInfinity;

        foreach (BattleCharacter wind in bitingWinds)
        {
            Vector3 direction = DirectionFromHeading(wind.Heading);
            float deltaX = point.X - wind.Location.X;
            float deltaZ = point.Z - wind.Location.Z;
            float forwardDistance = deltaX * direction.X + deltaZ * direction.Z;

            if (forwardDistance < 0f || forwardDistance > GurfurlurBitingWindForecastLength)
            {
                continue;
            }

            float lateralDistance = Math.Abs(deltaX * direction.Z - deltaZ * direction.X);
            if (lateralDistance > GurfurlurBitingWindForecastClearance)
            {
                continue;
            }

            earliestSeconds = Math.Min(earliestSeconds, forwardDistance / GurfurlurBitingWindSpeed);
        }

        return earliestSeconds;
    }

    private static bool IsPointInBitingWindForecast(Vector3 point, BattleCharacter wind)
    {
        return !float.IsPositiveInfinity(GetEarliestBitingWindArrivalSeconds(point, [wind]));
    }

    private async Task<bool> HandleGurfurlurSledgehammer(BattleCharacter caster)
    {
        if (caster != null && gurfurlurSledgehammerCasterId != caster.ObjectId)
        {
            gurfurlurSledgehammerCasterId = caster.ObjectId;
            gurfurlurSledgehammerTargetId = caster.SpellCastInfo.TargetId;
            gurfurlurSledgehammerFallbackDestination = Core.Player.Location;
        }
        else if (caster != null && gurfurlurSledgehammerTargetId == 0 && caster.SpellCastInfo.TargetId != 0)
        {
            gurfurlurSledgehammerTargetId = caster.SpellCastInfo.TargetId;
        }

        if (caster != null)
        {
            DateTime observedSequenceEnd = DateTime.UtcNow
                .Add(caster.SpellCastInfo.RemainingCastTime)
                .Add(GurfurlurSledgehammerFollowupGrace);
            if (observedSequenceEnd > gurfurlurSledgehammerHoldUntilUtc)
            {
                gurfurlurSledgehammerHoldUntilUtc = observedSequenceEnd;
            }
        }

        GameObject target = gurfurlurSledgehammerTargetId == 0
            ? null
            : GameObjectManager.GetObjectByObjectId(gurfurlurSledgehammerTargetId);
        bool playerIsTarget = gurfurlurSledgehammerTargetId == Core.Player.ObjectId;
        Vector3 destination = !playerIsTarget && target != null && target.IsValid
            ? target.Location
            : gurfurlurSledgehammerFallbackDestination;
        TimeSpan remainingSequence = gurfurlurSledgehammerHoldUntilUtc - DateTime.UtcNow;
        if (remainingSequence < TimeSpan.FromMilliseconds(100))
        {
            remainingSequence = TimeSpan.FromMilliseconds(100);
        }

        return await MoveToGurfurlurPosition(
            gurfurlurSledgehammerMovement,
            ClampToGurfurlurArena(destination),
            playerIsTarget || target == null ? 0.25f : GurfurlurSledgehammerStackDistance,
            remainingSequence,
            playerIsTarget
                ? "Holding as Sledgehammer's selected target so duty-support allies can share the line"
                : $"Joining Sledgehammer target 0x{gurfurlurSledgehammerTargetId:X8}");
    }

    private bool IsGurfurlurSledgehammerFollowupActive() =>
        gurfurlurSledgehammerCasterId != 0 && DateTime.UtcNow < gurfurlurSledgehammerHoldUntilUtc;

    private static async Task<bool> MoveToGurfurlurPosition(
        GurfurlurMovementLease lease,
        Vector3 destination,
        float arrivalDistance,
        TimeSpan remainingLifetime,
        string reason,
        bool yieldToAvoidance = true)
    {
        int leaseMilliseconds = Math.Max(750, (int)Math.Ceiling(remainingLifetime.TotalMilliseconds) + 250);
        CapabilityManager.Update(lease.Handle, CapabilityFlags.Movement, leaseMilliseconds, reason);
        lease.Owned = true;

        if (yieldToAvoidance && AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(destination) <= arrivalDistance)
        {
            Navigator.PlayerMover.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    private static Vector3 PredictRadialKnockbackLanding(Vector3 start, Vector3 source, float distance)
    {
        float deltaX = start.X - source.X;
        float deltaZ = start.Z - source.Z;
        double length = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        if (length < 0.001d)
        {
            return start;
        }

        return new Vector3(
            start.X + deltaX / (float)length * distance,
            ArenaCenter.Gurfurlur.Y,
            start.Z + deltaZ / (float)length * distance);
    }

    private static Vector3 DirectionFromHeading(float heading) =>
        new((float)Math.Sin(heading), 0f, (float)Math.Cos(heading));

    private static Vector3 ClampToGurfurlurArena(Vector3 location) =>
        new(
            Math.Max(ArenaCenter.Gurfurlur.X - GurfurlurArenaMovementHalfWidth,
                Math.Min(ArenaCenter.Gurfurlur.X + GurfurlurArenaMovementHalfWidth, location.X)),
            ArenaCenter.Gurfurlur.Y,
            Math.Max(ArenaCenter.Gurfurlur.Z - GurfurlurArenaMovementHalfWidth,
                Math.Min(ArenaCenter.Gurfurlur.Z + GurfurlurArenaMovementHalfWidth, location.Z)));

    private static bool IsInsideGurfurlurArena(Vector3 location) =>
        Math.Abs(location.X - ArenaCenter.Gurfurlur.X) <= GurfurlurArenaMovementHalfWidth &&
        Math.Abs(location.Z - ArenaCenter.Gurfurlur.Z) <= GurfurlurArenaMovementHalfWidth;

    private void ReleaseInactiveGurfurlurMechanics(
        BattleCharacter sphere,
        BattleCharacter greatFlood,
        BattleCharacter windswrath,
        BattleCharacter sledgehammer)
    {
        if (sphere == null)
        {
            ReleaseGurfurlurMovement(gurfurlurAuraSphereMovement, "Aura Sphere was consumed or disappeared");
            gurfurlurAuraSphereId = 0;
        }

        if (greatFlood == null)
        {
            ReleaseGurfurlurMovement(gurfurlurGreatFloodMovement, "Great Flood cast ended");
            gurfurlurGreatFloodCasterId = 0;
            gurfurlurGreatFloodAllfireCount = 0;
        }

        if (windswrath == null && !IsGurfurlurLongWindswrathImpactHoldActive())
        {
            ReleaseGurfurlurMovement(gurfurlurWindswrathMovement, "Windswrath cast ended");
            gurfurlurWindswrathCasterId = 0;
            gurfurlurWindswrathDestinationLatched = false;
            gurfurlurWindswrathHoldUntilUtc = DateTime.MinValue;
            gurfurlurLongWindswrathActive = false;
            gurfurlurWindswrathDestinationReached = false;
            gurfurlurWindswrathRouteCommitted = false;
            gurfurlurWindswrathPattern = GurfurlurWindswrathPattern.None;
        }

        if (sledgehammer == null && !IsGurfurlurSledgehammerFollowupActive())
        {
            ReleaseGurfurlurMovement(gurfurlurSledgehammerMovement, "Sledgehammer sequence ended");
            gurfurlurSledgehammerCasterId = 0;
            gurfurlurSledgehammerTargetId = 0;
            gurfurlurSledgehammerHoldUntilUtc = DateTime.MinValue;
        }
    }

    private void ResetGurfurlurState(string reason)
    {
        ReleaseGurfurlurMovement(gurfurlurAuraSphereMovement, reason);
        ReleaseGurfurlurMovement(gurfurlurGreatFloodMovement, reason);
        ReleaseGurfurlurVolcanicDrop(reason);
        ReleaseGurfurlurMovement(gurfurlurWindswrathMovement, reason);
        ReleaseGurfurlurMovement(gurfurlurSledgehammerMovement, reason);
        ReleaseGurfurlurBitingWindForecast(reason);
        gurfurlurAuraSphereId = 0;
        gurfurlurGreatFloodCasterId = 0;
        gurfurlurGreatFloodAllfireCount = 0;
        gurfurlurWindswrathCasterId = 0;
        gurfurlurWindswrathDestinationLatched = false;
        gurfurlurWindswrathHoldUntilUtc = DateTime.MinValue;
        gurfurlurLongWindswrathActive = false;
        gurfurlurWindswrathDestinationReached = false;
        gurfurlurWindswrathRouteCommitted = false;
        gurfurlurWindswrathPattern = GurfurlurWindswrathPattern.None;
        gurfurlurSledgehammerCasterId = 0;
        gurfurlurSledgehammerTargetId = 0;
        gurfurlurSledgehammerHoldUntilUtc = DateTime.MinValue;
    }

    private void ReleaseGurfurlurVolcanicDrop(string reason)
    {
        ReleaseGurfurlurMovement(gurfurlurVolcanicDropMovement, reason);
        gurfurlurVolcanicDropDestinationLatched = false;
        gurfurlurVolcanicDropLastOtherTargets = [];
        gurfurlurVolcanicDropHoldUntilUtc = DateTime.MinValue;
        gurfurlurVolcanicDropNextRecheckUtc = DateTime.MinValue;
    }

    private void ReleaseGurfurlurBitingWindForecast(string reason)
    {
        ReleaseGurfurlurMovement(gurfurlurBitingWindMovement, reason);
        gurfurlurBitingWindDestinationLatched = false;
        gurfurlurBitingWindNextClearPocketProbeUtc = DateTime.MinValue;
    }

    private static void ReleaseGurfurlurMovement(GurfurlurMovementLease lease, string reason)
    {
        if (!lease.Owned)
        {
            return;
        }

        CapabilityManager.Clear(lease.Handle, CapabilityFlags.Movement, reason);
        lease.Owned = false;
    }

    private sealed class RyoqorSnowBoulderLane
    {
        public RyoqorSnowBoulderLane(Vector3 location, float heading)
        {
            Location = location;
            Heading = heading;
        }

        public Vector3 Location { get; }

        public float Heading { get; }
    }

    private enum RyoqorFluffleShape
    {
        IceScream,

        FrozenSwirl,
    }

    private sealed class RyoqorFluffleAoe
    {
        public RyoqorFluffleAoe(
            RyoqorFluffleShape shape,
            Vector3 location,
            float heading,
            DateTime activationUtc,
            TimeSpan remainingCastTime)
        {
            Shape = shape;
            Location = location;
            Heading = heading;
            ActivationUtc = activationUtc;
            LastObservedRemainingCastTime = remainingCastTime;
        }

        public RyoqorFluffleShape Shape { get; }

        public Vector3 Location { get; }

        public float Heading { get; }

        public DateTime ActivationUtc { get; set; }

        public bool Frozen { get; set; }

        public TimeSpan LastObservedRemainingCastTime { get; set; }
    }

    // Client IDs for encounter actors.
    private static class EnemyNpc
    {
        public const uint RyoqorTerteh = 12699;

        public const uint QorrlohTeh = 12700;

        public const uint RorrlohTeh = 12701;

        public const uint Snowball = 12702;

        public const uint Kahderyor = 12703;

        public const uint CrystallineDebris = 0x415E;

        public const uint Gurfurlur = 12705;

        public const uint BitingWind = 12706;

        public const uint AuraSphere = 12708;
    }

    // Arena centers for leashes and destination grids.
    private static class ArenaCenter
    {
        public static readonly Vector3 RyoqorTerteh = new(-108f, 11f, 119f);

        public static readonly Vector3 Kahderyor = new(-53f, 323, -57f);

        public static readonly Vector3 Gurfurlur = new(-54f, 378f, -195f);
    }

    // Client action IDs for handled boss and helper casts.
    private static class EnemyAction
    {
        public const uint SparklingSprinklingAoe = 36281;

        public const uint IceScream = 36270;

        public const uint SnowBoulder = 36278;

        public const uint FrozenSwirlVisual = 36271;

        public const uint WindShotAoe = 36296;

        public const uint EarthenShotAoe = 36295;

        public const uint SeedCrystalsAoe = 36298;

        public const uint CrystallineCrushAoe = 36153;

        public const uint CrystallineStormAoe = 36290;

        public const uint CyclonicRingAoe = 36294;

        public const uint EyeOfTheFierce = 36297;

        public const uint StalagmiteCircleAoe = 36293;

        public const uint LithicImpact = 36302;

        public const uint Allfire1 = 36303;

        public const uint Allfire2 = 36304;

        public const uint Allfire3 = 36305;

        public const uint VolcanicDropAoe = 36306;

        public const uint GreatFlood = 36307;

        public const uint WindswrathShort = 36310;

        public const uint WindswrathLong = 39074;

        public const uint Sledgehammer = 36313;
    }

    // Alternating endpoint-row patterns inferred from tornado creation order.
    private enum GurfurlurWindswrathPattern
    {
        None,

        Ewew,

        Wewe,
    }

    // Independent leases prevent one mechanic from releasing another's movement hold.
    private sealed class GurfurlurMovementLease
    {
        public CapabilityManagerHandle Handle { get; } = CapabilityManager.CreateNewHandle();

        public bool Owned { get; set; }
    }

    // Persistent crystal geometry used by both response casts.
    private enum KahderyorCrystalShape
    {
        Circle,

        Rectangle,
    }

    private sealed class KahderyorCrystalSource
    {
        public KahderyorCrystalSource(KahderyorCrystalShape shape, Vector3 location, float heading)
        {
            Shape = shape;
            Location = location;
            Heading = heading;
        }

        public KahderyorCrystalShape Shape { get; }

        public Vector3 Location { get; }

        public float Heading { get; }
    }

    // Client tether ID for Cold Feat's delayed wave.
    private static class TetherId
    {
        public const ushort Freeze = 272;
    }

    // Client status IDs that block Kahderyor movement.
    private static class PlayerAura
    {
        public const uint CrystalBurden = 3810;

        public const uint Crystallized = 3811;
    }
}
