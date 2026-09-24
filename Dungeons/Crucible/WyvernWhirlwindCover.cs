using System;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // RB7696 stopped ahead of a small whirlwind at15:41:42 and was struck a
    // second later. Its measured travel reached2.85 y/s, while the old disk
    // covered only the current body. Warn one second ahead at3 y/s, retaining
    // the current/rear footprint. Native circles also preserve stable source
    // identity without the polygon edge-cell run/done oscillation.
    internal static class WyvernWhirlwindCover
    {
        internal static Vector2[] Offsets(bool large, out float radius)
        {
            float length = (large ? 3.5f : 2.5f) + 3f;
            int segments = (int)Math.Ceiling(length);
            float spacing = length / segments;
            float padded = (large ? 3f : 2f) + .5f;
            // At the midpoint between samples, retain the full half-yalm
            // lateral margin; an uninflated union would scallop that margin.
            radius = (float)Math.Sqrt(padded * padded + spacing * spacing / 4);
            var result = new Vector2[segments + 1];
            for (int i = 0; i <= segments; i++) result[i] = new Vector2(0, i * spacing);
            return result;
        }
    }
}
