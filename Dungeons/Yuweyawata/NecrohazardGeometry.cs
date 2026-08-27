using Clio.Common;
using Clio.Utilities;
using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Identifies the two authored walkable-floor layouts used by Overseer Kanilokka's Necrohazard.
/// </summary>
internal enum NecrohazardFloorLayout
{
    /// <summary>No confirmed layout is available; movement must use the conservative live fallback.</summary>
    None,

    /// <summary>The <c>0x00800040</c> map state with north, east, south, and west routes.</summary>
    FourRoutes,

    /// <summary>The <c>0x02000100</c> map state with east, north, and west routes.</summary>
    ThreeRoutes,
}

/// <summary>
/// Builds bounded routes through Necrohazard's exact floor polygons without relying on a moving Trust.
/// </summary>
/// <remarks>
/// The floor coordinates are authored encounter geometry. A half-yalm grid plus 0.35-yalm clearance
/// keeps the player center away from removed-floor edges, while the 19.5-yalm outer limit preserves
/// the near-wall destination required by Necrohazard's severe distance falloff. Route generation is
/// intentionally encounter-local and runs only when a newly confirmed layout is selected.
/// </remarks>
internal static class YuweyawataNecrohazardGeometry
{
    private const float CenterX = 116f;
    private const float CenterZ = -66f;
    private const float ArenaRadius = 19.5f;
    private const float CenterIslandRadius = 5f;
    private const float RequiredGoalRadius = 18.5f;
    private const float EdgeClearance = 0.35f;
    private const float GridResolution = 0.5f;
    private const float SegmentSampleSpacing = 0.25f;
    private const float GridMinimumX = CenterX - 20f;
    private const float GridMinimumZ = CenterZ - 20f;
    private const int GridCellCount = 81;

    private static readonly (int X, int Z, float Cost)[] NeighborOffsets =
    [
        (-1, 0, 1f), (1, 0, 1f), (0, -1, 1f), (0, 1, 1f),
        (-1, -1, 1.41421356f), (-1, 1, 1.41421356f),
        (1, -1, 1.41421356f), (1, 1, 1.41421356f),
    ];

    // These polygons are the surviving floor for state 0x00800040. They meet the five-yalm center
    // island and extend to the arena wall; the outer-radius guard below applies the authored donut
    // subtraction that clips any polygon point beyond the normal circular arena.
    private static readonly Vector2[][] FourRoutePolygons =
    [
        [
            new(119.75f, -69.197f), new(119.8f, -72.259f), new(117.464f, -76.648f),
            new(116.16f, -78.381f), new(117.654f, -79.69f), new(121.004f, -81.108f),
            new(123.091f, -84.666f), new(121.806f, -85.139f), new(119.902f, -85.616f),
            new(119.865f, -85.432f), new(117.019f, -83.869f), new(113.634f, -80.926f),
            new(112.872f, -77.57f), new(115.792f, -72.885f), new(113.913f, -70.537f),
        ],
        [
            new(119.843f, -62.908f), new(122.912f, -63.585f), new(126.417f, -65.231f),
            new(127.875f, -67.423f), new(129.415f, -67.738f), new(131.682f, -64.324f),
            new(135.734f, -62.999f), new(135.904f, -64.04f), new(136f, -66f),
            new(133.316f, -66.632f), new(131.885f, -70.919f), new(127.118f, -71.718f),
            new(124.3f, -68.396f), new(122.692f, -67.635f), new(120.364f, -68.278f),
        ],
        [
            new(112.599f, -62.343f), new(112.144f, -60.134f), new(107.746f, -57.919f),
            new(106.949f, -53.585f), new(111.361f, -49.956f), new(110.741f, -46.737f),
            new(112.098f, -46.384f), new(113.836f, -46.127f), new(114.178f, -47.5f),
            new(115.156f, -51.21f), new(111.583f, -53.679f), new(111.439f, -55.538f),
            new(116.423f, -58.691f), new(117.812f, -61.417f),
        ],
        [
            new(112.885f, -69.813f), new(110.681f, -70.201f), new(108.074f, -73.1f),
            new(103.282f, -73.098f), new(100.933f, -70.326f), new(98.686f, -70.669f),
            new(98.201f, -71.399f), new(97.212f, -72.791f), new(96.861f, -71.806f),
            new(96.384f, -69.902f), new(98.13f, -67.76f), new(98.394f, -67.416f),
            new(101.8f, -66.852f), new(104.669f, -69.078f), new(106.579f, -69.753f),
            new(108.694f, -66.477f), new(111.106f, -65.727f),
        ],
    ];

