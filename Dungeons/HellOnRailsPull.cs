using System;
using Clio.Utilities;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Distinguishes Windpipe's late ground displacement from earlier platform descent.
/// The captured draw-in did not set RB's forced-movement flag, so position and cast timing
/// decide the handoff. This observer never issues movement.
/// </summary>
internal sealed class HellOnRailsPull
{
    private float? stagingZ;
    private float? previousZ;
    private DateTime settleAfter;
    internal DateTime CastEnds { get; set; }
    internal bool Displaced { get; private set; }
    internal bool Settled { get; private set; }

    internal void Observe(DateTime now, Vector3 position)
    {
        if (Settled || position.Y is < -0.2f or > 1) return;
        if (now < CastEnds.AddSeconds(-0.5))
        {
            stagingZ = previousZ = position.Z;
            return;
        }

        // Two yalms toward the engine excludes stationary cast locks and small corrections.
        // Crate cover may prevent displacement entirely; after the observed pull duration,
        // that stationary survivor must still hand off to the turret-safe recovery route.
        if (stagingZ.HasValue && stagingZ.Value - position.Z > 2)
            Displaced = true;
        if (Displaced && previousZ.HasValue && previousZ.Value - position.Z > 0.05f)
            settleAfter = now.AddSeconds(0.15);
        previousZ = position.Z;
        if ((Displaced && now >= settleAfter) || (!Displaced && now >= CastEnds.AddSeconds(1.5)))
            Settled = true;
    }
}
