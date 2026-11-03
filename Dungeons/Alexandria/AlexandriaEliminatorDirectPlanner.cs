using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Choose one reachable point against all currently observed overlapping
// shapes. The caller rechecks it each pulse and retains responsibility for
// party targets and knockback, which are not ordinary walking hazards.
internal static class AlexandriaEliminatorDirectPlanner
{
    internal static Vector2? Choose(Vector2 player, Vector2? previous, AlexandriaGeometry.Hazard[] shapes,
        double now, double horizon, double minimumSpeed = 5, double maximumSpeed = 8,
        Func<Vector2, bool> additional = null)
    {
        if (!AlexandriaGeometry.Finite(player) || shapes == null || shapes.Length == 0 && additional == null ||
            !double.IsFinite(now) || !double.IsFinite(horizon) || horizon <= now) return null;
        bool InArena(Vector2 p) => AlexandriaGeometry.Finite(p) && Math.Abs(p.X + 759) < 14.5 && Math.Abs(p.Y + 648) < 14.5;
        bool Safe(Vector2 p) => InArena(p) && (additional == null || additional(p)) && AlexandriaGeometry.TravelSafe(player, p,
            new(now, horizon, .1, minimumSpeed, maximumSpeed), shapes);
        if (!InArena(player)) return null;
        if (previous is { } retained && Safe(retained)) return retained;
        if (Safe(player)) return player;
        Vector2? best = null;
        float distance = float.PositiveInfinity;
        for (int x = -14; x <= 14; ++x)
        for (int z = -14; z <= 14; ++z)
        {
            var p = new Vector2(-759 + x, -648 + z);
            float next = Vector2.DistanceSquared(player, p);
            if (next >= distance || !Safe(p)) continue;
            best = p; distance = next;
        }
        return best;
    }
}