    // State 0x02000100 removes the south route and reshapes the remaining three. Keeping these
    // vertices separate prevents a union of both variants from authorizing movement over missing floor.
    private static readonly Vector2[][] ThreeRoutePolygons =
    [
        [
            new(118.833f, -61.921f), new(119.469f, -61.185f), new(119.413f, -60.144f),
            new(119.473f, -58.867f), new(119.791f, -57.88f), new(120.141f, -57.679f),
            new(122.108f, -56.658f), new(123.843f, -55.884f), new(124.737f, -55.841f),
            new(125.679f, -56.271f), new(126.875f, -56.885f), new(127.948f, -57.751f),
            new(128.788f, -59.071f), new(131.158f, -58.659f), new(131.481f, -57.725f),
            new(131.425f, -55.926f), new(131.531f, -55.377f), new(132.491f, -54.745f),
            new(132.629f, -54.889f), new(133.638f, -56.572f), new(133.955f, -57.21f),
            new(133.725f, -57.423f), new(133.559f, -60.674f), new(131.641f, -62.617f),
            new(129.075f, -62.622f), new(126.341f, -60.472f), new(124.5f, -59.225f),
            new(122.207f, -60.495f), new(122.479f, -61.989f), new(122.001f, -63.285f),
            new(121.616f, -64.139f), new(121.34f, -64.649f), new(120.889f, -65.637f),
        ],
        [
            new(118.832f, -69.987f), new(118.838f, -70.248f), new(118.475f, -72.109f),
            new(117.978f, -72.52f), new(114.363f, -74.935f), new(114.086f, -75.623f),
            new(114.363f, -76.492f), new(114.925f, -76.961f), new(116f, -77.189f),
            new(117.126f, -77.333f), new(118.369f, -77.708f), new(119.344f, -78.285f),
            new(120.082f, -78.855f), new(120.657f, -80.633f), new(120.255f, -83.257f),
            new(119.559f, -83.894f), new(117.427f, -85.064f), new(117.477f, -85.922f),
            new(116f, -86f), new(114.535f, -85.923f), new(114.873f, -84.137f),
            new(116f, -83.292f), new(117.605f, -82.243f), new(117.465f, -80.887f),
            new(117.088f, -80.113f), new(116f, -79.851f), new(114.658f, -79.729f),
            new(113.371f, -79.419f), new(112.084f, -78.91f), new(111.137f, -77.537f),
            new(110.837f, -76.159f), new(110.837f, -74.844f), new(113.357f, -72.382f),
            new(113.314f, -71.026f), new(113.162f, -70.247f), new(112.787f, -69.83f),
        ],
        [
            new(111.116f, -65.833f), new(110.654f, -65.493f), new(107.256f, -65.506f),
            new(105.557f, -65.231f), new(104.614f, -64.362f), new(103.623f, -62.397f),
            new(103.125f, -60.408f), new(103.742f, -59.448f), new(103.925f, -57.932f),
            new(102.756f, -57.151f), new(100.074f, -58.462f), new(98.007f, -57.349f),
            new(98.367f, -56.588f), new(99.369f, -54.89f), new(99.472f, -54.752f),
            new(100.749f, -55.538f), new(103.599f, -53.968f), new(104.63f, -54.63f),
            new(106.026f, -55.591f), new(106.706f, -56.606f), new(106.861f, -58.172f),
            new(106.885f, -58.519f), new(107.005f, -59.99f), new(107.231f, -61.292f),
            new(108.879f, -62.314f), new(110.425f, -62.275f), new(111.901f, -61.901f),
            new(113.179f, -61.779f), new(113.27f, -61.814f),
        ],
    ];

