using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using P = DutyMechanic.Dungeons.ThornmarchHardPlanner;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Reworked Thornmarch's royal guard and king mechanics. Handles live parties and undersized
/// carries without replacing their queue, combat routine, target selection or completion flow.
/// </summary>
public sealed class ThornmarchHard : AbstractDungeon
{
    private readonly Dictionary<(uint Owner, uint Action), P.Cast> casts = [];
    private readonly Dictionary<uint, uint> mapStates = [];
    private readonly Dictionary<uint, P.Tower> towers = [];

    // Native avoidance keys by object identity. Update these stable envelopes rather than
    // replacing an avoid on every position/deadline sample and invalidating its escape path.
    private sealed class AvoidSnapshot
    {
        internal P.Hazard Geometry;
    }

    private readonly Dictionary<(uint Owner, uint Action, int Ordinal), AvoidSnapshot> envelopes = [];
    private AvoidSnapshot[] avoids = [];
    private P.Plan plan = new([], null, "idle", 0, false, true);
    private bool active;
    private bool moving;
    private bool leased;
    private bool sideStepOwned;
    private bool sideStepPreviouslyEnabled;
    private double nextPlan;
    private double nextLog;
    private double towerArmUntil;
    private double towerDelay = 8;
    private double lastCometFinish;
    private int cometsLeft;
    private string previousStage = "";

