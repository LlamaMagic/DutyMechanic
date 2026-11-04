using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

/// <summary>Finds a floor-connected position inside Brutal Crown's safe center.</summary>
/// <remarks>
/// On the X floor, native donut escape crossed a gap after Downburst. This
/// planner uses the reference floor and the cast's actual center, while the
/// caller owns timing, other hazards and movement. The 4.25-yalm target radius
/// leaves room for the mover's 0.25-yalm arrival tolerance inside the existing
/// 4.5-yalm safety boundary.
/// </remarks>
internal static class InterphosCrossCrown
{
    internal const float TargetRadius = 4.25f;
    internal readonly record struct Point(float X, float Z);

    /// <summary>Finds a short route through the X floor's actual junctions.</summary>
    /// <remarks>
    /// The captured landing (92.822, 99.395) had no direct path to the Crown
    /// at (100,82.5); standing still was lethal. The center and four crossbar
    /// junctions connect the diagonal arms. Every admitted edge still needs
    /// full floor and live-hazard validation; an unobstructed direct route wins.
    /// </remarks>
    internal static bool TryRoute(float x, float z, float centerX, float centerZ,
        Func<float, float, float, float, bool> acceptSegment, out Point[] route)
    {
        route = [];
        if (acceptSegment == null || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        Point[] nodes = [new(x, z), new(100, 92.5f), new(92.5f, 82.5f),
            new(107.5f, 82.5f), new(92.5f, 102.5f), new(107.5f, 102.5f)];
        float[] distance = [0, float.PositiveInfinity, float.PositiveInfinity,
            float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity];
        int[] previous = [-1, -1, -1, -1, -1, -1];
        bool[] visited = new bool[nodes.Length];
        float best = float.PositiveInfinity;
        int finalNode = -1;
        Point final = default;
        static float Length(Point a, Point b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        for (int pass = 0; pass < nodes.Length; ++pass)
        {
            int n = -1;
            for (int i = 0; i < nodes.Length; ++i)
            {
                if (!visited[i] && distance[i] < best && (n < 0 || distance[i] < distance[n]))
                {
                    n = i;
                }
            }

            if (n < 0)
            {
                break;
            }

            visited[n] = true;
            var a = nodes[n];
            if (TryPosition(a.X, a.Z, centerX, centerZ,
                (px, pz) => acceptSegment(a.X, a.Z, px, pz), out float gx, out float gz))
            {
                var goal = new Point(gx, gz);
                float total = distance[n] + Length(a, goal);
                if (total < best)
                {
                    best = total;
                    finalNode = n;
                    final = goal;
                }
            }
            for (int i = 1; i < nodes.Length; ++i)
            {
                var b = nodes[i];
                float total = distance[n] + Length(a, b);
                if (visited[i] || total >= distance[i] || total >= best ||
                    !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, a.X, a.Z, b.X, b.Z) ||
                    !acceptSegment(a.X, a.Z, b.X, b.Z))
                {
                    continue;
                }

                distance[i] = total;
                previous[i] = n;
            }
        }
        if (finalNode < 0)
        {
            return false;
        }

        var path = new List<Point> { final };
        for (int n = finalNode; n > 0; n = previous[n])
        {
            if (Length(path[^1], nodes[n]) > 0.01f)
            {
                path.Add(nodes[n]);
            }
        }

        path.Reverse();
        route = path.ToArray();
        return true;
    }

    internal static bool TryPosition(float x, float z, float centerX, float centerZ,
        Func<float, float, bool> acceptPath, out float targetX, out float targetZ)
    {
        targetX = targetZ = 0;
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(centerX) ||
            !float.IsFinite(centerZ) || acceptPath == null)
        {
            return false;
        }

        float d0 = (x - centerX) * (x - centerX) + (z - centerZ) * (z - centerZ);
        if (d0 <= TargetRadius * TargetRadius && InterphosFloor.Contains(InterphosFloor.Layout.Cross, x, z) && acceptPath(x, z))
        {
            targetX = x;
            targetZ = z;
            return true;
        }
        float best = float.PositiveInfinity, bx = 0, bz = 0;
        for (float dx = -TargetRadius; dx <= TargetRadius; dx += 0.25f)
        {
            for (float dz = -TargetRadius; dz <= TargetRadius; dz += 0.25f)
            {
                if (dx * dx + dz * dz > TargetRadius * TargetRadius)
                {
                    continue;
                }

                float px = centerX + dx, pz = centerZ + dz;
                float distance = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (distance >= best || !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, x, z, px, pz) ||
                    !acceptPath(px, pz))
                {
                    continue;
                }

                best = distance;
                bx = px;
                bz = pz;
            }
        }

        if (float.IsPositiveInfinity(best))
        {
            return false;
        }

        targetX = bx;
        targetZ = bz;
        return true;
    }
}
