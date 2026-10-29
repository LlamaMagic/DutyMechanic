using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing;
using ff14bot.Pathing.Service_Navigation;

namespace DutyMechanic.Dungeons;

public sealed partial class GarudaHard
{
    // Routine capabilities do not gate OrderBot's Kill Target paths. This adapter
    // admits both through one channel, with native escape above pillar shelter.
    // It also owns the last escape input: RB can clear its escape flag without
    // releasing SlideMover's forward key. New commands always revoke that old claim.
    private sealed class MovementGate : IPlayerMover
    {
        private readonly GarudaHard encounter;
        private readonly IPlayerMover inner;
        private readonly NavigationGate navigation;
        private bool detached;
        private bool forwarding;
        private bool semanticRequest;
        private string inputOwner;

        internal MovementGate(GarudaHard encounter)
        {
            this.encounter = encounter;
            inner = Navigator.PlayerMover ?? throw new InvalidOperationException("Garuda Hard requires an installed mover.");
            navigation = new NavigationGate(Navigator.NavigationProvider, this);
            Navigator.PlayerMover = this;
            Navigator.NavigationProvider = navigation;
        }

        private bool Owns => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal bool IsCurrent => Owns && navigation.OwnsProvider;
        private bool Restricted => EncounterActive() && (encounter.positioning || AvoidanceManager.IsRunningOutOfAvoid);

        private bool Allow(MoveToParameters request) => !EncounterActive()
            || (AvoidanceManager.IsRunningOutOfAvoid
                ? request.Destination?.StartsWith("Running from ", StringComparison.Ordinal) == true
                : !encounter.positioning || semanticRequest);

        internal void Approach(Vector3 point, float tolerance)
        {
            if (!Owns || AvoidanceManager.IsRunningOutOfAvoid)
                return;
            semanticRequest = true;
            try
            {
                if (Core.Me.Location.Distance2D(point) <= tolerance)
                    navigation.Hold();
                else
                    navigation.MoveTo(new MoveToParameters(point, "Garuda Hard mechanic")
                    { DistanceTolerance = tolerance, UseMount = false });
            }
            finally
            {
                semanticRequest = false;
            }
        }

        internal void ReleasePosition() => navigation.ReleasePosition();

        internal void CompleteEscape()
        {
            if (!Owns || inputOwner != "escape" || AvoidanceManager.IsRunningOutOfAvoid
                || MovementManager.GenerallyOutOfControl || InHazard()) return;
            StopOwnedInput();
            Logger.Information($"[GarudaHard] Released completed escape input at {Core.Me.Location}.");
        }

        private void StopOwnedInput()
        {
            if (!Owns)
                return;
            inputOwner = null;
            inner.MoveStop();
        }

        /// <summary>Forwards admitted movement and records its cleanup owner.</summary>
        /// <param name="point">The destination chosen by the existing navigator.</param>
        public void MoveTowards(Vector3 point)
        {
            if (!Owns || (!forwarding && !navigation.NativeEscapeInput && Restricted))
                return;
            inputOwner = AvoidanceManager.IsRunningOutOfAvoid ? "escape" : semanticRequest ? "mechanic" : "ordinary";
            inner.MoveTowards(point);
        }

        /// <summary>Accepts the current owner's stop; detached callbacks cannot stop a successor.</summary>
        public void MoveStop()
        {
            if (Owns && (forwarding || navigation.NativeEscapeInput || !Restricted))
                StopOwnedInput();
        }

        internal void Detach()
        {
            navigation.Detach();
            if (Owns)
            {
                if (inputOwner is "mechanic" or "escape")
                    StopOwnedInput();
                Navigator.PlayerMover = inner;
            }
            detached = true;
        }

        private sealed class NavigationGate : WrappingNavigationProvider
        {
            private readonly MovementGate owner;
            private bool detached;
            private string acceptedOwner;
            private long generation;

            internal NavigationGate(NavigationProvider original, MovementGate owner) : base(original)
            {
                this.owner = owner;
            }
            private bool Owns => !detached && owner.Owns && ReferenceEquals(Navigator.NavigationProvider, this);
            internal bool OwnsProvider => !detached && ReferenceEquals(Navigator.NavigationProvider, this);
            internal bool NativeEscapeInput => Owns && acceptedOwner == "escape" && AvoidanceManager.IsRunningOutOfAvoid;

