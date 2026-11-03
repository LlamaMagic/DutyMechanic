using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// A line aimed at the local player must not pass through another party member.
// The caller refreshes party positions and checks that the destination can be
// reached before the line's earliest possible effect.
internal static class AlexandriaEliminatorProtean
{
    internal static bool Safe(Vector2 source, Vector2 candidate, Vector2[] party)
    {
        if (!AlexandriaGeometry.Finite(source) || !AlexandriaGeometry.Finite(candidate) || party == null || party.Length == 0)
            return false;
        var delta = candidate - source;
        if (delta.LengthSquared() < .25f) return false;
        var direction = Vector2.Normalize(delta);
        if (!AlexandriaMechanicShapes.TryCreate(36783, source, direction, .5, 0, 1, out var line)) return false;
        foreach (var member in party)
            if (!AlexandriaGeometry.Finite(member) || AlexandriaGeometry.Distance(member, line) <= line.Margin) return false;
        return true;
    }
}
