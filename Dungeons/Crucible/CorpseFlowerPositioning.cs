using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Nearby briars can leave the player within suction range. Distance from
    // the flower is therefore the shelter's primary safety condition. Bee
    // alignment is only a tie-breaker; melee uptime cannot shorten that distance.
    internal static class CorpseFlowerPositioning
    {
        internal static Vector2? Shelter(IEnumerable<Vector2> patches, Vector2 boss,
            Vector2 player, Func<Vector2, bool> safe, Func<Vector2, float> alignment) =>
            patches.Where(safe).OrderByDescending(p => Vector2.DistanceSquared(p, boss))
                .ThenBy(alignment).ThenBy(p => Vector2.DistanceSquared(p, player))
                .Select(p => (Vector2?)p).FirstOrDefault();

        // No guessed thorn radius: use the live Briar aura as the exit receipt.
        // Short checked legs increase clearance from every visible patch while
        // never moving toward the flower's follow-up Devour. This is deliberate
        // shelter egress, not an alternative owner for emergency avoidance.
        internal static Vector2? Exit(IEnumerable<Vector2> candidates, Vector2[] patches,
            Vector2 boss, Vector2 player, Func<Vector2, bool> safe,
            Func<Vector2, Vector2, bool> corridor)
        {
            if (patches.Length == 0)
            {
                return null;
            }

            float Clearance(Vector2 p) => patches.Min(b => Vector2.Distance(p, b));
            float current = Clearance(player), bossDistance = Vector2.Distance(player, boss);
            return candidates.Where(p => Vector2.Distance(p, player) >= 1 && Vector2.Distance(p, player) <= 4 &&
                    Vector2.Distance(p, boss) >= bossDistance && Clearance(p) > current + .5f && safe(p) && corridor(player, p))
                .OrderByDescending(Clearance).ThenBy(p => Vector2.DistanceSquared(p, player))
                .Select(p => (Vector2?)p).FirstOrDefault();
        }
    }
}
