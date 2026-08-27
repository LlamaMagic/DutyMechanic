using Clio.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Selects stable safe points for Overseer Kanilokka's activation-timed Soulweave rings.
/// </summary>
/// <remarks>
/// RebornBuddy's polygon surface did not preserve this donut's hole reliably, so this sampler tests
/// the authored 28-to-32-yalm band directly. Endpoints keep a half-yalm movement cushion, while
/// current-position and route checks use the exact band so that cushion cannot trigger unsafe-start
/// routing.
/// </remarks>
internal static class YuweyawataSoulweaveGeometry
{
    private const float InnerRadius = 28f;
    private const float OuterRadius = 32f;
    // Observed OmenMatrix centers were 30 yalms from their Preserved Soul. The matching heading
    // projection is the fallback when RB has not populated the matrix on the first pulse.
    private const float RingOriginOffset = 30f;
    private const float RingOriginDistanceTolerance = 1f;
    private const float MinimumBandClearance = 0.5f;
    private const float GridResolution = 0.5f;
    private const float ConcurrentSpreadClearance = 5.5f;
    private const float ActualSpreadRadius = 5f;
    private const float EscapeImprovementEpsilon = 0.01f;
    // Search up to 1.5 yalms so an egress survives one behavior pulse; quarter-yalm probes must still
    // improve clearance along the complete segment.
    private const int MaximumEscapeGridSteps = 3;

    /// <summary>
    /// Resolves the damaging ring's ground origin from the cast's visual matrix, falling back to the
    /// observed fixed projection from the helper only when that matrix is absent or stale.
    /// </summary>
    /// <param name="actorLocation">Current-frame Preserved Soul location.</param>
    /// <param name="actorHeading">Current-frame Preserved Soul heading in FFXIV radians.</param>
    /// <param name="omenOrigin">Current-frame <c>OmenMatrix.Center</c> value.</param>
    /// <param name="origin">Resolved immutable ring origin.</param>
    /// <returns><see langword="true"/> when either evidence path produced finite geometry.</returns>
    internal static bool TryResolveRingOrigin(
        Vector3 actorLocation,
        float actorHeading,
        Vector3 omenOrigin,
        out Vector3 origin)
    {
        origin = default;
        if (!IsFinite(actorLocation))
        {
            return false;
        }

        float minimumOriginDistance = RingOriginOffset - RingOriginDistanceTolerance;
        float maximumOriginDistance = RingOriginOffset + RingOriginDistanceTolerance;
        float omenDistanceSquared = DistanceSquared2D(actorLocation, omenOrigin);
        if (IsFinite(omenOrigin) &&
            omenDistanceSquared >= minimumOriginDistance * minimumOriginDistance &&
            omenDistanceSquared <= maximumOriginDistance * maximumOriginDistance)
        {
            origin = omenOrigin;
            return true;
        }

        if (!float.IsFinite(actorHeading))
        {
            return false;
        }

        origin = new Vector3(
            actorLocation.X + (MathF.Cos(actorHeading) * RingOriginOffset),
            actorLocation.Y,
            actorLocation.Z - (MathF.Sin(actorHeading) * RingOriginOffset));
        return IsFinite(origin);
    }

    /// <summary>
    /// Finds the closest sampled point that is comfortably outside every concurrently resolving
    /// Soulweave band and every party position that the simultaneous Telltale Tears marker requires
    /// the local player to avoid.
    /// </summary>
    /// <param name="arenaCenter">Center of Kanilokka's current circular floor.</param>
    /// <param name="arenaRadius">Navigation-inset radius of the surviving floor.</param>
    /// <param name="current">Current player position and elevation.</param>
    /// <param name="ringCenters">Immutable cast-start centers for the active Soulweave wave.</param>
    /// <param name="spreadTargets">Current party positions excluded by the local player's Telltale Tears role.</param>
    /// <param name="destination">A stable safe point when one exists.</param>
    /// <param name="minimumClearance">Smallest radial clearance from any Soulweave band at the result.</param>
    /// <returns><see langword="true"/> when a point satisfying every supplied constraint was found.</returns>
    internal static bool TryFindDestination(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        out Vector3 destination,
        out float minimumClearance)
    {
        destination = default;
        minimumClearance = float.NegativeInfinity;
        if (arenaRadius <= 0f || ringCenters.Count == 0)
        {
            return false;
        }

        if (TryEvaluateCandidate(
                current,
                arenaCenter,
                arenaRadius,
                ringCenters,
                spreadTargets,
                out float currentClearance))
        {
            destination = current;
            minimumClearance = currentClearance;
            return true;
        }

        float bestDistanceSquared = float.MaxValue;
        float startX = arenaCenter.X - arenaRadius;
        float endX = arenaCenter.X + arenaRadius;
        float startZ = arenaCenter.Z - arenaRadius;
        float endZ = arenaCenter.Z + arenaRadius;
        for (float x = startX; x <= endX + 0.001f; x += GridResolution)
        {
            for (float z = startZ; z <= endZ + 0.001f; z += GridResolution)
            {
                Vector3 candidate = new(x, current.Y, z);
                if (!TryEvaluateCandidate(
                        candidate,
                        arenaCenter,
                        arenaRadius,
                        ringCenters,
                        spreadTargets,
                        out float candidateClearance))
                {
                    continue;
                }

                float distanceSquared = DistanceSquared2D(candidate, current);
                if (distanceSquared > bestDistanceSquared + 0.001f ||
                    (MathF.Abs(distanceSquared - bestDistanceSquared) <= 0.001f &&
                     candidateClearance <= minimumClearance))
                {
                    continue;
                }

                destination = candidate;
                minimumClearance = candidateClearance;
                bestDistanceSquared = distanceSquared;
            }
        }

        return bestDistanceSquared < float.MaxValue;
    }

