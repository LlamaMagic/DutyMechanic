using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Pick one stable spread destination that also satisfies the active sphere
// hazards. If the overlap has no safe route, wait for the earlier sphere impact
// rather than allowing a spread request to steer through the explosion.
internal static class VaultSpreadPlan
{
    internal static Vector2? Select(Vector2 player, IReadOnlyList<Vector2> participants, Vector2? previous,
        Func<Vector2, bool> safePoint, Func<Vector2, Vector2, bool> safeSegment)
    {
        bool Valid(Vector2 p) => participants.All(a => Vector2.Distance(p, a) >= 6f) &&
            safePoint(p) && safeSegment(player, p);
        if (previous.HasValue && Valid(previous.Value)) return previous;
        var candidates = new List<Vector2> { player };
        // The blast radius is five; one extra yalm includes half a yalm for
        // avoidance clearance and half for the movement owner's arrival tolerance.
        foreach (float distance in new[] { 2f, 4f, 6f, 8f, 12f, 16f })
            for (int i = 0; i < 48; ++i)
            {
                double angle = Math.PI * 2 * i / 48;
                candidates.Add(player + new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * distance);
            }
        return candidates.OrderBy(p => Vector2.DistanceSquared(player, p)).Where(Valid)
            .Select(p => (Vector2?)p).FirstOrDefault();
    }
}
