using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Pure handoff check for a detached Amalgam route. It never reads game objects
// or owns input: the caller supplies one complete, current absolute-time hazard
// snapshot and remains responsible for generation, lifecycle and movement.
internal static class AlexandriaAmalgamRoute
{
    internal enum Decision { Invalid, Complete, Hold, Move }

    // A completed worker cannot authorize its stale position or hazard snapshot.
    // Recheck the actual player-to-waypoint envelope and every remaining edge;
    // holding inside the nominal arrival tolerance is allowed only when both
    // the actual position and its next edge remain safe. The arena is convex.
    internal static Decision Evaluate(Vector2 player, double now, double routeStart,
        AlexandriaGridPlanner.Step[] route, ref int step, AlexandriaGeometry.Hazard[] hazards,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double delay,
        double minimumSpeed, double maximumSpeed, double tolerance)
    {
        bool InArena(Vector2 point) => AlexandriaGeometry.Finite(point) &&
            Math.Abs(point.X + 533) < 20 - margin && Math.Abs(point.Y + 373) < 20 - margin;
        if (!double.IsFinite(now) || !double.IsFinite(routeStart) || !double.IsFinite(margin) || margin < 0 || margin >= 20 ||
            !double.IsFinite(tolerance) || tolerance < 0 || !InArena(player) || route == null || route.Length < 2 ||
            hazards == null || requirements == null || step < 1 || step > route.Length)
            return Decision.Invalid;
        for (int i = 0; i < route.Length; ++i)
            if (!InArena(route[i].Position) || !double.IsFinite(route[i].Time) ||
                (i > 0 && route[i].Time <= route[i - 1].Time)) return Decision.Invalid;
        double horizon = routeStart + route[^1].Time;
        double plannedStart = routeStart + route[0].Time;
        if (!double.IsFinite(horizon) || !double.IsFinite(plannedStart) || now < routeStart) return Decision.Invalid;
        foreach (var hazard in hazards)
            if (!hazard.Valid || hazard.Latest > horizon) return Decision.Invalid;
        foreach (var requirement in requirements)
            if (!double.IsFinite(requirement.Latest) || requirement.Latest > horizon) return Decision.Invalid;

        int current = step;
        while (current < route.Length && now >= routeStart + route[current].Time)
        {
            if (Vector2.Distance(player, route[current].Position) > tolerance) return Decision.Invalid;
            ++current;
        }
        if (current >= route.Length) { step = current; return Decision.Complete; }

        bool Safe(Vector2 from, Vector2 to, double begin, double end, double startDelay)
        {
            var travel = new AlexandriaGeometry.Travel(begin, end, startDelay, minimumSpeed, maximumSpeed);
            return AlexandriaGeometry.TravelSafe(from, to, travel, hazards) &&
                AlexandriaPositionConstraints.TravelInside(from, to, travel, requirements);
        }

        bool preparing = now < plannedStart;
        if (preparing && (Vector2.Distance(player, route[0].Position) > tolerance ||
            !Safe(player, player, now, plannedStart, 0))) return Decision.Invalid;
        double executionStart = preparing ? plannedStart : now;
        // A reached intermediate stage need not impose a stop until its exact
        // deadline. Depart early only when the entire observed-position-to-next
        // envelope remains valid; remaining edges are checked below as usual.
        while (current + 1 < route.Length && Vector2.Distance(player, route[current].Position) <= tolerance &&
            Safe(player, route[current + 1].Position, executionStart, routeStart + route[current + 1].Time, delay))
            ++current;

        var waypoint = route[current];
        double due = routeStart + waypoint.Time;
        bool near = Vector2.Distance(player, waypoint.Position) <= tolerance;
        bool hold = near && Safe(player, player, executionStart, due, 0);
        Vector2 nextStart = hold ? player : waypoint.Position;
        if (!hold && !Safe(player, waypoint.Position, executionStart, due, delay)) return Decision.Invalid;
        for (int i = current + 1; i < route.Length; ++i)
        {
            var from = i == current + 1 ? nextStart : route[i - 1].Position;
            if (!Safe(from, route[i].Position, routeStart + route[i - 1].Time,
                routeStart + route[i].Time, delay)) return Decision.Invalid;
        }
        step = current;
        return preparing || hold ? Decision.Hold : Decision.Move;
    }
}
