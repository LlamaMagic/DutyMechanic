using System;
using System.Collections.Generic;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Detached plan lifetime state. This class deliberately cannot move the actor:
// the existing movement owner remains responsible for input and handoffs.
// A delayed worker or cleanup from an older generation must not retire a newer
// request, including after death, loading or an explicit stop invalidates work.
internal sealed class CastrumFluminisPlanLease
{
    internal enum Progress { Invalid, Waiting, OnSchedule, OffSchedule, Complete }
    private long generation;
    private DateTime expiresUtc;
    private IReadOnlyList<CastrumFluminisTimedPlanner.Step> route;

    internal long BeginGeneration()
    {
        generation = checked(generation + 1);
        route = null;
        expiresUtc = default;
        return generation;
    }

    internal bool TryPublish(long token, IReadOnlyList<CastrumFluminisTimedPlanner.Step> candidate,
        DateTime expires, DateTime now)
    {
        if (token != generation || candidate == null || candidate.Count < 2 || expires <= now) return false;
        double previous = -1;
        foreach (var step in candidate)
        {
            if (!float.IsFinite(step.Position.X) || !float.IsFinite(step.Position.Y) ||
                !double.IsFinite(step.Time) || step.Time < 0 || step.Time <= previous) return false;
            previous = step.Time;
        }
        // Copy so a caller cannot mutate a plan after it has been accepted.
        var copy = new CastrumFluminisTimedPlanner.Step[candidate.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = candidate[i];
        route = Array.AsReadOnly(copy);
        expiresUtc = expires;
        return true;
    }

    internal IReadOnlyList<CastrumFluminisTimedPlanner.Step> Get(long token, DateTime now)
        => token == generation && route != null && now < expiresUtc ? route : null;

    // A candidate is meaningful only while the actor follows its timed path.
    // This detached check reports drift; it never stops input or picks a new
    // waypoint. The caller must validate the new snapshot before replanning.
    internal Progress Assess(long token, DateTime now, double elapsedSeconds, Vector2 actual,
        float maximumDeviation)
    {
        var current = Get(token, now);
        if (current == null || !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0 ||
            !float.IsFinite(actual.X) || !float.IsFinite(actual.Y) ||
            !float.IsFinite(maximumDeviation) || maximumDeviation <= 0) return Progress.Invalid;
        // At the exact end of the startup wait the first segment is due; if it
        // is still treated as waiting, a full control pulse can be lost before
        // the first waypoint is issued and its deadline may become unreachable.
        if (elapsedSeconds < current[0].Time)
            return Vector2.Distance(actual, current[0].Position) <= maximumDeviation ? Progress.Waiting : Progress.OffSchedule;
        for (int i = 1; i < current.Count; i++)
        {
            var from = current[i - 1];
            var to = current[i];
            if (!double.IsFinite(from.Time) || !double.IsFinite(to.Time) || to.Time <= from.Time) return Progress.Invalid;
            if (elapsedSeconds > to.Time) continue;
            float fraction = (float)((elapsedSeconds - from.Time) / (to.Time - from.Time));
            var expected = Vector2.Lerp(from.Position, to.Position, fraction);
            return Vector2.Distance(actual, expected) <= maximumDeviation ? Progress.OnSchedule : Progress.OffSchedule;
        }
        return Vector2.Distance(actual, current[^1].Position) <= maximumDeviation ? Progress.Complete : Progress.OffSchedule;
    }

    // Only an on-schedule plan may provide the next timed waypoint. Waiting,
    // expiry and deviation are explicit non-movement outcomes; the movement
    // owner decides how to release input and request a fresh validated plan.
    internal bool TryNextWaypoint(long token, DateTime now, double elapsedSeconds, Vector2 actual,
        float maximumDeviation, out CastrumFluminisTimedPlanner.Step waypoint)
    {
        waypoint = default;
        if (Assess(token, now, elapsedSeconds, actual, maximumDeviation) != Progress.OnSchedule) return false;
        var current = Get(token, now);
        for (int i = 1; i < current.Count; i++)
            if (current[i].Time > elapsedSeconds)
            {
                waypoint = current[i];
                return true;
            }
        return false;
    }

    // Deviation within the tracking tolerance does not prove the remaining
    // path is safe or reachable. Revalidate from the observed position against
    // current detached constraints before exposing a waypoint to the owner.
    internal bool TryNextValidatedWaypoint(long token, DateTime now, double elapsedSeconds, Vector2 actual,
        float maximumDeviation, Vector2 arena, float arenaRadius, float speed,
        IReadOnlyList<CastrumFluminisTimedGeometry.Circle> hazards,
        IReadOnlyList<CastrumFluminisTimedGeometry.Cone> cones,
        IReadOnlyList<CastrumFluminisTimedPlanner.RequiredCircle> circles,
        IReadOnlyList<CastrumFluminisTimedPlanner.RequiredHalfPlane> planes,
        out CastrumFluminisTimedPlanner.Step waypoint,
        IReadOnlyList<CastrumFluminisTimedGeometry.Donut> donuts = null)
    {
        waypoint = default;
        if (!TryNextWaypoint(token, now, elapsedSeconds, actual, maximumDeviation, out var next) ||
            !CastrumFluminisTimedPlanner.ValidateRemainingRoute(Get(token, now), elapsedSeconds, actual,
                arena, arenaRadius, speed, hazards, cones, circles, planes, donuts)) return false;
        waypoint = next;
        return true;
    }

    internal bool TryClear(long token)
    {
        if (token != generation) return false;
        // Invalidate in-flight work as well as the currently published route.
        // Without advancing the generation a delayed worker can resurrect a
        // plan after death, loading, cancellation, or an explicit stop.
        generation = checked(generation + 1);
        route = null;
        expiresUtc = default;
        return true;
    }
}
