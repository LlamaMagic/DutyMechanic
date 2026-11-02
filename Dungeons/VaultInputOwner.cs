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

/// <summary>
/// Arbitrates Vault positioning, native avoidance and ordinary travel through one
/// borrowed mover. Faith Unmoving validates the flat arena floor, hazards and approach before
/// submitting it; this owner controls input lifetime, not geometry or route selection.
/// </summary>
internal sealed class VaultInputOwner : IPlayerMover
{
    private readonly IPlayerMover inner;
    private readonly NavigationGate navigation;
    private readonly Func<Vector3, bool> safeOrdinaryWaypoint;
    private Vector3? destination;
    private Vector3? ordinaryWaypoint;
    private bool ordinaryBlocked;
    private float tolerance;
    private bool detached;
    private bool mechanicOutstanding;
    private bool escapeOutstanding;
    private bool acquiring;
    internal VaultInputOwner(IPlayerMover inner, Func<Vector3, bool> safeOrdinaryWaypoint)
    {
        this.inner = inner;
        this.safeOrdinaryWaypoint = safeOrdinaryWaypoint;
        navigation = new NavigationGate(Navigator.NavigationProvider, this);
        Navigator.NavigationProvider = navigation;
    }

    internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this) && navigation.OwnsProvider;

    internal void Request(Vector3? point, float arrivalTolerance)
    {
        acquiring |= point.HasValue && !destination.HasValue;
        destination = point;
        tolerance = arrivalTolerance;
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
        if (!escaping && !MovementManager.GenerallyOutOfControl && !safeOrdinaryWaypoint(location))
        {
            BlockOrdinary(location);
            return;
        }
        if (ordinaryBlocked)
            Logger.Information(escaping ? "[Vault] Native escape superseded the held ordinary waypoint via " + location + "." :
                "[Vault] Ordinary movement resumed via " + location + ".");
        ordinaryBlocked = false;
        ordinaryWaypoint = escaping ? null : location;
        escapeOutstanding = escaping;
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
        if (!IsCurrent || MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        navigation.Synchronize();
        // A moving hazard can invalidate a waypoint after admission. Check the
        // held segment again without stopping a newer native escape.
        if (ordinaryWaypoint.HasValue && !safeOrdinaryWaypoint(ordinaryWaypoint.Value))
            BlockOrdinary(ordinaryWaypoint.Value);
        if (AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location)))
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
                ordinaryWaypoint = null;
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
                bool releasedEscape = escapeOutstanding;
                StopInput();
                if (releasedEscape) Logger.Information("[Vault] Released completed native escape input at " + Core.Me.Location + ".");
            }
        }
    }

    private void StopInput()
    {
        if (!IsCurrent)
        {
            return;
        }

        mechanicOutstanding = escapeOutstanding = false;
        ordinaryWaypoint = null;
        inner.MoveStop();
    }

    private void BlockOrdinary(Vector3 waypoint)
    {
        if (!ordinaryBlocked)
        {
            StopInput();
            Logger.Information("[Vault] Held ordinary movement from " + Core.Me.Location + " toward unsafe waypoint " + waypoint + ".");
        }
        ordinaryBlocked = true;
    }

    internal void Detach(bool releaseInput = true)
    {
        if (!detached && ReferenceEquals(Navigator.PlayerMover, this))
        {
            // If only the provider was replaced, unwrap our mover without stopping
            // input now owned by that successor. Requiring both identities here
            // leaves a detached mover installed and blocks all subsequent travel.
            if (releaseInput && IsCurrent && (mechanicOutstanding || escapeOutstanding) && !MovementManager.GenerallyOutOfControl)
            {
                StopInput();
            }

            Navigator.PlayerMover = inner;
        }

        navigation.Detach();
        detached = true;
        destination = null;
        acquiring = mechanicOutstanding = escapeOutstanding = false;
        ordinaryWaypoint = null;
        ordinaryBlocked = false;
    }

    // Service navigation's anti-stuck work can jump/turn without calling IPlayerMover.
    // Admit paths and background pulses through this same owner, clearing the previous
    // route at each handoff. Blocking only the mover leaves a live competing controller.
    private sealed class NavigationGate : WrappingNavigationProvider
    {
        private readonly VaultInputOwner owner;
        private bool detached;
        private string accepted;
        private long generation;
        internal NavigationGate(NavigationProvider original, VaultInputOwner owner) : base(original) => this.owner = owner;
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
            // An intentional hazard hold must not be treated as an unstuck
            // failure that triggers jump/turn input outside the mover channel.
            if (owner.ordinaryBlocked && !AvoidanceManager.IsRunningOutOfAvoid)
                Original.ClearStuckInfo();
            return Original.MoveTo(parameters);
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
