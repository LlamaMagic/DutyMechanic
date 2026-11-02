using System;

namespace DutyMechanic.Dungeons;

// Holy Chain breaks after the paired actors increase their initial separation;
// a fixed radius cannot express that requirement when they start far apart.
internal sealed class VaultChainState
{
    internal const float FallbackRadius = 20f;
    // The chain needs 13 yalms of additional separation. The 0.75-yalm margin
    // covers sampling and movement tolerance rather than changing its mechanic.
    private const float RequiredExtension = 13f;
    private const float ArrivalMargin = 0.75f;
    private uint partnerId;
    private float requiredRadius;
    internal uint PartnerId => partnerId;
    internal float RequiredRadius => requiredRadius;

    internal void Observe(uint observedPartner, float separation)
    {
        if (observedPartner == 0 || !float.IsFinite(separation) || separation < 0)
        {
            Reset();
            return;
        }

        // Keep the acquisition distance for this tether. Recomputing it as either
        // actor moves would move the goal indefinitely and prevent completion.
        if (partnerId != 0)
            return;
        partnerId = observedPartner;
        requiredRadius = separation + RequiredExtension + ArrivalMargin;
    }

    internal float RadiusFor(uint actorId) => actorId == partnerId && partnerId != 0
        ? requiredRadius : FallbackRadius;

    internal void Reset()
    {
        partnerId = 0;
        requiredRadius = 0;
    }
}
