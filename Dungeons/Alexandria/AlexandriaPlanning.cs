using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System;

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

/// <summary>
/// Constructs detached damaging geometry from verified Alexandria helper actions.
/// Detection, actor identity, actual origin and orientation, timing uncertainty,
/// positive-position mechanics and lifecycle remain the encounter adapter's job.
/// Unknown actions fail closed rather than inventing geometry from an omen.
/// </summary>
internal static class AlexandriaMechanicShapes
{
    /// <summary>
    /// Constructs the two side lanes announced by Split Current's parent cast.
    /// Their centers lie 17.5 yalms to either side of the verified cast axis;
    /// each lane extends 45 yalms both forward and back with 12.5 half-width.
    /// The resulting nominal ten-yalm center corridor is eroded by the supplied
    /// margin. Resolution timing and axis identity remain caller evidence.
    /// </summary>
    internal static bool TryCreateSplit(Vector2 origin, Vector2 direction, double margin,
        double earliest, double latest, out AlexandriaGeometry.Hazard[] hazards)
    {
        hazards = Array.Empty<AlexandriaGeometry.Hazard>();
        if (!AlexandriaGeometry.Finite(origin) || !AlexandriaGeometry.Finite(direction) ||
            Math.Abs(direction.LengthSquared() - 1) >= 1e-5 || !double.IsFinite(margin) || margin < 0 ||
            !double.IsFinite(earliest) || !double.IsFinite(latest) || earliest > latest) return false;
        // Perpendicular sign merely swaps the pair; the union is unchanged.
        var offset = new Vector2(-direction.Y, direction.X) * 17.5f;
        var first = new AlexandriaGeometry.Hazard(AlexandriaGeometry.Kind.Rectangle, origin + offset,
            direction, 45, 45, 12.5, margin, earliest, latest);
        var second = first with { Origin = origin - offset };
        if (!first.Valid || !second.Valid) return false;
        hazards = [first, second];
        return true;
    }

    /// <summary>
    /// Returns the nominal shape expanded by the caller's uncertainty margin.
    /// Direction is an already verified unit vector in the arena's X/Z plane.
    /// Effect windows must cover receipt and snapshot uncertainty, not merely
    /// cast finish. Shape recognition alone is not evidence that an event fired.
    /// </summary>
    internal static bool TryCreate(uint action, Vector2 origin, Vector2 direction,
        double margin, double earliest, double latest, out AlexandriaGeometry.Hazard hazard)
    {
        // Dimensions describe damage regions, not visual spell names. All
        // rectangles are source-forward with their stated half-width; ring
        // radii are inner/outer in yalms. Wider cone angles remain unresolved.
        hazard = action switch
        {
            36382 => new(AlexandriaGeometry.Kind.Annulus, origin, direction, 4, 40, 0, margin, earliest, latest),
            36383 => new(AlexandriaGeometry.Kind.Cross, origin, direction, 40, 0, 3, margin, earliest, latest),
            // The 2026-11-03 capture hit Arelia less than one yalm behind
            // the sampled boss origin. Reserve both directions across the
            // arena until the cast's source/heading convention is verified.
            36327 => new(AlexandriaGeometry.Kind.Rectangle, origin, direction, 90, 90, 7.5, margin, earliest, latest),
            // Only other actors' spread circles are negative constraints; the
            // local actor's marker does not make its own position unsafe.
            36334 => new(AlexandriaGeometry.Kind.Circle, origin, direction, 6, 0, 0, margin, earliest, latest),
            // Electray uses the same six-yalm spread radius. As with Static
            // Spark, a caller must exclude its own target and bound movement
            // of every other marked actor before using the fixed position.
            39243 => new(AlexandriaGeometry.Kind.Circle, origin, direction, 6, 0, 0, margin, earliest, latest),
            36336 => new(AlexandriaGeometry.Kind.Circle, origin, direction, 6, 0, 0, margin, earliest, latest),
            // A diagonal helper at an arena corner projects toward the center;
            // clipping this nominal rectangle to the square yields a half-arena
            // triangle. The caller must validate that actor's actual axis.
            39136 => new(AlexandriaGeometry.Kind.Rectangle, origin, direction, 28.2843, 0, 28.2843, margin, earliest, latest),
            39138 => new(AlexandriaGeometry.Kind.Rectangle, origin, direction, 55, 0, 4, margin, earliest, latest),
            39254 => new(AlexandriaGeometry.Kind.Circle, origin, direction, 10, 0, 0, margin, earliest, latest),
            39255 => new(AlexandriaGeometry.Kind.Annulus, origin, direction, 10, 20, 0, margin, earliest, latest),
            39256 => new(AlexandriaGeometry.Kind.Annulus, origin, direction, 20, 30, 0, margin, earliest, latest),
            39007 or 39008 or 39238 or 39249 => new(AlexandriaGeometry.Kind.Sector, origin, direction, 40, Math.PI / 2, 0, margin, earliest, latest),
            39615 => new(AlexandriaGeometry.Kind.Rectangle, origin, direction, 40, 0, 5, margin, earliest, latest),
            39616 => new(AlexandriaGeometry.Kind.Annulus, origin, direction, 6, 40, 0, margin, earliest, latest),
            36793 => new(AlexandriaGeometry.Kind.Circle, origin, direction, 6, 0, 0, margin, earliest, latest),
            36783 => new(AlexandriaGeometry.Kind.Rectangle, origin, direction, 40, 0, 3, margin, earliest, latest),
            // A 2026-11-03 receipt hit the player behind the helper's sampled
            // origin. Cover both directions until the helper offset is proven.
            39239 => new(AlexandriaGeometry.Kind.Rectangle, origin, direction, 50, 50, 4, margin, earliest, latest),
            _ => default
        };
        if (hazard.Valid) return true;
        hazard = default;
        return false;
    }
}

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

