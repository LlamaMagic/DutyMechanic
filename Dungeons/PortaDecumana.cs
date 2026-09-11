using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Planner = DutyMechanic.Dungeons.PortaDecumanaPlanner;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles both Ultima Weapon phases, including overlapping hazards and stack, knockback, and
/// orb positioning. RB owns ordinary avoidance, with bounded recovery for observed path failures.
/// </summary>
public class PortaDecumana : AbstractDungeon
{
    // RB NpcId is the name row used by historical captures, not the actor base/OID.
    // Both Ultima forms share 2137; proximity disambiguates them during the transition.
    private const uint UltimaNpc = 2137;
    private const uint OrbNpc = 2138;
    private static readonly Vector3 FirstArena = new(-772f, -400.0628f, -600f);
    private static readonly Vector3 SecondArena = new(-704f, -185.6595f, 480f);
    private static readonly HashSet<uint> TrackedActions =
    [
        Planner.Geocrush, Planner.TitanLandslide, Planner.UltimaLandslide, Planner.Weight,
        Planner.Eye, Planner.Shriek, Planner.Plume, Planner.Vulcan,
        Planner.RayForward, Planner.RayRight, Planner.RayLeft, Planner.Spread, Planner.Stack,
        Planner.Boom, Planner.Cannon, Planner.Buster, Planner.Explosion
    ];

    // Retain scalar snapshots only. Near-finished casts get a short effect grace; dead/despawned
    // owners and early cancellations clear immediately rather than leaking geometry into retries.
    private readonly Dictionary<(uint Owner, uint Action), Planner.Cast> casts = [];
    private Planner.Plan plan = new([], [], null, "idle", true);
    private Vector3 arena;
    private bool active;
    private bool moving;
    private bool movementLeaseActive;
    private bool sideStepSuppressed;
    private bool sideStepWasEnabled;

