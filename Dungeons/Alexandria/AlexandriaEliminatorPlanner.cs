using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Detached Eliminator overlap planner. Its caller supplies verified helper
/// geometry, target identities, effect windows and positive-position needs;
/// this class does not access the client or own movement.
/// </summary>
internal static class AlexandriaEliminatorPlanner
{
    internal readonly record struct Effect(uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    internal static bool TryPlan(Vector2 player, Effect[] effects, AlexandriaPositionConstraints.Timed[] requirements,
        double margin, double spacing, double[] times, double delay, double minimumSpeed, double maximumSpeed,
        bool evidenceComplete, out AlexandriaGridPlanner.Step[] route, CancellationToken cancellation = default,
        AlexandriaTemporalPlanner.RadialKnockback? knockback = null)
    {
        route = Array.Empty<AlexandriaGridPlanner.Step>();
        if (!evidenceComplete || effects == null || requirements == null || times == null || times.Length < 2 ||
            !double.IsFinite(times[^1]) || !double.IsFinite(margin) || margin < 0 || cancellation.IsCancellationRequested)
            return false;
        var hazards = new List<AlexandriaGeometry.Hazard>();
        bool sawKnockback = false;
        foreach (var effect in effects)
        {
            if (!double.IsFinite(effect.Latest) || effect.Latest > times[^1]) return false;
            if (effect.Action == 36794)
            {
                // Compression is a 15-yalm radial push, not ordinary walking.
                // Require an explicit paired edge inside the caller-supplied
                // effect window and reject duplicates or an unknown origin.
                if (sawKnockback || knockback is not { } push || times == null || push.Edge < 0 ||
                    push.Edge >= times.Length - 1 || !double.IsFinite(effect.Earliest) || !double.IsFinite(effect.Latest) ||
                    effect.Earliest > times[push.Edge] || effect.Latest < times[push.Edge + 1] ||
                    !AlexandriaGeometry.Finite(effect.Origin) || Vector2.Distance(effect.Origin, push.Origin) > .01f ||
                    Math.Abs(push.Distance - 15) > .001 || cancellation.IsCancellationRequested) return false;
                sawKnockback = true;
                continue;
            }
            if (cancellation.IsCancellationRequested || effect.Action is not
                (39007 or 39008 or 39238 or 39249 or 39243 or 39615 or 39616 or 36793 or 39239))
                return false;
            // Validate even a local spread: a malformed target snapshot must
            // not turn into apparent evidence of a clear arena.
            if (!AlexandriaMechanicShapes.TryCreate(effect.Action, effect.Origin, effect.Direction, margin,
                effect.Earliest, effect.Latest, out var hazard)) return false;
            if (effect.Action == 39243 && effect.IsLocalTarget) continue;
            hazards.Add(hazard);
        }
        if (knockback.HasValue != sawKnockback) return false;
        // The arena is a 30-yalm square. The inset must cover the caller's
        // validated position and arena-boundary uncertainty.
        route = AlexandriaGridPlanner.Plan(player, new Vector2(-759, -648), 15, 15, margin, spacing,
            times, hazards.ToArray(), requirements, delay, minimumSpeed, maximumSpeed, cancellation, knockback);
        return route.Length != 0;
    }
}
