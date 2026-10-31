using System;
using System.Linq;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

public sealed partial class Unmaking
{
    // These are planner stages, not separate movement controllers. Only UpdatePosition
    // submits the selected destination to the existing mover/provider owner.
    private enum Request { None, Holy, Shelter, LineStack, HealingRange, NaughtRecovery, BeaconApproach }
    // Independent leases prevent an expired Holy from releasing a newer line stack.
    private readonly CapabilityManagerHandle holyHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle shelterHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle lineHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle healingRangeHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle naughtRecoveryHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle beaconApproachHandle = CapabilityManager.CreateNewHandle();
    private static readonly CapabilityFlags[] PositionFlags =
        [CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser];
    private Request positionRequest;
    private uint lineTarget;
    private Point? position;
    private DateTime naughtStallSince;
    private Point naughtStallPosition;
    private Point? failedEscapeBend;
    private Point failedEscapeEnd;
    private DateTime nextFailedEscapePlan;
    private DateTime nextEscapeCastInterrupt;

    private void InterruptCastForEscape(DateTime now)
    {
        if (movement?.IsCurrent != true || !Core.Me.IsCasting || MovementManager.ForceMoved ||
            !AvoidanceManager.IsRunningOutOfAvoid || Safe(Xz(Core.Me.Location)) || now < nextEscapeCastInterrupt)
            return;

        // At the Beacon, a hard-cast Raise held WHM inside Nothingness for four
        // seconds while service navigation returned Moved without issuing input.
        // Cancel only a cast in an active, locally confirmed escape. Do not bypass
        // movement locks: native navigation resumes when the cast releases them.
        nextEscapeCastInterrupt = now.AddMilliseconds(250);
        ActionManager.StopCasting();
    }

    private CapabilityManagerHandle Handle(Request request) => request switch
    {
        Request.Holy => holyHandle,
        Request.Shelter => shelterHandle,
        Request.HealingRange => healingRangeHandle,
        Request.NaughtRecovery => naughtRecoveryHandle,
        Request.BeaconApproach => beaconApproachHandle,
        _ => lineHandle
    };

    private void UpdatePosition(DateTime now)
    {
        travelling = false;
        if (movement == null || !movement.IsCurrent || MovementManager.GenerallyOutOfControl)
        {
            naughtStallSince = default;
            ReleasePosition();
            return;
        }
        if (TryRecoverNaught(now)) return;
        // First-resolving semantic requirement wins. Native hazards are already unioned
        // and always preempt it. No component-registration order selects the destination.
        var pending = casts.Values.Where(c => c.Action is Holy or Lightless or Dimension)
            .OrderBy(c => c.Finish).ThenBy(c => c.Action == Lightless ? 0 : 1).FirstOrDefault();
        if (pending == null)
        {
            if (UpdateBeaconApproach()) return;
            UpdateHealingRange();
            return;
        }
        Request next = pending.Action == Lightless ? Request.Shelter : pending.Action == Holy ? Request.Holy : Request.LineStack;
        Point? destination = next switch
        {
            Request.Shelter => ShelterPoint(),
            Request.Holy => HolyPoint(pending.Target),
            _ => LinePoint()
        };
        if (destination == null || !SafeApproach(destination.Value))
        {
            ReleasePosition();
            Warn($"{next} has no verified safe participation/approach; native escape and routine remain available.");
            return;
        }
        if (next != positionRequest) ReleasePosition();
        positionRequest = next;
        position = destination;
        foreach (var flag in PositionFlags)
            CapabilityManager.Update(Handle(next), flag, 1000, $"Unmaking {next} participation");
        // The pulse uses the same owner during routine awaits. Once stationary it returns
        // control; no job actions, cooldown selection or replacement combat hook is added.
        movement.Request(World(destination.Value), .4f);
        travelling = Point.Distance(Xz(Core.Me.Location), destination.Value) > .4f && !AvoidanceManager.IsRunningOutOfAvoid;
    }

