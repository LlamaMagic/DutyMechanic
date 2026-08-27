using Clio.Utilities;
using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Finds short, fully validated movement segments through Yuweyawata's encounter-local hazard maps.
/// </summary>
/// <remarks>
/// A safe endpoint is insufficient when the mover's chord can cross a thin ring or rectangle. This
/// bounded half-yalm grid samples every connector at quarter-yalm intervals and is used only while
/// one semantic mechanic owns movement.
/// </remarks>
internal static class YuweyawataRouteGeometry
{
    private const float GridResolution = 0.5f;
    private const float SegmentProbeSpacing = 0.25f;

    private static readonly (int X, int Z)[] NeighborOffsets =
    [
        (-1, -1), (0, -1), (1, -1),
        (-1, 0),             (1, 0),
        (-1, 1),  (0, 1),   (1, 1),
    ];

    /// <summary>
    /// Returns the farthest immediately reachable waypoint on a safe grid route to a destination.
    /// </summary>
    /// <param name="arenaCenter">Center of the circular floor used to bound the search.</param>
    /// <param name="arenaRadius">Already-inset player-center radius of the walkable floor.</param>
    /// <param name="current">Current player position.</param>
    /// <param name="destination">Previously selected safe endpoint.</param>
    /// <param name="isSafe">Predicate that incorporates every hazard in the owning mechanic wave.</param>
    /// <param name="waypoint">Destination itself for a clear chord, otherwise a safe intermediate point.</param>
    /// <returns><see langword="true"/> when a completely sampled route exists.</returns>
    internal static bool TryFindWaypoint(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        Vector3 destination,
        Func<Vector3, bool> isSafe,
        out Vector3 waypoint)
    {
        waypoint = default;
        if (arenaRadius <= 0f || !isSafe(destination))
        {
            return false;
        }

        // Generic routing cannot prove how to leave an unsafe start without signed hazard geometry.
        // Encounter-specific solvers may supply a monotonic egress; every other caller fails closed.
        if (!isSafe(current))
        {
            return false;
        }

        if (IsSegmentSafe(current, destination, isSafe))
        {
            waypoint = destination;
            return true;
        }

        int side = (int)MathF.Ceiling((arenaRadius * 2f) / GridResolution) + 1;
        int nodeCount = side * side;
        float startX = arenaCenter.X - arenaRadius;
        float startZ = arenaCenter.Z - arenaRadius;
        bool[] safeNodes = new bool[nodeCount];
        Vector3[] points = new Vector3[nodeCount];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                int index = (z * side) + x;
                Vector3 point = new(
                    startX + (x * GridResolution),
                    current.Y,
                    startZ + (z * GridResolution));
                points[index] = point;
                safeNodes[index] = isSafe(point);
            }
        }

        int startNode = FindNearestConnectedNode(current, points, safeNodes, isSafe);
        int destinationNode = FindNearestConnectedNode(destination, points, safeNodes, isSafe);
        if (startNode < 0 || destinationNode < 0)
        {
            return false;
        }

        float[] costs = new float[nodeCount];
        int[] previous = new int[nodeCount];
        Array.Fill(costs, float.PositiveInfinity);
        Array.Fill(previous, -1);
        PriorityQueue<int, float> frontier = new();
        costs[startNode] = 0f;
        frontier.Enqueue(startNode, Heuristic(points[startNode], points[destinationNode]));

        while (frontier.TryDequeue(out int currentNode, out _))
        {
            if (currentNode == destinationNode)
            {
                break;
            }

            int currentX = currentNode % side;
            int currentZ = currentNode / side;
            foreach ((int offsetX, int offsetZ) in NeighborOffsets)
            {
                int neighborX = currentX + offsetX;
                int neighborZ = currentZ + offsetZ;
                if (neighborX < 0 || neighborX >= side || neighborZ < 0 || neighborZ >= side)
                {
                    continue;
                }

                int neighbor = (neighborZ * side) + neighborX;
                if (!safeNodes[neighbor] ||
                    !IsSegmentSafe(points[currentNode], points[neighbor], isSafe))
                {
                    continue;
                }

                float stepCost = offsetX == 0 || offsetZ == 0
                    ? GridResolution
                    : GridResolution * MathF.Sqrt(2f);
                float candidateCost = costs[currentNode] + stepCost;
                if (candidateCost >= costs[neighbor])
                {
                    continue;
                }

                costs[neighbor] = candidateCost;
                previous[neighbor] = currentNode;
                frontier.Enqueue(
                    neighbor,
                    candidateCost + Heuristic(points[neighbor], points[destinationNode]));
            }
        }

        if (startNode != destinationNode && previous[destinationNode] < 0)
        {
            return false;
        }

        List<int> route = [];
        for (int node = destinationNode; node >= 0; node = previous[node])
        {
            route.Add(node);
            if (node == startNode)
            {
                break;
            }
        }

        route.Reverse();
        waypoint = points[startNode];
        foreach (int node in route)
        {
            Vector3 candidate = points[node];
            if (!IsSegmentSafe(current, candidate, isSafe))
            {
                break;
            }

            waypoint = candidate;
        }

        return true;
    }

    /// <summary>
    /// Returns the farthest immediately reachable waypoint on the shortest safe route into a goal
    /// region rather than to one exact point.
    /// </summary>
    /// <remarks>
    /// Positive-position mechanics accept a region rather than an exact coordinate. This search
    /// stops at the nearest reachable goal node and applies the same connector validation as an
    /// exact route; unsafe starts remain the owning mechanic's responsibility.
    /// </remarks>
    /// <param name="arenaCenter">Center of the circular floor used to bound the search.</param>
    /// <param name="arenaRadius">Already-inset player-center radius of the walkable floor.</param>
    /// <param name="current">Current player position.</param>
    /// <param name="isSafe">Predicate incorporating every active hazard in the owning mechanic wave.</param>
    /// <param name="isGoal">Predicate identifying points that satisfy the positive-position mechanic.</param>
    /// <param name="waypoint">Current position when already in the goal, otherwise a sampled route waypoint.</param>
    /// <returns><see langword="true"/> when a completely sampled route reaches the goal region.</returns>
    internal static bool TryFindWaypointToRegion(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        Func<Vector3, bool> isSafe,
        Func<Vector3, bool> isGoal,
        out Vector3 waypoint)
    {
        waypoint = default;
        if (arenaRadius <= 0f || !isSafe(current))
        {
            return false;
        }

        if (isGoal(current))
        {
            waypoint = current;
            return true;
        }

        int side = (int)MathF.Ceiling((arenaRadius * 2f) / GridResolution) + 1;
        int nodeCount = side * side;
        float startX = arenaCenter.X - arenaRadius;
        float startZ = arenaCenter.Z - arenaRadius;
        bool[] safeNodes = new bool[nodeCount];
        Vector3[] points = new Vector3[nodeCount];
        for (int z = 0; z < side; z++)
        {
            for (int x = 0; x < side; x++)
            {
                int index = (z * side) + x;
                Vector3 point = new(
                    startX + (x * GridResolution),
                    current.Y,
                    startZ + (z * GridResolution));
                points[index] = point;
                safeNodes[index] = isSafe(point);
            }
        }

        int startNode = FindNearestConnectedNode(current, points, safeNodes, isSafe);
        if (startNode < 0)
        {
            return false;
        }

        float[] costs = new float[nodeCount];
        int[] previous = new int[nodeCount];
        Array.Fill(costs, float.PositiveInfinity);
        Array.Fill(previous, -1);
        PriorityQueue<int, float> frontier = new();
        costs[startNode] = 0f;
        frontier.Enqueue(startNode, 0f);
        int destinationNode = -1;

        while (frontier.TryDequeue(out int currentNode, out float queuedCost))
        {
            if (queuedCost > costs[currentNode])
            {
                continue;
            }

            if (isGoal(points[currentNode]))
            {
                destinationNode = currentNode;
                break;
            }

            int currentX = currentNode % side;
            int currentZ = currentNode / side;
            foreach ((int offsetX, int offsetZ) in NeighborOffsets)
            {
                int neighborX = currentX + offsetX;
                int neighborZ = currentZ + offsetZ;
                if (neighborX < 0 || neighborX >= side || neighborZ < 0 || neighborZ >= side)
                {
                    continue;
                }

                int neighbor = (neighborZ * side) + neighborX;
                if (!safeNodes[neighbor] ||
                    !IsSegmentSafe(points[currentNode], points[neighbor], isSafe))
                {
                    continue;
                }

                float stepCost = offsetX == 0 || offsetZ == 0
                    ? GridResolution
                    : GridResolution * MathF.Sqrt(2f);
                float candidateCost = costs[currentNode] + stepCost;
                if (candidateCost >= costs[neighbor])
                {
                    continue;
                }

                costs[neighbor] = candidateCost;
                previous[neighbor] = currentNode;
                frontier.Enqueue(neighbor, candidateCost);
            }
        }

        if (destinationNode < 0)
        {
            return false;
        }

        List<int> route = [];
        for (int node = destinationNode; node >= 0; node = previous[node])
        {
            route.Add(node);
            if (node == startNode)
            {
                break;
            }
        }

        route.Reverse();
        waypoint = points[startNode];
        foreach (int node in route)
        {
            Vector3 candidate = points[node];
            if (!IsSegmentSafe(current, candidate, isSafe))
            {
                break;
            }

            waypoint = candidate;
        }

        return true;
    }

    private static int FindNearestConnectedNode(
        Vector3 endpoint,
        Vector3[] points,
        bool[] safeNodes,
        Func<Vector3, bool> isSafe)
    {
        int best = -1;
        float bestDistanceSquared = float.PositiveInfinity;
        for (int index = 0; index < points.Length; index++)
        {
            if (!safeNodes[index])
            {
                continue;
            }

            float distanceSquared = DistanceSquared2D(endpoint, points[index]);
            if (distanceSquared >= bestDistanceSquared ||
                !IsSegmentSafe(endpoint, points[index], isSafe))
            {
                continue;
            }

            best = index;
            bestDistanceSquared = distanceSquared;
        }

        return best;
    }

    private static bool IsSegmentSafe(
        Vector3 start,
        Vector3 end,
        Func<Vector3, bool> isSafe)
    {
        float deltaX = end.X - start.X;
        float deltaZ = end.Z - start.Z;
        float distance = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        int probes = Math.Max(1, (int)MathF.Ceiling(distance / SegmentProbeSpacing));
        for (int probe = 0; probe <= probes; probe++)
        {
            float progress = probe / (float)probes;
            Vector3 point = new(
                start.X + (deltaX * progress),
                start.Y,
                start.Z + (deltaZ * progress));
            if (!isSafe(point))
            {
                return false;
            }
        }

        return true;
    }

    private static float Heuristic(Vector3 first, Vector3 second) =>
        MathF.Sqrt(DistanceSquared2D(first, second));

    private static float DistanceSquared2D(Vector3 first, Vector3 second)
    {
        float deltaX = first.X - second.X;
        float deltaZ = first.Z - second.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }
}
