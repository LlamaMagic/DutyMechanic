using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    private readonly AlexandriaEliminatorTimeline eliminatorTimeline = new();
    private bool eliminatorUnsupportedCast;
    private readonly Dictionary<uint, (uint Target, double Earliest, double Latest)> eliminatorSpreadCasts = [];
    private AlexandriaGeometry.Hazard[] eliminatorSpreadShapes = [];
    private bool eliminatorSpreadIncomplete, eliminatorSpreadCorrupt;
    private readonly Dictionary<uint, (uint Target, Vector2 Origin, double Earliest, double Latest)> eliminatorProteanCasts = [];
    private AlexandriaGeometry.Hazard[] eliminatorProteanShapes = [];
    private (Vector2 Origin, double Earliest, double Latest)[] eliminatorLocalProteans = [];
    private bool eliminatorProteanIncomplete, eliminatorProteanCorrupt;

    // Capture detached cast and target lifetimes for the movement owner. The
    // owner revalidates each pulse and releases movement on incomplete data.
    private void CaptureEliminatorCasts()
    {
        if (WorldManager.SubZoneId != (uint)SubZoneId.Reascension || Core.Me == null || !Core.Me.InCombat)
        {
            eliminatorTimeline.Reset(); eliminatorUnsupportedCast = false;
            eliminatorSpreadCasts.Clear(); eliminatorSpreadShapes = []; eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = false;
            eliminatorProteanCasts.Clear(); eliminatorProteanShapes = []; eliminatorLocalProteans = [];
            eliminatorProteanIncomplete = eliminatorProteanCorrupt = false;
            return;
        }
        double now = MonotonicNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        bool Supported(uint action) => action is EnemyAction.PartitionLeft or EnemyAction.PartitionRight2 or
            EnemyAction.PartitionLeft2 or EnemyAction.PartitionRight or EnemyAction.TerminateHelper or
            EnemyAction.HaloHelper or EnemyAction.CompressionDamage or EnemyAction.ImpactConst or EnemyAction.Explosion;
        eliminatorTimeline.Reconcile(now, actors.Where(a => Supported(a.CastingSpellId)).Select(a => (a.ObjectId, a.CastingSpellId)));
        // Spreads, target-dependent lines and the knockback visual carry
        // participant or source state not represented by these scalar casts.
        // Their target and forced-movement lifetimes are retained below.
        eliminatorUnsupportedCast = actors.Any(a => a.CastingSpellId is EnemyAction.Overexposure or EnemyAction.LightofDevotion);
        eliminatorSpreadIncomplete = eliminatorSpreadCorrupt;
        var activeSpreads = actors.Where(a => a.CastingSpellId == EnemyAction.Electray).ToArray();
        foreach (var source in activeSpreads)
        {
            if (!source.SpellCastInfo.IsValid) { eliminatorSpreadIncomplete = true; continue; }
            uint target = source.SpellCastInfo.TargetId;
            double remaining = source.SpellCastInfo.RemainingCastTime.TotalSeconds;
            if (target is 0 or 0xE0000000 || !double.IsFinite(remaining) || remaining < 0)
            { eliminatorSpreadIncomplete = true; continue; }
            double finish = now + remaining;
            if (eliminatorSpreadCasts.TryGetValue(source.ObjectId, out var prior))
            {
                if (prior.Target != target) { eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = true; continue; }
                if (remaining == 0) continue;
                eliminatorSpreadCasts[source.ObjectId] = (target, Math.Min(prior.Earliest, finish - .5), Math.Max(prior.Latest, finish + .7));
            }
            else if (remaining > 0) eliminatorSpreadCasts[source.ObjectId] = (target, finish - .5, finish + .7);
            else eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = true;
        }
        var spreadShapes = new List<AlexandriaGeometry.Hazard>();
        foreach (var entry in eliminatorSpreadCasts.ToArray())
        {
            if (now > entry.Value.Latest)
            {
                if (activeSpreads.Any(a => a.ObjectId == entry.Key)) eliminatorSpreadIncomplete = true;
                else eliminatorSpreadCasts.Remove(entry.Key);
                continue;
            }
            if (entry.Value.Target == Core.Me.ObjectId) continue;
            var target = actors.FirstOrDefault(a => a.ObjectId == entry.Value.Target && a.IsVisible && a.IsAlive);
            if (target == null || !AlexandriaMechanicShapes.TryCreate(EnemyAction.Electray, new Vector2(target.X, target.Z), Vector2.UnitX,
                .5, entry.Value.Earliest, entry.Value.Latest, out var spread))
            { eliminatorSpreadIncomplete = true; continue; }
            spreadShapes.Add(spread);
        }
        // If the local player carries a spread, unmarked party members must
        // also remain outside its six-yalm hit. Their current positions are
        // resampled and the chosen route is revalidated on every pulse.
        var localSpreads = eliminatorSpreadCasts.Values.Where(s => s.Target == Core.Me.ObjectId && s.Latest >= now).ToArray();
        if (localSpreads.Length != 0)
        {
            var party = actors.Where(a => a.IsVisible && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
            if (party.Length == 0) eliminatorSpreadIncomplete = true;
            foreach (var member in party)
            foreach (var local in localSpreads)
            {
                if (!AlexandriaMechanicShapes.TryCreate(EnemyAction.Electray, new Vector2(member.X, member.Z), Vector2.UnitX,
                    .5, local.Earliest, local.Latest, out var spread)) { eliminatorSpreadIncomplete = true; continue; }
                spreadShapes.Add(spread);
            }
        }
        eliminatorSpreadShapes = spreadShapes.ToArray();
        CaptureEliminatorProteans(now, actors);
        foreach (var actor in actors.Where(a => Supported(a.CastingSpellId)))
        {
            if (!actor.SpellCastInfo.IsValid) { eliminatorTimeline.MarkIncomplete(); continue; }
            double remaining = actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
            float heading = actor.Heading;
            var origin = new Vector2(actor.X, actor.Z);
            if (!double.IsFinite(remaining) || remaining < 0 || !float.IsFinite(heading) ||
                !AlexandriaGeometry.Finite(origin) || Math.Abs(origin.X + 759) > 30 || Math.Abs(origin.Y + 648) > 30)
            { eliminatorTimeline.MarkIncomplete(); continue; }
            if (!AlexandriaEliminatorDirections.TryGet(actor.CastingSpellId, heading, out var direction))
            { eliminatorTimeline.MarkIncomplete(); continue; }
            double finish = now + remaining;
            // Sixty-five matched local receipts arrived 0.154–0.340 seconds
            // after cast finish. The wider window is provisional and does not
            // claim to bound unseen snapshots or other network conditions.
            eliminatorTimeline.Observe(new(actor.ObjectId, actor.CastingSpellId, origin, direction,
                finish - .5, finish + .7, false), remainingIsZero: remaining == 0);
        }
    }

    private void CaptureEliminatorProteans(double now, BattleCharacter[] actors)
    {
        eliminatorProteanIncomplete = eliminatorProteanCorrupt;
        var active = actors.Where(a => a.CastingSpellId == EnemyAction.TargetedLine).ToArray();
        foreach (var source in active)
        {
            if (!source.SpellCastInfo.IsValid) { eliminatorProteanIncomplete = true; continue; }
            uint target = source.SpellCastInfo.TargetId;
            double remaining = source.SpellCastInfo.RemainingCastTime.TotalSeconds;
            var origin = new Vector2(source.X, source.Z);
            if (target is 0 or 0xE0000000 || !double.IsFinite(remaining) || remaining < 0 || !AlexandriaGeometry.Finite(origin))
            { eliminatorProteanIncomplete = true; continue; }
            double finish = now + remaining;
            if (eliminatorProteanCasts.TryGetValue(source.ObjectId, out var prior))
            {
                if (prior.Target != target || Vector2.Distance(prior.Origin, origin) > .25f)
                { eliminatorProteanIncomplete = eliminatorProteanCorrupt = true; continue; }
                if (remaining == 0) continue;
                eliminatorProteanCasts[source.ObjectId] = (target, prior.Origin, Math.Min(prior.Earliest, finish - .5), Math.Max(prior.Latest, finish + .7));
            }
            else if (remaining > 0) eliminatorProteanCasts[source.ObjectId] = (target, origin, finish - .5, finish + .7);
            else eliminatorProteanIncomplete = eliminatorProteanCorrupt = true;
        }
        var shapes = new List<AlexandriaGeometry.Hazard>();
        var local = new List<(Vector2 Origin, double Earliest, double Latest)>();
        foreach (var entry in eliminatorProteanCasts.ToArray())
        {
            var cast = entry.Value;
            if (now > cast.Latest)
            {
                if (active.Any(a => a.ObjectId == entry.Key)) eliminatorProteanIncomplete = true;
                else eliminatorProteanCasts.Remove(entry.Key);
                continue;
            }
            if (cast.Target == Core.Me.ObjectId) { local.Add((cast.Origin, cast.Earliest, cast.Latest)); continue; }
            var target = actors.FirstOrDefault(a => a.ObjectId == cast.Target && a.IsVisible && a.IsAlive);
            if (target == null) { eliminatorProteanIncomplete = true; continue; }
            var delta = new Vector2(target.X, target.Z) - cast.Origin;
            if (delta.LengthSquared() < .25f || !AlexandriaMechanicShapes.TryCreate(EnemyAction.TargetedLine, cast.Origin,
                Vector2.Normalize(delta), .5, cast.Earliest, cast.Latest, out var shape))
            { eliminatorProteanIncomplete = true; continue; }
            shapes.Add(shape);
        }
        eliminatorProteanShapes = shapes.ToArray();
        eliminatorLocalProteans = local.ToArray();
    }
}

