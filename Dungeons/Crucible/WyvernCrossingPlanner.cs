using System;
using System.Collections.Generic;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Plans the timed Blazing Trail crossing. Future sprite lanes are traversable
    // before their snapshot; persistent pools are not. Treating them alike caused
    // native avoidance to cross active pools in captured runs.
    internal static class WyvernCrossingPlanner
    {
        // 33304's second Trail overlapped large horizontal whirlwinds at about
        // 2 y/s. Predict their translated capsule envelopes, with 0.2 y/s drift
        // allowance and 0.5 y extra for waypoint/pulse timing uncertainty.
        internal sealed class MovingCircle
        {
            internal Vector2 Origin, Velocity;
            internal float Radius;
            // Forward capsule length from actor origin. Circumscribed circles
            // closed valid gaps in the 106808 overlap; preserve the observed
            // footprint with longitudinal uncertainty instead. Zero supports
            // the original circular replay fixtures.
            internal float Length = 0;
        }
        internal static bool MovingSafe(Vector2 point, double seconds, MovingCircle[] moving) =>
            moving == null || Array.TrueForAll(moving, h =>
            {
                var direction = h.Velocity.LengthSquared() > .001f ? Vector2.Normalize(h.Velocity) : Vector2.Zero;
                // Captured 1.8–2.2 y/s variation is longitudinal, not lateral
                // body growth. Include 0.15 s timing error in both directions.
                // Dynamic callers must plan at measured movement speed; an
                // arbitrarily slower estimate is not conservative when hazards
                // move. Preserve 0.5 y lateral clearance throughout this envelope.
                double early = Math.Max(0, (seconds - .15) * .9), late = (seconds + .15) * 1.1;
                var earliest = h.Origin + h.Velocity * (float)early;
                float uncertainty = h.Velocity.Length() * (float)(late - early);
                var origin = earliest;
                var closest = origin + direction * Math.Clamp(Vector2.Dot(point - origin, direction), 0, h.Length + uncertainty);
                return Vector2.Distance(point, closest) >= h.Radius + .5f;
            });

        internal static Vector2[] Plan(Vector2 start, Vector2[] pools,
            Func<Vector2, double, bool> timedSafe, Func<Vector2, bool> goal,
            double deadline, float speed = 5.5f, MovingCircle[] moving = null,
            bool retainLaterArrivals = false)
        {
            // The captured 40x 29.6 floor has a 0.5 y wall inset. Half-yalm cells
            // resolve its narrow pool corridors; quarter-yalm edge samples
            // prevent diagonal corner cutting. Static crossings can budget 5.5 y/s;
            // moving-hazard callers supply measured 6 y/s to preserve timing.
            const int width = 79, height = 57;
            const float radius = 5.5f;
            // A tornado may block the earliest arrival but permit a later one.
            // This opt-in search retains100ms arrival buckets; callers must use
            // a timed schedule, not feed its waiting cycles to a walking mover.
            const double timeBucket = .1;
            const int plane = width * height;
            bool timed = retainLaterArrivals && moving != null && moving.Length != 0;
            var distance = new double[plane * (timed ? Math.Max(1, (int)(Math.Max(0, deadline) / timeBucket) + 1) : 1)];
            var parent = new int[distance.Length];
            Array.Fill(distance, double.PositiveInfinity);
            Array.Fill(parent, -1);
            var queue = new PriorityQueue<int, double>();
            Vector2 Point(int index) => new Vector2(500.5f + index % plane % width * .5f, -14 + index % plane / width * .5f);
            int State(int cell, double seconds) => cell + (timed ? plane * (int)(seconds / timeBucket) : 0);
            bool Segment(Vector2 from, Vector2 to, double elapsed)
            {
                int samples = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) / .25));
                for (int step = 1; step <= samples; step++)
                {
                    var p = Vector2.Lerp(from, to, step / (float)samples);
                    if (p.X < 500.5f || p.X > 539.5f || Math.Abs(p.Y) > 14.3f ||
                        // Preserve a200ms arrival allowance for timed crossings;
                        // measured speed/launch variation must not enter a lane
                        // just as it snapshots. Tornado time remains unshifted.
                        !timedSafe(p, elapsed + Vector2.Distance(from, p) / speed + (timed ? .2 : 0)) ||
                        !MovingSafe(p, elapsed + Vector2.Distance(from, p) / speed, moving))
                        return false;
                    foreach (var pool in pools)
                    {
                        float previous = Vector2.Distance(from, pool), current = Vector2.Distance(p, pool);
                        // A capture may begin just inside the extra safety
                        // margin. Permit only outward escape from that overlap,
                        // never entering another pool or deepening penetration.
                        if (current < radius && (previous >= radius || current < previous + .001f))
                            return false;
                    }
                }
                return true;
            }
            bool AtGoal(Vector2 p, double arrival)
            {
                if (!goal(p) || !Array.TrueForAll(pools, c => Vector2.Distance(p, c) >= radius))
                    return false;
                // Arrival alone is insufficient: a moving tornado may cross a
                // seemingly safe hold before Trail's delayed effect. Deadline
                // includes 0.5 s arrival allowance; add it and the 1.4 s effect fence.
                for (double t = arrival; t <= deadline + 1.9; t += .1)
                    if (!MovingSafe(p, t, moving))
                        return false;
                return MovingSafe(p, deadline + 1.9, moving);
            }
            if (AtGoal(start, 0))
                return new[] { start };
            // Connect the actual position to nearby grid nodes, rather than
            // snapping across a pool boundary or inventing an initial teleport.
            for (int i = 0; i < plane; i++)
            {
                var p = Point(i);
                double seconds = Vector2.Distance(start, p) / speed;
                if (seconds <= .2 && seconds <= deadline && Segment(start, p, 0))
                {
                    int state = State(i, seconds);
                    distance[state] = seconds;
                    queue.Enqueue(state, seconds);
                }
            }
            while (queue.TryDequeue(out int index, out double elapsed))
            {
                if (elapsed > distance[index])
                    continue;
                var from = Point(index);
                if (AtGoal(from, elapsed))
                {
                    var route = new List<Vector2>();
                    for (int at = index; at >= 0; at = parent[at])
                        route.Add(Point(at));
                    route.Reverse();
                    return route.ToArray();
                }
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0)
                            continue;
                        int x = index % plane % width + dx, z = index % plane / width + dz;
                        if (x < 0 || x >= width || z < 0 || z >= height)
                            continue;
                        int cell = z * width + x;
                        var to = Point(cell);
                        double arrival = elapsed + Vector2.Distance(from, to) / speed;
                        if (arrival > deadline)
                            continue;
                        int next = State(cell, arrival);
                        if (arrival >= distance[next] || !Segment(from, to, elapsed))
                            continue;
                        parent[next] = index;
                        distance[next] = arrival;
                        queue.Enqueue(next, arrival);
                    }
            }
            // No route means the caller must keep its existing emergency
            // owner. It does not authorize a direct line through live damage.
            return Array.Empty<Vector2>();
        }
    }
}
