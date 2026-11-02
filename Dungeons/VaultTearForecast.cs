using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Slash creates a tear at the arena wall about 16.8y in its final facing
// direction. Captures differ from this projection by up to 1.59y, so the
// consumer adds a 1.75y prediction margin. Freeze the first sub-300ms facing
// rather than chasing a turning caster with a moving destination.
internal sealed class VaultTearForecast
{
    internal sealed class Forecast(Vector2 origin, DateTime until, HashSet<uint> existing)
    {
        internal Vector2 Origin = origin;
        internal DateTime Until = until;
        internal HashSet<uint> Existing = existing;
    }
    private readonly Dictionary<uint, (bool Captured, DateTime LastSeen)> casts = new();
    private readonly List<Forecast> forecasts = new();
    internal IReadOnlyList<Forecast> Active => forecasts;

    internal bool Observe(uint actor, double remainingMs, Vector2 position, float heading,
        IEnumerable<uint> existingTears, DateTime now)
    {
        casts.TryGetValue(actor, out var state);
        state.LastSeen = now;
        bool created = false;
        if (!state.Captured && remainingMs is >= 0 and <= 300)
        {
            var direction = new Vector2((float)Math.Sin(heading), (float)Math.Cos(heading));
            forecasts.Add(new Forecast(position + direction * 16.8f, now.AddSeconds(4), existingTears.ToHashSet()));
            state.Captured = true;
            created = true;
        }
        casts[actor] = state;
        return created;
    }

    internal void Reconcile(IEnumerable<(uint Id, Vector2 Position)> tears, DateTime now)
    {
        var observed = tears.ToArray();
        // Do not mistake a pre-existing tear at the same location for the new
        // actor. Otherwise the protective prediction could vanish before spawn.
        forecasts.RemoveAll(f => now >= f.Until || observed.Any(t => !f.Existing.Contains(t.Id) &&
            Vector2.Distance(t.Position, f.Origin) <= 2.25f));
        foreach (var id in casts.Where(p => now - p.Value.LastSeen > TimeSpan.FromMilliseconds(500)).Select(p => p.Key).ToArray())
            casts.Remove(id);
    }

    internal void Reset()
    {
        casts.Clear();
        forecasts.Clear();
    }
}