    private bool TryRecoverNaught(DateTime now)
    {
        var player = Xz(Core.Me.Location);
        var circle = adds || AvoidanceManager.IsRunningOutOfAvoid ? null : casts.Values.FirstOrDefault(c =>
            c.Action == GrowsLarge && !FinishMeltdownFirst(c) &&
            Point.Distance(player, Xz(c.Origin)) <= 40 + UnmakingGeometry.Margin);
        if (circle == null)
        {
            naughtStallSince = default;
            if (positionRequest == Request.NaughtRecovery) ReleasePosition();
            return false;
        }

        // Both WHM captures remained stationary through Naught Grows with repeated
        // native no-path failures; initial/pull collision rebuilds did not repair it.
        // Give native planning 0.75s, and intervene only for this large portal circle
        // on the verified main floor. Ordinary displacement restarts that grace period.
        if (positionRequest != Request.NaughtRecovery)
        {
            if (naughtStallSince == default || Point.Distance(player, naughtStallPosition) > .5f)
            {
                naughtStallSince = now;
                naughtStallPosition = player;
                return false;
            }
            if ((now - naughtStallSince).TotalSeconds < .75) return false;
        }
        var away = player - Xz(circle.Origin);
        if (away.LengthSquared() < 1) return false;
        // 40y radius +0.5y hazard margin +0.6y arrival clearance. The radial exit is
        // the shortest departure; never extend it through another hazard or off floor.
        var destination = positionRequest == Request.NaughtRecovery && position.HasValue ? position.Value :
            Xz(circle.Origin) + Point.Normalize(away) * 41.1f;
        string departing = $"cast/{circle.Caster}/{circle.Action}";
        bool clear = Safe(destination);
        int steps = Math.Max(1, (int)Math.Ceiling(Point.Distance(player, destination) * 2));
        for (int i = 0; clear && i <= steps; i++)
        {
            var p = player + (destination - player) * (i / (float)steps);
            clear = UnmakingGeometry.InArena(p, false) && !hazards.Any(pair => pair.Key != departing &&
                UnmakingGeometry.InPolygon(UnmakingGeometry.Local(p, Xz(pair.Value.Origin), pair.Value.Heading), pair.Value.MathPolygon));
        }
        if (!clear)
        {
            if (positionRequest == Request.NaughtRecovery) ReleasePosition();
            return false;
        }
        if (positionRequest != Request.NaughtRecovery)
        {
            ReleasePosition();
            Logger.Information($"[Unmaking] Recovering stalled Naught Grows departure to {destination}.");
        }
        positionRequest = Request.NaughtRecovery;
        position = destination;
        foreach (var flag in PositionFlags)
            CapabilityManager.Update(naughtRecoveryHandle, flag, 1000, "Unmaking stalled portal escape");
        movement.RequestHazardExit(World(destination), .4f);
        travelling = Point.Distance(player, destination) > .4f;
        return true;
    }

    private static bool IsHealer => Core.Me.CurrentJob is ff14bot.Enums.ClassJobType.WhiteMage
        or ff14bot.Enums.ClassJobType.Scholar or ff14bot.Enums.ClassJobType.Astrologian or ff14bot.Enums.ClassJobType.Sage;

    private bool CanContinueNativeEscape()
    {
        if (adds) return true;
        // At 17:23:17 native Chase escape retained forward input during path retries,
        // crossing the circular edge despite on-floor waypoints. Check actual facing,
        // not the requested waypoint: they disagreed in the capture. A 1.5y lookahead
        // gives 0.25s at running speed, in addition to the existing 0.5y floor margin.
        // This only releases the existing owner's input; native planning remains active.
        var forward = new Point((float)Math.Sin(Core.Me.Heading), (float)Math.Cos(Core.Me.Heading));
        return UnmakingGeometry.InArena(Xz(Core.Me.Location) + forward * 1.5f, false);
    }

    private bool CanUseFailedEscape(Clio.Utilities.Vector3 destination)
        => FailedEscapeSegment(destination, null);

