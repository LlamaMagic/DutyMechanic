using Clio.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Captured train-car collision and hazard calculations used by the pull recovery planner.
/// This class calculates routes only; HellOnRailsMovement owns their execution and cancellation.
/// </summary>
internal static class HellOnRailsGeometry
{
    // Ray is 35 degrees wide, but the stopped visual train is still settling while its
    // damage helper snapshots. Captured damage reached 20.2 degrees from the live target ray.
    // A 24-degree half-angle covers that observed origin/aim discrepancy with room to clear it.
    internal static readonly Vector2[] RayCone = BuildRayCone();

    internal readonly record struct Beam(Vector3 Origin, float Heading, float Length)
    {
        internal bool Contains(Vector3 point)
        {
            float dx = point.X - Origin.X, dz = point.Z - Origin.Z;
            float forward = dx * MathF.Sin(Heading) + dz * MathF.Cos(Heading);
            float sideways = dx * MathF.Cos(Heading) - dz * MathF.Sin(Heading);
            // 0.75y across the row accommodates actor radius and the captured edge hit at Z150.52.
            return forward >= -0.5f && forward <= Length + 0.5f && MathF.Abs(sideways) <= 3.25f;
        }
    }

    /// <summary>
    /// Finds a short walkable route out of Blastpipe and into a turret-safe ground pocket.
    /// Expanded obstacle footprints keep the approach clear of crate corners.
    /// </summary>
    /// <param name="car">Supported recovery car, two or five.</param>
    /// <param name="start">Post-pull player position.</param>
    /// <param name="beams">Observed turret wave, including its effect-latency tail.</param>
    /// <returns>Waypoints on verified floor, or null when no evidenced safe pocket is reachable.</returns>
    internal static List<Vector3> FindPullRoute(int car, Vector3 start, IReadOnlyCollection<Beam> beams)
    {
        if (car is not (2 or 5) || beams.Count == 0)
            return null;
        return FindGroundRoute(car, start,
            point => point.Z >= car * 50 + 47 && beams.All(b => b.Origin.Y >= 4 || !b.Contains(point)), true);
    }

