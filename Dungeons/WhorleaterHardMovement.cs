using System;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Interfaces;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing;
using ff14bot.Pathing.Service_Navigation;

namespace DutyMechanic.Dungeons;

public sealed partial class WhorleaterHard
{
    // OrderBot can request a Kill Target path during avoidance or converter staging;
    // routine capability flags do not block those requests. Gate both navigation and
    // mover input here, leaving path calculation to the existing provider.
    private sealed class MovementGate : IPlayerMover
    {
        private readonly WhorleaterHard encounter;
        private readonly IPlayerMover inner;
        private readonly NavigationGate navigation;
        private bool detached, forwarding, converterRequest;
        private DateTime nextBlockedLog;

        internal MovementGate(WhorleaterHard encounter)
        {
            this.encounter = encounter;
            inner = Navigator.PlayerMover ?? throw new InvalidOperationException("Whorleater requires an installed mover.");
            navigation = new NavigationGate(Navigator.NavigationProvider, this);
            Navigator.PlayerMover = this;
            Navigator.NavigationProvider = navigation;
        }

        private bool Owns => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        private bool InEncounter => encounter.active && TreeRoot.IsRunning && Core.Player != null
            && Core.Player.IsAlive && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene
            && DutyManager.InInstance && WorldManager.ZoneId == 281;
        // The selected target can be null or different from OrderBot's retained Kill POI.
        // Inspect the request owner too; targetable alone does not establish attackability.
        private bool TargetUnavailable => Poi.Current?.Type == PoiType.Kill
            ? !Ready(Poi.Current.Unit)
            : Core.Player.CurrentTarget is { } target && !Ready(target);
        private static bool Ready(ff14bot.Objects.GameObject target) => target != null && target.IsValid
            && target.IsVisible && target.IsTargetable && target.CanAttack;
        private bool Restricted => InEncounter && (encounter.leased || AvoidanceManager.IsRunningOutOfAvoid || TargetUnavailable);

        private bool Allow(MoveToParameters parameters)
        {
            if (!InEncounter)
                return true;
            // RB labels emergency paths with this prefix. The escape flag alone cannot
            // distinguish them from concurrent OrderBot Kill Target requests.
            bool escape = parameters.Destination?.StartsWith("Running from ", StringComparison.Ordinal) == true;
            bool allowed = WhorleaterHardPolicy.AllowPath(AvoidanceManager.IsRunningOutOfAvoid,
                encounter.leased, converterRequest, escape, TargetUnavailable);
            if (!allowed && DateTime.UtcNow >= nextBlockedLog)
            {
                nextBlockedLog = DateTime.UtcNow.AddSeconds(2);
                Logger.Information($"[Whorleater] Deferred competing path '{parameters.Destination}'; escape={AvoidanceManager.IsRunningOutOfAvoid}; converter={encounter.leased}; unavailableTarget={TargetUnavailable}.");
            }
            return allowed;
        }

        internal MoveResult Approach(Vector3 point, string destination = "Whorleater converter")
        {
            if (!Owns)
                return MoveResult.Failed;
            converterRequest = true;
            try
            {
                // Finish inside the converter's 3y interaction range; the provider's
                // default 5y tolerance can stop the approach too early.
                return navigation.MoveTo(new MoveToParameters(point, destination)
                {
                    DistanceTolerance = 2.5f,
                    UseMount = false
                });
            }
            finally { converterRequest = false; }
        }

        internal void Brake()
        {
            if (Owns && !AvoidanceManager.IsRunningOutOfAvoid)
                inner.MoveStop();
        }

        internal void BeginPositioning()
        {
            if (Owns && !AvoidanceManager.IsRunningOutOfAvoid)
                navigation.CancelOwnedPath();
        }

        internal void Detach()
        {
            // Restore only what this gate still owns. A late callback into this instance
            // cannot stop or unwrap a replacement installed by another lifecycle owner.
            navigation.Detach();
            if (Owns)
                Navigator.PlayerMover = inner;
            detached = true;
        }

        /// <summary>Forwards input from the admitted provider request or unrestricted combat.</summary>
        /// <param name="point">Destination produced by the existing RB navigator.</param>
        public void MoveTowards(Vector3 point)
        {
            if (Owns && (forwarding || navigation.NativeEscapeInput || !Restricted))
                inner.MoveTowards(point);
        }

