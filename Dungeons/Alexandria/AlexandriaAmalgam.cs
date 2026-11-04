using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Threading;
using System;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    // A cast timer is not the server's damage snapshot. These provisional
    // windows feed the rolling owner and are rechecked as new casts appear.
    private void CaptureAmalgamCasts()
    {
        if (WorldManager.SubZoneId != (uint)SubZoneId.CorruptedMemoryCache || Core.Me == null || !Core.Me.InCombat)
            return;
        double now = MonotonicNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(actor => actor.IsValid).ToArray();
        CaptureAmalgamSpreads(now, actors);
        amalgamTimeline.Reconcile(now, actors.Where(actor => actor.CastingSpellId is
            36327 or 36329 or 36336 or 39136 or 39138 or 39254 or 39255 or 39256)
            .Select(actor => (actor.ObjectId, actor.CastingSpellId)));
        foreach (var actor in actors)
        {
            uint action = actor.CastingSpellId;
            // Positive stack requirements retain their marker lifecycle;
            // moving-target spreads are sampled separately above.
            if (action is 36333)
            {
                if (!actor.SpellCastInfo.IsValid) { amalgamTimeline.MarkIncomplete(); continue; }
                double unsupportedRemaining = actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
                if (!double.IsFinite(unsupportedRemaining) || unsupportedRemaining < 0) { amalgamTimeline.MarkIncomplete(); continue; }
                // A timer does not prove resolution. Retain the exact source,
                // generation and cast target until the observed marker ends.
                ulong generation = amalgamTimeline.TrackUnsupported(actor.ObjectId, action);
                uint targetId = actor.SpellCastInfo.TargetId;
                // A target can be temporarily unavailable on the first cast
                // pulse. Keep the timeline blocked and retry while casting;
                // never turn an unknown target into a completed marker.
                if (targetId is not (0 or 0xE0000000))
                    amalgamMarkers.ObserveCast(actor.ObjectId, action, targetId, generation);
                continue;
            }
            if (action is not (36327 or 36329 or 36336 or 39136 or 39138 or 39254 or 39255 or 39256)) continue;
            if (!actor.SpellCastInfo.IsValid) { amalgamTimeline.MarkIncomplete(); continue; }
            double remaining = actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
            float heading = actor.Heading;
            if (!double.IsFinite(remaining) || remaining < 0 || !float.IsFinite(heading))
            { amalgamTimeline.MarkIncomplete(); continue; }
            bool ground = action is 36336 or 39254 or 39255 or 39256;
            var point = ground ? actor.SpellCastInfo.CastLocation : actor.Location;
            var origin = new Vector2(point.X, point.Z);
            // Reject zero and off-arena origins instead of constructing a
            // credible-looking hazard from an unpopulated game wrapper.
            if (!AlexandriaGeometry.Finite(origin) || Math.Abs(origin.X + 533) > 30 || Math.Abs(origin.Y + 373) > 30)
            { amalgamTimeline.MarkIncomplete(); continue; }
            var direction = new Vector2((float)Math.Sin(heading), (float)Math.Cos(heading));
            double finish = now + remaining;
            // Voltburst gain times range from 0.346 to 1.589 seconds after
            // estimated finish. Ternary has separate observed, sequential
            // effect windows so the circle and rings do not falsely overlap.
            var window = AlexandriaAmalgamTiming.Window(action, finish);
            amalgamTimeline.Observe(new(actor.ObjectId, action, origin, direction, window.Earliest, window.Latest, false),
                remainingIsZero: remaining == 0);
        }
        foreach (var pending in amalgamMarkers.Snapshot())
        {
            var source = actors.FirstOrDefault(actor => actor.ObjectId == pending.Source);
            var target = pending.Target == Core.Me.ObjectId ? Core.Me :
                actors.FirstOrDefault(actor => actor.ObjectId == pending.Target);
            bool casting = source != null && source.CastingSpellId == pending.Action;
            bool available = target != null && target.IsValid && target.IsVisible && target.IsAlive && target.VfxContainer.IsValid;
            bool present = available && target.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == pending.Marker);
            if (amalgamMarkers.ObserveMarker(pending.Source, pending.Action, now, casting, available, present, out ulong generation) &&
                amalgamTimeline.ResolveUnsupported(pending.Source, pending.Action, generation))
                amalgamMarkers.Forget(pending.Source, pending.Action, generation);
        }
        if (amalgamMarkers.Incomplete) amalgamTimeline.MarkIncomplete();
    }
}

// Search direct walking destinations before adding intermediate waypoints.
// The latter can introduce artificial stops inside a short overlap even when
// a continuous path is certified against every observed effect.
internal static class AlexandriaAmalgamDirectPlanner
{
    internal static AlexandriaGridPlanner.Step[] Plan(Vector2 player, AlexandriaGeometry.Hazard[] shapes,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double spacing,
        double preparationDelay, double horizon, double delay, double minimumSpeed, double maximumSpeed,
        CancellationToken cancellation = default)
    {
        if (!AlexandriaGeometry.Finite(player) || shapes == null || requirements == null ||
            !double.IsFinite(margin) || margin < 0 || margin >= 20 || !double.IsFinite(spacing) || spacing < .5 ||
            !double.IsFinite(preparationDelay) || preparationDelay < 0 || !double.IsFinite(horizon) ||
            horizon <= preparationDelay || cancellation.IsCancellationRequested) return [];
        var hold = new AlexandriaGeometry.Travel(0, preparationDelay, 0, minimumSpeed, maximumSpeed);
        if (!AlexandriaGeometry.TravelSafe(player, player, hold, shapes) ||
            !AlexandriaPositionConstraints.TravelInside(player, player, hold, requirements)) return [];
        var travel = new AlexandriaGeometry.Travel(preparationDelay, horizon, delay, minimumSpeed, maximumSpeed);
        var center = new Vector2(-533, -373);
        double usable = 20 - margin;
        if (Math.Abs(player.X - center.X) > usable || Math.Abs(player.Y - center.Y) > usable) return [];
        Vector2 best = default;
        float bestDistance = float.PositiveInfinity;
        void Consider(Vector2 candidate)
        {
            if (Math.Abs(candidate.X - center.X) > usable || Math.Abs(candidate.Y - center.Y) > usable) return;
            float distance = Vector2.DistanceSquared(player, candidate);
            if (distance >= bestDistance || !AlexandriaGeometry.TravelSafe(player, candidate, travel, shapes) ||
                !AlexandriaPositionConstraints.TravelInside(player, candidate, travel, requirements)) return;
            best = candidate; bestDistance = distance;
        }
        Consider(player);
        int extent = (int)Math.Floor(usable / spacing);
        for (int x = -extent; x <= extent; ++x)
        for (int z = -extent; z <= extent; ++z)
        {
            if (cancellation.IsCancellationRequested) return [];
            Consider(new Vector2((float)(center.X + x * spacing), (float)(center.Y + z * spacing)));
        }
        return float.IsFinite(bestDistance) ? [new(player, preparationDelay), new(best, horizon)] : [];
    }
}

