using System;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Retains a destination with clearance to prevent repeated escape/arrival
    // transitions at hazard boundaries. Geometry stays independent of live state.
    internal static class ManticoreSafePosition
    {
        internal static Vector2? Choose(Vector2 start, Vector2 center, float radius, Vector2[][] polygons, Vector2? previous, bool requireStraightSegment = true, float stationaryClearance = .75f, float destinationClearance = 1.5f)
        {
            // 1.5 y absorbs the 0.3 y arrival tolerance and pulse displacement.
            // The player may stand still with 0.75 y clearance if no move began;
            // this hysteresis avoids creating movement merely to improve margin.
            // Navigation-graph callers select only the stable destination here;
            // the registered native avoids determine the route around obstacles.
            // Direct waypoint callers must retain the stricter segment check.
            if (previous.HasValue && Safe(previous.Value, center, radius, polygons, 1.5f) && (!requireStraightSegment || ClearSegment(start, previous.Value, center, radius, polygons)))
                return previous;
            // Moving hazards may request a deeper new goal while retaining it
            // down to the normal 1.5 y margin. This avoids following a chasing
            // spinner in sub-yalm start/stop steps; static callers are unchanged.
            if (Safe(start, center, radius, polygons, stationaryClearance))
                return start;
            Vector2? best = null;
            float distance = float.MaxValue;
            for (int x = -18; x <= 18; x++)
                for (int z = -18; z <= 18; z++)
                {
                    var candidate = center + new Vector2(x, z);
                    float d = Vector2.DistanceSquared(start, candidate);
                    if (d >= distance || !Safe(candidate, center, radius, polygons, destinationClearance) || requireStraightSegment && !ClearSegment(start, candidate, center, radius, polygons))
                        continue;
                    distance = d;
                    best = candidate;
                }
            return best;
        }

        // Tablitaur combines a moving spinner with cast hazards. A safe endpoint
        // on the far side of another hazard is insufficient: allow escape from
        // each initial containment, then reject any entry/re-entry along travel.
        private static bool ClearSegment(Vector2 start, Vector2 end, Vector2 center, float radius, Vector2[][] polygons)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(start, end) / .25f));
            foreach (var polygon in polygons)
            {
                var one = new[] { polygon };
                bool escaped = Safe(start, center, radius, one, .1f);
                for (int i = 1; i <= steps; i++)
                {
                    bool safe = Safe(Vector2.Lerp(start, end, (float)i / steps), center, radius, one, .1f);
                    if (escaped && !safe)
                        return false;
                    if (safe)
                        escaped = true;
                }
            }
            return true;
        }

        internal static bool Safe(Vector2 point, Vector2 center, float radius, Vector2[][] polygons, float clearance)
        {
            if (Vector2.DistanceSquared(point, center) > radius * radius)
                return false;
            foreach (var polygon in polygons)
            {
                bool inside = false;
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    var a = polygon[j];
                    var b = polygon[i];
                    var edge = b - a;
                    float t = edge.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(point - a, edge) / edge.LengthSquared(), 0, 1) : 0;
                    if (Vector2.DistanceSquared(point, a + t * edge) < clearance * clearance)
                        return false;
                    if ((a.Y > point.Y) != (b.Y > point.Y) && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                        inside = !inside;
                }
                if (inside)
                    return false;
            }
            return true;
        }
    }
}
