using System;
using System.Collections.Generic;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Pure time-window geometry for the trial planner. A fan can become dangerous
// partway through a movement segment, so checking only the endpoints is unsafe.
// Times are relative to the same captured bot-thread snapshot; callers must
// supply evidence-backed conservative windows rather than inferred hit times.
internal static class CastrumFluminisTimedGeometry
{
    internal readonly record struct Circle(Vector2 Center, float Radius, double Start, double End);
    internal readonly record struct Cone(Vector2 Origin, float Heading, float Radius, float HalfAngle,
        double Start, double End);
    internal readonly record struct Donut(Vector2 Center, float InnerRadius, float OuterRadius,
        double Start, double End);

    // An active ring has two disconnected safe regions. Clip by time first,
    // then prove the whole segment stays inside the pocket or outside the ring.
    internal static bool DonutSegmentSafe(Vector2 from, Vector2 to, double start, double end, Donut donut)
    {
        if (!Finite(from) || !Finite(to) || !Finite(donut.Center) || !double.IsFinite(start) ||
            !double.IsFinite(end) || end <= start || !float.IsFinite(donut.InnerRadius) ||
            !float.IsFinite(donut.OuterRadius) || donut.InnerRadius <= 0 || donut.OuterRadius <= donut.InnerRadius ||
            !double.IsFinite(donut.Start) || !double.IsFinite(donut.End) || donut.End < donut.Start) return false;
        double first = Math.Max(0, (donut.Start - start) / (end - start));
        double last = Math.Min(1, (donut.End - start) / (end - start));
        if (first > last) return true;
        var a = Vector2.Lerp(from, to, (float)first);
        var b = Vector2.Lerp(from, to, (float)last);
        float innerSquared = donut.InnerRadius * donut.InnerRadius;
        if (Vector2.DistanceSquared(a, donut.Center) < innerSquared &&
            Vector2.DistanceSquared(b, donut.Center) < innerSquared) return true;
        var delta = b - a;
        double lengthSquared = delta.LengthSquared();
        double closest = lengthSquared == 0 ? 0 : Math.Clamp(Vector2.Dot(donut.Center - a, delta) / lengthSquared, 0, 1);
        return Vector2.DistanceSquared(a + delta * (float)closest, donut.Center) > donut.OuterRadius * donut.OuterRadius;
    }

    // Measures the smallest spatial margin over timed circle intersections.
    // It is diagnostic only: cones, player hitbox and execution delay are not
    // included, so callers must never treat this value as full route clearance.
    internal static double CircleClearance(Vector2 from, Vector2 to, double start, double end,
        IReadOnlyList<Circle> circles)
    {
        if (!Finite(from) || !Finite(to) || !double.IsFinite(start) || !double.IsFinite(end) ||
            end <= start || circles == null) return double.NaN;
        double minimum = double.PositiveInfinity;
        Vector2 delta = to - from;
        double lengthSquared = delta.LengthSquared();
        foreach (var c in circles)
        {
            if (!Finite(c.Center) || !float.IsFinite(c.Radius) || c.Radius <= 0 ||
                !double.IsFinite(c.Start) || !double.IsFinite(c.End) || c.End < c.Start) return double.NaN;
            double first = Math.Max(0, (c.Start - start) / (end - start));
            double last = Math.Min(1, (c.End - start) / (end - start));
            if (first > last) continue;
            double closest = lengthSquared == 0 ? first : Math.Clamp(
                Vector2.Dot(c.Center - from, delta) / lengthSquared, first, last);
            minimum = Math.Min(minimum, Vector2.Distance(from + delta * (float)closest, c.Center) - c.Radius);
        }
        return minimum;
    }

