using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Pure route calculation for Recollection's simultaneous cleaves and circles.
// Execution stays in the existing actor movement owner; this class never sends input.
internal static class RecollectionTimedPlanner
{
    internal enum Shape { Circle, Cone, HalfRoom, Petal }
    internal sealed class Hazard
    {
        internal Shape Kind;
        internal Vector2 Origin;
        internal float Heading;
        internal double ResolvesIn;
        internal double ExpiresIn;
    }
    internal readonly record struct Route(Vector2 First, Vector2 Final, double HoldSeconds, bool Partial = false, Vector2? Middle = null);
    // Captures show roughly six yalms/s while running; allow for a speed buff. Evaluate a range and
    // both immediate and delayed input so arriving early cannot enter a later hit.
    private const double Speed = 4.5;
    private static readonly double[] Speeds = [4.5, 6, 7.5, 9.5];
    private const double StartDelay = 0.8;
    private const double EffectLead = 0.35;
    private static readonly double[] HoldDurations = [0, 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5, 5.5, 6];

    internal static bool Contains(Hazard h, Vector2 point, double waypointMargin = 0)
    {
        Vector2 d = point - h.Origin;
        double r = d.Length();
        if (h.Kind == Shape.Circle) return r < 4.5 + waypointMargin;
        double forward = d.X * Math.Sin(h.Heading) + d.Y * Math.Cos(h.Heading);
        if (h.Kind == Shape.HalfRoom)
        {
            double side = d.X * Math.Cos(h.Heading) - d.Y * Math.Sin(h.Heading);
            return forward >= -0.5 - waypointMargin && forward <= 24.5 + waypointMargin && Math.Abs(side) <= 32.5 + waypointMargin;
        }
        double halfArc = (h.Kind == Shape.Petal ? Math.PI / 6 : Math.PI / 3) + 0.04 +
            Math.Asin(Math.Min(1, waypointMargin / Math.Max(r, waypointMargin + 0.001)));
        double range = (h.Kind == Shape.Petal ? 16.5 : 24.5) + waypointMargin;
        return r <= range && (r < 0.5 || forward / r >= Math.Cos(halfArc));
    }

    private static Vector2 CutCorner(Vector2 from, Vector2 target, double tolerance)
    {
        double distance = Vector2.Distance(from, target);
        return distance <= tolerance || distance < 0.001 ? from :
            Vector2.Lerp(from, target, (float)((distance - tolerance) / distance));
    }

    internal static Vector2 Position(Vector2 start, Route route, double time, double startDelay = StartDelay,
        double speed = Speed, double handoffDelay = 0, double firstTolerance = 0,
        double middleTolerance = 0, double finalTolerance = 0)
    {
        double travel = Math.Max(0, time - startDelay) * speed;
        Vector2 first = CutCorner(start, route.First, firstTolerance);
        bool direct = !route.Middle.HasValue && Vector2.DistanceSquared(route.First, route.Final) < 0.0001f;
        Vector2 middle = route.Middle.HasValue ? CutCorner(first, route.Middle.Value, middleTolerance) : first;
        Vector2 final = direct ? first : CutCorner(middle, route.Final, finalTolerance);
        double a = Vector2.Distance(start, first), b = Vector2.Distance(first, middle), c = Vector2.Distance(middle, final);
        if (travel < a && a > 0.001) return Vector2.Lerp(start, first, (float)(travel / a));
        travel = Math.Max(a, travel - (route.HoldSeconds + (b + c > 0.001 ? handoffDelay : 0)) * speed);
        if (travel < a + b && b > 0.001) return Vector2.Lerp(first, middle, (float)((travel - a) / b));
        if (route.Middle.HasValue && c > 0.001) travel = Math.Max(a + b, travel - handoffDelay * speed);
        if (travel < a + b + c && c > 0.001) return Vector2.Lerp(middle, final, (float)((travel - a - b) / c));
        return final;
    }