// Tracks observed stack/spread markers independently of cast timer estimates.
// A caller must supply actual target identity and valid marker observations;
// no elapsed cast time alone may retire an unsupported effect.
internal sealed class AlexandriaAmalgamMarkers
{
    internal readonly record struct Pending(uint Source, uint Action, uint Target, uint Marker, ulong Generation);
    private sealed class Entry(Pending pending)
    {
        internal Pending Value = pending;
        internal bool SeenWhileCasting;
        internal double? AbsentSince;
        internal double LastObservation;
        internal bool Observed;
    }
    private readonly Dictionary<(uint Source, uint Action), Entry> entries = [];
    internal bool Incomplete { get; private set; }
    internal Pending[] Snapshot() => entries.Values.Select(entry => entry.Value).ToArray();

    internal void ObserveCast(uint source, uint action, uint target, ulong generation)
    {
        uint marker = action switch { 36333 => 161, 36334 => 139, _ => 0 };
        if (source is 0 or 0xE0000000 || target is 0 or 0xE0000000 || marker == 0 || generation == 0)
        { Incomplete = true; return; }
        var key = (source, action);
        var pending = new Pending(source, action, target, marker, generation);
        if (entries.TryGetValue(key, out var previous))
        {
            if (previous.Value != pending) Incomplete = true;
            return;
        }
        entries.Add(key, new Entry(pending));
    }

    // Marker absence is accepted only after the same marker was seen while
    // this source cast was active, the cast ended, and valid observations agree
    // for 200ms. This debounces transient wrapper loss; it is not an estimated
    // effect deadline. Unknown or missing targets never resolve the mechanic.
    internal bool ObserveMarker(uint source, uint action, double now, bool casting,
        bool markerAvailable, bool markerPresent, out ulong generation)
    {
        generation = 0;
        if (!entries.TryGetValue((source, action), out var entry)) { Incomplete = true; return false; }
        if (!double.IsFinite(now) || now < 0 || (entry.Observed && now < entry.LastObservation))
        { Incomplete = true; return false; }
        entry.Observed = true; entry.LastObservation = now;
        if (!markerAvailable) { entry.AbsentSince = null; return false; }
        if (markerPresent)
        {
            if (casting) entry.SeenWhileCasting = true;
            entry.AbsentSince = null; return false;
        }
        if (casting || !entry.SeenWhileCasting) { entry.AbsentSince = null; return false; }
        entry.AbsentSince ??= now;
        if (now - entry.AbsentSince.Value < .2) return false;
        generation = entry.Value.Generation;
        return true;
    }

    // Remove only the exact generation acknowledged by the timeline; an old
    // marker observation must never retire a successor using the same source.
    internal bool Forget(uint source, uint action, ulong generation) =>
        entries.TryGetValue((source, action), out var entry) && entry.Value.Generation == generation &&
        entries.Remove((source, action));

    internal void Reset() { entries.Clear(); Incomplete = false; }
}

public partial class Alexandria
{
    private readonly AlexandriaAmalgamWorker amalgamWorker = new();
    private readonly AlexandriaTernaryPattern ternaryPattern = new();
    private AlexandriaGridPlanner.Step[] amalgamRoute = [];
    private double amalgamRouteStart, amalgamRetryAfter, amalgamFallbackReportAfter;
    private int amalgamStep;
    private uint amalgamStackTrust;
    private static bool amalgamAuthoredActive, amalgamPulseHandled, amalgamPulseMoving;
    private bool amalgamFallback;

    private void ResetAmalgamRoute()
    {
        amalgamWorker.Reset();
        ternaryPattern.Reset();
        amalgamRoute = []; amalgamStep = 0; amalgamRouteStart = amalgamRetryAfter = amalgamFallbackReportAfter = 0;
        amalgamFallback = false;
        amalgamStackTrust = 0;
        amalgamAuthoredActive = amalgamPulseHandled = amalgamPulseMoving = false;
    }

    private bool TryAmalgamShapes(double now, out AlexandriaGeometry.Hazard[] shapes)
    {
        shapes = [];
        if (amalgamTimeline.HasInvalidEvidence(now) || amalgamSpreadIncomplete) return false;
        var effects = amalgamTimeline.Snapshot(now);
        if (!AlexandriaAmalgamPlanner.TryCreateHazards(effects, .5, 30, out var fixedShapes)) return false;
        for (int i = 0; i < fixedShapes.Length; ++i)
            fixedShapes[i] = fixedShapes[i] with { Earliest = fixedShapes[i].Earliest + now, Latest = fixedShapes[i].Latest + now };
        shapes = fixedShapes.Concat(amalgamSpreadShapes).ToArray();
        return true;
    }

