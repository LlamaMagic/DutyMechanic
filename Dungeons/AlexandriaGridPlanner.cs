using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Builds finite candidate stages on a rectangular boss arena and delegates
/// simultaneous hazard/positive-window routing to the temporal planner. This
/// detached planner owns no movement and requires encounter-supplied evidence.
/// </summary>
internal static class AlexandriaGridPlanner
{
    internal readonly record struct Step(Vector2 Position, double Time);

    /// <summary>
    /// Finds a certified route from the actual position through the supplied
    /// ordered times. Empty means no route was certified, not that no continuous
    /// route exists. The caller supplies measured speed/delay, effect windows,
    /// relevant mechanic constraints and an arena inset for position uncertainty.
    /// An optional forced edge derives each landing from its paired approach;
    /// the caller must verify the displacement and its duration independently.
    /// </summary>
    internal static Step[] Plan(Vector2 start, Vector2 center, double halfWidth, double halfHeight,
        double inset, double spacing, double[] times, AlexandriaGeometry.Hazard[] hazards,
        AlexandriaPositionConstraints.Timed[] requirements, double delay, double minimumSpeed,
        double maximumSpeed, CancellationToken cancellation = default,
        AlexandriaTemporalPlanner.RadialKnockback? knockback = null,
        AlexandriaTemporalPlanner.MovementBoost? movementBoost = null)
    {
        if (!AlexandriaGeometry.Finite(start) || !AlexandriaGeometry.Finite(center) ||
            !double.IsFinite(halfWidth) || !double.IsFinite(halfHeight) || !double.IsFinite(inset) ||
            !double.IsFinite(spacing) || inset < 0 || spacing <= 0 || halfWidth <= inset || halfHeight <= inset ||
            times == null || times.Length < 2 || times.Length > 61 || hazards == null || requirements == null ||
            cancellation.IsCancellationRequested) return Array.Empty<Step>();
        // A finite route cannot certify a known later effect or requirement.
        // Callers must extend the horizon rather than accept an early endpoint.
        if (!double.IsFinite(times[^1])) return Array.Empty<Step>();
        foreach (var hazard in hazards)
            if (!double.IsFinite(hazard.Latest) || hazard.Latest > times[^1]) return Array.Empty<Step>();
        foreach (var requirement in requirements)
            if (!double.IsFinite(requirement.Latest) || requirement.Latest > times[^1]) return Array.Empty<Step>();
        if (knockback is { } push && (push.Edge < 0 || push.Edge >= times.Length - 1 ||
            !AlexandriaGeometry.Finite(push.Origin) || !double.IsFinite(push.Distance) || push.Distance <= 0))
            return Array.Empty<Step>();
        double usableX = halfWidth - inset, usableY = halfHeight - inset;
        if (Math.Abs((double)start.X - center.X) > usableX || Math.Abs((double)start.Y - center.Y) > usableY)
            return Array.Empty<Step>();
        double xCount = Math.Floor(usableX / spacing), yCount = Math.Floor(usableY / spacing);
        // Bounded input prevents a malformed arena or tiny grid spacing from
        // producing an unbounded worker allocation on the bot's behalf.
        if (xCount > 1000 || yCount > 1000 || (2 * xCount + 1) * (2 * yCount + 1) > 2000)
            return Array.Empty<Step>();
        var points = new List<Vector2>();
        for (int x = -(int)xCount; x <= (int)xCount; ++x)
        for (int y = -(int)yCount; y <= (int)yCount; ++y)
        {
            var p = new Vector2((float)(center.X + x * spacing), (float)(center.Y + y * spacing));
            if (!AlexandriaGeometry.Finite(p)) return Array.Empty<Step>();
            if (Math.Abs((double)p.X - center.X) > usableX || Math.Abs((double)p.Y - center.Y) > usableY) continue;
            points.Add(p);
        }
        var grid = points.ToArray();
        var allowed = new bool[grid.Length]; Array.Fill(allowed, true);
        var stages = new AlexandriaTemporalPlanner.Stage[times.Length];
        stages[0] = new(times[0], [start], [true]);
        for (int i = 1; i < stages.Length; ++i)
        {
            if (knockback is { } forced && i == forced.Edge + 1)
            {
                var source = stages[i - 1];
                var landings = new Vector2[source.Points.Length];
                var landingAllowed = new bool[source.Points.Length];
                for (int j = 0; j < landings.Length; ++j)
                {
                    if ((j & 63) == 0 && cancellation.IsCancellationRequested) return Array.Empty<Step>();
                    bool valid = AlexandriaPositionConstraints.TryKnockback(source.Points[j], forced.Origin, forced.Distance, out var landing);
                    landings[j] = valid ? landing : source.Points[j];
                    landingAllowed[j] = valid && source.Allowed[j] && Math.Abs((double)landing.X - center.X) <= usableX &&
                        Math.Abs((double)landing.Y - center.Y) <= usableY;
                }
                stages[i] = new(times[i], landings, landingAllowed);
            }
            else stages[i] = new(times[i], grid, allowed);
        }
        var path = AlexandriaTemporalPlanner.Plan(stages, hazards, delay, minimumSpeed, maximumSpeed, requirements, cancellation, knockback, movementBoost);
        if (path.Length != stages.Length) return Array.Empty<Step>();
        var result = new Step[path.Length];
        for (int i = 0; i < path.Length; ++i) result[i] = new(stages[i].Points[path[i]], times[i]);
        return result;
    }
}
