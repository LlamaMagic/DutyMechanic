using System;

namespace DutyMechanic.Dungeons;

/// <summary>Direct escape from the active Waltz bait circles.</summary>
/// <remarks>The encounter reference uses sqrt(212)/2 around each actor until
/// its visual resolves. The caller supplies currently active centers and checks
/// floor, other hazards, and movement ownership; no actor is followed.</remarks>
internal static class InterphosWaltz
{
    // Include the mover's 0.25-yalm arrival tolerance at the circle edge.
    internal const float Radius = 7.55f;
    internal readonly record struct Circle(float X, float Z);
    // Action 36632 helper casts reveal the five spokes. Replace the broad bait
    // disk with 14-by-4-yalm rectangles when their origin is published; keeping
    // the disk at that point would incorrectly hide the gaps between spokes.
    internal readonly record struct Ray(float X, float Z, float Heading);

    // Across eight captured waves, all 40 nonzero omen centers lay seven yalms
    // behind their helper's facing (maximum lateral discrepancy: 0.037 yalms).
    // These helpers face opposite their attack; this conversion is action-specific.
    internal static Ray FromHelper(float x, float z, float actorHeading) =>
        new(x, z, actorHeading + MathF.PI);

    internal static bool Unsafe(float x, float z, Ray ray)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(ray.Heading))
        {
            return true;
        }

        float dx = x - ray.X, dz = z - ray.Z;
        float along = dx * MathF.Sin(ray.Heading) + dz * MathF.Cos(ray.Heading);
        float across = dx * MathF.Cos(ray.Heading) - dz * MathF.Sin(ray.Heading);
        // The 0.7-second cast leaves little travel time from the shared origin.
        // Use a 2-yalm half-width, 0.25 arrival tolerance and 0.1 clearance;
        // a generic half-yalm margin falsely makes the walkable gap too far.
        return along >= -0.35f && along <= 14.35f && MathF.Abs(across) <= 2.35f;
    }

    internal static bool Unsafe(float x, float z, Ray[] rays)
    {
        foreach (var ray in rays)
        {
            if (Unsafe(x, z, ray))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool Unsafe(float x, float z, Circle circle)
    {
        float dx = x - circle.X, dz = z - circle.Z;
        return dx * dx + dz * dz <= Radius * Radius;
    }

    internal static bool Unsafe(float x, float z, Circle[] circles)
    {
        foreach (var c in circles)
        {
            if (Unsafe(x, z, c))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool TryDestination(float x, float z, Circle[] circles,
        Func<float, float, bool> acceptPath, out float targetX, out float targetZ, bool spokes = false)
    {
        targetX = targetZ = 0;
        if (!float.IsFinite(x) || !float.IsFinite(z) || circles == null || acceptPath == null)
        {
            return false;
        }

        if (!Unsafe(x, z, circles) && acceptPath(x, z))
        {
            targetX = x;
            targetZ = z;
            return true;
        }
        float best = float.PositiveInfinity, bx = 0, bz = 0;
        // Search outward from the player so simultaneous circles choose one
        // endpoint outside all of them without inventing intermediate waypoints.
        // Spoke gaps need finer sampling than broad bait disks: the captured
        // center route fits normal speed only without rounding travel up 0.5 yalms.
        float step = spokes ? 0.1f : 0.5f;
        for (float distance = step; distance <= 20f && distance < best; distance += step)
        {
            for (int degrees = 0; degrees < 360; degrees += spokes ? 1 : 5)
            {
                float angle = degrees * MathF.PI / 180f;
                float px = x + MathF.Sin(angle) * distance, pz = z + MathF.Cos(angle) * distance;
                if (distance >= best || Unsafe(px, pz, circles) || !acceptPath(px, pz))
                {
                    continue;
                }

                best = distance;
                bx = px;
                bz = pz;
            }
        }

        if (float.IsPositiveInfinity(best))
        {
            return false;
        }

        targetX = bx;
        targetZ = bz;
        return true;
    }
}