    // This is a rolling solve of the currently observed mechanics. Future
    // baited circles are not predicted: a new cast changes the snapshot and
    // must pass the route check before the owner issues another movement.
    private bool PositionForAmalgamRoute(out bool handled)
    {
        handled = false;
        if (!OwnsQuarantineMovement() || WorldManager.SubZoneId != (uint)SubZoneId.CorruptedMemoryCache)
        { ResetAmalgamRoute(); return false; }
        double now = MonotonicNow;
        var effects = amalgamTimeline.Snapshot(now).Concat(amalgamSpreadShapes.Select(s =>
            new AlexandriaAmalgamPlanner.Effect(36334, s.Origin, s.Direction, s.Earliest - now, s.Latest - now, false))).ToArray();
        bool stackPending = amalgamMarkers.Snapshot().Any(p => p.Action == 36333) || DateTime.UtcNow < superboltUntil;
        if (stackPending && !amalgamTimeline.HasInvalidEvidence(now) && !amalgamSpreadIncomplete)
        {
            amalgamWorker.Reset(); amalgamRoute = []; amalgamStep = 0;
            amalgamAuthoredActive = handled = true;
            bool moving = PositionForSuperboltStack();
            if (!stackLeased && stackInputOwner?.IsCurrent == true && !AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl && !MovementManager.ForceMoved)
            {
                var stackPlayer = new Vector2(Core.Me.X, Core.Me.Z);
                var trusts = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId) &&
                        Math.Abs(a.X + 533) < 19.5 && Math.Abs(a.Z + 373) < 19.5)
                    .Select(a => new AlexandriaTrustSelector.Candidate(a.ObjectId, new Vector2(a.X, a.Z))).ToArray();
                var trust = !amalgamSpreadCasts.Values.Any(s => s.Target == Core.Me.ObjectId && s.Latest >= now)
                    ? AlexandriaTrustSelector.Choose(stackPlayer, amalgamStackTrust, trusts) : null;
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Superbolt fallback");
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Superbolt fallback");
                stackLeased = true;
                if (trust is { } retained) amalgamStackTrust = retained.Id;
                if (trust is { } selected && Vector2.Distance(stackPlayer, selected.Position) > .35f)
                {
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
                    antivirusFacingLeased = true;
                    if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
                    { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
                    stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(selected.Position.X, Core.Me.Y, selected.Position.Y));
                    return true;
                }
                ReleaseAntivirusFacing(); stackInputOwner.HoldStack();
            }
            return moving;
        }
        if (!amalgamTimeline.HasIncompleteEvidence(now) && !amalgamSpreadIncomplete &&
            TryAmalgamShapes(now, out var voltburstShapes) &&
            AlexandriaVoltburstPattern.Choose(now, new Vector2(Core.Me.X, Core.Me.Z), effects, voltburstShapes) is { } voltburstTarget)
        {
            if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            {
                stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
                Navigator.PlayerMover = stackInputOwner; activeStackOwner = this;
            }
            if (stackInputOwner?.IsCurrent != true) return false;
            amalgamWorker.Reset(); amalgamRoute = []; amalgamStep = 0;
            amalgamAuthoredActive = handled = true;
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || MovementManager.ForceMoved) return false;
            stackInputOwner.CompleteEscape();
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Voltburst pattern");
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Voltburst pattern");
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria Voltburst travel");
            stackLeased = antivirusFacingLeased = true;
            if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
            { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
            TraceEncounterMovement("amalgam-voltburst-move", new Vector2(Core.Me.X, Core.Me.Z), voltburstTarget, now);
            stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(voltburstTarget.X, Core.Me.Y, voltburstTarget.Y));
            return true;
        }
        // Ternary's three observed concentric casts have a fixed order. Give
        // that short sequence an authored destination before the general
        // overlap solver, while checking the other live shapes as well.
        if (!amalgamTimeline.HasIncompleteEvidence(now) && !amalgamSpreadIncomplete &&
            TryAmalgamShapes(now, out var ternaryShapes) &&
            ternaryPattern.TrySelect(now, new Vector2(Core.Me.X, Core.Me.Z), effects, ternaryShapes, out var ternaryTarget))
        {
            if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            {
                stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
                Navigator.PlayerMover = stackInputOwner; activeStackOwner = this;
            }
            if (stackInputOwner?.IsCurrent != true) return false;
            amalgamWorker.Reset(); amalgamRoute = []; amalgamStep = 0;
            amalgamAuthoredActive = handled = true;
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || MovementManager.ForceMoved) return false;
            stackInputOwner.CompleteEscape();
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Ternary pattern");
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Ternary pattern");
            stackLeased = true;
            var ternaryPlayer = new Vector2(Core.Me.X, Core.Me.Z);
            if (Vector2.Distance(ternaryPlayer, ternaryTarget) <= .15f)
            {
                TraceEncounterMovement($"amalgam-ternary-{ternaryPattern.Stage}-hold", ternaryPlayer, ternaryTarget, now);
                ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false;
            }
            TraceEncounterMovement($"amalgam-ternary-{ternaryPattern.Stage}-move", ternaryPlayer, ternaryTarget, now);
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria Ternary travel");
            antivirusFacingLeased = true;
            if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
            { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
            stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(ternaryTarget.X, Core.Me.Y, ternaryTarget.Y));
            return true;
        }
        // Incomplete actor or marker data must not look like a clear arena.
        if (effects.Length == 0 || amalgamTimeline.HasIncompleteEvidence(now) || amalgamSpreadIncomplete)
        {
            bool wasActive = amalgamAuthoredActive;
            ResetAmalgamRoute();
            if (wasActive) ReleaseQuarantineStack();
            return false;
        }
        if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
            Navigator.PlayerMover = stackInputOwner; activeStackOwner = this;
        }
        if (stackInputOwner?.IsCurrent != true) { ResetAmalgamRoute(); return false; }
        amalgamAuthoredActive = true;
        var player = new Vector2(Core.Me.X, Core.Me.Z);
        bool wasPending = amalgamWorker.Pending;
        if (amalgamWorker.TryTake(now, .35, out var result))
        {
            amalgamRoute = result.Route; amalgamRouteStart = result.Started; amalgamStep = 1;
            amalgamFallback = false;
        }
        else if (wasPending && !amalgamWorker.Pending)
        {
            amalgamFallback = true;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[Alexandria] Amalgam worker returned no usable route; effects={effects.Length}.");
        }

        // Convert the newest snapshot using the route's actual horizon. If a
        // newly seen effect extends beyond that horizon, invalidate the route
        // and search again rather than silently dropping that mechanic.
        var requirements = Array.Empty<AlexandriaPositionConstraints.Timed>();
        AlexandriaGeometry.Hazard[] shapes = [];
        var decision = AlexandriaAmalgamRoute.Decision.Invalid;
        if (amalgamRoute.Length > 1 &&
            AlexandriaAmalgamPlanner.TryCreateHazards(effects, .5, amalgamRouteStart + amalgamRoute[^1].Time - now, out shapes))
        {
            for (int i = 0; i < shapes.Length; ++i)
                shapes[i] = shapes[i] with { Earliest = shapes[i].Earliest + now, Latest = shapes[i].Latest + now };
            decision = AlexandriaAmalgamRoute.Evaluate(player, now, amalgamRouteStart, amalgamRoute, ref amalgamStep,
                shapes, requirements, .5, .1, 5, 10, .15);
        }
        if (decision is AlexandriaAmalgamRoute.Decision.Invalid or AlexandriaAmalgamRoute.Decision.Complete)
        {
            if (amalgamRoute.Length != 0 && decision == AlexandriaAmalgamRoute.Decision.Invalid)
            {
                amalgamFallback = true;
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                    Logger.Information($"[Alexandria] Amalgam route invalidated by current position or effects; step={amalgamStep}, effects={effects.Length}.");
            }
            amalgamRoute = []; amalgamStep = 0;
            if (!amalgamWorker.Pending && now >= amalgamRetryAfter)
            {
                // Half-second stages cover every observed effect through its
                // conservative end. The worker holds the initial point for
                // its publication allowance and certifies that hold too.
                int ticks = Math.Clamp((int)Math.Ceiling(effects.Max(e => e.Latest) / .5) + 1, 2, 60);
                var times = Enumerable.Range(0, ticks + 1).Select(i => i * .5).ToArray();
                amalgamWorker.StartKnownSnapshot(player, now, effects, requirements, .5, 1, times, .1, 5, 10);
                amalgamRetryAfter = now + .5;
            }
            handled = true;
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || MovementManager.ForceMoved)
                return false;
            stackInputOwner.CompleteEscape();
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Amalgam planning");
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Amalgam planning");
            stackLeased = true;
            if (amalgamFallback && AlexandriaAmalgamPlanner.TryCreateHazards(effects, .5, 30, out shapes))
            {
                for (int i = 0; i < shapes.Length; ++i)
                    shapes[i] = shapes[i] with { Earliest = shapes[i].Earliest + now, Latest = shapes[i].Latest + now };
                var candidates = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))
                    .Select(a => new Vector2(a.X, a.Z)).OrderBy(p => Vector2.DistanceSquared(player, p));
                foreach (var point in candidates)
                {
                    double distance = Vector2.Distance(player, point);
                    double next = shapes.Where(h => h.Latest >= now).Select(h => h.Latest).DefaultIfEmpty(now).Min();
                    double end = Math.Max(now + .1 + distance / 5 + .25, Math.Min(now + 3, next + .15));
                    if (distance <= .15 || end - now > 3 || Math.Abs(point.X + 533) >= 19.5 || Math.Abs(point.Y + 373) >= 19.5 ||
                        !AlexandriaGeometry.TravelSafe(player, point, new(now, end, .1, 5, 10), shapes)) continue;
                    if (LoggingHelpers.MechanicDiagnosticsEnabled && now >= amalgamFallbackReportAfter)
                    {
                        Logger.Information(FormattableString.Invariant($"[Alexandria] Amalgam Trust candidate=({point.X:F3},{point.Y:F3})"));
                        amalgamFallbackReportAfter = now + 1;
                    }
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
                    antivirusFacingLeased = true;
                    if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
                    { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
                    stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(point.X, Core.Me.Y, point.Y));
                    return true;
                }
            }
            ReleaseAntivirusFacing();
            stackInputOwner.HoldStack();
            return false;
        }
        handled = true;
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || MovementManager.ForceMoved) return false;
        stackInputOwner.CompleteEscape();
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Amalgam route");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Amalgam route");
        stackLeased = true;
        if (decision == AlexandriaAmalgamRoute.Decision.Hold) { ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
        antivirusFacingLeased = true;
        if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
        { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
        var target = amalgamRoute[amalgamStep].Position;
        stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(target.X, Core.Me.Y, target.Y));
        return true;
    }
}

