using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Backward viability over a finite, encounter-supplied space-time graph. A locally
/// safe shelter is useful only if it has a certified route to every later stage.
/// This component owns no input and does not infer mechanics or sampling accuracy.
/// </summary>
internal static class AlexandriaTemporalPlanner
{
    // Each stage supplies candidate positions and the positive constraints known at
    // that instant. Stage zero is the actual actor position, not a choice of
    // starting points. Callers must include arena erosion and stack/shelter
    // requirements; this graph cannot prove its finite candidate set is complete.
    // Every connecting segment must lie in the caller's convex traversable arena;
    // nonconvex floors require separate proof. Timed positive constraints must be
    // supplied for windows that extend between stages.
    internal readonly record struct Stage(double Time, Vector2[] Points, bool[] Allowed);
    // A forced edge pairs pre-impact and landing candidates by index. The
    // caller must independently verify displacement, duration and linear
    // travel before using the resulting route in gameplay.
    internal readonly record struct RadialKnockback(int Edge, Vector2 Origin, double Distance);

    // A temporary speed effect may end anywhere within this observed interval.
    // Use its faster minimum only before the earliest possible expiry, and its
    // faster maximum until the latest possible expiry. A crossing edge gets
    // the slower minimum and faster maximum, covering either speed regime.
    internal readonly record struct MovementBoost(double EarliestExpiry, double LatestExpiry,
        double MinimumSpeed, double MaximumSpeed);

