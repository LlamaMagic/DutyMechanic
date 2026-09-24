using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using V2 = System.Numerics.Vector2;
using V3 = Clio.Utilities.Vector3;

namespace DutyMechanic.Dungeons
{
    /// <summary>
    /// Mechanics for the captured Third Board route. Native avoidance owns
    /// geometric dodges; explicit navigation owns required staging and facing.
    /// Staging yields to native escape; the routine owns completed-escape cleanup. Unobserved branch encounters are not registered.
    /// </summary>
    public sealed partial class ThirdBoardOfUnbroken : AbstractDungeon
    {
        private static readonly HashSet<uint> Bosses = new HashSet<uint>
        {
            0x4C8E,
            0x4C93,
            0x4C9B,
            0x4C9E,
            0x4CA1,
            0x4CAA
        };
        private readonly List<ThirdBoardHazard> hazards = new List<ThirdBoardHazard>();
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly CapabilityManagerHandle positioning = CapabilityManager.CreateNewHandle();
        private readonly List<Knockback> knockbacks = new List<Knockback>();
        private uint encounter;
        private V2? destination;
        private bool holding;
        private bool? autoFacing;
        private DateTime nextDiagnostic, baitUntil;
        private uint baitAction;
        private V2 baitSource;
        private V2? previousPlayerPosition;
        private DateTime previousPlayerTime;
        private DateTime nextPublication;
        private ThirdBoardHazard[] published = Array.Empty<ThirdBoardHazard>();
        private readonly Dictionary<ThirdBoardHazard, ThirdBoardCircle[]> cageCircles = new Dictionary<ThirdBoardHazard, ThirdBoardCircle[]>();
        private ThirdBoardCircle[] floorCircles = Array.Empty<ThirdBoardCircle>();
        private readonly Dictionary<ThirdBoardHazard, ThirdBoardEyePublication> eyePublications = new Dictionary<ThirdBoardHazard, ThirdBoardEyePublication>();
        private readonly Dictionary<V2[], Clio.Utilities.Vector2[]> nativePoints = new Dictionary<V2[], Clio.Utilities.Vector2[]>();
        private Clio.Utilities.Vector2[] NativePoints(ThirdBoardHazard hazard)
        {
            if (!nativePoints.TryGetValue(hazard.Points, out var points))
            {
                nativePoints[hazard.Points] = points = hazard.Points.Select(p => new Clio.Utilities.Vector2(p.X, p.Y)).ToArray();
            }

            return points;
        }