// Detached Sprint evidence for the first-boss planner. The caller samples the
// actual status on the bot thread and must invalidate a route when this
// evidence changes; this type neither activates Sprint nor owns movement.
internal static class AlexandriaSprintPolicy
{
    // Two 2026-11-03 cleared-arena captures observed roughly 7.5–8.9 y/s
    // steady movement and turns. These deliberately wider bounds and the
    // half-second expiry allowance remain provisional until combat and job
    // variants are tested; a speed-limiting status requires a slower model.
    internal const double SprintMinimum = 6.8, SprintMaximum = 10, ExpiryAllowance = .5;

    internal static bool TrySnapshot(double now, double? remaining,
        out AlexandriaTemporalPlanner.MovementBoost? boost)
    {
        boost = null;
        if (!double.IsFinite(now) || now < 0) return false;
        if (remaining is not { } value) return true;
        if (!double.IsFinite(value) || value < 0) return false;
        double earliest = now + Math.Max(0, value - ExpiryAllowance);
        double latest = now + value + ExpiryAllowance;
        if (!double.IsFinite(earliest) || !double.IsFinite(latest)) return false;
        boost = new(earliest, latest, SprintMinimum, SprintMaximum);
        return true;
    }

    // A newly acquired, prematurely lost, or extended status invalidates the
    // prior envelope. The caller must cancel its worker/route and replan;
    // absence inside the uncertain expiry interval remains conservatively
    // covered by that interval's slower minimum and faster maximum.
    internal static bool Consistent(AlexandriaTemporalPlanner.MovementBoost? planned,
        double now, double? observedRemaining)
    {
        if (!double.IsFinite(now) || now < 0 ||
            (observedRemaining is { } invalid && (!double.IsFinite(invalid) || invalid < 0))) return false;
        if (planned is not { } expected) return !observedRemaining.HasValue;
        if (!AlexandriaTemporalPlanner.TryMovementBounds(now, now, 5, 8, expected, out _, out _)) return false;
        if (observedRemaining is not { } remaining) return now >= expected.EarliestExpiry;
        double observedExpiry = now + remaining;
        return double.IsFinite(observedExpiry) && now <= expected.LatestExpiry &&
            observedExpiry >= expected.EarliestExpiry && observedExpiry <= expected.LatestExpiry;
    }
}

/// <summary>
/// Backward viability over a finite, encounter-supplied space-time graph. A locally
/// safe shelter is useful only if it has a certified route to every later stage.
/// This component owns no input and does not infer mechanics or sampling accuracy.
/// </summary>
internal static class AlexandriaTemporalPlanner
{
    // Each stage supplies candidate positions and the positive constraints known at
    // that instant. Stage zero is the actual actor position, not a choice of
    // starting points. Callers must include arena erosion and stack/shelter
    // requirements; this graph cannot prove its finite candidate set is complete.
    // Every connecting segment must lie in the caller's convex traversable arena;
    // nonconvex floors require separate proof. Timed positive constraints must be
    // supplied for windows that extend between stages.
    internal readonly record struct Stage(double Time, Vector2[] Points, bool[] Allowed);
    // A forced edge pairs pre-impact and landing candidates by index. The
    // caller must independently verify displacement, duration and linear
    // travel before using the resulting route in gameplay.
    internal readonly record struct RadialKnockback(int Edge, Vector2 Origin, double Distance);

    // A temporary speed effect may end anywhere within this observed interval.
    // Use its faster minimum only before the earliest possible expiry, and its
    // faster maximum until the latest possible expiry. A crossing edge gets
    // the slower minimum and faster maximum, covering either speed regime.
    internal readonly record struct MovementBoost(double EarliestExpiry, double LatestExpiry,
        double MinimumSpeed, double MaximumSpeed);

