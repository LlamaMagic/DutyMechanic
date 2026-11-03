using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Compression pushes 15 yalms from its helper. Choose a walking point whose
// pre-push position, entire push segment, and landing are safe for the
// observed impact window; never command movement during the forced motion.
internal static class AlexandriaEliminatorKnockback
{
    internal readonly record struct Destination(Vector2 Before, Vector2 Landing);

    internal static Destination? Choose(Vector2 player, Vector2 origin, Vector2? previous,
        AlexandriaGeometry.Hazard[] shapes, double now, double earliest, double latest)
    {
        if (!AlexandriaGeometry.Finite(player) || !AlexandriaGeometry.Finite(origin) || shapes == null ||
            !double.IsFinite(now) || !double.IsFinite(earliest) || !double.IsFinite(latest) ||
            earliest <= now || latest < earliest) return null;
        bool Inside(Vector2 p) => AlexandriaGeometry.Finite(p) && Math.Abs(p.X + 759) < 14.5 && Math.Abs(p.Y + 648) < 14.5;
        bool Safe(Vector2 p, out Vector2 landing)
        {
            landing = default;
            if (!Inside(p) || !AlexandriaPositionConstraints.TryKnockback(p, origin, 15, out landing) || !Inside(landing) ||
                !AlexandriaGeometry.TravelSafe(player, p, new(now, earliest, .1, 5, 10), shapes) ||
                !AlexandriaGeometry.TravelSafe(p, p, new(earliest, latest, 0, 5, 10), shapes) ||
                !AlexandriaGeometry.TravelSafe(landing, landing, new(earliest, latest, 0, 5, 10), shapes)) return false;
            foreach (var shape in shapes)
                if (shape.Earliest <= latest && shape.Latest >= earliest && !AlexandriaGeometry.SegmentSafe(p, landing, shape)) return false;
            return true;
        }
        if (!Inside(player)) return null;
        if (previous is { } old && Safe(old, out var retained)) return new(old, retained);
        if (Safe(player, out var current)) return new(player, current);
        Destination? best = null;
        float distance = float.PositiveInfinity;
        for (int x = -14; x <= 14; ++x)
        for (int z = -14; z <= 14; ++z)
        {
            var p = new Vector2(-759 + x, -648 + z);
            float next = Vector2.DistanceSquared(player, p);
            if (next >= distance || !Safe(p, out var landing)) continue;
            best = new(p, landing); distance = next;
        }
        return best;
    }
}
