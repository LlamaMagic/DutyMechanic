using Clio.Utilities;
using System;
using System.Collections.Generic;

namespace DutyMechanic.Dungeons;

// Kept independent of frame-scoped actors so selection can be checked with captured
// positions. Native navigation, not this distance heuristic, owns the actual path.
internal static class StrikingTreeHardPolicy
{
    internal readonly record struct Orb(uint Id, Vector3 Position);
    internal readonly record struct Member(uint Id, Vector3 Position, bool Marked, bool Immobilized);

    // Another carrier already within four yalms can supply the rescue hit. Do not
    // intentionally add a second Thunderstorm to that same immobilized player.
    internal static uint RescueTarget(uint self, IReadOnlyList<Member> party)
    {
        Member me = default;
        foreach (var member in party)
            if (member.Id == self)
                me = member;
        if (!me.Marked || me.Immobilized)
            return 0;
        uint selected = 0;
        float nearest = float.MaxValue;
        foreach (var victim in party)
        {
            if (!victim.Immobilized)
                continue;
            bool covered = false;
            foreach (var carrier in party)
                if (carrier.Id != self && carrier.Marked && !carrier.Immobilized
                    && carrier.Position.Distance2D(victim.Position) <= 4)
                    covered = true;
            float distance = me.Position.Distance2D(victim.Position);
            if (!covered && (distance < nearest || distance == nearest && victim.Id < selected))
            {
                selected = victim.Id;
                nearest = distance;
            }
        }
        return selected;
    }

    // Begin with a partial set: waiting for three lets other players consume each
    // small wave while we remain tethered. Aura stacks determine the remaining
    // pickups, and native navigation resolves the path for this distance estimate.
    internal static uint FirstOrb(Vector3 origin, int needed, IReadOnlyList<Orb> orbs)
    {
        if (needed < 1 || needed > 3 || orbs.Count == 0)
            return 0;
        double best = double.MaxValue;
        uint first = 0;
        var used = new bool[orbs.Count];
        void Visit(Vector3 point, int left, double distance, uint choice)
        {
            if (distance > best)
                return;
            if (left == 0)
            {
                if (distance < best || distance == best && choice < first)
                {
                    best = distance;
                    first = choice;
                }
                return;
            }
            for (int i = 0; i < orbs.Count; i++)
            {
                if (used[i])
                    continue;
                used[i] = true;
                Visit(orbs[i].Position, left - 1, distance + point.Distance2D(orbs[i].Position), choice == 0 ? orbs[i].Id : choice);
                used[i] = false;
            }
        }
        Visit(origin, Math.Min(needed, orbs.Count), 0, 0);
        return first;
    }
}
