using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace DutyMechanic.Dungeons;

// Pure, encounter-specific geometry for the Dissonance orb/charge overlap.
// The caller snapshots tells and owns movement; this search cannot touch RB.
internal static class HolminsterOrbRoute
{
    internal enum Shape { Line, Cross, Charge }
    internal readonly record struct Hazard(Shape Kind, Vector2 Origin, float Heading, double Opens, double Closes);
    internal readonly record struct Route(Vector2 First, Vector2 Final, double ReleaseAt, double MinimumSpeed = 4.5);
    private static readonly Vector2 Center = new(-15, 240);
    private static readonly double[] Speeds = [4.5, 5, 5.5, 6, 6.5, 8, 9.5];
    private static readonly double[] PlanningDelays = [0.4, 0.55, 0.7];

    // A fallback floor is valid only for a recent uninterrupted movement
    // sample with the same status effects. Displacement underestimates turns.
    internal sealed class SpeedEvidence
    {
        private Vector2 start;
        private DateTime started, observedAt;
        private string samplingStatuses, observedStatuses;
        private double floor = 4.5;

        internal void Observe(Vector2 position, DateTime now, string statuses, bool moving)
        {
            if (statuses == null || observedStatuses != statuses) observedAt = default;
            if (statuses == null || !moving) { started = default; return; }
            if (started == default || samplingStatuses != statuses)
            {
                start = position; started = now; samplingStatuses = statuses; return;
            }
            double elapsed = (now - started).TotalSeconds;
            if (elapsed < 0.75) return;
            double speed = Vector2.Distance(position, start) / elapsed;
            floor = speed >= 5.75 && speed <= 10 ? Math.Min(5.5, speed - 0.75) : 4.5;
            observedAt = now; observedStatuses = statuses;
            start = position; started = now;
        }

        internal double Minimum(DateTime now, string statuses) => statuses != null && statuses == observedStatuses &&
            observedAt != default && now >= observedAt && (now - observedAt).TotalSeconds <= 8 ? floor : 4.5;
    }

    internal static double FirstRelease(IReadOnlyList<Hazard> hazards)
    {
        double firstOpen = hazards.Min(h => h.Opens);
        return hazards.Where(h => h.Opens <= firstOpen + 0.35).Max(h => h.Closes);
    }

    internal static bool Contains(Hazard hazard, Vector2 point, float margin = 0)
    {
        Vector2 d = point - hazard.Origin;
        double forward = d.X * Math.Sin(hazard.Heading) + d.Y * Math.Cos(hazard.Heading);
        double side = d.X * Math.Cos(hazard.Heading) - d.Y * Math.Sin(hazard.Heading);
        if (hazard.Kind == Shape.Cross)
            return Math.Abs(forward) <= 3.5 + margin && Math.Abs(side) <= 40.5 + margin ||
                Math.Abs(side) <= 3.5 + margin && Math.Abs(forward) <= 40.5 + margin;
        double halfWidth = hazard.Kind == Shape.Line ? 2.5 : 4.5;
        return forward >= -0.5 - margin && forward <= 40.5 + margin && Math.Abs(side) <= halfWidth + margin;
    }

    internal static bool SafePoint(Vector2 point, double at, IReadOnlyList<Hazard> hazards, float margin = 0)
    {
        if (Vector2.Distance(point, Center) > 19 - margin) return false;
        foreach (var hazard in hazards)
            if (at >= hazard.Opens && at <= hazard.Closes && Contains(hazard, point, margin)) return false;
        return true;
    }

    internal static Vector2 Along(Vector2 from, Vector2 to, double distance)
    {
        float length = Vector2.Distance(from, to);
        return length < 0.001f ? to : Vector2.Lerp(from, to, (float)Math.Min(1, Math.Max(0, distance) / length));
    }

