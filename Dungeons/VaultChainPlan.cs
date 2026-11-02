using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Choose a stable point beyond the actual paired actor's captured break radius.
// Other hazards are checked along the entire approach; make safe partial progress
// when a complete separation point is temporarily unreachable.
internal static class VaultChainPlan
{
    internal static Vector2? Select(Vector2 player, Vector2 partner, float required,
        Vector2? previous, Func<Vector2, bool> safePoint, Func<Vector2, Vector2, bool> safeSegment)
    {
        bool Valid(Vector2 p) => Vector2.Distance(p, partner) >= required + .5f &&
            safePoint(p) && safeSegment(player, p);
        if (previous.HasValue && Valid(previous.Value)) return previous;
        var candidates = new List<Vector2> { player };
        // Half a yalm beyond the radius accounts for the owner's arrival tolerance.
        // Additional radii and corners retain choices when a circle intersects a wall.
        foreach (float extra in new[] { .6f, 2.5f, 5f })
            for (int i = 0; i < 96; ++i)
            {
                double angle = Math.PI * 2 * i / 96;
                candidates.Add(partner + new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * (required + extra));
            }
        foreach (float x in new[] { -19f, 0f, 19f })
            foreach (float z in new[] { -15f, 4f, 23f })
                candidates.Add(new Vector2(x, z));
        var complete = candidates.OrderBy(p => Vector2.DistanceSquared(player, p)).Where(Valid)
            .Select(p => (Vector2?)p).FirstOrDefault();
        if (complete.HasValue) return complete;

        // A moving NPC can make the captured break radius temporarily larger than
        // the arena permits. On the November 2 capture this left Rowan stationary
        // until the chain expired. Make safe progress toward greater separation,
        // and keep retrying the complete goal as the partner and hazards move.
        // Both the point and the full approach still use the shared hazard check.
        float current = Vector2.Distance(player, partner);
        bool Progress(Vector2 p) => Vector2.Distance(p, partner) > current + .5f &&
            safePoint(p) && safeSegment(player, p);
        if (previous.HasValue && Progress(previous.Value)) return previous;
        for (float edge = -19f; edge <= 19f; edge += 2f)
        {
            candidates.Add(new Vector2(edge, -15f));
            candidates.Add(new Vector2(edge, 23f));
            candidates.Add(new Vector2(-19f, edge + 4f));
            candidates.Add(new Vector2(19f, edge + 4f));
        }
        return candidates.OrderByDescending(p => Vector2.DistanceSquared(p, partner)).Where(Progress)
            .Select(p => (Vector2?)p).FirstOrDefault();
    }
}
