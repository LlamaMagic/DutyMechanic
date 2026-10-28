using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;

namespace DutyMechanic.Dungeons;

public sealed partial class AkhAfahHard
{
    // All native avoidance and routine travel continues through the existing mover.
    // Thin Ice overrides travel; native escape overrides the short Bash rear traversal.
    // RB1.0.941 also needs an explicit stop after escape. New ordinary input revokes
    // the old escape cleanup claim; detached callbacks cannot touch a successor.
    private sealed class MovementGate : IPlayerMover
    {
        private readonly IPlayerMover inner;
        private bool detached;
        private bool holding;
        private bool escapeOutstanding;
        private bool mechanicOutstanding;
        private Vector3? position;

        internal MovementGate(IPlayerMover inner) => this.inner = inner;

        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);

        internal void SetPosition(Vector3? point) => position = point;

        /// <summary>Admits native escape and ordinary travel according to the active mechanic hold.</summary>
        /// <param name="location">The unmodified destination chosen by navigation or combat.</param>
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent || holding || (position.HasValue && !AvoidanceManager.IsRunningOutOfAvoid))
                return;

            escapeOutstanding = AvoidanceManager.IsRunningOutOfAvoid;
            mechanicOutstanding = false;
            inner.MoveTowards(location);
        }

        /// <summary>Accepts an ordinary stop only when it cannot cancel the active rear-position request.</summary>
        public void MoveStop()
        {
            if (!IsCurrent || position.HasValue)
                return;

            StopOwnedInput();
        }

        private void StopOwnedInput()
        {
            if (!IsCurrent)
                return;

            escapeOutstanding = false;
            mechanicOutstanding = false;
            inner.MoveStop();
        }

        internal void Update(bool hold)
        {
            if (!IsCurrent)
                return;

            if (hold && !holding)
                StopOwnedInput();
            holding = hold;
            if (holding)
                return;

            if (position.HasValue && !AvoidanceManager.IsRunningOutOfAvoid
                && !MovementManager.GenerallyOutOfControl
                && !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location)))
            {
                if (Core.Me.Location.Distance2D(position.Value) > 0.6f)
                {
                    inner.MoveTowards(position.Value);
                    mechanicOutstanding = true;
                    escapeOutstanding = false;
                }
                else if (mechanicOutstanding || escapeOutstanding)
                    StopOwnedInput();

                return;
            }

            if (!position.HasValue && mechanicOutstanding && !AvoidanceManager.IsRunningOutOfAvoid)
                StopOwnedInput();
            if (!holding && escapeOutstanding && !AvoidanceManager.IsRunningOutOfAvoid
                && !MovementManager.GenerallyOutOfControl
                && !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location)))
                StopOwnedInput();
        }

        internal void Detach()
        {
            if (IsCurrent)
            {
                if (holding || escapeOutstanding || mechanicOutstanding)
                    StopOwnedInput();
                Navigator.PlayerMover = inner;
            }

            detached = true;
            holding = escapeOutstanding = mechanicOutstanding = false;
            position = null;
        }
    }
}
