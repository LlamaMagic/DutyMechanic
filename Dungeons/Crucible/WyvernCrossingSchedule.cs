using System;
using System.Collections.Generic;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Time-expanded search can revisit a cell while waiting for a tornado.
    // Convert those cycles into verified stationary waits and straight segments,
    // retaining their departure times so a mover cannot enter the opening early.
    internal static class WyvernCrossingSchedule
    {
        internal readonly struct Step
        {
            internal readonly Vector2 Position;
            internal readonly double Seconds;
            internal Step(Vector2 position, double seconds)
            {
                Position = position;
                Seconds = seconds;
            }
        }

        internal enum Command { Move, Hold, Complete, Expired }

        // This cursor preserves explicit departure times. An unexpected pause
        // cannot silently shift the entire path into a different tornado phase:
        // arriving over200ms late hands control back to native emergency logic.
        internal sealed class Cursor
        {
            private readonly Step[] steps;
            private int index = 1;
            internal Cursor(Step[] steps) => this.steps = steps;

            internal Command Update(Vector2 actual, double elapsed, out Vector2 target)
            {
                target = actual;
                while (index < steps.Length)
                {
                    var step = steps[index];
                    target = step.Position;
                    bool wait = step.Position == steps[index - 1].Position;
                    if (wait && elapsed < step.Seconds)
                    {
                        // Waiting is valid only at the verified point; a knockback
                        // is not permission to stand elsewhere until the timer.
                        return Vector2.Distance(actual, target) <= .1f ? Command.Hold : Command.Expired;
                    }
                    if (Vector2.Distance(actual, target) <= .1f)
                    {
                        index++;
                        continue;
                    }
                    if (elapsed > step.Seconds + .2)
                        return Command.Expired;
                    return Command.Move;
                }
                return steps.Length > 0 ? Command.Complete : Command.Expired;
            }
        }

        internal static Step[] Build(Vector2 start, Vector2[] route, float speed,
            Func<Vector2, double, bool> safe)
        {
            if (route.Length == 0 || speed <= 0 || !safe(start, 0))
                return Array.Empty<Step>();
            var input = new List<Step> { new Step(start, 0) };
            var previous = start;
            double seconds = 0;
            foreach (var point in route)
            {
                seconds += Vector2.Distance(previous, point) / speed;
                input.Add(new Step(point, seconds));
                previous = point;
            }

            var result = new List<Step> { input[0] };
            int index = 0;
            while (index < input.Count - 1)
            {
                bool found = false;
                // Prefer the farthest safe shortcut, without deleting elapsed
                // time. Hold at its start for the time saved by straight travel.
                // Both the hold and transit are sampled every25ms: smoothing is
                // rejected if it cuts a pool or crosses a moving capsule.
                for (int end = input.Count - 1; end > index; --end)
                {
                    var from = input[index];
                    var to = input[end];
                    double travel = Vector2.Distance(from.Position, to.Position) / speed;
                    double departure = to.Seconds - travel;
                    if (departure < from.Seconds - .0001 ||
                        !SegmentSafe(from.Position, from.Position, from.Seconds, Math.Max(from.Seconds, departure), safe) ||
                        !SegmentSafe(from.Position, to.Position, Math.Max(from.Seconds, departure), to.Seconds, safe))
                        continue;
                    if (departure > from.Seconds + .001)
                        result.Add(new Step(from.Position, departure));
                    result.Add(to);
                    index = end;
                    found = true;
                    break;
                }
                // Never return a partially verified route. The caller retains
                // native emergency avoidance when no complete schedule exists.
                if (!found) return Array.Empty<Step>();
            }
            return result.ToArray();
        }

        private static bool SegmentSafe(Vector2 from, Vector2 to, double begin,
            double end, Func<Vector2, double, bool> safe)
        {
            int samples = Math.Max(1, (int)Math.Ceiling(Math.Max(0, end - begin) / .025));
            for (int i = 0; i <= samples; ++i)
            {
                float fraction = i / (float)samples;
                if (!safe(Vector2.Lerp(from, to, fraction), begin + (end - begin) * fraction))
                    return false;
            }
            return true;
        }
    }
}