    private Clio.Utilities.Vector3? FailedEscapeWaypoint(Clio.Utilities.Vector3 destination)
    {
        if (CanUseFailedEscape(destination))
        {
            failedEscapeBend = null;
            return destination;
        }
        if (!active || !adds || !Core.Me.IsAlive || !AvoidanceManager.IsRunningOutOfAvoid) return null;
        var end = Xz(destination);
        if (failedEscapeBend.HasValue && Point.Distance(failedEscapeEnd, end) <= 1 &&
            FailedEscapeSegment(destination, failedEscapeBend)) return World(failedEscapeBend.Value);
        failedEscapeBend = null;
        // Failed searches must not consume every bot pulse. Valid cached segments and
        // newly clear direct paths are still checked immediately above.
        if (DateTime.UtcNow < nextFailedEscapePlan) return null;
        nextFailedEscapePlan = DateTime.UtcNow.AddMilliseconds(250);
        // At 15:56:17 the native endpoint was safe but its direct segment crossed an
        // Empty Shadow. Try one bend around the observed 10y circles, with 0.5y hazard
        // margin and 1y corner clearance. Both legs must be verified on the add floor;
        // keep the native endpoint and reuse a valid bend instead of oscillating.
        var player = Xz(Core.Me.Location);
        var candidates = casts.Values.Where(c => c.Action == Empty).SelectMany(c =>
            Enumerable.Range(0, 16).Select(i => Xz(c.Origin) +
                new Point(MathF.Sin(i * MathF.Tau / 16), MathF.Cos(i * MathF.Tau / 16)) * 11.5f));
        foreach (var bend in candidates.OrderBy(p => Point.Distance(player, p) + Point.Distance(p, end)))
        {
            if (!FailedEscapeSegment(destination, bend)) continue;
            failedEscapeBend = bend;
            failedEscapeEnd = end;
            return World(bend);
        }
        return null;
    }

