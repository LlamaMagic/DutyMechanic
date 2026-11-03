using System;

namespace DutyMechanic.Dungeons;

// Detached Sprint evidence for the first-boss planner. The caller samples the
// actual status on the bot thread and must invalidate a route when this
// evidence changes; this type neither activates Sprint nor owns movement.
internal static class AlexandriaSprintPolicy
{
    // Two 2026-11-03 cleared-arena captures observed roughly 7.5–8.9 y/s
    // steady movement and turns. These deliberately wider bounds and the
    // half-second expiry allowance remain provisional until combat and job
    // variants are tested; a speed-limiting status requires a slower model.
    internal const double SprintMinimum = 6.8, SprintMaximum = 10, ExpiryAllowance = .5;

    internal static bool TrySnapshot(double now, double? remaining,
        out AlexandriaTemporalPlanner.MovementBoost? boost)
    {
        boost = null;
        if (!double.IsFinite(now) || now < 0) return false;
        if (remaining is not { } value) return true;
        if (!double.IsFinite(value) || value < 0) return false;
        double earliest = now + Math.Max(0, value - ExpiryAllowance);
        double latest = now + value + ExpiryAllowance;
        if (!double.IsFinite(earliest) || !double.IsFinite(latest)) return false;
        boost = new(earliest, latest, SprintMinimum, SprintMaximum);
        return true;
    }

    // A newly acquired, prematurely lost, or extended status invalidates the
    // prior envelope. The caller must cancel its worker/route and replan;
    // absence inside the uncertain expiry interval remains conservatively
    // covered by that interval's slower minimum and faster maximum.
    internal static bool Consistent(AlexandriaTemporalPlanner.MovementBoost? planned,
        double now, double? observedRemaining)
    {
        if (!double.IsFinite(now) || now < 0 ||
            (observedRemaining is { } invalid && (!double.IsFinite(invalid) || invalid < 0))) return false;
        if (planned is not { } expected) return !observedRemaining.HasValue;
        if (!AlexandriaTemporalPlanner.TryMovementBounds(now, now, 5, 8, expected, out _, out _)) return false;
        if (observedRemaining is not { } remaining) return now >= expected.EarliestExpiry;
        double observedExpiry = now + remaining;
        return double.IsFinite(observedExpiry) && now <= expected.LatestExpiry &&
            observedExpiry >= expected.EarliestExpiry && observedExpiry <= expected.LatestExpiry;
    }
}
