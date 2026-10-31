using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Clio.Utilities;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing;
using ff14bot.Pathing.Service_Navigation;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Arbitrates Chrysalis positioning, native avoidance and ordinary travel through one
/// borrowed mover. The encounter must validate a request's floor and approach before
/// submitting it; this owner controls input lifetime, not geometry or route selection.
/// </summary>
internal sealed class ChrysalisMovement : IPlayerMover
{
    private readonly IPlayerMover inner;
    private readonly NavigationGate navigation;
    private readonly Func<Vector3, Vector3?> failedEscapeWaypoint;
    private readonly Func<bool> canContinueNativeEscape;
    private readonly Func<Vector3, bool> canMoveOrdinary;
    private Vector3? ordinaryDestination;
    private bool ordinaryBlocked;
    private Vector3? destination;
    private float tolerance;
    private bool detached;
    private bool mechanicOutstanding;
    private bool escapeOutstanding;
    private bool acquiring;
    private bool leavingHazard;
    private bool directEscapeOutstanding;
    private Vector3? directEscapeDestination;
    internal ChrysalisMovement(IPlayerMover inner,
        Func<Vector3, Vector3?> failedEscapeWaypoint = null, Func<bool> canContinueNativeEscape = null,
        Func<Vector3, bool> canMoveOrdinary = null)
    {
        this.inner = inner;
        this.failedEscapeWaypoint = failedEscapeWaypoint;
        this.canContinueNativeEscape = canContinueNativeEscape;
        this.canMoveOrdinary = canMoveOrdinary;
        navigation = new NavigationGate(Navigator.NavigationProvider, this);
        Navigator.NavigationProvider = navigation;
    }

    internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this) && navigation.OwnsProvider;

    // Native escape owns scheduling even when service navigation finds a path.
    // Target acquisition can otherwise stop that path every tick. Input may already
    // have been stopped when queried, so use the planner's lifetime, not held keys.
    internal bool IsEscaping => IsCurrent &&
        AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;

    internal void Request(Vector3? point, float arrivalTolerance)
    {
        acquiring |= point.HasValue && !destination.HasValue;
        destination = point;
        tolerance = arrivalTolerance;
        leavingHazard = false;
    }

    /// <summary>
    /// Accepts an encounter-validated departure from a hazard when native planning has
    /// stalled. The caller must validate the entire segment and release it on expiry;
    /// native escape still preempts this request through the same input owner.
    /// </summary>
    /// <param name="point">Safe endpoint of the validated departure segment.</param>
    /// <param name="arrivalTolerance">Distance at which this owner's input is released.</param>
    internal void RequestHazardExit(Vector3 point, float arrivalTolerance)
    {
        Request(point, arrivalTolerance);
        leavingHazard = true;
    }

    /// <summary>Forwards native escape or ordinary travel when no mechanic owns positioning.</summary>
    /// <param name="location">The destination selected by the current navigation or combat request.</param>
    public void MoveTowards(Vector3 location)
    {
        if (!IsCurrent || destination.HasValue && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        bool escaping = AvoidanceManager.IsRunningOutOfAvoid;
        // Unmaking's combat approach repeatedly re-entered a Vacuum immediately
        // after escape. Validate the actual next waypoint, not the final combat
        // destination: navigation may have a safe detour. Native escape is exempt.
        if (!escaping && !MovementManager.GenerallyOutOfControl && canMoveOrdinary?.Invoke(location) == false)
        {
            BlockOrdinary();
            return;
        }
        ordinaryBlocked = false;
        ordinaryDestination = !escaping && canMoveOrdinary != null ? location : null;
        escapeOutstanding = escaping;
        directEscapeOutstanding = false; // A newer native/ordinary command owns cleanup now.
        directEscapeDestination = null;
        mechanicOutstanding = false;
        inner.MoveTowards(location);
    }

    /// <summary>Accepts a stop without letting a routine callback cancel semantic positioning.</summary>
    public void MoveStop()
    {
        if (!IsCurrent || destination.HasValue && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        StopInput();
    }

    internal void Update()
    {
        if (!IsCurrent || MovementManager.GenerallyOutOfControl)
        {
            return;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // A changing native route can keep its escape flag and old forward input
            // while requesting the next path. An opt-in floor guard may release only
            // input still owned by this escape; it neither chooses nor replaces a path.
            if (escapeOutstanding && canContinueNativeEscape?.Invoke() == false)
            {
                StopInput();
            }
            return;
        }

        navigation.Synchronize();
        // A moving hazard can invalidate held input between navigation commands.
        // Release that same ordinary request; never cancel a newer native escape.
        if (ordinaryDestination.HasValue && canMoveOrdinary?.Invoke(ordinaryDestination.Value) == false)
            BlockOrdinary();
        if (!leavingHazard && AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location)))
        {
            // Stop our old semantic approach while native avoidance prepares its path.
            // Never stop native input merely because its escape flag has not cleared yet.
            if (mechanicOutstanding)
            {
                StopInput();
            }

            return;
        }

        if (destination.HasValue)
        {
            if (Core.Me.Location.Distance2D(destination.Value) > tolerance)
            {
                inner.MoveTowards(destination.Value);
                ordinaryDestination = null;
                ordinaryBlocked = false;
                mechanicOutstanding = true;
                escapeOutstanding = false;
            }
            else
            {
                if (acquiring || mechanicOutstanding || escapeOutstanding)
                {
                    // Acquisition may happen while routine travel is still held. This is
                    // the new request's explicit handoff, not an old request's delayed stop.
                    StopInput();
                }
            }

            acquiring = false;
        }
        else
        {
            if (mechanicOutstanding || escapeOutstanding)
            {
                // RB 1.0.941 can finish an escape while SlideMover still holds forward input.
                // A later ordinary command clears both claims before reaching this branch.
                StopInput();
            }
        }
    }

    private void StopInput()
    {
        if (!IsCurrent)
        {
            return;
        }

        mechanicOutstanding = escapeOutstanding = directEscapeOutstanding = false;
        ordinaryDestination = null;
        directEscapeDestination = null;
        inner.MoveStop();
    }

    private void BlockOrdinary()
    {
        if (!ordinaryBlocked)
        {
            StopInput();
        }
        ordinaryBlocked = true;
    }

    internal void Detach()
    {
        if (!detached && ReferenceEquals(Navigator.PlayerMover, this))
        {
            // If only the provider was replaced, unwrap our mover without stopping
            // input now owned by that successor. Requiring both identities here
            // leaves a detached mover installed and blocks all subsequent travel.
            if (IsCurrent && (mechanicOutstanding || escapeOutstanding))
            {
                StopInput();
            }

            Navigator.PlayerMover = inner;
        }

        navigation.Detach();
        detached = true;
        destination = null;
        acquiring = mechanicOutstanding = escapeOutstanding = false;
        leavingHazard = false;
        directEscapeOutstanding = false;
        directEscapeDestination = null;
        ordinaryDestination = null;
        ordinaryBlocked = false;
    }

    // Service navigation's anti-stuck work can jump/turn without calling IPlayerMover.
    // Admit paths and background pulses through this same owner, clearing the previous
    // route at each handoff. Blocking only the mover leaves a live competing controller.
    private sealed class NavigationGate : WrappingNavigationProvider
    {
        private readonly ChrysalisMovement owner;
        private bool detached;
        private string accepted;
        private long generation;
        internal NavigationGate(NavigationProvider original, ChrysalisMovement owner) : base(original) => this.owner = owner;
        internal bool OwnsProvider => !detached && ReferenceEquals(Navigator.NavigationProvider, this);

        private bool Allowed(MoveToParameters request) => AvoidanceManager.IsRunningOutOfAvoid ? request.Destination?.StartsWith("Running from ", StringComparison.Ordinal) == true : !owner.destination.HasValue;
        internal void Synchronize()
        {
            if (owner.IsCurrent && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Handoff(owner.destination.HasValue ? "mechanic" : null);
            }
        }

        private void Handoff(string next)
        {
            if (!owner.IsCurrent || accepted == next)
            {
                return;
            }

            Original.Clear();
            Original.ClearStuckInfo();
            accepted = next;
            generation++;
        }

        /// <inheritdoc/>
        public override MoveResult MoveTo(MoveToParameters parameters)
        {
            if (!owner.IsCurrent)
            {
                return MoveResult.Done;
            }

            if (!Allowed(parameters))
            {
                return MoveResult.PathGenerating;
            }

            Handoff(AvoidanceManager.IsRunningOutOfAvoid ? "escape" : null);
            // A service retry can return a path rooted at the pre-recovery position.
            // In Unmaking's Looming capture that pulled the player back into Empty
            // Shadow. Finish the still-valid recovery to the same native endpoint
            // before admitting service input; a new endpoint or unsafe leg revokes it.
            if (owner.directEscapeOutstanding && owner.directEscapeDestination.HasValue &&
                owner.directEscapeDestination.Value.Equals(parameters.Location) &&
                AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
                owner.failedEscapeWaypoint?.Invoke(parameters.Location) is Vector3 continuingWaypoint)
            {
                owner.MoveTowards(continuingWaypoint);
                owner.directEscapeOutstanding = true;
                owner.directEscapeDestination = parameters.Location;
                return MoveResult.Moved;
            }
            // Waiting at an unsafe ordinary waypoint is intentional, not a stuck
            // path. Keep route generation available for a safe detour, but prevent
            // the service's jump/turn recovery from bypassing the input guard.
            if (owner.ordinaryBlocked && !AvoidanceManager.IsRunningOutOfAvoid)
                Original.ClearStuckInfo();
            var result = Original.MoveTo(parameters);
            // Unmaking's enlarged floor produced a native escape endpoint but the
            // service route failed repeatedly until death. An opt-in encounter planner
            // may supply a validated segment toward that same native endpoint. Keep
            // its input on this owner and revoke it if the next segment is not valid.
            // Every other caller keeps the original service-navigation behavior.
            // A service retry can spend several pulses in PathGenerating after its
            // failure. The WHM capture lost nearly all travel by stopping the fallback
            // on each of those pulses. Retain it only for the same native endpoint,
            // revalidating its next segment each time; a new native command revokes it.
            bool continuingEscape = result == MoveResult.PathGenerating && owner.directEscapeOutstanding &&
                owner.directEscapeDestination.HasValue && owner.directEscapeDestination.Value.Equals(parameters.Location);
            if ((result == MoveResult.PathGenerationFailed || continuingEscape) && AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl && owner.failedEscapeWaypoint?.Invoke(parameters.Location) is Vector3 waypoint)
            {
                owner.MoveTowards(waypoint);
                owner.directEscapeOutstanding = true;
                owner.directEscapeDestination = parameters.Location;
                return MoveResult.Moved;
            }
            if (owner.directEscapeOutstanding && !MovementManager.GenerallyOutOfControl) owner.StopInput();
            return result;
        }

        /// <inheritdoc/>
        public override void OnPulse()
        {
            if (OwnsProvider && !ReferenceEquals(Navigator.PlayerMover, owner))
            {
                Detach();
                return;
            }

            if (owner.IsCurrent && !owner.destination.HasValue && !owner.ordinaryBlocked && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Original.OnPulse();
            }
        }

        /// <inheritdoc/>
        public override bool Clear() => owner.IsCurrent && !owner.destination.HasValue && !AvoidanceManager.IsRunningOutOfAvoid && Original.Clear();
        /// <inheritdoc/>
        public override void ClearStuckInfo()
        {
            if (owner.IsCurrent && !owner.destination.HasValue && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Original.ClearStuckInfo();
            }
        }

        // RB's wrapping base leaves these inherited query methods unimplemented.
        // GrindSafe calls them while choosing its hotspot; queries own no input and
        // must remain available even when ordinary movement is temporarily gated.
        /// <inheritdoc/>
        public override Task<List<ff14bot.ServiceClient.CanFullyNavigateResult>> CanFullyNavigateTo(ICollection<ff14bot.ServiceClient.CanFullyNavigateTarget> targets, Vector3 start, ushort zoneid) => Original.CanFullyNavigateTo(targets, start, zoneid);
        /// <inheritdoc/>
        public override Task<List<ff14bot.ServiceClient.CanFullyNavigateResult>> CanFullyNavigateFrom(ICollection<ff14bot.ServiceClient.CanFullyNavigateTarget> targets, Vector3 start, ushort zoneid) => Original.CanFullyNavigateFrom(targets, start, zoneid);
        /// <inheritdoc/>
        public override async Task<MoveResult> MoveToAndStopAsync(MoveToParameters parameters, Func<bool> abortCondition = null, float stopDistance = 3.5f)
        {
            long? requestGeneration = null;
            while (owner.IsCurrent)
            {
                // A suspended ordinary route cannot stop the semantic/escape request
                // that replaced it, even if its abort callback fires after the handoff.
                if (requestGeneration.HasValue && requestGeneration != generation)
                {
                    return MoveResult.Done;
                }

                if (abortCondition?.Invoke() == true)
                {
                    if (requestGeneration == generation && Allowed(parameters))
                    {
                        owner.StopInput();
                    }

                    return MoveResult.Done;
                }

                if (Allowed(parameters))
                {
                    if (stopDistance > 0 && Navigator.InPosition(Core.Me.Location, parameters.Location, stopDistance))
                    {
                        owner.StopInput();
                        return MoveResult.ReachedDestination;
                    }

                    var result = MoveTo(parameters);
                    requestGeneration = generation;
                    if (result is MoveResult.Done or MoveResult.ReachedDestination or MoveResult.Failed or MoveResult.PathGenerationFailed)
                    {
                        owner.StopInput();
                        return result;
                    }
                }

                await Coroutine.Yield();
            }

            return MoveResult.Done;
        }

        internal void Detach()
        {
            if (OwnsProvider)
            {
                Navigator.NavigationProvider = Original;
            }

            detached = true;
            generation++;
        }
    }
}
