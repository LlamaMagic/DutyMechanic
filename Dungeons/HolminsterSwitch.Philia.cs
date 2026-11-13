using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Behavior;
using TreeSharp;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;

public partial class HolminsterSwitch
{
    private static readonly Vector3 PhiliaCenter = new(134f, 23f, -465f);
    private readonly CapabilityManagerHandle pendulumHandle = CapabilityManager.CreateNewHandle();
    private uint pendulumTarget;
    private DateTime pendulumUntil;
    private Vector3 pendulumFlare;
    private Vector3? pendulumDestination;
    private bool pendulumLeased;
    private bool pendulumMarkerSeen;
    private readonly CapabilityManagerHandle fierceHandle = CapabilityManager.CreateNewHandle();
    private readonly HashSet<int> fierceHeadings = new();
    private DateTime fierceUntil;
    private DateTime fierceFirstConeAt;
    private DateTime fiercePhaseAt;
    private DateTime nextFierceEntrySearch;
    private float fierceHeading;
    private int fierceSide;
    private bool fierceLeased;
    private Composite fierceCombatGate;
    private bool fierceFinalSeen;
    private Vector3? fierceDestination;
    private Vector3? fierceWaypoint;
    private KnoutWindow knoutWindow;
    private KnoutWindow knoutEscapeWindow;
    private bool knoutEscapeOwned;
    private Vector3? knoutEscapeDestination;
    private readonly CapabilityManagerHandle knoutEscapeHandle = CapabilityManager.CreateNewHandle();
    private KnoutWindow aethersupWindow;
    private readonly List<KnoutWindow> fierceConeWindows = new();
    private readonly Dictionary<uint, KnoutWindow> fierceCircleWindows = new();
    private DateTime knoutObservedAt;
    private readonly CapabilityManagerHandle lightStackHandle = CapabilityManager.CreateNewHandle();
    private DateTime lightStackUntil;
    private Vector3 lightStackOrigin;
    private Vector3? lightStackFallback;
    private DateTime lightStackStarted, lightPairSince;
    private uint lightPairA, lightPairB;
    private Vector3 lightPairPoint;
    private bool lightStackLeased;

    private sealed class KnoutWindow
    {
        internal uint Action;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Until;
    }