    /// <summary>
    /// Builds a shortest grid route from the current position to the furthest reachable near-wall
    /// point in the selected floor layout.
    /// </summary>
    /// <param name="layout">Confirmed map-effect layout to traverse.</param>
    /// <param name="start">Current player position; only its horizontal coordinates are used.</param>
    /// <param name="route">Simplified, edge-clear waypoints when a near-wall route is available.</param>
    /// <returns><see langword="true"/> when the selected layout contains a reachable point at least 18.5 yalms from center.</returns>
    internal static bool TryBuildRoute(
        NecrohazardFloorLayout layout,
        Vector3 start,
        out Vector3[] route)
    {
        route = [];
        if (layout == NecrohazardFloorLayout.None)
        {
            return false;
        }

        int totalCells = GridCellCount * GridCellCount;
        bool[] walkable = new bool[totalCells];
        for (int z = 0; z < GridCellCount; z++)
        {
            for (int x = 0; x < GridCellCount; x++)
            {
                Vector3 point = GridPoint(x, z, start.Y);
                walkable[GridIndex(x, z)] = IsWalkable(layout, point);
            }
        }

        int startIndex = FindNearestWalkableIndex(walkable, start);
        if (startIndex < 0)
        {
            return false;
        }

        float[] costs = new float[totalCells];
        int[] predecessors = new int[totalCells];
        Array.Fill(costs, float.PositiveInfinity);
        Array.Fill(predecessors, -1);

        PriorityQueue<int, float> frontier = new();
        costs[startIndex] = 0f;
        frontier.Enqueue(startIndex, 0f);
        int bestIndex = startIndex;
        float bestRadius = RadiusFromCenter(GridPoint(startIndex, start.Y));

        while (frontier.TryDequeue(out int currentIndex, out float currentCost))
        {
            if (currentCost > costs[currentIndex] + 0.001f)
            {
                continue;
            }

            int currentX = currentIndex % GridCellCount;
            int currentZ = currentIndex / GridCellCount;
            float radius = RadiusFromCenter(GridPoint(currentIndex, start.Y));
            if (radius > bestRadius + 0.001f ||
                (MathF.Abs(radius - bestRadius) <= 0.001f && currentCost < costs[bestIndex]))
            {
                bestIndex = currentIndex;
                bestRadius = radius;
            }

            foreach ((int offsetX, int offsetZ, float stepCost) in NeighborOffsets)
            {
                int nextX = currentX + offsetX;
                int nextZ = currentZ + offsetZ;
                if (!IsGridCoordinateValid(nextX, nextZ))
                {
                    continue;
                }

                int nextIndex = GridIndex(nextX, nextZ);
                if (!walkable[nextIndex])
                {
                    continue;
                }

                // Diagonal motion may not cut across the corner of either removed cardinal cell.
                if (offsetX != 0 && offsetZ != 0 &&
                    (!walkable[GridIndex(currentX + offsetX, currentZ)] ||
                     !walkable[GridIndex(currentX, currentZ + offsetZ)]))
                {
                    continue;
                }

                float nextCost = currentCost + stepCost;
                if (nextCost + 0.001f >= costs[nextIndex])
                {
                    continue;
                }

                costs[nextIndex] = nextCost;
                predecessors[nextIndex] = currentIndex;
                frontier.Enqueue(nextIndex, nextCost);
            }
        }

        if (bestRadius < RequiredGoalRadius)
        {
            return false;
        }

        List<Vector3> denseRoute = [];
        for (int index = bestIndex; index >= 0; index = predecessors[index])
        {
            denseRoute.Add(GridPoint(index, start.Y));
            if (index == startIndex)
            {
                break;
            }
        }
        denseRoute.Reverse();
        route = SimplifyRoute(layout, denseRoute);
        return route.Length != 0;
    }

