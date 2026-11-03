// Scoped semantic movement and diagnostic observation for Alexandria.
using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    private static Alexandria activeStackOwner;
    private StackInputOwner stackInputOwner;
    private bool stackLeased;
    private bool reportedForeignMover;
    private static DateTime quarantineUntil;
    private static readonly AlexandriaInterferonSequence interferonSequence = new();
    private static double? interferonDue;
    private static readonly AlexandriaConeTimeline immuneResponse = new();
    private DateTime superboltUntil;
    private DateTime superboltCastEnd;
    private uint superboltTarget;
    private bool superboltMarkerSeen;
    private uint stackDestinationTarget;
    private string stackDestinationKind;
    private System.Numerics.Vector2? stackDestination;
    private System.Numerics.Vector2 ternaryOrigin;
    private DateTime ternaryUntil;
    private DateTime voltburstUntil;
    // The rolling movement owner uses supported casts only; unresolved party
    // markers block it until their positive constraints can be handled.
    private readonly AlexandriaAmalgamTimeline amalgamTimeline = new();
    private readonly AlexandriaAmalgamMarkers amalgamMarkers = new();
    private sealed record VoltburstCircle(uint Id, Vector3 Origin, DateTime Effect, DateTime Expires);
    private readonly Dictionary<uint, VoltburstCircle> voltburstCircles = [];
    private VoltburstCircle[] ActiveVoltburstCircles()
    {
        var live = voltburstCircles.Values.Where(c => c.Expires > DateTime.UtcNow).ToArray();
        if (live.Length == 0) return [];
        var next = live.Min(c => c.Effect);
        return live.Where(c => c.Effect <= next.AddSeconds(.6)).ToArray();
    }
    private bool AmalgamMatrixReady => DateTime.UtcNow >= voltburstUntil;
    private DateTime lineStackUntil;
    private System.Numerics.Vector2 lineStackSource;
    private bool TernaryFirstCircleActive => DateTime.UtcNow < ternaryUntil;

    // Quarantine's tankbuster marker may outlive its short visual cast.
    // Observe it independently of yielding encounter movement coroutines.
    internal static void ObserveAntivirusOnPulse()
    {
        if (Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
        {
            ResetAntivirusRoute();
            antivirusNodes.Clear();
            interferonSequence.Reset();
            interferonDue = null;
            immuneResponse.Reset();
            return;
        }
        if (EnemyAction.Quarantine.IsCasting())
            quarantineUntil = DateTime.UtcNow.AddSeconds(10);
        double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid).ToArray();
        var nodes = actors.Where(actor => actor.BaseId is 0x4174 or 0x4175).Select(actor =>
        {
            bool casting = actor.CastingSpellId is 36382 or 36383 && actor.SpellCastInfo.IsValid;
            return new AlexandriaInterferonSequence.Observation(actor.ObjectId, actor.IsVisible, casting,
                casting ? actor.SpellCastInfo.RemainingCastTime.TotalSeconds : 0,
                casting ? actor.CastingSpellId : 0, actor.IsVisible && actor.CastingSpellId == 0);
        }).ToArray();
        interferonSequence.Observe(now, nodes);
        interferonDue = interferonSequence.NextDue(now);
        if (interferonDue == null)
        {
            var live = nodes.Where(node => node.Casting).Select(node => now + node.RemainingSeconds).ToArray();
            if (live.Length != 0) interferonDue = live.Min();
        }
        // Preserve every simultaneous cone rather than selecting only the
        // earliest helper. Each stable deadline survives wrapper disappearance.
        var cones = actors.Where(actor => actor.CastingSpellId is 36379 or 36381)
            .Select(actor => new AlexandriaConeTimeline.Observation(actor.ObjectId, actor.CastingSpellId,
                actor.SpellCastInfo.IsValid ? actor.SpellCastInfo.RemainingCastTime.TotalSeconds : double.NaN)).ToArray();
        immuneResponse.Observe(now, cones);
        CaptureAntivirusHazards(now, actors);
        // Combat coroutines can yield across several waypoint deadlines. Pulse
        // runs on the same bot input channel while those casts are pending, so
        // it must drive exact route execution as well as hazard observation.
        if (activeStackOwner is { } owner)
            antivirusPulseMoving = owner.PositionForAntivirusRoute(out antivirusPulseHandled);
    }

    // Combat may immediately request a route back through the first circle
    // after native escape reports success. Keep its verified ground origin
    // through the cast and a short effect margin, including yielding hooks.
    internal static void ObserveAmalgamOnPulse()
    {
        var owner = activeStackOwner;
        if (owner == null || !owner.OwnsQuarantineMovement() || WorldManager.SubZoneId != (uint)SubZoneId.CorruptedMemoryCache)
        {
            owner?.ResetAmalgamRoute();
            owner?.amalgamTimeline.Reset();
            owner?.amalgamMarkers.Reset();
            owner?.amalgamSpreadCasts.Clear();
            if (owner != null) { owner.amalgamSpreadShapes = []; owner.amalgamSpreadIncomplete = owner.amalgamSpreadCorrupt = false; }
            amalgamAuthoredActive = amalgamPulseHandled = amalgamPulseMoving = false;
            return;
        }
        owner.CaptureAmalgamCasts();
        amalgamPulseMoving = owner.PositionForAmalgamRoute(out amalgamPulseHandled);
        // Two captured deaths occurred when the later Matrix geometry confined
        // escape from the still-resolving baited circles. Matrix has several
        // seconds remaining after the final Voltburst; preserve that order.
        foreach (var circle in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.CastingSpellId == EnemyAction.Voltburst && a.SpellCastInfo.IsValid))
        {
            var circleOrigin = circle.SpellCastInfo.CastLocation;
            if (!float.IsFinite(circleOrigin.X) || !float.IsFinite(circleOrigin.Z) || Math.Abs(circleOrigin.X + 533) > 25 || Math.Abs(circleOrigin.Z + 373) > 25) continue;
            var finish = DateTime.UtcNow + circle.SpellCastInfo.RemainingCastTime;
            var deadline = finish.AddSeconds(1);
            // Observed damage follows reported finish by about 0.4-0.9s.
            // Keep the ground position independently of helper casting state.
            owner.voltburstCircles[circle.ObjectId] = new(circle.ObjectId, circleOrigin, finish.AddSeconds(.45), deadline);
            if (deadline > owner.voltburstUntil) owner.voltburstUntil = deadline;
        }
        foreach (var id in owner.voltburstCircles.Where(p => p.Value.Expires <= DateTime.UtcNow).Select(p => p.Key).ToArray())
            owner.voltburstCircles.Remove(id);
        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid && actor.CastingSpellId == 39254 && actor.SpellCastInfo.IsValid);
        if (caster == null) return;
        var origin = caster.SpellCastInfo.CastLocation;
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Z) ||
            Math.Abs(origin.X + 533) > 20 || Math.Abs(origin.Z + 373) > 20) return;
        owner.ternaryOrigin = new System.Numerics.Vector2(origin.X, origin.Z);
        // The first and middle effects are two seconds apart; the captured
        // one-second margin left too little time to re-enter the safe center.
        // Retain 0.6s for the observed roughly 0.5s effect delay.
        owner.ternaryUntil = DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.6);
    }

    private bool AllowOrdinaryTravel(Vector3 destination)
    {
        if (WorldManager.SubZoneId != (uint)SubZoneId.CorruptedMemoryCache) return true;
        foreach (var circle in ActiveVoltburstCircles())
            if (!AlexandriaTernaryPolicy.Allows(new System.Numerics.Vector2(Core.Me.X, Core.Me.Z),
                new System.Numerics.Vector2(destination.X, destination.Z), new(circle.Origin.X, circle.Origin.Z), 6.5f)) return false;
        if (DateTime.UtcNow >= ternaryUntil) return true;
        return AlexandriaTernaryPolicy.Allows(new System.Numerics.Vector2(Core.Me.X, Core.Me.Z),
            new System.Numerics.Vector2(destination.X, destination.Z), ternaryOrigin, 10.5f);
    }

    private static bool HasQuarantineMarker(BattleCharacter actor, uint id) => actor != null && actor.IsValid &&
        actor.VfxContainer.IsValid && actor.VfxContainer.Vfx.Any(vfx => vfx != null && vfx.IsValid && vfx.Id == id);

    private bool OwnsQuarantineMovement() => Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive &&
        Core.Me.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.VolatileMemory or (uint)SubZoneId.CorruptedMemoryCache or (uint)SubZoneId.Reascension;

    private void ReleaseQuarantineStack()
    {
        ReleaseAntivirusFacing();
        stackInputOwner?.ReleaseStack();
        if (!stackLeased) return;
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Alexandria stack released");
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Alexandria stack released");
        stackLeased = false;
        stackDestination = null;
        stackDestinationTarget = 0;
        stackDestinationKind = null;
    }

    private void DetachQuarantineMovement()
    {
        ResetEliminatorRoute();
        ResetAmalgamRoute();
        ResetAntivirusRoute();
        antivirusNodes.Clear();
        ReleaseQuarantineStack();
        stackInputOwner?.Detach();
        stackInputOwner = null;
        reportedForeignMover = false;
        if (ReferenceEquals(activeStackOwner, this)) activeStackOwner = null;
        quarantineUntil = default;
        interferonSequence.Reset();
        interferonDue = null;
        immuneResponse.Reset();
        superboltUntil = default;
        superboltCastEnd = default;
        superboltTarget = 0;
        superboltMarkerSeen = false;
        ternaryUntil = default;
        voltburstUntil = default;
        voltburstCircles.Clear();
        amalgamTimeline.Reset();
        amalgamMarkers.Reset();
        amalgamSpreadCasts.Clear(); amalgamSpreadShapes = [];
        amalgamSpreadIncomplete = amalgamSpreadCorrupt = false;
        eliminatorTimeline.Reset();
        eliminatorUnsupportedCast = false;
        eliminatorSpreadCasts.Clear(); eliminatorSpreadShapes = [];
        eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = false;
        eliminatorProteanCasts.Clear(); eliminatorProteanShapes = []; eliminatorLocalProteans = [];
        eliminatorProteanIncomplete = eliminatorProteanCorrupt = false;
        lineStackUntil = default;
    }

    // Bot stop and hook replacement can occur without another dungeon pulse.
    internal static void ResetActive()
    {
        SetBossSideStep(false);
        ResetAntivirusRoute();
        antivirusNodes.Clear();
        activeStackOwner?.DetachQuarantineMovement();
        quarantineUntil = default;
        interferonSequence.Reset();
        interferonDue = null;
        immuneResponse.Reset();
    }

    private bool PositionForQuarantineStack()
    {
        if (!OwnsQuarantineMovement()) { DetachQuarantineMovement(); return false; }
        // Marker 62 is only authoritative here within the captured Quarantine
        // window; an unrelated visual with that ID must not acquire movement.
        if (EnemyAction.Quarantine.IsCasting()) quarantineUntil = DateTime.UtcNow.AddSeconds(10);
        if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
            Navigator.PlayerMover = stackInputOwner;
            activeStackOwner = this;
        }
        if (stackInputOwner?.IsCurrent != true)
        {
            if (!reportedForeignMover) Logger.Information("[Alexandria] Another movement owner is active; stack and Ternary travel handling are unavailable.");
            reportedForeignMover = true;
            ReleaseQuarantineStack(); return false;
        }
        reportedForeignMover = false;
        stackInputOwner.CompleteEscape();
        if (DateTime.UtcNow >= quarantineUntil) { ReleaseQuarantineStack(); return false; }
        if (Core.Me.IsTank() || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        { ReleaseQuarantineStack(); return false; }

        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.IsVisible &&
                (actor.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == actor.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)actor.NpcId))).ToList();
        if (actors.All(actor => actor.ObjectId != Core.Me.ObjectId)) actors.Add(Core.Me);
        var markers = actors.Where(actor => HasQuarantineMarker(actor, 62)).ToArray();
        // Do not select participants until the simultaneous tankbuster target
        // is known: Trust job fields need not identify the tank correctly.
        if (markers.Length != 1 || actors.Count(actor => HasQuarantineMarker(actor, 344)) != 1)
        { ReleaseQuarantineStack(); return false; }
        var marked = markers[0];
        bool self = marked.ObjectId == Core.Me.ObjectId;
        // Trust NPCs may not expose a player job; the observed 344 marker is
        // the authoritative tankbuster exclusion for this mechanic.
        var party = actors.Where(actor => actor.ObjectId != Core.Me.ObjectId && !actor.IsTank() && !HasQuarantineMarker(actor, 344))
            .Select(actor => new System.Numerics.Vector2(actor.X, actor.Z)).ToArray();
        var player = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
        bool Safe(System.Numerics.Vector2 p)
        {
            var point = new Vector3(p.X, Core.Me.Y, p.Y);
            return Math.Abs(p.X - 852) < 19.5f && Math.Abs(p.Y - 823) < 14.5f &&
                !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point));
        }
        bool Reachable(System.Numerics.Vector2 from, System.Numerics.Vector2 to)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(System.Numerics.Vector2.Distance(from, to) / .5f));
            for (int i = 0; i <= steps; ++i)
                if (!Safe(System.Numerics.Vector2.Lerp(from, to, (float)i / steps))) return false;
            return true;
        }
        var destination = AlexandriaStackPlanner.Choose(player, self,
            new System.Numerics.Vector2(marked.X, marked.Z), party, Safe, Reachable);
        if (destination == null) { ReleaseQuarantineStack(); return false; }
        bool Covers(System.Numerics.Vector2 p) => self ? party.Count(m => System.Numerics.Vector2.Distance(m, p) <= 4.5f) >= 2 :
            System.Numerics.Vector2.Distance(new System.Numerics.Vector2(marked.X, marked.Z), p) <= 4.5f;
        if (stackDestinationKind == "quarantine" && stackDestinationTarget == marked.ObjectId && stackDestination is { } previous &&
            Covers(previous) && Safe(previous) && Reachable(player, previous)) destination = previous;
        stackDestination = destination; stackDestinationTarget = marked.ObjectId; stackDestinationKind = "quarantine";
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Quarantine stack");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Quarantine stack");
        stackLeased = true;
        var target = new Vector3(destination.Value.X, Core.Me.Y, destination.Value.Y);
        if (Core.Me.Location.Distance2D(target) <= .7f) { stackInputOwner.HoldStack(); return false; }
        stackInputOwner.MoveStack(target);
        return true;
    }

    private bool PositionForSuperboltStack()
    {
        if (!OwnsQuarantineMovement()) { DetachQuarantineMovement(); return false; }
        if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
            Navigator.PlayerMover = stackInputOwner;
            activeStackOwner = this;
        }
        if (stackInputOwner?.IsCurrent != true)
        {
            if (!reportedForeignMover) Logger.Information("[Alexandria] Another movement owner is active; stack and Ternary travel handling are unavailable.");
            reportedForeignMover = true;
            ReleaseQuarantineStack(); return false;
        }
        reportedForeignMover = false;
        stackInputOwner.CompleteEscape();
        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid && actor.CastingSpellId == EnemyAction.Superbolt && actor.SpellCastInfo.IsValid);
        if (caster != null)
        {
            uint targetId = caster.SpellCastInfo.TargetId;
            if (superboltTarget != targetId || DateTime.UtcNow >= superboltUntil) superboltMarkerSeen = false;
            superboltTarget = targetId;
            superboltCastEnd = DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime;
            // The 2026-11-02 capture kept lock-on 161 for over two seconds
            // after cast end. Retain the request through that marker lifecycle.
            superboltUntil = superboltCastEnd + TimeSpan.FromSeconds(3);
        }
        if (superboltTarget == 0 || DateTime.UtcNow >= superboltUntil ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        { ReleaseQuarantineStack(); return false; }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.IsVisible &&
                (actor.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == actor.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)actor.NpcId))).ToList();
        if (actors.All(actor => actor.ObjectId != Core.Me.ObjectId)) actors.Add(Core.Me);
        var marked = actors.FirstOrDefault(actor => actor.ObjectId == superboltTarget);
        if (marked == null) { ReleaseQuarantineStack(); return false; }
        bool markedNow = marked.VfxContainer.IsValid && marked.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 161);
        superboltMarkerSeen |= markedNow;
        // If the marker was observed, its disappearance permits release after
        // a short effect margin. If it was missed, keep a conservative fallback.
        if (!AlexandriaStackPlanner.KeepSuperbolt(caster != null, superboltMarkerSeen, markedNow,
            (DateTime.UtcNow - superboltCastEnd).TotalSeconds))
        { superboltTarget = 0; ReleaseQuarantineStack(); return false; }
        bool self = marked.ObjectId == Core.Me.ObjectId;
        var party = actors.Where(actor => actor.ObjectId != Core.Me.ObjectId)
            .Select(actor => new System.Numerics.Vector2(actor.X, actor.Z)).ToArray();
        var player = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
        double now = MonotonicNow;
        AlexandriaGeometry.Hazard[] shapes = [];
        bool haveShapes = !amalgamAuthoredActive || TryAmalgamShapes(now, out shapes);
        if (!haveShapes) { ReleaseQuarantineStack(); return false; }
        double horizon = now + Math.Max(.35, (superboltUntil - DateTime.UtcNow).TotalSeconds);
        bool Safe(System.Numerics.Vector2 p)
        {
            var point = new Vector3(p.X, Core.Me.Y, p.Y);
            return Math.Abs(p.X + 533) < 19.5f && Math.Abs(p.Y + 373) < 19.5f &&
                (amalgamAuthoredActive
                    ? AlexandriaGeometry.TravelSafe(p, p, new(now, horizon, 0, 5, 10), shapes)
                    : !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)));
        }
        bool Reachable(System.Numerics.Vector2 from, System.Numerics.Vector2 to)
        {
            if (amalgamAuthoredActive)
                return AlexandriaGeometry.TravelSafe(from, to, new(now, horizon, .1, 5, 10), shapes);
            int steps = Math.Max(1, (int)Math.Ceiling(System.Numerics.Vector2.Distance(from, to) / .5f));
            for (int i = 0; i <= steps; ++i)
                if (!Safe(System.Numerics.Vector2.Lerp(from, to, (float)i / steps))) return false;
            return true;
        }
        var destination = AlexandriaStackPlanner.Choose(player, self,
            new System.Numerics.Vector2(marked.X, marked.Z), party, Safe, Reachable, selfRequired: 3);
        if (destination == null) { ReleaseQuarantineStack(); return false; }
        bool Covers(System.Numerics.Vector2 p) => self ? party.Count(m => System.Numerics.Vector2.Distance(m, p) <= 4.5f) >= 3 :
            System.Numerics.Vector2.Distance(new System.Numerics.Vector2(marked.X, marked.Z), p) <= 4.5f;
        if (stackDestinationKind == "superbolt" && stackDestinationTarget == marked.ObjectId && stackDestination is { } previous &&
            Covers(previous) && Safe(previous) && Reachable(player, previous)) destination = previous;
        stackDestination = destination; stackDestinationTarget = marked.ObjectId; stackDestinationKind = "superbolt";
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Superbolt stack");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Superbolt stack");
        stackLeased = true;
        var target = new Vector3(destination.Value.X, Core.Me.Y, destination.Value.Y);
        if (Core.Me.Location.Distance2D(target) <= .7f) { ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false; }
        if (amalgamAuthoredActive)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
            antivirusFacingLeased = true;
            if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
            { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
            stackInputOwner.MoveRoute(target);
        }
        else stackInputOwner.MoveStack(target);
        return true;
    }

    private bool PositionForLineStack()
    {
        if (!OwnsQuarantineMovement()) { DetachQuarantineMovement(); return false; }
        if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
            Navigator.PlayerMover = stackInputOwner;
            activeStackOwner = this;
        }
        if (stackInputOwner?.IsCurrent != true) { ReleaseQuarantineStack(); return false; }
        stackInputOwner.CompleteEscape();
        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.CastingSpellId is EnemyAction.Overexposure or EnemyAction.LightofDevotion && a.SpellCastInfo.IsValid);
        if (caster != null)
        {
            lineStackSource = new(caster.X, caster.Z);
            // The visual precedes the instant line effect; retain participation
            // through the effect margin instead of releasing when casting clears.
            lineStackUntil = DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }
        if (DateTime.UtcNow >= lineStackUntil || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        { ReleaseQuarantineStack(); return false; }
        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) || PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)))
            .Select(a => new System.Numerics.Vector2(a.X, a.Z)).ToArray();
        var player = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
        double now = MonotonicNow;
        AlexandriaGeometry.Hazard[] shapes = [];
        bool haveShapes = !eliminatorAuthoredActive || TryEliminatorShapes(now, out shapes);
        if (!haveShapes) { ReleaseQuarantineStack(); return false; }
        double horizon = now + Math.Max(.35, (lineStackUntil - DateTime.UtcNow).TotalSeconds);
        bool Safe(System.Numerics.Vector2 p) => Math.Abs(p.X + 759) < 13.5f && Math.Abs(p.Y + 648) < 13.5f &&
            (eliminatorAuthoredActive
                ? haveShapes && AlexandriaGeometry.TravelSafe(p, p, new(now, horizon, 0, 5, 10), shapes) &&
                    eliminatorLocalProteans.All(line =>
                        (System.Numerics.Vector2.Distance(player, p) <= .2f || now + .1 + System.Numerics.Vector2.Distance(player, p) / 5 <= line.Earliest) &&
                        AlexandriaEliminatorProtean.Safe(line.Origin, p, party))
                : !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(new Vector3(p.X, Core.Me.Y, p.Y))));
        bool Reachable(System.Numerics.Vector2 a, System.Numerics.Vector2 b)
        {
            if (eliminatorAuthoredActive)
                return haveShapes && AlexandriaGeometry.TravelSafe(a, b, new(now, horizon, .1, 5, 10), shapes);
            int steps = Math.Max(1, (int)Math.Ceiling(System.Numerics.Vector2.Distance(a, b) / .5f));
            for (int i = 0; i <= steps; ++i)
                if (!Safe(System.Numerics.Vector2.Lerp(a, b, (float)i / steps))) return false;
            return true;
        }
        var destination = AlexandriaStackPlanner.ChooseLine(player, lineStackSource, party, Safe, Reachable);
        if (destination == null) { ReleaseQuarantineStack(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria line stack");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria line stack");
        stackLeased = true;
        var point = new Vector3(destination.Value.X, Core.Me.Y, destination.Value.Y);
        if (Core.Me.Location.Distance2D(point) <= .7f) { ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false; }
        if (eliminatorAuthoredActive)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
            antivirusFacingLeased = true;
            if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
            { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
            stackInputOwner.MoveRoute(point);
        }
        else stackInputOwner.MoveStack(point);
        return true;
    }

    private sealed class StackInputOwner : IPlayerMover
    {
        private enum Command { None, Ordinary, Escape, Stack, Hold }
        private readonly IPlayerMover inner;
        private readonly Func<bool> owns;
        private readonly Func<Vector3, bool> allowOrdinary;
        private readonly Func<bool> semanticActive;
        private bool detached, submitting;
        private Command command;
        internal int SuppressedStops { get; private set; }
        internal int SuppressedTravel { get; private set; }
        internal StackInputOwner(IPlayerMover inner, Func<bool> owns, Func<Vector3, bool> allowOrdinary, Func<bool> semanticActive)
        { this.inner = inner; this.owns = owns; this.allowOrdinary = allowOrdinary; this.semanticActive = semanticActive; }
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent) return;
            // Capabilities do not cover every combat caller. Enforce the lease
            // at the input channel: ordinary travel cannot replace a newer
            // semantic request, while native escape retains priority.
            if (!submitting && OwnsSemanticInput()) { SuppressedTravel++; return; }
            if (owns() && !submitting && !AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl && !allowOrdinary(location))
            {
                // This synchronous rejection can cancel only an older ordinary
                // command, never a newer native escape or semantic request.
                if (command == Command.Ordinary) inner.MoveStop();
                if (command == Command.Ordinary) command = Command.None;
                return;
            }
            command = owns() && AvoidanceManager.IsRunningOutOfAvoid ? Command.Escape : submitting ? Command.Stack : Command.Ordinary;
            inner.MoveTowards(location);
        }
        private bool OwnsSemanticInput() => owns() && ff14bot.TreeRoot.IsRunning && semanticActive() &&
            !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;
        private void StopOwned() { if (!IsCurrent) return; command = Command.None; inner.MoveStop(); }
        public void MoveStop()
        {
            if (!IsCurrent) return;
            if (OwnsSemanticInput() && command is Command.Stack or Command.Hold) { SuppressedStops++; return; }
            StopOwned();
        }
        internal void MoveStack(Vector3 destination)
        {
            if (!IsCurrent || !owns() || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
            submitting = true;
            try { Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = .7f, UseMount = false }); }
            finally { submitting = false; }
        }
        // The temporal planner has already checked this short segment on the
        // verified arena floor. Navigator's 0.7y arrival tolerance left captured
        // cone/shelter waypoints unsafe; issue exact segments through this same
        // input owner instead of creating another mover or navigation channel.
        internal void MoveRoute(Vector3 destination)
        {
            if (!IsCurrent || !owns() || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
            submitting = true;
            try { MoveTowards(destination); }
            finally { submitting = false; }
        }
        internal void HoldStack()
        {
            if (!IsCurrent || !owns() || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || command == Command.Hold) return;
            StopOwned(); command = Command.Hold;
        }
        internal void ReleaseStack()
        {
            if (!IsCurrent || command is not (Command.Stack or Command.Hold)) return;
            if (owns() && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl) StopOwned();
            else command = Command.None;
        }
        internal void CompleteEscape()
        {
            if (!IsCurrent || command != Command.Escape || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl ||
                AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location))) return;
            StopOwned();
        }
        internal void Detach()
        {
            if (IsCurrent)
            {
                // A death, loading transition, or native escape owns the next
                // handoff; detaching must not cancel its successor input.
                if (owns() && command is (Command.Stack or Command.Hold) &&
                    !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl) MoveStop();
                Navigator.PlayerMover = inner;
            }
            command = Command.None; detached = true;
        }
    }
}
