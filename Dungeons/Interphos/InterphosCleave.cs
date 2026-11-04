namespace DutyMechanic.Dungeons;

/// <summary>One visible windup followed by its ordered, instant second cleave.</summary>
internal sealed class InterphosCleave
{
    // Invisible helpers share the boss NPC ID; the observed casting boss has this base ID.
    internal static bool IsBoss(uint npcId, uint baseId) => npcId == 13029 && baseId == 0x41C0;

    private uint windup;
    private int secondSide;
    private double secondEnds;
    internal int Side
    {
        get;
        private set;
    }
    internal ulong Caster
    {
        get;
        private set;
    }

    // Native escape stopped within its five-yalm tolerance at the cleave boundary.
    // Use a direct square-floor step and retain it through the ordered strikes.
    // The half-room edge is X=100, with 0.5 clearance and 0.25 arrival tolerance.
    internal static bool SquareUnsafe(float x, int side) =>
        side != 0 && (!float.IsFinite(x) || (x - 100f) * side >= -0.75f);

    internal static float SquareDestinationX(int side) => 100f - side;

    internal void Observe(ulong caster, uint action, double now)
    {
        if (caster == 0 || (Caster != 0 && Caster != caster))
        {
            Reset();
        }

        if (caster == 0)
        {
            return;
        }

        if (InterphosFloor.TryForceSequence(action, out var first, out var second))
        {
            if (windup == action)
            {
                return;
            }

            Caster = caster;
            windup = action;
            Side = first == InterphosFloor.Side.Left ? -1 : 1;
            secondSide = second == InterphosFloor.Side.Left ? -1 : 1;
            secondEnds = 0;
        }
        else if (action != 0)
        {
            Reset();
        }
        else if (windup != 0)
        {
            // The visible cast has ended. The reference places the instant
            // second strike 3.1 seconds later; never restart this window on polls.
            windup = 0;
            Side = secondSide;
            secondEnds = now + 3.1;
        }
        else if (Side != 0 && now >= secondEnds)
        {
            Reset();
        }
    }

    internal void Reset()
    {
        windup = 0;
        secondSide = 0;
        secondEnds = 0;
        Side = 0;
        Caster = 0;
    }
}
