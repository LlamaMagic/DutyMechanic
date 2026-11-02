using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// Edge-spawned marchers keep their identity as they cross the center; dormant
// same-name helpers at the center must never become moving-lane hazards.
internal sealed class VaultMarchState
{
    private readonly HashSet<uint> actors = new();
    internal bool Contains(uint id) => actors.Contains(id);
    internal void Clear() => actors.Clear();
    internal void Update(IEnumerable<(uint Id, float Distance)> visible)
    {
        var snapshot = visible.ToArray();
        var present = snapshot.Select(a => a.Id).ToHashSet();
        actors.RemoveWhere(id => !present.Contains(id));
        foreach (var actor in snapshot)
            if (actor.Distance >= 17f) actors.Add(actor.Id);
    }
}
