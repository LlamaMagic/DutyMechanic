using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Detached encounter geometry. Activation windows, actor identity, path timing,
// and movement ownership are supplied separately; this does not move the bot.
internal static class AlexandriaInterferonGeometry
{
    internal enum Shape { Cross, Donut, Cone }
    internal readonly record struct Hazard(Shape Shape, Vector2 Origin, Vector2 Direction, float ConeCosine,
        double Earliest, double Latest);

    // The caller's due time is reported cast finish plus 0.5 seconds. Keep
    // the earliest modeled impact at cast finish because observed receipts
    // can arrive before the old finish-plus-0.3-second lower bound.
    internal static (double Earliest, double Latest) ConeWindow(double due) => (due - .5, due + .4);

    internal static bool Safe(Vector2 point, double time, Hazard[] hazards)
    {
        if (point.X <= 832.5f || point.X >= 871.5f || point.Y <= 808.5f || point.Y >= 837.5f)
            return false;
        foreach (var hazard in hazards)
        {
            if (time < hazard.Earliest || time > hazard.Latest) continue;
            var delta = point - hazard.Origin;
            switch (hazard.Shape)
            {
                // Preserve a full yalm against the captured arrival shortfall
                // and server position sampling around the damage effect.
                case Shape.Cross when Math.Abs(delta.X) <= 4f || Math.Abs(delta.Y) <= 4f:
                case Shape.Donut when delta.LengthSquared() >= 3f * 3f:
                    return false;
                case Shape.Cone when !ConeSafe(delta, hazard):
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Certifies the complete travel envelope against the same conservative
    /// shapes, rather than sampling a few positions between waypoints. The caller
    /// must provide independently measured speed, delay and effect windows; this
    /// check cannot validate those estimates or the input controller's trajectory.
    /// </summary>
    internal static bool TravelSafe(Vector2 from, Vector2 to, double start, double end,
        double delay, double minimumSpeed, double maximumSpeed, Hazard[] hazards)
    {
        if (!TryConvert(hazards, out var shapes)) return false;
        return TravelSafe(from, to, start, end, delay, minimumSpeed, maximumSpeed, shapes);
    }

    // Conversion occurs once per immutable worker snapshot. The runtime can
    // also use the convenience overload when checking a newly changed snapshot.
    internal static bool TryConvert(Hazard[] hazards, out AlexandriaGeometry.Hazard[] shapes)
    {
        shapes = Array.Empty<AlexandriaGeometry.Hazard>();
        if (hazards == null) return false;
        var result = new AlexandriaGeometry.Hazard[hazards.Length];
        for (int i = 0; i < hazards.Length; ++i)
        {
            var h = hazards[i];
            if (!AlexandriaGeometry.Finite(h.Origin) || h.Origin.X < 832.5f || h.Origin.X > 871.5f ||
                h.Origin.Y < 808.5f || h.Origin.Y > 837.5f) return false;
            // A 200-yalm outer extent covers the entire bounded arena even
            // from a corner. It preserves the legacy infinite-arm/ring model.
            result[i] = h.Shape switch
            {
                Shape.Cross => new(AlexandriaGeometry.Kind.Cross, h.Origin, Vector2.UnitX, 200, 0, 4, 0, h.Earliest, h.Latest),
                Shape.Donut => new(AlexandriaGeometry.Kind.Annulus, h.Origin, Vector2.UnitX, 3, 200, 0, 0, h.Earliest, h.Latest),
                Shape.Cone when float.IsFinite(h.ConeCosine) && Math.Abs(h.ConeCosine) <= 1 =>
                    new(AlexandriaGeometry.Kind.Sector, h.Origin, h.Direction, 200, Math.Acos(h.ConeCosine), 0, 1, h.Earliest, h.Latest),
                _ => default
            };
            if (!result[i].Valid) return false;
        }
        shapes = result;
        return true;
    }

    internal static bool TravelSafe(Vector2 from, Vector2 to, double start, double end,
        double delay, double minimumSpeed, double maximumSpeed, AlexandriaGeometry.Hazard[] shapes)
    {
        if (shapes == null || !AlexandriaGeometry.Finite(from) || !AlexandriaGeometry.Finite(to) ||
            from.X <= 832.5f || from.X >= 871.5f || from.Y <= 808.5f || from.Y >= 837.5f ||
            to.X <= 832.5f || to.X >= 871.5f || to.Y <= 808.5f || to.Y >= 837.5f) return false;
        return AlexandriaGeometry.TravelSafe(from, to, new(start, end, delay, minimumSpeed, maximumSpeed), shapes);
    }

    /// <summary>
    /// Certifies the current waypoint from the observed position. Once inside
    /// the owner's arrival tolerance it holds the actual position, so a nominal
    /// restart delay cannot invalidate a nearly completed zero-distance move.
    /// The next segment must start at the returned actual or planned position.
    /// </summary>
    internal static bool CurrentWaypointSafe(Vector2 player, Vector2 waypoint, double start, double end,
        double delay, double minimumSpeed, double maximumSpeed, double arrivalTolerance,
        AlexandriaGeometry.Hazard[] shapes, out Vector2 nextStart)
    {
        nextStart = default;
        if (!double.IsFinite(arrivalTolerance) || arrivalTolerance < 0 ||
            !AlexandriaGeometry.Finite(player) || !AlexandriaGeometry.Finite(waypoint)) return false;
        bool arrived = Vector2.Distance(player, waypoint) <= arrivalTolerance;
        if (arrived && TravelSafe(player, player, start, end, 0, minimumSpeed, maximumSpeed, shapes))
        { nextStart = player; return true; }
        // Within the nominal arrival tolerance, holding the observed position
        // can still become unsafe even though the planned waypoint remains
        // viable. Attempt the remaining short move only if its entire envelope
        // is certified; callers must actually issue it rather than hold.
        nextStart = waypoint;
        return TravelSafe(player, waypoint, start, end, delay, minimumSpeed, maximumSpeed, shapes);
    }

    private static bool ConeSafe(Vector2 delta, Hazard hazard)
    {
        if (delta.LengthSquared() < 1) return false;
        double angle = Math.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(delta), hazard.Direction), -1, 1));
        double edge = Math.Acos(hazard.ConeCosine);
        // Angular clearance alone collapses near the caster. Require one yalm
        // beyond the cone edge, including at the short first-boss staging points.
        return angle > edge && Math.Sqrt(delta.LengthSquared()) * Math.Sin(Math.Min(Math.PI / 2, angle - edge)) >= 1;
    }