        /// <inheritdoc/>
        public override ZoneId ZoneId => (ZoneId)1341;
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToFollowDodge { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToTankBust { get; } = new HashSet<uint>
        {
            48563,
            48620
        };
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToMitigate { get; } = new HashSet<uint>
        {
            48479,
            48504,
            48561
        };

        private bool Active() => WorldManager.ZoneId == 1341 && Core.Me.IsAlive && encounter != 0 && V2.Distance(Point(Core.Me.Location), ThirdBoardGeometry.Center(encounter)) < 65;
        private static V2 Point(V3 p) => new V2(p.X, p.Z);
        private static V3 World(V2 p) => new V3(p.X, 0, p.Y);
        /// <inheritdoc/>
        protected override Task<bool> EnterDungeonAsync()
        {
            Reset();
            SidestepPlugin.Enabled = false;
            TreeRoot.OnStop += OnBotStopped;
            // Boundaries outrank attacks. RB's run-out field has one area cost
            // per span (Low20/Medium40/High80), not additive overlap penalties:
            // equal-priority arena-wide attacks hide the cost of crossing a wall.
            AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && encounter != 0x4C93 && encounter != 0x4CAA && encounter != 0x4C9B && encounter != 0x4CA1, 40 - 2 * ThirdBoardGeometry.EdgeClearance, 40 - 2 * ThirdBoardGeometry.EdgeClearance, 140, 140, () => new[] { new V3(120, 0, 0) }, priority: AvoidancePriority.High);
            // The fine Guttler mask below ends at +/-17.25X and +/-28.25Z.
            // Native navigation can route around this finite mask where no
            // exterior polygon exists. Seal beyond the mask with an
            // overlapping outer apron; this adds no restriction to legal alcoves.
            AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && encounter == 0x4CAA, 34, 56, 140, 140, () => new[] { new V3(520, 0, -420) }, priority: AvoidancePriority.High);
            // Keep live contact/floor at High, resolving attacks at Medium, and
            // future eye endpoints at Low.101164 crossed an eye while escaping
            // a High forecast: overlapping High spans offered no extra cost.
            // All tiers remain forbidden endpoints; this orders transit risk.
            AvoidanceManager.AddAvoidPolygon<ThirdBoardHazard>(Active, null, 80, h => -h.Heading, h => 1, h => 15, NativePoints, h => World(h.Origin),
                () => PublishedPolygons().Where(h => boundary.Contains(h)), priority: AvoidancePriority.High);
            AvoidanceManager.AddAvoidPolygon<ThirdBoardHazard>(Active, null, 80, h => -h.Heading, h => 1, h => 15, NativePoints, h => World(h.Origin),
                () => PublishedPolygons().Where(h => !boundary.Contains(h) && !EyeForecast(h)), priority: AvoidancePriority.Medium);
            AvoidanceManager.AddAvoidPolygon<ThirdBoardHazard>(Active, null, 80, h => -h.Heading, h => 1, h => 15, NativePoints, h => World(h.Origin),
                () => PublishedPolygons().Where(EyeForecast), priority: AvoidancePriority.Low);
            // AddAvoidLocation's convenience overload silently uses Medium.
            // Explicit High preserves the boundary/contact transit penalty.
            AvoidanceManager.AddAvoid(new AvoidLocationInfo<ThirdBoardCircle>(Active,
                h => World(h.Center), h => h.Radius, PublishedCircles,
                leashRadius: 80, priority: AvoidancePriority.High));
            return Task.FromResult(false);
        }

        private IEnumerable<ThirdBoardHazard> PublishedPolygons() => PublishedHazards()
            .Where(h => !(h.Action == 0 && h.Persistent))
            .Where(h => encounter != 0x4CAA || !GuttlerCageCover.Supports(h));

        // Forecasts belong to travelling eye actors; actual burst casts come
        // from distinct helpers and must retain resolving-attack priority.
        private bool EyeForecast(ThirdBoardHazard hazard) =>
            (hazard.Action == 48506 || hazard.Action == 48508) && eyes.ContainsKey(hazard.Actor);

        private IEnumerable<ThirdBoardCircle> PublishedCircles()
        {
            var snapshot = PublishedHazards().ToArray();
            var current = encounter == 0x4CAA
                ? snapshot.Where(GuttlerCageCover.Supports).ToArray()
                : Array.Empty<ThirdBoardHazard>();
            // Retain stable identities through a wave, and discard expired
            // shapes. Rebuilding hundreds of avoids every pulse churns routes.
            foreach (var old in cageCircles.Keys.Where(h => !current.Contains(h)).ToArray()) cageCircles.Remove(old);
            foreach (var hazard in current)
                if (!cageCircles.ContainsKey(hazard)) cageCircles[hazard] = GuttlerCageCover.Create(hazard);
            return cageCircles.Values.SelectMany(c => c).Concat(floorCircles)
                .Concat(eyePublications.Values.SelectMany(eye => eye.Circles));
        }

