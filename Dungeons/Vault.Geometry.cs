using System;
using System.Collections.Generic;
using Clio.Utilities;

namespace DutyMechanic.Dungeons;

public partial class Vault
{
    private static readonly Vector2[] collapseSmall = HalfRing(2.1f, 7f);
    private static readonly Vector2[] collapseMedium = HalfRing(7.3f, 12f);
    private static readonly Vector2[] collapseLarge = HalfRing(11.9f, 17f);
    private static readonly Vector2[] castCircle = Circle(5.5f);
    private static readonly Vector2[] riveLine = Rectangle(3f, 31.5f, -.5f);
    private static readonly Vector2[] slashLine = Rectangle(9f, 48f, -.5f);
    // Preserve the pre-existing tour dimensions until their cast and damage
    // traces establish a narrower safe margin in the live client.
    private static readonly Vector2[] tourLine = Rectangle(7f, 60f, 0f);

    private static Vector2[] CastPoints(uint action) => action switch
    {
        1099 or 4140 or 4146 => castCircle,
        1135 => riveLine,
        4134 => slashLine,
        4137 or 4138 or 4139 => CollapsePoints(action),
        4152 or 4153 => tourLine,
        _ => Array.Empty<Vector2>()
    };

    private static Vector2[] CollapsePoints(uint action) => action switch
    {
        DimensionalCollapseSmallSpell => collapseSmall,
        DimensionalCollapseMediumSpell => collapseMedium,
        DimensionalCollapseLargeSpell => collapseLarge,
        _ => Array.Empty<Vector2>()
    };

    // Expand both radial edges and the straight diameter by 0.5y. Intersecting
    // the expanded circles with y=-0.5 avoids an angular-only margin that
    // becomes too narrow near the small ring's inner edge. Three-degree facets
    // remain well inside the clearance at the largest observed radius.
    private static Vector2[] HalfRing(float inner, float outer)
    {
        inner -= .5f;
        outer += .5f;
        double outerAngle = Math.Acos(-.5 / outer);
        double innerAngle = Math.Acos(-.5 / inner);
        var points = new List<Vector2>();
        const int steps = 72;
        for (int i = 0; i <= steps; ++i)
        {
            double angle = -outerAngle + 2 * outerAngle * i / steps;
            points.Add(new Vector2(outer * (float)Math.Sin(angle), outer * (float)Math.Cos(angle)));
        }
        for (int i = steps; i >= 0; --i)
        {
            double angle = -innerAngle + 2 * innerAngle * i / steps;
            points.Add(new Vector2(inner * (float)Math.Sin(angle), inner * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }

    private static Vector2[] Rectangle(float width, float length, float offset) =>
        [new(-width / 2, offset), new(width / 2, offset),
         new(width / 2, length + offset), new(-width / 2, length + offset)];

    private static Vector2[] Circle(float radius)
    {
        var points = new Vector2[96];
        for (int i = 0; i < points.Length; ++i)
        {
            double angle = Math.PI * 2 * i / points.Length;
            points[i] = new Vector2(radius * (float)Math.Sin(angle), radius * (float)Math.Cos(angle));
        }
        return points;
    }
}
