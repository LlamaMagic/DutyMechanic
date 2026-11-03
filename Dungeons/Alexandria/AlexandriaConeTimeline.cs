using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// Multiple helpers can overlap. Keep each cast's stable deadline independently
// so observing a new cone never silently erases an unresolved earlier one.
internal sealed class AlexandriaConeTimeline
{
    internal readonly record struct Observation(uint Actor, uint Action, double Remaining);
    private readonly Dictionary<(uint Actor, uint Action), AlexandriaConeDeadline> casts = [];
    private bool invalid, transientInvalid, observed;
    private double lastObservation;

    internal bool Incomplete => invalid || transientInvalid || casts.Values.Any(cast => cast.Incomplete);
    internal IEnumerable<AlexandriaConeDeadline> Active => casts.Values.Where(cast => cast.Due.HasValue);
    internal void MarkIncomplete() => invalid = true;

    internal void Observe(double now, IReadOnlyList<Observation> observations)
    {
        if (!double.IsFinite(now) || now < 0 || (observed && now < lastObservation) || observations == null ||
            observations.Any(item => item.Actor is 0 or 0xE0000000 || item.Action is not (36379 or 36381)) ||
            observations.Select(item => item.Actor).Distinct().Count() != observations.Count)
        { invalid = true; return; }
        observed = true; lastObservation = now; transientInvalid = false;
        var seen = new HashSet<(uint Actor, uint Action)>();
        foreach (var item in observations)
        {
            var key = (item.Actor, item.Action); seen.Add(key);
            bool known = casts.TryGetValue(key, out var deadline);
            if (!double.IsFinite(item.Remaining) || item.Remaining < 0)
            {
                // A known cast retains its earlier deadline while its wrapper
                // is temporarily unreadable. An unseen cast has no trustworthy
                // deadline and cannot be made complete by later disappearance.
                if (known) transientInvalid = true; else invalid = true;
                continue;
            }
            if (!known) casts.Add(key, deadline = new AlexandriaConeDeadline());
            deadline.Observe(now, true, item.Actor, item.Action, item.Remaining);
        }
        foreach (var key in casts.Keys.ToArray())
        {
            if (seen.Contains(key)) continue;
            var deadline = casts[key]; deadline.Observe(now, false);
            // Preserve invalid evidence until the encounter reset, even if its
            // actor vanishes; an absent wrapper cannot repair an ambiguity.
            if (!deadline.Due.HasValue && !deadline.Incomplete) casts.Remove(key);
        }
    }

    internal void Reset()
    {
        casts.Clear(); invalid = transientInvalid = observed = false; lastObservation = 0;
    }
}
