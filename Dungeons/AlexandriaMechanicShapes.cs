using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

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
