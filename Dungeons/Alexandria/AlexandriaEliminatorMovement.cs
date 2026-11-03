using System;
using System.Linq;
using System.Numerics;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    private static bool eliminatorAuthoredActive, eliminatorPulseHandled, eliminatorPulseMoving;
    private Vector2? eliminatorDestination;
    private uint eliminatorTrust;
    private double eliminatorReportAfter;
    private bool eliminatorWasPush;
    private void ResetEliminatorRoute()
    {
        eliminatorDestination = null; eliminatorTrust = 0; eliminatorReportAfter = 0;
        eliminatorWasPush = false;
        eliminatorAuthoredActive = eliminatorPulseHandled = eliminatorPulseMoving = false;
    }

    // Build the same observed hazards for both direct dodges and a line-stack
    // destination. Participant-based baits are already refreshed this pulse.
    private bool TryEliminatorShapes(double now, out AlexandriaGeometry.Hazard[] shapes)
    {
        shapes = [];
        if (eliminatorTimeline.HasIncompleteEvidence(now) || eliminatorSpreadIncomplete || eliminatorProteanIncomplete) return false;
        var effects = eliminatorTimeline.Snapshot(now);
        if (effects.Any(e => e.Action == 36794)) return false;
        var result = new AlexandriaGeometry.Hazard[effects.Length + eliminatorSpreadShapes.Length + eliminatorProteanShapes.Length];
        for (int i = 0; i < effects.Length; ++i)
        {
            var effect = effects[i];
            if (!AlexandriaMechanicShapes.TryCreate(effect.Action, effect.Origin, effect.Direction, .5,
                now + effect.Earliest, now + effect.Latest, out result[i])) return false;
        }
        Array.Copy(eliminatorSpreadShapes, 0, result, effects.Length, eliminatorSpreadShapes.Length);
        Array.Copy(eliminatorProteanShapes, 0, result, effects.Length + eliminatorSpreadShapes.Length, eliminatorProteanShapes.Length);
        shapes = result;
        return true;
    }

    internal static void ObserveEliminatorOnPulse()
    {
        var owner = activeStackOwner;
        if (owner == null || !owner.OwnsQuarantineMovement() || WorldManager.SubZoneId != (uint)SubZoneId.Reascension)
        {
            owner?.ResetEliminatorRoute();
            owner?.eliminatorTimeline.Reset();
            eliminatorAuthoredActive = eliminatorPulseHandled = eliminatorPulseMoving = false;
            return;
        }
        owner.CaptureEliminatorCasts();
        eliminatorPulseMoving = owner.PositionForEliminatorRoute(out eliminatorPulseHandled);
    }

    private bool PositionForEliminatorRoute(out bool handled)
    {
        handled = false;
        double now = MonotonicNow;
        var effects = eliminatorTimeline.Snapshot(now);
        // Party-targeted lines and spreads require participation logic; do
        // not claim them as ordinary fixed-position shapes.
        bool hasPush = effects.Any(e => e.Action == 36794);
        // The pre-push staging point is no longer a destination after the
        // forced landing. Retaining it pulled the player back through the
        // subsequent targeted lines in the 2026-11-03 capture.
        if (!hasPush && eliminatorWasPush) { eliminatorDestination = null; eliminatorTrust = 0; }
        eliminatorWasPush = hasPush;
        if (!hasPush && (eliminatorUnsupportedCast || DateTime.UtcNow < lineStackUntil) &&
            !eliminatorTimeline.HasIncompleteEvidence(now) && !eliminatorSpreadIncomplete && !eliminatorProteanIncomplete)
        {
            // The target-select is instant and not exposed as a stable party
            // marker. When it cannot be identified, the existing geometric
            // stack solver uses the observed party line as the authorized
            // last resort while checking all known overlapping hazards.
            eliminatorAuthoredActive = true;
            handled = true;

            bool moving = PositionForLineStack();
            if (!stackLeased && stackInputOwner?.IsCurrent == true && !AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl && !MovementManager.ForceMoved)
            {
                var stackPlayer = new Vector2(Core.Me.X, Core.Me.Z);
                var trusts = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId) &&
                        Math.Abs(a.X + 759) < 14.5 && Math.Abs(a.Z + 648) < 14.5)
                    .Select(a => new AlexandriaTrustSelector.Candidate(a.ObjectId, new Vector2(a.X, a.Z))).ToArray();
                var trust = !eliminatorSpreadCasts.Values.Any(s => s.Target == Core.Me.ObjectId && s.Latest >= now) &&
                    eliminatorLocalProteans.Length == 0 ? AlexandriaTrustSelector.Choose(stackPlayer, eliminatorTrust, trusts) : null;
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria line stack fallback");
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria line stack fallback");
                stackLeased = true;
                if (trust is { } selected && Vector2.Distance(stackPlayer, selected.Position) > .35f)
                {
                    eliminatorTrust = selected.Id;
                    CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
                    antivirusFacingLeased = true;
                    if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
                    { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
                    stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(selected.Position.X, Core.Me.Y, selected.Position.Y));
                    return true;
                }
                ReleaseAntivirusFacing();
                stackInputOwner.HoldStack();
            }
            return moving;
        }
        // Knockback resolves before the later targeted lines.
        if (effects.Length == 0 && eliminatorSpreadShapes.Length == 0 && eliminatorProteanShapes.Length == 0 &&
                eliminatorLocalProteans.Length == 0 || eliminatorTimeline.HasIncompleteEvidence(now) ||
            eliminatorSpreadIncomplete || eliminatorUnsupportedCast && !hasPush || eliminatorProteanIncomplete && !hasPush)
        {
            bool wasActive = eliminatorAuthoredActive;

            ResetEliminatorRoute();
            if (wasActive) ReleaseQuarantineStack();
            return false;
        }
        var pushes = effects.Where(e => e.Action == 36794).ToArray();
        var damaging = effects.Where(e => e.Action != 36794).ToArray();
        // A push must be paired with the observed Compression damage helper;
        // otherwise neither its origin nor its timing is established.
        if (pushes.Length > 1 || pushes.Length == 1 && !damaging.Any(e => e.Action == 36793 &&
            Vector2.Distance(e.Origin, pushes[0].Origin) < .5f))
        { ResetEliminatorRoute(); return false; }
        int proteanCount = hasPush ? 0 : eliminatorProteanShapes.Length;
        var shapes = new AlexandriaGeometry.Hazard[damaging.Length + eliminatorSpreadShapes.Length + proteanCount];
        for (int i = 0; i < damaging.Length; ++i)
        {
            var e = damaging[i];
            if (!AlexandriaMechanicShapes.TryCreate(e.Action, e.Origin, e.Direction, .5,
                now + e.Earliest, now + e.Latest, out shapes[i]))
            { ResetEliminatorRoute(); return false; }
        }
        Array.Copy(eliminatorSpreadShapes, 0, shapes, damaging.Length, eliminatorSpreadShapes.Length);
        if (proteanCount != 0) Array.Copy(eliminatorProteanShapes, 0, shapes, damaging.Length + eliminatorSpreadShapes.Length, proteanCount);
        if (stackInputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            stackInputOwner = new StackInputOwner(Navigator.PlayerMover, OwnsQuarantineMovement, AllowOrdinaryTravel, () => stackLeased);
            Navigator.PlayerMover = stackInputOwner; activeStackOwner = this;
        }
        if (stackInputOwner?.IsCurrent != true) { ResetEliminatorRoute(); return false; }
        eliminatorAuthoredActive = handled = true;
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || MovementManager.ForceMoved)
        {

            return false;
        }
        stackInputOwner.CompleteEscape();
        var player = new Vector2(Core.Me.X, Core.Me.Z);
        double horizon = Math.Max(now + .35, shapes.Length == 0 ? now : shapes.Max(h => h.Latest));
        if (pushes.Length != 0) horizon = Math.Max(horizon, now + pushes[0].Latest);
        if (!hasPush && eliminatorLocalProteans.Length != 0) horizon = Math.Max(horizon, eliminatorLocalProteans.Max(p => p.Latest));
        Vector2? destination;
        if (pushes.Length != 0)
        {
            var push = pushes[0];
            double earliest = now + push.Earliest, latest = now + push.Latest;
            // At the impact boundary, stop issuing input and allow the game's
            // forced movement to finish before choosing any successor point.
            if (now >= earliest)
            { ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false; }
            destination = AlexandriaEliminatorKnockback.Choose(player, push.Origin, eliminatorDestination,
                shapes, now, earliest, latest)?.Before;
        }
        else
        {
            Func<Vector2, bool> localLines = null;
            if (eliminatorLocalProteans.Length != 0)
            {
                var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                         PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)))
                    .Select(a => new Vector2(a.X, a.Z)).ToArray();
                localLines = candidate => eliminatorLocalProteans.All(line =>
                    (Vector2.Distance(player, candidate) <= .2f || now + .1 + Vector2.Distance(player, candidate) / 5 <= line.Earliest) &&
                    AlexandriaEliminatorProtean.Safe(line.Origin, candidate, party));
            }
            destination = AlexandriaEliminatorDirectPlanner.Choose(player, eliminatorDestination, shapes, now, horizon, 5, 10, localLines);
        }
        if (destination == null && !eliminatorSpreadCasts.Values.Any(s => s.Target == Core.Me.ObjectId && s.Latest >= now) &&
            (hasPush || eliminatorLocalProteans.Length == 0))
        {
            // Explicitly authorized last resort after the authored direct
            // search fails; party NPC coordinates are observed on this pulse.
            var trusts = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                    PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId) &&
                    Math.Abs(a.X + 759) < 14.5 && Math.Abs(a.Z + 648) < 14.5)
                .Select(a => new AlexandriaTrustSelector.Candidate(a.ObjectId, new Vector2(a.X, a.Z))).ToArray();
            var trust = AlexandriaTrustSelector.Choose(player, eliminatorTrust, trusts);
            if (trust is { } selected)
            {
                eliminatorTrust = selected.Id; destination = selected.Position;
                if (LoggingHelpers.MechanicDiagnosticsEnabled && now >= eliminatorReportAfter)
                { Logger.Information("[Alexandria] Eliminator Trust last resort."); eliminatorReportAfter = now + 1; }
            }
        }
        else { eliminatorDestination = destination; eliminatorTrust = 0; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria Eliminator route");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria Eliminator route");
        stackLeased = true;

        if (destination is not { } target || Vector2.Distance(player, target) <= .2f)
        { ReleaseAntivirusFacing(); stackInputOwner.HoldStack(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria route travel facing");
        antivirusFacingLeased = true;
        if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
        { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
        stackInputOwner.MoveRoute(new Clio.Utilities.Vector3(target.X, Core.Me.Y, target.Y));
        return true;
    }
}