    /// <summary>
    /// Validates an already-selected destination against a new frame's ring and party positions.
    /// This lets the caller retain a stable point until a genuinely changed wave invalidates it.
    /// </summary>
    /// <param name="point">Previously selected point.</param>
    /// <param name="arenaCenter">Center of Kanilokka's current circular floor.</param>
    /// <param name="arenaRadius">Navigation-inset radius of the surviving floor.</param>
    /// <param name="ringCenters">Immutable cast-start centers for the active Soulweave wave.</param>
    /// <param name="spreadTargets">Current party positions excluded by the local player's Telltale Tears role.</param>
    /// <returns><see langword="true"/> when the point still satisfies every constraint.</returns>
    internal static bool IsSafe(
        Vector3 point,
        Vector3 arenaCenter,
        float arenaRadius,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets) =>
        TryEvaluateCandidate(
            point,
            arenaCenter,
            arenaRadius,
            ringCenters,
            spreadTargets,
            out _);

    /// <summary>
    /// Classifies the player's current position against authored damage geometry without applying
    /// endpoint cushions. This distinction is required because an expanded planning band is not an
    /// actual hazard and must never activate unsafe-start routing.
    /// </summary>
    /// <param name="point">Current player-center position.</param>
    /// <param name="arenaCenter">Center of Kanilokka's current circular floor.</param>
    /// <param name="arenaRadius">Navigation-inset radius of the surviving floor.</param>
    /// <param name="ringCenters">Immutable cast-start centers for the active Soulweave cohort.</param>
    /// <param name="spreadTargets">Current party positions excluded by the local player's Telltale Tears role.</param>
    /// <returns><see langword="true"/> when the point is outside every authored hazard.</returns>
    internal static bool IsOutsideActualHazards(
        Vector3 point,
        Vector3 arenaCenter,
        float arenaRadius,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets)
    {
        if (DistanceSquared2D(point, arenaCenter) > arenaRadius * arenaRadius)
        {
            return false;
        }

        foreach (Vector3 ringCenter in ringCenters)
        {
            float distance = MathF.Sqrt(DistanceSquared2D(point, ringCenter));
            if (distance >= InnerRadius && distance <= OuterRadius)
            {
                return false;
            }
        }

        float spreadRadiusSquared = ActualSpreadRadius * ActualSpreadRadius;
        foreach (Vector3 target in spreadTargets)
        {
            if (DistanceSquared2D(point, target) <= spreadRadiusSquared)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Converts a safe Soulweave endpoint into a waypoint whose complete movement chord also avoids
    /// every active ring and concurrent spread.
    /// </summary>
    /// <param name="arenaCenter">Center of Kanilokka's current circular floor.</param>
    /// <param name="arenaRadius">Navigation-inset radius of the surviving floor.</param>
    /// <param name="current">Current player position.</param>
    /// <param name="destination">Safe endpoint selected for the active cohort.</param>
    /// <param name="ringCenters">Immutable cast-start centers for the active Soulweave cohort.</param>
    /// <param name="spreadTargets">Current party positions excluded by the local player's Telltale Tears role.</param>
    /// <param name="waypoint">The endpoint for a clear chord, otherwise a sampled route waypoint.</param>
    /// <returns><see langword="true"/> when the endpoint is reachable without crossing active geometry.</returns>
    internal static bool TryFindRouteWaypoint(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        Vector3 destination,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        out Vector3 waypoint)
    {
        Func<Vector3, bool> isOutsideActualHazards = point => IsOutsideActualHazards(
            point,
            arenaCenter,
            arenaRadius,
            ringCenters,
            spreadTargets);
        if (isOutsideActualHazards(current))
        {
            return YuweyawataRouteGeometry.TryFindWaypoint(
                arenaCenter,
                arenaRadius,
                current,
                destination,
                isOutsideActualHazards,
                out waypoint);
        }

        return TryFindImprovingEscapeWaypoint(
            arenaCenter,
            arenaRadius,
            current,
            destination,
            ringCenters,
            spreadTargets,
            out waypoint);
    }

    /// <summary>
    /// Retains a prior unsafe-start egress waypoint when a fresh grid solve transiently fails.
    /// </summary>
    /// <remarks>
    /// The mover can advance farther than one grid cell between behavior pulses. Continue only when
    /// the complete prior segment still improves clearance from the current cohort; FIFO changes
    /// therefore invalidate stale waypoints automatically.
    /// </remarks>
    /// <param name="arenaCenter">Center of Kanilokka's current circular floor.</param>
    /// <param name="arenaRadius">Navigation-inset radius of the surviving floor.</param>
    /// <param name="current">Current player position.</param>
    /// <param name="previousWaypoint">Scalar waypoint published by the preceding behavior pulse.</param>
    /// <param name="ringCenters">Immutable cast-start centers for the active Soulweave cohort.</param>
    /// <param name="spreadTargets">Current party positions excluded by Telltale Tears.</param>
    /// <param name="waypoint">The retained waypoint when it remains a valid improving egress.</param>
    /// <returns><see langword="true"/> when continuing the prior segment cannot worsen any active hazard.</returns>
    internal static bool TryContinueImprovingEscapeWaypoint(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        Vector3 previousWaypoint,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        out Vector3 waypoint)
    {
        waypoint = default;
        if (!IsFinite(previousWaypoint) ||
            IsOutsideActualHazards(current, arenaCenter, arenaRadius, ringCenters, spreadTargets))
        {
            return false;
        }

        float currentScore = GetActualHazardScore(
            current,
            arenaCenter,
            arenaRadius,
            ringCenters,
            spreadTargets);
        float waypointScore = GetActualHazardScore(
            previousWaypoint,
            arenaCenter,
            arenaRadius,
            ringCenters,
            spreadTargets);
        if (waypointScore <= currentScore + EscapeImprovementEpsilon ||
            !IsImprovingEscapeSegment(
                current,
                previousWaypoint,
                arenaCenter,
                arenaRadius,
                ringCenters,
                spreadTargets,
                currentScore))
        {
            return false;
        }

        waypoint = previousWaypoint;
        return true;
    }

    /// <summary>
    /// Produces one bounded egress step from authored geometry. Every sample must improve signed
    /// clearance without entering a hazard that did not already contain the start.
    /// </summary>
    private static bool TryFindImprovingEscapeWaypoint(
        Vector3 arenaCenter,
        float arenaRadius,
        Vector3 current,
        Vector3 destination,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        out Vector3 waypoint)
    {
        waypoint = default;
        float currentScore = GetActualHazardScore(
            current,
            arenaCenter,
            arenaRadius,
            ringCenters,
            spreadTargets);
        float bestScore = currentScore;
        float bestDestinationDistanceSquared = float.PositiveInfinity;
        for (int offsetX = -MaximumEscapeGridSteps; offsetX <= MaximumEscapeGridSteps; offsetX++)
        {
            for (int offsetZ = -MaximumEscapeGridSteps; offsetZ <= MaximumEscapeGridSteps; offsetZ++)
            {
                int gridDistanceSquared = (offsetX * offsetX) + (offsetZ * offsetZ);
                if (gridDistanceSquared == 0 ||
                    gridDistanceSquared > MaximumEscapeGridSteps * MaximumEscapeGridSteps)
                {
                    continue;
                }

                Vector3 candidate = new(
                    current.X + (offsetX * GridResolution),
                    current.Y,
                    current.Z + (offsetZ * GridResolution));
                float candidateScore = GetActualHazardScore(
                    candidate,
                    arenaCenter,
                    arenaRadius,
                    ringCenters,
                    spreadTargets);
                if (candidateScore <= currentScore + EscapeImprovementEpsilon ||
                    !IsImprovingEscapeSegment(
                        current,
                        candidate,
                        arenaCenter,
                        arenaRadius,
                        ringCenters,
                        spreadTargets,
                        currentScore))
                {
                    continue;
                }

                float destinationDistanceSquared = DistanceSquared2D(candidate, destination);
                if (candidateScore < bestScore - EscapeImprovementEpsilon ||
                    (MathF.Abs(candidateScore - bestScore) <= EscapeImprovementEpsilon &&
                     destinationDistanceSquared >= bestDestinationDistanceSquared))
                {
                    continue;
                }

                waypoint = candidate;
                bestScore = candidateScore;
                bestDestinationDistanceSquared = destinationDistanceSquared;
            }
        }

        return bestScore > currentScore + EscapeImprovementEpsilon;
    }

    private static bool IsImprovingEscapeSegment(
        Vector3 start,
        Vector3 end,
        Vector3 arenaCenter,
        float arenaRadius,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        float startScore)
    {
        float deltaX = end.X - start.X;
        float deltaZ = end.Z - start.Z;
        float distance = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        int probes = Math.Max(4, (int)MathF.Ceiling(distance / 0.25f));
        float previousArenaClearance = GetArenaClearance(start, arenaCenter, arenaRadius);
        float[] previousRingClearances = ringCenters
            .Select(ringCenter => GetRingClearance(start, ringCenter))
            .ToArray();
        float[] previousSpreadClearances = spreadTargets
            .Select(target => GetSpreadClearance(start, target))
            .ToArray();
        for (int probe = 1; probe <= probes; probe++)
        {
            float progress = probe / (float)probes;
            Vector3 point = new(
                start.X + ((end.X - start.X) * progress),
                start.Y,
                start.Z + ((end.Z - start.Z) * progress));
            float arenaClearance = GetArenaClearance(point, arenaCenter, arenaRadius);
            if (!ClearanceDoesNotRegress(previousArenaClearance, arenaClearance))
            {
                return false;
            }

            previousArenaClearance = arenaClearance;
            int index = 0;
            foreach (Vector3 ringCenter in ringCenters)
            {
                float clearance = GetRingClearance(point, ringCenter);
                if (!ClearanceDoesNotRegress(previousRingClearances[index], clearance))
                {
                    return false;
                }

                previousRingClearances[index++] = clearance;
            }

            index = 0;
            foreach (Vector3 target in spreadTargets)
            {
                float clearance = GetSpreadClearance(point, target);
                if (!ClearanceDoesNotRegress(previousSpreadClearances[index], clearance))
                {
                    return false;
                }

                previousSpreadClearances[index++] = clearance;
            }
        }

        return GetActualHazardScore(
            end,
            arenaCenter,
            arenaRadius,
            ringCenters,
            spreadTargets) > startScore + EscapeImprovementEpsilon;
    }

    private static float GetActualHazardScore(
        Vector3 point,
        Vector3 arenaCenter,
        float arenaRadius,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets)
    {
        float score = GetArenaClearance(point, arenaCenter, arenaRadius);
        foreach (Vector3 ringCenter in ringCenters)
        {
            score = MathF.Min(score, GetRingClearance(point, ringCenter));
        }

        foreach (Vector3 target in spreadTargets)
        {
            score = MathF.Min(score, GetSpreadClearance(point, target));
        }

        return score;
    }

    private static bool ClearanceDoesNotRegress(float previous, float current) =>
        previous < 0f
            ? current + EscapeImprovementEpsilon >= previous
            : current >= 0f;

    private static float GetArenaClearance(Vector3 point, Vector3 arenaCenter, float arenaRadius) =>
        arenaRadius - MathF.Sqrt(DistanceSquared2D(point, arenaCenter));

    private static float GetRingClearance(Vector3 point, Vector3 ringCenter)
    {
        float distance = MathF.Sqrt(DistanceSquared2D(point, ringCenter));
        return MathF.Max(InnerRadius - distance, distance - OuterRadius);
    }

    private static float GetSpreadClearance(Vector3 point, Vector3 target) =>
        MathF.Sqrt(DistanceSquared2D(point, target)) - ActualSpreadRadius;

    private static bool TryEvaluateCandidate(
        Vector3 point,
        Vector3 arenaCenter,
        float arenaRadius,
        IReadOnlyCollection<Vector3> ringCenters,
        IReadOnlyCollection<Vector3> spreadTargets,
        out float minimumClearance)
    {
        minimumClearance = float.PositiveInfinity;
        if (DistanceSquared2D(point, arenaCenter) > arenaRadius * arenaRadius)
        {
            return false;
        }

        foreach (Vector3 ringCenter in ringCenters)
        {
            float distance = MathF.Sqrt(DistanceSquared2D(point, ringCenter));
            float clearance = MathF.Max(InnerRadius - distance, distance - OuterRadius);
            minimumClearance = MathF.Min(minimumClearance, clearance);
            if (clearance < MinimumBandClearance)
            {
                return false;
            }
        }

        float spreadClearanceSquared = ConcurrentSpreadClearance * ConcurrentSpreadClearance;
        foreach (Vector3 target in spreadTargets)
        {
            if (DistanceSquared2D(point, target) < spreadClearanceSquared)
            {
                return false;
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

    private static bool IsFinite(Vector3 point) =>
        float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