/// <summary>
/// Detached Amalgam overlap planner. Converts caller-verified mechanic
/// snapshots into a single timed constraint set before searching the arena;
/// it never reads client objects, issues input or guesses an effect time.
/// </summary>
internal static class AlexandriaAmalgamPlanner
{
    // A spread position is the other target's predicted position, with its
    // movement uncertainty included in the margin by the caller. IsLocalTarget
    // must come from a verified target identity, not party ordering.
    internal readonly record struct Effect(uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    /// <summary>
    /// Plans one route through all supplied effects and positive requirements.
    /// The caller must establish complete relevant evidence for this horizon;
    /// passing false forbids treating an absent effect as a safe arena. A false
    /// result includes both invalid/missing evidence and no certified
    /// finite-grid path. The caller must retain ownership and recover safely;
    /// this method does not turn an empty result into an avoidance fallback.
    /// Optional compression preserves the full temporal constraints but requires
    /// an executor that can follow long direct segments within the same envelope.
    /// </summary>
    internal static bool TryPlan(Vector2 player, Effect[] effects, AlexandriaPositionConstraints.Timed[] requirements,
        double margin, double spacing, double[] times, double delay, double minimumSpeed, double maximumSpeed,
        bool evidenceComplete, out AlexandriaGridPlanner.Step[] route, CancellationToken cancellation = default,
        bool compressRoute = false)
    {
        if (!evidenceComplete) { route = Array.Empty<AlexandriaGridPlanner.Step>(); return false; }
        return TryPlanKnownSnapshot(player, effects, requirements, margin, spacing, times, delay, minimumSpeed,
            maximumSpeed, out route, cancellation, compressRoute);
    }

    // Conditional rolling solve: this certifies only supplied, already observed
    // effects. A live caller must revalidate against newly discovered casts on
    // every pulse; success does not establish future mechanic completeness.
    internal static bool TryPlanKnownSnapshot(Vector2 player, Effect[] effects, AlexandriaPositionConstraints.Timed[] requirements,
        double margin, double spacing, double[] times, double delay, double minimumSpeed, double maximumSpeed,
        out AlexandriaGridPlanner.Step[] route, CancellationToken cancellation = default, bool compressRoute = false)
    {
        route = Array.Empty<AlexandriaGridPlanner.Step>();
        if (effects == null || requirements == null || times == null || times.Length < 2 ||
            !double.IsFinite(times[^1]) || !double.IsFinite(margin) || margin < 0 || cancellation.IsCancellationRequested)
            return false;
        if (!TryCreateHazards(effects, margin, times[^1], out var shapes, cancellation)) return false;
        // The square arena is 40 x 40 centered on (-533,-373). Erode it by
        // the same supplied uncertainty margin used for the damaging shapes.
        route = AlexandriaGridPlanner.Plan(player, new Vector2(-533, -373), 20, 20, margin, spacing,
            times, shapes, requirements, delay, minimumSpeed, maximumSpeed, cancellation);
        if (route.Length == 0) return false;
        // Existing callers may rely on the full staged route. A movement owner
        // must explicitly opt into direct-segment execution after checking its
        // controller's timing and arrival behavior.
        if (!compressRoute) return true;
        // The graph uses half-second stages to find overlap-safe positions.
        // Executing every artificial stage adds needless stops and repeated
        // input handoffs. Drop intermediate stages only when a direct segment
        // is certified against every timed hazard and positive requirement.
        // The arena is convex, so a segment between its eroded grid points
        // remains on the same traversable floor. This changes neither effect
        // lifetimes nor the caller's duty to provide complete evidence.
        int[] previous = new int[route.Length]; Array.Fill(previous, -1);
        int[] segments = new int[route.Length]; Array.Fill(segments, int.MaxValue); segments[0] = 0;
        for (int end = 1; end < route.Length; ++end)
        {
            if (cancellation.IsCancellationRequested) { route = Array.Empty<AlexandriaGridPlanner.Step>(); return false; }
            for (int begin = 0; begin < end; ++begin)
            {
                if (segments[begin] == int.MaxValue || segments[begin] + 1 >= segments[end]) continue;
                var travel = new AlexandriaGeometry.Travel(route[begin].Time, route[end].Time, delay, minimumSpeed, maximumSpeed);
                if (!AlexandriaGeometry.TravelSafe(route[begin].Position, route[end].Position, travel, shapes) ||
                    !AlexandriaPositionConstraints.TravelInside(route[begin].Position, route[end].Position, travel, requirements)) continue;
                previous[end] = begin; segments[end] = segments[begin] + 1;
            }
        }
        if (previous[^1] < 0 || cancellation.IsCancellationRequested)
        { route = Array.Empty<AlexandriaGridPlanner.Step>(); return false; }
        var compressed = new List<AlexandriaGridPlanner.Step>();
        for (int at = route.Length - 1; at > 0; at = previous[at]) compressed.Add(route[at]);
        compressed.Add(route[0]); compressed.Reverse(); route = compressed.ToArray();
        return true;
    }

    // Planning and runtime revalidation must use identical shape construction;
    // otherwise a newly observed overlap could be checked against weaker
    // geometry than the detached worker used. Unknown actions fail closed.
    internal static bool TryCreateHazards(Effect[] effects, double margin, double horizon,
        out AlexandriaGeometry.Hazard[] shapes, CancellationToken cancellation = default)
    {
        shapes = Array.Empty<AlexandriaGeometry.Hazard>();
        if (effects == null || !double.IsFinite(margin) || margin < 0 || !double.IsFinite(horizon) ||
            cancellation.IsCancellationRequested) return false;
        var hazards = new List<AlexandriaGeometry.Hazard>();
        foreach (var effect in effects)
        {
            if (cancellation.IsCancellationRequested || !double.IsFinite(effect.Latest) || effect.Latest > horizon) return false;
            // The parent announces two lanes centered either side of its axis.
            if (effect.Action == 36329)
            {
                if (!AlexandriaMechanicShapes.TryCreateSplit(effect.Origin, effect.Direction, margin,
                    effect.Earliest, effect.Latest, out var lanes)) return false;
                hazards.AddRange(lanes);
                continue;
            }
            if (effect.Action is not (36327 or 36334 or 36336 or 39136 or 39138 or 39254 or 39255 or 39256))
                return false;
            if (!AlexandriaMechanicShapes.TryCreate(effect.Action, effect.Origin, effect.Direction, margin,
                effect.Earliest, effect.Latest, out var hazard)) return false;
            if (effect.Action == 36334 && effect.IsLocalTarget) continue;
            hazards.Add(hazard);
        }
        shapes = hazards.ToArray();
        return true;
    }
}

// Pure handoff check for a detached Amalgam route. It never reads game objects
// or owns input: the caller supplies one complete, current absolute-time hazard
// snapshot and remains responsible for generation, lifecycle and movement.
internal static class AlexandriaAmalgamRoute
{
    internal enum Decision { Invalid, Complete, Hold, Move }

