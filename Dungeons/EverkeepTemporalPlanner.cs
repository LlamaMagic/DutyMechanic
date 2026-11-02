using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

// Detached space-time search for Everkeep. The caller must provide validated,
// immutable hazard windows and revalidate the returned route against new casts.
// An empty result means no verified route; it must never be treated as safety.
internal static class EverkeepTemporalPlanner
{
    internal readonly record struct Hazard(Vector2 Origin, Vector2 Direction, float Length,
        float HalfWidth, double Start, double End, bool HalfPlane, bool Circle = false);
    internal readonly record struct Step(Vector2 Position, double Time, bool CleavePriority = false);

    // Half-yalm clearance is already included in the caller's hazard geometry.
    // This search uses a one-yalm grid and .3s ticks. Each movement edge is
    // checked against a 4.5-to-9y/s speed envelope and startup delay; an
    // additional initial tick covers worker and movement setup. Segment/hazard
    // intersections are continuous; unobserved events cannot be predicted.
    internal static Step[] Plan(Vector2 start, IReadOnlyList<Hazard> hazards, int ticks = 30,
        CancellationToken cancellation = default)
    {
        const int width = 41, count = width * width;
        const double quantum = .3, speed = 4.5;
        if (cancellation.IsCancellationRequested || hazards == null || ticks < 2 || ticks > 60 || !InsideFloor(start))
            return Array.Empty<Step>();
        foreach (var h in hazards)
            if (!float.IsFinite(h.Origin.X) || !float.IsFinite(h.Origin.Y) ||
                !float.IsFinite(h.Direction.X) || !float.IsFinite(h.Direction.Y) ||
                Math.Abs(h.Direction.LengthSquared() - 1f) > .01f ||
                !float.IsFinite(h.Length) || !float.IsFinite(h.HalfWidth) ||
                h.Length < 0 || h.HalfWidth < 0 || !double.IsFinite(h.Start) ||
                !double.IsFinite(h.End) || h.Start > h.End || h.End > ticks * quantum)
                return Array.Empty<Step>();
        // Old captured windows are not relevant to any future state. Removing
        // them once also keeps the search bounded as a sequence progresses.
        var future = new List<Hazard>(hazards.Count);
        foreach (var h in hazards) if (h.End >= 0) future.Add(h);

        bool Safe(Vector2 p, double t)
        {
            if (!InsideFloor(p)) return false;
            foreach (var h in future)
            {
                if (t < h.Start || t > h.End) continue;
                var d = p - h.Origin;
                if (h.Circle)
                {
                    if (d.LengthSquared() <= (h.Length + .001f) * (h.Length + .001f)) return false;
                    continue;
                }
                float along = Vector2.Dot(d, h.Direction);
                float side = Math.Abs(d.X * h.Direction.Y - d.Y * h.Direction.X);
                if (h.HalfPlane ? along >= -h.HalfWidth :
                    along >= -.5f && along <= h.Length + .5f && side <= h.HalfWidth) return false;
            }
            return true;
        }

        bool Segment(Vector2 a, Vector2 b, double begin, double end)
        {
            // The inset floor is convex, so an edge whose endpoints are inside
            // cannot cross its boundary. Clip each hazard to its active interval
            // before testing the corresponding continuous movement segment.
            if (!InsideFloor(a) || !InsideFloor(b)) return false;
            foreach (var h in future)
            {
                double from = Math.Max(begin, h.Start), to = Math.Min(end, h.End);
                if (from > to) continue;
                double distance = Vector2.Distance(a, b);
                // Bound where a 4.5-to-9y/s actor can be during the active
                // interval, including 100ms of movement startup uncertainty.
                float f0 = distance < .001 ? 0 : (float)Math.Clamp((from - begin - .1) * speed / distance, 0, 1);
                float f1 = distance < .001 ? 0 : (float)Math.Clamp((to - begin) * 9 / distance, 0, 1);
                var p = Vector2.Lerp(a, b, f0) - h.Origin;
                var q = Vector2.Lerp(a, b, f1) - h.Origin;
                if (h.Circle)
                {
                    if (CircleIntersects(p, q, h.Length)) return false;
                    continue;
                }
                float along0 = Vector2.Dot(p, h.Direction), along1 = Vector2.Dot(q, h.Direction);
                if (h.HalfPlane)
                {
                    if (Math.Max(along0, along1) >= -h.HalfWidth - .001f) return false;
                    continue;
                }
                float side0 = p.X * h.Direction.Y - p.Y * h.Direction.X;
                float side1 = q.X * h.Direction.Y - q.Y * h.Direction.X;
                float lo = 0f, hi = 1f;
                if (Clip(ref lo, ref hi, along0, along1 - along0, -.5f, h.Length + .5f) &&
                    Clip(ref lo, ref hi, side0, side1 - side0, -h.HalfWidth, h.HalfWidth)) return false;
            }
            return true;
        }

        if (!Safe(start, 0)) return Array.Empty<Step>();
        // Never manufacture movement when staying put satisfies every known window.
        if (Segment(start, start, 0, ticks * quantum)) return new[] { new Step(start, ticks * quantum) };
        if (!Segment(start, start, 0, quantum)) return Array.Empty<Step>();
        var previous = new int[(ticks + 1) * count];
        Array.Fill(previous, -1);
        var costs = new double[previous.Length];
        Array.Fill(costs, double.PositiveInfinity);
        // Prefer central verified alternatives when movement is necessary;
        // this reduces travel to either observed cleave without forecasting one.
        var queue = new PriorityQueue<int, double>();
        // Admit initial grid nodes at their actual reachable arrival tick. A
        // forced one-tick snap needlessly spends the only escape window when
        // the actor begins between cells; every direct edge is still checked.
        for (int x = 0; x < width; x++) for (int z = 0; z < width; z++)
        {
            var p = new Vector2(80 + x, 80 + z);
            float distance = Vector2.Distance(start, p);
            if (distance > 6) continue;
            int travel = Math.Max(1, (int)Math.Ceiling((distance / speed + (distance < .001 ? 0 : .1)) / quantum));
            int arrival = 1 + travel;
            if (arrival > ticks || !Segment(start, p, quantum, arrival * quantum)) continue;
            int state = arrival * count + x * width + z;
            previous[state] = -2;
            costs[state] = distance;
            queue.Enqueue(state, costs[state] + 1.0 * Vector2.Distance(p, new Vector2(100, 100)));
        }
        while (queue.TryDequeue(out int state, out double cost))
        {
            // Replaced casts, death and movement-owner changes must invalidate
            // background work without publishing a stale route to the actor.
            if (cancellation.IsCancellationRequested) return Array.Empty<Step>();
            int tick = state / count, id = state % count;
            int x = id / width, z = id % width;
            var p = new Vector2(80 + x, 80 + z);
            if (cost > costs[state] + 1.0 * Vector2.Distance(p, new Vector2(100, 100)) + .000001) continue;
            if (tick == ticks || Segment(p, p, tick * quantum, ticks * quantum))
            {
                var path = new List<Step>();
                for (int cur = state; cur >= 0; cur = previous[cur])
                {
                    int cell = cur % count;
                    path.Add(new Step(new Vector2(80 + cell / width, 80 + cell % width), cur / count * quantum));
                }
                path.Reverse();
                if (tick < ticks) path.Add(new Step(p, ticks * quantum));
                return path.ToArray();
            }
            // Longer edges avoid quantizing the mover down to 3.3y/s on
            // a one-yalm grid. Arrival time still reflects the entire segment.
            for (int dx = -3; dx <= 3; dx++) for (int dz = -3; dz <= 3; dz++)
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nx >= width || nz < 0 || nz >= width) continue;
                var q = new Vector2(80 + nx, 80 + nz);
                int travelTicks = Math.Max(1, (int)Math.Ceiling((Vector2.Distance(p, q) / speed + (p == q ? 0 : .1)) / quantum));
                int arrival = tick + travelTicks;
                if (arrival > ticks) continue;
                int next = arrival * count + nx * width + nz;
                double nextCost = costs[state] + Vector2.Distance(p, q);
                if (nextCost >= costs[next] || !Segment(p, q, tick * quantum, arrival * quantum)) continue;
                previous[next] = state;
                costs[next] = nextCost;
                queue.Enqueue(next, nextCost + 1.0 * Vector2.Distance(q, new Vector2(100, 100)));
            }
        }
        return Array.Empty<Step>();
    }

    // Only call after full-hazard planning and egress both fail. A cleave can
    // kill outright, while a Vorpal hit is survivable; preserve all lethal
    // half-planes and Burst circles when the alternatives are exhausted.
    internal static Step[] FindCleavePriority(Vector2 start, Hazard[] hazards, int ticks, CancellationToken token)
    {
        if (token.IsCancellationRequested || hazards == null) return Array.Empty<Step>();
        var lethal = new List<Hazard>();
        bool imminentCleave = false;
        foreach (var h in hazards)
        {
            if (h.HalfPlane && h.End >= 0 && h.Start <= 3.5) imminentCleave = true;
            if (h.HalfPlane || h.Circle) lethal.Add(h);
        }
        if (!imminentCleave) return Array.Empty<Step>();
        var route = Plan(start, lethal, ticks, token);
        if (route.Length == 0 && !token.IsCancellationRequested) route = FindEgress(start, lethal, token);
        if (route.Length != 0) route[0] = route[0] with { CleavePriority = true };
        return route;
    }

    private static bool InsideFloor(Vector2 p)
    {
        float x = p.X - 100f, z = p.Y - 100f;
        const float c = .6967067f, s = .7173561f; // Observed .8-radian arena rotation.
        return float.IsFinite(x) && float.IsFinite(z) &&
            Math.Max(Math.Abs(x * c + z * s), Math.Abs(-x * s + z * c)) <= 19f;
    }

    // A fresh observation can find the player already inside a damaging shape.
    // This bounded emergency move only exits shapes occupied at discovery: it
    // cannot enter another timed shape, and must progress toward one boundary
    // of every occupied rectangle throughout the straight segment. It is not a
    // complete plan; the caller must revalidate and immediately plan again.
    internal static Step[] FindEgress(Vector2 start, IReadOnlyList<Hazard> hazards,
        CancellationToken cancellation = default)
    {
        if (!InsideFloor(start) || hazards == null || cancellation.IsCancellationRequested) return Array.Empty<Step>();
        for (int radius = 1; radius <= 24; radius++)
            for (int angle = 0; angle < 32; angle++)
            {
                if (cancellation.IsCancellationRequested) return Array.Empty<Step>();
                double a = angle * Math.PI / 16;
                var target = start + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * (radius * .5f);
                if (CanEgress(start, target, hazards))
                    return new[] { new Step(target, .4 + Vector2.Distance(start, target) / 4.5) };
            }
        return Array.Empty<Step>();
    }

    // A nearby initial grid cell can create a visible micro-move and stop.
    // Merge it with the next leg only when the direct movement and its early
    // arrival hold are safe for the full speed envelope and planned deadline.
    internal static Step[] SimplifyInitial(Vector2 start, Hazard[] hazards, Step[] route)
    {
        if (hazards == null || route == null || route.Length < 2 ||
            Vector2.Distance(route[0].Position, route[1].Position) < .05f) return route;
        float distance = Vector2.Distance(start, route[1].Position);
        if (.4 + distance / 4.5 > route[1].Time) return route;
        var shifted = new Hazard[hazards.Length];
        for (int i = 0; i < hazards.Length; i++)
            shifted[i] = hazards[i] with { Start = hazards[i].Start - .3, End = hazards[i].End - .3 };
        if (!CanTraverseTimed(start, route[1].Position, shifted) ||
            !CanTraverse(route[1].Position, route[1].Position, hazards,
                .3 + distance / 9, route[1].Time + .1)) return route;
        var result = new Step[route.Length - 1];
        Array.Copy(route, 1, result, 0, result.Length);
        return result;
    }

    // Verify the current position, not the old search snapshot, before every
    // emergency input. Initially occupied shapes may only be left; all other
    // active or imminent shapes remain forbidden for the whole movement.
    internal static bool CanEgress(Vector2 start, Vector2 target, IReadOnlyList<Hazard> hazards)
    {
        if (!InsideFloor(start) || !InsideFloor(target) || hazards == null || start == target) return false;
        bool any = false;
        double arrival = .4 + Vector2.Distance(start, target) / 4.5;
        foreach (var h in hazards)
        {
            if (!Valid(h)) return false;
            if (h.Start <= 0 && h.End >= 0 && Contains(h, start))
            {
                any = true;
                var p = start - h.Origin;
                var q = target - h.Origin;
                if (h.Circle)
                {
                    if (Vector2.Dot(p, q - p) < 0 || q.Length() <= h.Length + .001f) return false;
                    continue;
                }
                float pa = Vector2.Dot(p, h.Direction), qa = Vector2.Dot(q, h.Direction);
                float ps = p.X * h.Direction.Y - p.Y * h.Direction.X;
                float qs = q.X * h.Direction.Y - q.Y * h.Direction.X;
                bool exits = h.HalfPlane ? qa < -h.HalfWidth - .001f && qa < pa :
                    qa < -.501f && qa < pa || qa > h.Length + .501f && qa > pa ||
                    qs < -h.HalfWidth - .001f && qs < ps || qs > h.HalfWidth + .001f && qs > ps;
                if (!exits) return false;
                continue;
            }
            if (!HazardClear(h, start, target, 0, arrival)) return false;
        }
        return any;
    }

    private static bool Valid(Hazard h) =>
        float.IsFinite(h.Origin.X) && float.IsFinite(h.Origin.Y) &&
        float.IsFinite(h.Direction.X) && float.IsFinite(h.Direction.Y) &&
        Math.Abs(h.Direction.LengthSquared() - 1f) <= .01f &&
        float.IsFinite(h.Length) && float.IsFinite(h.HalfWidth) && h.Length >= 0 && h.HalfWidth >= 0 &&
        double.IsFinite(h.Start) && double.IsFinite(h.End) && h.Start <= h.End;

    // Runtime movement speed and pulse timing are not exact. Treat the entire
    // segment as occupied throughout the whole requested interval rather than
    // assuming that the actor reaches each point at the search's ideal time.
    // A controller must call this again after every changed observation.
    internal static bool CanTraverse(Vector2 from, Vector2 to, IReadOnlyList<Hazard> hazards,
        double begin, double end)
    {
        if (!InsideFloor(from) || !InsideFloor(to) || hazards == null || !double.IsFinite(begin) ||
            !double.IsFinite(end) || end < begin) return false;
        foreach (var h in hazards)
        {
            if (!Valid(h) || !HazardClear(h, from, to, begin, end)) return false;
        }
        return true;
    }

    // Bound every possible position during each active window, allowing startup
    // delay and a conservative speed range that includes sprint. The caller must monitor
    // progress and stop/replan if movement falls outside those bounds.
    internal static bool CanTraverseTimed(Vector2 from, Vector2 to, IReadOnlyList<Hazard> hazards,
        double minSpeed = 4.5, double maxSpeed = 9, double startup = .1)
    {
        if (!InsideFloor(from) || !InsideFloor(to) || hazards == null || !double.IsFinite(minSpeed) ||
            !double.IsFinite(maxSpeed) || !double.IsFinite(startup) || minSpeed <= 0 ||
            maxSpeed < minSpeed || startup < 0) return false;
        double distance = Vector2.Distance(from, to);
        if (distance < .001) return CanTraverse(from, to, hazards, 0, startup);
        double horizon = startup + distance / minSpeed + .2;
        foreach (var h in hazards)
        {
            if (!Valid(h)) return false;
            double begin = Math.Max(0, h.Start), end = Math.Min(horizon, h.End);
            if (begin > end) continue;
            float first = (float)Math.Clamp((begin - startup) * minSpeed / distance, 0, 1);
            float last = (float)Math.Clamp(end * maxSpeed / distance, 0, 1);
            var p = Vector2.Lerp(from, to, first);
            var q = Vector2.Lerp(from, to, last);
            if (!HazardClear(h, p, q, begin, end)) return false;
        }
        return true;
    }

    private static bool HazardClear(Hazard h, Vector2 from, Vector2 to, double begin, double end)
    {
        if (h.Start > end || h.End < begin) return true;
        var p = from - h.Origin;
        var q = to - h.Origin;
        if (h.Circle) return !CircleIntersects(p, q, h.Length);
        float a = Vector2.Dot(p, h.Direction), b = Vector2.Dot(q, h.Direction);
        if (h.HalfPlane) return Math.Max(a, b) < -h.HalfWidth - .001f;
        float s = p.X * h.Direction.Y - p.Y * h.Direction.X;
        float t = q.X * h.Direction.Y - q.Y * h.Direction.X;
        float lo = 0, hi = 1;
        return !(Clip(ref lo, ref hi, a, b - a, -.5f, h.Length + .5f) &&
            Clip(ref lo, ref hi, s, t - s, -h.HalfWidth, h.HalfWidth));
    }

    private static bool Contains(Hazard h, Vector2 point)
    {
        var d = point - h.Origin;
        if (h.Circle) return d.LengthSquared() <= (h.Length + .001f) * (h.Length + .001f);
        float along = Vector2.Dot(d, h.Direction);
        float side = Math.Abs(d.X * h.Direction.Y - d.Y * h.Direction.X);
        return h.HalfPlane ? along >= -h.HalfWidth - .001f :
            along >= -.501f && along <= h.Length + .501f && side <= h.HalfWidth + .001f;
    }

    private static bool CircleIntersects(Vector2 from, Vector2 to, float radius)
    {
        var delta = to - from;
        float lengthSquared = delta.LengthSquared();
        float t = lengthSquared < .00000001f ? 0 : Math.Clamp(-Vector2.Dot(from, delta) / lengthSquared, 0, 1);
        float limit = radius + .001f;
        return (from + delta * t).LengthSquared() <= limit * limit;
    }

    // Parametric interval intersection, padded by a milliyalm so numerical
    // rounding cannot turn a boundary contact into a safe route.
    private static bool Clip(ref float lo, ref float hi, float origin, float delta, float min, float max)
    {
        min -= .001f;
        max += .001f;
        if (Math.Abs(delta) < .000001f) return origin >= min && origin <= max;
        float a = (min - origin) / delta, b = (max - origin) / delta;
        if (a > b) (a, b) = (b, a);
        lo = Math.Max(lo, a);
        hi = Math.Min(hi, b);
        return lo <= hi;
    }
}