// Choose one reachable point against all currently observed overlapping
// shapes. The caller rechecks it each pulse and retains responsibility for
// party targets and knockback, which are not ordinary walking hazards.
internal static class AlexandriaEliminatorDirectPlanner
{
    // Halo resolves before the following line. Stage inside its six-yalm
    // opening while preserving clearance from any simultaneous cleave,
    // line or party-targeted hazard; the ordinary owner handles later casts.
    internal static Vector2? ChooseHalo(Vector2 player, Vector2? previous, AlexandriaGeometry.Hazard[] shapes,
        double now, Func<Vector2, bool> additional = null)
    {
        if (!AlexandriaGeometry.Finite(player) || shapes == null || !double.IsFinite(now)) return null;
        var halo = Array.Find(shapes, h => h.Kind == AlexandriaGeometry.Kind.Annulus && Math.Abs(h.A - 6) < .1);
        if (!halo.Valid || now > halo.Latest) return null;
        bool Safe(Vector2 p)
        {
            if (Math.Abs(p.X + 759) >= 14.5 || Math.Abs(p.Y + 648) >= 14.5 ||
                Vector2.Distance(p, halo.Origin) >= 5.4f || additional != null && !additional(p)) return false;
            double arrival = now + .101 + Vector2.Distance(player, p) / 5;
            if (!AlexandriaGeometry.TravelSafe(player, p, new(now, arrival, .1, 5, 10), shapes)) return false;
            foreach (var shape in shapes)
                if (shape.Earliest <= halo.Latest && shape.Latest >= halo.Earliest &&
                    AlexandriaGeometry.Distance(p, shape) <= shape.Margin) return false;
            return true;
        }
        if (previous is { } retained && Safe(retained)) return retained;
        if (Safe(player)) return player;
        Vector2? best = null; float bestDistance = float.PositiveInfinity;
        foreach (double radius in new[] { 4.5, 3.0, 0.0 })
        for (int i = 0; i < (radius == 0 ? 1 : 24); ++i)
        {
            double angle = Math.PI * i / 12;
            var point = halo.Origin + new Vector2((float)(Math.Cos(angle) * radius), (float)(Math.Sin(angle) * radius));
            float distance = Vector2.DistanceSquared(player, point);
            if (distance >= bestDistance || !Safe(point)) continue;
            best = point; bestDistance = distance;
        }
        return best;
    }