    // A completed worker cannot authorize its stale position or hazard snapshot.
    // Recheck the actual player-to-waypoint envelope and every remaining edge;
    // holding inside the nominal arrival tolerance is allowed only when both
    // the actual position and its next edge remain safe. The arena is convex.
    internal static Decision Evaluate(Vector2 player, double now, double routeStart,
        AlexandriaGridPlanner.Step[] route, ref int step, AlexandriaGeometry.Hazard[] hazards,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double delay,
        double minimumSpeed, double maximumSpeed, double tolerance)
    {
        bool InArena(Vector2 point) => AlexandriaGeometry.Finite(point) &&
            Math.Abs(point.X + 533) < 20 - margin && Math.Abs(point.Y + 373) < 20 - margin;
        if (!double.IsFinite(now) || !double.IsFinite(routeStart) || !double.IsFinite(margin) || margin < 0 || margin >= 20 ||
            !double.IsFinite(tolerance) || tolerance < 0 || !InArena(player) || route == null || route.Length < 2 ||
            hazards == null || requirements == null || step < 1 || step > route.Length)
            return Decision.Invalid;
        for (int i = 0; i < route.Length; ++i)
            if (!InArena(route[i].Position) || !double.IsFinite(route[i].Time) ||
                (i > 0 && route[i].Time <= route[i - 1].Time)) return Decision.Invalid;
        double horizon = routeStart + route[^1].Time;
        double plannedStart = routeStart + route[0].Time;
        if (!double.IsFinite(horizon) || !double.IsFinite(plannedStart) || now < routeStart) return Decision.Invalid;
        foreach (var hazard in hazards)
            if (!hazard.Valid || hazard.Latest > horizon) return Decision.Invalid;
        foreach (var requirement in requirements)
            if (!double.IsFinite(requirement.Latest) || requirement.Latest > horizon) return Decision.Invalid;

        int current = step;
        while (current < route.Length && now >= routeStart + route[current].Time)
        {
            if (Vector2.Distance(player, route[current].Position) > tolerance) return Decision.Invalid;
            ++current;
        }
        if (current >= route.Length) { step = current; return Decision.Complete; }

        bool Safe(Vector2 from, Vector2 to, double begin, double end, double startDelay)
        {
            var travel = new AlexandriaGeometry.Travel(begin, end, startDelay, minimumSpeed, maximumSpeed);
            return AlexandriaGeometry.TravelSafe(from, to, travel, hazards) &&
                AlexandriaPositionConstraints.TravelInside(from, to, travel, requirements);
        }

        bool preparing = now < plannedStart;
        if (preparing && (Vector2.Distance(player, route[0].Position) > tolerance ||
            !Safe(player, player, now, plannedStart, 0))) return Decision.Invalid;
        double executionStart = preparing ? plannedStart : now;
        // A reached intermediate stage need not impose a stop until its exact
        // deadline. Depart early only when the entire observed-position-to-next
        // envelope remains valid; remaining edges are checked below as usual.
        while (current + 1 < route.Length && Vector2.Distance(player, route[current].Position) <= tolerance &&
            Safe(player, route[current + 1].Position, executionStart, routeStart + route[current + 1].Time, delay))
            ++current;

        var waypoint = route[current];
        double due = routeStart + waypoint.Time;
        bool near = Vector2.Distance(player, waypoint.Position) <= tolerance;
        bool hold = near && Safe(player, player, executionStart, due, 0);
        Vector2 nextStart = hold ? player : waypoint.Position;
        if (!hold && !Safe(player, waypoint.Position, executionStart, due, delay)) return Decision.Invalid;
        for (int i = current + 1; i < route.Length; ++i)
        {
            var from = i == current + 1 ? nextStart : route[i - 1].Position;
            if (!Safe(from, route[i].Position, routeStart + route[i - 1].Time,
                routeStart + route[i].Time, delay)) return Decision.Invalid;
        }
        step = current;
        return preparing || hold ? Decision.Hold : Decision.Move;
    }
}

public partial class Alexandria
{
    private readonly Dictionary<uint, (uint Target, double Earliest, double Latest)> amalgamSpreadCasts = [];
    private AlexandriaGeometry.Hazard[] amalgamSpreadShapes = [];
    private bool amalgamSpreadIncomplete, amalgamSpreadCorrupt;