    private static readonly Vector2[] KnoutPoints = BuildKnoutPoints();
    private static readonly Vector2[] AethersupPoints = BuildAethersupPoints();
    private static readonly Vector2[] FierceConePoints = BuildFierceConePoints();
    private static readonly Vector2[] FierceCirclePoints = BuildFierceCirclePoints();
    private static Vector2[] BuildFierceCirclePoints()
    {
        var points = new List<Vector2>();
        for (int i = 0; i < 32; i++)
        {
            double angle = 2 * Math.PI * i / 32;
            points.Add(new Vector2(4.55f * (float)Math.Sin(angle), 4.55f * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }
    private static Vector2[] BuildFierceConePoints()
    {
        var points = new List<Vector2> { new(0, 0) };
        for (int i = 0; i <= 24; i++)
        {
            double angle = (-61 + 122.0 * i / 24) * Math.PI / 180;
            points.Add(new Vector2(25.5f * (float)Math.Sin(angle), 25.5f * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }
    private static Vector2[] BuildAethersupPoints()
    {
        var points = new List<Vector2> { new(0, 0) };
        for (int i = 0; i <= 24; i++)
        {
            double angle = (-61 + 122.0 * i / 24) * Math.PI / 180;
            points.Add(new Vector2(24.5f * (float)Math.Sin(angle), 24.5f * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }
    private static Vector2[] BuildKnoutPoints()
    {
        var points = new List<Vector2> { new(0, 0) };
        for (int i = 0; i <= 32; i++)
        {
            double angle = (-106 + 212.0 * i / 32) * Math.PI / 180;
            points.Add(new Vector2(24.5f * (float)Math.Sin(angle), 24.5f * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }

    private static bool PhiliaCombat() => TreeRoot.IsRunning && DutyManager.InInstance &&
        WorldManager.ZoneId == (uint)ZoneId.HolminsterSwitch &&
        WorldManager.SubZoneId == (uint)SubZoneId.TheManorHouseCourtyard &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.BaseId == 0x278C);

    private void RegisterPhiliaAvoids()
    {
        // Left and right Knout use mirrored omen axes. Snapshot the axis and
        // retain it through the hit, which can follow the cast bar.
        AvoidanceManager.AddAvoidPolygon<KnoutWindow>(PhiliaCombat,
            leashPointProducer: () => PhiliaCenter, leashRadius: 50f,
            rotationProducer: cone => -cone.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => KnoutPoints, locationProducer: cone => cone.Origin,
            collectionProducer: () => knoutWindow != null && DateTime.UtcNow < knoutWindow.Until
                ? new[] { knoutWindow } : Array.Empty<KnoutWindow>(), priority: AvoidancePriority.High);
        // The fan polygon missed a player 0.22y from its apex. Cover that
        // origin gap for the same Knout window.
        AvoidanceManager.AddAvoidLocation(
            canRun: () => PhiliaCombat() && knoutWindow != null && DateTime.UtcNow < knoutWindow.Until,
            radius: 2f, locationProducer: () => knoutWindow?.Origin ?? PhiliaCenter);
        // Let the party destroy the chain, then avoid the captured forward
        // omen through the observed delayed Aethersup hit.
        AvoidanceManager.AddAvoidPolygon<KnoutWindow>(PhiliaCombat,
            leashPointProducer: () => PhiliaCenter, leashRadius: 50f,
            rotationProducer: cone => -cone.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => AethersupPoints, locationProducer: cone => cone.Origin,
            collectionProducer: () => aethersupWindow != null && aethersupWindow.Action == 15848 && DateTime.UtcNow < aethersupWindow.Until
                ? new[] { aethersupWindow } : Array.Empty<KnoutWindow>(), priority: AvoidancePriority.High);
        // Fierce Beating combines an authoritative center sludge actor, two
        // 4y circles and a rotating 120-degree rear helper cone. Native hazards
        // remain able to preempt the encounter's ordered positioning.
        AvoidanceManager.AddAvoidObject<GameObject>(canRun: PhiliaCombat,
            objectSelector: actor => actor.IsValid && actor.BaseId == 0x1EABFA && actor.IsVisible, radiusProducer: _ => 10.3f);
        // The first circles hit after their cast bars disappeared in the
        // captured resolution; retain their positions through that tail.
        AvoidanceManager.AddAvoidPolygon<KnoutWindow>(PhiliaCombat,
            leashPointProducer: () => PhiliaCenter, leashRadius: 50f,
            rotationProducer: _ => 0f, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => FierceCirclePoints, locationProducer: circle => circle.Origin,
            collectionProducer: () => fierceCircleWindows.Values.Where(circle => DateTime.UtcNow < circle.Until).ToArray(),
            priority: AvoidancePriority.High);
        // While the encounter owns a validated timed transition, publishing
        // the next cone as immediately active would preempt that transition.
        // A rejected or expired transition releases the lease and restores it.
        AvoidanceManager.AddAvoidPolygon<KnoutWindow>(PhiliaCombat,
            leashPointProducer: () => PhiliaCenter, leashRadius: 50f,
            rotationProducer: cone => -cone.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => FierceConePoints, locationProducer: cone => cone.Origin,
            // A previous phase remains authoritative even if a new route is owned.
            collectionProducer: () => fierceConeWindows.Where(cone => DateTime.UtcNow < cone.Until &&
                (!fierceLeased || !ReferenceEquals(cone, fierceConeWindows.LastOrDefault()))).ToArray(),
            priority: AvoidancePriority.High);
    }

    private void UpdatePhilia()
    {
        DateTime now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsAlive).ToArray();
        ObserveKnout(actors, now);
        ObserveAethersup(actors, now);
        var boss = actors.FirstOrDefault(actor => Cast(actor, 0x278C, 16777));
        var party = actors.Append(Core.Me).Where(actor => actor.IsValid && actor.IsAlive && actor.IsVisible &&
            (actor.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)actor.NpcId)))
            .GroupBy(actor => actor.ObjectId).Select(group => group.First()).ToArray();
        ObserveFierce(actors, now);

        var light = actors.FirstOrDefault(actor => Cast(actor, 0x278C, 17232));
        if (light != null && lightStackUntil == default)
        {
            // One capture included the boss origin, but another placed the
            // player just behind it and missed. Track a small forward offset
            // so the player remains inside the directed line's near end.
            lightStackOrigin = light.Location;
            lightStackFallback = null;
            lightStackStarted = now;
            lightPairSince = default;
            lightStackUntil = now + light.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2.5);
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Line stack origin={0}, holdUntil={1:O}.", lightStackOrigin, lightStackUntil);
        }
        if (lightStackUntil != default && now >= lightStackUntil) ReleaseLightStack();

        if (boss != null && pendulumUntil == default)
        {
            // The captured flare hit followed the bar by ~0.9s, then the
            // center hit followed about 2s later. Hold through that sequence.
            pendulumUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(4);
            pendulumTarget = 0;
            pendulumMarkerSeen = false;
            pendulumDestination = null;
        }
        if (UpdateKnoutEscape(now)) return;
        if (pendulumUntil == default)
        {
            if (lightStackUntil != default && fierceUntil == default)
            {
                UpdateLightStack(actors.FirstOrDefault(actor => actor.BaseId == 0x278C), party);
                return;
            }
            ReleaseLightStackPosition();
            UpdateFierce(now); return;
        }
        ReleaseLightStackPosition();
        if (now >= pendulumUntil) { ReleasePendulum(); UpdateFierce(now); return; }
        ReleaseFiercePosition();
        var centerCast = actors.FirstOrDefault(actor => Cast(actor, 0x233C, 16778));
        if (centerCast != null)
        {
            var centerEnd = now + centerCast.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
            if (centerEnd > pendulumUntil) pendulumUntil = centerEnd;
        }
        var marked = party.FirstOrDefault(actor => actor.VfxContainer.IsValid &&
            actor.VfxContainer.LockOns.Any(icon => icon != null && icon.IsValid && icon.Id == 87));
        if (marked != null && (pendulumTarget == 0 || marked.ObjectId == pendulumTarget))
        {
            pendulumTarget = marked.ObjectId;
            pendulumFlare = marked.Location;
            pendulumMarkerSeen = true;
        }
        // Keep an acquired stationary hold while casting; forced input still
        // takes priority and the destination is revalidated afterward.
        if (pendulumLeased && pendulumDestination.HasValue && bossMover?.IsCurrent == true &&
            MovementManager.GenerallyOutOfControl && Core.Me.IsCasting && !AvoidanceManager.IsRunningOutOfAvoid &&
            Core.Me.Location.Distance2D(pendulumDestination.Value) <= 0.5f)
        {
            CapabilityManager.Update(pendulumHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Pendulum hold");
            CapabilityManager.Update(pendulumHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Pendulum hold");
            return;
        }
        // Unknown target is not evidence that any point avoids the flare.
        if (!pendulumMarkerSeen || pendulumTarget == 0 || bossMover?.IsCurrent != true ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleasePendulumPosition();
            return;
        }
        bool self = pendulumTarget == Core.Me.ObjectId;
        bool DestinationSafe(Vector3 p) => p.Distance2D(PhiliaCenter) >= 15.5f && p.Distance2D(PhiliaCenter) <= 18.75f &&
            (self || p.Distance2D(pendulumFlare) >= 20.5f) &&
            !AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(p));
        bool ApproachSafe(Vector3 destination)
        {
            if (!BossSegmentSafe(destination)) return false;
            if (self) return true;
            Vector3 start = Core.Me.Location;
            float distance = start.Distance2D(pendulumFlare);
            // If already inside the flare's 20y region, permit only a route
            // that never approaches it more closely while escaping.
            for (int i = 1; i <= 20; i++)
            {
                float t = i / 20f;
                var point = new Vector3(start.X + (destination.X - start.X) * t, start.Y,
                    start.Z + (destination.Z - start.Z) * t);
                float next = point.Distance2D(pendulumFlare);
                if (next + 0.05f < distance) return false;
                distance = next;
            }
            return true;
        }
        var candidates = new List<Vector3> { Core.Me.Location };
        for (int i = 0; i < 48; i++)
        {
            double angle = i * Math.PI / 24;
            candidates.Add(new Vector3(PhiliaCenter.X + 17.5f * (float)Math.Sin(angle), Core.Me.Y,
                PhiliaCenter.Z + 17.5f * (float)Math.Cos(angle)));
        }
        if (!pendulumDestination.HasValue || !DestinationSafe(pendulumDestination.Value) || !ApproachSafe(pendulumDestination.Value))
        {
            IEnumerable<Vector3> safe = candidates.Where(point => DestinationSafe(point) && ApproachSafe(point));
            if (self)
            {
                var others = party.Where(actor => actor.ObjectId != Core.Me.ObjectId).Select(actor => actor.Location).ToArray();
                safe = safe.OrderByDescending(point => others.Length == 0 ? 0 : others.Min(p => p.Distance2D(point)))
                    .ThenBy(point => point.Distance2D(Core.Me.Location));
            }
            else
            {
                // When the boss is casting, prefer an equally safe point on the
                // observed leading side of the following rotation. Only do so if
                // conservative walking time reaches it before the flare; a late
                // or unknown cast retains the nearest safe Pendulum escape.
                var reachable = boss == null ? Array.Empty<Vector3>() : safe.Where(point =>
                    now.AddSeconds(0.3 + point.Distance2D(Core.Me.Location) / 4.5) <
                    now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.5)).ToArray();
                if (reachable.Length > 0)
                {
                    double lead = boss.Heading + Math.PI + Math.PI / 3;
                    safe = reachable.OrderBy(point =>
                    {
                        double angle = Math.Atan2(point.X - PhiliaCenter.X, point.Z - PhiliaCenter.Z);
                        return Math.Abs(Math.Atan2(Math.Sin(angle - lead), Math.Cos(angle - lead)));
                    }).ThenBy(point => point.Distance2D(Core.Me.Location));
                }
                else safe = safe.OrderBy(point => point.Distance2D(Core.Me.Location));
            }
            pendulumDestination = safe.Select(point => (Vector3?)point).FirstOrDefault();
            if (pendulumDestination.HasValue)
                ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Pendulum target={0:X} self={1} flare={2} destination={3}.",
                    pendulumTarget, self, pendulumFlare, pendulumDestination);
        }
        if (!pendulumDestination.HasValue) { ReleasePendulumPosition(); return; }
        CapabilityManager.Update(pendulumHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Pendulum positioning");
        CapabilityManager.Update(pendulumHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Pendulum positioning");
        pendulumLeased = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(pendulumDestination.Value) > 0.5f) ActionManager.StopCasting();
        bossMover.Request(pendulumDestination, 0.5f);
    }

    private void ReleasePendulumPosition()
    {
        if (pendulumLeased && bossMover?.IsCurrent == true) bossMover.Request(null, 0);
        if (pendulumLeased)
        {
            CapabilityManager.Clear(pendulumHandle, CapabilityFlags.Movement, "Philia Pendulum released");
            CapabilityManager.Clear(pendulumHandle, CapabilityFlags.GapCloser, "Philia Pendulum released");
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Pendulum movement released.");
        }
        pendulumLeased = false;
        pendulumDestination = null;
    }

    private void ReleasePhilia()
    {
        ReleaseLightStack();
        ReleasePendulum();
        ReleaseFierce();
        ReleaseKnoutEscape();
        knoutWindow = null;
        aethersupWindow = null;
        fierceConeWindows.Clear();
        fierceCircleWindows.Clear();
    }

    private void ObserveAethersup(BattleCharacter[] actors, DateTime now)
    {
        var boss = actors.FirstOrDefault(actor => Cast(actor, 0x278C, 15848));
        // The five-second-only activation was too late for a freed player near
        // the arena edge. Once the chain is gone, start the escape immediately;
        // allow initial spawn time and retain the late fallback if it survives.
        bool chainPresent = actors.Any(actor => actor.NpcId == 8570 && actor.IsAlive && actor.IsTargetable);
        if (boss != null && (boss.SpellCastInfo.RemainingCastTime <= TimeSpan.FromSeconds(5) ||
            boss.SpellCastInfo.RemainingCastTime <= TimeSpan.FromSeconds(13.5) && !chainPresent))
        {
            var matrix = boss.OmenMatrix;
            double magnitude = Math.Sqrt(matrix.M20 * matrix.M20 + matrix.M22 * matrix.M22);
            if (magnitude > 1)
            {
                float heading = (float)Math.Atan2(matrix.M20, matrix.M22);
                double difference = Math.Abs(Math.Atan2(Math.Sin(heading - boss.Heading), Math.Cos(heading - boss.Heading)));
                if (difference <= 0.2)
                {
                    if (aethersupWindow == null)
                        ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Aethersup omen heading={0:F3} origin={1} remaining={2:F2}s chainPresent={3}.",
                            heading, boss.Location, boss.SpellCastInfo.RemainingCastTime.TotalSeconds, chainPresent);
                    // Keep one object identity while the avoid manager tracks
                    // this cast; replacing it every pulse churns active avoids.
                    aethersupWindow ??= new KnoutWindow();
                    aethersupWindow.Action = 15848;
                    aethersupWindow.Origin = boss.Location;
                    aethersupWindow.Heading = heading;
                    aethersupWindow.Until = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2.5);
                }
            }
            if (aethersupWindow == null)
            {
                // The captured boss finished facing the chained target early
                // in the long tell; use its settled facing if no omen arrived.
                ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Aethersup omen unavailable; using settled facing.");
                aethersupWindow = new KnoutWindow { Action = 15848, Heading = boss.Heading, Origin = boss.Location,
                    Until = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2.5) };
            }
        }
        if (aethersupWindow != null && now >= aethersupWindow.Until) aethersupWindow = null;
    }

    private void UpdateLightStack(BattleCharacter boss, BattleCharacter[] party)
    {
        if (boss == null || !boss.IsValid) { ReleaseLightStackPosition(); return; }
        if (lightStackFallback.HasValue)
        {
            var first = party.FirstOrDefault(actor => actor.ObjectId == lightPairA);
            var second = party.FirstOrDefault(actor => actor.ObjectId == lightPairB);
            if (first == null || second == null || first.Location.Distance2D(second.Location) > 4f)
            {
                lightStackFallback = null;
                lightPairSince = default;
            }
            else
            {
                var a = first.Location; var b = second.Location;
                lightStackFallback = new Vector3((a.X + b.X) / 2, Core.Me.Y, (a.Z + b.Z) / 2);
            }
        }
        // The boss-origin and forward-axis attempts both missed live line
        // stacks because the final target direction was not exposed early.
        // As a last resort, snapshot a converged pair of companions: their
        // stack position has preceded the hit in the captured Trust runs.
        if (!lightStackFallback.HasValue && DateTime.UtcNow >= lightStackStarted.AddSeconds(1))
        {
            var companions = party.Where(actor => actor.ObjectId != Core.Me.ObjectId).ToArray();
            bool observedPair = false;
            for (int i = 0; i < companions.Length && !observedPair; i++)
            for (int j = i + 1; j < companions.Length; j++)
            {
                var a = companions[i].Location;
                var b = companions[j].Location;
                if (a.Distance2D(b) > 3f) continue;
                observedPair = true;
                var midpoint = new Vector3((a.X + b.X) / 2, Core.Me.Y, (a.Z + b.Z) / 2);
                uint first = Math.Min(companions[i].ObjectId, companions[j].ObjectId);
                uint second = Math.Max(companions[i].ObjectId, companions[j].ObjectId);
                var now = DateTime.UtcNow;
                if (lightPairSince == default || lightPairA != first || lightPairB != second || lightPairPoint.Distance2D(midpoint) > 1.5f)
                {
                    lightPairSince = now; lightPairA = first; lightPairB = second; lightPairPoint = midpoint;
                }
                else if (now >= lightPairSince.AddMilliseconds(300))
                {
                    lightStackFallback = midpoint;
                    ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Line stack companion fallback={0} members={1:X8},{2:X8}.",
                        lightStackFallback, first, second);
                }
                break;
            }
            if (!observedPair) lightPairSince = default;
        }
        // Until a pair converges, holding the current safe point avoids
        // running toward a direction that may change at resolution.
        var destination = lightStackFallback ?? Core.Me.Location;
        if (lightStackLeased && bossMover?.IsCurrent == true && MovementManager.GenerallyOutOfControl &&
            Core.Me.IsCasting && !AvoidanceManager.IsRunningOutOfAvoid && Core.Me.Location.Distance2D(destination) <= 0.5f)
        {
            CapabilityManager.Update(lightStackHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia line stack hold");
            CapabilityManager.Update(lightStackHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia line stack hold");
            return;
        }
        if (bossMover?.IsCurrent != true || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid || !BossSegmentSafe(destination))
        {
            ReleaseLightStackPosition();
            return;
        }
        CapabilityManager.Update(lightStackHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia line stack");
        CapabilityManager.Update(lightStackHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia line stack");
        if (!lightStackLeased) ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Line stack position acquired at {0}, destination={1}.", Core.Me.Location, destination);
        lightStackLeased = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(destination) > 0.5f) ActionManager.StopCasting();
        bossMover.Request(destination, 0.3f);
    }

    private void ReleaseLightStackPosition()
    {
        if (lightStackLeased && bossMover?.IsCurrent == true) bossMover.Request(null, 0);
        if (lightStackLeased)
        {
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Line stack position released at {0}.", Core.Me.Location);
            CapabilityManager.Clear(lightStackHandle, CapabilityFlags.Movement, "Philia line stack released");
            CapabilityManager.Clear(lightStackHandle, CapabilityFlags.GapCloser, "Philia line stack released");
        }
        lightStackLeased = false;
    }

    private void ReleaseLightStack()
    {
        ReleaseLightStackPosition();
        lightStackUntil = default;
        lightStackFallback = null;
        lightPairSince = default;
    }

    private void ObserveKnout(BattleCharacter[] actors, DateTime now)
    {
        var boss = actors.FirstOrDefault(actor => Cast(actor, 0x278C, 15846) || Cast(actor, 0x278C, 15847));
        if (boss != null && (knoutWindow == null || now >= knoutWindow.Until))
        {
            var matrix = boss.OmenMatrix;
            double magnitude = Math.Sqrt(matrix.M20 * matrix.M20 + matrix.M22 * matrix.M22);
            if (magnitude > 1)
            {
                float heading = (float)Math.Atan2(matrix.M20, matrix.M22);
                double expected = boss.Heading + (boss.CastingSpellId == 15846 ? -1 : 1) * 75 * Math.PI / 180;
                double difference = Math.Abs(Math.Atan2(Math.Sin(heading - expected), Math.Cos(heading - expected)));
                if (difference <= 0.2)
                {
                    knoutWindow = new KnoutWindow { Action = boss.CastingSpellId, Origin = boss.Location, Heading = heading,
                        Until = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5) };
                    knoutObservedAt = now;
                }
            }
        }
        if (knoutWindow != null && now >= knoutWindow.Until) knoutWindow = null;
    }

    // Native avoidance twice requested paths beyond the courtyard during
    // Knout. Use the existing movement owner for a timed, in-arena escape
    // after the captured omen axis settles; other hazards stay authoritative.
    private bool UpdateKnoutEscape(DateTime now)
    {
        if (knoutWindow == null || now >= knoutWindow.Until || fierceUntil != default ||
            lightStackUntil != default || pendulumUntil != default || bossMover?.IsCurrent != true)
        {
            ReleaseKnoutEscape();
            return false;
        }
        if (!ReferenceEquals(knoutEscapeWindow, knoutWindow))
        {
            ReleaseKnoutEscape();
            knoutEscapeWindow = knoutWindow;
        }
        var shapes = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var fan = shapes.Where(avoid => ReferenceEquals(avoid.Object, knoutWindow)).ToArray();
        Vector3 start = Core.Me.Location, origin = knoutWindow.Origin;
        bool inOrigin = start.Distance2D(origin) < 2.05f;
        bool inFan = fan.Any(avoid => avoid.IsPointInAvoid(start));
        if (MovementManager.GenerallyOutOfControl)
        {
            // A cast at the fan apex can continue after native escape gives up.
            // Interrupt only while actually inside this captured Knout shape.
            if (Core.Me.IsCasting && (inOrigin || inFan)) ActionManager.StopCasting();
            ReleaseKnoutEscape();
            return false;
        }
        var otherAvoids = shapes.Where(avoid => !ReferenceEquals(avoid.Object, knoutWindow) &&
            !(avoid.Object is Vector3 point && point.Distance2D(origin) < 0.05f)).ToArray();
        bool SafeSegment(Vector3 end)
        {
            if (end.Distance2D(PhiliaCenter) > 18f || end.Distance2D(origin) < 2.5f ||
                fan.Length == 0 || fan.Any(avoid => avoid.IsPointInAvoid(end))) return false;
            double angle = Math.Atan2(end.X - origin.X, end.Z - origin.Z);
            double delta = Math.Abs(Math.Atan2(Math.Sin(angle - knoutWindow.Heading), Math.Cos(angle - knoutWindow.Heading)));
            if (delta < 110 * Math.PI / 180) return false;
            double previous = start.Distance2D(origin);
            for (int i = 0; i <= 32; i++)
            {
                float t = i / 32f;
                var point = new Vector3(start.X + (end.X - start.X) * t, start.Y, start.Z + (end.Z - start.Z) * t);
                double radius = point.Distance2D(origin);
                if (point.Distance2D(PhiliaCenter) > 18.5f ||
                    (!inOrigin && radius < 2.05) || (inOrigin && radius + 0.01 < previous) ||
                    otherAvoids.Any(avoid => avoid.IsPointInAvoid(point))) return false;
                previous = radius;
            }
            return now.AddSeconds(0.35 + start.Distance2D(end) / 4.5) < knoutWindow.Until.AddSeconds(-1.5);
        }
        if (knoutEscapeOwned)
        {
            if (!knoutEscapeDestination.HasValue || !SafeSegment(knoutEscapeDestination.Value))
            {
                ReleaseKnoutEscape();
                return false;
            }
            CapabilityManager.Update(knoutEscapeHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Knout escape");
            CapabilityManager.Update(knoutEscapeHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Knout escape");
            bossMover.RequestHazardExit(knoutEscapeDestination.Value, 0.3f);
            return true;
        }
        if (!(inOrigin || inFan) || now < knoutObservedAt.AddMilliseconds(150)) return false;
        Vector3? best = null;
        double bestDistance = double.PositiveInfinity;
        for (int x = -18; x <= 18; x++) for (int z = -18; z <= 18; z++)
        {
            var candidate = new Vector3(PhiliaCenter.X + x, start.Y, PhiliaCenter.Z + z);
            double distance = start.Distance2D(candidate);
            if (distance < bestDistance && SafeSegment(candidate)) { best = candidate; bestDistance = distance; }
        }
        if (!best.HasValue) return false;
        knoutEscapeDestination = best;
        knoutEscapeOwned = true;
        CapabilityManager.Update(knoutEscapeHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Knout escape");
        CapabilityManager.Update(knoutEscapeHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Knout escape");
        bossMover.RequestHazardExit(best.Value, 0.3f);
        ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Knout timed escape origin={0} destination={1} distance={2:F2}.", start, best, bestDistance);
        return true;
    }

    private void ReleaseKnoutEscape()
    {
        if (knoutEscapeOwned && bossMover?.IsCurrent == true) bossMover.Request(null, 0);
        if (knoutEscapeOwned)
        {
            CapabilityManager.Clear(knoutEscapeHandle, CapabilityFlags.Movement, "Philia Knout escape released");
            CapabilityManager.Clear(knoutEscapeHandle, CapabilityFlags.GapCloser, "Philia Knout escape released");
        }
        knoutEscapeOwned = false;
        knoutEscapeDestination = null;
        knoutEscapeWindow = null;
    }

    private void ReleasePendulum()
    {
        ReleasePendulumPosition();
        pendulumTarget = 0;
        pendulumUntil = default;
        pendulumMarkerSeen = false;
    }

    private void ObserveFierce(BattleCharacter[] actors, DateTime now)
    {
        fierceConeWindows.RemoveAll(cone => now >= cone.Until);
        foreach (uint id in fierceCircleWindows.Where(entry => now >= entry.Value.Until).Select(entry => entry.Key).ToArray())
            fierceCircleWindows.Remove(id);
        foreach (var circle in actors.Where(actor => Cast(actor, 0x233C, 15837)))
            if (!fierceCircleWindows.ContainsKey(circle.ObjectId))
            {
                fierceCircleWindows[circle.ObjectId] = new KnoutWindow { Action = 15837, Origin = circle.Location,
                    Until = now + circle.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5) };
            }
        var boss = actors.FirstOrDefault(actor => Cast(actor, 0x278C, 15834));
        if (boss != null && fierceUntil == default)
        {
            // The observed sequence uses eight clockwise 45-degree
            // cones roughly 3.8s apart. The initial boss tell precedes them.
            fierceUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(33);
            fierceFirstConeAt = now + boss.SpellCastInfo.RemainingCastTime;
            fierceHeading = boss.Heading;
            fierceHeadings.Clear();
            fierceSide = 0;
            fierceFinalSeen = false;
            fierceDestination = null;
            fierceWaypoint = null;
            nextFierceEntrySearch = default;
        }
        if (fierceUntil == default) return;
        if (now >= fierceUntil) { ReleaseFierce(); return; }
        var cone = actors.FirstOrDefault(actor => Cast(actor, 0x233C, 15841));
        if (cone == null) return;
        float h = cone.Heading;
        int phase = ((int)Math.Round(h / (Math.PI / 4)) % 8 + 8) % 8;
        bool preserveEntry = fierceHeadings.Count == 0 && fierceWaypoint.HasValue &&
            Math.Abs(Math.Atan2(Math.Sin(h - fierceHeading), Math.Cos(h - fierceHeading))) < 0.1;
        if (fierceHeadings.Add(phase))
        {
            fierceHeading = h;
            fiercePhaseAt = now;
            // The tail hit can land after the cast bar vanishes.
            // Snapshot each phase through the sampled delayed resolution.
            fierceConeWindows.Add(new KnoutWindow { Action = 15841, Origin = cone.Location,
                Heading = h + (float)Math.PI, Until = now + cone.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5) });
            if (!preserveEntry) { fierceDestination = null; fierceWaypoint = null; }
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Fierce Beating phase={0} heading={1:F3}.", fierceHeadings.Count, h);
        }
        if (fierceHeadings.Count >= 8 && !fierceFinalSeen)
        {
            fierceFinalSeen = true;
            fierceUntil = now + cone.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }
    }

    private void UpdateFierce(DateTime now)
    {
        if (fierceUntil == default || now >= fierceUntil) { ReleaseFierce(); return; }
        if (fierceLeased && fierceDestination.HasValue && bossMover?.IsCurrent == true &&
            MovementManager.GenerallyOutOfControl && Core.Me.IsCasting && !AvoidanceManager.IsRunningOutOfAvoid &&
            Core.Me.Location.Distance2D(fierceDestination.Value) <= 0.35f)
        {
            CapabilityManager.Update(fierceHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Fierce Beating hold");
            CapabilityManager.Update(fierceHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Fierce Beating hold");
            return;
        }
        // A new phase clears the old destination. A cast during the hold can
        // release the lease too late for native avoidance to escape the next cone.
        // Interrupt only on the phase handoff and keep the lease briefly while
        // the cast cancellation propagates; other native escapes still preempt.
        if (fierceLeased && (!fierceDestination.HasValue || Core.Me.Location.Distance2D(fierceWaypoint ?? fierceDestination.Value) > 0.35f) &&
            fierceHeadings.Count > 0 && now < fiercePhaseAt.AddMilliseconds(500) && bossMover?.IsCurrent == true &&
            Core.Me.IsCasting && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            ActionManager.StopCasting();
            CapabilityManager.Update(fierceHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Fierce Beating cast interruption");
            CapabilityManager.Update(fierceHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Fierce Beating cast interruption");
            return;
        }
        if (bossMover?.IsCurrent != true || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseFiercePosition();
            return;
        }
        bool SafeApproach(Vector3 destination)
        {
            if (!BossSegmentSafe(destination)) return false;
            Vector3 start = Core.Me.Location;
            // Before the first cone exists, leave its predicted sector early.
            // Waiting for native avoidance can lead through an exaflare. Require
            // arrival before the tell ends and respect currently published avoids.
            bool early = fierceHeadings.Count == 0 &&
                now.AddSeconds(0.3 + start.Distance2D(destination) / 4.5) < fierceFirstConeAt;
            // At 12.5y, successive 45-degree chords stay outside the sludge.
            // Both modeled side routes clear the rear fan and the observed
            // outward circle corridors at their resolution times.
            for (int i = 0; i <= 24; i++)
            {
                float t = i / 24f;
                float x = start.X + (destination.X - start.X) * t - PhiliaCenter.X;
                float z = start.Z + (destination.Z - start.Z) * t - PhiliaCenter.Z;
                double radius = Math.Sqrt(x * x + z * z);
                if (radius < 10.5 || radius > 19) return false;
                double angle = Math.Atan2(x, z);
                double tail = fierceHeading + Math.PI;
                double delta = Math.Abs(Math.Atan2(Math.Sin(angle - tail), Math.Cos(angle - tail)));
                if (delta < 65 * Math.PI / 180)
                {
                    // On the opposite side the new fan can initially cover
                    // the old position. Permit only an already-owned escape
                    // predicted to leave its buffered sector well before the
                    // captured ~2.5s impact; never extend that deadline.
                    double travel = start.Distance2D(destination) * t / 4.5 + 0.3;
                    bool timelyExit = fierceHeadings.Count > 0 && fierceLeased &&
                        now.AddSeconds(travel) < fiercePhaseAt.AddSeconds(1.4);
                    if ((!early && !timelyExit) || i == 24) return false;
                }
            }
            return true;
        }
        Vector3 Point(int side)
        {
            // The captured spin advances by decreasing heading. A +60-degree
            // position leads it and stays outside the next buffered rear cone;
            // retain the opposite side as a fallback when entry is unsafe.
            double angle = fierceHeading + (side > 0 ? Math.PI / 3 : -Math.PI / 2);
            return new Vector3(PhiliaCenter.X + 12.5f * (float)Math.Sin(angle), Core.Me.Y,
                PhiliaCenter.Z + 12.5f * (float)Math.Cos(angle));
        }
        bool SafeSecondSegment(Vector3 start, Vector3 end)
        {
            var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
            for (int i = 0; i <= 32; i++)
            {
                float t = i / 32f;
                var point = new Vector3(start.X + (end.X - start.X) * t, Core.Me.Y, start.Z + (end.Z - start.Z) * t);
                double x = point.X - PhiliaCenter.X, z = point.Z - PhiliaCenter.Z;
                double radius = Math.Sqrt(x * x + z * z);
                double angle = Math.Atan2(x, z), tail = fierceHeading + Math.PI;
                double delta = Math.Abs(Math.Atan2(Math.Sin(angle - tail), Math.Cos(angle - tail)));
                if (radius < 10.5 || radius > 19 || delta < 65 * Math.PI / 180 ||
                    avoids.Any(avoid => avoid.IsPointInAvoid(point))) return false;
            }
            return true;
        }
        double CircleClearance(Vector3 start, Vector3 end)
        {
            double dx = end.X - start.X, dz = end.Z - start.Z, length2 = dx * dx + dz * dz;
            return fierceCircleWindows.Values.Select(circle =>
            {
                double t = length2 < 0.0001 ? 0 : Math.Max(0, Math.Min(1,
                    ((circle.Origin.X - start.X) * dx + (circle.Origin.Z - start.Z) * dz) / length2));
                double x = start.X + t * dx - circle.Origin.X, z = start.Z + t * dz - circle.Origin.Z;
                return Math.Sqrt(x * x + z * z);
            }).DefaultIfEmpty(double.PositiveInfinity).Min();
        }
        if (fierceWaypoint.HasValue && Core.Me.Location.Distance2D(fierceWaypoint.Value) <= 0.35f)
            fierceWaypoint = null;
        if (!fierceDestination.HasValue || !SafeApproach(fierceWaypoint ?? fierceDestination.Value))
        {
            fierceWaypoint = null;
            // Before rotation starts, either side may be reachable around
            // the initial circles; once it starts, keep the chosen side.
            var sides = fierceSide == 0 || fierceHeadings.Count == 0 ? new[] { 1, -1 } : new[] { fierceSide };
            var choices = sides.Select(side => new { Side = side, Point = Point(side) })
                .Where(choice => SafeApproach(choice.Point) &&
                    (choice.Side < 0 || CircleClearance(Core.Me.Location, choice.Point) >= 5.0 &&
                     (fierceHeadings.Count > 0 || now.AddSeconds(0.3 + Core.Me.Location.Distance2D(choice.Point) / 4.5) < fierceFirstConeAt.AddSeconds(1.4))))
                .OrderBy(choice => choice.Side > 0 ? 0 : 1)
                .ThenBy(choice => choice.Point.Distance2D(Core.Me.Location)).ToArray();
            if (choices.Length > 0)
            {
                fierceSide = choices[0].Side;
                fierceDestination = choices[0].Point;
            }
            else if (fierceHeadings.Count == 0)
            {
                if (now < nextFierceEntrySearch) { ReleaseFiercePosition(); return; }
                nextFierceEntrySearch = now.AddMilliseconds(250);
                // A direct chord can cross the first circle after its cast.
                // Search a bounded two-leg entry while the tell leaves time.
                var routes = new List<(int Side, Vector3 Waypoint, Vector3 End, double Length, double Clearance)>();
                foreach (int side in sides)
                foreach (float radius in new[] { 14f, 18f })
                for (int i = 0; i < 24; i++)
                {
                    double angle = i * Math.PI / 12;
                    var point = new Vector3(PhiliaCenter.X + radius * (float)Math.Sin(angle), Core.Me.Y,
                        PhiliaCenter.Z + radius * (float)Math.Cos(angle));
                    var end = Point(side);
                    double length = Core.Me.Location.Distance2D(point) + point.Distance2D(end);
                    if (now.AddSeconds(0.3 + length / 4.5) >= fierceFirstConeAt.AddSeconds(1.4) ||
                        !SafeApproach(point) || !SafeSecondSegment(point, end)) continue;
                    double clearance = Math.Min(CircleClearance(Core.Me.Location, point), CircleClearance(point, end));
                    routes.Add((side, point, end, length, clearance));
                }
                // Prefer the leading side only with a modest circle margin;
                // otherwise retain the clearest validated entry on either side.
                var route = routes.OrderByDescending(item => item.Side > 0 && item.Clearance >= 5.0)
                    .ThenByDescending(item => item.Clearance).ThenBy(item => item.Length).FirstOrDefault();
                if (routes.Count == 0) { ReleaseFiercePosition(); return; }
                fierceSide = route.Side;
                fierceWaypoint = route.Waypoint;
                fierceDestination = route.End;
                ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Fierce Beating waypoint={0} length={1:F2} circleClearance={2:F2}.", fierceWaypoint, route.Length, route.Clearance);
            }
            else { ReleaseFiercePosition(); return; }
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Fierce Beating side={0} destination={1}.", fierceSide, fierceDestination);
        }
        CapabilityManager.Update(fierceHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Philia Fierce Beating positioning");
        CapabilityManager.Update(fierceHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Philia Fierce Beating positioning");
        // Heal runs before Combat in RB's combat logic. The scoped Combat hook
        // prevents the routine immediately restarting a cast while this validated
        // timed move begins; it leaves healing and every other encounter untouched.
        EnsureFierceCombatGate();
        fierceLeased = true;
        var target = fierceWaypoint ?? fierceDestination.Value;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(target) > 0.35f) ActionManager.StopCasting();
        bossMover.Request(target, 0.35f);
    }

    private void EnsureFierceCombatGate()
    {
        fierceCombatGate ??= new Decorator(_ =>
        {
            if (!fierceLeased || !PhiliaCombat() || bossMover?.IsCurrent != true ||
                AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl && !Core.Me.IsCasting) return false;
            DateTime deadline = fierceHeadings.Count == 0 ? fierceFirstConeAt.AddSeconds(1.4) : fiercePhaseAt.AddSeconds(1.4);
            Vector3? target = fierceWaypoint ?? fierceDestination;
            return DateTime.UtcNow < deadline && (!target.HasValue || Core.Me.Location.Distance2D(target.Value) > 0.35f);
        }, new ActionAlwaysSucceed());
        if (!TreeHooks.Instance.Hooks.TryGetValue("Combat", out var hooks) || !hooks.Contains(fierceCombatGate))
            TreeHooks.Instance.InsertHook("Combat", 0, fierceCombatGate);
    }

    private void ReleaseFiercePosition()
    {
        if (fierceCombatGate != null) TreeHooks.Instance.RemoveHook("Combat", fierceCombatGate);
        if (fierceLeased && bossMover?.IsCurrent == true) bossMover.Request(null, 0);
        if (fierceLeased)
        {
            CapabilityManager.Clear(fierceHandle, CapabilityFlags.Movement, "Philia Fierce Beating released");
            CapabilityManager.Clear(fierceHandle, CapabilityFlags.GapCloser, "Philia Fierce Beating released");
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Fierce Beating movement released.");
        }
        fierceLeased = false;
        fierceDestination = null;
        fierceWaypoint = null;
    }

    private void ReleaseFierce()
    {
        ReleaseFiercePosition();
        fierceUntil = default;
        fierceSide = 0;
        fierceFinalSeen = false;
        fierceHeadings.Clear();
    }
}
