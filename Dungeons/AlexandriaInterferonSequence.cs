using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// A scalar snapshot of the ordered first-boss actors. Pulse discovery provides
// advance warning for their 0.7s casts; equal-pulse discoveries remain unordered.
internal sealed class AlexandriaInterferonSequence
{
    internal static (bool Interferon, bool Cone) SelectEffects(double? interferon, double? cone) =>
        (interferon.HasValue && (!cone.HasValue || interferon.Value <= cone.Value + .5),
         cone.HasValue && (!interferon.HasValue || cone.Value <= interferon.Value + .5));

    internal readonly record struct Observation(uint Id, bool Visible, bool Casting, double RemainingSeconds, uint Action = 0, bool ExplicitlyIdle = false);
    private sealed class Entry(uint id) { internal uint Id = id, Action; internal bool SeenCast, Resolved, MissingCast, InvalidTiming; internal double Finish, CastObserved, IdleSince = double.NaN; }
    private sealed class Group(double due) { internal double Due = due; internal readonly List<Entry> Entries = []; }
    private readonly List<Group> groups = [];
    private readonly Dictionary<uint, Entry> entries = [];
    private double firstSeen, lastVisible, lastObservation;
    private bool lateDiscovery, overflow, invalidData, hasObservation;

    // An unanchored, overflowing or missed-cast wave cannot safely be treated
    // as empty. Keep the owner in an explicit incomplete state until evidence
    // resolves the missing cast or the wave is reset after disappearance.
    internal bool UnanchoredUnresolved => invalidData || entries.Values.Any(entry => !entry.Resolved &&
        (lateDiscovery || overflow || entry.MissingCast));

    internal void Reset()
    {
        groups.Clear(); entries.Clear(); firstSeen = lastVisible = lastObservation = 0;
        lateDiscovery = overflow = invalidData = hasObservation = false;
    }

    // A coherent action-effect receipt proves that this exact cast has
    // resolved before subsequent input. The caller must own a gap-free stream
    // for the encounter and supply the lower bound of the batch observation;
    // a receipt that might have been buffered before cast discovery cannot be
    // associated safely, even if its source ID and action happen to match.
    // This remains detached until the native observer is validated for use by
    // gameplay; no timer or missing actor is treated as an equivalent receipt.
    internal bool ConfirmEffect(uint id, uint action, double observedAfter, double observedBy, bool streamComplete)
    {
        if (!streamComplete || invalidData || !double.IsFinite(observedAfter) || !double.IsFinite(observedBy) ||
            observedAfter < 0 || observedBy < observedAfter || observedBy > lastObservation ||
            !entries.TryGetValue(id, out var entry) || entry.Resolved || !entry.SeenCast || entry.InvalidTiming ||
            entry.Action is not (36382 or 36383) || action != entry.Action || observedAfter <= entry.CastObserved)
            return false;
        entry.Resolved = true;
        entry.MissingCast = false;
        return true;
    }

