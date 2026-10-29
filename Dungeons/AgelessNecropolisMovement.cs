using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Arbitrates Necron's anchor, prison and native-escape input through the installed
/// mover. An earlier request cannot stop a newer dodge or a replacement controller.
/// </summary>
internal sealed class AgelessNecropolisMovement(IPlayerMover inner) : IPlayerMover
{
    // Each request identifies who may release the current input. External movement
    // supersedes encounter requests; native escape always takes priority.
    internal enum Request
    {
        None,
        External,
        Escape,
        Anchor,
        PrisonDodge,
        Portal,
        Tower
    }

    private Request owner;
    private bool detached;

    internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);

    /// <summary>Forwards ordinary framework input, superseding any earlier encounter request.</summary>
    /// <param name="location">Destination selected by navigation, combat or native avoidance.</param>
    public void MoveTowards(Vector3 location)
    {
        if (!IsCurrent)
        {
            return;
        }

        owner = AvoidanceManager.IsRunningOutOfAvoid ? Request.Escape : Request.External;
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

    internal void Detach()
    {
        if (IsCurrent)
        {
            if (owner is Request.Escape or Request.Anchor or Request.PrisonDodge or Request.Portal or Request.Tower)
            {
                MoveStop();
            }

            Navigator.PlayerMover = inner;
        }

        detached = true;
        owner = Request.None;
    }
}
