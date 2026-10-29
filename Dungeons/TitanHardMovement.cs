using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Releases native escape input through the existing mover channel. RB 1.0.941 can
/// clear its escape flag without releasing SlideMover's forward input; a later
/// ordinary move or stop supersedes that cleanup claim before it reaches the mover.
/// </summary>
internal sealed class TitanHardMovement : IPlayerMover
{
    private readonly IPlayerMover inner;
    private bool escapeOutstanding;
    private bool detached;

    internal TitanHardMovement(IPlayerMover inner) => this.inner = inner;
    internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);

    /// <summary>Forwards RB's destination and transfers ownership to its latest request.</summary>
    /// <param name="location">The unmodified navigation or combat destination.</param>
    public void MoveTowards(Vector3 location)
    {
        if (!IsCurrent)
        {
            return;
        }
        escapeOutstanding = AvoidanceManager.IsRunningOutOfAvoid;
        inner.MoveTowards(location);
    }

    /// <summary>Releases input only while this adapter still owns the movement channel.</summary>
    public void MoveStop()
    {
        if (!IsCurrent)
        {
            return;
        }
        escapeOutstanding = false;
        inner.MoveStop();
    }

    internal void CompleteEscape()
    {
        // A flag edge or IsMoving alone cannot prove ownership. A successor command,
        // overlapping hazard or forced movement must retain its own input lifetime.
        if (!IsCurrent || !escapeOutstanding || AvoidanceManager.IsRunningOutOfAvoid
            || MovementManager.GenerallyOutOfControl
            || AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location)))
            return;

        MoveStop();
        Logger.Information("[TitanHard] Released completed native escape input at " + Core.Me.Location + ".");
    }

    internal void Detach()
    {
        if (IsCurrent)
        {
            if (escapeOutstanding)
            {
                MoveStop();
            }
            Navigator.PlayerMover = inner;
        }
        detached = true;
        escapeOutstanding = false;
    }
}