        private IEnumerable<ThirdBoardHazard> PublishedHazards()
        {
            if (!Active())
            {
                return Array.Empty<ThirdBoardHazard>();
            }

            var now = DateTime.UtcNow;
            // Refresh the bounded contact envelopes even between wave snapshots:
            // a teleport must never leave its real contact circle uncovered.
            var contacts = hazards.Where(h => h.Action == 0 && h.Persistent && h.Until > now).ToArray();
            foreach (var source in eyePublications.Keys.Where(h => !contacts.Contains(h)).ToArray())
            {
                nativePoints.Remove(eyePublications[source].Hazard.Points);
                eyePublications.Remove(source);
                nextPublication = default;
            }

            foreach (var contact in contacts)
            {
                if (!eyePublications.TryGetValue(contact, out var publication))
                {
                    eyePublications[contact] = publication = new ThirdBoardEyePublication(contact, now);
                    nextPublication = default;
                }

                var previousPoints = publication.Hazard.Points;
                publication.Read(contact, now);
                // Moving-eye capsules replace their outline at bounded cadence.
                // Retire converted arrays with it rather than grow the cache.
                if (!ReferenceEquals(previousPoints, publication.Hazard.Points))
                    nativePoints.Remove(previousPoints);
            }

            if (now < nextPublication)
            {
                return published;
            }

            nextPublication = now.AddMilliseconds(200);
            var pending = hazards.Where(h => h.Until > now && !(h.Action == 48555 && knockbacks.Any(k => k.Lane != null && k.Until > now))).ToArray();
            // Never union mutually exclusive successive waves. Persistent cage
            // bodies remain active through every wave. Combine later casts only
            // when at least one common safe point exists in the actual arena.
            var imminent = ThirdBoardGeometry.SelectWave(pending, encounter);
            published = imminent.Select(h => eyePublications.TryGetValue(h, out var eye) ? eye.Hazard : h).Concat(boundary).ToArray();
            return published;
        }

        // A native escape or provider replacement invalidates permission to stop
        // our previous staging route. Never stop game-controlled displacement.
        private bool stagingMoving;
        private readonly ThirdBoardStagingProgress stagingProgress = new ThirdBoardStagingProgress();
        private V2? directStagingTarget;
        private object stagingProvider, stagingMover;
        private void StopStaging()
        {
            if (stagingMoving && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl && !Core.Me.HasAura(1257) && ReferenceEquals(stagingProvider, Navigator.NavigationProvider) && ReferenceEquals(stagingMover, Navigator.PlayerMover))
            {
                Navigator.Stop();
                Navigator.Clear();
            }

            stagingMoving = false;
            stagingProgress.Reset();
            directStagingTarget = null;
        }

        private void Hold(V2 point, float? facing = null)
        {
            SuppressPursuit(preserveAutoFacing: true);
            if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || Core.Me.HasAura(1257))
            {
                stagingMoving = false;
                stagingProgress.Reset();
                directStagingTarget = null;
                return;
            }

            // Restore explicit graph navigation for positive mechanics after the
            // tiny staging masks repeatedly failed native escape search. Disable
            // action auto-facing during travel so attacks cannot steer SlideMover.
            bool traveling = V2.Distance(Point(Core.Me.Location), point) > .35f;
            if (traveling || facing.HasValue)
            {
                if (!autoFacing.HasValue)
                    autoFacing = GameSettingsManager.FaceTargetOnAction;
                GameSettingsManager.FaceTargetOnAction = false;
            }

