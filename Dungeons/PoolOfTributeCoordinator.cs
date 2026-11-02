using System;
using System.Linq;
using System.Threading.Tasks;
using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using ff14bot.Pathing.Service_Navigation;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns normal Pool of Tribute's blade interaction and sword-finish positioning.
/// The existing profile and AutoParty adapters may call this on the bot thread.
/// </summary>
public static class PoolOfTributeCoordinator
{
    private const uint Territory = 674, Shadow = 2008185, SwordFinish = 8588, Seasplitter = 8232;
    // The circular arena is 20 yalms in radius. Never pursue an interaction whose
    // object origin is outside the floor: that cannot be a safe lotus destination.
    private const float ArenaRadius = 19.5f;
    private static InputOwner owner;
    private static bool initialized, ownsOverride;
    private static DateTime swordUntil, bandUntil, interactAfter, progressAt, nextErrorLog;
    private static Vector3 swordDestination, bandDestination, lastProgress, blockedAt;
    private static uint shadowId, blockedShadowId;

    internal static string InputSnapshot => owner?.Snapshot ?? "unavailable";

    /// <summary>Advances encounter movement once on the existing bot-thread scheduler.</summary>
    /// <returns>True while the encounter must preempt ordinary profile movement.</returns>
    public static bool Tick()
    {
        try { return TickCore(); }
        catch (Exception ex)
        {
            // Transient actor or navigation failures release only this encounter's
            // resources and leave combat schedulable; the next tick may reacquire.
            try { Reset(); } catch { }
            if (DateTime.UtcNow >= nextErrorLog)
            {
                nextErrorLog = DateTime.UtcNow.AddSeconds(30);
                Logger.Information("[PoolOfTribute] Coordinator unavailable: " + ex.GetType().Name);
            }
            return false;
        }
    }

