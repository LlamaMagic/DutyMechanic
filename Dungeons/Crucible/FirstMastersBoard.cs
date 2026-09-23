using LlamaLibrary.Memory;
using LlamaLibrary.Memory.Attributes;
using LlamaLibrary.Memory.PatternFinders;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Enums;
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
        private static readonly uint[] Bosses =
        {
            0x4CB6,
            0x4CBD,
            0x4CC0,
            0x4CC2,
            0x4CCD,
            0x4CD8
        };
        private readonly List<ThirdBoardHazard> hazards = new List<ThirdBoardHazard>();
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly HashSet<uint> collectedOrbs = new HashSet<uint>();
        private ThirdBoardHazard[] orbs = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] pitchPuddles = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] sludgeFields = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] iceFields = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] poisonFields = Array.Empty<ThirdBoardHazard>();
        private ThirdBoardHazard[] poisonClouds = Array.Empty<ThirdBoardHazard>();
        private readonly Dictionary<uint, CloudMotionPrediction> cloudSamples = new();
        private DateTime breathUntil, vomitUntil, phlegmUntil;
        private V2? lastBreathLanding;
        private readonly BorgnyBreathPrediction breathPrediction = new();
        private readonly List<ThirdBoardHazard> cauterize = new List<ThirdBoardHazard>();
        private V2[] localRoute = Array.Empty<V2>();
        private bool localMovement;
        private bool emergencyEgress;
        private DateTime cauterizeUntil;
        private V2? touchdownOrigin;
        private DateTime touchdownUntil;
        private DateTime nextRouteAttempt;
        private DateTime nextMeleeReview;
        private uint collectingOrb;
        private int collectionStacks;
        private DateTime nextOrbSelection;
        private readonly CorpseFlowerRain rain = new();
        private V2 rainHeading;
        private DateTime rainPrepAt, rainPrepUntil, nextRainSteer;
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
        // Keep each caster independently: Strix can have several live Plumes.
        // Fallout's early forecast is replaced by the actual knockback cast.
        private readonly List<(uint Actor, uint Action, V2 Origin, float Distance, bool Wall, DateTime At)> knockbacks = new();
        private readonly Dictionary<uint, uint> responses = new();
        private DateTime acornUntil;
        private bool acornMarked;
        private uint lastBaitIcon;
        private readonly HashSet<uint> vomitEffects = new();
        /// <inheritdoc/>
        public override ZoneId ZoneId => (ZoneId)1342;
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToFollowDodge { get; } = new HashSet<uint>();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToTankBust { get; } = new HashSet<uint>
        {
            48730,
            48731,
            48822
        };
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToMitigate { get; } = new HashSet<uint>
        {
            48669,
            48709,
            48723,
            48725,
            50696
        };

        private static V2 Point(V3 p) => new V2(p.X, p.Z);
        private static V3 World(V2 p) => new V3(p.X, 0, p.Y);
        private bool Active() => WorldManager.ZoneId == 1342 && Core.Me.IsAlive && encounter != 0;
        private bool Round => encounter == 0x4CB6 || encounter == 0x4CBD || encounter == 0x4CCD || encounter == 0x4CD8;
        // Cauterize's attack extends beyond the floor. Boundary damage starts
        // before Z21.25; keep destinations inside the shared 40-yalm arena.
        private float Extent => 18.75f;

        private bool InFloor(V2 p) => Round ? V2.Distance(p, center) <= Extent : Math.Abs(p.X - center.X) <= Extent && Math.Abs(p.Y - center.Y) <= Extent;
        /// <inheritdoc/>
        protected override Task<bool> EnterDungeonAsync()
        {
            Reset();
            BorgnyCastReader.Initialize();
            previousSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            TreeRoot.OnStop += OnStopped;
            AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && !Round && !localMovement, 37.5f, 37.5f, 140, 140, () => new[] { World(center) });
            AvoidanceManager.AddAvoidPolygon<ThirdBoardHazard>(() => Active() && !localMovement, null, 80, h => -h.Heading, h => 1, h => 15, h => h.Points.Select(p => new Clio.Utilities.Vector2(p.X, p.Y)).ToArray(), h => World(h.Origin), Published, priority: AvoidancePriority.High);
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
            // Sheet of Ice and Overdue also resolve in waves. Publishing later
            // impacts too early blocks the first wave's refuge. Persistent ice
            // stays active; only timed impacts advance to the next group.
            var rings = hazards.Where(h => h.Until > now && (h.Action == 48656 || h.Action == 48665 || h.Action == 48712 || h.Action >= 48771 && h.Action <= 48775 || h.Action == 48720 || h.Action == 48721 || h.Action == 48718)).ToArray();
            var first = rings.OrderBy(h => h.Until).FirstOrDefault();
            return hazards.Where(h => h.Until > now && (!rings.Contains(h) || h.Until <= first.Until.AddMilliseconds(250))).Concat(orbs.Where(h => h.Actor != collectingOrb)).Concat(pitchPuddles).Concat(sludgeFields).Concat(iceFields).Concat(poisonFields).Concat(poisonClouds).Concat(cauterize.Take(1)).Concat(RainFields(now)).ToArray();
        }

        private IEnumerable<ThirdBoardHazard> RainFields(DateTime now)
        {
            if (encounter != 0x4CBD || !rain.Active(now) && !(now >= rainPrepAt && now < rainPrepUntil))
                yield break;
            // This forecast also supplies native emergency escape if the local
            // continuous route has no safe leg. It is never a second movement owner.
            if (rain.Active(now))
                yield return new ThirdBoardHazard
                {
                    Action = 48694,
                    Origin = rain.Predict(Point(Core.Me.Location)),
                    Points = ThirdBoardHazard.Circle(CorpseFlowerRain.Radius),
                    Until = rain.NextAt.AddMilliseconds(400)
                };
            // Fully grown briars reach10y. During rain they slow escape; shelter
            // inversion remains exclusive to Floral Trap, outside this sequence.
            foreach (var patch in GameObjectManager.GameObjects.Where(a => a.BaseId == 0x1EC0E2 && a.IsVisible && V2.Distance(Point(a.Location), center) < 35))
                yield return new ThirdBoardHazard
                {
                    Action = 48687,
                    Actor = patch.ObjectId,
                    Origin = Point(patch.Location),
                    Points = ThirdBoardHazard.Circle(10.5f),
                    Until = now.AddSeconds(1)
                };
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
            ObserveEffects(actors, now);
            rain.Update(Point(Core.Me.Location), now);
            knockbacks.RemoveAll(k => now > k.At.AddMilliseconds(700) || k.Action == 48667 && !actors.Any(a => a.ObjectId == k.Actor && a.IsAlive));
            UpdatePitchPuddles();
            UpdateSludgeFields();
            UpdatePoisonFields();
            UpdatePoisonClouds(actors, now);
            // Ice object 0x1E972A persists after helper 48708 finishes casting.
            // Visibility owns its lifetime; the nine-yalm radius includes an
            // additional half-yalm clearance.
            iceFields = encounter != 0x4CC0 ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects.Where(a => a.BaseId == 0x1E972A && a.IsVisible && V2.Distance(Point(a.Location), center) < 35).Select(a => new ThirdBoardHazard { Actor = a.ObjectId, Origin = Point(a.Location), Points = ThirdBoardHazard.Circle(9.5f), Persistent = true, Until = DateTime.MaxValue }).ToArray();
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
            if (breathPrediction.Active)
            {
                foreach (var helper in actors.Where(a => a.BaseId == 0x233C))
                {
                    var response = BorgnyCastReader.Response(helper);
                    breathPrediction.Observe(response.Action, response.Sequence);
                }

                if (!breathPrediction.Active)
                {
                    breathUntil = default;
                    destination = null;
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] Toxic Breath complete: six distinct effect responses.");
                }
            }

            UpdateOrbs(actors);
            // Acorn Bomb is a player spread, not a self-centered escape circle.
            // With one player there is nothing to spread from; pets are not raid
            // members and must not drag the master away from melee unnecessarily.
            bool marked = encounter == 0x4CCD && (uint)Core.Me.Icon == 712;
            if (marked && !acornMarked)
                acornUntil = now.AddSeconds(5.8);
            acornMarked = marked;
            uint icon = (uint)Core.Me.Icon;
            if (encounter == 0x4CD8 && icon != lastBaitIcon)
            {
                // Target markers precede the baited helper casts. Start moving
                // on the marker rather than waiting for the first puddle.
                if (icon == 171)
                {
                    vomitUntil = now.AddSeconds(16);
                    vomitEffects.Clear();
                    destination = null;
                }

                if (icon == 669)
                {
                    phlegmUntil = now.AddSeconds(8);
                    destination = null;
                }
            }

            lastBaitIcon = icon;
            // Optional melee positioning must not pull while familiar setup is
            // running. The combat routine owns preparation and engagement.
            if (!Core.Me.InCombat && !boss.InCombat)
            {
                Release();
                return Task.FromResult(false);
            }

            Position(boss, actors, now);
            ReleaseStationaryFacing();
            // Arrival must not starve heals, pet orders or stationary attacks.
            return Task.FromResult(false);
        }

        // RB exposes the latest response, not an event stream. Baseline every
        // new actor and accept only a changed sequence; retain bounded timers
        // when polling misses an effect. Never replay historical hits on resume.
        private void ObserveEffects(BattleCharacter[] actors, DateTime now)
        {
            foreach (var actor in actors)
            {
                var effect = BorgnyCastReader.Response(actor);
                bool known = responses.TryGetValue(actor.ObjectId, out uint old);
                responses[actor.ObjectId] = effect.Sequence;
                if (!known || effect.Sequence == 0 || effect.Sequence == old)
                    continue;
                if (encounter == 0x4CBD && actor.BaseId == 0x233C && rain.Observe(effect.Action, effect.Sequence, Point(actor.Location), Point(Core.Me.Location), now))
                    ff14bot.Helpers.Logging.Write("[CrucibleRain] Impact {0}/8 action={1} sequence={2} helper={3} player={4}; next={5:O}.", rain.Impacts, effect.Action, effect.Sequence, Point(actor.Location), Point(Core.Me.Location), rain.NextAt);
                // Action 50543 has appeared just before Touchdown, but its shape
                // is unconfirmed. Record fresh receipts without adding an avoid.
                if (encounter == 0x4CD8 && effect.Action == 50543)
                {
                    var cloud = actors.Where(a => a.BaseId == 0x4CDA && a.IsVisible).OrderBy(a => V2.Distance(Point(a.Location), Point(Core.Me.Location))).FirstOrDefault();
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] Unknown50543 actor={0:X} base={1:X} source={2} player={3} cloud={4} cloudDistance={5:F2} destination={6} knockbackPending={7}.", actor.ObjectId, actor.BaseId, Point(actor.Location), Point(Core.Me.Location), cloud == null ? "none" : Point(cloud.Location).ToString(), cloud == null ? -1 : V2.Distance(Point(cloud.Location), Point(Core.Me.Location)), destination, knockbacks.Count > 0);
                }

                if (effect.Action == 48667 || effect.Action == 48816 || effect.Action == 48725)
                    knockbacks.RemoveAll(k => k.Action == effect.Action && (k.Actor == actor.ObjectId || effect.Action == 48725));
                if (effect.Action == 48781)
                    acornUntil = default;
                if (effect.Action == 48716 && actor.BaseId == 0x4CC3)
                    collectedOrbs.Add(actor.ObjectId); // Exact burst owner, not the currently selected distant orb.
                if (encounter == 0x4CD8 && now < vomitUntil && (effect.Action == 48809 || effect.Action == 48810) && vomitEffects.Add(effect.Sequence) && vomitEffects.Count >= 4)
                    vomitUntil = default;
                if (effect.Action == 48660 && semanticAction == 48659)
                    semanticUntil = default;
                if (effect.Action == 50932 && watchingSweep)
                {
                    // Actor position still needs its existing interpolation
                    // fence, but time the cones from the actual relocation.
                    sweepLandingAt = now;
                }

                if (effect.Action == 48718)
                {
                    var wave = hazards.Where(h => h.Action == 48718).OrderBy(h => h.Until).FirstOrDefault();
                    if (wave != null && wave.Until <= now.AddSeconds(1))
                        hazards.RemoveAll(h => h.Action == 48718 && h.Until == wave.Until);
                }
                else if (effect.Action == 48656 || effect.Action == 48665 || effect.Action == 48720 || effect.Action == 48721 || effect.Action >= 48771 && effect.Action <= 48775)
                {
                    hazards.RemoveAll(h => h.Action == effect.Action && h.Actor == actor.ObjectId && h.Until <= now.AddSeconds(1));
                }
            }

            var present = actors.Select(a => a.ObjectId).ToHashSet();
            foreach (uint id in responses.Keys.Where(id => !present.Contains(id)).ToArray())
                responses.Remove(id);
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
            if (action == 48667 || action == 48816 || action == 48725 || action == 50696)
            {
                uint resolved = action == 50696 ? 48725u : action;
                if (resolved == 48725)
                    knockbacks.RemoveAll(k => k.Action == resolved);
                else
                    knockbacks.RemoveAll(k => k.Actor == actor.ObjectId && k.Action == resolved);
                // Fallout announces four raidwide hits before its20y push. Its
                // actual three-second cast supersedes the11.8s early forecast.
                // Sept22 Touchdown retained the earlier Phlegm bait in its
                // CastLocation. These self-origin pushes belong to the caster;
                // proximity validation cannot make a stale target authoritative.
                var pushOrigin = origin;
                knockbacks.Add((actor.ObjectId, resolved, pushOrigin, resolved == 48667 ? 25 : resolved == 48816 ? 30 : 20, resolved == 48816, action == 50696 ? now.AddSeconds(11.8) : now + cast.RemainingCastTime));
            }

            if (action == 50518)
                acornUntil = until.AddMilliseconds(450);
            V2[] shape = null;
            if (actor.BaseId == 0x4CD8 && action == 48807)
            {
                // Breath backsteps after its2.7s choreography cast. Retain the
                // side shelter until the next boss mechanic, with a bounded
                // recovery fence if the actor never publishes the follow-up.
                breathUntil = now.AddSeconds(20);
                // The cast rotation fixes the landing direction; actor heading can change.
                // Seed existing helper responses so a previous Breath cannot
                // count toward this cast. Read snapshots only on the bot thread.
                breathPrediction.Reset();
                var rotation = BorgnyCastReader.Rotation(actor);
                if (rotation.HasValue)
                {
                    var baseline = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(a => a.BaseId == 0x233C).Select(a => BorgnyCastReader.Response(a).Sequence).ToArray();
                    breathPrediction.Start(center, rotation.Value, baseline);
                    lastBreathLanding = breathPrediction.Origin;
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] Breath castRotation={0:F3} actorHeading={1:F3} predictedLanding={2} castEnds={3:O}.", rotation.Value, actor.Heading, breathPrediction.Origin, now + cast.RemainingCastTime);
                }

                destination = null;
            }
            else if (actor.BaseId == 0x4CD8 && action != 48808)
            {
                breathUntil = default;
                breathPrediction.Reset();
            }

            if (actor.BaseId == 0x4CD8 && action == 48809)
            {
                // Four puddles land roughly 3.2 seconds apart after the cast.
                // New visible puddles invalidate the
                // bait point; do not run continuously and waste safe floor.
                vomitUntil = now + cast.RemainingCastTime + TimeSpan.FromSeconds(12);
                vomitEffects.Clear();
                destination = null;
            }

            if (actor.BaseId == 0x4CD8 && action == 48817)
            {
                // Bait away from persistent Toxic Vomit. The helper snapshots
                // the player's position before the parent's cast completes.
                phlegmUntil = until;
                destination = null;
            }

            if (action == 48819)
                phlegmUntil = default;
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
                cauterize.Add(new ThirdBoardHazard { Action = action, Origin = origin, Heading = actor.Heading, Points = ThirdBoardHazard.Rectangle(12, 46.5f), Until = cauterizeUntil });
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

            // The parent precedes the first damaging helper by9.4s in four
            // Sept23 captures. Use the last five seconds to bait near the inset
            // rim, leaving room for gradual turns; do not lap the arena early. The
            // helper owns a placed first hit followed by seven instant pursuits;
            // Rotten Stench starting must not discard their remaining lifetime.
            if (actor.BaseId == 0x4CBD && action == 48692)
            {
                rainPrepAt = now.AddSeconds(4.4);
                rainPrepUntil = now.AddSeconds(15); // Bounded recovery if the helper is never published.
            }

            if (encounter == 0x4CBD && actor.BaseId == 0x233C && action == 48693)
            {
                rain.Start(Point(cast.CastLocation), now + cast.RemainingCastTime);
                var radial = Point(Core.Me.Location) - center;
                rainHeading = new V2(-radial.Y, radial.X);
                rainPrepUntil = nextRainSteer = default;
                destination = null;
                ff14bot.Helpers.Logging.Write("[CrucibleRain] First bait={0}; impact={1:O}; continuous escape owns the eight-hit sequence.", Point(cast.CastLocation), rain.NextAt);
                return; // RainFields owns both the first circle and later forecasts.
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
                case 48712:
                case 48715:
                case 48729:
                case 48812:
                case 48819:
                    origin = Point(cast.CastLocation);
                    shape = ThirdBoardHazard.Circle(action == 48712 ? 5.5f : 6.5f);
                    break;
                case 48828:
                    // Touchdown is centered on its landing helper, not the
                    // player's bait target. Keep half a yalm beyond its6y blast.
                    shape = ThirdBoardHazard.Circle(6.5f);
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
                    shape = MasterArenaMovement.SweepingSlash();
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
                float heading = actor.Heading;
                if (action >= 48778 && action <= 48780)
                {
                    // Both side helpers reported actor heading6.283 in the
                    // Sept22 hit. Packet cast rotation owns their distinct cones.
                    heading = BorgnyCastReader.Rotation(actor) ?? heading;
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] Rustling action={0} castHeading={1:F3} actorHeading={2:F3} reader={3}.", action, heading, actor.Heading, BorgnyCastReader.Ready);
                }

                hazards.Add(new ThirdBoardHazard { Actor = actor.ObjectId, Action = action, Origin = origin, Heading = heading, Points = shape, Until = until });
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
            // The bounded Borgny planner checks InFloor on every edge. Its64
            // anonymous boundary wedges duplicate that same constraint and
            // dominate search time during cloud overlaps; retain them in native
            // publication, but not in this encounter's bounded search input.
            if (encounter == 0x4CD8)
                active = active.Where(h => h.Actor != 0 || h.Action != 0).ToArray();
            bool Safe(V2 p) => InFloor(p) && active.All(h => !h.Contains(p));
            if (encounter == 0x4CBD && rain.Active(now))
            {
                PositionRain(player, active, now);
                return;
            }

            if (encounter == 0x4CBD && now >= rainPrepAt && now < rainPrepUntil)
            {
                // A radius17 bait leaves1.75y of wall clearance and a long,
                // tangential escape. Actual rain geometry still validates every
                // following leg rather than committing to an unconditional lap.
                var bait = destination.HasValue && V2.Distance(destination.Value, center) >= 16.5f && Safe(destination.Value) ? destination : Enumerable.Range(0, 48).Select(i => center + ThirdBoardGeometry.Direction(i * MathF.PI / 24) * 17).Where(p => Safe(p) && ClearSegment(player, p, active)).OrderBy(p => V2.Distance(p, player)).Select(p => (V2? )p).FirstOrDefault();
                if (bait.HasValue)
                {
                    destination = bait;
                    Hold(bait.Value);
                }

                return;
            }

            // Landing safety takes precedence over optional melee, orb collection
            // and bait preferences. The same planner still checks every current
            // hazard on the approach; no independent movement owner is added.
            var pushes = knockbacks.Where(k => now >= k.At.AddSeconds(-4) && now <= k.At.AddMilliseconds(700)).ToArray();
            if (pushes.Length > 0)
            {
                // InFloor already represents the boundary. Do not sample its64
                // duplicate wedges at every quarter-yalm of every candidate.
                var landingFields = active.Where(h => (h.Persistent || h.Until == DateTime.MaxValue) && (h.Actor != 0 || h.Action != 0)).ToArray();
                // A Touchdown pulse used to eagerly score all4,421 floor points
                // before the existing route could be checked. Keep goals lazy
                // for Borgny: a valid destination costs one displacement check,
                // and a direct replacement stops the search at its first match.
                // Cache only within this pulse; moving clouds must be rechecked
                // from fresh geometry on the next pulse, never on a timer.
                var landingCache = new Dictionary<V2, bool>();
                bool Landing(V2 p)
                {
                    if (!landingCache.TryGetValue(p, out bool safe))
                        landingCache[p] = safe = pushes.All(k => MasterArenaMovement.KnockbackSafe(p, k.Origin, k.Distance, k.Wall, InFloor, landingFields));
                    return safe;
                }

                var goals = Candidates().Where(Safe)// Rank the endpoint cheaply, then validate the entire push
                // once. Valid interior paths still precede valid wall stops;
                // a clear endpoint never licenses crossing a poison field.
                .OrderBy(p => pushes.All(k => MasterArenaMovement.KnockbackEndsInside(p, k.Origin, k.Distance, InFloor)) ? 0 : 1).ThenBy(p => encounter == 0x4CD8 && V2.Distance(p, center) >= 6 && V2.Distance(p, center) <= 12 ? 0 : 1).ThenBy(CrucibleMeleePreference.Capture()).ThenBy(p => V2.Distance(p, player)).Where(Landing);
                PositionLocally(boss, player, active, now, encounter == 0x4CD8 ? goals : goals.ToArray(), Landing);
                return;
            }

            if (encounter == 0x4CCD && now < acornUntil)
            {
                foreach (var other in actors.Where(a => a.Type == GameObjectType.Pc && a.ObjectId != Core.Me.ObjectId && a.IsAlive))
                    active = active.Append(new ThirdBoardHazard { Origin = Point(other.Location), Points = ThirdBoardHazard.Circle(3.5f), Actor = other.ObjectId, Action = 48781, Persistent = true, Until = acornUntil }).ToArray();
                if (active.Any(h => h.Action == 48781))
                {
                    PositionLocally(boss, player, active, now);
                    return;
                }
            }

            // Breath alone owns the bounded Borgny route. Restore native
            // puddle/cloud avoidance before a later semantic branch can return.
            bool movingCloudPhase = encounter == 0x4CD8 && (poisonClouds.Length != 0 || active.Any(h => h.Action == 48812));
            if (encounter == 0x4CD8 && localMovement && now >= breathUntil && now >= phlegmUntil && now >= vomitUntil && !movingCloudPhase)
                Release();
            if (encounter == 0x4CD8 && now < breathUntil)
            {
                // Freeze the packet-derived forecast through the jump and all
                // six pulses. Interpolated actor positions/facing must not rotate
                // the refuge midway through movement. Preserve our established
                // cone clearance and apply it to every bounded route edge.
                var observed = Point(boss.Location);
                if (!breathPrediction.Active && V2.Distance(observed, center) < 12)
                {
                    // Unsupported native state: do not fabricate a pre-jump
                    // destination from actor heading. Existing field avoidance
                    // remains available until an actual rim position is observed.
                    PositionLocally(boss, player, active, now);
                    return;
                }

                var origin = breathPrediction.Active ? breathPrediction.Origin : observed;
                var forward = breathPrediction.Active ? breathPrediction.Forward : V2.Normalize(center - observed);
                active = active.Append(MasterArenaMovement.BreathCone(origin, forward)).ToArray();
                var candidates = Candidates().Where(p => Safe(p) && V2.Distance(p, origin) > 2).OrderBy(p => V2.Distance(p, player)).ToArray();
                var goals = candidates.OrderBy(p => V2.Distance(p, origin) <= boss.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range - .5f ? 0 : 1).ThenBy(p => V2.Distance(p, player));
                PositionLocally(boss, player, active, now, goals);
                return;
            }

            if (encounter == 0x4CD8 && now < phlegmUntil)
            {
                var puddles = poisonFields.Select(h => h.Origin).ToArray();
                var goals = Candidates().Where(Safe).OrderByDescending(p => MasterArenaMovement.PuddleClearance(p, puddles)).ThenBy(p => V2.Distance(p, player)).ToArray();
                // Try greatest clearance first, retaining reachable fallbacks.
                // Once selected, keep the safe bait stable until the helper
                // snapshots it instead of chasing tiny per-pulse improvements.
                PositionLocally(boss, player, active, now, goals);
                return;
            }

            if (encounter == 0x4CD8 && now < vomitUntil)
            {
                // Sept22 native escape and Hold produced repeated cross-arena
                // detours as each bait landed. One bounded owner takes the
                // shortest safe rim step, retaining the endpoint until occupied.
                PositionLocally(boss, player, active, now, Candidates().Where(p => Safe(p) && V2.Distance(p, center) >= 15)// Sept22's next Breath reused the east landing, whose flank
                // had been filled by our first Vomit. Prefer other rim floor
                // so the known refuge stays available; this is a preference,
                // never permission to cross a hazard or predict the next jump.
                // Thirteen yalms reserves two buffered6.5y puddle radii.
                .OrderBy(p => lastBreathLanding.HasValue && V2.Distance(p, lastBreathLanding.Value) < 13 ? 1 : 0).ThenBy(p => V2.Distance(p, player)).ToArray());
                return;
            }

            if (movingCloudPhase)
            {
                // Sept22 113276 died during Fuming on both attempts. Ending the
                // bait timer handed moving clouds back to native avoidance,
                // despite the same arena's repeated path/steering failures.
                // Keep one owner until live clouds and their spawning casts end;
                // higher-priority Breath/Phlegm/Vomit goals above still win.
                PositionLocally(boss, player, active, now);
                return;
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

            if (encounter == 0x4CC2 && now >= semanticUntil && DreadStacks() < 4 && orbs.Length > 0)
            {
                if (collectingOrb == 0 && now >= nextOrbSelection)
                {
                    nextOrbSelection = now.AddMilliseconds(500);
                    foreach (var orb in orbs.OrderBy(h => V2.Distance(h.Origin, player)))
                    {
                        var others = active.Where(h => h.Actor != orb.Actor).ToArray();
                        // A blocked straight segment is not an unreachable orb.
                        // Retain all other orb avoids and test a bounded detour.
                        if (MasterArenaMovement.Route(player, orb.Origin, center, InFloor, others, now).Length == 0)
                            continue;
                        collectingOrb = orb.Actor;
                        collectionStacks = DreadStacks();
                        destination = null;
                        ff14bot.Helpers.Logging.Write("[CrucibleMaster] Collect orb={0:X} remaining={1} stacks={2}.", collectingOrb, orbs.Length, collectionStacks);
                        break;
                    }
                }

                var selected = orbs.FirstOrDefault(h => h.Actor == collectingOrb);
                if (selected != null)
                {
                    var approach = active.Where(h => h.Actor != collectingOrb).ToArray();
                    // Orb goals are optional while Desolation resolves. The
                    // previous single-goal search held inside the line when the
                    // selected orb was unsafe; dodge first, then resume collection.
                    if (!Safe(player) || approach.Any(h => h.Contains(selected.Origin)))
                        PositionLocally(boss, player, active, now);
                    else
                        PositionLocally(boss, player, approach, now, new[] { selected.Origin });
                    return;
                }
            }

            if (localMovement)
            {
                Release();
            }

            if (gargoyleWave || plummetWave)
            {
                collectingOrb = 0;
                bool Refuge(V2 p) => Safe(p) && Enumerable.Range(0, 8).All(i => Safe(p + ThirdBoardGeometry.Direction(i * MathF.PI / 4) * .8f));
                // Prefer reachable melee only after checking all queued waves,
                // including the still-hidden opposite Sweeping half. The native
                // escape completes first; a safe route then permits closing in.
                var pending = hazards.Where(h => h.Until > now).Concat(pitchPuddles).Concat(orbs).Concat(sludgeFields).ToArray();
                // Plummet's legal staging point may sit in a later circle.
                // Hold it through the current impact, then invalidate against
                // the next wave. DPS preference cannot reorder these crossings.
                if (!plummetWave && boss.IsTargetable && !AvoidanceManager.IsRunningOutOfAvoid)
                {
                    var melee = ThirdBoardGeometry.PreferMeleeRefuge(destination, player, Point(boss.Location), boss.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range - .5f, Candidates(), p => Refuge(p) && pending.All(h => !h.Contains(p)), (from, to) => ClearSegment(from, to, pending));
                    if (melee.HasValue)
                    {
                        destination = melee;
                    }
                }

                if (!destination.HasValue || !Refuge(destination.Value))
                {
                    destination = Refuge(player) ? player : Candidates().Where(Refuge).OrderBy(p => V2.Distance(p, player)).Select(p => (V2? )p).FirstOrDefault();
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
                    var shelter = CorpseFlowerPositioning.Shelter(patches, Point(boss.Location), player, Safe, p => bee == null ? 0 : Alignment(p, Point(boss.Location), Point(bee.Location)));
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
                    var exit = destination.HasValue && Safe(destination.Value) && V2.Distance(player, destination.Value) > .5f && ClearSegment(player, destination.Value, active) ? destination : CorpseFlowerPositioning.Exit(Candidates(), patches, Point(boss.Location), player, Safe, (from, to) => ClearSegment(from, to, active));
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
                    // Flood/Mallet transformations persist after leaving their
                    // pad. Levitation does not: Quake must retain its refuge.
                    bool transformed = semanticAction == 48658 && Core.Me.HasAura(1134) || semanticAction == 48659 && Core.Me.HasAura(1608);
                    if (!transformed)
                        goal = objects.Where(a => a.BaseId == pad).Select(a => (V2? )Point(a.Location)).FirstOrDefault();
                    else if (leased)
                        Release();
                    if (semanticAction == 48657 && goal.HasValue)
                        goal = MasterArenaMovement.LevitationPosition(goal.Value, Point(boss.Location), boss.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range, Safe);
                }
                else if (semanticAction == 48717)
                {
                    goal = destination.HasValue && Safe(destination.Value) ? destination : Candidates().Where(Safe).OrderByDescending(p => V2.Distance(p, Point(boss.Location))).Select(p => (V2? )p).FirstOrDefault();
                }
            }

            if (!InFloor(player) && !goal.HasValue)
            {
                goal = center;
            }

            if (!goal.HasValue && now >= semanticUntil && !watchingSweep && (active.Any(h => h.Action != 0) || sludgeFields.Length > 0))
            {
                // All Master encounters share this post-escape preference,
                // including persistent Pitch/Sludge. Required pads, briars,
                // tether retreats, orb collection and moving rain win above.
                var melee = CrucibleMeleePreference.ReachableMelee(destination.HasValue ? World(destination.Value) : (V3? )null, p => Safe(Point(p)) && Enumerable.Range(0, 8).All(i => Safe(Point(p) + ThirdBoardGeometry.Direction(i * MathF.PI / 4) * .8f)));
                if (melee.HasValue)
                {
                    goal = Point(melee.Value);
                }
            }

            if (goal.HasValue && Safe(goal.Value))
            {
                destination = goal;
                // Quake's edge refuge needs a tighter arrival contract than
                // ordinary holds so its melee and pad margins remain valid.
                Hold(goal.Value, now < semanticUntil && semanticAction == 48657 ? .1f : .5f);
            }
            else
            {
                Release();
                if (now < semanticUntil && now >= nextLog && !(semanticAction == 48658 && Core.Me.HasAura(1134) || semanticAction == 48659 && Core.Me.HasAura(1608)))
                {
                    nextLog = now.AddSeconds(3);
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] No safe semantic target action={0}; native dodge retained.", semanticAction);
                }
            }
        }

        private static int DreadStacks() => Core.Me.CharacterAuras.Where(a => a.Id == 5178).Select(a => (int)a.Value).DefaultIfEmpty(0).Max();
        private void PositionRain(V2 player, ThirdBoardHazard[] active, DateTime now)
        {
            // Sept23 repeatedly lost5 stacks to an unmodelled chase; a released
            // lease also allowed Shield Charge mid-rain. Retain ownership even
            // when no route exists, while native emergency escape can run first.
            CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Acid Rain pursuit");
            leased = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }

            var staticFields = active.Where(h => h.Action != 48694 && (h.Actor != 0 || h.Action != 0)).ToArray();
            bool Clear(V2 p) => InFloor(p) && staticFields.All(h => h.Action == 48687 && h.Contains(player) ? V2.Distance(p, h.Origin) >= V2.Distance(player, h.Origin) : !h.Contains(p));
            // Execute a quarter-second leg before choosing its next turn. The
            // forecast uses that same cadence; replanning a full turn every RB
            // pulse bends the actual route much faster than the prediction.
            V2? goal = destination;
            if (now >= nextRainSteer || !goal.HasValue || V2.Distance(player, goal.Value) < .1f || !Clear(goal.Value))
            {
                goal = rain.Escape(player, rainHeading, now, Clear, CrucibleMeleePreference.Capture());
                if (!goal.HasValue && !localMovement)
                    goal = rain.Escape(player, -rainHeading, now, Clear, CrucibleMeleePreference.Capture());
                nextRainSteer = now.AddMilliseconds(250);
            }

            if (!goal.HasValue)
            {
                // A rejected leg must not become the cached destination on the
                // next pulse while the steering timer is still pending.
                destination = null;
                if (moving)
                    Navigator.Stop();
                moving = localMovement = false; // Re-enable the forecast for native escape, but never ordinary pursuit.
                if (now >= nextLog)
                {
                    nextLog = now.AddSeconds(2);
                    ff14bot.Helpers.Logging.Write("[CrucibleRain] No checked continuous leg; native forecast escape retained at {0}, impacts={1}.", player, rain.Impacts);
                }

                return;
            }

            if (!localMovement)
                Navigator.Stop();
            localMovement = true;
            rainHeading = V2.Normalize(goal.Value - player);
            destination = goal;
            OwnTravelFacing();
            // Every direct leg was checked against floor, briars and predicted
            // impacts. No explicit arrival stop or service-path chord can pause
            // the escape between these one-second instant hits.
            Navigator.PlayerMover.MoveTowards(World(goal.Value));
            moving = true;
        }

        private void PositionLocally(BattleCharacter boss, V2 player, ThirdBoardHazard[] active, DateTime now, IEnumerable<V2> requiredGoals = null, Func<V2, bool> requiredSafety = null)
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

            if (!localMovement)
            {
                // A prior semantic Hold has no bounded waypoints to inherit.
                destination = null;
                localRoute = Array.Empty<V2>();
            }

            localMovement = true;
            CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Master bounded route");
            leased = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                return;
            }

            if (!InFloor(player))
            {
                // Knockback may start us outside. Recover straight inward to
                // the closest inset point; never follow the perimeter or issue
                // a graph detour while already taking boundary damage.
                var inside = Round ? center + V2.Normalize(player - center) * (Extent - 1) : center + new V2(Math.Clamp(player.X - center.X, -Extent + 1, Extent - 1), Math.Clamp(player.Y - center.Y, -Extent + 1, Extent - 1));
                OwnTravelFacing();
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
                if (requiredSafety != null && !requiredSafety(p))
                    return false;
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
            bool closing = requiredGoals == null && !retreat && !touchdown && active.Length == 0 && target.IsTargetable && V2.Distance(player, Point(target.Location)) > reach;
            // A safe refuge is not permanent: cloud movement can reopen melee.
            // Keep the old refuge unless a complete safe replacement route exists.
            // Required baits/orbs and knockback staging are never displaced for DPS.
            if (!retreat && !touchdown && requiredSafety == null && (requiredGoals == null || encounter == 0x4CD8 && now < breathUntil) && target.IsTargetable && Safe(player) && V2.Distance(player, Point(target.Location)) > reach && localRoute.Length == 0 && now >= nextMeleeReview)
            {
                nextMeleeReview = now.AddSeconds(1);
                var meleeGoals = Candidates().Where(p => Safe(p) && V2.Distance(p, Point(target.Location)) <= reach).OrderBy(p => V2.Distance(p, player));
                var meleeRoute = MasterArenaMovement.RouteToAny(player, meleeGoals, center, InFloor, active, now);
                if (meleeRoute.Length > 0)
                {
                    localRoute = meleeRoute;
                    destination = meleeRoute[meleeRoute.Length - 1];
                    emergencyEgress = false;
                }
            }

            if (closing && destination.HasValue && V2.Distance(destination.Value, Point(target.Location)) > reach)
            {
                destination = null;
            }

            bool egressValid = emergencyEgress && destination.HasValue && MasterArenaMovement.ReducingExposure(player, destination.Value, InFloor, active);
            if (!destination.HasValue || (emergencyEgress ? !egressValid : !Safe(destination.Value)) || !LandingSafe(destination.Value) || !emergencyEgress && encounter != 0x4CD8 && requiredGoals != null && !requiredGoals.Contains(destination.Value))
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

                // Sept22 slash transitions at137.83,17.84 and102.10,-17.90
                // exhausted whole-yalm goals. Preserve the margin and boundary;
                // add fine local goals instead of declaring the pocket blocked.
                var available = encounter == 0x4CC2 && active.Any(h => h.Action == 48718) ? Candidates().Concat(MasterArenaMovement.SlashRefuges(Point(boss.Location))) : Candidates();
                var choices = (requiredGoals ?? available).Where(p => Safe(p) && LandingSafe(p));
                if (closing)
                {
                    choices = choices.Where(p => V2.Distance(p, Point(target.Location)) <= reach);
                }

                choices = requiredGoals != null ? choices : retreat ? choices.OrderByDescending(p => V2.Distance(p, Point(boss.Location))) : choices.OrderBy(p => target.IsTargetable && V2.Distance(p, Point(target.Location)) <= reach ? 0 : 1).ThenBy(p => V2.Distance(p, player));
                // During Borgny's compound phases, a blocked ideal bait/refuge
                // must not prevent reaching other safe floor. Keep preferences
                // first, and retain fallback destinations without oscillation.
                if (encounter == 0x4CD8 && requiredGoals != null)
                    choices = choices.Concat(Candidates().Where(p => Safe(p) && LandingSafe(p)).OrderBy(p => V2.Distance(p, player)));
                destination = null;
                emergencyEgress = false;
                localRoute = MasterArenaMovement.RouteToAny(player, choices, center, InFloor, active, now);
                // Try complete detours first. Only then retain a safe real
                // position that fell between grid samples; adding it to the
                // normal goal list would beat every indirect route prematurely.
                if (localRoute.Length == 0 && encounter == 0x4CD8 && Safe(player) && LandingSafe(player))
                    localRoute = new[]
                    {
                        player
                    };
                if (localRoute.Length == 0 && encounter == 0x4CD8)
                {
                    // Sept22: holding for a complete route left us taking
                    // repeated Breath ticks while clouds shifted. This fallback
                    // reduces existing exposure without entering new hazards.
                    var escape = MasterArenaMovement.EscapeStep(player, InFloor, active);
                    if (escape.HasValue)
                    {
                        localRoute = new[]
                        {
                            escape.Value
                        };
                        emergencyEgress = true;
                        if (now >= nextLog)
                        {
                            nextLog = now.AddSeconds(3);
                            ff14bot.Helpers.Logging.Write("[CrucibleMaster] Bounded exposure-reducing escape: {0} -> {1}.", player, escape.Value);
                        }
                    }
                }

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
                    // Preserve the actual blocking fields for replay. Counts
                    // alone could not distinguish stale ice from a cast overlap.
                    ff14bot.Helpers.Logging.Write("[CrucibleMaster] Route blockers: {0}", string.Join("; ", active.Where(h => h.Actor != 0 || h.Action != 0).Select(h => $"{h.Actor:X}/{h.Action}@{h.Origin},persistent={h.Persistent}")));
                }
            }

            while (localRoute.Length > 0 && V2.Distance(player, localRoute[0]) < .3f)
            {
                localRoute = localRoute.Skip(1).ToArray();
            }

            if (emergencyEgress && localRoute.Length == 0)
                destination = null;
            if (localRoute.Length > 0 && !(emergencyEgress ? MasterArenaMovement.ReducingExposure(player, localRoute[0], InFloor, active) : MasterArenaMovement.Corridor(player, localRoute[0], InFloor, active, now)))
            {
                destination = null;
                localRoute = Array.Empty<V2>();
            }

            if (localRoute.Length > 0)
            {
                OwnTravelFacing();
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
            foreach (int half in new[]
            {
                0,
                1
            }

            )
            {
                hazards.Add(new ThirdBoardHazard { Actor = boss.ObjectId, Action = 48718, Origin = location, Heading = boss.Heading + half * MathF.PI, Points = MasterArenaMovement.SweepingSlash(), Until = sweepLandingAt.AddSeconds(2.95 + 2 * half) });
            }

            ff14bot.Helpers.Logging.Write("[CrucibleMaster] Sweeping landing={0} heading={1:F3}; front={2:O} rear={3:O}.", location, boss.Heading, sweepLandingAt.AddSeconds(2.8), sweepLandingAt.AddSeconds(4.8));
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
            var next = encounter != 0x4CCD ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects.Where(a => a.BaseId == 0x1EABFA && a.IsVisible && V2.Distance(Point(a.Location), center) < 35).Select(a => new ThirdBoardHazard { Actor = a.ObjectId, Origin = Point(a.Location), Points = ThirdBoardHazard.Circle(10.5f), Until = DateTime.MaxValue, Persistent = true }).ToArray();
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
            // clearance and a one-second forward sweep of measured travel.
            // Sept22 contact reports showed that 350ms radial padding reacted
            // too late. Directional coverage gives lead time without blocking
            // equally far behind a departing cloud during overlapping mechanics.
            // Do not extrapolate spawn heading: the capture's first headings
            // change before motion stabilizes. Visibility/removal owns lifetime.
            var clouds = encounter == 0x4CD8 ? actors.Where(a => a.BaseId == 0x4CDA && a.IsVisible).ToArray() : Array.Empty<BattleCharacter>();
            poisonClouds = clouds.Select(a =>
            {
                V2 point = Point(a.Location);
                if (!cloudSamples.TryGetValue(a.ObjectId, out var motion))
                    cloudSamples[a.ObjectId] = motion = new CloudMotionPrediction();
                V2 travel = motion.Observe(point, now);
                // The 6y/s cap bounds interpolation/spawn corrections; observed
                // cloud motion was about 2y/s. No player/rotation movement is owned here.
                return new ThirdBoardHazard
                {
                    Actor = a.ObjectId,
                    Origin = point,
                    // The damaging body is at least2.5y regardless of its
                    // target hitbox; add the standard half-yalm safety margin.
                    Points = ThirdBoardHazard.SweptCircle(Math.Max(2.5f, a.CombatReach) + .5f, travel),
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
            poisonFields = encounter != 0x4CD8 ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects.Where(a => a.BaseId == 0x1EB704 && a.IsVisible && V2.Distance(Point(a.Location), center) < 35).Select(a => new ThirdBoardHazard { Actor = a.ObjectId, Origin = Point(a.Location), Points = ThirdBoardHazard.Circle(6.5f), Until = DateTime.MaxValue, Persistent = true }).ToArray();
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
            var next = encounter != 0x4CC2 ? Array.Empty<ThirdBoardHazard>() : GameObjectManager.GameObjects// A puddle centered at the rim still overlaps walkable floor;
            // observation bounds must not use the inset destination bounds.
            .Where(a => a.BaseId == 0x1E963D && a.IsVisible && V2.Distance(Point(a.Location), center) < 35).Select(a => new ThirdBoardHazard { Actor = a.ObjectId, Action = 48729, Origin = Point(a.Location), Points = ThirdBoardHazard.Circle(6.5f), Until = DateTime.MaxValue, Persistent = true }).ToArray();
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
            // A stack may come from an incidental nearby orb. Do not blacklist
            // the selected distant actor merely because another one exploded.
            var selected = orbs.FirstOrDefault(h => h.Actor == collectingOrb);
            if (selected != null && DreadStacks() > collectionStacks && V2.Distance(Point(Core.Me.Location), selected.Origin) <= 2)
            {
                collectedOrbs.Add(collectingOrb);
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Orb receipt actor={0:X} stacks={1}.", collectingOrb, DreadStacks());
            }

            // Malady's visible collectible actors use4CC3. Reserve a two-yalm
            // approach margin so a path to one cannot brush an adjacent orb;
            // the exact trigger radius remains a trial-validation item.
            orbs = encounter != 0x4CC2 ? Array.Empty<ThirdBoardHazard>() : actors.Where(a => a.BaseId == 0x4CC3 && a.IsVisible && !a.IsCasting && a.IsAlive && !collectedOrbs.Contains(a.ObjectId)).Select(a => new ThirdBoardHazard { Actor = a.ObjectId, Origin = Point(a.Location), Points = ThirdBoardHazard.Circle(2), // Orbs have no future activation window. The bounded
            // planner must never cross an unselected orb speculatively.
            Persistent = true, Until = DateTime.MaxValue }).ToArray();
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
            // Borgny's buffered cone leaves narrow lateral pockets. Half-yalm
            // samples retain those without relaxing the bleeding floor inset.
            float spacing = encounter == 0x4CD8 ? .5f : 1;
            for (float x = -22; x <= 22; x += spacing)
            {
                for (float z = -22; z <= 22; z += spacing)
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
            return Enumerable.Range(0, count + 1).All(i =>
            {
                var p = V2.Lerp(from, to, i / (float)count);
                return InFloor(p) && active.All(h => !h.Contains(p));
            });
        }

        // Only transit owns facing. Sept22 captures show stationary in-range
        // ErrorNotInFront until this lease released, across Ice, Gargoyle and
        // Treant. Keep movement held at safety while returning rotation facing.
        private void OwnTravelFacing()
        {
            CapabilityManager.Update(positioning, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Master route steering");
            if (!previousFacing.HasValue)
                previousFacing = GameSettingsManager.FaceTargetOnAction;
            GameSettingsManager.FaceTargetOnAction = false;
        }

        private void ReleaseStationaryFacing()
        {
            if (!previousFacing.HasValue || moving || MovementManager.IsMoving || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
                return;
            CapabilityManager.Clear(positioning, CapabilityFlags.Facing, "Master refuge reached; allow attacks");
            GameSettingsManager.FaceTargetOnAction = previousFacing.Value;
            previousFacing = null;
        }

        private void Hold(V2 point, float arrivalTolerance = .5f)
        {
            CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "First Master positioning");
            leased = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }

            if (V2.Distance(Point(Core.Me.Location), point) > arrivalTolerance)
            {
                OwnTravelFacing();
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(World(point), "First Master mechanic") { DistanceTolerance = arrivalTolerance, UseMount = false });
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
            emergencyEgress = false;
            destination = null;
            localRoute = Array.Empty<V2>();
        }

        private void Reset()
        {
            Release();
            hazards.Clear();
            casts.Clear();
            knockbacks.Clear();
            responses.Clear();
            acornUntil = default;
            acornMarked = false;
            lastBaitIcon = 0;
            vomitEffects.Clear();
            collectedOrbs.Clear();
            orbs = Array.Empty<ThirdBoardHazard>();
            pitchPuddles = Array.Empty<ThirdBoardHazard>();
            sludgeFields = Array.Empty<ThirdBoardHazard>();
            iceFields = Array.Empty<ThirdBoardHazard>();
            cauterize.Clear();
            cauterizeUntil = touchdownUntil = default;
            poisonFields = Array.Empty<ThirdBoardHazard>();
            breathUntil = vomitUntil = phlegmUntil = default;
            breathPrediction.Reset();
            lastBreathLanding = null;
            poisonClouds = Array.Empty<ThirdBoardHazard>();
            cloudSamples.Clear();
            touchdownOrigin = null;
            nextRouteAttempt = nextOrbSelection = default;
            nextMeleeReview = default;
            encounter = semanticAction = collectingOrb = 0;
            semanticUntil = default;
            rain.Reset();
            rainHeading = default;
            rainPrepAt = rainPrepUntil = nextRainSteer = default;
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

    // Keep the encounter-specific reader beside its sole consumer.
    // RB exposes the CastInfo pointer but not cast rotation or effect responses.
    // Resolve once at dungeon entry, never inside a movement/click operation.
    // Read-only, bot-thread access; unsupported layouts disable prediction rather
    // than silently substituting actor heading (the original wrong-side bug).
    internal static class BorgnyCastReader
    {
        private static object memory;
        private static int rotation, responseAction, responseSequence;
        internal static bool Ready { get; private set; }

        internal static void Initialize()
        {
            if (ReferenceEquals(memory, Core.Memory))
                return;
            memory = Core.Memory;
            Ready = false;
            try
            {
                if (OffsetManager.ActiveRecord.RegionFlag != OffsetFlags.Global)
                    return;
                using var finder = new GreyMagicPf();
                // HandleActorCastPacket decodes packet RotationInt and passes it
                // to the action-visual initializer. Its flag clear / rotation
                // store / action-row test is stable across Global images with
                // SHA prefixes5bbc501d,9483706d,04766591 (current/two prior).
                // Ghidra operand masks wildcard all displacements/immediates;
                // each image has exactly one match. Add11 Read32 =>0x231C.
                // TC9fb8dd46 resolves0x230C and is deliberately not supported.
                var site = Site(finder, "80 A3 ? ? ? ? ? F3 0F 11 93 ? ? ? ? F6 47 ? ?");
                rotation = Core.Memory.Read<int>(site + 11);
                // ActionEffect receive stores type/action/spell/source-sequence/
                // global-sequence/count consecutively into CastInfo. This six-
                // instruction anchor is unique on the same three Global images;
                // no TC match, CN image unavailable. Add7 Read8 =>0x48 (action),
                // Add20 Read8 =>0x4C (global sequence), validated against the
                // packet-to-state assignments, not inferred adjacent fields.
                site = Site(finder, "41 88 4B ? 41 89 53 ? 45 89 43 ? 66 45 89 63 ? 45 89 4B ? 41 88 83 ? ? ? ?");
                responseAction = Core.Memory.Read<byte>(site + 7);
                responseSequence = Core.Memory.Read<byte>(site + 20);
                Ready = rotation == 0x231C && responseAction == 0x48 && responseSequence == 0x4C;
            }
            catch (Exception error)
            {
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Cast-state layout unavailable: {0}", error.GetType().Name);
            }

            if (!Ready)
                ff14bot.Helpers.Logging.Write("[CrucibleMaster] Breath prediction unavailable; observed landing fallback only.");
        }

        private static IntPtr Site(GreyMagicPf finder, string pattern)
        {
            var matches = finder.SearchMany("Search " + pattern);
            var module = Core.Memory.Process.MainModule;
            if (matches == null || matches.Length != 1 || module == null || matches[0].ToInt64() < 0 || matches[0].ToInt64() >= module.ModuleMemorySize)
                throw new NotSupportedException("Cast-state signature changed.");
            return Core.Memory.GetAbsolute(matches[0]);
        }

        internal static float? Rotation(BattleCharacter actor)
        {
            if (!Ready || !actor.IsValid || !actor.IsCasting)
                return null;
            float value = Core.Memory.Read<float>(actor.Pointer + rotation);
            return float.IsFinite(value) && Math.Abs(value) <= MathF.PI * 2 ? value : null;
        }

        internal static (uint Action, uint Sequence) Response(BattleCharacter actor)
        {
            if (!Ready || !actor.IsValid)
                return default;
            // Use RB's maintained CastInfo owner, avoiding a duplicated
            // BattleChara member offset or a native function call.
            var pointer = actor.SpellCastInfo.Pointer;
            return pointer == IntPtr.Zero ? default : (Core.Memory.Read<uint>(pointer + responseAction), Core.Memory.Read<uint>(pointer + responseSequence));
        }
    }
}
