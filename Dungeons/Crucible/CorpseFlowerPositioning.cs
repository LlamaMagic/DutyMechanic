using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Acid Rain starts with a placed cast, then seven instant circles chase the
    // player by up to 5y each second. Cast polling misses the instant hits;
    // predict each impact so movement cannot cut back through the chase.
    internal sealed class CorpseFlowerRain
    {
        internal const float Radius = 6.5f; // Six-yalm damage circle plus0.5y clearance.
        private readonly HashSet<uint> receipts = new();
        private Vector2 origin;
        private bool first;
        private DateTime expires, fallbackAt, completeAt;
        internal DateTime NextAt { get; private set; }
        internal int Impacts { get; private set; }

        internal bool Active(DateTime now) => NextAt != default && now < expires && (completeAt == default || now < completeAt);
        internal void Start(Vector2 point, DateTime at)
        {
            Reset();
            origin = point;
            first = true;
            NextAt = at;
            // Eight nominal impacts span seven seconds; extra3s tolerates
            // missed polling without releasing on the next overlapping boss cast.
            expires = at.AddSeconds(10);
        }

        internal Vector2 Predict(Vector2 player) => first ? origin : Chase(origin, player);
        private static Vector2 Chase(Vector2 from, Vector2 target)
        {
            var offset = target - from;
            return offset.LengthSquared() > 25 ? from + Vector2.Normalize(offset) * 5 : target;
        }

        internal bool Observe(uint action, uint sequence, Vector2 helper, Vector2 player, DateTime now)
        {
            if (!Active(now) || (action != 48693 && action != 48694) || sequence == 0 || !receipts.Add(sequence))
                return false;
            var predicted = fallbackAt != default && now - fallbackAt < TimeSpan.FromMilliseconds(500) ? origin : Predict(player);
            // The exposed response has no packet target location. A helper may
            // retain its first-cast position: accept its location only when it
            // agrees with this pulse's forecast, otherwise retain the forecast.
            origin = Vector2.Distance(helper, predicted) <= .75f ? helper : predicted;
            first = false;
            fallbackAt = default;
            NextAt = now.AddSeconds(1);
            if (++Impacts >= 8)
                completeAt = now.AddMilliseconds(400);
            return true;
        }

        internal void Update(Vector2 player, DateTime now)
        {
            // A dropped sample cannot leave a permanent circle at the first
            // bait. Timed predictions never count as confirmed receipts.
            if (Active(now) && completeAt == default && now > NextAt.AddMilliseconds(250))
            {
                origin = Predict(player);
                first = false;
                fallbackAt = now;
                NextAt = now.AddSeconds(1);
            }
        }

        internal Vector2? Escape(Vector2 player, Vector2 forward, DateTime now, Func<Vector2, bool> floorAndHazards, Func<Vector2, int> melee)
        {
            // A two-second curved lookahead avoids the old ring's unsafe chords
            // without requiring a twelve-yalm straight corridor beside a wall.
            // Quarter-second legs assume ordinary6y/s running; every half-yalm
            // is checked. A fixed beam bounds work independently of arena size.
            float heading = forward.LengthSquared() > .01f ? MathF.Atan2(forward.Y, forward.X) : MathF.Atan2(player.Y - origin.Y, player.X - origin.X);
            var beam = new List<RainLeg>
            {
                new()
                {
                    Position = player,
                    Heading = heading,
                    Previous = origin,
                    First = first,
                    Due = Math.Max(.01f, (float)(NextAt - now).TotalSeconds)
                }
            };
            float[] turns =
            {
                0,
                -.0872665f,
                .0872665f,
                -.2617994f,
                .2617994f,
                -.5235988f,
                .5235988f
            };
            for (int step = 0; step < 8; step++)
            {
                var next = new List<RainLeg>();
                foreach (var leg in beam)
                    foreach (float turn in turns)
                    {
                        float angle = leg.Heading + turn;
                        var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                        var point = leg.Position + direction * 1.5f;
                        if (!floorAndHazards(leg.Position + direction * .5f) || !floorAndHazards(leg.Position + direction) || !floorAndHazards(point))
                            continue;
                        Vector2 previous = leg.Previous;
                        bool initial = leg.First;
                        float due = leg.Due;
                        if (due <= (step + 1) * .25f)
                        {
                            var atImpact = Vector2.Lerp(leg.Position, point, Math.Clamp((due - step * .25f) / .25f, 0, 1));
                            var impact = initial ? previous : Chase(previous, atImpact);
                            if (Vector2.Distance(atImpact, impact) < Radius)
                                continue;
                            previous = impact;
                            initial = false;
                            due += 1;
                        }

                        next.Add(new RainLeg { Position = point, Heading = angle, Previous = previous, First = initial, Due = due, Goal = step == 0 ? point : leg.Goal, Turning = leg.Turning + Math.Abs(turn) });
                    }

                if (next.Count == 0)
                    return null;
                // Preserve alternatives for each immediate steering choice; a single
                // cheapest beam postpones turning until the arena rim traps it.
                beam = next.GroupBy(n => n.Goal).SelectMany(g => g.OrderBy(n => n.Turning).Take(8)).ToList();
            }

            return beam.OrderBy(n => n.Turning).ThenBy(n => melee(n.Position)).First().Goal;
        }

        private sealed class RainLeg
        {
            internal Vector2 Position, Previous, Goal;
            internal float Heading, Due, Turning;
            internal bool First;
        }

        internal void Reset()
        {
            receipts.Clear();
            Impacts = 0;
            first = true;
            origin = default;
            NextAt = expires = fallbackAt = completeAt = default;
        }
    }

    // Nearby briars can leave the player within suction range. Distance from
    // the flower is therefore the shelter's primary safety condition. Bee
    // alignment is only a tie-breaker; melee uptime cannot shorten that distance.
    internal static class CorpseFlowerPositioning
    {
        internal static Vector2? Shelter(IEnumerable<Vector2> patches, Vector2 boss, Vector2 player, Func<Vector2, bool> safe, Func<Vector2, float> alignment) => patches.Where(safe).OrderByDescending(p => Vector2.DistanceSquared(p, boss)).ThenBy(alignment).ThenBy(p => Vector2.DistanceSquared(p, player)).Select(p => (Vector2? )p).FirstOrDefault();
        // No guessed thorn radius: use the live Briar aura as the exit receipt.
        // Short checked legs increase clearance from every visible patch while
        // never moving toward the flower's follow-up Devour. This is deliberate
        // shelter egress, not an alternative owner for emergency avoidance.
        internal static Vector2? Exit(IEnumerable<Vector2> candidates, Vector2[] patches, Vector2 boss, Vector2 player, Func<Vector2, bool> safe, Func<Vector2, Vector2, bool> corridor)
        {
            if (patches.Length == 0)
            {
                return null;
            }

            float Clearance(Vector2 p) => patches.Min(b => Vector2.Distance(p, b));
            float current = Clearance(player), bossDistance = Vector2.Distance(player, boss);
            return candidates.Where(p => Vector2.Distance(p, player) >= 1 && Vector2.Distance(p, player) <= 4 && Vector2.Distance(p, boss) >= bossDistance && Clearance(p) > current + .5f && safe(p) && corridor(player, p)).OrderByDescending(Clearance).ThenBy(p => Vector2.DistanceSquared(p, player)).Select(p => (Vector2? )p).FirstOrDefault();
        }
    }
}
