using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Detached final-boss cast ledger. Keeping source and action together prevents
// a reused helper from silently replacing another still-unresolved effect.
// It records only observed signals; future baits and party constraints remain
// the caller's responsibility.
internal sealed class AlexandriaEliminatorTimeline
{
    internal readonly record struct Cast(uint Source, uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    private readonly Dictionary<(uint Source, uint Action), Cast> casts = [];
    private readonly HashSet<(uint Source, uint Action)> overdue = [];
    internal bool Incomplete { get; private set; }

    internal void MarkIncomplete() => Incomplete = true;

    internal void Reset()
    {
        casts.Clear(); overdue.Clear(); Incomplete = false;
    }

    internal void Observe(Cast cast, bool remainingIsZero = false)
    {
        if (cast.Source == 0 || cast.Action == 0 || !AlexandriaGeometry.Finite(cast.Origin) ||
            !AlexandriaGeometry.Finite(cast.Direction) || !double.IsFinite(cast.Earliest) ||
            !double.IsFinite(cast.Latest) || cast.Earliest > cast.Latest)
        { MarkIncomplete(); return; }
        var key = (cast.Source, cast.Action);
        if (overdue.Contains(key)) return;
        if (casts.TryGetValue(key, out var previous))
        {
            if (Vector2.Distance(previous.Origin, cast.Origin) > .25f ||
                Vector2.Distance(previous.Direction, cast.Direction) > .01f || previous.IsLocalTarget != cast.IsLocalTarget)
            { MarkIncomplete(); return; }
            cast = remainingIsZero ? previous : cast with
            { Earliest = Math.Min(previous.Earliest, cast.Earliest), Latest = Math.Max(previous.Latest, cast.Latest) };
        }
        else if (remainingIsZero) { MarkIncomplete(); return; }
        casts[key] = cast;
    }

    // A timer alone cannot retire a helper that remains visibly casting: the
    // wrapper might be stale or the same identity might have been reused.
    internal void Reconcile(double now, IEnumerable<(uint Source, uint Action)> active)
    {
        if (!double.IsFinite(now) || active == null) { MarkIncomplete(); return; }
        var present = active.ToHashSet();
        foreach (var entry in casts.Where(pair => pair.Value.Latest < now).ToArray())
        {
            if (present.Contains(entry.Key)) overdue.Add(entry.Key);
            else { casts.Remove(entry.Key); overdue.Remove(entry.Key); }
        }
    }

    internal bool HasIncompleteEvidence(double now) => !double.IsFinite(now) || Incomplete || overdue.Count != 0;

    internal AlexandriaEliminatorPlanner.Effect[] Snapshot(double now)
    {
        if (!double.IsFinite(now)) { MarkIncomplete(); return []; }
        return casts.Values.Where(cast => cast.Latest >= now).Select(cast =>
            new AlexandriaEliminatorPlanner.Effect(cast.Action, cast.Origin, cast.Direction,
                cast.Earliest - now, cast.Latest - now, cast.IsLocalTarget)).ToArray();
    }
}