        /// <summary>Rejects competing stops during a mechanic; explicit user-stop cleanup remains available.</summary>
        public void MoveStop()
        {
            if (Owns && (forwarding || navigation.NativeEscapeInput || !Restricted))
                inner.MoveStop();
        }

        private sealed class NavigationGate : WrappingNavigationProvider
        {
            private readonly MovementGate owner;
            private bool detached;
            private string acceptedOwner;
            private long generation;
            private bool unavailableStopped;
            internal NavigationGate(NavigationProvider original, MovementGate owner) : base(original) { this.owner = owner; }
            private bool Owns => !detached && owner.Owns && ReferenceEquals(Navigator.NavigationProvider, this);
            // Native local-heightfield traversal also writes through IPlayerMover after
            // its admitted provider call. Preserve that path while rejecting kill requests.
            internal bool NativeEscapeInput => Owns && acceptedOwner == "avoidance" && AvoidanceManager.IsRunningOutOfAvoid;

            internal void CancelOwnedPath()
            {
                if (!Owns)
                    return;
                Original.Clear();
                Original.ClearStuckInfo();
                owner.Brake();
                acceptedOwner = null;
                ++generation;
            }

            /// <inheritdoc/>
            public override MoveResult MoveTo(MoveToParameters parameters)
            {
                if (!Owns)
                    return MoveResult.Done;
                if (!owner.Allow(parameters))
                {
                    if (!unavailableStopped && owner.TargetUnavailable && !owner.encounter.leased && !AvoidanceManager.IsRunningOutOfAvoid)
                    {
                        Original.Clear();
                        Original.ClearStuckInfo();
                        owner.Brake();
                        unavailableStopped = true;
                        ++generation;
                    }
                    return MoveResult.PathGenerating;
                }
                unavailableStopped = false;
                string requestOwner = owner.converterRequest ? parameters.Destination
                    : owner.InEncounter && AvoidanceManager.IsRunningOutOfAvoid ? "avoidance" : "combat";
                if (requestOwner != acceptedOwner)
                {
                    // Cancel old provider progress/anti-stuck state once at the handoff.
                    // Keep the native AvoidanceManager heightfield and registered hazards.
                    Original.Clear();
                    Original.ClearStuckInfo();
                    acceptedOwner = requestOwner;
                    ++generation;
                    Logger.Information($"[Whorleater] Movement owner={requestOwner}; destination={parameters.Location}.");
                }
                owner.forwarding = true;
                try
                {
                    return Original.MoveTo(parameters);
                }
                finally { owner.forwarding = false; }
            }

            /// <inheritdoc/>
            public override void OnPulse()
            {
                // Service navigation can perform jump/wall-follow input in its own path
                // progress. Suppress that background work during a restricted handoff.
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
            public override async Task<MoveResult> MoveToAndStopAsync(MoveToParameters parameters,
                Func<bool> abortCondition = null, float stopDistance = 3.5f)
            {
                // Do not delegate a long-lived coroutine to Original: it would bypass
                // admission on later ticks and its final stop could cancel its successor.
                long? requestGeneration = null;
                while (Owns)
                {
                    if (requestGeneration.HasValue && requestGeneration != generation)
                        return MoveResult.Done;
                    if (abortCondition?.Invoke() == true)
                    {
                        if (owner.Allow(parameters))
                            owner.Brake();
                        return MoveResult.Done;
                    }
                    if (owner.Allow(parameters))
                    {
                        if (stopDistance > 0 && Navigator.InPosition(Core.Player.Location, parameters.Location, stopDistance))
                        {
                            owner.Brake();
                            return MoveResult.ReachedDestination;
                        }
                        var result = MoveTo(parameters);
                        requestGeneration = generation;
                        if (result is MoveResult.Done or MoveResult.ReachedDestination or MoveResult.Failed or MoveResult.PathGenerationFailed)
                        {
                            owner.Brake();
                            return result;
                        }
                    }
                    await Coroutine.Yield();
                }
                return MoveResult.Done;
            }

            internal void Detach()
            {
                if (Owns)
                    Navigator.NavigationProvider = Original;
                detached = true;
                ++generation;
            }
        }
    }
}
