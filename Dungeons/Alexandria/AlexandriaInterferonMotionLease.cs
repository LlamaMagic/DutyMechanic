using System;
using System.Numerics;

namespace DutyMechanic.Dungeons;

// Tracks only the current owner's most recent direct-move request. A route
// check must not charge a complete new startup delay on every pulse while the
// same command is visibly progressing, but an interrupted, stale, or changed
// command must never inherit that reduced allowance.
internal sealed class AlexandriaInterferonMotionLease
{
    private Vector2? target;
    private double submitted, lastProgress;
    private float bestDistance;

    internal void Begin(Vector2 destination, Vector2 player, double now)
    {
        if (!AlexandriaGeometry.Finite(destination) || !AlexandriaGeometry.Finite(player) ||
            !double.IsFinite(now) || now < 0) { Reset(); return; }
        if (target == destination) return;
        target = destination; submitted = now; lastProgress = double.NegativeInfinity;
        bestDistance = Vector2.Distance(player, destination);
    }

    internal double Delay(Vector2 destination, Vector2 player, double now,
        bool moving, bool casting, bool interrupted)
    {
        const double startup = .1, ongoing = .05;
        if (!AlexandriaGeometry.Finite(player) || !double.IsFinite(now) || now < submitted)
        { Reset(); return startup; }
        if (target != destination) return startup;
        float distance = Vector2.Distance(player, destination);
        if (!moving || casting || interrupted)
        {
            submitted = now; lastProgress = double.NegativeInfinity; bestDistance = distance;
            return startup;
        }
        if (distance + .03f < bestDistance) { bestDistance = distance; lastProgress = now; }
        // Keep half the original allowance even after progress: the measured
        // 33 ms retarget sample is not a worst-case guarantee. Stale progress
        // or a newly submitted request restores the full startup allowance.
        return now - submitted >= startup && now - lastProgress <= .15 ? ongoing : startup;
    }

    internal void Reset() { target = null; submitted = 0; lastProgress = 0; bestDistance = 0; }
}
