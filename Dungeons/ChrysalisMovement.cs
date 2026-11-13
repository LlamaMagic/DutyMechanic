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
    private readonly Func<Vector3, Vector3?> validateNativeWaypoint;
    private readonly bool gateUnsafeNativeRecovery;
    private readonly Func<bool> prioritizeRequestedHazardExit;
    private readonly bool fenceNativeNavigationWhileOwned;
    private Vector3? ordinaryDestination;
    private bool ordinaryBlocked;
    // An unsafe final request may still have a valid detour. Once its service
    // child is fenced before Unstick, this owner must finish that admitted leg
    // even if the caller stops asking for navigation while the routine acts.
    private bool ordinaryArrivalOwned;
    private Vector3 ordinaryLegStart;
    // Malikah's captured hazard holds entered service recovery despite denied
    // waypoints. Opt in to retiring that coroutine, not merely its stuck history.
    // Other encounters keep their existing movement and cancellation behavior.
    private readonly bool retireBlockedOrdinaryNavigation;
    private bool detachDeferred;
    private bool ForcedInput => MovementManager.MovementLocked || MovementManager.ForceMoved;
    private Vector3? destination;
    private float tolerance;
    private bool detached;
    private bool mechanicOutstanding;
    private bool escapeOutstanding;
    private bool acquiring;
    private bool leavingHazard;
    private bool directEscapeOutstanding;
    private Vector3? directEscapeDestination;
    private string navigationRequest;
    private bool forwardingNavigation;
    private bool deferredNavigationEnd;
    internal ChrysalisMovement(IPlayerMover inner,
        Func<Vector3, Vector3?> failedEscapeWaypoint = null, Func<bool> canContinueNativeEscape = null,
        Func<Vector3, bool> canMoveOrdinary = null, Func<Vector3, Vector3?> validateNativeWaypoint = null,
        bool gateUnsafeNativeRecovery = false, Func<bool> prioritizeRequestedHazardExit = null,
        bool fenceNativeNavigationWhileOwned = false, bool retireBlockedOrdinaryNavigation = false)
    {
        this.inner = inner;
        this.retireBlockedOrdinaryNavigation = retireBlockedOrdinaryNavigation;
        this.failedEscapeWaypoint = failedEscapeWaypoint;
        this.canContinueNativeEscape = canContinueNativeEscape;
        this.canMoveOrdinary = canMoveOrdinary;
        this.validateNativeWaypoint = validateNativeWaypoint;
        this.gateUnsafeNativeRecovery = gateUnsafeNativeRecovery;
        this.prioritizeRequestedHazardExit = prioritizeRequestedHazardExit;
        this.fenceNativeNavigationWhileOwned = fenceNativeNavigationWhileOwned;
        navigation = new NavigationGate(Navigator.NavigationProvider, this);
        Navigator.NavigationProvider = navigation;
    }

    // Recollection can explicitly own a validated combined escape even while
    // native avoidance reports an overlapping unrelated shape. The default is
    // false so other encounters retain their established native handoff.
    private bool EncounterEscapeOwnsInput => destination.HasValue && leavingHazard &&
        prioritizeRequestedHazardExit?.Invoke() == true;

    internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this) && navigation.OwnsProvider;

    // Native escape owns scheduling even when service navigation finds a path.
    // Target acquisition can otherwise stop that path every tick. Input may already
    // have been stopped when queried, so use the planner's lifetime, not held keys.
    internal bool IsEscaping => IsCurrent &&
        AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;

    // Interactions need a mesh route rather than an unvalidated straight crossing.
    // The same owner gates competing travel and gives native escape precedence.
    internal void BeginNavigation(string name)
    {
        if (!IsCurrent || string.IsNullOrWhiteSpace(name) || navigationRequest == name ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
        if (deferredNavigationEnd)
        {
            navigation.AbandonDeferredNavigation();
            deferredNavigationEnd = false;
        }
        // Path generation can take multiple ticks; do not leave the predecessor's
        // forward input held while the new route is being computed.
        StopInput();
        navigationRequest = name;
        navigation.BeginSemanticNavigation();
    }

    internal MoveResult Navigate(Vector3 point, float arrivalTolerance)
    {
        if (!IsCurrent || navigationRequest == null || AvoidanceManager.IsRunningOutOfAvoid ||
            MovementManager.GenerallyOutOfControl) return MoveResult.PathGenerating;
        return navigation.MoveTo(new MoveToParameters(point, navigationRequest)
        {
            DistanceTolerance = arrivalTolerance,
            UseMount = false
        });
    }

    internal void EndNavigation()
    {
        if (navigationRequest == null) return;
        navigationRequest = null;
        if (MovementManager.GenerallyOutOfControl)
        {
            // Forced motion owns the input channel. Retire our path only after
            // control returns, unless an escape or successor has already taken it.
            deferredNavigationEnd = true;
            return;
        }
        navigation.EndSemanticNavigation();
    }

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
        if (ReleaseDeferredForeignProvider()) return;
        if (EncounterEscapeOwnsInput) return;
        if (!IsCurrent || deferredNavigationEnd && MovementManager.GenerallyOutOfControl ||
            (destination.HasValue || navigationRequest != null && !forwardingNavigation) && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        if (retireBlockedOrdinaryNavigation && !navigation.InProviderCall)
        {
            if (detachDeferred && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                CompleteDeferredDetach();
                return;
            }
            navigation.PrepareUpdate();
            navigation.RetireGuardedOrdinaryBeforeSuccessor();
        }

        if (deferredNavigationEnd && !MovementManager.GenerallyOutOfControl)
        {
            // A new input supersedes the old cannon route; clear only that old
            // path before forwarding the successor, never stop the successor later.
            navigation.AbandonDeferredNavigation();
            deferredNavigationEnd = false;
        }

        bool escaping = AvoidanceManager.IsRunningOutOfAvoid;
        if (escaping && !MovementManager.GenerallyOutOfControl && validateNativeWaypoint != null)
        {
            // Recollection's November 5 native path crossed the center bleed and
            // an active petal. Only its opted-in guard may redirect that waypoint;
            // callers without the guard retain the original native behavior.
            Vector3? safeWaypoint = validateNativeWaypoint(location);
            if (!safeWaypoint.HasValue)
            {
                // A rejected native waypoint must stop the currently held input even
                // if its bookkeeping was cleared during the preceding handoff. The
                // November 5 Recollection capture continued through the arena wall.
                StopInput();
                return;
            }
            location = safeWaypoint.Value;
        }
        // Unmaking's combat approach repeatedly re-entered a Vacuum immediately
        // after escape. Validate the actual next waypoint, not the final combat
        // destination: navigation may have a safe detour. Native escape is exempt.
        if (!escaping && (retireBlockedOrdinaryNavigation || !MovementManager.GenerallyOutOfControl) && canMoveOrdinary?.Invoke(location) == false)
        {
            BlockOrdinary();
            return;
        }
        // Occupied/animation state is not proof of safe ordinary input. Actual
        // forced motion retains priority; a queued predecessor is retired before
        // any new ordinary command can become the successor it might stop.
        if (retireBlockedOrdinaryNavigation && (ForcedInput || navigation.HasPendingOrdinary && !escaping)) return;
        bool guardedRecovery = !escaping && navigation.GuardsUnsafeOrdinaryRecovery;
        if (guardedRecovery && Core.Me.Location.Distance2D(location) <= .4f)
        {
            if (!ordinaryBlocked) StopInput();
            ordinaryBlocked = true;
            navigation.StopUnsafeOrdinaryRecovery(location);
            return;
        }
        ordinaryBlocked = false;
        ordinaryArrivalOwned = guardedRecovery;
        if (guardedRecovery) ordinaryLegStart = Core.Me.Location;
        ordinaryDestination = !escaping && canMoveOrdinary != null ? location : null;
        escapeOutstanding = escaping;
        directEscapeOutstanding = false; // A newer native/ordinary command owns cleanup now.
        directEscapeDestination = null;
        mechanicOutstanding = false;
        inner.MoveTowards(location);
        // Keep the valid detour input, but do not let the same child immediately
        // bypass this mover with a jump/turn/forward recovery behind unsafe intent.
        if (guardedRecovery) navigation.StopUnsafeOrdinaryRecovery(location);
    }

    /// <summary>Accepts a stop without letting a routine callback cancel semantic positioning.</summary>
    public void MoveStop()
    {
        if (ReleaseDeferredForeignProvider()) return;
        if (EncounterEscapeOwnsInput) return;
        if (!IsCurrent || deferredNavigationEnd && MovementManager.GenerallyOutOfControl ||
            (destination.HasValue || navigationRequest != null && !forwardingNavigation) && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        StopInput();
    }

    internal void Update()
    {
        if (ReleaseDeferredForeignProvider()) return;
        if (retireBlockedOrdinaryNavigation && IsCurrent)
        {
            if (detachDeferred) { CompleteDeferredDetach(); return; }
            if (ordinaryArrivalOwned && !ForcedInput)
            {
                if (destination.HasValue || navigationRequest != null) ordinaryArrivalOwned = false;
                else if (AvoidanceManager.IsRunningOutOfAvoid || OrdinaryLegFinished()) StopInput();
            }
            if (!destination.HasValue && navigationRequest == null && !AvoidanceManager.IsRunningOutOfAvoid &&
                ordinaryDestination.HasValue && canMoveOrdinary?.Invoke(ordinaryDestination.Value) == false)
                BlockOrdinary();
            // Retire the provider outside Resume even when its nested child
            // already stopped itself at rejection. Occupied state alone must not
            // defer this cleanup; real forced-input flags still do.
            navigation.PrepareUpdate();
        }
        if (!IsCurrent || MovementManager.GenerallyOutOfControl)
        {
            return;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid && !EncounterEscapeOwnsInput)
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

        if (deferredNavigationEnd)
        {
            navigation.EndSemanticNavigation();
            deferredNavigationEnd = false;
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
                ordinaryArrivalOwned = false;
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

    private bool OrdinaryLegFinished()
    {
        if (!ordinaryDestination.HasValue) return true;
        var point = Core.Me.Location;
        var end = ordinaryDestination.Value;
        // Match the existing owner's0.4y arrival window. A passed endpoint plane
        // also catches an update that stepped over the whole arrival circle.
        if (point.Distance2D(end) <= .4f) return true;
        double dx = end.X - ordinaryLegStart.X, dz = end.Z - ordinaryLegStart.Z;
        double lengthSquared = dx * dx + dz * dz;
        if (lengthSquared < .0001) return true;
        double projection = ((point.X - ordinaryLegStart.X) * dx + (point.Z - ordinaryLegStart.Z) * dz) / lengthSquared;
        if (projection >= 1) return true;
        double t = Math.Clamp(projection, 0, 1);
        var nearest = new Vector3(ordinaryLegStart.X + (float)(dx * t), point.Y, ordinaryLegStart.Z + (float)(dz * t));
        // The admitted straight segment does not authorize drifting off it.
        // Release only its still-owned input beyond the default half-yalm margin.
        return point.Distance2D(nearest) > .5f;
    }

    private void StopInput()
    {
        if (!IsCurrent)
        {
            return;
        }

        ordinaryArrivalOwned = false;
        mechanicOutstanding = escapeOutstanding = directEscapeOutstanding = false;
        ordinaryDestination = null;
        directEscapeDestination = null;
        inner.MoveStop();
    }

    private void BlockOrdinary()
    {
        if (retireBlockedOrdinaryNavigation) navigation.RecordBlockedOrdinary();
        if (!ordinaryBlocked && (!retireBlockedOrdinaryNavigation || !ForcedInput))
        {
            StopInput();
        }
        ordinaryBlocked = true;
        if (retireBlockedOrdinaryNavigation) navigation.StopRejectedServiceCoroutine();
    }

    // Replan an ordinary route without taking over its destination. Releasing
    // only our input while no escape, mechanic or hazard owns the channel lets
    // the original provider rebuild its own route on the next caller pulse.
    internal bool RefreshOrdinaryPath()
    {
        if (!IsCurrent || destination.HasValue || navigationRequest != null || deferredNavigationEnd ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore)) return false;
        if (!navigation.RefreshOrdinaryPath()) return false;
        if (ordinaryDestination.HasValue) StopInput();
        return true;
    }

    internal void Detach(bool preserveCurrentInput = false)
    {
        if (retireBlockedOrdinaryNavigation && IsCurrent)
        {
            if (ForcedInput || AvoidanceManager.IsRunningOutOfAvoid || navigation.InProviderCall)
            {
                // Keep the existing provider fence until cancellation is safe.
                // Restoring Original now could resume its old jump/wall-follow
                // after forced motion. The next navigation pulse completes it.
                detachDeferred = true;
                destination = null;
                acquiring = false;
                return;
            }
            navigation.PrepareUpdate();
            navigation.RetireGuardedOrdinaryBeforeSuccessor();
        }

        if (!detached && ReferenceEquals(Navigator.PlayerMover, this))
        {
            // If only the provider was replaced, unwrap our mover without stopping
            // input now owned by that successor. Requiring both identities here
            // leaves a detached mover installed and blocks all subsequent travel.
            if (IsCurrent) navigation.CancelSemanticPathOnDetach();
            // Doma can end its lifecycle while forced motion or native escape
            // owns the channel. Its opt-in handoff unwraps without cancelling
            // that successor; all existing callers retain the normal cleanup.
            if (IsCurrent && !preserveCurrentInput && (mechanicOutstanding || escapeOutstanding || ordinaryArrivalOwned))
            {
                StopInput();
            }

            Navigator.PlayerMover = inner;
        }

        navigation.Detach();
        detached = true;
        navigationRequest = null;
        deferredNavigationEnd = false;
        destination = null;
        acquiring = mechanicOutstanding = escapeOutstanding = false;
        leavingHazard = false;
        directEscapeOutstanding = false;
        directEscapeDestination = null;
        ordinaryDestination = null;
        ordinaryArrivalOwned = false;
        ordinaryBlocked = false;
    }

    private void CompleteDeferredDetach()
    {
        if (ReleaseDeferredForeignProvider()) return;
        if (!detachDeferred || !IsCurrent || ForcedInput || AvoidanceManager.IsRunningOutOfAvoid || navigation.InProviderCall) return;
        navigation.PrepareUpdate();
        detachDeferred = false;
        Detach();
    }

    private bool ReleaseDeferredForeignProvider()
    {
        if (!retireBlockedOrdinaryNavigation || !detachDeferred || navigation.OwnsProvider ||
            !ReferenceEquals(Navigator.PlayerMover, this)) return false;
        // The successor already owns navigation/input. Unwrap only our stale
        // mover; clearing its provider or replaying this callback could stop or
        // steer the successor. Its next request goes directly to the inner mover.
        Navigator.PlayerMover = inner;
        navigation.Detach();
        detachDeferred = false;
        detached = true;
        destination = ordinaryDestination = directEscapeDestination = null;
        navigationRequest = null;
        acquiring = mechanicOutstanding = escapeOutstanding = directEscapeOutstanding = false;
        ordinaryArrivalOwned = false;
        return true;
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
        private long? blockedOrdinaryGeneration;
        private bool inProviderCall;
        private bool inServiceMove;
        private Coroutine providerCaller;
        private bool unsafeOrdinaryRequest;
        private long unsafeRequestGeneration;
        private Vector3 unsafeRequestGoal;
        private bool reportedUnsafeRecovery;
        private bool ordinaryServiceUsed;
        private bool previousUnsafeOrdinary;
        private long previousOrdinaryGeneration;
        private long? protectedOrdinaryGeneration;

        internal void RetireGuardedOrdinaryBeforeSuccessor()
        {
            if (!protectedOrdinaryGeneration.HasValue) return;
            if (!owner.IsCurrent || protectedOrdinaryGeneration != generation)
            {
                protectedOrdinaryGeneration = null;
                return;
            }
            if (inProviderCall || owner.ForcedInput) return;
            // Near-waypoint progress can yield in the provider's final settle
            // loop. Retire that predecessor before a direct successor or safe
            // detach: its later StopMoving must not cancel replacement input.
            Original.Clear();
            Original.ClearStuckInfo();
            protectedOrdinaryGeneration = null;
            generation++;
            owner.StopInput();
        }

        private Coroutine OwnedServiceChild
        {
            get
            {
                Coroutine current = Coroutine.Current;
                return owner.IsCurrent && inServiceMove && !owner.ForcedInput &&
                    !AvoidanceManager.IsRunningOutOfAvoid && current != null && !ReferenceEquals(current, providerCaller)
                    ? current : null;
            }
        }
        internal bool GuardsUnsafeOrdinaryRecovery => unsafeOrdinaryRequest && unsafeRequestGeneration == generation && OwnedServiceChild != null;

        internal void StopUnsafeOrdinaryRecovery(Vector3 waypoint)
        {
            Coroutine child = OwnedServiceChild;
            if (!unsafeOrdinaryRequest || unsafeRequestGeneration != generation || child == null) return;
            protectedOrdinaryGeneration = generation;
            // The inspected mapper skips Unstick below one yalm in3D. Let that
            // callback finish so corridor completion/advancement is not starved.
            // Horizontal proximity alone is insufficient during vertical motion.
            if (Core.Me.Location.DistanceSqr(waypoint) < 1f) return;
            if (!reportedUnsafeRecovery)
            {
                reportedUnsafeRecovery = true;
                DutyMechanic.Logging.Logger.Information($"[MovementOwner] Guarded ordinary recovery goal={unsafeRequestGoal} waypoint={waypoint} player={Core.Me.Location} generation={generation}.");
            }
            // The safe leg remains admitted. Do not queue provider.Clear or a
            // delayed stop: its input now has an owner-managed arrival lifetime.
            child.Dispose();
        }
        private bool reportedOrdinaryRetirement;
        private bool reportedServiceStop;
        internal bool InProviderCall => inProviderCall;
        internal bool HasPendingOrdinary => blockedOrdinaryGeneration == generation;
        internal void RecordBlockedOrdinary()
        {
            if (!owner.ordinaryBlocked) reportedOrdinaryRetirement = reportedServiceStop = false;
            blockedOrdinaryGeneration = generation;
            // Detector history is separate from the progress-starvation pool.
            // Clear it now, then unwind only the known nested service coroutine;
            // clearing the provider itself must wait until its Resume returns.
            Original.ClearStuckInfo();
        }

        internal void StopRejectedServiceCoroutine()
        {
            // RB's service mapper calls the mover before Unstick, which can issue
            // input directly. A next-pulse Clear is too late: the captured Wellbore
            // hold moved inward before retirement. Public self-disposal unwinds
            // this child before recovery starts, without disposing its caller or
            // the provider while Resume is active. Never catch its stop exception.
            Coroutine current = OwnedServiceChild;
            if (current == null) return;
            if (!reportedServiceStop)
            {
                reportedServiceStop = true;
                DutyMechanic.Logging.Logger.Information($"[MovementOwner] Stopped rejected service child at {Core.Me.Location} generation={generation}.");
            }
            current.Dispose();
        }

        internal void PrepareUpdate()
        {
            if (!owner.retireBlockedOrdinaryNavigation || !owner.IsCurrent || inProviderCall) return;
            if (blockedOrdinaryGeneration.HasValue && blockedOrdinaryGeneration != generation) blockedOrdinaryGeneration = null;
            // Clear disposes recovery; some finally blocks stop input directly.
            // Therefore defer cancellation itself, not just our later stop.
            if (owner.ForcedInput || AvoidanceManager.IsRunningOutOfAvoid) return;
            if (owner.destination.HasValue)
            {
                bool acquiringInput = accepted != "mechanic";
                Handoff("mechanic");
                blockedOrdinaryGeneration = null;
                if (acquiringInput && owner.IsCurrent && !owner.ForcedInput && !AvoidanceManager.IsRunningOutOfAvoid)
                {
                    owner.StopInput();
                    owner.acquiring = false;
                }
                return;
            }
            // Direct native input clears ordinaryBlocked without replacing the
            // service iterator. Its pending generation still needs retirement
            // after escape ends, before an ordinary successor is admitted.
            if (owner.navigationRequest != null || owner.deferredNavigationEnd || !HasPendingOrdinary) return;
            blockedOrdinaryGeneration = null;
            Original.Clear();
            Original.ClearStuckInfo();
            generation++;
            if (!reportedOrdinaryRetirement)
            {
                // One record per continuous hold verifies the real cancellation
                // without logging every cached-path retry on subsequent pulses.
                reportedOrdinaryRetirement = true;
                DutyMechanic.Logging.Logger.Information($"[MovementOwner] Retired unsafe ordinary navigation at {Core.Me.Location} generation={generation}.");
            }
            // JumpProbe has no input finally; release its held forward key once
            // through this owner, before permitting a new ordinary successor.
            if (owner.IsCurrent && !owner.ForcedInput && !AvoidanceManager.IsRunningOutOfAvoid &&
                !owner.destination.HasValue && owner.navigationRequest == null)
                owner.StopInput();
        }
        internal NavigationGate(NavigationProvider original, ChrysalisMovement owner) : base(original) => this.owner = owner;
        internal bool OwnsProvider => !detached && ReferenceEquals(Navigator.NavigationProvider, this);

        private bool Allowed(MoveToParameters request) => !(owner.fenceNativeNavigationWhileOwned && owner.EncounterEscapeOwnsInput &&
            (!MovementManager.GenerallyOutOfControl || Core.Me != null && Core.Me.IsValid && Core.Me.IsCasting && owner.destination.HasValue &&
             Core.Me.Location.Distance2D(owner.destination.Value) <= Math.Max(0.3f, owner.tolerance))) &&
            !(owner.deferredNavigationEnd && MovementManager.GenerallyOutOfControl) && (AvoidanceManager.IsRunningOutOfAvoid
            ? request.Destination?.StartsWith("Running from ", StringComparison.Ordinal) == true
            : !owner.destination.HasValue && (owner.navigationRequest == null || request.Destination == owner.navigationRequest));

        internal bool RefreshOrdinaryPath()
        {
            if (!owner.IsCurrent || accepted != null) return false;
            // Clear only the current service path and its stuck state; callers
            // continue to supply the same route target and own retry decisions.
            Original.Clear();
            Original.ClearStuckInfo();
            generation++;
            return true;
        }

        internal void BeginSemanticNavigation()
        {
            if (owner.IsCurrent && !AvoidanceManager.IsRunningOutOfAvoid) Handoff("semantic-navigation");
        }

        internal void EndSemanticNavigation()
        {
            if (owner.IsCurrent && !AvoidanceManager.IsRunningOutOfAvoid && accepted == "semantic-navigation")
            {
                Handoff(null);
                owner.StopInput();
            }
        }
        internal void AbandonDeferredNavigation()
        {
            if (owner.IsCurrent && !AvoidanceManager.IsRunningOutOfAvoid && accepted == "semantic-navigation") Handoff(null);
        }
        internal void CancelSemanticPathOnDetach()
        {
            // Retire only this route before restoring the provider. Clearing a
            // path does not issue input, so forced motion retains its priority.
            if (owner.IsCurrent && !AvoidanceManager.IsRunningOutOfAvoid && accepted == "semantic-navigation") Handoff(null);
        }
        internal void Synchronize()
        {
            if (owner.IsCurrent && owner.fenceNativeNavigationWhileOwned && owner.EncounterEscapeOwnsInput &&
                !MovementManager.GenerallyOutOfControl)
            {
                // A native escape's service path can run anti-stuck input
                // independently of IPlayerMover. Holminster's verified timed
                // hold must retire that predecessor before owning the channel.
                Handoff("mechanic");
                return;
            }
            if (owner.IsCurrent && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Handoff(owner.destination.HasValue ? "mechanic" : owner.navigationRequest != null ? "semantic-navigation" : null);
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

            if (owner.retireBlockedOrdinaryNavigation)
            {
                if (owner.ForcedInput || owner.detachDeferred && !AvoidanceManager.IsRunningOutOfAvoid)
                    return MoveResult.PathGenerating;
                PrepareUpdate();
            }
            if (!Allowed(parameters))
            {
                return MoveResult.PathGenerating;
            }

            Handoff(AvoidanceManager.IsRunningOutOfAvoid ? "escape" : owner.navigationRequest != null ? "semantic-navigation" : null);
            // In Recollection the service's anti-stuck wall-follow bypassed the
            // mover guard and walked through the center after every path was denied.
            // Opted-in encounters hold and clear that recovery until a validated
            // segment exists; forced movement and all other encounters retain control.
            if (owner.gateUnsafeNativeRecovery && AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl &&
                owner.failedEscapeWaypoint?.Invoke(parameters.Location) == null)
            {
                Original.Clear();
                Original.ClearStuckInfo();
                owner.StopInput();
                return MoveResult.PathGenerating;
            }
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
            MoveResult result;
            owner.forwardingNavigation = owner.navigationRequest != null && !AvoidanceManager.IsRunningOutOfAvoid;
            inProviderCall = true;
            // Bind cancellation to this exact built-in pipeline. Other providers
            // and a callback running on the caller itself keep deferred cleanup.
            inServiceMove = Original.GetType() == typeof(ServiceNavigationProvider);
            providerCaller = Coroutine.Current;
            try
            {
                // Intent is not a path veto. Forward safe actual waypoints, but
                // forbid opaque recovery toward an unsafe final goal/straight crossing.
                // Refresh and generation-bind it for this invocation only; expiry and
                // safe successors immediately regain ordinary obstacle recovery.
                unsafeOrdinaryRequest = owner.retireBlockedOrdinaryNavigation && owner.navigationRequest == null &&
                    !AvoidanceManager.IsRunningOutOfAvoid && owner.canMoveOrdinary?.Invoke(parameters.Location) == false;
                if (owner.retireBlockedOrdinaryNavigation && inServiceMove && owner.navigationRequest == null &&
                    !owner.ForcedInput && !AvoidanceManager.IsRunningOutOfAvoid)
                {
                    // A formerly safe request may already be suspended inside
                    // Unstick, before another mover callback can guard it. Retire
                    // that predecessor once, before Original.MoveTo resumes it.
                    // Native/semantic handoffs already changed the generation.
                    if (unsafeOrdinaryRequest && ordinaryServiceUsed && previousOrdinaryGeneration == generation && !previousUnsafeOrdinary)
                    {
                        Original.Clear();
                        Original.ClearStuckInfo();
                        generation++;
                        owner.StopInput();
                    }
                    ordinaryServiceUsed = true;
                    previousOrdinaryGeneration = generation;
                    previousUnsafeOrdinary = unsafeOrdinaryRequest;
                }
                unsafeRequestGeneration = generation;
                unsafeRequestGoal = parameters.Location;
                if (!unsafeOrdinaryRequest) reportedUnsafeRecovery = false;
                result = Original.MoveTo(parameters);
            }
            finally
            {
                unsafeOrdinaryRequest = false;
                inServiceMove = false;
                providerCaller = null;
                inProviderCall = false;
                owner.forwardingNavigation = false;
            }
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
            if (owner.detachDeferred && owner.IsCurrent)
            {
                owner.CompleteDeferredDetach();
                return;
            }
            if (OwnsProvider && !ReferenceEquals(Navigator.PlayerMover, owner))
            {
                Detach();
                return;
            }

            if (owner.IsCurrent && !owner.destination.HasValue && !owner.ordinaryBlocked && !AvoidanceManager.IsRunningOutOfAvoid && !owner.deferredNavigationEnd &&
                (owner.navigationRequest == null || accepted == "semantic-navigation"))
            {
                owner.forwardingNavigation = owner.navigationRequest != null && accepted == "semantic-navigation";
                inProviderCall = true;
                try { Original.OnPulse(); }
                finally { inProviderCall = false; owner.forwardingNavigation = false; }
            }
        }

        /// <inheritdoc/>
        public override bool Clear() => owner.IsCurrent && !owner.destination.HasValue && owner.navigationRequest == null && !owner.deferredNavigationEnd && !AvoidanceManager.IsRunningOutOfAvoid && Original.Clear();
        /// <inheritdoc/>
        public override void ClearStuckInfo()
        {
            if (owner.IsCurrent && !owner.destination.HasValue && owner.navigationRequest == null && !owner.deferredNavigationEnd && !AvoidanceManager.IsRunningOutOfAvoid)
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
            protectedOrdinaryGeneration = null;
            if (OwnsProvider)
            {
                Navigator.NavigationProvider = Original;
            }

            detached = true;
            generation++;
        }
    }
}