    internal static bool Safe(Vector2 start, Route route, IReadOnlyList<Hazard> hazards, double startDelay = StartDelay, double maxSpeed = 9.5, double? releaseAfter = null)
    {
        double earliest = double.PositiveInfinity;
        if (route.Partial)
            foreach (var hazard in hazards) earliest = Math.Min(earliest, hazard.ResolvesIn);
        double distance = Vector2.Distance(start, route.First);
        // Revalidation preserves the acquired absolute departure time even when
        // the player is already inside the waypoint arrival tolerance.
        double baselineRelease = releaseAfter ?? startDelay + distance / Speed + route.HoldSeconds;
        for (int speedIndex = 0; speedIndex <= Speeds.Length; speedIndex++)
        for (int delayIndex = 0; delayIndex < (startDelay > 0 ? 5 : 1); delayIndex++)
        for (int handoffIndex = 0; handoffIndex < 2; handoffIndex++)
        for (int toleranceMask = 0; toleranceMask < 8; toleranceMask++)
        {
            double handoff = handoffIndex == 0 ? 0 : 0.2;
            double firstTolerance = (toleranceMask & 1) == 0 ? 0 : 0.35;
            double middleTolerance = (toleranceMask & 2) == 0 ? 0 : 0.35;
            double finalTolerance = (toleranceMask & 4) == 0 ? 0 : 0.35;
            double speed = speedIndex == Speeds.Length ? maxSpeed : Speeds[speedIndex];
            if (speed > maxSpeed || speedIndex == Speeds.Length && Math.Abs(speed - Speed) < 0.001) continue;
            double delay = startDelay * delayIndex / 4;
            // A planned hold releases at one absolute time. Faster movement
            // reaches its unmarked first waypoint sooner and waits longer.
            double hold = !releaseAfter.HasValue && route.HoldSeconds <= 0 ? 0 : Math.Max(0, baselineRelease - delay - Math.Max(0, distance - firstTolerance) / speed);
            var adjusted = route with { HoldSeconds = hold };
            foreach (var h in hazards)
            {
                if (route.Partial && h.ResolvesIn > earliest + 0.6) continue;
                double from = Math.Max(0, h.ResolvesIn - EffectLead);
                double to = Math.Max(from, h.ExpiresIn);
                for (double t = from; t < to; t += 0.2)
                    if (Contains(h, Position(start, adjusted, t, delay, speed, handoff, firstTolerance, middleTolerance, finalTolerance), 0.35)) return false;
                if (Contains(h, Position(start, adjusted, to, delay, speed, handoff, firstTolerance, middleTolerance, finalTolerance), 0.35)) return false;
            }
        }
        return true;
    }

    // An imminent hit may not leave time for the background multi-leg search.
    // This bounded main-thread pass only claims a verified first-stage exit.
    internal static Route? FindEmergency(Vector2 start, Vector2 center, IReadOnlyList<Hazard> hazards,
        Func<Vector2, Vector2, bool> segmentSafe, Func<Vector2, bool> destinationSafe,
        int cursor, out int nextCursor, double maxSpeed = 9.5)
    {
        nextCursor = cursor;
        var timer = Stopwatch.StartNew();
        var imminent = hazards.Where(h => h.ResolvesIn <= 4.5)
            .OrderBy(h => Contains(h, start) ? 0 : 1).ThenBy(h => h.ResolvesIn).ToArray();
        // A half-room cleave frequently requires turning around the central
        // hole. Check its two tangent routes before the broader polar search.
        // Transit points are never held; the final point must be unmarked.
        if (cursor == 0)
        foreach (var cleave in imminent.Where(h => h.Kind == Shape.HalfRoom && Contains(h, start)))
        {
            var safe = new Vector2(-(float)Math.Sin(cleave.Heading), -(float)Math.Cos(cleave.Heading));
            var tangent = new Vector2(-safe.Y, safe.X);
            foreach (float radius in new[] { 6f, 8f, 10f, 12f })
            foreach (float side in new[] { -1f, 1f })
            foreach (double angle in new[] { Math.PI / 6, -Math.PI / 6, 0d, Math.PI / 3, -Math.PI / 3 })
            {
                if (timer.ElapsedMilliseconds >= 8) { nextCursor = -1; return null; }
                var via = center + tangent * (radius * side);
                var rotated = new Vector2(safe.X * (float)Math.Cos(angle) - safe.Y * (float)Math.Sin(angle),
                    safe.X * (float)Math.Sin(angle) + safe.Y * (float)Math.Cos(angle));
                var target = center + rotated * 12;
                var route = new Route(via, target, 0, true);
                if (destinationSafe(target) && Safe(start, route with { Partial = false }, imminent, maxSpeed: maxSpeed) &&
                    segmentSafe(start, via) && segmentSafe(via, target)) { nextCursor = 0; return route; }
            }
        }
        const int directCount = 12 * 24;
        // Zero starts the tangent prepass; positive cursors encode the next polar candidate.
        int directStart = Math.Max(0, cursor - 1);
        for (int n = directStart; n < directCount; n++)
        {
            if (timer.ElapsedMilliseconds >= 8) { nextCursor = n + 1; return null; }
            float distance = n / 24 + 1;
            double angle = (n % 24) * Math.PI / 12;
            var point = start + new Vector2(distance * (float)Math.Sin(angle), distance * (float)Math.Cos(angle));
            var route = new Route(point, point, 0, true);
            if (destinationSafe(point) && Safe(start, route with { Partial = false }, imminent, maxSpeed: maxSpeed) && segmentSafe(start, point))
            { nextCursor = 0; return route; }
        }
        // Search turning paths in bounded slices; a later pulse resumes the
        // next pair so a difficult overlap does not block the bot thread.
        var points = new List<Vector2>();
        for (float radius = 4.75f; radius <= 14.75f; radius += 1.25f)
        for (int i = 0; i < 24; i++)
        {
            double angle = i * Math.PI / 12;
            points.Add(center + new Vector2(radius * (float)Math.Sin(angle), radius * (float)Math.Cos(angle)));
        }
        var vias = points.OrderBy(point => Vector2.DistanceSquared(start, point)).ToArray();
        var targets = points.Where(destinationSafe).OrderBy(point => imminent.Count(h => Contains(h, point))).ToArray();
        if (targets.Length == 0) { nextCursor = 0; return null; }
        int total = vias.Length * targets.Length;
        for (int n = Math.Max(0, cursor - directCount - 1); n < total; n++)
        {
            if (timer.ElapsedMilliseconds >= 25) { nextCursor = directCount + 1 + n; return null; }
            var via = vias[n / targets.Length];
            var target = targets[n % targets.Length];
            var route = new Route(via, target, 0, true);
            if (Safe(start, route with { Partial = false }, imminent, maxSpeed: maxSpeed) &&
                segmentSafe(start, via) && segmentSafe(via, target)) { nextCursor = 0; return route; }
        }
        nextCursor = 0;
        return null;
    }