    private bool FailedEscapeSegment(Clio.Utilities.Vector3 destination, Point? bend)
    {
        // The add transition can produce a valid native endpoint while service
        // navigation repeatedly fails to issue movement.
        // Permit only departure from that proximity circle on the confirmed add floor;
        // preserve the native destination and refuse any secondary hazard crossing.
        if (!active || !adds || !Core.Me.IsAlive || !AvoidanceManager.IsRunningOutOfAvoid) return false;
        // Captured floor and native endpoints differ by only 0.05y; another elevation
        // is not covered by the flat add-floor proof.
        if (Math.Abs(destination.Y - Core.Me.Y) > 1) return false;
        var player = Xz(Core.Me.Location);
        var circle = casts.Values.FirstOrDefault(c => c.Action == Proximity &&
            Point.Distance(player, Xz(c.Origin)) <= 42 + UnmakingGeometry.Margin);
        if (circle == null) return false;
        var end = Xz(destination);
        if (!Safe(end)) return false;
        string departing = $"cast/{circle.Caster}/{circle.Action}";
        bool Clear(Point from, Point to)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(Point.Distance(from, to) * 2));
            for (int i = 0; i <= steps; i++)
            {
                var p = from + (to - from) * (i / (float)steps);
                if (!UnmakingGeometry.InArena(p, adds, beacon) || hazards.Any(pair => pair.Key != departing &&
                    UnmakingGeometry.InPolygon(UnmakingGeometry.Local(p, Xz(pair.Value.Origin), pair.Value.Heading), pair.Value.MathPolygon))) return false;
            }
            return true;
        }
        return bend.HasValue ? Clear(player, bend.Value) && Clear(bend.Value, end) : Clear(player, end);
    }

    private bool UpdateBeaconApproach()
    {
        var target = actors.FirstOrDefault(a => a.ObjectId == beaconId && a.BaseId == Beacon &&
            a.IsAlive && a.IsVisible && a.IsTargetable && a.CanAttack);
        if (!adds || !beacon.HasValue || target == null) return false;

        // The extended floor is walkable, but repeated Beacon Kill Target requests
        // failed service navigation while the party reached the crack. Healing-range
        // correction alone left WHM over 30y away. Advance toward the actual Beacon
        // through this owner; OrderBot still selects it and runs the combat routine.
        var inward = UnmakingGeometry.Center - beacon.Value;
        if (inward.LengthSquared() < 1) return false;
        var goal = beacon.Value + Point.Normalize(inward) * 6;
        var player = Xz(Core.Me.Location);
        var delta = goal - player;
        float distance = delta.Length();
        // Six yalms short of its center joins the group without standing inside the
        // object. Release on arrival so ordinary combat can make its final approach.
        if (distance <= 1) return false;

        // Check only the next second of travel, not a 90y segment through several
        // alternating beams. Each pulse revalidates it; a blocked segment waits for
        // its beam to clear, while native escape preempts travel from unsafe ground.
        var next = player + delta / distance * Math.Min(6, distance);
        if (Core.Me.IsCasting || AvoidanceManager.IsRunningOutOfAvoid || !SafeApproach(next))
        {
            ReleasePosition();
            return true;
        }
        if (positionRequest != Request.BeaconApproach) ReleasePosition();
        positionRequest = Request.BeaconApproach;
        position = next;
        foreach (var flag in PositionFlags)
            CapabilityManager.Update(beaconApproachHandle, flag, 1000, "Unmaking Beacon approach");
        movement.Request(World(next), .4f);
        travelling = true;
        return true;
    }

    private void UpdateHealingRange()
    {
        // Formation is lower priority than every mechanic. A marked healer must not
        // bring a chase, flare or spread back to the group, and no cast is interrupted
        // merely to improve coverage. Native escape always retains the input channel.
        if (!IsHealer || !Core.Me.InCombat || Core.Me.IsCasting || AvoidanceManager.IsRunningOutOfAvoid ||
            chased.Contains(Core.Me.ObjectId) || casts.Values.Any(c => c.Action is Flare or Spread && c.Target == Core.Me.ObjectId))
        {
            ReleasePosition();
            return;
        }
        var members = party.Where(p => !p.IsMe).Select(p => Xz(p.Location)).ToArray();
        var destination = UnmakingGeometry.HealingPosition(Xz(Core.Me.Location), members,
            positionRequest == Request.HealingRange ? position : null, SafeApproach);
        if (!destination.HasValue)
        {
            ReleasePosition();
            return;
        }
        if (positionRequest != Request.HealingRange) ReleasePosition();
        positionRequest = Request.HealingRange;
        position = destination;
        foreach (var flag in PositionFlags)
            CapabilityManager.Update(healingRangeHandle, flag, 1000, "Unmaking party healing range");
        movement.Request(World(destination.Value), .4f);
        travelling = Point.Distance(Xz(Core.Me.Location), destination.Value) > .4f;
    }

    private Point? ShelterPoint()
    {
        var zeros = actors.Where(a => a.BaseId == Zero && a.IsAlive && a.IsVisible).ToArray();
        if (zeros.Length != 1) return null;
        var p = UnmakingGeometry.Shelter(Xz(boss.Location), Xz(zeros[0].Location));
        return p.HasValue && Safe(p.Value) ? p : null;
    }

    private Point? HolyPoint(uint targetId)
    {
        // Chase and flare targets cannot serve as stack participants. An unresolved
        // self-target is not stationary success: find at least three other participants.
        var flares = casts.Values.Where(c => c.Action == Flare).Select(c => c.Target).ToHashSet();
        if (chased.Contains(Core.Me.ObjectId) || flares.Contains(Core.Me.ObjectId) || chased.Contains(targetId)) return null;
        var target = party.FirstOrDefault(p => p.ObjectId == targetId);
        if (target == null) return null;
        var members = party.Where(p => !chased.Contains(p.ObjectId) && !flares.Contains(p.ObjectId)).ToArray();
        var player = Xz(Core.Me.Location);
        var targetPoint = Xz(target.Location);
        var candidates = target.IsMe
            ? UnmakingGeometry.StackCandidates(player, members.Where(p => !p.IsMe).Select(p => Xz(p.Location)).ToArray())
            : new[] { player, targetPoint }.Concat(Enumerable.Range(0, 16).Select(i => targetPoint +
                new Point(MathF.Sin(i * MathF.Tau / 16), MathF.Cos(i * MathF.Tau / 16)) * 3));
        //5y participation leaves 1y for arrival tolerance, target movement and resolution.
        return candidates.Where(SafeApproach).Select(p => new
            {
                Point = p,
                Count = target.IsMe ? 1 + members.Count(m => !m.IsMe && Point.Distance(Xz(m.Location), p) <= 5)
                    : (Point.Distance(p, targetPoint) <= 5 ? 1 + members.Count(m => !m.IsMe && Point.Distance(Xz(m.Location), targetPoint) <= 5) : 0)
            })
            .Where(c => c.Count >= 4).OrderByDescending(c => c.Count)
            .ThenBy(c => positionRequest == Request.Holy && position.HasValue ? Point.Distance(c.Point, position.Value) : 0)
            .ThenBy(c => Point.Distance(c.Point, player)).Select(c => (Point?)c.Point).FirstOrDefault();
    }

    private Point? LinePoint()
    {
        var target = party.FirstOrDefault(p => p.ObjectId == lineTarget);
        if (chased.Contains(Core.Me.ObjectId)) return null;
        if (target == null) return UnmarkedLinePoint();
        if (chased.Contains(target.ObjectId)) return null;
        var origin = Xz(boss.Location);
        var player = Xz(Core.Me.Location);
        var members = party.Where(p => !chased.Contains(p.ObjectId)).Select(p => Xz(p.Location)).ToArray();
        var ray = Xz(target.Location) - origin;
        if (ray.LengthSquared() < 1) return null;
        ray = Point.Normalize(ray);
        // An unmarked player may join anywhere on the marked line; the carrier chooses
        // a line through the party. Do not substitute the boss's ordinary tank target.
        var candidates = target.IsMe ? UnmakingGeometry.StackCandidates(player, members) :
            new[] { player }.Concat(Enumerable.Range(2, 56).Select(d => origin + ray * d));
        return candidates.Where(SafeApproach).Select(p => new
            {
                Point = p,
                Count = UnmakingGeometry.LineParticipants(origin, target.IsMe ? p : Xz(target.Location),
                    party.Where(m => !m.IsMe && !chased.Contains(m.ObjectId)).Select(m => Xz(m.Location)).Append(p)),
                Inside = target.IsMe || UnmakingGeometry.LineParticipants(origin, Xz(target.Location), new[] { p }) == 1
            }).Where(c => c.Inside && c.Count >= 4).OrderByDescending(c => c.Count)
            .ThenBy(c => positionRequest == Request.LineStack && position.HasValue ? Point.Distance(c.Point, position.Value) : 0)
            .ThenBy(c => Point.Distance(c.Point, player)).Select(c => (Point?)c.Point).FirstOrDefault();
    }

    private Point? UnmarkedLinePoint()
    {
        // RB's boss VFX does not expose the line icon's target. During the confirmed
        // line-stack cast, a compact group of at least four allies supplies meaningful
        // participation without pretending the ordinary tank target is the carrier.
        // Refuse split/isolated groups, unsafe approaches and chased participants.
        // This is stack participation, not a general-purpose party-follow rule.
        var origin = Xz(boss.Location);
        var player = Xz(Core.Me.Location);
        var members = party.Where(p => !p.IsMe && !chased.Contains(p.ObjectId))
            .Select(p => Xz(p.Location)).ToArray();
        return UnmakingGeometry.StackCandidates(player, members).Where(SafeApproach)
            .Select(p => new { Point = p, Count = members.Count(m => Point.Distance(m, p) <= 5 &&
                UnmakingGeometry.LineParticipants(origin, p, new[] { m }) == 1) })
            .Where(c => c.Count >= 4).OrderByDescending(c => c.Count)
            .ThenBy(c => positionRequest == Request.LineStack && position.HasValue ? Point.Distance(c.Point, position.Value) : 0)
            .ThenBy(c => Point.Distance(c.Point, player)).Select(c => (Point?)c.Point).FirstOrDefault();
    }

    private void ReleasePosition()
    {
        travelling = false;
        if (positionRequest == Request.None) return;
        var previous = positionRequest;
        positionRequest = Request.None;
        foreach (var flag in PositionFlags) CapabilityManager.Clear(Handle(previous), flag, "Unmaking participation released");
        movement?.Request(null, 0);
        position = null;
    }
}
