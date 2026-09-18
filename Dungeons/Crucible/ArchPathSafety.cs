using System;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Checks copied X/Z geometry without live client state. A route may escape
    // its initial hazard, but must not enter another hazard after reaching safety.
    internal static class ArchPathSafety
    {
        internal static bool Beam(Vector2 start, Vector2 end, Vector2 center, float heading, float halfWidth)
        {
            float sin = (float)Math.Sin(heading), cos = (float)Math.Cos(heading);
            return Segment(start, end, p =>
            {
                var delta = p - center;
                float forward = delta.X * sin + delta.Y * cos;
                float side = delta.X * cos - delta.Y * sin;
                // Matches the handler's 40 y forward rectangle and its existing
                // half-yalm margin, with another 0.25 y for segment sampling.
                return forward >= -.75f && forward <= 40.75f && Math.Abs(side) <= halfWidth + .25f;
            });
        }

        internal static bool Circle(Vector2 start, Vector2 end, Vector2 center, float radius) =>
            Segment(start, end, p => Vector2.DistanceSquared(p, center) < (radius + .25f) * (radius + .25f));

        private static bool Segment(Vector2 start, Vector2 end, Func<Vector2, bool> inside)
        {
            // Sampling is at most 0.5 y, below the narrowest 5 y beam. The 0.25 y
            // extra clearance bounds an unseen incursion between sample points.
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(start, end) / .5f));
            bool outside = false;
            for (int i = 0; i <= steps; i++)
            {
                bool unsafePoint = inside(Vector2.Lerp(start, end, i / (float)steps));
                if (unsafePoint && outside)
                    return false;
                if (!unsafePoint)
                    outside = true;
            }
            // Endpoint safety is also enforced here so callers cannot mistake
            // a segment wholly inside a hazard for a successful escape.
            return !inside(end);
        }
    }
}
