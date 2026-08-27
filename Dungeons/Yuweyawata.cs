using Buddy.Coroutines;
using Clio.Common;
using Clio.Utilities;
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
using GreyMagic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Yuweyawata Field Station dungeon logic.
/// </summary>
/// <remarks>
/// Ordinary geometric casts are owned by registered avoidance shapes. Positive-position mechanics
/// and rotating sequences whose individual activation times cannot be represented by RebornBuddy's
/// location-only avoidance API use one encounter-local planner, preventing concurrent mechanics
/// from issuing competing destinations or keeping the coroutine inside a combat-long follow loop.
/// </remarks>
public class YuweyawataFieldStation : AbstractDungeon
{
    // A cast that resolves within this window is concurrent for movement-priority purposes. Effect
    // order remains primary; lethality is only the tie-breaker for effectively simultaneous hits.
    private static readonly TimeSpan ConcurrentResolutionWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ResolutionGrace = TimeSpan.FromMilliseconds(750);

    // RB exposes cast wrappers but not these repeated no-cast effects. Each window covers the
    // observed choreography plus one scheduler tick.
    private static readonly TimeSpan RagingClawRepeatWindow = TimeSpan.FromSeconds(3.5);
    private static readonly TimeSpan BoulderDanceRepeatWindow = TimeSpan.FromSeconds(3);

    // Leaping Earth's impacts resolve 0.24-0.46 seconds before the visual ends. Forecast them half a
    // second early and retain them through delayed damage/status updates.
    private static readonly TimeSpan LeapingEarthCurveImpactLead = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan LeapingEarthCurvePostVisualPersistence = TimeSpan.FromSeconds(1.25);
    // Action 40661 resolves at quarter-second intervals. Plan the first ten impacts during the
    // visual instead of publishing all twenty as simultaneous RB avoids.
    private static readonly TimeSpan LeapingEarthSpiralInterval = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan LeapingEarthImpactPersistence = TimeSpan.FromSeconds(1.25);
    private static readonly TimeSpan RockBlastInterval = TimeSpan.FromSeconds(0.6);

    // Rebuild local collision one bot pulse after Crater Carve so the missing floor is observable;
    // the persistent crater avoid covers the handoff.
    private static readonly TimeSpan CraterNavigationResetDelay = TimeSpan.FromMilliseconds(250);

    private const int LeapingEarthSpiralForecastCount = 10;
    // A 2.7-second horizon exposes one additional 0.6-second Rock Blast impact. This preserves lead
    // room over the observed 5.94-5.98-yalm hit boundary without closing the narrow safe ring.
    private static readonly TimeSpan RockBlastMovementLead = TimeSpan.FromSeconds(2.7);

    // Phantom Flood's floor persists after its cast wrapper. Preposition only during the final two
    // seconds so earlier Soulweave retains the outer arena; the map-effect latch owns persistence.
    private static readonly TimeSpan PhantomFloodMovementLead = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PhantomFloodPersistenceWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PhantomFloodMapEffectCaptureGrace = TimeSpan.FromSeconds(1);

    // Only Soulweave casts within 1.3 seconds of the earliest queued cast resolve as one risky FIFO
    // prefix. NPC cast wrappers disappear about 0.3 seconds before the observed effect boundary, so
    // finish processing carries that delay; the two-second expiry is only a stale-wrapper fallback.
    private static readonly TimeSpan SoulweaveConcurrentWaveWindow = TimeSpan.FromSeconds(1.3);
    private static readonly TimeSpan SoulweaveNpcCastFinishDelay = TimeSpan.FromSeconds(0.3);
    private static readonly TimeSpan SoulweaveCastFinishFailsafe = TimeSpan.FromSeconds(2);
    // Half a second distinguishes a recycled helper's next cast from scheduler jitter while staying
    // well below the observed 5.4-second reuse cadence.
    private static readonly TimeSpan SoulweaveCastGenerationTolerance = TimeSpan.FromMilliseconds(500);
    // Preserve every Caber Toss rectangle as forecast data, but expose only the 1.5-second concurrent
    // cohort. The short grace covers bot latency without merging the next wall.
    private static readonly TimeSpan LineVoltageConcurrentWaveWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LineVoltagePostActivationGrace = TimeSpan.FromMilliseconds(750);
    // The post-Cell Shock lanes arrive in two subwaves about 1.26 seconds apart. Only casts within
    // half a second of the first lane can constrain Cell Shock's preposition point; folding the later
    // subwave into that solve removes the real shared crescent before its own movement stage is due.
    private static readonly TimeSpan CellShockLineVoltagePlanningWindow = TimeSpan.FromMilliseconds(500);

    // RB omits map-effect packet indices. Scope record-state capture to Caber Toss and its Cell Shock
    // handoff so stable record IDs can identify the four quadrants.
    private static readonly TimeSpan CaberTossMapEffectCaptureGrace = TimeSpan.FromSeconds(3);

    // The selector appears about eight seconds before Cell Shock; twelve seconds covers the complete
    // handoff while bounding stale geometry.
    private static readonly TimeSpan CellShockWarningLead = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CellShockForecastLifetime = TimeSpan.FromSeconds(12);

    private const int MinimumMovementLeaseMilliseconds = 250;
    private const float MovementArrivalTolerance = 1f;
    // A quarter-yalm arrival tolerance preserves half of Soulweave's endpoint safety margin.
    private const float SoulweaveArrivalTolerance = 0.25f;
    // Sequential five-yalm impacts also need quarter-yalm arrival precision to preserve their
    // half-yalm planning margin.
    private const float LunipyatiSequentialHazardArrivalTolerance = 0.25f;
    private const float NecrohazardTrailSpacing = 1.25f;
    private const float NecrohazardWaypointTolerance = 1.5f;
    private const float NecrohazardPreparationRadius = 4f;
    private const float NecrohazardRouteWaypointTolerance = 1f;
    private const float NecrohazardRouteRecoveryDistance = 2.5f;
    // The 2026-08-26 ThreeRoutes capture crossed the modeled five-yalm center-island edge by only
    // 0.10 yalms before the exact map arrived. The route builder correctly snapped to a safe grid
    // point, but the pulse gate rejected its unwalkable origin forever. Permit at most one yalm of
    // unmodeled prefix while entering that exact route; longer gaps remain fail-closed.
    private const float NecrohazardRouteEntryRecoveryDistance = 1f;
    private const float NecrohazardForcedDirectionProbeDistance = 3f;
    // A short route lookahead keeps the input heading stable without drawing a chord across the
    // narrow curved paths. The failed pull remained about ten yalms behind its Trust because it
    // aimed at one already-stale 1.25-yalm breadcrumb at a time.
    private const float NecrohazardTrailLookaheadDistance = 3.75f;
    private const int MaximumNecrohazardTrailPoints = 64;
    // The second live capture proved that navigator steering advanced only about 0.25 yalms per hand
    // cycle and left the player at radius 7.9 when the wall-distance check resolved. Open early enough
    // to cover the complete positive-progress half-turn, but close at 90 degrees so no admitted input
    // can move back toward the proximity source. A direct movement-key pulse is used below because
    // Temporary Misdirection applies the hand direction when a movement key is pressed.
    private const float MisdirectionGateOpenToleranceRadians = 70f * (MathF.PI / 180f);
    private const float MisdirectionGateCloseToleranceRadians = 90f * (MathF.PI / 180f);
    private const float MisdirectionMaximumPredictionRadians = 45f * (MathF.PI / 180f);
    private static readonly TimeSpan MisdirectionPredictionHorizon = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MisdirectionDiagnosticInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MisdirectionMovementPulse = TimeSpan.FromMilliseconds(250);

    // Ghidra 12.1.2 identifies the anchor as MOVSS [RIP+disp32],XMM1 followed by TEST RBX,RBX.
    // Sleigh marks all four displacement bytes as operand data, so only those bytes are wildcarded;
    // Add 4/TraceRelative resolves the float written by the first instruction. The complete two-
    // instruction pattern matched exactly once in executable .text and resolved into writable data
    // on Global 2026-08-14 (SHA-256 74F0408AD357), Global 2026-08-07 (04766591BCA8), Global
    // 2026-07-28 (9483706DDCCC), and Tencent 2026-07-31 (9FB8DD46FDE7). Keeping the register-bearing
    // ModRM byte fixed avoids accepting a different MOVSS global if compiler allocation changes.
    private const string ForcedMovementDirectionPattern =
        "Search F3 0F 11 0D ? ? ? ? 48 85 DB Add 4 TraceRelative";

    // Arena measurements follow the encounter's observed geometry. Insets are applied only where a
    // normal player radius or network latency could otherwise place RB on the lethal boundary.
    private const float LindblumArenaNavigationRadius = 19f;
    private const float KanilokkaInitialNavigationRadius = 19f;
    private const float KanilokkaSoulweaveNavigationRadius = 15f;
    // The manual Soulweave solver owns player-center destinations, so it carries the normal half-yalm
    // wall inset explicitly rather than relying on RB's separate exterior polygon.
    private const float KanilokkaSoulweavePlannerRadius = 14.5f;
    private const float KanilokkaPhantomFloodNavigationRadius = 4.5f;
    private const float KanilokkaNecrohazardNavigationRadius = 19.5f;
    // Soul Douse is a six-yalm stack. Use a 5.5-yalm goal region so Dark II can route around radial
    // sectors without requiring the Trust's exact position, while retaining snapshot clearance.
    private const float SoulDouseStackNavigationRadius = 5.5f;
    // Crater Carve leaves an 11-to-15-yalm annulus. Half-yalm insets preserve a three-yalm route.
    private const float LunipyatiArenaNavigationRadius = 14.5f;
    private const float LunipyatiCraterAvoidRadius = 11.5f;
    private const float LunipyatiEdgeDestinationRadius = 14f;
    private const float LunipyatiRingPathRadius = 13f;
    // Re-center below 12.25 yalms so Rock Blast retains recovery room above the 11.5-yalm crater.
    private const float LunipyatiRingMinimumHoldRadius = 12.25f;
    private const float RagingClawBehindDistance = 7f;
    private const float LeapingEarthImpactAvoidRadius = 5.5f;
    // Rock Blast hit moving snapshots at 5.94-5.98 yalms; 6.5 preserves the normal half-yalm margin.
    private const float RockBlastImpactAvoidRadius = 6.5f;
    // The combined rear-point sampler adds half a yalm to the verified six-yalm spread radius.
    private const float JaggedEdgeRadius = 6f;
    private const float JaggedEdgeOverlapAvoidRadius = 6.5f;
    private const int LeapingEarthConcurrentCurveCount = 4;
    private const int RockBlastImpactCount = 15;
    private const int RockBlastForecastCount = 10;
    private const float RockBlastStepDegrees = 22.5f;
    private const float LunipyatiAngularWaypointDegrees = 15f;
    private const float LineVoltageNarrowWidth = 5.5f;
    private const float LineVoltageWideWidth = 10.5f;
    private const float LineVoltageLength = 50f;
    // Route Cell Shock overlaps half a yalm inside the wall so chords follow the surviving outer arc.
    private const float LindblumCellShockRouteRadius = 18.5f;
    private const float LindblumCellShockWaypointDegrees = 10f;
    // A quarter-yalm planner inset and 0.2-yalm arrival tolerance preserve the narrow shared crescent.
    private const float LindblumOverlapPlannerClearance = 0.25f;
    private const float LindblumOverlapArrivalTolerance = 0.2f;
    // Cell Shock's authored radius is 26 yalms. The additional half-yalm is DutyMechanic's standard
    // network/player-center margin and still leaves the intended opposite-corner safe region open.
    private const float CellShockAvoidRadius = 26.5f;
    // Once action 40626 appears, correct the forecast if its helper differs by at least half a yalm.
    private const float CellShockLiveCorrectionTolerance = 0.5f;

    // Completed 2026-08-26 Global-client pulls showed two separate signals: a selector record enters
    // 0x0010 during Caber Toss, then a paired warning record changes about six seconds before the
    // visible Cell Shock cast. The warning state is part of the spatial key rather than an alternate
    // encoding for the same point: 0x0001 uses the selector's direct quadrant, while 0x0010 mirrors it
    // through arena center. Captures confirmed E5/EF 0x0001 at (81.132, 268.868), E5/EF 0x0010 at
    // (64.868, 285.132), and E4/D9 0x0010 at (64.868, 268.868). The encounter's four-index mapping
    // predicts the still-unobserved E4/D9 0x0001 point at (81.132, 285.132); the live helper correction
    // below remains armed so a later capture can safely validate or replace that prediction. Warning
    // states are accepted only after their paired selector, and MapEffect.Flags remain diagnostic-only
    // because they varied between otherwise identical pulls.
    private const ushort CellShockActiveMapEffectState = 0x0010;
    private const ushort CellShockDirectWarningState = 0x0001;
    private const ushort CellShockMirroredWarningState = 0x0010;
    private static readonly Vector3 CellShockEastLowZLocation = new(81.132f, -0.75f, 268.868f);
    private static readonly Vector3 CellShockEastHighZLocation = new(81.132f, -0.75f, 285.132f);
    private static readonly Vector3 CellShockWestLowZLocation = new(64.868f, -0.75f, 268.868f);
    private static readonly Vector3 CellShockWestHighZLocation = new(64.868f, -0.75f, 285.132f);
    private static readonly CellShockSelector[] CellShockSelectors =
    [
        new(
            0x00A601E5,
            0x00A609EF,
            CellShockEastLowZLocation,
            CellShockWestHighZLocation),
        new(
            0x00A601E4,
            0x00A608D9,
            CellShockEastHighZLocation,
            CellShockWestLowZLocation),
    ];

    // RB exposes the low 16 bits of the director state in MapEffect.State, as corroborated by the
    // first-boss 0x00200010 -> 0x0010 capture. During Lost Hope, a transition to one of these two
    // scoped values selects the authored Necrohazard floor; the same record returning to 0x0004 is
    // the 0x00080004 full-arena reset. Unknown or ambiguous transitions retain the Trust fallback.
    private const ushort NecrohazardFourRoutesMapState = 0x0040;
    private const ushort NecrohazardThreeRoutesMapState = 0x0100;
    private const ushort KanilokkaArenaResetMapState = 0x0004;
    // RB exposes Phantom Flood's 0x00200010 floor transition as its low word. Track it independently
    // so it cannot alter Necrohazard's mutually exclusive 0x0040/0x0100 layout state.
    private const ushort PhantomFloodActiveMapState = 0x0010;

    // Each curve is described by a distance and clockwise heading offset from action 40662. The
    // values reproduce the four observed five-yalm impact centers from arena center to wall.
    private static readonly (float Distance, float HeadingOffsetRadians)[] LeapingEarthCurveOffsets =
    [
        (0f, 0f),
        (5.92f, 66.8f * (MathF.PI / 180f)),
        (11.74f, 40.1f * (MathF.PI / 180f)),
        (14.33f, 15.8f * (MathF.PI / 180f)),
    ];

    // Action 40661 rotates this authored local-space spiral. Retain the complete sequence so the last
    // helper cannot outlive its semantic forecast, but only treat the activation-aware movement window
    // as unsafe; RB must never receive every location as simultaneous circles because that erases the
    // verified route through the arena.
    private static readonly (float X, float Z)[] LeapingEarthSpiralOffsets =
    [
        (0f, 0f), (-5.3f, 1.8f), (-4.6f, -4f), (1.4f, -5.8f), (6f, -1f),
        (4.7f, 5f), (0f, 8.5f), (-6f, 8.6f), (-10f, 5.6f), (-12f, 0.3f),
        (-10.9f, -5.1f), (-7.5f, -9.5f), (-2f, -11.7f), (4f, -11.5f),
        (9f, -8f), (11.7f, -2.7f), (11.9f, 3.3f), (8.9f, 8.8f),
        (4.5f, 13f), (-1.5f, 14.8f),
    ];

    // Rectangles are forward-only from each immutable helper snapshot. The extra quarter-yalm on
    // each side is DutyMechanic's latency margin over the authored five- and ten-yalm total widths.
    private static readonly Vector2[] LineVoltageNarrowRectangle =
    [
        new(LineVoltageNarrowWidth / 2f, LineVoltageLength),
        new(-LineVoltageNarrowWidth / 2f, LineVoltageLength),
        new(-LineVoltageNarrowWidth / 2f, 0f),
        new(LineVoltageNarrowWidth / 2f, 0f),
    ];

    private static readonly Vector2[] LineVoltageWideRectangle =
    [
        new(LineVoltageWideWidth / 2f, LineVoltageLength),
        new(-LineVoltageWideWidth / 2f, LineVoltageLength),
        new(-LineVoltageWideWidth / 2f, 0f),
        new(LineVoltageWideWidth / 2f, 0f),
    ];

    private readonly DirectedMovementState priorityMovement = new();
    // Line Voltage actors are recycled and can begin a future wave before the current wave resolves.
    // Immutable scalar snapshots preserve each cast's original rotation and activation while the
    // companion map prevents the polling loop from enqueueing the same live wrapper more than once.
    private readonly List<TimedLineVoltageRectangle> lineVoltageForecasts = [];
    private readonly Dictionary<uint, uint> observedLineVoltageCasts = [];
    // MapEffect wrappers are frame-scoped, so retain only scalar states and the confirmed location.
    // The state table is reset at each Caber Toss start to distinguish a new selector transition
    // from the director record left behind by the preceding sequence.
    private readonly Dictionary<uint, ushort> observedLindblumMapEffectStates = [];
    // Soulweave's helper wrappers can move or disappear before their delayed action effects. Keep a
    // chronological list rather than one entry per actor: a recycled Preserved Soul can begin its
    // next cast while the preceding ring is still retained for effect latency, and overwriting that
    // entry would prematurely advance the active wave. The companion map prevents per-tick polling
    // from enqueueing the same live cast repeatedly without retaining any frame-scoped actor.
    private readonly List<TimedSoulweaveRing> soulweaveForecasts = [];
    private readonly Dictionary<uint, SoulweaveCastObservation> observedSoulweaveCasts = [];
    // RB can drop an NPC cast wrapper about 0.3 seconds before its observed action-effect boundary.
    // Deferred scalar finishes keep the FIFO prefix active through that polling gap.
    private readonly List<TimedSoulweaveFinish> pendingSoulweaveFinishes = [];
    // Phantom Flood and Necrohazard reuse the arena director but have independent lifecycle rules.
    // Never merge this baseline with observedKanilokkaMapEffectStates: Lost Hope deliberately clears
    // the protected layout selector, while the blood-floor latch must survive until its own record
    // changes state.
    private readonly Dictionary<uint, ushort> observedPhantomFloodMapEffectStates = [];
    private readonly Dictionary<uint, ushort> observedKanilokkaMapEffectStates = [];
    private readonly List<Vector3> necrohazardExactRoute = [];
    private readonly Dictionary<uint, TimedCircle> boulderDanceForecasts = [];
    private readonly Dictionary<uint, LeapingEarthCurveForecast> leapingEarthCurveForecasts = [];
    private readonly Dictionary<uint, TimedHazardCircle> leapingEarthFallbackImpacts = [];
    private readonly List<TimedHazardCircle> leapingEarthSpiralForecasts = [];
    private readonly List<TimedHazardCircle> rockBlastForecasts = [];
    // Scalar breadcrumbs preserve the Trust's actual turns through the two unknown map-effect
    // layouts. Chasing only its current position can cut across a removed-floor corner when the bot
    // falls behind; no frame-scoped BattleCharacter wrappers are retained here.
    private readonly Queue<Vector3> necrohazardTrustTrail = [];