    private uint selectedOrbId;
    // Only scalar bot-thread samples survive a frame. Sample over 200ms to avoid amplifying
    // position quantization; discontinuities and lifecycle reset discard stale travel estimates.
    private readonly Dictionary<uint, (Point Position, double Time, Point Velocity)> orbMotion = [];
    private Point? orbIntercept;
    // Contact proximity is evidence, not proof of a soak. Keep it per actor so disappearance
    // caused by another player or an orb collision cannot be reported as our successful soak.
    private readonly Dictionary<uint, float> orbClosestDistances = [];
    private double nextOrbLogTime;
    private string goalKey = "";
    private string lastLoggedPlan = "";
    private double nextWarningTime;
    private bool recovering;
    private double unsafeSince;
    private Point? recoveryDestination;
    private readonly Planner.NavigationResetLatch navigationReset = new();

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.ThePortaDecumana;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [29023]; // Homing Lasers, actual boss cast.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [28998, 28996, 29002, 29022]; // Primal raidwides and Tank Purge, not visual parents.

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        TreeRoot.OnStop += OnBotStop;
        GameEvents.OnMapChanged += OnMapChanged;
        // Exactly one boundary follows the active Ultima arena; no Lahabrea circle clips phase two.
        AvoidanceHelpers.AddAvoidDonut(() => active && !recovering, () => arena, 90f, Planner.ArenaRadius);
        // Avoidance and positioning must use the same stage. Callbacks read scalar snapshots
        // so a later frame cannot substitute a different cast under the planner's destination.
        AvoidanceManager.AddAvoidPolygon<Planner.Hazard>(
            condition: () => active && !recovering, leashPointProducer: () => arena, leashRadius: 40f,
            rotationProducer: _ => 0f, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: hazard => hazard.Polygon().Select(p => new Vector2(p.X - arena.X, p.Y - arena.Z)).ToArray(),
            locationProducer: _ => arena,
            collectionProducer: () => plan.Hazards, priority: AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        TreeRoot.OnStop -= OnBotStop;
        GameEvents.OnMapChanged -= OnMapChanged;
        Reset("Porta Decumana exited");
        RestoreSideStep();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (Core.Player == null || WorldManager.ZoneId != (uint)ZoneId)
        {
            navigationReset.Observe(false, 0);
            Reset("Porta Decumana combat inactive");
            RestoreSideStep();
            return false;
        }
        if (CommonBehaviors.IsLoading || QuestLogManager.InCutscene || !Core.Player.IsAlive)
        {
            navigationReset.Observe(false, 0);
            // Keep the already-acquired trial suppression through death/cutscenes. Re-enabling
            // SideStep here could leave duplicate shapes behind when the checkpoint resumes.
            Reset("Ultima loading, cutscene or death");
            return false;
        }
        BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(bc => bc.IsValid).ToArray();
        BattleCharacter boss = actors.Where(bc => bc.NpcId == UltimaNpc && bc.IsAlive)
            .OrderBy(bc => bc.Distance2D()).FirstOrDefault();
        if (boss == null || boss.Distance2D() > 65f)
        {
            Reset("No nearby Ultima Weapon; solo-duty ownership retained");
            RestoreSideStep();
            return false;
        }
        Vector3 nextArena = boss.Location.Distance2D(FirstArena) < boss.Location.Distance2D(SecondArena) ? FirstArena : SecondArena;
        if (arena != nextArena)
        {
            navigationReset.Observe(false, 0);
            Reset("Ultima arena changed");
            arena = nextArena;
        }
        DisableSideStep();
        // Refresh local collision once after landing on either platform: captures showed SpanRef
        // failures after both transitions and re-entry. The latch survives pre-pull combat resets
        // and also handles checkpoint starts. Registered avoids and the navigation provider stay intact.
        bool platformReady = Core.Player.Distance2D(arena) <= 22f && Math.Abs(Core.Player.Y - arena.Y) < 2f;
        if (navigationReset.Observe(platformReady, DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond))
        {
            AvoidanceManager.ResetNavigation();
            Logger.Information($"[PortaDecumana] Navigation reset after platform landing; phase={(arena == FirstArena ? 1 : 2)}; player={Core.Player.Location}");
        }
        // Disable before the pull, otherwise SideStep can register the first cast before its
        // hook is removed. Its disable callback does not remove already-created avoids.
        if (!Core.Player.InCombat)
        {
            Reset("Waiting for Ultima pull");
            return false;
        }
        active = true;
        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        SnapshotCasts(actors, now);
        Planner.Goal orb = SelectOrb(actors, now);
        Point[] party = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
            .Where(member => member != null && member.IsValid && member.IsAlive
                && member.ObjectId != Core.Player.ObjectId && member.Location.Distance2D(arena) < 22f)
            .Select(member => Xz(member.Location)).ToArray();
        Planner.Plan next = Planner.Build(Xz(arena), Xz(Core.Player.Location), Core.Player.ObjectId, casts.Values.ToArray(), orb, plan.Destination, party);
        string nextKey = string.Join("/", next.Goals.Select(g => $"{g.Kind}:{g.Owner}"));
        if (nextKey != goalKey)
        {
            // A prior stack point is not an orb latch. Within one owner retain a still-safe point
            // so equivalent samples cannot cause oscillation while another mechanic resolves.
            next = Planner.Build(Xz(arena), Xz(Core.Player.Location), Core.Player.ObjectId, casts.Values.ToArray(), orb, null, party);
            goalKey = nextKey;
        }
        plan = next;
        LogPlan(now);
        await TankBusterSpells();
        await DamageMitigationSpells();
        if (await RecoverFailedAvoidanceAsync(now))
            return true;
        if (recovering)
            return false;
        if (plan.Goals.Length > 0)
            return await MoveToPlannedPositionAsync();
        ReleaseMovement("No active positive-position mechanic");
        // Outside the evidence-scoped recovery above, geometric escapes remain RB-owned.
        if (AvoidanceManager.IsRunningOutOfAvoid || !plan.Feasible)
            return false;
        // Preserve the existing duty-specific LB exception, now restricted to the real enrage.
        // Ordinary rotation/healing and invulnerable transition behavior remain routine-owned.
        if (boss.CastingSpellId == 29024 && Core.Player.IsDPS() && LimitBreak.Percentage >= 3
            && Core.Player.CurrentTarget?.ObjectId == boss.ObjectId && boss.CanAttack && !Core.Player.IsCasting)
            await CombatHelpers.UseLB3();
        return false;
    }

