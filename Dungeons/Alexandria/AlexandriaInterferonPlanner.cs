using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

// Pure scalar route search for the first boss. The runtime supplies validated
// effect windows and revalidates each segment under the encounter input owner.
internal static class AlexandriaInterferonPlanner
{
    internal readonly record struct Step(Vector2 Position, double Time);
    private const int Width = 39, Height = 29, Count = Width * Height;
    private const double Tick = .5;
    // A 2026-11-03 command-to-hold capture averaged 7.16y/s, likely with
    // sprint active, so the old 6y/s ceiling could certify arrival too late.
    // Eight remains provisional until boosted movement and input timing are
    // independently bounded; both planning and execution must share it.
    internal const double MinimumSpeed = 5, MaximumSpeed = 8;

    // The route has only negative hazard constraints. If a waypoint is
    // already reached, an earlier departure is valid only when the complete
    // new travel envelope is certified against the current hazard snapshot.
    internal static int AdvanceReachedWaypoints(Vector2 player, Step[] route, int step, double routeStart,
        double now, AlexandriaGeometry.Hazard[] shapes, AlexandriaTemporalPlanner.MovementBoost? movementBoost = null)
    {
        while (step >= 0 && step + 1 < route.Length && Vector2.Distance(player, route[step].Position) <= .15f &&
            AlexandriaTemporalPlanner.TryMovementBounds(now, routeStart + route[step + 1].Time, MinimumSpeed, MaximumSpeed,
                movementBoost, out double minimum, out double maximum) &&
            AlexandriaInterferonGeometry.CurrentWaypointSafe(player, route[step + 1].Position,
                now, routeStart + route[step + 1].Time, .1, minimum, maximum, .15, shapes, out _))
            step++;
        return step;
    }

