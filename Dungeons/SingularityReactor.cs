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
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>Coordinates movement, hazards and routine handoffs in the story-mode Thordan trial.</summary>
public sealed class SingularityReactor : AbstractDungeon
{
    internal const string HandlerRevision = "singularity-candidate-1";
    private static readonly Vector3 Center = Vector3.Zero;
    private const float ArenaRadius = 23.5f;
    private static readonly HashSet<uint> AttackableNames = [3632, 3633, 3634, 3635, 4386, 4387];
    private static SingularityReactor current;
    private bool sideStepBorrowed, previousSideStep, active, engaged, servicingRoutine, unsupportedMover;
    private bool autoFaceBorrowed, previousAutoFace, facingLeased, positionLeased, travelling, gapCloserLeased;
    private DateTime gazeUntil;
    private Vector3 gazeOrigin;
    private Vector3? gazeHold;
    private readonly CapabilityManagerHandle gazeHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle positionHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle gapCloserHandle = CapabilityManager.CreateNewHandle();
    private ChrysalisMovement movement;
    private readonly Dictionary<uint, uint> casting = [];
    private readonly List<Hazard> hazards = [];
    private readonly List<Tower> towers = [];
    private readonly List<Charge> charges = [];
    private readonly Dictionary<(uint Actor, uint Marker), DateTime> spreads = [];
    private readonly HashSet<(uint Actor, uint Marker)> seenSpreadMarkers = [];
    private static readonly uint[] SpreadMarkers = [21, 29];
    private readonly Dictionary<uint, DateTime> orbExpiries = [];
    private readonly Dictionary<(uint Action, uint Owner), Hazard> movingHazards = [];
    private readonly Dictionary<uint, Hazard> chargeHazards = [];
    private uint selectedTower;
    private uint selectedCharge;
    private Vector3? chargeGoal;
    private DateTime nextChargeSearch;
    private Hazard[] circles = [], polygons = [];

    // Detached geometry avoids making an untargetable helper a navigation attraction.
    private sealed class Hazard
    {
        internal uint Action, Owner;
        internal Vector3 Origin;
        internal float Heading, Radius, Length;
        internal Vector2[] Points;
        internal DateTime Expires;
    }

    private sealed class Tower
    {
        internal uint Owner;
        internal Vector3 Origin;
        internal DateTime Expires;
    }

    private sealed class Charge
    {
        internal uint Owner, Target;
        internal Vector3 Origin;
        internal DateTime Expires;
    }

    /// <inheritdoc />
    public override ZoneId ZoneId => (ZoneId)437;
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc />
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        AvoidanceManager.AddAvoidLocation<Hazard>(() => active, () => Center, 30,
            h => h.Radius, h => h.Origin, () => circles);
        AvoidanceManager.AddAvoidPolygon<Hazard>(() => active, () => Center, 30,
            h => -h.Heading, _ => 1, _ => 8, h => h.Points, h => h.Origin, () => polygons);
        // The circular arena has a 24y boundary; leave half a yalm for the player.
        AvoidanceHelpers.AddAvoidDonut(() => active, () => Center, 80, ArenaRadius);
        CheckLifecycle();
        Logger.Information($"[Singularity] Registered {HandlerRevision}: cast geometry, gaze and targetless healing bridge.");
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public override async Task<bool> RunAsync()
    {
        if (servicingRoutine)
            return false;
        // Party readiness occurs before the pull and is distinct from the queue pop.
        await UsefulTasks.HandleReadyCheck();
        CheckLifecycle();
        bool hasTarget = active && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(a => a.IsValid && a.IsAlive && a.IsTargetable && a.CanAttack &&
                AttackableNames.Contains(a.NpcId) && a.Distance2D(Center) < 30);
        bool serviceTargetless = active && engaged && !hasTarget && !travelling && !AvoidanceManager.IsRunningOutOfAvoid;
        if (serviceTargetless)
        {
            // During untargetable knight choreography OrderBot may have no combat POI.
            // Let the existing routine heal and raise without selecting actions here.
            servicingRoutine = true;
            try { await CommonTasks.ExecuteCoroutine(BrainBehavior.CombatLogic); }
            finally { servicingRoutine = false; }
            CheckLifecycle();
        }
        return movement?.IsEscaping == true || travelling && !AvoidanceManager.IsRunningOutOfAvoid;
    }

