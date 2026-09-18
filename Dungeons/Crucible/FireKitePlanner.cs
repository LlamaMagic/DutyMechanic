using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Searches complete escape routes in copied X/Z geometry. An endpoint-only
    // search can choose a safe corner that becomes a dead end as fire approaches.
    internal static class FireKitePlanner
    {
        internal sealed class Cone
        {
            internal Vector2 Center;
            internal float Heading;
            internal double Remaining;
        }

        private sealed class State
        {
            internal Vector2 Player, Fire;
            internal List<Vector2> Route = new List<Vector2>();
            internal float Score, MinimumGap;
            internal int Direction = -1;
        }

        // Half-second legs are sampled every 0.1 s to prevent cutting through a
        // pool between endpoints. Captures measure player 6 y/s and fire 3 y/s;
        // planning at 5.5/3.2 leaves a small execution/observation allowance.
        // Confirmed Sprint permits 7.3 y/s only for its remaining aura duration.
        // The bounded beam retains spatial alternatives rather than only the
        // locally greatest separation. All returned routes reach the horizon.
        internal static Vector2[] Plan(Vector2 start, Vector2 fire, Vector2 center,
            Vector2[] pools, Cone[] cones, double stationarySeconds, double blastIn, double sprintSeconds, Vector2? preferred, Action<int, int> observe = null)
        {
            const float dt = .5f, fireSpeed = 3.2f;
            float initialGap = Vector2.Distance(start, fire);
            int depth = (int)Math.Ceiling(Math.Max(6.5, cones.Select(c => c.Remaining + .6).DefaultIfEmpty(0).Max()) / dt);
            depth = Math.Min(22, depth); // Longest observed helper is9.3s; bound bot-thread work.
            var beam = new List<State> { new State { Player = start, Fire = fire, MinimumGap = initialGap } };
            for (int step = 0; step < depth; step++)
            {
                float playerSpeed = (step + 1) * dt < sprintSeconds ? 7.3f : 5.5f;
                var next = new Dictionary<(int, int, int), State>();
                foreach (var node in beam)
                    for (int direction = 0; direction < 17; direction++)
                    {
                        // Sixteen headings plus a stationary choice. Holding while
                        // the fire waits at spawn avoids pointless early orbiting.
                        float angle = direction * (float)Math.PI / 8;
                        var delta = direction == 16 ? Vector2.Zero : new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)) * playerSpeed * dt;
                        var end = node.Player + delta;
                        var predictedFire = node.Fire;
                        float minimum = node.MinimumGap;
                        bool safe = true;
                        for (int sample = 1; sample <= 5; sample++)
                        {
                            double time = step * dt + sample * .1;
                            var p = node.Player + delta * (sample / 5f);
                            var toPlayer = p - predictedFire;
                            float gap = toPlayer.Length();
                            if (time > stationarySeconds && time < blastIn - .7 && gap > .001f)
                                predictedFire += toPlayer / gap * Math.Min(gap, fireSpeed * .1f);
                            gap = Vector2.Distance(p, predictedFire);
                            // The fire is a timed 10 y explosion, not a permanent 10 y
                            // contact hazard. Keeping that entire radius excluded
                            // during pursuit removed the only path around the pool.
                            // Preserve 6.5 y pursuit separation, then expand to 10.8 y
                            // over the final two seconds before the early snapshot.
                            float blastClearance = 6.5f + 4.3f * (float)Math.Clamp((time - blastIn + 2.3) / 2, 0, 1);
                            float required = Math.Min(blastClearance, initialGap + (float)time * 1.5f);
                            if (Vector2.Distance(p, center) > 18.3f || pools.Any(c => Vector2.Distance(p, c) < 5.7f) ||
                                gap < required - .1f || cones.Any(c => time >= c.Remaining - .35 && time <= c.Remaining + .5 && InsideCone(p, c)))
                            {
                                safe = false;
                                break;
                            }
                            minimum = Math.Min(minimum, gap);
                        }
                        if (!safe)
                            continue;
                        float turnPenalty = node.Direction >= 0 && node.Direction != direction ? .08f : 0;
                        float score = node.Score - turnPenalty - (direction == 16 ? 0 : .015f);
                        if (step == 0 && preferred.HasValue)
                            score -= Vector2.Distance(end, preferred.Value) * .12f;
                        var candidate = new State { Player = end, Fire = predictedFire, MinimumGap = minimum, Score = score, Direction = direction, Route = new List<Vector2>(node.Route) };
                        candidate.Route.Add(end);
                        // Include pursuit position: identical player cells reached
                        // by different paths need not have equivalent escape space.
                        var key = ((int)Math.Round(end.X), (int)Math.Round(end.Y), (int)Math.Round(Math.Atan2(predictedFire.Y - end.Y, predictedFire.X - end.X) * 4 / Math.PI));
                        if (!next.TryGetValue(key, out var prior) || Rank(candidate) > Rank(prior))
                            next[key] = candidate;
                    }
                observe?.Invoke(step, next.Count);
                if (next.Count == 0)
                    return Array.Empty<Vector2>();
                beam = next.Values.OrderByDescending(Rank).Take(512).ToList();
            }
            return beam.OrderByDescending(Rank).First().Route.ToArray();
        }

        private static float Rank(State state) => state.Score + Vector2.Distance(state.Player, state.Fire) * .15f + Math.Min(state.MinimumGap, 14) * .2f;

        private static bool InsideCone(Vector2 p, Cone cone)
        {
            var delta = p - cone.Center;
            float distance = delta.Length();
            return distance <= 40.5f && (distance < .6f || Vector2.Dot(delta, new Vector2((float)Math.Sin(cone.Heading), (float)Math.Cos(cone.Heading))) / distance >= Math.Cos(61 * Math.PI / 180));
        }
    }
}
