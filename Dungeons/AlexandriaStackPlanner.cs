using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// The movement adapter supplies current party and hazard snapshots, arbitrates
// input, and retains each marked stack through its observed lifecycle.
internal static class AlexandriaStackPlanner
{
    // The final-boss instant marker target is not exposed by the captured RB
    // actor state. Evaluate every living member as the possible carrier, including
    // the player. This shares the actual source-to-carrier line without treating
    // an unrelated ally as a proxy for safety. Width six leaves 0.5y clearance.
    internal static Vector2? ChooseLine(Vector2 player, Vector2 source,
        IReadOnlyList<Vector2> others, Func<Vector2, bool> safe,
        Func<Vector2, Vector2, bool> reachable)
    {
        if (others == null || others.Count == 0) return null;
        int Coverage(Vector2 candidate)
        {
            var members = others.Concat(new[] { candidate }).ToArray();
            int worst = members.Length;
            foreach (var carrier in members)
            {
                var axis = carrier - source;
                if (axis.LengthSquared() < .25f) return 0;
                axis = Vector2.Normalize(axis);
                int count = members.Count(p =>
                {
                    var delta = p - source;
                    float along = Vector2.Dot(delta, axis);
                    return along >= 0 && along <= 39 && Math.Abs(delta.X * axis.Y - delta.Y * axis.X) <= 2.5f;
                });
                worst = Math.Min(worst, count);
            }
            return worst;
        }
        var candidates = new List<Vector2> { player };
        var center = others.Aggregate(Vector2.Zero, (sum, p) => sum + p) / others.Count;
        foreach (var anchor in others.Concat(new[] { center }))
            for (int z = -4; z <= 4; ++z)
                for (int x = -4; x <= 4; ++x)
                    candidates.Add(anchor + new Vector2(x, z));
        return candidates.Where(p => safe(p) && reachable(player, p))
            .Select(p => (Point: p, Count: Coverage(p)))
            .Where(p => p.Count >= 2)
            .OrderByDescending(p => p.Count)
            .ThenBy(p => Vector2.DistanceSquared(p.Point, player))
            .Select(p => (Vector2?)p.Point).FirstOrDefault();
    }

    internal static bool KeepSuperbolt(bool casting, bool markerSeen, bool markerPresent, double secondsSinceCastEnd)
    {
        if (secondsSinceCastEnd >= 3) return false;
        if (casting || secondsSinceCastEnd < .35) return true;
        return markerSeen ? markerPresent : secondsSinceCastEnd < 2.5;
    }

    internal static Vector2? Choose(Vector2 player, bool selfMarked, Vector2 marked,
        IReadOnlyList<Vector2> participants, Func<Vector2, bool> safe,
        Func<Vector2, Vector2, bool> reachable, int selfRequired = 2)
    {
        if (participants == null || safe == null || reachable == null ||
            selfRequired < 1 || (selfMarked && participants.Count == 0))
            return null;

        // Radius six with a 1y arrival tolerance and half-yalm clearance.
        int Coverage(Vector2 point) => selfMarked
            ? participants.Count(member => Vector2.Distance(member, point) <= 4.5f)
            : Vector2.Distance(marked, point) <= 4.5f ? 1 : 0;
        var centers = selfMarked ? participants : new[] { marked };
        var candidates = new List<Vector2> { player };
        foreach (var center in centers)
        {
            candidates.Add(center);
            for (int i = 0; i < 16; ++i)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(center + new Vector2(4f * (float)Math.Sin(angle), 4f * (float)Math.Cos(angle)));
            }
        }
        // If the group is still scattered, move toward the best available
        // participation instead of leaving an isolated marked player behind.
        // Coverage is re-evaluated as members converge on later pulses.
        return candidates.Where(point => Coverage(point) >= 1 && safe(point) && reachable(player, point))
            .OrderByDescending(point => Math.Min(Coverage(point), selfMarked ? selfRequired : 1))
            .ThenByDescending(Coverage).ThenBy(point => Vector2.DistanceSquared(point, player))
            .Select(point => (Vector2?)point).FirstOrDefault();
    }
}
