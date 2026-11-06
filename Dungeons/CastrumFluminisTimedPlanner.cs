using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Experimental pure search. It issues no movement: the caller must supply
// verified windows and independently validate execution before any live use.
internal static class CastrumFluminisTimedPlanner
{
    internal readonly record struct Step(Vector2 Position, double Time);
    // A positive-position requirement is an allowed region throughout a time
    // window, not an unsafe avoid. This expresses a stack or staging deadline
    // without letting it compete with the hazard owner for movement input.
    internal readonly record struct RequiredCircle(Vector2 Center, float Radius, double Start, double End);
    // Signed half-plane permits a moon-side requirement to coexist with stacks
    // and hazards in the same search; Normal need not be unit length.
    internal readonly record struct RequiredHalfPlane(Vector2 Origin, Vector2 Normal, float Minimum,
        double Start, double End);
    private readonly record struct Cell(int X, int Z);
    private readonly record struct Arrival(Cell Parent, double Clearance);
    private const double Tick = 0.25;
    private const int MaxExpansions = 250000;

    internal readonly record struct FanTell(Vector2 Position, double Impact);

    // A late observer can see two adjacent fans and still have missed the
    // beginning of the sequence. Anchor the first tell to the center and check
    // every observed deadline before extrapolating the remaining casts.
    internal static IReadOnlyList<CastrumFluminisTimedGeometry.Circle> ForecastAnchoredFans(
        Vector2 arena, IReadOnlyList<FanTell> tells, double centerImpact,
        double spacing, double timingTolerance, double early, double late, float dangerRadius)
    {
        if (tells == null || tells.Count < 2 || tells.Count > 8 ||
            !double.IsFinite(centerImpact) || !double.IsFinite(spacing) || spacing <= 0 ||
            !double.IsFinite(timingTolerance) || timingTolerance < 0 || timingTolerance >= spacing / 2)
            return null;
        var positions = new Vector2[tells.Count];
        for (int i = 0; i < tells.Count; i++)
        {
            double expected = centerImpact + (i + 1) * spacing;
            if (!double.IsFinite(tells[i].Impact) || !double.IsFinite(expected) ||
                Math.Abs(tells[i].Impact - expected) > timingTolerance) return null;
            positions[i] = tells[i].Position;
        }
        return ForecastFans(arena, positions, tells[0].Impact, spacing, early, late, dangerRadius);
    }

    // Keep the original conservative forecast and add every actual cast window.
    // Contradictory or missed tells reject the entire refresh; the caller must
    // also validate the remaining route before retaining movement ownership.
    internal static CastrumFluminisTimedGeometry.Circle[] ReconcileFanTells(
        Vector2 arena, IReadOnlyList<FanTell> tells, double centerImpact,
        IReadOnlyList<CastrumFluminisTimedGeometry.Circle> previous)
    {
        if (previous == null || ForecastAnchoredFans(arena, tells, centerImpact,
                0.5, 0.2, 0.15, 1.1, 10.5f) == null) return null;
        return previous.Concat(tells.Select(t => new CastrumFluminisTimedGeometry.Circle(
            t.Position, 10.5f, t.Impact - 0.15, t.Impact + 1.1))).ToArray();
    }

