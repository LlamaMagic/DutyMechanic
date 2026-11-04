using System;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>Direct square-floor destinations for Authority's observed flare-to-pair transition.</summary>
/// <remarks>
/// Captures correlate VFX 327 with four flare targets and VFX 55 with
/// all eight pair targets. The markers overlap briefly: flares retain priority
/// until they disappear. The normal encounter uses a 12-yalm flare radius,
/// a 3-yalm pair radius (at least two players), and an 8-yalm ground-circle radius. Geometry is
/// refreshed from the actual party/casters; no inferred role assignment or NPC
/// following is used. The caller supplies other hazards and the existing mover.
/// </remarks>
internal static class InterphosAuthority
{
    internal readonly record struct Point(float X, float Z);
    internal readonly record struct Circle(float X, float Z, float Radius, float SecondsUntil = 0);
    internal readonly record struct Member(uint Id, float X, float Z, bool Flare);

    // Pair targets stay within 2.5 yalms: 0.25 for arrival tolerance and 0.25
    // for clearance inside the three-yalm stack. A larger margin excludes
    // the captured circle/pair intersection at (103.5, 103.5).
    internal static Circle[] Threats(Member[] party, uint self, bool selfFlare, Circle[] ground) =>
        ground.Concat(party.Where(p => p.Id != self && (selfFlare || p.Flare))
            .Select(p => new Circle(p.X, p.Z, 12.75f))).ToArray();

    internal static bool Safe(Point p, Member[] party, uint self, bool pair, Circle[] threats) =>
        float.IsFinite(p.X) && float.IsFinite(p.Z) &&
        p.X >= 80.75f && p.X <= 119.25f && p.Z >= 80.75f && p.Z <= 119.25f &&
        threats.All(c => DistanceSquared(p, new(c.X, c.Z)) > c.Radius * c.Radius) &&
        (!pair || party.Any(m => m.Id != self && DistanceSquared(p, new(m.X, m.Z)) <= 2.5f * 2.5f));

    // A player already inside a freshly published circle must be able to leave
    // it. The captured pair escape initially approaches one occupied circle's
    // center by 0.03 yalms before leaving both; demanding monotonic distance rejects
    // a short walk during its 3.5-second windup. A straight segment can leave a convex
    // circle only once. Safe starts still may not enter a different circle.
    internal static bool SafeSegment(Point from, Point to, Circle[] threats, float speed = 6)
    {
        if (!InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Square, from.X, from.Z, to.X, to.Z))
        {
            return false;
        }

        if (!float.IsFinite(speed) || speed <= 0)
        {
            return false;
        }
        // Never depend on Sprint. An observed slower speed tightens crossing
        // eligibility on every pulse; an external boost adds no required budget.
        speed = Math.Min(speed, 6);
        float dx = to.X - from.X, dz = to.Z - from.Z, length2 = dx * dx + dz * dz;
        foreach (var c in threats)
        {
            float ox = from.X - c.X, oz = from.Z - c.Z;
            float initial2 = ox * ox + oz * oz, dot = ox * dx + oz * dz;
            if (initial2 > c.Radius * c.Radius)
            {
                float t = length2 > 0 ? Math.Clamp(-dot / length2, 0, 1) : 0;
                float nx = ox + t * dx, nz = oz + t * dz;
                if (nx * nx + nz * nz <= c.Radius * c.Radius)
                {
                    // A windup circle can be crossed before it resolves. Budget
                    // travel to the last intersection plus 0.25 seconds for
                    // input startup, not the full trip to the destination.
                    // Unknown or expired timing cannot authorize a crossing.
                    float discriminant = dot * dot - length2 * (initial2 - c.Radius * c.Radius);
                    float exit = length2 > 0 ? Math.Clamp((-dot + MathF.Sqrt(Math.Max(0, discriminant))) / length2, 0, 1) : 1;
                    float exitSeconds = MathF.Sqrt(length2) * exit / speed + 0.25f;
                    if (!float.IsFinite(c.SecondsUntil) || c.SecondsUntil <= exitSeconds)
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    internal static bool TryPosition(Point from, Point? retained, Member[] party, uint self,
        bool pair, Circle[] threats, Func<Point, bool> otherPathSafe, out Point target, float speed = 6)
    {
        target = default;
        bool Valid(Point p) => Safe(p, party, self, pair, threats) && SafeSegment(from, p, threats, speed) && otherPathSafe(p);
        if (retained is Point prior && Valid(prior))
        {
            target = prior;
            return true;
        }
        if (Valid(from))
        {
            target = from;
            return true;
        }
        float best = float.PositiveInfinity;
        Point selected = default;
        void Consider(Point p)
        {
            float distance = DistanceSquared(from, p);
            if (distance >= best || !Valid(p))
            {
                return;
            }

            best = distance;
            selected = p;
        }
        // Include actual partner positions before the arena grid so narrow
        // stack intersections are represented without a required staging point.
        if (pair)
        {
            foreach (var member in party.Where(m => m.Id != self))
            {
                Consider(new(member.X, member.Z));
            }
        }

        for (float x = 81; x <= 119; x += 0.5f)
        {
            for (float z = 81; z <= 119; z += 0.5f)
            {
                Consider(new(x, z));
            }
        }

        if (float.IsPositiveInfinity(best))
        {
            return false;
        }

        target = selected;
        return true;
    }

    // On November 4, a partner-only search stranded the Viper inside the first
    // circle wave while partners were still crossing into its safe pockets.
    // Native escape repeatedly found no path. Keep the same authored owner:
    // escape the ground first, then reacquire a safe pair on the next observation.
    internal static bool TryResolve(Point from, Point? retained, Member[] party, uint self,
        bool pair, Circle[] threats, bool groundActive, Func<Point, bool> otherPathSafe,
        out Point target, out bool groundFirst, float speed = 6)
    {
        groundFirst = false;
        if (TryPosition(from, retained, party, self, pair, threats, otherPathSafe, out target, speed))
        {
            return true;
        }

        groundFirst = pair && groundActive &&
            TryPosition(from, retained, party, self, false, threats, otherPathSafe, out target, speed);
        return groundFirst;
    }

    internal static float DistanceSquared(Point a, Point b) => (a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z);
}