    /// <summary>Routes around crates and platforms to an evidenced safe floor point.</summary>
    /// <param name="car">Car two, four, or five.</param>
    /// <param name="start">Current ground position.</param>
    /// <param name="safe">Predicate for the destination; hazards may be crossed before their snapshot.</param>
    /// <param name="retreatFromFront">Keep post-pull travel away from the earlier Blastpipe impact.</param>
    /// <returns>A collision-checked route, or null when the starting floor or safe pocket is unknown.</returns>
    internal static List<Vector3> FindGroundRoute(int car, Vector3 start, Func<Vector3, bool> safe, bool retreatFromFront = false)
    {
        if (car is not (2 or 4 or 5)) return null;
        float centerZ = car * 50 + 50;
        var frontier = new PriorityQueue<(int X, int Z), float>();
        var cost = new Dictionary<(int X, int Z), float>();
        var previous = new Dictionary<(int X, int Z), (int X, int Z)>();
        Vector3 Point((int X, int Z) cell) => new(cell.X, 0, centerZ + cell.Z);
        // Seed only visible nearby floor cells; never snap through a crate or platform footprint.
        for (int x = 91; x <= 109; x++)
        for (int z = -14; z <= 14; z++)
        {
            var cell = (x, z);
            var point = Point(cell);
            float distance = start.Distance2D(point);
            if (distance > 2 || !ClearSegment(car, start, point)) continue;
            cost[cell] = distance;
            frontier.Enqueue(cell, distance);
        }
        while (frontier.TryDequeue(out var cell, out float travel))
        {
            if (travel > cost[cell] + 0.01f) continue;
            var point = Point(cell);
            if (safe(point))
            {
                var route = new List<Vector3> { point };
                while (previous.TryGetValue(cell, out var parent))
                {
                    cell = parent;
                    route.Add(Point(cell));
                }
                route.Reverse();
                // Keep only corners, but validate every shortcut against the same collision model.
                var simplified = new List<Vector3>();
                var anchor = start;
                for (int i = 0; i < route.Count;)
                {
                    int next = i;
                    while (next + 1 < route.Count && ClearSegment(car, anchor, route[next + 1])) next++;
                    simplified.Add(route[next]);
                    anchor = route[next];
                    i = next + 1;
                }
                return simplified;
            }
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                var next = (X: cell.X + dx, Z: cell.Z + dz);
                var to = Point(next);
                if (!ClearSegment(car, point, to)) continue;
                // Retreat monotonically until outside the front two rows: the front explosion
                // resolves before turrets, so a shorter route towards the boss is not acceptable.
                if (retreatFromFront && point.Z < centerZ - 3 && to.Z < point.Z) continue;
                float candidate = travel + point.Distance2D(to);
                if (cost.TryGetValue(next, out float old) && old <= candidate) continue;
                cost[next] = candidate;
                previous[next] = cell;
                frontier.Enqueue(next, candidate);
            }
        }
        return null;
    }

    /// <summary>Finds a reachable floor substitute for a service waypoint inside an obstacle.</summary>
    /// <param name="car">Car two or five, whose ground obstacles are modeled.</param>
    /// <param name="start">Current ground position.</param>
    /// <param name="destination">Requested point, already bounded to the car.</param>
    /// <param name="safe">Current native hazard filter for candidate endpoints.</param>
    /// <returns>A collision-checked route to the nearest usable floor point, or null.</returns>
    internal static List<Vector3> FindApproachRoute(int car, Vector3 start, Vector3 destination, Func<Vector3, bool> safe)
    {
        var candidates = new List<Vector3>();
        for (int x = 91; x <= 109; x++)
        for (int z = -14; z <= 14; z++)
        {
            var point = new Vector3(x, 0, car * 50 + 50 + z);
            if (Walkable(car, point.X, point.Z) && safe(point)) candidates.Add(point);
        }
        // Every modeled ground region is connected. Trying nearby alternatives also permits
        // an endpoint excluded by a pending native hazard without abandoning floor bounds.
        foreach (var goal in candidates.OrderBy(point => point.Distance2D(destination)).Take(8))
        {
            var route = FindGroundRoute(car, start, point => point.Distance2D(goal) < 0.1f);
            if (route != null) return route;
        }
        return null;
    }

    internal static bool ClearSegment(int car, Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / 0.2f));
        // Live movement can stop inside our extra 0.6y obstacle clearance while remaining
        // outside the physical crate. Permit only an outward prefix of at most 0.8y, checked
        // against the actual footprint; after reaching clearance, never enter it again.
        bool leavingMargin = !Walkable(car, from.X, from.Z);
        if (leavingMargin && !Walkable(car, from.X, from.Z, 0)) return false;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            float x = from.X + (to.X - from.X) * t, z = from.Z + (to.Z - from.Z) * t;
            if (Walkable(car, x, z)) leavingMargin = false;
            else if (!leavingMargin || from.Distance2D(new Vector3(x, from.Y, z)) > 0.8f || !Walkable(car, x, z, 0)) return false;
        }
        return !leavingMargin;
    }

    internal static bool Walkable(int car, float x, float z, float clearance = 0.6f)
    {
        float relativeZ = z - (car * 50 + 50);
        // Express can land at Z+14.4, inside the real floor but outside the route's one-yalm
        // inset. The bounded prefix may recover inward from there; retain a quarter-yalm
        // physical-edge margin even in that mode, never seed a route from a falling actor.
        float edgeInset = clearance > 0 ? 1 : 0.25f;
        if (x < 90 + edgeInset || x > 110 - edgeInset ||
            relativeZ < -15 + edgeInset || relativeZ > 15 - edgeInset) return false;
        bool Box(float cx, float cz, float halfX, float halfZ) =>
            MathF.Abs(x - cx) < halfX && MathF.Abs(relativeZ - cz) < halfZ;
        return car switch
        {
            2 => !Box(102.5f, -2.5f, 2.5f + clearance, 2.5f + clearance)
                && !Box(97.5f, 7.5f, 2.5f + clearance, 2.5f + clearance),
            4 => true,
            // Raised platforms are ground obstacles; their ascent panels are not ordinary floor.
            5 => !Box(92.5f, 10, 2.5f + clearance, 5 + clearance)
                && !Box(107.5f, 0, 2.5f + clearance, 5 + clearance),
            _ => false
        };
    }

    private static Vector2[] BuildRayCone()
    {
        var vertices = new List<Vector2> { new(0, 0) };
        for (int i = 0; i <= 16; i++)
        {
            float angle = (-24f + 48f * i / 16) * MathF.PI / 180;
            vertices.Add(new(50.5f * MathF.Sin(angle), 50.5f * MathF.Cos(angle)));
        }
        return vertices.ToArray();
    }
}
