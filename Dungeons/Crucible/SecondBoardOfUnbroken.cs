using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons
{
    /// <summary>
    /// Coordinates Second Board encounters and their independent movement owners.
    /// Only enemy helper actions define hazards; friendly pet casts are excluded.
    /// </summary>
    public sealed class SecondBoardOfUnbroken : AbstractDungeon
    {
        private static readonly Vector3 Center = new Vector3(120, 0, -420);
        private readonly WyvernMechanics wyvern = new WyvernMechanics();
        private readonly VoidmancerMechanics voidmancer = new VoidmancerMechanics();
        private readonly TablitaurMechanics tablitaur = new TablitaurMechanics();
        private readonly LoosefroxMechanics loosefrox = new LoosefroxMechanics();
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly List<Hazard> active = new List<Hazard>();
        private readonly Queue<Hazard> charge = new Queue<Hazard>();
        private readonly List<uint> glowOrder = new List<uint>();
        private readonly HashSet<uint> seenGlows = new HashSet<uint>();
        private Vector3 lastChargeEnd;
        private float lastChargeHeading;
        private bool armsQueued;
        private int previewCount;
        private DateTime queueExpires, resolveCharge, nextHealth;
        private readonly CapabilityManagerHandle halfRoomHandle = CapabilityManager.CreateNewHandle();
        private DateTime halfRoomUntil;
        private bool halfRoomOwned;
        private System.Numerics.Vector2? halfRoomDestination;
        private bool halfRoomMoving;
        private static bool InOpening => WorldManager.ZoneId == 1340 && Core.Me.Distance2D(Center) < 45;
        /// <inheritdoc/>
        public override ZoneId ZoneId => (ZoneId)1340;
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToFollowDodge { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToTankBust { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToMitigate { get; } = new HashSet<uint>();

        /// <inheritdoc/>
        protected override Task<bool> EnterDungeonAsync()
        {
            // One owner, as requested for Crucible. Generic pet-telegraph detection
            // already fled friendly Meteor in First Board trials. Native RB
            // avoidance retains path selection; no manual mover competes here.
            SidestepPlugin.Enabled = false;
            Reset();
            wyvern.Register();
            voidmancer.Register();
            tablitaur.Register();
            loosefrox.Register();
            AvoidanceHelpers.AddAvoidDonut(InOpeningCondition, () => Center, 65, 19);
            AvoidanceManager.AddAvoidPolygon<Hazard>(condition: InOpeningCondition, leashPointProducer: null, leashRadius: 60, rotationProducer: h => -h.Heading, scaleProducer: h => 1, heightProducer: h => 15, pointsProducer: h => h.Points, locationProducer: h => h.Origin, collectionProducer: NativeHazards, priority: AvoidancePriority.High);
            ff14bot.Helpers.Logging.Write("[CrucibleSecond] Manticore/Wyvern/Voidmancer/Tablitaur trial handlers registered; live validation required.");
            return Task.FromResult(false);
        }

        private static bool InOpeningCondition() => InOpening;
        private void Reset()
        {
            casts.Clear();
            active.Clear();
            charge.Clear();
            glowOrder.Clear();
            seenGlows.Clear();
            previewCount = 0;
            armsQueued = false;
            resolveCharge = default;
            queueExpires = default;
            halfRoomUntil = default;
            ReleaseHalfRoom();
        }

        private IEnumerable<Hazard> CurrentHazards()
        {
            // Later inverse half-room casts must not erase the currently safe
            // side. Charge previews are likewise resolved one at a time, in
            // effect order, rather than publishing all four legs simultaneously.
            var now = DateTime.UtcNow;
            var next = active.Where(h => h.Until > now).OrderBy(h => h.Until).FirstOrDefault();
            if (next != null)
                return active.Where(h => h.Until > now && h.Until <= next.Until.AddMilliseconds(200)).ToArray();
            return charge.Count > 0 && now < queueExpires ? new[]
            {
                charge.Peek()
            }

            : Array.Empty<Hazard>();
        }

        // Keep hazards stamped into the navigation graph even while holding a
        // chosen destination. Hiding them made graph-based approach blind to the
        // very obstacles that the stable destination was intended to avoid.
        private IEnumerable<Hazard> NativeHazards() => CurrentHazards();
        /// <inheritdoc/>
        public override Task<bool> RunAsync()
        {
            wyvern.Tick();
            voidmancer.Tick();
            tablitaur.Tick();
            loosefrox.Tick();
            if (!InOpening || !Core.Me.IsAlive)
            {
                Reset();
                return Task.FromResult(false);
            }

            if (SidestepPlugin.Enabled)
                SidestepPlugin.Enabled = false;
            var now = DateTime.UtcNow;
            active.RemoveAll(h => h.Until <= now);
            if (resolveCharge != default && now >= resolveCharge)
            {
                if (charge.Count > 0)
                    charge.Dequeue();
                resolveCharge = default;
            }

            if (charge.Count > 0 && now >= queueExpires)
            {
                charge.Clear();
                previewCount = 0;
            }

            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).ToArray();
            var boss = actors.FirstOrDefault(a => a.BaseId == 0x4C53 && a.IsAlive);
            if (boss != null)
            {
                // 109080 retained the opening single-arm glows from 03:13:43/52
                // and queued that stale right/left order for the 03:14:45/50
                // left/right charge sequence. Both resulting arm hits inflicted
                // Damage Down. Match status loss as well as gain: only currently
                // present glows may seed a new sequence; already queued hazards
                // retain their independent lifetime through the actual impacts.
                glowOrder.RemoveAll(id => !boss.HasAura(id));
                seenGlows.RemoveWhere(id => !boss.HasAura(id));
                foreach (var aura in boss.CharacterAuras.Where(a => a.Id == 2193 || a.Id == 2056).OrderBy(a => a.TimespanLeft))
                    if (seenGlows.Add(aura.Id))
                    {
                        glowOrder.Add(aura.Id);
                        ff14bot.Helpers.Logging.Write("[CrucibleSecond] arm glow={0} remaining={1}", aura.Id, aura.TimespanLeft);
                    }
            }

            foreach (var actor in actors)
            {
                uint spell = actor.IsCasting ? actor.CastingSpellId : 0;
                if (casts.TryGetValue(actor.ObjectId, out uint previous) && previous == spell)
                    continue;
                casts[actor.ObjectId] = spell;
                if (spell == 0)
                    continue;
                var cast = actor.SpellCastInfo;
                bool halfRoom = spell == 48140 || spell == 48142 || spell == 50411 || spell == 50413;
                bool openingArm = spell == 48123 || spell == 48125;
                // 109424's Heads/Tails effects arrived 343–422 ms after cast end.
                // The 250 ms fence released movement before both hits. Preserve
                // 750 ms for these helpers only; charge sequencing is independent.
                var until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(halfRoom || openingArm ? 750 : 250);
                ff14bot.Helpers.Logging.Write("[CrucibleSecondCast] id={0} actor={1:X} base={2:X} pos={3} heading={4:F4} destination={5} remaining={6:F2}", spell, actor.ObjectId, actor.BaseId, actor.Location, actor.Heading, cast.CastLocation, cast.RemainingCastTime.TotalSeconds);
                if (spell == 48127)
                {
                    charge.Clear();
                    previewCount = 0;
                    armsQueued = false;
                    resolveCharge = default;
                    // Glows may precede this cast; preserve their observed order.
                    queueExpires = now.AddSeconds(25);
                }

                if (spell == 48128 && boss != null)
                {
                    var origin = previewCount == 0 ? boss.Location : lastChargeEnd;
                    var end = cast.CastLocation;
                    float dx = end.X - origin.X, dz = end.Z - origin.Z;
                    float length = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (length > .5f && length < 45 && end.Distance2D(Center) < 25)
                    {
                        float heading = (float)Math.Atan2(dx, dz);
                        charge.Enqueue(Line(origin, heading, length, queueExpires));
                        lastChargeEnd = end;
                        lastChargeHeading = heading;
                        ++previewCount;
                    }
                    else
                        ff14bot.Helpers.Logging.Write("[CrucibleSecond] Rejecting ambiguous charge preview; awaiting actual helper.");
                }

                if (spell == 48130 || spell == 48132 || spell == 48134)
                {
                    // Cast completion plus 250 ms is a provisional effect fence;
                    // live action/result capture must verify it before release.
                    resolveCharge = until;
                }

                // Live action rows verified September 16: radius 30 circle,
                // width 8 charge, and type 13 front/back helpers. Half-plane arms
                // are a conservative envelope pending the 90/180-degree source
                // disagreement; use 0.5 y radial and 1degree angular margins.
                if (spell == 48137)
                    active.Add(Circle(actor.Location, 30.5f, until));
                // The queued arms retain their observed glow side. Both actual
                // follow-up helpers reported the same actor heading in 101416;
                // replacing prediction with that heading would erase the side
                // exactly when the 0.3 s cast begins. Raw helper fallback remains
                // only for a missing preview/glow sequence.
                // 67380: both opening helpers retained the boss's forward heading,
                // and both rear-centerline dodges took Damage Down. The action
                // selects the raised arm, just like the later glow predictions:
                // 48123 right (-pi/2),48125 left (+pi/2). Keep approach suppressed
                // through impact; standing on the rear dividing line is unsafe.
                if (openingArm)
                {
                    float side = spell == 48125 ? 1 : -1;
                    active.Add(Cone(actor.Location, actor.Heading + side * (float)Math.PI / 2, 30.5f, until));
                    halfRoomUntil = until;
                }

                if ((spell == 48132 || spell == 48134) && (!armsQueued || charge.Count == 0))
                    active.Add(Cone(actor.Location, actor.Heading, 30.5f, until));
                if (halfRoom)
                {
                    // 104000:48140 and 48142 retained the same helper heading
                    // 4.5532 through both casts. The rear hit damaged the player
                    // behind that heading while the forward region was safely
                    // held. These rear action IDs encode the inverse hemisphere;
                    // actor heading alone does not carry the cast's reversal.
                    bool rear = spell == 48142 || spell == 50411;
                    active.Add(Cone(actor.Location, actor.Heading + (rear ? (float)Math.PI : 0), 40.5f, until));
                    halfRoomUntil = until;
                }

                if (spell == 48130)
                {
                    var delta = cast.CastLocation - actor.Location;
                    float length = (float)Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
                    if (length > .5f && length < 45)
                        active.Add(Line(actor.Location, (float)Math.Atan2(delta.X, delta.Z), length, until));
                }
            }

            // Glow statuses can arrive one tick after the fourth preview. Join
            // both observations across ticks; the actual casts leave only 0.3s to dodge.
            if (previewCount == 4 && !armsQueued && glowOrder.Count >= 2 && charge.Count > 0)
            {
                foreach (uint glow in glowOrder.Take(2))
                    charge.Enqueue(Cone(lastChargeEnd, lastChargeHeading + (glow == 2193 ? 1 : -1) * (float)Math.PI / 2, 30.5f, queueExpires));
                armsQueued = true;
                ff14bot.Helpers.Logging.Write("[CrucibleSecond] queued final arms from observed glows {0}; origin={1} heading={2:F4}", string.Join(",", glowOrder.Take(2)), lastChargeEnd, lastChargeHeading);
                glowOrder.Clear();
            }

            // 82404's opening arms were stable, but the 30 y leap and queued
            // charges still restarted native escape every 33 ms at its boundary.
            // Give every published Manticore hazard the same destination and
            // facing lease; otherwise routine approach resumes between those
            // native completion/re-entry pulses and pulls back toward danger.
            if (now < halfRoomUntil || CurrentHazards().Any())
            {
                // 109424 alternated avoidance/approach every 33 ms and returned
                // into both half-room hits. Native avoidance keeps path ownership;
                // suppress only routine approach through this cast's impact fence.
                // Returning false below leaves pet commands and rotation active.
                if (!halfRoomOwned && !AvoidanceManager.IsRunningOutOfAvoid)
                    MovementManager.MoveStop();
                CapabilityManager.Update(halfRoomHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: hold Manticore dodge through impact");
                // SlideMover steers with character facing; routine SetFacing
                // must not turn a committed escape back toward the boss.
                CapabilityManager.Update(halfRoomHandle, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: face Manticore escape destination");
                halfRoomOwned = true;
                MoveHalfRoom();
            }
            else
                ReleaseHalfRoom();
            // Status-loss handling above re-arms each glow independently. Do
            // not clear seenGlows here: zero-duration statuses can remain present
            // after queuing and must not be mistaken for a new sequence.
            if (now >= nextHealth)
            {
                nextHealth = now.AddSeconds(2);
                ff14bot.Helpers.Logging.Write("[CrucibleSecondHealth] player={0:F1} pet={1} petHP={2:F1} boss={3:F1} pos={4} queued={5}", Core.Me.CurrentHealthPercent, Core.Me.Pet?.Name, Core.Me.Pet?.CurrentHealthPercent, boss?.CurrentHealthPercent, Core.Me.Location, charge.Count);
            }

            return Task.FromResult(false);
        }

        private void ReleaseHalfRoom()
        {
            if (halfRoomMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                ff14bot.Navigation.Navigator.Stop();
            halfRoomMoving = false;
            halfRoomDestination = null;
            if (halfRoomOwned)
                CapabilityManager.Clear(halfRoomHandle, CapabilityFlags.Movement | CapabilityFlags.Facing, "Crucible: Manticore half-room effect resolved");
            halfRoomOwned = false;
        }

        private void MoveHalfRoom()
        {
            // Native escape can oscillate at the half-plane edge. Hold a
            // destination with 1.5y clearance, but let native navigation route
            // there so travel cannot cross another active avoid.
            if (AvoidanceManager.IsRunningOutOfAvoid)
                return;
            var polygons = CurrentHazards().Select(h => h.Points.Select(p => new System.Numerics.Vector2(h.Origin.X + p.X * (float)Math.Cos(h.Heading) + p.Y * (float)Math.Sin(h.Heading), h.Origin.Z - p.X * (float)Math.Sin(h.Heading) + p.Y * (float)Math.Cos(h.Heading))).ToArray()).ToArray();
            var position = new System.Numerics.Vector2(Core.Me.Location.X, Core.Me.Location.Z);
            // Shared preference ranks only destinations that pass dodge safety.
            var destination = ManticoreSafePosition.Choose(position, new System.Numerics.Vector2(Center.X, Center.Z), 18.5f, polygons, halfRoomDestination, false, preference: CrucibleMeleePreference.Capture());
            if (!destination.HasValue)
            {
                // An unexpected overlap must retain the existing native escape
                // fallback rather than silently holding an unsafe location.
                ff14bot.Helpers.Logging.Write("[CrucibleManticoreHold] No safe point; restoring native avoidance for this overlap.");
                halfRoomUntil = default;
                ReleaseHalfRoom();
                return;
            }

            if (halfRoomDestination != destination)
                ff14bot.Helpers.Logging.Write("[CrucibleManticoreHold] from={0} destination={1} hazards={2}", position, destination, polygons.Length);
            halfRoomDestination = destination;
            if (System.Numerics.Vector2.DistanceSquared(position, destination.Value) > .3f * .3f)
            {
                ff14bot.Navigation.Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(new Vector3(destination.Value.X, Center.Y, destination.Value.Y), "Crucible: stable Manticore dodge") { DistanceTolerance = .3f, UseMount = false });
                halfRoomMoving = true;
            }
            else if (halfRoomMoving)
            {
                ff14bot.Navigation.Navigator.Stop();
                halfRoomMoving = false;
            }
        }

        private static Hazard Line(Vector3 origin, float heading, float length, DateTime until) => new Hazard
        {
            Origin = origin,
            Heading = heading,
            Until = until,
            Points = new[]
            {
                new Vector2(-4.5f, -.5f),
                new Vector2(4.5f, -.5f),
                new Vector2(4.5f, length + .5f),
                new Vector2(-4.5f, length + .5f)
            }
        };
        private static Hazard Cone(Vector3 origin, float heading, float radius, DateTime until)
        {
            var points = new List<Vector2>
            {
                new Vector2(0, -.5f)
            };
            for (int i = 0; i <= 36; i++)
            {
                double angle = (-91 + 182.0 * i / 36) * Math.PI / 180;
                points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
            }

            return new Hazard
            {
                Origin = origin,
                Heading = heading,
                Until = until,
                Points = points.ToArray()
            };
        }

        private static Hazard Circle(Vector3 origin, float radius, DateTime until) => new Hazard
        {
            Origin = origin,
            Until = until,
            Points = Enumerable.Range(0, 64).Select(i => new Vector2((float)Math.Sin(i * Math.PI / 32) * radius, (float)Math.Cos(i * Math.PI / 32) * radius)).ToArray()
        };
        private sealed class Hazard
        {
            internal Vector3 Origin;
            internal float Heading;
            internal DateTime Until;
            internal Vector2[] Points;
        }
    }
}
