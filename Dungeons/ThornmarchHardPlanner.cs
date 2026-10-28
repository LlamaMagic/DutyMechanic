using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Scalar Thornmarch choreography shared by native avoidance and semantic positioning. It never
/// selects combat actions or assumes that other live players follow our role assignments.
/// </summary>
internal static class ThornmarchHardPlanner
{
    // The reworked arena is a 21-yalm circle. Half a yalm leaves room for the collision wall;
    // damaging shapes separately receive the same half-yalm margin. X/Z ground center is zero.
    internal const float Arena = 20.5f;
    internal const float Margin = 0.5f;
    internal const double Grace = 0.25; // Polling fallback until exact damage timing is captured.

    internal enum Role
    {
        Tank,
        Healer,
        Melee,
        Ranged
    }

    internal enum Shape
    {
        Circle,
        Donut,
        Cone,
        Line
    }

    internal sealed record Member(uint Id, Vector2 Position, Role Role);
    internal sealed record Cast(uint Owner, uint Action, Vector2 Position, Vector2 Forward, double Finish, uint Target);
    internal sealed record Tower(uint Id, Vector2 Position, double Finish);

    // Native indicator state 1 is active/unoccupied; 0x10 is active/occupied.
    // Both remain soak goals until state 4 clears them. Preserve the first deadline
    // across occupancy changes, including after the cast's arming window closes.
    // Unknown states fail closed; an unarmed first observation cannot create a goal.
    internal static Tower ObserveTower(Tower current, uint id, Vector2 position,
        uint state, double now, double armUntil, double delay)
    {
        if (state is not (1 or 0x10))
            return null;
        return current ?? (now <= armUntil ? new Tower(id, position, now + delay) : null);
    }

    internal sealed record Hazard(uint Owner, uint Action, Vector2 Position, Vector2 Forward, Shape Shape,
        float Radius, float Inner = 0, float HalfWidth = 0, float HalfAngle = 0)
    {
        internal bool Contains(Vector2 point)
        {
            Vector2 d = point - Position;
            float distance = d.Length();
            return Shape switch
            {
                Shape.Circle => distance <= Radius,
                Shape.Donut => distance >= Inner && distance <= Radius,
                Shape.Cone => distance <= Radius && (distance <= Margin || Vector2.Dot(d, Forward) >= distance * MathF.Cos(HalfAngle)),
                _ => Vector2.Dot(d, Forward) >= -Margin && Vector2.Dot(d, Forward) <= Radius
                    && MathF.Abs(d.X * Forward.Y - d.Y * Forward.X) <= HalfWidth
            };
        }

        internal Vector2[] Polygon()
        {
            if (Shape == Shape.Line)
            {
                Vector2 side = new(Forward.Y, -Forward.X);
                return [Position - Forward * Margin + side * HalfWidth, Position + Forward * Radius + side * HalfWidth,
                    Position + Forward * Radius - side * HalfWidth, Position - Forward * Margin - side * HalfWidth];
            }
            // Circumscribed outer edges keep the polygon outside the damaging circle;
            // the inner edge stays inside the safe opening. This avoids chord-shaped gaps.
            const int segments = 96;
            List<Vector2> points = [];
            if (Shape == Shape.Cone)
            {
                float heading = MathF.Atan2(Forward.Y, Forward.X);
                points.Add(Position);
                for (int i = 0; i <= segments; i++)
                    points.Add(Position + Direction(heading - HalfAngle + 2 * HalfAngle * i / segments) * (Radius / MathF.Cos(HalfAngle / segments)));
                return points.ToArray();
            }
            for (int i = 0; i <= segments; i++)
                points.Add(Position + Direction(i * MathF.Tau / segments) * (Radius / MathF.Cos(MathF.PI / segments)));
            if (Shape == Shape.Donut)
                for (int i = segments; i >= 0; i--)
                    points.Add(Position + Direction(i * MathF.Tau / segments) * Inner);
            return points.ToArray();
        }
    }

