using System;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Endless Swing pursues the player, including turns. In RB9548's September24
    // capture, past velocity pointed east while the player crossed west; the
    // spinner caught that crossing and knocked the player into the boundary.
    // Keep the corroborated five-yalm forward capsule aimed at its chase target,
    // extending it when the measured one-second travel is larger. The caller
    // retains the current contact disk and its half-yalm damage margin.
    internal static class TablitaurSpinPrediction
    {
        internal static Vector2 Lead(Vector2 origin, Vector2 player, Vector2 observedTravel)
        {
            var towardPlayer = player - origin;
            if (towardPlayer.LengthSquared() < .001f)
                return observedTravel;
            return Vector2.Normalize(towardPlayer) * Math.Max(5f, observedTravel.Length());
        }
    }
}
