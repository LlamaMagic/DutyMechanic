using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// The encounter reference describes 13y, but two November 2 captures moved
// Rowan about 16.5y from her last stationary staging point to the observed
// landing. Check the whole 17y envelope, including a margin for that observed
// displacement and possible residual input. The approach and forced path must
// both remain on verified floor; arrival tolerance is handled by the caller.
internal static class VaultKnockbackPlan
{
    // Some client frames omit the forced-movement flag during the captured
    // landing. A post-bar jump over five yalms independently signals handoff.
    internal static bool Delivered(bool casting, bool forced, Vector2? lastCasting, Vector2 current) =>
        !casting && (forced || lastCasting.HasValue && Vector2.Distance(lastCasting.Value, current) > 5f);

    internal static Vector2? Select(Vector2 player, Vector2 origin, Vector2? previous,
        Func<Vector2, bool> safePoint, Func<Vector2, Vector2, bool> safeSegment)
    {
        bool Valid(Vector2 point)
        {
            if (!safePoint(point) || !safeSegment(player, point)) return false;
            // Arrival can stop anywhere within 0.5y of the selected point.
            // Validate that entire tolerance in eight directions as well as the
            // center, including the full forced path from every sampled arrival.
            for (int i = -1; i < 8; ++i)
            {
                double angle = Math.PI * 2 * i / 8;
                var offset = i < 0 ? Vector2.Zero : new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * .5f;
                var staged = point + offset;
                var delta = staged - origin;
                float distance = delta.Length();
                if (distance < .45f || distance > 2.5f) return false;
                var landing = staged + delta / distance * 17f;
                if (!safePoint(staged) || !safePoint(landing) || !safeSegment(staged, landing)) return false;
            }
            return true;
        }

        // Do not churn a valid choice as the player and party move; stability is
        // needed to arrive before the cast resolves and to avoid stop/start loops.
        if (previous.HasValue && Valid(previous.Value)) return previous;
        var candidates = new List<Vector2> { player };
        foreach (float radius in new[] { 1f, 1.25f, 1.5f })
            for (int i = 0; i < 48; i++)
            {
                double angle = Math.PI * 2 * i / 48;
                candidates.Add(origin + new Vector2((float)Math.Sin(angle), (float)Math.Cos(angle)) * radius);
            }
        return candidates.OrderBy(p => Vector2.DistanceSquared(player, p)).Where(Valid)
            .Select(p => (Vector2?)p).FirstOrDefault();
    }
}