    /// <summary>Keeps the SideStep lease current while profile hooks yield.</summary>
    public static void CheckLifecycle()
    {
        if (current == null)
            return;
        if (!TreeRoot.IsRunning || !DutyManager.InInstance || WorldManager.ZoneId != 437)
        {
            ResetActive();
            return;
        }
        // Keep suppression through targetless waves, wipes and loading: restoring
        // only on exit prevents generic avoidance competing with trial ownership.
        if (!current.sideStepBorrowed && SidestepPlugin != null)
        {
            current.previousSideStep = SidestepPlugin.Enabled;
            current.sideStepBorrowed = true;
        }
        if (SidestepPlugin != null && SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = false;
        current.Update();
    }

    private void Update()
    {
        if (Core.Me == null || !Core.Me.IsAlive || CommonBehaviors.IsLoading)
        {
            ResetCombat();
            return;
        }
        active = true;
        // Gap closers can bypass hazard admission; their lease is independent of
        // short tower and gaze requests so one cleanup cannot release another.
        CapabilityManager.Update(gapCloserHandle, CapabilityFlags.GapCloser,
            TimeSpan.FromSeconds(1), "Singularity boss safety");
        gapCloserLeased = true;
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
        }
        if (movement == null && !unsupportedMover)
        {
            if (Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            {
                // Native escape and ordinary travel share one actor channel. Retire
                // blocked ordinary navigation so its unstuck path cannot bypass a hazard.
                movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: OrdinarySegmentClear,
                    retireBlockedOrdinaryNavigation: true);
                Navigator.PlayerMover = movement;
            }
            else
            {
                unsupportedMover = true;
                Logger.Warning("[Singularity] Unsupported movement owner; not installing a competing mover.");
            }
        }
        var now = DateTime.UtcNow;
        hazards.RemoveAll(h => h.Expires <= now);
        towers.RemoveAll(t => t.Expires <= now);
        charges.RemoveAll(c => c.Expires <= now);
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        // The untargetable intermission can temporarily drop combat flags. Once
        // the party has pulled, retain engagement for this visit so healing is
        // not suppressed merely because no hostile is currently targetable.
        if (Core.Me.InCombat || actors.Any(a => a.IsAlive && a.InCombat &&
            PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            engaged = true;
        var ids = actors.Select(a => a.ObjectId).ToHashSet();
        foreach (var id in casting.Keys.Where(id => !ids.Contains(id)).ToArray())
            casting.Remove(id);
        bool changed = false;
        foreach (var actor in actors)
        {
            uint action = actor.IsCasting ? actor.CastingSpellId : 0;
            if (action == 0)
            {
                casting.Remove(actor.ObjectId);
                continue;
            }
            bool first = !casting.TryGetValue(actor.ObjectId, out uint previous) || previous != action;
            casting[actor.ObjectId] = action;
            if (!first || actor.SpellCastInfo == null)
                continue;
            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            if (action == 4201 && actor.BaseId == 0x1073)
            {
                // Two captured gazes each applied vulnerability while facing Thordan.
                gazeOrigin = actor.Location;
                // The two captured effects resolved about 1.4s after the
                // estimated cast end; keep the facing lease through that delay.
                gazeUntil = ResolutionExpiry(action, finish);
                gazeHold = null;
                continue;
            }
            if (action == 4215)
            {
                // Captured helper casts identify the actual tower centers, not the
                // central choreography actor. Reconsider occupancy on every pulse.
                towers.Add(new Tower { Owner = actor.ObjectId, Origin = actor.SpellCastInfo.CastLocation,
                    Expires = ResolutionExpiry(action, finish) });
                continue;
            }
            if (action == 4223)
            {
                // The helper names the actual carrier; the charge line follows
                // that player until impact rather than using the helper's initial heading.
                charges.Add(new Charge { Owner = actor.ObjectId, Target = actor.SpellCastInfo.TargetId,
                    Origin = actor.Location, Expires = ResolutionExpiry(action, finish) });
                changed = true;
                continue;
            }
            if (action == 4195 && actor.SpellCastInfo.TargetId != 0 && actor.SpellCastInfo.TargetId != 0xE0000000)
            {
                var key = (actor.SpellCastInfo.TargetId, 21u);
                // A direct helper target is a second tell for Lightning Storm.
                // Keep an already armed marker's deadline instead of restarting it.
                if (!spreads.ContainsKey(key))
                    spreads[key] = finish.AddSeconds(1);
                changed = true;
                continue;
            }
            var h = new Hazard { Action = action, Owner = actor.ObjectId, Origin = actor.Location,
                Heading = actor.Heading, Expires = ResolutionExpiry(action, finish) };
            switch (action)
            {
                case 4197: // Meteorain: the first baseline death stood in overlapping ground circles.
                    h.Origin = actor.SpellCastInfo.CastLocation;
                    h.Radius = 6.5f;
                    break;
                case 4213: // Collapse grows during its cast; overlapping impacts killed the player.
                    h.Origin = actor.SpellCastInfo.CastLocation;
                    h.Radius = 10.5f;
                    break;
                case 4192:
                case 4193:
                    h.Points = Sector(35.3f, 11);
                    break;
                case 4222:
                    h.Points = Rectangle(13, 54.7f);
                    break;
                case 4225:
                    // The captured helper appeared at the arena edge before
                    // Skyward Leap. Do not apply the falloff model to an
                    // unexplained central helper, where it would cover all floor.
                    if (h.Origin.Distance2D(Center) < 18)
                        continue;
                    h.Radius = 38.5f;
                    break;
                case 4218: case 4219: case 4220: case 4221:
                    // The captured rotating Heavy Impact stages overlap in time;
                    // each helper owns its own 270-degree sector and lifetime.
                    float inner = action == 4218 ? 0 : action == 4219 ? 5.5f : action == 4220 ? 11.5f : 17.5f;
                    float outer = action == 4218 ? 6.5f : action == 4219 ? 12.5f : action == 4220 ? 18.5f : 27.5f;
                    h.Points = RingSector(inner, outer, 137);
                    break;
                case 4228: case 4229: case 4230: case 4231:
                    h.Origin = actor.SpellCastInfo.CastLocation;
                    h.Radius = action - 4225 + 0.5f;
                    break;
                default:
                    continue;
            }
            hazards.Add(h);
            changed = true;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[Singularity] Hazard action={action} actor={actor.ObjectId:X} origin={h.Origin} heading={h.Heading:F3} expires={h.Expires:O}.");
        }
        var partyHazards = CapturePartyHazards(actors, now, out bool spreadChanged);
        circles = hazards.Where(h => h.Radius > 0).Concat(partyHazards).ToArray();
        var chargeLanes = CaptureChargeHazards(actors, out bool chargeChanged);
        polygons = hazards.Where(h => h.Points != null).Concat(chargeLanes).ToArray();
        if (changed || spreadChanged || chargeChanged)
            AvoidanceManager.Pulse();
        SelectPosition(actors, now);
        movement?.Update();
        UpdateGaze(now);
    }

    private Hazard[] CapturePartyHazards(BattleCharacter[] actors, DateTime now, out bool changed)
    {
        var party = actors.Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a.IsValid && a.IsAlive && (a.ObjectId == Core.Me.ObjectId ||
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var visible = new HashSet<(uint Actor, uint Marker)>();
        var readable = new HashSet<uint>();
        foreach (var actor in party)
        {
            if (!actor.VfxContainer.IsValid)
                continue;
            readable.Add(actor.ObjectId);
            var markerIds = actor.VfxContainer.LockOns.Where(v => v != null && v.IsValid).Select(v => v.Id).ToArray();
            foreach (uint marker in SpreadMarkers)
            {
                if (markerIds.Contains(marker))
                    visible.Add((actor.ObjectId, marker));
            }
        }
        // An unreadable VFX sample is not a marker disappearance: treating it
        // as one would repeatedly re-arm and extend an already resolved spread.
        seenSpreadMarkers.RemoveWhere(key => readable.Contains(key.Actor) && !visible.Contains(key));
        changed = false;
        foreach (var key in visible)
        {
            if (!seenSpreadMarkers.Add(key))
                continue;
            // A lingering icon must not continually extend the impact window.
            // These tell-to-impact estimates remain subject to live verification.
            if (!spreads.ContainsKey(key))
                spreads[key] = now.AddSeconds(key.Marker == 21 ? 4.5 : 3.2);
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[Singularity] Spread marker={key.Marker} actor={key.Actor:X} until={spreads[key]:O}.");
            changed = true;
        }
        // An unobserved party wrapper is not proof that its marker disappeared.
        foreach (var key in spreads.Where(pair => pair.Value <= now)
            .Select(pair => pair.Key).ToArray())
            spreads.Remove(key);

        // A marked player must separate from everyone, including unmarked allies;
        // an unmarked player only needs to avoid marked carriers. The player never
        // becomes its own avoid, and every circle follows its current carrier.
        float ownRadius = spreads.Where(pair => pair.Key.Actor == Core.Me.ObjectId)
            .Select(pair => pair.Key.Marker == 21 ? 5.5f : 6.5f).DefaultIfEmpty(0).Max();
        var result = new List<Hazard>();
        var present = new HashSet<(uint Action, uint Owner)>();
        bool geometryChanged = false;
        void Track(uint action, uint owner, Vector3 origin, float radius)
        {
            var key = (action, owner);
            present.Add(key);
            if (!movingHazards.TryGetValue(key, out var h))
            {
                h = new Hazard { Action = action, Owner = owner };
                movingHazards.Add(key, h);
                geometryChanged = true;
            }
            // Keep the same envelope while its carrier moves; replacing every
            // object on every pulse can churn the native escape path.
            h.Origin = origin;
            h.Radius = radius;
            result.Add(h);
        }
        foreach (var actor in party.Where(a => a.ObjectId != Core.Me.ObjectId))
        {
            float markedRadius = spreads.Where(pair => pair.Key.Actor == actor.ObjectId)
                .Select(pair => pair.Key.Marker == 21 ? 5.5f : 6.5f).DefaultIfEmpty(0).Max();
            float radius = Math.Max(ownRadius, markedRadius);
            if (radius > 0)
                Track(0, actor.ObjectId, actor.Location, radius);
        }
        // The ice object is authoritative after placement, rather than a prediction
        // kept alive for an arbitrary duration after its marker resolves.
        foreach (var ice in GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x1E9729))
            Track(4234, ice.ObjectId, ice.Location, 6.5f);
        var orbs = actors.Where(a => a.IsValid && a.IsVisible && a.BaseId == 0x1081).ToArray();
        foreach (var id in orbExpiries.Keys.Where(id => !orbs.Any(o => o.ObjectId == id)).ToArray())
            orbExpiries.Remove(id);
        foreach (var orb in orbs)
        {
            if (!orbExpiries.TryGetValue(orb.ObjectId, out var expiry))
            {
                // Bright Flare is an uncast event; a newly observed sphere is
                // hazardous for a bounded window and is removed on despawn.
                expiry = now.AddSeconds(5.2);
                orbExpiries[orb.ObjectId] = expiry;
                changed = true;
            }
            if (now < expiry)
                Track(4211, orb.ObjectId, orb.Location, 6.5f);
        }
        foreach (var key in movingHazards.Keys.Where(key => !present.Contains(key)).ToArray())
        {
            movingHazards.Remove(key);
            geometryChanged = true;
        }
        changed |= geometryChanged;
        return result.ToArray();
    }

    private Hazard[] CaptureChargeHazards(BattleCharacter[] actors, out bool changed)
    {
        var result = new List<Hazard>();
        var present = new HashSet<uint>();
        changed = false;
        foreach (var charge in charges.Where(c => c.Target != Core.Me.ObjectId))
        {
            var caster = actors.FirstOrDefault(a => a.ObjectId == charge.Owner);
            if (caster != null)
                charge.Origin = caster.Location;
            var target = actors.FirstOrDefault(a => a.ObjectId == charge.Target);
            if (target == null || !target.IsAlive)
                continue;
            var delta = target.Location - charge.Origin;
            float length = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            if (length < 0.1f)
                continue;
            present.Add(charge.Owner);
            if (!chargeHazards.TryGetValue(charge.Owner, out var lane))
            {
                lane = new Hazard { Action = 4223, Owner = charge.Owner };
                chargeHazards.Add(charge.Owner, lane);
                changed = true;
            }
            lane.Origin = charge.Origin;
            lane.Heading = MathF.Atan2(delta.X, delta.Z);
            if (lane.Points == null || MathF.Abs(lane.Length - length) > 0.1f)
            {
                lane.Length = length;
                lane.Points = Rectangle(13, length + 0.5f);
            }
            result.Add(lane);
        }
        foreach (uint id in chargeHazards.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            chargeHazards.Remove(id);
            changed = true;
        }
        return result.ToArray();
    }

    private void SelectPosition(BattleCharacter[] actors, DateTime now)
    {
        uint previousTower = selectedTower;
        uint previousCharge = selectedCharge;
        bool wasTravelling = travelling;
        if (now < gazeUntil && movement?.IsCurrent == true && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            // Hold the current safe position while facing away. A native escape
            // can preempt it; after escape, reacquire the new position instead of
            // returning to a stale point that may now be hazardous.
            gazeHold ??= Core.Me.Location;
            if (OrdinarySegmentClear(gazeHold.Value))
            {
                foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
                    CapabilityManager.Update(positionHandle, flag, TimeSpan.FromMilliseconds(400), "Singularity gaze hold");
                positionLeased = true;
                selectedTower = 0;
                movement.Request(gazeHold.Value, 0.5f);
                travelling = false;
                if (previousTower != 0)
                    Logger.Information($"[Singularity] Tower {previousTower:X} preempted by gaze at {Core.Me.Location}.");
                return;
            }
        }
        else
            gazeHold = null;
        var party = actors.Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a.IsValid && a.IsAlive && (a.ObjectId == Core.Me.ObjectId ||
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var ownCharge = charges.FirstOrDefault(c => c.Target == Core.Me.ObjectId);
        if (ownCharge != null)
        {
            var caster = actors.FirstOrDefault(a => a.ObjectId == ownCharge.Owner);
            if (caster != null)
                ownCharge.Origin = caster.Location;
            bool SafeBait(Vector3 p) => OrdinarySegmentClear(p) && party.Where(a => a.ObjectId != Core.Me.ObjectId)
                .All(a => DistanceToSegment(a.Location, ownCharge.Origin, p) >= 6.5f);
            if (selectedCharge != ownCharge.Owner || chargeGoal.HasValue && !SafeBait(chargeGoal.Value) ||
                !chargeGoal.HasValue && now >= nextChargeSearch)
            {
                // A short line near the caster reduces the proximity hit and is
                // less likely to cross allies. Validate the whole approach as well.
                chargeGoal = Ring(18).Concat(Ring(20)).Where(SafeBait)
                    .OrderBy(p => p.Distance2D(ownCharge.Origin) + 0.7f * p.Distance2D(Core.Me.Location))
                    .Cast<Vector3?>().FirstOrDefault();
                selectedCharge = ownCharge.Owner;
                nextChargeSearch = now.AddMilliseconds(250);
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                    Logger.Information($"[Singularity] Charge owner={selectedCharge:X} destination={chargeGoal} player={Core.Me.Location}.");
            }
            if (chargeGoal.HasValue && movement?.IsCurrent == true && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
                    CapabilityManager.Update(positionHandle, flag, TimeSpan.FromMilliseconds(400), "Singularity charge bait");
                positionLeased = true;
                selectedTower = 0;
                movement.Request(chargeGoal.Value, 1.0f);
                travelling = Core.Me.Location.Distance2D(chargeGoal.Value) > 1.0f;
                if (previousCharge == selectedCharge && wasTravelling && !travelling)
                    Logger.Information($"[Singularity] Charge owner={selectedCharge:X} arrived at {Core.Me.Location}.");
                if (previousTower != 0)
                    Logger.Information($"[Singularity] Tower {previousTower:X} preempted by targeted charge.");
                return;
            }
        }
        else
        {
            selectedCharge = 0;
            chargeGoal = null;
            if (previousCharge != 0)
                Logger.Information($"[Singularity] Charge owner={previousCharge:X} released at {Core.Me.Location}.");
        }
        bool Free(Tower t) => !party.Any(a => a.ObjectId != Core.Me.ObjectId && a.Distance2D(t.Origin) < 2);
        var tower = towers.FirstOrDefault(t => t.Owner == selectedTower && Free(t) && OrdinarySegmentClear(t.Origin));
        tower ??= towers.Where(t => Free(t) && OrdinarySegmentClear(t.Origin))
            .OrderBy(t => Core.Me.Location.Distance2D(t.Origin)).FirstOrDefault();
        bool take = ownCharge == null && tower != null && movement?.IsCurrent == true && !AvoidanceManager.IsRunningOutOfAvoid &&
            now >= gazeUntil;
        selectedTower = take ? tower.Owner : 0;
        if (take)
        {
            foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
                CapabilityManager.Update(positionHandle, flag, TimeSpan.FromMilliseconds(400), "Singularity tower");
            positionLeased = true;
            movement.Request(tower.Origin, 1.2f);
            travelling = Core.Me.Location.Distance2D(tower.Origin) > 1.2f;
            if (previousTower != selectedTower)
                Logger.Information($"[Singularity] Tower owner={selectedTower:X} destination={tower.Origin} player={Core.Me.Location}.");
            else if (wasTravelling && !travelling)
                Logger.Information($"[Singularity] Tower owner={selectedTower:X} arrived at {Core.Me.Location}.");
        }
        else
        {
            if (positionLeased)
            {
                foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
                    CapabilityManager.Clear(positionHandle, flag, "Singularity tower released");
                movement?.Request(null, 0);
            }
            positionLeased = false;
            travelling = false;
            if (previousTower != 0)
                Logger.Information($"[Singularity] Tower owner={previousTower:X} released at {Core.Me.Location}.");
        }
    }

    private void UpdateGaze(DateTime now)
    {
        if (now < gazeUntil)
        {
            if (!autoFaceBorrowed)
            {
                previousAutoFace = GameSettingsManager.FaceTargetOnAction;
                autoFaceBorrowed = true;
            }
            GameSettingsManager.FaceTargetOnAction = false;
            CapabilityManager.Update(gazeHandle, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(400), "Singularity gaze");
            facingLeased = true;
            if (!travelling && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
            {
                var away = Core.Me.Location * 2 - gazeOrigin;
                if (away.Distance2D(Core.Me.Location) < 0.1f)
                    away = Core.Me.Location + new Vector3(1, 0, 0);
                MovementManager.SetFacing2D(away);
            }
        }
        else
            ReleaseFacing();
    }

    private void ReleaseFacing()
    {
        if (facingLeased)
            CapabilityManager.Clear(gazeHandle, CapabilityFlags.Facing, "Singularity gaze ended");
        facingLeased = false;
        if (autoFaceBorrowed && !GameSettingsManager.FaceTargetOnAction)
            GameSettingsManager.FaceTargetOnAction = previousAutoFace;
        autoFaceBorrowed = false;
    }

    private static bool OrdinarySegmentClear(Vector3 destination)
    {
        var start = Core.Me.Location;
        int count = Math.Max(1, (int)Math.Ceiling(start.Distance2D(destination) / 0.5f));
        for (int i = 1; i <= count; ++i)
        {
            var p = start + (destination - start) * (i / (float)count);
            if (p.Distance2D(Center) > ArenaRadius || AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p)))
                return false;
        }
        return true;
    }

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -0.5f), new(width / 2, -0.5f), new(width / 2, length), new(-width / 2, length)];