    internal static Vector2? Choose(Vector2 player, Vector2? previous, AlexandriaGeometry.Hazard[] shapes,
        double now, double horizon, double minimumSpeed = 5, double maximumSpeed = 8,
        Func<Vector2, bool> additional = null)
    {
        if (!AlexandriaGeometry.Finite(player) || shapes == null || shapes.Length == 0 && additional == null ||
            !double.IsFinite(now) || !double.IsFinite(horizon) || horizon <= now) return null;
        bool InArena(Vector2 p) => AlexandriaGeometry.Finite(p) && Math.Abs(p.X + 759) < 14.5 && Math.Abs(p.Y + 648) < 14.5;
        bool Safe(Vector2 p) => InArena(p) && (additional == null || additional(p)) && AlexandriaGeometry.TravelSafe(player, p,
            new(now, horizon, .1, minimumSpeed, maximumSpeed), shapes);
        if (!InArena(player)) return null;
        if (previous is { } retained && Safe(retained)) return retained;
        if (Safe(player)) return player;
        Vector2? best = null;
        float distance = float.PositiveInfinity;
        for (int x = -14; x <= 14; ++x)
        for (int z = -14; z <= 14; ++z)
        {
            var p = new Vector2(-759 + x, -648 + z);
            float next = Vector2.DistanceSquared(player, p);
            if (next >= distance || !Safe(p)) continue;
            best = p; distance = next;
        }
        return best;
    }
}

