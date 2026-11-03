using System;
using System.Linq;
using System.Numerics;
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
    private readonly AlexandriaAmalgamWorker amalgamWorker = new();
    private AlexandriaGridPlanner.Step[] amalgamRoute = [];
    private double amalgamRouteStart, amalgamRetryAfter, amalgamFallbackReportAfter;
    private int amalgamStep;
    private uint amalgamStackTrust;
    private static bool amalgamAuthoredActive, amalgamPulseHandled, amalgamPulseMoving;
    private bool amalgamFallback;

    private void ResetAmalgamRoute()
    {
        amalgamWorker.Reset();
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
            Logger.Information($"[Alexandria] Amalgam route ready: {amalgamRoute.Length} steps, effects={effects.Length}");
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
