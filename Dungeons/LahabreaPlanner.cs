using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>Scalar geometry and ordered damage stages for the quest 70058 solo encounter.</summary>
internal static class LahabreaPlanner
{
    // The burning ring starts at radius fifteen. Keep a half-yalm inset for paths and knockback landings.
    internal static readonly Vector2 Center = new(-704, 480);
    internal const float Radius = 14.5f;
    internal const float Margin = 0.5f;
    internal const uint Knockback = 29758;
    internal const uint Burst = 29333;
    internal const double Grace = 0.25;
    internal enum Shape
    {
        Circle,
        Rectangle,
        Cone
    }

    /// <summary>Copied actor-relative geometry; Finish orders helpers, never their discovery order.</summary>
    internal sealed record Hazard(uint Owner, uint Action, Vector2 Origin, float Heading, double Finish, Shape Kind, float Size, float Width = 0, bool Persistent = false)
    {
        internal bool Contains(Vector2 p)
        {
            Vector2 d = p - Origin;
            Vector2 forward = new(MathF.Sin(Heading), MathF.Cos(Heading));
            float along = Vector2.Dot(d, forward);
            float across = Math.Abs(d.X * forward.Y - d.Y * forward.X);
            return Kind switch
            {
                Shape.Circle => d.Length() <= Size,
                Shape.Rectangle => along >= -Margin && along <= Size && across <= Width,
                // Shift the apex backwards by the margin instead of guessing an angular padding
                // that becomes excessively wide at the far end of a forty-yalm cone.
                _ => ConeContains(d + forward * Margin, forward, Size, Width)};
        }

        private static bool ConeContains(Vector2 d, Vector2 forward, float length, float halfAngle) => d.Length() <= length && (d.LengthSquared() < 0.0001f || Vector2.Dot(Vector2.Normalize(d), forward) >= MathF.Cos(halfAngle));
        internal Vector2[] Polygon()
        {
            Vector2 forward = new(MathF.Sin(Heading), MathF.Cos(Heading));
            Vector2 side = new(forward.Y, -forward.X);
            if (Kind == Shape.Rectangle)
            {
                return[Origin - forward * Margin + side * Width, Origin + forward * Size + side * Width, Origin + forward * Size - side * Width, Origin - forward * Margin - side * Width];
            }

            List<Vector2> points = [];
            const int n = 96; // Circumscribed arc chords preserve the declared safety margin.
            Vector2 center = Kind == Shape.Cone ? Origin - forward * Margin : Origin;
            if (Kind == Shape.Cone)
            {
                points.Add(center);
            }

            float arc = Kind == Shape.Cone ? Width : MathF.PI;
            float radius = Size / MathF.Cos(arc / n);
            for (int i = 0; i <= n; ++i)
            {
                float angle = Heading - arc + 2 * arc * i / n;
                points.Add(center + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * radius);
            }

            return points.ToArray();
        }
    }

    /// <summary>Knockback source and effect deadline, copied from the actual helper 29758.</summary>
    internal sealed record Push(Vector2 Origin, double Finish);
    /// <summary>One published stage drives both RB hazards and optional positive positioning.</summary>
    internal sealed record Plan(Hazard[] Hazards, Push Push, Vector2? Destination, string Stage);
    internal static Hazard Cast(uint owner, uint action, Vector2 origin, float heading, double finish) => action switch
    {
        29329 => new(owner, action, origin, heading, finish, Shape.Circle, 1 + Margin),
        Burst => new(owner, action, origin, heading, finish, Shape.Circle, 8 + Margin),
        29335 or 29756 => new(owner, action, origin, heading, finish, Shape.Circle, 6 + Margin),
        29331 or 29762 => new(owner, action, origin, heading, finish, Shape.Rectangle, 60 + Margin, 4 + Margin),
        29760 => new(owner, action, origin, heading, finish, Shape.Cone, 40 + Margin, MathF.PI / 6),
        29337 => new(owner, action, origin, heading, finish, Shape.Cone, 40 + Margin, 75 * MathF.PI / 180),
        _ => null
    };
    internal static bool Safe(Vector2 p, IEnumerable<Hazard> hazards) => Vector2.Distance(p, Center) < Radius && !hazards.Any(h => h.Contains(p));
    internal static bool SafeLanding(Vector2 p, Push push, IEnumerable<Hazard> hazards)
    {
        Vector2 delta = p - push.Origin;
        // Exactly atop the source has an undefined direction. A 10-yalm push must finish on
        // walkable floor and outside the persistent pitch, not merely start near the middle.
        return delta.Length() >= 0.5f && Safe(p, hazards) && Safe(p + Vector2.Normalize(delta) * 10, hazards);
    }

    internal static bool ClearSegment(Vector2 start, Vector2 end, Hazard[] hazards)
    {
        int samples = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(start, end) / 0.2f));
        for (int i = 1; i <= samples; i++)
        {
            if (!Safe(Vector2.Lerp(start, end, (float)i / samples), hazards))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<Vector2> Floor()
    {
        yield return Center;
        for (float r = 0.5f; r < Radius; r += 0.5f)
        {
            for (int i = 0; i < 96; i++)
            {
                float angle = i * MathF.Tau / 96;
                yield return Center + new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * r;
            }
        }
    }

    internal static Plan Build(Vector2 player, Hazard[] input, Push push, Vector2? previous)
    {
        Hazard[] hazards = input;
        bool common = Floor().Any(p => Safe(p, hazards));
        if (!common)
        {
            // Spheres -> phantom lines and Cross -> Grip are deliberately sequential. If all
            // announced geometry closes the floor, escape the first damage wave, then replan.
            // Persistent pitch never drops out of this union. 350ms groups simultaneous helpers.
            double first = hazards.Where(h => !h.Persistent).Select(h => h.Finish).DefaultIfEmpty(double.MaxValue).Min();
            hazards = hazards.Where(h => h.Persistent || h.Finish <= first + 0.35).ToArray();
        }

        if (push == null)
        {
            return new(hazards, null, null, common ? "geometry" : "first damage wave");
        }

        // The earlier sphere explosion must resolve before staging the knockback. It is unsafe
        // to blindly run to the middle or let a later cone drive us into an active fire circle.
        if (input.Any(h => h.Action == Burst && h.Finish < push.Finish - 0.35))
        {
            return new(hazards, null, null, "spheres before knockback");
        }

        Hazard[] landingHazards = input.Where(h => h.Persistent || h.Finish >= push.Finish - 0.35).ToArray();
        bool Valid(Vector2 p) => SafeLanding(p, push, landingHazards) && Safe(p, hazards) && ClearSegment(player, p, hazards);
        Vector2? destination = previous.HasValue && Valid(previous.Value) ? previous : Valid(player) ? player : Floor().Where(Valid).OrderBy(p => Vector2.DistanceSquared(player, p)).Select(p => (Vector2? )p).FirstOrDefault();
        return new(hazards, push, destination, destination.HasValue ? "knockback staging" : "knockback blocked; geometry first");
    }
}
