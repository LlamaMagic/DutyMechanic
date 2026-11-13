using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Malikah's Well: cast-authored hazards, ordered nail impacts and stack participation.
/// OrderBot retains routing and the routine retains job actions.
/// </summary>
public class MalikahsWell : AbstractDungeon
{
    // Reflected from the loaded assembly, independently of installed file hashes.
    internal const string Revision = "malikah-candidate13-20261112";
    private const uint ArmadilloBase = 0x2679, TalosBase = 0x267A, StorgeBase = 0x267B,
        MorningstarBase = 0x267E, GeyserBase = 0x1EAAC9;
    private const uint ArmadilloRoom = (uint)SubZoneId.Terminus, TalosRoom = (uint)SubZoneId.MalikahsGift,
        StorgeRoom = (uint)SubZoneId.UnquestionedAcceptance;
    private const uint RotateClockwise = 156, RotateCounterClockwise = 157;
    // Share the existing owner's0.4y stopping radius with Swift's endpoint
    // certificate; arrival and geometry must agree about where input stops.
    private const float PositionArrivalTolerance = .4f;
    private const uint FallingRock = 15594, HeadToss = 15590, RightRound = 15591,
        FlailSmash = 15593, Earthshake = 15929, Wellbore = 15597, GeyserEruption = 15598,
        HighPressure = 15596, SwiftSpill = 15599, Fork = 15602, Wheel = 15605,
        CrystalNail = 15607, NailFork = 15609, NailWheel = 15610, LongFork = 15886, LongWheel = 15887;
    private static readonly Vector3 ArmadilloCenter = new(278, 17, 204), TalosCenter = new(208, -86, 275), StorgeCenter = new(195, -94, -95);
    private static readonly Vector2[] SwiftCurrentCone = Cone(50, 70);
    private static MalikahsWell active;
    private readonly Dictionary<(uint, uint), Hazard> hazards = new();
    private readonly CapabilityManagerHandle positioning = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle hazardCapabilities = CapabilityManager.CreateNewHandle();
    private ChrysalisMovement mover;
    private bool? savedSideStep, boundarySideStep;
    private uint room, stackTarget;
    private AvoidInfo registration;
    private DateTime nextFrame, nextArmadilloFrame, stackUntil, knockbackAt, knockbackUntil;
    private Vector3 knockbackOrigin;
    private bool unsupportedReported, rightRoundOwned;
    private Vector3? destination;
    private bool travelling, leased;
    private string positioningReason;
    private Hazard swiftSequence;
    // Retain the exact admitted RR through its own effect tail. Rock expiry
    // cannot release a partly executed exit; a new cast cannot inherit it.
    private Hazard rightRoundSequence;
    private Vector3[] rightRoundRoute;
    private int rightRoundLeg;
    private Vector3 rightRoundRouteOrigin;
    private float? swiftWaypointAngle;
    private DateTime nextSwiftBlocked;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.MalikahsWell;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        RestoreBoundary();
        if (active != null && !ReferenceEquals(active, this)) active.Reset();
        active = this;
        // First rooms are circular; the square final room must retain its
        // safe diagonal corners. Half-yalm wall insets preserve native paths.
        AvoidanceHelpers.AddAvoidDonut(() => CombatRoom(ArmadilloRoom), () => ArmadilloCenter, 70, 19, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(() => CombatRoom(TalosRoom), () => TalosCenter, 70, 19, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidSquareDonut(() => CombatRoom(StorgeRoom), 28, 28, 140, 140, () => [StorgeCenter], AvoidancePriority.High);
        // Eight-yalm eruption casts and four-yalm persistent water are distinct.
        // Using eight for both permanently seals the knockback lanes.
        AvoidanceManager.AddAvoidObject<EventObject>(canRun: () => CombatRoom(TalosRoom),
            objectSelector: a => a.IsValid && a.IsVisible && a.BaseId == GeyserBase,
            radiusProducer: _ => 4.5f, locationProducer: a => a.Location);
        RegisterHazards();
        Logger.Information($"[Malikah] Loaded {Revision}.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Reset(); RestoreBoundary();
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        Update();
        // A stationary semantic hold must leave healing/rotation schedulable.
        return Task.FromResult(travelling && !AvoidanceManager.IsRunningOutOfAvoid);
    }

    internal static void CheckLifecycle() => active?.Update();
    internal static void ResetActive() => active?.Reset();

    // The base class enables SideStep at both boundaries. Preserve only this
    // encounter's borrowed value, following the existing Dohn/Doma handoff.
    internal void PrepareBoundary(bool leaving)
    {
        if (leaving) ReleaseSideStep();
        boundarySideStep = SidestepPlugin?.Enabled;
    }

    private void RestoreBoundary()
    {
        if (boundarySideStep.HasValue && SidestepPlugin != null) SidestepPlugin.Enabled = boundarySideStep.Value;
        boundarySideStep = null;
    }

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene &&
        WorldManager.ZoneId == (uint)Data.ZoneId.MalikahsWell && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat;
    private static bool CombatRoom(uint subzone) => Live() && WorldManager.SubZoneId == subzone;
    private static uint BossBase(uint subzone) => subzone switch { ArmadilloRoom => ArmadilloBase, TalosRoom => TalosBase, StorgeRoom => StorgeBase, _ => 0 };
    private Vector3 Center => room == ArmadilloRoom ? ArmadilloCenter : room == TalosRoom ? TalosCenter : StorgeCenter;

    private void Update()
    {
        uint currentRoom = WorldManager.SubZoneId;
        if (!Live() || BossBase(currentRoom) == 0) { Reset(); return; }
        if (room != 0 && room != currentRoom) Reset();
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        var boss = actors.FirstOrDefault(a => a.BaseId == BossBase(currentRoom));
        if (boss != null && !boss.IsAlive) { Reset(); return; }
        // A real living boss admits the lease, retained through temporary
        // disappearance. Helpers sharing a name row cannot acquire ownership.
        if (room == 0 && boss?.IsAlive != true) return;
        room = currentRoom;
        if (savedSideStep == null && SidestepPlugin != null)
        {
            savedSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information($"[Malikah] SideStep suspended room={room}.");
        }
        if (mover != null && !mover.IsCurrent)
        {
            // A foreign mover/provider preempted the command. A later supported
            // owner must plan anew, even if the same RR phase remains active.
            ReleasePosition(); mover.Detach(); mover = null;
            rightRoundOwned = false; rightRoundRoute = null; rightRoundLeg = 0;
        }
        if (mover == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            // A denied approach is an intentional hold, not a stuck route.
            // Retire service recovery before it can bypass this input owner.
            mover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: OrdinarySegmentSafe,
                prioritizeRequestedHazardExit: () => rightRoundOwned, fenceNativeNavigationWhileOwned: true,
                retireBlockedOrdinaryNavigation: true);
            Navigator.PlayerMover = mover;
        }
        if (mover?.IsCurrent != true && !unsupportedReported)
        {
            // Do not stack controllers. Native geometry remains registered,
            // but positive positioning is unavailable until the platform returns
            // SlideMover; this state cannot count as mechanic verification.
            Logger.Information($"[Malikah] Unsupported movement platform: {Navigator.PlayerMover?.GetType().FullName}; native avoids retained, positive positioning unavailable.");
            unsupportedReported = true;
        }
        if (mover?.IsCurrent == true) unsupportedReported = false;
        RegisterHazards();
        var now = DateTime.UtcNow;
        foreach (var key in hazards.Where(p => now >= p.Value.Expires).Select(p => p.Key).ToArray()) hazards.Remove(key);
        foreach (var actor in actors.Where(a => a.IsCasting && a.SpellCastInfo != null)) Observe(actor, now);
        foreach (var h in hazards.Values.Where(h => h.Unresolved))
            ResolveRightRound(h, actors.FirstOrDefault(a => a.ObjectId == h.Actor), now);
        // The first Wellbore is announced while Talos is still returning to
        // center. Candidate5 escaped15.65y from the early origin and held,
        // yet that point is only13.45y from Talos's recurring arena center.
        // Follow this self-centered caster during its cast, then freeze the
        // last valid origin through the existing effect tail. No deadline reset.
        foreach (var h in hazards.Values.Where(h => h.Action == Wellbore && !h.CasterOriginLocked))
        {
            var source = actors.FirstOrDefault(a => a.ObjectId == h.Actor);
            if (source == null) continue;
            if (source.IsCasting && source.CastingSpellId == Wellbore)
            {
                var p = source.Location;
                if (float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)) h.Origin = p;
            }
            else
            {
                h.CasterOriginLocked = true;
                Logger.Information($"[Malikah] Wellbore effect origin locked={h.Origin} until={h.Expires:O}.");
            }
        }
        // Candidate2's Fork and candidate3's inward Earthshake both admitted
        // native escape while an ordinary cast held input for over a second.
        // Interrupt only during a real native escape with this encounter's
        // active geometry; routine actions remain available during safe holds.
        if (AvoidanceManager.IsRunningOutOfAvoid && Core.Me.IsCasting && ActiveHazards().Any())
            ActionManager.StopCasting();
        if (boss?.IsCasting == true && boss.CastingSpellId == HighPressure && now >= knockbackUntil)
        {
            knockbackOrigin = boss.Location;
            // November12 use/snapshot evidence followed the bar by~1.14s;
            // 1.7s retains another~0.56s of snapshot/sampling clearance.
            // Confirming tells never restart the deadline.
            // After the expected snapshot, hold the actual current position
            // rather than requesting a return from the knockback landing.
            knockbackAt = now + boss.SpellCastInfo.RemainingCastTime;
            knockbackUntil = knockbackAt.AddSeconds(1.7);
        }
        // Job gap closers bypass ordinary waypoint admission. Give pending
        // hazards their own handle so releasing a stack cannot reopen that path.
        if (hazards.Count > 0)
            CapabilityManager.Update(hazardCapabilities, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Malikah pending hazards");
        else
            CapabilityManager.Clear(hazardCapabilities, CapabilityFlags.GapCloser, "Malikah hazards clear");
        // Swift's authored rear sequence reconciles every observed heading.
        // Fixed helper/ground attacks retain their discovered origins.
        foreach (var h in hazards.Values.Where(h => h.Action == SwiftSpill))
        {
            var source = actors.FirstOrDefault(a => a.ObjectId == h.Actor);
            if (source == null) continue;
            float delta = (float)Math.Atan2(Math.Sin(source.Heading - h.LastHeading), Math.Cos(source.Heading - h.LastHeading));
            // Admit smooth bot-pulse edges of ~0.86-23 degrees: reject small
            // floating-point jitter and large facing snaps. These provisional
            // bounds require capture confirmation in both rotation directions.
            if (now >= h.Impact && Math.Abs(delta) > .015f && Math.Abs(delta) < .4f && h.Direction != Math.Sign(delta))
            {
                h.Direction = Math.Sign(delta);
                swiftWaypointAngle = null;
                Logger.Information($"[Malikah] SWIFT rotation={h.Direction} reconciled from heading change; waypoint invalidated.");
            }
            h.LastHeading = source.Heading;
            h.Heading = source.Heading;
            if (h.Origin.Distance2D(source.Location) > .5f)
            {
                h.Origin = source.Location;
                swiftWaypointAngle = null;
                Logger.Information("[Malikah] SWIFT origin changed; waypoint invalidated.");
            }
        }
        var currentSwift = hazards.Values.FirstOrDefault(h => h.Action == SwiftSpill);
        if (!ReferenceEquals(swiftSequence, currentSwift))
        {
            swiftSequence = currentSwift;
            swiftWaypointAngle = null;
        }
        var currentRound = hazards.Values.FirstOrDefault(h => h.Action == RightRound && !h.Unresolved);
        if (rightRoundSequence != null && !ReferenceEquals(rightRoundSequence, currentRound))
        {
            rightRoundSequence = null;
            rightRoundRoute = null;
            rightRoundLeg = 0;
        }
        if (mover?.IsCurrent == true)
        {
            rightRoundOwned = false;
            if (stackTarget != 0 && now < stackUntil) PositionStack(actors);
            else if (now < knockbackUntil)
            {
                if (now < knockbackAt) PositionKnockback(knockbackOrigin);
                else SetPosition(Core.Me.Location, "High Pressure effect hold");
            }
            else if (swiftSequence != null) PositionSwift(swiftSequence);
            else if (room == ArmadilloRoom && currentRound != null &&
                (hazards.Values.Any(h => h.Action == FallingRock) || ReferenceEquals(currentRound, rightRoundSequence)))
            {
                if (!ReferenceEquals(currentRound, rightRoundSequence))
                {
                    rightRoundSequence = currentRound;
                    rightRoundRoute = null;
                    rightRoundLeg = 0;
                }
                PositionRightRound(currentRound, now);
            }
            else { stackTarget = 0; ReleasePosition(); }
            // Preemption releases this command. Retain only the phase identity;
            // reacquisition must plan from the actual position with fresh input.
            if (!rightRoundOwned) { rightRoundRoute = null; rightRoundLeg = 0; }
            mover.Update();
        }
        if (LoggingHelpers.MechanicDiagnosticsEnabled && room == ArmadilloRoom && now >= nextArmadilloFrame)
        {
            // Battle text omits the helper action on some environmental hits.
            // Pair its timing with actual native registration and containment;
            // a discovered hazard alone does not prove RB received that shape.
            nextArmadilloFrame = now.AddMilliseconds(250);
            var native = AvoidanceManager.Avoids.Where(a => ReferenceEquals(a.AvoidInfo, registration)).ToArray();
            Logger.Information($"[Malikah] ARMADILLO_GEOMETRY player={Core.Me.Location} escape={AvoidanceManager.IsRunningOutOfAvoid} " +
                string.Join(";", ActiveHazards().Select(h => $"action={h.Action} actor={h.Actor:X} origin={h.Origin} impact={h.Impact:O} expires={h.Expires:O} " +
                    $"native={native.Count(a => ReferenceEquals(a.Object, h) && a.IsValid && !a.ShouldIgnore)} contains={native.Any(a => ReferenceEquals(a.Object, h) && !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location))}")));
        }
        if (LoggingHelpers.MechanicDiagnosticsEnabled && now >= nextFrame)
        {
            nextFrame = now.AddMilliseconds(500);
            Logger.Information($"[Malikah] FRAME room={room} player={Core.Me.Location} moving={MovementManager.IsMoving} escape={AvoidanceManager.IsRunningOutOfAvoid} forced={MovementManager.GenerallyOutOfControl} destination={destination} owner={positioningReason} bossPosition={boss?.Location} bossHeading={boss?.Heading} " +
                string.Join(";", Party(actors).Select(a => $"party={a.ObjectId:X}:{a.Location}")));
        }
    }

    private void RegisterHazards()
    {
        if (registration != null && AvoidanceManager.AvoidInfos.Contains(registration)) return;
        registration = AvoidanceManager.AddAvoidPolygon<Hazard>(condition: Live, leashPointProducer: () => Center, leashRadius: 60,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: h => h.Points, locationProducer: h => h.Origin,
            collectionProducer: () => ActiveHazards().ToArray(), objectValidator: _ => true,
            ignoreIfBlocking: false, priority: AvoidancePriority.High);
    }

    private IEnumerable<Hazard> ActiveHazards()
    {
        var pending = hazards.Values.Where(h => DateTime.UtcNow < h.Expires).ToArray();
        // Long bars announce later impacts. Publishing all donuts together
        // would intersect disjoint safe circles and make native escape impossible.
        var first = pending.Where(h => h.Ordered).OrderBy(h => h.Impact).FirstOrDefault();
        // Head Toss impacts precede the rock waves it creates. Candidate1
        // recorded a 1.2-1.92s animation/effect delay after the cast bar;
        // newly announced three-second rocks must not eject the stack early.
        return pending.Where(h => h.Action != SwiftSpill && (!h.Ordered || ReferenceEquals(h, first)) &&
            !(h.Action == FallingRock && stackTarget != 0 && DateTime.UtcNow < stackUntil && h.Impact > stackUntil));
    }

    private void Observe(BattleCharacter actor, DateTime now)
    {
        uint action = actor.CastingSpellId;
        var key = (actor.ObjectId, action);
        if (hazards.ContainsKey(key)) return;
        var finish = now + actor.SpellCastInfo.RemainingCastTime;
        if (action == HeadToss)
        {
            if (stackTarget == 0 || now >= stackUntil)
            {
                stackTarget = actor.SpellCastInfo.TargetId;
                // November 12 RDM capture: attributed hits followed the bar
                // by 1.2-1.92s. Retain participants for 2.2s, including 0.28s
                // beyond the latest observed delay, before Right Round egress.
                stackUntil = finish.AddSeconds(2.2);
                Logger.Information($"[Malikah] Head Toss target={stackTarget:X} self={stackTarget == Core.Me.ObjectId} until={stackUntil:O}.");
            }
            return;
        }
        Vector2[] points;
        Vector3 origin = actor.Location;
        bool ordered = false;
        // Cast completion and server damage need not coincide. Half a second
        // is the initial conservative allowance, to calibrate from live effects.
        double tail = .5;
        switch (action)
        {
            case FallingRock:
                // Five captured environmental hits aligned with this wave at
                // 0.84-0.89s after its bar, beyond the old half-second lifetime.
                // Retain the four-yalm rock plus body margin through1.3s so
                // overlapping escapes cannot treat the resolving rock as clear.
                points = Circle(4.5f); origin = actor.SpellCastInfo.CastLocation; tail = 1.3; break;
            case RightRound:
                // The boss first closes to the ball's combined combat reach,
                // then the hidden damage helper resolves at that position.
                // November13 capture: a5.38y approach moved the origin from
                //282.002,193.255 to280.075,198.275, hitting a player who had
                //cleared the old snapshot. A second close target required no
                //approach; a third moved~0.43y. Snapshot the predicted endpoint,
                //not the ball center or the pre-approach boss position.
                // Nine-yalm damage plus one yalm origin uncertainty and
                // half-yalm body clearance retain the conservative10.5y avoid.
                points = Circle(10.5f);
                // Candidate1's 21:58:42.84 hit was 1.72s after visual completion;
                // retain two seconds, including 0.28s sampling/effect clearance.
                tail = 2;
                break;
            case FlailSmash:
                // Four November12 outcomes:18.75y took4505 versus~10.5y
                // taking~10000. Stage near the wall (not a binary damage
                // radius), leaving a1.5y band inside the ordinary19y boundary.
                // Latest HP loss followed the bar by2.05s;2.3s preserves~0.25s
                // beyond that before admitting the following inward donut.
                points = Circle(17.5f); origin = actor.SpellCastInfo.CastLocation;
                ordered = true; tail = 2.3; break;
            // Candidate3's vulnerability snapshot followed the predicted bar
            // by0.60s (battle damage by0.80s). Keep the inward safe region for
            //1.3s so ordinary travel cannot resume during that effect tail.
            case Earthshake: points = Donut(40, 9.5f); ordered = true; tail = 1.3; break;
            // Candidate2 escaped, then routine travel re-entered during the
            // old0.5s tail and gained vulnerability. The use follows the bar
            // by~0.92s;1.5s keeps the waypoint guard through impact and margin.
            case Wellbore: points = Circle(15.5f); tail = 1.5; break;
            case GeyserEruption: points = Circle(8.5f); break;
            // November12 pulses were 1.08-1.30s apart and the final hit was
            // ~6.8s after cast completion. Retain 7.5s to cover that observed
            // animation tail with ~0.7s margin; live direction enables forecast.
            case SwiftSpill: points = SwiftCurrentCone; tail = 7.5; break;
            case CrystalNail: points = Circle(5.5f); break;
            // November12 nail effects followed predicted bar ends by1.15-1.33s.
            // Retain each stage1.5s, leaving ~0.17s beyond the latest observed
            // effect instead of releasing its safe region during that animation.
            case Fork: case NailFork: case LongFork: points = Cross(5.5f, 60); ordered = true; tail = 1.5; break;
            case Wheel: case NailWheel: case LongWheel: points = Donut(60, 4.5f); ordered = true; tail = 1.5; break;
            default: return;
        }
        if (origin == Vector3.Zero || !float.IsFinite(origin.X) || !float.IsFinite(origin.Z)) return;
        var hazard = new Hazard { Actor = actor.ObjectId, Action = action, Origin = origin, Heading = actor.Heading, LastHeading = actor.Heading,
            Points = points, Impact = finish, Expires = finish.AddSeconds(tail), Ordered = ordered };
        if (action == RightRound)
        {
            // Keep the fixed deadline even if the target arrives late. An
            // unresolved cast-start avoid is provisional, not a safe forecast;
            // suppress ordinary approach and retry through the effect window.
            hazard.Target = actor.SpellCastInfo.TargetId;
            hazard.Unresolved = true;
            ResolveRightRound(hazard, actor, now);
        }
        if (action == SwiftSpill && actor.VfxContainer.IsValid)
        {
            var icon = actor.VfxContainer.LockOns.FirstOrDefault(v => v != null && v.IsValid && (v.Id == RotateClockwise || v.Id == RotateCounterClockwise));
            // The direction icon is an earlier independent tell when RB exposes
            // it. Smooth heading edges remain the verified fallback signal.
            if (icon != null) hazard.Direction = icon.Id == RotateClockwise ? -1 : 1;
            Logger.Information($"[Malikah] SWIFT icon={icon?.Id} rotation={hazard.Direction}.");
        }
        hazards.Add(key, hazard);
        // Keep raw discovery and resolved geometry distinct for the predicted
        // Right Round endpoint and the moving Wellbore origin.
        Logger.Information($"[Malikah] HAZARD action={action} actor={actor.ObjectId:X} base={actor.BaseId:X} origin={hazard.Origin} castOrigin={origin} unresolved={hazard.Unresolved} heading={actor.Heading:F3} impact={finish:O} expires={hazard.Expires:O} ordered={ordered}.");
    }

    private static BattleCharacter[] Party(BattleCharacter[] actors) => actors.Append(Core.Me)
        .Where(a => a.IsValid && a.IsAlive && (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
            PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();

    private void PositionStack(BattleCharacter[] actors)
    {
        var party = Party(actors);
        var carrier = party.FirstOrDefault(a => a.ObjectId == stackTarget);
        if (carrier == null) { ReleasePosition(); return; }
        bool self = carrier.ObjectId == Core.Me.ObjectId;
        var others = party.Where(a => a.ObjectId != Core.Me.ObjectId).Select(a => a.Location).ToArray();
        // Six-yalm stack: target five-yalm coverage, leaving arrival clearance.
        int Coverage(Vector3 p) => self ? others.Count(a => a.Distance2D(p) <= 5) : p.Distance2D(carrier.Location) <= 5 ? 1 : 0;
        var candidates = new List<Vector3> { Core.Me.Location, carrier.Location };
        candidates.AddRange(others);
        if (others.Length > 0) candidates.Add(new Vector3(others.Average(p => p.X), Core.Me.Y, others.Average(p => p.Z)));
        var best = candidates.Where(p => Coverage(p) > 0 && PointSafe(p) && SegmentSafe(p)).OrderByDescending(Coverage)
            .ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        if (destination.HasValue && Coverage(destination.Value) >= (best.HasValue ? Coverage(best.Value) : 1) && PointSafe(destination.Value) && SegmentSafe(destination.Value)) best = destination;
        SetPosition(best, "Head Toss stack");
    }

    private void PositionKnockback(Vector3 origin)
    {
        var puddles = GameObjectManager.GameObjects.Where(a => a.IsValid && a.IsVisible && a.BaseId == GeyserBase).Select(a => a.Location).ToArray();
        // Wall-stopped twenty-yalm knockback: validate the whole radial lane,
        // including its actual endpoint, separately from the artificial 19y
        // ordinary-travel inset. The physical 19.55y radius is reference geometry
        // pending local calibration, not permission to omit the final corridor.
        bool LandingSafe(Vector3 p)
        {
            var landing = KnockbackLanding(origin, p);
            if (!landing.HasValue) return false;
            var end = landing.Value;
            int steps = Math.Max(1, (int)Math.Ceiling(p.Distance2D(end) / .25f));
            for (int i = 0; i <= steps; ++i)
            {
                float t = i / (float)steps;
                var q = new Vector3(p.X + (end.X - p.X) * t, p.Y, p.Z + (end.Z - p.Z) * t);
                if (puddles.Any(w => w.Distance2D(q) < 5) || !KnockbackDamageSafe(q)) return false;
            }
            return true;
        }
        var candidates = Enumerable.Range(0, 32).Select(i => new Vector3(origin.X + 3 * (float)Math.Sin(i * Math.Tau / 32), Core.Me.Y,
            origin.Z + 3 * (float)Math.Cos(i * Math.Tau / 32))).Prepend(Core.Me.Location);
        var best = candidates.Where(p => PointSafe(p) && SegmentSafe(p) && LandingSafe(p)).OrderBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (destination.HasValue && PointSafe(destination.Value) && SegmentSafe(destination.Value) && LandingSafe(destination.Value)) best = destination;
        SetPosition(best, "High Pressure lane");
    }

    private void PositionRightRound(Hazard round, DateTime now)
    {
        // The retained RR owns its own lifetime, not the shorter rock waves.
        // Other active stages and unsupported starting floor retain preemption.
        if (Core.Me.Location.Distance2D(ArmadilloCenter) > 19 ||
            ActiveHazards().Any(h => h.Action != RightRound && h.Action != FallingRock))
        {
            ReleasePosition();
            return;
        }
        var start = Core.Me.Location;
        var rocks = hazards.Values.Where(h => h.Action == FallingRock).Select(h => h.Origin).ToArray();
        bool valid = rightRoundRoute != null && rightRoundRouteOrigin.Distance2D(round.Origin) <= .01f;
        if (valid)
        {
            while (rightRoundLeg + 1 < rightRoundRoute.Length && start.Distance2D(rightRoundRoute[rightRoundLeg]) <= PositionArrivalTolerance)
                rightRoundLeg++;
            var from = start;
            for (int i = rightRoundLeg; i < rightRoundRoute.Length && valid; ++i)
            {
                // The next leg is certified for every point in the turn's
                // arrival disk. At the turn, recheck from the actual position.
                valid = RightRoundLegSafe(from, rightRoundRoute[i], round.Origin, rocks,
                    requireExit: i == rightRoundRoute.Length - 1, startUncertainty: i == rightRoundLeg ? 0 : PositionArrivalTolerance,
                    out _, out _);
                from = rightRoundRoute[i];
            }
        }
        string rejection = null;
        if (!valid)
        {
            rightRoundRoute = RightRoundRoute(start, round.Origin, rocks, (round.Impact - now).TotalSeconds,
                detail => rejection = detail);
            rightRoundLeg = 0;
            rightRoundRouteOrigin = round.Origin;
            if (rightRoundRoute != null)
                Logger.Information($"[Malikah] RR_ROUTE actor={round.Actor:X} legs={rightRoundRoute.Length} player={start} goals={string.Join(";", rightRoundRoute.Select(p => p.ToString()))} impact={round.Impact:O} expires={round.Expires:O}.");
        }
        rightRoundOwned = true;
        if (rightRoundRoute == null && now >= round.NextWarning)
        {
            round.NextWarning = now.AddSeconds(1);
            Logger.Information($"[Malikah] RR_EXIT blocked player={start} origin={round.Origin} rocks={rocks.Length} impact={round.Impact:O} {rejection}.");
        }
        SetPosition(rightRoundRoute == null ? start : rightRoundRoute[rightRoundLeg],
            rightRoundRoute == null ? "Right Round route pending" : "Right Round rock-safe exit", hazardExit: true);
        if (!leased) { rightRoundOwned = false; rightRoundRoute = null; rightRoundLeg = 0; }
    }

    private static Vector3[] RightRoundRoute(Vector3 start, Vector3 origin, Vector3[] rocks, double remaining, Action<string> blocked = null)
    {
        string directRejection = null;
        var direct = RightRoundExit(start, origin, rocks, remaining, null, detail => directRejection = detail);
        if (direct.HasValue) return [direct.Value];
        if (rocks == null || rocks.Any(r => !float.IsFinite(r.X) || !float.IsFinite(r.Y) || !float.IsFinite(r.Z)) || !double.IsFinite(remaining) ||
            !float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(start.Z) ||
            !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
        {
            blocked?.Invoke(directRejection ?? "invalid geometry/deadline");
            return null;
        }
        // Candidate11b proves the straight family is insufficient: one bend
        // around the containing rock clears RR in time while preserving all
        // eight circles. Rock-centered5y turns reserve0.4y arrival plus0.1y.
        // This is a bounded two-leg search on the observed flat arena.
        Vector3[] best = null;
        double bestSeconds = double.MaxValue;
        int candidates = 0, firstRejected = 0, secondRejected = 0;
        foreach (var rock in rocks)
        {
            for (int i = 0; i < 24; ++i)
            {
                candidates++;
                double angle = i * Math.Tau / 24;
                var turn = new Vector3(rock.X + 5 * (float)Math.Sin(angle), start.Y, rock.Z + 5 * (float)Math.Cos(angle));
                if (!RightRoundLegSafe(start, turn, origin, rocks, requireExit: false, startUncertainty: 0, out _, out _))
                {
                    firstRejected++;
                    continue;
                }
                // Inflate the entire second chord for the turn's arrival disk,
                // during candidate selection, not after choosing a nominal goal.
                var end = RightRoundExit(turn, origin, rocks, remaining, null, startUncertainty: PositionArrivalTolerance);
                if (!end.HasValue) { secondRejected++; continue; }
                double distance = start.Distance2D(turn) + turn.Distance2D(end.Value);
                // Two dispatch reserves and two arrival-position error bounds.
                // The failed11b layout's independently checked full-route bound
                // is~1.94s versus2.20s available; no later deadline is needed.
                double seconds = (distance + 2 * PositionArrivalTolerance) / 5.5 + .5;
                if (seconds >= bestSeconds) continue;
                best = [turn, end.Value];
                bestSeconds = seconds;
            }
        }
        // An already-late attachment still takes the fastest certified route;
        // waiting in RR cannot improve it. Cache a valid admitted route rather
        // than charging dispatch again and retargeting it on every pulse.
        if (best == null)
            blocked?.Invoke($"{directRejection}; two-leg candidates={candidates} first={firstRejected} second={secondRejected}");
        return best;
    }

    private static bool RightRoundLegSafe(Vector3 start, Vector3 end, Vector3 origin, Vector3[] rocks,
        bool requireExit, float startUncertainty, out double exitDistance, out string rejection)
    {
        exitDistance = 0;
        rejection = null;
        static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
        if (!Finite(start) || !Finite(end) || !Finite(origin) || rocks == null || rocks.Any(r => !Finite(r)) ||
            !float.IsFinite(startUncertainty) || startUncertainty < 0 || startUncertainty > PositionArrivalTolerance ||
            start.Distance2D(ArmadilloCenter) > 19 - startUncertainty || end.Distance2D(ArmadilloCenter) > 19 - startUncertainty)
        { rejection = "floor"; return false; }

        double dx = end.X - start.X, dz = end.Z - start.Z, lengthSquared = dx * dx + dz * dz;
        double rockRadius = 4.5 + startUncertainty;
        foreach (var rock in rocks)
        {
            // A whole turn disk needs4.9y clearance along its second chord.
            // Start/end uncertainty are alternatives, not additive margins.
            if (lengthSquared > .0001 && end.Distance2D(rock) < 4.5f + Math.Max(PositionArrivalTolerance, startUncertainty))
            { rejection = "arrival"; return false; }
            double rx = start.X - rock.X, rz = start.Z - rock.Z;
            double dot = rx * dx + rz * dz, startSquared = rx * rx + rz * rz;
            double t = lengthSquared == 0 ? 0 : Math.Clamp(-dot / lengthSquared, 0, 1);
            double x = rx + dx * t, z = rz + dz * t;
            if (startSquared < rockRadius * rockRadius)
            {
                // Only an exact observed start may leave a containing warning.
                // A planned turn disk must already be wholly clear. Rounded
                // tangents retain the tested one-millionth-yalm depth bound.
                if (startUncertainty > 0 || Math.Sqrt(startSquared) - Math.Sqrt(x * x + z * z) > .000001 ||
                    end.Distance2D(rock) < rockRadius) { rejection = "rock"; return false; }
            }
            else if (x * x + z * z < rockRadius * rockRadius) { rejection = "rock"; return false; }
        }
        // A future turn disk must lie wholly inside or wholly outside RR.
        // Otherwise a nominally inside certificate could authorize re-entry
        // for an already-clear actual arrival, which the live leg would reject.
        if (startUncertainty > 0 && Math.Abs(start.Distance2D(origin) - 10.5) <= startUncertainty + .0001)
        { rejection = "round"; return false; }
        double roundRadius = 10.5 + startUncertainty;
        double ox = start.X - origin.X, oz = start.Z - origin.Z;
        double depth = ox * ox + oz * oz - roundRadius * roundRadius;
        if (depth >= 0)
        {
            double t = lengthSquared == 0 ? 0 : Math.Clamp(-(ox * dx + oz * dz) / lengthSquared, 0, 1);
            double x = ox + dx * t, z = oz + dz * t;
            if (x * x + z * z < roundRadius * roundRadius) { rejection = "round"; return false; }
            return true;
        }
        if (!requireExit) { exitDistance = Math.Sqrt(lengthSquared); return true; }
        if (lengthSquared == 0 || end.Distance2D(origin) < 10.5f + PositionArrivalTolerance)
        { rejection = "arrival"; return false; }
        double projection = ox * dx + oz * dz;
        double tExit = (-projection + Math.Sqrt(projection * projection - lengthSquared * depth)) / lengthSquared;
        exitDistance = tExit * Math.Sqrt(lengthSquared);
        return true;
    }

    private static Vector3? RightRoundExit(Vector3 start, Vector3 origin, Vector3[] rocks, double remaining, Vector3? previous,
        Action<string> blocked = null, float startUncertainty = 0)
    {
        // Native7b/10 crossed live rocks. The direct route remains the first
        // choice, with RR11 reserving0.4y arrival beyond its10.5y unsafe region.
        static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
        if (!Finite(start) || !Finite(origin) || rocks == null || rocks.Any(r => !Finite(r)) || !double.IsFinite(remaining))
        { blocked?.Invoke("invalid geometry/deadline"); return null; }
        int floorRejected = 0, rockRejected = 0, arrivalRejected = 0, roundRejected = 0, candidates = 0;
        Vector3? lastGoal = null;
        bool Certificate(Vector3 end, out double exitDistance)
        {
            lastGoal = end;
            bool safe = RightRoundLegSafe(start, end, origin, rocks, requireExit: true, startUncertainty,
                out exitDistance, out string reason);
            if (!safe)
            {
                switch (reason)
                {
                    case "floor": floorRejected++; break;
                    case "rock": rockRejected++; break;
                    case "arrival": arrivalRejected++; break;
                    case "round": roundRejected++; break;
                }
            }
            return safe;
        }
        // Dispatch is an admission cost, not a fresh penalty every pulse.
        // Revalidate geometry from the actual position and retain a valid goal.
        if (previous.HasValue && Certificate(previous.Value, out _)) return previous;
        if (Certificate(start, out _)) return start;
        Vector3? best = null;
        double bestExit = double.MaxValue, bestTravel = double.MaxValue;
        void Consider(Vector3 point)
        {
            candidates++;
            if (!Certificate(point, out double exit)) return;
            double travel = start.Distance2D(point);
            if (exit > bestExit || exit == bestExit && travel >= bestTravel) return;
            best = point; bestExit = exit; bestTravel = travel;
        }
        for (int i = 0; i < 120; ++i)
        {
            double angle = i * Math.Tau / 120;
            Consider(new Vector3(origin.X + 11 * (float)Math.Sin(angle), start.Y, origin.Z + 11 * (float)Math.Cos(angle)));
        }
        if (start.Distance2D(origin) >= 10.5f + startUncertainty)
        {
            // Already-RR-clear rock departures must not be pulled inward merely
            // to reach the11y ring. Preserve outward and paired-rock tangents.
            foreach (var rock in rocks.Where(r => start.Distance2D(r) < 4.5f + startUncertainty))
            {
                double outward = Math.Atan2(start.X - rock.X, start.Z - rock.Z);
                foreach (double angle in new[] { outward - Math.PI / 2, outward + Math.PI / 2 })
                    Consider(new Vector3(start.X + 5 * (float)Math.Sin(angle), start.Y, start.Z + 5 * (float)Math.Cos(angle)));
                for (int i = 0; i < 24; ++i)
                {
                    double angle = outward + i * Math.Tau / 24;
                    Consider(new Vector3(rock.X + 5 * (float)Math.Sin(angle), start.Y, rock.Z + 5 * (float)Math.Cos(angle)));
                }
            }
        }
        if (!best.HasValue)
            blocked?.Invoke($"no sampled straight exit; candidates={candidates} floor={floorRejected} rock={rockRejected} arrival={arrivalRejected} round={roundRejected} lastGoal={lastGoal}");
        return best;
    }

    private void PositionSwift(Hazard sequence)
    {
        // Native attempts failed at22:08:10 (reactive cone) and22:48:31
        // (correct next-cone forecast driving along the outer wall). Use one
        // checked inner rear route through the existing input owner instead.
        // Radius2.5 leaves water outside the route; quarter chords are~3.54y,
        // comfortably below ordinary movement per~1.2s pulse without Sprint.
        const float radius = 2.5f;
        swiftWaypointAngle ??= sequence.Heading + (float)Math.PI;
        for (int step = 0; step < 4 && Math.Abs(AngleDifference(swiftWaypointAngle.Value, sequence.Heading)) < 135 * Math.PI / 180; ++step)
        {
            // Advance early enough to preserve the current and next60-degree
            // cones, including arrival tolerance. Stable quarter-turn goals
            // avoid issuing a new destination for every animated facing sample.
            swiftWaypointAngle = sequence.Direction == 0 ? sequence.Heading + (float)Math.PI :
                swiftWaypointAngle.Value + sequence.Direction * (float)(Math.PI / 2);
        }
        Vector3 At(float angle) => new(sequence.Origin.X + radius * (float)Math.Sin(angle), Core.Me.Y,
            sequence.Origin.Z + radius * (float)Math.Cos(angle));
        var goal = At(swiftWaypointAngle.Value);
        var player = Core.Me.Location;
        // Crossing the marked frontal region is permitted only when the whole
        // approach finishes before the conservative cast deadline.5.5y/s is
        // below observed~6y/s;0.25s reserves dispatch/arrival latency.
        bool EarlyEnough(float distance) => DateTime.UtcNow.AddSeconds(distance / 5.5 + .25) < sequence.Impact;
        Vector3? selected = SwiftSegmentSafe(sequence, goal, EarlyEnough(player.Distance2D(goal))) ? goal : null;
        string reason = "Swift Spill inner rear";
        if (!selected.HasValue && player.Distance2D(sequence.Origin) > radius + 1)
        {
            // A direct rear chord can intersect an outer pool. First enter
            // radially through a clear lane, then complete the inner approach.
            float angle = (float)Math.Atan2(player.X - sequence.Origin.X, player.Z - sequence.Origin.Z);
            var entry = At(angle);
            float fullDistance = player.Distance2D(entry) + entry.Distance2D(goal);
            if (EarlyEnough(fullDistance) && SwiftSegmentSafe(sequence, entry, true)) selected = entry;
        }
        if (!selected.HasValue && (InSwiftCone(player, sequence.Origin, sequence.Heading) || sequence.Direction != 0 &&
            InSwiftCone(player, sequence.Origin, sequence.Heading + sequence.Direction * (float)(Math.PI / 3))))
        {
            // A cast cancellation can leave the start inside the forecast
            // after this rear leg was selected. Preserve the purposeful goal
            // when its checked decreasing-depth departure remains valid.
            // Nearest-boundary fallback churn caused the November13 death.
            if (SwiftSegmentSafe(sequence, goal, false, allowDeparture: true))
            {
                selected = goal;
                reason = "Swift Spill resume rear";
            }
            // Interrupted/late execution can begin inside the cone. A safe
            // endpoint alone is insufficient, but rejecting that starting
            // point would prohibit leaving it. Inspect a small fixed set of
            // inner recovery goals and admit only a bounded decreasing-depth
            // unsafe prefix followed by an entirely clear path. A late entry
            // may still take a hit; it must not idle in danger until expiry.
            if (!selected.HasValue)
            {
                reason = "Swift Spill emergency departure";
                selected = (from recoveryRadius in new[] { 2.5f, 6f, 12f }
                            from i in Enumerable.Range(0, 24)
                            let angle = (float)(i * Math.Tau / 24)
                            let point = new Vector3(sequence.Origin.X + recoveryRadius * (float)Math.Sin(angle), player.Y,
                                sequence.Origin.Z + recoveryRadius * (float)Math.Cos(angle))
                            orderby point.Distance2D(player)
                            where PointSafe(point) && SwiftSegmentSafe(sequence, point, false, allowDeparture: true)
                            select (Vector3?)point).FirstOrDefault();
            }
        }
        if (!selected.HasValue)
        {
            // Reconsider from the next actual position/tell; do not keep an
            // unsafe old request or silently retry the failed native cone.
            ReleasePosition();
            if (DateTime.UtcNow >= nextSwiftBlocked)
            {
                nextSwiftBlocked = DateTime.UtcNow.AddSeconds(1);
                Logger.Information($"[Malikah] SWIFT approach blocked player={player} goal={goal} heading={sequence.Heading:F3} direction={sequence.Direction} until={sequence.Expires:O}.");
            }
            return;
        }
        SetPosition(selected, reason);
    }

    private bool SwiftSegmentSafe(Hazard sequence, Vector3 end, bool beforeFirstImpact, bool allowDeparture = false)
    {
        if (!SegmentSafe(end)) return false;
        if (beforeFirstImpact) return true;
        return SwiftConePathSafe(Core.Me.Location, end, sequence.Origin, sequence.Heading, sequence.Direction, allowDeparture);
    }

    // Pure geometry seam used by both the real route and interrupted-departure
    // regression checks. Other hazards and floor are checked by SegmentSafe.
    private static bool SwiftConePathSafe(Vector3 start, Vector3 end, Vector3 origin, float heading, int direction, bool allowDeparture)
    {
        bool Danger(Vector3 p) => InSwiftCone(p, origin, heading) || direction != 0 && InSwiftCone(p, origin, heading + direction * (float)(Math.PI / 3));
        float Depth(Vector3 p)
        {
            float r = p.Distance2D(origin);
            float margin = Math.Max((float)(5 * Math.PI / 180), (float)Math.Asin(Math.Min(1, .5f / Math.Max(.001f, r))));
            float half = (float)((direction == 0 ? 30 : 60) * Math.PI / 180) + margin;
            float center = heading + direction * (float)(Math.PI / 6);
            float angle = (float)Math.Atan2(p.X - origin.X, p.Z - origin.Z);
            float penetration = r * (float)Math.Sin(Math.Clamp(half - Math.Abs(AngleDifference(angle, center)), 0, Math.PI / 2));
            return Math.Max(.5f - r, penetration);
        }
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(end) / .25f));
        bool departed = !Danger(start);
        float previousDepth = Depth(start);
        // One radial distance plus half-yalm clearance bounds an emergency
        // prefix toward the inner safe region. This is not a safe hold or a
        // guarantee that a late arrival beats the next pulse; revalidate every
        // bot pulse and never cross another native hazard to perform it.
        float prefixBound = start.Distance2D(origin) + .5f;
        for (int i = 0; i <= steps; ++i)
        {
            float t = i / (float)steps;
            var point = new Vector3(start.X + (end.X - start.X) * t, start.Y, start.Z + (end.Z - start.Z) * t);
            if (!Danger(point)) { departed = true; continue; }
            if (!allowDeparture || departed || start.Distance2D(point) > prefixBound) return false;
            float depth = Depth(point);
            // Five centimeters only tolerates floating-point/sample variation;
            // it does not permit a route that dives deeper into the cone.
            if (depth > previousDepth + .05f) return false;
            // A running minimum prevents many individually small increases
            // from ratcheting the tolerated penetration deeper along the path.
            previousDepth = Math.Min(previousDepth, depth);
        }
        // The controller stops anywhere within its arrival tolerance. A point
        // just beyond the forecast edge is not a safe hold if that arrival
        // region still intersects it: candidate6b selected a0.33y move, called
        // itself arrived at0.4y and restarted Jolt without clearing danger.
        float endpointClearance = .5f + PositionArrivalTolerance;
        return departed && !InSwiftCone(end, origin, heading, endpointClearance) &&
            (direction == 0 || !InSwiftCone(end, origin, heading + direction * (float)(Math.PI / 3), endpointClearance));
    }

    private static bool InSwiftCone(Vector3 point, Vector3 origin, float heading, float clearance = .5f)
    {
        float distance = point.Distance2D(origin);
        if (distance < clearance) return true;
        float angle = (float)Math.Atan2(point.X - origin.X, point.Z - origin.Z);
        // Path checks preserve0.5y; endpoint checks also include the owner's
        // arrival radius. Retain the five-degree angular minimum at long range;
        // fixed35degrees alone loses physical clearance near the boss.
        double margin = Math.Max(5 * Math.PI / 180, Math.Asin(Math.Min(1, clearance / distance)));
        return distance < 50 && Math.Abs(AngleDifference(angle, heading)) <= 30 * Math.PI / 180 + margin;
    }

    private static float AngleDifference(float left, float right) => (float)Math.Atan2(Math.Sin(left - right), Math.Cos(left - right));

    // Captured helper endpoints agree with stopping at combined actor reach,
    // including an already-close target. Do not move the forecast backward or
    // use an unbounded facing ray when the target distance is shorter.
    private static Vector3? RightRoundOrigin(Vector3 boss, Vector3 ball, float reach)
    {
        bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
        if (!Finite(boss) || !Finite(ball) || !float.IsFinite(reach) || reach <= 0) return null;
        float distance = boss.Distance2D(ball);
        if (!float.IsFinite(distance)) return null;
        if (distance <= reach) return boss;
        float fraction = (distance - reach) / distance;
        return new Vector3(boss.X + (ball.X - boss.X) * fraction, boss.Y, boss.Z + (ball.Z - boss.Z) * fraction);
    }

    private void ResolveRightRound(Hazard h, BattleCharacter boss, DateTime now)
    {
        // A cast can be discovered before its target id/object is populated.
        // Reconcile that tell while unresolved without restarting its lifetime.
        if (boss?.IsCasting == true && boss.CastingSpellId == RightRound && boss.SpellCastInfo != null)
            h.Target = boss.SpellCastInfo.TargetId;
        var ball = GameObjectManager.GetObjectByObjectId(h.Target) as BattleCharacter;
        Vector3? prediction = boss?.IsValid == true && ball?.IsValid == true && ball.BaseId == MorningstarBase
            ? RightRoundOrigin(h.Origin, ball.Location, boss.CombatReach + ball.CombatReach) : null;
        if (prediction.HasValue) { h.Origin = prediction.Value; h.Unresolved = false; return; }
        if (now < h.NextWarning) return;
        h.NextWarning = now.AddSeconds(1);
        Logger.Information("[Malikah] Right Round target/reach geometry unresolved; ordinary approach blocked, prediction retrying until fixed expiry.");
    }

    // Intersect the ray from the actual staging point with the arena wall,
    // rather than measuring a shortened radial sample from the caster.
    private static Vector3? KnockbackLanding(Vector3 origin, Vector3 from)
    {
        float dx = from.X - origin.X, dz = from.Z - origin.Z;
        float length = (float)Math.Sqrt(dx * dx + dz * dz);
        if (length < 1 || from.Distance2D(TalosCenter) > 19.55f) return null;
        dx /= length; dz /= length;
        float x = from.X - TalosCenter.X, z = from.Z - TalosCenter.Z;
        float projection = x * dx + z * dz;
        float discriminant = projection * projection - (x * x + z * z - 19.55f * 19.55f);
        if (!float.IsFinite(discriminant) || discriminant < 0) return null;
        float travel = Math.Min(20, -projection + (float)Math.Sqrt(discriminant));
        if (travel < 0) return null;
        return new Vector3(from.X + dx * travel, from.Y, from.Z + dz * travel);
    }

    private bool KnockbackDamageSafe(Vector3 point)
    {
        // Talos's native regions are Wellbore and eruptions. Swift's warning
        // follows knockback and has its own authored owner. Do not mistake the
        // inset exterior avoid for physical knockback damage.
        foreach (var h in ActiveHazards())
        {
            float distance = point.Distance2D(h.Origin);
            if (h.Action == Wellbore && distance <= 15.5f || h.Action == GeyserEruption && distance <= 8.5f) return false;
        }
        return true;
    }

    private void SetPosition(Vector3? point, string reason, bool hazardExit = false)
    {
        if (!point.HasValue || AvoidanceManager.IsRunningOutOfAvoid && !hazardExit ||
            MovementManager.GenerallyOutOfControl && !Core.Me.IsCasting) { ReleasePosition(); return; }
        // Terrain elevation jitters by~0.00001y during otherwise stable travel.
        // Log logical horizontal goal changes, not those per-pulse Y samples.
        if (!destination.HasValue || destination.Value.Distance2D(point.Value) > .01f || positioningReason != reason)
            Logger.Information($"[Malikah] POSITION {reason} destination={point} player={Core.Me.Location} swiftHeading={swiftSequence?.Heading}.");
        destination = point; positioningReason = reason;
        travelling = Core.Me.Location.Distance2D(point.Value) > PositionArrivalTolerance;
        // RB includes ordinary casting in GenerallyOutOfControl. Admit this
        // semantic request before stopping that cast, otherwise a caster can
        // repeatedly resume rotation without ever admitting its stack approach.
        if (travelling && Core.Me.IsCasting) ActionManager.StopCasting();
        // RB validates one capability value per call; combining these flags
        // throws before the semantic request reaches its movement owner.
        CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        CapabilityManager.Update(positioning, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), reason);
        leased = true;
        if (hazardExit) mover.RequestHazardExit(point.Value, PositionArrivalTolerance);
        else mover.Request(point, PositionArrivalTolerance);
    }

    private void ReleasePosition()
    {
        if (leased)
        {
            CapabilityManager.Clear(positioning, CapabilityFlags.Movement, "Malikah positioning released");
            CapabilityManager.Clear(positioning, CapabilityFlags.GapCloser, "Malikah positioning released");
            Logger.Information("[Malikah] POSITION released.");
        }
        leased = travelling = false; destination = null; positioningReason = null;
        mover?.Request(null, PositionArrivalTolerance);
    }

    private bool PointSafe(Vector3 p) => (room == StorgeRoom ? Math.Abs(p.X - Center.X) <= 14 && Math.Abs(p.Z - Center.Z) <= 14 : p.Distance2D(Center) <= 19) &&
        !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));

    private bool OrdinarySegmentSafe(Vector3 end) => swiftSequence == null && !hazards.Values.Any(h => h.Unresolved) && SegmentSafe(end);

    private bool SegmentSafe(Vector3 end)
    {
        var start = Core.Me.Location;
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(end) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = i / (float)steps;
            if (!PointSafe(new Vector3(start.X + (end.X - start.X) * t, start.Y, start.Z + (end.Z - start.Z) * t))) return false;
        }
        return true;
    }

    private void ReleaseSideStep()
    {
        if (savedSideStep.HasValue && SidestepPlugin != null && !SidestepPlugin.Enabled) SidestepPlugin.Enabled = savedSideStep.Value;
        if (savedSideStep.HasValue) Logger.Information("[Malikah] SideStep restored.");
        savedSideStep = null;
    }

    private void Reset()
    {
        ReleasePosition(); mover?.Detach(); mover = null; ReleaseSideStep();
        CapabilityManager.Clear(hazardCapabilities, CapabilityFlags.GapCloser, "Malikah lifecycle reset");
        hazards.Clear(); room = 0; stackTarget = 0; stackUntil = knockbackAt = knockbackUntil = DateTime.MinValue;
        unsupportedReported = rightRoundOwned = false;
        swiftSequence = null; swiftWaypointAngle = null; nextSwiftBlocked = DateTime.MinValue;
        rightRoundSequence = null; rightRoundRoute = null; rightRoundLeg = 0;
    }

    private sealed class Hazard
    {
        internal uint Actor, Action, Target;
        internal Vector3 Origin;
        internal float Heading;
        internal float LastHeading;
        internal int Direction;
        internal Vector2[] Points;
        internal DateTime Impact, Expires, NextWarning;
        internal bool Ordered, Unresolved, CasterOriginLocked;
    }

    // Same local polygon convention as the maintained avoidance helpers: +Z
    // forward, rotated by negative heading. Damage bounds carry 0.5y clearance.
    private static Vector2[] Circle(float radius) => Enumerable.Range(0, 64).Select(i =>
        new Vector2(radius * (float)Math.Sin(i * Math.Tau / 64), radius * (float)Math.Cos(i * Math.Tau / 64))).ToArray();
    private static Vector2[] Donut(float outer, float inner)
    {
        // Close both rings and reverse the hole winding. Candidate1's two
        // unclosed rings admitted a radial wedge: the native escape stopped
        // 13.91y from a nail whose safe radius was4.5y. A repeated bridge point
        // removes that false corridor without changing shared/frozen helpers.
        var outside = Circle(outer);
        var hole = Circle(inner).Reverse().ToArray();
        return [.. outside, outside[0], .. hole, hole[0]];
    }
    private static Vector2[] Cross(float half, float length) => [new(half,length), new(half,half), new(length,half), new(length,-half),
        new(half,-half), new(half,-length), new(-half,-length), new(-half,-half), new(-length,-half), new(-length,half), new(-half,half), new(-half,length)];
    private static Vector2[] Cone(float radius, float arc) => [new(0,0), .. Enumerable.Range(0,33).Select(i =>
        new Vector2(radius * (float)Math.Sin((-arc / 2 + arc * i / 32) * Math.PI / 180), radius * (float)Math.Cos((-arc / 2 + arc * i / 32) * Math.PI / 180)))];
}
