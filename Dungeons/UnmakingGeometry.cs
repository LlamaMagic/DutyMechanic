using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Encounter-local shape and participation calculations, independent of native state.
/// Native avoidance chooses geometric escape paths; these functions only describe hazards
/// and safe participation in positive-position mechanics.
/// </summary>
internal static class UnmakingGeometry
{
    internal const float Margin = .5f;
    internal static readonly Vector2 Center = new(100, 100);

    // The normal floor is a radius 20 circle. Add waves expand to a walled square;
    // the beacon extends that square along its cardinal direction. Its observed
    // position supplies the extension, never a guessed route waypoint.
    internal static bool InArena(Vector2 p, bool adds, Vector2? beacon = null)
    {
        if (!adds) return Vector2.Distance(p, Center) <= 19.5f;
        var (center, half) = AddBounds(beacon);
        return Math.Abs(p.X - center.X) <= half.X - Margin && Math.Abs(p.Y - center.Y) <= half.Y - Margin;
    }

    internal static (Vector2 Center, Vector2 Half) AddBounds(Vector2? beacon)
    {
        if (beacon == null) return (Center, new(40, 40));
        var off = beacon.Value - Center;
        bool horizontal = Math.Abs(off.X) > Math.Abs(off.Y);
        float distance = horizontal ? Math.Abs(off.X) : Math.Abs(off.Y);
        if (distance <= 40) return (Center, new(40, 40));
        var direction = horizontal ? new Vector2(Math.Sign(off.X), 0) : new Vector2(0, Math.Sign(off.Y));
        // The extended floor reaches 10y beyond the beacon and retains the opposite 40y edge.
        return (Center + direction * ((distance - 30) / 2), horizontal
            ? new((distance + 50) / 2, 40) : new(40, (distance + 50) / 2));
    }

    // Local polygon forward is +Z (Vector 2.Y); RB rotates it by -actor.Heading.
    // Move the cone apex backward by 0.5/sin(half-angle) to add half a yalm at both
    // straight edges without swallowing adjacent sectors near their shared origin.
    internal static Vector2[] Cone(float radius, float halfAngleDegrees)
    {
        double half = halfAngleDegrees * Math.PI / 180;
        float back = Margin / (float)Math.Sin(half);
        var points = new List<Vector2> { new(0, -back) };
        for (int i = 0; i <= 24; i++)
        {
            double a = -half + 2 * half * i / 24;
            points.Add(new((float)Math.Sin(a) * (radius + Margin + back),
                (float)Math.Cos(a) * (radius + Margin + back) - back));
        }
        return points.ToArray();
    }

    internal static Vector2[] Sector(float halfAngleDegrees)
    {
        // Silent Torrent is an annular sector 17..19y around its offset omen origin.
        // Shrink the safe inner radius and widen the angular edges by the same 0.5y margin.
        const float inner = 17 - Margin, outer = 19 + Margin;
        double half = halfAngleDegrees * Math.PI / 180 + Math.Asin(Margin / inner);
        var points = new List<Vector2>();
        for (int i = 0; i <= 24; i++)
        {
            double a = -half + 2 * half * i / 24;
            points.Add(new((float)Math.Sin(a) * outer, (float)Math.Cos(a) * outer));
        }
        for (int i = 24; i >= 0; i--)
        {
            double a = -half + 2 * half * i / 24;
            points.Add(new((float)Math.Sin(a) * inner, (float)Math.Cos(a) * inner));
        }
        return points.ToArray();
    }

    internal static Vector2[] Rectangle(float length, float halfWidth) =>
        [new(-halfWidth - Margin, -Margin), new(halfWidth + Margin, -Margin),
         new(halfWidth + Margin, length + Margin), new(-halfWidth - Margin, length + Margin)];

    internal static Vector2[] Circle(float radius) => Enumerable.Range(0, 96)
        // Circumscribe so polygon chords do not reduce the declared clearance.
        .Select(i => new Vector2(MathF.Sin(i * MathF.Tau / 96), MathF.Cos(i * MathF.Tau / 96))
            * (radius / MathF.Cos(MathF.PI / 96))).ToArray();

