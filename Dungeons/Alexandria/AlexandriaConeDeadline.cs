using System;

namespace DutyMechanic.Dungeons;

// Tracks one observed cone cast without letting a zero-time wrapper extend its
// deadline on every pulse. Ambiguous replacement or an overdue active cast is
// incomplete evidence, not a new guessed safe interval.
internal sealed class AlexandriaConeDeadline
{
    internal double? Due { get; private set; }
    internal double Until { get; private set; }
    internal uint Actor { get; private set; }
    internal uint Action { get; private set; }
    internal bool Incomplete => invalid || overdue;
    private double lastObservation;
    private bool observed, wasCasting, invalid, overdue;

    internal void Observe(double now, bool casting, uint actor = 0, uint action = 0, double remaining = 0)
    {
        if (!double.IsFinite(now) || now < 0 || (observed && now < lastObservation)) { invalid = true; return; }
        observed = true; lastObservation = now;
        if (!casting)
        {
            wasCasting = false;
            if (Due.HasValue && now >= Until) { Due = null; Until = 0; Actor = Action = 0; overdue = false; }
            return;
        }
        if (actor is 0 or 0xE0000000 || action is not (36379 or 36381) ||
            !double.IsFinite(remaining) || remaining < 0) { invalid = true; return; }
        bool same = Due.HasValue && Actor == actor && Action == action;
        if (Due.HasValue && !same && now < Until) { invalid = true; return; }
        if (!same || (!wasCasting && now >= Until))
        {
            double due = now + remaining + .5;
            // The route snapshot retains this cone through due + .4. Use
            // exactly the same boundary for the active-wrapper watchdog: a
            // cast still present after the snapshot ends must fail closed,
            // not disappear for another pulse before becoming incomplete.
            if (!double.IsFinite(due) || !double.IsFinite(due + .4)) { invalid = true; return; }
            Due = due;
            Until = due + .4;
            Actor = actor; Action = action;
            overdue = false;
        }
        else if (wasCasting && now >= Until)
        {
            // A continuously casting wrapper beyond the retained bound may be
            // stale or another cast. Neither possibility proves safety.
            overdue = true;
        }
        wasCasting = true;
    }

    internal void Reset()
    {
        Due = null; Until = 0; Actor = Action = 0;
        lastObservation = 0; observed = wasCasting = invalid = overdue = false;
    }
}
