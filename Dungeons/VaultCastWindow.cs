using System;

namespace DutyMechanic.Dungeons;

// The captured cast bars end before their effects: approximately 0.7s for
// collapse/Rip and 1.6s for Slash. Each window includes scheduling margin;
// absent helpers are not treated as interrupted while their effects can arrive.
internal static class VaultCastWindow
{
    internal static DateTime Until(DateTime expectedEnd, uint action) =>
        expectedEnd + TimeSpan.FromSeconds(action == 4134 ? 2.2 : 1.4);

    internal static bool Interrupted(DateTime now, DateTime expectedEnd, bool observed, bool actorPresent) =>
        !observed && actorPresent && expectedEnd > now.AddMilliseconds(500);
}