    internal sealed record Plan(Hazard[] Hazards, Vector2? Destination, string Stage, uint Owner, bool Positive, bool Feasible);
    internal static Vector2 Direction(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));
    internal static Vector2 Forward(float heading) => new(MathF.Sin(heading), MathF.Cos(heading));
    internal static bool Safe(Vector2 point, IReadOnlyList<Hazard> hazards) => point.Length() <= Arena && !hazards.Any(h => h.Contains(point));

    /// <summary>
    /// Resolve stack before spread, first two Go-Round impacts before the third, and only the
    /// earliest Pom Stone ring cohort. Towers wait until all comet baits have finished. Native
    /// avoidance receives exactly this stage, so it cannot contradict the positive destination.
    /// </summary>
    internal static Plan Build(Member self, Member[] party, Cast[] casts, Tower[] towers,
        Vector2[] bogs, bool baitingComets, Vector2? previous, double now, uint assignedTower = 0)
    {
        List<Hazard> hazards = [];
        foreach (Vector2 bog in bogs)
            hazards.Add(new(0, 29207, bog, Vector2.Zero, Shape.Circle, 8 + Margin));
        Cast[] timed = casts.Where(c => c.Finish + Grace >= now).OrderBy(c => c.Finish).ThenBy(c => c.Owner).ToArray();
        Cast stack = timed.FirstOrDefault(c => c.Action == 29204 || c.Action == 29206 && self.Role == Role.Tank);
        double deadline = stack?.Finish + 0.35 ?? double.PositiveInfinity;
        Cast[] allRounds = timed.Where(c => c.Action is 29196 or 29197).ToArray();
        Cast[] rounds = allRounds.Take(2).ToArray();
        double ringFirst = timed.Where(c => c.Action is 29201 or 29202 or 29619).Select(c => c.Finish).DefaultIfEmpty(double.PositiveInfinity).Min();
        bool selfSpread = timed.Any(c => c.Action == 29192 && c.Target == self.Id && c.Finish <= deadline);
        foreach (Cast c in timed)
        {
            if (c.Finish > deadline && c.Action != 29207)
                continue; // Do not scatter an earlier required stack to pre-position for a later spread.
            Hazard h = c.Action switch
            {
                29216 => Circle(c, 6),
                29215 => new(c.Owner, c.Action, c.Position - c.Forward * Margin, c.Forward, Shape.Cone, 20 + Margin, HalfAngle: MathF.PI / 4),
                29213 => new(c.Owner, c.Action, c.Position - c.Forward * Margin, c.Forward, Shape.Cone, 30 + Margin, HalfAngle: MathF.PI / 3),
                29192 when c.Target != 0 && c.Target != self.Id => Circle(c, 6),
                29206 when self.Role != Role.Tank && c.Target != 0 && c.Target != self.Id => Circle(c, 6),
                29196 or 29197 when rounds.Contains(c) => Circle(c, 20),
                29199 => Circle(c, 6),
                29207 => Circle(c, 8),
                29201 when c.Finish < ringFirst + 1 => Circle(c, 10),
                29202 when c.Finish < ringFirst + 1 => new(c.Owner, c.Action, c.Position, c.Forward, Shape.Donut, 20 + Margin, 10 - Margin),
                29619 when c.Finish < ringFirst + 1 => new(c.Owner, c.Action, c.Position, c.Forward, Shape.Donut, 30 + Margin, 20 - Margin),
                29208 => new(c.Owner, c.Action, c.Position, c.Forward, Shape.Line, 50 + Margin, HalfWidth: 5 + Margin),
                _ => null
            };
            if (h != null)
                hazards.Add(h);
        }
        if (selfSpread)
            foreach (Member m in party.Where(m => m.Id != self.Id))
                hazards.Add(new(m.Id, 29192, m.Position, Vector2.Zero, Shape.Circle, 6 + Margin));
        if (allRounds.Length > 2 && stack == null)
        {
            // Stage near the first circle's edge for the eventual safe region, without entering
            // its still-active damage. These 23/10-yalm regions are travel constraints, not extra
            // damage radii; the first two real circles already include their 0.5-yalm margins.
            // Do not manufacture an impossible region if a partial/late cast sample disagrees.
            Hazard[] staging = [
                new(allRounds[0].Owner, 0, allRounds[0].Position, Vector2.Zero, Shape.Donut, 100, 23),
                new(allRounds[2].Owner, 0, allRounds[2].Position, Vector2.Zero, Shape.Circle, 10)
            ];
            var staged = hazards.Concat(staging).ToArray();
            if (Samples().Any(p => Safe(p, staged)))
                hazards.AddRange(staging);
        }
        if (baitingComets)
            foreach (Tower tower in towers)
                hazards.Add(new(tower.Id, 29199, tower.Position, Vector2.Zero, Shape.Circle, 6 + Margin));

        Func<Vector2, bool> allowed = _ => true;
        Func<Vector2, float> preference = p => Vector2.Distance(p, self.Position);
        string stage = "native geometry";
        uint owner = 0;
        bool positive = false;
        List<Vector2> extra = [];
        if (stack != null)
        {
            positive = true;
            owner = stack.Owner;
            stage = stack.Action == 29204 ? "party stack" : "shared tankbuster";
            Member target = party.FirstOrDefault(m => m.Id == stack.Target);
            if (target == null)
                return new(hazards.ToArray(), null, stage + ": unresolved target", owner, true, false);
            Member[] participants = party.Where(m => m.Id != self.Id && (stack.Action == 29204 || m.Role == Role.Tank)).ToArray();
            extra.AddRange(participants.Select(m => m.Position));
            extra.Add(target.Position);
            if (target.Id != self.Id)
                allowed = p => Vector2.Distance(p, target.Position) <= 5.4f;
            // A self mark is not a stationary success: maximize actual living participant coverage.
            // In a reduced-size carry party this uses the real party size rather than demanding eight.
            preference = p => -1000 * participants.Count(m => Vector2.Distance(p, m.Position) <= 5.4f)
                + Vector2.Distance(p, self.Position);
        }
        else if (!baitingComets && towers.Length > 0)
        {
            Tower chosen = AssignTower(self, party, towers, now, assignedTower);
            if (chosen != null)
            {
                positive = true;
                owner = chosen.Id;
                stage = "uncovered meteor tower";
                allowed = p => Vector2.Distance(p, chosen.Position) <= 3.5f;
                preference = p => Vector2.Distance(p, chosen.Position) * 2 + Vector2.Distance(p, self.Position);
                extra.Add(chosen.Position);
            }
        }
        Hazard[] result = hazards.ToArray();
        if (!positive)
            return new(result, null, stage, 0, false, true);
        var candidates = Samples().Concat(extra).Append(self.Position).ToList();
        if (previous.HasValue)
            candidates.Add(previous.Value);
        Vector2[] valid = candidates.Where(p => Safe(p, result) && allowed(p)).ToArray();
        if (valid.Length == 0)
            return new(result, null, stage, owner, true, false);
        Vector2 best = valid.OrderBy(preference).First();
        // Equivalent points retain their owner destination. A substantial participation change
        // still beats hysteresis, so a marked player cannot remain isolated from a moving group.
        if (previous.HasValue && Safe(previous.Value, result) && allowed(previous.Value)
            && preference(previous.Value) <= preference(best) + 1)
            best = previous.Value;
        return new(result, best, stage, owner, true, true);
    }

    private static Hazard Circle(Cast c, float radius) => new(c.Owner, c.Action, c.Position, c.Forward, Shape.Circle, radius + Margin);

    private static Tower AssignTower(Member self, Member[] party, Tower[] towers, double now, uint assignedTower)
    {
        // Keep occupied towers claimed by their actual occupant. Assign the remaining roles in
        // healer/ranged-first order with stable IDs; melee/tanks fill shortages, not occupied towers.
        var covered = towers.Where(t => party.Any(m => Vector2.Distance(m.Position, t.Position) <= 4)).ToArray();
        Tower current = covered.FirstOrDefault(t => Vector2.Distance(self.Position, t.Position) <= 4);
        if (current != null)
            return current;
        List<Tower> available = towers.Except(covered).OrderBy(t => t.Id).ToList();
        // Predicted assignments can change as other players move. Keep our accepted tower
        // until actual coverage, expiry or loss of reachability releases it; otherwise a
        // passing ally can make us abandon an empty tower.
        Tower retained = available.FirstOrDefault(t => t.Id == assignedTower && Reachable(self, t, now));
        if (retained != null)
            return retained;
        var free = party.Where(m => !covered.Any(t => Vector2.Distance(m.Position, t.Position) <= 4))
            .OrderBy(m => m.Role is Role.Healer or Role.Ranged ? 0 : 1).ThenBy(m => m.Id);
        foreach (Member member in free)
        {
            Tower chosen = available.Where(t => Reachable(member, t, now))
                .OrderBy(t => Vector2.DistanceSquared(member.Position, t.Position)).ThenBy(t => t.Id).FirstOrDefault();
            if (member.Id == self.Id)
                return chosen;
            if (chosen != null)
                available.Remove(chosen);
        }
        return null;
    }

    // Walking speed is six yalms/sec; half a second remains for frame/arrival tolerance.
    private static bool Reachable(Member member, Tower tower, double now) =>
        Vector2.Distance(member.Position, tower.Position) <= Math.Max(0, tower.Finish - now - 0.5) * 6 + 3.5;

    private static IEnumerable<Vector2> Samples()
    {
        yield return Vector2.Zero;
        // Resolution supports a six-yalm stack and five-yalm towers without choosing wall points.
        foreach (float radius in new[] { 3f, 6f, 9f, 12f, 15f, 18f, 20f })
            for (int i = 0; i < 64; i++)
                yield return Direction(i * MathF.Tau / 64) * radius;
    }

    internal static bool SegmentSafe(Vector2 from, Vector2 to, IReadOnlyList<Hazard> hazards)
    {
        int samples = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(from, to) / 0.25f));
        for (int i = 0; i <= samples; i++)
            if (!Safe(Vector2.Lerp(from, to, (float)i / samples), hazards))
                return false;
        return true;
    }

    /// <summary>
    /// Route semantic moves around a persistent center bog instead of walking through it. The
    /// short ring graph is a planner only: the adapter remains the sole movement owner. An unsafe
    /// origin belongs to native escape, never a forced crossing of an unverified hazard.
    /// </summary>
    internal static Vector2? NextWaypoint(Vector2 from, Vector2 to, Hazard[] hazards)
    {
        if (!Safe(from, hazards) || !Safe(to, hazards))
            return null;
        if (SegmentSafe(from, to, hazards))
            return to;
        var nodes = new List<Vector2> { from, to };
        foreach (float radius in new[] { 10f, 14f, 19f })
            for (int i = 0; i < 32; i++)
                nodes.Add(Direction(i * MathF.Tau / 32) * radius);
        float[] distances = Enumerable.Repeat(float.PositiveInfinity, nodes.Count).ToArray();
        int[] previous = Enumerable.Repeat(-1, nodes.Count).ToArray();
        HashSet<int> visited = [];
        distances[0] = 0;
        while (true)
        {
            int u = Enumerable.Range(0, nodes.Count).Where(i => !visited.Contains(i)).OrderBy(i => distances[i]).FirstOrDefault(-1);
            if (u < 0 || float.IsPositiveInfinity(distances[u]))
                return null;
            if (u == 1)
                break;
            visited.Add(u);
            for (int v = 1; v < nodes.Count; v++)
            {
                if (visited.Contains(v))
                    continue;
                float length = Vector2.Distance(nodes[u], nodes[v]);
                // Only the endpoints need long edges; adjacent rings provide the curved route.
                if (u > 1 && v > 1 && length > 6)
                    continue;
                float next = distances[u] + length;
                if (next >= distances[v] || !SegmentSafe(nodes[u], nodes[v], hazards))
                    continue;
                distances[v] = next;
                previous[v] = u;
            }
        }
        int step = 1;
        while (previous[step] > 0)
            step = previous[step];
        return nodes[step];
    }
}
