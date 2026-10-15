// Diagnostic-only scalar history: never retain a native RB wrapper in this buffer.
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Helpers;
/// <summary>Bounds diagnostic evidence independently by expiry and capacity on the bot thread.</summary>
internal sealed class MechanicDiagnosticHistory<T>
{
    private readonly int capacity;
    private readonly List<Entry> entries = new();
    internal int Dropped { get; private set; }
    internal int Count => entries.Count;

    internal MechanicDiagnosticHistory(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    // Casts end out of insertion order. A FIFO head check would let an old short cast
    // linger behind a still-active long cast; prune every entry before eviction.
    internal void Prune(DateTime now) => entries.RemoveAll(e => e.ExpiresAtUtc < now);
    internal void Add(DateTime observed, DateTime expires, T value)
    {
        Prune(observed);
        if (entries.Count == capacity)
        {
            entries.RemoveAt(0);
            Dropped++;
        }

        entries.Add(new Entry(observed, expires, value));
    }

    internal Entry[] Snapshot(DateTime now)
    {
        Prune(now);
        return entries.ToArray();
    }

    internal void Clear()
    {
        entries.Clear();
        Dropped = 0;
    }

    // Thirty seconds after estimated cast finish covers delayed choreography, including
    // the 12.7s Fireflight cast whose later hit outlived the previous 12s start window.
    // This is correlation evidence, not an assertion that a particular action dealt damage.
    // Cap malformed/native sentinel durations at two minutes to keep evidence bounded.
    internal static DateTime CastExpiry(DateTime now, double remainingSeconds) => now.AddSeconds(Math.Clamp(double.IsFinite(remainingSeconds) ? remainingSeconds : 0, 0, 120) + 30);
    internal readonly record struct Entry(DateTime ObservedAtUtc, DateTime ExpiresAtUtc, T Value);
}
