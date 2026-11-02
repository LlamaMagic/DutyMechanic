namespace DutyMechanic.Dungeons;

// Another party member may consume either Dragonkiller while we approach.
// Selection operates on current-frame identities; zero represents absence.
internal static class LimitlessBlueCannonPolicy
{
    internal static uint Select(uint first, bool firstAvailable, uint second, bool secondAvailable, uint current = 0)
    {
        if (current != 0 && ((current == first && firstAvailable) || (current == second && secondAvailable)))
            return current;
        return first != 0 && firstAvailable ? first : second != 0 && secondAvailable ? second : 0;
    }
}