    internal static Route? Find(Vector2 start, Vector2 center, IReadOnlyList<Hazard> hazards,
        Func<Vector2, Vector2, bool> segmentSafe, Func<Vector2, bool> destinationSafe,
        Func<Route, bool> candidateAllowed = null, double maxSpeed = 9.5)
    {
        if (hazards.Count == 0) return null;
        // The caller runs this pure snapshot search away from the bot thread.
        // Bound impossible layouts and return a first-stage fallback if necessary.
        var timer = Stopwatch.StartNew();
        var points = new List<Vector2>();
        for (float radius = 4.75f; radius <= 14.75f; radius += 1.25f)
        for (int i = 0; i < 48; ++i)
        {
            double angle = i * Math.PI / 24;
            var point = center + new Vector2(radius * (float)Math.Sin(angle), radius * (float)Math.Cos(angle));
            if (Vector2.Distance(start, point) < 22) points.Add(point);
        }
        points.Sort((a,b) => Vector2.DistanceSquared(start,a).CompareTo(Vector2.DistanceSquared(start,b)));
        var first = new List<Vector2>();
        double earliest = hazards.Min(h => h.ResolvesIn);
        var early = hazards.Where(h => h.ResolvesIn <= earliest + 0.6).ToArray();
        foreach (var point in points)
        {
            if (!segmentSafe(start, point)) continue;
            var stay = new Route(point, point, 0);
            if (destinationSafe(point) && Safe(start, stay, hazards, maxSpeed: maxSpeed) && (candidateAllowed?.Invoke(stay) ?? true)) return stay;
            if (Safe(start, stay, early, maxSpeed: maxSpeed)) first.Add(point);
        }
        // The gap between opposing cone waves can require holding until the
        // earlier effect has resolved before moving into its former footprint.
        var targets = points.Where(destinationSafe).OrderBy(point => hazards.Count(h => Contains(h, point)))
            .ThenBy(point => Vector2.DistanceSquared(start, point)).ToArray();
        // Reserve time for turning routes when a direct path is unavailable.
        foreach (var via in first)
        foreach (var target in targets)
        {
            if (timer.ElapsedMilliseconds >= 450) goto TurningRoutes;
            if (!segmentSafe(via, target)) continue;
            foreach (double hold in HoldDurations)
            {
                if (hold > 0 && !destinationSafe(via)) continue;
                var route = new Route(via, target, hold);
                if (Safe(start, route, hazards, maxSpeed: maxSpeed) && (candidateAllowed?.Invoke(route) ?? true)) return route;
            }
        }
        // A circle arena with a lethal center sometimes requires turning while
        // traversing a marked wedge. The middle point is transit-only: the owner
        // must hand off immediately, never hold there.
        TurningRoutes:
        var transit = points.Where(point => !destinationSafe(point)).ToArray();
        double last = hazards.Max(h => h.ResolvesIn);
        var lastHazards = hazards.Where(h => h.ResolvesIn >= last - 0.6).ToArray();
        foreach (var via in first.Where(destinationSafe))
        foreach (var middle in transit)
        {
            if (timer.ElapsedMilliseconds >= 800) goto FirstStageFallback;
            if (lastHazards.Any(h => Contains(h, middle)) || !segmentSafe(via, middle)) continue;
            foreach (var target in targets)
            {
                if (timer.ElapsedMilliseconds >= 800) goto FirstStageFallback;
                if (!segmentSafe(middle, target)) continue;
                foreach (double hold in HoldDurations)
                {
                    var route = new Route(via, target, hold, false, middle);
                    if (Safe(start, route, hazards, maxSpeed: maxSpeed) && (candidateAllowed?.Invoke(route) ?? true)) return route;
                }
            }
        }
        FirstStageFallback:
        // Prefer a first-stage position that is safe now and least exposed to
        // later effects. A partial route must be reconsidered after arrival.
        foreach (var via in first.OrderBy(point => hazards.Count(h =>
                     h.ResolvesIn > earliest + 0.6 && Contains(h, point, 0.35)))
                 .ThenBy(point => Vector2.DistanceSquared(start, point)))
        {
            var partial = new Route(via, via, 0, true);
            if (destinationSafe(via) && (candidateAllowed?.Invoke(partial) ?? true)) return partial;
        }
        return null;
    }
}
