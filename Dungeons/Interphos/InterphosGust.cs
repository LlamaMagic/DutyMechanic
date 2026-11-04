using System;

namespace DutyMechanic.Dungeons;

/// <summary>Finds X-floor staging points for the two observed knockbacks.</summary>
/// <remarks>
/// Action 36612 pushes 20 yalms in its helper's forward direction. A captured
/// heading of pi/2 moved the player east. This planner only accepts a lateral
/// heading, checks the direct approach and displacement against the reference
/// floor, and leaves scheduling and other hazards to the encounter owner.
/// </remarks>
internal static class InterphosGust
{
    // Downburst (36610) pushes radially by 10 yalms; Gust pushes laterally by 20.
    // Crown starts afterward, so its safe spot cannot protect this landing.
    // Check the full displacement with a 0.25-yalm arrival disk and floor margin.
    internal static bool ValidRadialStage(float x, float z, float originX, float originZ)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(originX) || !float.IsFinite(originZ))
        {
            return false;
        }

        for (int sample = -1; sample < 16; ++sample)
        {
            float angle = sample * (MathF.PI / 8);
            float px = x + (sample < 0 ? 0 : 0.25f * MathF.Cos(angle));
            float pz = z + (sample < 0 ? 0 : 0.25f * MathF.Sin(angle));
            float dx = px - originX, dz = pz - originZ, length = MathF.Sqrt(dx * dx + dz * dz);
            if (length < 0.01f)
            {
                return false;
            }

            dx /= length;
            dz /= length;
            for (int step = 0; step <= 50; ++step)
            {
                float sx = px + dx * step * 0.2f, sz = pz + dz * step * 0.2f;
                if (!InterphosFloor.Contains(InterphosFloor.Layout.Cross, sx - 0.25f, sz) ||
                    !InterphosFloor.Contains(InterphosFloor.Layout.Cross, sx + 0.25f, sz) ||
                    !InterphosFloor.Contains(InterphosFloor.Layout.Cross, sx, sz - 0.25f) ||
                    !InterphosFloor.Contains(InterphosFloor.Layout.Cross, sx, sz + 0.25f))
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal static bool TryRadialStage(float x, float z, float originX, float originZ,
        Func<float, float, bool> acceptApproach, out float targetX, out float targetZ)
    {
        targetX = targetZ = 0;
        if (!float.IsFinite(x) || !float.IsFinite(z) || acceptApproach == null)
        {
            return false;
        }

        if (ValidRadialStage(x, z, originX, originZ) && acceptApproach(x, z))
        {
            targetX = x;
            targetZ = z;
            return true;
        }
        float best = float.PositiveInfinity;
        // Search direct destinations on the observed X floor. No party proxy,
        // mandatory ability or intermediate waypoint is needed for this push.
        for (float px = 88; px <= 112; px += 0.25f)
        {
            for (float pz = 80.5f; pz <= 104.5f; pz += 0.25f)
            {
                float distance = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (distance >= best || !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, x, z, px, pz) ||
                    !ValidRadialStage(px, pz, originX, originZ) || !acceptApproach(px, pz))
                {
                    continue;
                }

                best = distance;
                targetX = px;
                targetZ = pz;
            }
        }

        return !float.IsPositiveInfinity(best);
    }

    internal static bool TryStage(float x, float z, float heading,
        Func<float, float, bool> acceptApproach, out float targetX, out float targetZ)
    {
        targetX = targetZ = 0;
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(heading) || acceptApproach == null)
        {
            return false;
        }

        float dx = 20f * MathF.Sin(heading), dz = 20f * MathF.Cos(heading);
        // The observed Gust is lateral. Do not approximate a diagonal or
        // forward knockback as a crossbar route without evidence.
        if (MathF.Abs(dz) > 0.25f || MathF.Abs(MathF.Abs(dx) - 20f) > 0.25f)
        {
            return false;
        }

        if (Inset(x, z) && Inset(x + dx, z + dz) &&
            InterphosFloor.DirectionalKnockbackOnFloor(InterphosFloor.Layout.Cross, x, z, dx, dz) && acceptApproach(x, z))
        {
            targetX = x;
            targetZ = z;
            return true;
        }
        float best = float.PositiveInfinity, chosenX = 0, chosenZ = 0;
        // The crossbars run across the two edges of the modeled X floor. A
        // half-yalm inset leaves room for position and boundary uncertainty.
        foreach (float centerZ in new[] { 82.5f, 102.5f })
        {
            for (float px = 88.5f; px <= 111.5f; px += 0.5f)
            {
                for (float pz = centerZ - 1.5f; pz <= centerZ + 1.5f; pz += 0.5f)
                {
                    float tx = px - x, tz = pz - z, distance = tx * tx + tz * tz;
                    if (distance >= best || !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, x, z, px, pz) ||
                        !InterphosFloor.DirectionalKnockbackOnFloor(InterphosFloor.Layout.Cross, px, pz, dx, dz) ||
                        !Inset(px, pz) || !Inset(px + dx, pz + dz) || !acceptApproach(px, pz))
                    {
                        continue;
                    }

                    best = distance;
                    chosenX = px;
                    chosenZ = pz;
                }
            }
        }

        if (float.IsPositiveInfinity(best))
        {
            return false;
        }

        targetX = chosenX;
        targetZ = chosenZ;
        return true;
    }

    private static bool Inset(float x, float z) =>
        InterphosFloor.Contains(InterphosFloor.Layout.Cross, x - 0.5f, z) &&
        InterphosFloor.Contains(InterphosFloor.Layout.Cross, x + 0.5f, z) &&
        InterphosFloor.Contains(InterphosFloor.Layout.Cross, x, z - 0.5f) &&
        InterphosFloor.Contains(InterphosFloor.Layout.Cross, x, z + 0.5f);
}
