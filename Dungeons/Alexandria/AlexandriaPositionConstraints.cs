using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Detached positive-position checks for Alexandria. Unlike unsafe-area geometry,
/// stacks require entering a region and knockbacks require testing the landing.
/// Callers own actor identity, timing, participant state and movement.
/// </summary>
internal static class AlexandriaPositionConstraints
{
    internal enum Kind { Circles, LineStack }

    // The centers and axis are immutable scalar snapshots for this entire
    // interval. Moving party members require a conservative envelope or replan.
    internal readonly record struct Timed(Kind Kind, Vector2[] Centers, Vector2 Source, Vector2 Carrier,
        double RadiusOrLength, double HalfWidth, double Margin, double Earliest, double Latest);

    /// <summary>
    /// Certifies all possible progress along a straight movement during every
    /// overlapping positive-position window. This must be combined with hazard,
    /// arena, deadline and ownership checks; it makes no moving-target prediction.
    /// </summary>
    internal static bool TravelInside(Vector2 from, Vector2 to, AlexandriaGeometry.Travel travel, Timed[] requirements)
    {
        if (!AlexandriaGeometry.Finite(from) || !AlexandriaGeometry.Finite(to) || !travel.Valid || requirements == null)
            return false;
        double length = Vector2.Distance(from, to);
        if (!double.IsFinite(length) || (length > 0 && travel.MaximumDelay + length / travel.MinimumSpeed > travel.End - travel.Start))
            return false;
        foreach (var requirement in requirements)
        {
            if (!Enum.IsDefined(requirement.Kind) || !double.IsFinite(requirement.Earliest) ||
                !double.IsFinite(requirement.Latest) || requirement.Earliest > requirement.Latest ||
                !double.IsFinite(requirement.RadiusOrLength) || !double.IsFinite(requirement.HalfWidth) ||
                !double.IsFinite(requirement.Margin) || requirement.Margin < 0) return false;
            // Validate even requirements outside this edge: malformed inputs must
            // not silently become acceptable when their time window shifts.
            bool Geometry(Vector2 a, Vector2 b) => requirement.Kind switch
            {
                Kind.Circles => InsideCircles(a, b, requirement.Centers, requirement.RadiusOrLength, requirement.Margin),
                Kind.LineStack => InsideLineStack(a, b, requirement.Source, requirement.Carrier,
                    requirement.RadiusOrLength, requirement.HalfWidth, requirement.Margin),
                _ => false
            };
            // A valid geometry need not contain the current position, so test
            // its structural inputs independently before temporal clipping.
            if (requirement.Kind == Kind.Circles && (requirement.Centers == null || requirement.Centers.Length == 0 ||
                requirement.RadiusOrLength <= requirement.Margin || Array.Exists(requirement.Centers, p => !AlexandriaGeometry.Finite(p)))) return false;
            if (requirement.Kind == Kind.LineStack && (!AlexandriaGeometry.Finite(requirement.Source) ||
                !AlexandriaGeometry.Finite(requirement.Carrier) || requirement.RadiusOrLength <= 2 * requirement.Margin ||
                requirement.HalfWidth <= requirement.Margin || Vector2.Distance(requirement.Source, requirement.Carrier) < .001f)) return false;
            double first = Math.Max(travel.Start, requirement.Earliest), last = Math.Min(travel.End, requirement.Latest);
            if (first > last) continue;
            float low = length == 0 ? 0 : (float)Math.Clamp((first - travel.Start - travel.MaximumDelay) * travel.MinimumSpeed / length, 0, 1);
            float high = length == 0 ? 0 : (float)Math.Clamp((last - travel.Start) * travel.MaximumSpeed / length, 0, 1);
            if (!Geometry(Vector2.Lerp(from, to, low), Vector2.Lerp(from, to, high))) return false;
        }
        return true;
    }

    /// <summary>
    /// Certifies an entire straight segment inside the intersection of participant
    /// circles. Circle interiors are convex, so checking both endpoints suffices.
    /// Participant centers must remain fixed over the checked interval; moving
    /// actors require an appropriately conservative envelope or fresh plan.
    /// </summary>
    internal static bool InsideCircles(Vector2 from, Vector2 to, Vector2[] centers, double radius, double margin)
    {
        if (!AlexandriaGeometry.Finite(from) || !AlexandriaGeometry.Finite(to) || centers == null || centers.Length == 0 ||
            !double.IsFinite(radius) || !double.IsFinite(margin) || margin < 0 || radius <= margin) return false;
        double allowed = radius - margin;
        foreach (var center in centers)
        {
            if (!AlexandriaGeometry.Finite(center) || Vector2.Distance(from, center) > allowed ||
                Vector2.Distance(to, center) > allowed) return false;
        }
        return true;
    }

    /// <summary>
    /// Tests a line-stack segment inside an eroded source-to-carrier rectangle.
    /// Its convexity proves the path between endpoints remains in the region.
    /// A zero-length axis is ambiguous and must never become an arbitrary heading.
    /// </summary>
    internal static bool InsideLineStack(Vector2 from, Vector2 to, Vector2 source, Vector2 carrier,
        double length, double halfWidth, double margin)
    {
        if (!AlexandriaGeometry.Finite(from) || !AlexandriaGeometry.Finite(to) ||
            !AlexandriaGeometry.Finite(source) || !AlexandriaGeometry.Finite(carrier) ||
            !double.IsFinite(length) || !double.IsFinite(halfWidth) || !double.IsFinite(margin) ||
            margin < 0 || length <= 2 * margin || halfWidth <= margin) return false;
        double dx = (double)carrier.X - source.X, dy = (double)carrier.Y - source.Y;
        double norm = Math.Sqrt(dx * dx + dy * dy);
        if (norm < .001) return false;
        bool Inside(Vector2 p)
        {
            double x = (double)p.X - source.X, y = (double)p.Y - source.Y;
            double along = (x * dx + y * dy) / norm, across = Math.Abs(-x * dy + y * dx) / norm;
            return along >= margin && along <= length - margin && across <= halfWidth - margin;
        }
        return Inside(from) && Inside(to);
    }

    /// <summary>
    /// Calculates radial knockback landing in world coordinates. An origin at the
    /// actor supplies no direction, so it fails closed rather than guessing one.
    /// The caller must certify the landing, displacement path if relevant, and
    /// subsequent mechanics against the actual arena and timed hazards.
    /// </summary>
    internal static bool TryKnockback(Vector2 position, Vector2 origin, double distance, out Vector2 landing)
    {
        landing = default;
        if (!AlexandriaGeometry.Finite(position) || !AlexandriaGeometry.Finite(origin) ||
            !double.IsFinite(distance) || distance < 0) return false;
        double dx = (double)position.X - origin.X, dy = (double)position.Y - origin.Y;
        double norm = Math.Sqrt(dx * dx + dy * dy);
        if (norm < .001) return false;
        double x = position.X + distance * dx / norm, y = position.Y + distance * dy / norm;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x > float.MaxValue || x < -float.MaxValue ||
            y > float.MaxValue || y < -float.MaxValue) return false;
        landing = new Vector2((float)x, (float)y);
        return true;
    }
}
