using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Validate the actual next movement leg, including both endpoints. Half-yalm
// sampling is finer than the narrowest confirmed Vault boss hazard and lets the
// native path provider propose a detour when a direct combat approach is unsafe.
internal static class VaultSegmentSafety
{
    internal static bool Clear(Vector2 from, Vector2 to, Func<Vector2, bool> safePoint)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) / .5f));
        for (int i = 0; i <= steps; ++i)
            if (!safePoint(Vector2.Lerp(from, to, (float)i / steps))) return false;
        return true;
    }
}
