using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using NVector2 = System.Numerics.Vector2;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Recollection normal-trial logic for Zelenia.
/// </summary>
/// <remarks>
/// Rose-sector positioning, Roseblood Drop tile selection and Stock Break's marked stack target
/// still require live verification. Shock puddles depend on an observed
/// target-marker edge; the marker and conservative puddle lifetime require live validation.
/// </remarks>
public class Recollection : AbstractDungeon
{
    private static Recollection current;
    private ChrysalisMovement movement;
    private bool reportedUnsupportedMover;
    private DateTime blockedEscapeSinceUtc;
    private Vector3 blockedEscapePosition;
    private Vector3? fallbackEscapeDestination;
    private bool fallbackEscapeOwned;
    private Vector3? nativeEscapeWaypoint;
    private Vector3? nativeEscapeDetour;
    private bool nativeEscapeDetourPartial;
    private Vector3? nativeEscapeSecondLeg;
    private DateTime nextNativeDetourSearchUtc;
    // Resolve overlapping mechanics through the same movement owner.
    private sealed class TimedSnapshot
    {
        internal RecollectionTimedPlanner.Shape Shape;
        internal NVector2 Origin;
        internal float Heading;
        internal DateTime ResolvesAt;
        internal DateTime ExpiresAt;
    }
    private readonly Dictionary<ulong, TimedSnapshot> timedHazards = [];
    private readonly InterphosMovementSpeed timedMovementSpeed = new();
    private bool timedMoveOwned;
    private NVector2 timedFirst, timedFinal;
    private DateTime timedReleaseFirstAt, nextTimedSearchUtc, nextTimedEmergencyUtc;
    private int timedEmergencyCursor;
    private NVector2 timedEmergencyOrigin;
    private int timedPhase;
    private NVector2? timedMiddle;
    private bool timedPartial;
    private bool timedEmergency;
    private DateTime timedEmergencyHorizonUtc;
    private Task<RecollectionTimedPlanner.Route?> timedSearchTask;
    private NVector2 timedSearchOrigin;
    private DateTime timedSearchStartedUtc;
    private bool timedSearchHolding;
    private string timedSearchSignature;
    private string timedRejectedSignature;
    private readonly HashSet<RecollectionTimedPlanner.Route> rejectedTimedRoutes = [];

    // Shock and Roseblood Bloom can overlap later in the fight. Independent leases prevent one
    // mechanic from releasing the combat routine's movement suppression while the other is active.
    private readonly CapabilityManagerHandle shockMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle rosebloodBloomMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle stockMovementHandle = CapabilityManager.CreateNewHandle();
    private const string StockRoute = "Recollection Stock Break stack";
    private bool stockRouteOwned;
    private bool stockLeaseActive;

    private DateTime shockSpreadEndsAtUtc = DateTime.MinValue;
    private DateTime shockPuddlesExpireAtUtc = DateTime.MinValue;
    private DateTime lastShockMarkerSeenUtc = DateTime.MinValue;
    private Vector3[] shockPuddleOrigins = [];
    private bool shockMarkerWasObserved;
    private bool shockPuddlesCapturedForMarker;
    private uint shockPreviousHealth;
    private bool shockSpreadActive;
    private Vector3? shockEdgeDestination;
    private bool shockEdgeOwned;
    private bool shockPlacementOwned;
    private Vector3? shockPlacementDestination;
    private bool shockVisualWasCasting;
    private bool shockMovementLeaseActive;
    private bool rosebloodBloomMovementLeaseActive;
    private bool movingToArenaCenter;
    private bool rosebloodFloorActive;
    private uint powerBreakCaster;
    private uint powerBreakAction;
    private sealed class PowerBreakHazard { public Vector3 Origin; }
    private readonly PowerBreakHazard powerBreakHazard = new();
    private float powerBreakHeading;
    private bool powerBreakOmenLocked;
    private DateTime powerBreakCastEndsUtc;
    private DateTime powerBreakUntilUtc;
    private readonly bool[] rosePetals = new bool[6];
    private readonly bool[] roseOrbPredicted = new bool[6];
    private readonly DateTime[] roseOrbPredictionAtUtc = new DateTime[6];
    private bool rosePatternWasMarked;
    private DateTime roseTransitUntilUtc;
    private Vector3? roseTransitDestination;
    private bool roseTransitOwned;
    private DateTime nextRoseOrbSampleUtc;
    private readonly bool[] roseStagePetals = new bool[6];
    private readonly bool[] roseTailPetals = new bool[6];
    private readonly Dictionary<uint, bool[]> roseCastPetals = [];
    private uint roseStageCaster;
    private uint roseStageAction;
    private Vector3 roseStageOrigin;
    private DateTime roseStageTailUntilUtc;
    private bool roseLayoutWarned;
    private bool roseLayoutKnown;
    private Vector3? roseSafeDestination;
    private bool roseSafeMoveOwned;
    private DateTime nextRoseSafeSearchUtc;

