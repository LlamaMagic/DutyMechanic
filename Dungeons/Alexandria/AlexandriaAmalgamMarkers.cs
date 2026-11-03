using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// Tracks observed stack/spread markers independently of cast timer estimates.
// A caller must supply actual target identity and valid marker observations;
// no elapsed cast time alone may retire an unsupported effect.
internal sealed class AlexandriaAmalgamMarkers
{
    internal readonly record struct Pending(uint Source, uint Action, uint Target, uint Marker, ulong Generation);
    private sealed class Entry(Pending pending)
    {
        internal Pending Value = pending;
        internal bool SeenWhileCasting;
        internal double? AbsentSince;
        internal double LastObservation;
        internal bool Observed;
    }
    private readonly Dictionary<(uint Source, uint Action), Entry> entries = [];
    internal bool Incomplete { get; private set; }
    internal Pending[] Snapshot() => entries.Values.Select(entry => entry.Value).ToArray();

    internal void ObserveCast(uint source, uint action, uint target, ulong generation)
    {
        uint marker = action switch { 36333 => 161, 36334 => 139, _ => 0 };
        if (source is 0 or 0xE0000000 || target is 0 or 0xE0000000 || marker == 0 || generation == 0)
        { Incomplete = true; return; }
        var key = (source, action);
        var pending = new Pending(source, action, target, marker, generation);
        if (entries.TryGetValue(key, out var previous))
        {
            if (previous.Value != pending) Incomplete = true;
            return;
        }
        entries.Add(key, new Entry(pending));
    }

    // Marker absence is accepted only after the same marker was seen while
    // this source cast was active, the cast ended, and valid observations agree
    // for 200ms. This debounces transient wrapper loss; it is not an estimated
    // effect deadline. Unknown or missing targets never resolve the mechanic.
    internal bool ObserveMarker(uint source, uint action, double now, bool casting,
        bool markerAvailable, bool markerPresent, out ulong generation)
    {
        generation = 0;
        if (!entries.TryGetValue((source, action), out var entry)) { Incomplete = true; return false; }
        if (!double.IsFinite(now) || now < 0 || (entry.Observed && now < entry.LastObservation))
        { Incomplete = true; return false; }
        entry.Observed = true; entry.LastObservation = now;
        if (!markerAvailable) { entry.AbsentSince = null; return false; }
        if (markerPresent)
        {
            if (casting) entry.SeenWhileCasting = true;
            entry.AbsentSince = null; return false;
        }
        if (casting || !entry.SeenWhileCasting) { entry.AbsentSince = null; return false; }
        entry.AbsentSince ??= now;
        if (now - entry.AbsentSince.Value < .2) return false;
        generation = entry.Value.Generation;
        return true;
    }

    // Remove only the exact generation acknowledged by the timeline; an old
    // marker observation must never retire a successor using the same source.
    internal bool Forget(uint source, uint action, ulong generation) =>
        entries.TryGetValue((source, action), out var entry) && entry.Value.Generation == generation &&
        entries.Remove((source, action));

    internal void Reset() { entries.Clear(); Incomplete = false; }
}