// Partition helpers retain the arena-facing heading for both left and right
// variants. The action selects which side of that axis is dangerous.
internal static class AlexandriaEliminatorDirections
{
    internal static bool TryGet(uint action, float heading, out Vector2 direction)
    {
        direction = default;
        if (!float.IsFinite(heading)) return false;
        var forward = new Vector2((float)Math.Sin(heading), (float)Math.Cos(heading));
        direction = action switch
        {
            39007 or 39238 => new Vector2(-forward.Y, forward.X),
            39008 or 39249 => new Vector2(forward.Y, -forward.X),
            _ => forward
        };
        return AlexandriaGeometry.Finite(direction);
    }
}

// Compression pushes 15 yalms from its helper. Choose a walking point whose
// pre-push position, entire push segment, and landing are safe for the
// observed impact window; never command movement during the forced motion.
internal static class AlexandriaEliminatorKnockback
{
    internal readonly record struct Destination(Vector2 Before, Vector2 Landing);

    internal static Destination? Choose(Vector2 player, Vector2 origin, Vector2? previous,
        AlexandriaGeometry.Hazard[] shapes, double now, double earliest, double latest)
    {
        if (!AlexandriaGeometry.Finite(player) || !AlexandriaGeometry.Finite(origin) || shapes == null ||
            !double.IsFinite(now) || !double.IsFinite(earliest) || !double.IsFinite(latest) ||
            earliest <= now || latest < earliest) return null;
        bool Inside(Vector2 p) => AlexandriaGeometry.Finite(p) && Math.Abs(p.X + 759) < 14.5 && Math.Abs(p.Y + 648) < 14.5;
        bool Safe(Vector2 p, out Vector2 landing)
        {
            landing = default;
            if (!Inside(p) || !AlexandriaPositionConstraints.TryKnockback(p, origin, 15, out landing) || !Inside(landing) ||
                !AlexandriaGeometry.TravelSafe(player, p, new(now, earliest, .1, 5, 10), shapes) ||
                !AlexandriaGeometry.TravelSafe(p, p, new(earliest, latest, 0, 5, 10), shapes) ||
                !AlexandriaGeometry.TravelSafe(landing, landing, new(earliest, latest, 0, 5, 10), shapes)) return false;
            foreach (var shape in shapes)
                if (shape.Earliest <= latest && shape.Latest >= earliest && !AlexandriaGeometry.SegmentSafe(p, landing, shape)) return false;
            return true;
        }
        if (!Inside(player)) return null;
        if (previous is { } old && Safe(old, out var retained)) return new(old, retained);
        if (Safe(player, out var current)) return new(player, current);
        Destination? best = null;
        float distance = float.PositiveInfinity;
        for (int x = -14; x <= 14; ++x)
        for (int z = -14; z <= 14; ++z)
        {
            var p = new Vector2(-759 + x, -648 + z);
            float next = Vector2.DistanceSquared(player, p);
            if (next >= distance || !Safe(p, out var landing)) continue;
            best = new(p, landing); distance = next;
        }
        return best;
    }
}

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
            // The donut's inner shelter is the first stage when it overlaps
            // a later line or half-room cleave. Recheck all live hazards.
            var haloPoint = AlexandriaEliminatorDirectPlanner.ChooseHalo(player, eliminatorDestination, shapes, now, localLines);
            destination = haloPoint ?? AlexandriaEliminatorDirectPlanner.Choose(player, eliminatorDestination, shapes, now, horizon, 5, 10, localLines);
            if (haloPoint is { } haloTarget)
                TraceEncounterMovement("eliminator-halo-move", player, haloTarget, now);
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

