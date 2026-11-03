using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

// Full-horizon alternative to the half-second lattice. Stage boundaries at
// actual hazard windows avoid charging a new input handoff at artificial
// intermediate ticks. Every resulting direct edge still has the same speed,
// delay and continuous safety certificate as the encounter input owner.
internal static class AlexandriaInterferonBoundaryPlanner
{
    internal static AlexandriaInterferonPlanner.Step[] Plan(Vector2 player,
        AlexandriaInterferonGeometry.Hazard[] hazards, double horizon, double preparationDelay,
        CancellationToken cancellation = default, AlexandriaTemporalPlanner.MovementBoost? movementBoost = null)
    {
        if (cancellation.IsCancellationRequested || !double.IsFinite(horizon) ||
            !double.IsFinite(preparationDelay) || preparationDelay < 0 || horizon <= preparationDelay || hazards == null || hazards.Length == 0 ||
            !AlexandriaInterferonGeometry.TryConvert(hazards, out var shapes)) return [];

        // The worker can use its whole publication allowance. Any damage
        // during that time must be safe at the position actually captured.
        if (!AlexandriaTemporalPlanner.TryMovementBounds(0, horizon, AlexandriaInterferonPlanner.MinimumSpeed,
            AlexandriaInterferonPlanner.MaximumSpeed, movementBoost, out _, out _)) return [];
        var hold = new AlexandriaGeometry.Travel(0, preparationDelay, 0,
            AlexandriaInterferonPlanner.MinimumSpeed, AlexandriaInterferonPlanner.MaximumSpeed);
        if (!AlexandriaGeometry.TravelSafe(player, player, hold, shapes)) return [];

        var stages = new SortedSet<double> { preparationDelay, horizon };
        if (movementBoost is { } boost)
        {
            if (boost.EarliestExpiry > preparationDelay && boost.EarliestExpiry < horizon) stages.Add(boost.EarliestExpiry);
            if (boost.LatestExpiry > preparationDelay && boost.LatestExpiry < horizon) stages.Add(boost.LatestExpiry);
        }
        foreach (var shape in shapes)
        {
            if (shape.Latest > horizon) return [];
            if (shape.Earliest > preparationDelay && shape.Earliest < horizon) stages.Add(shape.Earliest);
            if (shape.Latest > preparationDelay && shape.Latest < horizon) stages.Add(shape.Latest);
        }
        // Concentrate candidates around all known origins and the player,
        // with four and a half yalms of room on every side for the node
        // shelters and cross clearance. Clip the convex rectangle to the
        // interior of the arena; a larger forecast uses a coarser bounded grid.
        // This candidate region can miss a route elsewhere in the arena; an
        // empty result is not proof that no continuous solution exists.
        float left = Math.Max(833, Math.Min(player.X, hazards.Min(h => h.Origin.X)) - 4.5f);
        float right = Math.Min(871, Math.Max(player.X, hazards.Max(h => h.Origin.X)) + 4.5f);
        float top = Math.Max(809, Math.Min(player.Y, hazards.Min(h => h.Origin.Y)) - 4.5f);
        float bottom = Math.Min(837, Math.Max(player.Y, hazards.Max(h => h.Origin.Y)) + 4.5f);
        double halfWidth = (right - left) / 2, halfHeight = (bottom - top) / 2;
        var center = new Vector2((left + right) / 2, (top + bottom) / 2);
        double spacing = .6;
        while ((2 * Math.Floor(halfWidth / spacing) + 1) * (2 * Math.Floor(halfHeight / spacing) + 1) > 2000)
            spacing += .1;
        var route = AlexandriaGridPlanner.Plan(player, center, halfWidth, halfHeight, 0, spacing,
            stages.ToArray(), shapes, Array.Empty<AlexandriaPositionConstraints.Timed>(), .1,
            AlexandriaInterferonPlanner.MinimumSpeed, AlexandriaInterferonPlanner.MaximumSpeed, cancellation,
            movementBoost: movementBoost);
        return route.Select(step => new AlexandriaInterferonPlanner.Step(step.Position, step.Time)).ToArray();
    }
}