    private static DateTime ResolutionExpiry(uint action, DateTime estimatedFinish)
    {
        // The November 16 capture placed Mercy, Gaze, Collapse, Spiral Thrust,
        // Pierce and Skyward effects after their estimated cast ends. Retain each
        // envelope through its observed delay plus a small cleanup margin.
        double seconds = action switch
        {
            4201 => 2,
            4213 => 1.5,
            4192 or 4193 => 1.7,
            4222 or 4223 or 4225 => 2.5,
            4218 or 4219 or 4220 or 4221 => 1.4,
            _ => 0.8
        };
        return estimatedFinish.AddSeconds(seconds);
    }

    private static IEnumerable<Vector3> Ring(float radius)
    {
        for (int i = 0; i < 32; i++)
        {
            float angle = i * MathF.PI / 16;
            yield return Center + new Vector3(MathF.Sin(angle) * radius, 0, MathF.Cos(angle) * radius);
        }
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        float dx = end.X - start.X, dz = end.Z - start.Z;
        float lengthSquared = dx * dx + dz * dz;
        float t = lengthSquared < 0.01f ? 0 : Math.Clamp(((point.X - start.X) * dx + (point.Z - start.Z) * dz) / lengthSquared, 0, 1);
        float x = start.X + t * dx - point.X, z = start.Z + t * dz - point.Z;
        return MathF.Sqrt(x * x + z * z);
    }

