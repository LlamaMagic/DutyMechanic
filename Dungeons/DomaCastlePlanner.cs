using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;

namespace DutyMechanic.Dungeons;

// Candidate selection is separate from input ownership so that an unsafe or
// unreachable destination cannot acquire the mover. The caller supplies a
// snapshot of live hazards and must revalidate it while executing the request.
internal static class DomaCastlePlanner
{
    // Reserve a tower only when an ally's measured approach can plausibly reach
    // its six-yalm circle before impact. This estimates assignment; the caller
    // must keep checking actual positions and path hazards on every pulse.
    internal static Vector3? ApproachedTower(Vector3 previous, Vector3 current, float elapsed,
        IEnumerable<Vector3> towers, float remaining)
    {
        if (elapsed < 0.25f || elapsed > 3f || remaining <= 0f) return null;
        return towers.Select(tower => new
            {
                Tower = tower,
                Distance = Distance(current, tower),
                Speed = (Distance(previous, tower) - Distance(current, tower)) / elapsed
            })
            .Where(candidate => candidate.Speed >= 1.5f && candidate.Speed <= 9f &&
                Math.Max(0, candidate.Distance - 6f) / candidate.Speed <= remaining)
            .OrderBy(candidate => Math.Max(0, candidate.Distance - 6f) / candidate.Speed)
            .Select(candidate => (Vector3?)candidate.Tower).FirstOrDefault();
    }

    internal static Vector3? UnoccupiedTower(Vector3 player, IEnumerable<Vector3> towers,
        IEnumerable<Vector3> allies, Func<Vector3, bool> safe, float travelBudget)
    {
        var party = allies.ToArray();
        // The encounter reference models a six-yalm tower. Count allies across
        // that full radius, but target inside five to retain a one-yalm margin.
        var candidates = new List<Vector3>();
        foreach (var tower in towers.Where(tower => !party.Any(ally => Distance(ally, tower) <= 6f)))
        {
            if (Distance(player, tower) <= 5f) candidates.Add(player);
            candidates.Add(tower);
            foreach (float radius in new[] { 2f, 4f, 5f })
                for (int i = 0; i < 16; i++)
                {
                    double angle = i * Math.PI / 8;
                    candidates.Add(new Vector3(tower.X + radius * (float)Math.Cos(angle), player.Y,
                        tower.Z + radius * (float)Math.Sin(angle)));
                }
        }
        return candidates.Where(point => Distance(player, point) <= travelBudget && SegmentSafe(player, point, safe))
            .OrderBy(point => Distance(player, point)).Select(point => (Vector3?)point).FirstOrDefault();
    }

    // Prey marks the player before the falloff origin is fixed. Aim the bait
    // southwest, opposite the Duty Support party's observed northeast position;
    // a blocked straight segment must not override another active hazard.
    internal static Vector3? ChargeBaitDestination(Vector3 player, Vector3 arena, Func<Vector3, bool> safe)
    {
        var corner = new Vector3(arena.X - 18f, player.Y, arena.Z + 18f);
        var candidates = new List<Vector3> { player };
        for (float x = -18f; x <= 18f; x += 2f)
            for (float z = -18f; z <= 18f; z += 2f)
                candidates.Add(new Vector3(arena.X + x, player.Y, arena.Z + z));
        return candidates.Where(point => SegmentSafe(player, point, safe))
            .OrderBy(point => Distance(point, corner)).ThenBy(point => Distance(player, point))
            .Select(point => (Vector3?)point).FirstOrDefault();
    }

    internal static Vector3? ChargeDestination(Vector3 player, Vector3 origin, Vector3 arena,
        float arenaRadius, float travelBudget, Func<Vector3, bool> safe)
    {
        // Charge damage falls with distance; maximize separation without making
        // the whole arena an impassable avoid. Include the present location so a
        // failed search never invents a reason to cross an unsafe lane.
        var candidates = new List<Vector3> { player };
        for (float radius = 2f; radius <= travelBudget; radius += 2f)
            for (int i = 0; i < 24; i++)
            {
                double angle = i * Math.PI / 12;
                candidates.Add(new Vector3(player.X + radius * (float)Math.Cos(angle), player.Y,
                    player.Z + radius * (float)Math.Sin(angle)));
            }
        return candidates.Where(point => Math.Abs(point.X - arena.X) <= arenaRadius &&
                Math.Abs(point.Z - arena.Z) <= arenaRadius &&
                SegmentSafe(player, point, safe))
            .OrderByDescending(point => Distance(point, origin))
            .ThenBy(point => Distance(point, player))
            .Select(point => (Vector3?)point).FirstOrDefault();
    }

