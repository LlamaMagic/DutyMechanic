using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Arbitrates Doomtrain's panel, lane, tower and native-escape input through the installed
/// mover. An earlier request cannot stop a newer dodge or a replacement controller.
/// </summary>
internal sealed class HellOnRailsMovement(IPlayerMover inner, Func<Vector3, Vector3?> constrainGroundMove = null) : IPlayerMover
{
    // Each request identifies who may release the current input. External movement
    // supersedes encounter requests; native escape always takes priority.
    internal enum Request
    {
        None,
        External,
        Escape,
        Elevation,
        Lightning,
        Windpipe,
        Derail,
        Tower,
        // Platform drops share input ownership with every other encounter request.
        DeckEscape,
        // Collision-checked turret/Hail routes remain requests to this same input owner.
        GroundRoute
    }

    private Request owner;
    private bool detached;
    private DateTime nextCastInterrupt;
    private DateTime lastExternalInput;

    internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);

    /// <summary>Forwards ordinary framework input, superseding any earlier encounter request.</summary>
    /// <param name="location">Destination selected by navigation, combat or native avoidance.</param>
    public void MoveTowards(Vector3 location)
    {
        if (!IsCurrent)
        {
            return;
        }

        if (constrainGroundMove != null)
        {
            // The service's wall-follow recovery issues its own steering after a crate collision.
            // Clear that recovery before it can accumulate; this actor's owner resolves blocked
            // ground steps on the encounter floor instead. A missing step releases held input.
            Navigator.NavigationProvider?.ClearStuckInfo();
            var constrained = constrainGroundMove(location);
            if (constrained == null)
            {
                MoveStop();
                return;
            }
            location = constrained.Value;
        }
        owner = AvoidanceManager.IsRunningOutOfAvoid ? Request.Escape : Request.External;
        lastExternalInput = DateTime.UtcNow;
        inner.MoveTowards(location);
    }

    /// <summary>Forwards an explicit framework stop only while this adapter remains installed.</summary>
    public void MoveStop()
    {
        if (!IsCurrent)
        {
            return;
        }

        owner = Request.None;
        inner.MoveStop();
    }

    internal void Move(Request request, Vector3 destination)
    {
        if (!IsCurrent || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            return;
        }

        // Cancel a previous service route before taking ownership; all resulting stop
        // calls pass through this channel before the new request becomes authoritative.
        Navigator.Stop();
        owner = request;
        inner.MoveTowards(destination);
    }

    internal void ReleaseExpiredExternalInput(DateTime now)
    {
        // The service can repeatedly await new paths without issuing another mover call.
        // Without expiry, a retained sideways command can carry the player off the car.
        // Active path following refreshes each tick; 150ms permits several ticks but limits
        // stale travel to under one yalm at running speed. Never release a semantic successor.
        if (IsCurrent && owner is Request.External or Request.Escape &&
            !MovementManager.GenerallyOutOfControl && now - lastExternalInput > TimeSpan.FromMilliseconds(150))
            MoveStop();
    }

    internal void InterruptCastForEscape()
    {
        if (!IsCurrent || !Core.Me.IsCasting || MovementManager.ForceMoved || DateTime.UtcNow < nextCastInterrupt)
            return;
        // Do not bypass GenerallyOutOfControl. Cancel only the interruptible cast and let
        // the ordinary gate reopen on a later frame; stun, loading and forced travel still win.
        nextCastInterrupt = DateTime.UtcNow.AddMilliseconds(250);
        ActionManager.StopCasting();
    }

    internal void Release(Request request)
    {
        if (IsCurrent && owner == request && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            MoveStop();
        }
    }

    internal void CompleteEscape()
    {
        // RB1.0.941 can clear its escape path without releasing forward input. Track
        // the command itself, not just a flag edge, so successor travel is preserved.
        if (!IsCurrent || owner != Request.Escape || AvoidanceManager.IsRunningOutOfAvoid ||
            MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location)))
        {
            return;
        }

        MoveStop();
    }

    internal void ClearNavigationRecovery(bool landedOnNewCar)
    {
        if (!IsCurrent || constrainGroundMove == null) return;
        // Recovery must not outlive the floor it was calculated for. Clearing its state on
        // pulses also prevents direct wall-follow input while a service path request is pending.
        Navigator.NavigationProvider?.ClearStuckInfo();
        if (landedOnNewCar)
        {
            Navigator.Stop();
            MoveStop();
        }
    }

    internal void Detach()
    {
        if (IsCurrent)
        {
            if (owner is Request.Escape or Request.Elevation or Request.Lightning or Request.Windpipe or Request.Derail or Request.Tower or Request.DeckEscape or Request.GroundRoute)
            {
                MoveStop();
            }

            Navigator.PlayerMover = inner;
        }

        detached = true;
        owner = Request.None;
    }
}