    internal static Step[] Plan(Vector2 start, AlexandriaInterferonGeometry.Hazard[] hazards, int ticks = 44,
        CancellationToken cancellation = default, Step? requiredStep = null, double preparationDelay = 0)
    {
        if (cancellation.IsCancellationRequested || hazards == null || ticks < 1 || ticks > 60 || !float.IsFinite(start.X) || !float.IsFinite(start.Y) ||
            !double.IsFinite(preparationDelay) || preparationDelay < 0 || !AlexandriaInterferonGeometry.Safe(start, 0, hazards))
            return Array.Empty<Step>();
        if (requiredStep is { } required && (required.Time < Tick || required.Time > ticks * Tick ||
            Math.Abs(required.Time / Tick - Math.Round(required.Time / Tick)) > .0001))
            return Array.Empty<Step>();
        foreach (var h in hazards)
            if (!float.IsFinite(h.Origin.X) || !float.IsFinite(h.Origin.Y) || !double.IsFinite(h.Earliest) ||
                !double.IsFinite(h.Latest) || h.Earliest > h.Latest || h.Latest > ticks * Tick ||
                !Enum.IsDefined(h.Shape) ||
                (h.Shape == AlexandriaInterferonGeometry.Shape.Cone &&
                 (!float.IsFinite(h.ConeCosine) || Math.Abs(h.ConeCosine) > 1 ||
                  !float.IsFinite(h.Direction.X) || !float.IsFinite(h.Direction.Y) || Math.Abs(h.Direction.LengthSquared() - 1) > .01)))
                return Array.Empty<Step>();
        // Anchor the lattice on the observed start, so reserving worker time
        // never requires snapping an arbitrary position to an integer point.
        // Arena checks below still reject any shifted boundary cell outside.
        // Explicit branch-point replay requests use the historical integer grid;
        // ordinary execution has no such constraint and anchors to the player.
        float offsetX = requiredStep.HasValue ? 0 : start.X - MathF.Round(start.X);
        float offsetY = requiredStep.HasValue ? 0 : start.Y - MathF.Round(start.Y);
        Vector2 Point(int cell) => new(833 + cell % Width + offsetX, 809 + cell / Width + offsetY);
        static int Cell(int x, int z) => (z - 809) * Width + x - 833;
        if (!AlexandriaInterferonGeometry.TryConvert(hazards, out var shapes)) return Array.Empty<Step>();
        // Use the same continuous certificate as runtime revalidation. Reuse the
        // detached converted snapshot for all edges to avoid per-edge allocations.
        bool Segment(Vector2 a, Vector2 b, double begin, double end) =>
            AlexandriaInterferonGeometry.TravelSafe(a, b, begin, end, requiredStep.HasValue ? .1 : 0, MinimumSpeed, MaximumSpeed, shapes);
        // At most 2.5y per half-second matches the provisional 5y/s minimum;
        // the separate envelope also accounts for faster arrival.
        var costs = new double[Count];
        Array.Fill(costs, double.PositiveInfinity);
        var parents = new int[(ticks + 1) * Count];
        Array.Fill(parents, -1);
        for (int z = Math.Max(809, (int)Math.Floor(start.Y) - 1); z <= Math.Min(837, (int)Math.Ceiling(start.Y) + 1); ++z)
            for (int x = Math.Max(833, (int)Math.Floor(start.X) - 1); x <= Math.Min(871, (int)Math.Ceiling(start.X) + 1); ++x)
            {
                int cell = Cell(x, z); var p = Point(cell);
                if (requiredStep is { } requiredInitial && requiredInitial.Time == Tick && requiredInitial.Position != p) continue;
                if (Vector2.Distance(start, p) <= 2.5 &&
                    AlexandriaInterferonGeometry.TravelSafe(start, p, 0, Tick, .1 + preparationDelay, MinimumSpeed, MaximumSpeed, shapes))
                { costs[cell] = Vector2.Distance(start, p); parents[Count + cell] = cell; }
            }
        for (int tick = 2; tick <= ticks; ++tick)
        {
            if (cancellation.IsCancellationRequested) return Array.Empty<Step>();
            var next = new double[Count]; Array.Fill(next, double.PositiveInfinity);
            // Equal-distance routes used to defer departure until the last
            // possible steps. Live execution then had no tolerance for a missed
            // tick. Prefer staging in the next effect's safe region early;
            // this changes cost only, never the timed safety constraints.
            double time = tick * Tick;
            double nextEffect = double.PositiveInfinity;
            foreach (var h in hazards)
                if (h.Latest >= time) nextEffect = Math.Min(nextEffect, Math.Max(time, h.Earliest));
            var staging = new double[Count];
            if (double.IsFinite(nextEffect))
                for (int cell = 0; cell < Count; ++cell)
                    staging[cell] = AlexandriaInterferonGeometry.Safe(Point(cell), nextEffect, hazards) ? 0 : .1;
            for (int from = 0; from < Count; ++from)
            {
                if ((from & 63) == 0 && cancellation.IsCancellationRequested) return Array.Empty<Step>();
                if (double.IsPositiveInfinity(costs[from])) continue;
                var a = Point(from);
                for (int dz = -2; dz <= 2; ++dz)
                    for (int dx = -2; dx <= 2; ++dx)
                    {
                        if (dx * dx + dz * dz > 6.25) continue;
                        int x = 833 + from % Width + dx, z = 809 + from / Width + dz;
                        if (x < 833 || x > 871 || z < 809 || z > 837) continue;
                        int to = Cell(x, z); var b = Point(to);
                        if (requiredStep is { } requiredLater && requiredLater.Time == tick * Tick && requiredLater.Position != b) continue;
                        double distance = Math.Sqrt(dx * dx + dz * dz);
                        // A two-yalm edge leaves almost no pulse-latency slack
                        // within half a second at the assumed minimum speed.
                        // Prefer shorter edges when possible, but retain long
                        // edges because captured waves can require them.
                        var candidate = costs[from] + distance + Math.Max(0, distance - 1.5) * 2 + staging[to] + .001 * Vector2.Distance(b, new Vector2(852, 823));
                        if (candidate >= next[to] || !Segment(a, b, (tick - 1) * Tick, tick * Tick)) continue;
                        next[to] = candidate; parents[tick * Count + to] = from;
                    }
            }
            costs = next;
        }
        int best = -1;
        for (int cell = 0; cell < Count; ++cell) if (best < 0 || costs[cell] < costs[best]) best = cell;
        if (best < 0 || double.IsPositiveInfinity(costs[best])) return Array.Empty<Step>();
        var result = new List<Step>();
        for (int tick = ticks, cell = best; tick >= 1; --tick)
        {
            result.Add(new Step(Point(cell), tick * Tick));
            cell = parents[tick * Count + cell];
            if (cell < 0) return Array.Empty<Step>();
        }
        result.Reverse();
        if (requiredStep.HasValue) return result.ToArray();
        // The graph above proposes positions with continuous movement. It may
        // contain many half-second waypoints that would each incur an input
        // handoff if executed separately. Publish only a complete chain of
        // direct segments independently certified with the full 100ms delay;
        // the first segment additionally reserves worker preparation. This
        // retains the same timing/speed assumptions without charging a new
        // start at artificial intermediate lattice points.
        int count = result.Count;
        var previous = new int[count + 1]; Array.Fill(previous, -1);
        var segments = new int[count + 1]; Array.Fill(segments, int.MaxValue); segments[0] = 0;
        for (int end = 1; end <= count; ++end)
        {
            if (cancellation.IsCancellationRequested) return Array.Empty<Step>();
            for (int begin = 0; begin < end; ++begin)
            {
                if (segments[begin] == int.MaxValue || segments[begin] + 1 >= segments[end]) continue;
                var from = begin == 0 ? start : result[begin - 1].Position;
                double fromTime = begin == 0 ? 0 : result[begin - 1].Time;
                double allowance = .1 + (begin == 0 ? preparationDelay : 0);
                if (!AlexandriaInterferonGeometry.TravelSafe(from, result[end - 1].Position, fromTime,
                    result[end - 1].Time, allowance, MinimumSpeed, MaximumSpeed, shapes)) continue;
                previous[end] = begin; segments[end] = segments[begin] + 1;
            }
        }
        if (previous[count] < 0 || cancellation.IsCancellationRequested) return Array.Empty<Step>();
        var certified = new List<Step>();
        for (int i = count; i > 0; i = previous[i]) certified.Add(result[i - 1]);
        certified.Reverse();
        return certified.ToArray();
    }
}