    internal static Vector2[] Exterior(bool adds, Vector2? beacon)
    {
        // A single bridged polygon encodes the exterior without clipping valid corners.
        Vector2[] inner;
        if (!adds)
            inner = Enumerable.Range(0, 97).Select(i => Center + new Vector2(-MathF.Sin(i * MathF.Tau / 96),
                MathF.Cos(i * MathF.Tau / 96)) * 19.5f).ToArray();
        else
        {
            var (c, h) = AddBounds(beacon);
            h -= new Vector2(Margin);
            inner = [c + new Vector2(h.X, h.Y), c + new Vector2(-h.X, h.Y),
                c - h, c + new Vector2(h.X, -h.Y), c + h];
        }
        // Opposite winding joins the finite outer border and inner safe ring.
        return inner.Concat(new Vector2[] { new(600, 600), new(600, -400),
            new(-400, -400), new(-400, 600), new(600, 600), inner[0] }).ToArray();
    }

    internal static bool InPolygon(Vector2 p, IReadOnlyList<Vector2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    internal static Vector2 Local(Vector2 world, Vector2 origin, float heading)
    {
        var v = world - origin;
        // Inverse of RB's polygon rotation(-heading).
        return new(v.X * MathF.Cos(heading) - v.Y * MathF.Sin(heading),
            v.X * MathF.Sin(heading) + v.Y * MathF.Cos(heading));
    }

    // The omen centers are 18y to the helper's left; the curved line starts
    // at the helper and sweeps clockwise 20/40/60 degrees to the orb's cast destination.
    internal static Vector2 TorrentOrigin(Vector2 caster, float heading) =>
        caster + new Vector2(MathF.Cos(heading), -MathF.Sin(heading)) * 18;

    internal static Vector2[] Rebase(IReadOnlyList<Vector2> polygon, Vector2 origin, float heading, Vector2 anchor)
    {
        float cos = MathF.Cos(heading), sin = MathF.Sin(heading);
        return polygon.Select(p => origin - anchor + new Vector2(p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos)).ToArray();
    }

    internal static Vector2? Shelter(Vector2 boss, Vector2 zero)
    {
        var away = zero - boss;
        return away.LengthSquared() < 1 ? null : zero + Vector2.Normalize(away) * 2;
    }

    internal static int LineParticipants(Vector2 boss, Vector2 target, IEnumerable<Vector2> party)
    {
        var direction = target - boss;
        if (direction.LengthSquared() < 1) return 0;
        direction = Vector2.Normalize(direction);
        return party.Count(p =>
        {
            var offset = p - boss;
            float along = Vector2.Dot(offset, direction);
            // Width 8 minus 0.5y per side and arrival tolerance 0.4y leaves safe participation.
            return along > 0 && along < 59.5f && Math.Abs(offset.X * direction.Y - offset.Y * direction.X) <= 3.1f;
        });
    }

    internal static IEnumerable<Vector2> StackCandidates(Vector2 player, IReadOnlyList<Vector2> party)
    {
        yield return player;
        foreach (var p in party)
        {
            yield return p;
            var cluster = party.Where(q => Vector2.Distance(q, p) <= 9).ToArray();
            if (cluster.Length > 1) yield return cluster.Aggregate(Vector2.Zero, (a, b) => a + b) / cluster.Length;
        }
    }

    // Domestic's healer requirement is majority coverage within 30y, not following one
    // player. Enter only after losing that coverage; aim for 28y to absorb movement and
    // arrival tolerance. Retain a still-useful destination instead of chasing centroids.
    // The caller supplies the encounter's safe-path predicate and mechanic exclusions.
    internal static Vector2? HealingPosition(Vector2 player, IReadOnlyList<Vector2> members,
        Vector2? previous, Func<Vector2, bool> safeApproach)
    {
        if (members.Count == 0) return null;
        int majority = members.Count / 2 + 1;
        int Coverage(Vector2 p, float radius) => members.Count(m => Vector2.Distance(p, m) <= radius);
        if (Coverage(player, 30) >= majority) return null;
        if (previous.HasValue && Coverage(previous.Value, 28) >= majority && safeApproach(previous.Value))
            return previous;
        // Pair midpoints cover split groups that have no member at their shared center.
        var candidates = StackCandidates(player, members).Concat(
            members.SelectMany((a, i) => members.Skip(i + 1).Select(b => (a + b) / 2)));
        return candidates.Where(safeApproach).Select(p => new { Point = p, Count = Coverage(p, 28) })
            .Where(c => c.Count >= majority).OrderByDescending(c => c.Count)
            .ThenBy(c => Vector2.DistanceSquared(c.Point, player))
            .Select(c => (Vector2?)c.Point).FirstOrDefault();
    }
}
