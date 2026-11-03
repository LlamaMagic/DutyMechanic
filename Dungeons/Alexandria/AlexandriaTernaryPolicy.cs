using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// While the first Ternary Charge circle is unresolved, ordinary combat travel
// must not undo native avoidance by cutting back through that circle.
internal static class AlexandriaTernaryPolicy
{
    internal static bool Allows(Vector2 from, Vector2 to, Vector2 origin, float radius)
    {
        var start = from - origin;
        var delta = to - from;
        float radiusSquared = radius * radius;
        if (start.LengthSquared() <= radiusSquared)
        {
            // A character already inside may continue directly outward, never
            // be held there or travel deeper into the danger.
            return Vector2.Dot(start, delta) >= 0 && (to - origin).LengthSquared() > start.LengthSquared();
        }
        float lengthSquared = delta.LengthSquared();
        if (lengthSquared < .0001f) return true;
        float fraction = Math.Clamp(-Vector2.Dot(start, delta) / lengthSquared, 0, 1);
        return (start + delta * fraction).LengthSquared() > radiusSquared;
    }
}