    // A 120-degree unsafe cone is a convex wedge. Clip the segment against
    // its two half-planes; no sampling gap may skip a short intersection.
    // The safe region outside a 240-degree cone is itself a convex wedge,
    // so safe endpoints already establish segment safety in this arena.
    internal static bool CrossesCone(Vector2 from, Vector2 to, Hazard hazard)
    {
        if (hazard.ConeCosine <= 0) return false;
        if (hazard.ConeCosine > 1 || Math.Abs(hazard.Direction.LengthSquared() - 1) > .01f) return true;
        double tangent = Math.Sqrt(1 - hazard.ConeCosine * hazard.ConeCosine) / hazard.ConeCosine;
        var p = from - hazard.Origin; var q = to - hazard.Origin;
        double Along(Vector2 v) => Vector2.Dot(v, hazard.Direction);
        double Side(Vector2 v) => hazard.Direction.X * v.Y - hazard.Direction.Y * v.X;
        double lo = 0, hi = 1;
        bool Clip(double start, double finish)
        {
            if (start < 0 && finish < 0) return false;
            if (start >= 0 && finish >= 0) return true;
            double crossing = -start / (finish - start);
            if (start < 0) lo = Math.Max(lo, crossing); else hi = Math.Min(hi, crossing);
            return lo <= hi;
        }
        double clearance = Math.Sqrt(1 + tangent * tangent);
        return Clip(Along(p) * tangent + Side(p) + clearance, Along(q) * tangent + Side(q) + clearance) &&
            Clip(Along(p) * tangent - Side(p) + clearance, Along(q) * tangent - Side(q) + clearance);
    }
}
