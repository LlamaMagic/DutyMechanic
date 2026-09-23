using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Toxic Breath lands on the rear rim using the cast's fixed rotation and
    // resolves in six effects. Deduplicate polled packet sequences so multiple
    // targets cannot end it early. The caller bounds missing receipts by timeout.
    internal sealed class BorgnyBreathPrediction
    {
        private readonly HashSet<uint> seen = new();
        internal Vector2 Origin { get; private set; }
        internal Vector2 Forward { get; private set; }
        internal int Pulses { get; private set; }
        internal bool Active { get; private set; }

        internal void Start(Vector2 center, float castRotation, IEnumerable<uint> baseline)
        {
            seen.Clear();
            foreach (uint sequence in baseline)
                if (sequence != 0)
                    seen.Add(sequence);
            Forward = ThirdBoardGeometry.Direction(castRotation);
            // The arena radius is 19.7y; safe player floor stays
            // independently inset at18.75y. Never project from the player inset.
            Origin = center - Forward * 19.7f;
            Pulses = 0;
            Active = true;
        }

        internal void Observe(uint action, uint sequence)
        {
            if (Active && action == 48808 && sequence != 0 && seen.Add(sequence) && ++Pulses >= 6)
                Active = false;
        }

        internal void Reset()
        {
            Active = false;
            Pulses = 0;
            seen.Clear();
        }
    }

    // Native locations may repeat between render updates. Retain the measured
    // one-second cloud lead between100ms samples rather than alternating a
    // swept hazard and a contact disk on every identical position read. Stalls
    // over one second invalidate velocity; never project a stale direction.
    internal sealed class CloudMotionPrediction
    {
        private Vector2 anchor;
        private DateTime sampledAt;
        private Vector2 lead;
        internal Vector2 Observe(Vector2 point, DateTime now)
        {
            double seconds = (now - sampledAt).TotalSeconds;
            if (sampledAt == default || seconds < 0 || seconds > 1)
                lead = Vector2.Zero;
            else if (seconds < .1)
                return lead;
            else
            {
                Vector2 velocity = (point - anchor) / (float)seconds;
                float speed = velocity.Length();
                // Bound interpolation corrections without enlarging the cloud
                // behind its travel direction; ordinary observed motion is2y/s.
                lead = speed > .05f ? velocity / speed * Math.Min(6, speed) : Vector2.Zero;
            }

            anchor = point;
            sampledAt = now;
            return lead;
        }
    }

    // Ice Dragon's local navmesh repeatedly has no start SpanRef. These short
    // encounter-local routes use the observed flat floor instead. Every edge,
    // not just the destination, is checked; an empty route means stop, never
    // delegate an unchecked detour across the bleeding perimeter.
    internal static class MasterArenaMovement
    {
        // Sept22: both Sweeping halves clipped a refuge beside their dividing
        // line. Add0.5y perpendicular clearance beyond the existing91-degree
        // half-angle, rather than0.5 degrees whose protection vanishes nearby.
        // Local +Y is forward; rotating this same shape protects the rear slash.
        internal static Vector2[] SweepingSlash() => ThirdBoardHazard.Cone(61, 91).Select(p => p - new Vector2(0, .5f / MathF.Sin(91 * MathF.PI / 180))).ToArray();
        // Corner landings leave less than a grid cell beyond the buffered slash.
        // Sample around the actual landing, not just arena-aligned whole yalms;
        // callers still validate floor, every hazard and the full travel segment.
        internal static IEnumerable<Vector2> SlashRefuges(Vector2 landing) => Enumerable.Range(-12, 25).SelectMany(x => Enumerable.Range(-12, 25).Select(z => landing + new Vector2(x * .25f, z * .25f)));
        // Knockbacks are positive positioning, not circles to escape. Check the
        // entire forced displacement against persistent hazards (including full
        // Dread orbs); testing only the endpoint allows crossing a poison cloud.
        // Wall-stop is exclusive to Borgny. Use the inset floor as the wall so
        // navigation never deliberately stages on the damaging arena perimeter.
        internal static bool KnockbackSafe(Vector2 start, Vector2 origin, float distance, bool wallStop, Func<Vector2, bool> floor, IEnumerable<ThirdBoardHazard> fields)
        {
            var delta = start - origin;
            if (delta.LengthSquared() < 1 || !floor(start))
                return false;
            var direction = Vector2.Normalize(delta);
            var hazards = fields.ToArray();
            for (float travel = 0; travel <= distance; travel += .25f)
            {
                var point = start + direction * travel;
                if (!floor(point))
                    return wallStop;
                if (hazards.Any(h => h.Contains(point)))
                    return false;
            }

            return true;
        }

        // Preference only: the caller must still check KnockbackSafe before
        // accepting a goal. Separating the cheap endpoint rank from the full
        // quarter-yalm corridor avoids evaluating every push twice during sort.
        internal static bool KnockbackEndsInside(Vector2 start, Vector2 origin, float distance, Func<Vector2, bool> floor)
        {
            var delta = start - origin;
            return delta.LengthSquared() >= 1 && floor(start + Vector2.Normalize(delta) * distance);
        }

        // Gravity (1E9582) grants Levitation only inside its 6y pad. Approach
        // melee up to 5.65y from the pad center; the caller's 0.1y arrival
        // tolerance leaves 0.25y clearance. Stay levitated if melee is out of reach.
        internal static Vector2? LevitationPosition(Vector2 pad, Vector2 boss, float meleeReach, Func<Vector2, bool> safe)
        {
            Vector2 delta = boss - pad;
            float distance = delta.Length();
            if (distance > .001f)
            {
                Vector2 direction = delta / distance;
                float desired = Math.Clamp(distance - Math.Max(0, meleeReach - .2f), 0, 5.65f);
                for (float offset = Math.Min(desired, distance); offset > 0; offset -= .25f)
                {
                    Vector2 point = pad + direction * offset;
                    if (safe(point))
                        return point;
                }
            }

            return safe(pad) ? pad : null;
        }

        // Sept22 cone-edge refuges at906.099,-429.899 and934.046,-408.226
        // still took Breath ticks. Offset the60-degree cone backwards by
        // 1.25/sin(60): both side rays gain1.25y perpendicular clearance,
        // independent of distance. A larger2y inset removes all flank space
        // between the19.5y landing and18.75y safe floor. Extend the far cap too.
        internal static ThirdBoardHazard BreathCone(Vector2 origin, Vector2 forward) => new()
        {
            Action = 48808,
            Origin = origin - Vector2.Normalize(forward) * (1.25f / MathF.Sin(MathF.PI / 3)),
            Heading = MathF.Atan2(forward.X, forward.Y),
            Points = ThirdBoardHazard.Cone(62.5f, 60),
            Persistent = true,
            Until = DateTime.MaxValue
        };
        // Borgny's moving-cloud overlap can temporarily disconnect every full
        // route. A short escape may still improve survival. Never enter another
        // hazard or deepen any occupied one; quarter-yalm samples enforce this
        // along the entire step, with floor bounds remaining unconditional.
        internal static bool ReducingExposure(Vector2 from, Vector2 to, Func<Vector2, bool> floor, ThirdBoardHazard[] hazards)
        {
            var depths = hazards.Select(h => Penetration(h, from)).ToArray();
            float initial = depths.Sum();
            if (initial <= 0 || Vector2.Distance(from, to) > 3.01f)
                return false;
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) * 4));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(from, to, i / (float)steps);
                if (!floor(p))
                    return false;
                for (int j = 0; j < hazards.Length; j++)
                {
                    float next = Penetration(hazards[j], p);
                    if (next > depths[j] + .001f)
                        return false;
                    depths[j] = next;
                }
            }

            return depths.Sum() < initial - .05f;
        }

        internal static Vector2? EscapeStep(Vector2 start, Func<Vector2, bool> floor, ThirdBoardHazard[] hazards)
        {
            return Enumerable.Range(0, 48).SelectMany(i => Enumerable.Range(1, 6).Select(r => start + ThirdBoardGeometry.Direction(i * MathF.PI / 24) * (r * .5f))).Where(p => ReducingExposure(start, p, floor, hazards)).OrderBy(p => hazards.Sum(h => Penetration(h, p))).ThenBy(p => Vector2.Distance(start, p)).Select(p => (Vector2? )p).FirstOrDefault();
        }

        // Distance to the closest polygon edge measures how far an occupied
        // point must move to leave. Rotate into the same local frame as Contains;
        // outside points have zero exposure and cannot be entered by egress.
        private static float Penetration(ThirdBoardHazard hazard, Vector2 point)
        {
            if (!hazard.Contains(point))
                return 0;
            var delta = point - hazard.Origin;
            float c = MathF.Cos(hazard.Heading), s = MathF.Sin(hazard.Heading);
            var local = new Vector2(delta.X * c - delta.Y * s, delta.X * s + delta.Y * c);
            float nearest = float.MaxValue;
            for (int i = 0; i < hazard.Points.Length; i++)
            {
                var a = hazard.Points[i];
                var edge = hazard.Points[(i + 1) % hazard.Points.Length] - a;
                float t = edge.LengthSquared() == 0 ? 0 : Math.Clamp(Vector2.Dot(local - a, edge) / edge.LengthSquared(), 0, 1);
                nearest = Math.Min(nearest, Vector2.Distance(local, a + edge * t));
            }

            return nearest;
        }

        // Rear rim staging follows the guide's wall-behind instruction without
        // choosing a single blocked point. Keep the outer three-yalm band and
        // a rear-facing sector; the caller still checks every route edge.
        internal static bool BreathStaging(Vector2 point, Vector2 center, Vector2 forward, float extent)
        {
            var delta = point - center;
            return delta.Length() >= extent - 3 && delta.Length() <= extent && Vector2.Dot(Vector2.Normalize(delta), forward) <= -.7f;
        }

        // Equal-radius Toxic Vomit fields can be compared by nearest center;
        // subtract their verified6.5y avoid radius for actual edge clearance.
        internal static float PuddleClearance(Vector2 point, Vector2[] puddles) => puddles.Length == 0 ? 0 : puddles.Min(p => Vector2.Distance(point, p) - 6.5f);
        // A relocated Borgny breathes inward from the rim. Initial cast facing
        // is not stable across the backstep, so it cannot choose the side refuge.
        internal static Vector2 BreathForward(Vector2 boss, Vector2 center, float heading) => Vector2.Distance(boss, center) >= 12 ? Vector2.Normalize(center - boss) : ThirdBoardGeometry.Direction(heading);
        internal static bool Corridor(Vector2 from, Vector2 to, Func<Vector2, bool> floor, ThirdBoardHazard[] hazards, DateTime now, float elapsed = 0)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) * 4));
            var left = new HashSet<ThirdBoardHazard>();
            // A complete path must not cross deeper into active Breath merely
            // because its starting point was already inside. Apply the same
            // exposure constraint as the short escape to this encounter's cone.
            var breathDepth = hazards.Where(h => h.Action == 48808 && h.Persistent).ToDictionary(h => h, h => Penetration(h, from));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(from, to, i / (float)steps);
                if (!floor(p))
                {
                    return false;
                }

                // Six yalms/s is normal run speed. Reserve one second before
                // impact for animation/server uncertainty; persistent ice and
                // orbs never acquire a speculative crossing window.
                var arrival = now.AddSeconds(elapsed + Vector2.Distance(from, p) / 6 + 1);
                foreach (var h in hazards)
                {
                    if (breathDepth.TryGetValue(h, out var depth))
                    {
                        var next = Penetration(h, p);
                        if (next > depth + .001f)
                            return false;
                        breathDepth[h] = next;
                    }

                    if (!h.Contains(p))
                    {
                        left.Add(h);
                        continue;
                    }

                    if (!h.Persistent && h.Until != DateTime.MaxValue && arrival < h.Until)
                    {
                        continue;
                    }

                    // Egress may leave an already occupied hazard, but may not
                    // enter another or re-enter the original on the same edge.
                    if (!h.Contains(from) || left.Contains(h))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        internal static Vector2[] Route(Vector2 start, Vector2 goal, Vector2 center, Func<Vector2, bool> floor, ThirdBoardHazard[] hazards, DateTime now) => RouteToAny(start, new[] { goal }, center, floor, hazards, now);
        // Search the reachable component once, then apply destination preference.
        // The former 24-goal cutoff repeatedly missed Ice Dragon's other pocket;
        // removing the cutoff must not rebuild the entire graph for each goal.
        internal static Vector2[] RouteToAny(Vector2 start, IEnumerable<Vector2> goals, Vector2 center, Func<Vector2, bool> floor, ThirdBoardHazard[] hazards, DateTime now)
        {
            if (!floor(start))
            {
                return Array.Empty<Vector2>();
            }

            // Preserve first-reachable goal order without eagerly validating all
            // later goals. Touchdown supplies expensive knockback predicates;
            // a direct route should not pay for thousands of unused candidates.
            // Keep failed direct goals for the unchanged complete graph fallback.
            var options = new List<Vector2>();
            foreach (var goal in goals.Where(p => floor(p) && hazards.All(h => !h.Contains(p))).Distinct())
            {
                if (Corridor(start, goal, floor, hazards, now))
                {
                    return new[]
                    {
                        goal
                    };
                }

                options.Add(goal);
            }

            if (options.Count == 0)
            {
                return Array.Empty<Vector2>();
            }

            var frontier = new Queue<Vector2>();
            var parents = new Dictionary<Vector2, Vector2>();
            var distance = new Dictionary<Vector2, float>();
            // One-yalm eight-neighbour graph is fine enough for the observed
            // cardinal ice pockets; quarter-yalm edge samples prevent corner
            // cutting. Attach the real position, rather than teleporting to grid.
            for (int x = -1; x <= 1; x++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    var p = center + new Vector2(MathF.Round(start.X - center.X) + x, MathF.Round(start.Y - center.Y) + z);
                    if (!Corridor(start, p, floor, hazards, now))
                    {
                        continue;
                    }

                    parents[p] = start;
                    distance[p] = Vector2.Distance(start, p);
                    frontier.Enqueue(p);
                }
            }

            while (frontier.Count > 0)
            {
                var p = frontier.Dequeue();
                for (int x = -1; x <= 1; x++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if (x == 0 && z == 0)
                        {
                            continue;
                        }

                        var next = p + new Vector2(x, z);
                        if (parents.ContainsKey(next) || next == start || !Corridor(p, next, floor, hazards, now, distance[p] / 6))
                        {
                            continue;
                        }

                        parents[next] = p;
                        distance[next] = distance[p] + Vector2.Distance(p, next);
                        frontier.Enqueue(next);
                    }
                }
            }

            foreach (var goal in options)
            {
                var entry = parents.Keys.Where(p => Vector2.Distance(p, goal) <= 2 && Corridor(p, goal, floor, hazards, now, distance[p] / 6)).OrderBy(p => distance[p]).Select(p => (Vector2? )p).FirstOrDefault();
                if (!entry.HasValue)
                {
                    continue;
                }

                var result = new List<Vector2>
                {
                    goal
                };
                var cursor = entry.Value;
                while (cursor != start)
                {
                    result.Add(cursor);
                    cursor = parents[cursor];
                }

                result.Reverse();
                return result.ToArray();
            }

            return Array.Empty<Vector2>();
        }
    }
}