    // Only adjacent, ordered cast tells establish a rotation. Never use the
    // common observed direction when actors are missing or disagree. The caller
    // supplies impact uncertainty; log-delivery time is not a server snapshot.
    internal static IReadOnlyList<CastrumFluminisTimedGeometry.Circle> ForecastFans(
        Vector2 arena, IReadOnlyList<Vector2> observed, double firstImpact,
        double spacing, double early, double late, float dangerRadius)
    {
        if (!float.IsFinite(arena.X) || !float.IsFinite(arena.Y) || observed == null ||
            observed.Count < 2 || observed.Count > 8 || !double.IsFinite(firstImpact) ||
            !double.IsFinite(spacing) || spacing <= 0 || !double.IsFinite(early) || early < 0 ||
            !double.IsFinite(late) || late < 0 || !float.IsFinite(dangerRadius) || dangerRadius <= 0)
            return null;
        double firstAngle = 0;
        int direction = 0;
        for (int i = 0; i < observed.Count; i++)
        {
            Vector2 offset = observed[i] - arena;
            float radius = offset.Length();
            if (!float.IsFinite(radius) || radius < 15.5f || radius > 16.5f) return null;
            double angle = Math.Atan2(offset.Y, offset.X);
            if (i == 0) { firstAngle = angle; continue; }
            double difference = Math.IEEERemainder(angle - firstAngle, 2 * Math.PI);
            if (i == 1)
            {
                if (Math.Abs(difference - Math.PI / 4) < 0.05) direction = 1;
                else if (Math.Abs(difference + Math.PI / 4) < 0.05) direction = -1;
                else return null;
            }
            double expected = firstAngle + direction * i * Math.PI / 4;
            if (Math.Abs(Math.IEEERemainder(angle - expected, 2 * Math.PI)) >= 0.05) return null;
        }
        var result = new CastrumFluminisTimedGeometry.Circle[8];
        for (int i = 0; i < result.Length; i++)
        {
            double angle = firstAngle + direction * i * Math.PI / 4;
            // Retain exact observed centers. For predicted actors, cover the
            // accepted radial (0.5y) and angular (0.05rad) tell tolerances so a
            // slightly displaced helper cannot fall outside the forecast.
            var center = i < observed.Count ? observed[i] :
                arena + 16 * new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle));
            double impact = firstImpact + i * spacing;
            if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !double.IsFinite(impact - early) ||
                !double.IsFinite(impact + late)) return null;
            result[i] = new CastrumFluminisTimedGeometry.Circle(center,
                dangerRadius + (i < observed.Count ? 0 : 1.35f), impact - early, impact + late);
        }
        return result;
    }

    // Execution must not trust a stale or malformed route merely because search
    // returned it. Validate speed, elapsed times and all timed unsafe regions
    // against the current immutable snapshot before submitting any waypoint.
    internal static bool ValidateHazardRoute(IReadOnlyList<Step> route, Vector2 expectedStart,
        Vector2 arena, float arenaRadius, float speed,
        IReadOnlyList<CastrumFluminisTimedGeometry.Circle> circles,
        IReadOnlyList<CastrumFluminisTimedGeometry.Cone> cones,
        IReadOnlyList<CastrumFluminisTimedGeometry.Donut> donuts = null)
    {
        if (route == null || route.Count < 2 || circles == null || !float.IsFinite(speed) || speed <= 0 ||
            Vector2.DistanceSquared(route[0].Position, expectedStart) > 0.000001f ||
            !double.IsFinite(route[0].Time) || route[0].Time < 0) return false;
        if (route[0].Time > 0)
        {
            if (!CastrumFluminisTimedGeometry.SegmentSafe(expectedStart, expectedStart, 0, route[0].Time,
                circles, arena, arenaRadius)) return false;
            if (cones != null)
                foreach (var cone in cones)
                    if (!CastrumFluminisTimedGeometry.ConeSegmentSafe(expectedStart, expectedStart, 0, route[0].Time, cone))
                        return false;
            if (donuts != null)
                foreach (var donut in donuts)
                    if (!CastrumFluminisTimedGeometry.DonutSegmentSafe(expectedStart, expectedStart, 0, route[0].Time, donut))
                        return false;
        }
        for (int i = 1; i < route.Count; i++)
        {
            Step before = route[i - 1], after = route[i];
            double elapsed = after.Time - before.Time;
            if (!double.IsFinite(elapsed) || elapsed <= 0 ||
                Vector2.Distance(before.Position, after.Position) > speed * elapsed + 0.0001 ||
                !CastrumFluminisTimedGeometry.SegmentSafe(before.Position, after.Position,
                    before.Time, after.Time, circles, arena, arenaRadius)) return false;
            if (cones != null)
                foreach (var cone in cones)
                    if (!CastrumFluminisTimedGeometry.ConeSegmentSafe(before.Position, after.Position,
                        before.Time, after.Time, cone)) return false;
            if (donuts != null)
                foreach (var donut in donuts)
                    if (!CastrumFluminisTimedGeometry.DonutSegmentSafe(before.Position, after.Position,
                        before.Time, after.Time, donut)) return false;
        }
        return true;
    }

    // Check positive requirements independently of the search result, including
    // the initial delay. Convex regions need only their time-clipped endpoints;
    // windows outside the route horizon cannot be claimed as satisfied.
    internal static bool ValidatePositionRoute(IReadOnlyList<Step> route,
        IReadOnlyList<RequiredCircle> circles, IReadOnlyList<RequiredHalfPlane> planes)
    {
        if (route == null || route.Count < 2 || !double.IsFinite(route[0].Time) || route[0].Time < 0 ||
            !double.IsFinite(route[^1].Time)) return false;
        bool Segment(Vector2 a, Vector2 b, double from, double to)
        {
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y) ||
                !double.IsFinite(from) || !double.IsFinite(to) || to <= from) return false;
            if (circles != null) foreach (var c in circles)
            {
                if (!float.IsFinite(c.Center.X) || !float.IsFinite(c.Center.Y) || !float.IsFinite(c.Radius) ||
                    c.Radius <= 0 || !double.IsFinite(c.Start) || !double.IsFinite(c.End) || c.End < c.Start ||
                    c.Start < 0 || c.End > route[^1].Time) return false;
                double lo = Math.Max(0, (c.Start - from) / (to - from));
                double hi = Math.Min(1, (c.End - from) / (to - from));
                if (lo <= hi && (Vector2.Distance(Vector2.Lerp(a, b, (float)lo), c.Center) > c.Radius ||
                    Vector2.Distance(Vector2.Lerp(a, b, (float)hi), c.Center) > c.Radius)) return false;
            }
            if (planes != null) foreach (var p in planes)
            {
                float length = p.Normal.Length();
                if (!float.IsFinite(p.Origin.X) || !float.IsFinite(p.Origin.Y) || !float.IsFinite(length) ||
                    length < 0.001f || !float.IsFinite(p.Minimum) || !double.IsFinite(p.Start) ||
                    !double.IsFinite(p.End) || p.End < p.Start || p.Start < 0 || p.End > route[^1].Time) return false;
                double lo = Math.Max(0, (p.Start - from) / (to - from));
                double hi = Math.Min(1, (p.End - from) / (to - from));
                if (lo <= hi && (Vector2.Dot(Vector2.Lerp(a, b, (float)lo) - p.Origin, p.Normal) < p.Minimum * length ||
                    Vector2.Dot(Vector2.Lerp(a, b, (float)hi) - p.Origin, p.Normal) < p.Minimum * length)) return false;
            }
            return true;
        }
        if (route[0].Time > 0 && !Segment(route[0].Position, route[0].Position, 0, route[0].Time)) return false;
        for (int i = 1; i < route.Count; i++)
            if (!Segment(route[i - 1].Position, route[i].Position, route[i - 1].Time, route[i].Time)) return false;
        return true;
    }

    // A worker can finish after the actor has coasted away from its snapshot.
    // Rebase only the initial wait, and accept it only if the complete remaining
    // path is reachable and safe from the newly observed position.
    internal static IReadOnlyList<Step> RebaseInitialWait(IReadOnlyList<Step> route, double elapsed,
        Vector2 actual, Vector2 arena, float arenaRadius, float speed,
        IReadOnlyList<CastrumFluminisTimedGeometry.Circle> hazards,
        IReadOnlyList<CastrumFluminisTimedGeometry.Cone> cones)
    {
        if (route == null || route.Count < 2 || !double.IsFinite(elapsed) || elapsed < 0 ||
            elapsed >= route[0].Time || Vector2.DistanceSquared(actual, route[0].Position) > 0.25f) return null;
        var rebased = route.ToArray();
        rebased[0] = new Step(actual, route[0].Time);
        return ValidateRemainingRoute(rebased, elapsed, actual, arena, arenaRadius, speed, hazards, cones, null, null)
            ? rebased : null;
    }

    // Validate only the unelapsed portion against a fresh snapshot. Times use
    // the same origin as the leased route; expired requirements are harmless,
    // but an uncovered future requirement cannot be declared satisfied.
    internal static bool ValidateRemainingRoute(IReadOnlyList<Step> route, double now, Vector2 actual,
        Vector2 arena, float arenaRadius, float speed,
        IReadOnlyList<CastrumFluminisTimedGeometry.Circle> hazards,
        IReadOnlyList<CastrumFluminisTimedGeometry.Cone> cones,
        IReadOnlyList<RequiredCircle> circles, IReadOnlyList<RequiredHalfPlane> planes,
        IReadOnlyList<CastrumFluminisTimedGeometry.Donut> donuts = null)
    {
        if (route == null || route.Count < 2 || hazards == null || !double.IsFinite(now) || now < 0 ||
            !float.IsFinite(actual.X) || !float.IsFinite(actual.Y) || !float.IsFinite(speed) || speed <= 0 ||
            !double.IsFinite(route[^1].Time) || now >= route[^1].Time) return false;
        double horizon = route[^1].Time;
        if (hazards.Any(h => !double.IsFinite(h.End) || h.End > horizon) ||
            cones != null && cones.Any(c => !double.IsFinite(c.End) || c.End > horizon) ||
            donuts != null && donuts.Any(d => !double.IsFinite(d.End) || d.End > horizon) ||
            circles != null && circles.Any(c => !double.IsFinite(c.Start) || !double.IsFinite(c.End) ||
                c.Start < 0 || c.End < c.Start || c.End > horizon || !float.IsFinite(c.Center.X) ||
                !float.IsFinite(c.Center.Y) || !float.IsFinite(c.Radius) || c.Radius <= 0) ||
            planes != null && planes.Any(p => !double.IsFinite(p.Start) || !double.IsFinite(p.End) ||
                p.Start < 0 || p.End < p.Start || p.End > horizon || !float.IsFinite(p.Origin.X) ||
                !float.IsFinite(p.Origin.Y) || !float.IsFinite(p.Normal.Length()) ||
                p.Normal.Length() < 0.001f || !float.IsFinite(p.Minimum))) return false;
        var remaining = new List<Step> { new(actual, 0) };
        foreach (var step in route)
            if (step.Time > now) remaining.Add(new Step(step.Position, step.Time - now));
        if (remaining.Count < 2) return false;
        var shiftedHazards = hazards.Select(h => h with { Start = h.Start - now, End = h.End - now }).ToArray();
        var shiftedCones = cones?.Select(c => c with { Start = c.Start - now, End = c.End - now }).ToArray();
        var shiftedDonuts = donuts?.Select(d => d with { Start = d.Start - now, End = d.End - now }).ToArray();
        var shiftedCircles = circles?.Where(c => c.End >= now).Select(c => c with
        {
            Start = Math.Max(0, c.Start - now), End = c.End - now
        }).ToArray();
        var shiftedPlanes = planes?.Where(p => p.End >= now).Select(p => p with
        {
            Start = Math.Max(0, p.Start - now), End = p.End - now
        }).ToArray();
        return ValidateHazardRoute(remaining, actual, arena, arenaRadius, speed, shiftedHazards, shiftedCones, shiftedDonuts) &&
            ValidatePositionRoute(remaining, shiftedCircles, shiftedPlanes);
    }

    // A line stack occupies a finite rectangle between the origin and marked
    // player, not an infinite ray. Callers choose an evidence-backed inner width
    // and end margins; this pure helper does not predict party movement.
    internal static IReadOnlyList<RequiredHalfPlane> LineStackRegion(Vector2 origin, Vector2 target,
        float halfWidth, float endpointMargin, double start, double end)
    {
        Vector2 delta = target - origin;
        float length = delta.Length();
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(target.X) ||
            !float.IsFinite(target.Y) || !float.IsFinite(length) || length <= 0 ||
            !float.IsFinite(halfWidth) || halfWidth <= 0 || !float.IsFinite(endpointMargin) ||
            endpointMargin < 0 || 2 * endpointMargin >= length || !double.IsFinite(start) ||
            !double.IsFinite(end) || end < start) return null;
        Vector2 forward = delta / length;
        Vector2 side = new(-forward.Y, forward.X);
        return new[]
        {
            new RequiredHalfPlane(origin, forward, endpointMargin, start, end),
            new RequiredHalfPlane(target, -forward, endpointMargin, start, end),
            new RequiredHalfPlane(origin, side, -halfWidth, start, end),
            new RequiredHalfPlane(origin, -side, -halfWidth, start, end)
        };
    }

    internal static IReadOnlyList<Step> Search(Vector2 start, Vector2 arena, float arenaRadius,
        float speed, double startupDelay, int ticks, IReadOnlyList<CastrumFluminisTimedGeometry.Circle> hazards,
        IReadOnlyList<CastrumFluminisTimedGeometry.Cone> cones = null,
        IReadOnlyList<RequiredCircle> requirements = null,
        IReadOnlyList<RequiredHalfPlane> halfPlanes = null,
        IReadOnlyList<CastrumFluminisTimedGeometry.Donut> donuts = null)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(arena.X) ||
            !float.IsFinite(arena.Y) || !float.IsFinite(arenaRadius) || arenaRadius <= 0 ||
            !float.IsFinite(speed) || speed <= 0 || !double.IsFinite(startupDelay) || startupDelay < 0 ||
            ticks < 1 || ticks > 80 || startupDelay >= ticks * Tick || hazards == null) return null;
        // Search must cover the entire declared damage window. Returning a
        // horizon-ending route while a known hazard remains active would turn
        // an incomplete plan into an apparent success.
        foreach (var hazard in hazards)
            if (!double.IsFinite(hazard.End) || hazard.End > ticks * Tick) return null;
        if (cones != null)
            foreach (var cone in cones)
                if (!double.IsFinite(cone.End) || cone.End > ticks * Tick) return null;
        if (donuts != null)
            foreach (var donut in donuts)
                if (!double.IsFinite(donut.End) || donut.End > ticks * Tick) return null;
        // A partial horizon cannot establish that a later required position was
        // satisfied. Reject it rather than returning a misleading complete route.
        if (requirements != null)
            foreach (var requirement in requirements)
                if (!double.IsFinite(requirement.End) || requirement.End > ticks * Tick) return null;
        if (halfPlanes != null)
            foreach (var requirement in halfPlanes)
                if (!double.IsFinite(requirement.End) || requirement.End > ticks * Tick) return null;
        bool Safe(Vector2 a, Vector2 b, double from, double to, out double margin)
        {
            if (!CastrumFluminisTimedGeometry.SegmentSafe(a, b, from, to, hazards, arena, arenaRadius, out margin)) return false;
            if (cones != null)
                foreach (var cone in cones)
                    if (!CastrumFluminisTimedGeometry.ConeSegmentSafe(a, b, from, to, cone)) return false;
            if (donuts != null)
                foreach (var donut in donuts)
                    if (!CastrumFluminisTimedGeometry.DonutSegmentSafe(a, b, from, to, donut)) return false;
            if (requirements != null)
                foreach (var requirement in requirements)
                {
                    if (!float.IsFinite(requirement.Center.X) || !float.IsFinite(requirement.Center.Y) ||
                        !float.IsFinite(requirement.Radius) || requirement.Radius <= 0 ||
                        !double.IsFinite(requirement.Start) || !double.IsFinite(requirement.End) ||
                        requirement.End < requirement.Start) return false;
                    double first = Math.Max(0, (requirement.Start - from) / (to - from));
                    double last = Math.Min(1, (requirement.End - from) / (to - from));
                    if (first > last) continue;
                    // A circle is convex, so both clipped endpoints inside prove
                    // the entire linear segment remains within this requirement.
                    if (Vector2.Distance(Vector2.Lerp(a, b, (float)first), requirement.Center) > requirement.Radius ||
                        Vector2.Distance(Vector2.Lerp(a, b, (float)last), requirement.Center) > requirement.Radius) return false;
                }
            if (halfPlanes != null)
                foreach (var requirement in halfPlanes)
                {
                    if (!float.IsFinite(requirement.Origin.X) || !float.IsFinite(requirement.Origin.Y) ||
                        !float.IsFinite(requirement.Normal.X) || !float.IsFinite(requirement.Normal.Y) ||
                        !float.IsFinite(requirement.Normal.LengthSquared()) || requirement.Normal.LengthSquared() < 0.000001f ||
                        !float.IsFinite(requirement.Minimum) ||
                        !double.IsFinite(requirement.Start) || !double.IsFinite(requirement.End) ||
                        requirement.End < requirement.Start) return false;
                    double first = Math.Max(0, (requirement.Start - from) / (to - from));
                    double last = Math.Min(1, (requirement.End - from) / (to - from));
                    if (first > last) continue;
                    float threshold = requirement.Minimum * requirement.Normal.Length();
                    // Half-planes are convex; clipped endpoint checks cover the
                    // complete segment during the required window.
                    if (Vector2.Dot(Vector2.Lerp(a, b, (float)first) - requirement.Origin, requirement.Normal) < threshold ||
                        Vector2.Dot(Vector2.Lerp(a, b, (float)last) - requirement.Origin, requirement.Normal) < threshold) return false;
                }
            return true;
        }
        int waitTicks = (int)Math.Ceiling(startupDelay / Tick);
        if (waitTicks >= ticks) return null;
        double waitMargin = double.PositiveInfinity;
        for (int t = 0; t < waitTicks; t++)
        {
            if (!Safe(start, start, t * Tick, (t + 1) * Tick, out double margin)) return null;
            waitMargin = Math.Min(waitMargin, margin);
        }

        double begin = waitTicks * Tick;
        var layers = new List<Dictionary<Cell, Arrival>>();
        var first = new Dictionary<Cell, Arrival>();
        int x0 = (int)Math.Floor(start.X), z0 = (int)Math.Floor(start.Y);
        float reach = speed * (float)Tick;
        for (int x = x0 - 2; x <= x0 + 2; x++)
        for (int z = z0 - 2; z <= z0 + 2; z++)
        {
            var point = new Vector2(x, z);
            if (Vector2.Distance(start, point) <= reach &&
                Safe(start, point, begin, begin + Tick, out double margin))
                first[new Cell(x, z)] = new Arrival(default, Math.Min(waitMargin, margin));
        }
        if (first.Count == 0) return null;
        layers.Add(first);
        int expansions = 0;
        for (int tick = waitTicks + 2; tick <= ticks; tick++)
        {
            var next = new Dictionary<Cell, Arrival>();
            foreach (var entry in layers[^1])
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (++expansions > MaxExpansions) return null;
                if (dx * dx + dz * dz > reach * reach) continue;
                var cell = entry.Key;
                var target = new Cell(cell.X + dx, cell.Z + dz);
                if (next.TryGetValue(target, out var previous) && previous.Clearance >= entry.Value.Clearance) continue;
                var from = new Vector2(cell.X, cell.Z);
                var to = new Vector2(target.X, target.Z);
                if (!Safe(from, to, (tick - 1) * Tick, tick * Tick, out double edgeMargin)) continue;
                double margin = Math.Min(entry.Value.Clearance, edgeMargin);
                if (!next.TryGetValue(target, out previous) || margin > previous.Clearance)
                    next[target] = new Arrival(cell, margin);
            }
            if (next.Count == 0) return null;
            layers.Add(next);
        }
        // Maximize the worst circle and arena-boundary margin. Cone clearance
        // is not measured here. Stable ties avoid hash-dependent selection.
        Cell best = default;
        double bestMargin = double.NegativeInfinity, score = double.PositiveInfinity;
        foreach (var entry in layers[^1])
        {
            var cell = entry.Key;
            double distance = Vector2.DistanceSquared(new Vector2(cell.X, cell.Z), arena);
            if (entry.Value.Clearance > bestMargin || entry.Value.Clearance == bestMargin &&
                (distance < score || distance == score && (cell.X < best.X || cell.X == best.X && cell.Z < best.Z)))
                { best = cell; bestMargin = entry.Value.Clearance; score = distance; }
        }
        var route = new Step[layers.Count + 1];
        route[0] = new Step(start, begin);
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            route[i + 1] = new Step(new Vector2(best.X, best.Z), begin + (i + 1) * Tick);
            best = layers[i][best].Parent;
        }
        return route;
    }
}
