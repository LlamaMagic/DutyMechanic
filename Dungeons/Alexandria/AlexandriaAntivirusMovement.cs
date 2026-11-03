using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    // The 2026-11-02 native-avoidance capture repeatedly delayed travel until
    // the next short cast, missing the next shelter despite advance actor data.
    // A scalar worker stages the ordered wave; the existing input adapter alone
    // executes it. No game wrappers or input calls leave the bot thread.
    private static AlexandriaInterferonGeometry.Hazard[] antivirusHazards = [];
    // A helper can leave the object table before its forecast effect. Retain
    // only detached coordinates for actors still in this wave or visible now.
    private static readonly AlexandriaNodeSnapshotCache antivirusNodes = new();
    private static bool antivirusIncomplete;
    private static Task<AlexandriaInterferonPlanner.Step[]> antivirusWorker;
    private static CancellationTokenSource antivirusCancellation;
    private static AlexandriaInterferonPlanner.Step[] antivirusRoute = [];
    private static double antivirusRouteStart, antivirusRetryAfter, antivirusReportAfter;
    private static AlexandriaTemporalPlanner.MovementBoost? antivirusPlanBoost;
    private static int antivirusStep;
    private static bool antivirusPulseHandled, antivirusPulseMoving;
    private static bool antivirusFallbackEnabled;
    private static bool antivirusSearchFailed;
    private static uint antivirusFallbackActor;
    private static string antivirusInputMode;
    private static Vector2 antivirusInputTarget;
    private static readonly AlexandriaInterferonMotionLease antivirusMotionLease = new();
    private bool antivirusFacingLeased;
    private DateTime antivirusNextCastCancel;
    private static double MonotonicNow => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    // Log only changed requests, not every pulse. Combining these monotonic
    // command times with actor-position samples can bound handoff latency;
    // the request itself does not prove that the game accepted the input.
    private static void TraceAntivirusInput(string mode, Vector2 player, Vector2 target, double now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled) { antivirusInputMode = null; return; }
        if (antivirusInputMode == mode && (mode != "move" || antivirusInputTarget == target)) return;
        antivirusInputMode = mode; antivirusInputTarget = target;
        Logger.Information(FormattableString.Invariant($"[AlexandriaInput] mode={mode} monotonic={now:F6} player=({player.X:F3},{player.Y:F3}) target=({target.X:F3},{target.Y:F3}) step={antivirusStep} casting={Core.Me.IsCasting}"));
    }

    private static void CaptureAntivirusHazards(double now, BattleCharacter[] actors)
    {
        var result = new System.Collections.Generic.List<AlexandriaInterferonGeometry.Hazard>();
        antivirusIncomplete = interferonSequence.UnanchoredUnresolved || immuneResponse.Incomplete;
        var forecast = interferonSequence.Forecast();
        var visible = actors.Where(a => (a.BaseId is 0x4174 or 0x4175) && a.IsVisible).ToArray();
        antivirusNodes.Refresh(visible.Select(a => new AlexandriaNodeSnapshotCache.Node(a.ObjectId, a.BaseId,
            new Vector2(a.X, a.Z))).ToArray(), forecast.Select(f => f.Id).ToArray());
        foreach (var entry in forecast)
        {
            if (entry.Latest + .3 < now) continue;
            if (!antivirusNodes.TryGet(entry.Id, out var node)) { antivirusIncomplete = true; continue; }
            // Later captured waves delivered the first effect as early as 9.46s
            // after visibility, earlier than the 9.9s forecast minus its old
            // .25s margin. A .55s margin covers the observed receipt by .11s;
            // the server snapshot and delivery delay remain unmeasured. A full
            // second made a captured wave infeasible in the current planner,
            // so this provisional bound needs live validation and refinement.
            result.Add(new(node.BaseId == 0x4175 ? AlexandriaInterferonGeometry.Shape.Cross : AlexandriaInterferonGeometry.Shape.Donut,
                node.Origin, default, 0, entry.Earliest - .55, entry.Latest + .3));
        }
        // When discovery order is ambiguous the forecast deliberately returns
        // no guessed sequence. Preserve every actually casting helper instead
        // of handing that window to native avoidance. These provisional finish
        // bounds are not a substitute for the missing effect-receipt evidence.
        var forecastIds = forecast.Select(f => f.Id).ToHashSet();
        foreach (var actor in actors.Where(a => (a.BaseId is 0x4174 or 0x4175) &&
                (a.CastingSpellId is 36382 or 36383) && a.SpellCastInfo.IsValid && !forecastIds.Contains(a.ObjectId)))
            {
                double castDue = now + actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
                var origin = new Vector2(actor.X, actor.Z);
                if (!AlexandriaGeometry.Finite(origin)) { antivirusIncomplete = true; continue; }
                result.Add(new(actor.BaseId == 0x4175 ? AlexandriaInterferonGeometry.Shape.Cross : AlexandriaInterferonGeometry.Shape.Donut,
                    origin, default, 0, now, castDue + .3));
            }
        foreach (var cone in immuneResponse.Active)
        {
            if (cone.Due is not { } due || due + .4 < now) continue;
            var actor = actors.FirstOrDefault(a => a.ObjectId == cone.Actor);
            if (actor != null)
            {
                var direction = new Vector2((float)Math.Sin(actor.Heading), (float)Math.Cos(actor.Heading));
                // The wide helper covers the rear/sides, leaving the forward wedge.
                if (cone.Action == 36381) direction = -direction;
                float cosine = (float)Math.Cos((cone.Action == 36381 ? 125 : 62.5) * Math.PI / 180);
                // Across the 2026-11-03 captures, cone receipts arrived as
                // early as 0.171s after the reported cast finish. The prior
                // lower bound of finish + 0.3s missed those events. Begin at
                // reported finish; the server snapshot may still precede the
                // local receipt, so this remains a provisional bound.
                var window = AlexandriaInterferonGeometry.ConeWindow(due);
                result.Add(new(AlexandriaInterferonGeometry.Shape.Cone, new(actor.X, actor.Z), direction, cosine, window.Earliest, window.Latest));
            }
            else antivirusIncomplete = true;
        }
        antivirusHazards = result.ToArray();
    }

    private static void ResetAntivirusRoute()
    {
        if (antivirusInputMode != null && LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Information(FormattableString.Invariant($"[AlexandriaInput] mode=release monotonic={MonotonicNow:F6}"));
        antivirusInputMode = null;
        antivirusMotionLease.Reset();
        activeStackOwner?.ReleaseAntivirusFacing();
        antivirusCancellation?.Cancel();
        // Retired work has only scalar data. Observe any failure without allowing
        // an old completion to acquire input or replace the next encounter route.
        if (antivirusWorker != null)
            _ = antivirusWorker.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        antivirusWorker = null;
        antivirusCancellation?.Dispose(); antivirusCancellation = null;
        antivirusHazards = []; antivirusRoute = []; antivirusStep = 0;
        antivirusIncomplete = false;
        antivirusRetryAfter = antivirusReportAfter = 0;
        antivirusPlanBoost = null;
        antivirusPulseHandled = antivirusPulseMoving = false;
        antivirusFallbackEnabled = false;
        antivirusSearchFailed = false; antivirusFallbackActor = 0;
    }

    private void ReleaseAntivirusFacing()
    {
        if (!antivirusFacingLeased) return;
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Facing, "Alexandria route travel ended");
        antivirusFacingLeased = false; antivirusNextCastCancel = default;
    }

    private bool PositionForAntivirusRoute(out bool handled)
    {
        handled = false;
        if (!OwnsQuarantineMovement() || WorldManager.SubZoneId != 4356 || DateTime.UtcNow < quarantineUntil)
        { ResetAntivirusRoute(); return false; }
        double now = MonotonicNow;
        if (antivirusHazards.Length == 0 && !antivirusIncomplete)
        { ReleaseQuarantineStack(); ResetAntivirusRoute(); return false; }
        // The previous fallback handed Interferon and cone movement back to
        // native avoidance whenever the worker replanned. Captures showed that
        // handoff interrupting the authored route and missing the next shelter.
        // Keep this window under the encounter owner even while planning.
        if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
            Navigator.PlayerMover = stackInputOwner; activeStackOwner = this;
        }
        if (stackInputOwner?.IsCurrent != true) return false;
        var player = new Vector2(Core.Me.X, Core.Me.Z);
        // Sprint is optional. If something else activates it, the travel
        // certificate must account for faster arrivals as well as departures.
        double? sprintRemaining = Core.Me.Auras.Where(a => a.Id == 50)
            .Select(a => (double?)a.TimespanLeft.TotalSeconds).FirstOrDefault();
        bool sprintEvidenceValid = AlexandriaSprintPolicy.TrySnapshot(now, sprintRemaining, out var sampledBoost);
        if (!sprintEvidenceValid) antivirusIncomplete = true;
        // A worker owns its captured speed envelope. A published route can
        // instead be revalidated below against the newly observed envelope;
        // throwing it away merely because Sprint appeared stopped movement
        // during the 2026-11-03 capture even though its next edge was useful.
        if ((antivirusWorker != null || antivirusRoute.Length != 0) &&
            !AlexandriaSprintPolicy.Consistent(antivirusPlanBoost, now, sprintRemaining))
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information(FormattableString.Invariant($"[AlexandriaSprint] evidence-changed monotonic={now:F6} remaining={sprintRemaining} hadBoost={antivirusPlanBoost.HasValue}"));
            antivirusCancellation?.Cancel();
            if (antivirusWorker != null)
                _ = antivirusWorker.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            antivirusWorker = null;
            antivirusCancellation?.Dispose(); antivirusCancellation = null;
            antivirusPlanBoost = sampledBoost;
            antivirusRetryAfter = 0;
        }
        if (antivirusWorker?.IsCompleted == true)
        {
            var completed = antivirusWorker; antivirusWorker = null;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information(FormattableString.Invariant($"[AlexandriaPlanResult] started={antivirusRouteStart:F6} elapsed={now - antivirusRouteStart:F3} status={completed.Status} steps={(completed.IsCompletedSuccessfully ? completed.Result.Length : -1)}"));
            if (completed.IsCompletedSuccessfully && now - antivirusRouteStart < .35)
            {
                antivirusRoute = completed.Result; antivirusStep = 0;
                antivirusFallbackEnabled = antivirusRoute.Length == 0;
                antivirusSearchFailed = antivirusRoute.Length == 0;
                if (antivirusRoute.Length != 0)
                {
                    antivirusFallbackActor = 0;
                    Logger.Information($"[Alexandria] Antivirus route ready: {antivirusRoute.Length} steps, hazards={antivirusHazards.Length}, suppressedStops={stackInputOwner.SuppressedStops}, suppressedTravel={stackInputOwner.SuppressedTravel}");
                }
            }
            else { _ = completed.Exception; antivirusRoute = []; antivirusSearchFailed = true; }
        }
        // Validate against current signals, not the worker's possibly superseded
        // snapshot. A newly discovered cone may invalidate an otherwise safe wave.
        // Convert once per pulse, so every segment uses the same detached view
        // without allocating and rebuilding all hazard shapes for every waypoint.
        bool haveShapes = AlexandriaInterferonGeometry.TryConvert(antivirusHazards, out var shapes);
        bool Segment(Vector2 a, Vector2 b, double begin, double end)
        {
            // The earlier sampled check could skip a narrow crossing or accept
            // a point reached earlier/later than its interpolated timestamp.
            // These existing provisional movement bounds still need live
            // calibration; the certificate does not make them measured facts.
            return haveShapes && AlexandriaTemporalPlanner.TryMovementBounds(begin, end,
                AlexandriaInterferonPlanner.MinimumSpeed, AlexandriaInterferonPlanner.MaximumSpeed,
                antivirusPlanBoost, out double minimum, out double maximum) &&
                AlexandriaInterferonGeometry.TravelSafe(a, b, begin, end, .1, minimum, maximum, shapes);
        }
        while (antivirusStep < antivirusRoute.Length && now >= antivirusRouteStart + antivirusRoute[antivirusStep].Time)
        {
            if (Vector2.Distance(player, antivirusRoute[antivirusStep].Position) > .15f)
            {
                Logger.Information($"[Alexandria] Antivirus route late: step={antivirusStep}, elapsed={now-antivirusRouteStart:F3}, target={antivirusRoute[antivirusStep].Position}, player={player}");
                antivirusRoute = []; break;
            }
            antivirusStep++;
        }
        // Waiting at a reached waypoint until its exact deadline left a
        // two-yalm successor with zero pulse-latency margin. Start the next
        // segment early only when the whole observed-position-to-successor
        // travel envelope is safe from now through that successor's deadline.
        // The remaining route is still checked below against current hazards.
        if (!antivirusIncomplete && haveShapes && antivirusRoute.Length != 0 &&
            antivirusRouteStart + antivirusRoute[^1].Time >= antivirusHazards.Max(h => h.Latest))
            antivirusStep = AlexandriaInterferonPlanner.AdvanceReachedWaypoints(player, antivirusRoute,
                antivirusStep, antivirusRouteStart, now, shapes, antivirusPlanBoost);
        Vector2 actualWaypoint = default;
        string invalidReason = null;
        int invalidEdge = -1;
        double currentDelay = .1;
        bool valid = false;
        if (antivirusIncomplete) invalidReason = "incomplete-snapshot";
        else if (!haveShapes) invalidReason = "invalid-shape";
        else if (antivirusStep >= antivirusRoute.Length) invalidReason = "no-route";
        else if (antivirusRouteStart + antivirusRoute[^1].Time < antivirusHazards.Max(h => h.Latest)) invalidReason = "horizon-short";
        else if (!AlexandriaTemporalPlanner.TryMovementBounds(now, antivirusRouteStart + antivirusRoute[antivirusStep].Time,
            AlexandriaInterferonPlanner.MinimumSpeed, AlexandriaInterferonPlanner.MaximumSpeed, antivirusPlanBoost,
            out double currentMinimum, out double currentMaximum) ||
            !AlexandriaInterferonGeometry.CurrentWaypointSafe(player, antivirusRoute[antivirusStep].Position,
            now, antivirusRouteStart + antivirusRoute[antivirusStep].Time,
            currentDelay = antivirusMotionLease.Delay(antivirusRoute[antivirusStep].Position, player, now,
                MovementManager.IsMoving, Core.Me.IsCasting, MovementManager.ForceMoved || MovementManager.GenerallyOutOfControl),
            currentMinimum, currentMaximum, .15, shapes, out actualWaypoint))
        { invalidReason = "current-edge"; invalidEdge = antivirusStep; }
        else valid = true;
        if (valid)
            for (int i = antivirusStep + 1; i < antivirusRoute.Length; ++i)
                if (!Segment(i == antivirusStep + 1 ? actualWaypoint : antivirusRoute[i - 1].Position, antivirusRoute[i].Position,
                    antivirusRouteStart + antivirusRoute[i - 1].Time, antivirusRouteStart + antivirusRoute[i].Time))
                { valid = false; invalidReason = "future-edge"; invalidEdge = i; break; }
        if (!valid)
        {
            if (antivirusRoute.Length != 0) antivirusFallbackEnabled = true;
            antivirusMotionLease.Reset();
            ReleaseAntivirusFacing();
            if (antivirusRoute.Length != 0)
            {
                string waypoint = antivirusStep < antivirusRoute.Length ? antivirusRoute[antivirusStep].Position.ToString() : "none";
                double deadline = antivirusStep < antivirusRoute.Length ? antivirusRouteStart + antivirusRoute[antivirusStep].Time - now : 0;
                Logger.Information($"[Alexandria] Antivirus route replan: reason={invalidReason}, edge={invalidEdge}, step={antivirusStep}/{antivirusRoute.Length}, elapsed={now-antivirusRouteStart:F3}, hazards={antivirusHazards.Length}, player={player}, waypoint={waypoint}, deadline={deadline:F3}, delay={currentDelay:F3}");
            }
            antivirusRoute = [];
            if (now >= antivirusReportAfter)
            {
                Logger.Information($"[Alexandria] Antivirus route unavailable; retrying, hazards={antivirusHazards.Length}, incomplete={antivirusIncomplete}, player={player}.");
                antivirusReportAfter = now + 5;
            }
            if (!antivirusIncomplete && antivirusHazards.Length != 0 && antivirusWorker == null && now >= antivirusRetryAfter)
            {
                var snapshot = antivirusHazards.Select(h => h with { Earliest = h.Earliest - now, Latest = h.Latest - now }).ToArray();
                int ticks = Math.Clamp((int)Math.Ceiling(snapshot.Max(h => h.Latest) / .5) + 1, 2, 60);
                // A live overlap failed although a reconstruction of its visible
                // casts was feasible. Record the exact scalar worker input so
                // changing forecasts and cone windows can be replayed without
                // inferring them from asynchronous cast and position logs.
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                    Logger.Information(FormattableString.Invariant($"[AlexandriaPlanInput] monotonic={now:F6} player=({player.X:F3},{player.Y:F3}) ticks={ticks} hazards=[{string.Join(";", snapshot.Select(h => FormattableString.Invariant($"{h.Shape}@({h.Origin.X:F3},{h.Origin.Y:F3}) dir=({h.Direction.X:F4},{h.Direction.Y:F4}) cos={h.ConeCosine:F4} window=({h.Earliest:F3},{h.Latest:F3})")))}]"));
                antivirusCancellation?.Dispose(); antivirusCancellation = new();
                var token = antivirusCancellation.Token;
                antivirusRouteStart = now; antivirusRetryAfter = now + .5;
                antivirusPlanBoost = sampledBoost;
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                    Logger.Information(FormattableString.Invariant($"[AlexandriaSprint] plan monotonic={now:F6} remaining={sprintRemaining} hasBoost={sampledBoost.HasValue}"));
                var relativeBoost = sampledBoost is { } activeBoost ? activeBoost with
                { EarliestExpiry = activeBoost.EarliestExpiry - now, LatestExpiry = activeBoost.LatestExpiry - now } :
                    (AlexandriaTemporalPlanner.MovementBoost?)null;
                // Results older than 350ms are already discarded below. Reserve
                // that same upper bound in the first edge instead of pretending
                // the worker's planning time was available for movement.
                antivirusWorker = Task.Run(() =>
                {
                    // The original search assumes unboosted speed. Do not use
                    // it while Sprint may still be active: faster arrivals can
                    // cross a hazard earlier than that envelope permits.
                    var route = relativeBoost.HasValue ? [] :
                        AlexandriaInterferonPlanner.Plan(player, snapshot, ticks, token, preparationDelay: .35);
                    // The fixed half-second graph can require artificial input
                    // handoffs that make an otherwise certified direct route
                    // disappear. Retry against every known hazard using its
                    // actual window boundaries; publication and every edge
                    // remain subject to the same owner and current snapshot.
                    return route.Length != 0 || token.IsCancellationRequested ? route :
                        AlexandriaInterferonBoundaryPlanner.Plan(player, snapshot, ticks * .5, .35, token, relativeBoost);
                }, token);
            }
            // If the full forecast has failed, a Trust's current position can
            // supply a short-lived candidate, not an unchecked follow target.
            // Certify the travel and arrival against every currently known
            // shape, and resample on the next pulse as the encounter changes.
            if (antivirusFallbackEnabled && !antivirusIncomplete && haveShapes &&
                !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl && !MovementManager.ForceMoved)
            {
                stackInputOwner.CompleteEscape();
                var candidates = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))
                    .Select(a => new Vector2(a.X, a.Z)).OrderBy(p => Vector2.DistanceSquared(player, p));
                foreach (var candidate in candidates)
                {
                    double distance = Vector2.Distance(player, candidate);
                    double end = now + .1 + distance / AlexandriaInterferonPlanner.MinimumSpeed + .25;
                    // Do not choose a point that is merely safe while walking
                    // there but becomes unsafe at the next known impact.
                    double nextExpiry = antivirusHazards.Where(h => h.Latest >= now)
                        .Select(h => h.Latest).DefaultIfEmpty(now).Min();
                    end = Math.Max(end, Math.Min(now + 3, nextExpiry + .15));
                    if (distance <= .15 || end - now > 3 ||
                        !AlexandriaTemporalPlanner.TryMovementBounds(now, end, AlexandriaInterferonPlanner.MinimumSpeed,
                            AlexandriaInterferonPlanner.MaximumSpeed, sampledBoost, out double minimum, out double maximum) ||
                        !AlexandriaInterferonGeometry.TravelSafe(player, candidate, now, end, .1, minimum, maximum, shapes)) continue;
                    var fallbackWorld = new Clio.Utilities.Vector3(candidate.X, Core.Me.Y, candidate.Y);
                    if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(fallbackWorld))) continue;
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Antivirus fallback");
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Antivirus fallback");
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
                    antivirusFacingLeased = stackLeased = true;
                    if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
                    { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
                    TraceAntivirusInput("npc-candidate", player, candidate, now);
                    stackInputOwner.MoveRoute(fallbackWorld);
                    handled = true;
                    return true;
                }
            }
            // Domestic explicitly authorized a Trust as a last resort when
            // both the full search and checked candidates fail. Stick to one
            // visible party NPC so changing nearest actors cannot oscillate
            // input; stop following as soon as a certified route is ready.
            if ((antivirusSearchFailed || antivirusIncomplete || !haveShapes) && !AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl && !MovementManager.ForceMoved)
            {
                var trusts = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId) &&
                        a.X > 832.5f && a.X < 871.5f && a.Z > 808.5f && a.Z < 837.5f)
                    .Select(a => new AlexandriaTrustSelector.Candidate(a.ObjectId, new Vector2(a.X, a.Z))).ToArray();
                var trust = AlexandriaTrustSelector.Choose(player, antivirusFallbackActor, trusts);
                if (trust is { } selected)
                {
                    antivirusFallbackActor = selected.Id;
                    var trustPoint = selected.Position;
                    stackInputOwner.CompleteEscape();
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Antivirus Trust fallback");
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Antivirus Trust fallback");
                    stackLeased = true; handled = true;
                    if (Vector2.Distance(player, trustPoint) <= .35f)
                    { ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false; }
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
                    antivirusFacingLeased = true;
                    if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
                    { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
                    TraceAntivirusInput("trust-last-resort", player, trustPoint, now);
                    stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(trustPoint.X, Core.Me.Y, trustPoint.Y));
                    return true;
                }
            }
            // No certified path means no safe destination is known. Stop only
            // this owner's movement while retrying; never invent a point or let
            // ordinary combat travel cross the unresolved hazards. Keep healing
            // and rotation schedulable, and yield to forced movement or a prior
            // escape until the framework has completed that handoff.
            handled = true;
            stackInputOwner.CompleteEscape();
            if (!AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
            {
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Antivirus planning");
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Antivirus planning");
                stackLeased = true;
                TraceAntivirusInput("planning-hold", player, player, now);
                stackInputOwner.HoldStack();
            }
            else TraceAntivirusInput("external-handoff", player, player, now);
            return false;
        }
        antivirusFallbackEnabled = false;
        handled = true;
        // A prior escape or forced movement must finish before this owner can
        // submit a route; stale cleanup must not stop a successor's input.
        stackInputOwner.CompleteEscape();
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.ForceMoved)
        { antivirusMotionLease.Reset(); TraceAntivirusInput("external-handoff", player, player, now); ReleaseAntivirusFacing(); return false; }
        var target = antivirusRoute[antivirusStep].Position;
        var world = new Clio.Utilities.Vector3(target.X, Core.Me.Y, target.Y);
        if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(world)))
        { antivirusMotionLease.Reset(); TraceAntivirusInput("blocked-waypoint", player, target, now); ReleaseAntivirusFacing(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Antivirus route");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Antivirus route");
        stackLeased = true;
        if (Vector2.Distance(player, target) <= .15f && actualWaypoint == player)
        { antivirusMotionLease.Reset(); ReleaseAntivirusFacing(); TraceAntivirusInput("route-hold", player, target, now); stackInputOwner.HoldStack(); return false; }
        // The capture held Verstone and faced the boss across a route deadline.
        // Magitek honors Facing for actions that auto-face their target. Gate
        // only travel, cancel an already-running cast, and release while staged.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
        antivirusFacingLeased = true;
        if (Core.Me.IsCasting && !MovementManager.ForceMoved && DateTime.UtcNow >= antivirusNextCastCancel)
        {
            ActionManager.StopCasting();
            antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200);
        }
        if (MovementManager.GenerallyOutOfControl)
        { antivirusMotionLease.Reset(); TraceAntivirusInput("external-handoff", player, player, now); return false; }
        TraceAntivirusInput("move", player, target, now);
        stackInputOwner.MoveRoute(world);
        antivirusMotionLease.Begin(target, player, now);
        return true;
    }
}
