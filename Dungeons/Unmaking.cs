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
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Normal Unmaking trial geometry, add-floor lifecycle and stack/shelter participation.
/// The standalone profile selects targets; OrderBot and the selected routine retain combat.
/// </summary>
public sealed partial class Unmaking : AbstractDungeon
{
    // BNpcBase identities distinguish the real actors from shared name rows and helpers.
    private const uint Boss = 0x4DB9, Portal = 0x4DBA, Chaser = 0x4EB4, VacuumOrb = 0x4DBB;
    private const uint Looming = 0x4DBC, Uncast = 0x4DBD, Beacon = 0x4DBE, Zero = 0x4DBF;
    private const uint GrowsLarge = 49933, GrowsSmall = 49934, GazeCone = 49953;
    private const uint ChaseFirst = 48474, Holy = 49968, Flare = 49966;
    private const uint Puddle = 49963, Spread = 49964, TorrentSmall = 49946, TorrentMedium = 49947, TorrentLarge = 49948;
    private const uint Vacuum = 49949, Proximity = 49981, Empty = 50667, Nothingness = 49958;
    private const uint VacuumSmallVisual = 49943, VacuumMediumVisual = 49944, VacuumLargeVisual = 49945;
    private const uint Lightless = 49959, Dimension = 49969;
    private const ushort ChaseTether = 404, PassTether = 405;
    private const uint LineMarker = 719;
    private static readonly HashSet<uint> WatchedCasts =
        [GrowsLarge, GrowsSmall, GazeCone, ChaseFirst, Holy, Flare, Puddle, Spread,
         TorrentSmall, TorrentMedium, TorrentLarge, Vacuum, Proximity, Empty, Nothingness, Lightless, Dimension];

    // Reference identity is deliberately stable through a cast: native avoidance caches
    // collection objects, so replacing records for deadline jitter restarts escape paths.
    private sealed class Cast
    {
        internal uint Caster, Action, Target;
        internal Vector3 Origin;
        internal float Heading;
        internal Vector3 TorrentOrigin;
        internal float TorrentHeading;
        internal DateTime Finish;
        internal bool Seen;
        internal bool GeometryCaptured;
        // The shelter starts 1s after the bar, then hits five more times at 0.5s intervals.
        // Keep a 0.5s tail after it and after the line stack's three hits, 1.4s apart.
        // Vacuum and Meltdown applied damage 0.88-0.94s
        // after the bar. Keep these circles through that delay with a small grace.
        internal DateTime Until => Finish.AddSeconds(Action == Lightless ? 4 : Action == Dimension ? 3.3 :
            Action is Vacuum or Puddle ? 1.1 : .6);
    }

    private sealed class Hazard
    {
        internal Vector3 Origin;
        internal float Heading;
        internal Point[] MathPolygon;
        // RB 942 admits polygons by their supplied origin, before examining their bounds.
        // Keep a nearby native anchor separately from the actual mechanic origin so long
        // add lines and the arena exterior cannot disappear outside its 70y heightfield.
        internal Vector3 NativeAnchor;
        internal Vector2[] NativePolygon;
        internal Vector3 PublishedOrigin;
        internal float PublishedHeading;
    }