    internal static bool TryMovementBounds(double start, double end, double minimumSpeed, double maximumSpeed,
        MovementBoost? boost, out double minimum, out double maximum)
    {
        minimum = minimumSpeed; maximum = maximumSpeed;
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start ||
            !double.IsFinite(minimumSpeed) || minimumSpeed <= 0 ||
            !double.IsFinite(maximumSpeed) || maximumSpeed < minimumSpeed) return false;
        if (boost is not { } b) return true;
        if (!double.IsFinite(b.EarliestExpiry) || !double.IsFinite(b.LatestExpiry) ||
            b.EarliestExpiry > b.LatestExpiry || !double.IsFinite(b.MinimumSpeed) ||
            !double.IsFinite(b.MaximumSpeed) || b.MinimumSpeed < minimumSpeed ||
            b.MaximumSpeed < Math.Max(maximumSpeed, b.MinimumSpeed)) return false;
        if (end <= b.EarliestExpiry) minimum = b.MinimumSpeed;
        if (start <= b.LatestExpiry) maximum = b.MaximumSpeed;
        return true;
    }

    /// <summary>
    /// Returns one candidate index per stage, or an empty array when no certified
    /// route exists. Every edge is checked continuously over its effect windows;
    /// observed speed, departure delay and hazard timing must be supplied by callers.
    /// </summary>
    internal static int[] Plan(Stage[] stages, AlexandriaGeometry.Hazard[] hazards,
        double delay, double minimumSpeed, double maximumSpeed,
        AlexandriaPositionConstraints.Timed[] requirements = null, CancellationToken cancellation = default,
        RadialKnockback? knockback = null, MovementBoost? movementBoost = null)
    {
        requirements ??= Array.Empty<AlexandriaPositionConstraints.Timed>();
        if (cancellation.IsCancellationRequested || stages == null || stages.Length == 0 || hazards == null ||
            !double.IsFinite(delay) || delay < 0 || !double.IsFinite(minimumSpeed) ||
            minimumSpeed <= 0 || !double.IsFinite(maximumSpeed) || maximumSpeed < minimumSpeed)
            return Array.Empty<int>();
        for (int i = 0; i < stages.Length; ++i)
        {
            var s = stages[i];
            if (!double.IsFinite(s.Time) || (i > 0 && s.Time <= stages[i - 1].Time) ||
                s.Points == null || s.Allowed == null || s.Points.Length == 0 || s.Points.Length != s.Allowed.Length)
                return Array.Empty<int>();
            foreach (var p in s.Points)
                if (!AlexandriaGeometry.Finite(p)) return Array.Empty<int>();
        }
        if (stages[0].Points.Length != 1) return Array.Empty<int>();
        if (knockback is { } forced && (forced.Edge < 0 || forced.Edge >= stages.Length - 1 ||
            !AlexandriaGeometry.Finite(forced.Origin) || !double.IsFinite(forced.Distance) || forced.Distance <= 0 ||
            stages[forced.Edge].Points.Length != stages[forced.Edge + 1].Points.Length)) return Array.Empty<int>();
        foreach (var h in hazards) if (!h.Valid) return Array.Empty<int>();
        if (!TryMovementBounds(stages[0].Time, stages[^1].Time, minimumSpeed, maximumSpeed,
            movementBoost, out _, out _)) return Array.Empty<int>();

        var next = new int[stages.Length][];
        var viable = new bool[stages.Length][];
        for (int i = 0; i < stages.Length; ++i)
        {
            viable[i] = new bool[stages[i].Points.Length];
            next[i] = new int[stages[i].Points.Length];
            Array.Fill(next[i], -1);
        }
        int last = stages.Length - 1;
        // A zero-length edge certifies even a single-stage request against active
        // hazards. Endpoint flags alone cannot establish spatial hazard safety.
        var terminal = new AlexandriaGeometry.Travel(stages[last].Time, stages[last].Time, 0, minimumSpeed, maximumSpeed);
        for (int j = 0; j < viable[last].Length; ++j)
            viable[last][j] = stages[last].Allowed[j] && AlexandriaGeometry.TravelSafe(
                stages[last].Points[j], stages[last].Points[j], terminal, hazards) &&
                AlexandriaPositionConstraints.TravelInside(stages[last].Points[j], stages[last].Points[j], terminal, requirements);

        for (int i = last - 1; i >= 0; --i)
        {
            if (cancellation.IsCancellationRequested) return Array.Empty<int>();
            if (knockback is { } push && push.Edge == i)
            {
                // Captured knockback decelerated over roughly 0.8s. It is not
                // constant-speed walking: while any effect overlaps this edge,
                // conservatively check its entire possible straight trajectory.
                // The caller must validate this trajectory and bound the arena.
                for (int j = 0; j < viable[i].Length; ++j)
                {
                    if ((j & 63) == 0 && cancellation.IsCancellationRequested) return Array.Empty<int>();
                    var start = stages[i].Points[j]; var landing = stages[i + 1].Points[j];
                    if (!stages[i].Allowed[j] || !viable[i + 1][j] ||
                        !AlexandriaPositionConstraints.TryKnockback(start, push.Origin, push.Distance, out var expected) ||
                        Vector2.Distance(expected, landing) > .001f) continue;
                    bool safe = true;
                    foreach (var hazard in hazards)
                        if (!hazard.Valid || (hazard.Earliest <= stages[i + 1].Time && hazard.Latest >= stages[i].Time &&
                            !AlexandriaGeometry.SegmentSafe(start, landing, hazard))) { safe = false; break; }
                    if (!safe) continue;
                    double speed = Vector2.Distance(start, landing) / (stages[i + 1].Time - stages[i].Time);
                    var forcedTravel = new AlexandriaGeometry.Travel(stages[i].Time, stages[i + 1].Time, 0, speed, speed);
                    if (!forcedTravel.Valid || !AlexandriaPositionConstraints.TravelInside(start, landing, forcedTravel, requirements)) continue;
                    foreach (var requirement in requirements)
                        if (requirement.Earliest <= stages[i + 1].Time && requirement.Latest >= stages[i].Time &&
                            !AlexandriaPositionConstraints.TravelInside(start, landing, forcedTravel,
                                [requirement with { Earliest = stages[i].Time, Latest = stages[i + 1].Time }]))
                        { safe = false; break; }
                    if (!safe) continue;
                    viable[i][j] = true; next[i][j] = j;
                }
                continue;
            }
            if (!TryMovementBounds(stages[i].Time, stages[i + 1].Time, minimumSpeed, maximumSpeed,
                movementBoost, out double edgeMinimum, out double edgeMaximum)) return Array.Empty<int>();
            var travel = new AlexandriaGeometry.Travel(stages[i].Time, stages[i + 1].Time, delay, edgeMinimum, edgeMaximum);
            // A cheap necessary reachability bound avoids expensive continuous
            // geometry checks for distant grid points. The exact deadline and
            // entire possible trajectory are still certified below.
            double reach = Math.Max(0, (travel.End - travel.Start - delay) * edgeMinimum);
            if (!double.IsFinite(reach)) return Array.Empty<int>();
            double cellSize = Math.Max(reach, .001);
            // Every reachable point lies in the same bucket or one of its eight
            // neighbors. Restrict keys before conversion to keep malformed
            // coordinates from overflowing arithmetic or aliasing another cell.
            bool Cell(Vector2 p, out (long X, long Y) cell)
            {
                double x = Math.Floor(p.X / cellSize), y = Math.Floor(p.Y / cellSize);
                if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e15 || Math.Abs(y) > 1e15)
                { cell = default; return false; }
                cell = ((long)x, (long)y); return true;
            }
            var buckets = new Dictionary<(long X, long Y), List<int>>();
            for (int k = 0; k < viable[i + 1].Length; ++k)
            {
                if (!viable[i + 1][k]) continue;
                if (!Cell(stages[i + 1].Points[k], out var key)) return Array.Empty<int>();
                if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<int>();
                list.Add(k);
            }
            for (int j = 0; j < viable[i].Length; ++j)
            {
                if (cancellation.IsCancellationRequested) return Array.Empty<int>();
                if (!stages[i].Allowed[j]) continue;
                if (!Cell(stages[i].Points[j], out var cell)) return Array.Empty<int>();
                double best = double.PositiveInfinity;
                for (int dx = -1; dx <= 1; ++dx)
                for (int dy = -1; dy <= 1; ++dy)
                {
                    if (!buckets.TryGetValue((cell.X + dx, cell.Y + dy), out var candidates)) continue;
                    foreach (int k in candidates)
                    {
                        double distance = Vector2.Distance(stages[i].Points[j], stages[i + 1].Points[k]);
                        if (distance >= best || distance > reach || !AlexandriaGeometry.TravelSafe(stages[i].Points[j], stages[i + 1].Points[k], travel, hazards) ||
                            !AlexandriaPositionConstraints.TravelInside(stages[i].Points[j], stages[i + 1].Points[k], travel, requirements)) continue;
                        best = distance;
                        next[i][j] = k;
                        viable[i][j] = true;
                    }
                }
            }
        }
        int first = Array.FindIndex(viable[0], v => v);
        // The caller still owns generation checks when publishing a result;
        // cancellation here prevents completed-but-retired work from returning a route.
        if (first < 0 || cancellation.IsCancellationRequested) return Array.Empty<int>();
        var route = new int[stages.Length];
        route[0] = first;
        for (int i = 0; i < last; ++i) route[i + 1] = next[i][route[i]];
        return route;
    }
}
