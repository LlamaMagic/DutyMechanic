using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Innocence's normal trial, including towers, gaze, spreads and add priority.
/// Required positioning and native avoidance share the encounter's borrowed mover.
/// </summary>
public sealed class Immaculate : AbstractDungeon
{
    internal const string HandlerRevision = "immaculate-dev4";
    private const uint Territory = 846, FirstForm = 0x28FB, SecondForm = 0x28FE;
    private const uint TowerBase = 0x1EAD40, Enthrall = 16025, Daybreak = 16029;
    private const uint Trinity = 16051, OutgoingLine = 16056, ReturningLine = 16075;
    private const uint RayInner = 16062, RayMiddle = 16063, RayOuter = 16064;
    private const uint SoulAndBody = 16049, Vision = 16071, PillarCast = 16190;
    private const uint Manacle = 18064, AddSword = 18065, DropMarker = 138;
    private const float ArenaRadius = 19.5f; // 20y circular floor with the default 0.5y inset.
    private static readonly Vector3 Center = new(100, 0, 100);
    private static readonly CapabilityFlags[] PositionFlags =
        [CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser];
    private static Immaculate current;
    private readonly Dictionary<uint, uint> casting = [];
    private readonly List<Hazard> hazards = [];
    private readonly Dictionary<uint, DateTime> spreads = [];
    private readonly HashSet<uint> observedSpreadMarkers = [];
    private readonly Dictionary<uint, string> actorStates = [];
    private Hazard[] circles = [], polygons = [];
    private ChrysalisMovement movement;
    private bool active, sideStepBorrowed, previousSideStep, positionLeased, facingLeased;
    private bool unsupportedMover, travelling, autoFaceBorrowed, previousAutoFace;
    private DateTime ribbonUntil, nextActorSample, stackUntil, gazeUntil, visionUntil;
    private Vector3 gazeOrigin, visionOrigin;
    private bool secondFormEntered, addProviderLost;
    private uint priorityAdd;
    private AddTargetProvider addProvider;
    private Vector3? goal;
    private uint towerId, stackParticipant;
    private string positionReason;
    private string lastWaypointOwner;
    private Vector3 lastWaypoint;

    // No frame-scoped actors are retained in avoid registrations. An untargetable
    // helper must not become RB's implicit combat-range attraction point.
    private sealed class Hazard
    {
        internal uint Action, Owner;
        internal Vector3 Origin;
        internal float Heading, Radius;
        internal Vector2[] Points;
        internal DateTime Starts, Expires;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)Territory;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        AvoidanceManager.AddAvoidLocation<Hazard>(() => active, () => Center, 30,
            h => h.Radius, h => h.Origin, () => circles);
        AvoidanceManager.AddAvoidPolygon<Hazard>(() => active, () => Center, 30,
            h => -h.Heading, _ => 1, _ => 8, h => h.Points, h => h.Origin, () => polygons);
        AvoidanceHelpers.AddAvoidDonut(() => active, () => Center, 80, ArenaRadius);
        // The rotating ribbon leaves an 11.5-16y annular gap in either direction.
        // Stay within 12-15.5y to leave clearance from both edges.
        AvoidanceManager.AddAvoidLocation<Vector3>(() => active && DateTime.UtcNow < ribbonUntil,
            () => Center, 30, _ => 12, p => p, () => new[] { Center });
        AvoidanceHelpers.AddAvoidDonut(() => active && DateTime.UtcNow < ribbonUntil,
            () => Center, 80, 15.5);
        Logger.Information($"[Immaculate] Registered {HandlerRevision}: native geometry and shared positioning owner.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Party readiness precedes combat; a queue pop is a different interaction.
        await UsefulTasks.HandleReadyCheck();
        CheckLifecycle();
        return travelling && !AvoidanceManager.IsRunningOutOfAvoid;
    }

    /// <summary>Maintains bot-thread ownership while an OrderBot profile hook yields.</summary>
    public static void CheckLifecycle() => current?.Update();

    /// <summary>Releases only this encounter's borrowed state after stop or disable.</summary>
    public static void ResetActive()
    {
        current?.Release();
        current = null;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    private void Update()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != Territory || !DutyManager.InInstance)
        {
            Release();
            return;
        }
        if (!sideStepBorrowed && SidestepPlugin != null)
        {
            previousSideStep = SidestepPlugin.Enabled;
            sideStepBorrowed = true;
        }
        if (SidestepPlugin != null && SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = false;
        }

