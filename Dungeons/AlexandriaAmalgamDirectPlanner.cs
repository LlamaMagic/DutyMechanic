using System;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

// Search direct walking destinations before adding intermediate waypoints.
// The latter can introduce artificial stops inside a short overlap even when
// a continuous path is certified against every observed effect.
internal static class AlexandriaAmalgamDirectPlanner
{
    internal static AlexandriaGridPlanner.Step[] Plan(Vector2 player, AlexandriaGeometry.Hazard[] shapes,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double spacing,
        double preparationDelay, double horizon, double delay, double minimumSpeed, double maximumSpeed,
        CancellationToken cancellation = default)
    {
        if (!AlexandriaGeometry.Finite(player) || shapes == null || requirements == null ||
            !double.IsFinite(margin) || margin < 0 || margin >= 20 || !double.IsFinite(spacing) || spacing < .5 ||
            !double.IsFinite(preparationDelay) || preparationDelay < 0 || !double.IsFinite(horizon) ||
            horizon <= preparationDelay || cancellation.IsCancellationRequested) return [];
        var hold = new AlexandriaGeometry.Travel(0, preparationDelay, 0, minimumSpeed, maximumSpeed);
        if (!AlexandriaGeometry.TravelSafe(player, player, hold, shapes) ||
            !AlexandriaPositionConstraints.TravelInside(player, player, hold, requirements)) return [];
        var travel = new AlexandriaGeometry.Travel(preparationDelay, horizon, delay, minimumSpeed, maximumSpeed);
        var center = new Vector2(-533, -373);
        double usable = 20 - margin;
        if (Math.Abs(player.X - center.X) > usable || Math.Abs(player.Y - center.Y) > usable) return [];
        Vector2 best = default;
        float bestDistance = float.PositiveInfinity;
        void Consider(Vector2 candidate)
        {
            if (Math.Abs(candidate.X - center.X) > usable || Math.Abs(candidate.Y - center.Y) > usable) return;
            float distance = Vector2.DistanceSquared(player, candidate);
            if (distance >= bestDistance || !AlexandriaGeometry.TravelSafe(player, candidate, travel, shapes) ||
                !AlexandriaPositionConstraints.TravelInside(player, candidate, travel, requirements)) return;
            best = candidate; bestDistance = distance;
        }
        Consider(player);
        int extent = (int)Math.Floor(usable / spacing);
        for (int x = -extent; x <= extent; ++x)
        for (int z = -extent; z <= extent; ++z)
        {
            if (cancellation.IsCancellationRequested) return [];
            Consider(new Vector2((float)(center.X + x * spacing), (float)(center.Y + z * spacing)));
        }
        return float.IsFinite(bestDistance) ? [new(player, preparationDelay), new(best, horizon)] : [];
    }
}
