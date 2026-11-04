using System;

namespace DutyMechanic.Dungeons;

/// <summary>Reference lane geometry for normal Interphos Castellation.</summary>
/// <remarks>
/// This model is not a live state decoder: packet indices and RB's retained map
/// effects must be correlated before its output can own movement. Sequence is the
/// number of previously recognized wall formations in the current encounter.
/// </remarks>
internal static class InterphosWalls
{
    internal readonly record struct Lane(float CenterX, float Width);

    internal static Lane[] Decode(uint state, int sequence)
    {
        if (sequence < 0)
        {
            return Array.Empty<Lane>();
        }

        bool normal = sequence != 1;
        return state switch
        {
            0x00200010 when normal => [new(114, 12), new(90, 8), new(101, 10), new(82, 4)],
            0x00200010 => [new(116, 8), new(94, 8), new(105, 10), new(84, 8)],
            0x02000100 when normal => [new(95, 10), new(111, 18), new(84, 8)],
            0x08000400 when normal => [new(116, 8), new(101, 18), new(85, 10)],
            0x00800040 when normal => [new(116, 8), new(105, 10), new(94, 8), new(84, 8)],
            0x00800040 => [new(114, 12), new(101, 10), new(90, 8), new(82, 4)],
            _ => Array.Empty<Lane>(),
        };
    }

    // Return false when no authoritative layout is known: absence of decoded
    // lanes is not evidence that a gap is safe.
    internal static bool IsSafeX(Lane[] lanes, float x, float margin)
    {
        if (lanes == null || lanes.Length == 0 || !float.IsFinite(x) ||
            !float.IsFinite(margin) || margin < 0f)
        {
            return false;
        }

        foreach (var lane in lanes)
        {
            if (Math.Abs(x - lane.CenterX) <= lane.Width / 2f + margin)
            {
                return false;
            }
        }

        return true;
    }

    // Castellation requires occupying an opening. Select a nearby same-depth
    // candidate only when the full route remains on the modeled floor and the
    // caller verifies other active hazards. Unknown walls or collision are not safe.
    internal static bool TryFindOpening(Lane[] lanes, InterphosFloor.Layout layout,
        float playerX, float playerZ, float margin, Func<float, float, bool> otherUnsafe,
        out float destinationX)
    {
        destinationX = playerX;
        if (lanes == null || lanes.Length == 0 || otherUnsafe == null ||
            !float.IsFinite(playerX) || !float.IsFinite(playerZ))
        {
            return false;
        }

        if (IsSafeX(lanes, playerX, margin) && !otherUnsafe(playerX, playerZ) &&
            InterphosFloor.Contains(layout, playerX, playerZ))
        {
            return true;
        }

        // Search by increasing travel distance to retain natural positioning.
        // The bound covers the arena without accepting an off-platform detour.
        for (int step = 1; step <= 80; step++)
        {
            float offset = step * 0.5f;
            foreach (float x in new[] { playerX - offset, playerX + offset })
            {
                if (!IsSafeX(lanes, x, margin) || otherUnsafe(x, playerZ) ||
                    !InterphosFloor.SegmentOnFloor(layout, playerX, playerZ, x, playerZ))
                {
                    continue;
                }

                if (!InterphosFloor.SegmentAvoids(playerX, 0, playerZ, x, 0, playerZ,
                    (px, _, pz) => otherUnsafe(px, pz)))
                {
                    continue;
                }

                destinationX = x;
                return true;
            }
        }
        return false;
    }
}
