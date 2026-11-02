using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.Interfaces;
using ff14bot.Navigation;
using ff14bot.AClasses;
using ff14bot.Behavior;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LlamaLibrary.Memory.PatternFinders;
using LlamaLibrary.Memory.Attributes;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 99: Everkeep trial logic.
/// </summary>
public class Everkeep : AbstractDungeon
{
    private bool smallArenaActive;
    private static Everkeep active;
    private EncounterMover movementOwner;
    private bool positioningLeased;
    private bool positioningMoving;
    private bool subscribed;
    private bool actualizeSeen;
    private bool spreadActive;
    private bool dawnPreparing;
    private bool dawnNoPathLogged;
    private Vector3? dawnDestination;
    private Vector3? cleaveDestination;
    private uint cleaveFirstId;
    private DateTime nextCleaveSearch;
    private string positioningKind;
    private readonly Dictionary<uint, TileHazard> tiles = [];
    private static readonly Vector2[] TileRectangle = [new(3f, 3f), new(-3f, 3f), new(-3f, -3f), new(3f, -3f)];

    private sealed class TileHazard
    {
        internal Vector3 Center;
        internal float Heading;
        internal DateTime Expires;
    }
    private DateTime nextPartyScan;
    private DateTime edgeObserveUntil;
    private DateTime nextEdgeCastScan;
    private DateTime edgeHelperUntil;
    private DateTime nextEdgeHelperScan;
    private int edgeEffectSlot;
    private static object edgeReaderMemory;
    private static int edgeReaderPid, edgeReaderCachedSlot;
    private static long edgeReaderStartTicks;
    private readonly HashSet<string> edgeEffectSeen = [];
    private DateTime edgeVisualAt, edgeReceiptAt, edgeStackUntil, nextEdgePosition;
    private uint edgeTargetId, edgeSourceId;
    private Vector3? edgeDestination;
    private AvoidInfo outerFloorAvoid, smallFloorAvoid;
    private readonly Dictionary<uint, TrackHazard> tracks = [];
    private readonly Dictionary<uint, VorpalHazard> vorpalPaths = [];
    private readonly Dictionary<uint, DateTime> cleaveImpacts = [];
    private readonly Dictionary<uint, (Vector3 Origin, DateTime End)> burstWindows = [];
    private readonly Dictionary<uint, (Vector3 Origin, float Heading, DateTime End)> cleaveWindows = [];
    private long vorpalHazardRevision;
    private bool vorpalExclusive;
    private EverkeepPlannerWorker vorpalWorker;
    private long vorpalGeneration, vorpalSubmittedRevision = -1, vorpalAppliedRevision = -1;
    private DateTime nextVorpalCheck, vorpalSubmittedAt, nextVorpalSearch, nextVorpalNoRouteLog, vorpalCastCancelAt, nextVorpalRejectLog;
    private Vector3 vorpalSubmittedStart, vorpalTarget;
    private bool vorpalTargetSet, vorpalRouteIntent, vorpalCleavePriority;
    private EverkeepRouteProgress vorpalRoute;
    private DateTime vorpalRouteAt, vorpalMotionAt;
    private Vector3 vorpalMotionStart;
    private long vorpalAddRevision, vorpalRouteAddRevision, vorpalSubmittedAddRevision;

    private uint activeCleaveId, sequenceFirstCleave;
    private uint rememberedFirstCleave;
    private float rememberedCleaveHeading;
    private bool cleaveForecastActive, cleaveForecastUsed, cleaveForecastDisabled;
    private DateTime cleaveForecastUntil, nextCleaveForecastCheck;

    private sealed class VorpalHazard
    {
        internal Vector3 Origin;
        internal float Heading;
        internal float Length;
        internal Vector2[] Points;
        internal DateTime Expires;
    }
    private static readonly Vector2[] TrackRectangle = [new(3f, 20.5f), new(-3f, 20.5f), new(-3f, -.5f), new(3f, -.5f)];

    private sealed class TrackHazard
    {
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Expires;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Everkeep;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        TreeRoot.OnStop += OnBotStopped;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnBotPulse;
        subscribed = true;
        InitializeEdgeEffectReader();

        // Each helper telegraphs one finite sprint segment. In the 2026-11-02
        // capture's apparent 1.37s-late status had already aged by 1.06s;
        // fresh action receipts and 33 status-age-adjusted captures place the
        // observed hit within .31-.54s of cast end. The route treats the lane
        // as active from cast completion through 1.2s afterward, retaining
        // margin on both sides and considering all observed segments together.
        AvoidanceManager.AddAvoidPolygon<VorpalHazard>(
            condition: () => !vorpalExclusive && Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            leashPointProducer: () => ArenaCenter.ZoraalJa,
            leashRadius: 50f,
            rotationProducer: h => -h.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: VorpalPoints,
            locationProducer: h => h.Origin,
            collectionProducer: () => vorpalPaths.Values.ToArray(),
            priority: AvoidancePriority.High);