    // Sample the time-clipped segment with an inflated shape: the maximum
    // distance to the nearest sample is half the sampling interval. Inflating
    // both radial and angular boundaries by that amount makes missed crossings
    // conservative rather than depending on endpoint or server-frame timing.
    internal static bool ConeSegmentSafe(Vector2 from, Vector2 to, double start, double end, Cone cone)
    {
        if (!Finite(from) || !Finite(to) || !Finite(cone.Origin) || !float.IsFinite(cone.Heading) ||
            !float.IsFinite(cone.Radius) || cone.Radius <= 0 || !float.IsFinite(cone.HalfAngle) ||
            cone.HalfAngle < 0 || cone.HalfAngle > MathF.PI || !double.IsFinite(start) ||
            !double.IsFinite(end) || end <= start || !double.IsFinite(cone.Start) ||
            !double.IsFinite(cone.End) || cone.End < cone.Start) return false;
        double first = Math.Max(0, (cone.Start - start) / (end - start));
        double last = Math.Min(1, (cone.End - start) / (end - start));
        if (first > last) return true;
        Vector2 a = Vector2.Lerp(from, to, (float)first), b = Vector2.Lerp(from, to, (float)last);
        int intervals = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(a, b) / 0.1f));
        float margin = Vector2.Distance(a, b) / (2 * intervals) + 0.001f;
        var direction = new Vector2(MathF.Sin(cone.Heading), MathF.Cos(cone.Heading));
        for (int i = 0; i <= intervals; i++)
        {
            Vector2 offset = Vector2.Lerp(a, b, (float)i / intervals) - cone.Origin;
            float distance = offset.Length();
            if (distance > cone.Radius + margin) continue;
            if (distance <= margin) return false;
            double expansion = Math.Asin(Math.Min(1, margin / distance));
            double angle = Math.Acos(Math.Clamp(Vector2.Dot(offset, direction) / distance, -1, 1));
            if (angle <= cone.HalfAngle + expansion) return false;
        }
        return true;
    }

    internal static bool SegmentSafe(Vector2 from, Vector2 to, double start, double end,
        IReadOnlyList<Circle> circles, Vector2 arena, float arenaRadius)
        => SegmentSafe(from, to, start, end, circles, arena, arenaRadius, out _);

    internal static bool SegmentSafe(Vector2 from, Vector2 to, double start, double end,
        IReadOnlyList<Circle> circles, Vector2 arena, float arenaRadius, out double clearance)
    {
        clearance = double.PositiveInfinity;
        if (!Finite(from) || !Finite(to) || !Finite(arena) || !double.IsFinite(start) ||
            !double.IsFinite(end) || end <= start || !float.IsFinite(arenaRadius) || arenaRadius <= 0 ||
            circles == null ||
            Vector2.Distance(from, arena) > arenaRadius || Vector2.Distance(to, arena) > arenaRadius)
            return false;

        // A circle is convex: along a straight segment the farthest point from
        // the arena center is an endpoint. Include the wall in the same margin
        // used to rank paths, so avoiding a fan does not favor the arena edge.
        clearance = arenaRadius - Math.Max(Vector2.Distance(from, arena), Vector2.Distance(to, arena));

        Vector2 delta = to - from;
        double lengthSquared = delta.LengthSquared();
        foreach (var circle in circles)
        {
            if (!Finite(circle.Center) || !float.IsFinite(circle.Radius) || circle.Radius <= 0 ||
                !double.IsFinite(circle.Start) || !double.IsFinite(circle.End) || circle.End < circle.Start)
                return false;
            double first = Math.Max(0, (circle.Start - start) / (end - start));
            double last = Math.Min(1, (circle.End - start) / (end - start));
            if (first > last) continue;
            double closest = lengthSquared == 0 ? first : Math.Clamp(
                Vector2.Dot(circle.Center - from, delta) / lengthSquared, first, last);
            Vector2 point = from + delta * (float)closest;
            // Boundary contact is unsafe: rounded capture coordinates and player
            // movement tolerance cannot establish positive clearance at equality.
            float distanceSquared = Vector2.DistanceSquared(point, circle.Center);
            if (distanceSquared <= circle.Radius * circle.Radius)
                return false;
            clearance = Math.Min(clearance, Math.Sqrt(distanceSquared) - circle.Radius);
        }
        return true;
    }

    private static bool Finite(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
