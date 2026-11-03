using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Detached Amalgam overlap planner. Converts caller-verified mechanic
/// snapshots into a single timed constraint set before searching the arena;
/// it never reads client objects, issues input or guesses an effect time.
/// </summary>
internal static class AlexandriaAmalgamPlanner
{
    // A spread position is the other target's predicted position, with its
    // movement uncertainty included in the margin by the caller. IsLocalTarget
    // must come from a verified target identity, not party ordering.
    internal readonly record struct Effect(uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    /// <summary>
    /// Plans one route through all supplied effects and positive requirements.
    /// The caller must establish complete relevant evidence for this horizon;
    /// passing false forbids treating an absent effect as a safe arena. A false
    /// result includes both invalid/missing evidence and no certified
    /// finite-grid path. The caller must retain ownership and recover safely;
    /// this method does not turn an empty result into an avoidance fallback.
    /// Optional compression preserves the full temporal constraints but requires
    /// an executor that can follow long direct segments within the same envelope.
    /// </summary>
    internal static bool TryPlan(Vector2 player, Effect[] effects, AlexandriaPositionConstraints.Timed[] requirements,
        double margin, double spacing, double[] times, double delay, double minimumSpeed, double maximumSpeed,
        bool evidenceComplete, out AlexandriaGridPlanner.Step[] route, CancellationToken cancellation = default,
        bool compressRoute = false)
    {
        if (!evidenceComplete) { route = Array.Empty<AlexandriaGridPlanner.Step>(); return false; }
        return TryPlanKnownSnapshot(player, effects, requirements, margin, spacing, times, delay, minimumSpeed,
            maximumSpeed, out route, cancellation, compressRoute);
    }

    // Conditional rolling solve: this certifies only supplied, already observed
    // effects. A live caller must revalidate against newly discovered casts on
    // every pulse; success does not establish future mechanic completeness.
    internal static bool TryPlanKnownSnapshot(Vector2 player, Effect[] effects, AlexandriaPositionConstraints.Timed[] requirements,
        double margin, double spacing, double[] times, double delay, double minimumSpeed, double maximumSpeed,
        out AlexandriaGridPlanner.Step[] route, CancellationToken cancellation = default, bool compressRoute = false)
    {
        route = Array.Empty<AlexandriaGridPlanner.Step>();
        if (effects == null || requirements == null || times == null || times.Length < 2 ||
            !double.IsFinite(times[^1]) || !double.IsFinite(margin) || margin < 0 || cancellation.IsCancellationRequested)
            return false;
        if (!TryCreateHazards(effects, margin, times[^1], out var shapes, cancellation)) return false;
        // The square arena is 40 x 40 centered on (-533,-373). Erode it by
        // the same supplied uncertainty margin used for the damaging shapes.
        route = AlexandriaGridPlanner.Plan(player, new Vector2(-533, -373), 20, 20, margin, spacing,
            times, shapes, requirements, delay, minimumSpeed, maximumSpeed, cancellation);
        if (route.Length == 0) return false;
        // Existing callers may rely on the full staged route. A movement owner
        // must explicitly opt into direct-segment execution after checking its
        // controller's timing and arrival behavior.
        if (!compressRoute) return true;
        // The graph uses half-second stages to find overlap-safe positions.
        // Executing every artificial stage adds needless stops and repeated
        // input handoffs. Drop intermediate stages only when a direct segment
        // is certified against every timed hazard and positive requirement.
        // The arena is convex, so a segment between its eroded grid points
        // remains on the same traversable floor. This changes neither effect
        // lifetimes nor the caller's duty to provide complete evidence.
        int[] previous = new int[route.Length]; Array.Fill(previous, -1);
        int[] segments = new int[route.Length]; Array.Fill(segments, int.MaxValue); segments[0] = 0;
        for (int end = 1; end < route.Length; ++end)
        {
            if (cancellation.IsCancellationRequested) { route = Array.Empty<AlexandriaGridPlanner.Step>(); return false; }
            for (int begin = 0; begin < end; ++begin)
            {
                if (segments[begin] == int.MaxValue || segments[begin] + 1 >= segments[end]) continue;
                var travel = new AlexandriaGeometry.Travel(route[begin].Time, route[end].Time, delay, minimumSpeed, maximumSpeed);
                if (!AlexandriaGeometry.TravelSafe(route[begin].Position, route[end].Position, travel, shapes) ||
                    !AlexandriaPositionConstraints.TravelInside(route[begin].Position, route[end].Position, travel, requirements)) continue;
                previous[end] = begin; segments[end] = segments[begin] + 1;
            }
        }
        if (previous[^1] < 0 || cancellation.IsCancellationRequested)
        { route = Array.Empty<AlexandriaGridPlanner.Step>(); return false; }
        var compressed = new List<AlexandriaGridPlanner.Step>();
        for (int at = route.Length - 1; at > 0; at = previous[at]) compressed.Add(route[at]);
        compressed.Add(route[0]); compressed.Reverse(); route = compressed.ToArray();
        return true;
    }

    // Planning and runtime revalidation must use identical shape construction;
    // otherwise a newly observed overlap could be checked against weaker
    // geometry than the detached worker used. Unknown actions fail closed.
    internal static bool TryCreateHazards(Effect[] effects, double margin, double horizon,
        out AlexandriaGeometry.Hazard[] shapes, CancellationToken cancellation = default)
    {
        shapes = Array.Empty<AlexandriaGeometry.Hazard>();
        if (effects == null || !double.IsFinite(margin) || margin < 0 || !double.IsFinite(horizon) ||
            cancellation.IsCancellationRequested) return false;
        var hazards = new List<AlexandriaGeometry.Hazard>();
        foreach (var effect in effects)
        {
            if (cancellation.IsCancellationRequested || !double.IsFinite(effect.Latest) || effect.Latest > horizon) return false;
            // The parent announces two lanes centered either side of its axis.
            if (effect.Action == 36329)
            {
                if (!AlexandriaMechanicShapes.TryCreateSplit(effect.Origin, effect.Direction, margin,
                    effect.Earliest, effect.Latest, out var lanes)) return false;
                hazards.AddRange(lanes);
                continue;
            }
            if (effect.Action is not (36327 or 36334 or 36336 or 39136 or 39138 or 39254 or 39255 or 39256))
                return false;
            if (!AlexandriaMechanicShapes.TryCreate(effect.Action, effect.Origin, effect.Direction, margin,
                effect.Earliest, effect.Latest, out var hazard)) return false;
            if (effect.Action == 36334 && effect.IsLocalTarget) continue;
            hazards.Add(hazard);
        }
        shapes = hazards.ToArray();
        return true;
    }
}
