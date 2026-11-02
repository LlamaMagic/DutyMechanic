using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Pure schedule cursor for a verified Everkeep route. Timing is relative to
// the immutable worker snapshot, so a delayed or interrupted route expires
// rather than silently teleporting to its next waypoint.
internal sealed class EverkeepRouteProgress
{
    internal enum Decision { Wait, Move, Complete, Expired }
    internal enum Arrival { Approaching, Reached, Deviated }
    private readonly EverkeepTemporalPlanner.Step[] steps;
    private int index;
    private bool crossedCurrent;
    private readonly double firstDeparture;
    internal EverkeepRouteProgress(EverkeepTemporalPlanner.Step[] route, bool emergencyEgress = false)
    {
        steps = route ?? throw new ArgumentNullException(nameof(route));
        firstDeparture = emergencyEgress ? 0 : .3;
    }

    internal Decision Next(double elapsed, Vector2 here, out Vector2 target, out double untilDeparture)
    {
        target = here;
        untilDeparture = double.PositiveInfinity;
        if (!double.IsFinite(elapsed) || elapsed < 0 || !float.IsFinite(here.X) || !float.IsFinite(here.Y))
            return Decision.Expired;
        while (index < steps.Length)
        {
            var step = steps[index];
            double departure = index == 0 ? firstDeparture : steps[index - 1].Time;
            if (crossedCurrent || Vector2.Distance(here, step.Position) <= .25f)
            {
                if (elapsed + .025 < step.Time)
                {
                    untilDeparture = step.Time - elapsed;
                    return Decision.Wait;
                }
                index++;
                crossedCurrent = false;
                continue;
            }
            if (elapsed > step.Time + .2) return Decision.Expired;
            if (elapsed + .025 < departure)
            {
                untilDeparture = departure - elapsed;
                return Decision.Wait;
            }
            target = step.Position;
            untilDeparture = 0;
            return Decision.Move;
        }
        return Decision.Complete;
    }

    internal void MarkCrossed() => crossedCurrent = true;

    // A discrete pulse can skip a small arrival circle entirely. Crossing the
    // target plane is still an arrival, provided the actor stayed close to the
    // commanded line. A lateral departure instead requires a fresh plan.
    internal static Arrival Classify(Vector2 start, Vector2 target, Vector2 here)
    {
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) ||
            !float.IsFinite(target.X) || !float.IsFinite(target.Y) ||
            !float.IsFinite(here.X) || !float.IsFinite(here.Y)) return Arrival.Deviated;
        var direction = target - start;
        float length = direction.Length();
        if (length < .001f) return Arrival.Reached;
        var fromStart = here - start;
        float lateral = Math.Abs(fromStart.X * direction.Y - fromStart.Y * direction.X) / length;
        if (lateral > .75f) return Arrival.Deviated;
        return Vector2.Distance(here, target) <= .25f || Vector2.Dot(here - target, direction) >= 0
            ? Arrival.Reached : Arrival.Approaching;
    }

    // Look past same-position holds without advancing the cursor. A new cast
    // can then be checked against the next real leg before cancelling input.
    internal bool TryNextMove(double elapsed, Vector2 here, out Vector2 target, out double departure)
    {
        target = here;
        departure = 0;
        for (int i = index; i < steps.Length; i++)
        {
            if (Vector2.Distance(here, steps[i].Position) <= .25f) continue;
            target = steps[i].Position;
            departure = Math.Max(0, (i == 0 ? firstDeparture : steps[i - 1].Time) - elapsed);
            return true;
        }
        return false;
    }
}
