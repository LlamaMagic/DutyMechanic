namespace DutyMechanic.Dungeons;

// Sanuwa gain damage reduction when close. A tank should preserve its own add
// instead of attacking the one already held by the other tank.
internal static class LimitlessBlueSanuwaPolicy
{
    internal readonly record struct Add(uint Id, bool Ready, uint Target);

    // Moving away separates the pair only if the assigned add follows this
    // tank and the other add follows a different actor. Unknown or shared
    // targets need enmity recovery, not an unproductive movement loop.
    internal static uint SeparationTarget(Add green, Add blue, uint self, uint assigned)
    {
        if (self == 0 || !green.Ready || !blue.Ready) return 0;
        Add own = assigned == green.Id ? green : assigned == blue.Id ? blue : default;
        Add other = assigned == green.Id ? blue : green;
        return own.Ready && own.Target == self && other.Target != 0 && other.Target != self ? other.Id : 0;
    }

    internal static uint Select(Add green, Add blue, uint self, uint otherTank, uint current)
    {
        if (green.Ready && blue.Ready && otherTank != 0)
        {
            if (green.Target == otherTank && blue.Target != otherTank) return blue.Id;
            if (blue.Target == otherTank && green.Target != otherTank) return green.Id;
        }
        if (self != 0 && green.Ready && blue.Ready)
        {
            if (green.Target == self && blue.Target != self) return green.Id;
            if (blue.Target == self && green.Target != self) return blue.Id;
        }
        if (current != 0 && ((green.Ready && green.Id == current) || (blue.Ready && blue.Id == current)))
            return current;
        return green.Ready ? green.Id : blue.Ready ? blue.Id : 0;
    }
}