            private void Handoff(string next)
            {
                if (!Owns || acceptedOwner == next)
                    return;
                Original.Clear();
                Original.ClearStuckInfo();
                // End the previous path's input before admitting its successor. There
                // is no delayed stop that could erase the successor's first command.
                owner.StopOwnedInput();
                acceptedOwner = next;
                generation++;
            }

            internal void Hold()
            {
                if (!Owns || AvoidanceManager.IsRunningOutOfAvoid)
                    return;
                Handoff("mechanic");
                if (owner.inputOwner != null)
                    owner.StopOwnedInput();
            }

            internal void ReleasePosition()
            {
                if (Owns && acceptedOwner == "mechanic" && !AvoidanceManager.IsRunningOutOfAvoid)
                    Handoff(null);
            }

            /// <inheritdoc/>
            public override MoveResult MoveTo(MoveToParameters parameters)
            {
                if (!Owns)
                    return MoveResult.Done;
                if (!owner.Allow(parameters))
                    return MoveResult.PathGenerating;
                Handoff(AvoidanceManager.IsRunningOutOfAvoid ? "escape" : owner.semanticRequest ? "mechanic" : "ordinary");
                owner.forwarding = true;
                try
                {
                    return Original.MoveTo(parameters);
                }
                finally
                {
                    owner.forwarding = false;
                }
            }

            /// <inheritdoc/>
            public override void OnPulse()
            {
                if (OwnsProvider && !owner.Owns)
                {
                    owner.encounter.LoseMovementOwnership();
                    return;
                }
                // Death/loading can bypass the encounter's TreeStart hook. The
                // navigator's lifecycle still releases only this gate's input.
                if (Owns && !EncounterActive())
                {
                    owner.encounter.ResetMovement();
                    return;
                }
                // Service navigation's background jump/wall-follow work otherwise
                // bypasses the mover while a semantic hold owns the current input.
                if (Owns && !owner.Restricted)
                    Original.OnPulse();
            }

            /// <inheritdoc/>
            public override bool Clear() => Owns && !owner.Restricted && Original.Clear();
            /// <inheritdoc/>
            public override void ClearStuckInfo()
            {
                if (Owns && !owner.Restricted)
                    Original.ClearStuckInfo();
            }

            /// <inheritdoc/>
            public override Task<List<ff14bot.ServiceClient.CanFullyNavigateResult>> CanFullyNavigateTo(
                ICollection<ff14bot.ServiceClient.CanFullyNavigateTarget> targets, Vector3 start, ushort zoneid)
                => Original.CanFullyNavigateTo(targets, start, zoneid);

            // The wrapping base leaves bulk queries unimplemented. GrindSafe needs
            // these read-only mesh queries even while mechanic movement owns input.
            /// <inheritdoc/>
            public override Task<List<ff14bot.ServiceClient.CanFullyNavigateResult>> CanFullyNavigateFrom(
                ICollection<ff14bot.ServiceClient.CanFullyNavigateTarget> targets, Vector3 start, ushort zoneid)
                => Original.CanFullyNavigateFrom(targets, start, zoneid);

            /// <inheritdoc/>
            public override async Task<MoveResult> MoveToAndStopAsync(MoveToParameters parameters,
                Func<bool> abortCondition = null, float stopDistance = 3.5f)
            {
                long? requestGeneration = null;
                while (Owns)
                {
                    if (requestGeneration.HasValue && requestGeneration != generation)
                        return MoveResult.Done;
                    if (abortCondition?.Invoke() == true)
                    {
                        if (requestGeneration == generation && owner.Allow(parameters))
                            Handoff(null);
                        return MoveResult.Done;
                    }
                    if (owner.Allow(parameters))
                    {
                        if (stopDistance > 0 && Navigator.InPosition(Core.Me.Location, parameters.Location, stopDistance))
                        {
                            owner.StopOwnedInput();
                            return MoveResult.ReachedDestination;
                        }
                        var result = MoveTo(parameters);
                        requestGeneration = generation;
                        if (result is MoveResult.Done or MoveResult.ReachedDestination or MoveResult.Failed or MoveResult.PathGenerationFailed)
                        {
                            owner.StopOwnedInput();
                            return result;
                        }
                    }
                    await Coroutine.Yield();
                }
                return MoveResult.Done;
            }

            internal void Detach()
            {
                // Losing only the mover must still unwrap our provider; otherwise
                // the foreign mover inherits a permanently rejecting old gate.
                if (OwnsProvider)
                    Navigator.NavigationProvider = Original;
                detached = true;
                generation++;
            }
        }
    }
}