    // Out-of-combat travel can cycle through real movement without progress.
    // Require sustained movement in a small envelope; stationary waits alone
    // must not be mistaken for a failed path.
    internal static bool ConfinedTravel(IReadOnlyList<Vector3> samples, float maximumExtent, float minimumTravel)
    {
        if (samples.Count < 2) return false;
        float minX = samples.Min(p => p.X), maxX = samples.Max(p => p.X);
        float minZ = samples.Min(p => p.Z), maxZ = samples.Max(p => p.Z);
        float travel = 0;
        for (int i = 1; i < samples.Count; i++) travel += Distance(samples[i - 1], samples[i]);
        return maxX - minX <= maximumExtent && maxZ - minZ <= maximumExtent && travel >= minimumTravel;
    }

    // Keep a consistent orbit around the boss. Pull an edge position toward
    // a 16.5-yalm radius so repeated short legs do not bounce at arena walls.
    // Callers retain the chosen direction until the sequence ends.
    internal static Vector3? GunsawOrbitLeg(Vector3 player, Vector3 boss, Vector3 arena,
        float arenaHalfSize, int turn, Func<Vector3, bool> safe)
    {
        if (turn != -1 && turn != 1) return null;
        float dx = player.X - boss.X, dz = player.Z - boss.Z;
        float radius = (float)Math.Sqrt(dx * dx + dz * dz);
        if (radius < 1f) return null;
        float desiredRadius = Math.Max(6f, Math.Min(16.5f, radius));
        double angle = Math.Atan2(dz, dx);
        foreach (double step in new[] { 0.35, 0.25, 0.15 })
        {
            double next = angle + turn * step;
            var point = new Vector3(boss.X + desiredRadius * (float)Math.Cos(next), player.Y,
                boss.Z + desiredRadius * (float)Math.Sin(next));
            if (Math.Abs(point.X - arena.X) <= arenaHalfSize && Math.Abs(point.Z - arena.Z) <= arenaHalfSize &&
                Distance(player, point) >= 2f && SegmentSafe(player, point, safe)) return point;
        }
        return null;
    }

    internal static int GunsawOrbitDirection(Vector3 player, Vector3 boss, Vector3 arena,
        float arenaHalfSize, Func<Vector3, bool> safe)
    {
        // Compare three safe legs in each direction, retaining a choice for the
        // whole sequence instead of reselecting opposite tangents every pulse.
        int best = 0, bestSteps = 0;
        foreach (int turn in new[] { -1, 1 })
        {
            Vector3 cursor = player;
            int steps = 0;
            for (; steps < 3; steps++)
            {
                var next = GunsawOrbitLeg(cursor, boss, arena, arenaHalfSize, turn, safe);
                if (!next.HasValue) break;
                cursor = next.Value;
            }
            if (steps > bestSteps) { best = turn; bestSteps = steps; }
        }
        return best;
    }

    internal static bool InGunsawBaitLine(Vector3 player, Vector3 origin, float heading)
    {
        float dx = player.X - origin.X, dz = player.Z - origin.Z;
        float forward = dx * (float)Math.Sin(heading) + dz * (float)Math.Cos(heading);
        float lateral = dx * (float)Math.Cos(heading) - dz * (float)Math.Sin(heading);
        // A wider activation band anticipates the narrow line while the boss is
        // turning, rather than waiting for the player's hitbox to be inside it.
        return forward > 0 && forward <= 61f && Math.Abs(lateral) <= 3.5f;
    }

    internal static bool SegmentSafe(Vector3 from, Vector3 to, Func<Vector3, bool> safe)
    {
        // Half-yalm samples bound the gap between checks; callers must still
        // supply hazards with a margin and reject stale snapshots.
        int steps = Math.Max(1, (int)Math.Ceiling(Distance(from, to) / 0.5f));
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            if (!safe(new Vector3(from.X + (to.X - from.X) * t, from.Y,
                    from.Z + (to.Z - from.Z) * t))) return false;
        }
        return true;
    }

    private static float Distance(Vector3 a, Vector3 b)
    {
        float x = a.X - b.X, z = a.Z - b.Z;
        return (float)Math.Sqrt(x * x + z * z);
    }
}