    // Static Spark follows its target. Store only detached identities and
    // update the target positions on each bot pulse through the effect margin.
    private void CaptureAmalgamSpreads(double now, BattleCharacter[] actors)
    {
        amalgamSpreadIncomplete = amalgamSpreadCorrupt;
        var active = actors.Where(a => a.CastingSpellId == 36334).ToArray();
        foreach (var source in active)
        {
            if (!source.SpellCastInfo.IsValid) { amalgamSpreadIncomplete = true; continue; }
            uint target = source.SpellCastInfo.TargetId;
            double remaining = source.SpellCastInfo.RemainingCastTime.TotalSeconds;
            if (target is 0 or 0xE0000000 || !double.IsFinite(remaining) || remaining < 0)
            { amalgamSpreadIncomplete = true; continue; }
            double finish = now + remaining;
            if (amalgamSpreadCasts.TryGetValue(source.ObjectId, out var prior))
            {
                if (prior.Target != target) { amalgamSpreadIncomplete = amalgamSpreadCorrupt = true; continue; }
                if (remaining == 0) continue;
                amalgamSpreadCasts[source.ObjectId] = (target, Math.Min(prior.Earliest, finish - .2), Math.Max(prior.Latest, finish + .8));
            }
            else if (remaining > 0) amalgamSpreadCasts[source.ObjectId] = (target, finish - .2, finish + .8);
            else amalgamSpreadIncomplete = amalgamSpreadCorrupt = true;
        }
        var shapes = new List<AlexandriaGeometry.Hazard>();
        foreach (var entry in amalgamSpreadCasts.ToArray())
        {
            if (now > entry.Value.Latest)
            {
                if (active.Any(a => a.ObjectId == entry.Key)) amalgamSpreadIncomplete = true;
                else amalgamSpreadCasts.Remove(entry.Key);
                continue;
            }
            if (entry.Value.Target == Core.Me.ObjectId) continue;
            var target = actors.FirstOrDefault(a => a.ObjectId == entry.Value.Target && a.IsVisible && a.IsAlive);
            if (target == null || !AlexandriaMechanicShapes.TryCreate(36334, new Vector2(target.X, target.Z), Vector2.UnitX,
                .5, entry.Value.Earliest, entry.Value.Latest, out var spread))
            { amalgamSpreadIncomplete = true; continue; }
            shapes.Add(spread);
        }
        var local = amalgamSpreadCasts.Values.Where(s => s.Target == Core.Me.ObjectId && s.Latest >= now).ToArray();
        if (local.Length != 0)
        {
            var party = actors.Where(a => a.IsVisible && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
            if (party.Length == 0) amalgamSpreadIncomplete = true;
            foreach (var member in party)
            foreach (var cast in local)
            {
                if (!AlexandriaMechanicShapes.TryCreate(36334, new Vector2(member.X, member.Z), Vector2.UnitX,
                    .5, cast.Earliest, cast.Latest, out var spread)) { amalgamSpreadIncomplete = true; continue; }
                shapes.Add(spread);
            }
        }
        amalgamSpreadShapes = shapes.ToArray();
    }
}

/// <summary>
/// Retains detached Amalgam helper casts across disappearing game wrappers so
/// a combined route does not silently lose a still-active effect. The caller
/// owns cast discovery, identity, timing bounds and the encounter lifecycle.
/// </summary>
internal sealed class AlexandriaAmalgamTimeline
{
    internal readonly record struct Cast(uint Source, uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    private readonly Dictionary<(uint Source, uint Action), Cast> casts = [];
    private readonly HashSet<(uint Source, uint Action)> overdue = [];
    private readonly Dictionary<(uint Source, uint Action), ulong> unsupported = [];
    private ulong nextGeneration;
    internal bool Incomplete { get; private set; }

    // Once a snapshot is invalid, retaining prior entries is not proof that
    // every hazard was captured. Only an encounter reset clears this condition.
    internal void MarkIncomplete() => Incomplete = true;

    // Unsupported stack/spread effects must remain unresolved until an
    // authoritative matching signal exists. Estimated cast timers alone do
    // not prove their effect or marker has ended.
    internal ulong TrackUnsupported(uint source, uint action)
    {
        if (source == 0 || action == 0) { MarkIncomplete(); return 0; }
        var key = (source, action);
        if (unsupported.TryGetValue(key, out var generation)) return generation;
        if (nextGeneration == ulong.MaxValue) { MarkIncomplete(); return 0; }
        generation = ++nextGeneration;
        unsupported.Add(key, generation);
        return generation;
    }

    // A caller may invoke this only after verifying the matching effect or
    // cancellation. Generations prevent an old receipt from clearing a newer
    // mechanic that reused the same helper identity, including after reset.
    internal bool ResolveUnsupported(uint source, uint action, ulong generation) =>
        generation != 0 && unsupported.TryGetValue((source, action), out var current) &&
        current == generation && unsupported.Remove((source, action));

    internal bool HasIncompleteEvidence(double now) =>
        !double.IsFinite(now) || Incomplete || overdue.Count != 0 || unsupported.Count != 0;

    // A stack marker deliberately keeps unsupported evidence pending. The
    // stack owner can still use separately observed fixed hazards if no
    // malformed or overdue cast has made that snapshot unreliable.
    internal bool HasInvalidEvidence(double now) => !double.IsFinite(now) || Incomplete || overdue.Count != 0;

    internal void Reset()
    {
        casts.Clear();
        overdue.Clear();
        Incomplete = false;
        unsupported.Clear();
    }

    internal void Observe(Cast cast, bool remainingIsZero = false)
    {
        if (cast.Source == 0 || cast.Action == 0 || !AlexandriaGeometry.Finite(cast.Origin) ||
            !AlexandriaGeometry.Finite(cast.Direction) || !double.IsFinite(cast.Earliest) ||
            !double.IsFinite(cast.Latest) || cast.Earliest > cast.Latest)
        { MarkIncomplete(); return; }

        var key = (cast.Source, cast.Action);
        // A continuously visible wrapper beyond its original retained window
        // cannot be distinguished from stale or reused cast data. Do not let
        // repeated polling extend it or authorize a route until it disappears.
        if (overdue.Contains(key)) return;
        if (casts.TryGetValue(key, out var previous))
        {
            // A changed origin, orientation or target can mean a reused helper
            // identity. Do not merge distinct effects into a guessed shape.
            if (Vector2.Distance(previous.Origin, cast.Origin) > .25f ||
                Vector2.Distance(previous.Direction, cast.Direction) > .01f ||
                previous.IsLocalTarget != cast.IsLocalTarget)
            { MarkIncomplete(); return; }
            // A wrapper can keep reporting zero remaining after its actual
            // cast has ended. Recomputing now + zero would indefinitely move
            // the effect forward, so keep the last valid union in that state.
            // While the cast still has positive time, retain polling jitter
            // conservatively rather than shrinking the prior window.
            cast = remainingIsZero ? previous : cast with { Earliest = Math.Min(previous.Earliest, cast.Earliest),
                Latest = Math.Max(previous.Latest, cast.Latest) };
        }
        else if (remainingIsZero)
        {
            // Discovering an already expired wrapper cannot establish its
            // true finish or whether an earlier cast has just been retired.
            MarkIncomplete();
            return;
        }
        casts[key] = cast;
    }

    // Reconcile on the same pulse that sampled the object table, before
    // observing casts. Retain expired identities while their wrapper remains;
    // otherwise a stale zero timer would be mistaken for a fresh generation.
    // Once absent, release only that expired identity so normal planning can
    // recover without clearing unrelated unresolved mechanics.
    internal void Reconcile(double now, IEnumerable<(uint Source, uint Action)> active)
    {
        if (!double.IsFinite(now) || active == null) { MarkIncomplete(); return; }
        var present = active.ToHashSet();
        foreach (var item in casts.Where(pair => pair.Value.Latest < now).ToArray())
        {
            if (present.Contains(item.Key)) overdue.Add(item.Key);
            else { casts.Remove(item.Key); overdue.Remove(item.Key); }
        }
    }

    // The worker's stages begin at zero; translate retained monotonic absolute
    // windows to that same snapshot origin before returning detached effects.
    internal AlexandriaAmalgamPlanner.Effect[] Snapshot(double now)
    {
        if (!double.IsFinite(now)) { MarkIncomplete(); return []; }
        return casts.Values.Where(cast => cast.Latest >= now).Select(cast => new AlexandriaAmalgamPlanner.Effect(cast.Action, cast.Origin,
            cast.Direction, cast.Earliest - now, cast.Latest - now, cast.IsLocalTarget)).ToArray();
    }

    // The caller must independently establish that its discovery covers the
    // whole planning horizon, including upcoming mechanics and marker targets.
    // An empty or blocked snapshot cannot authorize an encounter-owned route.
    internal bool TryGetPlanningSnapshot(double now, bool discoveryComplete,
        out AlexandriaAmalgamPlanner.Effect[] effects)
    {
        effects = Snapshot(now);
        if (!discoveryComplete || HasIncompleteEvidence(now) || effects.Length == 0)
        {
            effects = [];
            return false;
        }
        return true;
    }

    // The worker consumes relative windows, whereas a route remains live as
    // monotonic time advances. Revalidation must restore those same windows to
    // the absolute clock rather than compare unlike time bases. Completeness
    // and horizon remain caller-supplied evidence, never inferred from a cast.
    internal bool TryGetAbsoluteHazards(double now, double routeHorizon, double margin,
        bool discoveryComplete, out AlexandriaGeometry.Hazard[] hazards)
    {
        hazards = [];
        if (!double.IsFinite(now) || !double.IsFinite(routeHorizon) || routeHorizon < now ||
            !TryGetPlanningSnapshot(now, discoveryComplete, out var effects) ||
            !AlexandriaAmalgamPlanner.TryCreateHazards(effects, margin, routeHorizon - now, out var relative))
            return false;
        for (int i = 0; i < relative.Length; ++i)
        {
            var shape = relative[i] with { Earliest = relative[i].Earliest + now, Latest = relative[i].Latest + now };
            if (!shape.Valid || shape.Latest > routeHorizon) return false;
            relative[i] = shape;
        }
        hazards = relative;
        return true;
    }
}

internal static class AlexandriaAmalgamTiming
{
    // Five Arelia captures for each Ternary effect placed receipts 0.203–0.367s
    // after reported cast finish. The wider provisional window preserves a
    // full second between the two-second-spaced circle/rings; the previous
    // generic 2.2-second window made those disjoint shapes overlap and left no
    // possible route. Other actions retain their older, wider bounds.
    internal static (double Earliest, double Latest) Window(uint action, double finish) =>
        action is 39254 or 39255 or 39256 ? (finish - .2, finish + .8) : (finish - .5, finish + 1.7);
}

// Owned by the encounter pulse thread. Worker inputs are detached values only;
// cancellation and reset retire this worker without touching the input channel.
internal sealed class AlexandriaAmalgamWorker
{
    internal readonly record struct Result(double Started, AlexandriaGridPlanner.Step[] Route);
    private Task<AlexandriaGridPlanner.Step[]> pending;
    private CancellationTokenSource cancellation;
    private double started;

