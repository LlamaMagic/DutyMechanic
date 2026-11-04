using System;

namespace DutyMechanic.Dungeons;

/// <summary>Geometry for a single normal-mode Aethertithe cone.</summary>
/// <remarks>
/// The three reference cones are centered at -55, 0 and +55 degrees from
/// arena north-to-south and have a 35-degree half-angle. A caller must supply
/// a verified live sector, current arena bounds and other hazard validation;
/// this class neither reads client state nor owns movement.
/// </remarks>
internal static class InterphosAethertithe
{
    private const float HalfAngle = 35f * MathF.PI / 180f;
    private const float SideAngle = 55f * MathF.PI / 180f;
    private const float Margin = 0.5f;

    internal static bool TryAngle(int timelineIndex, out float angle)
    {
        angle = timelineIndex switch
        {
            10 => -SideAngle,
            11 => 0f,
            12 => SideAngle,
            _ => float.NaN
        };
        return float.IsFinite(angle);
    }

    internal static bool IsUnsafe(float x, float z, float originX, float originZ, float angle)
    {
        float dx = x - originX, dz = z - originZ;
        float radius = MathF.Sqrt(dx * dx + dz * dz);
        if (!float.IsFinite(radius) || !float.IsFinite(angle))
        {
            return true;
        }

        if (radius < 0.01f)
        {
            return true;
        }

        float relative = MathF.IEEERemainder(MathF.Atan2(dx, dz) - angle, 2f * MathF.PI);
        // An angular expansion models a half-yalm lateral safety margin even
        // near the cone's apex, where a fixed angular margin would be too small.
        return MathF.Abs(relative) <= HalfAngle + MathF.Atan2(Margin, radius);
    }

    /// <summary>Selects the shortest direct safe endpoint accepted by the caller.</summary>
    /// <remarks>
    /// First project onto either cone edge; a compact polar fallback supplies
    /// alternatives when an arena edge or another hazard blocks that projection.
    /// The caller checks the full path against its current floor and hazards.
    /// </remarks>
    internal static bool TryDestination(float x, float z, float originX, float originZ,
        float angle, Func<float, float, bool> validPath, out float targetX, out float targetZ)
    {
        targetX = targetZ = 0;
        if (validPath == null || !float.IsFinite(x) || !float.IsFinite(z) ||
            !float.IsFinite(originX) || !float.IsFinite(originZ) || !float.IsFinite(angle))
        {
            return false;
        }

        float dx = x - originX, dz = z - originZ, best = float.PositiveInfinity;
        float chosenX = 0, chosenZ = 0;
        void Consider(float px, float pz)
        {
            float tx = px - x, tz = pz - z, distanceSquared = tx * tx + tz * tz;
            if (!float.IsFinite(distanceSquared) || distanceSquared >= best || IsUnsafe(px, pz, originX, originZ, angle) ||
                !validPath(px, pz))
            {
                return;
            }

            best = distanceSquared;
            chosenX = px;
            chosenZ = pz;
        }
        foreach (int side in new[] { -1, 1 })
        {
            float edge = angle + side * HalfAngle;
            float ux = MathF.Sin(edge), uz = MathF.Cos(edge);
            float along = MathF.Max(0, dx * ux + dz * uz);
            // Step slightly beyond the expanded edge, avoiding an endpoint that
            // becomes unsafe from floating-point or position-report rounding.
            Consider(originX + along * ux + side * MathF.Cos(edge) * 0.8f,
                originZ + along * uz - side * MathF.Sin(edge) * 0.8f);
        }
        if (float.IsPositiveInfinity(best))
        {
            for (int degrees = -90; degrees <= 90; degrees += 5)
            {
                float ray = degrees * MathF.PI / 180f;
                for (int radius = 2; radius <= 40; radius += 2)
                {
                    Consider(originX + MathF.Sin(ray) * radius, originZ + MathF.Cos(ray) * radius);
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
}
