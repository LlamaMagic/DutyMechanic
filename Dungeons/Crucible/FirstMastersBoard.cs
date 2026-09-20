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
    /// Handles the right-hand route through First Master's Board. Helper casts
    /// supply avoidance geometry; encounter positioning handles transformations,
    /// briar shelter, orb collection and bounded navigation recovery.
    /// Alternate branches are not supported.
    /// </summary>
    public sealed class FirstMastersBoard : AbstractDungeon
    {
        private static readonly uint[] Bosses = { 0x4CB6, 0x4CBD, 0x4CC0, 0x4CC2, 0x4CCD, 0x4CD8 };
        private readonly List<ThirdBoardHazard> hazards = new List<ThirdBoardHazard>();
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly HashSet<uint> collectedOrbs = new HashSet<uint>();
        private ThirdBoardHazard[] orbs = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] pitchPuddles = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] sludgeFields = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] iceFields = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] poisonFields = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] poisonClouds = Array.Empty<ThirdBoardHazard>();
        private readonly Dictionary<uint, (V2 Point, DateTime Time)> cloudSamples = new();
        private DateTime breathUntil, vomitUntil;
        private float breathHeading;
        private readonly List<ThirdBoardHazard> cauterize = new List<ThirdBoardHazard>();
        private V2[] localRoute = Array.Empty<V2>();
        private bool localMovement;
        private DateTime cauterizeUntil;
        private V2? touchdownOrigin;
        private DateTime touchdownUntil;
        private DateTime nextRouteAttempt;
        private uint collectingOrb;
        private int collectionStacks;
        private DateTime rainUntil;
        private readonly CapabilityManagerHandle positioning = CapabilityManager.CreateNewHandle();
        private uint encounter, semanticAction;
        private DateTime semanticUntil, nextLog;
        private V2? destination;
        private bool moving, leased;
        private bool? previousFacing, previousSideStep;
        private V2 center;
        private V2 sweepStart, sweepSample;
        private DateTime sweepCastEnd, sweepWatchUntil, sweepLandingAt, sweepStableSince;
        private bool watchingSweep;
        private bool briarSheltering, briarExiting;
        private bool retreatWasBlocked;

        /// <inheritdoc/>
        public override ZoneId ZoneId => (ZoneId)1342;
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToFollowDodge { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToTankBust { get; } = new HashSet<uint> { 48730, 48731 };
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToMitigate { get; } = new HashSet<uint> { 48669, 48709, 48723, 48725, 50696 };
        private static V2 Point(V3 p) => new V2(p.X, p.Z);
        private static V3 World(V2 p) => new V3(p.X, 0, p.Y);
        private bool Active() => WorldManager.ZoneId == 1342 && Core.Me.IsAlive && encounter != 0;
        private bool Round => encounter == 0x4CB6 || encounter == 0x4CBD || encounter == 0x4CCD || encounter == 0x4CD8;
        // Cauterize's attack extends beyond the floor. Boundary damage starts
        // before Z21.25; keep destinations inside the shared 40-yalm arena.
        private float Extent => 18.75f;
        private bool InFloor(V2 p) => Round ? V2.Distance(p, center) <= Extent :
            Math.Abs(p.X - center.X) <= Extent && Math.Abs(p.Y - center.Y) <= Extent;

        /// <inheritdoc/>
        protected override Task<bool> EnterDungeonAsync()
        {
            Reset();
            previousSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            TreeRoot.OnStop += OnStopped;
            AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && !Round && !localMovement, 37.5f, 37.5f, 140, 140,
                () => new[] { World(center) });
            AvoidanceManager.AddAvoidPolygon<ThirdBoardHazard>(() => Active() && !localMovement, null, 80,
                h => -h.Heading, h => 1, h => 15,
                h => h.Points.Select(p => new Clio.Utilities.Vector2(p.X, p.Y)).ToArray(),
                h => World(h.Origin), Published, priority: AvoidancePriority.High);
            return Task.FromResult(false);
        }

        private IEnumerable<ThirdBoardHazard> Published()
        {
            if (!Active())
            {
                return Array.Empty<ThirdBoardHazard>();
            }

            var now = DateTime.UtcNow;
            // Plummet resolves in three groups roughly two seconds apart.
            // Publishing all nine circles blocks the staging space needed to
            // escape the first wave; expose only the next resolving group.
            var rings = hazards.Where(h => h.Until > now && (h.Action == 48656 || h.Action >= 48771 && h.Action <= 48775 || h.Action == 48720 || h.Action == 48721 || h.Action == 48718)).ToArray();
            var first = rings.OrderBy(h => h.Until).FirstOrDefault();
            return hazards.Where(h => h.Until > now && (!rings.Contains(h) || h.Until <= first.Until.AddMilliseconds(250)))
                .Concat(orbs.Where(h => h.Actor != collectingOrb)).Concat(pitchPuddles).Concat(sludgeFields)
                .Concat(iceFields).Concat(poisonFields).Concat(poisonClouds).Concat(cauterize.Take(1)).ToArray();
        }

        /// <inheritdoc/>
        public override Task<bool> RunAsync()
        {
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Core.Me.Location) < 90).ToArray();
            var boss = actors.FirstOrDefault(a => Bosses.Contains(a.BaseId) && a.IsAlive);
            if (WorldManager.ZoneId != 1342 || !Core.Me.IsAlive || boss == null)
            {
                Reset();
                return Task.FromResult(false);
            }
            if (encounter != boss.BaseId)
            {
                Reset();
                encounter = boss.BaseId;
                center = encounter == 0x4CD8 ? new V2(920, -420) : Round ? new V2(120, -420) : new V2(120, 0);
                if (Round)
                {
                    foreach (var wedge in ThirdBoardHazard.Donut(Extent, 70))
                    {
                        hazards.Add(new ThirdBoardHazard { Origin = center, Points = wedge, Until = DateTime.MaxValue, Persistent = true });
                    }
                }
            }
            var now = DateTime.UtcNow;
            UpdatePitchPuddles();
            UpdateSludgeFields();
            UpdatePoisonFields();
            UpdatePoisonClouds(actors, now);
            // Ice object 0x1E972A persists after helper 48708 finishes casting.
            // Visibility owns its lifetime; the nine-yalm radius includes an
            // additional half-yalm clearance.
            iceFields = encounter != 0x4CC0 ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects
                .Where(a => a.BaseId == 0x1E972A && a.IsVisible && V2.Distance(Point(a.Location), center) < 35)
                .Select(a => new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Origin = Point(a.Location),
                    Points = ThirdBoardHazard.Circle(9.5f),
                    Persistent = true,
                    Until = DateTime.MaxValue
                }).ToArray();
            if (now > cauterizeUntil)
            {
                cauterize.Clear();
            }

            cauterize.RemoveAll(h => h.Until <= now);
            hazards.RemoveAll(h => h.Until <= now);
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
                    Capture(actor, action, now);
                }
            }
            var present = actors.Select(a => a.ObjectId).ToHashSet();
            foreach (var id in casts.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                casts.Remove(id);
            }

            UpdateSweep(boss, now);
            UpdateOrbs(actors);
            // Optional melee positioning must not pull while familiar setup is
            // running. The combat routine owns preparation and engagement.
            if (!Core.Me.InCombat && !boss.InCombat)
            {
                Release();
                return Task.FromResult(false);
            }
            Position(boss, actors, now);
            // Arrival must not starve heals, pet orders or stationary attacks.
            return Task.FromResult(false);
        }

        private void Capture(BattleCharacter actor, uint action, DateTime now)
        {
            var cast = actor.SpellCastInfo;
            var until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(350);
            // Observed effects outlasted the 350 ms fence: Icy
            // Torment by 96ms and Rustling Breeze by 131ms. Keep these hazards
            // through 700ms after cast end instead of reopening melee early.
            if (action == 48697 || action == 48699 || action >= 48778 && action <= 48780)
            {
                until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(700);
            }

            var origin = Point(actor.Location);
            V2[] shape = null;
            if (actor.BaseId == 0x4CD8 && action == 48807)
            {
                breathHeading = actor.Heading;
                // Breath backsteps after its2.7s choreography cast. Retain the
                // side shelter until the next boss mechanic, with a bounded
                // recovery fence if the actor never publishes the follow-up.
                breathUntil = now.AddSeconds(20);
                destination = null;
            }
            else if (actor.BaseId == 0x4CD8 && action != 48808)
            {
                breathUntil = default;
            }

            if (actor.BaseId == 0x4CD8 && action == 48809)
            {
                // Four puddles land roughly 3.2 seconds apart after the cast.
                // New visible puddles invalidate the
                // bait point; do not run continuously and waste safe floor.
                vomitUntil = now + cast.RemainingCastTime + TimeSpan.FromSeconds(12);
                destination = null;
            }
            if (action == 48700)
            {
                cauterize.Clear();
                touchdownOrigin = null;
                // Four previews precede four actual cleaves4.3s apart. Keep
                // the next preview reserved until its matching damage helper;
                // this fence only recovers from an interrupted/missing sequence.
                cauterizeUntil = now.AddSeconds(40);
            }
            if (action == 48701)
            {
                cauterize.Add(new ThirdBoardHazard
                {
                    Action = action,
                    Origin = origin,
                    Heading = actor.Heading,
                    Points = ThirdBoardHazard.Rectangle(12, 46.5f),
                    Until = cauterizeUntil
                });
                return;
            }
            if (action == 48704)
            {
                var preview = cauterize.FirstOrDefault(h => V2.Distance(h.Origin, origin) < 1);
                if (preview != null)
                {
                    preview.Until = until;
                }
            }
            if (action == 48702)
            {
                touchdownOrigin = origin;
                touchdownUntil = cauterizeUntil;
            }
            if (action == 48706)
            {
                touchdownUntil = until.AddSeconds(1);
            }
            // Acid Rain's helper starts well after the parent cast ends (9.5s
            // in the capture). Keep a movement owner across that gap;
            // a subsequent boss mechanic ends it. Thirty seconds is a bounded
            // recovery fence, not an asserted duration of the uncaptured tail.
            if (actor.BaseId == 0x4CBD && action == 48692)
            {
                rainUntil = now.AddSeconds(30);
            }
            else if (actor.BaseId == 0x4CBD && action != 48693 && action != 48694)
            {
                rainUntil = default;
            }
            // Dimensions are Global7.56 action EffectRange/XAxisModified,
            // corroborated with captured helpers. Add0.5y clearance per edge.
            switch (action)
            {
                case 48656:
                    // The observed hit at 12:56:47.388 followed the cast end by
                    // about 526ms. Retain the old floor until700ms after cast end;
                    // the generic350ms pad released it before damage resolved.
                    until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(700);
                    shape = ThirdBoardHazard.Circle(10.5f);
                    break;
                case 48665:
                    shape = ThirdBoardHazard.Circle(15.5f);
                    break;
                case 48693:
                case 48712:
                case 48715:
                case 48729:
                case 48812:
                case 48819:
                    origin = Point(cast.CastLocation);
                    shape = ThirdBoardHazard.Circle(action == 48712 ? 5.5f : 6.5f);
                    break;
                case 48697:
                case 48699:
                    shape = ThirdBoardHazard.Rectangle(8, 100.5f);
                    break;
                case 48704:
                    shape = ThirdBoardHazard.Rectangle(12, 46.5f);
                    break;
                // Global48709 has EffectRange100, no omen. It is roomwide;
                // retain mitigation rather than trying to outrun it into a wall.
                case 48708:
                    // Manual capture publishes floor objects about 1.3s after
                    // the helper ends. Bridge that object-publication gap.
                    until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(1500);
                    shape = ThirdBoardHazard.Circle(9.5f);
                    break;
                case 48720:
                    shape = ThirdBoardHazard.Circle(13.5f);
                    break;
                case 48721:
                    Ring(actor, action, origin, 12.5f, 30.5f, until);
                    break;
                case 48718:
                    shape = ThirdBoardHazard.Cone(60.5f, 91);
                    break;
                case 48727:
                    shape = ThirdBoardHazard.Rectangle(4, 60.5f);
                    break;
                case 48771:
                    shape = ThirdBoardHazard.Circle(12.5f);
                    break;
                case 48772:
                case 48773:
                case 48774:
                case 48775:
                    float outer = 18 + 6 * (action - 48772);
                    Ring(actor, action, origin, outer - 6.5f, outer + .5f, until);
                    break;
                case 48778:
                    shape = ThirdBoardHazard.Cone(60.5f, 46);
                    break;
                case 48779:
                case 48780:
                    shape = ThirdBoardHazard.Cone(60.5f, 76);
                    break;
                case 48783:
                    shape = ThirdBoardHazard.Circle(5.5f);
                    break;
                case 48785:
                    shape = ThirdBoardHazard.Rectangle(2.5f, 40.5f);
                    break;
                case 48691:
                    shape = ThirdBoardHazard.Rectangle(6.5f, 45.5f);
                    break;
                case 48814:
                    shape = ThirdBoardHazard.Rectangle(10.5f, 48.5f);
                    break;
                case 48657:
                case 48658:
                case 48659:
                    semanticAction = action;
                    semanticUntil = until;
                    destination = null;
                    break;
                case 48683:
                    // Bind 2518 lasts two seconds after the cast before the
                    // pull and follow-up Devour. Retain shelter through that
                    // delay plus 700 ms; cast completion alone is too early.
                    semanticAction = action;
                    semanticUntil = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(2700);
                    briarSheltering = true;
                    briarExiting = false;
                    destination = null;
                    break;
                case 48717:
                    // The cast only stretches the tether. Cleaves48718 are
                    // instant, so a cast-bar subscription cannot detect them.
                    // Keep pursuit suppressed until the charge landing is seen.
                    semanticAction = action;
                    semanticUntil = until;
                    sweepCastEnd = now + cast.RemainingCastTime;
                    sweepWatchUntil = sweepCastEnd.AddSeconds(4);
                    sweepStart = origin;
                    sweepSample = origin;
                    sweepLandingAt = sweepStableSince = default;
                    watchingSweep = true;
                    destination = null;
                    break;
            }
            if (shape != null && V2.Distance(origin, center) < 65)
            {
                hazards.Add(new ThirdBoardHazard { Actor = actor.ObjectId, Action = action, Origin = origin, Heading = actor.Heading, Points = shape, Until = until });
            }

            // Per-cast telemetry belongs to the shared diagnostic collector.
            // Encounter logs below describe state changes and recovery failures.
        }

        private void Ring(BattleCharacter actor, uint action, V2 origin, float inner, float outer, DateTime until)
        {
            foreach (var wedge in ThirdBoardHazard.Donut(inner, outer))
            {
                hazards.Add(new ThirdBoardHazard { Actor = actor.ObjectId, Action = action, Origin = origin, Points = wedge, Until = until });
            }
        }

        private void Position(BattleCharacter boss, BattleCharacter[] actors, DateTime now)
        {
            var player = Point(Core.Me.Location);
            var active = Published().ToArray();
            bool Safe(V2 p) => InFloor(p) && active.All(h => !h.Contains(p));
            if (encounter == 0x4CD8 && now < breathUntil)
            {
                // Stage behind the centered boss before its backstep, then
                // beside its landing. No guessed cone angle is
                // published: select the safest available side by projection
                // against the captured facing, strictly within the inset floor.
                var origin = Point(boss.Location);
                var forward = ThirdBoardGeometry.Direction(breathHeading);
                bool staging = V2.Distance(origin, center) < 12;
                float Score(V2 p) => staging ? V2.Dot(p - center, forward) :
                    V2.Dot(V2.Normalize(p - origin), forward);
                var candidates = Candidates().Where(p => Safe(p) && V2.Distance(p, origin) > 2)
                    .OrderBy(Score).ThenBy(p => V2.Distance(p, player)).ToArray();
                if (candidates.Length > 0)
                {
                    var best = candidates[0];
                    if (!destination.HasValue || !Safe(destination.Value) || Score(destination.Value) > Score(best) + .1f)
                    {
                        destination = best;
                    }

                    Hold(destination.Value);
                    return;
                }
            }
            if (encounter == 0x4CD8 && now < vomitUntil)
            {
                // Bait near the interior rim and move only when the actual
                // puddle appears. Native registered fields own emergency egress;
                // the positive destination keeps pursuit from returning to it.
                if (!destination.HasValue || !Safe(destination.Value))
                {
                    destination = Candidates().Where(p => Safe(p) && V2.Distance(p, center) >= 15)
                        .OrderBy(p => V2.Distance(p, player)).Select(p => (V2?)p).FirstOrDefault();
                }

                if (destination.HasValue)
                {
                    Hold(destination.Value);
                    return;
                }
            }
            // Ripple escape can repeatedly relinquish movement at the boundary.
            // Hold a buffered refuge through each impact, then let the
            // next published wave invalidate it. The same owner handles the two
            // Sweeping halves; never publish opposite half-rooms simultaneously.
            bool gargoyleWave = encounter == 0x4CC2 && active.Any(h => h.Action == 48720 || h.Action == 48721 || h.Action == 48718);
            bool plummetWave = encounter == 0x4CB6 && active.Any(h => h.Action == 48656);
            if (encounter == 0x4CC0 || encounter == 0x4CC2 && (gargoyleWave || watchingSweep || now < semanticUntil && semanticAction == 48717))
            {
                PositionLocally(boss, player, active, now);
                return;
            }
            if (localMovement)
            {
                Release();
            }

            if (gargoyleWave || plummetWave)
            {
                collectingOrb = 0;
                bool Refuge(V2 p) => Safe(p) && Enumerable.Range(0, 8).All(i =>
                    Safe(p + ThirdBoardGeometry.Direction(i * MathF.PI / 4) * .8f));
                // Prefer reachable melee only after checking all queued waves,
                // including the still-hidden opposite Sweeping half. The native
                // escape completes first; a safe route then permits closing in.
                var pending = hazards.Where(h => h.Until > now).Concat(pitchPuddles)
                    .Concat(orbs).Concat(sludgeFields).ToArray();
                // Plummet's legal staging point may sit in a later circle.
                // Hold it through the current impact, then invalidate against
                // the next wave. DPS preference cannot reorder these crossings.
                if (!plummetWave && boss.IsTargetable && !AvoidanceManager.IsRunningOutOfAvoid)
                {
                    var melee = ThirdBoardGeometry.PreferMeleeRefuge(destination, player, Point(boss.Location),
                        boss.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range - .5f,
                        Candidates(), p => Refuge(p) && pending.All(h => !h.Contains(p)),
                        (from, to) => ClearSegment(from, to, pending));
                    if (melee.HasValue)
                    {
                        destination = melee;
                    }
                }
                if (!destination.HasValue || !Refuge(destination.Value))
                {
                    destination = Refuge(player) ? player : Candidates().Where(Refuge)
                        .OrderBy(p => V2.Distance(p, player)).Select(p => (V2?)p).FirstOrDefault();
                }
                // Emergency native escape retains movement ownership. Even if
                // all sampled refuges are blocked, do not hand pursuit back.
                Hold(destination ?? player);
                return;
            }
            if (watchingSweep && now >= sweepCastEnd)
            {
                // Do not charge back at the old boss position in the interval
                // between the tether cast ending and the observed relocation.
                Hold(player);
                return;
            }
            var objects = GameObjectManager.GameObjects.Where(a => a.IsVisible && a.Distance2D(Core.Me.Location) < 60).ToArray();
            if (encounter == 0x4CBD && (briarSheltering || briarExiting))
            {
                var patches = objects.Where(a => a.BaseId == 0x1EC0E2).Select(a => Point(a.Location)).ToArray();
                if (briarSheltering && (now < semanticUntil || Core.Me.HasAura(2518)))
                {
                    var bee = actors.FirstOrDefault(a => a.BaseId == 0x4CBF && a.IsAlive);
                    var shelter = CorpseFlowerPositioning.Shelter(patches, Point(boss.Location), player, Safe,
                        p => bee == null ? 0 : Alignment(p, Point(boss.Location), Point(bee.Location)));
                    if (shelter.HasValue)
                    {
                        destination = shelter;
                        Hold(shelter.Value);
                    }
                    else
                    {
                        Hold(player); // Missing safe shelter cannot authorize walking into suction.
                    }

                    return;
                }
                if (briarSheltering)
                {
                    briarSheltering = false;
                    briarExiting = true;
                    destination = null;
                }
                // Briar 5176 is present in the captured shelter and has no useful
                // expiry timer. Leave until the status clears, not for a guessed
                // patch lifetime; native emergency avoidance retains priority.
                if (briarExiting && Core.Me.HasAura(5176))
                {
                    var exit = destination.HasValue && Safe(destination.Value) &&
                        V2.Distance(player, destination.Value) > .5f && ClearSegment(player, destination.Value, active)
                        ? destination : CorpseFlowerPositioning.Exit(Candidates(), patches, Point(boss.Location), player,
                            Safe, (from, to) => ClearSegment(from, to, active));
                    if (exit.HasValue)
                    {
                        destination = exit;
                        Hold(exit.Value);
                    }
                    else
                    {
                        Hold(player);
                        if (now >= nextLog)
                        {
                            nextLog = now.AddSeconds(3);
                            ff14bot.Helpers.Logging.Write("[CrucibleMaster] Briar exit has no safe outward leg; retaining native avoidance.");
                        }
                    }
                    return;
                }
                briarExiting = false;
                Release();
            }
            V2? goal = null;
            if (now < semanticUntil)
            {
                // Strix requires air for Quakes, kappa for Floods and slime for
                // Mallet. Match pad identities because their positions shuffle.
                uint pad = semanticAction == 48657 ? 0x1E9582u : semanticAction == 48658 ? 0x1EC0E1u : semanticAction == 48659 ? 0x1EC0E0u : 0;
                if (pad != 0)
                {
                    goal = objects.Where(a => a.BaseId == pad).Select(a => (V2?)Point(a.Location)).FirstOrDefault();
                }
                else if (semanticAction == 48717)
                {
                    goal = destination.HasValue && Safe(destination.Value) ? destination : Candidates().Where(Safe).OrderByDescending(p => V2.Distance(p, Point(boss.Location))).Select(p => (V2?)p).FirstOrDefault();
                }
            }
            // Retain a waypoint until reached or invalidated; choosing a fresh
            // nearest point each pulse made earlier boards oscillate. Bait along
            // the interior ring and let the graph handle published circles.
            if (!goal.HasValue && now < rainUntil)
            {
                if (destination.HasValue && Safe(destination.Value) && V2.Distance(player, destination.Value) > 1)
                {
                    goal = destination;
                }
                else
                {
                    float angle = MathF.Atan2(player.X - center.X, player.Y - center.Y);
                    goal = Enumerable.Range(1, 12)
                        .Select(i => center + ThirdBoardGeometry.Direction(angle + i * MathF.PI / 12) * 14)
                        .Where(p => V2.Distance(player, p) >= 3 && Safe(p) && ClearSegment(player, p, active))
                        .OrderBy(CrucibleMeleePreference.Capture())
                        .Select(p => (V2?)p).FirstOrDefault();
                }
            }
            // Native avoids reserve every orb except the single selected one.
            // Selection persists until a stack/actor receipt, preventing another
            // collection while the game is still publishing the prior effect.
            if (!goal.HasValue && encounter == 0x4CC2 && now >= semanticUntil)
            {
                if (collectingOrb != 0)
                {
                    goal = orbs.Where(h => h.Actor == collectingOrb).Select(h => (V2?)h.Origin).FirstOrDefault();
                }
                else if (DreadStacks() < 4)
                {
                    var meleePreference = CrucibleMeleePreference.Capture();
                    var selected = orbs.OrderBy(h => meleePreference(h.Origin)).ThenBy(h => V2.Distance(h.Origin, player)).FirstOrDefault(h =>
                    {
                        var others = active.Where(a => a.Actor != h.Actor).ToArray();
                        return InFloor(h.Origin) && others.All(a => !a.Contains(h.Origin)) && ClearSegment(player, h.Origin, others);
                    });
                    if (selected != null)
                    {
                        collectingOrb = selected.Actor;
                        collectionStacks = DreadStacks();
                        goal = selected.Origin;
                        active = Published().ToArray();
                    }
                }
            }
            if (!InFloor(player) && !goal.HasValue)
            {
                goal = center;
            }

            if (!goal.HasValue && now >= semanticUntil && !watchingSweep && now >= rainUntil &&
                (active.Any(h => h.Action != 0) || sludgeFields.Length > 0))
            {
                // All Master encounters share this post-escape preference,
                // including persistent Pitch/Sludge. Required pads, briars,
                // tether retreats, orb collection and moving rain win above.
                var melee = CrucibleMeleePreference.ReachableMelee(destination.HasValue ? World(destination.Value) : (V3?)null,
                    p => Safe(Point(p)) && Enumerable.Range(0, 8).All(i =>
                        Safe(Point(p) + ThirdBoardGeometry.Direction(i * MathF.PI / 4) * .8f)));
                if (melee.HasValue)
                {
                    goal = Point(melee.Value);
                }
            }
            if (goal.HasValue && Safe(goal.Value))
            {
                destination = goal;
                Hold(goal.Value);
            }
            else
            {
                Release();
                if (now < semanticUntil && now >= nextLog)
                {
                    nextLog = now.AddSeconds(3);
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] No safe semantic target action={0}; native dodge retained.", semanticAction);
                }
            }
        }

        private static int DreadStacks() => Core.Me.CharacterAuras.Where(a => a.Id == 5178)
            .Select(a => (int)a.Value).DefaultIfEmpty(0).Max();

        private void PositionLocally(BattleCharacter boss, V2 player, ThirdBoardHazard[] active, DateTime now)
        {
            bool Safe(V2 p) => InFloor(p) && active.All(h => !h.Contains(p));
            // The native escape has repeatedly failed its start SpanRef here.
            // One owner now plans AND executes the bounded route. Sweeping uses
            // the same owner through retreat/landing/front/rear, preventing the
            // former native-escape/MoveTo handoff from oscillating each pulse.
            if (!localMovement && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.Stop();
            }

            localMovement = true;
            CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Master bounded route");
            CapabilityManager.Update(positioning, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Master bounded route");
            leased = true;
            if (!previousFacing.HasValue)
            {
                previousFacing = GameSettingsManager.FaceTargetOnAction;
            }

            GameSettingsManager.FaceTargetOnAction = false;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                return;
            }

            if (!InFloor(player))
            {
                // Knockback may start us outside. Recover straight inward to
                // the closest inset point; never follow the perimeter or issue
                // a graph detour while already taking boundary damage.
                var inside = center + new V2(Math.Clamp(player.X - center.X, -Extent + 1, Extent - 1),
                    Math.Clamp(player.Y - center.Y, -Extent + 1, Extent - 1));
                Navigator.PlayerMover.MoveTowards(World(inside));
                moving = true;
                destination = null;
                localRoute = Array.Empty<V2>();
                return;
            }
            bool retreat = watchingSweep && now < sweepCastEnd;
            bool ripple = active.Any(h => h.Action == 48720 || h.Action == 48721);
            // Sweeping can begin before Ripple's final donut resolves. Its
            // temporary inner refuge is not a valid stretched-tether goal.
            // Invalidate exactly once when that overlap releases, not per tick.
            if (retreat && retreatWasBlocked && !ripple)
            {
                destination = null;
                localRoute = Array.Empty<V2>();
                nextRouteAttempt = default;
            }
            retreatWasBlocked = retreat && ripple;
            bool landing = watchingSweep && now >= sweepCastEnd;
            if (landing)
            {
                if (moving)
                {
                    MovementManager.MoveStop();
                }
                moving = false;
                return;
            }
            bool touchdown = touchdownOrigin.HasValue && now < touchdownUntil && cauterize.Count == 0;
            bool LandingSafe(V2 p)
            {
                if (!touchdown)
                {
                    return true;
                }

                var delta = p - touchdownOrigin.Value;
                if (delta.Length() < 2)
                {
                    return false;
                }
                // Captured knockback displacement was 27.46 yalms. Check a
                // 25–30-yalm envelope to allow for one-second sampling error.
                var ray = V2.Normalize(delta);
                return Enumerable.Range(25, 6).All(d => Safe(p + ray * d));
            }
            var target = Core.Me.CurrentTarget as BattleCharacter ?? boss;
            float reach = target.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range - .5f;
            bool closing = !retreat && !touchdown && active.Length == 0 && target.IsTargetable && V2.Distance(player, Point(target.Location)) > reach;
            if (closing && destination.HasValue && V2.Distance(destination.Value, Point(target.Location)) > reach)
            {
                destination = null;
            }

            if (!destination.HasValue || !Safe(destination.Value) || !LandingSafe(destination.Value))
            {
                localRoute = Array.Empty<V2>();
                if (now < nextRouteAttempt)
                {
                    if (moving)
                    {
                        MovementManager.MoveStop();
                    }
                    moving = false;
                    return;
                }
                var choices = Candidates().Where(p => Safe(p) && LandingSafe(p));
                if (closing)
                {
                    choices = choices.Where(p => V2.Distance(p, Point(target.Location)) <= reach);
                }

                choices = retreat ? choices.OrderByDescending(p => V2.Distance(p, Point(boss.Location))) :
                    choices.OrderBy(p => target.IsTargetable && V2.Distance(p, Point(target.Location)) <= reach ? 0 : 1)
                        .ThenBy(p => V2.Distance(p, player));
                destination = null;
                localRoute = MasterArenaMovement.RouteToAny(player, choices, center, InFloor, active, now);
                if (localRoute.Length > 0)
                {
                    destination = localRoute[localRoute.Length - 1];
                }
                // No-path retries are throttled, but an already chosen route is
                // still checked every pulse as new ice/baited hazards arrive.
                nextRouteAttempt = destination.HasValue ? default : now.AddMilliseconds(250);
                if (!destination.HasValue && now >= nextLog)
                {
                    nextLog = now.AddSeconds(3);
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] No bounded route; holding instead of crossing boundary. player={0} hazards={1}", player, active.Length);
                }
            }
            while (localRoute.Length > 0 && V2.Distance(player, localRoute[0]) < .3f)
            {
                localRoute = localRoute.Skip(1).ToArray();
            }

            if (localRoute.Length > 0 && !MasterArenaMovement.Corridor(player, localRoute[0], InFloor, active, now))
            {
                destination = null;
                localRoute = Array.Empty<V2>();
            }
            if (localRoute.Length > 0)
            {
                Navigator.PlayerMover.MoveTowards(World(localRoute[0]));
                moving = true;
            }
            else if (moving)
            {
                MovementManager.MoveStop();
                moving = false;
            }
        }

        private void UpdateSweep(BattleCharacter boss, DateTime now)
        {
            if (!watchingSweep)
            {
                return;
            }

            if (encounter != 0x4CC2 || now > sweepWatchUntil)
            {
                watchingSweep = false;
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sweeping landing not observed; prediction cancelled.");
                return;
            }
            if (now < sweepCastEnd)
            {
                return;
            }

            var location = Point(boss.Location);
            // A stretched tether produces a large relocation. Confirm a stable
            // landing rather than forecasting from an interpolated midpoint.
            // This fallback uses public actor snapshots, not guessed offsets or
            // a fictitious cast receipt. Missing relocation times out explicitly.
            if (V2.Distance(location, sweepStart) < 6)
            {
                return;
            }

            if (sweepLandingAt == default)
            {
                sweepLandingAt = now;
            }

            if (sweepStableSince == default || V2.Distance(location, sweepSample) > .25f)
            {
                sweepSample = location;
                sweepStableSince = now;
                return;
            }
            if ((now - sweepStableSince).TotalMilliseconds < 100)
            {
                return;
            }

            watchingSweep = false;
            retreatWasBlocked = false;
            semanticUntil = default;
            destination = null;
            // Landing 50932 precedes the front 180° at 2.8s and rear 180° at 4.8s.
            // Snapshot heading once so target tracking cannot rotate either
            // forecast. A150ms effect fence leaves time for the two-second swap.
            foreach (int half in new[] { 0, 1 })
            {
                hazards.Add(new ThirdBoardHazard
                {
                    Actor = boss.ObjectId,
                    Action = 48718,
                    Origin = location,
                    Heading = boss.Heading + half * MathF.PI,
                    Points = ThirdBoardHazard.Cone(60.5f, 91),
                    Until = sweepLandingAt.AddSeconds(2.95 + 2 * half)
                });
            }

            ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sweeping landing={0} heading={1:F3}; front={2:O} rear={3:O}.",
                location, boss.Heading, sweepLandingAt.AddSeconds(2.8), sweepLandingAt.AddSeconds(4.8));
        }

        private void UpdateSludgeFields()
        {
            // Treant's ground object 0x1EABFA applies Sludge 3071/3072 and
            // persists independently of casts. Its 10-yalm radius plus
            // half-yalm clearance leaves the boss's outer
            // melee reach usable; never replace it with a timed cast-circle avoid
            // or move into it after Arboreal Storm's first ring clears.
            // As with Pitch, RB lacks EventState: visibility/removal is our
            // conservative lifetime signal, scoped to this encounter only.
            var next = encounter != 0x4CCD ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects
                .Where(a => a.BaseId == 0x1EABFA && a.IsVisible && V2.Distance(Point(a.Location), center) < 35)
                .Select(a => new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Origin = Point(a.Location),
                    Points = ThirdBoardHazard.Circle(10.5f),
                    Until = DateTime.MaxValue,
                    Persistent = true
                }).ToArray();
            foreach (var added in next.Where(p => !sludgeFields.Any(old => old.Actor == p.Actor)))
            {
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sludge field added actor={0:X} origin={1} radius=10.5.", added.Actor, added.Origin);
            }

            foreach (var removed in sludgeFields.Where(p => !next.Any(current => current.Actor == p.Actor)))
            {
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sludge field removed actor={0:X}.", removed.Actor);
            }

            sludgeFields = next;
        }

        private void UpdatePoisonClouds(BattleCharacter[] actors, DateTime now)
        {
            // Poison Clouds (0x4CDA, NPC 14630) are visible, untargetable contact
            // hazards without a cast. Track native body radius plus half-yalm
            // clearance and 350 ms of measured travel on every bot pulse.
            // Do not extrapolate spawn heading: the capture's first headings
            // change before motion stabilizes. Visibility/removal owns lifetime.
            var clouds = encounter == 0x4CD8 ? actors.Where(a => a.BaseId == 0x4CDA && a.IsVisible).ToArray() : Array.Empty<BattleCharacter>();
            poisonClouds = clouds.Select(a =>
            {
                V2 point = Point(a.Location);
                float travel = 0;
                if (cloudSamples.TryGetValue(a.ObjectId, out var prior))
                {
                    double seconds = (now - prior.Time).TotalSeconds;
                    if (seconds >= .01 && seconds <= 1)
                    {
                        travel = Math.Min(6, V2.Distance(point, prior.Point) / (float)seconds) * .35f;
                    }
                }
                cloudSamples[a.ObjectId] = (point, now);
                // The 6y/s cap bounds interpolation/spawn corrections; observed
                // cloud motion was about 2y/s. No player/rotation movement is owned here.
                return new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Origin = point,
                    Points = ThirdBoardHazard.Circle(Math.Max(0, a.CombatReach) + .5f + travel),
                    Until = DateTime.MaxValue,
                    Persistent = true
                };
            }).ToArray();
            foreach (var id in cloudSamples.Keys.Where(id => !clouds.Any(a => a.ObjectId == id)).ToArray())
            {
                cloudSamples.Remove(id);
            }
        }

        private void UpdatePoisonFields()
        {
            // Toxic Vomit leaves visible objects 0x1EB704. Actions 48809/48810
            // have a six-yalm effect range; add half-yalm clearance and use
            // visibility rather than a guessed puddle lifetime.
            poisonFields = encounter != 0x4CD8 ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects
                .Where(a => a.BaseId == 0x1EB704 && a.IsVisible && V2.Distance(Point(a.Location), center) < 35)
                .Select(a => new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Origin = Point(a.Location),
                    Points = ThirdBoardHazard.Circle(6.5f),
                    Until = DateTime.MaxValue,
                    Persistent = true
                }).ToArray();
        }

        private void UpdatePitchPuddles()
        {
            // Sea of Pitch48729 leaves a separate six-yalm ground object 1E963D.
            // Re-entry after the cast fence still applies Hysteria, so the cast
            // deadline cannot describe the puddle's lifetime.
            // Keep an obstacle for every visible object rather than guessing a
            // timeout. RB exposes visibility but not the native EventState here;
            // a visible inactive object remains conservatively blocked until
            // hidden/removed. Validate that removal behavior in the next capture.
            var next = encounter != 0x4CC2 ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects
                // A puddle centered at the rim still overlaps walkable floor;
                // observation bounds must not use the inset destination bounds.
                .Where(a => a.BaseId == 0x1E963D && a.IsVisible && V2.Distance(Point(a.Location), center) < 35)
                .Select(a => new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Action = 48729,
                    Origin = Point(a.Location),
                    Points = ThirdBoardHazard.Circle(6.5f),
                    Until = DateTime.MaxValue,
                    Persistent = true
                }).ToArray();
            foreach (var added in next.Where(p => !pitchPuddles.Any(old => old.Actor == p.Actor)))
            {
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sea of Pitch puddle added actor={0:X} origin={1}.", added.Actor, added.Origin);
            }

            foreach (var removed in pitchPuddles.Where(p => !next.Any(current => current.Actor == p.Actor)))
            {
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sea of Pitch puddle removed actor={0:X}.", removed.Actor);
            }

            pitchPuddles = next;
        }

        private void UpdateOrbs(BattleCharacter[] actors)
        {
            if (collectingOrb != 0 && DreadStacks() > collectionStacks)
            {
                collectedOrbs.Add(collectingOrb);
            }
            // Malady's visible collectible actors use4CC3. Reserve a two-yalm
            // approach margin so a path to one cannot brush an adjacent orb;
            // the exact trigger radius remains a trial-validation item.
            orbs = encounter != 0x4CC2 ? Array.Empty<ThirdBoardHazard>() : actors
                .Where(a => a.BaseId == 0x4CC3 && a.IsVisible && !a.IsCasting && a.IsAlive && !collectedOrbs.Contains(a.ObjectId))
                .Select(a => new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Origin = Point(a.Location),
                    Points = ThirdBoardHazard.Circle(2),
                    Until = DateTime.MaxValue
                }).ToArray();
            if (DreadStacks() >= 4 || DreadStacks() != collectionStacks || !orbs.Any(h => h.Actor == collectingOrb))
            {
                collectingOrb = 0;
            }
        }

        private static float Alignment(V2 p, V2 boss, V2 bee)
        {
            var ray = bee - boss;
            if (ray.LengthSquared() < 1)
            {
                return 100;
            }

            ray = V2.Normalize(ray);
            var delta = p - boss;
            return Math.Abs(delta.X * ray.Y - delta.Y * ray.X) + (V2.Dot(delta, ray) < V2.Distance(boss, bee) ? 50 : 0);
        }

        private IEnumerable<V2> Candidates()
        {
            for (int x = -22; x <= 22; x++)
            {
                for (int z = -22; z <= 22; z++)
                {
                    var p = center + new V2(x, z);
                    if (InFloor(p))
                    {
                        yield return p;
                    }
                }
            }
        }

        private bool ClearSegment(V2 from, V2 to, ThirdBoardHazard[] active)
        {
            int count = Math.Max(1, (int)Math.Ceiling(V2.Distance(from, to) * 2));
            return Enumerable.Range(0, count + 1).All(i => { var p = V2.Lerp(from, to, i / (float)count); return InFloor(p) && active.All(h => !h.Contains(p)); });
        }

        private void Hold(V2 point)
        {
            CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "First Master positioning");
            CapabilityManager.Update(positioning, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "First Master positioning");
            leased = true;
            if (!previousFacing.HasValue)
            {
                previousFacing = GameSettingsManager.FaceTargetOnAction;
            }

            GameSettingsManager.FaceTargetOnAction = false;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }
            if (V2.Distance(Point(Core.Me.Location), point) > .5f)
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(World(point), "First Master mechanic") { DistanceTolerance = .5f, UseMount = false });
                moving = true;
            }
            else if (moving)
            {
                Navigator.Stop();
                moving = false;
            }
        }

        private void Release()
        {
            if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.Stop();
            }

            if (leased)
            {
                CapabilityManager.Clear(positioning, CapabilityFlags.Movement, "Master mechanic resolved");
                CapabilityManager.Clear(positioning, CapabilityFlags.Facing, "Master mechanic resolved");
            }
            if (previousFacing.HasValue)
            {
                GameSettingsManager.FaceTargetOnAction = previousFacing.Value;
                previousFacing = null;
            }
            moving = leased = localMovement = false;
            destination = null;
            localRoute = Array.Empty<V2>();
        }

        private void Reset()
        {
            Release();
            hazards.Clear();
            casts.Clear();
            collectedOrbs.Clear();
            orbs = Array.Empty<ThirdBoardHazard>();
            pitchPuddles = Array.Empty<ThirdBoardHazard>();
            sludgeFields = Array.Empty<ThirdBoardHazard>();
            iceFields = Array.Empty<ThirdBoardHazard>();
            cauterize.Clear();
            cauterizeUntil = touchdownUntil = default;
            poisonFields = Array.Empty<ThirdBoardHazard>();
            breathUntil = vomitUntil = default;
            poisonClouds = Array.Empty<ThirdBoardHazard>();
            cloudSamples.Clear();
            touchdownOrigin = null;
            nextRouteAttempt = default;
            encounter = semanticAction = collectingOrb = 0;
            semanticUntil = rainUntil = default;
            watchingSweep = false;
            briarSheltering = briarExiting = false;
            retreatWasBlocked = false;
            sweepCastEnd = sweepWatchUntil = sweepLandingAt = sweepStableSince = default;
        }

        private void OnStopped(ff14bot.AClasses.BotBase bot) => Reset();
        /// <inheritdoc/>
        protected override Task<bool> ExitDungeonAsync()
        {
            TreeRoot.OnStop -= OnStopped;
            Reset();
            if (previousSideStep.HasValue)
            {
                SidestepPlugin.Enabled = previousSideStep.Value;
            }
            previousSideStep = null;
            return Task.FromResult(false);
        }
    }
}