    internal static bool TryMovementBounds(double start, double end, double minimumSpeed, double maximumSpeed,
        MovementBoost? boost, out double minimum, out double maximum)
    {
        minimum = minimumSpeed; maximum = maximumSpeed;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start ||
            !double.IsFinite(minimumSpeed) || minimumSpeed <= 0 ||
            !double.IsFinite(maximumSpeed) || maximumSpeed < minimumSpeed) return false;
        if (boost is not { } b) return true;
        if (!double.IsFinite(b.EarliestExpiry) || !double.IsFinite(b.LatestExpiry) ||
            b.EarliestExpiry > b.LatestExpiry || !double.IsFinite(b.MinimumSpeed) ||
            !double.IsFinite(b.MaximumSpeed) || b.MinimumSpeed < minimumSpeed ||
            b.MaximumSpeed < Math.Max(maximumSpeed, b.MinimumSpeed)) return false;
        if (end <= b.EarliestExpiry) minimum = b.MinimumSpeed;
        if (start <= b.LatestExpiry) maximum = b.MaximumSpeed;
        return true;
    }

    /// <summary>
    /// Returns one candidate index per stage, or an empty array when no certified
    /// route exists. Every edge is checked continuously over its effect windows;
    /// observed speed, departure delay and hazard timing must be supplied by callers.
    /// </summary>
    internal static int[] Plan(Stage[] stages, AlexandriaGeometry.Hazard[] hazards,
        double delay, double minimumSpeed, double maximumSpeed,
        AlexandriaPositionConstraints.Timed[] requirements = null, CancellationToken cancellation = default,
        RadialKnockback? knockback = null, MovementBoost? movementBoost = null)
    {
        requirements ??= Array.Empty<AlexandriaPositionConstraints.Timed>();
        if (cancellation.IsCancellationRequested || stages == null || stages.Length == 0 || hazards == null ||
            !double.IsFinite(delay) || delay < 0 || !double.IsFinite(minimumSpeed) ||
            minimumSpeed <= 0 || !double.IsFinite(maximumSpeed) || maximumSpeed < minimumSpeed)
            return Array.Empty<int>();
        for (int i = 0; i < stages.Length; ++i)
        {
            var s = stages[i];
            if (!double.IsFinite(s.Time) || (i > 0 && s.Time <= stages[i - 1].Time) ||
                s.Points == null || s.Allowed == null || s.Points.Length == 0 || s.Points.Length != s.Allowed.Length)
                return Array.Empty<int>();
            foreach (var p in s.Points)
                if (!AlexandriaGeometry.Finite(p)) return Array.Empty<int>();
        }
        if (stages[0].Points.Length != 1) return Array.Empty<int>();
        if (knockback is { } forced && (forced.Edge < 0 || forced.Edge >= stages.Length - 1 ||
            !AlexandriaGeometry.Finite(forced.Origin) || !double.IsFinite(forced.Distance) || forced.Distance <= 0 ||
            stages[forced.Edge].Points.Length != stages[forced.Edge + 1].Points.Length)) return Array.Empty<int>();
        foreach (var h in hazards) if (!h.Valid) return Array.Empty<int>();
        if (!TryMovementBounds(stages[0].Time, stages[^1].Time, minimumSpeed, maximumSpeed,
            movementBoost, out _, out _)) return Array.Empty<int>();

        var next = new int[stages.Length][];
        var viable = new bool[stages.Length][];
        for (int i = 0; i < stages.Length; ++i)
        {
            viable[i] = new bool[stages[i].Points.Length];
            next[i] = new int[stages[i].Points.Length];
            Array.Fill(next[i], -1);
        }
        int last = stages.Length - 1;
        // A zero-length edge certifies even a single-stage request against active
        // hazards. Endpoint flags alone cannot establish spatial hazard safety.
        var terminal = new AlexandriaGeometry.Travel(stages[last].Time, stages[last].Time, 0, minimumSpeed, maximumSpeed);
        for (int j = 0; j < viable[last].Length; ++j)
            viable[last][j] = stages[last].Allowed[j] && AlexandriaGeometry.TravelSafe(
                stages[last].Points[j], stages[last].Points[j], terminal, hazards) &&
                AlexandriaPositionConstraints.TravelInside(stages[last].Points[j], stages[last].Points[j], terminal, requirements);

        for (int i = last - 1; i >= 0; --i)
        {
            if (cancellation.IsCancellationRequested) return Array.Empty<int>();
            if (knockback is { } push && push.Edge == i)
            {
                // Captured knockback decelerated over roughly 0.8s. It is not
                // constant-speed walking: while any effect overlaps this edge,
                // conservatively check its entire possible straight trajectory.
                // The caller must validate this trajectory and bound the arena.
                for (int j = 0; j < viable[i].Length; ++j)
                {
                    if ((j & 63) == 0 && cancellation.IsCancellationRequested) return Array.Empty<int>();
                    var start = stages[i].Points[j]; var landing = stages[i + 1].Points[j];
                    if (!stages[i].Allowed[j] || !viable[i + 1][j] ||
                        !AlexandriaPositionConstraints.TryKnockback(start, push.Origin, push.Distance, out var expected) ||
                        Vector2.Distance(expected, landing) > .001f) continue;
                    bool safe = true;
                    foreach (var hazard in hazards)
                        if (!hazard.Valid || (hazard.Earliest <= stages[i + 1].Time && hazard.Latest >= stages[i].Time &&
                            !AlexandriaGeometry.SegmentSafe(start, landing, hazard))) { safe = false; break; }
                    if (!safe) continue;
                    double speed = Vector2.Distance(start, landing) / (stages[i + 1].Time - stages[i].Time);
                    var forcedTravel = new AlexandriaGeometry.Travel(stages[i].Time, stages[i + 1].Time, 0, speed, speed);
                    if (!forcedTravel.Valid || !AlexandriaPositionConstraints.TravelInside(start, landing, forcedTravel, requirements)) continue;
                    foreach (var requirement in requirements)
                        if (requirement.Earliest <= stages[i + 1].Time && requirement.Latest >= stages[i].Time &&
                            !AlexandriaPositionConstraints.TravelInside(start, landing, forcedTravel,
                                [requirement with { Earliest = stages[i].Time, Latest = stages[i + 1].Time }]))
                        { safe = false; break; }
                    if (!safe) continue;
                    viable[i][j] = true; next[i][j] = j;
                }
                continue;
            }
            if (!TryMovementBounds(stages[i].Time, stages[i + 1].Time, minimumSpeed, maximumSpeed,
                movementBoost, out double edgeMinimum, out double edgeMaximum)) return Array.Empty<int>();
            var travel = new AlexandriaGeometry.Travel(stages[i].Time, stages[i + 1].Time, delay, edgeMinimum, edgeMaximum);
            // A cheap necessary reachability bound avoids expensive continuous
            // geometry checks for distant grid points. The exact deadline and
            // entire possible trajectory are still certified below.
            double reach = Math.Max(0, (travel.End - travel.Start - delay) * edgeMinimum);
            if (!double.IsFinite(reach)) return Array.Empty<int>();
            double cellSize = Math.Max(reach, .001);
            // Every reachable point lies in the same bucket or one of its eight
            // neighbors. Restrict keys before conversion to keep malformed
            // coordinates from overflowing arithmetic or aliasing another cell.
            bool Cell(Vector2 p, out (long X, long Y) cell)
            {
                double x = Math.Floor(p.X / cellSize), y = Math.Floor(p.Y / cellSize);
                if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e15 || Math.Abs(y) > 1e15)
                { cell = default; return false; }
                cell = ((long)x, (long)y); return true;
            }
            var buckets = new Dictionary<(long X, long Y), List<int>>();
            for (int k = 0; k < viable[i + 1].Length; ++k)
            {
                if (!viable[i + 1][k]) continue;
                if (!Cell(stages[i + 1].Points[k], out var key)) return Array.Empty<int>();
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                list.Add(k);
            }
            for (int j = 0; j < viable[i].Length; ++j)
            {
                if (cancellation.IsCancellationRequested) return Array.Empty<int>();
                if (!stages[i].Allowed[j]) continue;
                if (!Cell(stages[i].Points[j], out var cell)) return Array.Empty<int>();
                double best = double.PositiveInfinity;
                for (int dx = -1; dx <= 1; ++dx)
                for (int dy = -1; dy <= 1; ++dy)
                {
                    if (!buckets.TryGetValue((cell.X + dx, cell.Y + dy), out var candidates)) continue;
                    foreach (int k in candidates)
                    {
                        double distance = Vector2.Distance(stages[i].Points[j], stages[i + 1].Points[k]);
                        if (distance >= best || distance > reach || !AlexandriaGeometry.TravelSafe(stages[i].Points[j], stages[i + 1].Points[k], travel, hazards) ||
                            !AlexandriaPositionConstraints.TravelInside(stages[i].Points[j], stages[i + 1].Points[k], travel, requirements)) continue;
                        best = distance;
                        next[i][j] = k;
                        viable[i][j] = true;
                    }
                }
            }
        }
        int first = Array.FindIndex(viable[0], v => v);
        // The caller still owns generation checks when publishing a result;
        // cancellation here prevents completed-but-retired work from returning a route.
        if (first < 0 || cancellation.IsCancellationRequested) return Array.Empty<int>();
        var route = new int[stages.Length];
        route[0] = first;
        for (int i = 0; i < last; ++i) route[i + 1] = next[i][route[i]];
        return route;
    }
}