    internal void Observe(double now, IReadOnlyList<Observation> observations)
    {
        // Invalid or reordered samples cannot prove an empty wave. Retain the
        // incomplete state until an explicit lifecycle reset rather than turn
        // corrupted time or actor identity into a seemingly safe forecast.
        if (!double.IsFinite(now) || now < 0 || observations == null || (hasObservation && now < lastObservation) ||
            observations.Any(actor => actor.Id is 0 or 0xE0000000) || observations.Select(actor => actor.Id).Distinct().Count() != observations.Count)
        { invalidData = true; return; }
        hasObservation = true; lastObservation = now;
        if (observations.Any(actor => actor.Visible)) lastVisible = now;
        else if (entries.Count != 0 && now - lastVisible > 2)
        {
            // A vanished wrapper is not proof that an unseen attack fired.
            // Retire only previously observed casts after their conservative
            // window; an unobserved attack remains explicitly incomplete.
            foreach (var entry in entries.Values)
            {
                if (!entry.Resolved && entry.SeenCast && !entry.MissingCast && now >= entry.Finish + .30)
                    entry.Resolved = true;
                else if (!entry.Resolved && !entry.SeenCast)
                    entry.MissingCast = true;
            }
            if (entries.Values.All(entry => entry.Resolved)) { Reset(); return; }
        }
        var newActors = observations.Where(actor => actor.Visible && !entries.ContainsKey(actor.Id)).ToArray();
        if (newActors.Length != 0)
        {
            if (entries.Count == 0)
            {
                firstSeen = now;
                // An already-casting first observation cannot anchor a wave.
                lateDiscovery = newActors.Length > 1 || newActors.Any(actor => actor.Casting);
            }
            var group = new Group(firstSeen + 9.9 + entries.Count * 2.5);
            foreach (var actor in newActors)
            {
                var entry = new Entry(actor.Id);
                entries.Add(actor.Id, entry);
                group.Entries.Add(entry);
            }
            groups.Add(group);
            if (entries.Count > 5) overflow = true;
        }
        // An absent or invisible wrapper breaks the continuous-idle evidence;
        // a later reappearance must begin a fresh confirmation interval.
        var visibleIds = observations.Where(actor => actor.Visible).Select(actor => actor.Id).ToHashSet();
        foreach (var entry in entries.Values)
            if (!visibleIds.Contains(entry.Id)) entry.IdleSince = double.NaN;
        foreach (var actor in observations)
        {
            if (!entries.TryGetValue(actor.Id, out var entry) || entry.Resolved) continue;
            if (actor.Casting)
            {
                entry.IdleSince = double.NaN;
                if (actor.RemainingSeconds < 0 || !double.IsFinite(actor.RemainingSeconds))
                { entry.MissingCast = entry.InvalidTiming = true; continue; }
                // A helper identity can be reused or its wrapper can be read
                // during a transition. A different action must not inherit
                // the earlier cast's deadline or release its hazard. Restore
                // this entry only if its originally observed action returns.
                if (entry.SeenCast && entry.Action != 0 && actor.Action != entry.Action)
                { entry.MissingCast = entry.InvalidTiming = true; continue; }
                // Some wrappers retain the cast with a zero remaining time
                // after its predicted finish. Recomputing now + remaining on
                // every pulse drifts the effect later and can strand the next
                // shelter. Each discovered helper owns one cast in this wave;
                // retain its first valid finish estimate instead.
                bool firstCast = !entry.SeenCast;
                entry.SeenCast = true;
                entry.MissingCast = entry.InvalidTiming = false;
                // Captured vulnerability effects arrived 0.44-0.80s after the reported
                // cast finish. Model the effect, then retain a 0.30s observation margin.
                if (firstCast)
                {
                    entry.Finish = now + actor.RemainingSeconds + .55;
                    entry.CastObserved = now;
                    entry.Action = actor.Action;
                }
            }
            else if (entry.SeenCast && !entry.InvalidTiming && actor.Visible && actor.ExplicitlyIdle &&
                entry.Action is 36382 or 36383 && now >= entry.Finish - .55)
            {
                // In five Global-client captures, 163 matched node effects and
                // explicit cast-end observations appeared in the same pulse.
                // Wait 100ms of continuously visible idle state to reject a
                // transient blank wrapper before releasing the shelter. The
                // observed cast and its first finish estimate anchor this to
                // the current helper; missing/invalid wrappers cannot resolve.
                // This empirical transition still needs live and regional
                // validation; it is not proof of the server snapshot time.
                if (double.IsNaN(entry.IdleSince)) entry.IdleSince = now;
                if (now - entry.IdleSince >= .1)
                { entry.Resolved = true; entry.MissingCast = false; }
            }
            else if (entry.SeenCast && !entry.InvalidTiming && actor.Visible && actor.ExplicitlyIdle && now >= entry.Finish + .30)
            {
                // Only an explicitly idle, visible wrapper confirms a cast
                // ended. An invalid cast-info wrapper or another action is
                // not evidence that the prior node effect has resolved.
                entry.Resolved = true;
                entry.MissingCast = false;
            }
            else entry.IdleSince = double.NaN;
        }
        // A previously seen cast can also disappear from the object table.
        // Once its modeled window ends without a valid non-casting sample,
        // do not let forecast filtering turn it into an empty safe region.
        foreach (var entry in entries.Values)
            if (!entry.Resolved && entry.SeenCast && !entry.MissingCast && now > entry.Finish + .30)
                entry.MissingCast = true;
        if (lateDiscovery || overflow) return;
        foreach (var group in groups)
        {
            // A predicted time passing without a cast is not proof of an
            // effect. Match the forecast consumer's 0.30s retirement bound so
            // there is no interval where a missed cast becomes invisible.
            double groupEnd = group.Due + (group.Entries.Count - 1) * 2.5 + .30;
            if (now <= groupEnd) continue;
            foreach (var entry in group.Entries)
                if (!entry.SeenCast) entry.MissingCast = true;
        }
    }

    internal bool IsActive(uint id, bool casting)
    {
        if (UnanchoredUnresolved) return casting;
        var group = groups.FirstOrDefault(candidate => candidate.Entries.Any(entry => !entry.Resolved));
        if (group == null) return casting;
        var castingEntries = group.Entries.Where(entry => !entry.Resolved && entry.SeenCast).ToArray();
        // Once a member of an unordered group starts casting, it is the
        // current hazard; otherwise all still-possible members remain active.
        return castingEntries.Length != 0
            ? castingEntries.Any(entry => entry.Id == id)
            : group.Entries.Any(entry => !entry.Resolved && entry.Id == id);
    }

    internal double? NextDue(double now)
    {
        if (UnanchoredUnresolved) return null;
        var group = groups.FirstOrDefault(candidate => candidate.Entries.Any(entry => !entry.Resolved));
        if (group == null) return null;
        var casting = group.Entries.Where(entry => !entry.Resolved && entry.SeenCast).ToArray();
        // A known earlier member of an unordered group has consumed its slot;
        // do not let its stale deadline suppress an intervening boss cone.
        return casting.Length == 0
            ? group.Due + group.Entries.Count(entry => entry.Resolved) * 2.5
            : casting.Min(entry => entry.Finish);
    }

    // Simultaneous discovery does not reveal internal order. Give every
    // unresolved actor the full remaining slot range, so a planner considers
    // every possible order rather than treating an ambiguous wave as absent.
    // These slot estimates remain provisional until action receipts calibrate
    // them; an unanchored first observation cannot provide any forecast.
    internal (uint Id, double Earliest, double Latest)[] Forecast()
    {
        if (UnanchoredUnresolved) return [];
        return groups.SelectMany(g =>
        {
            double first = g.Due + g.Entries.Count(e => e.Resolved) * 2.5;
            double last = g.Due + (g.Entries.Count - 1) * 2.5;
            return g.Entries.Where(e => !e.Resolved)
                .Select(e => (e.Id, e.SeenCast ? e.Finish : first, e.SeenCast ? e.Finish : last));
        }).ToArray();
    }
}
