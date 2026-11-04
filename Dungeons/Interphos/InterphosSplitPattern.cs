using System;

namespace DutyMechanic.Dungeons;

/// <summary>Observed south entry and reference wall openings for split floor.</summary>
internal static class InterphosSplitPattern
{
    internal const float EntryX = 100f, EntryZ = 101.5f;
    private const float WallMargin = 0.5f;

    // The captured native force escape left the modeled floor. Use inner solid
    // platform points so the opposite second strike is reachable at normal speed.
    // Side is the dangerous half; zero requests recovery to the nearest platform.
    internal static bool TryPlatformPosition(float x, float z, int side, InterphosFloor.Layout layout,
        Func<float, float, bool>[] hazards, out float targetX, out float targetZ)
    {
        targetX = targetZ = 0;
        if (side < -1 || side > 1 || hazards == null)
        {
            return false;
        }

        float best = float.PositiveInfinity;
        foreach (float px in new[] { 94.75f, 105.25f })
        {
            if (side != 0 && (px - 100f) * side >= 0)
            {
                continue;
            }

            float pz = Math.Clamp(z, 89f, 99f);
            if (!InterphosFloor.Contains(InterphosFloor.Layout.Split, px, pz) ||
                !InterphosFloor.SegmentOnFloor(layout, x, z, px, pz))
            {
                continue;
            }

            bool clear = true;
            foreach (var hazard in hazards)
            {
                if (!AvoidsReentry(x, z, px, pz, hazard))
                {
                    clear = false;
                    break;
                }
            }

            if (!clear)
            {
                continue;
            }

            float distance = (px - x) * (px - x) + (pz - z) * (pz - z);
            if (distance >= best)
            {
                continue;
            }

            best = distance;
            targetX = px;
            targetZ = pz;
        }
        return !float.IsPositiveInfinity(best);
    }

    internal static uint FullWallState(ushort state) => state switch
    {
        0x10 => 0x00200010,
        0x100 => 0x02000100,
        0x400 => 0x08000400,
        0x40 => 0x00800040,
        _ => 0
    };

    internal static bool ObserveWall(ushort state, ref ushort lastState, ref int sequence, out InterphosWalls.Lane[] lanes,
        bool sequenceKnown = true)
    {
        lanes = [];
        if (state == lastState)
        {
            return false;
        }

        lastState = state;
        uint full = FullWallState(state);
        if (full == 0)
        {
            return false;
        }
        // Recovery can lose the earlier wall count. States 0x100/0x400 identify
        // one formation; 0x10/0x40 each identify two. Decode only unambiguous
        // tells without claiming to know the sequence number.
        if (!sequenceKnown && state is not (0x100 or 0x400))
        {
            return false;
        }

        lanes = InterphosWalls.Decode(full, sequenceKnown ? sequence : 0);
        if (lanes.Length == 0)
        {
            return false;
        }

        if (sequenceKnown)
        {
            sequence++;
        }

        return true;
    }

    // After the forced shift, the captured party passed from the south staging
    // point through this central strip while gravity 3814 was present. This
    // deliberately does not authorize arbitrary off-platform recovery paths.
    internal static bool EntrySegment(float x, float z, float targetX, float targetZ)
    {
        float dx = targetX - x, dz = targetZ - z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (!float.IsFinite(distance) || distance > 20 || z > 117 || z < 101.5f)
        {
            return false;
        }

        int steps = Math.Max(1, (int)MathF.Ceiling(distance / 0.2f));
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps, px = x + dx * t, pz = z + dz * t;
            if (pz > 102f)
            {
                if (MathF.Abs(px - 100f) > 1.5f || pz > 117f)
                {
                    return false;
                }
            }
            else if (!InterphosFloor.Contains(InterphosFloor.Layout.Floating, px, pz))
            {
                return false;
            }
        }
        return true;
    }

    // While gravity permits travel over the center gap, choose a wall opening
    // that also lands inside a real split platform. The arrival margin keeps the
    // mover's tolerance away from the platform edge and the wall hitboxes.
    internal static bool TryWallPosition(float x, float z, InterphosFloor.Layout pathLayout, InterphosWalls.Lane[] lanes,
        Func<float, float, bool>[] hazards, out float targetX, out float targetZ)
    {
        targetX = targetZ = 0;
        if (lanes == null || lanes.Length == 0 || hazards == null ||
            pathLayout is not (InterphosFloor.Layout.Floating or InterphosFloor.Layout.Split) ||
            !InterphosFloor.Contains(pathLayout, x, z))
        {
            return false;
        }

        float best = float.PositiveInfinity, bx = 0, bz = 0;
        float pz = Math.Clamp(z, 88.75f, 99.25f);
        for (float px = 89f; px <= 111f; px += 0.25f)
        {
            if (!ValidWallPosition(x, z, px, pz, pathLayout, lanes, hazards))
            {
                continue;
            }

            float dx = px - x, dz = pz - z, distance = dx * dx + dz * dz;
            if (distance >= best)
            {
                continue;
            }

            best = distance;
            bx = px;
            bz = pz;
        }
        if (float.IsPositiveInfinity(best))
        {
            return false;
        }

        targetX = bx;
        targetZ = bz;
        return true;
    }

    internal static bool ValidWallPosition(float x, float z, float px, float pz, InterphosFloor.Layout pathLayout,
        InterphosWalls.Lane[] lanes, Func<float, float, bool>[] hazards)
    {
        if (hazards == null || !((px >= 89f && px <= 95f) || (px >= 105f && px <= 111f)) ||
            pz < 88.75f || pz > 99.25f || !InterphosWalls.IsSafeX(lanes, px, WallMargin) ||
            pathLayout is not (InterphosFloor.Layout.Floating or InterphosFloor.Layout.Split) ||
            !InterphosFloor.SegmentOnFloor(pathLayout, x, z, px, pz))
        {
            return false;
        }
        // Check each hazard independently so exiting one cannot mask entering another.
        foreach (var hazard in hazards)
        {
            if (!AvoidsReentry(x, z, px, pz, hazard))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool AvoidsReentry(float x, float z, float px, float pz, Func<float, float, bool> otherUnsafe)
    {
        if (otherUnsafe == null || otherUnsafe(px, pz))
        {
            return false;
        }

        float dx = px - x, dz = pz - z, distance = MathF.Sqrt(dx * dx + dz * dz);
        if (!float.IsFinite(distance))
        {
            return false;
        }
        // Permit leaving an already occupied independent hazard but reject
        // entering or re-entering one on the way to the wall opening.
        bool escaped = !otherUnsafe(x, z);
        int steps = Math.Max(1, (int)MathF.Ceiling(distance * 2));
        for (int step = 1; step <= steps; step++)
        {
            float t = (float)step / steps;
            bool unsafePoint = otherUnsafe(x + dx * t, z + dz * t);
            if (!unsafePoint)
            {
                escaped = true;
            }
            else if (escaped)
            {
                return false;
            }
        }
        return true;
    }
}