    /// <summary>
    /// Verifies that every sampled player-center point along a segment retains edge clearance in the
    /// selected layout. The misdirection input gate uses this before allowing a movement pulse.
    /// </summary>
    /// <param name="layout">Confirmed active floor layout.</param>
    /// <param name="start">Segment origin.</param>
    /// <param name="end">Segment destination.</param>
    /// <returns><see langword="true"/> when the complete segment stays on surviving floor.</returns>
    internal static bool IsSegmentWalkable(
        NecrohazardFloorLayout layout,
        Vector3 start,
        Vector3 end)
    {
        float deltaX = end.X - start.X;
        float deltaZ = end.Z - start.Z;
        float distance = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        int samples = Math.Max(1, (int)MathF.Ceiling(distance / SegmentSampleSpacing));
        for (int index = 0; index <= samples; index++)
        {
            float interpolation = (float)index / samples;
            Vector3 point = new(
                start.X + (deltaX * interpolation),
                start.Y,
                start.Z + (deltaZ * interpolation));
            if (!IsWalkable(layout, point))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Allows a bounded route-entry recovery when the player begins just outside the conservative
    /// authored floor but the exact route has already supplied a verified walkable waypoint.
    /// </summary>
    /// <param name="layout">Confirmed active floor layout.</param>
    /// <param name="start">Current player position, which may begin outside the conservative model.</param>
    /// <param name="end">End of the proposed forced-movement probe.</param>
    /// <param name="routeEntry">Exact-route waypoint that owns this recovery.</param>
    /// <param name="maximumRouteEntryDistance">Maximum permitted distance from the player to the owning route waypoint.</param>
    /// <param name="maximumUnmodeledPrefixDistance">Maximum initial segment length permitted before entering verified walkable floor.</param>
    /// <returns>
    /// <see langword="true"/> only when the probe advances toward a nearby walkable route waypoint,
    /// enters modeled floor within the bounded prefix, and remains walkable thereafter.
    /// </returns>
    internal static bool IsRouteEntryRecoverySegmentWalkable(
        NecrohazardFloorLayout layout,
        Vector3 start,
        Vector3 end,
        Vector3 routeEntry,
        float maximumRouteEntryDistance,
        float maximumUnmodeledPrefixDistance)
    {
        float startToEntrySquared = DistanceSquared2D(start, routeEntry);
        float movementX = end.X - start.X;
        float movementZ = end.Z - start.Z;
        float entryX = routeEntry.X - start.X;
        float entryZ = routeEntry.Z - start.Z;
        if (startToEntrySquared > maximumRouteEntryDistance * maximumRouteEntryDistance ||
            !IsWalkable(layout, routeEntry) ||
            ((movementX * entryX) + (movementZ * entryZ)) <= 0f)
        {
            return false;
        }

        // Positive projection is the local progress guarantee used by the rotating-input gate. The
        // complete probe can pass the waypoint at a wide but still productive angle, so comparing
        // only endpoint distances would incorrectly reject the captured 68-degree recovery pulse.
        float distance = MathF.Sqrt((movementX * movementX) + (movementZ * movementZ));
        int samples = Math.Max(1, (int)MathF.Ceiling(distance / SegmentSampleSpacing));
        bool enteredWalkableFloor = false;
        for (int index = 0; index <= samples; index++)
        {
            float interpolation = (float)index / samples;
            Vector3 point = new(
                start.X + (movementX * interpolation),
                start.Y,
                start.Z + (movementZ * interpolation));
            if (IsWalkable(layout, point))
            {
                enteredWalkableFloor = true;
                continue;
            }

            if (enteredWalkableFloor || distance * interpolation > maximumUnmodeledPrefixDistance)
            {
                return false;
            }
        }

        return enteredWalkableFloor;
    }

    private static Vector3[] SimplifyRoute(
        NecrohazardFloorLayout layout,
        IReadOnlyList<Vector3> denseRoute)
    {
        if (denseRoute.Count <= 2)
        {
            Vector3[] unchanged = new Vector3[denseRoute.Count];
            for (int index = 0; index < denseRoute.Count; index++)
            {
                unchanged[index] = denseRoute[index];
            }
            return unchanged;
        }

        List<Vector3> simplified = [denseRoute[0]];
        int current = 0;
        while (current < denseRoute.Count - 1)
        {
            int next = denseRoute.Count - 1;
            while (next > current + 1 && !IsSegmentWalkable(layout, denseRoute[current], denseRoute[next]))
            {
                next--;
            }

            simplified.Add(denseRoute[next]);
            current = next;
        }

        return simplified.ToArray();
    }

    private static int FindNearestWalkableIndex(bool[] walkable, Vector3 start)
    {
        int nearestIndex = -1;
        float nearestDistanceSquared = float.MaxValue;
        for (int index = 0; index < walkable.Length; index++)
        {
            if (!walkable[index])
            {
                continue;
            }

            Vector3 point = GridPoint(index, start.Y);
            float deltaX = point.X - start.X;
            float deltaZ = point.Z - start.Z;
            float distanceSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestIndex = index;
                nearestDistanceSquared = distanceSquared;
            }
        }

        return nearestIndex;
    }

    private static bool IsWalkable(NecrohazardFloorLayout layout, Vector3 point)
    {
        if (!IsInsideFloor(layout, point.X, point.Z))
        {
            return false;
        }

        // Cardinal and diagonal probes approximate the player footprint without erasing the narrow
        // authored corridors the way a one-yalm inset would.
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                if (x == 0 && z == 0)
                {
                    continue;
                }

                float length = x != 0 && z != 0 ? 1.41421356f : 1f;
                if (!IsInsideFloor(
                        layout,
                        point.X + ((x / length) * EdgeClearance),
                        point.Z + ((z / length) * EdgeClearance)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static float DistanceSquared2D(Vector3 first, Vector3 second)
    {
        float deltaX = first.X - second.X;
        float deltaZ = first.Z - second.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private static bool IsInsideFloor(NecrohazardFloorLayout layout, float x, float z)
    {
        float deltaX = x - CenterX;
        float deltaZ = z - CenterZ;
        float radiusSquared = (deltaX * deltaX) + (deltaZ * deltaZ);
        if (radiusSquared > ArenaRadius * ArenaRadius)
        {
            return false;
        }

        if (radiusSquared <= CenterIslandRadius * CenterIslandRadius)
        {
            return true;
        }

        Vector2[][] polygons = layout switch
        {
            NecrohazardFloorLayout.FourRoutes => FourRoutePolygons,
            NecrohazardFloorLayout.ThreeRoutes => ThreeRoutePolygons,
            _ => [],
        };
        foreach (Vector2[] polygon in polygons)
        {
            if (IsPointInPolygon(x, z, polygon))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPointInPolygon(float x, float z, IReadOnlyList<Vector2> polygon)
    {
        bool inside = false;
        for (int current = 0, previous = polygon.Count - 1; current < polygon.Count; previous = current++)
        {
            Vector2 first = polygon[current];
            Vector2 second = polygon[previous];
            bool crosses = (first.Y > z) != (second.Y > z) &&
                           x < ((second.X - first.X) * (z - first.Y) /
                               ((second.Y - first.Y) + float.Epsilon)) + first.X;
            if (crosses)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float RadiusFromCenter(Vector3 point)
    {
        float deltaX = point.X - CenterX;
        float deltaZ = point.Z - CenterZ;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static Vector3 GridPoint(int index, float elevation) =>
        GridPoint(index % GridCellCount, index / GridCellCount, elevation);

    private static Vector3 GridPoint(int x, int z, float elevation) =>
        new(GridMinimumX + (x * GridResolution), elevation, GridMinimumZ + (z * GridResolution));

    private static int GridIndex(int x, int z) => (z * GridCellCount) + x;

    private static bool IsGridCoordinateValid(int x, int z) =>
        x >= 0 && x < GridCellCount && z >= 0 && z < GridCellCount;
}
