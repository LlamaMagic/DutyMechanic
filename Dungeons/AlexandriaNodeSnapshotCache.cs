using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Keeps detached first-boss helper positions only while a node is visible or
/// still belongs to the active forecast. A vanished wrapper must not silently
/// remove an unresolved hazard; invalid fresh data invalidates the old value.
/// </summary>
internal sealed class AlexandriaNodeSnapshotCache
{
    internal readonly record struct Node(uint Id, uint BaseId, Vector2 Origin);
    private readonly Dictionary<uint, Node> nodes = new();

    internal void Refresh(Node[] visible, uint[] forecastIds)
    {
        foreach (var node in visible)
        {
            if (node.Id != 0 && (node.BaseId is 0x4174 or 0x4175) && AlexandriaGeometry.Finite(node.Origin) &&
                node.Origin.X >= 832.5f && node.Origin.X <= 871.5f && node.Origin.Y >= 808.5f && node.Origin.Y <= 837.5f)
                nodes[node.Id] = node;
            else nodes.Remove(node.Id);
        }
        var retained = forecastIds.Concat(visible.Select(node => node.Id)).ToHashSet();
        foreach (var id in nodes.Keys.Where(id => !retained.Contains(id)).ToArray()) nodes.Remove(id);
    }

    internal bool TryGet(uint id, out Node node) => nodes.TryGetValue(id, out node);
    internal void Clear() => nodes.Clear();
}