        if (Core.Me == null || !Core.Me.IsAlive || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            ResetCombat();
            return;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid).ToArray();
        // Both forms exist from entry; targetability identifies the phase change.
        var firstForm = actors.FirstOrDefault(a => a.IsAlive && a.BaseId == FirstForm);
        var secondForm = actors.FirstOrDefault(a => a.IsAlive && a.BaseId == SecondForm);
        if (firstForm?.IsTargetable == true)
        {
            secondFormEntered = false;
        }

        if (secondForm?.IsTargetable == true)
        {
            secondFormEntered = true;
        }

        var boss = secondFormEntered ? secondForm : firstForm;
        active = Core.Me.InCombat && boss != null;
        if (!active)
        {
            ResetCombat();
            return;
        }
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
        }
        if (movement == null && !unsupportedMover)
        {
            if (Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            {
                movement = new ChrysalisMovement(Navigator.PlayerMover,
                    canMoveOrdinary: OrdinaryWaypointAllowed, validateNativeWaypoint: ObserveNativeWaypoint);
                Navigator.PlayerMover = movement;
            }
            else
            {
                unsupportedMover = true;
                Logger.Warning("[Immaculate] Unsupported mover; input cleanup and semantic movement cannot be verified.");
            }
        }
        DateTime now = DateTime.UtcNow;
        bool newGeometry = CaptureCasts(actors, now);
        var party = actors.Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a.IsAlive && (a.ObjectId == Core.Me.ObjectId ||
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        UpdateSpreads(party, now);
        circles = hazards.Where(h => h.Starts <= now && h.Expires > now && h.Radius > 0).ToArray();
        polygons = hazards.Where(h => h.Starts <= now && h.Expires > now && h.Points != null).ToArray();
        if (newGeometry)
        {
            // The normal avoidance pulse may precede this plugin's cast snapshot.
            // Publish new shapes immediately so the native planner sees the tell
            // on this tick and still sends its route through the borrowed mover.
            bool escapedBefore = AvoidanceManager.IsRunningOutOfAvoid;
            AvoidanceManager.Pulse();
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                Logger.Information($"[Immaculate] Geometry refreshed circles={circles.Length} polygons={polygons.Length} nativeBefore={escapedBefore} nativeAfter={AvoidanceManager.IsRunningOutOfAvoid} player={Core.Me.Location}.");
            }
        }
        PrioritizeTetheredAdd(actors);
        SelectPosition(boss, party, now);
        bool canPosition = goal.HasValue && movement?.IsCurrent == true && SegmentClear(goal.Value);
        UpdatePositionLease(canPosition);
        movement?.Request(canPosition ? goal : null, 0.65f);
        movement?.Update();
        travelling = canPosition && Core.Me.Location.Distance2D(goal.Value) > 0.65f;
        UpdateGaze(boss, travelling);
        CaptureActors(actors, party, now);
    }

    private bool CaptureCasts(BattleCharacter[] actors, DateTime now)
    {
        bool changed = false;
        hazards.RemoveAll(h => h.Expires <= now);
        var live = actors.Select(a => a.ObjectId).ToHashSet();
        foreach (uint id in casting.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            casting.Remove(id);
        }

        foreach (var actor in actors)
        {
            uint action = actor.IsCasting ? actor.CastingSpellId : 0;
            if (action == 0)
            {
                casting.Remove(actor.ObjectId);
                continue;
            }
            bool first = !casting.TryGetValue(actor.ObjectId, out uint previous) || action != previous;
            casting[actor.ObjectId] = action;
            if (!first || actor.SpellCastInfo == null)
            {
                continue;
            }

            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            // Keep cast geometry for another 600ms so ordinary travel cannot
            // resume between cast completion and the effect.
            var hazard = new Hazard
            {
                Action = action,
                Owner = actor.ObjectId,
                Origin = actor.Location,
                Heading = actor.Heading,
                Starts = now,
                Expires = finish.AddMilliseconds(600)
            };
            switch (action)
            {
                case Daybreak:
                case Manacle:
                    hazard.Origin = actor.SpellCastInfo.CastLocation;
                    hazard.Radius = 6.5f; // 6y placed circle + 0.5y margin.
                    break;
                case Trinity:
                case OutgoingLine:
                case ReturningLine:
                    hazard.Points = Rectangle(5, action == Trinity ? 40.5f : action == OutgoingLine ? 21.5f : 42.5f);
                    if (action == OutgoingLine)
                    {
                        // The swords return about 9.5s after the outgoing cast.
                        // Protect that lane shortly before the return, with a bounded
                        // expiry if its authoritative returning helper is unavailable.
                        hazards.Add(new Hazard
                        {
                            Action = ReturningLine,
                            Owner = actor.ObjectId,
                            Origin = hazard.Origin,
                            Heading = hazard.Heading,
                            Points = Rectangle(5, 21.5f),
                            Starts = finish.AddSeconds(7.5),
                            Expires = finish.AddSeconds(10.5)
                        });
                    }
                    break;
                case RayInner:
                case RayMiddle:
                case RayOuter:
                    // Three 100-degree sectors expand in ordered radial stages.
                    // Add three degrees at each edge for angular clearance.
                    // Only the casting helper's stage is active; these are not full donuts.
                    hazard.Points = Sector(action == RayInner ? 0 : action == RayMiddle ? 4.5f : 9.5f,
                        action == RayInner ? 5.5f : action == RayMiddle ? 10.5f : 20.5f, 53);
                    break;
                case SoulAndBody:
                    // One initial tell anchors the complete 26-step, 0.6s sequence.
                    // Confirming helper casts do not keep restarting its lifetime.
                    if (now >= ribbonUntil)
                    {
                        ribbonUntil = finish.AddSeconds(16);
                    }

                    changed = true;
                    continue;
                case Enthrall:
                    gazeOrigin = actor.Location;
                    // The gaze applies about 1.4s after cast completion.
                    gazeUntil = finish.AddSeconds(1.8);
                    continue;
                case Vision:
                    visionOrigin = actor.Location;
                    // The charge resolves about 1.8s after cast completion.
                    // Its rim-to-center line is stable before the boss finishes turning.
                    visionUntil = finish.AddSeconds(2.2);
                    continue;
                case PillarCast:
                    // Line damage follows cast completion by about 2.4s.
                    stackUntil = finish.AddSeconds(2.8);
                    stackParticipant = 0;
                    continue;
                default:
                    continue; // Choreography/raidwides are not arena-wide avoids.
            }
            hazards.Add(hazard);
            changed = true;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                Logger.Information($"[Immaculate] Hazard action={action} owner={actor.ObjectId:X} origin={hazard.Origin} heading={hazard.Heading:F3} finish={finish:O}.");
            }
        }
        return changed;
    }

    private void UpdateSpreads(BattleCharacter[] party, DateTime now)
    {
        var marked = party.Where(a => HasMarker(a, DropMarker)).Select(a => a.ObjectId).ToHashSet();
        observedSpreadMarkers.RemoveWhere(id => !marked.Contains(id));
        foreach (uint id in marked)
        {
            // The icon can remain after impact. Arm once per appearance so it
            // cannot delay the following stack; impact is about 5.8s after the tell.
            if (observedSpreadMarkers.Add(id))
            {
                spreads[id] = now.AddSeconds(6.2);
            }
        }

        foreach (uint id in spreads.Where(s => s.Value <= now || !party.Any(a => a.ObjectId == s.Key))
            .Select(s => s.Key).ToArray())
        {
            spreads.Remove(id);
        }
        // Party circles track their carrier through impact. The controlled carrier
        // uses explicit outward placement below, rather than treating itself as safe.
        hazards.RemoveAll(h => h.Action == DropMarker);
        foreach (var actor in party.Where(a => a.ObjectId != Core.Me.ObjectId && spreads.ContainsKey(a.ObjectId)))
        {
            hazards.Add(new Hazard
            {
                Action = DropMarker,
                Origin = actor.Location,
                Radius = 10.5f,
                Starts = now,
                Expires = spreads[actor.ObjectId]
            });
        }
    }

    private void SelectPosition(BattleCharacter boss, BattleCharacter[] party, DateTime now)
    {
        Vector3? selected = null;
        string reason = null;
        // Native escape takes precedence. Other requests follow tower/gaze,
        // proximity, spread and stack order.
        if (boss.BaseId == FirstForm)
        {
            var towers = GameObjectManager.GameObjects
                .Where(a => a.IsValid && a.IsVisible && (a.NpcId == TowerBase || a.BaseId == TowerBase)).ToArray();
            var chosen = towers.FirstOrDefault(t => t.ObjectId == towerId &&
                (Core.Me.Location.Distance2D(t.Location) < 4 ||
                 !party.Any(p => p.ObjectId != Core.Me.ObjectId && p.Distance2D(t.Location) < 4)));
            chosen ??= towers.Where(t => !party.Any(p => p.ObjectId != Core.Me.ObjectId && p.Distance2D(t.Location) < 4))
                .OrderBy(t => t.Distance2D(Core.Me.Location)).FirstOrDefault();
            if (chosen != null)
            {
                towerId = chosen.ObjectId;
                selected = chosen.Location;
                reason = "Sinsphere tower";
            }
            else
            {
                towerId = 0;
            }
        }
        if (!selected.HasValue && now < gazeUntil)
        {
            // Stop ordinary approach through the same input channel before facing.
            selected = positionReason == "Enthrall facing" && goal.HasValue ? goal : Core.Me.Location;
            reason = "Enthrall facing";
        }
        if (!selected.HasValue && now < visionUntil)
        {
            // Maximize perpendicular distance from the charge line. Retain the
            // nearest side while valid; the charge is proximity damage, not a 40y avoid.
            var ray = Center - visionOrigin;
            float length = MathF.Sqrt(ray.X * ray.X + ray.Z * ray.Z);
            var side = length > 1 ? new Vector3(ray.Z / length, 0, -ray.X / length) * 18.5f : Vector3.Zero;
            selected = new[] { Center + side, Center - side }.Where(SegmentClear)
                .OrderBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
            reason = "Beatific Vision";
        }
        if (!selected.HasValue && spreads.ContainsKey(Core.Me.ObjectId))
        {
            // Keep a valid edge placement instead of chasing small party movements.
            selected = positionReason == "Drop of Light placement" && goal.HasValue && SegmentClear(goal.Value) &&
                party.All(a => a.ObjectId == Core.Me.ObjectId || a.Distance2D(goal.Value) >= 10.5f) ? goal :
                Ring(18).Where(SegmentClear)
                .OrderByDescending(p => party.Where(a => a.ObjectId != Core.Me.ObjectId)
                    .Select(a => p.Distance2D(a.Location)).DefaultIfEmpty(20).Min())
                .ThenBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
            reason = "Drop of Light placement";
        }
        if (!selected.HasValue && now < stackUntil)
        {
            // The stack carrier has no verified public marker here. Use a living
            // party cluster as a fallback, subject to the same hazard checks.
            var participant = party.FirstOrDefault(a => a.ObjectId == stackParticipant && SegmentClear(a.Location));
            participant ??= party.Where(a => a.ObjectId != Core.Me.ObjectId && SegmentClear(a.Location))
                .OrderByDescending(a => party.Count(p => p.Distance2D(a.Location) < 2.4f))
                .ThenBy(a => a.Distance2D(Core.Me.Location)).FirstOrDefault();
            if (participant != null)
            {
                // Keep one valid participant for this cast instead of alternating
                // between equally sized clusters every tick. Refresh its position
                // only when the held point no longer covers that participant.
                stackParticipant = participant.ObjectId;
                selected = positionReason == "Light Pillar participants" && goal.HasValue && SegmentClear(goal.Value) &&
                    participant.Distance2D(goal.Value) < 1.5f ? goal : participant.Location;
            }
            reason = "Light Pillar participants";
        }
        // Retain a valid nearby goal to avoid small direction changes every pulse.
        if (reason == positionReason && goal.HasValue && selected.HasValue &&
            goal.Value.Distance2D(selected.Value) < 1 && SegmentClear(goal.Value))
        {
            selected = goal;
        }

        if (reason != positionReason || selected.HasValue != goal.HasValue ||
            selected.HasValue && goal.HasValue && selected.Value.Distance2D(goal.Value) > 1)
        {
            Logger.Information($"[Immaculate] Position owner={reason ?? "ordinary/native"} goal={selected} player={Core.Me.Location}.");
        }

        goal = selected;
        positionReason = reason;
    }

    private void UpdateGaze(BattleCharacter boss, bool moving)
    {
        bool gaze = DateTime.UtcNow < gazeUntil && !moving &&
            !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;
        if (gaze)
        {
            // This coordinator owns the facing channel only while stationary.
            // Auto-facing actions must not turn the player back into the gaze.
            if (!autoFaceBorrowed)
            {
                previousAutoFace = GameSettingsManager.FaceTargetOnAction;
                autoFaceBorrowed = true;
            }
            GameSettingsManager.FaceTargetOnAction = false;
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(400), "Immaculate Enthrall");
            facingLeased = true;
            MovementManager.SetFacing2D(Core.Me.Location * 2 - gazeOrigin);
        }
        else
        {
            ReleaseFacing();
        }
    }

    private void PrioritizeTetheredAdd(BattleCharacter[] actors)
    {
        var caster = actors.FirstOrDefault(a => a.IsCasting && a.CastingSpellId == AddSword);
        var target = caster == null ? null : actors.FirstOrDefault(a => a.ObjectId == caster.SpellCastInfo.TargetId &&
            a.IsAlive && a.IsTargetable && a.CanAttack);
        if (target == null)
        {
            ReleaseAddProvider();
            return;
        }
        if (addProvider != null && !ReferenceEquals(CombatTargeting.Instance.Provider, addProvider))
        {
            addProvider = null;
            addProviderLost = true; // A foreign successor owns targeting until this cast ends.
        }
        if (addProvider == null && !addProviderLost && !CombatTargeting.Instance.Locked)
        {
            addProvider = new AddTargetProvider(this, CombatTargeting.Instance.Provider);
            CombatTargeting.Instance.Provider = addProvider;
        }
        if (priorityAdd == target.ObjectId)
        {
            return;
        }

        priorityAdd = target.ObjectId;
        // Reorder OrderBot's eligible targets rather than competing with its POI.
        // Combat and approach then follow the same tethered add.
        Logger.Information($"[Immaculate] Tethered add priority={priorityAdd:X} npc={target.NpcId} providerOwned={addProvider != null}.");
    }

    private sealed class AddTargetProvider(Immaculate owner, ITargetingProvider previous) : ITargetingProvider
    {
        internal ITargetingProvider Previous => previous;
        public List<BattleCharacter> GetObjectsByWeight()
        {
            var ranked = previous?.GetObjectsByWeight() ?? [];
            return owner.active && ReferenceEquals(owner.addProvider, this) && owner.priorityAdd != 0
                ? ranked.OrderByDescending(a => a.ObjectId == owner.priorityAdd).ToList() : ranked;
        }
    }

    private void ReleaseAddProvider()
    {
        if (addProvider != null && ReferenceEquals(CombatTargeting.Instance.Provider, addProvider))
        {
            bool locked = CombatTargeting.Instance.Locked;
            CombatTargeting.Instance.Locked = false;
            CombatTargeting.Instance.Provider = addProvider.Previous;
            CombatTargeting.Instance.Locked = locked;
        }
        addProvider = null;
        addProviderLost = false;
        priorityAdd = 0;
    }

    private static bool HasMarker(BattleCharacter actor, uint id) => actor.VfxContainer.IsValid &&
        actor.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == id);

    private static bool SegmentClear(Vector3 destination)
    {
        Vector3 start = Core.Me.Location;
        int count = Math.Max(1, (int)Math.Ceiling(start.Distance2D(destination) / 0.5f));
        for (int i = 1; i <= count; i++)
        {
            float t = i / (float)count;
            var p = start + (destination - start) * t;
            if (p.Distance2D(Center) > ArenaRadius || AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool OrdinaryWaypointAllowed(Vector3 point)
    {
        bool allowed = SegmentClear(point);
        current?.RecordWaypoint(point, allowed ? "ordinary admitted" : "ordinary blocked");
        return allowed;
    }

    private static Vector3? ObserveNativeWaypoint(Vector3 point)
    {
        // The adapter calls this on the actual native waypoint immediately before
        // forwarding it. This observer never replaces or rejects native geometry.
        current?.RecordWaypoint(point, "native");
        return point;
    }

    private void RecordWaypoint(Vector3 point, string owner)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled ||
            owner == lastWaypointOwner && point.Distance2D(lastWaypoint) < 0.25f)
        {
            return;
        }

        lastWaypointOwner = owner;
        lastWaypoint = point;
        Logger.Information($"[Immaculate] Waypoint owner={owner} destination={point} player={Core.Me.Location}.");
    }

    private void UpdatePositionLease(bool enabled)
    {
        if (enabled)
        {
            foreach (var flag in PositionFlags)
            {
                CapabilityManager.Update(CapabilityHandle, flag, TimeSpan.FromMilliseconds(400), positionReason);
            }

            positionLeased = true;
        }
        else if (positionLeased)
        {
            foreach (var flag in PositionFlags)
            {
                CapabilityManager.Clear(CapabilityHandle, flag, "Immaculate positioning released");
            }

            positionLeased = false;
        }
    }

    private void ReleaseFacing()
    {
        if (facingLeased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Facing, "Immaculate gaze ended");
        }

        facingLeased = false;
        if (autoFaceBorrowed && !GameSettingsManager.FaceTargetOnAction)
        {
            GameSettingsManager.FaceTargetOnAction = previousAutoFace;
        }

        autoFaceBorrowed = false;
    }

    private void CaptureActors(BattleCharacter[] actors, BattleCharacter[] party, DateTime now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || now < nextActorSample)
        {
            return;
        }

        nextActorSample = now.AddMilliseconds(250);
        foreach (var actor in actors.Concat(party).GroupBy(a => a.ObjectId).Select(g => g.First())
            .Where(a => a.Distance2D(Center) < 50))
        {
            string markers = actor.VfxContainer.IsValid
                ? string.Join(",", actor.VfxContainer.LockOns.Where(v => v != null && v.IsValid).Select(v => v.Id)) : "unavailable";
            string vfx = actor.VfxContainer.IsValid
                ? string.Join(",", actor.VfxContainer.Vfx.Where(v => v != null && v.IsValid).Select(v => v.Id)) : "unavailable";
            var rawCast = actor.SpellCastInfo;
            string state = $"{actor.BaseId}/{actor.IsAlive}/{actor.IsTargetable}/{actor.CanAttack}/{markers}/vfx={vfx}/target={actor.CurrentTargetId:X}/rawAction={rawCast?.ActionId}/rawTarget={rawCast?.TargetId:X}";
            if (actorStates.TryGetValue(actor.ObjectId, out string old) && old == state)
            {
                continue;
            }

            actorStates[actor.ObjectId] = state;
            Logger.Information($"[Immaculate] Actor={actor.ObjectId:X} base={actor.BaseId:X} npc={actor.NpcId} state={state} position={actor.Location} heading={actor.Heading:F3} target={actor.CurrentTargetId:X} party={party.Any(a => a.ObjectId == actor.ObjectId)} lockCenter={actor.LockOn.Center} omenCenter={actor.OmenMatrix.Center} avfxCenter={actor.AVFX.Center}.");
        }
        foreach (var actor in GameObjectManager.GameObjects.Where(a => a.IsValid && a.IsVisible &&
            a.Distance2D(Center) < 25 && a is not BattleCharacter))
        {
            string state = $"{actor.BaseId}/{actor.NpcId}/{actor.IsTargetable}";
            if (actorStates.TryGetValue(actor.ObjectId, out string old) && old == state)
            {
                continue;
            }

            actorStates[actor.ObjectId] = state;
            Logger.Information($"[Immaculate] Environment actor={actor.ObjectId:X} type={actor.GetType().Name} base={actor.BaseId:X} npc={actor.NpcId:X} targetable={actor.IsTargetable} position={actor.Location}.");
        }
    }

    private void ResetCombat()
    {
        active = travelling = false;
        movement?.Detach();
        movement = null;
        UpdatePositionLease(false);
        ReleaseFacing();
        ReleaseAddProvider();
        hazards.Clear();
        casting.Clear();
        spreads.Clear();
        observedSpreadMarkers.Clear();
        actorStates.Clear();
        circles = [];
        polygons = [];
        ribbonUntil = stackUntil = gazeUntil = visionUntil = default;
        secondFormEntered = false;
        goal = null;
        positionReason = null;
        towerId = stackParticipant = 0;
        lastWaypointOwner = null;
        unsupportedMover = false;
    }

    private void Release()
    {
        ResetCombat();
        if (sideStepBorrowed && SidestepPlugin != null && !SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = previousSideStep;
        }

        sideStepBorrowed = false;
    }

    private static IEnumerable<Vector3> Ring(float radius)
    {
        for (int i = 0; i < 32; i++)
        {
            float angle = i * MathF.PI / 16;
            yield return Center + new Vector3(MathF.Sin(angle) * radius, 0, MathF.Cos(angle) * radius);
        }
    }

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -0.5f), new(width / 2, -0.5f), new(width / 2, length), new(-width / 2, length)];

    private static Vector2[] Sector(float inner, float outer, float halfAngle)
    {
        var points = new List<Vector2>();
        for (float degrees = -halfAngle; degrees <= halfAngle; degrees += 1)
        {
            float angle = degrees * MathF.PI / 180;
            points.Add(new Vector2(MathF.Sin(angle) * outer, MathF.Cos(angle) * outer));
        }
        for (float degrees = halfAngle; degrees >= -halfAngle; degrees -= 1)
        {
            float angle = degrees * MathF.PI / 180;
            points.Add(new Vector2(MathF.Sin(angle) * inner, MathF.Cos(angle) * inner));
        }
        return points.ToArray();
    }
}