    private static Vector2[] Sector(float radius, float halfAngle)
    {
        var points = new List<Vector2> { Vector2.Zero };
        for (float degrees = -halfAngle; degrees <= halfAngle; degrees += 1)
        {
            float radians = degrees * MathF.PI / 180;
            points.Add(new Vector2(radius * MathF.Sin(radians), radius * MathF.Cos(radians)));
        }
        points.Add(Vector2.Zero);
        return points.ToArray();
    }

    private static Vector2[] RingSector(float inner, float outer, float halfAngle)
    {
        var points = new List<Vector2>();
        for (float degrees = -halfAngle; degrees <= halfAngle; degrees += 2)
        {
            float angle = degrees * MathF.PI / 180;
            points.Add(new Vector2(outer * MathF.Sin(angle), outer * MathF.Cos(angle)));
        }
        for (float degrees = halfAngle; degrees >= -halfAngle; degrees -= 2)
        {
            float angle = degrees * MathF.PI / 180;
            points.Add(new Vector2(inner * MathF.Sin(angle), inner * MathF.Cos(angle)));
        }
        return points.ToArray();
    }

    private void ResetCombat()
    {
        active = false;
        hazards.Clear();
        towers.Clear();
        charges.Clear();
        spreads.Clear();
        seenSpreadMarkers.Clear();
        orbExpiries.Clear();
        movingHazards.Clear();
        chargeHazards.Clear();
        casting.Clear();
        circles = polygons = [];
        gazeUntil = default;
        gazeHold = null;
        selectedTower = 0;
        selectedCharge = 0;
        chargeGoal = null;
        nextChargeSearch = default;
        if (positionLeased)
        {
            foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
                CapabilityManager.Clear(positionHandle, flag, "Singularity interrupted");
        }
        positionLeased = travelling = false;
        if (gapCloserLeased)
            CapabilityManager.Clear(gapCloserHandle, CapabilityFlags.GapCloser, "Singularity interrupted");
        gapCloserLeased = false;
        ReleaseFacing();
        movement?.Detach();
        movement = null;
        unsupportedMover = false;
    }

    /// <summary>Restores the prior SideStep state when this encounter releases it.</summary>
    public static void ResetActive()
    {
        current?.ResetCombat();
        if (current?.sideStepBorrowed == true && SidestepPlugin != null)
            SidestepPlugin.Enabled = current.previousSideStep;
        current = null;
    }

    /// <inheritdoc />
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }
}