    private void SnapshotCasts(BattleCharacter[] actors, double now)
    {
        HashSet<(uint Owner, uint Action)> seen = [];
        foreach (BattleCharacter actor in actors)
        {
            if (!actor.IsAlive || !actor.IsCasting || !TrackedActions.Contains(actor.CastingSpellId)
                || actor.Location.Distance2D(arena) > 65f || !actor.SpellCastInfo.IsValid)
                continue;
            var info = actor.SpellCastInfo;
            uint action = actor.CastingSpellId;
            uint targetId = info.TargetId;
            Point origin = Xz(actor.Location);
            if (action is Planner.Stack or Planner.Spread)
            {
                GameObject target = GameObjectManager.GetObjectByObjectId(targetId);
                if (target == null || !target.IsValid || target is BattleCharacter character && !character.IsAlive)
                {
                    // Missing target evidence must never turn a stack into following an arbitrary
                    // party member, nor turn a helper's own position into a player-centered avoid.
                    Warn(now, $"Unresolved target: action={action}, caster=0x{actor.ObjectId:X8}, target=0x{targetId:X8}");
                    targetId = 0; // Keep the cast's lifecycle so an orb cannot preempt this unknown stack.
                }
                else
                    origin = Xz(target.Location);
            }
            var key = (actor.ObjectId, action);
            // RB heading zero points along +Z. Forward is (sin(h), cos(h)); (cos(h), -sin(h))
            // is the lateral vector and would reproduce the old sideways-rectangle problem.
            Point forward = Planner.Forward(actor.Heading);
            casts[key] = new(actor.ObjectId, action, origin, forward, now + info.RemainingCastTime.TotalSeconds, targetId);
            seen.Add(key);
        }
        foreach (var pair in casts.ToArray())
        {
            if (seen.Contains(pair.Key))
                continue;
            bool ownerAlive = actors.Any(a => a.ObjectId == pair.Key.Owner && a.IsAlive);
            if (!Planner.KeepAfterDisappearance(pair.Value, now, ownerAlive))
                casts.Remove(pair.Key);
        }
    }

    // Keep observation separate from selection: every visible orb needs motion and proximity
    // history, including those deferred while a higher-priority mechanic owns movement.
    private void UpdateOrbObservations(BattleCharacter[] orbs, double now)
    {
        foreach (uint id in orbMotion.Keys.Where(id => !orbs.Any(o => o.ObjectId == id)).ToArray())
        {
            Logger.Information($"[PortaDecumana] Orb left observation: owner=0x{id:X8} last={orbMotion[id].Position} closestPlayerDistance={orbClosestDistances.GetValueOrDefault(id):F2}; soak outcome unconfirmed");
            orbMotion.Remove(id);
            orbClosestDistances.Remove(id);
        }
        foreach (BattleCharacter candidate in orbs)
        {
            Point position = Xz(candidate.Location);
            float distance = Point.Distance(position, Xz(Core.Player.Location));
            orbClosestDistances[candidate.ObjectId] = Math.Min(orbClosestDistances.GetValueOrDefault(candidate.ObjectId, float.PositiveInfinity), distance);
            if (!orbMotion.TryGetValue(candidate.ObjectId, out var sample) || now - sample.Time > 1)
                orbMotion[candidate.ObjectId] = (position, now, Point.Zero);
            else if (now - sample.Time >= 0.2)
            {
                Point velocity = (position - sample.Position) / (float)(now - sample.Time);
                // A jump larger than ten yalms/second is not a trustworthy travel sample.
                orbMotion[candidate.ObjectId] = (position, now, velocity.Length() <= 10f ? velocity : Point.Zero);
            }
        }
    }

