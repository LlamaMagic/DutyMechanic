using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class DohnMheg
{
    // Helpers share NPC 8141 with the boss; base 0x3F2 identifies the real boss.
    // Only that living actor can retain stack and movement ownership.
    private const uint LingeringGazeBossBase = 0x3F2;
    private static DohnMheg activeDungeon;
    private readonly CapabilityManagerHandle firstBossHandle = CapabilityManager.CreateNewHandle();
    private ChrysalisMovement firstBossMover;
    private object firstBossOriginalProvider;
    private bool firstBossUnsupportedReported;
    private uint stackTarget, lastStackMarker;
    private DateTime stackUntil, stackMarkerGoneAt, nextStackSnapshot;
    private Vector3? stackDestination;
    private bool stackLeased, gapCloserLeased;

    // Pulse ownership keeps warning discovery and input cleanup alive while the
    // combat coroutine yields. Only this first-boss arena can acquire the mover.
    internal static void CheckLifecycle()
    {
        if (activeDungeon == null)
            return;
        activeDungeon.UpdateBossSideStep();
        activeDungeon.UpdateFirstBoss();
        activeDungeon.UpdateGriaule();
        activeDungeon.UpdateFinalBoss();
    }

    internal static void ResetActive() => activeDungeon?.ResetDungeonState();

    private static bool FirstBossCombat() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene &&
        WorldManager.ZoneId == (uint)Data.ZoneId.DohnMheg &&
        WorldManager.SubZoneId == (uint)SubZoneId.TeagGye &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat &&
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(LordOfLingeringGaze)
            .Any(a => a.IsValid && a.BaseId == LingeringGazeBossBase && a.IsAlive);

    private void UpdateFirstBoss()
    {
        if (!FirstBossCombat())
        {
            ResetFirstBoss();
            return;
        }
        UpdateGeysers();
        if (firstBossMover != null && !firstBossMover.IsCurrent)
        {
            ReleaseStackPosition();
            firstBossMover.Detach();
            firstBossMover = null;
        }
        if (firstBossMover == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover) &&
            (firstBossOriginalProvider == null || ReferenceEquals(firstBossOriginalProvider, Navigator.NavigationProvider)))
        {
            // Reacquire only after a replacement returns the original platform;
            // stale wrappers cannot take input back from a foreign provider.
            firstBossOriginalProvider ??= Navigator.NavigationProvider;
            firstBossMover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: FirstBossSegmentSafe);
            Navigator.PlayerMover = firstBossMover;
            firstBossUnsupportedReported = false;
        }
        if (firstBossMover?.IsCurrent != true)
        {
            ReleaseStackPosition();
            if (!firstBossUnsupportedReported)
                Logger.Information("[Dohn Stack] Foreign movement owner; geyser detection continues, stack positioning waits for platform handoff.");
            firstBossUnsupportedReported = true;
            return;
        }

        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Append(Core.Me).Where(a => a.IsValid && a.IsAlive && a.IsVisible &&
                (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var marked = party.FirstOrDefault(a => a.VfxContainer.IsValid &&
            a.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 62));
        var now = DateTime.UtcNow;
        if (marked == null)
        {
            // Disappearance is not effect completion. Keep a bounded effect
            // tail rather than allowing routine travel at the visual reset.
            if (now >= stackUntil && party.All(a => a.VfxContainer.IsValid))
                lastStackMarker = 0;
        }
        else if (marked.ObjectId != lastStackMarker)
        {
            lastStackMarker = stackTarget = marked.ObjectId;
            // The live lock-on can precede damage by 6.44s. Keep an 8s maximum
            // for missed removal, or a 0.8s tail after confirmed marker removal.
            stackUntil = now.AddSeconds(8);
            stackMarkerGoneAt = DateTime.MinValue;
            stackDestination = null;
            Logger.Information($"[Dohn Stack] marker=62 target=0x{stackTarget:X} self={stackTarget == Core.Me.ObjectId} until={stackUntil:O}");
        }

        var carrier = party.FirstOrDefault(a => a.ObjectId == stackTarget);
        if (carrier != null && carrier.VfxContainer.IsValid)
        {
            bool markerPresent = carrier.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 62);
            if (markerPresent)
                stackMarkerGoneAt = DateTime.MinValue;
            else if (stackMarkerGoneAt == DateTime.MinValue)
            {
                stackMarkerGoneAt = now;
                Logger.Information($"[Dohn Stack] marker cleared; retain effect tail target=0x{stackTarget:X}");
            }
        }
        if (stackTarget == 0 || now >= stackUntil ||
            stackMarkerGoneAt != DateTime.MinValue && now >= stackMarkerGoneAt.AddSeconds(.8))
        {
            ReleaseStackPosition();
            stackTarget = 0;
        }
        else if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            // Earlier ground damage owns egress; retain the stack's identity so
            // participation can be reacquired when that urgent escape finishes.
            ReleaseStackPosition();
        }
        else
        {
            PositionForStack(party);
        }

        // Gap closers can bypass ordinary-waypoint admission. Suppress them only
        // while this encounter has pending floor hazards or positive positioning.
        if (stackTarget != 0 || geyserAvoids.Count > 0 ||
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsCasting &&
                a.CastingSpellId is Hydrofall or LaughingLeapGround))
        {
            CapabilityManager.Update(firstBossHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Dohn first-boss hazard/stack");
            gapCloserLeased = true;
        }
        else if (gapCloserLeased)
        {
            CapabilityManager.Clear(firstBossHandle, CapabilityFlags.GapCloser, "Dohn floor clear");
            gapCloserLeased = false;
        }
        firstBossMover.Update();
    }

    private bool FirstBossPointSafe(Vector3 point) =>
        point.Distance2D(LordOfLingeringGazeArenaCenter) <= 19f &&
        !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point));

    private bool FirstBossSegmentSafe(Vector3 destination)
    {
        var start = Core.Me.Location;
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(destination) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = (float)i / steps;
            if (!FirstBossPointSafe(new Vector3(start.X + (destination.X - start.X) * t,
                    start.Y, start.Z + (destination.Z - start.Z) * t)))
                return false;
        }
        return true;
    }

    private void PositionForStack(BattleCharacter[] party)
    {
        var carrier = party.FirstOrDefault(a => a.ObjectId == stackTarget);
        if (carrier == null)
        {
            ReleaseStackPosition();
            return;
        }
        bool self = carrier.ObjectId == Core.Me.ObjectId;
        var participants = party.Where(a => a.ObjectId != Core.Me.ObjectId).Select(a => a.Location).ToArray();
        if (self && participants.Length == 0)
        {
            ReleaseStackPosition();
            return;
        }

        // Laughing Leap shares within 4y. Target 3y coverage with 0.5y arrival
        // tolerance, preserving another half-yalm instead of holding at the edge.
        int Coverage(Vector3 point) => self ? participants.Count(p => p.Distance2D(point) <= 3f) :
            point.Distance2D(carrier.Location) <= 3f ? 1 : 0;
        bool Safe(Vector3 point) => FirstBossPointSafe(point) &&
            geyserAvoids.All(w => DateTime.UtcNow >= w.Expires ||
                w.Discovered.AddSeconds(5.1) > stackUntil || point.Distance2D(w.Center) > 6.5f);

        var candidates = new List<Vector3> { Core.Me.Location };
        var centers = self ? participants : new[] { carrier.Location };
        if (self)
            candidates.Add(new Vector3(centers.Average(p => p.X), Core.Me.Y, centers.Average(p => p.Z)));
        foreach (var center in centers)
        {
            candidates.Add(center);
            for (int i = 0; i < 16; ++i)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new Vector3(center.X + 2.5f * (float)Math.Sin(angle), Core.Me.Y,
                    center.Z + 2.5f * (float)Math.Cos(angle)));
            }
        }
        var best = candidates.Where(p => Coverage(p) > 0 && Safe(p) && FirstBossSegmentSafe(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (!best.HasValue)
        {
            ReleaseStackPosition();
            return;
        }
        if (!stackDestination.HasValue || !Safe(stackDestination.Value) || !FirstBossSegmentSafe(stackDestination.Value) ||
            Coverage(stackDestination.Value) < Coverage(best.Value))
        {
            stackDestination = best;
            Logger.Information($"[Dohn Stack] destination={best.Value} coverage={Coverage(best.Value)} self={self}");
        }
        CapabilityManager.Update(firstBossHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Laughing Leap stack participation");
        stackLeased = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(stackDestination.Value) > .5f)
            ActionManager.StopCasting();
        firstBossMover.Request(stackDestination, .5f);
        if (LoggingHelpers.MechanicDiagnosticsEnabled && DateTime.UtcNow >= nextStackSnapshot)
        {
            nextStackSnapshot = DateTime.UtcNow.AddMilliseconds(250);
            Logger.Information($"[Dohn Stack] position={Core.Me.Location} target=0x{stackTarget:X} self={self} coverageNow={Coverage(Core.Me.Location)} participants={party.Count(a => a.Location.Distance2D(carrier.Location) <= 4f)}/{party.Length} party=" +
                string.Join(";", party.Select(a => $"{a.Name}:{a.Location}")));
        }
    }

    private void ReleaseStackPosition()
    {
        if (stackLeased && firstBossMover?.IsCurrent == true)
            firstBossMover.Request(null, 0);
        if (stackLeased)
            CapabilityManager.Clear(firstBossHandle, CapabilityFlags.Movement, "Laughing Leap stack released");
        stackLeased = false;
        stackDestination = null;
    }

    private void ResetFirstBoss()
    {
        // Pulses continue through all later rooms. Release once on the lifecycle
        // edge, rather than issuing/logging an empty capability clear every frame.
        if (firstBossMover == null && firstBossOriginalProvider == null && !stackLeased && !gapCloserLeased && stackTarget == 0 &&
            lastStackMarker == 0 && geyserStates.Count == 0 && geyserAvoids.Count == 0)
            return;
        ReleaseStackPosition();
        firstBossMover?.Detach();
        firstBossMover = null;
        firstBossOriginalProvider = null;
        firstBossUnsupportedReported = false;
        CapabilityManager.Clear(firstBossHandle, reason: "Dohn first-boss lifecycle ended");
        gapCloserLeased = false;
        stackTarget = lastStackMarker = 0;
        stackUntil = stackMarkerGoneAt = DateTime.MinValue;
        ResetGeysers();
    }
}
