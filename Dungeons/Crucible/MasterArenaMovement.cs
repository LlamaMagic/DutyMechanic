using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Ice Dragon's local navmesh repeatedly has no start SpanRef. These short
    // encounter-local routes use the observed flat floor instead. Every edge,
    // not just the destination, is checked; an empty route means stop, never
    // delegate an unchecked detour across the bleeding perimeter.
    internal static class MasterArenaMovement
    {
        internal static bool Corridor(Vector2 from, Vector2 to, Func<Vector2, bool> floor,
            ThirdBoardHazard[] hazards, DateTime now, float elapsed = 0)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) * 4));
            var left = new HashSet<ThirdBoardHazard>();
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
                    if (!h.Contains(p))
                    {
                        left.Add(h);
                        continue;
                    }
                    if (!h.Persistent && arrival < h.Until)
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

        internal static Vector2[] Route(Vector2 start, Vector2 goal, Vector2 center,
            Func<Vector2, bool> floor, ThirdBoardHazard[] hazards, DateTime now)
            => RouteToAny(start, new[] { goal }, center, floor, hazards, now);

        // Search the reachable component once, then apply destination preference.
        // The former 24-goal cutoff repeatedly missed Ice Dragon's other pocket;
        // removing the cutoff must not rebuild the entire graph for each goal.
        internal static Vector2[] RouteToAny(Vector2 start, IEnumerable<Vector2> goals, Vector2 center,
            Func<Vector2, bool> floor, ThirdBoardHazard[] hazards, DateTime now)
        {
            if (!floor(start))
            {
                return Array.Empty<Vector2>();
            }

            var options = goals.Where(p => floor(p) && hazards.All(h => !h.Contains(p))).Distinct().ToArray();
            foreach (var goal in options)
            {
                if (Corridor(start, goal, floor, hazards, now))
                {
                    return new[] { goal };
                }
            }

            if (options.Length == 0)
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
                var entry = parents.Keys.Where(p => Vector2.Distance(p, goal) <= 2 &&
                    Corridor(p, goal, floor, hazards, now, distance[p] / 6))
                    .OrderBy(p => distance[p]).Select(p => (Vector2?)p).FirstOrDefault();
                if (!entry.HasValue)
                {
                    continue;
                }

                var result = new List<Vector2> { goal };
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
