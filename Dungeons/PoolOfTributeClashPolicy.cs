using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// A complete roster is the encounter-local evidence for choosing a blocker; do not
// infer the visit's mode from mutable Duty Finder settings or from a partial roster.
internal static class PoolOfTributeClashPolicy
{
    internal const int TrialLevelCap = 64;
    internal enum Mode { Unknown, LiveParty, AutoParty }
    internal readonly record struct Member(uint ObjectId, int Level, bool IsTank, bool IsAlive);
    internal readonly record struct Decision(Mode PartyMode, uint? BlockerId);

    internal static Decision Select(IReadOnlyCollection<Member> members, int partyCount)
    {
        if (members == null || members.Count != partyCount || members.Any(m => m.ObjectId == 0 || m.Level <= 0) ||
            members.Select(m => m.ObjectId).Distinct().Count() != members.Count)
            return new Decision(Mode.Unknown, null);

        if (partyCount == 8 && members.First().Level <= TrialLevelCap && members.All(m => m.Level == members.First().Level))
        {
            // In a full matched party, only a living tank claims the lotus. Stable
            // object-ID order lets independently controlled tanks agree on one.
            var tank = members.Where(m => m.IsAlive && m.IsTank).OrderBy(m => m.ObjectId).Select(m => (Member?)m).FirstOrDefault();
            return new Decision(Mode.LiveParty, tank?.ObjectId);
        }

        if (partyCount == 2)
        {
            var ordered = members.OrderBy(m => m.Level).ToArray();
            // The higher-level carry must remain on the sword. Do not silently
            // reassign the interaction to them if the lower-level player is dead.
            if (ordered[1].Level > TrialLevelCap && ordered[0].Level < ordered[1].Level)
                return new Decision(Mode.AutoParty, ordered[0].IsAlive ? ordered[0].ObjectId : null);
        }

        return new Decision(Mode.Unknown, null);
    }
}
