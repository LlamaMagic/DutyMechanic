using System;

namespace DutyMechanic.Dungeons;

/// <summary>Estimates walking capacity without retaining a start/stop velocity fragment.</summary>
/// <remarks>
/// A captured startup sample of 1.02 yalms/second rejected a reachable Divide
/// escape. Default to normal running speed (6 yalms/second) until movement has
/// settled for 200 ms. Sustained slowdowns and external boosts update the estimate;
/// standing still preserves the last settled speed.
/// </remarks>
internal sealed class InterphosMovementSpeed
{
    private const double SettlementSeconds = 0.2;
    private float candidate;
    private double candidateSince;
    private bool sampling;
    internal float Value { get; private set; } = 6;

    internal float Observe(double now, float velocity, bool moving)
    {
        if (!moving || !double.IsFinite(now) || !float.IsFinite(velocity) || velocity <= 0)
        {
            sampling = false;
            return Value;
        }
        // Small scalar rounding differences must not prevent a settled reading.
        if (!sampling || now < candidateSince || MathF.Abs(velocity - candidate) > 0.05f)
        {
            candidate = velocity;
            candidateSince = now;
            sampling = true;
        }
        if (now - candidateSince >= SettlementSeconds)
        {
            Value = velocity;
        }

        return Value;
    }

    internal void Reset()
    {
        sampling = false;
        candidate = 0;
        candidateSince = 0;
        Value = 6;
    }
}
