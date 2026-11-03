using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Partition helpers retain the arena-facing heading for both left and right
// variants. The action selects which side of that axis is dangerous.
internal static class AlexandriaEliminatorDirections
{
    internal static bool TryGet(uint action, float heading, out Vector2 direction)
    {
        direction = default;
        if (!float.IsFinite(heading)) return false;
        var forward = new Vector2((float)Math.Sin(heading), (float)Math.Cos(heading));
        direction = action switch
        {
            39007 or 39238 => new Vector2(-forward.Y, forward.X),
            39008 or 39249 => new Vector2(forward.Y, -forward.X),
            _ => forward
        };
        return AlexandriaGeometry.Finite(direction);
    }
}