/// <summary>
/// Detached Eliminator overlap planner. Its caller supplies verified helper
/// geometry, target identities, effect windows and positive-position needs;
/// this class does not access the client or own movement.
/// </summary>
internal static class AlexandriaEliminatorPlanner
{
    internal readonly record struct Effect(uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    internal static bool TryPlan(Vector2 player, Effect[] effects, AlexandriaPositionConstraints.Timed[] requirements,
        double margin, double spacing, double[] times, double delay, double minimumSpeed, double maximumSpeed,
        bool evidenceComplete, out AlexandriaGridPlanner.Step[] route, CancellationToken cancellation = default,
        AlexandriaTemporalPlanner.RadialKnockback? knockback = null)
    {
        route = Array.Empty<AlexandriaGridPlanner.Step>();
        if (!evidenceComplete || effects == null || requirements == null || times == null || times.Length < 2 ||
            !double.IsFinite(times[^1]) || !double.IsFinite(margin) || margin < 0 || cancellation.IsCancellationRequested)
            return false;
        var hazards = new List<AlexandriaGeometry.Hazard>();
        bool sawKnockback = false;
        foreach (var effect in effects)
        {
            if (!double.IsFinite(effect.Latest) || effect.Latest > times[^1]) return false;
            if (effect.Action == 36794)
            {
                // Compression is a 15-yalm radial push, not ordinary walking.
                // Require an explicit paired edge inside the caller-supplied
                // effect window and reject duplicates or an unknown origin.
                if (sawKnockback || knockback is not { } push || times == null || push.Edge < 0 ||
                    push.Edge >= times.Length - 1 || !double.IsFinite(effect.Earliest) || !double.IsFinite(effect.Latest) ||
                    effect.Earliest > times[push.Edge] || effect.Latest < times[push.Edge + 1] ||
                    !AlexandriaGeometry.Finite(effect.Origin) || Vector2.Distance(effect.Origin, push.Origin) > .01f ||
                    Math.Abs(push.Distance - 15) > .001 || cancellation.IsCancellationRequested) return false;
                sawKnockback = true;
                continue;
            }
            if (cancellation.IsCancellationRequested || effect.Action is not
                (39007 or 39008 or 39238 or 39249 or 39243 or 39615 or 39616 or 36793 or 39239))
                return false;
            // Validate even a local spread: a malformed target snapshot must
            // not turn into apparent evidence of a clear arena.
            if (!AlexandriaMechanicShapes.TryCreate(effect.Action, effect.Origin, effect.Direction, margin,
                effect.Earliest, effect.Latest, out var hazard)) return false;
            if (effect.Action == 39243 && effect.IsLocalTarget) continue;
            hazards.Add(hazard);
        }
        if (knockback.HasValue != sawKnockback) return false;
        // The arena is a 30-yalm square. The inset must cover the caller's
        // validated position and arena-boundary uncertainty.
        route = AlexandriaGridPlanner.Plan(player, new Vector2(-759, -648), 15, 15, margin, spacing,
            times, hazards.ToArray(), requirements, delay, minimumSpeed, maximumSpeed, cancellation, knockback);
        return route.Length != 0;
    }
}

// A line aimed at the local player must not pass through another party member.
// The caller refreshes party positions and checks that the destination can be
// reached before the line's earliest possible effect.
internal static class AlexandriaEliminatorProtean
{
    internal static bool Safe(Vector2 source, Vector2 candidate, Vector2[] party)
    {
        if (!AlexandriaGeometry.Finite(source) || !AlexandriaGeometry.Finite(candidate) || party == null || party.Length == 0)
            return false;
        var delta = candidate - source;
        if (delta.LengthSquared() < .25f) return false;
        var direction = Vector2.Normalize(delta);
        if (!AlexandriaMechanicShapes.TryCreate(36783, source, direction, .5, 0, 1, out var line)) return false;
        foreach (var member in party)
            if (!AlexandriaGeometry.Finite(member) || AlexandriaGeometry.Distance(member, line) <= line.Margin) return false;
        return true;
    }
}

// Detached final-boss cast ledger. Keeping source and action together prevents
// a reused helper from silently replacing another still-unresolved effect.
// It records only observed signals; future baits and party constraints remain
// the caller's responsibility.
internal sealed class AlexandriaEliminatorTimeline
{
    internal readonly record struct Cast(uint Source, uint Action, Vector2 Origin, Vector2 Direction,
        double Earliest, double Latest, bool IsLocalTarget);