    // ReleaseAt is absolute relative to this snapshot: fast movement may arrive
    // earlier, but must wait until the earlier line's bounded window has passed.
    // A route can become unsafe at its hold if the
    // original forecast omitted a fresh input delay for the second leg.
    internal static bool Safe(Vector2 start, Route route, IReadOnlyList<Hazard> hazards, double speed, double inputDelay, double? transitionDelay = null)
    {
        double firstDistance = Vector2.Distance(start, route.First);
        // Revalidation from an acquired hold starts at the observed position,
        // not at a fictional new delayed move to the same waypoint.
        // The movement owner stops within 0.2y, not the 0.3y stage-switch
        // threshold. Treating 0.27y as a hold invalidated an otherwise safe
        // cross route before the player reached its buffered waypoint.
        bool alreadyAtFirst = firstDistance <= 0.2;
        Vector2 first = alreadyAtFirst ? start : route.First;
        double firstArrival = alreadyAtFirst ? 0 : inputDelay + firstDistance / speed;
        double last = hazards.Count == 0 ? 0 : hazards.Max(h => h.Closes);
        for (double t = 0; t <= last + 0.05; t += 0.1)
        {
            Vector2 point = t < firstArrival
                ? Along(start, first, (t - inputDelay) * speed)
                : Along(first, route.Final, (t - Math.Max(firstArrival, route.ReleaseAt) - (transitionDelay ?? inputDelay)) * speed);
            // Include arrival tolerance and sampling uncertainty in every test.
            if (!SafePoint(point, t, hazards, 0.25f)) return false;
        }
        return true;
    }

    internal static double[] SpeedRange(double minimum) => Speeds.Where(speed => speed >= minimum)
        .Prepend(minimum).Distinct().ToArray();

    internal static Route? Find(Vector2 start, Hazard[] hazards, double releaseAt, CancellationToken cancel,
        double measuredMinimum = 4.5)
    {
        var conservative = FindRange(start, hazards, releaseAt, cancel, 4.5);
        return conservative ?? (measuredMinimum > 4.5 ? FindRange(start, hazards, releaseAt, cancel, measuredMinimum) : null);
    }

    private static Route? FindRange(Vector2 start, Hazard[] hazards, double releaseAt, CancellationToken cancel, double minimumSpeed)
    {
        var first = new List<Vector2>();
        var final = new List<Vector2>();
        var speeds = SpeedRange(minimumSpeed);
        double last = hazards.Max(h => h.Closes);
        // Reserve 0.4s for asynchronous search plus up to 0.3s for input.
        // Actual elapsed time is still deducted and every result revalidated.
        for (float x = -19; x <= 19; x += 1f) for (float z = -19; z <= 19; z += 1f)
        {
            var point = Center + new Vector2(x, z);
            if (Vector2.Distance(point, Center) > 18.75f) continue;
            // Leave room for the 0.2y arrival tolerance in addition to the
            // 0.25y collision/sampling margin used during live revalidation.
            if (SafePoint(point, last, hazards, 0.45f)) final.Add(point);
            if (Vector2.Distance(start, point) <= Math.Max(0, releaseAt - 0.7) * minimumSpeed &&
                SafePoint(point, releaseAt, hazards, 0.45f)) first.Add(point);
        }
        first.Sort((a, b) => Vector2.DistanceSquared(start, a).CompareTo(Vector2.DistanceSquared(start, b)));
        final.Sort((a, b) => Vector2.DistanceSquared(start, a).CompareTo(Vector2.DistanceSquared(start, b)));
        foreach (Vector2 a in first)
        {
            cancel.ThrowIfCancellationRequested();
            bool firstSafe = true;
            foreach (double speed in speeds)
            foreach (double delay in PlanningDelays)
            {
                for (double t = 0; t <= releaseAt; t += 0.1)
                    if (!SafePoint(Along(start, a, (t - delay) * speed), t, hazards, 0.25f)) { firstSafe = false; break; }
                if (!firstSafe) break;
            }
            if (!firstSafe) continue;
            foreach (Vector2 b in final)
            {
                var route = new Route(a, b, releaseAt, minimumSpeed);
                bool safe = true;
                foreach (double speed in speeds)
                {
                    if (!Safe(start, route, hazards, speed, 0.4, 0) || !Safe(start, route, hazards, speed, 0.55, 0.15) || !Safe(start, route, hazards, speed, 0.7, 0.3)) { safe = false; break; }
                }
                if (safe) return route;
            }
        }
        return null;
    }
}