    private static bool TickCore()
    {
        if (Core.Me == null || !TreeRoot.IsRunning || !DutyManager.InInstance || WorldManager.ZoneId != Territory ||
            !Core.Me.IsAlive || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            Reset();
            return false;
        }
        if (!initialized)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover)) return false;
            owner = new InputOwner(Navigator.PlayerMover);
            Navigator.PlayerMover = owner;
            ownsOverride = LlamaLibrary.Helpers.SideStep.Override(SwordFinish);
            TreeRoot.OnStop += OnStop;
            initialized = true;
        }
        if (!owner.IsCurrent || !owner.NavigationCurrent) { owner.Abandon(); Reset(); return false; }
        owner.CompleteEscape();
        owner.CompleteDeferredOwned();
        owner.CompleteAbandonedTravel();
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            owner.YieldToEscape();
            lastProgress = Core.Me.Location;
            progressAt = DateTime.UtcNow;
            return true;
        }
        if (MovementManager.GenerallyOutOfControl) { owner.ReleaseForForcedMotion(); return false; }
        // A newly registered hazard can contain the player before native escape
        // raises its running flag. Do not acquire semantic movement in that gap.
        if (AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location)))
        {
            // Hold ordinary navigation while leaving the routine schedulable;
            // the gate opens as soon as native avoidance starts its escape.
            owner.Acquire();
            owner.Brake();
            lastProgress = Core.Me.Location;
            progressAt = DateTime.UtcNow;
            return false;
        }

        DateTime now = DateTime.UtcNow;
        var casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == SwordFinish).ToArray();
        if (casters.Length == 1)
        {
            if (now >= swordUntil)
            {
                float heading = casters[0].Heading;
                float fx = (float)Math.Sin(heading), fz = (float)Math.Cos(heading);
                var p = Core.Me.Location;
                // The observed line passes through (0,0); six yalms lateral and
                // +/-12 along it remain inside the arena with room for arrival error.
                float along = Math.Clamp(p.X * fx + p.Z * fz, -12f, 12f);
                float side = p.X * fz - p.Z * fx < 0 ? -6f : 6f;
                swordDestination = new Vector3(fx * along + fz * side, p.Y, fz * along - fx * side);
            }
            // The captured finish damages after cast removal; hold through that impact.
            swordUntil = now + casters[0].SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }
        if (now < swordUntil)
        {
            shadowId = 0;
            owner.Acquire();
            if (Core.Me.Location.Distance2D(swordDestination) > 0.6f) owner.Command(swordDestination);
            else owner.Brake();
            return true;
        }

        if (ff14bot.RemoteWindows.QTE.IsOpen)
        {
            owner.Release();
            shadowId = 0;
            return true;
        }
        var band = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == Seasplitter);
        if (band != null)
        {
            // Opposing rectangles extend along their headings; their shared safe
            // strip is perpendicular to that axis through the helper origin.
            var p = Core.Me.Location;
            float dx = (float)Math.Sin(band.Heading), dz = (float)Math.Cos(band.Heading);
            float along = (p.X - band.Location.X) * dx + (p.Z - band.Location.Z) * dz;
            bandDestination = new Vector3(p.X - along * dx, p.Y, p.Z - along * dz);
            // Two 2026-11-02 captures show the impact up to two seconds after
            // cast completion; retain a one-second margin for delayed resolution.
            bandUntil = now + band.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3);
        }
        if (now < bandUntil)
        {
            owner.Acquire();
            // The safe strip is narrow; a 0.75-yalm arrival tolerance can stop
            // outside it even before ordinary movement resumes.
            if (Core.Me.Location.Distance2D(bandDestination) > 0.25f) { owner.Command(bandDestination); return true; }
            // The navigation gate retains the safe position but leaves healing and
            // rotation schedulable after arrival rather than consuming the tree tick.
            owner.Brake();
            return false;
        }
        var shadows = GameObjectManager.GetObjectsOfType<ff14bot.Objects.GameObject>(true, false)
            .Where(actor => actor.IsValid && actor.IsVisible && actor.IsTargetable && actor.NpcId == Shadow).ToArray();
        var members = PartyManager.VisibleMembers.Where(member => member.IsInObjectManager)
            .Select(member => member.BattleCharacter).Where(member => member != null && member.IsValid)
            .Select(member => new PoolOfTributeClashPolicy.Member(member.ObjectId, member.ClassLevel,
                ClassJobRoles.Tanks.Contains(member.CurrentJob), member.IsAlive)).ToArray();
        var decision = PoolOfTributeClashPolicy.Select(members, (int)PartyManager.NumMembers);
        if (shadows.Length != 1 || decision.BlockerId != Core.Me.ObjectId)
        {
            owner.Release();
            shadowId = 0;
            if (shadows.Length != 1 || shadows[0].ObjectId != blockedShadowId) blockedShadowId = 0;
            return false;
        }
        var shadow = shadows[0];
        if (blockedShadowId == shadow.ObjectId && Core.Me.Location.Distance2D(blockedAt) < 1f) return false;
        blockedShadowId = 0;
        var destination = shadow.Location;
        if (destination.X * destination.X + destination.Z * destination.Z > ArenaRadius * ArenaRadius)
        {
            owner.Release();
            shadowId = 0;
            return false;
        }
        if (shadowId != shadow.ObjectId)
        {
            shadowId = shadow.ObjectId;
            lastProgress = Core.Me.Location;
            progressAt = now;
        }
        owner.Acquire();
        if (Core.Me.Location.Distance2D(destination) > 2.5f)
        {
            if (Core.Me.Location.Distance2D(lastProgress) >= 0.5f)
            {
                lastProgress = Core.Me.Location;
                progressAt = now;
            }
            // Do not repeatedly push into the same wall. Allow normal recovery and
            // retry this object only after the player has moved or the object changes.
            if (now - progressAt > TimeSpan.FromSeconds(3))
            {
                owner.Release();
                blockedShadowId = shadow.ObjectId;
                blockedAt = Core.Me.Location;
                shadowId = 0;
                Logger.Information("[PoolOfTribute] Lotus approach made no progress; releasing input until position changes.");
                return false;
            }
            owner.Command(destination);
            return true;
        }
        owner.Brake();
        if (!MovementManager.IsOccupied && now >= interactAfter)
        {
            interactAfter = now.AddSeconds(2);
            shadow.Interact();
        }
        return true;
    }

    /// <summary>Releases this encounter's own input and SideStep override on exit or stop.</summary>
    public static void Reset()
    {
        owner?.Detach();
        owner = null;
        if (ownsOverride) LlamaLibrary.Helpers.SideStep.RemoveHandler(SwordFinish);
        if (initialized) TreeRoot.OnStop -= OnStop;
        initialized = ownsOverride = false;
        swordUntil = bandUntil = interactAfter = progressAt = DateTime.MinValue;
        shadowId = blockedShadowId = 0;
    }

    /// <summary>Releases an active lease when death or loading bypasses the encounter hook.</summary>
    public static void CheckLifecycle()
    {
        if (!initialized) return;
        if (!owner.IsCurrent || !owner.NavigationCurrent) { owner.Abandon(); Reset(); return; }
        if (!TreeRoot.IsRunning || !DutyManager.InInstance || WorldManager.ZoneId != Territory ||
            CommonBehaviors.IsLoading || Core.Me == null || !Core.Me.IsAlive) { Reset(); return; }
        // Encounter/profile hooks can yield while native avoidance still runs. Its
        // forward input must be released on the plugin pulse even during that gap.
        owner.CompleteEscape();
        owner.CompleteDeferredOwned();
        owner.CompleteAbandonedTravel();
    }

    private static void OnStop(BotBase bot) => Reset();

    // One interposed channel arbitrates native avoidance, semantic requests, and
    // ordinary navigation. A later admitted move revokes an old escape's cleanup
    // claim; stale callbacks cannot stop a successor or restore over another mover.
    private sealed class InputOwner(IPlayerMover inner) : IPlayerMover
    {
        private bool detached, semantic, ownInput, escapeInput, ordinaryInput;
        private NavigationGate navigation;
        private Vector3 lastCommand;
        private DateTime ordinaryCommandAt;
        private float ordinaryClosest;
        private int moveCount, stopCount;
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal bool NavigationCurrent => !semantic || navigation?.IsCurrent == true;
        internal string Snapshot => $"semantic={semantic},owned={ownInput},escapeOwned={escapeInput},ordinaryOwned={ordinaryInput},last={lastCommand},moves={moveCount},stops={stopCount}";

        /// <inheritdoc/>
        public void MoveTowards(Vector3 point)
        {
            if (!IsCurrent) return;
            bool escape = AvoidanceManager.IsRunningOutOfAvoid;
            if (semantic && !escape && !MovementManager.GenerallyOutOfControl && !CommonBehaviors.IsLoading) return;
            escapeInput = escape;
            ownInput = false;
            ordinaryInput = !escape && !MovementManager.GenerallyOutOfControl && !CommonBehaviors.IsLoading;
            if (ordinaryInput)
            {
                ordinaryCommandAt = DateTime.UtcNow;
                ordinaryClosest = Core.Me.Location.Distance2D(point);
            }
            lastCommand = point;
            moveCount++;
            inner.MoveTowards(point);
        }

        /// <inheritdoc/>
        public void MoveStop()
        {
            if (!IsCurrent || (semantic && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl && !CommonBehaviors.IsLoading)) return;
            escapeInput = ownInput = ordinaryInput = false;
            stopCount++;
            inner.MoveStop();
        }

        internal void CompleteEscape()
        {
            if (!IsCurrent || !escapeInput || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location))) return;
            escapeInput = false;
            stopCount++;
            inner.MoveStop();
        }

        internal void CompleteAbandonedTravel()
        {
            if (!IsCurrent || !ordinaryInput || semantic || escapeInput || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl || CommonBehaviors.IsLoading) return;
            float distance = Core.Me.Location.Distance2D(lastCommand);
            ordinaryClosest = Math.Min(ordinaryClosest, distance);
            var target = Core.Me.CurrentTarget;
            if ((target != null && target.IsValid && target.IsTargetable) ||
                DateTime.UtcNow - ordinaryCommandAt < TimeSpan.FromMilliseconds(750) ||
                distance <= ordinaryClosest + 2f ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location))) return;
            // The 2026-11-02 synced capture crossed the arena after its target
            // vanished: the last ordinary waypoint had been left far behind.
            // Stop only that still-current, diverging request, never a successor.
            ordinaryInput = false;
            stopCount++;
            inner.MoveStop();
            Logger.Information($"[PoolOfTribute] Released abandoned travel player={Core.Me.Location} waypoint={lastCommand} closest={ordinaryClosest:F1} distance={distance:F1}");
        }

        internal void Acquire()
        {
            if (!IsCurrent || semantic) return;
            // Cancel the prior provider's path once before borrowing its channel;
            // otherwise restoring it can replay an obsolete approach after the hold.
            Navigator.Stop();
            if (!IsCurrent || Navigator.NavigationProvider == null) return;
            navigation = new NavigationGate(Navigator.NavigationProvider, this);
            Navigator.NavigationProvider = navigation;
            semantic = true;
        }

        internal void Command(Vector3 point)
        {
            if (!IsCurrent || !semantic || AvoidanceManager.IsRunningOutOfAvoid) return;
            if (ownInput && point.Distance2D(lastCommand) < 0.1f) return;
            escapeInput = false;
            ordinaryInput = false;
            ownInput = true;
            lastCommand = point;
            moveCount++;
            inner.MoveTowards(point);
        }

        internal void Brake()
        {
            if (!IsCurrent || !ownInput || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
            ownInput = false;
            stopCount++;
            inner.MoveStop();
        }

        // Retain our input claim until native avoidance actually issues a move or
        // stop through this channel. A raised escape flag alone is not a handoff.
        internal void YieldToEscape() { }
        internal void Abandon() { ownInput = escapeInput = ordinaryInput = false; }

        internal void ReleaseForForcedMotion()
        {
            if (!semantic) return;
            // Keep the owned-input claim, but never issue a stop during forced
            // motion. An admitted successor command revokes it before forwarding.
            semantic = false;
            navigation?.Detach();
            navigation = null;
        }

        internal void CompleteDeferredOwned()
        {
            if (semantic || !IsCurrent || !ownInput || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl) return;
            ownInput = false;
            stopCount++;
            inner.MoveStop();
        }

        internal void Release()
        {
            if (!semantic) return;
            Brake();
            semantic = false;
            navigation?.Detach();
            navigation = null;
        }

        internal void Detach()
        {
            Release();
            if (IsCurrent)
            {
                if ((escapeInput || ownInput) && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
                    inner.MoveStop();
                Navigator.PlayerMover = inner;
            }
            detached = true;
            escapeInput = ownInput = ordinaryInput = false;
        }

        private sealed class NavigationGate(NavigationProvider original, InputOwner owner) : WrappingNavigationProvider(original)
        {
            private bool detached;
            private bool Owns => !detached && ReferenceEquals(Navigator.NavigationProvider, this);
            internal bool IsCurrent => Owns;
            private bool PassThrough => !owner.IsCurrent || !owner.semantic || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || !TreeRoot.IsRunning;
            /// <inheritdoc/>
            public override MoveResult MoveTo(MoveToParameters parameters) => Owns && PassThrough ? Original.MoveTo(parameters) : MoveResult.PathGenerating;
            /// <inheritdoc/>
            public override void OnPulse() { if (Owns && PassThrough) Original.OnPulse(); }
            /// <inheritdoc/>
            public override async Task<MoveResult> MoveToAndStopAsync(MoveToParameters parameters, Func<bool> abortCondition = null, float stopDistance = 3.5f)
            {
                while (Owns && !PassThrough)
                {
                    if (abortCondition?.Invoke() == true) return MoveResult.Done;
                    await Coroutine.Yield();
                }
                if (!Owns) return MoveResult.Done;
                return await Original.MoveToAndStopAsync(parameters, abortCondition, stopDistance);
            }
            internal void Detach()
            {
                if (Owns) Navigator.NavigationProvider = Original;
                detached = true;
            }
        }
    }
}