    internal bool Pending => pending != null;

    // The caller must establish complete mechanic evidence for its horizon.
    // Copy nested party centers as well as arrays, so a later pulse cannot
    // mutate a worker's view while it searches. Times are snapshot-relative.
    internal bool Start(Vector2 player, double now, AlexandriaAmalgamPlanner.Effect[] effects,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double spacing,
        double[] times, double delay, double minimumSpeed, double maximumSpeed, bool evidenceComplete,
        double preparationDelay = .35)
    {
        return evidenceComplete && StartKnownSnapshot(player, now, effects, requirements, margin, spacing,
            times, delay, minimumSpeed, maximumSpeed, preparationDelay);
    }

    // A rolling caller may plan the currently observed effects without
    // pretending that future baited casts are already known. It must check
    // the live snapshot and revalidate every edge before issuing any input.
    internal bool StartKnownSnapshot(Vector2 player, double now, AlexandriaAmalgamPlanner.Effect[] effects,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double spacing,
        double[] times, double delay, double minimumSpeed, double maximumSpeed, double preparationDelay = .35)
    {
        if (pending != null || !double.IsFinite(now) || effects == null ||
            requirements == null || times == null || times.Length < 2 || times[0] != 0 ||
            !double.IsFinite(preparationDelay) || preparationDelay < 0) return false;
        var detachedEffects = effects.ToArray();
        var detachedRequirements = requirements.Select(r => r with { Centers = r.Centers?.ToArray() }).ToArray();
        var detachedTimes = times.Select(time => time + preparationDelay).ToArray();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        started = now;
        pending = Task.Run(() =>
        {
            // The worker may consume its entire publication allowance. Reserve
            // that time at the observed starting point and certify the hold;
            // otherwise a route can already be unsafe before its first command.
            if (!AlexandriaAmalgamPlanner.TryCreateHazards(detachedEffects, margin, detachedTimes[^1], out var shapes, token))
                return Array.Empty<AlexandriaGridPlanner.Step>();
            var hold = new AlexandriaGeometry.Travel(0, preparationDelay, 0, minimumSpeed, maximumSpeed);
            if (!AlexandriaGeometry.TravelSafe(player, player, hold, shapes) ||
                !AlexandriaPositionConstraints.TravelInside(player, player, hold, detachedRequirements))
                return Array.Empty<AlexandriaGridPlanner.Step>();
            var direct = AlexandriaAmalgamDirectPlanner.Plan(player, shapes, detachedRequirements, margin, spacing,
                preparationDelay, detachedTimes[^1], delay, minimumSpeed, maximumSpeed, token);
            if (direct.Length != 0 || token.IsCancellationRequested) return direct;
            return AlexandriaAmalgamPlanner.TryPlanKnownSnapshot(player, detachedEffects, detachedRequirements,
                margin, spacing, detachedTimes, delay, minimumSpeed, maximumSpeed, out var route, token,
                compressRoute: true) ? route : Array.Empty<AlexandriaGridPlanner.Step>();
        }, token);
        return true;
    }

    // Publication does not authorize movement. The caller must compare the
    // result with current mechanic evidence and run the route evaluator from the
    // actual player position before every input, including after new hazards.
    internal bool TryTake(double now, double maximumAge, out Result result)
    {
        result = default;
        if (pending == null || !pending.IsCompleted) return false;
        var completed = pending; pending = null;
        cancellation?.Dispose(); cancellation = null;
        if (!double.IsFinite(now) || !double.IsFinite(maximumAge) || maximumAge < 0 || now < started ||
            now - started > maximumAge || !completed.IsCompletedSuccessfully)
        {
            _ = completed.Exception;
            return false;
        }
        var route = completed.Result;
        if (route.Length == 0) return false;
        result = new Result(started, route);
        return true;
    }