    // RB's map-effect IDs differ from reference packet indices, and the shared cast
    // stream does not include target lock-on IDs. Keep only scalar observations so
    // a later capture can correlate sector changes and marker carriers with impacts.
    private readonly Dictionary<uint, (uint Unknown, ushort State, byte Flags)> capturedMapEffects = [];
    private readonly Dictionary<uint, string> capturedMarkers = [];
    private DateTime nextCaptureSampleUtc = DateTime.MinValue;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Recollection;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.SpecterOfTheLost];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        // Perfumed Quietus resolves after the party is knocked back and stunned, so Roseblood
        // Bloom is the last actionable cast window in which group mitigation can be applied.
        EnemyAction.RosebloodBloomKnockback,
        EnemyAction.ValorousAscensionFirstHit,
        EnemyAction.ThornedCatharsis,
    ];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        // Alexandrian Thunder IV helpers cast the circle and donut together but resolve them in
        // sequence. Publishing both at once removes the entire arena, so only the next resolving
        // helper is active; the remaining helper becomes active after the first cast finishes.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInRecollectionCombat,
            objectSelector: caster => EnemyAction.AlexandrianThunderCircleCasts.Contains(caster.CastingSpellId)
                && IsAmongNextCasters(caster, EnemyAction.AlexandrianThunderIVCasts, 1),
            // A November 5 hit at 7.9 yalms exposed edge sensitivity in the
            // sequential circle/donut; retain half a yalm for movement latency.
            radiusProducer: caster => 8.5f,
            priority: AvoidancePriority.High));

        // Shock locks persistent four-yalm circles at the marked players' positions.
        // Snapshot at the observed marker's removal rather than at a guessed time so
        // native avoidance can leave the player's own puddle as well as the others'.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => IsInRecollectionCombat() && DateTime.UtcNow < shockPuddlesExpireAtUtc,
            // The November 5 death continued taking damage 4.55 yalms from the
            // sampled center. Allow for lock-position sampling and movement latency.
            () => ArenaCenter.Zelenia, 40, _ => 5.5f, point => point,
            () => shockPuddleOrigins);

        // This encounter owns its spread lifetime; the shared helper caches a
        // separate expiry and could keep moving party avoids after the lock edge.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => IsInRecollectionCombat() && shockSpreadActive,
            objectSelector: actor => actor.ObjectId != Core.Me.ObjectId && actor.IsAlive &&
                PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId),
            radiusProducer: _ => 4.5f,
            priority: AvoidancePriority.High));

        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => EnemyAction.AlexandrianThunderDonutCasts.Contains(caster.CastingSpellId)
                && IsAmongNextCasters(caster, EnemyAction.AlexandrianThunderIVCasts, 1),
            outerRadius: 24.0f,
            innerRadius: 7.5f,
            priority: AvoidancePriority.High);

        // Power Break is a 24-yalm forward half-room cleave with a 64-yalm total width. The cast
        // heading, rather than the current target, owns which side is unsafe.
        // Holy Hazard resolves as two opposing cones followed by a second opposing pair. Selecting
        // the two earliest helpers preserves the next safe wedges instead of covering all six sectors.
        // Specter of the Lost is aimed at both tanks. Non-targets avoid the 45-degree cones while
        // the targeted tank remains in place and relies on the dedicated mitigation path.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.SpecterOfTheLost
                && caster.SpellCastInfo.TargetId != Core.Player.ObjectId,
            leashPointProducer: () => ArenaCenter.Zelenia,
            leashRadius: ArenaSafeRadius,
            rotationDegrees: 0.0f,
            radius: 50.0f,
            arcDegrees: 45.0f);

        // Thunder Slash is three sequential opposing cone pairs. Only the earliest pair is unsafe;
        // the ordered Alexandrian Thunder IV handlers above remain active at the same time.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => IsAmongNextCasters(caster, EnemyAction.ThunderSlashCasts, 2),
            leashPointProducer: () => ArenaCenter.Zelenia,
            leashRadius: ArenaSafeRadius,
            rotationDegrees: 0.0f,
            radius: 24.0f,
            arcDegrees: 60.0f);

        // Alexandrian Thunder III is authored at the helper's cast location rather than its actor
        // location. These circles are safe to model independently; rose-sector chain reactions are
        // omitted until RebornBuddy captures expose the map-effect lifecycle.
        // Queen's Crusade establishes the persistent four-yalm center hazard after the
        // transition. The November 5 capture showed later combat positioning repeatedly
        // crossing this floor; add a half-yalm safety margin for movement latency.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => rosebloodFloorActive && IsInRecollectionCombat(),
            () => ArenaCenter.Zelenia, 40, _ => 4.5f, point => point,
            () => new[] { ArenaCenter.Zelenia });

        // Candidate 22's native avoid retained input without a route for seven
        // seconds while the player stood on a petal. Petals are owned by the
        // encounter's sector planner instead; other mechanics retain native avoids.

        // Valorous Ascension summons two Briar Thorn lines at a time. Keeping only the earliest pair
        // prevents the later pair from prematurely eliminating its still-valid lanes.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => IsAmongNextCasters(caster, EnemyAction.ValorousAscensionLineCasts, 2),
            width: 8.0f,
            length: 40.0f,
            priority: AvoidancePriority.High);

        // The visible arena radius is 16 yalms. A one-yalm inset keeps path candidates away from the
        // lethal wall without clipping the six phase-two sectors more than necessary.
        AvoidanceHelpers.AddAvoidDonut(
            canRun: IsInRecollectionCombat,
            locationProducer: () => ArenaCenter.Zelenia,
            outerRadius: 90.0f,
            innerRadius: ArenaSafeRadius,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        capturedMapEffects.Clear();
        capturedMarkers.Clear();
        nextCaptureSampleUtc = DateTime.MinValue;
        rosebloodFloorActive = false;
        Array.Clear(rosePetals, 0, rosePetals.Length);
        Array.Clear(roseStagePetals, 0, roseStagePetals.Length);
        Array.Clear(roseTailPetals, 0, roseTailPetals.Length);
        roseCastPetals.Clear();
        roseStageCaster = 0;
        roseStageAction = 0;
        roseStageTailUntilUtc = default;
        roseLayoutWarned = false;
        powerBreakCaster = powerBreakAction = 0;
        powerBreakUntilUtc = DateTime.MinValue;
        ReleaseShockMovement("Leaving Recollection");
        ReleaseRosebloodBloomMovement("Leaving Recollection");
        shockVisualWasCasting = false;
        shockSpreadEndsAtUtc = DateTime.MinValue;
        shockPuddlesExpireAtUtc = DateTime.MinValue;
        lastShockMarkerSeenUtc = DateTime.MinValue;
        shockPuddleOrigins = [];
        shockMarkerWasObserved = false;
        shockPuddlesCapturedForMarker = false;
        shockPreviousHealth = 0;
        shockSpreadActive = false;

        return Task.FromResult(false);
    }

    /// <summary>Maintains the borrowed movement channel even when the combat hook yields.</summary>
    public static void CheckLifecycle()
    {
        if (current == null) return;
        if (TreeRoot.IsRunning && WorldManager.ZoneId == (uint)Data.ZoneId.Recollection &&
            Core.Me != null && !CommonBehaviors.IsLoading)
        {
            current.UpdateRosebloodFloor();
            current.UpdateRosePetals();
        }
        current.UpdateMovement();
    }

    /// <summary>Releases the encounter's borrowed channel without stopping a successor.</summary>
    public static void ResetActive()
    {
        current?.ReleaseMovement();
        current = null;
    }

    private void UpdateMovement()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)Data.ZoneId.Recollection ||
            !DutyManager.InInstance || Core.Me == null || !Core.Me.IsAlive ||
            CommonBehaviors.IsLoading || !Core.Me.InCombat)
        {
            ReleaseMovement();
            return;
        }

        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
        }
        if (movement == null && !reportedUnsupportedMover)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
            {
                reportedUnsupportedMover = true;
                Logger.Warning("[Recollection] Native escape cleanup unavailable: another mover owns input.");
                return;
            }
            // The November 5 capture showed travel continuing toward the center after
            // native escape ended. Borrow the existing input arbiter so escape cleanup
            // and ordinary waypoint admission cannot cancel a newer movement owner.
            movement = new ChrysalisMovement(Navigator.PlayerMover,
                // Repeated Shock and cone captures showed native path generation
                // failing before any mover waypoint reached the existing guard.
                // Route that failure through the same segment validator and owner.
                failedEscapeWaypoint: ValidateNativeEscapeWaypoint,
                canContinueNativeEscape: CanContinueNativeEscape,
                canMoveOrdinary: IsOrdinaryWaypointSafe,
                validateNativeWaypoint: ValidateNativeEscapeWaypoint,
                gateUnsafeNativeRecovery: true,
                prioritizeRequestedHazardExit: () => timedMoveOwned || timedSearchHolding);
            Navigator.PlayerMover = movement;
        }
        // Petal propagation snapshots must not depend on diagnostic logging
        // being enabled; capture them before constructing timed circle hazards.
        if (rosebloodFloorActive && roseLayoutKnown) CurrentRosePetalHazards();
        timedMovementSpeed.Observe(Environment.TickCount64 / 1000.0, (float)MovementManager.Speed, MovementManager.IsMoving);
        UpdateTimedHazards();
        if (UpdateTimedMove())
        {
            movement?.Update();
            return;
        }
        if (!UpdateShockEdgeEscape() && !shockPlacementOwned && !roseTransitOwned)
            UpdateStalledEscape();
        movement?.Update();
    }

    private static bool IsOrdinaryWaypointSafe(Vector3 waypoint)
    {
        Vector3 from = Core.Me.Location;
        // Unknown floor state must not be mistaken for an empty floor. Emergency
        // escape retains its separate owner; only ordinary combat travel is held.
        if (current?.rosebloodFloorActive == true && !current.roseLayoutKnown) return false;
        // Do not depend solely on the polygon avoid for the arena wall: the
        // November 5 escape capture crossed it while an avoid was registered.
        if (waypoint.Distance2D(ArenaCenter.Zelenia) >= ArenaSafeRadius) return false;
        var encounter = current;
        if (encounter != null && !encounter.TimedStraightSafe(from, waypoint)) return false;
        bool checkMarked = encounter?.rosebloodFloorActive == true && encounter.rosePetals.Any(marked => marked);
        bool[] initiallyMarked = checkMarked ? encounter.MarkedPetalsAt(from) : null;
        var leftMarked = checkMarked ? new bool[6] : null;
        int samples = Math.Max(1, (int)Math.Ceiling(from.Distance2D(waypoint) / 0.5f));
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            var point = new Vector3(from.X + (waypoint.X - from.X) * t, from.Y,
                from.Z + (waypoint.Z - from.Z) * t);
            if (AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(point)))
                return false;
            if (checkMarked)
            {
                var marked = encounter.MarkedPetalsAt(point);
                for (int sector = 0; sector < 6; sector++)
                {
                    if (marked[sector] && (!initiallyMarked[sector] || leftMarked[sector])) return false;
                    if (initiallyMarked[sector] && !marked[sector]) leftMarked[sector] = true;
                }
                if (i == samples && marked.Any(value => value)) return false;
            }
        }
        return true;
    }

    private static NVector2 PlanPoint(Vector3 p) => new(p.X, p.Z);
    private static Vector3 WorldPoint(NVector2 p, float height) => new(p.X, height, p.Y);

    private void UpdateTimedHazards()
    {
        DateTime now = DateTime.UtcNow;
        foreach (ulong key in timedHazards.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            timedHazards.Remove(key);
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false))
        {
            if (!actor.IsCasting) continue;
            bool circle = EnemyAction.AlexandrianThunderIIICasts.Contains(actor.CastingSpellId);
            bool cone = EnemyAction.HolyHazardCasts.Contains(actor.CastingSpellId);
            if (!circle && !cone) continue;
            Vector3 origin = circle ? actor.SpellCastInfo.CastLocation : actor.Location;
            // A zero helper cast location is not a real circle in this arena.
            if (circle && origin.Distance2D(ArenaCenter.Zelenia) > 30) continue;
            DateTime resolves = now + actor.SpellCastInfo.RemainingCastTime;
            ulong key = ((ulong)actor.ObjectId << 32) | actor.CastingSpellId;
            timedHazards[key] = new TimedSnapshot
            {
                Shape = circle ? RecollectionTimedPlanner.Shape.Circle : RecollectionTimedPlanner.Shape.Cone,
                Origin = PlanPoint(origin), Heading = actor.Heading, ResolvesAt = resolves,
                ExpiresAt = resolves.AddMilliseconds(1250)
            };
            if (circle && roseCastPetals.TryGetValue(actor.ObjectId, out bool[] sectors))
            {
                // Each circle detonates the connected marked sectors captured at
                // that helper's discovery, not every marked tile simultaneously.
                for (int sector = 0; sector < 6; sector++)
                {
                    if (!sectors[sector]) continue;
                    ulong petalKey = key | (1UL << 63) | ((ulong)sector << 24);
                    timedHazards[petalKey] = new TimedSnapshot
                    {
                        Shape = RecollectionTimedPlanner.Shape.Petal, Origin = PlanPoint(ArenaCenter.Zelenia),
                        Heading = (float)(Math.PI - sector * Math.PI / 3), ResolvesAt = resolves,
                        ExpiresAt = resolves.AddMilliseconds(1250)
                    };
                }
            }
        }
        if (now < powerBreakUntilUtc)
        {
            timedHazards[ulong.MaxValue] = new TimedSnapshot
            {
                Shape = RecollectionTimedPlanner.Shape.HalfRoom, Origin = PlanPoint(powerBreakHazard.Origin),
                Heading = powerBreakHeading, ResolvesAt = powerBreakCastEndsUtc, ExpiresAt = powerBreakUntilUtc
            };
        }
    }

    private RecollectionTimedPlanner.Hazard[] GetTimedHazards(DateTime now) => timedHazards.Values
        .Select(h => new RecollectionTimedPlanner.Hazard
        {
            Kind = h.Shape, Origin = h.Origin, Heading = h.Heading,
            ResolvesIn = (h.ResolvesAt - now).TotalSeconds, ExpiresIn = (h.ExpiresAt - now).TotalSeconds
        }).ToArray();

    private double TimedMaxSpeed()
    {
        // Sprint can become active while stationary, before a settled velocity is
        // available. Never assume the slower observed speed in that window.
        bool sprint = Core.Me.Auras.Any(aura => aura.Id == 50);
        return Math.Max(sprint ? 9.5 : 6.25, timedMovementSpeed.Value + 0.5);
    }

    private bool TimedStraightSafe(Vector3 from, Vector3 to)
    {
        var hazards = GetTimedHazards(DateTime.UtcNow);
        return hazards.Length == 0 || RecollectionTimedPlanner.Safe(PlanPoint(from),
            new RecollectionTimedPlanner.Route(PlanPoint(to), PlanPoint(to), 0), hazards, maxSpeed: TimedMaxSpeed());
    }

    private bool TimedSegmentSafe(NVector2 from, NVector2 to)
    {
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        Vector3 a = WorldPoint(from, Core.Me.Location.Y), b = WorldPoint(to, Core.Me.Location.Y);
        var initial = active.Where(avoid => avoid.IsPointInAvoid(a)).ToArray();
        return IsEscapeSegmentSafe(a, b, active, initial, allowMarkedTransit: true, ignoreTimed: true);
    }

    // These helpers use copied scalars only. The worker must never read RB
    // objects, managers, or UI state while the bot thread is running.
    private static bool SnapshotPetalSafe(NVector2 point, NVector2 center, bool[] petals)
    {
        double angle = Math.Atan2(point.X - center.X, point.Y - center.Y);
        double halfArc = Math.PI / 6 + Math.Asin(1.0 / 4.5);
        for (int i = 0; i < 6; i++)
        {
            double delta = angle - (Math.PI - i * Math.PI / 3);
            if (petals[i] && Math.Abs(Math.Atan2(Math.Sin(delta), Math.Cos(delta))) <= halfArc) return false;
        }
        return true;
    }

    private static bool SnapshotSegmentSafe(NVector2 from, NVector2 to, NVector2 center, bool centerActive)
    {
        float first = NVector2.Distance(from, center), previous = first;
        // Waypoints hand off within 0.35 yalms. Inflate the center exclusion
        // during search so the actual corner-cut position can still take the
        // next leg without crossing the live 4.5-yalm guard.
        const float plannedCenterRadius = 4.85f;
        int steps = Math.Max(1, (int)Math.Ceiling(NVector2.Distance(from, to) / 0.25));
        for (int i = 1; i <= steps; i++)
        {
            float radius = NVector2.Distance(NVector2.Lerp(from, to, (float)i / steps), center);
            if (first < ArenaSafeRadius && radius >= ArenaSafeRadius ||
                first >= ArenaSafeRadius && radius > previous + 0.001f ||
                centerActive && (first >= plannedCenterRadius && radius < plannedCenterRadius || first < plannedCenterRadius && radius + 0.001f < previous)) return false;
            previous = radius;
        }
        return NVector2.Distance(to, center) < ArenaSafeRadius && (!centerActive || NVector2.Distance(to, center) >= plannedCenterRadius);
    }

    private string TimedSignature() => string.Join(",", timedHazards.Keys.OrderBy(key => key)) + ":" +
        string.Join("", rosePetals.Select(marked => marked ? "1" : "0")) + ":" + roseLayoutKnown;

    private bool ValidTimedResult(NVector2 from, RecollectionTimedPlanner.Route route,
        RecollectionTimedPlanner.Hazard[] hazards)
    {
        bool Unmarked(NVector2 point) => !MarkedPetalsAt(WorldPoint(point, Core.Me.Location.Y)).Any(marked => marked);
        NVector2 middle = route.Middle ?? route.Final;
        return Unmarked(route.Final) && (route.HoldSeconds <= 0 || Unmarked(route.First)) &&
            TimedSegmentSafe(from, route.First) && TimedSegmentSafe(route.First, middle) &&
            TimedSegmentSafe(middle, route.Final) && RecollectionTimedPlanner.Safe(from, route, hazards, maxSpeed: TimedMaxSpeed());
    }

    private bool UpdateTimedMove()
    {
        // Keep the route deadline while a cast blocks movement. The mover still
        // yields to forced movement; revalidate when control returns.
        if (timedMoveOwned && movement?.IsCurrent == true && MovementManager.GenerallyOutOfControl &&
            Core.Me.IsCasting && !rosebloodBloomMovementLeaseActive && !shockSpreadActive && !shockEdgeOwned &&
            !shockPlacementOwned && !IsAnyActionCasting(EnemyAction.ShockVisualCasts) &&
            !(Core.Me.VfxContainer.IsValid && Core.Me.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == 581)))
            return true;
        if (movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            rosebloodBloomMovementLeaseActive || shockSpreadActive || shockEdgeOwned || shockPlacementOwned ||
            IsAnyActionCasting(EnemyAction.ShockVisualCasts) ||
            Core.Me.VfxContainer.IsValid && Core.Me.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == 581))
        {
            // Preserve the reason for a handoff during diagnostic captures.
            if (LoggingHelpers.MechanicDiagnosticsEnabled && (timedMoveOwned || timedSearchHolding))
                Logger.Information($"[Recollection] Timed ownership preempted current={movement?.IsCurrent} " +
                    $"control={MovementManager.GenerallyOutOfControl} bloomLease={rosebloodBloomMovementLeaseActive} " +
                    $"shockSpread={shockSpreadActive} shockEdge={shockEdgeOwned} shockPlacement={shockPlacementOwned} " +
                    $"shockCast={IsAnyActionCasting(EnemyAction.ShockVisualCasts)} casting={Core.Me.IsCasting}.");
            ReleaseTimedMove();
            return false;
        }
        DateTime now = DateTime.UtcNow;
        var hazards = GetTimedHazards(now);
        double maxSpeed = TimedMaxSpeed();
        if (hazards.Length == 0 || rosebloodFloorActive && !roseLayoutKnown)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled && (timedMoveOwned || timedSearchHolding))
                Logger.Information($"[Recollection] Timed ownership released hazards={hazards.Length} " +
                    $"floorActive={rosebloodFloorActive} layoutKnown={roseLayoutKnown}.");
            // An unknown map uses the existing party fallback, whose segment is
            // still checked against these timed hazards by TimedStraightSafe.
            ReleaseTimedMove();
            return false;
        }
        NVector2 from = PlanPoint(Core.Me.Location);
        if (timedMoveOwned)
        {
            NVector2 activeTarget = timedPhase == 0 ? timedFirst : timedPhase == 1 ? timedMiddle.Value : timedFinal;
            if (NVector2.Distance(from, activeTarget) <= 0.35f &&
                (timedPhase != 0 || now >= timedReleaseFirstAt))
            {
                if (timedPhase == 0) timedPhase = timedMiddle.HasValue ? 1 : 2;
                else if (timedPhase == 1) timedPhase = 2;
                activeTarget = timedPhase == 1 ? timedMiddle.Value : timedFinal;
                movement.RequestHazardExit(WorldPoint(activeTarget, Core.Me.Location.Y), 0.35f);
            }
            double remainingHold = timedPhase != 0 ? 0 : Math.Max(0,
                (timedReleaseFirstAt - now).TotalSeconds - NVector2.Distance(from, activeTarget) / 4.5);
            var remaining = timedPhase == 0
                ? new RecollectionTimedPlanner.Route(activeTarget, timedFinal, remainingHold, timedPartial && !timedEmergency, timedMiddle)
                : new RecollectionTimedPlanner.Route(activeTarget, timedFinal, 0, timedPartial && !timedEmergency);
            var validationHazards = timedEmergency ? hazards.Where(h => h.ResolvesIn <= (timedEmergencyHorizonUtc - now).TotalSeconds).ToArray() : hazards;
            bool finalUnmarked = !MarkedPetalsAt(WorldPoint(timedFinal, Core.Me.Location.Y)).Any(marked => marked);
            bool holdingUnmarked = timedPhase != 0 || remainingHold <= 0 ||
                !MarkedPetalsAt(WorldPoint(timedFirst, Core.Me.Location.Y)).Any(marked => marked);
            bool laterSegments = timedPhase == 0 && timedMiddle.HasValue
                ? TimedSegmentSafe(timedFirst, timedMiddle.Value) && TimedSegmentSafe(timedMiddle.Value, timedFinal)
                : timedPhase != 2 && TimedSegmentSafe(activeTarget, timedFinal) || timedPhase == 2;
            bool activeSegmentSafe = TimedSegmentSafe(from, activeTarget);
            bool timingSafe = RecollectionTimedPlanner.Safe(from, remaining, validationHazards, 0, maxSpeed,
                timedPhase == 0 && timedReleaseFirstAt != DateTime.MinValue
                    ? Math.Max(0, (timedReleaseFirstAt - now).TotalSeconds) : null);
            bool keep = finalUnmarked && holdingUnmarked && activeSegmentSafe && laterSegments && timingSafe;
            if (!keep && LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                // Live avoids and control locks can invalidate an offline route.
                Logger.Information($"[Recollection] Timed handoff rejected phase={timedPhase} " +
                    $"finalUnmarked={finalUnmarked} holdingUnmarked={holdingUnmarked} " +
                    $"activeSegmentSafe={activeSegmentSafe} laterSegmentsSafe={laterSegments} timingSafe={timingSafe} " +
                    $"from={from} target={activeTarget} final={timedFinal} " +
                    $"hold={remainingHold:F2} releaseIn={(timedReleaseFirstAt - now).TotalSeconds:F2} " +
                    $"casting={Core.Me.IsCasting} hazards={hazards.Length} avoids={AvoidanceManager.Avoids.Count()}.");
            }
            bool inTransit = timedPhase != 2 || NVector2.Distance(from, timedFinal) > 0.35f;
            if (keep && (!timedPartial || inTransit || now < nextTimedSearchUtc)) return true;
            // A partial is only a first-stage fallback. Keep its ownership while
            // looking for a complete successor; release it only if invalidated.
            if (!keep) ReleaseTimedMove();
        }
        var stationary = new RecollectionTimedPlanner.Route(from, from, 0);
        if (!timedMoveOwned && RecollectionTimedPlanner.Safe(from, stationary, hazards, maxSpeed: maxSpeed))
        {
            ReleaseTimedMove();
            return false;
        }
        string signature = TimedSignature();
        if (signature != timedRejectedSignature)
        {
            rejectedTimedRoutes.Clear();
            timedEmergencyCursor = 0;
            timedRejectedSignature = signature;
        }
        RecollectionTimedPlanner.Route? route;
        bool acquiredEmergency = false;
        // A new or approaching hit must be able to interrupt a pending snapshot
        // search. Never leave the player pinned while an urgent exit is known.
        bool urgentNow = hazards.Any(h => h.ResolvesIn <= 4.5 && RecollectionTimedPlanner.Contains(h, from));
        if (!timedMoveOwned && urgentNow && now >= nextTimedEmergencyUtc)
        {
            nextTimedEmergencyUtc = now.AddMilliseconds(50);
            bool LiveSafe(NVector2 point) => !MarkedPetalsAt(WorldPoint(point, Core.Me.Location.Y)).Any(marked => marked);
            if (NVector2.Distance(from, timedEmergencyOrigin) > 0.5f) timedEmergencyCursor = 0;
            timedEmergencyOrigin = from;
            var quick = RecollectionTimedPlanner.FindEmergency(from, PlanPoint(ArenaCenter.Zelenia), hazards,
                TimedSegmentSafe, LiveSafe, timedEmergencyCursor, out int nextCursor, maxSpeed);
            timedEmergencyCursor = nextCursor;
            if (quick.HasValue)
            {
                timedSearchTask = null;
                if (timedSearchHolding) { timedSearchHolding = false; movement.Request(null, 0); }
                route = quick;
                acquiredEmergency = true;
                goto AcquireTimedRoute;
            }
        }
        if (timedSearchTask != null)
        {
            if (!timedSearchTask.IsCompleted) return timedMoveOwned || timedSearchHolding;
            var completed = timedSearchTask;
            timedSearchTask = null;
            try { route = completed.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                Logger.Warning($"[Recollection] Timed snapshot search failed: {error.Message}");
                ReleaseTimedMove();
                return false;
            }
            if (timedSearchHolding)
            {
                timedSearchHolding = false;
                if (!timedMoveOwned) movement.Request(null, 0);
            }
            if (timedSearchSignature != signature || NVector2.Distance(from, timedSearchOrigin) > 0.5f)
            {
                nextTimedSearchUtc = default;
                return timedMoveOwned;
            }
            if (!route.HasValue)
            {
                Logger.Warning("[Recollection] No verified timed route for overlapping hazards.");
                return timedMoveOwned;
            }
            // Preserve the planned absolute release time after worker latency;
            // every geometry and timing constraint is still rechecked below.
            var originalRoute = route.Value;
            if (originalRoute.HoldSeconds > 0)
                route = originalRoute with { HoldSeconds = Math.Max(0, originalRoute.HoldSeconds - (now - timedSearchStartedUtc).TotalSeconds) };
            // All live avoids, floor changes, and remaining deadlines are checked
            // again on the bot thread before the worker result may own input.
            if (!ValidTimedResult(from, route.Value, hazards))
            {
                rejectedTimedRoutes.Add(originalRoute);
                nextTimedSearchUtc = default;
                return timedMoveOwned;
            }
        }
        else
        {
            if (now < nextTimedSearchUtc) return timedMoveOwned;
            nextTimedSearchUtc = now.AddMilliseconds(500);
            NVector2 center = PlanPoint(ArenaCenter.Zelenia);
            bool centerActive = rosebloodFloorActive;
            bool[] petals = (bool[])rosePetals.Clone();
            var rejected = new HashSet<RecollectionTimedPlanner.Route>(rejectedTimedRoutes);
            timedSearchOrigin = from;
            timedSearchStartedUtc = now;
            timedSearchSignature = signature;
            var capturedHazards = hazards;
            // When already in another avoid or on a marked tile, do not pin the
            // player in place while searching. Otherwise hold only this owner's
            // movement so a concurrent ordinary request cannot stale the snapshot.
            bool insideNative = AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location));
            if (!timedMoveOwned && !insideNative && SnapshotPetalSafe(from, center, petals))
            {
                ReleaseStalledEscape(); ReleaseStockMovement(); ReleaseRoseSafeMove();
                ReleaseRoseTransit(); ReleaseShockPlacement(); ReleaseShockEdgeEscape();
                timedSearchHolding = true;
                movement.RequestHazardExit(Core.Me.Location, 0.1f);
            }
            timedSearchTask = Task.Run(() => RecollectionTimedPlanner.Find(from, center, capturedHazards,
                (a, b) => SnapshotSegmentSafe(a, b, center, centerActive),
                point => SnapshotPetalSafe(point, center, petals), candidate => !rejected.Contains(candidate), maxSpeed));
            return timedMoveOwned || timedSearchHolding;
        }
        AcquireTimedRoute:
        if (timedMoveOwned && route.Value.Partial && NVector2.Distance(route.Value.First, timedFirst) < 0.35f)
            return true;
        ReleaseStalledEscape();
        ReleaseStockMovement();
        ReleaseRoseSafeMove();
        ReleaseRoseTransit();
        ReleaseShockPlacement();
        ReleaseShockEdgeEscape();
        timedPartial = route.Value.Partial;
        timedEmergency = acquiredEmergency;
        // Keep the emergency horizon anchored to acquisition; a moving horizon
        // cancels an escape before it can start as later casts enter the window.
        if (acquiredEmergency) timedEmergencyHorizonUtc = now.AddSeconds(4.5);
        timedFirst = route.Value.First;
        timedFinal = route.Value.Final;
        timedMiddle = route.Value.Middle;
        timedPhase = !timedMiddle.HasValue && NVector2.Distance(timedFirst, timedFinal) < 0.01f ? 2 : 0;
        // A no-hold intermediate may be on a marked tile: hand off immediately
        // at arrival rather than waiting there for the conservative speed estimate.
        timedReleaseFirstAt = route.Value.HoldSeconds <= 0 ? DateTime.MinValue :
            now.AddSeconds(0.8 + NVector2.Distance(from, timedFirst) / 4.5 + route.Value.HoldSeconds);
        timedMoveOwned = true;
        movement.RequestHazardExit(WorldPoint(timedFirst, Core.Me.Location.Y), 0.35f);
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Information($"[Recollection] Timed escape first={timedFirst} final={timedFinal} middle={route.Value.Middle} hold={route.Value.HoldSeconds:F1}s partial={route.Value.Partial}.");
        return true;
    }

    private void ReleaseTimedMove()
    {
        if (timedMoveOwned || timedSearchHolding) movement?.Request(null, 0);
        timedMoveOwned = false;
        timedSearchHolding = false;
        timedSearchTask = null;
    }

    private void ReleaseMovement()
    {
        if (LoggingHelpers.MechanicDiagnosticsEnabled && (timedMoveOwned || timedSearchHolding))
            Logger.Information($"[Recollection] Timed ownership reset running={TreeRoot.IsRunning} " +
                $"zone={WorldManager.ZoneId} inDuty={DutyManager.InInstance} alive={Core.Me?.IsAlive} " +
                $"combat={Core.Me?.InCombat} loading={CommonBehaviors.IsLoading}.");
        timedMoveOwned = false;
        timedSearchHolding = false;
        timedSearchTask = null;
        rejectedTimedRoutes.Clear();
        timedHazards.Clear();
        timedMovementSpeed.Reset();
        ReleaseShockPlacement();
        ReleaseShockEdgeEscape();
        ReleaseStalledEscape();
        ReleaseStockMovement();
        ReleaseRoseSafeMove();
        ReleaseRoseTransit();
        movement?.Detach();
        movement = null;
        nativeEscapeWaypoint = nativeEscapeDetour = null;
        nativeEscapeSecondLeg = null;
        nativeEscapeDetourPartial = false;
        nextNativeDetourSearchUtc = default;
        nextRoseSafeSearchUtc = default;
        reportedUnsupportedMover = false;
        movingToArenaCenter = false;
    }

    private void ReleaseStalledEscape()
    {
        if (fallbackEscapeOwned) movement?.Request(null, 0);
        fallbackEscapeOwned = false;
        fallbackEscapeDestination = null;
        blockedEscapeSinceUtc = default;
    }

    private void UpdateStalledEscape()
    {
        if (movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid || rosebloodBloomMovementLeaseActive ||
            !(DateTime.UtcNow < shockPuddlesExpireAtUtc ||
              IsAnyActionCasting(EnemyAction.HolyHazardCasts) ||
              IsAnyActionCasting(EnemyAction.ThunderSlashCasts) ||
              // The November 5 capture repeatedly failed to generate an inward
              // route for Thunder IV's donut before a navigation request existed.
              IsAnyActionCasting(EnemyAction.AlexandrianThunderIVCasts) ||
              rosePetals.Any(active => active)))
        {
            ReleaseStalledEscape();
            return;
        }

        Vector3 from = Core.Me.Location;
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        if (initial.Length == 0)
        {
            ReleaseStalledEscape();
            return;
        }
        if (fallbackEscapeOwned)
        {
            if (fallbackEscapeDestination.HasValue &&
                IsEscapeSegmentSafe(from, fallbackEscapeDestination.Value, active, initial))
                return;
            ReleaseStalledEscape();
        }
        if (blockedEscapeSinceUtc == default || from.Distance2D(blockedEscapePosition) > 0.75f)
        {
            blockedEscapeSinceUtc = DateTime.UtcNow;
            blockedEscapePosition = from;
            return;
        }
        if (DateTime.UtcNow - blockedEscapeSinceUtc < TimeSpan.FromMilliseconds(700)) return;

        // Native avoidance repeatedly returned no path for Shock puddles and the
        // Holy Hazard cones and petal sectors on November 5, despite a geometric gap among the
        // captured puddles. Only authorize a short straight escape when every
        // sampled point avoids new hazards and never re-enters a departed one.
        for (float distance = 1.0f; distance <= 10.0f; distance += 0.5f)
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI / 12;
            var destination = new Vector3(from.X + distance * (float)Math.Sin(angle), from.Y,
                from.Z + distance * (float)Math.Cos(angle));
            if (!IsEscapeSegmentSafe(from, destination, active, initial)) continue;
            ReleaseStockMovement();
            ReleaseRoseSafeMove();
            fallbackEscapeDestination = destination;
            fallbackEscapeOwned = true;
            movement.RequestHazardExit(destination, 0.2f);
            Logger.Information($"[Recollection] Native escape stalled; taking validated short exit to {destination}.");
            return;
        }
        // Avoid scanning every bot pulse when overlapping hazards have no sampled exit.
        blockedEscapeSinceUtc = DateTime.UtcNow.AddMilliseconds(300);
    }

    private static bool IsEscapeSegmentSafe(Vector3 from, Vector3 destination, Avoid[] active, Avoid[] initial, bool requireExit = true, bool allowMarkedTransit = false, bool ignoreTimed = false)
    {
        // Every other encounter request must respect the timed mechanics after
        // their native avoids are removed; the timed planner validates its own
        // staged route separately, so its segment-only check opts out here.
        if (!ignoreTimed && current != null && !current.TimedStraightSafe(from, destination)) return false;
        if (destination.Distance2D(ArenaCenter.Zelenia) >= ArenaSafeRadius ||
            requireExit && active.Any(avoid => avoid.IsPointInAvoid(destination))) return false;
        // The November 5 capture crossed the center bleed despite the registered
        // avoid reporting a valid segment. Enforce its observed 4-yalm hazard with
        // the same 0.5-yalm margin as the registered circle. If already inside,
        // allow only an outward departure; never route farther through the center.
        bool protectCenter = current?.rosebloodFloorActive == true;
        float initialCenterDistance = from.Distance2D(ArenaCenter.Zelenia);
        float previousCenterDistance = initialCenterDistance;
        var encounter = current;
        bool checkPetals = !allowMarkedTransit && encounter?.rosebloodFloorActive == true && encounter.rosePetals.Any(marked => marked);
        bool[] initialPetals = checkPetals ? encounter.MarkedPetalsAt(from) : null;
        var leftPetals = checkPetals ? new bool[6] : null;
        var left = new bool[initial.Length];
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(destination) / 0.25f));
        for (int step = 1; step <= steps; step++)
        {
            float t = (float)step / steps;
            var point = new Vector3(from.X + (destination.X - from.X) * t, from.Y,
                from.Z + (destination.Z - from.Z) * t);
            float centerDistance = point.Distance2D(ArenaCenter.Zelenia);
            // Never move outward through the wall, including while recovering from
            // an observed position outside the arena. Allow only an inward return.
            if (initialCenterDistance < ArenaSafeRadius && centerDistance >= ArenaSafeRadius ||
                initialCenterDistance >= ArenaSafeRadius && centerDistance > previousCenterDistance + 0.001f)
                return false;
            if (protectCenter && (initialCenterDistance >= 4.5f && centerDistance < 4.5f ||
                initialCenterDistance < 4.5f && centerDistance + 0.001f < previousCenterDistance)) return false;
            previousCenterDistance = centerDistance;
            if (checkPetals)
            {
                // A November 5 native escape crossed an inactive-but-marked tile
                // to avoid Power Break and was caught by the next petal wave.
                // Permit departure from the initial tile, but never enter another
                // marked tile or stop on any marked tile.
                var marked = encounter.MarkedPetalsAt(point);
                for (int i = 0; i < 6; i++)
                {
                    if (marked[i] && (!initialPetals[i] || leftPetals[i])) return false;
                    if (initialPetals[i] && !marked[i]) leftPetals[i] = true;
                }
                if (requireExit && step == steps && marked.Any(value => value)) return false;
            }
            foreach (var avoid in active)
            {
                bool inside = avoid.IsPointInAvoid(point);
                int index = Array.IndexOf(initial, avoid);
                if (index < 0 && inside || index >= 0 && left[index] && inside) return false;
                if (index >= 0 && !inside) left[index] = true;
            }
        }
        return true;
    }

    private bool CanContinueNativeEscape()
    {
        // Shock can require an intermediate before the rose floor is active;
        // keep that partial leg subject to the same changing-hazard checks.
        if (!nativeEscapeWaypoint.HasValue || !rosebloodFloorActive && !nativeEscapeDetourPartial) return true;
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var from = Core.Me.Location;
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        // A validated escape may cross a marked tile, but its final destination
        // must be unmarked. Candidate 23 otherwise held a native escape for
        // seconds despite Domestic confirming that crossing petals is allowed.
        if (!IsEscapeSegmentSafe(from, nativeEscapeWaypoint.Value, active, initial, !nativeEscapeDetourPartial, allowMarkedTransit: true)) return false;
        if (!nativeEscapeDetourPartial && MarkedPetalsAt(nativeEscapeWaypoint.Value).Any(marked => marked)) return false;
        if (!nativeEscapeDetourPartial) return true;
        if (nativeEscapeSecondLeg.HasValue)
        {
            var via = nativeEscapeWaypoint.Value;
            var viaInitial = active.Where(avoid => avoid.IsPointInAvoid(via)).ToArray();
            return !MarkedPetalsAt(nativeEscapeSecondLeg.Value).Any(marked => marked) &&
                IsEscapeSegmentSafe(via, nativeEscapeSecondLeg.Value, active, viaInitial, allowMarkedTransit: true);
        }
        return IsPermittedIntermediate(nativeEscapeWaypoint.Value, active);
    }

    private static bool IsPermittedIntermediate(Vector3 point, Avoid[] active)
    {
        var firstThunder = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsCasting && EnemyAction.AlexandrianThunderIIICasts.Contains(actor.CastingSpellId))
            .Select(actor => (TimeSpan?)actor.SpellCastInfo.RemainingCastTime)
            .OrderBy(time => time).FirstOrDefault();
        // Run 3 remained inside a locked Shock puddle for six seconds because
        // the only safe first step was still inside the later Power Break. Allow
        // that intermediate only when it clears every other avoid and there is
        // time for this leg plus a conservative follow-up before the cleave.
        bool shockFirst = current != null && current.shockPuddlesCapturedForMarker &&
            DateTime.UtcNow < current.shockPuddlesExpireAtUtc &&
            current.shockPuddleOrigins.Any(origin => Core.Me.Location.Distance2D(origin) < 5.5f);
        if (!firstThunder.HasValue && !shockFirst) return false;
        foreach (var avoid in active.Where(avoid => avoid.IsPointInAvoid(point)))
        {
            // Only the later Power Break may contain a temporary waypoint. Petals,
            // center floor, circles and unknown shapes must already be clear. The
            // extra 1.25 seconds preserves the observed earlier-effect tail.
            if (avoid.Object is not PowerBreakHazard || current == null)
                return false;
            var remaining = current.powerBreakCastEndsUtc - DateTime.UtcNow;
            if (shockFirst)
            {
                // At 6 yalms/s this bounds the candidate leg; two extra seconds
                // reserve a subsequent escape and reaction time before Power Break.
                var required = TimeSpan.FromSeconds(Core.Me.Location.Distance2D(point) / 6.0 + 2.0);
                if (remaining <= required) return false;
            }
            else if (remaining <= firstThunder.Value + TimeSpan.FromMilliseconds(1250)) return false;
        }
        return true;
    }

    private Vector3? ValidateNativeEscapeWaypoint(Vector3 proposed)
    {
        var from = Core.Me.Location;
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        // On November 5 the native escape repeatedly chose points within two
        // yalms of Specter's cone origin and the player died as it resolved.
        // Give non-targets room to clear either moving tank cone, subject to the
        // same full segment checks and without directing a targeted player away.
        var specterCasters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsCasting && actor.CastingSpellId == EnemyAction.SpecterOfTheLost).ToArray();
        bool specterNonTarget = specterCasters.Length > 0 &&
            specterCasters.All(actor => actor.SpellCastInfo.TargetId != Core.Player.ObjectId);
        bool Safe(Vector3 point) => TimedStraightSafe(from, point) && (!specterNonTarget || point.Distance2D(ArenaCenter.Zelenia) >= 5.0f) &&
            !MarkedPetalsAt(point).Any(marked => marked) &&
            IsEscapeSegmentSafe(from, point, active, initial, allowMarkedTransit: true);
        bool SafePartial(Vector3 point) => TimedStraightSafe(from, point) && !MarkedPetalsAt(point).Any(marked => marked) &&
            IsEscapeSegmentSafe(from, point, active, initial, false, allowMarkedTransit: true) &&
            IsPermittedIntermediate(point, active);
        // A verified two-leg escape must actually hand off to its second leg.
        // Candidate 18 held inside overlapping hazards after redirected waypoints;
        // inspection also found that this handoff discarded a planned continuation.
        // Recheck the remaining leg against current hazards before handing it off.
        if (nativeEscapeDetour.HasValue && nativeEscapeSecondLeg.HasValue &&
            from.Distance2D(nativeEscapeDetour.Value) <= 0.5f && Safe(nativeEscapeSecondLeg.Value))
        {
            nativeEscapeDetour = nativeEscapeSecondLeg;
            nativeEscapeWaypoint = nativeEscapeSecondLeg;
            nativeEscapeSecondLeg = null;
            nativeEscapeDetourPartial = false;
            Logger.Information($"[Recollection] Continuing verified native escape to {nativeEscapeDetour.Value}.");
            return nativeEscapeDetour;
        }
        // Keep a selected detour stable until arrival; a changing hazard can revoke
        // it immediately. This prevents a new nearest-point choice on every pulse.
        if (nativeEscapeDetour.HasValue && from.Distance2D(nativeEscapeDetour.Value) > 0.5f &&
            (nativeEscapeSecondLeg.HasValue ? CanContinueNativeEscape() :
                nativeEscapeDetourPartial ? SafePartial(nativeEscapeDetour.Value) : Safe(nativeEscapeDetour.Value)))
        {
            nativeEscapeWaypoint = nativeEscapeDetour;
            return nativeEscapeDetour;
        }
        nativeEscapeDetour = null;
        nativeEscapeSecondLeg = null;
        nativeEscapeDetourPartial = false;
        if (Safe(proposed))
        {
            nativeEscapeWaypoint = proposed;
            return proposed;
        }
        if (DateTime.UtcNow < nextNativeDetourSearchUtc) return null;
        nextNativeDetourSearchUtc = DateTime.UtcNow.AddMilliseconds(500);

        Vector3? best = null;
        float bestScore = float.MaxValue;
        // A half-room cleave can put the nearest safe point more than ten
        // yalms away; search the full arena diameter rather than treating an
        // arbitrary short range as proof that no safe departure exists.
        for (float distance = 2; distance <= 30; distance += 2)
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI / 12;
            var point = new Vector3(from.X + distance * (float)Math.Sin(angle), from.Y,
                from.Z + distance * (float)Math.Cos(angle));
            if (!Safe(point)) continue;
            float score = point.Distance2D(proposed) + distance * 0.1f;
            if (score >= bestScore) continue;
            best = point;
            bestScore = score;
        }
        if (!best.HasValue && rosebloodFloorActive && roseLayoutKnown && initial.Length > 0)
        {
            // A direct escape can be blocked by the center island even though a
            // two-leg route around it exists. The first leg may remain in an
            // initial hazard, but cannot enter another; the second must leave all
            // hazards and marked petals. Revalidate both as the arena changes.
            var targets = new List<Vector3>();
            for (int sector = 0; sector < 6; sector++)
            {
                if (rosePetals[sector]) continue;
                double heading = Math.PI - sector * Math.PI / 3;
                foreach (float radius in new[] { 6.5f, 9.0f, 12.0f, 13.5f })
                    targets.Add(new Vector3(ArenaCenter.Zelenia.X + radius * (float)Math.Sin(heading), from.Y,
                        ArenaCenter.Zelenia.Z + radius * (float)Math.Cos(heading)));
            }
            for (float distance = 2; distance <= 8 && !best.HasValue; distance += 2)
            for (int i = 0; i < 24 && !best.HasValue; i++)
            {
                double angle = i * Math.PI / 12;
                var via = new Vector3(from.X + distance * (float)Math.Sin(angle), from.Y,
                    from.Z + distance * (float)Math.Cos(angle));
                if (MarkedPetalsAt(via).Any(marked => marked) ||
                    !IsEscapeSegmentSafe(from, via, active, initial, false, allowMarkedTransit: true)) continue;
                var viaInitial = active.Where(avoid => avoid.IsPointInAvoid(via)).ToArray();
                var target = targets.Where(point => !MarkedPetalsAt(point).Any(marked => marked) &&
                    IsEscapeSegmentSafe(via, point, active, viaInitial, allowMarkedTransit: true))
                    .OrderBy(point => point.Distance2D(via)).Select(point => (Vector3?)point).FirstOrDefault();
                if (!target.HasValue) continue;
                best = via;
                nativeEscapeSecondLeg = target;
                nativeEscapeDetourPartial = true;
            }
        }
        if (!best.HasValue && initial.Length > 0)
        {
            // An earlier sector can resolve before a later half-room cleave. A
            // short intermediate leg may remain inside the hazard already owned
            // by native escape, provided it enters no new hazard, never re-enters
            // one already left, and advances toward the native destination.
            float originalDistance = from.Distance2D(proposed);
            for (float distance = 2; distance <= 8; distance += 2)
            for (int i = 0; i < 24; i++)
            {
                double angle = i * Math.PI / 12;
                var point = new Vector3(from.X + distance * (float)Math.Sin(angle), from.Y,
                    from.Z + distance * (float)Math.Cos(angle));
                float score = point.Distance2D(proposed);
                if (score >= originalDistance - 0.5f || score >= bestScore || !SafePartial(point)) continue;
                best = point;
                bestScore = score;
            }
            nativeEscapeDetourPartial = best.HasValue;
        }
        nativeEscapeWaypoint = nativeEscapeDetour = best;
        if (best.HasValue)
            Logger.Information($"[Recollection] Redirecting unsafe native escape waypoint {proposed} via {best.Value}.");
        else
            Logger.Warning("[Recollection] No verified native escape segment is available; holding owned input.");
        return best;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Party ready checks can occur before the boss is pulled, so handle them
        // before the combat-only branch while preserving normal combat scheduling.
        await UsefulTasks.HandleReadyCheck();
        CaptureUnresolvedSignals();
        UpdateRosebloodFloor();
        UpdateRosePetals();
        UpdatePowerBreak();
        // The ordered helper casts above must have one geometry owner. SideStep can publish both
        // halves of an in/out or sequential cone set simultaneously, leaving no navigable point.
        SidestepPlugin.Enabled = false;

        if (!IsInRecollectionCombat())
        {
            powerBreakCaster = powerBreakAction = 0;
            powerBreakUntilUtc = DateTime.MinValue;
            // Helper object IDs can be reused after a wipe; no cast snapshot from
            // the previous pull may define the next pull's petal propagation.
            roseCastPetals.Clear();
            ReleaseShockPlacement();
            ReleaseShockEdgeEscape();
            ReleaseShockMovement("Recollection combat ended");
            ReleaseRosebloodBloomMovement("Recollection combat ended");
            ReleaseRoseTransit();
            shockVisualWasCasting = false;
            shockSpreadEndsAtUtc = DateTime.MinValue;
            shockPuddlesExpireAtUtc = DateTime.MinValue;
            lastShockMarkerSeenUtc = DateTime.MinValue;
            shockPuddleOrigins = [];
            shockMarkerWasObserved = false;
            shockPuddlesCapturedForMarker = false;
            shockPreviousHealth = 0;
            shockSpreadActive = false;
            return false;
        }

        await TankBusterSpells();
        await DamageMitigationSpells();
        // Shock still samples marker edges even during another mechanic; otherwise
        // a puddle could be omitted while the timed route owns movement.
        HandleShockSpread();
        // Keep the encounter planner's movement claim while allowing healing and
        // rotation hooks to run; native urgent avoids can still preempt the owner.
        if (timedMoveOwned || timedSearchHolding) return false;
        // Relinquish the transit before a newly detected Shock can acquire input.
        bool roseTransit = HandleRoseTransit();

        if (roseTransit) return true;
        if (shockEdgeOwned || shockPlacementOwned) return true;
        if (fallbackEscapeOwned) return true;
        if (await HandleRosebloodBloomKnockbackAsync()) return true;
        if (HandleStockBreak()) return true;
        return HandleRoseSafePosition();
    }

    private void UpdatePowerBreak()
    {
        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsCasting && EnemyAction.PowerBreakCasts.Contains(actor.CastingSpellId));
        if (caster == null)
        {
            powerBreakCaster = 0;
            powerBreakAction = 0;
            powerBreakOmenLocked = false;
            return;
        }
        if (caster.ObjectId != powerBreakCaster || caster.CastingSpellId != powerBreakAction)
        {
            // The boss can rotate while another mechanic is resolving. Snapshot
            // the cast-discovery direction, rather than changing a locked cleave.
            powerBreakCaster = caster.ObjectId;
            powerBreakAction = caster.CastingSpellId;
            powerBreakHazard.Origin = caster.Location;
            powerBreakOmenLocked = false;
            // Fallback while the omen is not populated. Three observed 43113
            // hits were behind the actor's discovery heading.
            powerBreakHeading = caster.Heading + (caster.CastingSpellId == 43113 ? (float)Math.PI : 0f);
        }
        if (!powerBreakOmenLocked)
        {
            var omen = caster.OmenMatrix;
            double length = Math.Sqrt(omen.M20 * omen.M20 + omen.M22 * omen.M22);
            // November 5 telemetry showed the 24-yalm forward omen basis pointed
            // east while the boss faced north; the cleave hit on the omen side.
            // Snapshot only a populated, correctly scaled matrix at the caster.
            if (length >= 20 && length <= 28 && omen.Center.Distance2D(caster.Location) <= 1)
            {
                powerBreakHazard.Origin = omen.Center;
                powerBreakHeading = (float)Math.Atan2(omen.M20, omen.M22);
                powerBreakOmenLocked = true;
            }
        }
        // The recorded effect arrived up to about 1.2 seconds after cast expiry.
        powerBreakCastEndsUtc = DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime;
        powerBreakUntilUtc = powerBreakCastEndsUtc + TimeSpan.FromMilliseconds(1250);
    }

    // The orb warning can isolate the player's wedge before Thunder III begins.
    // Candidate 20 captured a player crossing one marked wedge without sampled HP
    // loss before the helper casts. This is a bounded inference, not proof that
    // marked floor is generally safe: abort on any helper, new avoid, or timeout.
    private bool HandleRoseTransit()
    {
        DateTime now = DateTime.UtcNow;
        bool interrupted = !rosebloodFloorActive || !roseLayoutKnown || movement?.IsCurrent != true ||
            MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid ||
            rosebloodBloomMovementLeaseActive || shockSpreadActive || shockEdgeOwned || shockPlacementOwned ||
            stockLeaseActive || IsAnyActionCasting(EnemyAction.AlexandrianThunderIIICasts) ||
            IsAnyActionCasting(EnemyAction.AlexandrianThunderIVCasts) || IsAnyActionCasting(EnemyAction.ShockVisualCasts) ||
            now < roseStageTailUntilUtc ||
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor => actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.RosebloodBloomKnockback) ||
            PartyManager.VisibleMembers.Select(member => member.BattleCharacter).Concat(new BattleCharacter[] { Core.Me })
                .Any(actor => actor != null && actor.IsValid && actor.IsAlive && actor.VfxContainer.IsValid &&
                    actor.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && (marker.Id == 581 || marker.Id == 590)));
        // Only Recollection's int-indexed petal polygons are temporarily removed;
        // center, wall, cleaves, circles, and every other avoid still gate the route.
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore &&
            !(avoid.GetType().Name == "AvoidPolygon`1" && avoid.Object is int)).ToArray();
        Vector3 from = Core.Me.Location;
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        interrupted |= initial.Length > 0;
        bool Safe(Vector3 point) => !MarkedPetalsAt(point).Any(value => value) &&
            IsEscapeSegmentSafe(from, point, active, initial, allowMarkedTransit: true);

        if (roseTransitOwned)
        {
            if (interrupted || now >= roseTransitUntilUtc || !roseTransitDestination.HasValue ||
                from.Distance2D(roseTransitDestination.Value) <= 0.5f || !Safe(roseTransitDestination.Value))
            {
                ReleaseRoseTransit();
                return false;
            }
            return true;
        }

        var underPlayer = MarkedPetalsAt(from);
        if (interrupted || !Enumerable.Range(0, 6).Any(i => roseOrbPredicted[i] && underPlayer[i] &&
            now - roseOrbPredictionAtUtc[i] <= TimeSpan.FromMilliseconds(600))) return false;
        var party = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && actor.IsVisible &&
                actor.ObjectId != Core.Me.ObjectId).Select(actor => actor.Location).ToArray();
        var candidates = new List<Vector3>();
        for (int sector = 0; sector < 6; sector++)
        {
            if (rosePetals[sector]) continue;
            double heading = Math.PI - sector * Math.PI / 3;
            // Offset points retain the existing angular clearance while allowing
            // a short early crossing toward a neighboring safe wedge.
            foreach (double offset in new[] { 0.0, -0.15, 0.15, -0.2, 0.2 })
            foreach (float radius in new[] { 5.0f, 6.5f, 9.0f, 12.0f })
                candidates.Add(new Vector3(ArenaCenter.Zelenia.X + radius * (float)Math.Sin(heading + offset), from.Y,
                    ArenaCenter.Zelenia.Z + radius * (float)Math.Cos(heading + offset)));
        }
        // A conservative four-yalm/second estimate plus half a second of latency
        // bounds an eleven-yalm route to 3.25 seconds; helper discovery preempts it.
        var destination = candidates.Where(point => from.Distance2D(point) <= 11.0f && Safe(point))
            .OrderByDescending(point => party.Count(member => member.Distance2D(point) <= 3.0f))
            .ThenBy(point => from.Distance2D(point)).Select(point => (Vector3?)point).FirstOrDefault();
        if (!destination.HasValue) return false;
        ReleaseStalledEscape();
        ReleaseRoseSafeMove();
        roseTransitDestination = destination;
        roseTransitUntilUtc = now.AddMilliseconds(3250);
        roseTransitOwned = true;
        movement.RequestHazardExit(destination.Value, 0.4f);
        movement.Update();
        Logger.Information($"[Recollection] Crossing a warned rose wedge toward {destination.Value} before helper casts.");
        return true;
    }

    private void ReleaseRoseTransit()
    {
        if (roseTransitOwned) movement?.Request(null, 0);
        roseTransitOwned = false;
        roseTransitDestination = null;
        roseTransitUntilUtc = default;
    }

    private bool HandleRoseSafePosition()
    {
        bool stackMarked = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Concat(new BattleCharacter[] { Core.Me }).Any(actor => actor != null && actor.IsValid && actor.IsAlive &&
                actor.VfxContainer.IsValid && actor.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == 590));
        if (!rosebloodFloorActive || movement?.IsCurrent != true ||
            MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid ||
            rosebloodBloomMovementLeaseActive || shockSpreadActive || stockLeaseActive || stackMarked)
        {
            ReleaseRoseSafeMove();
            return false;
        }

        if (!roseLayoutKnown) return HandleRosePartyFallback();
        if (!rosePetals.Any(marked => marked)) { ReleaseRoseSafeMove(); return false; }

        Vector3 from = Core.Me.Location;
        var initialMarked = MarkedPetalsAt(from);
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        // Domestic confirmed that marked tiles can be crossed while moving to a
        // safe tile, but must never be used as standing destinations. Other active
        // hazards and the arena boundary still constrain the complete segment.
        bool Safe(Vector3 point) => !MarkedPetalsAt(point).Any(marked => marked) &&
            IsEscapeSegmentSafe(from, point, active, initial, allowMarkedTransit: true);
        // The November 5 capture showed that releasing at the first margin crossing
        // left the player near the tile edge. Finish the verified wedge-center route,
        // unless another mechanic takes ownership or its remaining segment is unsafe.
        if (roseSafeMoveOwned && roseSafeDestination.HasValue &&
            from.Distance2D(roseSafeDestination.Value) > 0.5f && Safe(roseSafeDestination.Value))
            return true;
        ReleaseRoseSafeMove();
        if (!initialMarked.Any(marked => marked))
            return false;
        if (DateTime.UtcNow < nextRoseSafeSearchUtc) return false;
        // A blocked overlap can leave no valid gap. Retry locally without doing
        // an expensive full sector scan on every bot-thread pulse.
        nextRoseSafeSearchUtc = DateTime.UtcNow.AddMilliseconds(500);

        // Captured phase-two layouts have opposite or alternating unmarked gaps.
        // Select from every current gap, not a hard-coded compass direction: the
        // orb can mark another sector and the layout can rotate or mirror.
        var candidates = new List<Vector3>();
        for (int sector = 0; sector < 6; sector++)
        {
            if (rosePetals[sector]) continue;
            double heading = Math.PI - sector * Math.PI / 3;
            // Center-only candidates can all intersect an overlapping mechanic even
            // when a checked point nearer the wedge edge is reachable. The existing
            // segment guard still enforces petal clearance and every active avoid.
            foreach (double offset in new[] { 0.0, -0.1, 0.1, -0.2, 0.2 })
            foreach (float radius in new[] { 6.5f, 9.0f, 12.0f, 13.5f })
                candidates.Add(new Vector3(ArenaCenter.Zelenia.X + radius * (float)Math.Sin(heading + offset), from.Y,
                    ArenaCenter.Zelenia.Z + radius * (float)Math.Cos(heading + offset)));
        }
        var destination = candidates.Where(Safe).OrderBy(point => point.Distance2D(from))
            .Select(point => (Vector3?)point).FirstOrDefault();
        if (!destination.HasValue)
        {
            // A straight path can cross the central hazard even when a path around
            // the ring exists. Commit only the first checked leg; replan after its
            // arrival or any changed hazard rather than retaining a stale second leg.
            var viaPoints = new List<Vector3>();
            foreach (float radius in new[] { 6.5f, 9.0f, 12.0f, 13.5f })
            for (int i = 0; i < 48; i++)
            {
                double angle = i * Math.PI / 24;
                var via = new Vector3(ArenaCenter.Zelenia.X + radius * (float)Math.Sin(angle), from.Y,
                    ArenaCenter.Zelenia.Z + radius * (float)Math.Cos(angle));
                // An intermediate waypoint can be held while the next leg is
                // revalidated, so it must itself be clear of marked petals.
                if (Safe(via) && from.Distance2D(via) > 0.5f) viaPoints.Add(via);
            }
            destination = viaPoints.OrderBy(via => from.Distance2D(via))
                .Where(via => candidates.Any(target =>
                {
                    var viaInitial = active.Where(avoid => avoid.IsPointInAvoid(via)).ToArray();
                    return !MarkedPetalsAt(target).Any(marked => marked) &&
                        IsEscapeSegmentSafe(via, target, active, viaInitial, allowMarkedTransit: true);
                }))
                .Select(via => (Vector3?)via).FirstOrDefault();
        }
        if (!destination.HasValue) return HandleRosePartyFallback();

        roseSafeDestination = destination;
        roseSafeMoveOwned = true;
        // This explicit departure may start inside a marked sector; the entire
        // segment was checked against both active avoids and marked-sector edges.
        movement.RequestHazardExit(destination.Value, 0.4f);
        movement.Update();
        Logger.Information($"[Recollection] Leaving marked rose sector for {destination.Value}.");
        return true;
    }

    // A clustered living party is a fallback signal, not proof of safe floor.
    // Never follow a lone player or override spread, knockback, stack or native
    // avoidance; validate the whole segment against the currently known hazards.
    private bool HandleRosePartyFallback()
    {
        var members = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && actor.IsVisible &&
                actor.ObjectId != Core.Me.ObjectId)
            .GroupBy(actor => actor.ObjectId).Select(group => group.First().Location).ToArray();
        int minimum = Math.Max(2, (members.Length + 1) / 2 + 1);
        Vector3 from = Core.Me.Location;
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        var target = members.Select(point => new { Point = point, Count = members.Count(other => other.Distance2D(point) <= 3.0f) })
            .Where(group => group.Count >= minimum && IsEscapeSegmentSafe(from, group.Point, active, initial))
            .OrderByDescending(group => group.Count).ThenBy(group => group.Point.Distance2D(from))
            .Select(group => (Vector3?)group.Point).FirstOrDefault();
        if (!target.HasValue || from.Distance2D(target.Value) <= 1.5f)
        {
            ReleaseRoseSafeMove();
            return false;
        }
        if (roseSafeMoveOwned && roseSafeDestination.HasValue &&
            roseSafeDestination.Value.Distance2D(target.Value) < 1.0f &&
            IsEscapeSegmentSafe(from, roseSafeDestination.Value, active, initial)) return true;
        ReleaseRoseSafeMove();
        roseSafeDestination = target;
        roseSafeMoveOwned = true;
        movement.RequestHazardExit(target.Value, 1.0f);
        movement.Update();
        Logger.Information($"[Recollection] Following a clustered party to {target.Value} while floor safety is uncertain.");
        return true;
    }

    private bool[] MarkedPetalsAt(Vector3 point)
    {
        double dx = point.X - ArenaCenter.Zelenia.X, dz = point.Z - ArenaCenter.Zelenia.Z;
        double angle = Math.Atan2(dx, dz);
        // Two November 5 attempts stopped just outside the half-yalm avoid
        // margin and were hit by successive Thunder III waves. Require a full
        // yalm of angular clearance when choosing a standing position so native
        // avoidance's nearest-edge exit is followed by a move into the wedge.
        double halfArc = Math.PI / 6 + Math.Asin(1.0 / 4.5);
        var result = new bool[6];
        for (int i = 0; i < 6; i++)
        {
            double difference = Math.Atan2(Math.Sin(angle - (Math.PI - i * Math.PI / 3)),
                Math.Cos(angle - (Math.PI - i * Math.PI / 3)));
            result[i] = rosePetals[i] && Math.Abs(difference) <= halfArc;
        }
        return result;
    }

    private bool IsMarkedPetalDepartureSafe(Vector3 from, Vector3 destination, bool[] initial)
    {
        if (MarkedPetalsAt(destination).Any(marked => marked)) return false;
        var left = new bool[6];
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(destination) / 0.25f));
        for (int step = 1; step <= steps; step++)
        {
            float t = (float)step / steps;
            var point = new Vector3(from.X + (destination.X - from.X) * t, from.Y,
                from.Z + (destination.Z - from.Z) * t);
            var marked = MarkedPetalsAt(point);
            for (int i = 0; i < 6; i++)
            {
                if (marked[i] && (!initial[i] || left[i])) return false;
                if (initial[i] && !marked[i]) left[i] = true;
            }
        }
        return true;
    }

    private void ReleaseRoseSafeMove()
    {
        if (roseSafeMoveOwned) movement?.Request(null, 0);
        roseSafeMoveOwned = false;
        roseSafeDestination = null;
    }

    private bool HandleStockBreak()
    {
        var party = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Concat(new BattleCharacter[] { Core.Me })
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && actor.IsVisible)
            .GroupBy(actor => actor.ObjectId).Select(group => group.First()).ToArray();
        var marked = party.FirstOrDefault(actor => actor.VfxContainer.IsValid &&
            actor.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == 590));
        if (marked != null) ReleaseRoseSafeMove();
        if (marked == null || movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid || rosebloodBloomMovementLeaseActive || shockSpreadActive)
        {
            ReleaseStockMovement();
            return false;
        }

        bool self = marked.ObjectId == Core.Me.ObjectId;
        Vector3[] participants = party.Where(actor => actor.ObjectId != Core.Me.ObjectId)
            .Select(actor => actor.Location).ToArray();
        if (self && participants.Length == 0) { ReleaseStockMovement(); return false; }

        // The four-hit stack has radius six. Keep a one-yalm allowance for
        // movement and sampling so the marked player actually covers allies.
        int Coverage(Vector3 point) => self
            ? participants.Count(position => point.Distance2D(position) <= 5.0f)
            : point.Distance2D(marked.Location) <= 5.0f ? 1 : 0;
        bool Safe(Vector3 point) => point.Distance2D(ArenaCenter.Zelenia) < ArenaSafeRadius &&
            IsOrdinaryWaypointSafe(point);
        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (var center in self ? participants : new[] { marked.Location })
        {
            candidates.Add(center);
            for (int i = 0; i < 12; i++)
            {
                double angle = i * Math.PI / 6;
                candidates.Add(new Vector3(center.X + 4.5f * (float)Math.Sin(angle), Core.Me.Location.Y,
                    center.Z + 4.5f * (float)Math.Cos(angle)));
            }
        }
        var destination = candidates.Where(point => Coverage(point) > 0 && Safe(point))
            .OrderByDescending(Coverage).ThenBy(point => point.Distance2D(Core.Me.Location))
            .Select(point => (Vector3?)point).FirstOrDefault();
        if (!destination.HasValue) { ReleaseStockMovement(); return false; }

        stockLeaseActive = true;
        CapabilityManager.Update(stockMovementHandle, CapabilityFlags.Movement, 1000,
            "Holding combat movement for Stock Break stack");
        if (Core.Me.Location.Distance2D(destination.Value) <= 0.5f)
        {
            if (stockRouteOwned) { movement.EndNavigation(); stockRouteOwned = false; }
            return false;
        }
        if (!stockRouteOwned)
        {
            movement.BeginNavigation(StockRoute);
            stockRouteOwned = true;
        }
        var result = movement.Navigate(destination.Value, 0.5f);
        if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.Done or MoveResult.ReachedDestination)
        {
            movement.EndNavigation();
            stockRouteOwned = false;
            return false;
        }
        return true;
    }

    private void ReleaseStockMovement()
    {
        if (stockRouteOwned) movement?.EndNavigation();
        stockRouteOwned = false;
        if (stockLeaseActive) CapabilityManager.Clear(stockMovementHandle, CapabilityFlags.Movement, "Stock Break ended or yielded");
        stockLeaseActive = false;
    }

    // Retain the floor across player death while the boss is still engaged. A reset
    // to a full-health, out-of-combat boss denotes a fresh pull rather than a phase
    // transition, so the original center remains available for the knockback.
    private void UpdateRosebloodFloor()
    {
        if (IsAnyActionCasting(EnemyAction.QueensCrusadeCasts))
        {
            rosebloodFloorActive = true;
            return;
        }

        if (Core.Player == null || Core.Player.InCombat)
            return;

        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.BaseId == 0x47C3 && actor.IsTargetable && actor.IsAlive);
        if (boss != null && !boss.InCombat && boss.CurrentHealth == boss.MaxHealth)
            rosebloodFloorActive = false;
    }

    private void UpdateRosePetals()
    {
        if (!rosebloodFloorActive || DirectorManager.ActiveDirector is not InstanceContentDirector director || !director.IsValid)
        {
            roseLayoutKnown = false;
            if (!rosebloodFloorActive)
            {
                Array.Clear(rosePetals, 0, rosePetals.Length);
                Array.Clear(roseOrbPredicted, 0, roseOrbPredicted.Length);
                rosePatternWasMarked = false;
                nextRoseOrbSampleUtc = default;
            }
            return;
        }

        var effects = director.MapEffects.ToArray();
        // Validate object identity before interpreting these slots. Treat an unfamiliar
        // layout as unknown rather than silently mapping unrelated effects to the floor.
        if (effects.Length < 10 || Enumerable.Range(0, 6).Any(i => effects[i + 4].ID != 0xB06067u + i))
        {
            // Retain the last observed layout for emergency avoidance, while the
            // unknown flag blocks ordinary travel until the six IDs return.
            roseLayoutKnown = false;
            if (!roseLayoutWarned)
            {
                Logger.Warning("[Recollection] Petal map objects differ from the captured layout; sector avoidance is unavailable.");
                roseLayoutWarned = true;
            }
            return;
        }
        roseLayoutWarned = false;
        roseLayoutKnown = true;
        bool patternMarked = effects.Skip(4).Take(6).Any(effect => effect.State == 0x40);
        // A blank floor before an orb arrives is not a pattern reset.
        if (rosePatternWasMarked && !patternMarked)
            Array.Clear(roseOrbPredicted, 0, roseOrbPredicted.Length);
        rosePatternWasMarked = patternMarked;
        for (int i = 0; i < 6; i++)
        {
            // The November 5 attributed Thunder III hits outside their helper
            // circles exposed a reflection between placed-object order and the
            // previous rotation. This mapping covers all 21 observed hits; the
            // complete layout and mirrored variants still require live verification.
            int sector = i;
            bool marked = effects[i + 4].State == 0x40 || roseOrbPredicted[sector];
            if (rosePetals[sector] != marked) nextRoseSafeSearchUtc = default;
            rosePetals[sector] = marked;
        }
        if (DateTime.UtcNow >= nextRoseOrbSampleUtc)
        {
            nextRoseOrbSampleUtc = DateTime.UtcNow.AddMilliseconds(250);
            foreach (var orb in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                         .Where(actor => actor.IsValid && actor.BaseId == 0x485B && !string.IsNullOrEmpty(actor.LuaString)))
            {
                int timeline;
                try { timeline = Lua.GetReturnVal<int>("local o=_G['" + orb.LuaString + "']; if not o or not o.GetActionTimelineId then return -1 end; return o:GetActionTimelineId()"); }
                catch (Exception) { continue; }
                if (timeline != 50) continue;
                // In the November 5 capture timeline 50 tracked the travelling orb;
                // it headed toward the occupied tile for ten seconds before the
                // map effect appeared. Direction identifies the tile before arrival.
                double degrees = orb.Heading * 180.0 / Math.PI;
                while (degrees > 180) degrees -= 360;
                while (degrees < -180) degrees += 360;
                // Candidate 24 recorded 143.64 degrees for the northward orb;
                // rounding it to 144 missed the previously observed 143-degree
                // heading. Accept only a two-degree neighborhood of recorded
                // headings and choose the closest, leaving other directions unknown.
                (double Heading, int Sector)[] directions =
                [
                    (143, 0), (-143, 0), (180, 0),
                    (83, 1), (156, 1), (119, 1),
                    (96, 2), (23, 2), (59, 2),
                    (0, 3), (-36, 3), (36, 3),
                    (-96, 4), (-23, 4), (-60, 4),
                    (-120, 5), (-156, 5), (-83, 5)
                ];
                var closest = directions.Select(direction => new
                    {
                        direction.Sector,
                        Difference = Math.Abs(Math.IEEERemainder(degrees - direction.Heading, 360))
                    }).OrderBy(direction => direction.Difference).First();
                int sector = closest.Difference <= 2.0 ? closest.Sector : -1;
                if (sector < 0 || roseOrbPredicted[sector]) continue;
                roseOrbPredicted[sector] = true;
                rosePetals[sector] = true;
                roseOrbPredictionAtUtc[sector] = DateTime.UtcNow;
                nextRoseSafeSearchUtc = default;
                Logger.Information($"[Recollection] Predicting orb-activated rose sector {sector} from heading {degrees:F1}.");
            }
        }
    }

    private static Vector2[] RosePetalPolygon(int tile)
    {
        // Reference sectors are sixty degrees around the 16-yalm arena. Size the
        // angular margin at the 4.5-yalm inner boundary so it remains at least
        // half a yalm even close to the center hazard.
        double heading = Math.PI - tile * Math.PI / 3;
        double margin = Math.Asin(0.5 / 4.5);
        var points = new List<Vector2> { new(0, 0) };
        for (int step = 0; step <= 8; step++)
        {
            double angle = heading - Math.PI / 6 - margin + (Math.PI / 3 + 2 * margin) * step / 8;
            points.Add(new Vector2((float)(Math.Sin(angle) * 16.5), (float)(Math.Cos(angle) * 16.5)));
        }
        return points.ToArray();
    }

    private int[] CurrentRosePetalHazards()
    {
        var all = Enumerable.Range(0, 6).Where(i => rosePetals[i]).ToArray();
        // The November 5 party capture shows a player crossing a marked wedge
        // before the helper casts without a sampled HP loss. Suppress these
        // polygons only during the bounded, checked orb transit below; all other
        // encounter avoids stay active, and helper discovery restores them.
        if (roseTransitOwned && DateTime.UtcNow < roseTransitUntilUtc &&
            !IsAnyActionCasting(EnemyAction.AlexandrianThunderIIICasts) &&
            !IsAnyActionCasting(EnemyAction.AlexandrianThunderIVCasts))
            return Array.Empty<int>();
        // Phase-two in/out activates every marked sector. Outside a known Thunder
        // III sequence keep all marked petals unsafe, as requested by Domestic.
        if (IsAnyActionCasting(EnemyAction.AlexandrianThunderIVCasts)) return all;
        var casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsCasting && EnemyAction.AlexandrianThunderIIICasts.Contains(actor.CastingSpellId))
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .ThenBy(actor => actor.ObjectId).ToArray();
        foreach (uint id in roseCastPetals.Keys.Where(id => !casters.Any(actor => actor.ObjectId == id)).ToArray())
            roseCastPetals.Remove(id);
        if (casters.Length == 0)
        {
            roseStageCaster = 0;
            roseStageAction = 0;
            Array.Clear(roseStagePetals, 0, roseStagePetals.Length);
            return all;
        }

        var cutoff = casters[0].SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(500);
        // Keep the current representative while it remains in the earliest
        // group so minor finish-time jitter cannot restart the stage tail.
        var caster = casters.FirstOrDefault(actor => actor.ObjectId == roseStageCaster &&
            actor.SpellCastInfo.RemainingCastTime <= cutoff) ?? casters[0];
        Vector3 stageOrigin = caster.SpellCastInfo.CastLocation;
        if (caster.ObjectId != roseStageCaster || caster.CastingSpellId != roseStageAction ||
            roseStageOrigin.Distance2D(stageOrigin) > 0.5f)
        {
            // The November 5 helper-sourced gain appeared about 0.9 seconds after
            // its estimated cast end. Retain the prior stage for 1.25 seconds while
            // moving to the next; live action-effect timing must verify this tail.
            Array.Copy(roseStagePetals, roseTailPetals, 6);
            roseStageTailUntilUtc = DateTime.UtcNow.AddMilliseconds(1250);
            roseStageCaster = caster.ObjectId;
            roseStageAction = caster.CastingSpellId;
            roseStageOrigin = stageOrigin;
        }
        Array.Clear(roseStagePetals, 0, roseStagePetals.Length);
        // Capture each helper's connected petals when its cast first appears. Map
        // effects can change while later helpers are still casting; recomputing
        // their sectors at impact loses the layout that selected the propagation.
        foreach (var member in casters)
        {
            if (roseCastPetals.ContainsKey(member.ObjectId)) continue;
            Vector3 origin = member.SpellCastInfo.CastLocation;
            float dx = origin.X - ArenaCenter.Zelenia.X, dz = origin.Z - ArenaCenter.Zelenia.Z;
            double radius = Math.Sqrt(dx * dx + dz * dz);
            if (radius < 0.1 || radius > 20)
                return all;
            var selected = new bool[6];
            var pending = new Queue<int>();
            double angularExpansion = Math.Asin(Math.Min(1, 4.0 / radius));
            for (int i = 0; i < 6; i++)
            {
                if (!rosePetals[i]) continue;
                double heading = Math.PI - i * Math.PI / 3;
                double cosine = (dx * Math.Sin(heading) + dz * Math.Cos(heading)) / radius;
                if (Math.Acos(Math.Clamp(cosine, -1, 1)) <= Math.PI / 6 + angularExpansion)
                    pending.Enqueue(i);
            }
            while (pending.Count > 0)
            {
                int i = pending.Dequeue();
                if (selected[i] || !rosePetals[i]) continue;
                selected[i] = true;
                pending.Enqueue((i + 5) % 6);
                pending.Enqueue((i + 1) % 6);
            }
            roseCastPetals[member.ObjectId] = selected;
        }
        foreach (var member in casters.Where(actor => actor.SpellCastInfo.RemainingCastTime <= cutoff))
            for (int i = 0; i < 6; i++)
                roseStagePetals[i] |= roseCastPetals[member.ObjectId][i];
        bool tailActive = DateTime.UtcNow < roseStageTailUntilUtc;
        return Enumerable.Range(0, 6).Where(i => roseStagePetals[i] || tailActive && roseTailPetals[i]).ToArray();
    }

    // This is passive, bounded bot-thread telemetry: it does not acquire movement
    // or retain frame-scoped objects. Marker samples distinguish stacks and spreads;
    // floor-phase samples reveal routes taken when an orb changes the safe sectors.
    private void CaptureUnresolvedSignals()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || Core.Me == null ||
            WorldManager.ZoneId != (uint)Data.ZoneId.Recollection || DateTime.UtcNow < nextCaptureSampleUtc)
            return;

        nextCaptureSampleUtc = DateTime.UtcNow.AddMilliseconds(500);
        var powerCaster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsCasting && EnemyAction.PowerBreakCasts.Contains(actor.CastingSpellId));
        if (powerCaster != null)
        {
            // Capture both live actor and omen basis: two observed boss-sourced hits
            // fell on opposite sides of the heading sampled at cast discovery.
            var omen = powerCaster.OmenMatrix;
            Logger.Information($"[RecollectionCapture] POWER action={powerCaster.CastingSpellId} caster=0x{powerCaster.ObjectId:X} heading={powerCaster.Heading} savedHeading={powerBreakHeading} remainingMs={powerCaster.SpellCastInfo.RemainingCastTime.TotalMilliseconds:F0} origin={powerCaster.Location} omenCenter={omen.Center} basis=({omen.M00},{omen.M02},{omen.M20},{omen.M22}) player={Core.Me.Location}");
        }
        if (rosebloodFloorActive)
        {
            // Keep player position and the interpreted sector state together so a
            // screenshot, hit or safe standing interval can verify the mapping.
            int containing = AvoidanceManager.Avoids.Count(avoid => !avoid.ShouldIgnore &&
                avoid.IsPointInAvoid(Core.Me.Location));
            Logger.Information($"[RecollectionCapture] PETALS marked={string.Join(",", Enumerable.Range(0, 6).Where(i => rosePetals[i]))} stage={string.Join(",", CurrentRosePetalHazards())} player={Core.Me.Location} containing={containing} escaping={AvoidanceManager.IsRunningOutOfAvoid}");
        }
        if (DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
        {
            var effects = director.MapEffects.ToArray();
            var present = effects.Select(effect => effect.ID).ToHashSet();
            foreach (uint removed in capturedMapEffects.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                Logger.Information($"[RecollectionCapture] MAP_REMOVED id=0x{removed:X}");
                capturedMapEffects.Remove(removed);
            }
            for (int index = 0; index < effects.Length; index++)
            {
                var effect = effects[index];
                var snapshot = (effect.unk, effect.State, effect.Flags);
                if (capturedMapEffects.TryGetValue(effect.ID, out var previous) && previous == snapshot)
                    continue;
                capturedMapEffects[effect.ID] = snapshot;
                Logger.Information($"[RecollectionCapture] MAP index={index} id=0x{effect.ID:X} state=0x{effect.State:X} flags=0x{effect.Flags:X} raw=0x{effect.unk:X}");
            }
        }

        var party = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Concat(new BattleCharacter[] { Core.Me })
            .Where(actor => actor != null && actor.IsValid)
            .GroupBy(actor => actor.ObjectId).Select(group => group.First()).ToArray();
        bool markerActive = party.Any(actor => actor.VfxContainer.IsValid &&
            actor.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid));
        var presentActors = party.Select(actor => actor.ObjectId).ToHashSet();
        foreach (uint removed in capturedMarkers.Keys.Where(id => !presentActors.Contains(id)).ToArray())
        {
            Logger.Information($"[RecollectionCapture] PARTY_REMOVED actor=0x{removed:X}");
            capturedMarkers.Remove(removed);
        }
        foreach (var actor in party)
        {
            string markers = actor.VfxContainer.IsValid
                ? string.Join(",", actor.VfxContainer.LockOns.Where(marker => marker != null && marker.IsValid).Select(marker => marker.Id).OrderBy(id => id))
                : "invalid";
            bool changed = !capturedMarkers.TryGetValue(actor.ObjectId, out string previous) || previous != markers;
            capturedMarkers[actor.ObjectId] = markers;
            if (changed || markerActive || rosebloodFloorActive)
                Logger.Information($"[RecollectionCapture] PARTY actor=0x{actor.ObjectId:X} npc={actor.NpcId} markers=[{markers}] pos={actor.Location} hp={actor.CurrentHealth}/{actor.MaxHealth} alive={actor.IsAlive} player={Core.Me.Location} escaping={AvoidanceManager.IsRunningOutOfAvoid}");
        }
        foreach (var orb in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor => actor.IsValid && actor.BaseId == 0x485B))
        {
            // Capture the public timeline getter alongside heading and map states.
            // Its relationship to the orb's tile activation is not yet established,
            // so this observation must not itself mark or clear any floor sector.
            string timeline = "unavailable";
            try
            {
                if (!string.IsNullOrEmpty(orb.LuaString))
                    timeline = Lua.GetReturnVal<string>("local o=_G['" + orb.LuaString + "']; if not o or not o.GetActionTimelineId then return '-1' end; return tostring(o:GetActionTimelineId())");
            }
            catch (Exception) { timeline = "unreadable"; }
            Logger.Information($"[RecollectionCapture] ORB actor=0x{orb.ObjectId:X} pos={orb.Location} heading={orb.Heading} cast={orb.CastingSpellId} timeline={timeline}");
        }
    }

    /// <summary>
    /// Spreads the party during Shock's eight-second overhead countdown.
    /// </summary>
    private void HandleShockSpread()
    {
        DateTime now = DateTime.UtcNow;
        bool visualIsCasting = IsAnyActionCasting(EnemyAction.ShockVisualCasts);
        bool markerReadable = Core.Me.VfxContainer.IsValid;
        bool marked = markerReadable && Core.Me.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == 581);

        if (visualIsCasting && !shockVisualWasCasting)
        {
            shockPuddlesCapturedForMarker = false;
            shockPreviousHealth = 0;
            // Preserve the previous visual fallback until the actual target marker is
            // observed. The November 5 capture showed that fallback expiring before
            // repeated puddle damage; it does not establish the lock timestamp.
            shockSpreadEndsAtUtc = now.AddMilliseconds(8_500);
            shockMovementLeaseActive = true;
            CapabilityManager.Update(
                shockMovementHandle,
                CapabilityFlags.Movement,
                8_500,
                "Holding combat-routine movement for Shock spread");
        }

        shockVisualWasCasting = visualIsCasting;

        // On November 5 marker 581 remained visible throughout repeated damage;
        // waiting for its removal held the player in the ground effect until death.
        // Candidate 21 took a severe hit while still moving the marker to the edge;
        // requiring stationary movement delayed departure until further damage.
        // A hit can be unrelated to Shock, so the sampled puddles remain a
        // conservative hypothesis and all route hazard checks still apply.
        bool damagedWhileMarked = marked && shockMarkerWasObserved && !shockPuddlesCapturedForMarker &&
            shockPreviousHealth > Core.Me.CurrentHealth &&
            shockPreviousHealth - Core.Me.CurrentHealth >= Core.Me.MaxHealth / 20;
        if (marked && !shockPuddlesCapturedForMarker)
        {
            shockMarkerWasObserved = true;
            lastShockMarkerSeenUtc = now;
            shockMovementLeaseActive = true;
            CapabilityManager.Update(shockMovementHandle, CapabilityFlags.Movement, 1_000,
                "Holding combat-routine movement while Shock marker is active");
        }

        // A long unreadable interval cannot identify where the marker ended;
        // do not create a stale puddle at a later player position on recovery.
        else if (!markerReadable && now - lastShockMarkerSeenUtc > TimeSpan.FromSeconds(1))
        {
            shockMarkerWasObserved = false;
        }

        if (!marked || damagedWhileMarked) ReleaseShockPlacement();

        if (!shockPuddlesCapturedForMarker &&
            ((shockMarkerWasObserved && markerReadable && !marked) || damagedWhileMarked))
        {
            // Sample a conservative candidate location on the marker edge or early
            // damage trigger. The latter can be another attack; its timing and the
            // resulting departure require live confirmation.
            shockPuddleOrigins = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
                .Concat(new BattleCharacter[] { Core.Me })
                .Where(actor => actor != null && actor.IsValid && actor.IsAlive)
                .GroupBy(actor => actor.ObjectId).Select(group => group.First().Location).ToArray();
            shockPuddlesExpireAtUtc = now.AddSeconds(8);
            shockPuddlesCapturedForMarker = true;
            shockSpreadEndsAtUtc = DateTime.MinValue;
            // The departing route must be checked against locked locations, not
            // the still-moving party spread circles from the preceding phase.
            shockSpreadActive = false;
            ReleaseShockMovement("Shock ground damage or marker edge");
            StartShockEdgeEscape();
            shockMarkerWasObserved = false;
            Logger.Information($"[Recollection] Shock departure triggered by {(damagedWhileMarked ? "damage while marked" : "marker removal")}.");
        }

        if (marked && !shockPuddlesCapturedForMarker) UpdateShockPlacement();

        shockSpreadActive = !shockPuddlesCapturedForMarker && (marked || now < shockSpreadEndsAtUtc);
        shockPreviousHealth = Core.Me.CurrentHealth;

        if (!marked && now >= shockSpreadEndsAtUtc)
        {
            ReleaseShockMovement("Shock marker or visual fallback ended");
        }
    }

    // Place the personal marker before it locks: radius 13.5 leaves 1.5 yalms
    // inside the validated wall, and nine yalms separates two 4.5-yalm puddles.
    // Recheck the full route on every pulse because petals and other avoids change.
    private void UpdateShockPlacement()
    {
        if (movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid || rosebloodBloomMovementLeaseActive || shockEdgeOwned)
        {
            ReleaseShockPlacement();
            return;
        }
        Vector3 from = Core.Me.Location;
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        bool Safe(Vector3 point) => IsEscapeSegmentSafe(from, point, active, initial);
        var others = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && actor.ObjectId != Core.Me.ObjectId &&
                actor.VfxContainer.IsValid && actor.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == 581))
            .Select(actor => actor.Location).ToArray();
        if (shockPlacementOwned && shockPlacementDestination.HasValue &&
            from.Distance2D(shockPlacementDestination.Value) > 0.5f && Safe(shockPlacementDestination.Value) &&
            others.All(other => shockPlacementDestination.Value.Distance2D(other) >= 9.0f)) return;
        ReleaseShockPlacement();
        var candidates = new List<Vector3>();
        for (int i = 0; i < 48; i++)
        {
            double angle = i * Math.PI / 24;
            candidates.Add(new Vector3(ArenaCenter.Zelenia.X + 13.5f * (float)Math.Sin(angle), from.Y,
                ArenaCenter.Zelenia.Z + 13.5f * (float)Math.Cos(angle)));
        }
        var destination = candidates.Where(Safe)
            .OrderByDescending(point => others.Length == 0 || others.All(other => point.Distance2D(other) >= 9.0f))
            .ThenBy(point => point.Distance2D(from))
            .Select(point => (Vector3?)point).FirstOrDefault();
        if (!destination.HasValue || from.Distance2D(destination.Value) <= 0.5f) return;
        ReleaseStalledEscape();
        ReleaseStockMovement();
        ReleaseRoseSafeMove();
        shockPlacementDestination = destination;
        shockPlacementOwned = true;
        movement.RequestHazardExit(destination.Value, 0.4f);
        movement.Update();
        Logger.Information($"[Recollection] Placing Shock marker near arena edge at {destination.Value}.");
    }

    private void ReleaseShockPlacement()
    {
        if (shockPlacementOwned) movement?.Request(null, 0);
        shockPlacementOwned = false;
        shockPlacementDestination = null;
    }

    private bool ShockEdgeSegmentSafe(Vector3 from, Vector3 destination)
    {
        var active = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var initial = active.Where(avoid => avoid.IsPointInAvoid(from)).ToArray();
        if (!IsEscapeSegmentSafe(from, destination, active, initial)) return false;
        // The lock locations were captured on this pulse; native avoids may not
        // have materialized yet. Leave every initial circle without entering a new
        // one or turning back into a circle already left.
        var left = new bool[shockPuddleOrigins.Length];
        var initiallyInside = shockPuddleOrigins.Select(origin => from.Distance2D(origin) < 5.5f).ToArray();
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(destination) / 0.25f));
        for (int step = 1; step <= steps; step++)
        {
            float t = (float)step / steps;
            var point = new Vector3(from.X + (destination.X - from.X) * t, from.Y,
                from.Z + (destination.Z - from.Z) * t);
            for (int i = 0; i < shockPuddleOrigins.Length; i++)
            {
                bool inside = point.Distance2D(shockPuddleOrigins[i]) < 5.5f;
                if (inside && (!initiallyInside[i] || left[i])) return false;
                if (initiallyInside[i] && !inside) left[i] = true;
            }
        }
        return shockPuddleOrigins.All(origin => destination.Distance2D(origin) >= 5.5f);
    }

    private void StartShockEdgeEscape()
    {
        if (movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid || rosebloodBloomMovementLeaseActive) return;
        Vector3 from = Core.Me.Location;
        for (float distance = 5; distance <= 10; distance += 0.5f)
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI / 12;
            var point = new Vector3(from.X + distance * (float)Math.Sin(angle), from.Y,
                from.Z + distance * (float)Math.Cos(angle));
            if (!ShockEdgeSegmentSafe(from, point)) continue;
            ReleaseStalledEscape();
            ReleaseStockMovement();
            ReleaseRoseSafeMove();
            shockEdgeDestination = point;
            shockEdgeOwned = true;
            movement.RequestHazardExit(point, 0.3f);
            movement.Update();
            Logger.Information($"[Recollection] Leaving locked Shock puddles for {point}.");
            return;
        }
    }

    private bool UpdateShockEdgeEscape()
    {
        if (!shockEdgeOwned) return false;
        if (movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid || DateTime.UtcNow >= shockPuddlesExpireAtUtc ||
            !shockEdgeDestination.HasValue || Core.Me.Location.Distance2D(shockEdgeDestination.Value) <= 0.3f ||
            !ShockEdgeSegmentSafe(Core.Me.Location, shockEdgeDestination.Value))
        {
            ReleaseShockEdgeEscape();
            return false;
        }
        return true;
    }

    private void ReleaseShockEdgeEscape()
    {
        if (shockEdgeOwned) movement?.Request(null, 0);
        shockEdgeOwned = false;
        shockEdgeDestination = null;
    }

    /// <summary>
    /// Moves inside the safe center radius before Roseblood Bloom's unavoidable knockback.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only while this handler is actively traveling to the center;
    /// otherwise <see langword="false"/> so healing, mitigation, and rotation remain schedulable.
    /// </returns>
    private async Task<bool> HandleRosebloodBloomKnockbackAsync()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(character => character.IsCasting
                && character.CastingSpellId == EnemyAction.RosebloodBloomKnockback);

        if (caster == null)
        {
            ReleaseRosebloodBloomMovement("Roseblood Bloom knockback ended");
            return false;
        }

        ReleaseRoseSafeMove();
        rosebloodBloomMovementLeaseActive = true;
        CapabilityManager.Update(
            rosebloodBloomMovementHandle,
            CapabilityFlags.Movement,
            caster.SpellCastInfo.RemainingCastTime,
            "Holding combat-routine movement for Roseblood Bloom knockback");

        // Active emergency avoidance retains ownership. The center remains the desired destination
        // and will be reacquired on the next tick if the knockback cast is still active.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        if (Core.Player.Distance2D(ArenaCenter.Zelenia) > KnockbackCenterRadius)
        {
            if (movement?.IsCurrent != true)
                return false;
            movingToArenaCenter = true;
            movement.Request(ArenaCenter.Zelenia, KnockbackCenterRadius);
            movement.Update();
            await Coroutine.Yield();
            return true;
        }

        if (movingToArenaCenter)
        {
            movement?.Request(null, 0);
            movement?.Update();
            movingToArenaCenter = false;
        }

        return false;
    }

    /// <summary>
    /// Selects only the next resolving members of a simultaneous helper-cast family.
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
            .Where(character => character.IsCasting && actionIds.Contains(character.CastingSpellId))
            .OrderBy(character => character.SpellCastInfo.RemainingCastTime)
            .ThenBy(character => character.ObjectId)
            .Take(count)
            .Any(character => character.ObjectId == candidate.ObjectId);
    }

    /// <summary>
    /// Determines whether any actor is currently casting an action in the supplied family.
    /// </summary>
    /// <param name="actionIds">Action IDs to inspect.</param>
    /// <returns><see langword="true"/> when a matching live cast exists.</returns>
    private static bool IsAnyActionCasting(HashSet<uint> actionIds)
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(character => character.IsCasting && actionIds.Contains(character.CastingSpellId));
    }

    /// <summary>
    /// Releases only the Shock movement lease.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded by the capability manager.</param>
    private void ReleaseShockMovement(string reason)
    {
        if (!shockMovementLeaseActive)
        {
            return;
        }

        CapabilityManager.Clear(shockMovementHandle, CapabilityFlags.Movement, reason);
        shockMovementLeaseActive = false;
    }

    /// <summary>
    /// Releases only the Roseblood Bloom movement lease and stops movement owned by this handler.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded by the capability manager.</param>
    private void ReleaseRosebloodBloomMovement(string reason)
    {
        if (rosebloodBloomMovementLeaseActive)
        {
            CapabilityManager.Clear(rosebloodBloomMovementHandle, CapabilityFlags.Movement, reason);
            rosebloodBloomMovementLeaseActive = false;
        }

        if (movingToArenaCenter)
            movement?.Request(null, 0);

        movingToArenaCenter = false;
    }

    /// <summary>
    /// Returns whether the dedicated Recollection territory is active and the player is in combat.
    /// </summary>
    /// <returns><see langword="true"/> only during combat in normal-mode Recollection.</returns>
    private static bool IsInRecollectionCombat()
    {
        return Core.Player != null
            && Core.Player.InCombat
            && WorldManager.ZoneId == (uint)Data.ZoneId.Recollection;
    }

    // Normal Recollection uses a circular arena centered at (100, 100) on the horizontal X/Z plane.
    private static class ArenaCenter
    {
        internal static readonly Vector3 Zelenia = new(100.0f, 0.0f, 100.0f);
    }

    // The visible wall is at radius 16.0; avoidance keeps a one-yalm inset for navigation latency.
    private const float ArenaSafeRadius = 15.0f;

    // A 10-yalm center-origin knockback is safe from within five yalms even with ordinary latency.
    private const float KnockbackCenterRadius = 5.0f;

    private static class EnemyAction
    {
        // Alexandrian Thunder IV: ordered 8-yalm circle and 8-to-24-yalm donut helper casts.
        internal static readonly HashSet<uint> AlexandrianThunderCircleCasts = [43084, 43446];
        internal static readonly HashSet<uint> AlexandrianThunderDonutCasts = [43085, 43447];
        internal static readonly HashSet<uint> AlexandrianThunderIVCasts = [43084, 43085, 43446, 43447];

        // Shock visual starts the player-targeted eight-second spread countdown.
        internal static readonly HashSet<uint> ShockVisualCasts = [43056];

        // Power Break is the boss-authored 24-by-64-yalm half-room cleave.
        internal static readonly HashSet<uint> PowerBreakCasts = [43112, 43113];

        // Holy Hazard helpers own the sequential 24-yalm, 120-degree cone pairs.
        internal static readonly HashSet<uint> HolyHazardCasts = [43126];

        // Specter of the Lost is the helper-authored 50-yalm conal tankbuster.
        internal const uint SpecterOfTheLost = 43129;

        // Thunder Slash helpers own the three sequential 24-yalm, 60-degree cone pairs.
        internal static readonly HashSet<uint> ThunderSlashCasts = [43083];

        // The helper-owned five-second cast performs the 10-yalm phase-transition knockback.
        internal const uint RosebloodBloomKnockback = 43479;

        // Queen's Crusade starts the persistent center-floor phase after the transition.
        internal static readonly HashSet<uint> QueensCrusadeCasts = [43092];

        // Alexandrian Thunder III helpers place four-yalm ground circles in phase two.
        internal static readonly HashSet<uint> AlexandrianThunderIIICasts = [43102, 43439];

        // Valorous Ascension begins a three-hit physical raidwide sequence.
        internal const uint ValorousAscensionFirstHit = 43071;

        // Briar Thorn helpers cast the phase-two 40-by-8-yalm line attacks.
        internal static readonly HashSet<uint> ValorousAscensionLineCasts = [43074];

        // Thorned Catharsis is the repeatable five-second magical raidwide.
        internal const uint ThornedCatharsis = 43127;
    }
}