    private readonly Dictionary<(uint Source, uint Action), Cast> casts = [];
    private readonly HashSet<(uint Source, uint Action)> overdue = [];
    internal bool Incomplete { get; private set; }

    internal void MarkIncomplete() => Incomplete = true;

    internal void Reset()
    {
        casts.Clear(); overdue.Clear(); Incomplete = false;
    }

    internal void Observe(Cast cast, bool remainingIsZero = false)
    {
        if (cast.Source == 0 || cast.Action == 0 || !AlexandriaGeometry.Finite(cast.Origin) ||
            !AlexandriaGeometry.Finite(cast.Direction) || !double.IsFinite(cast.Earliest) ||
            !double.IsFinite(cast.Latest) || cast.Earliest > cast.Latest)
        { MarkIncomplete(); return; }
        var key = (cast.Source, cast.Action);
        if (overdue.Contains(key)) return;
        if (casts.TryGetValue(key, out var previous))
        {
            if (Vector2.Distance(previous.Origin, cast.Origin) > .25f ||
                Vector2.Distance(previous.Direction, cast.Direction) > .01f || previous.IsLocalTarget != cast.IsLocalTarget)
            { MarkIncomplete(); return; }
            cast = remainingIsZero ? previous : cast with
            { Earliest = Math.Min(previous.Earliest, cast.Earliest), Latest = Math.Max(previous.Latest, cast.Latest) };
        }
        else if (remainingIsZero) { MarkIncomplete(); return; }
        casts[key] = cast;
    }

    // A timer alone cannot retire a helper that remains visibly casting: the
    // wrapper might be stale or the same identity might have been reused.
    internal void Reconcile(double now, IEnumerable<(uint Source, uint Action)> active)
    {
        if (!double.IsFinite(now) || active == null) { MarkIncomplete(); return; }
        var present = active.ToHashSet();
        foreach (var entry in casts.Where(pair => pair.Value.Latest < now).ToArray())
        {
            if (present.Contains(entry.Key)) overdue.Add(entry.Key);
            else { casts.Remove(entry.Key); overdue.Remove(entry.Key); }
        }
    }

    internal bool HasIncompleteEvidence(double now) => !double.IsFinite(now) || Incomplete || overdue.Count != 0;

    internal AlexandriaEliminatorPlanner.Effect[] Snapshot(double now)
    {
        if (!double.IsFinite(now)) { MarkIncomplete(); return []; }
        return casts.Values.Where(cast => cast.Latest >= now).Select(cast =>
            new AlexandriaEliminatorPlanner.Effect(cast.Action, cast.Origin, cast.Direction,
                cast.Earliest - now, cast.Latest - now, cast.IsLocalTarget)).ToArray();
    }
}
