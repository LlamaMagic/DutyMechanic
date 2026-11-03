using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Detached geometry for Alexandria's authored movement. Distances and continuous
/// segment certificates replace endpoint-only safety checks; this type reads no game
/// state and owns no movement. Encounter adapters must supply verified geometry.
/// </summary>
internal static class AlexandriaGeometry
{
    internal enum Kind { Circle, Annulus, Rectangle, Cross, Sector }

    // Local +X is the supplied unit direction; local +Y is its perpendicular.
    // A/B mean radius/unused, inner/outer radius, forward/backward extent,
    // arm length/unused, or radius/half-angle radians respectively.
    // Width is rectangle/cross half-width. Margin is spatial uncertainty, not
    // an increase to nominal mechanic dimensions. Times use one monotonic epoch.
    internal readonly record struct Hazard(Kind Kind, Vector2 Origin, Vector2 Direction,
        double A, double B, double Width, double Margin, double Earliest, double Latest)
    {
        internal bool Valid => Enum.IsDefined(Kind) && Finite(Origin) && Finite(Direction) &&
            Math.Abs(Direction.LengthSquared() - 1) < 1e-5 &&
            double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(Width) &&
            double.IsFinite(Margin) && double.IsFinite(Earliest) && double.IsFinite(Latest) &&
            A > 0 && B >= 0 && Width >= 0 && Margin >= 0 && Earliest <= Latest &&
            (Kind != Kind.Annulus || B > A) &&
            (Kind != Kind.Sector || B > 0 && B <= Math.PI) &&
            (Kind is not (Kind.Rectangle or Kind.Cross) || Width > 0);
    }

    // A request can be delayed, then progress at any speed within these bounds.
    // The certificate covers every position between the resulting extreme progress
    // fractions. It is conservative: correlation between time and speed is not needed.
    internal readonly record struct Travel(double Start, double End, double MaximumDelay,
        double MinimumSpeed, double MaximumSpeed)
    {
        internal bool Valid => double.IsFinite(Start) && double.IsFinite(End) && End >= Start &&
            double.IsFinite(MaximumDelay) && MaximumDelay >= 0 &&
            double.IsFinite(MinimumSpeed) && MinimumSpeed > 0 &&
            double.IsFinite(MaximumSpeed) && MaximumSpeed >= MinimumSpeed;
    }

    internal static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);

    /// <summary>Returns clearance from the nominal hazard; negative values are inside.</summary>
    internal static double Distance(Vector2 point, Hazard h)
    {
        if (!Finite(point) || !h.Valid) return double.NegativeInfinity;
        // Normalize after validating so the transform preserves the Lipschitz bound
        // even with float rounding in a heading-derived direction vector.
        double norm = Math.Sqrt((double)h.Direction.X * h.Direction.X + (double)h.Direction.Y * h.Direction.Y);
        double dx = (double)point.X - h.Origin.X, dy = (double)point.Y - h.Origin.Y;
        double x = (dx * h.Direction.X + dy * h.Direction.Y) / norm;
        double y = (-dx * h.Direction.Y + dy * h.Direction.X) / norm;
        double radius = Math.Sqrt(x * x + y * y);
        switch (h.Kind)
        {
            case Kind.Circle: return radius - h.A;
            case Kind.Annulus: return Math.Max(h.A - radius, radius - h.B);
            case Kind.Rectangle: return Box(x, y, h.A, h.B, h.Width);
            case Kind.Cross:
                // min of two 1-Lipschitz distance functions remains 1-Lipschitz.
                // Inside an overlap it is a conservative field rather than the
                // exact union distance; outside it is the exact distance to union.
                return Math.Min(Box(x, y, h.A, h.A, h.Width), Box(y, x, h.A, h.A, h.Width));
            case Kind.Sector:
                double angle = Math.Abs(Math.Atan2(y, x));
                if (angle <= h.B)
                {
                    double boundary = radius * Math.Sin(Math.Min(Math.PI / 2, h.B - angle));
                    return Math.Max(radius - h.A, -boundary);
                }
                // Nearest point on either finite bounding ray, including the apex
                // and outer corner. This also handles sectors wider than 180 degrees.
                double projection = Math.Clamp(radius * Math.Cos(angle - h.B), 0, h.A);
                return Math.Sqrt(Math.Max(0, radius * radius + projection * projection -
                    2 * radius * projection * Math.Cos(angle - h.B)));
            default: return double.NegativeInfinity;
        }
    }

    private static double Box(double x, double y, double forward, double backward, double halfWidth)
    {
        double qx = Math.Abs(x - (forward - backward) / 2) - (forward + backward) / 2;
        double qy = Math.Abs(y) - halfWidth;
        return Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) +
            Math.Min(Math.Max(qx, qy), 0);
    }

    /// <summary>
    /// Certifies every point on a segment against one spatial hazard. A positive
    /// distance is 1-Lipschitz: midpoint clearance greater than half-length proves
    /// the entire interval safe. Subdivision never accepts an unresolved interval.
    /// </summary>
    internal static bool SegmentSafe(Vector2 from, Vector2 to, Hazard hazard)
    {
        if (!Finite(from) || !Finite(to) || !hazard.Valid) return false;
        // 1mm numeric reserve dominates float coordinate interpolation at these
        // arenas. Maximum depth bounds worker cost; exhausting it rejects a path.
        const double reserve = .001;
        bool Certify(Vector2 a, Vector2 b, int depth)
        {
            if (Distance(a, hazard) <= hazard.Margin + reserve ||
                Distance(b, hazard) <= hazard.Margin + reserve) return false;
            var middle = (a + b) * .5f;
            double halfLength = Vector2.Distance(a, b) / 2.0;
            double clearance = Distance(middle, hazard) - hazard.Margin;
            if (clearance > halfLength + reserve) return true;
            if (clearance <= reserve || depth == 18) return false;
            return Certify(a, middle, depth + 1) && Certify(middle, b, depth + 1);
        }
        return Certify(from, to, 0);
    }

    /// <summary>
    /// Rejects infeasible deadlines and checks the whole possible position interval
    /// during each effect window, including early arrival and delayed departure.
    /// Arena containment is the caller's separate positive-position constraint.
    /// </summary>
    internal static bool TravelSafe(Vector2 from, Vector2 to, Travel travel, Hazard[] hazards)
    {
        if (!Finite(from) || !Finite(to) || !travel.Valid || hazards == null) return false;
        double length = Vector2.Distance(from, to);
        if (length > 0 && travel.MaximumDelay + length / travel.MinimumSpeed > travel.End - travel.Start)
            return false;
        foreach (var hazard in hazards)
        {
            if (!hazard.Valid) return false;
            double first = Math.Max(travel.Start, hazard.Earliest), last = Math.Min(travel.End, hazard.Latest);
            if (first > last) continue;
            float low = length == 0 ? 0 : (float)Math.Clamp(
                (first - travel.Start - travel.MaximumDelay) * travel.MinimumSpeed / length, 0, 1);
            float high = length == 0 ? 0 : (float)Math.Clamp(
                (last - travel.Start) * travel.MaximumSpeed / length, 0, 1);
            if (!SegmentSafe(Vector2.Lerp(from, to, low), Vector2.Lerp(from, to, high), hazard)) return false;
        }
        return true;
    }
}