    private Planner.Goal SelectOrb(BattleCharacter[] actors, double now)
    {
        // Keep a reachable intercept stable so paired orbs do not make us switch destinations
        // each time their relative distances change. Soaking requires contact, not targeting.
        BattleCharacter[] orbs = actors.Where(a => a.NpcId == OrbNpc && a.IsAlive && a.IsVisible
            && a.Location.Distance2D(arena) < 22f).ToArray();
        UpdateOrbObservations(orbs, now);

        if (orbs.Length == 0)
        {
            selectedOrbId = 0;
            orbIntercept = null;
            return null;
        }
        // Finish escaping the current stage before acquiring an orb lease. Starting immediately
        // after Boom can otherwise request a route from an unsafe wall landing.
        Planner.Plan timed = Planner.Build(Xz(arena), Xz(Core.Player.Location), Core.Player.ObjectId,
            casts.Values.ToArray(), null, null);
        if (AvoidanceManager.IsRunningOutOfAvoid || !Planner.Safe(Xz(Core.Player.Location), Xz(arena), timed.Hazards)
            || casts.Values.Any(c => c.Action is Planner.Stack or Planner.Spread or Planner.Boom or Planner.Vulcan))
            return null;
        // Prefer the owned orb only while its actual safe route can still beat its travel.
        // Rejected latches no longer block another reachable orb; compare routed distance,
        // including detours, rather than actor proximity or an unreachable off-floor center.
        var selected = orbs.Select(o => new
        {
            Actor = o,
            Point = Planner.OrbIntercept(Xz(o.Location), orbMotion[o.ObjectId].Velocity, Xz(Core.Player.Location),
                Xz(arena), o.ObjectId == selectedOrbId ? orbIntercept : null, timed.Hazards)
        }).Where(c => c.Point.HasValue)
            .OrderBy(c => c.Actor.ObjectId == selectedOrbId ? 0 : 1)
            .ThenBy(c => Planner.RouteLength(Xz(Core.Player.Location), c.Point.Value, Xz(arena), timed.Hazards))
            .ThenBy(c => c.Actor.ObjectId).FirstOrDefault();
        if (selected == null)
        {
            selectedOrbId = 0;
            orbIntercept = null;
            if (orbs.Length > 0)
                Warn(now, "No reachable orb intercept; waiting for safe travel");
            return null;
        }
        BattleCharacter orb = selected.Actor;
        selectedOrbId = orb.ObjectId;
        orbIntercept = selected.Point;
        Point velocityNow = orbMotion[selectedOrbId].Velocity;
        // Log motion even when the destination is unchanged, so a held intercept can be checked
        // against the orb's approach rather than just its stationary spawn sample.
        if (now >= nextOrbLogTime)
        {
            nextOrbLogTime = now + 1;
            Logger.Information($"[PortaDecumana] Orb intercept: owner=0x{selectedOrbId:X8} actor={orb.Location} velocity={velocityNow} destination={orbIntercept} player={Core.Player.Location} closestPlayerDistance={orbClosestDistances[selectedOrbId]:F2}");
        }
        return new(Planner.GoalKind.Orb, selectedOrbId, orbIntercept.Value, 0.1f, double.PositiveInfinity);
    }

    private async Task<bool> RecoverFailedAvoidanceAsync(double now)
    {
        Point player = Xz(Core.Player.Location);
        bool unsafePosition = !Planner.Safe(player, Xz(arena), plan.Hazards);
        // Both phases have captured failures, but only the explicitly supported actions may
        // borrow the mover. Cannon/Explosion and unrelated encounter behavior retain ownership.
        bool RecoveryAction(uint action) => Planner.SupportsRecovery(arena == FirstArena, action);
        bool supported = Point.Distance(player, Xz(arena)) >= Planner.ArenaRadius - 0.05f
            || plan.Hazards.Any(h => h.Contains(player) && RecoveryAction(h.Cast.Action));
        if (recovering && !unsafePosition && !AvoidanceManager.IsRunningOutOfAvoid
            && plan.Hazards.Any(h => RecoveryAction(h.Cast.Action)) && plan.Goals.Length == 0)
        {
            // Escaping once is insufficient if the routine walks back into the same live hazard.
            // Hold only movement through that cast; a positive mechanic can reclaim the lease.
            StopOwnedMovement();
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Holding Porta recovery through active hazard");
            return false;
        }
        if (!unsafePosition || !supported || AvoidanceManager.IsRunningOutOfAvoid)
        {
            if (recovering)
                ReleaseMovement("Porta recovery reached safety or yielded to RB");
            recovering = false;
            unsafeSince = 0;
            recoveryDestination = null;
            return false;
        }
        if (unsafeSince == 0)
            unsafeSince = now;
        // Give ordinary RB avoidance several pulses to begin. SpanRef failures leave
        // IsRunningOutOfAvoid false; 350ms bounds the delay within the captured 4.7s spread.
        if (!recovering && now - unsafeSince < 0.35)
            return false;
        recoveryDestination = Planner.RecoveryDestination(Xz(arena), player, plan.Hazards, recoveryDestination);
        if (!recoveryDestination.HasValue)
        {
            if (recovering)
                ReleaseMovement("No bounded recovery segment");
            recovering = false;
            Warn(now, $"No monotonic escape: player={player}, arena={Xz(arena)}");
            return false;
        }
        if (!recovering)
            Logger.Information($"[PortaDecumana] Span recovery: player={player}, destination={recoveryDestination}; hazards={string.Join(",", plan.Hazards.Select(h => h.Cast.Action))}");
        recovering = true;
        movementLeaseActive = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Porta failed-avoid recovery");
        if (Core.Player.IsCasting)
            ActionManager.StopCasting();
        moving = true;
        Navigator.PlayerMover.MoveTowards(new Vector3(recoveryDestination.Value.X, Core.Player.Y, recoveryDestination.Value.Y));
        await Coroutine.Yield();
        return true;
    }

