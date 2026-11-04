using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

/// <summary>Horizontal floor geometry for the normal Interphos arena.</summary>
/// <remarks>
/// Geometry describes modeled safe floor, not the client's navigation mesh. Callers must
/// identify the live floor transition and gravity state before using it for movement.
/// </remarks>
internal static class InterphosFloor
{
    internal enum Layout
    {
        Square, Cross, Split, Floating, Final
    }
    internal enum Side
    {
        Left, Right
    }

    // Legitimate Force's cast ID encodes both strikes. Repeated sides are valid;
    // do not assume that the second impact always switches halves.
    internal static bool TryForceSequence(uint action, out Side first, out Side second)
    {
        first = default;
        second = default;
        switch (action)
        {
            case 36638:
                first = Side.Left;
                second = Side.Left;
                return true;
            case 36639:
                first = Side.Left;
                second = Side.Right;
                return true;
            case 36640:
                first = Side.Right;
                second = Side.Right;
                return true;
            case 36641:
                first = Side.Right;
                second = Side.Left;
                return true;
            default:
                return false;
        }
    }

    // A missing, expired or malformed status must not claim movement. Emergency
    // avoidance keeps priority over a final-second stillness request.
    internal static bool ShouldHoldStill(float? secondsRemaining, bool movementAvailable, bool escaping) =>
        movementAvailable && !escaping && secondsRemaining.HasValue &&
        float.IsFinite(secondsRemaining.Value) && secondsRemaining.Value >= 0f && secondsRemaining.Value <= 1f;

    // Gaze sources are player-relative vectors. Sum unit vectors pointing away so
    // distant and nearby sources have equal influence. Ambiguous opposing or
    // coincident sources do not produce an invented facing direction.
    internal static bool TryGazeHeading(IEnumerable<(float X, float Z)> sources, out float heading)
    {
        heading = 0f;
        if (sources == null)
        {
            return false;
        }

        float awayX = 0f, awayZ = 0f;
        foreach (var (x, z) in sources)
        {
            float distance = MathF.Sqrt(x * x + z * z);
            if (!float.IsFinite(distance) || distance <= 0.01f)
            {
                return false;
            }

            awayX -= x / distance;
            awayZ -= z / distance;
        }
        if (MathF.Abs(awayX) + MathF.Abs(awayZ) < 0.01f)
        {
            return false;
        }

        heading = MathF.Atan2(awayX, awayZ);
        return true;
    }

    // The X floor has horizontal north/south crossbars and two diagonal arms,
    // centered at (100,92.5). The crossbars are 24.4 wide by 4.4 deep;
    // swapping these axes removes the staging floor needed for the lateral gust.
    internal static bool Contains(Layout layout, float x, float z)
    {
        return layout switch
        {
            Layout.Square => Math.Abs(x - 100f) <= 20f && Math.Abs(z - 100f) <= 20f,
            Layout.Cross => ContainsCross(x - 100f, z - 92.5f),
            Layout.Split => Math.Abs(z - 94f) <= 5.75f &&
                (Math.Abs(x - 92f) <= 3.25f || Math.Abs(x - 108f) <= 3.25f),
            Layout.Floating => Math.Abs(x - 100f) <= 12f && Math.Abs(z - 94f) <= 8f,
            Layout.Final => Math.Abs(x - 100f) <= 20f && Math.Abs(z - 105f) <= 15f,
            _ => false,
        };
    }

    private static bool ContainsCross(float x, float z)
    {
        const float halfWidth = 2.2f;
        const float diagonal = 0.70710678f;
        bool stem = Math.Abs(x) <= 12.2f &&
            (Math.Abs(z + 10f) <= halfWidth || Math.Abs(z - 10f) <= halfWidth);
        float forwardA = (x + z) * diagonal;
        float lateralA = (x - z) * diagonal;
        float forwardB = (x - z) * diagonal;
        float lateralB = (x + z) * diagonal;
        return stem || (Math.Abs(forwardA) <= 15f && Math.Abs(lateralA) <= halfWidth) ||
            (Math.Abs(forwardB) <= 15f && Math.Abs(lateralB) <= halfWidth);
    }

    // Endpoints alone do not prove a knockback or route stays on connected floor.
    // Dense sampling gives the planner a conservative check until collision captures
    // establish the runtime's exact boundaries and transition timing.
    internal static bool SegmentOnFloor(Layout layout, float x1, float z1, float x2, float z2)
    {
        float distance = MathF.Sqrt((x2 - x1) * (x2 - x1) + (z2 - z1) * (z2 - z1));
        int steps = Math.Max(1, (int)MathF.Ceiling(distance / 0.2f));
        for (int step = 0; step <= steps; step++)
        {
            float fraction = (float)step / steps;
            if (!Contains(layout, x1 + (x2 - x1) * fraction, z1 + (z2 - z1) * fraction))
            {
                return false;
            }
        }
        return true;
    }

    // Ordinary movement may resume while a previous avoid remains active. Checking
    // only the endpoint lets a combat approach cut through the hazard on its way back.
    internal static bool SegmentAvoids(float x1, float y1, float z1, float x2, float y2, float z2,
        Func<float, float, float, bool> isUnsafe)
    {
        float dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (!float.IsFinite(distance) || !float.IsFinite(dy) || distance > 200f || isUnsafe == null)
        {
            return false;
        }

        int steps = Math.Max(1, (int)MathF.Ceiling(distance * 2f));
        for (int step = 1; step <= steps; step++)
        {
            float fraction = (float)step / steps;
            if (isUnsafe(x1 + dx * fraction, y1 + dy * fraction, z1 + dz * fraction))
            {
                return false;
            }
        }
        return true;
    }

    // A knockback is unsafe if any portion of its displacement crosses missing floor,
    // even when its landing is on a different platform. A zero-distance radial source
    // has no defined direction, so it must never be treated as a confirmed safe point.
    internal static bool RadialKnockbackOnFloor(Layout layout, float x, float z,
        float originX, float originZ, float distance)
    {
        float dx = x - originX;
        float dz = z - originZ;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        if (!float.IsFinite(length) || length < 0.001f || !float.IsFinite(distance) || distance < 0f)
        {
            return false;
        }

        return SegmentOnFloor(layout, x, z, x + dx / length * distance, z + dz / length * distance);
    }

    // Directional gusts use the measured displacement vector, avoiding an assumed
    // heading convention at this geometry boundary.
    internal static bool DirectionalKnockbackOnFloor(Layout layout, float x, float z,
        float displacementX, float displacementZ)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(displacementX) || !float.IsFinite(displacementZ))
        {
            return false;
        }

        return SegmentOnFloor(layout, x, z, x + displacementX, z + displacementZ);
    }
}