    // Global 2026.10.16 bg.lgb: b1922 tower indicators, confirmed by live state changes.
    // The colocated mogmete01 decorations stay inactive and cannot identify a soak.
    // These are native placed-instance IDs, not network map-effect indices 8..15.
    private static readonly Dictionary<uint, Point> TowerPositions = new()
    {
        [9284371] = new(0, -10),
        [9284374] = new(10, 0),
        [9284375] = new(0, 10),
        [9284376] = new(-10, 0),
        [9284377] = new(7.0711f, -7.0711f),
        [9284378] = new(7.0711f, 7.0711f),
        [9284379] = new(-7.0711f, 7.0711f),
        [9284380] = new(-7.0711f, -7.0711f)
    };

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)1067;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [29209, 29214, 29206];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [29210, 29211, 29217];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        AvoidanceHelpers.AddAvoidDonut(() => active, () => Vector3.Zero, 90, P.Arena);
        AvoidanceManager.AddAvoidPolygon<AvoidSnapshot>(condition: () => active,
            leashPointProducer: () => Vector3.Zero, leashRadius: 40,
            rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: h => h.Geometry.Polygon().Select(p => new Vector2(p.X, p.Y)).ToArray(),
            locationProducer: _ => Vector3.Zero, collectionProducer: () => avoids, priority: AvoidancePriority.High);
        Logger.Information("[Thornmarch] Handler registered for live parties and undersized carries.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        TreeRoot.OnStop -= OnStop;
        GameEvents.OnMapChanged -= OnMapChanged;
        Reset();
        RestoreSideStep();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (Core.Player == null || !DutyManager.InInstance || WorldManager.ZoneId != 1067)
        {
            Reset();
            RestoreSideStep();
            return false;
        }
        if (CommonBehaviors.IsLoading || QuestLogManager.InCutscene || !Core.Player.IsAlive)
        {
            Reset();
            return false;
        }
        BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid).ToArray();
        // Base IDs distinguish the reworked guard/king from helpers sharing their name rows.
        bool encounter = actors.Any(a => a.BaseId >= 0x391C && a.BaseId <= 0x3923 && a.IsAlive);
        if (!encounter)
        {
            Reset();
            RestoreSideStep();
            return false;
        }
        SuppressSideStep(); // Before the first cast, not after generic geometry has registered.
        if (!Core.Player.InCombat)
        {
            Reset();
            return false;
        }
        active = true;
        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (now >= nextPlan)
        {
            nextPlan = now + 0.1;
            SnapshotCasts(actors, now);
            SnapshotTowers(now);
            BattleCharacter[] members = PartyManager.VisibleMembers.Select(m => m.BattleCharacter)
                .Append(Core.Player).Where(m => m != null && m.IsValid && m.IsAlive && m.Location.Distance2D(Vector3.Zero) < 35)
                .DistinctBy(m => m.ObjectId).ToArray();
            P.Member[] party = members.Select(Member).ToArray();
            Point[] bogs = GameObjectManager.GetObjectsOfType<EventObject>().Where(e => e.IsValid && e.IsVisible
                    && e.BaseId == 0x1E8F67 && e.Location.Distance2D(Vector3.Zero) < 30)
                .Select(e => Xz(e.Location)).ToArray();
            P.Plan next = P.Build(Member(Core.Player), party, casts.Values.ToArray(), towers.Values.ToArray(), bogs,
                cometsLeft > 0 || now <= lastCometFinish + P.Grace, plan.Destination, now,
                plan.Stage == "uncovered meteor tower" ? plan.Owner : 0);
            if (next.Owner != plan.Owner || next.Stage != plan.Stage)
                next = P.Build(Member(Core.Player), party, casts.Values.ToArray(), towers.Values.ToArray(), bogs,
                    cometsLeft > 0 || now <= lastCometFinish + P.Grace, null, now);
            plan = next;
            PublishAvoids();
            string stage = $"{plan.Stage}; owner={plan.Owner:X}; feasible={plan.Feasible}; hazards={string.Join(',', plan.Hazards.Select(h => h.Action).Distinct().Order())}";
            if (stage != previousStage || now >= nextLog && plan.Positive)
            {
                Logger.Information($"[Thornmarch] {stage}; player={Core.Player.Location}; destination={plan.Destination}; towers={towers.Count}; comets={cometsLeft}");
                previousStage = stage;
                nextLog = now + 3;
            }
        }
        await TankBusterSpells();
        await DamageMitigationSpells();
        if (!plan.Positive)
        {
            ReleaseMovement();
            return false;
        }
        return await MoveAsync();
    }

    private void SnapshotCasts(BattleCharacter[] actors, double now)
    {
        HashSet<(uint Owner, uint Action)> seen = [];
        foreach (BattleCharacter actor in actors)
        {
            if (!actor.IsAlive || !actor.IsCasting || !actor.SpellCastInfo.IsValid)
                continue;
            uint action = actor.CastingSpellId;
            if (!(action >= 29188 && action <= 29217 || action == 29619))
                continue;
            var info = actor.SpellCastInfo;
            var key = (actor.ObjectId, action);
            double finish = now + info.RemainingCastTime.TotalSeconds;
            bool started = !casts.TryGetValue(key, out var old) || finish > old.Finish + 1;
            Point origin = Xz(actor.Location);
            uint target = info.TargetId;
            if (action is 29192 or 29204 or 29206)
            {
                GameObject victim = GameObjectManager.GetObjectByObjectId(target);
                if (victim != null && victim.IsValid)
                    origin = Xz(victim.Location);
                else
                    target = 0; // An unresolved mark must not select an arbitrary ally.
            }
            else if (action is 29199 or 29207)
                origin = Xz(info.CastLocation); // Zero is a valid center for Pom Bog.
            casts[key] = new(actor.ObjectId, action, origin, P.Forward(actor.Heading), finish, target);
            seen.Add(key);
            if (started)
            {
                Logger.Information($"[Thornmarch] Cast {action} owner={actor.ObjectId:X} base={actor.BaseId:X} target={target:X} origin={origin} heading={actor.Heading:F3} finish={finish:F3}");
                // Third Decree includes three comet baits before the sixteen-second tower window.
                // Pom Meteor opens the shorter initial window without comet baits.
                if (action == 29190)
                {
                    towerDelay = 16;
                    towerArmUntil = finish + 20;
                    cometsLeft = 3;
                }
                else if (action == 29193)
                {
                    towerDelay = 8;
                    towerArmUntil = finish + 12;
                    cometsLeft = 0;
                }
                else if (action == 29199)
                {
                    cometsLeft = Math.Max(0, cometsLeft - 1);
                    lastCometFinish = Math.Max(lastCometFinish, finish);
                }
            }
        }
        foreach (var entry in casts.ToArray())
        {
            if (seen.Contains(entry.Key))
                continue;
            // Keep only a nearly completed cast through the polling grace. An early cancellation
            // or despawn releases immediately, including stack leases during a wipe.
            if (now > entry.Value.Finish + P.Grace || now < entry.Value.Finish - 0.35
                || !actors.Any(a => a.ObjectId == entry.Key.Owner && a.IsAlive))
                casts.Remove(entry.Key);
        }
        if (now > towerArmUntil && towers.Count == 0)
            cometsLeft = 0;
    }

    private void SnapshotTowers(double now)
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector director || !director.IsValid)
            return;
        foreach (var effect in director.MapEffects.Where(e => TowerPositions.ContainsKey(e.ID)))
        {
            if (mapStates.TryGetValue(effect.ID, out uint previous) && previous == effect.State)
                continue;
            mapStates[effect.ID] = effect.State;
            Logger.Information($"[Thornmarch] Tower placement={effect.ID} state={effect.State:X}; armed={now <= towerArmUntil}; player={Core.Player.Location}");
            // Occupancy toggles must neither release a soak nor restart its deadline.
            // Only the observed meteor/decree window can introduce a new tower.
            towers.TryGetValue(effect.ID, out var current);
            var updated = P.ObserveTower(current, effect.ID, TowerPositions[effect.ID],
                effect.State, now, towerArmUntil, towerDelay);
            if (updated != null)
                towers[effect.ID] = updated;
            else
                towers.Remove(effect.ID);
        }
        foreach (uint id in towers.Where(t => now > t.Value.Finish + 0.5).Select(t => t.Key).ToArray())
        {
            Logger.Information($"[Thornmarch] Tower {id} observation expired; soak outcome unconfirmed.");
            towers.Remove(id);
        }
    }

    private void PublishAvoids()
    {
        var live = new HashSet<(uint Owner, uint Action, int Ordinal)>();
        var current = new List<AvoidSnapshot>();
        for (int i = 0; i < plan.Hazards.Length; i++)
        {
            P.Hazard hazard = plan.Hazards[i];
            var key = (hazard.Owner, hazard.Action, hazard.Owner == 0 ? i : 0);
            if (!envelopes.TryGetValue(key, out var envelope))
                envelopes[key] = envelope = new();
            envelope.Geometry = hazard;
            if (live.Add(key))
                current.Add(envelope);
        }
        foreach (var key in envelopes.Keys.Where(k => !live.Contains(k)).ToArray())
            envelopes.Remove(key);
        avoids = current.ToArray();
    }

    private async Task<bool> MoveAsync()
    {
        // Once held, return false so the routine can heal and attack. Native escape takes priority
        // and invalidates our stop ownership; releasing an old stack must not cancel its successor.
        if (!leased && !AvoidanceManager.IsRunningOutOfAvoid)
            moving = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Thornmarch " + plan.Stage);
        leased = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            return false;
        }
        if (!plan.Feasible || !plan.Destination.HasValue)
        {
            StopOwned();
            return false;
        }
        Point from = Xz(Core.Player.Location);
        if (Point.Distance(from, plan.Destination.Value) <= 0.2f)
        {
            StopOwned();
            return false;
        }
        Point? waypoint = P.NextWaypoint(from, plan.Destination.Value, plan.Hazards);
        if (!waypoint.HasValue)
        {
            StopOwned();
            return false;
        }
        Vector3 destination = new(waypoint.Value.X, Core.Player.Y, waypoint.Value.Y);
        int count = Math.Max(1, (int)Math.Ceiling(Core.Player.Location.Distance2D(destination) / 0.25));
        for (int i = 1; i <= count; i++)
            if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(Core.Player.Location + (destination - Core.Player.Location) * ((float)i / count))))
            {
                StopOwned();
                return false;
            }
        if (Core.Player.IsCasting)
            ActionManager.StopCasting();
        moving = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    private static P.Member Member(BattleCharacter a) => new(a.ObjectId, Xz(a.Location),
        a.IsTank() ? P.Role.Tank : a.IsHealer() ? P.Role.Healer : a.IsMelee() ? P.Role.Melee : P.Role.Ranged);

    private static Point Xz(Vector3 v) => new(v.X, v.Z);

    private void StopOwned()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        moving = false;
    }

    private void ReleaseMovement()
    {
        StopOwned();
        if (leased)
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Thornmarch mechanic released");
        leased = false;
    }

    private void Reset()
    {
        active = false;
        ReleaseMovement();
        casts.Clear();
        mapStates.Clear();
        towers.Clear();
        envelopes.Clear();
        avoids = [];
        plan = new([], null, "idle", 0, false, true);
        towerArmUntil = lastCometFinish = nextPlan = 0;
        cometsLeft = 0;
    }

    private void SuppressSideStep()
    {
        if (SidestepPlugin == null)
            return;
        if (!sideStepOwned)
        {
            sideStepPreviouslyEnabled = SidestepPlugin.Enabled;
            sideStepOwned = true;
        }
        SidestepPlugin.Enabled = false;
    }

    private void RestoreSideStep()
    {
        if (sideStepOwned && SidestepPlugin != null)
            SidestepPlugin.Enabled = sideStepPreviouslyEnabled;
        sideStepOwned = false;
    }

    private void OnStop(BotBase bot)
    {
        Reset();
        RestoreSideStep();
    }

    private void OnMapChanged(object sender, EventArgs e)
    {
        if (WorldManager.ZoneId != 1067)
        {
            Reset();
            RestoreSideStep();
        }
    }
}
