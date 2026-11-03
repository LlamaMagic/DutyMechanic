using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Retains detached Amalgam helper casts across disappearing game wrappers so
/// a combined route does not silently lose a still-active effect. The caller
/// owns cast discovery, identity, timing bounds and the encounter lifecycle.
/// </summary>
internal sealed class AlexandriaAmalgamTimeline
{
    internal readonly record struct Cast(uint Source, uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    private readonly Dictionary<(uint Source, uint Action), Cast> casts = [];
    private readonly HashSet<(uint Source, uint Action)> overdue = [];
    private readonly Dictionary<(uint Source, uint Action), ulong> unsupported = [];
    private ulong nextGeneration;
    internal bool Incomplete { get; private set; }

    // Once a snapshot is invalid, retaining prior entries is not proof that
    // every hazard was captured. Only an encounter reset clears this condition.
    internal void MarkIncomplete() => Incomplete = true;

    // Unsupported stack/spread effects must remain unresolved until an
    // authoritative matching signal exists. Estimated cast timers alone do
    // not prove their effect or marker has ended.
    internal ulong TrackUnsupported(uint source, uint action)
    {
        if (source == 0 || action == 0) { MarkIncomplete(); return 0; }
        var key = (source, action);
        if (unsupported.TryGetValue(key, out var generation)) return generation;
        if (nextGeneration == ulong.MaxValue) { MarkIncomplete(); return 0; }
        generation = ++nextGeneration;
        unsupported.Add(key, generation);
        return generation;
    }

    // A caller may invoke this only after verifying the matching effect or
    // cancellation. Generations prevent an old receipt from clearing a newer
    // mechanic that reused the same helper identity, including after reset.
    internal bool ResolveUnsupported(uint source, uint action, ulong generation) =>
        generation != 0 && unsupported.TryGetValue((source, action), out var current) &&
        current == generation && unsupported.Remove((source, action));

    internal bool HasIncompleteEvidence(double now) =>
        !double.IsFinite(now) || Incomplete || overdue.Count != 0 || unsupported.Count != 0;

    // A stack marker deliberately keeps unsupported evidence pending. The
    // stack owner can still use separately observed fixed hazards if no
    // malformed or overdue cast has made that snapshot unreliable.
    internal bool HasInvalidEvidence(double now) => !double.IsFinite(now) || Incomplete || overdue.Count != 0;

    internal void Reset()
    {
        casts.Clear();
        overdue.Clear();
        Incomplete = false;
        unsupported.Clear();
    }

    internal void Observe(Cast cast, bool remainingIsZero = false)
    {
        if (cast.Source == 0 || cast.Action == 0 || !AlexandriaGeometry.Finite(cast.Origin) ||
            !AlexandriaGeometry.Finite(cast.Direction) || !double.IsFinite(cast.Earliest) ||
            !double.IsFinite(cast.Latest) || cast.Earliest > cast.Latest)
        { MarkIncomplete(); return; }

        var key = (cast.Source, cast.Action);
        // A continuously visible wrapper beyond its original retained window
        // cannot be distinguished from stale or reused cast data. Do not let
        // repeated polling extend it or authorize a route until it disappears.
        if (overdue.Contains(key)) return;
        if (casts.TryGetValue(key, out var previous))
        {
            // A changed origin, orientation or target can mean a reused helper
            // identity. Do not merge distinct effects into a guessed shape.
            if (Vector2.Distance(previous.Origin, cast.Origin) > .25f ||
                Vector2.Distance(previous.Direction, cast.Direction) > .01f ||
                previous.IsLocalTarget != cast.IsLocalTarget)
            { MarkIncomplete(); return; }
            // A wrapper can keep reporting zero remaining after its actual
            // cast has ended. Recomputing now + zero would indefinitely move
            // the effect forward, so keep the last valid union in that state.
            // While the cast still has positive time, retain polling jitter
            // conservatively rather than shrinking the prior window.
            cast = remainingIsZero ? previous : cast with { Earliest = Math.Min(previous.Earliest, cast.Earliest),
                Latest = Math.Max(previous.Latest, cast.Latest) };
        }
        else if (remainingIsZero)
        {
            // Discovering an already expired wrapper cannot establish its
            // true finish or whether an earlier cast has just been retired.
            MarkIncomplete();
            return;
        }
        casts[key] = cast;
    }

    // Reconcile on the same pulse that sampled the object table, before
    // observing casts. Retain expired identities while their wrapper remains;
    // otherwise a stale zero timer would be mistaken for a fresh generation.
    // Once absent, release only that expired identity so normal planning can
    // recover without clearing unrelated unresolved mechanics.
    internal void Reconcile(double now, IEnumerable<(uint Source, uint Action)> active)
    {
        if (!double.IsFinite(now) || active == null) { MarkIncomplete(); return; }
        var present = active.ToHashSet();
        foreach (var item in casts.Where(pair => pair.Value.Latest < now).ToArray())
        {
            if (present.Contains(item.Key)) overdue.Add(item.Key);
            else { casts.Remove(item.Key); overdue.Remove(item.Key); }
        }
    }

    // The worker's stages begin at zero; translate retained monotonic absolute
    // windows to that same snapshot origin before returning detached effects.
    internal AlexandriaAmalgamPlanner.Effect[] Snapshot(double now)
    {
        if (!double.IsFinite(now)) { MarkIncomplete(); return []; }
        return casts.Values.Where(cast => cast.Latest >= now).Select(cast => new AlexandriaAmalgamPlanner.Effect(cast.Action, cast.Origin,
            cast.Direction, cast.Earliest - now, cast.Latest - now, cast.IsLocalTarget)).ToArray();
    }

    // The caller must independently establish that its discovery covers the
    // whole planning horizon, including upcoming mechanics and marker targets.
    // An empty or blocked snapshot cannot authorize an encounter-owned route.
    internal bool TryGetPlanningSnapshot(double now, bool discoveryComplete,
        out AlexandriaAmalgamPlanner.Effect[] effects)
    {
        effects = Snapshot(now);
        if (!discoveryComplete || HasIncompleteEvidence(now) || effects.Length == 0)
        {
            effects = [];
            return false;
        }
        return true;
    }

    // The worker consumes relative windows, whereas a route remains live as
    // monotonic time advances. Revalidation must restore those same windows to
    // the absolute clock rather than compare unlike time bases. Completeness
    // and horizon remain caller-supplied evidence, never inferred from a cast.
    internal bool TryGetAbsoluteHazards(double now, double routeHorizon, double margin,
        bool discoveryComplete, out AlexandriaGeometry.Hazard[] hazards)
    {
        hazards = [];
        if (!double.IsFinite(now) || !double.IsFinite(routeHorizon) || routeHorizon < now ||
            !TryGetPlanningSnapshot(now, discoveryComplete, out var effects) ||
            !AlexandriaAmalgamPlanner.TryCreateHazards(effects, margin, routeHorizon - now, out var relative))
            return false;
        for (int i = 0; i < relative.Length; ++i)
        {
            var shape = relative[i] with { Earliest = relative[i].Earliest + now, Latest = relative[i].Latest + now };
            if (!shape.Valid || shape.Latest > routeHorizon) return false;
            relative[i] = shape;
        }
        hazards = relative;
        return true;
    }
}