    private readonly Dictionary<uint, Cast> casts = [];
    // The travelling orb's visual cast exposes its destination several seconds before
    // its short damaging cast. Retain that destination across the travel animation.
    private readonly Dictionary<uint, Cast> approachingVacuum = [];
    private readonly Dictionary<string, Hazard> hazards = [];
    private readonly List<AvoidInfo> avoids = [];
    // This existing owner has no duty-specific geometry. Reusing its mover/provider
    // gate also suppresses service-navigation jump/anti-stuck input during a hold;
    // wrapping only IPlayerMover would leave that competing input path active.
    private ChrysalisMovement movement;
    private bool active, lifecycle, lostMover, travelling, adds, encounterEngaged;
    private bool targetlessHealing, servicingRoutine;
    private Point? beacon;
    private uint beaconId;
    private bool refreshCollision;
    private BattleCharacter[] actors = [], party = [];
    private BattleCharacter boss;
    private DateTime nextWarning;
    private bool sideStepBorrowed, savedSideStep;
    private readonly HashSet<uint> chased = [];
    private readonly HashSet<uint> observedChasers = [];
    private Point[] boundary;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheUnmaking;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        lifecycle = true;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        TreeRoot.OnStop += OnStop;
        TreeHooks.Instance.OnHooksCleared += OnHooksCleared;
        GameEvents.OnMapChanged += OnMapChanged;
        avoids.Add(AvoidanceManager.AddAvoidPolygon<Hazard>(
            condition: () => active,
            leashPointProducer: null, leashRadius: 200,
            rotationProducer: _ => 0, scaleProducer: _ => 1,
            heightProducer: _ => 10,
            pointsProducer: h => h.NativePolygon,
            locationProducer: h => h.NativeAnchor,
            collectionProducer: () => hazards.Values.ToArray(),
            priority: AvoidancePriority.High));
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        EndLifecycle();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // CombatLogic can revisit installed hooks. The bridge must not recursively
        // schedule itself or retain a wrapper while the routine awaits a cast.
        if (servicingRoutine) return false;
        Update();
        // Targetless acquisition stopped Looming's fallback path and later stopped
        // successful service paths through Nothingness. Keep lower-priority target
        // acquisition out of every active escape tick. Native avoidance pulses first;
        // targeting and healing resume immediately when its escape flag clears.
        if (movement?.IsEscaping == true) return true;
        if (targetlessHealing && !travelling && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            // WHM lost routine actions while the beacon was out
            // of target range and during Lightless World, despite returning false here.
            // Service the existing routine only in those targetless phases. It still
            // chooses all heals and mitigation; the existing mover owns positioning.
            servicingRoutine = true;
            try { await CommonTasks.ExecuteCoroutine(BrainBehavior.CombatLogic); }
            finally { servicingRoutine = false; }
            Update(); // Stop, death, loading and phase changes revoke the prior request.
            // A routine opportunity does not own the rest of OrderBot's tick. In the
            // beacon phase it may have no usable heal; target acquisition and route
            // travel must still run. Only an outstanding semantic move consumes it.
        }
        return travelling && !AvoidanceManager.IsRunningOutOfAvoid;
    }

    private void OnPulse(object sender, EventArgs args) => Update();
    private void OnStop(ff14bot.AClasses.BotBase bot) => EndLifecycle();
    private void OnHooksCleared(object sender, EventArgs args) => EndLifecycle();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != (uint)ZoneId) EndLifecycle();
    }

    private void EndLifecycle()
    {
        lifecycle = false;
        ff14bot.NeoProfile.BotEvents.OnPulse -= OnPulse;
        TreeRoot.OnStop -= OnStop;
        TreeHooks.Instance.OnHooksCleared -= OnHooksCleared;
        GameEvents.OnMapChanged -= OnMapChanged;
        Reset();
        foreach (var avoid in avoids) AvoidanceManager.RemoveAvoid(avoid);
        avoids.Clear();
    }

    private void Reset()
    {
        active = travelling = targetlessHealing = false;
        ReleasePosition();
        movement?.Detach();
        movement = null;
        casts.Clear(); approachingVacuum.Clear(); hazards.Clear(); chased.Clear(); observedChasers.Clear();
        lineTarget = 0;
        actors = party = [];
        boss = null;
        // Loading/cutscenes can restore the floor while the handler is suspended.
        // Reacquisition must rebuild once instead of keeping the prior add heightfield.
        refreshCollision |= boundary != null;
        adds = false; beacon = null; beaconId = 0; boundary = null;
        encounterEngaged = false;
        naughtStallSince = default;
        failedEscapeBend = null;
        nextFailedEscapePlan = default;
        nextEscapeCastInterrupt = default;
        if (sideStepBorrowed && SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStep;
        sideStepBorrowed = false;
    }

    private void Update()
    {
        if (!lifecycle) return;
        if (!PluginManager.Plugins.Any(p => p.Plugin is DutyMechanicPlugin && p.Enabled))
        {
            EndLifecycle();
            return;
        }
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)ZoneId || !DutyManager.InInstance ||
            CommonBehaviors.IsLoading || QuestLogManager.InCutscene || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            DirectorManager.ActiveDirector is InstanceContentDirector { InstanceEnded: true })
        {
            Reset();
            return;
        }
        // Read wrappers only in this bot-thread frame; only scalar cast state survives a pulse.
        actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid).ToArray();
        boss = actors.FirstOrDefault(a => a.BaseId == Boss && a.IsAlive);
        if (boss == null)
        {
            Reset();
            return;
        }
        party = actors.Append(Core.Me).Where(a => a.IsValid && a.IsAlive &&
            (a.IsMe || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        active = true;
        if (!sideStepBorrowed && SidestepPlugin != null)
        {
            savedSideStep = SidestepPlugin.Enabled;
            sideStepBorrowed = true;
        }
        if (sideStepBorrowed && SidestepPlugin?.Enabled == true)
        {
            // Profile rescheduling can re-enable SideStep during the encounter.
            // Keep one geometry owner without overwriting the original preference:
            // generic avoidance misclassifies stacks/flares and duplicates our hazards.
            SidestepPlugin.Enabled = false;
        }
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach(); movement = null; lostMover = true;
        }
        if (!lostMover && movement == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new ChrysalisMovement(Navigator.PlayerMover, FailedEscapeWaypoint,
                CanContinueNativeEscape, point => SafeApproach(Xz(point)));
            Navigator.PlayerMover = movement;
        }
        if (movement == null) Warn("Unsupported or replaced movement owner; semantic movement is released.");

        var now = DateTime.UtcNow;
        UpdateCasts(now);
        UpdateArena();
        BuildHazards(now);
        InterruptCastForEscape(now);
        if (!adds || !AvoidanceManager.IsRunningOutOfAvoid) failedEscapeBend = null;
        UpdatePosition(now);
        movement?.Update();
        targetlessHealing = IsHealer && Core.Me.InCombat &&
            (adds || casts.Values.Any(c => c.Action == Lightless)) &&
            (Core.Me.CurrentTarget == null || !Core.Me.CurrentTarget.IsValid || !Core.Me.CurrentTarget.CanAttack) &&
            party.Any(p => p.CurrentHealthPercent < 90);
        // Do not retain live wrappers between native frames, including during routine awaits.
        actors = party = [];
        boss = null;
    }

    private void UpdateCasts(DateTime now)
    {
        foreach (var a in actors.Where(a => a.BaseId == VacuumOrb && a.IsCasting && a.SpellCastInfo.IsValid &&
                     a.CastingSpellId is VacuumSmallVisual or VacuumMediumVisual or VacuumLargeVisual))
        {
            var destination = a.SpellCastInfo.CastLocation;
            // The visual destination matches the later Vacuum center. Reject missing
            // or off-floor data rather than placing an avoid at world zero.
            if (destination == Vector3.Zero || !UnmakingGeometry.InArena(Xz(destination), false)) continue;
            if (!approachingVacuum.TryGetValue(a.ObjectId, out var preview))
                approachingVacuum[a.ObjectId] = preview = new Cast { Caster = a.ObjectId, Action = a.CastingSpellId };
            preview.Origin = destination;
            preview.Finish = now + a.SpellCastInfo.RemainingCastTime;
        }
        foreach (var c in casts.Values) c.Seen = false;
        foreach (var a in actors.Where(a => a.IsCasting && a.SpellCastInfo.IsValid && WatchedCasts.Contains(a.CastingSpellId)))
        {
            var spell = a.SpellCastInfo;
            if (!casts.TryGetValue(a.ObjectId, out var c) || c.Action != a.CastingSpellId)
            {
                c = new Cast { Caster = a.ObjectId, Action = a.CastingSpellId };
                casts[a.ObjectId] = c;
                if (c.Action == Dimension) lineTarget = 0;
            }
            c.Seen = true;
            c.Finish = now + spell.RemainingCastTime;
            c.Heading = a.Heading;
            c.Target = spell.TargetId;
            if (c.Action == Vacuum) approachingVacuum.Remove(a.ObjectId);
            // Ground Meltdown is location-targeted. Actor-relative circles/sectors/cones
            // must use their caster instead of the frequently empty CastLocation field.
            c.Origin = c.Action == Puddle ? spell.CastLocation : a.Location;
            if (c.Action is TorrentSmall or TorrentMedium or TorrentLarge)
            {
                // Raw-type 10 helpers remain at arena center while their omen
                // centers lay 18y sideways. Snapshot the validated ring origin and its arc
                // rotation; the actor transform is the start of the curved line, not its
                // center. Zero omen after telegraph expiry must not relocate the hazard.
                if (!c.GeometryCaptured)
                {
                    float half = TorrentHalfAngle(c.Action);
                    var origin = UnmakingGeometry.TorrentOrigin(Xz(a.Location), a.Heading);
                    var omen = a.OmenMatrix.Center;
                    if (omen != Vector3.Zero && Point.Distance(Xz(omen), origin) <= .25f)
                        origin = Xz(omen);
                    c.TorrentOrigin = new Vector3(origin.X, a.Y, origin.Y);
                    c.TorrentHeading = a.Heading - MathF.PI / 2 + half * MathF.PI / 180;
                    c.GeometryCaptured = true;
                }
                c.Origin = c.TorrentOrigin;
                c.Heading = c.TorrentHeading;
            }
            if (c.Action == Dimension)
            {
                // Enuo's ordinary target does not identify the carrier: the tank
                // stayed north while the line and party faced east. Only a marker on
                // a party member can identify a carrier through this managed surface.
                var marked = party.Where(p => HasMarker(p, LineMarker)).ToArray();
                if (marked.Length == 1) lineTarget = marked[0].ObjectId;
            }
        }
        foreach (var pair in approachingVacuum.ToArray())
        {
            var preview = pair.Value;
            var actor = actors.FirstOrDefault(a => a.ObjectId == pair.Key);
            // Observed travel plus the damaging cast finished about 2.6s after the
            // visual bar. Four seconds includes its effect tail and bounds missing
            // follow-up telemetry. Actual cast discovery takes over the same hazard.
            if (actor == null || now > preview.Finish.AddSeconds(4) ||
                (now < preview.Finish.AddSeconds(-.3) && (!actor.IsCasting || actor.CastingSpellId != preview.Action)))
                approachingVacuum.Remove(pair.Key);
        }
        foreach (var pair in casts.ToArray())
        {
            var c = pair.Value;
            // A disappeared/cancelled bar before its end is not a successful impact.
            // Retain only the bounded resolution tail when the bar ends normally.
            if (now > c.Until || (!c.Seen && now < c.Finish.AddSeconds(-.3)))
                casts.Remove(pair.Key);
        }
        if (!casts.Values.Any(c => c.Action == Dimension)) lineTarget = 0;
    }

    private void UpdateArena()
    {
        // A wipe can rebuild while the entry barrier is still present. Refresh again
        // on the next actual pull, rather than treating early reacquisition as proof
        // that the battle floor is final. Continuous boss combat does not retrigger it.
        refreshCollision |= boss.InCombat && !encounterEngaged;
        encounterEngaged = boss.InCombat;
        bool before = adds;
        var oldBeacon = beacon;
        // The first wave casts while all nine adds are hidden. Their active casts are
        // already on the enlarged floor; visibility previously trapped native escape in
        // the small circle until after death. Uncast pre-spawn actors alone are not proof.
        var addActors = actors.Where(a => a.IsAlive && a.BaseId is Looming or Uncast or Beacon &&
            (a.IsVisible || a.IsCasting)).ToArray();
        if (addActors.Length != 0) adds = true;
        // The return cutscene restores the main floor before Lightless World. Boss
        // targetability plus no surviving adds also reconstructs a mid-fight attach.
        if (casts.Values.Any(c => c.Action == Lightless) || (boss.IsTargetable && addActors.Length == 0)) adds = false;
        var beaconActor = addActors.FirstOrDefault(a => a.BaseId == Beacon);
        if (beaconActor != null && beaconActor.ObjectId != beaconId)
        {
            beaconId = beaconActor.ObjectId;
            beacon = Xz(beaconActor.Location);
        }
        if (!adds) { beacon = null; beaconId = 0; }
        if (boundary == null || adds != before || beacon != oldBeacon)
        {
            // The first pull can inherit pre-barrier collision while the player stays
            // within RB's 10-yalm recenter distance. Refresh once on acquisition and
            // again when the physical floor changes, never on every pulse.
            refreshCollision |= boundary == null || before != adds || oldBeacon != beacon;
            boundary = UnmakingGeometry.Exterior(adds, beacon);
            hazards.Remove("boundary");
        }
        // Do not discard a current emergency escape when the new floor was observed
        // during it. The explicit boundary already protects that handoff.
        if (refreshCollision && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
        {
            refreshCollision = false;
            AvoidanceManager.ResetNavigation();
        }
    }

    private static Point Xz(Vector3 v) => new(v.X, v.Z);
    private static Vector3 World(Point p) => new(p.X, Core.Me.Y, p.Y);
    private static bool HasMarker(BattleCharacter actor, uint id) => actor.VfxContainer.IsValid &&
        actor.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == id);

    private void Warn(string message)
    {
        if (DateTime.UtcNow < nextWarning) return;
        nextWarning = DateTime.UtcNow.AddSeconds(5);
        Logger.Information("[Unmaking] " + message);
    }
}
