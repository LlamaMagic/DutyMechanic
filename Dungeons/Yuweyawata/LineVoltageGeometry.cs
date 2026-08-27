using Clio.Utilities;
using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Solves one concurrent Line Voltage wave as a union of forward-only rectangles.
/// </summary>
/// <remarks>
/// Independent rectangle avoids can choose conflicting escape directions. This sampler solves the
/// complete activation cohort and validates the movement chord against every lane. Widths already
/// include the normal latency margin; the additional quarter yalm preserves arrival clearance.
/// </remarks>
internal static class YuweyawataLineVoltageGeometry
{
    private const float GridResolution = 0.5f;
    private const float RectangleClearance = 0.25f;

    /// <summary>
    /// Finds the closest safe point outside every rectangle in one activation cohort.
    /// </summary>
    /// <param name="arenaCenter">Center of Lindblum's circular floor.</param>
    /// <param name="arenaRadius">Inset player-center radius used by manual movement.</param>
    /// <param name="current">Current player position and elevation.</param>
    /// <param name="rectangles">Immutable cast-start rectangles in the concurrent wave.</param>
    /// <param name="destination">Current point when safe, otherwise the nearest sampled union-safe point.</param>
    /// <returns><see langword="true"/> when the arena contains a point outside the complete wave.</returns>
    internal static bool TryFindDestination(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        IReadOnlyCollection<YuweyawataLineVoltageRectangle> rectangles,
        out Vector3 destination)
    {
        destination = default;
        if (arenaRadius <= 0f || rectangles.Count == 0)
        {
            return false;
        }

        if (IsSafe(current, arenaCenter, arenaRadius, rectangles))
        {
            destination = current;
            return true;
        }

        float bestDistanceSquared = float.PositiveInfinity;
        float startX = arenaCenter.X - arenaRadius;
        float endX = arenaCenter.X + arenaRadius;
        float startZ = arenaCenter.Z - arenaRadius;
        float endZ = arenaCenter.Z + arenaRadius;
        for (float x = startX; x <= endX + 0.001f; x += GridResolution)
        {
            for (float z = startZ; z <= endZ + 0.001f; z += GridResolution)
            {
                Vector3 candidate = new(x, current.Y, z);
                if (!IsSafe(candidate, arenaCenter, arenaRadius, rectangles))
                {
                    continue;
                }

                float distanceSquared = DistanceSquared2D(current, candidate);
                if (distanceSquared >= bestDistanceSquared)
                {
                    continue;
                }

                destination = candidate;
                bestDistanceSquared = distanceSquared;
            }
        }

        return bestDistanceSquared < float.PositiveInfinity;
    }

    /// <summary>
    /// Validates a point against the complete concurrent rectangle wave and arena inset.
    /// </summary>
    /// <param name="point">Player-center position to validate.</param>
    /// <param name="arenaCenter">Center of Lindblum's circular floor.</param>
    /// <param name="arenaRadius">Inset player-center radius used by manual movement.</param>
    /// <param name="rectangles">Immutable cast-start rectangles in the concurrent wave.</param>
    /// <returns><see langword="true"/> when no rectangle contains the point.</returns>
    internal static bool IsSafe(
        Vector3 point,
        Vector3 arenaCenter,
        float arenaRadius,
        IReadOnlyCollection<YuweyawataLineVoltageRectangle> rectangles)
    {
        if (DistanceSquared2D(point, arenaCenter) > arenaRadius * arenaRadius)
        {
            return false;
        }

        foreach (YuweyawataLineVoltageRectangle rectangle in rectangles)
        {
            float deltaX = point.X - rectangle.Origin.X;
            float deltaZ = point.Z - rectangle.Origin.Z;
            float forward = (deltaX * MathF.Sin(rectangle.Heading)) +
                            (deltaZ * MathF.Cos(rectangle.Heading));
            float sideways = (deltaX * MathF.Cos(rectangle.Heading)) -
                             (deltaZ * MathF.Sin(rectangle.Heading));
            if (forward >= -RectangleClearance &&
                forward <= rectangle.Length + RectangleClearance &&
                MathF.Abs(sideways) <= rectangle.HalfWidth + RectangleClearance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns a safe intermediate waypoint when a direct chord to the selected endpoint would cross
    /// any rectangle in the concurrent wave.
    /// </summary>
    /// <param name="arenaCenter">Center of Lindblum's circular floor.</param>
    /// <param name="arenaRadius">Inset player-center radius used by manual movement.</param>
    /// <param name="current">Current player position.</param>
    /// <param name="destination">Union-safe endpoint selected for the wave.</param>
    /// <param name="rectangles">Immutable cast-start rectangles in the concurrent wave.</param>
    /// <param name="waypoint">The endpoint for a clear chord, otherwise a sampled route waypoint.</param>
    /// <returns><see langword="true"/> when the endpoint is reachable without entering a rectangle.</returns>
    internal static bool TryFindRouteWaypoint(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        Vector3 destination,
        IReadOnlyCollection<YuweyawataLineVoltageRectangle> rectangles,
        out Vector3 waypoint) =>
        YuweyawataRouteGeometry.TryFindWaypoint(
            arenaCenter,
            arenaRadius,
            current,
            destination,
            point => IsSafe(point, arenaCenter, arenaRadius, rectangles),
            out waypoint);

    private static float DistanceSquared2D(Vector3 first, Vector3 second)
    {
        float deltaX = first.X - second.X;
        float deltaZ = first.Z - second.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }
}

/// <summary>
/// Immutable geometry needed to solve one Line Voltage cast without retaining an actor wrapper.
/// </summary>
/// <param name="Origin">Cast-start helper origin.</param>
/// <param name="Heading">Cast-start FFXIV heading in radians.</param>
/// <param name="HalfWidth">Half of the already-expanded registered rectangle width.</param>
/// <param name="Length">Forward-only rectangle length.</param>
internal readonly record struct YuweyawataLineVoltageRectangle(
    Vector3 Origin,
    float Heading,
    float HalfWidth,
    float Length);