        // Burst
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            objectSelector: bc => !vorpalExclusive && bc.CastingSpellId is EnemyAction.Burst,
            radiusProducer: bc => 8.0f,
            priority: AvoidancePriority.High));

        // The two 37714 helpers overlap by 2.35s. The boss's 37713 cast is
        // visual only; selecting both opposite helper halves at once blocks all
        // paths. Stage near the first safe boundary, then avoid the first impact.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            objectSelector: IsNextCleave,
            leashPointProducer: () => ArenaCenter.ZoraalJa,
            leashRadius: 40.0f,
            rotationDegrees: 0f,
            radius: 40.0f,
            arcDegrees: 180f);

        // In repeated captured Duty Support sequences the first helper of the
        // opening double cleave also led the following Vorpal overlap. Once
        // observed in this encounter, forecast that same helper only while the
        // converging Vorpal paths cover center, for at most three seconds. The
        // actual cast immediately replaces this avoid; any changed helper order
        // disables the forecast for the rest of the encounter.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep && !vorpalExclusive && cleaveForecastActive,
            objectSelector: a => a.IsValid && a.ObjectId == rememberedFirstCleave,
            leashPointProducer: () => ArenaCenter.ZoraalJa,
            leashRadius: 40f,
            rotationDegrees: 0f,
            radius: 40f,
            arcDegrees: 180f);

        // On 2026-11-02 native escape stopped only 0.19y beyond this
        // dividing line before the second half hit; a 0.75y margin still
        // failed in the later capture at 0.79y. Extend the active half 1.5y
        // across that boundary, retaining the same single-half lifetime.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            objectSelector: IsNextCleave,
            width: 80f,
            length: 3f,
            yOffset: -1.5f,
            priority: AvoidancePriority.High);

        // Half Full's helper is a 60-by-120y rectangle rather than a radial
        // cone; extend the dividing line .5y for a conservative safe edge.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.HalfFull,
            width: 121f,
            length: 60.5f,
            yOffset: -0.5f,
            priority: AvoidancePriority.High);

        // Smiting Circuit Donut
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            objectSelector: c => c.CastingSpellId == EnemyAction.SmitingCircuitDonut,
            outerRadius: 40.0f,
            innerRadius: 9.0F,
            priority: AvoidancePriority.High);

        // Smiting Circuit Avoid
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.SmitingCircuitAvoid,
            radiusProducer: bc => 10.0f,
            priority: AvoidancePriority.High));

        // Half Circuit uses concurrent 37741 half-room and either 37742 donut
        // or 37743 circle. The 2026-11-01 capture shows both helpers starting
        // together; their combined unsafe geometry leaves one shared safe region.
        // A 0.5y margin protects the ordinary edge without enlarging the 10y
        // center enough to remove the small-platform safe space.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep && !spreadActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.HalfCircuitRect,
            width: 121f,
            length: 60.5f,
            yOffset: -0.5f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep && !spreadActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.HalfCircuitDonut,
            outerRadius: 30.5f,
            innerRadius: 9.5f,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep && !spreadActive,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.HalfCircuitCircle,
            radiusProducer: bc => 10.5f,
            priority: AvoidancePriority.High));

        // The 376 lock-on marks Fire III's 5y spreads on each living participant.
        // On 2026-11-01 the next Half Circuit started while those marks remained;
        // resolve spread first, then enable its later in/out and half-room hazards.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep && spreadActive,
            objectSelector: a => IsOtherPartyMember(a) && HasSpreadMarker(a),
            radiusProducer: a => 5.5f,
            priority: AvoidancePriority.High));

        // On 2026-11-01 both Chasm hits landed at a point inside the
        // preview/impact actor-centered square but outside the omen-centered one.
        // Keep actor-centered tiles through the observed delayed damage, with a
        // .5y margin; the helper may stop casting before the impact resolves.
        AvoidanceManager.AddAvoidPolygon<TileHazard>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            leashPointProducer: () => ArenaCenter.ZoraalJa,
            leashRadius: 50f,
            rotationProducer: h => -h.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: _ => TileRectangle,
            locationProducer: h => h.Center,
            collectionProducer: () => tiles.Values.ToArray(),
            priority: AvoidancePriority.High);

        // Forged Track's preview is on an outer platform; the actual 5y-wide,
        // 20y-long lane is shifted into the central platform. Snapshot the lane
        // and retain it beyond the preview cast through the observed impact. Only
        // the earliest wave is active: the next preview overlaps in time but its
        // damage follows the first wave by about ten seconds.
        AvoidanceManager.AddAvoidPolygon<TrackHazard>(
            condition: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            leashPointProducer: () => ArenaCenter.ZoraalJa,
            leashRadius: 50f,
            rotationProducer: h => -h.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => 15f,
            pointsProducer: _ => TrackRectangle,
            locationProducer: h => h.Origin,
            collectionProducer: ActiveTrackWave,
            priority: AvoidancePriority.High);

        // Boss Arenas
        outerFloorAvoid = AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            rotation: 0.80f,
            collectionProducer: () => [ArenaCenter.ZoraalJa],
            priority: AvoidancePriority.High);

        // Register once per encounter. During Dawn's cast the full floor remains
        // available for the explicit approach below; the small boundary activates
        // on resolution, avoiding the captured no-path loop from outside it.
        smallFloorAvoid = AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Everkeep && smallArenaActive && !EnemyAction.DawnofanAge.IsCasting(),
            innerWidth: 19.0f,
            innerHeight: 19.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            rotation: 0.8f,
            collectionProducer: () => [ArenaCenter.ZoraalJa],
            priority: AvoidancePriority.High);

        return false;
    }

    private static Vector2[] VorpalPoints(VorpalHazard h) => h.Points;

    private static VorpalHazard CreateVorpalHazard(Vector3 from, Vector3 to, TimeSpan remaining, DateTime now)
    {
        float dx = to.X - from.X, dz = to.Z - from.Z;
        float length = (float)Math.Sqrt(dx * dx + dz * dz);
        // The cast location may lag discovery. Reject absent or implausible
        // endpoints and retry on the next pulse rather than inventing a path.
        if (!float.IsFinite(length) || length < .5f || length > 60f || (to.X == 0f && to.Z == 0f)) return null;
        // Captured 37712 helper endpoints already extend about two yalms past
        // the matching Fang sprint on each end. Adding that extension again made
        // overlapping paths unnecessarily restrictive. Keep only a .5y margin;
        // cache the vertices so avoidance pulses do not allocate new arrays.
        return new VorpalHazard { Origin = from, Heading = (float)Math.Atan2(dx, dz), Length = length,
            Points = [new(3f, length + .5f), new(-3f, length + .5f), new(-3f, -.5f), new(3f, -.5f)],
            Expires = now + remaining + TimeSpan.FromMilliseconds(1200) };
    }

    private static Vector3 ProjectedTileCenter(BattleCharacter actor)
    {
        var origin = actor.Location;
        var p = origin;
        float x = origin.X - ArenaCenter.ZoraalJa.X;
        float z = origin.Z - ArenaCenter.ZoraalJa.Z;
        if (x * x + z * z > 225f)
        {
            const float shift = 21.2132034f;
            p.X -= Math.Sign(x) * shift;
            p.Z -= Math.Sign(z) * shift;
        }
        return p;
    }

    private static bool IsOtherPartyMember(BattleCharacter actor) => actor.IsValid && actor.IsAlive && actor.IsVisible &&
        actor.ObjectId != Core.Me.ObjectId && (PartyManager.VisibleMembers.Any(p => p.ObjectId == actor.ObjectId) ||
        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)actor.NpcId));

    private static bool HasSpreadMarker(BattleCharacter actor) => actor.VfxContainer.IsValid &&
        actor.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 376);

    private static Vector3 ProjectedTrackOrigin(Vector3 origin, float heading)
    {
        float dx = (float)Math.Sin(heading), dz = (float)Math.Cos(heading);
        float leftX = -dz, leftZ = dx, offset = 0f;
        if (dx * dz < 0f)
        {
            // Gateways alternate lanes on these two diagonals. The captured
            // player hit lay within both independently predicted wave lanes.
            int lane = (int)Math.Floor(((origin.X - 100f) * leftX + (origin.Z - 100f) * leftZ) / 5f);
            offset = lane % 2 == 0 ? 5f : -5f;
        }
        return new Vector3(origin.X + 30f * dx + offset * leftX, origin.Y,
            origin.Z + 30f * dz + offset * leftZ);
    }

    private IEnumerable<TrackHazard> ActiveTrackWave()
    {
        if (tracks.Count == 0)
            return Array.Empty<TrackHazard>();
        DateTime earliest = tracks.Values.Min(h => h.Expires);
        return tracks.Values.Where(h => h.Expires <= earliest.AddMilliseconds(750)).ToArray();
    }

    private static bool IsNextCleave(BattleCharacter actor)
    {
        return actor.IsValid && active != null && !active.vorpalExclusive && active.activeCleaveId != 0 &&
            active.activeCleaveId == actor.ObjectId;
    }

    private static uint NextCleaveId(Dictionary<uint, DateTime> pending) =>
        pending.Count == 0 ? 0 : pending.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;

    private void OnBotStopped(BotBase bot)
    {
        ReleasePosition(true);
        Unsubscribe();
        if (ReferenceEquals(active, this)) active = null;
    }
    private void OnBotPulse(object sender, EventArgs args) => UpdateMechanics();

    private void UpdateMechanics()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)ZoneId.Everkeep || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            ReleaseVorpalOwner();
            tiles.Clear();
            tracks.Clear();
            vorpalPaths.Clear();
            cleaveImpacts.Clear();
            cleaveWindows.Clear();
            burstWindows.Clear();
            activeCleaveId = sequenceFirstCleave = 0;
            ResetCleaveForecast();
            edgeObserveUntil = DateTime.MinValue;
            nextEdgeCastScan = DateTime.MinValue;
            edgeHelperUntil = DateTime.MinValue;
            nextEdgeHelperScan = DateTime.MinValue;
            ClearEdgeStack();
            edgeEffectSeen.Clear();
            spreadActive = false;
            smallArenaActive = false;
            actualizeSeen = false;
            dawnPreparing = false;
            dawnNoPathLogged = false;
            dawnDestination = null;
            cleaveDestination = null;
            cleaveFirstId = 0;
            nextCleaveSearch = DateTime.MinValue;
            ReleasePosition(true);
            return;
        }

        // Bot-thread pulses continue when a routine coroutine occupies the tree.
        // Keep the floor restriction through Actualize's effect, with no timer
        // that can expire while the small platform is still physically present.
        if (EnemyAction.Actualize.IsCasting())
            actualizeSeen = true;
        else if (actualizeSeen)
        {
            smallArenaActive = false;
            actualizeSeen = false;
        }
        bool dawnCasting = EnemyAction.DawnofanAge.IsCasting();
        if (dawnCasting)
            smallArenaActive = true;

        // On 2026-11-02 the second cleave hit about 0.4s after its helper
        // cast ended; native avoidance had stopped with the player still on the
        // unsafe side. Retain each helper for 0.8s beyond its expected cast end,
        // and expose only the earliest half so the opposite half stays reachable.
        var now = DateTime.UtcNow;

        // Snapshot observed cast geometry once so moving helpers cannot alter
        // an active lane, tile, cleave, or Burst during its damage window.
        var previews = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsCasting && a.SpellCastInfo.IsValid &&
                a.CastingSpellId is EnemyAction.ChasmofVollok or EnemyAction.ForgedTrack or EnemyAction.ChasmImpact or EnemyAction.VorpalTrail or EnemyAction.DoubleedgedSwordsFront or EnemyAction.Burst or EnemyAction.HalfFull).ToArray();
        foreach (var preview in previews)
        {
            if (preview.CastingSpellId == EnemyAction.Burst && !burstWindows.ContainsKey(preview.ObjectId))
            {
                // Burst already uses an eight-yalm avoid from cast discovery.
                // Snapshot that conservative lifetime, plus the player margin;
                // do not infer an unobserved future cast or a narrower window.
                burstWindows.Add(preview.ObjectId, (preview.Location,
                    now + preview.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(800)));
                vorpalHazardRevision++;
                vorpalAddRevision++;
            }
            else if (preview.CastingSpellId == EnemyAction.Burst &&
                     burstWindows.TryGetValue(preview.ObjectId, out var burst) &&
                     burst.Origin.Distance2D(preview.Location) > .1f)
            {
                burstWindows[preview.ObjectId] = (preview.Location, burst.End);
                vorpalHazardRevision++;
                vorpalAddRevision++;
            }
            if (preview.CastingSpellId == EnemyAction.DoubleedgedSwordsFront && !cleaveImpacts.ContainsKey(preview.ObjectId))
            {
                if (cleaveImpacts.Count == 0 && preview.SpellCastInfo.RemainingCastTime.TotalMilliseconds > 4000 &&
                    EnemyAction.DoubleedgedSwordsBackHash.IsCasting())
                {
                    sequenceFirstCleave = preview.ObjectId;
                    ObserveFirstCleave(preview.ObjectId, preview.Heading);
                }
                cleaveForecastActive = false;
                var cleaveEnd = now + preview.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(800);
                cleaveImpacts.Add(preview.ObjectId, cleaveEnd);
                cleaveWindows[preview.ObjectId] = (preview.Location, preview.Heading, cleaveEnd);
                vorpalHazardRevision++;
                vorpalAddRevision++;
            }
            if (preview.CastingSpellId == EnemyAction.VorpalTrail && !vorpalPaths.ContainsKey(preview.ObjectId))
            {
                var hazard = CreateVorpalHazard(preview.Location, preview.SpellCastInfo.CastLocation,
                    preview.SpellCastInfo.RemainingCastTime, DateTime.UtcNow);
                if (hazard != null) { vorpalPaths.Add(preview.ObjectId, hazard); vorpalHazardRevision++; vorpalAddRevision++; }
            }
            if (preview.CastingSpellId == EnemyAction.ChasmofVollok && !tiles.ContainsKey(preview.ObjectId))
                tiles.Add(preview.ObjectId, new TileHazard
                {
                    Center = ProjectedTileCenter(preview),
                    Heading = preview.Heading,
                    Expires = DateTime.UtcNow + preview.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(2000)
                });
            if (preview.CastingSpellId == EnemyAction.ForgedTrack && !tracks.ContainsKey(preview.ObjectId))
            {
                var hazard = new TrackHazard
                {
                    Origin = ProjectedTrackOrigin(preview.Location, preview.Heading),
                    Heading = preview.Heading,
                    // The two recorded hits arrived 2.23s and 2.13s after their
                    // preview cast ends. Hold .37s longer for pulse/damage jitter;
                    // wipe, exit and replacement clear the snapshots immediately.
                    Expires = DateTime.UtcNow + preview.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(2600)
                };
                tracks.Add(preview.ObjectId, hazard);
            }
        }
        foreach (var id in burstWindows.Where(pair => pair.Value.End <= now).Select(pair => pair.Key).ToArray())
        {
            burstWindows.Remove(id);
            vorpalHazardRevision++;
        }
        foreach (var id in cleaveImpacts.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            cleaveImpacts.Remove(id);
            cleaveWindows.Remove(id);
            vorpalHazardRevision++;
        }
        activeCleaveId = NextCleaveId(cleaveImpacts);
        if (activeCleaveId == 0) sequenceFirstCleave = 0;
        foreach (var id in vorpalPaths.Where(kv => kv.Value.Expires <= DateTime.UtcNow).Select(kv => kv.Key).ToArray())
        {
            vorpalPaths.Remove(id);
            vorpalHazardRevision++;
        }
        foreach (var id in tiles.Where(kv => kv.Value.Expires <= DateTime.UtcNow).Select(kv => kv.Key).ToArray())
            tiles.Remove(id);
        foreach (var id in tracks.Where(kv => kv.Value.Expires <= DateTime.UtcNow).Select(kv => kv.Key).ToArray())
            tracks.Remove(id);

        UpdateCleaveForecast(now);

        // Marker reads are sampled at 10Hz, avoiding per-pulse VFX traversal while
        // retaining ample lead time for the observed five-second spread markers.
        if (DateTime.UtcNow >= nextPartyScan)
        {
            nextPartyScan = DateTime.UtcNow.AddMilliseconds(100);
            UpdatePartyMarkers();
        }

        if (movementOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movementOwner = new EncounterMover(Navigator.PlayerMover, OrdinaryPathSafe);
            Navigator.PlayerMover = movementOwner;
        }
        if (movementOwner?.IsCurrent != true)
        {
            // A foreign successor owns input now. Release only our lease; a stale
            // adapter must never stop or restore over that replacement.
            ReleasePosition(true);
            return;
        }
        if (UpdateVorpalOwner(now, previews.Any(a => a.CastingSpellId == EnemyAction.HalfFull))) return;
        movementOwner.CompleteEscape();
        movementOwner.RecheckOrdinary();

        if (dawnCasting)
        {
            if (!dawnPreparing)
            {
                dawnPreparing = true;
                dawnNoPathLogged = false;
                dawnDestination = null;
                ReleasePosition(false);
            }
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
            {
                if (MaintainStationaryCastHold("Dawn of an Age")) return;
                if (positioningLeased)
                    Logger.Information("[Everkeep] Dawn yield escaping={0} outOfControl={1} playerCasting={2} forced={3} locked={4} occupied={5} movingLease={6}",
                        AvoidanceManager.IsRunningOutOfAvoid, MovementManager.GenerallyOutOfControl, Core.Me.IsCasting,
                        MovementManager.ForceMoved, MovementManager.MovementLocked, MovementManager.IsOccupied, positioningMoving);
                ReleasePosition(false);
                return;
            }
            if (!dawnDestination.HasValue || !CleavePathSafe(Core.Me.Location, dawnDestination.Value))
                dawnDestination = FindDawnDestination(Core.Me.Location);
            if (!dawnDestination.HasValue)
            {
                if (!dawnNoPathLogged)
                {
                    Logger.Information("[Everkeep] Dawn staging has no verified safe path from {0}; yielding to active hazards.", Core.Me.Location);
                    dawnNoPathLogged = true;
                }
                ReleasePosition(false);
                return;
            }
            dawnNoPathLogged = false;
            StagePosition(dawnDestination.Value, "Dawn of an Age");
            return;
        }
        if (dawnPreparing)
        {
            dawnPreparing = false;
            dawnNoPathLogged = false;
            dawnDestination = null;
            ReleasePosition(false);
        }

        if (UpdateEdgeStack()) return;
        UpdateCleaveStaging();
    }

    // The exclusive lease covers only the observed Vorpal / paired cleave /
    // Burst overlap. Unrelated phase mechanics and forced movement preempt it.
    // All snapshots are scalar copies; route searches never touch RB objects.
    private bool UpdateVorpalOwner(DateTime now, bool halfFullCasting)
    {
        bool castTransition = Core.Me.IsCasting || vorpalExclusive && vorpalCastCancelAt != default &&
            now - vorpalCastCancelAt < TimeSpan.FromMilliseconds(750);
        bool eligible = vorpalPaths.Count != 0 && !smallArenaActive && !dawnPreparing &&
            !EnemyAction.DawnofanAge.IsCasting() && !halfFullCasting &&
            !EnemyAction.HalfCircuit.IsCasting() && tiles.Count == 0 && tracks.Count == 0 &&
            !spreadActive && !MovementManager.ForceMoved && !MovementManager.MovementLocked &&
            (vorpalExclusive || castTransition || !MovementManager.GenerallyOutOfControl && !MovementManager.IsOccupied);
        if (!eligible)
        {
            ReleaseVorpalOwner();
            return false;
        }
        if (!vorpalExclusive)
        {
            ReleasePosition(false);
            if (!movementOwner.BeginVorpal()) return false;
            vorpalExclusive = true;
            vorpalWorker = new EverkeepPlannerWorker((start, hazards, ticks, token) =>
            {
                var route = EverkeepTemporalPlanner.Plan(start, hazards, ticks, token);
                if (route.Length != 0) return EverkeepTemporalPlanner.SimplifyInitial(start, hazards, route);
                if (token.IsCancellationRequested) return route;
                route = EverkeepTemporalPlanner.FindEgress(start, hazards, token);
                if (route.Length != 0 || token.IsCancellationRequested) return route;
                return EverkeepTemporalPlanner.FindCleavePriority(start, hazards, ticks, token);
            });
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information("[Everkeep] Vorpal acquired exclusive movement at {0}.", Core.Me.Location);
        }
        ObserveVorpalArrival(now);
        if (now < nextVorpalCheck && vorpalAppliedRevision == vorpalHazardRevision) return true;
        nextVorpalCheck = now.AddMilliseconds(100);
        var hazards = CaptureVorpalHazards(now);
        if (vorpalCleavePriority && vorpalRoute != null)
            hazards = hazards.Where(h => h.HalfPlane || h.Circle).ToList();
        var here = Core.Me.Location;
        var here2 = new System.Numerics.Vector2(here.X, here.Z);

        // A fresh cast need not cancel a still-safe next leg. Check the
        // actual wait and movement against the complete new snapshot; later
        // legs are checked again before input. No future cast is predicted.
        if (vorpalRoute != null && vorpalRouteAddRevision != vorpalAddRevision)
        {
            double elapsed = (now - vorpalRouteAt).TotalSeconds;
            bool preserve = vorpalRoute.TryNextMove(elapsed, here2, out var nextKnown, out double departure) &&
                EverkeepTemporalPlanner.CanTraverse(here2, here2, hazards, 0, departure);
            if (preserve)
            {
                var shifted = hazards.Select(h => h with { Start = h.Start - departure, End = h.End - departure }).ToArray();
                preserve = EverkeepTemporalPlanner.CanTraverseTimed(here2, nextKnown, shifted);
            }
            if (preserve)
                vorpalRouteAddRevision = vorpalAddRevision;
            else
            {
                LogVorpalRejection("new hazard blocks next leg", here, now);
                movementOwner.HoldVorpal();
                vorpalRoute = null;
                vorpalTargetSet = vorpalRouteIntent = false;
                vorpalMotionAt = default;
                nextVorpalSearch = default;
            }
        }
        if (vorpalRoute != null)
        {
            var decision = vorpalRoute.Next((now - vorpalRouteAt).TotalSeconds, here2,
                out var next, out double untilDeparture);
            if (decision == EverkeepRouteProgress.Decision.Wait)
            {
                movementOwner.HoldVorpal();
                vorpalTargetSet = false;
                vorpalMotionAt = default;
                if (!EverkeepTemporalPlanner.CanTraverse(here2, here2, hazards, 0, untilDeparture + .1))
                {
                    // An early stop can be short of the planned grid point;
                    // only wait where the actual actor is still verified safe.
                    LogVorpalRejection("actual waiting position unsafe", here, now);
                    vorpalRoute = null;
                    vorpalRouteIntent = false;
                    nextVorpalSearch = default;
                }
                else
                    vorpalRouteIntent = untilDeparture <= .6;
            }
            else if (decision == EverkeepRouteProgress.Decision.Move)
            {
                var target = new Vector3(next.X, here.Y, next.Y);
                bool sameInput = vorpalTargetSet && vorpalTarget.Distance2D(target) <= .1f &&
                    movementOwner.HasVorpalMovement && vorpalMotionAt != default;
                double startupRemaining = sameInput ? Math.Max(0, .1 - (now - vorpalMotionAt).TotalSeconds) : .1;
                bool safe = EverkeepTemporalPlanner.CanTraverseTimed(here2, next, hazards, 4.5, 9, startupRemaining) ||
                    EverkeepTemporalPlanner.CanEgress(here2, next, hazards);
                if (!safe)
                {
                    LogVorpalRejection("timed movement segment unsafe", here, now);
                    movementOwner.HoldVorpal();
                    vorpalRoute = null;
                    vorpalTargetSet = vorpalRouteIntent = false;
                    vorpalMotionAt = default;
                    nextVorpalSearch = default;
                }
                else
                {
                    bool changed = !vorpalTargetSet || vorpalTarget.Distance2D(target) > .1f;
                    vorpalTarget = target;
                    vorpalTargetSet = vorpalRouteIntent = true;
                    if (Core.Me.IsCasting)
                    {
                        if (vorpalCastCancelAt == default || now - vorpalCastCancelAt >= TimeSpan.FromMilliseconds(500))
                        {
                            vorpalCastCancelAt = now;
                            ActionManager.StopCasting();
                        }
                    }
                    else
                    {
                        movementOwner.MoveVorpal(target);
                        if (movementOwner.HasVorpalMovement && (changed || vorpalMotionAt == default))
                        {
                            vorpalMotionAt = now;
                            vorpalMotionStart = here;
                            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                                Logger.Information("[Everkeep] Vorpal owned move player={0} target={1} hazards={2}.", here, target, hazards.Count);
                        }
                        if (vorpalMotionAt != default && (now - vorpalMotionAt).TotalSeconds > .2 &&
                            (here.Distance2D(vorpalMotionStart) < .1 ||
                             here.Distance2D(vorpalMotionStart) + .6 < ((now - vorpalMotionAt).TotalSeconds - .1) * 4.5))
                        {
                            // Movement slower than the verified envelope must not
                            // continue into a path checked for a faster actor.
                            movementOwner.HoldVorpal();
                            vorpalRoute = null;
                            vorpalTargetSet = vorpalRouteIntent = false;
                            vorpalMotionAt = default;
                            nextVorpalSearch = default;
                            Logger.Information("[Everkeep] Vorpal route stopped: movement fell outside its timing envelope.");
                        }
                    }
                }
            }
            else
            {
                if (decision == EverkeepRouteProgress.Decision.Expired)
                    LogVorpalRejection("waypoint deadline missed", here, now);
                movementOwner.HoldVorpal();
                vorpalRoute = null;
                vorpalTargetSet = vorpalRouteIntent = false;
                vorpalMotionAt = default;
                nextVorpalSearch = default;
            }
        }
        if (vorpalRoute == null && vorpalCleavePriority)
        {
            vorpalCleavePriority = false;
            hazards = CaptureVorpalHazards(now);
        }
        vorpalAppliedRevision = vorpalHazardRevision;
        if (vorpalWorker.TryTake(out long resultGeneration, out var route, out bool failed) && resultGeneration == vorpalGeneration)
        {
            bool current = !failed && vorpalSubmittedAddRevision == vorpalAddRevision &&
                (now - vorpalSubmittedAt).TotalMilliseconds <= 400 && here.Distance2D(vorpalSubmittedStart) <= .35f;
            if (current && vorpalRoute == null && route.Length != 0)
            {
                // Egress starts inside a live shape and cannot safely spend the
                // normal planner startup tick waiting in that same shape.
                vorpalCleavePriority = route[0].CleavePriority;
                if (vorpalCleavePriority)
                {
                    hazards = hazards.Where(h => h.HalfPlane || h.Circle).ToList();
                    Logger.Information("[Everkeep] No fully safe route; prioritizing observed cleaves and Burst over Vorpal lanes.");
                }
                bool emergencyEgress = EverkeepTemporalPlanner.CanEgress(here2, route[0].Position, hazards);
                vorpalRoute = new EverkeepRouteProgress(route, emergencyEgress);
                vorpalRouteAt = vorpalSubmittedAt;
                vorpalRouteAddRevision = vorpalAddRevision;
                vorpalRouteIntent = true;
                if (Core.Me.IsCasting && System.Numerics.Vector2.Distance(here2, route[0].Position) > .25f)
                {
                    // Clear an ordinary cast before the first scheduled input;
                    // waiting until departure can consume the route's safety margin.
                    vorpalCastCancelAt = now;
                    ActionManager.StopCasting();
                }
            }
            else if (current && route.Length == 0 && now >= nextVorpalNoRouteLog)
            {
                nextVorpalNoRouteLog = now.AddSeconds(1);
                Logger.Information("[Everkeep] Vorpal has no verified route at {0}; holding and retrying.", here);
            }
        }
        if (vorpalRoute == null && (now >= nextVorpalSearch || vorpalSubmittedRevision != vorpalHazardRevision))
        {
            int ticks = Math.Max(2, (int)Math.Ceiling(hazards.Max(h => h.End) / .3));
            if (ticks <= 60)
            {
                vorpalSubmittedStart = here;
                vorpalSubmittedAt = now;
                vorpalSubmittedRevision = vorpalHazardRevision;
                vorpalSubmittedAddRevision = vorpalAddRevision;
                vorpalGeneration = vorpalWorker.Submit(here2, hazards.ToArray(), ticks);
                nextVorpalSearch = now.AddMilliseconds(400);
            }
        }
        return true;
    }

    private void LogVorpalRejection(string reason, Vector3 here, DateTime now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || now < nextVorpalRejectLog) return;
        nextVorpalRejectLog = now.AddMilliseconds(500);
        Logger.Information("[Everkeep] Vorpal route replan reason={0} player={1}.", reason, here);
    }

    private List<EverkeepTemporalPlanner.Hazard> CaptureVorpalHazards(DateTime now)
    {
        var hazards = new List<EverkeepTemporalPlanner.Hazard>(vorpalPaths.Count + cleaveWindows.Count + burstWindows.Count);
        foreach (var h in vorpalPaths.Values)
        {
            var direction = new System.Numerics.Vector2((float)Math.Sin(h.Heading), (float)Math.Cos(h.Heading));
            hazards.Add(new EverkeepTemporalPlanner.Hazard(new System.Numerics.Vector2(h.Origin.X, h.Origin.Z) - direction * .25f, direction,
                h.Length + .5f, 3.25f, (h.Expires.AddMilliseconds(-1200) - now).TotalSeconds, (h.Expires - now).TotalSeconds, false));
        }
        foreach (var h in cleaveWindows.Values)
        {
            var direction = new System.Numerics.Vector2((float)Math.Sin(h.Heading), (float)Math.Cos(h.Heading));
            hazards.Add(new EverkeepTemporalPlanner.Hazard(new System.Numerics.Vector2(h.Origin.X, h.Origin.Z), direction,
                40f, 1.5f, (h.End.AddMilliseconds(-1150) - now).TotalSeconds, (h.End - now).TotalSeconds, true));
        }
        foreach (var h in burstWindows.Values)
            hazards.Add(new EverkeepTemporalPlanner.Hazard(new System.Numerics.Vector2(h.Origin.X, h.Origin.Z),
                System.Numerics.Vector2.UnitX, 8.5f, 0, 0, (h.End - now).TotalSeconds, false, true));
        return hazards;
    }

    private void ReleaseVorpalOwner()
    {
        if (vorpalExclusive)
        {
            movementOwner?.EndVorpal();
            Logger.Information("[Everkeep] Vorpal released exclusive movement: casting={0} outOfControl={1} forced={2} locked={3} occupied={4} paths={5}.",
                Core.Me?.IsCasting, MovementManager.GenerallyOutOfControl, MovementManager.ForceMoved,
                MovementManager.MovementLocked, MovementManager.IsOccupied, vorpalPaths.Count);
        }
        vorpalExclusive = false;
        vorpalCleavePriority = false;
        vorpalTargetSet = vorpalRouteIntent = false;
        vorpalRoute = null;
        vorpalMotionAt = default;
        vorpalWorker?.Dispose();
        vorpalWorker = null;
        vorpalSubmittedRevision = vorpalAppliedRevision = -1;
        nextVorpalCheck = nextVorpalSearch = nextVorpalNoRouteLog = vorpalCastCancelAt = nextVorpalRejectLog = default;
    }

    // Check arrival each pulse so sparse route planning cannot leave movement held past a waypoint.
    private void ObserveVorpalArrival(DateTime now)
    {
        if (vorpalRoute != null && vorpalTargetSet && movementOwner.HasVorpalMovement && vorpalMotionAt != default)
        {
            var here = Core.Me.Location;
            var here2 = new System.Numerics.Vector2(here.X, here.Z);
            var motionStart = new System.Numerics.Vector2(vorpalMotionStart.X, vorpalMotionStart.Z);
            var target = new System.Numerics.Vector2(vorpalTarget.X, vorpalTarget.Z);
            var arrival = EverkeepRouteProgress.Classify(motionStart, target, here2);
            if (arrival != EverkeepRouteProgress.Arrival.Approaching)
            {
                nextVorpalCheck = default;
                movementOwner.HoldVorpal();
                vorpalTargetSet = false;
                vorpalMotionAt = default;
                if (arrival == EverkeepRouteProgress.Arrival.Reached)
                    vorpalRoute.MarkCrossed();
                else
                {
                    LogVorpalRejection("movement deviated from planned line", here, now);
                    vorpalRoute = null;
                    vorpalRouteIntent = false;
                    nextVorpalSearch = default;
                }
            }
        }

    }

    private void ResetCleaveForecast()
    {
        rememberedFirstCleave = 0;
        rememberedCleaveHeading = 0;
        cleaveForecastActive = cleaveForecastUsed = cleaveForecastDisabled = false;
        cleaveForecastUntil = nextCleaveForecastCheck = DateTime.MinValue;
    }

    private void ObserveFirstCleave(uint id, float heading)
    {
        if (rememberedFirstCleave == 0)
        {
            rememberedFirstCleave = id;
            rememberedCleaveHeading = heading;
        }
        else if (id != rememberedFirstCleave || Math.Abs(Math.Sin((heading - rememberedCleaveHeading) / 2)) > .05)
        {
            cleaveForecastDisabled = true;
            cleaveForecastActive = false;
            Logger.Information("[Everkeep] Cleave forecast disabled after first-helper order or heading changed.");
        }
    }

    private void UpdateCleaveForecast(DateTime now)
    {
        if (vorpalPaths.Count == 0)
        {
            cleaveForecastActive = false;
            cleaveForecastUsed = false;
            return;
        }
        if (cleaveImpacts.Count != 0 || cleaveForecastDisabled || now >= cleaveForecastUntil && cleaveForecastActive)
        {
            cleaveForecastActive = false;
            return;
        }
        if (now < nextCleaveForecastCheck) return;
        nextCleaveForecastCheck = now.AddMilliseconds(100);
        // The forecast is speculative. On 2026-11-02 it activated while the
        // player was already inside a real Vorpal path, constraining native
        // escape during the first 1.1s of the preview. Yield the forecast to
        // that known hazard and never reissue it after an active forecast yields.
        bool playerSafeFromVorpal = VorpalSnapshotsSafe(Core.Me.Location, Core.Me.Location, vorpalPaths.Values);
        if (cleaveForecastActive && !playerSafeFromVorpal)
        {
            cleaveForecastActive = false;
            Logger.Information("[Everkeep] Cleave forecast yielded to occupied Vorpal path at {0}.", Core.Me.Location);
            return;
        }
        if (cleaveForecastActive || cleaveForecastUsed || rememberedFirstCleave == 0 ||
            !ForecastMayConstrain(playerSafeFromVorpal, AvoidanceManager.IsRunningOutOfAvoid)) return;
        var helper = GameObjectManager.GetObjectByObjectId(rememberedFirstCleave) as BattleCharacter;
        if (helper == null || !helper.IsValid || Math.Abs(Math.Sin((helper.Heading - rememberedCleaveHeading) / 2)) > .05 ||
            VorpalSnapshotsSafe(ArenaCenter.ZoraalJa, ArenaCenter.ZoraalJa, vorpalPaths.Values)) return;
        cleaveForecastUsed = cleaveForecastActive = true;
        cleaveForecastUntil = now.AddSeconds(3);
        Logger.Information("[Everkeep] Forecasting previously observed first cleave helper={0:X} heading={1:F3} player={2}",
            helper.ObjectId, helper.Heading, Core.Me.Location);
    }

    private static bool ForecastMayConstrain(bool safeFromVorpal, bool nativeEscaping) =>
        safeFromVorpal && !nativeEscaping;

    private void UpdatePartyMarkers()
    {
        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && (a.IsMe || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToList();
        // Enumeration can omit the player; include them for self-targeted markers.
        if (party.All(a => a.ObjectId != Core.Me.ObjectId)) party.Add(Core.Me);
        spreadActive = party.Any(a => a.IsAlive && HasSpreadMarker(a));
        var now = DateTime.UtcNow;
        // Duty's Edge is an instant effect. Bound the receipt scan around the
        // observed visual or Forged Track rather than reading memory every pulse.
        if (now >= edgeObserveUntil && now >= nextEdgeCastScan)
        {
            nextEdgeCastScan = now.AddMilliseconds(500);
            if (GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Any(a => a.IsValid && a.IsCasting && a.CastingSpellId == 37748))
            {
                edgeVisualAt = now;
                edgeObserveUntil = now.AddSeconds(15);
            }
        }
        if (tracks.Count > 0) edgeHelperUntil = now.AddSeconds(20);
        if (now < edgeObserveUntil && edgeHelperUntil < edgeObserveUntil) edgeHelperUntil = edgeObserveUntil;
        if (now < edgeHelperUntil && now >= nextEdgeHelperScan)
        {
            nextEdgeHelperScan = now.AddMilliseconds(200);
            ObserveEdgeReceipts(party);
        }
    }

    private void InitializeEdgeEffectReader()
    {
        edgeEffectSlot = 0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            // Incoming-effect allocator call, instruction-masked with Ghidra:
            // unique in Global 46A52AB1, 9483706D, 04766591 and available CN/TC.
            // Read32 is the virtual slot; TC differs and is intentionally rejected.
            if (LlamaLibrary.Memory.OffsetManager.ActiveRecord.RegionFlag != OffsetFlags.Global) return;
            var process = Core.Memory.Process;
            long start = process.StartTime.ToUniversalTime().Ticks;
            if (ReferenceEquals(edgeReaderMemory, Core.Memory) && edgeReaderPid == process.Id &&
                edgeReaderStartTicks == start && edgeReaderCachedSlot != 0)
            {
                edgeEffectSlot = edgeReaderCachedSlot;
                return;
            }
            const string pattern = "Search FF 90 ? ? ? ? 48 8B C8 E8 ? ? ? ? 48 8B D8 C7 40 ? ? ? ? ? 41 8B 4F ? 89 08";
            using var finder = new GreyMagicPf();
            var hits = finder.SearchMany(pattern);
            if (hits.Length != 1) return;
            var module = Core.Memory.Process.MainModule;
            long rva = hits[0].ToInt64();
            if (module == null || rva < 0 || rva + 6 > module.ModuleMemorySize) return;
            var site = Core.Memory.GetAbsolute(hits[0]);
            int slot = Core.Memory.Read<int>(site + 2);
            if (slot == 0x290)
            {
                edgeEffectSlot = edgeReaderCachedSlot = slot;
                edgeReaderMemory = Core.Memory;
                edgeReaderPid = process.Id;
                edgeReaderStartTicks = start;
            }
        }
        catch (Exception ex) { Logger.Information("[Everkeep] EDGE_RECEIPT unavailable: {0}", ex.Message); }
        finally { Logger.Information("[Everkeep] EDGE_RECEIPT init slot={0:X} ms={1:F2}", edgeEffectSlot,
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency); }
    }

    private void ObserveEdgeReceipts(List<BattleCharacter> party)
    {
        if (edgeEffectSlot == 0) return;
        try
        {
            using (Core.Memory.AcquireFrame())
            using (Core.Memory.TemporaryCacheState(false))
            foreach (var actor in party)
            {
                if (!actor.IsValid || actor.Pointer == IntPtr.Zero) continue;
                var table = Core.Memory.Read<IntPtr>(actor.Pointer);
                if (table == IntPtr.Zero) continue;
                var getter = Core.Memory.Read<IntPtr>(table + edgeEffectSlot);
                var module = Core.Memory.Process.MainModule;
                long relative = getter.ToInt64() - module.BaseAddress.ToInt64();
                if (relative < 0 || relative + 8 > module.ModuleMemorySize) continue;
                var code = Core.Memory.ReadBytes(getter, 8);
                if (code[0] != 0x48 || code[1] != 0x8D || code[2] != 0x81 || code[7] != 0xC3 ||
                    BitConverter.ToInt32(code, 3) != 0x2900) continue;
                // 32 EffectEntry records, validated by Crucible's live receipt
                // reader. Require the intended recipient and a nonzero sequence
                // before using the effect to select the stack carrier.
                var bytes = Core.Memory.ReadBytes(actor.Pointer + 0x2900, 0xF00);
                for (int offset = 0; offset < bytes.Length; offset += 0x78)
                {
                    if (!TryEdgeReceipt(bytes, offset, actor.ObjectId, out uint sequence, out ulong source)) continue;
                    string key = $"{sequence}:{source:X}:{actor.ObjectId:X}";
                    if (edgeEffectSeen.Add(key))
                    {
                        // The source is the helper that emits the line, not an
                        // arbitrary party member. Reject stale/foreign objects.
                        if (source <= uint.MaxValue && GameObjectManager.GetObjectByObjectId((uint)source) is BattleCharacter helper &&
                            helper.IsValid && helper.BaseId == 0x233C)
                        {
                            Logger.Information("[Everkeep] Duty's Edge source helper={0:X} location={1} target={2:X} location={3}",
                                helper.ObjectId, helper.Location, actor.ObjectId, actor.Location);
                            edgeReceiptAt = DateTime.UtcNow;
                            edgeTargetId = actor.ObjectId;
                            edgeSourceId = (uint)source;
                            edgeDestination = null;
                            nextEdgePosition = DateTime.MinValue;
                        }
                        Logger.Information("[Everkeep] EDGE_RECEIPT sequence={0} action=35567 source={1:X} target={2:X} npc={3} location={4}",
                            sequence, source, actor.ObjectId, actor.NpcId, actor.Location);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            edgeEffectSlot = 0;
            Logger.Information("[Everkeep] EDGE_RECEIPT disabled: {0}", ex.Message);
        }
    }

    private static bool TryEdgeReceipt(byte[] bytes, int offset, uint actorId, out uint sequence, out ulong source)
    {
        sequence = 0;
        source = 0;
        if (bytes == null || offset < 0 || offset > bytes.Length - 0x78) return false;
        sequence = BitConverter.ToUInt32(bytes, offset);
        if (sequence == 0 || BitConverter.ToUInt32(bytes, offset + 0x0C) != 35567 ||
            BitConverter.ToUInt64(bytes, offset + 0x28) != actorId) return false;
        source = BitConverter.ToUInt64(bytes, offset + 0x18);
        return true;
    }

    private void ClearEdgeStack()
    {
        edgeVisualAt = edgeReceiptAt = edgeStackUntil = nextEdgePosition = DateTime.MinValue;
        edgeTargetId = edgeSourceId = 0;
        edgeDestination = null;
    }

    // The four 8y-wide line hits begin approximately 5.4s after 35567 and
    // continue at 2.1s intervals. The 2026-11-02 receipts identified distinct
    // targets in consecutive sequences; require a matching nearby visual so a
    // stale ring-buffer entry cannot command movement.
    private bool UpdateEdgeStack()
    {
        var now = DateTime.UtcNow;
        if (edgeStackUntil == DateTime.MinValue && edgeTargetId != 0 && edgeVisualAt != DateTime.MinValue &&
            Math.Abs((edgeReceiptAt - edgeVisualAt).TotalSeconds) <= 2 && now - edgeReceiptAt < TimeSpan.FromSeconds(2))
        {
            edgeStackUntil = edgeReceiptAt.AddSeconds(13);
            Logger.Information("[Everkeep] Duty's Edge stack source={0:X} target={1:X} self={2}", edgeSourceId, edgeTargetId, edgeTargetId == Core.Me.ObjectId);
        }
        if (edgeStackUntil == DateTime.MinValue) return false;
        if (now >= edgeStackUntil)
        {
            ClearEdgeStack();
            if (positioningKind == "Duty's Edge") ReleasePosition(false);
            return false;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            if (MaintainStationaryCastHold("Duty's Edge")) return true;
            if (positioningKind == "Duty's Edge")
            {
                Logger.Information("[Everkeep] Edge yield escaping={0} casting={1} forced={2} locked={3} occupied={4} movingLease={5}",
                    AvoidanceManager.IsRunningOutOfAvoid, Core.Me.IsCasting, MovementManager.ForceMoved,
                    MovementManager.MovementLocked, MovementManager.IsOccupied, positioningMoving);
                ReleasePosition(false);
            }
            return true;
        }
        var source = GameObjectManager.GetObjectByObjectId(edgeSourceId) as BattleCharacter;
        var target = edgeTargetId == Core.Me.ObjectId ? Core.Me : GameObjectManager.GetObjectByObjectId(edgeTargetId) as BattleCharacter;
        if (source == null || !source.IsValid || target == null || !target.IsValid || !target.IsAlive)
        {
            if (positioningKind == "Duty's Edge") ReleasePosition(false);
            return true;
        }
        if (now >= nextEdgePosition)
        {
            nextEdgePosition = now.AddMilliseconds(500);
            Vector3? proposed;
            if (target.ObjectId != Core.Me.ObjectId)
                proposed = EdgeSharePoint(source.Location, target.Location, Core.Me.Location);
            else
            {
                var participants = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.IsAlive && !a.IsMe && (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).Select(a => a.Location).ToArray();
                proposed = EdgeSelfPoint(source.Location, Core.Me.Location, participants, CleavePathSafe);
            }
            if (!proposed.HasValue || !CleavePathSafe(Core.Me.Location, proposed.Value)) edgeDestination = null;
            else if (!edgeDestination.HasValue || edgeDestination.Value.Distance2D(proposed.Value) > 1.25f ||
                     !CleavePathSafe(Core.Me.Location, edgeDestination.Value)) edgeDestination = proposed;
        }
        if (edgeDestination.HasValue && CleavePathSafe(Core.Me.Location, edgeDestination.Value))
            StagePosition(edgeDestination.Value, "Duty's Edge");
        else if (positioningKind == "Duty's Edge") ReleasePosition(false);
        return true;
    }

    private static bool InEdgeLine(Vector3 source, Vector3 target, Vector3 player)
    {
        float dx = target.X - source.X, dz = target.Z - source.Z;
        float length = (float)Math.Sqrt(dx * dx + dz * dz);
        if (length < 2f) return false;
        float px = player.X - source.X, pz = player.Z - source.Z;
        float along = (px * dx + pz * dz) / length;
        float side = Math.Abs(px * dz - pz * dx) / length;
        return along >= 0.5f && along <= length - .5f && side <= 3f;
    }

    private static Vector3? EdgeSharePoint(Vector3 source, Vector3 target, Vector3 player)
    {
        if (InEdgeLine(source, target, player)) return player;
        float dx = target.X - source.X, dz = target.Z - source.Z;
        float length = (float)Math.Sqrt(dx * dx + dz * dz);
        if (length < 2f) return null;
        return new Vector3(target.X - dx / length * 1.5f, player.Y, target.Z - dz / length * 1.5f);
    }

    private static Vector3? EdgeSelfPoint(Vector3 source, Vector3 player, Vector3[] participants, Func<Vector3, Vector3, bool> safe)
    {
        int Count(Vector3 p) => participants.Count(a => InEdgeLine(source, p, a));
        if (Count(player) >= Math.Min(3, participants.Length) && participants.Length > 0) return player;
        Vector3? best = null; int score = 0; float distance = float.MaxValue;
        foreach (var member in participants)
        {
            float dx = member.X - source.X, dz = member.Z - source.Z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length < 1f) continue;
            var candidate = new Vector3(member.X + dx / length * 2f, player.Y, member.Z + dz / length * 2f);
            int count = Count(candidate); float travel = player.Distance2D(candidate);
            if (count == 0 || !safe(player, candidate) || count < score || (count == score && travel >= distance)) continue;
            best = candidate; score = count; distance = travel;
        }
        return best;
    }

    private void UpdateCleaveStaging()
    {
        // The boss's visual starts with the first helper, 2.35s before the second
        // helper appears. Begin staging immediately: waiting for both helpers left
        // too little time to travel from the 18y opening position. Two yalms behind
        // the first helper faces its safe half and allows a short second crossing.
        // The boss visual ended before the first helper's delayed impact in
        // the 2026-11-02 capture. Keep staging only while that remembered first
        // helper remains the active retained half; do not stage for the second.
        var first = FirstCleaveStageAllowed(sequenceFirstCleave, activeCleaveId, cleaveImpacts.ContainsKey(sequenceFirstCleave))
            ? GameObjectManager.GetObjectByObjectId(sequenceFirstCleave) as BattleCharacter : null;
        if (first == null || !first.IsValid || !cleaveImpacts.ContainsKey(sequenceFirstCleave) ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            if (first != null && first.IsValid && MaintainStationaryCastHold("cleave")) return;
            cleaveDestination = null;
            cleaveFirstId = 0;
            ReleasePosition(false);
            return;
        }
        if (cleaveFirstId != first.ObjectId)
        {
            cleaveFirstId = first.ObjectId;
            cleaveDestination = null;
            nextCleaveSearch = DateTime.MinValue;
        }
        if (cleaveDestination.HasValue && !CleaveStagingPathSafe(Core.Me.Location, cleaveDestination.Value))
            cleaveDestination = null;
        if (!cleaveDestination.HasValue && DateTime.UtcNow >= nextCleaveSearch)
        {
            // Burst can cover the usual central point while leaving a nearby
            // portion of the same safe half open. Keep a verified alternative
            // stable, and bound failed searches to avoid per-pulse churn.
            nextCleaveSearch = DateTime.UtcNow.AddMilliseconds(250);
            cleaveDestination = FindCleaveDestination(Core.Me.Location, first.Location, first.Heading, CleaveStagingPathSafe);
        }
        if (!cleaveDestination.HasValue)
        {
            ReleasePosition(false);
            return;
        }

        if (!positioningLeased)
            Logger.Information("[Everkeep] Cleave staging first={0:X} heading={1:F3} player={2} destination={3} remainingMs={4:F0}",
                first.ObjectId, first.Heading, Core.Me.Location, cleaveDestination.Value, (cleaveImpacts[sequenceFirstCleave] - DateTime.UtcNow).TotalMilliseconds);
        StagePosition(cleaveDestination.Value, "cleave");
    }

    private static bool FirstCleaveStageAllowed(uint first, uint activeHalf, bool retained) =>
        first != 0 && first == activeHalf && retained;

    private static Vector3? FindCleaveDestination(Vector3 from, Vector3 center, float heading,
        Func<Vector3, Vector3, bool> pathSafe)
    {
        float fx = (float)Math.Sin(heading), fz = (float)Math.Cos(heading);
        // The preferred point remains two yalms behind the first helper. Sweep
        // laterally on that safe side if a simultaneous Burst blocks the center.
        for (int step = 0; step <= 16; ++step)
        {
            int side = step == 0 ? 0 : (step + 1) / 2 * (step % 2 == 1 ? 1 : -1);
            var point = new Vector3(center.X - 2f * fx + side * fz, from.Y,
                center.Z - 2f * fz - side * fx);
            if (pathSafe(from, point)) return point;
        }
        return null;
    }

    private bool CleaveStagingPathSafe(Vector3 from, Vector3 to) =>
        CleavePathSafe(from, to) && VorpalSnapshotsSafe(from, to, vorpalPaths.Values);

    private bool OrdinaryPathSafe(Vector3 from, Vector3 to) =>
        OrdinaryAvoidsSafe(from, to) && (vorpalPaths.Count == 0 || VorpalSnapshotsSafe(from, to, vorpalPaths.Values));

    private static float FloorDepth(Vector3 p)
    {
        const float angle = .8f;
        float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
        float dx = p.X - 100f, dz = p.Z - 100f;
        return Math.Max(Math.Abs(dx * c + dz * s), Math.Abs(-dx * s + dz * c));
    }

    private bool OrdinaryAvoidsSafe(Vector3 from, Vector3 to)
    {
        // The 2026-11-02 live guard stopped a return waypoint when the player
        // already stood outside the rotated floor. Allow monotonic inward egress
        // from only the encounter's own registered floor polygons; no other initially
        // occupied hazard may be crossed and no cleared floor may be re-entered.
        if (float.IsNaN(from.X) || float.IsNaN(from.Z) || float.IsNaN(to.X) || float.IsNaN(to.Z) ||
            float.IsInfinity(from.X) || float.IsInfinity(from.Z) || float.IsInfinity(to.X) || float.IsInfinity(to.Z)) return false;
        bool outerEgress = false, smallEgress = false, outerExited = false, smallExited = false;
        foreach (var avoid in AvoidanceManager.Avoids)
        {
            if (avoid.ShouldIgnore || !avoid.IsPointInAvoid(from)) continue;
            if (outerFloorAvoid != null && ReferenceEquals(avoid.AvoidInfo, outerFloorAvoid)) outerEgress = true;
            else if (smallFloorAvoid != null && ReferenceEquals(avoid.AvoidInfo, smallFloorAvoid)) smallEgress = true;
            else return false;
        }
        bool anyFloorEgress = outerEgress || smallEgress;
        float previousDepth = FloorDepth(from);
        if (anyFloorEgress && FloorDepth(to) >= previousDepth - .01f) return false;
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 1; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var point = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            float depth = FloorDepth(point);
            if (anyFloorEgress && depth > previousDepth + .01f) return false;
            previousDepth = depth;
            foreach (var avoid in AvoidanceManager.Avoids)
            {
                if (avoid.ShouldIgnore) continue;
                bool inside = avoid.IsPointInAvoid(point);
                if (outerFloorAvoid != null && ReferenceEquals(avoid.AvoidInfo, outerFloorAvoid) && outerEgress)
                {
                    if (outerExited && inside) return false;
                    if (!inside) outerExited = true;
                }
                else if (smallFloorAvoid != null && ReferenceEquals(avoid.AvoidInfo, smallFloorAvoid) && smallEgress)
                {
                    if (smallExited && inside) return false;
                    if (!inside) smallExited = true;
                }
                else if (inside) return false;
            }
        }
        return true;
    }

    private static bool VorpalSnapshotsSafe(Vector3 from, Vector3 to, IEnumerable<VorpalHazard> hazards)
    {
        // The native avoidance collection can lag a newly captured helper by a
        // pulse. At 03:10:10.821 a cleave request chose a point inside that new
        // path and was cancelled 36ms later. Check our immutable snapshots too,
        // so the existing owner never starts that immediately superseded input.
        var active = hazards.ToArray();
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var p = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            foreach (var h in active)
            {
                if (Math.Abs(p.Y - h.Origin.Y) >= 7.5f) continue;
                float dx = p.X - h.Origin.X, dz = p.Z - h.Origin.Z;
                float along = dx * (float)Math.Sin(h.Heading) + dz * (float)Math.Cos(h.Heading);
                float side = dx * (float)Math.Cos(h.Heading) - dz * (float)Math.Sin(h.Heading);
                if (along >= -.5f && along <= h.Length + .5f && Math.Abs(side) <= 3f) return false;
            }
        }
        return true;
    }

    private bool MaintainStationaryCastHold(string kind)
    {
        // Casting alone makes GenerallyOutOfControl true. The 2026-11-02
        // captures repeatedly dropped and reacquired a stationary hold between
        // spells. The 2026-11-02 trace shows occupied=true during ordinary
        // casts while forced=false and locked=false. Refresh only an existing
        // stationary hold; forced movement, lock, loading and native escape
        // retain priority, and this branch never issues movement.
        if (!positioningLeased || positioningMoving || positioningKind != kind || !Core.Me.IsCasting ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.ForceMoved || MovementManager.MovementLocked)
            return false;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Everkeep " + kind + " staging");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, 500, "Everkeep " + kind + " staging");
        return true;
    }

    private void StagePosition(Vector3 point, string kind)
    {
        bool acquiring = !positioningLeased;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Everkeep " + kind + " staging");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, 500, "Everkeep " + kind + " staging");
        if (!positioningLeased && kind == "Dawn of an Age")
            Logger.Information("[Everkeep] Dawn staging player={0} destination={1}.", Core.Me.Location, point);
        positioningKind = kind;
        positioningLeased = true;
        if (Core.Me.Location.Distance2D(point) <= .65f)
        {
            positioningMoving = false;
            movementOwner.Hold(acquiring);
        }
        else
        {
            positioningMoving = true;
            movementOwner.Move(point);
        }
    }

    private static Vector3? FindDawnDestination(Vector3 from)
    {
        // The incoming 19y square is rotated .8 radians. Project onto a square
        // inset another yalm, then try interior grid points if another verified
        // avoid blocks that direct route. Keep the chosen point until invalidated.
        const float angle = .8f, half = 8.5f;
        float c = (float)Math.Cos(angle), s = (float)Math.Sin(angle);
        float dx = from.X - 100f, dz = from.Z - 100f;
        float u = Math.Clamp(dx * c + dz * s, -half, half);
        float v = Math.Clamp(-dx * s + dz * c, -half, half);
        Vector3 World(float a, float b) => new(100f + a * c - b * s, from.Y, 100f + a * s + b * c);
        var points = new List<Vector3> { World(u, v) };
        for (int i = -2; i <= 2; ++i)
            for (int j = -2; j <= 2; ++j)
                points.Add(World(i * 4f, j * 4f));
        return points.Where(p => CleavePathSafe(from, p)).OrderBy(p => p.Distance2D(from))
            .Select(p => (Vector3?)p).FirstOrDefault();
    }

    private static bool CleavePathSafe(Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var p = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p)))
                return false;
        }
        return true;
    }

    private void ReleasePosition(bool detach)
    {
        positioningMoving = false;
        movementOwner?.Release();
        if (positioningLeased)
        {
            Logger.Information("[Everkeep] {0} staging released at {1}; escaping={2}.", positioningKind, Core.Me?.Location, AvoidanceManager.IsRunningOutOfAvoid);
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Everkeep cleave staging ended");
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Everkeep cleave staging ended");
            positioningLeased = false;
            positioningKind = null;
        }
        if (detach)
        {
            ReleaseVorpalOwner();
            movementOwner?.Detach();
            movementOwner = null;
        }
    }

    protected override Task<bool> ExitDungeonAsync()
    {
        ReleasePosition(true);
        Unsubscribe();
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }

    /// <summary>Releases encounter input and pulse subscriptions on plugin disable or replacement.</summary>
    public static void ResetActive()
    {
        var previous = active;
        active = null;
        previous?.ReleasePosition(true);
        previous?.Unsubscribe();
    }

    private void Unsubscribe()
    {
        smallArenaActive = false;
        actualizeSeen = false;
        dawnPreparing = false;
        dawnNoPathLogged = false;
        dawnDestination = null;
        tiles.Clear();
        tracks.Clear();
        ReleaseVorpalOwner();
        cleaveImpacts.Clear();
        cleaveWindows.Clear();
        burstWindows.Clear();
        activeCleaveId = sequenceFirstCleave = 0;
        ResetCleaveForecast();
        ClearEdgeStack();
        edgeEffectSeen.Clear();
        edgeEffectSlot = 0;
        spreadActive = false;
        nextPartyScan = default;
        if (subscribed)
        {
            TreeRoot.OnStop -= OnBotStopped;
            ff14bot.NeoProfile.BotEvents.OnPulse -= OnBotPulse;
            subscribed = false;
        }
    }

    // This adapter is the only input channel while installed. Native escape and
    // newer ordinary commands supersede staging; stale cleanup cannot stop them.
    private sealed class EncounterMover(IPlayerMover inner, Func<Vector3, Vector3, bool> pathSafe) : IPlayerMover
    {
        private enum Owner { None, Ordinary, Escape, Stage, Vorpal }
        private Owner owner;
        private bool detached, vorpalMoving;
        private Vector3 destination;
        private DateTime nextHandoffLog;
        private DateTime nextGuardCheck;
        private Vector3 ordinaryWaypoint;
        private bool ordinaryGuarded;
        private Vector3 stalledAnchor, stalledWaypoint, shortEscapeTarget;
        private DateTime stalledSince;
        private bool shortEscapeActive, shortEscapeStopped;
        private readonly CapabilityManagerHandle ordinaryGuardHandle = CapabilityManager.CreateNewHandle();
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal bool HasNativeEscapePriority => IsCurrent && owner != Owner.Vorpal && AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;
        internal bool HasVorpalMovement => IsCurrent && owner == Owner.Vorpal && vorpalMoving;
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent || owner == Owner.Vorpal) return;
            if (owner == Owner.Stage && DateTime.UtcNow >= nextHandoffLog)
            {
                nextHandoffLog = DateTime.UtcNow.AddMilliseconds(500);
                Logger.Information("[Everkeep] Staging input superseded by {0} destination={1} player={2}.",
                    AvoidanceManager.IsRunningOutOfAvoid ? "native escape" : "ordinary movement", location, Core.Me.Location);
            }
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
            {
                ReleaseOrdinaryGuard();
                owner = AvoidanceManager.IsRunningOutOfAvoid ? Owner.Escape : Owner.Ordinary;
                bool canRecoverEscape = AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
                    !MovementManager.ForceMoved && !MovementManager.MovementLocked && Core.Me.IsAlive && Core.Me.InCombat &&
                    !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene;
                if (canRecoverEscape && TryRecoverStalledEscape(location)) return;
                if (!canRecoverEscape) ResetShortEscape();

                inner.MoveTowards(location);
                return;
            }
            ResetShortEscape();
            ordinaryWaypoint = location;
            if (!pathSafe(Core.Me.Location, location))
            {
                // An unsafe ordinary request cannot cancel an existing semantic
                // request or a still-owned native escape.
                if (owner is Owner.Stage or Owner.Escape) return;
                HoldUnsafeOrdinary();
                return;
            }
            ReleaseOrdinaryGuard();
            owner = Owner.Ordinary;

            inner.MoveTowards(location);
        }
        public void MoveStop()
        {
            if (!IsCurrent || owner == Owner.Vorpal) return;
            ResetShortEscape();

            if (owner == Owner.Stage && DateTime.UtcNow >= nextHandoffLog)
            {
                nextHandoffLog = DateTime.UtcNow.AddMilliseconds(500);
                Logger.Information("[Everkeep] Staging input stopped at {0}; escaping={1}.", Core.Me.Location, AvoidanceManager.IsRunningOutOfAvoid);
            }
            ReleaseOrdinaryGuard();
            owner = Owner.None;
            inner.MoveStop();
        }
        // During a captured Half Circuit escape the native owner repeatedly sent
        // one waypoint within 0.2y of the player for over four seconds while the
        // player oscillated at the half-room boundary. Continue forwarding native
        // escape while observing progress. Only after it stalls, choose a short
        // segment that exits every initial avoid without entering any new one;
        // revalidate on every input and yield immediately to a changed waypoint.
        private bool TryRecoverStalledEscape(Vector3 requested)
        {
            var here = Core.Me.Location;
            if (shortEscapeActive)
            {
                if (requested.Distance2D(stalledWaypoint) > .4f || !EnemyAction.HalfCircuit.IsCasting())
                {
                    ResetShortEscape();
                    return false;
                }
                var current = AvoidanceManager.Avoids.Where(a => !a.ShouldIgnore).ToArray();
                if (!current.Any(a => a.IsPointInAvoid(here)))
                {
                    if (!shortEscapeStopped)
                    {
                        inner.MoveStop();

                        shortEscapeStopped = true;
                    }
                    return true;
                }
                if (!ShortEscapeSegmentSafe(here, shortEscapeTarget, current))
                {
                    ResetShortEscape();
                    return false;
                }

                inner.MoveTowards(shortEscapeTarget);
                return true;
            }
            var now = DateTime.UtcNow;
            if (stalledSince == default || here.Distance2D(stalledAnchor) > .25f ||
                requested.Distance2D(stalledWaypoint) > .4f || here.Distance2D(requested) > .3f)
            {
                stalledSince = now;
                stalledAnchor = here;
                stalledWaypoint = requested;
                return false;
            }
            if ((now - stalledSince).TotalMilliseconds < 350 || !EnemyAction.HalfCircuit.IsCasting()) return false;
            stalledSince = now; // Bound failed searches while native escape remains in control.
            var avoids = AvoidanceManager.Avoids.Where(a => !a.ShouldIgnore).ToArray();
            if (!avoids.Any(a => a.IsPointInAvoid(here))) return false;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long budget = System.Diagnostics.Stopwatch.Frequency * 4 / 1000;
            for (int radius = 1; radius <= 4; radius++)
            {
                for (int direction = 0; direction < 8; direction++)
                {
                    if (System.Diagnostics.Stopwatch.GetTimestamp() - started >= budget) return false;
                    double angle = direction * Math.PI / 4;
                    var target = new Vector3(here.X + (float)Math.Cos(angle) * radius * .5f, here.Y,
                        here.Z + (float)Math.Sin(angle) * radius * .5f);
                    if (!ShortEscapeSegmentSafe(here, target, avoids)) continue;
                    shortEscapeTarget = target;
                    shortEscapeActive = true;
                    shortEscapeStopped = false;

                    Logger.Information("[Everkeep] Half Circuit stalled native escape: verified short exit from {0} to {1}; native waypoint={2}.", here, target, requested);
                    inner.MoveTowards(target);
                    return true;
                }
            }
            return false;
        }
        private static bool ShortEscapeSegmentSafe(Vector3 from, Vector3 to, Avoid[] avoids)
        {
            // Initially occupied shapes may be exited once, but cannot be entered
            // again. No other active shape may be crossed, even between endpoints.
            var exited = new bool[avoids.Length];
            for (int i = 0; i < avoids.Length; i++) exited[i] = !avoids[i].IsPointInAvoid(from);
            int samples = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .1f));
            for (int step = 1; step <= samples; step++)
            {
                float f = (float)step / samples;
                var p = new Vector3(from.X + (to.X - from.X) * f, from.Y, from.Z + (to.Z - from.Z) * f);
                for (int i = 0; i < avoids.Length; i++)
                {
                    bool inside = avoids[i].IsPointInAvoid(p);
                    if (inside && exited[i]) return false;
                    if (!inside) exited[i] = true;
                    if (step == samples && inside) return false;
                }
            }
            return true;
        }
        private void ResetShortEscape()
        {
            shortEscapeActive = shortEscapeStopped = false;
            stalledSince = default;
        }

        // Vorpal owns this same input adapter exclusively; late native or
        // routine callbacks cannot cancel its lease or overwrite its target.
        internal bool BeginVorpal()
        {
            if (!IsCurrent || MovementManager.ForceMoved || MovementManager.MovementLocked ||
                !Core.Me.IsCasting && (MovementManager.GenerallyOutOfControl || MovementManager.IsOccupied)) return false;
            ReleaseOrdinaryGuard();
            ResetShortEscape();
            Navigator.Stop();
            owner = Owner.Vorpal;
            vorpalMoving = false;
            return true;
        }
        internal void MoveVorpal(Vector3 point)
        {
            if (!IsCurrent || owner != Owner.Vorpal || MovementManager.GenerallyOutOfControl ||
                MovementManager.ForceMoved || MovementManager.MovementLocked || MovementManager.IsOccupied) return;
            if (vorpalMoving && destination.Distance2D(point) < .1f) return;
            destination = point;
            vorpalMoving = true;

            inner.MoveTowards(point);
        }
        internal void HoldVorpal()
        {
            if (!IsCurrent || owner != Owner.Vorpal || !vorpalMoving) return;
            vorpalMoving = false;
            if (MovementManager.GenerallyOutOfControl || MovementManager.ForceMoved || MovementManager.MovementLocked) return;

            inner.MoveStop();
        }
        internal void EndVorpal()
        {
            if (!IsCurrent || owner != Owner.Vorpal) return;
            HoldVorpal();
            owner = Owner.None;
        }

        internal void Move(Vector3 point)
        {
            if (!IsCurrent || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
            ReleaseOrdinaryGuard();
            if (owner == Owner.Stage && destination.Distance2D(point) < .1f) return;
            // Cancel the prior ordinary service route once before the staging input
            // becomes authoritative; subsequent pulses do not repeatedly stop it.
            if (owner != Owner.Stage) Navigator.Stop();
            destination = point;
            owner = Owner.Stage;

            inner.MoveTowards(point);
        }
        internal void Hold(bool acquiring)
        {
            // A newly acquired hold must stop an outstanding ordinary route;
            // later stale callbacks cannot stop an unrelated successor.
            ReleaseOrdinaryGuard();
            if (acquiring && IsCurrent && owner == Owner.Ordinary &&
                !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
                MoveStop();
            else
                Release();
        }
        internal void Release()
        {
            if (!IsCurrent || owner != Owner.Stage) return;
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
                owner = Owner.None; // The successor owns movement; do not stop it later.
            else
                MoveStop();
        }
        private void HoldUnsafeOrdinary()
        {
            if (!IsCurrent) return;
            if (!ordinaryGuarded)
            {
                // Cancelling the service route prevents unstuck jump/turn inputs
                // while the actual next waypoint crosses a live hazard. A new
                // safe waypoint or native escape immediately supersedes this hold.
                Navigator.Stop();
                owner = Owner.None;
                ordinaryGuarded = true;
                Logger.Information("[Everkeep] Ordinary waypoint held player={0} waypoint={1}", Core.Me.Location, ordinaryWaypoint);
            }
            CapabilityManager.Update(ordinaryGuardHandle, CapabilityFlags.Movement, 500, "Everkeep unsafe ordinary waypoint");
            CapabilityManager.Update(ordinaryGuardHandle, CapabilityFlags.GapCloser, 500, "Everkeep unsafe ordinary waypoint");
        }
        private void ReleaseOrdinaryGuard()
        {
            if (!ordinaryGuarded) return;
            CapabilityManager.Clear(ordinaryGuardHandle, CapabilityFlags.Movement, "Everkeep ordinary waypoint released");
            CapabilityManager.Clear(ordinaryGuardHandle, CapabilityFlags.GapCloser, "Everkeep ordinary waypoint released");
            ordinaryGuarded = false;
        }
        internal void RecheckOrdinary()
        {
            if (!IsCurrent) return;
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.ForceMoved ||
                !Core.Me.IsAlive || !Core.Me.InCombat || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
            {
                ReleaseOrdinaryGuard();
                return;
            }
            if (ordinaryGuarded)
            {
                CapabilityManager.Update(ordinaryGuardHandle, CapabilityFlags.Movement, 500, "Everkeep unsafe ordinary waypoint");
                CapabilityManager.Update(ordinaryGuardHandle, CapabilityFlags.GapCloser, 500, "Everkeep unsafe ordinary waypoint");
            }
            if (owner != Owner.Ordinary && !ordinaryGuarded) return;
            if (DateTime.UtcNow < nextGuardCheck) return;
            nextGuardCheck = DateTime.UtcNow.AddMilliseconds(100);
            if (pathSafe(Core.Me.Location, ordinaryWaypoint)) ReleaseOrdinaryGuard();
            else HoldUnsafeOrdinary();
        }

        internal void CompleteEscape()
        {
            if (IsCurrent && owner == Owner.Escape && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
                !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location)))
                MoveStop();
        }
        internal void Detach()
        {
            ReleaseOrdinaryGuard();
            if (IsCurrent)
            {
                if (owner == Owner.Vorpal) EndVorpal();
                if (owner is Owner.Stage or Owner.Escape) MoveStop();
                Navigator.PlayerMover = inner;
            }
            owner = Owner.None;
            detached = true;
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        SidestepPlugin.Enabled = false;

        if (!Core.Me.InCombat)
            CapabilityManager.Clear();

        // A live input owns scheduling while it is moving; otherwise routine
        // stops can repeatedly interrupt staging or a native escape. Once held,
        // return control so rotation, healing and mitigation remain schedulable.
        return positioningMoving || vorpalTargetSet || vorpalRouteIntent || movementOwner?.HasVorpalMovement == true || movementOwner?.HasNativeEscapePriority == true;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Boss: Zoraal Ja.
        /// </summary>
        public const uint ZoraalJa = 12881;

        /// <summary>
        /// Shadow of Tural
        /// </summary>
        public const uint ShadowofTural = 12886;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.ZoraalJa"/>.
        /// </summary>
        public static readonly Vector3 ZoraalJa = new(100f, 0f, 100f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Zoraal Ja
        /// Vorpal Trail
        /// Swords appear and make a small frontal laser AoE to dodge
        /// </summary>
        public const uint VorpalTrail = 37712;

        /// <summary>
        /// Zoraal Ja
        /// Burst
        /// Little guys explode
        /// </summary>
        public const uint Burst = 37709;

        /// <summary>
        /// Zoraal Ja
        /// Forged Track
        /// This is the spell cast when the swords go after you, let's see if lazer aoe works
        /// </summary>
        public const uint ForgedTrack = 37729;

        /// <summary>
        /// Zoraal Ja
        /// Chasm of Vollok
        /// Avoiding the target here might work
        /// </summary>
        public const uint ChasmofVollok = 37720;

        /// <summary>Short impact helper used to verify the projected preview origin.</summary>
        public const uint ChasmImpact = 37722;

        /// <summary>
        /// Zoraal Ja
        /// Dawn of an Age
        /// Square donut around boss, then transforms the battlefield into a small square
        /// </summary>
        public static readonly HashSet<uint> DawnofanAge = [37716];

        /// <summary>
        /// Zoraal Ja
        /// Actualize
        /// Signals the end of the Dawn of an Age area
        /// </summary>
        public static readonly HashSet<uint> Actualize = [37718];

        /// <summary>
        /// Zoraal Ja
        /// Half Circuit
        /// Donut avoid
        /// </summary>
        public static readonly HashSet<uint> HalfCircuit =
        [
            37739,
            37740,
            37741,
            37742,
            37743,
        ];

        /// <summary>
        /// Zoraal Ja
        /// Half Circuit
        /// Circle
        /// </summary>
        public const uint HalfCircuitCircle = 37743;

        /// <summary>Half Circuit's simultaneously resolving half-room helper.</summary>
        public const uint HalfCircuitRect = 37741;

        /// <summary>Half Circuit's simultaneously resolving ten-to-thirty-yalm ring.</summary>
        public const uint HalfCircuitDonut = 37742;

        /// <summary>
        /// Zoraal Ja
        /// Smiting Circuit
        /// Donut around boss. SideStep picksthis up but it's slightly too big
        /// </summary>
        public const uint SmitingCircuitDonut = 37734;

        /// <summary>
        /// Zoraal Ja
        /// Smiting Circuit
        /// AoE avoid around boss
        /// </summary>
        public const uint SmitingCircuitAvoid = 37735;

        /// <summary>
        /// Zoraal Ja
        /// Half Full
        /// Half line AoE
        /// </summary>
        public const uint HalfFull = 37738;

        /// <summary>
        /// Zoraal Ja
        /// Double-edged Swords
        /// Frontal AoE Cone
        /// </summary>
        public const uint DoubleedgedSwordsFront = 37714;

        public static readonly HashSet<uint> DoubleedgedSwordsFrontHash = [37714];

        /// <summary>
        /// Zoraal Ja
        /// Double-edged Swords
        /// Back AoE Cone
        /// </summary>
        public const uint DoubleedgedSwordsBack = 37713;

        public static readonly HashSet<uint> DoubleedgedSwordsBackHash = [37713];
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Prey. Thermal Charge bomb
        /// </summary>
        public const uint Prey = 1253;
    }
}
