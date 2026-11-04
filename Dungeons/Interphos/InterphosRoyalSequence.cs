using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>Orders Royal Banishment helpers by their observed cast deadlines.</summary>
/// <remarks>
/// Captures contain three-then-four and four-then-three cone groups,
/// separated by about two seconds. Combining both makes every arena point unsafe.
/// Only the earliest group publishes avoidance; later groups remain pending.
/// This state owns no input and is reset with the encounter movement lifecycle.
/// </remarks>
internal sealed class InterphosRoyalSequence
{
    // Status observations occurred roughly 0.7–0.9 seconds after RB's cast timer.
    // The 0.7-second handoff remains provisional: status polling is not an
    // exact effect timestamp. Recheck it against the next damage trace.
    internal const double EffectTail = 0.7;
    private readonly Dictionary<uint, double> deadlines = new();
    private double first = double.PositiveInfinity;

    internal bool Active => deadlines.Count != 0;
    internal int Count => deadlines.Count;

    internal void Observe(uint actor, double remainingSeconds, double now)
    {
        if (actor == 0 || !double.IsFinite(now) || !double.IsFinite(remainingSeconds) || remainingSeconds < 0)
        {
            return;
        }
        // Snapshot once: timer jitter must not continually extend a resolved wave.
        if (!deadlines.ContainsKey(actor))
        {
            deadlines.Add(actor, now + remainingSeconds + EffectTail);
        }
    }

    internal void Advance(double now)
    {
        foreach (var actor in deadlines.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
        {
            deadlines.Remove(actor);
        }

        first = deadlines.Count == 0 ? double.PositiveInfinity : deadlines.Values.Min();
    }

    // Helpers from one simultaneous group can be sampled on adjacent pulses.
    // The 150ms grouping tolerance is much smaller than the captured 1.98s stagger.
    internal bool Includes(uint actor) => deadlines.TryGetValue(actor, out double end) && end <= first + 0.15;

    internal void Reset()
    {
        deadlines.Clear();
        first = double.PositiveInfinity;
    }
}