    private SubZoneId lastSubZoneId = SubZoneId.NONE;
    private DateTime caberTossMapEffectCaptureUntilUtc = DateTime.MinValue;
    private DateTime cellShockResolvesAtUtc = DateTime.MinValue;
    private DateTime cellShockUntilUtc = DateTime.MinValue;
    private TimedCircle cellShockForecast;
    private DateTime phantomFloodResolvesAtUtc = DateTime.MinValue;
    private DateTime phantomFloodUntilUtc = DateTime.MinValue;
    private DateTime phantomFloodMapEffectCaptureUntilUtc = DateTime.MinValue;
    private DateTime necrohazardUntilUtc = DateTime.MinValue;
    private DateTime ragingClawUntilUtc = DateTime.MinValue;
    private DateTime ragingClawJaggedEdgeUntilUtc = DateTime.MinValue;
    private DateTime leapingEarthSpiralUntilUtc = DateTime.MinValue;
    private DateTime rockBlastUntilUtc = DateTime.MinValue;
    private DateTime beastlyRoarResolvesAtUtc = DateTime.MinValue;
    private DateTime beastlyRoarUntilUtc = DateTime.MinValue;
    private DateTime lastMisdirectionDiagnosticUtc = DateTime.MinValue;
    private DateTime lastForcedMovementDirectionUtc = DateTime.MinValue;
    // Raging Claw's helper snapshots a stationary 45-yalm half-arena cone at cast start while the
    // visible boss continues moving and turning. Persist only these scalar cast values; using the
    // boss's live transform put the planned rear point 46-47 degrees inside the actual cone.
    private Vector3 ragingClawSource;
    private float ragingClawHeading;
    private uint ragingClawAnchorId;
    private Vector3 beastlyRoarSource;
    private float lastForcedMovementDirection;
    private uint leapingEarthSpiralAnchorId;
    private uint rockBlastAnchorId;
    private uint beastlyRoarAnchorId;
    private IntPtr forcedMovementDirectionAddress;
    private uint necrohazardTrailAnchorId;
    private uint phantomFloodMapEffectId;
    private uint necrohazardMapEffectId;
    private int necrohazardExactRouteIndex;
    private int rockBlastTraversalDirection;
    private string lastCaberTossMapEffectsFingerprint = string.Empty;
    private NecrohazardFloorLayout necrohazardFloorLayout;
    private NecrohazardFloorLayout necrohazardExactRouteLayout;
    private bool? lastMisdirectionInputAllowed;
    private bool caberTossWasCasting;
    private bool cellShockManualMovementActive;
    private bool lineVoltageManualMovementActive;
    private bool lineVoltageSolveFailureReported;
    private CellShockSelector pendingCellShockSelector;
    private Vector3? cellShockLineVoltageDestination;
    private Vector3? lineVoltageDestination;
    private DateTime lineVoltageWaveFirstActivationUtc = DateTime.MinValue;
    private bool hasLastForcedMovementDirection;
    private bool misdirectionInputGateOpen;
    private bool forcedMovementReadFailureReported;
    private bool lostHopeWasCasting;
    private bool necrohazardExactRouteFailureReported;
    private bool soulweaveSolveFailureReported;
    private bool soulweaveRouteFailureReported;
    private bool soulweaveOriginFailureReported;
    private bool soulweaveManualMovementActive;
    // Set only after the behavior pulse proves a sampled route into Soul Douse's stack region. The
    // avoidance predicates consume this cached decision instead of rerunning a full grid search for
    // every one of Dark II's twelve helper shapes; the next pulse clears it before revalidation.
    private bool darkIISoulDouseManualMovementActive;
    private bool phantomFloodFloorActive;
    private bool kanilokkaStandardBoundsEstablished;
    private bool craterActive;
    private DateTime craterCarveAoeResolvesAtUtc = DateTime.MinValue;
    private DateTime craterNavigationResetAtUtc = DateTime.MinValue;
    private bool craterCarveAoeWasCasting;
    private bool craterNavigationResetIssued;
    private SoulweaveWavePlan activeSoulweavePlan;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.YuweyawataFieldStation;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } =
    [
        EnemyAction.DarkSouls,
        EnemyAction.Slabber,
    ];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResolveForcedMovementDirectionAddress();
        RegisterLindblumAvoidance();
        RegisterKanilokkaAvoidance();
        RegisterLunipyatiAvoidance();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetEncounterState("leaving Yuweyawata Field Station");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;
        if (lastSubZoneId != SubZoneId.NONE && currentSubZoneId != lastSubZoneId)
        {
            ResetEncounterState($"sub-zone changed from {lastSubZoneId} to {currentSubZoneId}");
        }

        if (WorldManager.ZoneId == (uint)Data.ZoneId.YuweyawataFieldStation &&
            currentSubZoneId == SubZoneId.TheDustYoke)
        {
            // This also runs after combat so a boss death in the short post-cast delay cannot skip the
            // collision rebuild needed by the persistent route to the treasure chest.
            UpdateLunipyatiCraterNavigation(DateTime.UtcNow);
        }

        // DutyMechanic owns every verified boss telegraph below. Leaving SideStep enabled here would
        // duplicate approximate shapes and can erase the narrow safe regions in bosses two and three.
        SidestepPlugin.Enabled = !IsBossSubZone(currentSubZoneId);

        if (!Core.Player.InCombat)
        {
            if (WorldManager.ZoneId == (uint)Data.ZoneId.YuweyawataFieldStation &&
                currentSubZoneId == SubZoneId.SoulCenter &&
                phantomFloodFloorActive)
            {
                // Phantom Flood is a director-owned floor state rather than a combat timer. Continue
                // observing its latched record after a wipe or kill so only the actual reset/next-floor
                // transition releases the five-yalm boundary.
                UpdatePhantomFloodMapEffects(DateTime.UtcNow);
            }

            // Crater Carve changes the physical floor for the remainder of a successful instance.
            // Preserve only a living player's completed-fight crater; a death/wipe must clear it so
            // the next pull can use the restored center before Crater Carve happens again.
            ResetEncounterState(
                "combat ended",
                preserveLunipyatiCrater: ShouldRetainLunipyatiCrater(currentSubZoneId),
                preservePhantomFloodFloor: phantomFloodFloorActive);
            lastSubZoneId = currentSubZoneId;
            return false;
        }

        UpdateEncounterForecasts(currentSubZoneId);

        // These helpers are non-blocking and retain rotation ownership in the combat routine.
        _ = await TankBusterSpells();
        _ = await DamageMitigationSpells();

        bool handled = currentSubZoneId switch
        {
            SubZoneId.CrystalQuarry => await HandleLindblumZaghnalAsync(),
            SubZoneId.SoulCenter => await HandleOverseerKanilokkaAsync(),
            SubZoneId.TheDustYoke => await HandleLunipyatiAsync(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;
        return handled;
    }

    /// <summary>
    /// Keeps add targeting with OrderBot while giving one activation-aware owner either the confirmed
    /// Cell Shock overlap or the earliest concurrent Line Voltage wave. Other Lindblum geometry
    /// remains registered avoidance.
    /// </summary>
    private async Task<bool> HandleLindblumZaghnalAsync()
    {
        PrioritizeRawElectrope();
        DateTime now = DateTime.UtcNow;
        if (TryGetCellShockLineVoltageStage(now, out MechanicStage stage))
        {
            ClearLineVoltageMovementState(clearWaveIdentity: false);
            return await ExecutePriorityStageAsync(stage);
        }

        cellShockManualMovementActive = false;
        cellShockLineVoltageDestination = null;
        if (TryGetLineVoltageStage(now, out stage))
        {
            return await ExecutePriorityStageAsync(stage);
        }

        ClearLineVoltageMovementState(clearWaveIdentity: true);
        ReleasePriorityMovement("Lindblum has no active activation-aware movement stage");
        return false;
    }

    /// <summary>
    /// Solves the earliest Line Voltage activation cohort as one union, preventing independent
    /// rectangle avoids from escaping one lane through another lane that damages at the same time.
    /// The final point remains latched while late helper snapshots join the same 1.5-second wave;
    /// newly observed geometry invalidates and resamples it only when necessary.
    /// </summary>
    private bool TryGetLineVoltageStage(DateTime now, out MechanicStage stage)
    {
        stage = null;
        TimedLineVoltageRectangle[] wave = GetEarliestLineVoltageForecasts(now);
        if (wave.Length == 0 || DoesCellShockResolveBeforeLineVoltage(wave, now))
        {
            lineVoltageManualMovementActive = false;
            return false;
        }

        DateTime firstActivation = wave.Min(forecast => forecast.ActivatesAtUtc);
        if (lineVoltageWaveFirstActivationUtc != firstActivation)
        {
            lineVoltageWaveFirstActivationUtc = firstActivation;
            lineVoltageDestination = null;
            lineVoltageSolveFailureReported = false;
        }

        YuweyawataLineVoltageRectangle[] rectangles = wave
            .Select(forecast => new YuweyawataLineVoltageRectangle(
                forecast.Location,
                forecast.Heading,
                (forecast.IsWide ? LineVoltageWideWidth : LineVoltageNarrowWidth) / 2f,
                LineVoltageLength))
            .ToArray();
        bool latchedDestinationIsSafe = lineVoltageDestination is Vector3 latched &&
            YuweyawataLineVoltageGeometry.IsSafe(
                latched,
                ArenaCenter.LindblumZaghnal,
                LindblumCellShockRouteRadius,
                rectangles);
        if (!latchedDestinationIsSafe)
        {
            if (!YuweyawataLineVoltageGeometry.TryFindDestination(
                    ArenaCenter.LindblumZaghnal,
                    LindblumCellShockRouteRadius,
                    Core.Player.Location,
                    rectangles,
                    out Vector3 destination))
            {
                lineVoltageManualMovementActive = false;
                if (!lineVoltageSolveFailureReported)
                {
                    lineVoltageSolveFailureReported = true;
                    Logger.Warning(
                        "[Yuweyawata] No point clears the complete Line Voltage activation wave; " +
                        "leaving registered rectangle avoidance enabled as a fail-closed fallback.");
                }

                return false;
            }

            lineVoltageDestination = destination;
        }

        if (!YuweyawataLineVoltageGeometry.TryFindRouteWaypoint(
                ArenaCenter.LindblumZaghnal,
                LindblumCellShockRouteRadius,
                Core.Player.Location,
                lineVoltageDestination.Value,
                rectangles,
                out Vector3 waypoint))
        {
            lineVoltageManualMovementActive = false;
            if (!lineVoltageSolveFailureReported)
            {
                lineVoltageSolveFailureReported = true;
                Logger.Warning(
                    "[Yuweyawata] The selected Line Voltage point has no fully safe route; leaving " +
                    "registered rectangle avoidance enabled as a fail-closed fallback.");
            }

            return false;
        }

        lineVoltageSolveFailureReported = false;
        lineVoltageManualMovementActive = true;
        bool currentIsSafe = YuweyawataLineVoltageGeometry.IsSafe(
            Core.Player.Location,
            ArenaCenter.LindblumZaghnal,
            LindblumCellShockRouteRadius,
            rectangles);
        stage = new MechanicStage(
            MechanicKind.LineVoltage,
            firstActivation,
            wave.Max(forecast => forecast.ExpiresAtUtc),
            MovementPriority.LethalGeometry,
            waypoint,
            0,
            currentIsSafe
                ? "holding outside the complete concurrent Line Voltage wave"
                : "taking a segment-safe route out of the complete concurrent Line Voltage wave");
        return true;
    }

    /// <summary>
    /// Clears only the manual Line Voltage owner's latch. The immutable forecast queue is retained so
    /// Cell Shock can temporarily preempt movement and hand the same activation wave back afterward.
    /// </summary>
    private void ClearLineVoltageMovementState(bool clearWaveIdentity)
    {
        lineVoltageManualMovementActive = false;
        lineVoltageDestination = null;
        lineVoltageSolveFailureReported = false;
        if (clearWaveIdentity)
        {
            lineVoltageWaveFirstActivationUtc = DateTime.MinValue;
        }
    }

    /// <summary>
    /// Builds one shared-crescent stage when Cell Shock resolves before the queued Line Voltage
    /// cohort, preventing either mechanic from selecting a route through the other.
    /// </summary>
    private bool TryGetCellShockLineVoltageStage(DateTime now, out MechanicStage stage)
    {
        stage = null;
        TimedLineVoltageRectangle[] lineVoltageWave = GetEarliestLineVoltageForecasts(now);
        if (!DoesCellShockResolveBeforeLineVoltage(lineVoltageWave, now) || cellShockForecast == null)
        {
            cellShockManualMovementActive = false;
            return false;
        }

        TimedLineVoltageRectangle[] planningWave = GetCellShockPlanningLineVoltageForecasts(lineVoltageWave);
        Vector3? finalDestination = GetCellShockLineVoltageDestination(planningWave);
        if (!finalDestination.HasValue)
        {
            // Leave the Cell Shock fallback circle exposed when the shared sampler cannot prove a
            // destination. Line Voltage remains deferred because it resolves later in this window.
            cellShockManualMovementActive = false;
            return false;
        }

        Vector3? waypoint = GetCellShockMovementWaypoint(finalDestination.Value);
        if (!waypoint.HasValue)
        {
            cellShockManualMovementActive = false;
            return false;
        }

        cellShockManualMovementActive = true;
        stage = new MechanicStage(
            MechanicKind.CellShockLineVoltage,
            cellShockResolvesAtUtc,
            cellShockUntilUtc,
            MovementPriority.LethalGeometry,
            waypoint,
            0,
            planningWave.Length > 0
                ? "holding the Cell Shock-safe crescent at a point that also clears the following Line Voltage wave"
                : "moving into the confirmed Cell Shock-safe crescent before the following lane wave appears");
        return true;
    }

    /// <summary>
    /// Narrows the broader 1.5-second reactive lane batch to the truly simultaneous first subwave for
    /// Cell Shock prepositioning. Later lanes remain queued and regain ordinary ownership after the
    /// circle resolves; treating both subwaves as one intersection has no valid point in the arena.
    /// </summary>
    private static TimedLineVoltageRectangle[] GetCellShockPlanningLineVoltageForecasts(
        IReadOnlyCollection<TimedLineVoltageRectangle> lineVoltageWave)
    {
        if (lineVoltageWave.Count == 0)
        {
            return [];
        }

        DateTime cutoff = lineVoltageWave.Min(forecast => forecast.ActivatesAtUtc) +
                          CellShockLineVoltagePlanningWindow;
        return lineVoltageWave
            .Where(forecast => forecast.ActivatesAtUtc < cutoff)
            .ToArray();
    }

    /// <summary>
    /// Returns whether Cell Shock is the next damaging geometry. Earlier lane cohorts retain normal
    /// avoidance; an empty queue means the early warning should immediately begin prepositioning.
    /// </summary>
    private bool DoesCellShockResolveBeforeLineVoltage(
        IReadOnlyCollection<TimedLineVoltageRectangle> lineVoltageWave,
        DateTime now) =>
        IsCellShockForecastActive(now) &&
        (lineVoltageWave.Count == 0 ||
         cellShockResolvesAtUtc <= lineVoltageWave.Min(forecast => forecast.ActivatesAtUtc));

    /// <summary>
    /// Latches the nearest point inside Lindblum's inset arena, outside Cell Shock, and outside every
    /// lane in the cohort that resolves next. The latch changes only when newly observed lane geometry
    /// invalidates it, preventing the per-pulse destination oscillation seen with independent avoids.
    /// </summary>
    private Vector3? GetCellShockLineVoltageDestination(
        IReadOnlyCollection<TimedLineVoltageRectangle> lineVoltageWave)
    {
        float protectedCellShockRadius = CellShockAvoidRadius + LindblumOverlapPlannerClearance;
        bool IsSafe(Vector3 point) =>
            DistanceSquared2D(point, ArenaCenter.LindblumZaghnal) <=
                LindblumCellShockRouteRadius * LindblumCellShockRouteRadius &&
            DistanceSquared2D(point, cellShockForecast.Location) >
                protectedCellShockRadius * protectedCellShockRadius &&
            lineVoltageWave.All(forecast =>
                !IsInsideLineVoltageRectangle(point, forecast, LindblumOverlapPlannerClearance));

        if (cellShockLineVoltageDestination is Vector3 latched && IsSafe(latched))
        {
            return latched;
        }

        Vector3 current = Core.Player.Location;
        if (IsSafe(current))
        {
            cellShockLineVoltageDestination = current;
            return current;
        }

        Vector3? best = null;
        float bestDistanceSquared = float.MaxValue;
        for (float radius = 0f; radius <= LindblumCellShockRouteRadius + 0.001f; radius += 0.5f)
        {
            for (int degrees = 0; degrees < 360; degrees += 5)
            {
                float angle = degrees * (MathF.PI / 180f);
                Vector3 candidate = new(
                    ArenaCenter.LindblumZaghnal.X + (MathF.Sin(angle) * radius),
                    cellShockForecast.Location.Y,
                    ArenaCenter.LindblumZaghnal.Z + (MathF.Cos(angle) * radius));
                if (!IsSafe(candidate))
                {
                    continue;
                }

                float distanceSquared = DistanceSquared2D(current, candidate);
                if (distanceSquared < bestDistanceSquared)
                {
                    best = candidate;
                    bestDistanceSquared = distanceSquared;
                }
            }
        }

        cellShockLineVoltageDestination = best;
        return best;
    }

    /// <summary>
    /// Converts a final crescent destination into short outer-arena waypoints when the direct chord
    /// would cross Cell Shock. Moving radially to the inset wall and then around it keeps every segment
    /// outside the large circle while preserving enough time to preposition for the later lane wave.
    /// </summary>
    private Vector3? GetCellShockMovementWaypoint(Vector3 desired)
    {
        Vector3 current = Core.Player.Location;
        float protectedCellShockRadius = CellShockAvoidRadius + LindblumOverlapPlannerClearance;
        if (DistanceSquared2D(current, cellShockForecast.Location) <=
            protectedCellShockRadius * protectedCellShockRadius)
        {
            // Starting inside the forecast has no fully safe prefix; the sampled destination is the
            // nearest proven exit and therefore minimizes time spent in the dangerous region.
            return desired;
        }

        if (!DoesSegmentEnterCellShock(current, desired))
        {
            return desired;
        }

        float currentX = current.X - ArenaCenter.LindblumZaghnal.X;
        float currentZ = current.Z - ArenaCenter.LindblumZaghnal.Z;
        float currentRadius = MathF.Sqrt((currentX * currentX) + (currentZ * currentZ));
        if (currentRadius < 0.01f)
        {
            return null;
        }

        float currentAngle = MathF.Atan2(currentX, currentZ);
        Vector3 outerCurrent = new(
            ArenaCenter.LindblumZaghnal.X + (MathF.Sin(currentAngle) * LindblumCellShockRouteRadius),
            cellShockForecast.Location.Y,
            ArenaCenter.LindblumZaghnal.Z + (MathF.Cos(currentAngle) * LindblumCellShockRouteRadius));
        if (currentRadius < LindblumCellShockRouteRadius - MovementArrivalTolerance &&
            DistanceSquared2D(outerCurrent, cellShockForecast.Location) >
                protectedCellShockRadius * protectedCellShockRadius &&
            !DoesSegmentEnterCellShock(current, outerCurrent))
        {
            return outerCurrent;
        }

        float desiredAngle = MathF.Atan2(
            desired.X - ArenaCenter.LindblumZaghnal.X,
            desired.Z - ArenaCenter.LindblumZaghnal.Z);
        float angularDifference = NormalizeRadians(desiredAngle - currentAngle);
        float maximumStep = LindblumCellShockWaypointDegrees * (MathF.PI / 180f);
        int preferredDirection = angularDifference >= 0f ? 1 : -1;
        foreach (int direction in new[] { preferredDirection, -preferredDirection })
        {
            float step = direction * MathF.Min(maximumStep, MathF.Abs(angularDifference));
            float waypointAngle = currentAngle + step;
            Vector3 waypoint = new(
                ArenaCenter.LindblumZaghnal.X + (MathF.Sin(waypointAngle) * LindblumCellShockRouteRadius),
                cellShockForecast.Location.Y,
                ArenaCenter.LindblumZaghnal.Z + (MathF.Cos(waypointAngle) * LindblumCellShockRouteRadius));
            if (DistanceSquared2D(waypoint, cellShockForecast.Location) >
                    protectedCellShockRadius * protectedCellShockRadius &&
                !DoesSegmentEnterCellShock(current, waypoint))
            {
                return waypoint;
            }
        }

        return null;
    }

    /// <summary>
    /// Tests the forward-only five- or ten-yalm lane using FFXIV's heading-zero-along-positive-Z
    /// convention. Width constants already contain the measured half-yalm player/latency margin.
    /// </summary>
    private static bool IsInsideLineVoltageRectangle(
        Vector3 point,
        TimedLineVoltageRectangle forecast,
        float clearance = 0f)
    {
        float deltaX = point.X - forecast.Location.X;
        float deltaZ = point.Z - forecast.Location.Z;
        float forward = (deltaX * MathF.Sin(forecast.Heading)) +
                        (deltaZ * MathF.Cos(forecast.Heading));
        float sideways = (deltaX * MathF.Cos(forecast.Heading)) -
                         (deltaZ * MathF.Sin(forecast.Heading));
        float halfWidth = ((forecast.IsWide ? LineVoltageWideWidth : LineVoltageNarrowWidth) / 2f) +
                          clearance;
        return forward >= -clearance &&
               forward <= LineVoltageLength + clearance &&
               MathF.Abs(sideways) <= halfWidth;
    }

    /// <summary>
    /// Tests the complete movement chord rather than its endpoints. Two points in Cell Shock's narrow
    /// safe crescent can both be legal while their straight segment cuts through the lethal circle.
    /// </summary>
    private bool DoesSegmentEnterCellShock(Vector3 start, Vector3 end)
    {
        float protectedRadius = CellShockAvoidRadius + LindblumOverlapPlannerClearance;
        float startX = start.X - cellShockForecast.Location.X;
        float startZ = start.Z - cellShockForecast.Location.Z;
        float segmentX = end.X - start.X;
        float segmentZ = end.Z - start.Z;
        float segmentLengthSquared = (segmentX * segmentX) + (segmentZ * segmentZ);
        float interpolation = segmentLengthSquared < 0.0001f
            ? 0f
            : MathF.Max(0f, MathF.Min(1f,
                -((startX * segmentX) + (startZ * segmentZ)) / segmentLengthSquared));
        float closestX = startX + (segmentX * interpolation);
        float closestZ = startZ + (segmentZ * interpolation);
        return (closestX * closestX) + (closestZ * closestZ) <=
               protectedRadius * protectedRadius;
    }

    /// <summary>
    /// Mirrors the encounter's add-first target hint. Electrify is a sixteen-second failure cast,
    /// so every live Raw Electrope must outrank the boss without taking combat-rotation ownership.
    /// </summary>
    private static void PrioritizeRawElectrope()
    {
        BattleCharacter electrope = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.RawElectrope)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.IsVisible && actor.IsTargetable)
            .OrderBy(actor => actor.Distance2D())
            .ThenBy(actor => actor.ObjectId)
            .FirstOrDefault();
        if (electrope != null && Core.Player.CurrentTarget?.ObjectId != electrope.ObjectId)
        {
            electrope.Target();
        }
    }

    /// <summary>
    /// Resolves Kanilokka's semantic path, activation-timed ring, and stack mechanics by action-effect
    /// time. Soulweave uses an encounter-local radial test because RB polygons cannot preserve its
    /// hole or its sequential activation order.
    /// </summary>
    private async Task<bool> HandleOverseerKanilokkaAsync()
    {
        List<MechanicStage> stages = [];
        DateTime now = DateTime.UtcNow;
        darkIISoulDouseManualMovementActive = false;

        if (now < necrohazardUntilUtc || Core.Player.HasAura(PlayerAura.TemporaryMisdirection))
        {
            Vector3? destination;
            uint anchorObjectId;
            string reason;
            if (TryGetExactNecrohazardDestination(out Vector3 exactDestination))
            {
                destination = exactDestination;
                anchorObjectId = necrohazardMapEffectId;
                reason = $"following the confirmed {necrohazardFloorLayout} Necrohazard floor route";
            }
            else if (GetFirstCaster(EnemyAction.LostHope) != null)
            {
                destination = GetNecrohazardPreparationDestination();
                anchorObjectId = 0;
                reason = "prepositioning on Necrohazard's surviving center island";
            }
            else
            {
                BattleCharacter trustAnchor = GetLatchedTrustAnchor();
                destination = GetNecrohazardTrailDestination(trustAnchor);
                anchorObjectId = trustAnchor?.ObjectId ?? 0;
                reason = "following a live duty-support path because the floor layout is unconfirmed";
            }

            stages.Add(new MechanicStage(
                MechanicKind.NecrohazardPath,
                now,
                necrohazardUntilUtc > now ? necrohazardUntilUtc : now + ResolutionGrace,
                MovementPriority.ForcedPath,
                destination,
                anchorObjectId,
                reason));
        }
        else
        {
            ClearNecrohazardTrail();
            ClearExactNecrohazardRoute();
        }

        if (TryGetSoulweaveStage(now, out MechanicStage soulweaveStage))
        {
            stages.Add(soulweaveStage);
        }

        BattleCharacter soulDouseCaster = GetFirstCaster(EnemyAction.SoulDouse);
        if (soulDouseCaster != null && TryGetStackDestination(soulDouseCaster, out Vector3 soulDouseDestination))
        {
            BattleCharacter[] activeDarkIIWave = GetActiveDarkIIWaveCasters();
            bool darkIIOverlap = activeDarkIIWave.Length > 0;
            bool targetHasSafeRoute = TryGetDarkIIStackWaypoint(
                soulDouseDestination,
                activeDarkIIWave,
                out Vector3 soulDouseWaypoint);
            DateTime soulDouseResolvesAt = now + soulDouseCaster.SpellCastInfo.RemainingCastTime;

            // Dark II's second wave resolves only about 0.15 seconds before Soul Douse. Route into
            // the stack region during the cone wave; if no safe path exists, leave cone avoidance on.
            MechanicKind kind = darkIIOverlap && targetHasSafeRoute
                ? MechanicKind.DarkIISoulDouse
                : MechanicKind.SoulDouse;
            DateTime resolvesAt = kind == MechanicKind.DarkIISoulDouse
                ? now + activeDarkIIWave.Min(caster => caster.SpellCastInfo.RemainingCastTime)
                : soulDouseResolvesAt;
            stages.Add(new MechanicStage(
                kind,
                resolvesAt,
                soulDouseResolvesAt + ResolutionGrace,
                kind == MechanicKind.DarkIISoulDouse
                    ? MovementPriority.LethalGeometry
                    : MovementPriority.Stack,
                targetHasSafeRoute ? soulDouseWaypoint : null,
                soulDouseCaster.SpellCastInfo.TargetId,
                kind == MechanicKind.DarkIISoulDouse
                    ? "routing around the current Dark II cone wave toward Soul Douse's live Trust target"
                    : "stacking for Soul Douse after earlier Dark II geometry is safe"));
        }

        MechanicStage selectedStage = SelectPriorityStage(stages);
        // Suppress the standalone cone collection only when the concurrent priority resolver really
        // chose this sampled route. Merely having a valid candidate must not disable Dark II while an
        // earlier forced-path or lethal stage owns the same behavior pulse.
        darkIISoulDouseManualMovementActive = selectedStage?.Kind == MechanicKind.DarkIISoulDouse;
        return await ExecutePriorityStageAsync(selectedStage);
    }

    /// <summary>
    /// Resolves Lunipyati's cleave, activation-sensitive impact, proximity, spread-overlap, and
    /// stack requirements through one movement owner.
    /// </summary>
    private async Task<bool> HandleLunipyatiAsync()
    {
        List<MechanicStage> stages = [];
        DateTime now = DateTime.UtcNow;
        TimedHazardCircle[] leapingEarthCurveHazards = GetPublishedLeapingEarthCurveHazards(now);
        TimedHazardCircle[] leapingEarthSpiralForecasts = GetPublishedLeapingEarthSpiralForecasts(now);
        // The authored spiral predicts the entire sequence, while live 40606 helpers are the
        // authoritative immediate positions. Merge both scalar sources before narrowing the
        // activation window so a late or truncated forecast can never suppress a visible impact.
        TimedHazardCircle[] leapingEarthSpiralHazards = GetLeapingEarthSpiralPlanningHazards(
            leapingEarthSpiralForecasts.Concat(GetLiveLeapingEarthImpactHazards(now)),
            now);
        TimedHazardCircle[] rockBlastForecasts = GetPublishedRockBlastForecasts(now);
        TimedHazardCircle[] rockBlastHazards = GetActionableHazards(
            rockBlastForecasts,
            now,
            RockBlastMovementLead);

        if ((now < ragingClawUntilUtc || now < ragingClawJaggedEdgeUntilUtc) &&
            ragingClawAnchorId != 0)
        {
            BattleCharacter[] jaggedEdgeCasters = GetCasters([EnemyAction.JaggedEdge]).ToArray();
            // Post-crater Raging Claw is followed by Jagged Edge about 5.5 seconds later, so one
            // movement owner handles the complete sequence.
            bool jaggedEdgeOverlap = IsRagingClawJaggedEdgeOverlapActive();
            // Recompute the annulus waypoint every pulse; retaining the first point can stop movement
            // inside the repeated cleave.
            Vector3? destination = jaggedEdgeOverlap
                ? jaggedEdgeCasters.Length > 0
                    ? GetRagingClawJaggedEdgeDestination(
                        ragingClawSource,
                        ragingClawHeading,
                        jaggedEdgeCasters)
                    : GetLunipyatiMovementWaypoint(
                        GetRagingClawBehindDestination(ragingClawSource, ragingClawHeading))
                : GetLunipyatiMovementWaypoint(
                    GetRagingClawBehindDestination(ragingClawSource, ragingClawHeading));
            DateTime activeUntil = jaggedEdgeOverlap
                ? Max(ragingClawUntilUtc, ragingClawJaggedEdgeUntilUtc)
                : ragingClawUntilUtc;
            stages.Add(new MechanicStage(
                jaggedEdgeOverlap ? MechanicKind.RagingClawJaggedEdge : MechanicKind.RagingClaw,
                now,
                activeUntil,
                MovementPriority.LethalGeometry,
                destination,
                ragingClawAnchorId,
                jaggedEdgeOverlap
                    ? "holding one rear safe point through concurrent Raging Claw and Jagged Edge"
                    : "remaining behind Lunipyati through every Raging Claw hit"));
        }

        if (leapingEarthCurveHazards.Length > 0)
        {
            stages.Add(new MechanicStage(
                MechanicKind.LeapingEarthCurve,
                leapingEarthCurveHazards.Min(hazard => hazard.ActivatesAtUtc),
                leapingEarthCurveHazards.Max(hazard => hazard.ExpiresAtUtc),
                MovementPriority.LethalGeometry,
                GetLunipyatiForecastHoldDestination(leapingEarthCurveHazards),
                leapingEarthCurveHazards[0].AnchorObjectId,
                "holding a safe region through the prioritized Leaping Earth curve wave"));
        }

        if (leapingEarthSpiralForecasts.Length > 0)
        {
            stages.Add(new MechanicStage(
                MechanicKind.LeapingEarthSpiral,
                leapingEarthSpiralForecasts.Min(hazard => hazard.ActivatesAtUtc),
                leapingEarthSpiralUntilUtc,
                MovementPriority.LethalGeometry,
                GetTimeAwareLunipyatiDestination(
                    MechanicKind.LeapingEarthSpiral,
                    leapingEarthSpiralHazards,
                    requireRingPath: false,
                    hazardRadius: LeapingEarthImpactAvoidRadius),
                leapingEarthSpiralAnchorId,
                "stepping through only the imminently resolving Leaping Earth spiral impacts"));
        }

        if (now < beastlyRoarUntilUtc)
        {
            stages.Add(new MechanicStage(
                MechanicKind.BeastlyRoar,
                beastlyRoarResolvesAtUtc,
                beastlyRoarUntilUtc,
                MovementPriority.LethalGeometry,
                GetLunipyatiMovementWaypoint(GetLunipyatiEdgeDestinationAwayFrom(beastlyRoarSource)),
                beastlyRoarAnchorId,
                "moving around the crater ring to the far side of Beastly Roar"));
        }

        if (rockBlastForecasts.Length > 0)
        {
            stages.Add(new MechanicStage(
                MechanicKind.RockBlast,
                rockBlastForecasts.Min(hazard => hazard.ActivatesAtUtc),
                rockBlastUntilUtc,
                MovementPriority.LethalGeometry,
                GetTimeAwareLunipyatiDestination(
                    MechanicKind.RockBlast,
                    rockBlastHazards,
                    requireRingPath: true,
                    hazardRadius: RockBlastImpactAvoidRadius),
                rockBlastAnchorId,
                "following the surviving ring ahead of only the imminently resolving Rock Blast impacts"));
        }

        BattleCharacter turaliStoneCaster = GetFirstCaster(EnemyAction.TuraliStone);
        if (turaliStoneCaster != null && TryGetStackDestination(turaliStoneCaster, out Vector3 turaliStoneDestination))
        {
            // A live Raging Claw cone remains lethal through its repeat hits. Do not enter the front
            // half merely because the later stack has started; the planner will re-evaluate next tick.
            bool blockedByEarlierGeometry =
                (now < ragingClawUntilUtc && IsInsideRagingClaw(turaliStoneDestination)) ||
                !IsPointInLunipyatiWalkableArena(turaliStoneDestination) ||
                IsInsideAnyHazard(
                    turaliStoneDestination,
                    leapingEarthCurveHazards,
                    LeapingEarthImpactAvoidRadius) ||
                IsInsideAnyHazard(
                    turaliStoneDestination,
                    leapingEarthSpiralHazards,
                    LeapingEarthImpactAvoidRadius) ||
                IsInsideAnyHazard(
                    turaliStoneDestination,
                    rockBlastHazards,
                    RockBlastImpactAvoidRadius);
            Vector3? destination = blockedByEarlierGeometry
                ? null
                : GetLunipyatiMovementWaypoint(turaliStoneDestination);
            DateTime resolvesAt = now + turaliStoneCaster.SpellCastInfo.RemainingCastTime;
            stages.Add(new MechanicStage(
                MechanicKind.TuraliStone,
                resolvesAt,
                resolvesAt + ResolutionGrace,
                MovementPriority.Stack,
                destination,
                turaliStoneCaster.SpellCastInfo.TargetId,
                "stacking on Turali Stone's live cast target"));
        }

        if (craterActive)
        {
            BattleCharacter boss = GetBoss(EnemyNpc.Lunipyati);
            Vector3 maintenanceDestination = GetCraterRingCombatDestination(boss);
            stages.Add(new MechanicStage(
                MechanicKind.CraterRing,
                now + TimeSpan.FromHours(1),
                now + TimeSpan.FromSeconds(1),
                MovementPriority.RingMaintenance,
                GetLunipyatiMovementWaypoint(maintenanceDestination),
                boss?.ObjectId ?? 0,
                "keeping combat movement and gap closers on the surviving crater ring"));
        }

        return await ExecutePriorityStageAsync(SelectPriorityStage(stages));
    }

    /// <summary>
    /// Selects the first action-effect stage; mechanics within the concurrency window use explicit
    /// lethality priority so collection and registration order cannot change the destination.
    /// </summary>
    private static MechanicStage SelectPriorityStage(IReadOnlyCollection<MechanicStage> stages)
    {
        if (stages.Count == 0)
        {
            return null;
        }

        DateTime firstResolution = stages.Min(stage => stage.ResolvesAtUtc);
        DateTime concurrentCutoff = firstResolution + ConcurrentResolutionWindow;
        return stages
            .Where(stage => stage.ResolvesAtUtc <= concurrentCutoff)
            .OrderBy(stage => stage.Priority)
            .ThenBy(stage => stage.ResolvesAtUtc)
            .ThenBy(stage => stage.Kind)
            .First();
    }

    /// <summary>
    /// Moves toward one selected destination without starving healing, mitigation, or rotation after
    /// arrival. Ordinary geometry yields to active RebornBuddy avoidance; activation-aware sequences
    /// that suppress their standalone registrations retain exclusive ownership of their shared solve.
    /// </summary>
    private async Task<bool> ExecutePriorityStageAsync(MechanicStage stage)
    {
        if (stage == null)
        {
            ReleasePriorityMovement("no semantic mechanic is active");
            return false;
        }

        if (stage.Destination == null)
        {
            ReleasePriorityMovement($"{stage.Kind} has no currently safe semantic destination");
            return false;
        }

        bool changedStage = priorityMovement.Kind != stage.Kind ||
                            priorityMovement.AnchorObjectId != stage.AnchorObjectId;
        if (changedStage)
        {
            ReleasePriorityMovement($"priority changed to {stage.Kind}");
            priorityMovement.Kind = stage.Kind;
            priorityMovement.AnchorObjectId = stage.AnchorObjectId;
            Logger.Information($"[Yuweyawata] Selected {stage.Kind}: {stage.Reason}.");
        }

        priorityMovement.Destination = stage.Destination.Value;
        int leaseMilliseconds = Math.Max(
            MinimumMovementLeaseMilliseconds,
            (int)Math.Ceiling((stage.ActiveUntilUtc - DateTime.UtcNow).TotalMilliseconds));
        CapabilityManager.Update(
            priorityMovement.Handle,
            CapabilityFlags.Movement,
            leaseMilliseconds,
            stage.Reason);
        priorityMovement.Owned = true;

        if (stage.Kind == MechanicKind.NecrohazardPath &&
            Core.Player.HasAura(PlayerAura.TemporaryMisdirection))
        {
            // Necrohazard's route already incorporates either the exact floor or the scoped Trust
            // fallback. Its center circle is suppressed while the aura is active, so this stage must
            // remain movement owner and gate input instead of yielding to competing RB avoidance.
            return await ExecuteMisdirectionStageAsync(stage);
        }

        // Exclusive stages already include the standalone geometry they suppress. Soulweave folds in
        // Telltale spacing, and Dark II/Soul Douse owns its sampled cone-safe stack route; yielding to
        // RB avoidance would reintroduce a second movement destination.
        bool soulweaveOwnsMovement = stage.Kind == MechanicKind.Soulweave;
        bool exclusiveMovement = stage.Kind is
            MechanicKind.CellShockLineVoltage or
            MechanicKind.LineVoltage or
            MechanicKind.DarkIISoulDouse or
            MechanicKind.LeapingEarthSpiral or
            MechanicKind.RockBlast or
            MechanicKind.RagingClawJaggedEdge ||
            (craterActive && stage.Kind == MechanicKind.RagingClaw) ||
            soulweaveOwnsMovement;
        if (AvoidanceManager.IsRunningOutOfAvoid && !exclusiveMovement)
        {
            priorityMovement.MovementIssued = false;
            return false;
        }

        float arrivalTolerance = stage.Kind switch
        {
            MechanicKind.CellShockLineVoltage or MechanicKind.LineVoltage =>
                LindblumOverlapArrivalTolerance,
            MechanicKind.Soulweave => SoulweaveArrivalTolerance,
            MechanicKind.LeapingEarthSpiral or MechanicKind.RockBlast =>
                LunipyatiSequentialHazardArrivalTolerance,
            _ => MovementArrivalTolerance,
        };
        if (Core.Player.Distance2D(priorityMovement.Destination) <= arrivalTolerance)
        {
            if (priorityMovement.MovementIssued)
            {
                Navigator.PlayerMover.MoveStop();
                priorityMovement.MovementIssued = false;
            }

            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(priorityMovement.Destination);
        priorityMovement.MovementIssued = true;
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Pulses a real movement key only while the client's forced direction advances the selected
    /// floor route. Navigator steering advanced only a fraction of a yalm per hand cycle in the
    /// observed failure; direct forward input preserves full movement while the angular and floor-
    /// segment gates decide whether the current rotating direction is safe.
    /// </summary>
    private async Task<bool> ExecuteMisdirectionStageAsync(MechanicStage stage)
    {
        if (!TryReadForcedMovementDirection(out float forcedDirection))
        {
            // A later successful read must establish a fresh angular-velocity baseline; retaining an
            // open gate across a read failure could authorize input from an unrelated old heading.
            ResetMisdirectionInputGate();
            StopPriorityMovement();
            return false;
        }

        Vector3 destination = stage.Destination.Value;
        float deltaX = destination.X - Core.Player.X;
        float deltaZ = destination.Z - Core.Player.Z;
        float distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
        if (distanceSquared <= MovementArrivalTolerance * MovementArrivalTolerance)
        {
            StopPriorityMovement();
            return false;
        }

        // FFXIV headings use zero along +Z, so atan2(X, Z) produces the same angular convention as
        // the client forced-direction float and the actor headings used by the avoidance helpers.
        float desiredDirection = NormalizeRadians(MathF.Atan2(deltaX, deltaZ));
        float angularDifference = MathF.Abs(NormalizeRadians(desiredDirection - forcedDirection));
        float predictedDirection = PredictForcedMovementDirection(forcedDirection);
        float predictedDifference = MathF.Abs(NormalizeRadians(desiredDirection - predictedDirection));
        bool exactRouteOwnsDestination =
            stage.AnchorObjectId == necrohazardMapEffectId && necrohazardExactRoute.Count > 0;
        bool forcedDirectionKeepsFloor = IsNecrohazardForcedDirectionSafe(
            forcedDirection,
            destination,
            exactRouteOwnsDestination,
            out bool usingRouteEntryRecovery);

        // Prediction opens the gate just before the hand aligns. Once moving, hysteresis keeps input
        // active through the full positive-progress arc instead of stopping on every scheduler tick.
        // A confirmed layout adds the same independent requirement used by geometry-driven movement:
        // several yalms in the current forced direction must remain on surviving floor.
        if (misdirectionInputGateOpen)
        {
            misdirectionInputGateOpen =
                angularDifference <= MisdirectionGateCloseToleranceRadians &&
                forcedDirectionKeepsFloor;
        }
        else
        {
            misdirectionInputGateOpen =
                angularDifference <= MisdirectionGateCloseToleranceRadians &&
                MathF.Min(angularDifference, predictedDifference) <= MisdirectionGateOpenToleranceRadians &&
                forcedDirectionKeepsFloor;
        }

        bool inputAllowed = misdirectionInputGateOpen;
        LogMisdirectionDecision(
            stage,
            forcedDirection,
            predictedDirection,
            desiredDirection,
            angularDifference,
            predictedDifference,
            forcedDirectionKeepsFloor,
            usingRouteEntryRecovery,
            inputAllowed);

        if (!inputAllowed)
        {
            StopPriorityMovement();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        if (!priorityMovement.MovementIssued)
        {
            // End any stale SlideMover steering before beginning direct key input. Repeating this on
            // every tick would recreate the observed micro-pulses, so it is done only when the gate
            // transitions from closed to open.
            Navigator.PlayerMover.MoveStop();
        }

        MovementManager.Move(MovementDirection.Forward, MisdirectionMovementPulse);
        priorityMovement.MovementIssued = true;
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Verifies the next forced-direction pulse against the confirmed floor. Unknown layouts retain
    /// the captured Trust fallback rather than applying geometry from both mutually exclusive maps;
    /// exact routes alone may recover through the bounded conservative-model edge described below.
    /// </summary>
    private bool IsNecrohazardForcedDirectionSafe(
        float forcedDirection,
        Vector3 destination,
        bool allowRouteEntryRecovery,
        out bool usingRouteEntryRecovery)
    {
        usingRouteEntryRecovery = false;
        if (necrohazardFloorLayout == NecrohazardFloorLayout.None)
        {
            return true;
        }

        float destinationDistanceSquared = DistanceSquared2D(Core.Player.Location, destination);
        float probeDistance = MathF.Min(
            NecrohazardForcedDirectionProbeDistance,
            MathF.Sqrt(destinationDistanceSquared));
        Vector3 start = Core.Player.Location;
        Vector3 end = new(
            start.X + (MathF.Sin(forcedDirection) * probeDistance),
            start.Y,
            start.Z + (MathF.Cos(forcedDirection) * probeDistance));
        if (YuweyawataNecrohazardGeometry.IsSegmentWalkable(
                necrohazardFloorLayout,
                start,
                end))
        {
            return true;
        }

        // Route construction deliberately snaps a slightly off-model starting point to its nearest
        // safe grid cell. Apply the matching recovery rule only to a destination owned by that exact
        // route; Trust breadcrumbs and arbitrary points retain the original strict segment test.
        usingRouteEntryRecovery = allowRouteEntryRecovery &&
            YuweyawataNecrohazardGeometry.IsRouteEntryRecoverySegmentWalkable(
                necrohazardFloorLayout,
                start,
                end,
                destination,
                NecrohazardRouteRecoveryDistance,
                NecrohazardRouteEntryRecoveryDistance);
        return usingRouteEntryRecovery;
    }

    /// <summary>
    /// Projects the rotating client direction over one short bot-scheduler horizon. Projection is
    /// capped at 45 degrees so a delayed frame or noisy read can open the input gate slightly early
    /// but can never authorize movement based on a remote part of the next rotation.
    /// </summary>
    private float PredictForcedMovementDirection(float forcedDirection)
    {
        DateTime now = DateTime.UtcNow;
        float predictedDirection = forcedDirection;
        TimeSpan elapsed = now - lastForcedMovementDirectionUtc;
        if (hasLastForcedMovementDirection &&
            elapsed > TimeSpan.FromMilliseconds(5) &&
            elapsed < TimeSpan.FromMilliseconds(250))
        {
            float angularVelocity = NormalizeRadians(forcedDirection - lastForcedMovementDirection) /
                                    (float)elapsed.TotalSeconds;
            float projectedDelta = angularVelocity * (float)MisdirectionPredictionHorizon.TotalSeconds;
            projectedDelta = Math.Clamp(
                projectedDelta,
                -MisdirectionMaximumPredictionRadians,
                MisdirectionMaximumPredictionRadians);
            predictedDirection = NormalizeRadians(forcedDirection + projectedDelta);
        }

        lastForcedMovementDirection = forcedDirection;
        lastForcedMovementDirectionUtc = now;
        hasLastForcedMovementDirection = true;
        return predictedDirection;
    }

    /// <summary>
    /// Resolves the read-only client float used to render and apply Temporary Misdirection's rotating
    /// direction. Failure is non-fatal: the mechanic will stop issuing movement rather than guess at
    /// an angle after a client update.
    /// </summary>
    private void ResolveForcedMovementDirectionAddress()
    {
        forcedMovementDirectionAddress = IntPtr.Zero;
        forcedMovementReadFailureReported = false;

        try
        {
            using PatternFinder patternFinder = new(Core.Memory);
            forcedMovementDirectionAddress = patternFinder.Find(ForcedMovementDirectionPattern);
            if (forcedMovementDirectionAddress == IntPtr.Zero)
            {
                Logger.Warning(
                    "[Yuweyawata] Forced-movement direction pattern returned no address; " +
                    "Necrohazard movement will fail closed.");
            }
        }
        catch (Exception exception)
        {
            Logger.Warning(
                $"[Yuweyawata] Could not resolve the forced-movement direction ({exception.Message}); " +
                "Necrohazard movement will fail closed.");
        }
    }

    /// <summary>
    /// Reads and normalizes the current client-owned forced direction without retaining a pointer to
    /// any RB object wrapper.
    /// </summary>
    private bool TryReadForcedMovementDirection(out float direction)
    {
        direction = 0f;
        if (forcedMovementDirectionAddress == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            float value = Core.Memory.Read<float>(forcedMovementDirectionAddress);
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new InvalidOperationException($"client returned non-finite angle {value}");
            }

            direction = NormalizeRadians(value);
            return true;
        }
        catch (Exception exception)
        {
            if (!forcedMovementReadFailureReported)
            {
                forcedMovementReadFailureReported = true;
                Logger.Warning(
                    $"[Yuweyawata] Forced-movement direction read failed ({exception.Message}); " +
                    "Necrohazard movement will remain stopped.");
            }

            return false;
        }
    }

    /// <summary>
    /// Emits bounded live evidence for tuning the angle gate without turning normal dungeon runs into
    /// per-frame log spam.
    /// </summary>
    private void LogMisdirectionDecision(
        MechanicStage stage,
        float forcedDirection,
        float predictedDirection,
        float desiredDirection,
        float angularDifference,
        float predictedDifference,
        bool forcedDirectionKeepsFloor,
        bool usingRouteEntryRecovery,
        bool inputAllowed)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (lastMisdirectionInputAllowed == inputAllowed &&
            now - lastMisdirectionDiagnosticUtc < MisdirectionDiagnosticInterval)
        {
            return;
        }

        lastMisdirectionInputAllowed = inputAllowed;
        lastMisdirectionDiagnosticUtc = now;
        Logger.Information(
            $"[MechanicDiag] YUWEYAWATA_MISDIRECTION anchor=0x{stage.AnchorObjectId:X8} " +
            $"forced={forcedDirection:F3} predicted={predictedDirection:F3} desired={desiredDirection:F3} " +
            $"differenceDegrees={angularDifference * (180f / MathF.PI):F1} " +
            $"predictedDifferenceDegrees={predictedDifference * (180f / MathF.PI):F1} " +
            $"layout={necrohazardFloorLayout} floorSafe={forcedDirectionKeepsFloor} " +
            $"routeEntryRecovery={usingRouteEntryRecovery} " +
            $"inputAllowed={inputAllowed} player={Core.Player.Location} destination={stage.Destination.Value}.");
    }

    /// <summary>
    /// Cancels both navigation steering and direct movement-key input while preserving the planner's
    /// capability lease. Repeating the stop while the angle is wrong is intentional because the
    /// client direction keeps rotating even when no new destination has been issued.
    /// </summary>
    private void StopPriorityMovement()
    {
        Navigator.PlayerMover.MoveStop();
        MovementManager.MoveStop();
        priorityMovement.MovementIssued = false;
    }

    /// <summary>
    /// Normalizes an angle to [-pi, pi] so wraparound near north does not incorrectly close the
    /// Temporary Misdirection input gate.
    /// </summary>
    private static float NormalizeRadians(float angle)
    {
        float fullTurn = 2f * MathF.PI;
        float normalized = angle % fullTurn;
        if (normalized > MathF.PI)
        {
            normalized -= fullTurn;
        }
        else if (normalized < -MathF.PI)
        {
            normalized += fullTurn;
        }

        return normalized;
    }

    /// <summary>
    /// Registers Lindblum's verified line, landing-circle, puddle, spread, and arena geometry.
    /// </summary>
    private void RegisterLindblumAvoidance()
    {
        // A single activation-aware queue owns all four Line Voltage action IDs. Registering each
        // live cast directly made sequential Caber Toss waves overlap in RB and left no navigable
        // intersection; immutable forecasts also prevent recycled helpers from rotating old lanes.
        AvoidanceManager.AddAvoidPolygon<TimedLineVoltageRectangle>(
            condition: IsLindblumCombat,
            leashPointProducer: () => ArenaCenter.LindblumZaghnal,
            leashRadius: LindblumArenaNavigationRadius,
            rotationProducer: forecast => -forecast.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: forecast => forecast.IsWide
                ? LineVoltageWideRectangle
                : LineVoltageNarrowRectangle,
            locationProducer: forecast => forecast.Location,
            collectionProducer: GetActiveLineVoltageForecasts,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // The paired director warning supplies roughly 8.1 seconds of lead. Earlier Line Voltage
        // cohorts remain reactive, while the encounter planner suppresses this fallback after it has
        // selected a path-safe point for Cell Shock and the later-resolving lane cohort.
        AvoidanceHelpers.AddAvoidDonut(
            IsLindblumCombat,
            GetActiveCellShockForecastLocations,
            outerRadius: CellShockAvoidRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);

        // Unmapped selector variants still receive the live helper fallback. Suppress it only when
        // a confirmed paired warning has armed predictive geometry.
        AddCastCircle(
            () => IsLindblumCombat() && !IsCellShockForecastActive(DateTime.UtcNow),
            EnemyAction.CellShock,
            CellShockAvoidRadius,
            useCastLocation: false);
        AddCastCircle(IsLindblumCombat, EnemyAction.LightningBolt, 6f, useCastLocation: true);
        AddTargetedSpread(IsLindblumCombat, EnemyAction.LightningStormAoe, 5f);

        AvoidanceHelpers.AddAvoidDonut(
            IsLindblumCombat,
            () => ArenaCenter.LindblumZaghnal,
            outerRadius: 90f,
            innerRadius: LindblumArenaNavigationRadius,
            priority: AvoidancePriority.High);
    }

    /// <summary>
    /// Registers Kanilokka's cast geometry and mutually exclusive arena states.
    /// </summary>
    private void RegisterKanilokkaAvoidance()
    {
        // Preserved Souls create thin 28-to-32-yalm bands, but RB's polygon surface cannot reliably
        // retain a polygon hole: the generated donuts behaved like competing circles and caused
        // twelve vulnerability gains in one pull. The priority planner below instead evaluates the
        // radial band directly from immutable actor-position snapshots and one resolving wave at a
        // time; no duplicate Soulweave polygon is registered here.

        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: IsKanilokkaCombat,
            objectSelector: actor => actor.CastingSpellId == EnemyAction.FreeSpiritsAoe,
            locationProducer: actor => actor.Location,
            outerRadius: 20f,
            innerRadius: 15f,
            priority: AvoidancePriority.High);

        // Soul Douse resolves within one concurrent-mechanic window of the second Dark II wave. When
        // its live target already occupies the active gap, the combined semantic stage becomes the
        // sole mover; otherwise these normal cone avoids stay enabled and fail closed.
        AddDarkIICone(
            () => IsKanilokkaCombat() && !darkIISoulDouseManualMovementActive,
            EnemyAction.DarkIIAoe1);
        AddDarkIICone(
            () => IsKanilokkaCombat() && !darkIISoulDouseManualMovementActive && !IsDarkIIFirstWaveActive(),
            EnemyAction.DarkIIAoe2);
        // During any cast overlap the Soulweave stage folds every live Telltale spacing constraint
        // into the same endpoint solve. Suppressing the standalone spread for the complete overlap
        // prevents RB's avoidance mover and the semantic ring mover from fighting over position between snapshots.
        AddTargetedSpread(
            () => IsKanilokkaCombat() && !IsSoulweaveTelltaleTearsOverlapActive(),
            EnemyAction.TelltaleTears,
            5f);
        AddCastCircle(
            () => IsKanilokkaCombat() && !Core.Player.HasAura(PlayerAura.TemporaryMisdirection),
            EnemyAction.Necrohazard,
            18f,
            useCastLocation: false);

        // Kanilokka changes the walkable floor. These mutually exclusive boundaries prevent the
        // original fixed 15-yalm circle from rejecting the initial arena or accepting Phantom Flood.
        // Soulweave's route sampler already enforces the same inset on every segment, so suspend this
        // duplicate leash while it owns movement; live logs showed the generic leash repeatedly
        // replacing the ring waypoint even though both were nominally high-priority avoids.
        AvoidanceHelpers.AddAvoidDonut(
            () => IsKanilokkaCombat() && !kanilokkaStandardBoundsEstablished &&
                  !soulweaveManualMovementActive &&
                  !IsPhantomFloodNavigationActive(DateTime.UtcNow) && !IsNecrohazardWindowActive(DateTime.UtcNow),
            () => ArenaCenter.OverseerKanilokka,
            outerRadius: 90f,
            innerRadius: KanilokkaInitialNavigationRadius,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => IsKanilokkaCombat() && kanilokkaStandardBoundsEstablished &&
                  !soulweaveManualMovementActive &&
                  !IsPhantomFloodNavigationActive(DateTime.UtcNow) && !IsNecrohazardWindowActive(DateTime.UtcNow),
            () => ArenaCenter.OverseerKanilokka,
            outerRadius: 90f,
            innerRadius: KanilokkaSoulweaveNavigationRadius,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => IsKanilokkaArenaScope() && IsPhantomFloodNavigationActive(DateTime.UtcNow),
            () => ArenaCenter.OverseerKanilokka,
            outerRadius: 90f,
            innerRadius: KanilokkaPhantomFloodNavigationRadius,
            priority: AvoidancePriority.High);

        // The Soulweave planner applies this same 4.5-yalm Phantom Flood inset to its destination.
        // Keeping the exterior boundary registered supplies a fail-closed floor guard if forecast
        // capture is incomplete, without reintroducing the removed ring polygons.

        // RB avoidance cannot express a union of winding safe-floor polygons, so the priority planner
        // pathfinds the confirmed layout directly and retains a Trust trail only when the director
        // transition is unavailable. The center circle is suspended during Temporary Misdirection:
        // live logs showed generic avoidance taking movement ownership away from the gated route.
        AvoidanceHelpers.AddAvoidDonut(
            () => IsKanilokkaCombat() && IsNecrohazardWindowActive(DateTime.UtcNow) &&
                  !IsPhantomFloodNavigationActive(DateTime.UtcNow),
            () => ArenaCenter.OverseerKanilokka,
            outerRadius: 90f,
            innerRadius: KanilokkaNecrohazardNavigationRadius,
            priority: AvoidancePriority.High);
    }

    /// <summary>
    /// Registers Lunipyati's cleaves, sequential circles, persistent crater, and arena geometry.
    /// </summary>
    private void RegisterLunipyatiAvoidance()
    {
        // After Crater Carve the semantic planner must route around the missing floor; generic cone
        // avoidance is safe only while the arena remains intact.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => IsLunipyatiCombat() && !craterActive &&
                          !IsRagingClawJaggedEdgeOverlapActive(),
            objectSelector: actor => actor.CastingSpellId == EnemyAction.RagingClawFirst,
            leashPointProducer: () => ArenaCenter.Lunipyati,
            leashRadius: 45f,
            rotationDegrees: 0f,
            radius: 45f,
            arcDegrees: 180f,
            priority: AvoidancePriority.High);

        // Boulder Dance repeats at its two initial helper positions after the cast bar disappears.
        // Immutable timed locations preserve those circles without retaining frame-scoped wrappers.
        AvoidanceManager.AddAvoidLocation(
            canRun: IsLunipyatiCombat,
            radiusProducer: _ => 7.5f,
            locationProducer: location => location,
            collectionProducer: () => boulderDanceForecasts.Values
                .Where(forecast => forecast.ExpiresAtUtc > DateTime.UtcNow)
                .Select(forecast => forecast.Location)
                .ToArray());

        // The 40662 curve batches resolved safely under RB in the 2026-08-25 pull, so retain their
        // existing geometry owner. Action 40661 is deliberately excluded: its 0.3-second activation
        // offsets cannot survive this location-only API and are handled by the time-aware planner.
        // Live 40606 circles remain a conservative fallback when neither predictive visual exists.
        AvoidanceManager.AddAvoidLocation(
            canRun: IsLunipyatiCombat,
            radiusProducer: _ => LeapingEarthImpactAvoidRadius,
            locationProducer: location => location,
            collectionProducer: () => GetReactiveLeapingEarthAvoidHazards(DateTime.UtcNow)
                .Select(hazard => hazard.Location)
                .ToArray());

        // A captured Rock Blast forecast is owned exclusively by the annulus planner. If the first
        // helper was missed, fall back to its live cast circle with the same verified clearance rather
        // than leaving an unknown sequence unhandled; the fallback disables once a forecast is armed.
        AddCastCircle(
            () => IsLunipyatiCombat() && craterActive && !IsRockBlastForecastActive(DateTime.UtcNow),
            EnemyAction.RockBlast,
            RockBlastImpactAvoidRadius,
            useCastLocation: false);

        AddTargetedSpread(
            () => IsLunipyatiCombat() && !IsRagingClawJaggedEdgeOverlapActive(),
            EnemyAction.JaggedEdge,
            JaggedEdgeRadius);
        AddCastCircle(IsLunipyatiCombat, EnemyAction.CraterCarveAoe, LunipyatiCraterAvoidRadius, useCastLocation: true);

        // Beastly Roar is proximity damage, not a 25-yalm lethal circle. Marking that radius inside a
        // 15-yalm arena removes every candidate; the priority planner instead moves to the far edge
        // and retains ownership until its delayed action effect resolves.

        AvoidanceHelpers.AddAvoidDonut(
            IsLunipyatiCraterNavigationActive,
            () => ArenaCenter.Lunipyati,
            outerRadius: LunipyatiCraterAvoidRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => IsLunipyatiCombat() || IsLunipyatiCraterNavigationActive(),
            () => ArenaCenter.Lunipyati,
            outerRadius: 90f,
            innerRadius: LunipyatiArenaNavigationRadius,
            priority: AvoidancePriority.High);
    }

    /// <summary>
    /// Updates scalar forecasts whose hazards outlive the corresponding RebornBuddy cast wrapper.
    /// </summary>
    private void UpdateEncounterForecasts(SubZoneId subZoneId)
    {
        DateTime now = DateTime.UtcNow;
        if (subZoneId == SubZoneId.CrystalQuarry)
        {
            UpdateLindblumForecasts(now);
        }
        else if (subZoneId == SubZoneId.SoulCenter)
        {
            UpdateKanilokkaForecasts(now);
        }
        else if (subZoneId == SubZoneId.TheDustYoke)
        {
            UpdateLunipyatiForecasts(now);
        }
    }

    /// <summary>
    /// Snapshots Lindblum's recycled helper casts and records the director state needed to identify
    /// Cell Shock before its short cast wrapper appears.
    /// </summary>
    private void UpdateLindblumForecasts(DateTime now)
    {
        BattleCharacter[] lineVoltageCasters = GetCasters(EnemyAction.LineVoltage).ToArray();
        HashSet<uint> observedCasterIds = [];
        foreach (BattleCharacter caster in lineVoltageCasters)
        {
            observedCasterIds.Add(caster.ObjectId);
            if (observedLineVoltageCasts.TryGetValue(caster.ObjectId, out uint observedActionId) &&
                observedActionId == caster.CastingSpellId)
            {
                continue;
            }

            DateTime activatesAtUtc = now + caster.SpellCastInfo.RemainingCastTime;
            lineVoltageForecasts.Add(new TimedLineVoltageRectangle(
                caster.ObjectId,
                caster.Location,
                caster.Heading,
                EnemyAction.WideLineVoltage.Contains(caster.CastingSpellId),
                activatesAtUtc,
                activatesAtUtc + LineVoltagePostActivationGrace));
            observedLineVoltageCasts[caster.ObjectId] = caster.CastingSpellId;
        }

        // Removing only the polling key at cast finish permits a recycled actor to enqueue its next
        // cast while the immutable old rectangle survives through bounded action-effect latency.
        foreach (uint objectId in observedLineVoltageCasts.Keys
                     .Where(objectId => !observedCasterIds.Contains(objectId))
                     .ToArray())
        {
            observedLineVoltageCasts.Remove(objectId);
        }

        lineVoltageForecasts.RemoveAll(forecast => forecast.ExpiresAtUtc <= now);
        UpdateCellShockForecastLifecycle(now);
        UpdateCaberTossMapEffectCapture(now);
    }

    /// <summary>
    /// Returns only the earliest resolving Line Voltage cohort. Later rectangles stay queued as
    /// forecast data and cannot make RebornBuddy solve multiple sequential Caber Toss walls at once.
    /// A confirmed earlier Cell Shock suppresses this reactive owner because its encounter-local
    /// planner already incorporates the following lane cohort into one stable destination. Once the
    /// union solver has a verified point and route, it also suppresses these individual rectangles so
    /// their equal-priority escape vectors cannot fight the selected cohort destination.
    /// </summary>
    private TimedLineVoltageRectangle[] GetActiveLineVoltageForecasts()
    {
        DateTime now = DateTime.UtcNow;
        TimedLineVoltageRectangle[] wave = GetEarliestLineVoltageForecasts(now);
        return DoesCellShockResolveBeforeLineVoltage(wave, now) || lineVoltageManualMovementActive
            ? []
            : wave;
    }

    /// <summary>
    /// Selects the earliest activation cohort without applying Cell Shock ownership. Keeping this
    /// primitive independent lets both reactive registration and the combined planner compare the
    /// same immutable lane wave without recursive collection calls.
    /// </summary>
    private TimedLineVoltageRectangle[] GetEarliestLineVoltageForecasts(DateTime now)
    {
        TimedLineVoltageRectangle[] pending = lineVoltageForecasts
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .OrderBy(forecast => forecast.ActivatesAtUtc)
            .ThenBy(forecast => forecast.CasterObjectId)
            .ToArray();
        if (pending.Length == 0)
        {
            return [];
        }

        DateTime waveCutoff = pending[0].ActivatesAtUtc + LineVoltageConcurrentWaveWindow;
        return pending
            .TakeWhile(forecast => forecast.ActivatesAtUtc < waveCutoff)
            .ToArray();
    }

    /// <summary>
    /// Returns a confirmed Cell Shock quadrant only while reactive avoidance remains the fallback.
    /// Earlier Line Voltage cohorts resolve first and hide the future circle; once the combined planner
    /// has a safe destination, it suppresses this duplicate mover and owns the overlap exclusively.
    /// </summary>
    private Vector3[] GetActiveCellShockForecastLocations()
    {
        DateTime now = DateTime.UtcNow;
        TimedLineVoltageRectangle[] lineVoltageWave = GetEarliestLineVoltageForecasts(now);
        if (!IsCellShockForecastActive(now) ||
            !DoesCellShockResolveBeforeLineVoltage(lineVoltageWave, now) ||
            cellShockManualMovementActive)
        {
            return [];
        }

        return [cellShockForecast.Location];
    }

    /// <summary>
    /// Refines the map-effect estimate from the live helper and retains predicted Cell Shock geometry
    /// through the delayed action-effect snapshot. RB removed the first helper wrapper roughly half a
    /// second before its vulnerability gain was observed, so cast disappearance cannot clear safety.
    /// The forecast deliberately survives the selector's earlier map-effect reset.
    /// </summary>
    private void UpdateCellShockForecastLifecycle(DateTime now)
    {
        BattleCharacter caster = GetFirstCaster(EnemyAction.CellShock);
        if (caster != null && cellShockForecast != null)
        {
            float correctionDistanceSquared = DistanceSquared2D(
                cellShockForecast.Location,
                caster.Location);
            if (correctionDistanceSquared >
                CellShockLiveCorrectionTolerance * CellShockLiveCorrectionTolerance)
            {
                Vector3 warningLocation = cellShockForecast.Location;
                cellShockForecast = new TimedCircle(
                    caster.Location,
                    cellShockForecast.ExpiresAtUtc);
                cellShockLineVoltageDestination = null;
                cellShockManualMovementActive = false;

                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    Logger.Information(
                        $"[MechanicDiag] LINDBLUM_CELL_SHOCK_FORECAST_CORRECTED " +
                        $"warningLocation={warningLocation} helperLocation={caster.Location} " +
                        $"difference={MathF.Sqrt(correctionDistanceSquared):F2}.");
                }
            }

            DateTime liveResolution = now + caster.SpellCastInfo.RemainingCastTime;
            cellShockResolvesAtUtc = Max(cellShockResolvesAtUtc, liveResolution);
            cellShockUntilUtc = Max(cellShockUntilUtc, liveResolution + ResolutionGrace);
        }

        if (cellShockForecast != null &&
            (cellShockForecast.ExpiresAtUtc <= now || cellShockUntilUtc <= now))
        {
            cellShockForecast = null;
            cellShockResolvesAtUtc = DateTime.MinValue;
            cellShockUntilUtc = DateTime.MinValue;
            cellShockLineVoltageDestination = null;
            cellShockManualMovementActive = false;
        }

    }

    /// <summary>
    /// Reports whether a confirmed, unexpired paired warning owns the live fallback geometry.
    /// </summary>
    private bool IsCellShockForecastActive(DateTime now) =>
        cellShockForecast != null &&
        cellShockForecast.ExpiresAtUtc > now &&
        cellShockUntilUtc > now;

    /// <summary>
    /// Tracks Caber Toss map effects and arms the early Cell Shock forecast. RB exposes stable map
    /// records without the packet indices used by the encounter, so the paired record transition is
    /// retained until the live helper can confirm or correct the forecast.
    /// </summary>
    private void UpdateCaberTossMapEffectCapture(DateTime now)
    {
        BattleCharacter caberToss = GetFirstCaster(EnemyAction.CaberToss);
        if (caberToss != null)
        {
            caberTossMapEffectCaptureUntilUtc = Max(
                caberTossMapEffectCaptureUntilUtc,
                now + caberToss.SpellCastInfo.RemainingCastTime + CaberTossMapEffectCaptureGrace);
            if (!caberTossWasCasting)
            {
                lastCaberTossMapEffectsFingerprint = string.Empty;
                observedLindblumMapEffectStates.Clear();
                cellShockForecast = null;
                cellShockResolvesAtUtc = DateTime.MinValue;
                cellShockUntilUtc = DateTime.MinValue;
                cellShockLineVoltageDestination = null;
                cellShockManualMovementActive = false;
                pendingCellShockSelector = null;
            }
        }

        caberTossWasCasting = caberToss != null;
        if (now >= caberTossMapEffectCaptureUntilUtc)
        {
            return;
        }

        InstanceContentDirector instanceDirector = DirectorManager.ActiveDirector as InstanceContentDirector;
        MapEffect[] mapEffects = instanceDirector != null && instanceDirector.IsValid
            ? instanceDirector.MapEffects
            : [];
        UpdateConfirmedCellShockForecast(now, mapEffects);

        if (!LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            return;
        }

        string fingerprint = FormatLindblumMapEffects(mapEffects);
        if (fingerprint == lastCaberTossMapEffectsFingerprint)
        {
            return;
        }

        lastCaberTossMapEffectsFingerprint = fingerprint;
        Logger.Information(
            $"[MechanicDiag] LINDBLUM_MAP_EFFECTS caberCasting={caberToss != null} " +
            $"captureRemainingMs={Math.Max(0, (int)(caberTossMapEffectCaptureUntilUtc - now).TotalMilliseconds)} " +
            $"count={mapEffects.Length} effects=[{fingerprint}].");
    }

    /// <summary>
    /// Pairs a confirmed quadrant signal with its later warning transition. When the first Caber Toss
    /// poll occurs after the quadrant transition, exactly one already-active confirmed selector may
    /// restore that missed signal; the paired warning remains mandatory before geometry is published.
    /// Unknown or ambiguous active records remain fail-closed.
    /// </summary>
    private void UpdateConfirmedCellShockForecast(DateTime now, IReadOnlyCollection<MapEffect> mapEffects)
    {
        if (observedLindblumMapEffectStates.Count == 0)
        {
            var activeSelectors = mapEffects
                .Where(effect => effect.State == CellShockActiveMapEffectState)
                .Select(effect => new
                {
                    Effect = effect,
                    Selector = CellShockSelectors.FirstOrDefault(candidate =>
                        candidate.QuadrantMapEffectId == effect.ID),
                })
                .Where(candidate => candidate.Selector != null)
                .ToArray();

            if (activeSelectors.Length == 1)
            {
                MapEffect effect = activeSelectors[0].Effect;
                pendingCellShockSelector = activeSelectors[0].Selector;
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    Logger.Information(
                        $"[MechanicDiag] LINDBLUM_CELL_SHOCK_QUADRANT " +
                        $"id=0x{effect.ID:X8} initialSnapshot=True state=0x{effect.State:X4} " +
                        $"flags=0x{effect.Flags:X2} " +
                        $"directLocation={pendingCellShockSelector.DirectLocation} " +
                        $"mirroredLocation={pendingCellShockSelector.MirroredLocation}; " +
                        $"location awaits paired warning state.");
                }
            }
            else if (activeSelectors.Length > 1 && LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                Logger.Information(
                    $"[MechanicDiag] LINDBLUM_CELL_SHOCK_QUADRANT_AMBIGUOUS " +
                    $"initialSnapshot=True count={activeSelectors.Length}; forecast remains disabled.");
            }
        }

        foreach (MapEffect effect in mapEffects)
        {
            bool hadPreviousState = observedLindblumMapEffectStates.TryGetValue(effect.ID, out ushort previousState);
            observedLindblumMapEffectStates[effect.ID] = effect.State;

            if (!hadPreviousState || previousState == effect.State)
            {
                continue;
            }

            CellShockSelector selector = CellShockSelectors.FirstOrDefault(candidate =>
                candidate.QuadrantMapEffectId == effect.ID);
            if (selector != null && effect.State == CellShockActiveMapEffectState)
            {
                pendingCellShockSelector = selector;
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    Logger.Information(
                        $"[MechanicDiag] LINDBLUM_CELL_SHOCK_QUADRANT " +
                        $"id=0x{effect.ID:X8} previousState=0x{previousState:X4} " +
                        $"state=0x{effect.State:X4} flags=0x{effect.Flags:X2} " +
                        $"directLocation={selector.DirectLocation} " +
                        $"mirroredLocation={selector.MirroredLocation}; " +
                        $"location awaits paired warning state.");
                }

                continue;
            }

            if (pendingCellShockSelector == null ||
                effect.ID != pendingCellShockSelector.WarningMapEffectId ||
                !pendingCellShockSelector.TryGetWarningLocation(
                    effect.State,
                    out Vector3 forecastLocation))
            {
                continue;
            }

            cellShockForecast = new TimedCircle(
                forecastLocation,
                now + CellShockForecastLifetime);
            cellShockResolvesAtUtc = now + CellShockWarningLead;
            cellShockUntilUtc = cellShockResolvesAtUtc + ResolutionGrace;
            cellShockLineVoltageDestination = null;
            cellShockManualMovementActive = false;

            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                Logger.Information(
                    $"[MechanicDiag] LINDBLUM_CELL_SHOCK_FORECAST " +
                    $"id=0x{effect.ID:X8} previousState=0x{previousState:X4} state=0x{effect.State:X4} " +
                    $"flags=0x{effect.Flags:X2} location={forecastLocation} " +
                    $"mapping={(effect.State == CellShockDirectWarningState ? "direct" : "mirrored")} " +
                    $"resolvesInMs={(int)CellShockWarningLead.TotalMilliseconds} " +
                    $"expiresInMs={(int)CellShockForecastLifetime.TotalMilliseconds}.");
            }

            break;
        }
    }

    /// <summary>
    /// Formats public RB map-effect fields in stable order without reading packet memory or retaining
    /// director wrappers beyond the current bot frame.
    /// </summary>
    private static string FormatLindblumMapEffects(IEnumerable<MapEffect> mapEffects) =>
        string.Join("; ", mapEffects
            .OrderBy(effect => effect.ID)
            .ThenBy(effect => effect.State)
            .Select(effect => FormattableString.Invariant(
                $"id=0x{effect.ID:X8} state=0x{effect.State:X4} flags=0x{effect.Flags:X2} unk=0x{effect.unk:X8}")));

    /// <summary>
    /// Retains Kanilokka's floor-state transitions through short gaps between helper casts.
    /// </summary>
    private void UpdateKanilokkaForecasts(DateTime now)
    {
        BattleCharacter lostHope = GetFirstCaster(EnemyAction.LostHope);
        if (lostHope != null && !lostHopeWasCasting)
        {
            // Snapshot a fresh baseline for each Lost Hope. Requiring a later state transition keeps
            // a stale director record from selecting the wrong mutually exclusive floor layout.
            observedKanilokkaMapEffectStates.Clear();
            necrohazardFloorLayout = NecrohazardFloorLayout.None;
            necrohazardMapEffectId = 0;
            ClearExactNecrohazardRoute();
            ClearNecrohazardTrail();
        }

        // The initial 20-yalm floor changes to the normal 15-yalm combat floor at Free Spirits. The
        // director reset detected below restores the full arena after a Necrohazard layout ends.
        if (GetFirstCaster(EnemyAction.StandardBoundsTransition) != null)
        {
            kanilokkaStandardBoundsEstablished = true;
        }

        BattleCharacter[] soulweaveCasters = GetCasters(EnemyAction.Soulweave).ToArray();
        HashSet<uint> observedSoulweaveCasterIds = [];
        bool unresolvedSoulweaveOrigin = false;
        ProcessPendingSoulweaveFinishes(now);
        foreach (BattleCharacter caster in soulweaveCasters)
        {
            observedSoulweaveCasterIds.Add(caster.ObjectId);
            DateTime activatesAt = now + caster.SpellCastInfo.RemainingCastTime +
                                   SoulweaveNpcCastFinishDelay;
            if (observedSoulweaveCasts.TryGetValue(
                    caster.ObjectId,
                    out SoulweaveCastObservation observation) &&
                observation.ActionId == caster.CastingSpellId &&
                activatesAt <= observation.ActivatesAtUtc + SoulweaveCastGenerationTolerance)
            {
                continue;
            }

            if (observation != null)
            {
                // A recycled helper can begin its next cast between two polling frames without ever
                // appearing absent. Advance one FIFO record at the delayed NPC finish so polling
                // preserves the encounter's cast-finish boundary.
                observedSoulweaveCasts.Remove(caster.ObjectId);
                QueueSoulweaveCastFinish(observation, now);
            }

            Vector3 omenOrigin = caster.OmenMatrix.Center;
            if (!YuweyawataSoulweaveGeometry.TryResolveRingOrigin(
                    caster.Location,
                    caster.Heading,
                    omenOrigin,
                    out Vector3 ringOrigin))
            {
                unresolvedSoulweaveOrigin = true;
                if (!soulweaveOriginFailureReported)
                {
                    Logger.Warning(
                        $"[Yuweyawata] Could not resolve Soulweave origin for caster " +
                        $"0x{caster.ObjectId:X8}; the cast will be retried on the next bot pulse.");
                    soulweaveOriginFailureReported = true;
                }

                continue;
            }

            soulweaveForecasts.Add(new TimedSoulweaveRing(
                caster.ObjectId,
                ringOrigin,
                activatesAt,
                activatesAt + SoulweaveCastFinishFailsafe));
            observedSoulweaveCasts[caster.ObjectId] = new SoulweaveCastObservation(
                caster.CastingSpellId,
                activatesAt);

        }

        if (!unresolvedSoulweaveOrigin)
        {
            soulweaveOriginFailureReported = false;
        }

        // Soulweave advances one FIFO entry per cast finish, not by actor identity. RB exposes that
        // boundary as a live wrapper disappearing, so process missing wrappers in projected order.
        foreach (uint objectId in observedSoulweaveCasts
                     .Where(pair => !observedSoulweaveCasterIds.Contains(pair.Key))
                     .OrderBy(pair => pair.Value.ActivatesAtUtc)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            SoulweaveCastObservation finished = observedSoulweaveCasts[objectId];
            observedSoulweaveCasts.Remove(objectId);
            QueueSoulweaveCastFinish(finished, now);
        }

        // This only protects against a permanently stale wrapper-generation map. The ordinary path
        // above advances on cast finish and therefore does not retain rings for a fixed damage grace.
        int removedStaleSoulweaves = soulweaveForecasts.RemoveAll(forecast => forecast.ExpiresAtUtc <= now);
        if (removedStaleSoulweaves > 0)
        {
            activeSoulweavePlan = null;
        }

        BattleCharacter phantomFlood = GetFirstCaster(EnemyAction.PhantomFlood);
        if (phantomFlood != null)
        {
            DateTime resolvesAt = now + phantomFlood.SpellCastInfo.RemainingCastTime;
            phantomFloodResolvesAtUtc = Max(phantomFloodResolvesAtUtc, resolvesAt);
            phantomFloodUntilUtc = Max(
                phantomFloodUntilUtc,
                resolvesAt + PhantomFloodPersistenceWindow);
            phantomFloodMapEffectCaptureUntilUtc = Max(
                phantomFloodMapEffectCaptureUntilUtc,
                resolvesAt + PhantomFloodMapEffectCaptureGrace);
        }

        UpdatePhantomFloodMapEffects(now);

        BattleCharacter necrohazard = GetFirstCaster(EnemyAction.NecrohazardSequence);
        if (necrohazard != null)
        {
            necrohazardUntilUtc = Max(
                necrohazardUntilUtc,
                now + necrohazard.SpellCastInfo.RemainingCastTime + ResolutionGrace);
        }

        if (Core.Player.HasAura(PlayerAura.TemporaryMisdirection))
        {
            necrohazardUntilUtc = Max(necrohazardUntilUtc, now + TimeSpan.FromSeconds(3));
        }
        else
        {
            ResetMisdirectionInputGate();
        }

        UpdateKanilokkaMapEffects(
            lostHope != null ||
            now < necrohazardUntilUtc ||
            Core.Player.HasAura(PlayerAura.TemporaryMisdirection) ||
            necrohazardFloorLayout != NecrohazardFloorLayout.None);
        lostHopeWasCasting = lostHope != null;
    }

    /// <summary>
    /// Defers an early RB wrapper disappearance until the delayed NPC finish, or advances immediately
    /// when that finish has already passed.
    /// </summary>
    /// <param name="observation">Scalar activation boundary captured from the completed wrapper.</param>
    /// <param name="now">Current UTC scheduler time.</param>
    private void QueueSoulweaveCastFinish(
        SoulweaveCastObservation observation,
        DateTime now)
    {
        if (observation.ActivatesAtUtc <= now)
        {
            RemoveEarliestSoulweaveForecast();
            return;
        }

        pendingSoulweaveFinishes.Add(new TimedSoulweaveFinish(observation.ActivatesAtUtc));
    }

    /// <summary>
    /// Applies deferred finish boundaries in chronological order so several wrappers disappearing
    /// during one slow bot frame still remove exactly the same FIFO sequence as cast-finish events.
    /// </summary>
    /// <param name="now">Current UTC scheduler time.</param>
    private void ProcessPendingSoulweaveFinishes(DateTime now)
    {
        foreach (TimedSoulweaveFinish finish in pendingSoulweaveFinishes
                     .Where(candidate => candidate.FinishesAtUtc <= now)
                     .OrderBy(candidate => candidate.FinishesAtUtc)
                     .ToArray())
        {
            pendingSoulweaveFinishes.Remove(finish);
            RemoveEarliestSoulweaveForecast();
        }
    }

    /// <summary>
    /// Advances Soulweave by cast-finish order. Actor identity is deliberately ignored because
    /// recycled Preserved Souls do not remain paired with queued AOE records.
    /// </summary>
    private void RemoveEarliestSoulweaveForecast()
    {
        TimedSoulweaveRing earliest = soulweaveForecasts
            .OrderBy(forecast => forecast.ActivatesAtUtc)
            .ThenBy(forecast => forecast.CasterObjectId)
            .FirstOrDefault();
        if (earliest == null)
        {
            return;
        }

        soulweaveForecasts.Remove(earliest);
        // Rebuild after every finish so the next queued ring becomes the active FIFO prefix.
        activeSoulweavePlan = null;
    }

    /// <summary>
    /// Latches Phantom Flood from its own boss-scoped 0x0010 map transition and holds the five-yalm
    /// floor until that exact record changes again. This state is intentionally independent of the
    /// protected Necrohazard map-effect baseline, selection, timing, and cleanup.
    /// </summary>
    /// <param name="now">Current UTC scheduler time used to bound transition capture to the cast.</param>
    private void UpdatePhantomFloodMapEffects(DateTime now)
    {
        InstanceContentDirector instanceDirector = DirectorManager.ActiveDirector as InstanceContentDirector;
        MapEffect[] mapEffects = instanceDirector != null && instanceDirector.IsValid
            ? instanceDirector.MapEffects
            : [];
        bool captureActive = now <= phantomFloodMapEffectCaptureUntilUtc;
        bool floorWasActive = phantomFloodFloorActive;
        List<(MapEffect Effect, ushort PreviousState)> candidates = [];

        foreach (MapEffect effect in mapEffects)
        {
            bool hadPreviousState = observedPhantomFloodMapEffectStates.TryGetValue(
                effect.ID,
                out ushort previousState);
            observedPhantomFloodMapEffectStates[effect.ID] = effect.State;
            if (!hadPreviousState || previousState == effect.State)
            {
                continue;
            }

            if (floorWasActive && effect.ID == phantomFloodMapEffectId &&
                effect.State != PhantomFloodActiveMapState)
            {
                Logger.Information(
                    $"[Yuweyawata] Phantom Flood floor ended " +
                    $"(mapEffect=0x{effect.ID:X8}, state=0x{previousState:X4}->0x{effect.State:X4}).");
                phantomFloodFloorActive = false;
                phantomFloodMapEffectId = 0;
                if (activeSoulweavePlan != null)
                {
                    activeSoulweavePlan.Destination = null;
                }
                continue;
            }

            if (!floorWasActive && captureActive && effect.State == PhantomFloodActiveMapState)
            {
                candidates.Add((effect, previousState));
            }
        }

        if (floorWasActive || candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count != 1)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                Logger.Warning(
                    $"[MechanicDiag] KANILOKKA_PHANTOM_FLOOD_MAP_AMBIGUOUS candidates=" +
                    string.Join(", ", candidates.Select(candidate =>
                        $"id=0x{candidate.Effect.ID:X8} previous=0x{candidate.PreviousState:X4} " +
                        $"state=0x{candidate.Effect.State:X4} flags=0x{candidate.Effect.Flags:X2}")));
            }

            return;
        }

        (MapEffect selectedEffect, ushort selectedPreviousState) = candidates[0];
        phantomFloodFloorActive = true;
        phantomFloodMapEffectId = selectedEffect.ID;
        if (activeSoulweavePlan != null)
        {
            activeSoulweavePlan.Destination = null;
        }
        Logger.Information(
            $"[Yuweyawata] Phantom Flood five-yalm floor latched " +
            $"(mapEffect=0x{selectedEffect.ID:X8}, state=0x{selectedPreviousState:X4}->" +
            $"0x{selectedEffect.State:X4}, flags=0x{selectedEffect.Flags:X2}).");
    }

    /// <summary>
    /// Selects one exact Necrohazard layout from a scoped RB map-effect transition. RB exposes the
    /// packet state's low word but not its index, so this requires exactly one candidate after Lost
    /// Hope; ambiguity retains the live Trust route instead of merging mutually exclusive floors.
    /// </summary>
    private void UpdateKanilokkaMapEffects(bool captureActive)
    {
        if (!captureActive)
        {
            return;
        }

        InstanceContentDirector instanceDirector = DirectorManager.ActiveDirector as InstanceContentDirector;
        MapEffect[] mapEffects = instanceDirector != null && instanceDirector.IsValid
            ? instanceDirector.MapEffects
            : [];
        List<(MapEffect Effect, ushort PreviousState, NecrohazardFloorLayout Layout)> candidates = [];

        foreach (MapEffect effect in mapEffects)
        {
            bool hadPreviousState = observedKanilokkaMapEffectStates.TryGetValue(
                effect.ID,
                out ushort previousState);
            observedKanilokkaMapEffectStates[effect.ID] = effect.State;
            if (!hadPreviousState || previousState == effect.State)
            {
                continue;
            }

            if (necrohazardFloorLayout != NecrohazardFloorLayout.None &&
                effect.ID == necrohazardMapEffectId &&
                effect.State == KanilokkaArenaResetMapState)
            {
                Logger.Information(
                    $"[Yuweyawata] Necrohazard floor reset from {necrohazardFloorLayout} " +
                    $"(mapEffect=0x{effect.ID:X8}).");
                necrohazardFloorLayout = NecrohazardFloorLayout.None;
                necrohazardMapEffectId = 0;
                // The director reset is a stronger lifecycle signal than the conservative cast/aura
                // timeout. Ending the window here prevents the old implementation from following a
                // Trust back toward center for several seconds after the dangerous floor was gone.
                necrohazardUntilUtc = DateTime.MinValue;
                kanilokkaStandardBoundsEstablished = false;
                ResetMisdirectionInputGate();
                ClearNecrohazardTrail();
                ClearExactNecrohazardRoute();
                continue;
            }

            if (necrohazardFloorLayout != NecrohazardFloorLayout.None)
            {
                continue;
            }

            NecrohazardFloorLayout layout = effect.State switch
            {
                NecrohazardFourRoutesMapState => NecrohazardFloorLayout.FourRoutes,
                NecrohazardThreeRoutesMapState => NecrohazardFloorLayout.ThreeRoutes,
                _ => NecrohazardFloorLayout.None,
            };
            if (layout != NecrohazardFloorLayout.None)
            {
                candidates.Add((effect, previousState, layout));
            }
        }

        if (candidates.Count != 1)
        {
            if (candidates.Count > 1 && LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                Logger.Warning(
                    $"[MechanicDiag] KANILOKKA_NECROHAZARD_LAYOUT_AMBIGUOUS candidates=" +
                    string.Join(", ", candidates.Select(candidate =>
                        $"id=0x{candidate.Effect.ID:X8} previous=0x{candidate.PreviousState:X4} " +
                        $"state=0x{candidate.Effect.State:X4} flags=0x{candidate.Effect.Flags:X2}")));
            }

            return;
        }

        (MapEffect selectedEffect, ushort selectedPreviousState, NecrohazardFloorLayout selectedLayout) =
            candidates[0];
        necrohazardFloorLayout = selectedLayout;
        necrohazardMapEffectId = selectedEffect.ID;
        ClearExactNecrohazardRoute();
        Logger.Information(
            $"[Yuweyawata] Selected {selectedLayout} Necrohazard floor " +
            $"(mapEffect=0x{selectedEffect.ID:X8}, state=0x{selectedPreviousState:X4}->" +
            $"0x{selectedEffect.State:X4}, flags=0x{selectedEffect.Flags:X2}).");
    }

    /// <summary>
    /// Publishes one activation-aware Soulweave movement stage from the current FIFO prefix.
    /// Later queued cohorts remain non-risky until a cast finish advances them, while every live
    /// Telltale Tears spacing constraint is folded into this stage's sole movement destination.
    /// </summary>
    private bool TryGetSoulweaveStage(DateTime now, out MechanicStage stage)
    {
        stage = null;
        TimedSoulweaveRing[] wave = GetActiveSoulweaveWave(now);
        if (wave.Length == 0)
        {
            soulweaveSolveFailureReported = false;
            soulweaveRouteFailureReported = false;
            soulweaveManualMovementActive = false;
            return false;
        }

        Vector3[] ringCenters = wave.Select(forecast => forecast.Origin).ToArray();
        BattleCharacter[] concurrentTelltaleTears = GetConcurrentTelltaleTearsCasters(wave);
        // Target wrappers are current-frame data. The combined stage remains the one movement owner
        // while an ID is temporarily unresolved, then invalidates its destination as soon as that
        // target becomes available on the next pulse.
        bool spreadTargetsResolved = TryGetTelltaleTearsConstraintLocations(
            concurrentTelltaleTears,
            out Vector3[] spreadTargets);

        float arenaRadius = ShouldSoulweaveUsePhantomFloodBounds(wave, now)
            ? KanilokkaPhantomFloodNavigationRadius
            : KanilokkaSoulweavePlannerRadius;

        Vector3 current = Core.Player.Location;
        // Current occupancy uses the authored 28-to-32 band and five-yalm spread radius. Endpoint
        // selection below retains its half-yalm cushion, but that cushion must not classify an
        // actually safe start as hazardous and enable an unsafe crossing.
        bool currentIsSafe = YuweyawataSoulweaveGeometry.IsOutsideActualHazards(
            current,
            ArenaCenter.OverseerKanilokka,
            arenaRadius,
            ringCenters,
            spreadTargets);
        bool latchedDestinationIsSafe = activeSoulweavePlan?.Destination is Vector3 latched &&
            YuweyawataSoulweaveGeometry.IsSafe(
                latched,
                ArenaCenter.OverseerKanilokka,
                arenaRadius,
                ringCenters,
                spreadTargets);
        if (!latchedDestinationIsSafe)
        {
            // At a cohort handoff the player is often still travelling to the previous destination.
            // Retain that destination when it also clears the new thin bands; choosing the currently
            // safe in-transit point first repeatedly stopped progress and forced another late escape.
            if (priorityMovement.Kind == MechanicKind.Soulweave &&
                YuweyawataSoulweaveGeometry.IsSafe(
                    priorityMovement.Destination,
                    ArenaCenter.OverseerKanilokka,
                    arenaRadius,
                    ringCenters,
                    spreadTargets))
            {
                activeSoulweavePlan.Destination = priorityMovement.Destination;
            }
            else if (YuweyawataSoulweaveGeometry.TryFindDestination(
                         ArenaCenter.OverseerKanilokka,
                         arenaRadius,
                         current,
                         ringCenters,
                         spreadTargets,
                         out Vector3 destination,
                         out _))
            {
                activeSoulweavePlan.Destination = destination;
            }
            else
            {
                if (!soulweaveSolveFailureReported)
                {
                    soulweaveSolveFailureReported = true;
                    Logger.Warning(
                        "[Yuweyawata] No shared Soulweave destination satisfies the active floor and " +
                        "concurrent spread constraints; leaving ordinary emergency avoidance enabled.");
                }

                soulweaveManualMovementActive = false;
                return false;
            }
        }

        soulweaveSolveFailureReported = false;
        bool usedDutySupportSafeAnchor = false;
        bool continuedPreviousEgress = false;
        uint dutySupportSafeAnchorObjectId = 0;
        if (!YuweyawataSoulweaveGeometry.TryFindRouteWaypoint(
                ArenaCenter.OverseerKanilokka,
                arenaRadius,
                current,
                activeSoulweavePlan.Destination.Value,
                ringCenters,
                spreadTargets,
                out Vector3 waypoint))
        {
            // A mover can consume a short egress between behavior pulses. Retain the previous scalar
            // waypoint only after revalidating it against the complete current cohort.
            if (!currentIsSafe &&
                priorityMovement.Owned &&
                priorityMovement.Kind == MechanicKind.Soulweave &&
                YuweyawataSoulweaveGeometry.TryContinueImprovingEscapeWaypoint(
                    ArenaCenter.OverseerKanilokka,
                    arenaRadius,
                    current,
                    priorityMovement.Destination,
                    ringCenters,
                    spreadTargets,
                    out waypoint))
            {
                continuedPreviousEgress = true;
                soulweaveRouteFailureReported = false;
            }
            // Duty Support has already selected a point that survives the current mechanic. Treat
            // that scalar location as another destination candidate, never as a moving actor to
            // chase. Route validation remains mandatory so a safe endpoint cannot authorize a
            // straight chord through one of the thin rings.
            else if (TryGetSoulweaveDutySupportSafeAnchor(
                    current,
                    arenaRadius,
                    ringCenters,
                    spreadTargets,
                    concurrentTelltaleTears,
                    out Vector3 dutySupportSafeAnchor,
                    out dutySupportSafeAnchorObjectId,
                    out waypoint))
            {
                activeSoulweavePlan.Destination = dutySupportSafeAnchor;
                usedDutySupportSafeAnchor = true;
                soulweaveRouteFailureReported = false;
                Logger.Information(
                    $"[Yuweyawata] Soulweave route recovered through snapshotted duty-support " +
                    $"safe point actor=0x{dutySupportSafeAnchorObjectId:X8} " +
                    $"destination={dutySupportSafeAnchor} waypoint={waypoint}.");
            }
            // Never replace a failed route with a direct chord. Hold a currently valid point and
            // retry as the FIFO prefix changes; a genuine unsafe start must produce the bounded
            // improving egress in YuweyawataSoulweaveGeometry or fail closed.
            else
            {
                if (!currentIsSafe)
                {
                    soulweaveManualMovementActive = false;
                    if (!soulweaveRouteFailureReported)
                    {
                        soulweaveRouteFailureReported = true;
                        Logger.Warning(
                            "[Yuweyawata] The selected Soulweave point has no fully safe route from the " +
                            "current position; leaving ordinary emergency avoidance enabled.");
                    }

                    return false;
                }

                waypoint = current;
                if (!soulweaveRouteFailureReported)
                {
                    soulweaveRouteFailureReported = true;
                    Logger.Warning(
                        "[Yuweyawata] The current Soulweave destination is temporarily disconnected; " +
                        "holding the current safe point until a segment-safe route opens.");
                }
            }
        }
        else
        {
            soulweaveRouteFailureReported = false;
        }

        DateTime resolvesAt = activeSoulweavePlan.FirstActivationUtc;
        DateTime activeUntil = activeSoulweavePlan.ActiveUntilUtc;
        soulweaveManualMovementActive = true;
        string overlap = concurrentTelltaleTears.Length > 0 && spreadTargetsResolved
            ? " and simultaneous Telltale Tears spreads"
            : string.Empty;
        string movementReason = usedDutySupportSafeAnchor
            ? $"routing to a snapshotted duty-support safe point " +
              $"(actor=0x{dutySupportSafeAnchorObjectId:X8}){overlap}"
            : continuedPreviousEgress
                ? $"continuing a revalidated Soulweave egress waypoint across a transient solve miss{overlap}"
            : currentIsSafe
                ? $"holding or routing from a point clear of the current Soulweave FIFO prefix{overlap}"
                : $"taking a bounded improving egress to an activation-aware Soulweave point{overlap}";
        // Helpers are recycled between cohorts, so key the lease to the mechanic rather than an
        // actor that can disappear before the ring resolves.
        stage = new MechanicStage(
            MechanicKind.Soulweave,
            resolvesAt,
            activeUntil,
            MovementPriority.LethalGeometry,
            waypoint,
            0,
            movementReason);
        return true;
    }

    /// <summary>
    /// Uses a living Duty Support member's current position as a snapshotted Soulweave destination
    /// when the normal sampled destination belongs to a disconnected safe region.
    /// </summary>
    /// <remarks>
    /// The actor itself is deliberately not retained or followed. Every candidate must satisfy the
    /// active FIFO ring, arena, and Telltale constraints and must expose a completely validated route
    /// from the player. When the local player owns Telltale Tears, standing on any party member is
    /// semantically unsafe, so this exact-location fallback remains disabled for that overlap.
    /// </remarks>
    /// <param name="current">Current player position.</param>
    /// <param name="arenaRadius">Current player-center arena inset.</param>
    /// <param name="ringCenters">Ring origins in the current FIFO prefix.</param>
    /// <param name="spreadTargets">Current party positions excluded by the local player's Telltale Tears role.</param>
    /// <param name="telltaleTearsCasters">Current Telltale Tears wrappers used only to identify a player-owned spread.</param>
    /// <param name="safeAnchor">Snapshotted Duty Support position selected as the safe destination.</param>
    /// <param name="safeAnchorObjectId">Diagnostic identity of the Duty Support member that supplied the position.</param>
    /// <param name="waypoint">Completely route-validated movement waypoint toward the safe anchor.</param>
    /// <returns><see langword="true"/> when a safe, reachable Duty Support position exists.</returns>
    private static bool TryGetSoulweaveDutySupportSafeAnchor(
        Vector3 current,
        float arenaRadius,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        IReadOnlyCollection<BattleCharacter> telltaleTearsCasters,
        out Vector3 safeAnchor,
        out uint safeAnchorObjectId,
        out Vector3 waypoint)
    {
        safeAnchor = default;
        safeAnchorObjectId = 0;
        waypoint = default;

        bool playerOwnsSpread = telltaleTearsCasters.Any(caster =>
            caster.SpellCastInfo.IsValid && caster.SpellCastInfo.TargetId == Core.Player.ObjectId);
        if (playerOwnsSpread)
        {
            return false;
        }

        BattleCharacter[] dutySupportMembers = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member => member != null && member.IsValid && member.IsAlive && !member.IsMe &&
                             member.IsNpc &&
                             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)member.NpcId))
            .OrderBy(member => DistanceSquared2D(current, member.Location))
            .ThenBy(member => member.ObjectId)
            .ToArray();

        foreach (BattleCharacter member in dutySupportMembers)
        {
            // Copy the position before route evaluation. Later pulses revalidate the scalar point
            // against the new FIFO prefix rather than turning this fallback into NPC following.
            Vector3 candidate = member.Location;
            if (!YuweyawataSoulweaveGeometry.IsSafe(
                    candidate,
                    ArenaCenter.OverseerKanilokka,
                    arenaRadius,
                    ringCenters,
                    spreadTargets) ||
                !YuweyawataSoulweaveGeometry.TryFindRouteWaypoint(
                    ArenaCenter.OverseerKanilokka,
                    arenaRadius,
                    current,
                    candidate,
                    ringCenters,
                    spreadTargets,
                    out waypoint))
            {
                continue;
            }

            safeAnchor = candidate;
            safeAnchorObjectId = member.ObjectId;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the current risky FIFO prefix: the earliest queued ring plus later records whose
    /// activation is less than 1.3 seconds after it. Cast-finish polling clears the plan and removes
    /// one record before this method runs again, allowing the prefix to slide immediately without a
    /// post-effect grace or a union with the following sequential cohort.
    /// </summary>
    private TimedSoulweaveRing[] GetActiveSoulweaveWave(DateTime now)
    {
        if (activeSoulweavePlan != null && activeSoulweavePlan.ActiveUntilUtc <= now)
        {
            activeSoulweavePlan = null;
        }

        TimedSoulweaveRing[] retained = soulweaveForecasts
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .OrderBy(forecast => forecast.ActivatesAtUtc)
            .ThenBy(forecast => forecast.Origin.X)
            .ThenBy(forecast => forecast.Origin.Z)
            .ToArray();
        if (retained.Length == 0)
        {
            if (activeSoulweavePlan != null)
            {
                return activeSoulweavePlan.Rings.ToArray();
            }

            return [];
        }

        if (activeSoulweavePlan == null)
        {
            // The earliest queued record defines the risky prefix regardless of whether its projected
            // finish is a few scheduler milliseconds in the past; the wrapper-disappearance boundary,
            // not a local timestamp comparison, is what advances the FIFO.
            TimedSoulweaveRing first = retained[0];
            activeSoulweavePlan = new SoulweaveWavePlan(
                first.ActivatesAtUtc,
                first.ActivatesAtUtc + SoulweaveConcurrentWaveWindow);
        }

        // The cutoff is exclusive: a ring exactly at the next-wave boundary must not make both
        // sequential walls risky during the same movement stage.
        foreach (TimedSoulweaveRing forecast in retained.Where(forecast =>
                     forecast.ActivatesAtUtc >= activeSoulweavePlan.FirstActivationUtc &&
                     forecast.ActivatesAtUtc < activeSoulweavePlan.CohortCutoffUtc))
        {
            if (!activeSoulweavePlan.Rings.Contains(forecast))
            {
                activeSoulweavePlan.Rings.Add(forecast);
                activeSoulweavePlan.ActiveUntilUtc = Max(
                    activeSoulweavePlan.ActiveUntilUtc,
                    forecast.ExpiresAtUtc);
            }
        }

        return activeSoulweavePlan.Rings.ToArray();
    }

    /// <summary>
    /// Returns every live Telltale Tears cast while Soulweave owns a FIFO cohort. Target spacing stays
    /// folded into one shared destination for the complete overlap, not only a narrow finish-time
    /// window in which two independent movement owners could alternate between scheduler pulses.
    /// </summary>
    private static BattleCharacter[] GetConcurrentTelltaleTearsCasters(
        IReadOnlyCollection<TimedSoulweaveRing> wave)
    {
        return wave.Count == 0
            ? []
            : GetCasters([EnemyAction.TelltaleTears]).ToArray();
    }

    /// <summary>
    /// Resolves the current positions that the local player's Telltale Tears destination must avoid.
    /// When another party member owns a marker, only that marked member can overlap damage onto the
    /// player. When the local player owns a marker, every other living party member is a constraint
    /// because the player's own circle would otherwise clip an unmarked ally.
    /// </summary>
    /// <remarks>
    /// All living party members are constraints when the player owns a marker; limiting the set to
    /// other marked targets allowed an unmarked ally inside the spread radius. Positions remain
    /// current-frame scalars so Duty Support movement invalidates stale endpoints.
    /// </remarks>
    private static bool TryGetTelltaleTearsConstraintLocations(
        IEnumerable<BattleCharacter> casters,
        out Vector3[] locations)
    {
        bool allTargetsResolved = true;
        bool playerOwnsSpread = false;
        HashSet<uint> targetIds = [];
        foreach (BattleCharacter caster in casters)
        {
            if (!caster.SpellCastInfo.IsValid || caster.SpellCastInfo.TargetId == 0)
            {
                allTargetsResolved = false;
                continue;
            }

            if (caster.SpellCastInfo.TargetId == Core.Player.ObjectId)
            {
                playerOwnsSpread = true;
                continue;
            }

            targetIds.Add(caster.SpellCastInfo.TargetId);
        }

        if (playerOwnsSpread)
        {
            foreach (BattleCharacter member in PartyManager.VisibleMembers
                         .Select(member => member.BattleCharacter)
                         .Where(member => member != null && member.IsValid && member.IsAlive &&
                                          member.ObjectId != Core.Player.ObjectId))
            {
                targetIds.Add(member.ObjectId);
            }
        }

        List<Vector3> resolved = [];
        foreach (uint targetId in targetIds)
        {
            GameObject target = GameObjectManager.GetObjectByObjectId(targetId);
            if (target == null || !target.IsValid)
            {
                allTargetsResolved = false;
                continue;
            }

            resolved.Add(target.Location);
        }

        locations = resolved.ToArray();
        return allTargetsResolved;
    }

    /// <summary>
    /// Suppresses the standalone Telltale mover for the complete period in which a live spread and a
    /// queued Soulweave FIFO prefix overlap. The combined stage remains the sole owner while target
    /// IDs populate, preventing per-pulse ownership changes.
    /// </summary>
    private bool IsSoulweaveTelltaleTearsOverlapActive()
    {
        DateTime now = DateTime.UtcNow;
        return GetActiveSoulweaveWave(now).Length > 0 &&
               GetCasters([EnemyAction.TelltaleTears]).Any();
    }

    /// <summary>
    /// Tracks the helper-owned Crater Carve floor transition and requests one local avoidance
    /// navigation rebuild after the changed collision scene has become observable.
    /// </summary>
    /// <param name="now">Current UTC time used to preserve cast and rebuild ordering.</param>
    private void UpdateLunipyatiCraterNavigation(DateTime now)
    {
        BattleCharacter craterAoeCaster = GetFirstCaster([EnemyAction.CraterCarveAoe]);
        BattleCharacter craterCaster = craterAoeCaster ?? GetFirstCaster([EnemyAction.CraterCarveVisual]);
        if (craterCaster != null)
        {
            // The cast itself already requires leaving center. Latching the future hole at cast start
            // safely pre-stages the annulus and guarantees the boundary survives the helper despawn.
            craterActive = true;
        }

        if (craterAoeCaster != null)
        {
            // Action 40605 is the non-targetable helper that owns the floor-changing action effect.
            // Retain its expected finish so a transient missing wrapper cannot trigger an early rebuild.
            craterCarveAoeWasCasting = true;
            craterCarveAoeResolvesAtUtc = Max(
                craterCarveAoeResolvesAtUtc,
                now + craterAoeCaster.SpellCastInfo.RemainingCastTime);
        }
        else if (craterCarveAoeWasCasting)
        {
            craterCarveAoeWasCasting = false;
            craterNavigationResetAtUtc = Max(now, craterCarveAoeResolvesAtUtc) +
                                        CraterNavigationResetDelay;
        }

        if (Core.Player.IsAlive &&
            craterActive &&
            !craterNavigationResetIssued &&
            craterNavigationResetAtUtc != DateTime.MinValue &&
            now >= craterNavigationResetAtUtc)
        {
            // This refreshes AvoidanceManager's local heightfield and cached escape path. It does not
            // replace the persistent authored crater avoid or clear OrderBot's global navigation route.
            AvoidanceManager.ResetNavigation();
            craterNavigationResetIssued = true;
            craterNavigationResetAtUtc = DateTime.MinValue;
            Logger.Information(
                "[Yuweyawata] Crater Carve changed the arena; rebuilding local avoidance navigation.");
        }
    }

    /// <summary>
    /// Retains Lunipyati's repeated cleave, forecast routes, impact grace, and permanent floor state
    /// using scalar snapshots rather than frame-scoped actor wrappers.
    /// </summary>
    private void UpdateLunipyatiForecasts(DateTime now)
    {
        BattleCharacter ragingClawHelper = GetFirstCaster([EnemyAction.RagingClawFirst]);
        if (ragingClawHelper != null &&
            (ragingClawHelper.ObjectId != ragingClawAnchorId || now >= ragingClawUntilUtc))
        {
            // Action 40613's cast location and rotation own the complete six-hit AOE. Capture them
            // before extending the retained timer so a recycled helper can arm a later sequence.
            ragingClawSource = ragingClawHelper.Location;
            ragingClawHeading = ragingClawHelper.Heading;
            ragingClawAnchorId = ragingClawHelper.ObjectId;
        }

        foreach (BattleCharacter caster in GetCasters(EnemyAction.RagingClawSequence))
        {
            ragingClawUntilUtc = Max(
                ragingClawUntilUtc,
                now + caster.SpellCastInfo.RemainingCastTime + RagingClawRepeatWindow);
        }

        // Jagged Edge begins near the retained cleave's final hits, but its cast wrapper disappears
        // before the damage/status snapshot. Latch the combined owner through cast completion plus
        // grace; otherwise the planner drops to CraterRing about 1.35 seconds early and generic
        // spread movement resumes oscillating between stay-near and run-from destinations.
        if (now < ragingClawUntilUtc)
        {
            foreach (BattleCharacter caster in GetCasters([EnemyAction.JaggedEdge]))
            {
                ragingClawJaggedEdgeUntilUtc = Max(
                    ragingClawJaggedEdgeUntilUtc,
                    now + caster.SpellCastInfo.RemainingCastTime + ResolutionGrace);
            }
        }

        foreach (BattleCharacter caster in GetCasters(EnemyAction.BoulderDanceInitialCasts))
        {
            DateTime resolvesAt = now + caster.SpellCastInfo.RemainingCastTime;
            boulderDanceForecasts[caster.ObjectId] = new TimedCircle(
                caster.Location,
                resolvesAt + BoulderDanceRepeatWindow);
        }

        foreach (uint objectId in boulderDanceForecasts
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            boulderDanceForecasts.Remove(objectId);
        }

        foreach (BattleCharacter caster in GetCasters([EnemyAction.LeapingEarthCurve]))
        {
            DateTime visualFinishesAt = now + caster.SpellCastInfo.RemainingCastTime;
            DateTime activatesAt = visualFinishesAt - LeapingEarthCurveImpactLead;
            leapingEarthCurveForecasts[caster.ObjectId] = new LeapingEarthCurveForecast(
                caster.ObjectId,
                caster.Heading,
                activatesAt,
                visualFinishesAt + LeapingEarthCurvePostVisualPersistence);
        }

        foreach (uint objectId in leapingEarthCurveForecasts
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            leapingEarthCurveForecasts.Remove(objectId);
        }

        BattleCharacter spiralCaster = GetFirstCaster(EnemyAction.LeapingEarthSpiral);
        if (spiralCaster != null &&
            (spiralCaster.ObjectId != leapingEarthSpiralAnchorId || now >= leapingEarthSpiralUntilUtc))
        {
            ArmLeapingEarthSpiralForecast(spiralCaster, now);
        }

        leapingEarthSpiralForecasts.RemoveAll(forecast => forecast.ExpiresAtUtc <= now);

        foreach (BattleCharacter caster in GetCasters([EnemyAction.LeapingEarthImpact]))
        {
            DateTime activatesAt = now + caster.SpellCastInfo.RemainingCastTime;
            // Damage can arrive about one second after the helper's nominal finish, so retain the
            // circle through delayed action effects.
            leapingEarthFallbackImpacts[caster.ObjectId] = new TimedHazardCircle(
                caster.Location,
                activatesAt,
                activatesAt + LeapingEarthImpactPersistence,
                caster.ObjectId);
        }

        foreach (uint objectId in leapingEarthFallbackImpacts
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            leapingEarthFallbackImpacts.Remove(objectId);
        }

        BattleCharacter beastlyRoarCaster = GetFirstCaster(EnemyAction.BeastlyRoar);
        if (beastlyRoarCaster != null)
        {
            beastlyRoarResolvesAtUtc = now + beastlyRoarCaster.SpellCastInfo.RemainingCastTime;
            beastlyRoarUntilUtc = beastlyRoarResolvesAtUtc + ResolutionGrace;
            beastlyRoarSource = beastlyRoarCaster.Location;
            beastlyRoarAnchorId = beastlyRoarCaster.ObjectId;
        }

        rockBlastForecasts.RemoveAll(forecast => forecast.ExpiresAtUtc <= now);
        BattleCharacter rockBlastCaster = GetFirstCaster(EnemyAction.RockBlast);
        if (rockBlastCaster != null && now >= rockBlastUntilUtc)
        {
            ArmRockBlastForecast(rockBlastCaster, now);
        }
    }

    /// <summary>
    /// Expands the five-second spiral visual into twenty five-yalm impacts, each 0.25 seconds apart.
    /// The visual resolves half a second after the first impact begins, so subtracting that lead from
    /// the remaining cast time preserves the observed action-effect order when capture starts late.
    /// </summary>
    private void ArmLeapingEarthSpiralForecast(BattleCharacter caster, DateTime now)
    {
        leapingEarthSpiralForecasts.Clear();
        leapingEarthSpiralAnchorId = caster.ObjectId;

        TimeSpan remaining = caster.SpellCastInfo.RemainingCastTime;
        TimeSpan firstImpactLead = remaining > TimeSpan.FromSeconds(0.5)
            ? remaining - TimeSpan.FromSeconds(0.5)
            : TimeSpan.Zero;
        DateTime firstActivation = now + firstImpactLead;
        float rotation = -caster.Heading;

        for (int index = 0; index < LeapingEarthSpiralOffsets.Length; index++)
        {
            (float x, float z) = LeapingEarthSpiralOffsets[index];
            DateTime activatesAt = firstActivation +
                TimeSpan.FromTicks(LeapingEarthSpiralInterval.Ticks * index);
            leapingEarthSpiralForecasts.Add(new TimedHazardCircle(
                RotateLunipyatiOffset(x, z, rotation),
                activatesAt,
                activatesAt + LeapingEarthImpactPersistence,
                caster.ObjectId));
        }

        leapingEarthSpiralUntilUtc = leapingEarthSpiralForecasts.Count > 0
            ? leapingEarthSpiralForecasts[^1].ExpiresAtUtc
            : now;
    }

    /// <summary>
    /// Forecasts the fifteen Rock Blast impacts from the first helper's radial position and heading.
    /// The sequence advances 22.5 degrees every 0.6 seconds and stops slightly short of a full turn.
    /// </summary>
    private void ArmRockBlastForecast(BattleCharacter caster, DateTime now)
    {
        float offsetX = caster.X - ArenaCenter.Lunipyati.X;
        float offsetZ = caster.Z - ArenaCenter.Lunipyati.Z;
        if ((offsetX * offsetX) + (offsetZ * offsetZ) < 1f)
        {
            return;
        }

        float headingX = MathF.Sin(caster.Heading);
        float headingZ = MathF.Cos(caster.Heading);
        float leftDotHeading = (offsetZ * headingX) - (offsetX * headingZ);
        float stepRadians = (leftDotHeading < 0f ? -RockBlastStepDegrees : RockBlastStepDegrees) *
                            (MathF.PI / 180f);

        // The safe side trails the rotating helper sequence. Seed continuity opposite its authored
        // angular progression; a later fully safe sampled waypoint can update this direction, but a
        // transient no-candidate pulse must not surrender movement to routine combat.
        rockBlastTraversalDirection = stepRadians > 0f ? -1 : 1;

        rockBlastForecasts.Clear();
        rockBlastAnchorId = caster.ObjectId;
        DateTime firstActivation = now + caster.SpellCastInfo.RemainingCastTime;
        for (int index = 0; index < RockBlastImpactCount; index++)
        {
            DateTime activatesAt = firstActivation + TimeSpan.FromTicks(RockBlastInterval.Ticks * index);
            rockBlastForecasts.Add(new TimedHazardCircle(
                new Vector3(
                    ArenaCenter.Lunipyati.X + offsetX,
                    ArenaCenter.Lunipyati.Y,
                    ArenaCenter.Lunipyati.Z + offsetZ),
                activatesAt,
                activatesAt + ResolutionGrace,
                caster.ObjectId));

            float cosine = MathF.Cos(stepRadians);
            float sine = MathF.Sin(stepRadians);
            (offsetX, offsetZ) = (
                (offsetX * cosine) + (offsetZ * sine),
                (offsetZ * cosine) - (offsetX * sine));
        }

        rockBlastUntilUtc = rockBlastForecasts.Count > 0
            ? rockBlastForecasts[^1].ExpiresAtUtc
            : now;
    }

    /// <summary>
    /// Expands only the earliest four 40662 curves; queued choreography must not close the safe
    /// region for the wave that resolves first.
    /// </summary>
    private TimedHazardCircle[] GetPublishedLeapingEarthCurveHazards(DateTime now)
    {
        LeapingEarthCurveForecast[] curves = leapingEarthCurveForecasts.Values
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .OrderBy(forecast => forecast.ActivatesAtUtc)
            .ThenBy(forecast => forecast.CasterObjectId)
            .Take(LeapingEarthConcurrentCurveCount)
            .ToArray();

        return DeduplicateHazards(curves
            .SelectMany(curve => LeapingEarthCurveOffsets.Select(offset =>
            {
                float heading = curve.Heading + offset.HeadingOffsetRadians;
                return new TimedHazardCircle(
                    new Vector3(
                        ArenaCenter.Lunipyati.X + (MathF.Sin(heading) * offset.Distance),
                        ArenaCenter.Lunipyati.Y,
                        ArenaCenter.Lunipyati.Z + (MathF.Cos(heading) * offset.Distance)),
                    curve.ActivatesAtUtc,
                    curve.ExpiresAtUtc,
                    curve.CasterObjectId);
            })));
    }

    /// <summary>
    /// Returns every unresolved 40661 spiral entry with its individual activation time intact.
    /// The activation-aware movement horizon, rather than an arbitrary list truncation, decides which
    /// entries are presently unsafe; this guarantees that the final impacts become actionable early
    /// enough even when more than ten prior entries still retain effect grace.
    /// </summary>
    private TimedHazardCircle[] GetPublishedLeapingEarthSpiralForecasts(DateTime now) =>
        DeduplicateHazards(leapingEarthSpiralForecasts
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .OrderBy(forecast => forecast.ActivatesAtUtc));

    /// <summary>
    /// Returns immutable snapshots of every currently relevant live 40606 helper. These locations
    /// reinforce the authored spiral inside the semantic planner instead of registering a competing
    /// generic mover, preserving one movement owner while making the visible helper authoritative.
    /// </summary>
    private TimedHazardCircle[] GetLiveLeapingEarthImpactHazards(DateTime now) =>
        DeduplicateHazards(leapingEarthFallbackImpacts.Values
            .Where(forecast => forecast.ExpiresAtUtc > now));

    /// <summary>
    /// Returns the ten-impact Leaping Earth planning window, reinforced by live helpers.
    /// Publishing it at the visual gives the mover the full 4.5-second warning while retaining
    /// activation timestamps for priority ordering and excluding the distant half of the spiral.
    /// </summary>
    private static TimedHazardCircle[] GetLeapingEarthSpiralPlanningHazards(
        IEnumerable<TimedHazardCircle> forecasts,
        DateTime now) =>
        DeduplicateHazards(forecasts.Where(forecast => forecast.ExpiresAtUtc > now))
            .OrderBy(forecast => forecast.ActivatesAtUtc)
            .Take(LeapingEarthSpiralForecastCount)
            .ToArray();

    /// <summary>
    /// Supplies RB only the verified time-insensitive curve batch. A live 40606 fallback is exposed
    /// when no visual forecast exists, while an active spiral deliberately returns no locations so
    /// its activation-aware planner remains the sole movement owner.
    /// </summary>
    private TimedHazardCircle[] GetReactiveLeapingEarthAvoidHazards(DateTime now)
    {
        TimedHazardCircle[] curves = GetPublishedLeapingEarthCurveHazards(now);
        TimedHazardCircle[] spiral = GetPublishedLeapingEarthSpiralForecasts(now);
        if (curves.Length > 0 &&
            (spiral.Length == 0 || curves.Min(hazard => hazard.ActivatesAtUtc) <=
                                   spiral.Min(hazard => hazard.ActivatesAtUtc)))
        {
            return curves;
        }

        return spiral.Length > 0
            ? []
            : DeduplicateHazards(
                leapingEarthFallbackImpacts.Values.Where(forecast => forecast.ExpiresAtUtc > now));
    }

    /// <summary>
    /// Returns the next ten Rock Blast impacts with activation times preserved for manual planning.
    /// </summary>
    private TimedHazardCircle[] GetPublishedRockBlastForecasts(DateTime now) =>
        DeduplicateHazards(rockBlastForecasts
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .OrderBy(forecast => forecast.ActivatesAtUtc)
            .Take(RockBlastForecastCount));

    /// <summary>
    /// Narrows a rotating sequence to impacts that can resolve during one movement horizon. This is
    /// the time information RB's location-only avoidance collection cannot represent.
    /// </summary>
    private static TimedHazardCircle[] GetActionableHazards(
        IEnumerable<TimedHazardCircle> forecasts,
        DateTime now,
        TimeSpan movementLead)
    {
        DateTime cutoff = now + movementLead;
        return DeduplicateHazards(forecasts.Where(forecast =>
            forecast.ExpiresAtUtc > now && forecast.ActivatesAtUtc <= cutoff));
    }

    private bool IsRockBlastForecastActive(DateTime now) =>
        now < rockBlastUntilUtc && rockBlastForecasts.Any(forecast => forecast.ExpiresAtUtc > now);

    /// <summary>
    /// Collapses coincident helper impacts, notably the four Leaping Earth circles at arena center.
    /// Duplicate regions add no pathing information and previously amplified an unsafe center move
    /// into four simultaneous vulnerability stacks.
    /// </summary>
    private static TimedHazardCircle[] DeduplicateHazards(IEnumerable<TimedHazardCircle> hazards)
    {
        List<TimedHazardCircle> result = [];
        foreach (TimedHazardCircle hazard in hazards.OrderBy(candidate => candidate.ActivatesAtUtc))
        {
            int duplicateIndex = result.FindIndex(existing =>
                DistanceSquared2D(existing.Location, hazard.Location) <= 0.25f * 0.25f);
            if (duplicateIndex < 0)
            {
                result.Add(hazard);
                continue;
            }

            TimedHazardCircle existing = result[duplicateIndex];
            result[duplicateIndex] = existing with
            {
                ActivatesAtUtc = existing.ActivatesAtUtc <= hazard.ActivatesAtUtc
                    ? existing.ActivatesAtUtc
                    : hazard.ActivatesAtUtc,
                ExpiresAtUtc = Max(existing.ExpiresAtUtc, hazard.ExpiresAtUtc),
            };
        }

        return result.ToArray();
    }

    /// <summary>
    /// Rotates one local X/Z offset using FFXIV's heading-zero-along-positive-Z convention.
    /// </summary>
    private static Vector3 RotateLunipyatiOffset(float x, float z, float rotation)
    {
        float cosine = MathF.Cos(rotation);
        float sine = MathF.Sin(rotation);
        return new Vector3(
            ArenaCenter.Lunipyati.X + (x * cosine) + (z * sine),
            ArenaCenter.Lunipyati.Y,
            ArenaCenter.Lunipyati.Z + (z * cosine) - (x * sine));
    }

    /// <summary>
    /// Registers one cast-linked circle at either the helper or its authored cast location.
    /// </summary>
    private static void AddCastCircle(
        Func<bool> canRun,
        uint actionId,
        float radius,
        bool useCastLocation)
    {
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: canRun,
            objectSelector: actor => actor.CastingSpellId == actionId,
            radiusProducer: _ => radius,
            locationProducer: actor => useCastLocation ? actor.SpellCastInfo.CastLocation : actor.Location);
    }

    /// <summary>
    /// Registers other players' live cast targets as spread hazards while leaving the local player's
    /// own marker to the shared safe-point solver.
    /// </summary>
    private static void AddTargetedSpread(Func<bool> canRun, uint actionId, float radius)
    {
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: canRun,
            objectSelector: actor => actor.CastingSpellId == actionId &&
                                     actor.SpellCastInfo.IsValid &&
                                     actor.SpellCastInfo.TargetId != 0 &&
                                     actor.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: _ => radius,
            locationProducer: actor => GameObjectManager.GetObjectByObjectId(actor.SpellCastInfo.TargetId)?.Location ??
                                       actor.SpellCastInfo.CastLocation);
    }

    /// <summary>
    /// Registers one Dark II cone wave. The second wave is gated until every first-wave cast wrapper
    /// has resolved because the two interleaved six-cone sets cover the full arena when combined.
    /// </summary>
    private static void AddDarkIICone(Func<bool> canRun, uint actionId)
    {
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: canRun,
            objectSelector: actor => actor.CastingSpellId == actionId,
            leashPointProducer: () => ArenaCenter.OverseerKanilokka,
            leashRadius: 40f,
            rotationDegrees: 0f,
            radius: 36f,
            arcDegrees: 32f,
            priority: AvoidancePriority.High);
    }

    private static BattleCharacter GetFirstCaster(HashSet<uint> actionIds)
    {
        return GetCasters(actionIds)
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .ThenBy(actor => actor.ObjectId)
            .FirstOrDefault();
    }

    private static BattleCharacter GetFirstCaster(uint actionId)
    {
        return GetCasters([actionId])
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .ThenBy(actor => actor.ObjectId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Enumerates current-frame casters only; callers retain scalar snapshots when a cast must outlive
    /// the RebornBuddy wrapper.
    /// </summary>
    private static IEnumerable<BattleCharacter> GetCasters(HashSet<uint> actionIds)
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid &&
                            actionIds.Contains(actor.CastingSpellId));
    }

    /// <summary>
    /// Resolves a stack helper's target, using the party's central living member when the local player
    /// owns the marker and therefore cannot follow itself.
    /// </summary>
    private static bool TryGetStackDestination(BattleCharacter caster, out Vector3 destination)
    {
        if (caster.SpellCastInfo.TargetId != Core.Player.ObjectId)
        {
            GameObject target = GameObjectManager.GetObjectByObjectId(caster.SpellCastInfo.TargetId);
            if (target != null && target.IsValid)
            {
                destination = target.Location;
                return true;
            }
        }

        List<BattleCharacter> party = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member => member != null && member.IsValid && member.IsAlive && !member.IsMe)
            .ToList();
        BattleCharacter anchor = party
            .OrderBy(candidate => party.Sum(other => candidate.Distance2D(other)))
            .ThenBy(candidate => Core.Player.Distance2D(candidate))
            .ThenBy(candidate => candidate.ObjectId)
            .FirstOrDefault();

        destination = anchor?.Location ?? default;
        return anchor != null;
    }

    /// <summary>
    /// Latches one living duty-support member so the misdirection planner does not oscillate between
    /// separate safe corridors as party positions change.
    /// </summary>
    private BattleCharacter GetLatchedTrustAnchor()
    {
        List<BattleCharacter> trusts = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member => member != null && member.IsValid && member.IsAlive && !member.IsMe &&
                             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)member.NpcId))
            .ToList();

        BattleCharacter latched = trusts.FirstOrDefault(member =>
            member.ObjectId == priorityMovement.AnchorObjectId);
        return latched ?? trusts
            .OrderByDescending(member => member.Distance2D(ArenaCenter.OverseerKanilokka))
            .ThenBy(member => Core.Player.Distance2D(member))
            .ThenBy(member => member.ObjectId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Moves into the center island before the floor transition. Four yalms is inside the authored
    /// five-yalm island with a full yalm of tolerance for RB movement and the player footprint.
    /// </summary>
    private static Vector3 GetNecrohazardPreparationDestination()
    {
        Vector3 player = Core.Player.Location;
        float deltaX = player.X - ArenaCenter.OverseerKanilokka.X;
        float deltaZ = player.Z - ArenaCenter.OverseerKanilokka.Z;
        float distance = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= NecrohazardPreparationRadius || distance < 0.01f)
        {
            return player;
        }

        return new Vector3(
            ArenaCenter.OverseerKanilokka.X + ((deltaX / distance) * NecrohazardPreparationRadius),
            ArenaCenter.OverseerKanilokka.Y,
            ArenaCenter.OverseerKanilokka.Z + ((deltaZ / distance) * NecrohazardPreparationRadius));
    }

    /// <summary>
    /// Returns the next waypoint from a route built entirely inside the confirmed floor polygons.
    /// A large deviation rebuilds from the current position so stale waypoints cannot pull the player
    /// back across a removed corner after knockback, latency, or manual intervention.
    /// </summary>
    private bool TryGetExactNecrohazardDestination(out Vector3 destination)
    {
        destination = default;
        if (necrohazardFloorLayout == NecrohazardFloorLayout.None)
        {
            return false;
        }

        bool routeNeedsBuild = necrohazardExactRoute.Count == 0 ||
                               necrohazardExactRouteLayout != necrohazardFloorLayout;
        if (!routeNeedsBuild &&
            DistanceSquaredToRoute2D(Core.Player.Location, necrohazardExactRoute, necrohazardExactRouteIndex) >
            NecrohazardRouteRecoveryDistance * NecrohazardRouteRecoveryDistance)
        {
            routeNeedsBuild = true;
        }

        // A failed search is deterministic for the current authored layout and origin. Avoid doing
        // the full grid search every pulse; the live duty-support breadcrumb remains the safe fallback
        // until the next Lost Hope supplies a fresh map-effect transition.
        if (necrohazardExactRoute.Count == 0 &&
            necrohazardExactRouteFailureReported &&
            necrohazardExactRouteLayout == necrohazardFloorLayout)
        {
            return false;
        }

        if (routeNeedsBuild)
        {
            ClearExactNecrohazardRoute();
            if (!YuweyawataNecrohazardGeometry.TryBuildRoute(
                    necrohazardFloorLayout,
                    Core.Player.Location,
                    out Vector3[] route))
            {
                if (!necrohazardExactRouteFailureReported)
                {
                    necrohazardExactRouteFailureReported = true;
                    Logger.Warning(
                        $"[Yuweyawata] Could not build the {necrohazardFloorLayout} Necrohazard route; " +
                        "falling back to the live duty-support trail.");
                }

                necrohazardExactRouteLayout = necrohazardFloorLayout;
                return false;
            }

            necrohazardExactRoute.AddRange(route);
            necrohazardExactRouteLayout = necrohazardFloorLayout;
            necrohazardExactRouteIndex = 0;
            necrohazardExactRouteFailureReported = false;
            Logger.Information(
                $"[Yuweyawata] Built {necrohazardFloorLayout} Necrohazard route with " +
                $"{route.Length} waypoints; goal={route[^1]}.");
        }

        // Direct movement pulses can carry the player past a corner without ever entering the old
        // one-yalm waypoint tolerance. The live ThreeRoutes pull reached radius 17.5, then walked
        // inward to reacquire such a missed point. Advance to the furthest remaining waypoint whose
        // complete segment is safe from the current position, eliminating that backtracking while
        // preserving every turn required by the surviving floor.
        for (int candidateIndex = necrohazardExactRoute.Count - 1;
             candidateIndex > necrohazardExactRouteIndex;
             candidateIndex--)
        {
            if (!YuweyawataNecrohazardGeometry.IsSegmentWalkable(
                    necrohazardFloorLayout,
                    Core.Player.Location,
                    necrohazardExactRoute[candidateIndex]))
            {
                continue;
            }

            necrohazardExactRouteIndex = candidateIndex;
            break;
        }

        float toleranceSquared = NecrohazardRouteWaypointTolerance * NecrohazardRouteWaypointTolerance;
        while (necrohazardExactRouteIndex < necrohazardExactRoute.Count - 1 &&
               DistanceSquared2D(
                   Core.Player.Location,
                   necrohazardExactRoute[necrohazardExactRouteIndex]) <= toleranceSquared)
        {
            necrohazardExactRouteIndex++;
        }

        destination = necrohazardExactRoute[necrohazardExactRouteIndex];
        return true;
    }

    /// <summary>
    /// Measures horizontal distance to the uncompleted route, including its segments rather than
    /// only sparse simplified waypoints.
    /// </summary>
    private static float DistanceSquaredToRoute2D(
        Vector3 point,
        IReadOnlyList<Vector3> route,
        int nextWaypointIndex)
    {
        if (route.Count == 0)
        {
            return float.MaxValue;
        }

        float minimum = DistanceSquared2D(point, route[Math.Clamp(nextWaypointIndex, 0, route.Count - 1)]);
        int firstSegment = Math.Max(0, nextWaypointIndex - 1);
        for (int index = firstSegment; index < route.Count - 1; index++)
        {
            minimum = MathF.Min(
                minimum,
                DistanceSquaredToSegment2D(point, route[index], route[index + 1]));
        }

        return minimum;
    }

    private static float DistanceSquaredToSegment2D(Vector3 point, Vector3 start, Vector3 end)
    {
        float segmentX = end.X - start.X;
        float segmentZ = end.Z - start.Z;
        float segmentLengthSquared = (segmentX * segmentX) + (segmentZ * segmentZ);
        float interpolation = segmentLengthSquared < 0.0001f
            ? 0f
            : Math.Clamp(
                (((point.X - start.X) * segmentX) + ((point.Z - start.Z) * segmentZ)) /
                segmentLengthSquared,
                0f,
                1f);
        float closestX = start.X + (segmentX * interpolation);
        float closestZ = start.Z + (segmentZ * interpolation);
        float deltaX = point.X - closestX;
        float deltaZ = point.Z - closestZ;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    /// <summary>
    /// Records and follows immutable Trust breadcrumbs when RB did not expose one unambiguous floor
    /// transition. A delayed player then takes the same turns instead of drawing a straight chord
    /// across removed floor.
    /// </summary>
    private Vector3? GetNecrohazardTrailDestination(BattleCharacter trustAnchor)
    {
        if (trustAnchor == null)
        {
            return null;
        }

        if (necrohazardTrailAnchorId != trustAnchor.ObjectId)
        {
            ClearNecrohazardTrail();
            necrohazardTrailAnchorId = trustAnchor.ObjectId;
        }

        Vector3 anchorLocation = trustAnchor.Location;
        if (necrohazardTrustTrail.Count == 0 ||
            DistanceSquared2D(necrohazardTrustTrail.Last(), anchorLocation) >=
            NecrohazardTrailSpacing * NecrohazardTrailSpacing)
        {
            // Sixty-four 1.25-yalm breadcrumbs exceed either arena layout's complete route. If an
            // abnormal actor update fills the bound, retain the earlier unvisited points rather than
            // discarding the safe path underneath a delayed player.
            if (necrohazardTrustTrail.Count < MaximumNecrohazardTrailPoints)
            {
                necrohazardTrustTrail.Enqueue(anchorLocation);
            }
        }

        float waypointToleranceSquared = NecrohazardWaypointTolerance * NecrohazardWaypointTolerance;
        while (necrohazardTrustTrail.Count > 1 &&
               DistanceSquared2D(Core.Player.Location, necrohazardTrustTrail.Peek()) <= waypointToleranceSquared)
        {
            necrohazardTrustTrail.Dequeue();
        }

        if (necrohazardTrustTrail.Count == 0)
        {
            return anchorLocation;
        }

        // Select a few recorded steps ahead while retaining the queue head for progress accounting.
        // Each segment is an observed Trust path, so the lookahead smooths the requested heading
        // without replacing the curved route with an unsafe straight line to the current actor.
        Vector3[] trail = necrohazardTrustTrail.ToArray();
        Vector3 destination = trail[0];
        float routeDistance = 0f;
        for (int index = 1; index < trail.Length; index++)
        {
            float segmentDistance = (float)Math.Sqrt(DistanceSquared2D(trail[index - 1], trail[index]));
            if (routeDistance + segmentDistance > NecrohazardTrailLookaheadDistance)
            {
                break;
            }

            routeDistance += segmentDistance;
            destination = trail[index];
        }

        return destination;
    }

    /// <summary>
    /// Clears scalar Necrohazard route history when its sequence or selected Trust changes.
    /// </summary>
    private void ClearNecrohazardTrail()
    {
        necrohazardTrustTrail.Clear();
        necrohazardTrailAnchorId = 0;
    }

    /// <summary>
    /// Clears only the polygon-derived route; layout selection and Trust fallback have independent
    /// lifetimes and must not release one another.
    /// </summary>
    private void ClearExactNecrohazardRoute()
    {
        necrohazardExactRoute.Clear();
        necrohazardExactRouteIndex = 0;
        necrohazardExactRouteLayout = NecrohazardFloorLayout.None;
        necrohazardExactRouteFailureReported = false;
    }

    /// <summary>
    /// Clears prediction and hysteresis at the end of each Temporary Misdirection window so a later
    /// debuff cannot inherit stale angular velocity or an already-open movement gate.
    /// </summary>
    private void ResetMisdirectionInputGate()
    {
        lastForcedMovementDirectionUtc = DateTime.MinValue;
        lastForcedMovementDirection = 0f;
        hasLastForcedMovementDirection = false;
        misdirectionInputGateOpen = false;
    }

    /// <summary>
    /// Returns a point behind Raging Claw's frozen helper cone, clamped one yalm inside the wall.
    /// </summary>
    private static Vector3 GetRagingClawBehindDestination(Vector3 source, float heading)
    {
        Vector3 destination = new(
            source.X - (MathF.Sin(heading) * RagingClawBehindDistance),
            ArenaCenter.Lunipyati.Y,
            source.Z - (MathF.Cos(heading) * RagingClawBehindDistance));
        return ClampToArena(destination, ArenaCenter.Lunipyati, LunipyatiEdgeDestinationRadius);
    }

    /// <summary>
    /// Returns whether the predictable post-crater Jagged Edge sequence or its live cast overlaps
    /// Raging Claw's retained six-hit cone. Pre-arming the overlap keeps one movement lease through
    /// the transition instead of reacting only when the five-second spread wrapper appears.
    /// </summary>
    private bool IsRagingClawJaggedEdgeOverlapActive()
    {
        DateTime now = DateTime.UtcNow;
        return now < ragingClawJaggedEdgeUntilUtc ||
               (craterActive && now < ragingClawUntilUtc);
    }

    /// <summary>
    /// Finds one rear-half point outside every other live Jagged Edge target. The 2026-08-25 overlap
    /// produced 59 competing escape requests and 31 facing transitions when cone and spreads owned
    /// movement independently; sampling their intersection removes that registration-order fight.
    /// If actor data is incomplete, the fallback preserves the repeated frontal-cleave requirement,
    /// which resolves before the later spread marker.
    /// </summary>
    private Vector3 GetRagingClawJaggedEdgeDestination(
        Vector3 ragingClawOrigin,
        float ragingClawRotation,
        IReadOnlyCollection<BattleCharacter> jaggedEdgeCasters)
    {
        Vector3 current = Core.Player.Location;
        Vector3 preferred = GetRagingClawBehindDestination(ragingClawOrigin, ragingClawRotation);
        Vector3[] otherTargets = jaggedEdgeCasters
            .Where(caster => caster.SpellCastInfo.IsValid &&
                             caster.SpellCastInfo.TargetId != 0 &&
                             caster.SpellCastInfo.TargetId != Core.Player.ObjectId)
            .Select(caster => GameObjectManager.GetObjectByObjectId(caster.SpellCastInfo.TargetId))
            .Where(target => target != null && target.IsValid)
            .Select(target => target.Location)
            .ToArray();

        bool IsSafe(Vector3 point) =>
            IsPointInLunipyatiWalkableArena(point) &&
            !IsInsideCone(point, ragingClawOrigin, ragingClawRotation, 45f, 90f) &&
            otherTargets.All(target =>
                DistanceSquared2D(point, target) >
                JaggedEdgeOverlapAvoidRadius * JaggedEdgeOverlapAvoidRadius);

        if (IsSafe(current))
        {
            return current;
        }

        float minimumRadius = craterActive ? LunipyatiRingMinimumHoldRadius : 0f;
        Vector3? best = null;
        float bestScore = float.MaxValue;
        for (float radius = minimumRadius;
             radius <= LunipyatiEdgeDestinationRadius + 0.001f;
             radius += 0.5f)
        {
            for (int degrees = 0; degrees < 360; degrees += 5)
            {
                float angle = degrees * (MathF.PI / 180f);
                Vector3 candidate = new(
                    ArenaCenter.Lunipyati.X + (MathF.Sin(angle) * radius),
                    ArenaCenter.Lunipyati.Y,
                    ArenaCenter.Lunipyati.Z + (MathF.Cos(angle) * radius));
                if (!IsSafe(candidate))
                {
                    continue;
                }

                Vector3 waypoint = GetLunipyatiMovementWaypoint(candidate);
                if (!IsSafe(waypoint))
                {
                    continue;
                }

                float score = DistanceSquared2D(current, waypoint) +
                              (0.25f * DistanceSquared2D(preferred, waypoint));
                if (score < bestScore)
                {
                    best = waypoint;
                    bestScore = score;
                }
            }
        }

        return best ?? GetLunipyatiMovementWaypoint(preferred);
    }

    /// <summary>
    /// Returns the farthest inset arena point from Beastly Roar's source.
    /// </summary>
    private static Vector3 GetLunipyatiEdgeDestinationAwayFrom(Vector3 source)
    {
        float deltaX = ArenaCenter.Lunipyati.X - source.X;
        float deltaZ = ArenaCenter.Lunipyati.Z - source.Z;
        float length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (length < 0.01f)
        {
            deltaX = Core.Player.X - ArenaCenter.Lunipyati.X;
            deltaZ = Core.Player.Z - ArenaCenter.Lunipyati.Z;
            length = MathF.Max(0.01f, MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ)));
        }

        return new Vector3(
            ArenaCenter.Lunipyati.X + ((deltaX / length) * LunipyatiEdgeDestinationRadius),
            ArenaCenter.Lunipyati.Y,
            ArenaCenter.Lunipyati.Z + ((deltaZ / length) * LunipyatiEdgeDestinationRadius));
    }

    /// <summary>
    /// Holds the player's current position when it already clears every forecast, otherwise selects
    /// the nearest sampled safe point. Avoidance remains the higher-priority mover while escaping;
    /// this destination primarily prevents the combat routine from stepping or gap-closing back into
    /// a delayed action effect after RB reports that its immediate escape is complete.
    /// </summary>
    private Vector3? GetLunipyatiForecastHoldDestination(IReadOnlyCollection<TimedHazardCircle> hazards)
    {
        Vector3 current = Core.Player.Location;
        if (IsPointInLunipyatiWalkableArena(current) &&
            !IsInsideAnyHazard(current, hazards, LeapingEarthImpactAvoidRadius))
        {
            return current;
        }

        return FindNearestLunipyatiSafePoint(
            current,
            hazards,
            LeapingEarthImpactAvoidRadius);
    }

    /// <summary>
    /// Selects and retains a destination against only an activation-aware hazard window. Rock Blast
    /// candidates stay on the 13-yalm route and reject the inner 12.25-yalm recovery band; this hard
    /// radial invariant prevents a sequence of individually valid nearest-point choices from
    /// drifting into the permanent crater.
    /// </summary>
    private Vector3? GetTimeAwareLunipyatiDestination(
        MechanicKind kind,
        IReadOnlyCollection<TimedHazardCircle> hazards,
        bool requireRingPath,
        float hazardRadius)
    {
        bool IsValid(Vector3 point) =>
            (requireRingPath
                ? IsPointOnLunipyatiMovementRing(point)
                : IsPointInLunipyatiWalkableArena(point)) &&
            !IsInsideAnyHazard(point, hazards, hazardRadius);

        if (priorityMovement.Kind == kind && IsValid(priorityMovement.Destination))
        {
            Vector3 retainedWaypoint = GetLunipyatiMovementWaypoint(priorityMovement.Destination);
            if (IsValid(retainedWaypoint))
            {
                return retainedWaypoint;
            }
        }

        Vector3 current = Core.Player.Location;
        if (IsValid(current))
        {
            return current;
        }

        Vector3? safePoint = FindNearestLunipyatiSafePoint(
            current,
            hazards,
            hazardRadius,
            requireRingPath ? LunipyatiRingPathRadius : null);
        if (safePoint.HasValue)
        {
            if (kind == MechanicKind.RockBlast)
            {
                UpdateRockBlastTraversalDirection(current, safePoint.Value);
            }

            return safePoint;
        }

        if (kind != MechanicKind.RockBlast || !requireRingPath)
        {
            return null;
        }

        // Three adjacent impacts can cover both 15-degree samples. Finish a retained waypoint when
        // possible; otherwise choose the clearer adjacent annulus step without releasing movement.
        if (priorityMovement.Kind == MechanicKind.RockBlast &&
            IsPointOnLunipyatiMovementRing(priorityMovement.Destination) &&
            Core.Player.Distance2D(priorityMovement.Destination) > MovementArrivalTolerance)
        {
            return priorityMovement.Destination;
        }

        return GetBestEffortRockBlastWaypoint(hazards);
    }

    /// <summary>
    /// Returns the adjacent ring waypoint that most quickly increases clearance when every fully safe
    /// sampled point is temporarily covered. Continuity wins near-ties so sequential forecast ticks
    /// cannot reverse direction and oscillate in place.
    /// </summary>
    private Vector3 GetBestEffortRockBlastWaypoint(IReadOnlyCollection<TimedHazardCircle> hazards)
    {
        Vector3 current = Core.Player.Location;
        int preferredDirection = rockBlastTraversalDirection == 0 ? 1 : rockBlastTraversalDirection;
        int[] directions = [preferredDirection, -preferredDirection];
        Vector3 best = GetAdjacentLunipyatiRingWaypoint(current, preferredDirection);
        float bestClearance = GetMinimumHazardDistanceSquared(best, hazards);
        int selectedDirection = preferredDirection;

        foreach (int direction in directions.Skip(1))
        {
            Vector3 candidate = GetAdjacentLunipyatiRingWaypoint(current, direction);
            float clearance = GetMinimumHazardDistanceSquared(candidate, hazards);
            // A quarter-yalm squared deadband prevents tiny floating-point changes from reversing a
            // route whose two candidates are effectively equivalent.
            if (clearance <= bestClearance + 0.25f * 0.25f)
            {
                continue;
            }

            best = candidate;
            bestClearance = clearance;
            selectedDirection = direction;
        }

        rockBlastTraversalDirection = selectedDirection;
        return best;
    }

    /// <summary>
    /// Advances one deterministic angular step on the center of Lunipyati's surviving annulus.
    /// </summary>
    private static Vector3 GetAdjacentLunipyatiRingWaypoint(Vector3 origin, int direction)
    {
        float angle = MathF.Atan2(
            origin.X - ArenaCenter.Lunipyati.X,
            origin.Z - ArenaCenter.Lunipyati.Z);
        float step = MathF.Sign(direction) * LunipyatiAngularWaypointDegrees * (MathF.PI / 180f);
        float waypointAngle = angle + step;
        return new Vector3(
            ArenaCenter.Lunipyati.X + (MathF.Sin(waypointAngle) * LunipyatiRingPathRadius),
            ArenaCenter.Lunipyati.Y,
            ArenaCenter.Lunipyati.Z + (MathF.Cos(waypointAngle) * LunipyatiRingPathRadius));
    }

    /// <summary>
    /// Records the angular direction of a newly selected safe Rock Blast waypoint for continuity
    /// across later forecast frames where no completely safe candidate exists.
    /// </summary>
    private void UpdateRockBlastTraversalDirection(Vector3 origin, Vector3 destination)
    {
        float originAngle = MathF.Atan2(
            origin.X - ArenaCenter.Lunipyati.X,
            origin.Z - ArenaCenter.Lunipyati.Z);
        float destinationAngle = MathF.Atan2(
            destination.X - ArenaCenter.Lunipyati.X,
            destination.Z - ArenaCenter.Lunipyati.Z);
        float delta = NormalizeRadians(destinationAngle - originAngle);
        if (MathF.Abs(delta) > 1f * (MathF.PI / 180f))
        {
            rockBlastTraversalDirection = MathF.Sign(delta) > 0f ? 1 : -1;
        }
    }

    /// <summary>
    /// Scores a fallback waypoint by its nearest forecast center. The common hazard radius need not
    /// be subtracted because only relative clearance between two candidates is compared.
    /// </summary>
    private static float GetMinimumHazardDistanceSquared(
        Vector3 point,
        IReadOnlyCollection<TimedHazardCircle> hazards) =>
        hazards.Count == 0
            ? float.MaxValue
            : hazards.Min(hazard => DistanceSquared2D(point, hazard.Location));

    /// <summary>
    /// Finds the nearest immediately reachable forecast-safe waypoint on a deterministic half-yalm/
    /// five-degree grid. The sampler validates the actual 15-degree annulus waypoint rather than only
    /// its eventual candidate: a safe point across the arena is unusable when the first routed step
    /// enters the next impact. A fixed radius prevents rotating post-crater mechanics from drifting
    /// radially across successive solves.
    /// </summary>
    private Vector3? FindNearestLunipyatiSafePoint(
        Vector3 origin,
        IReadOnlyCollection<TimedHazardCircle> hazards,
        float hazardRadius,
        float? fixedRadius = null)
    {
        float minimumRadius = fixedRadius ?? (craterActive ? LunipyatiCraterAvoidRadius + 0.25f : 0f);
        float maximumRadius = fixedRadius ?? (LunipyatiArenaNavigationRadius - 0.25f);
        Vector3? best = null;
        float bestDistanceSquared = float.MaxValue;

        for (float radius = minimumRadius; radius <= maximumRadius + 0.001f; radius += 0.5f)
        {
            for (int degrees = 0; degrees < 360; degrees += 5)
            {
                float angle = degrees * (MathF.PI / 180f);
                Vector3 candidate = new(
                    ArenaCenter.Lunipyati.X + (MathF.Sin(angle) * radius),
                    ArenaCenter.Lunipyati.Y,
                    ArenaCenter.Lunipyati.Z + (MathF.Cos(angle) * radius));
                Vector3 waypoint = GetLunipyatiMovementWaypoint(candidate);
                if (!IsPointInLunipyatiWalkableArena(waypoint) ||
                    (fixedRadius.HasValue && !IsPointOnLunipyatiMovementRing(waypoint)) ||
                    IsInsideAnyHazard(waypoint, hazards, hazardRadius))
                {
                    continue;
                }

                float distanceSquared = DistanceSquared2D(origin, waypoint);
                if (distanceSquared < bestDistanceSquared)
                {
                    best = waypoint;
                    bestDistanceSquared = distanceSquared;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Applies the stricter hold band used by exclusive post-crater movement. The ordinary 11.5-yalm
    /// avoid remains the emergency floor boundary; 12.25 is the proactive threshold that preserves
    /// time to recover before latency or mover overshoot reaches that boundary.
    /// </summary>
    private bool IsPointOnLunipyatiMovementRing(Vector3 point)
    {
        if (!craterActive)
        {
            return false;
        }

        float distanceSquared = DistanceSquared2D(point, ArenaCenter.Lunipyati);
        return distanceSquared >= LunipyatiRingMinimumHoldRadius * LunipyatiRingMinimumHoldRadius &&
               distanceSquared <= LunipyatiEdgeDestinationRadius * LunipyatiEdgeDestinationRadius;
    }

    /// <summary>
    /// Keeps routine combat close enough to Lunipyati after Crater Carve without permitting a direct
    /// chord across the hole. Holding the current valid point when already in melee avoids needless
    /// orbiting while the movement capability lease suppresses unsafe gap closers.
    /// </summary>
    private Vector3 GetCraterRingCombatDestination(BattleCharacter boss)
    {
        Vector3 current = Core.Player.Location;
        if (boss != null && IsPointInLunipyatiWalkableArena(current) && Core.Player.Distance2D(boss) <= 7.5f)
        {
            return current;
        }

        Vector3 reference = boss?.Location ?? current;
        float deltaX = reference.X - ArenaCenter.Lunipyati.X;
        float deltaZ = reference.Z - ArenaCenter.Lunipyati.Z;
        float length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (length < 0.01f)
        {
            deltaX = current.X - ArenaCenter.Lunipyati.X;
            deltaZ = current.Z - ArenaCenter.Lunipyati.Z;
            length = MathF.Max(0.01f, MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ)));
        }

        return new Vector3(
            ArenaCenter.Lunipyati.X + ((deltaX / length) * LunipyatiRingPathRadius),
            ArenaCenter.Lunipyati.Y,
            ArenaCenter.Lunipyati.Z + ((deltaZ / length) * LunipyatiRingPathRadius));
    }

    /// <summary>
    /// Converts a desired post-crater destination into short angular waypoints whenever the direct
    /// chord intersects the hole. This restriction applies to cleaves, proximity movement, stacks,
    /// and routine combat alike; no lower-priority destination may traverse removed floor.
    /// </summary>
    private Vector3 GetLunipyatiMovementWaypoint(Vector3 desired)
    {
        Vector3 clamped = ClampToArena(
            desired,
            ArenaCenter.Lunipyati,
            LunipyatiEdgeDestinationRadius);
        if (!craterActive)
        {
            return clamped;
        }

        clamped = ClampToLunipyatiAnnulus(clamped);
        Vector3 current = Core.Player.Location;
        if (!DoesSegmentEnterLunipyatiCrater(current, clamped))
        {
            return clamped;
        }

        float currentX = current.X - ArenaCenter.Lunipyati.X;
        float currentZ = current.Z - ArenaCenter.Lunipyati.Z;
        if ((currentX * currentX) + (currentZ * currentZ) < 0.01f)
        {
            currentX = clamped.X - ArenaCenter.Lunipyati.X;
            currentZ = clamped.Z - ArenaCenter.Lunipyati.Z;
        }

        float currentAngle = MathF.Atan2(currentX, currentZ);
        float targetAngle = MathF.Atan2(
            clamped.X - ArenaCenter.Lunipyati.X,
            clamped.Z - ArenaCenter.Lunipyati.Z);
        float angularDifference = NormalizeRadians(targetAngle - currentAngle);
        float maximumStep = LunipyatiAngularWaypointDegrees * (MathF.PI / 180f);
        float step = MathF.Max(-maximumStep, MathF.Min(maximumStep, angularDifference));
        float waypointAngle = currentAngle + step;
        return new Vector3(
            ArenaCenter.Lunipyati.X + (MathF.Sin(waypointAngle) * LunipyatiRingPathRadius),
            ArenaCenter.Lunipyati.Y,
            ArenaCenter.Lunipyati.Z + (MathF.Cos(waypointAngle) * LunipyatiRingPathRadius));
    }

    /// <summary>
    /// Projects a destination into the inset surviving ring while preserving its radial direction.
    /// </summary>
    private static Vector3 ClampToLunipyatiAnnulus(Vector3 point)
    {
        float deltaX = point.X - ArenaCenter.Lunipyati.X;
        float deltaZ = point.Z - ArenaCenter.Lunipyati.Z;
        float length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        float minimumDestinationRadius = LunipyatiCraterAvoidRadius + 0.5f;
        float targetRadius = MathF.Max(
            minimumDestinationRadius,
            MathF.Min(LunipyatiEdgeDestinationRadius, length));
        if (length < 0.01f)
        {
            return new Vector3(
                ArenaCenter.Lunipyati.X,
                ArenaCenter.Lunipyati.Y,
                ArenaCenter.Lunipyati.Z + LunipyatiRingPathRadius);
        }

        return new Vector3(
            ArenaCenter.Lunipyati.X + ((deltaX / length) * targetRadius),
            ArenaCenter.Lunipyati.Y,
            ArenaCenter.Lunipyati.Z + ((deltaZ / length) * targetRadius));
    }

    /// <summary>
    /// Tests a movement chord against the hole plus a quarter-yalm routing cushion.
    /// </summary>
    private static bool DoesSegmentEnterLunipyatiCrater(Vector3 start, Vector3 end)
    {
        float startX = start.X - ArenaCenter.Lunipyati.X;
        float startZ = start.Z - ArenaCenter.Lunipyati.Z;
        float segmentX = end.X - start.X;
        float segmentZ = end.Z - start.Z;
        float segmentLengthSquared = (segmentX * segmentX) + (segmentZ * segmentZ);
        float interpolation = segmentLengthSquared < 0.0001f
            ? 0f
            : MathF.Max(0f, MathF.Min(1f,
                -((startX * segmentX) + (startZ * segmentZ)) / segmentLengthSquared));
        float closestX = startX + (segmentX * interpolation);
        float closestZ = startZ + (segmentZ * interpolation);
        float protectedRadius = LunipyatiCraterAvoidRadius + 0.25f;
        return (closestX * closestX) + (closestZ * closestZ) < protectedRadius * protectedRadius;
    }

    /// <summary>
    /// Returns whether a point lies on the currently surviving Lunipyati floor inset.
    /// </summary>
    private bool IsPointInLunipyatiWalkableArena(Vector3 point)
    {
        float distanceSquared = DistanceSquared2D(point, ArenaCenter.Lunipyati);
        if (distanceSquared > LunipyatiArenaNavigationRadius * LunipyatiArenaNavigationRadius)
        {
            return false;
        }

        return !craterActive ||
               distanceSquared >= LunipyatiCraterAvoidRadius * LunipyatiCraterAvoidRadius;
    }

    /// <summary>
    /// Returns whether a point lies inside any forecast circle using the mechanic-specific radius.
    /// Leaping Earth retains its authored five-yalm circle plus ordinary margin, while Rock Blast
    /// uses the larger observed moving-player clearance required by its rotating sequence.
    /// </summary>
    private static bool IsInsideAnyHazard(
        Vector3 point,
        IEnumerable<TimedHazardCircle> hazards,
        float hazardRadius) =>
        hazards.Any(hazard =>
            DistanceSquared2D(point, hazard.Location) <=
            hazardRadius * hazardRadius);

    /// <summary>
    /// Tests one point only against the wave whose action effect resolves next. The 36-yalm radius
    /// and 16-degree half-angle include the required half-yalm safety margin over the corroborated
    /// 35-yalm, 15-degree cones.
    /// </summary>
    private static bool IsInsideAnyDarkIICone(
        Vector3 point,
        IEnumerable<BattleCharacter> activeWave)
    {
        // All cones share one origin. Protect a small center disk because IsInsideCone's normalization
        // guard cannot classify a point exactly at the source.
        return activeWave.Any(caster =>
            DistanceSquared2D(point, caster.Location) <= 0.5f * 0.5f ||
            IsInsideCone(point, caster.Location, caster.Heading, 36f, 16f));
    }

    /// <summary>
    /// Routes toward Soul Douse's live stack target without crossing the currently resolving Dark II
    /// wave.
    /// </summary>
    /// <remarks>
    /// Dark II sectors are radial, so a safe endpoint can still have an unsafe direct chord. The
    /// sampled router approaches Soul Douse's cushioned six-yalm region through the active cone gap.
    /// </remarks>
    /// <param name="destination">Current position of Soul Douse's marked Duty Support target.</param>
    /// <param name="activeWave">Current-frame snapshot of the next resolving Dark II cone wave.</param>
    /// <param name="waypoint">A point inside the cushioned stack region, or the next sampled waypoint toward it.</param>
    /// <returns><see langword="true"/> when the stack region is reachable without entering the active cone wave.</returns>
    private static bool TryGetDarkIIStackWaypoint(
        Vector3 destination,
        BattleCharacter[] activeWave,
        out Vector3 waypoint)
    {
        Vector3 current = Core.Player.Location;
        float stackRadiusSquared = SoulDouseStackNavigationRadius * SoulDouseStackNavigationRadius;
        if (DistanceSquared2D(current, destination) <= stackRadiusSquared)
        {
            waypoint = current;
            return true;
        }

        if (activeWave.Length == 0)
        {
            float deltaX = current.X - destination.X;
            float deltaZ = current.Z - destination.Z;
            float inverseDistance = 1f / MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
            waypoint = new Vector3(
                destination.X + (deltaX * inverseDistance * SoulDouseStackNavigationRadius),
                current.Y,
                destination.Z + (deltaZ * inverseDistance * SoulDouseStackNavigationRadius));
            return true;
        }

        Func<Vector3, bool> isSafe = point =>
            DistanceSquared2D(point, ArenaCenter.OverseerKanilokka) <=
            KanilokkaSoulweavePlannerRadius * KanilokkaSoulweavePlannerRadius &&
            !IsInsideAnyDarkIICone(point, activeWave);
        return YuweyawataRouteGeometry.TryFindWaypointToRegion(
            ArenaCenter.OverseerKanilokka,
            KanilokkaSoulweavePlannerRadius,
            current,
            isSafe,
            point => DistanceSquared2D(point, destination) <= stackRadiusSquared,
            out waypoint);
    }

    /// <summary>
    /// Returns the next resolving Dark II wave; combining both interleaved sets would cover the arena.
    /// </summary>
    private static BattleCharacter[] GetActiveDarkIIWaveCasters()
    {
        BattleCharacter[] firstWave = GetCasters([EnemyAction.DarkIIAoe1]).ToArray();
        return firstWave.Length > 0
            ? firstWave
            : GetCasters([EnemyAction.DarkIIAoe2]).ToArray();
    }

    private static bool IsDarkIIFirstWaveActive() =>
        GetFirstCaster(EnemyAction.DarkIIAoe1) != null;

    private bool IsInsideRagingClaw(Vector3 point) =>
        ragingClawAnchorId != 0 &&
        IsInsideCone(point, ragingClawSource, ragingClawHeading, 45f, 90f);

    /// <summary>
    /// Tests actor-relative cone geometry using FFXIV's heading-zero-along-positive-Z convention.
    /// </summary>
    private static bool IsInsideCone(
        Vector3 point,
        Vector3 origin,
        float heading,
        float radius,
        float halfAngleDegrees)
    {
        float deltaX = point.X - origin.X;
        float deltaZ = point.Z - origin.Z;
        float distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
        if (distanceSquared > radius * radius || distanceSquared < 0.0001f)
        {
            return false;
        }

        float inverseDistance = 1f / MathF.Sqrt(distanceSquared);
        float dot = ((deltaX * inverseDistance) * MathF.Sin(heading)) +
                    ((deltaZ * inverseDistance) * MathF.Cos(heading));
        float minimumDot = MathF.Cos(halfAngleDegrees * (MathF.PI / 180f));
        return dot >= minimumDot;
    }

    /// <summary>
    /// Clamps a destination to a circular navigation inset without changing elevation.
    /// </summary>
    private static Vector3 ClampToArena(Vector3 point, Vector3 center, float radius)
    {
        float deltaX = point.X - center.X;
        float deltaZ = point.Z - center.Z;
        float length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (length <= radius || length < 0.01f)
        {
            return point;
        }

        return new Vector3(
            center.X + ((deltaX / length) * radius),
            point.Y,
            center.Z + ((deltaZ / length) * radius));
    }

    private static float DistanceSquared2D(Vector3 first, Vector3 second)
    {
        float deltaX = first.X - second.X;
        float deltaZ = first.Z - second.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private static BattleCharacter GetBoss(uint npcId) =>
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(npcId)
            .FirstOrDefault(actor => actor.IsValid && actor.IsAlive);

    private static DateTime Max(DateTime first, DateTime second) => first >= second ? first : second;

    /// <summary>
    /// Returns whether Phantom Flood is close enough to begin prepositioning or its reduced floor is
    /// already persistent. Separating this from cast start preserves earlier Soulweave resolution order.
    /// </summary>
    private bool IsPhantomFloodWindowActive(DateTime now) =>
        phantomFloodFloorActive ||
        (phantomFloodResolvesAtUtc != DateTime.MinValue &&
         now >= phantomFloodResolvesAtUtc - PhantomFloodMovementLead &&
         now < phantomFloodUntilUtc);

    /// <summary>
    /// Enables the generic five-yalm boundary only when no earlier Soulweave wave still owns the
    /// outer floor. Once Phantom Flood resolves, the physical floor always wins.
    /// </summary>
    private bool IsPhantomFloodNavigationActive(DateTime now)
    {
        if (phantomFloodFloorActive)
        {
            return true;
        }

        if (!IsPhantomFloodWindowActive(now))
        {
            return false;
        }

        if (now >= phantomFloodResolvesAtUtc)
        {
            return true;
        }

        TimedSoulweaveRing[] wave = GetActiveSoulweaveWave(now);
        return wave.Length == 0 ||
               wave.Max(forecast => forecast.ActivatesAtUtc) + ConcurrentResolutionWindow >=
               phantomFloodResolvesAtUtc;
    }

    /// <summary>
    /// Applies Phantom Flood's small floor to a Soulweave destination only after that floor exists or
    /// when their snapshots are concurrent. An earlier ring retains the standard arena for its stage.
    /// </summary>
    private bool ShouldSoulweaveUsePhantomFloodBounds(
        IReadOnlyCollection<TimedSoulweaveRing> wave,
        DateTime now)
    {
        if (phantomFloodFloorActive)
        {
            return true;
        }

        if (phantomFloodResolvesAtUtc == DateTime.MinValue || now >= phantomFloodUntilUtc)
        {
            return false;
        }

        if (now >= phantomFloodResolvesAtUtc)
        {
            return true;
        }

        return IsPhantomFloodWindowActive(now) &&
               wave.Count > 0 &&
               wave.Max(forecast => forecast.ActivatesAtUtc) + ConcurrentResolutionWindow >=
               phantomFloodResolvesAtUtc;
    }

    private bool IsNecrohazardWindowActive(DateTime now) =>
        now < necrohazardUntilUtc || Core.Player.HasAura(PlayerAura.TemporaryMisdirection);

    private static bool IsBossSubZone(SubZoneId subZoneId) => subZoneId is
        SubZoneId.CrystalQuarry or SubZoneId.SoulCenter or SubZoneId.TheDustYoke;

    private static bool IsLindblumCombat() => Core.Player.InCombat &&
        WorldManager.SubZoneId == (uint)SubZoneId.CrystalQuarry;

    private static bool IsKanilokkaCombat() => Core.Player.InCombat &&
        WorldManager.SubZoneId == (uint)SubZoneId.SoulCenter;

    /// <summary>
    /// Keeps director-latched floor geometry available across combat-end frames until leaving the
    /// boss sub-zone. Preposition timers are still armed only by combat casts; this wider scope exists
    /// solely for persistent Phantom Flood state.
    /// </summary>
    private static bool IsKanilokkaArenaScope() =>
        WorldManager.ZoneId == (uint)Data.ZoneId.YuweyawataFieldStation &&
        WorldManager.SubZoneId == (uint)SubZoneId.SoulCenter;

    private static bool IsLunipyatiCombat() => Core.Player.InCombat &&
        WorldManager.SubZoneId == (uint)SubZoneId.TheDustYoke;

    /// <summary>
    /// Keeps the authored crater and outer wall registered after Lunipyati dies so post-duty chest
    /// navigation cannot ask the mesh for a chord across the missing center. Zone and sub-zone checks
    /// make leaving the instance an independent fail-safe even before the dungeon exit callback runs.
    /// </summary>
    private bool IsLunipyatiCraterNavigationActive() =>
        craterActive &&
        WorldManager.ZoneId == (uint)Data.ZoneId.YuweyawataFieldStation &&
        WorldManager.SubZoneId == (uint)SubZoneId.TheDustYoke;

    /// <summary>
    /// Distinguishes a completed final-boss fight from a wipe when combat drops. The floor persists
    /// only for a living player in the final arena; a dead player clears the latch before revival.
    /// </summary>
    private bool ShouldRetainLunipyatiCrater(SubZoneId currentSubZoneId) =>
        craterActive &&
        Core.Player.IsAlive &&
        WorldManager.ZoneId == (uint)Data.ZoneId.YuweyawataFieldStation &&
        currentSubZoneId == SubZoneId.TheDustYoke;

    /// <summary>
    /// Clears only state and movement owned by this dungeon instance.
    /// </summary>
    /// <param name="reason">Lifecycle boundary recorded when releasing semantic movement.</param>
    /// <param name="preserveLunipyatiCrater">Whether the final-boss crater still exists for post-combat traversal.</param>
    /// <param name="preservePhantomFloodFloor">Whether the latched boss-two map record has not yet exposed its reset transition.</param>
    private void ResetEncounterState(
        string reason,
        bool preserveLunipyatiCrater = false,
        bool preservePhantomFloodFloor = false)
    {
        ReleasePriorityMovement(reason);
        lineVoltageForecasts.Clear();
        observedLineVoltageCasts.Clear();
        observedLindblumMapEffectStates.Clear();
        cellShockForecast = null;
        soulweaveForecasts.Clear();
        observedSoulweaveCasts.Clear();
        pendingSoulweaveFinishes.Clear();
        activeSoulweavePlan = null;
        if (!preservePhantomFloodFloor)
        {
            observedPhantomFloodMapEffectStates.Clear();
        }
        observedKanilokkaMapEffectStates.Clear();
        boulderDanceForecasts.Clear();
        leapingEarthCurveForecasts.Clear();
        leapingEarthFallbackImpacts.Clear();
        leapingEarthSpiralForecasts.Clear();
        rockBlastForecasts.Clear();
        ClearNecrohazardTrail();
        ClearExactNecrohazardRoute();
        caberTossMapEffectCaptureUntilUtc = DateTime.MinValue;
        if (!preservePhantomFloodFloor)
        {
            phantomFloodResolvesAtUtc = DateTime.MinValue;
            phantomFloodUntilUtc = DateTime.MinValue;
            phantomFloodMapEffectCaptureUntilUtc = DateTime.MinValue;
        }
        necrohazardUntilUtc = DateTime.MinValue;
        ragingClawUntilUtc = DateTime.MinValue;
        ragingClawJaggedEdgeUntilUtc = DateTime.MinValue;
        leapingEarthSpiralUntilUtc = DateTime.MinValue;
        rockBlastUntilUtc = DateTime.MinValue;
        beastlyRoarResolvesAtUtc = DateTime.MinValue;
        beastlyRoarUntilUtc = DateTime.MinValue;
        lastMisdirectionDiagnosticUtc = DateTime.MinValue;
        ResetMisdirectionInputGate();
        ragingClawSource = default;
        ragingClawHeading = 0f;
        ragingClawAnchorId = 0;
        beastlyRoarSource = default;
        leapingEarthSpiralAnchorId = 0;
        rockBlastAnchorId = 0;
        rockBlastTraversalDirection = 0;
        beastlyRoarAnchorId = 0;
        if (!preservePhantomFloodFloor)
        {
            phantomFloodMapEffectId = 0;
        }
        necrohazardMapEffectId = 0;
        necrohazardFloorLayout = NecrohazardFloorLayout.None;
        lastCaberTossMapEffectsFingerprint = string.Empty;
        lastMisdirectionInputAllowed = null;
        caberTossWasCasting = false;
        cellShockManualMovementActive = false;
        lineVoltageManualMovementActive = false;
        lineVoltageSolveFailureReported = false;
        lineVoltageWaveFirstActivationUtc = DateTime.MinValue;
        lineVoltageDestination = null;
        cellShockResolvesAtUtc = DateTime.MinValue;
        cellShockUntilUtc = DateTime.MinValue;
        cellShockLineVoltageDestination = null;
        pendingCellShockSelector = null;
        forcedMovementReadFailureReported = false;
        lostHopeWasCasting = false;
        soulweaveSolveFailureReported = false;
        soulweaveRouteFailureReported = false;
        soulweaveOriginFailureReported = false;
        soulweaveManualMovementActive = false;
        darkIISoulDouseManualMovementActive = false;
        if (!preservePhantomFloodFloor)
        {
            phantomFloodFloorActive = false;
        }
        kanilokkaStandardBoundsEstablished = false;
        if (!preserveLunipyatiCrater)
        {
            craterActive = false;
            craterCarveAoeResolvesAtUtc = DateTime.MinValue;
            craterNavigationResetAtUtc = DateTime.MinValue;
            craterCarveAoeWasCasting = false;
            craterNavigationResetIssued = false;
        }
    }

    /// <summary>
    /// Releases the planner's movement lease without clearing capabilities owned by other mechanics.
    /// </summary>
    private void ReleasePriorityMovement(string reason)
    {
        if (priorityMovement.Owned)
        {
            CapabilityManager.Clear(priorityMovement.Handle, CapabilityFlags.Movement, reason);
        }

        if (priorityMovement.MovementIssued && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
            MovementManager.MoveStop();
        }

        priorityMovement.Clear();
    }

    private static class EnemyNpc
    {
        // RB's NpcId is the name-sheet ID shown in live diagnostics, not the 0x4642 actor base ID.
        public const uint RawElectrope = 13622;
        public const uint LindblumZaghnal = 13623;
        public const uint OverseerKanilokka = 13634;
        public const uint Lunipyati = 13610;
    }

    private static class ArenaCenter
    {
        public static readonly Vector3 LindblumZaghnal = new(73f, 0.75f, 277f);
        public static readonly Vector3 OverseerKanilokka = new(116f, 12.5f, -66f);
        public static readonly Vector3 Lunipyati = new(34f, -88f, -710f);
    }

    /// <summary>
    /// Action IDs are grouped by boss and role. Keep choreography separate from helper geometry:
    /// action 40608 belongs to Boulder Dance despite its misleading presentation in early captures.
    /// </summary>
    private static class EnemyAction
    {
        // Lindblum Zaghnal.
        public const uint CaberToss = 40624;
        public const uint LineVoltageNarrowLong = 40625;
        public const uint LineVoltageWideShort = 41121;
        public const uint LineVoltageWideLong = 40627;
        public const uint LineVoltageNarrowShort = 41122;
        public const uint CellShock = 40626;
        public const uint LightningStormAoe = 40637;
        public const uint LightningBolt = 40638;
        public const uint Electrify = 40634;

        // Overseer Kanilokka.
        public const uint DarkSouls = 40658;
        public const uint FreeSpiritsVisual = 40639;
        public const uint FreeSpiritsAoe = 40640;
        public const uint Soulweave1 = 40641;
        public const uint Soulweave2 = 40642;
        public const uint PhantomFloodVisual = 40643;
        public const uint PhantomFloodAoe = 40644;
        public const uint DarkIIAoe1 = 40656;
        public const uint DarkIIAoe2 = 40657;
        public const uint TelltaleTears = 40649;
        public const uint LostHope = 40645;
        public const uint Necrohazard = 40646;
        public const uint SoulDouse = 40651;

        // Lunipyati.
        public const uint RagingClawVisual = 40612;
        public const uint RagingClawFirst = 40613;
        public const uint BoulderDancePrimary = 40607;
        public const uint BoulderDanceSecondary = 40608;
        public const uint JaggedEdge = 40615;
        public const uint LeapingEarthSpiral = 40661;
        public const uint LeapingEarthCurve = 40662;
        public const uint LeapingEarthImpact = 40606;
        public const uint CraterCarveVisual = 40604;
        public const uint CraterCarveAoe = 40605;
        public const uint BeastlyRoar = 40610;
        public const uint RockBlast = 40611;
        public const uint TuraliStone = 40616;
        public const uint Slabber = 40619;

        public static readonly HashSet<uint> LineVoltage =
            [LineVoltageNarrowLong, LineVoltageWideShort, LineVoltageWideLong, LineVoltageNarrowShort];
        public static readonly HashSet<uint> WideLineVoltage = [LineVoltageWideShort, LineVoltageWideLong];
        public static readonly HashSet<uint> Soulweave = [Soulweave1, Soulweave2];
        public static readonly HashSet<uint> PhantomFlood = [PhantomFloodVisual, PhantomFloodAoe];
        public static readonly HashSet<uint> NecrohazardSequence = [LostHope, Necrohazard];
        public static readonly HashSet<uint> StandardBoundsTransition =
            [FreeSpiritsVisual, FreeSpiritsAoe, Soulweave1, Soulweave2, PhantomFloodVisual,
                PhantomFloodAoe, DarkIIAoe1, DarkIIAoe2, LostHope, Necrohazard];
        public static readonly HashSet<uint> RagingClawSequence = [RagingClawVisual, RagingClawFirst];
        public static readonly HashSet<uint> BoulderDanceInitialCasts =
            [BoulderDancePrimary, BoulderDanceSecondary];
    }

    private static class PlayerAura
    {
        // Temporary Misdirection forces movement along the rotating hand direction during Necrohazard.
        public const uint TemporaryMisdirection = 3909;
    }

    /// <summary>
    /// Priority used only when action effects resolve within <see cref="ConcurrentResolutionWindow"/>.
    /// </summary>
    private enum MovementPriority
    {
        ForcedPath = 0,
        LethalGeometry = 1,
        Stack = 2,
        RingMaintenance = 3,
    }

    private enum MechanicKind
    {
        None,
        CellShockLineVoltage,
        LineVoltage,
        NecrohazardPath,
        Soulweave,
        DarkIISoulDouse,
        SoulDouse,
        RagingClaw,
        RagingClawJaggedEdge,
        LeapingEarthCurve,
        LeapingEarthSpiral,
        BeastlyRoar,
        RockBlast,
        TuraliStone,
        CraterRing,
    }

    /// <summary>
    /// One immutable planner candidate containing effect order, tie-break priority, and destination.
    /// </summary>
    private sealed record MechanicStage(
        MechanicKind Kind,
        DateTime ResolvesAtUtc,
        DateTime ActiveUntilUtc,
        MovementPriority Priority,
        Vector3? Destination,
        uint AnchorObjectId,
        string Reason);

    /// <summary>
    /// Immutable cast-start geometry for one queued Line Voltage rectangle. Location and heading
    /// must never follow the recycled helper wrapper.
    /// </summary>
    private sealed record TimedLineVoltageRectangle(
        uint CasterObjectId,
        Vector3 Location,
        float Heading,
        bool IsWide,
        DateTime ActivatesAtUtc,
        DateTime ExpiresAtUtc);

    /// <summary>
    /// One confirmed Cell Shock director selector. Stable record IDs pair the early quadrant signal
    /// with its later warning; that warning chooses either the direct point or its arena-center mirror.
    /// Keeping both points on the armed selector prevents an unrelated common state from publishing
    /// geometry and preserves the state-dependent inversion required by the encounter protocol.
    /// </summary>
    private sealed record CellShockSelector(
        uint QuadrantMapEffectId,
        uint WarningMapEffectId,
        Vector3 DirectLocation,
        Vector3 MirroredLocation)
    {
        /// <summary>
        /// Resolves the paired warning state to the Cell Shock damage origin.
        /// </summary>
        /// <param name="state">Low 16 bits of the paired warning's director state.</param>
        /// <param name="location">
        /// Receives the direct or arena-mirrored helper location when the state is recognized.
        /// </param>
        /// <returns>
        /// <see langword="true"/> for the two scoped warning states; otherwise
        /// <see langword="false"/> so unknown transitions remain fail-closed.
        /// </returns>
        public bool TryGetWarningLocation(ushort state, out Vector3 location)
        {
            if (state == CellShockDirectWarningState)
            {
                location = DirectLocation;
                return true;
            }

            if (state == CellShockMirroredWarningState)
            {
                location = MirroredLocation;
                return true;
            }

            location = default;
            return false;
        }
    }

    /// <summary>
    /// Immutable cast-start origin for one Soulweave ring. The origin must come from validated omen
    /// geometry or its measured heading projection, never actor position. <c>ExpiresAtUtc</c> is only
    /// a stale-wrapper fail-safe; normal removal follows cast-finish FIFO order.
    /// </summary>
    private sealed record TimedSoulweaveRing(
        uint CasterObjectId,
        Vector3 Origin,
        DateTime ActivatesAtUtc,
        DateTime ExpiresAtUtc);

    /// <summary>
    /// Tracks the most recently queued Soulweave cast generation for one recycled helper. Comparing
    /// both action and delayed activation time detects a new cast even when RB polling never observes
    /// an absent wrapper between generations.
    /// </summary>
    private sealed record SoulweaveCastObservation(
        uint ActionId,
        DateTime ActivatesAtUtc);

    /// <summary>
    /// Scalar delayed finish retained after RB drops a Soulweave cast wrapper early.
    /// </summary>
    private sealed record TimedSoulweaveFinish(DateTime FinishesAtUtc);

    /// <summary>
    /// Owns the current Soulweave FIFO prefix across staggered cast starts. Every observed cast finish
    /// discards this plan so the next earliest queued record defines a fresh 1.3-second risky prefix;
    /// the destination is retained only while it remains safe for that exact prefix and floor state.
    /// Movement intentionally is not keyed to a recycled helper because actor reuse must not stop the
    /// player between adjacent records.
    /// </summary>
    private sealed class SoulweaveWavePlan
    {
        public SoulweaveWavePlan(
            DateTime firstActivationUtc,
            DateTime cohortCutoffUtc)
        {
            FirstActivationUtc = firstActivationUtc;
            CohortCutoffUtc = cohortCutoffUtc;
        }

        public DateTime FirstActivationUtc { get; }
        public DateTime CohortCutoffUtc { get; }
        public DateTime ActiveUntilUtc { get; set; }
        public List<TimedSoulweaveRing> Rings { get; } = [];
        public Vector3? Destination { get; set; }
    }

    private sealed record TimedCircle(Vector3 Location, DateTime ExpiresAtUtc);

    /// <summary>
    /// One immutable damaging-circle forecast with separate activation and delayed-effect expiry.
    /// </summary>
    private sealed record TimedHazardCircle(
        Vector3 Location,
        DateTime ActivatesAtUtc,
        DateTime ExpiresAtUtc,
        uint AnchorObjectId);

    /// <summary>
    /// Scalar snapshot of one action-40662 curve; only the earliest four snapshots are expanded so
    /// the queued interleaved pattern cannot remove the current wave's safe region.
    /// </summary>
    private sealed record LeapingEarthCurveForecast(
        uint CasterObjectId,
        float Heading,
        DateTime ActivatesAtUtc,
        DateTime ExpiresAtUtc);

    /// <summary>
    /// Owns the single destination and capability lease selected by the concurrent-mechanic planner.
    /// </summary>
    private sealed class DirectedMovementState
    {
        public CapabilityManagerHandle Handle { get; } = CapabilityManager.CreateNewHandle();
        public MechanicKind Kind { get; set; }
        public Vector3 Destination { get; set; }
        public uint AnchorObjectId { get; set; }
        public bool Owned { get; set; }
        public bool MovementIssued { get; set; }

        public void Clear()
        {
            Kind = MechanicKind.None;
            Destination = default;
            AnchorObjectId = 0;
            Owned = false;
            MovementIssued = false;
        }
    }
}