            if (traveling)
            {
                var current = Point(Core.Me.Location);
                // Keep graph navigation as the normal owner. Its captured short
                // endpoint stall may use a direct final segment only after one
                // second without progress and full current-hazard validation.
                // Never bypass an active escape or game-controlled movement.
                if (directStagingTarget.HasValue && V2.Distance(directStagingTarget.Value,point) > .1f) directStagingTarget=null;
                if ((directStagingTarget.HasValue || stagingProgress.Stalled(current, point, DateTime.UtcNow)) && ClearStagingSegment(current, point))
                {
                    if (!directStagingTarget.HasValue)
                    {
                        Navigator.Clear();
                        ff14bot.Helpers.Logging.Write("[CrucibleThirdStaging] Completing verified clear final segment from {0} to {1} after graph stall.",current,point);
                    }
                    directStagingTarget=point;
                    Navigator.PlayerMover.MoveTowards(World(point));
                }
                else
                {
                    directStagingTarget=null;
                    Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(World(point), "Third Board staging") { DistanceTolerance = .35f, UseMount = false });
                }
                stagingProvider = Navigator.NavigationProvider;
                stagingMover = Navigator.PlayerMover;
                stagingMoving = true;
            }
            else
            {
                StopStaging();
                if (!facing.HasValue && autoFacing.HasValue)
                {
                    GameSettingsManager.FaceTargetOnAction = autoFacing.Value;
                    autoFacing = null;
                }

                if (facing.HasValue)
                    Core.Me.SetFacing(facing.Value);
            }
        }

        private bool ClearStagingSegment(V2 from, V2 to)
        {
            if (!ThirdBoardGeometry.Corridor(from, to, encounter, hazards.Where(h => h.Until > DateTime.UtcNow))) return false;
            int steps = Math.Max(1, (int)Math.Ceiling(V2.Distance(from,to)*4));
            for (int i=0;i<=steps;i++)
            {
                var p=World(V2.Lerp(from,to,i/(float)steps));
                if (AvoidanceManager.Avoids.Any(a=>!a.ShouldIgnore && a.IsPointInAvoid(p))) return false;
            }
            return true;
        }

        /// <inheritdoc/>
        public override Task<bool> RunAsync()
        {
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Core.Me.Location) < 90).ToArray();
            var boss = actors.FirstOrDefault(a => Bosses.Contains(a.BaseId) && a.IsAlive);
            // Ymir's shell/head can die before Sahagin. Keep that encounter's
            // knockback handling alive until the remaining add is also dead.
            if (boss == null)
            {
                boss = actors.FirstOrDefault(a => (a.BaseId == 0x4C94 || a.BaseId == 0x4C95) && a.IsAlive);
            }

            if (WorldManager.ZoneId != 1341 || !Core.Me.IsAlive || boss == null)
            {
                Reset();
                return Task.FromResult(false);
            }

            uint nextEncounter = boss.BaseId == 0x4C94 || boss.BaseId == 0x4C95 ? 0x4C93u : boss.BaseId;
            if (encounter != nextEncounter)
            {
                Reset();
                encounter = nextEncounter;
                BuildBoundary();
            }

            SidestepPlugin.Enabled = false;
            var now = DateTime.UtcNow;
            var player = Point(Core.Me.Location);
            if (encounter == 0x4CA1)
            {
                bool previousRecovery = boundaryRecovery.Active;
                boundaryRecovery.Update(player, AvoidanceManager.IsRunningOutOfAvoid,
                    MovementManager.GenerallyOutOfControl || Core.Me.HasAura(1257) || Core.Me.HasAura(5710), now);
                if (previousRecovery != boundaryRecovery.Active)
                {
                    // Changing this polygon asks the existing native owner to
                    // regenerate its escape. Real attack avoids remain active.
                    BuildBoundary();
                    nextPublication = default;
                    ff14bot.Helpers.Logging.Write("[CrucibleSirenBoundary] Recovery={0} position={1}.", boundaryRecovery.Active, player);
                }
            }
            foreach (var lane in knockbacks.Where(k => k.Lane != null && now < k.ResolveAt))
            {
                lane.BeforeEffect = lane.Lane.Contains(player) ? player : (V2? )null;
            }

            if (knockbacks.Any(k => k.Lane != null && now >= k.ResolveAt && now < k.Until && k.BeforeEffect.HasValue && ThirdBoardGeometry.LandslipMoved(k.BeforeEffect.Value, player, k.Heading)))
            {
                // Retire the simultaneous lane set, not unrelated knockbacks.
                // Publishing Rockslide again hands the landing to native dodge.
                knockbacks.RemoveAll(k => k.Lane != null && now >= k.ResolveAt);
                Release();
                nextPublication = default;
                ff14bot.Helpers.Logging.Write("[CrucibleThirdLandslip] Displacement confirmed at {0}; staging released, Rockslide avoidance restored.", player);
            }

            // A resolved knockback must not send us back to its staging point
            // during the effect fence. Ordinary motion cannot travel nine yalms
            // between adjacent (<500ms) pulses. Long pauses do not qualify.
            if (previousPlayerPosition.HasValue && now - previousPlayerTime < TimeSpan.FromMilliseconds(500) && V2.Distance(player, previousPlayerPosition.Value) > 9 && knockbacks.Any(k => k.Until < now.AddSeconds(4)))
            {
                knockbacks.Clear();
                Release();
                nextPublication = default;
            }

            previousPlayerPosition = player;
            previousPlayerTime = now;
            hazards.RemoveAll(h => h.Until <= now);
            knockbacks.RemoveAll(k => k.Until <= now);
            CapturePredictions(actors, boss, now);
            foreach (var actor in actors)
            {
                uint action = actor.IsCasting ? actor.CastingSpellId : 0;
                if (casts.TryGetValue(actor.ObjectId, out var old) && old == action)
                {
                    continue;
                }

                casts[actor.ObjectId] = action;
                if (action != 0)
                {
                    CaptureCast(actor, action, now);
                }
            }

            // Snapshot cleanup prevents object-id reuse and growth across waves.
            var present = new HashSet<uint>(actors.Select(a => a.ObjectId));
            foreach (uint id in casts.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                casts.Remove(id);
            }

            TickPositioning(now);
            return Task.FromResult(false);
        }

        private void CaptureCast(BattleCharacter actor, uint action, DateTime now)
        {
            nextPublication = default;
            var cast = actor.SpellCastInfo;
            var until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(450);
            var origin = Point(actor.Location);
            float heading = actor.Heading;
            // Half-yalm spatial margin and a short effect fence cover cast
            // snapshot latency. This is not a substitute for action-effect data.
            System.Numerics.Vector2[] shape = null;
            switch (action)
            {
                case 48473:
                    shape = ThirdBoardHazard.Cone(60.5f, 66);
                    break;
                case 48464:
                    shape = ThirdBoardHazard.Circle(20.5f);
                    break;
                case 48462:
                    shape = ThirdBoardHazard.Rectangle(4.5f, 60.5f);
                    break;
                case 48483:
                case 48560:
                    origin = Point(cast.CastLocation);
                    if (!ThirdBoardGeometry.InArena(origin, encounter, -8))
                    {
                        return;
                    }

                    shape = ThirdBoardHazard.Circle(6.5f);
                    break;
                case 48512:
                    shape = ThirdBoardHazard.Cone(60.5f, 91);
                    break;
                case 48555:
                    shape = ThirdBoardHazard.Rectangle(5.5f, 45.5f);
                    break;
                case 48562:
                    shape = ThirdBoardHazard.Circle(12.5f);
                    break;
                case 48570:
                    shape = ThirdBoardHazard.Rectangle(8.5f, 50.5f);
                    break;
                case 48573:
                    shape = ThirdBoardHazard.Circle(12.5f);
                    break;
                case 48577:
                    shape = ThirdBoardHazard.Circle(9.5f);
                    break;
                case 50662:
                    shape = ThirdBoardHazard.Rectangle(2, 5.5f);
                    break;
                case 48597:
                    shape = ThirdBoardHazard.Circle(8.5f);
                    break;
                // The captured helper faced south at arena center, yet the
                // player north of it took Gyrocleave. Cover both halves of the
                // arena-length strip; the forward-only origin interpretation
                // incorrectly marked the northern main floor safe. Side alcoves
                // remain the refuge. Each end includes the usual 0.5y margin.
                case 48605:
                    shape = ThirdBoardHazard.Rectangle(10.5f, 40.5f, 40.5f);
                    break;
                case 48603:
                    shape = ThirdBoardHazard.Circle(40.5f);
                    break;
                case 48600:
                    shape = ThirdBoardHazard.Rectangle(20.5f, 50.5f);
                    break;
                case 48614:
                    shape = ThirdBoardHazard.Rectangle(3.5f, 60.5f);
                    baitUntil = default;
                    break;
                case 48618:
                    shape = ThirdBoardHazard.Circle(6.5f);
                    baitUntil = default;
                    break;
                case 48506:
                    shape = ThirdBoardHazard.Circle(25.5f);
                    RetireEye(origin, false);
                    break;
                case 48508:
                    RetireEye(origin, true);
                    AddRing(actor.ObjectId, action, origin, 4.5f, 50.5f, until);
                    break;
                case 48575:
                    AddRing(actor.ObjectId, action, origin, 2.5f, 43.5f, until);
                    break;
                case 48611:
                    // The event object and casting helper have different IDs.
                    // Replace only this cage's forecast by spatial ownership;
                    // use the observed cast deadline for its contact body too.
                    hazards.RemoveAll(h => h.Persistent && h.Action == 48611 && V2.Distance(h.Origin, origin) < 1);
                    foreach (var cage in hazards.Where(h => h.Action == 48609 && V2.Distance(h.Origin, origin) < 1))
                    {
                        cage.Until = until;
                    }

                    Add(actor.ObjectId, action, origin, heading, ThirdBoardHazard.Rectangle(5.5f, 15.5f, 15.5f), until);
                    Add(actor.ObjectId, action, origin, heading + MathF.PI / 2, ThirdBoardHazard.Rectangle(5.5f, 15.5f, 15.5f), until);
                    break;
                case 48471:
                    // RB96792 and97904: Crushing Blade displaced the player
                    // about1.4s after its bar ended. The ordinary450ms hazard
                    // grace released pursuit too early. Preserve staging through
                    // a bounded1.95s effect fence, without rerouting during it.
                    knockbacks.Add(new Knockback { Origin = origin, Distance = 15, Radial = true, ResolveAt = now + cast.RemainingCastTime, Until = until.AddSeconds(1.5) });
                    break;
                case 48481:
                    // RB51900 released staging at21:28:34.78 before Tsunami
                    // hit35.35; its35y displacement continued through36.99.
                    // Fence the cast-to-hit delay plus travel, without walking
                    // back to the staging point while the game moves us.
                    knockbacks.Add(new Knockback { Origin = origin, Distance = 35, Heading = heading,
                        ResolveAt = now + cast.RemainingCastTime, Until = until.AddSeconds(2.5) });
                    break;
                case 48556:
                    // Landslip's effect followed its cast bar by roughly 2.5s
                    // in the capture. Rockslide resolves in that same interval.
                    knockbacks.Add(new Knockback { Origin = origin, Distance = 20, Heading = heading, Lane = new ThirdBoardHazard { Origin = origin, Heading = heading, Points = ThirdBoardHazard.Rectangle(5, 45, 0) }, ResolveAt = now + cast.RemainingCastTime, Until = until.AddSeconds(2.5) });
                    break;
                case 48607:
                    knockbacks.Add(new Knockback { Origin = origin, Distance = 20, Heading = heading + MathF.PI / 2, Bidirectional = true, Until = until });
                    break;
                case 48612:
                case 48615:
                case 48557:
                    baitAction = action;
                    baitSource = origin;
                    baitUntil = until.AddSeconds(action == 48557 ? 1 : 2.5);
                    destination = null;
                    break;
                case 48619:
                    // Flare damage scales with distance; 35y is not immunity.
                    baitAction = action;
                    baitSource = origin;
                    baitUntil = until;
                    destination = null;
                    break;
            }

            if (shape != null)
            {
                hazards.RemoveAll(h => h.Actor == actor.ObjectId && h.Action == action);
                Add(actor.ObjectId, action, origin, heading, shape, until);
            }
        // The shared diagnostic collector owns per-cast telemetry; keep
        // encounter logging focused on forecasts and recovery decisions.
        }

        private void Add(uint actor, uint action, V2 origin, float heading, V2[] points, DateTime until, bool persistent = false) => hazards.Add(new ThirdBoardHazard { Actor = actor, Action = action, Origin = origin, Heading = heading, Points = points, Until = until, Persistent = persistent });
        private void AddRing(uint actor, uint action, V2 origin, float inner, float outer, DateTime until)
        {
            hazards.RemoveAll(h => h.Actor == actor && h.Action == action);
            foreach (var wedge in ThirdBoardHazard.Donut(inner, outer))
            {
                Add(actor, action, origin, 0, wedge, until);
            }
        }

        private readonly List<ThirdBoardHazard> boundary = new List<ThirdBoardHazard>();
        private readonly SirenBoundaryRecovery boundaryRecovery = new SirenBoundaryRecovery();
        private void BuildBoundary()
        {
            foreach (var old in boundary) nativePoints.Remove(old.Points);
            boundary.Clear();
            floorCircles = Array.Empty<ThirdBoardCircle>();
            if (encounter == 0x4C93)
            {
                // Dreadwash can displace the player onto the bleeding edge.
                // Publish this square alongside Water II in the native graph;
                // boundary recovery must not create another movement owner.
                float inner = 20 - ThirdBoardGeometry.EdgeClearance;
                const float outer = 70;
                float halfBand = (outer - inner) / 2;
                float offset = (outer + inner) / 2;
                var center = ThirdBoardGeometry.Center(encounter);
                foreach (float side in new[]
                {
                    -1f,
                    1f
                }

                )
                {
                    boundary.Add(new ThirdBoardHazard { Origin = center + new V2(side * offset, 0), Points = ThirdBoardHazard.Rectangle(halfBand, outer, outer) });
                    boundary.Add(new ThirdBoardHazard { Origin = center + new V2(0, side * offset), Points = ThirdBoardHazard.Rectangle(inner, halfBand, halfBand) });
                }
            }
            else if (encounter == 0x4C9B || encounter == 0x4CA1)
            {
                float inner = boundaryRecovery.Active ? SirenBoundaryRecovery.EscapeRadius : SirenBoundaryRecovery.NormalRadius;
                foreach (var wedge in ThirdBoardHazard.Donut(inner, 65))
                {
                    boundary.Add(new ThirdBoardHazard { Origin = ThirdBoardGeometry.Center(encounter), Points = wedge });
                }
            }
            else if (encounter == 0x4CAA)
            {
                floorCircles = GuttlerBoundaryCover.Create();
            }
        }

        private void Release()
        {
            StopStaging();
            nextPublication = default;
            if (holding)
            {
                CapabilityManager.Clear(positioning, CapabilityFlags.Movement, "Third Board positioning resolved");
                CapabilityManager.Clear(positioning, CapabilityFlags.Facing, "Third Board positioning resolved");
            }

            if (autoFacing.HasValue)
            {
                GameSettingsManager.FaceTargetOnAction = autoFacing.Value;
                autoFacing = null;
            }

            holding = false;
            destination = null;
        }

        private void SuppressPursuit(float? facing = null, bool preserveAutoFacing = false)
        {
            // Suppress routine pursuit/facing, never RB avoidance. RunAsync
            // returns false so safe damage and healing remain schedulable.
            // No MoveTo/Stop command may steal the native escape route.
            CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Third Board native avoidance");
            CapabilityManager.Update(positioning, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Third Board native avoidance");
            holding = true;
            if (facing.HasValue || AvoidanceManager.IsRunningOutOfAvoid)
            {
                if (!autoFacing.HasValue)
                    autoFacing = GameSettingsManager.FaceTargetOnAction;
                GameSettingsManager.FaceTargetOnAction = false;
            }
            else if (autoFacing.HasValue && !preserveAutoFacing)
            {
                GameSettingsManager.FaceTargetOnAction = autoFacing.Value;
                autoFacing = null;
            }

            if (facing.HasValue && !AvoidanceManager.IsRunningOutOfAvoid)
                Core.Me.SetFacing(facing.Value);
        }

        private void Reset()
        {
            Release();
            hazards.Clear();
            casts.Clear();
            knockbacks.Clear();
            boundary.Clear();
            boundaryRecovery.Reset();
            nativePoints.Clear();
            eyePublications.Clear();
            cageCircles.Clear();
            floorCircles = Array.Empty<ThirdBoardCircle>();
            published = Array.Empty<ThirdBoardHazard>();
            nextPublication = default;
            ResetPredictions();
            encounter = 0;
            baitUntil = default;
            marchHeading = null;
            nextMelodySprint = default;
            previousPlayerPosition = null;
        }

        private void OnBotStopped(ff14bot.AClasses.BotBase bot) => Reset();
        /// <inheritdoc/>
        protected override Task<bool> ExitDungeonAsync()
        {
            TreeRoot.OnStop -= OnBotStopped;
            Reset();
            return Task.FromResult(false);
        }

        private sealed class Knockback
        {
            internal V2 Origin;
            internal float Heading, Distance;
            internal bool Radial, Bidirectional;
            internal ThirdBoardHazard Lane;
            internal V2? BeforeEffect;
            internal DateTime ResolveAt;
            internal DateTime Until;
        }
    }
}
