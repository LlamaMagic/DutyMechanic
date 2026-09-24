using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Slash follows its marked target, so an ordinary player-centered avoid
    // chases itself. Separate the pet from the boss-to-player line instead:
    // RB11440 lost pets to the direct cleave plus the redirected Cover hit.
    internal static class TablitaurSlashBait
    {
        internal static bool PetIsClear(Vector2 boss, Vector2 player, Vector2 pet, float petReach)
        {
            Vector2 direction = player - boss;
            if (direction.LengthSquared() < .01f) return false;
            direction = Vector2.Normalize(direction);
            Vector2 offset = pet - boss;
            float forward = Vector2.Dot(offset, direction);
            // Native width8 and length65; half-yalm clearance plus pet hitbox.
            float margin = .5f + petReach;
            float side = System.Math.Abs(offset.X * direction.Y - offset.Y * direction.X);
            return forward < -margin || forward > 65 + margin || side > 4 + margin;
        }
    }
}