    internal void Reset()
    {
        cancellation?.Cancel();
        // A retired task can finish later, but has no reference to this
        // owner's state or mover and may never publish into a successor.
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        cancellation?.Dispose(); cancellation = null;
        started = 0;
    }
}

// While the first Ternary Charge circle is unresolved, ordinary combat travel
// must not undo native avoidance by cutting back through that circle.
internal static class AlexandriaTernaryPolicy
{
    internal static bool Allows(Vector2 from, Vector2 to, Vector2 origin, float radius)
    {
        var start = from - origin;
        var delta = to - from;
        float radiusSquared = radius * radius;
        if (start.LengthSquared() <= radiusSquared)
        {
            // A character already inside may continue directly outward, never
            // be held there or travel deeper into the danger.
            return Vector2.Dot(start, delta) >= 0 && (to - origin).LengthSquared() > start.LengthSquared();
        }
        float lengthSquared = delta.LengthSquared();
        if (lengthSquared < .0001f) return true;
        float fraction = Math.Clamp(-Vector2.Dot(start, delta) / lengthSquared, 0, 1);
        return (start + delta * fraction).LengthSquared() > radiusSquared;
    }
}

// Ternary Charge is a fixed circle, inner ring, outer ring sequence. Stage
// just outside the first circle, then enter the center after its observed
// effect window. Other simultaneous casts still constrain every candidate.
internal sealed class AlexandriaTernaryPattern
{
    private Vector2 origin;
    private double switchAt, expires;
    private Vector2? destination;
    private int stage = -1;
    internal int Stage => stage;

    internal void Reset() { switchAt = expires = 0; destination = null; stage = -1; }

    internal bool TrySelect(double now, Vector2 player, AlexandriaAmalgamPlanner.Effect[] effects,
        AlexandriaGeometry.Hazard[] hazards, out Vector2 target)
    {
        target = default;
        if (!double.IsFinite(now) || !AlexandriaGeometry.Finite(player) || effects == null || hazards == null) return false;
        var first = Array.Find(effects, e => e.Action == 39254);
        var middle = Array.Find(effects, e => e.Action == 39255);
        var last = Array.Find(effects, e => e.Action == 39256);
        if (first.Action != 0 && middle.Action != 0 && last.Action != 0)
        {
            if (Vector2.Distance(first.Origin, middle.Origin) > .5f || Vector2.Distance(first.Origin, last.Origin) > .5f ||
                middle.Earliest <= first.Earliest || last.Earliest <= middle.Earliest) { Reset(); return false; }
            if (expires == 0) { origin = first.Origin; switchAt = now + first.Latest; expires = now + last.Latest; }
        }
        if (expires == 0 || now >= expires) { Reset(); return false; }
        int nextStage = now < switchAt ? 0 : 1;
        if (nextStage != stage) { stage = nextStage; destination = null; }
        double deadline = stage == 0 ? switchAt - 1 : now + middle.Earliest;
        if (stage == 1 && middle.Action == 0) deadline = now + .5;
        bool InStage(Vector2 p)
        {
            double radius = Vector2.Distance(p, origin);
            return stage == 0 ? radius >= 10.6 : radius <= 9.2;
        }
        bool Safe(Vector2 p)
        {
            if (Math.Abs(p.X + 533) >= 19.5 || Math.Abs(p.Y + 373) >= 19.5 || !InStage(p)) return false;
            double length = Vector2.Distance(player, p), arrival = now + .101 + length / 5;
            if (!AlexandriaGeometry.TravelSafe(player, p, new(now, arrival, .1, 5, 8), hazards)) return false;
            // The selected point must also be safe at the next effect, not
            // merely at the instant the walking segment finishes.
            double check = Math.Max(now, deadline);
            foreach (var h in hazards)
            {
                // The next line can start during the rings and resolve just
                // afterward. Pick an inner-ring point safe for that observed
                // successor too, before the general owner needs to resume.
                bool relevant = h.Earliest <= check && check <= h.Latest ||
                    stage == 1 && h.Latest >= now && h.Earliest <= expires + 3;
                if (relevant && AlexandriaGeometry.Distance(p, h) <= h.Margin) return false;
            }
            return true;
        }
        if (destination is { } retained && Safe(retained)) { target = retained; return true; }
        if (Safe(player)) { target = player; destination = player; return true; }
        Vector2? best = null; float bestDistance = float.PositiveInfinity;
        double[] radii = stage == 0 ? [11.5, 13.0] : [8.5, 6.5, 0];
        foreach (double radius in radii)
        for (int i = 0; i < (radius == 0 ? 1 : 24); ++i)
        {
            double angle = Math.PI * i / 12;
            var p = origin + new Vector2((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
            float distance = Vector2.DistanceSquared(player, p);
            if (distance >= bestDistance || !Safe(p)) continue;
            best = p; bestDistance = distance;
        }
        if (best is not { } selected) return false;
        destination = selected; target = selected; return true;
    }
}

// Voltburst circles resolve in observed order. Move out of the first circle
// threatening the player, while checking the simultaneously timed shapes;
// this avoids waiting for a multi-stage worker when the ground target is known.
internal static class AlexandriaVoltburstPattern
{
    internal static Vector2? Choose(double now, Vector2 player, AlexandriaAmalgamPlanner.Effect[] effects,
        AlexandriaGeometry.Hazard[] hazards)
    {
        if (!double.IsFinite(now) || !AlexandriaGeometry.Finite(player) || effects == null || hazards == null) return null;
        var threatening = Array.FindAll(effects, e => e.Action == 36336 && e.Latest >= 0 &&
            Vector2.Distance(player, e.Origin) <= 6.5f);
        if (threatening.Length == 0) return null;
        Array.Sort(threatening, (a, b) => a.Earliest.CompareTo(b.Earliest));
        var next = threatening[0]; double impact = now + Math.Max(0, next.Earliest);
        Vector2? best = null; float bestDistance = float.PositiveInfinity;
        foreach (double radius in new[] { 7.25, 9.0, 12.0 })
        for (int i = 0; i < 24; ++i)
        {
            double angle = Math.PI * i / 12;
            var point = next.Origin + new Vector2((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
            float distance = Vector2.DistanceSquared(player, point);
            if (distance >= bestDistance || Math.Abs(point.X + 533) >= 19.5 || Math.Abs(point.Y + 373) >= 19.5) continue;
            bool safe = true;
            foreach (var h in hazards)
                if (h.Earliest <= impact && impact <= h.Latest && AlexandriaGeometry.Distance(point, h) <= h.Margin)
                { safe = false; break; }
            if (!safe) continue;
            double arrival = now + .101 + Math.Sqrt(distance) / 5;
            if (!AlexandriaGeometry.TravelSafe(player, point, new(now, arrival, .1, 5, 8), hazards)) continue;
            best = point; bestDistance = distance;
        }
        return best;
    }
}
