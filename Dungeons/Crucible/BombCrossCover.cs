using System;
using System.Collections.Generic;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Big-bomb crosses use native circle rasterization because RB's polygon
    // raster rejects partly covered minimum-edge cells. September24's escape
    // repeatedly selected its own span for17s there. This cover keeps native
    // movement ownership; it does not introduce a safe-point or movement lease.
    internal static class BombCrossCover
    {
        internal const float HalfWidth = 4.5f; // Four-yalm damage plus the existing half-yalm margin.
        internal const float HalfLength = 40.5f;
        private const float MaximumSpacing = 4f;
        // Midway between centers, the circle must still cover the full padded
        // width. At a center it extends at most0.425y beyond the old rectangle.
        internal static readonly float Radius = (float)Math.Sqrt(HalfWidth * HalfWidth + MaximumSpacing * MaximumSpacing / 4);

        internal static IEnumerable<Vector2> Centers(Vector2 origin, float heading, Vector2 arena, float arenaRadius)
        {
            int intervals = (int)Math.Ceiling(2 * HalfLength / MaximumSpacing);
            float reach = arenaRadius + Radius;
            for (int arm = 0; arm < 2; arm++)
            {
                double angle = heading + arm * Math.PI / 2;
                var direction = new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle));
                for (int i = 0; i <= intervals; i++)
                {
                    var point = origin + direction * (-HalfLength + 2 * HalfLength * i / intervals);
                    // Discard only circles disjoint from the entire usable floor;
                    // retain their outside centers when their edge reaches inside.
                    if (Vector2.DistanceSquared(point, arena) <= reach * reach)
                        yield return point;
                }
            }
        }
    }
}