    private async Task<bool> MoveToPlannedPositionAsync()
    {
        // A single lease belongs to the selected overlap stage. Its short renewable lifetime
        // bounds stale suppression if the plugin is disabled before its next encounter tick.
        if (!movementLeaseActive && !AvoidanceManager.IsRunningOutOfAvoid)
            moving = true; // Take over any outstanding routine movement before holding position.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Porta Decumana " + goalKey);
        movementLeaseActive = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false; // RB took the mover; a later release must not stop its escape.
            return false;
        }
        if (!plan.Feasible || !plan.Destination.HasValue)
        {
            StopOwnedMovement();
            return false;
        }
        Point player = Xz(Core.Player.Location);
        Point destination = plan.Destination.Value;
        if (Point.Distance(player, destination) <= 0.2f)
        {
            StopOwnedMovement();
            return false; // Hold the lease, but let healing/rotation continue while safely parked.
        }
        Point? waypoint = Planner.NextWaypoint(player, destination, Xz(arena), plan.Hazards);
        if (!waypoint.HasValue)
        {
            StopOwnedMovement();
            Warn(DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond, "No safe semantic route for " + goalKey);
            return false;
        }
        Vector3 next = new(waypoint.Value.X, Core.Player.Y, waypoint.Value.Y);
        // Guard against unrelated active RB avoids as well. Never take ownership of an obstacle
        // merely because it is absent from this encounter's snapshot.
        int samples = Math.Max(1, (int)Math.Ceiling(Core.Player.Distance2D(next) / 0.25f));
        for (int i = 1; i <= samples; ++i)
        {
            Vector3 sample = Core.Player.Location + (next - Core.Player.Location) * ((float)i / samples);
            if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(sample)))
            {
                StopOwnedMovement();
                return false;
            }
        }
        if (Core.Player.IsCasting)
            ActionManager.StopCasting();
        moving = true;
        Navigator.PlayerMover.MoveTowards(next);
        await Coroutine.Yield();
        return true;
    }

    private void LogPlan(double now)
    {
        string state = $"{plan.Stage}; goals={goalKey}; hazards={string.Join(",", plan.Hazards.Select(h => h.Cast.Action).Distinct().Order())}; feasible={plan.Feasible}";
        if (state != lastLoggedPlan)
        {
            Logger.Information($"[PortaDecumana] {state}; destination={plan.Destination}");
            lastLoggedPlan = state;
        }
        if (!plan.Feasible)
            Warn(now, "No shared safe position; preserving simultaneous requirements for capture");
    }

    private void Warn(double now, string reason)
    {
        if (now < nextWarningTime)
            return;
        nextWarningTime = now + 3;
        Logger.Information("[PortaDecumana] " + reason);
    }

    private void StopOwnedMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        moving = false;
    }

    private void ReleaseMovement(string reason)
    {
        StopOwnedMovement();
        if (movementLeaseActive)
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, reason);
        movementLeaseActive = false;
    }

    private void Reset(string reason)
    {
        active = false;
        casts.Clear();
        plan = new([], [], null, "idle", true);
        selectedOrbId = 0;
        orbMotion.Clear();
        orbClosestDistances.Clear();
        nextOrbLogTime = 0;
        orbIntercept = null;
        goalKey = "";
        recovering = false;
        unsafeSince = 0;
        recoveryDestination = null;
        ReleaseMovement(reason);
    }

    private void DisableSideStep()
    {
        if (SidestepPlugin == null)
            return;
        if (!sideStepSuppressed)
        {
            sideStepWasEnabled = SidestepPlugin.Enabled;
            sideStepSuppressed = true;
        }
        SidestepPlugin.Enabled = false;
    }

    private void RestoreSideStep()
    {
        if (sideStepSuppressed && SidestepPlugin != null)
            SidestepPlugin.Enabled = sideStepWasEnabled;
        sideStepSuppressed = false;
    }

    private void OnBotStop(BotBase bot)
    {
        navigationReset.Observe(false, 0);
        Reset("Porta Decumana bot stopped");
        RestoreSideStep();
    }

    private void OnMapChanged(object sender, EventArgs args)
    {
        // DungeonManager's in-instance decorator may not tick after duty exit. Restore ownership
        // on the map event as well; this does not alter other encounters' manager lifecycle.
        if (WorldManager.ZoneId != (uint)ZoneId)
        {
            navigationReset.Observe(false, 0);
            Reset("Porta Decumana map left");
            RestoreSideStep();
        }
    }

    private static Point Xz(Vector3 point) => new(point.X, point.Z);
}
