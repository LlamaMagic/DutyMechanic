using System;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing;
using ff14bot.Pathing.Service_Navigation;

namespace DutyMechanic.Dungeons;
// Reuses the exercised final-input gate design: one owner controls semantic holds,
// native emergency avoids retain priority, and detached callbacks cannot stop a successor.
public static partial class SeatOfSacrifice
{
    // One synchronous final-input gate, not another mover loop. Stale callbacks into a
    // detached gate are discarded; foreign replacements are never unwrapped or stopped.
    private sealed class MovementGate : IPlayerMover
    {
        private readonly IPlayerMover inner;
        private bool detached;
        private readonly NavigationGate navigation;
        internal MovementGate(IPlayerMover innerMover)
        {
            inner = innerMover ?? throw new InvalidOperationException("Seat of Sacrifice needs an installed mover");
            navigation = new NavigationGate(Navigator.NavigationProvider, this);
            Navigator.PlayerMover = this;
            Navigator.NavigationProvider = navigation;
            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                inner.MoveStop();
            }
        }

        private bool Owns => !detached && ReferenceEquals(Navigator.PlayerMover, this);

        internal void Command(Vector3 point)
        {
            if (Owns)
            {
                inner.MoveTowards(point);
            }
        }

        internal void Brake()
        {
            if (Owns)
            {
                inner.MoveStop();
            }
        }

        internal void Detach()
        {
            navigation.Detach();
            if (Owns)
            {
                Navigator.PlayerMover = inner;
            }

            detached = true;
        }

        // RB936's path Progress performs its own jump/wall-follow input, bypassing
        // IPlayerMover. The13:23 meteor hold exposed that competing input. Gate path
        // progression under this same owner, while forwarding read-only path queries.
        private sealed class NavigationGate : WrappingNavigationProvider
        {
            private readonly MovementGate owner;
            private bool detached;
            internal NavigationGate(NavigationProvider original, MovementGate owner) : base(original)
            {
                this.owner = owner;
            }

            private bool Owns => !detached && ReferenceEquals(Navigator.NavigationProvider, this);
            private bool PassThrough => !owner.Owns || AvoidanceManager.IsRunningOutOfAvoid || !TreeRoot.IsRunning || !InDuty();

            /// <inheritdoc/>
            public override MoveResult MoveTo(MoveToParameters parameters) => Owns && PassThrough ? Original.MoveTo(parameters) : MoveResult.PathGenerating;
            /// <inheritdoc/>
            public override void OnPulse()
            {
                if (Owns && PassThrough)
                {
                    Original.OnPulse();
                }
            }

            /// <inheritdoc/>
            public override async Task<MoveResult> MoveToAndStopAsync(MoveToParameters parameters, Func<bool> abortCondition = null, float stopDistance = 3.5f)
            {
                while (Owns && !PassThrough)
                {
                    if (abortCondition?.Invoke() == true)
                    {
                        return MoveResult.Done;
                    }

                    await Coroutine.Yield();
                }

                // A detached request is canceled, not an arrival, and must never stop
                // or issue a destination into a successor's movement window.
                if (!Owns)
                {
                    return MoveResult.Done;
                }

                return await Original.MoveToAndStopAsync(parameters, abortCondition, stopDistance);
            }

            internal void Detach()
            {
                if (Owns)
                {
                    // RB's supported provider lifecycle cancels its old path/anti-stuck
                    // state before restoration. Do not replay a pre-mechanic escape path.
                    Navigator.NavigationProvider = Original;
                }

                detached = true;
            }
        }

        /// <summary>Allows only an explicit RB avoidance handoff during a semantic movement lease.</summary>
        /// <param name = "point">Destination requested by the provider or routine.</param>
        public void MoveTowards(Vector3 point)
        {
            if (Owns && (AvoidanceManager.IsRunningOutOfAvoid || !TreeRoot.IsRunning || !InDuty()))
            {
                inner.MoveTowards(point);
            }
        }

        /// <summary>Preserves avoidance/user-stop cleanup while rejecting stale lower-priority path stops.</summary>
        public void MoveStop()
        {
            if (Owns && (AvoidanceManager.IsRunningOutOfAvoid || !TreeRoot.IsRunning || !InDuty()))
            {
                inner.MoveStop();
            }
        }
    }
}
