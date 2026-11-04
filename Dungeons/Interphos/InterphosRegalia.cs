using System;

namespace DutyMechanic.Dungeons;

/// <summary>Floor-connected escape from Ruthless Regalia's moving line.</summary>
/// <remarks>
/// Captures identify base 0x41C2 and timeline 0x11D2 as the line's tell. The
/// 100-by-12-yalm line follows its helper's live position and heading. Native
/// escape crossed gaps in the X floor, so destinations require a direct floor
/// segment validated by the encounter's movement owner.
/// </remarks>
internal static class InterphosRegalia
{
    internal const int TellTimeline = 0x11D2;
    // Start 3.5 seconds before the earliest observed impact (tell + 11.1s).
    // Keep tracking through 12.1s: square and X-floor damage arrived as late
    // as 11.838s and 11.874s while the helper was still moving. This leaves
    // one polling interval of margin and ends before the later Downburst.
    internal static bool ShouldPosition(double elapsed, bool square = false) =>
        double.IsFinite(elapsed) && elapsed >= 7.6 && elapsed < 12.1;

    internal static bool Unsafe(float x, float z, float originX, float originZ,
        float heading, float margin = 0.5f)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(originX) ||
            !float.IsFinite(originZ) || !float.IsFinite(heading))
        {
            return true;
        }

        float dx = x - originX, dz = z - originZ;
        float along = dx * MathF.Sin(heading) + dz * MathF.Cos(heading);
        float across = dx * MathF.Cos(heading) - dz * MathF.Sin(heading);
        return along >= -margin && along <= 100 + margin && MathF.Abs(across) <= 6 + margin;
    }

    internal static bool TryPosition(float x, float z, float originX, float originZ,
        float heading, Func<float, float, bool> acceptPath, out float targetX, out float targetZ,
        InterphosFloor.Layout layout = InterphosFloor.Layout.Cross)
    {
        targetX = targetZ = 0;
        if (!float.IsFinite(x) || !float.IsFinite(z) || acceptPath == null ||
            layout is not (InterphosFloor.Layout.Cross or InterphosFloor.Layout.Square))
        {
            return false;
        }
        // Add 0.25 yalms of arrival tolerance to the half-yalm hazard margin.
        bool Safe(float px, float pz) => !Unsafe(px, pz, originX, originZ, heading, 0.75f);
        if (Safe(x, z) && InterphosFloor.Contains(layout, x, z) && acceptPath(x, z))
        {
            targetX = x;
            targetZ = z;
            return true;
        }
        float best = float.PositiveInfinity, bx = 0, bz = 0;
        // The second captured Coronation repeats the same line on square floor
        // during Aethertithe. The caller validates that live layout and overlap;
        // do not keep the X-floor search bounds after the actual square return.
        float minX = layout == InterphosFloor.Layout.Cross ? 87.5f : 80.5f;
        float maxX = layout == InterphosFloor.Layout.Cross ? 112.5f : 119.5f;
        float maxZ = layout == InterphosFloor.Layout.Cross ? 104.5f : 119.5f;
        for (float px = minX; px <= maxX; px += 0.25f)
        {
            for (float pz = 80.5f; pz <= maxZ; pz += 0.25f)
            {
                float d = (px - x) * (px - x) + (pz - z) * (pz - z);
                if (d >= best || !Safe(px, pz) ||
                    !InterphosFloor.SegmentOnFloor(layout, x, z, px, pz) ||
                    !acceptPath(px, pz))
                {
                    continue;
                }

                best = d;
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
