using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Used only after authored and checked movement fail. Keep the chosen Trust
// while it remains visible so similar distances do not alternate input owners.
internal static class AlexandriaTrustSelector
{
    internal readonly record struct Candidate(uint Id, Vector2 Position);

    internal static Candidate? Choose(Vector2 player, uint previous, Candidate[] candidates)
    {
        if (!AlexandriaGeometry.Finite(player) || candidates == null) return null;
        Candidate? nearest = null;
        float distance = float.PositiveInfinity;
        foreach (var candidate in candidates)
        {
            if (candidate.Id is 0 or 0xE0000000 || !AlexandriaGeometry.Finite(candidate.Position)) continue;
            if (candidate.Id == previous) return candidate;
            float next = Vector2.DistanceSquared(player, candidate.Position);
            if (next < distance || next == distance && (nearest == null || candidate.Id < nearest.Value.Id))
            { distance = next; nearest = candidate; }
        }
        return nearest;
    }
}
