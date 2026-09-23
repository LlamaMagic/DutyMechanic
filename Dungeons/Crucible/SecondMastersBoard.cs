using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.Navigation;
using V2 = System.Numerics.Vector2;
using V3 = Clio.Utilities.Vector3;

namespace DutyMechanic.Dungeons
{
    /// <summary>
    /// Handles the selected Second Master's Board route. Native avoidance
    /// owns damaging geometry; encounter positioning owns required refuges and
    /// facing. The routine retains combat and ordinary approach actions.
    /// </summary>
    public sealed partial class SecondMastersBoard : AbstractDungeon
    {
        private readonly List<ThirdBoardHazard> hazards = new();
        private readonly Dictionary<uint, uint> casts = new();
        private bool active;
        private bool? previousSideStep;
        private uint encounter;
        private readonly CapabilityManagerHandle feeding = CapabilityManager.CreateNewHandle();
        private bool feedingMoving;
        private bool feedingHeld;
        private bool feedingFacingHeld;
        private uint feedingTarget;
        private DateTime nextFeedingRoute;
        private DateTime nextDrakeSprint;
        private readonly HashSet<uint> drakeTornadoes = new();
        private readonly CapabilityManagerHandle drakeEdge = CapabilityManager.CreateNewHandle();
        private bool drakeEdgeHeld, drakeEdgeMoving;
        private V2[] feedingRoute = Array.Empty<V2>();
        private readonly CapabilityManagerHandle charge = CapabilityManager.CreateNewHandle();
        private bool chargeHeld, chargeMoving, chargeFacingHeld;
        private uint previousIcon;
        private DateTime chargeUntil, pushUntil, nextChargeRoute;
        private V2 chargeGoal, pushOrigin;
        private V2[] chargeRoute = Array.Empty<V2>();
        private readonly CapabilityManagerHandle squareEscape = CapabilityManager.CreateNewHandle();
        private bool escapeHeld, escapeMoving;
        private DateTime nextEscapeRoute;
        private DateTime nextEscapeSprint;
        private V2[] escapeRoute = Array.Empty<V2>();
        private readonly ConcurrentQueue<int> sphinxPrompts = new();
        private readonly CapabilityManagerHandle riddle = CapabilityManager.CreateNewHandle();
        private bool riddleHeld, riddleFacingHeld, riddleMoving, answerReady;
        private uint answerSpecies, numericStatus;
        private DateTime answerUntil, nextRiddleRoute, nextAnswer;
        private V2[] riddleRoute = Array.Empty<V2>();
        private IntPtr forcedDirection;
        private readonly CapabilityManagerHandle stamp = CapabilityManager.CreateNewHandle();
        private readonly HashSet<uint> slimeWave = new();
        private uint slimeBait;
        private bool stampLocked, stampHeld, stampMoving, stampFacingHeld;
        private DateTime nextStampRoute;
        private V2[] stampRoute = Array.Empty<V2>();
        private readonly Dictionary<V2[], Clio.Utilities.Vector2[]> nativePoints = new();
        private V2 ArenaCenter => encounter == 0x4D1E ? SecondMasterGeometry.LaudaCenter : encounter == 0x4CDB || encounter == 0x4D03 ? SecondMasterGeometry.FlaurosCenter : encounter == 0x4CF6 || encounter == 0x4CFE ? SecondMasterGeometry.DurgaCenter : SecondMasterGeometry.DrakeCenter;
        /// <inheritdoc/>
        public override ZoneId ZoneId => (ZoneId)1343;
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToTankBust { get; } = new()
        {
            49188,
            49189,
            49254,
            49470
        };
        /// <inheritdoc/>
        protected override HashSet<uint> SpellsToMitigate { get; } = new();

        /// <inheritdoc/>
        protected override Task<bool> EnterDungeonAsync()
        {
            Reset();
            TreeRoot.OnStop += OnStopped;
            GamelogManager.MessageRecevied += OnSphinxMessage;
            ResolveForcedDirection();
            // Reuse the validated cast-rotation/effect reader. Lauda's invisible
            // helper snapshots require the packet rotation, not later facing.
            BorgnyCastReader.Initialize();
            // Lauda's local planner validates the entire narrow-floor corridor.
            // Independent native escape crossed the centre cage during
            // Rush despite its contact polygon. Never run both movement owners;
            // ordinary combat regains native avoidance after the local hold ends.
            AvoidanceManager.AddAvoidPolygon<ThirdBoardHazard>(() => active && Core.Me.IsAlive && WorldManager.ZoneId == 1343 && !drakeEdgeHeld && !(encounter == 0x4CFE && Core.Me.HasAura(3909)) && !(encounter == 0x4D1E && (laudaHeld || Core.Me.HasAura(5341) || forceUntil > DateTime.UtcNow)), null, 80, h => -h.Heading, h => 1, h => 15, NativePoints, h => new V3(h.Origin.X, 0, h.Origin.Y), () => encounter == 0x4D1E ? LaudaPublished() : hazards.Where(h => h.Until > DateTime.UtcNow).ToArray(), priority: AvoidancePriority.High);
            return Task.FromResult(false);
        }

        private Clio.Utilities.Vector2[] NativePoints(ThirdBoardHazard hazard)
        {
            // The custom floor publishes many immutable polygons. Convert once,
            // never allocate a full floor mask on each avoidance-provider read.
            if (!nativePoints.TryGetValue(hazard.Points, out var points))
                nativePoints[hazard.Points] = points = hazard.Points.Select(p => new Clio.Utilities.Vector2(p.X, p.Y)).ToArray();
            return points;
        }

        /// <inheritdoc/>
        public override Task<bool> RunAsync()
        {
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Core.Me.Location) < 90).ToArray();
            uint nextEncounter = actors.Any(a => a.BaseId == 0x4CDB && a.IsAlive) ? 0x4CDBu : // Fire/tornado helpers remain dangerous between Drake's death
            // and Abaddon's arrival. Keep their owner through that gap.
            actors.Any(a => a.IsAlive && (a.BaseId == 0x4CF0 || a.BaseId == 0x4CF2 || a.BaseId == 0x4CF4 || a.BaseId == 0x4CF5) || a.IsVisible && (a.BaseId == 0x4CF1 || a.BaseId == 0x4CF3)) ? 0x4CF0u : actors.Any(a => a.IsAlive && (a.BaseId == 0x4CF6 || a.BaseId == 0x4CF8)) ? 0x4CF6u : actors.Any(a => a.IsAlive && a.BaseId == 0x4CFE) ? 0x4CFEu : actors.Any(a => a.IsAlive && a.BaseId == 0x4D03) ? 0x4D03u : actors.Any(a => a.IsAlive && a.BaseId == 0x4D1E) ? 0x4D1Eu : 0;
            if (WorldManager.ZoneId != 1343 || !Core.Me.IsAlive || nextEncounter == 0)
            {
                Reset();
                return Task.FromResult(false);
            }

            if (active && encounter != nextEncounter)
                Reset();
            if (!active)
            {
                active = true;
                encounter = nextEncounter;
                previousSideStep = SidestepPlugin.Enabled;
                // This owner combines helper geometry and required positioning.
                // Disable duplicate generic shapes only during this encounter.
                SidestepPlugin.Enabled = false;
                if (encounter == 0x4D1E)
                    hazards.AddRange(SecondMasterGeometry.LaudaBoundary());
                else
                    foreach (var shape in encounter == 0x4CDB || encounter == 0x4D03 ? ThirdBoardHazard.Donut(SecondMasterGeometry.FlaurosRadius, 70) : encounter == 0x4CF6 || encounter == 0x4CFE ? SecondMasterGeometry.DurgaBoundary() : SecondMasterGeometry.DrakeBoundary())
                        hazards.Add(new ThirdBoardHazard { Origin = ArenaCenter, Points = shape, Persistent = true, Until = DateTime.MaxValue });
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Encounter {0:X} native avoidance active.", encounter);
            }

            var now = DateTime.UtcNow;
            hazards.RemoveAll(h => h.Until <= now);
            foreach (var actor in actors)
            {
                uint action = actor.IsCasting ? actor.CastingSpellId : 0;
                if (casts.TryGetValue(actor.ObjectId, out uint old) && old == action)
                    continue;
                casts[actor.ObjectId] = action;
                if (encounter == 0x4D1E)
                    CaptureLaudaCast(actor, action, now);
                if (encounter == 0x4CFE && (action == 49348 || action == 49350))
                {
                    answerReady = false;
                    answerSpecies = 0;
                }

                if (encounter == 0x4CF6 && action == 49274)
                {
                    pushOrigin = Point(actor.Location);
                    pushUntil = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(350);
                    nextChargeRoute = default;
                }

                if (encounter == 0x4D03 && action == 49379)
                    stampLocked = true;
                if (encounter == 0x4CF0 && action == 49259)
                    // The cast supplies the definitive end time for the earlier
                    // spawn warning. Replace it rather than publishing two donuts.
                    hazards.RemoveAll(h => h.Actor == actor.ObjectId && h.Action == 49259 && h.Persistent);
                var single = encounter == 0x4D03 ? SecondMasterGeometry.GigantisShape(action) : encounter == 0x4CDB ? SecondMasterGeometry.FlaurosShape(action) : SecondMasterGeometry.DurgaShape(action);
                var shapes = (encounter == 0x4D1E ? SecondMasterGeometry.LaudaShapes(action) : encounter == 0x4CFE ? SecondMasterGeometry.SphinxShapes(action) : encounter == 0x4CF0 ? SecondMasterGeometry.DrakeShapes(action) : single == null ? Array.Empty<V2[]>() : new[]
                {
                    single
                }

                ).ToArray();
                if (shapes.Length == 0)
                    continue;
                var cast = actor.SpellCastInfo;
                // Only Heat Lightning targets the ground. Self-cast helpers and
                // sprites own their origins; a stale CastLocation is not a center.
                var source = action == 49186 ? cast.CastLocation : actor.Location;
                var origin = new V2(source.X, source.Z);
                if (V2.Distance(origin, ArenaCenter) > 65)
                    continue;
                // Line Voltage locks its packet rotation while the sprite is
                // still turning. Its effects arrive about 0.7s after the bar.
                bool voltage = action == 49194 || action == 49195;
                // Blowing Ring and Jolt hit about 0.7s after the cast bar;
                // retain their geometry through that server delay.
                bool delayedImpact = voltage || action == 49259 || action == 49265 || action == 49266;
                // Cage damage arrives about 400ms after the bar. Retain 750ms
                // clearance, then permit the later Molten escape.
                bool cageImpact = action == 49457 || action == 49460;
                // Gigantic Rage and Banish can aim opposite their helper's
                // visible facing. Use the locked cast rotation for these shapes.
                float heading = encounter == 0x4D1E || encounter == 0x4D03 || encounter == 0x4CFE || voltage ? BorgnyCastReader.Rotation(actor) ?? actor.Heading : actor.Heading;
                if (voltage)
                    ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Voltage actor={0:X} visual={1:F3} packet={2:F3} remaining={3:F2}.", actor.ObjectId, actor.Heading, heading, cast.RemainingCastTime.TotalSeconds);
                foreach (var shape in shapes)
                    hazards.Add(new ThirdBoardHazard { Actor = actor.ObjectId, Action = action, Origin = origin, Heading = heading, Points = shape, Until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(delayedImpact ? 1100 : cageImpact ? 750 : 350) });
            }

            var present = actors.Select(a => a.ObjectId).ToHashSet();
            foreach (uint id in casts.Keys.Where(id => !present.Contains(id)).ToArray())
                casts.Remove(id);
            if (encounter == 0x4CF0)
                TickDrake(actors, now);
            if (encounter == 0x4CF6)
                TickDurga(actors, now);
            if (encounter == 0x4CFE)
                TickSphinx(actors, now);
            if (encounter == 0x4D03)
                TickGigantis(actors, now);
            if (encounter == 0x4D1E)
                TickLauda(actors, now);
            // Ordinary avoids leave combat schedulable. The memory answer is
            // an interaction with an invulnerable boss and no combat target;
            // own those pulses so OrderBot's idle/POI cleanup cannot cancel
            // the last steps toward the answer.
            // Lauda's narrow floor requires local, validated transit rather
            // than an independent native escape cutting across pocket corners.
            // Stationary holds still yield for safe combat uptime.
            return Task.FromResult(drakeEdgeHeld || encounter == 0x4CFE && answerReady && riddleHeld || encounter == 0x4D1E && laudaMoving);
        }

        private void TickDrake(BattleCharacter[] actors, DateTime now)
        {
            // Actor occupancy outlives individual1s explosion bars. Replace the
            // old fireball circle when its position becomes the donut shelter.
            // Refresh only these dynamic fields; never rebuild the arena fence.
            // Preserve object identity across pulses. Replacing these fields
            // every tick made native avoidance repeatedly rediscover the same
            // fireball while escaping the first live tornado sequence.
            foreach (var h in hazards.Where(h => h.Action == 49258 || h.Action == 5465 || h.Action == 5145))
                h.Until = now;
            var tornadoes = actors.Where(a => a.BaseId == 0x4CF3 && a.IsVisible).ToArray();
            foreach (var tornado in tornadoes)
            {
                if (!drakeTornadoes.Add(tornado.ObjectId) || hazards.Any(h => h.Actor == tornado.ObjectId && h.Action == 49259))
                    continue;
                // The transformed tornado marks the shelter before its cast.
                // Waiting for the 4.7s bar left too little time for a 37y crossing,
                // even with Sprint. Expire an unconfirmed preview after 10s.
                foreach (var shape in SecondMasterGeometry.DrakeShapes(49259))
                    hazards.Add(new ThirdBoardHazard { Actor = tornado.ObjectId, Action = 49259, Origin = Point(tornado.Location), Points = shape, Persistent = true, Until = now.AddSeconds(10) });
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Tornado spawn shelter: actor={0:X} position={1}.", tornado.ObjectId, tornado.Location);
            }

            hazards.RemoveAll(h => h.Action == 49259 && h.Persistent && !tornadoes.Any(t => t.ObjectId == h.Actor));
            // A 29y crossing can consume the entire 4.7s cast. Sprint assists
            // the existing safe route; it never changes
            // refuge geometry or movement ownership. Retry a rejected request
            // at most once a second rather than flooding an animation lock.
            var shelter = tornadoes.FirstOrDefault(a => (a.IsCasting && a.CastingSpellId == 49259 || hazards.Any(h => h.Actor == a.ObjectId && h.Action == 49259 && h.Persistent)) && SecondMasterGeometry.DrakeShelterNeedsSprint(a.Distance2D(Core.Me.Location), a.IsCasting ? a.SpellCastInfo.RemainingCastTime.TotalSeconds : 5));
            var cyclone = actors.FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 49256 && SecondMasterGeometry.DrakeCycloneNeedsSprint(Point(Core.Me.Location), Point(a.Location), a.Heading, a.SpellCastInfo.RemainingCastTime.TotalSeconds));
            if (cyclone != null && now >= nextDrakeSprint && ActionManager.IsSprintReady)
            {
                nextDrakeSprint = now.AddSeconds(1);
                ActionManager.Sprint();
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Cyclone Sprint requested: remaining={0:F2}s.", cyclone.SpellCastInfo.RemainingCastTime.TotalSeconds);
            }

            if (shelter != null && now >= nextDrakeSprint && ActionManager.IsSprintReady)
            {
                nextDrakeSprint = now.AddSeconds(1);
                ActionManager.Sprint();
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Tornado Sprint requested: distance={0:F1}, remaining={1:F2}s.", shelter.Distance2D(Core.Me.Location), shelter.IsCasting ? shelter.SpellCastInfo.RemainingCastTime.TotalSeconds : 5);
            }

            foreach (var orb in actors.Where(a => a.BaseId == 0x4CF1 && a.IsVisible && !tornadoes.Any(t => t.Distance2D(a.Location) < .75f)))
                RefreshDrakeCircle(orb, 49258, 4.5f, now);
            foreach (var reflected in actors.Where(a => a.IsAlive && (a.BaseId == 0x4CF0 && a.HasAura(5465) || a.BaseId == 0x4CF2 && a.HasAura(5145))))
                // Stop player autoattacks by keeping their full reach outside
                // the reflected actor. Routine guards separately reject strikes.
                RefreshDrakeCircle(reflected, reflected.BaseId == 0x4CF0 ? 5465u : 5145u, reflected.CombatReach + Core.Me.CombatReach + 3.5f, now);
            hazards.RemoveAll(h => h.Until <= now);
            if (TickDrakeEdge(now))
            {
                ReleaseFeeding();
                return;
            }

            var abaddon = actors.FirstOrDefault(a => a.BaseId == 0x4CF4 && a.IsAlive);
            var morphos = actors.Where(a => a.BaseId == 0x4CF5 && a.IsAlive && a.IsVisible).ToArray();
            if (abaddon == null || morphos.Length == 0)
            {
                ReleaseFeeding();
                return;
            }

            // Consumption is positive positioning: bring the pursuing Abaddon
            // to a Morpho, not a damaging circle to evade. Native floor attacks
            // retain priority. The routine owns Challenge and pauses burst AoEs.
            CapabilityManager.Update(feeding, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Second Master Morpho consumption");
            feedingHeld = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                feedingMoving = false;
                feedingRoute = Array.Empty<V2>();
                return;
            }

            if (abaddon.CurrentTargetId != Core.Me.ObjectId)
            {
                StopFeedingMovement();
                return;
            }

            var target = morphos.FirstOrDefault(a => a.ObjectId == feedingTarget) ?? morphos.OrderBy(a => a.Distance2D(Core.Me.Location)).First();
            if (feedingTarget != target.ObjectId)
            {
                feedingTarget = target.ObjectId;
                nextFeedingRoute = default;
            }

            var player = Point(Core.Me.Location);
            var goal = Point(target.Location);
            if (V2.Distance(player, goal) < .65f)
            {
                StopFeedingMovement();
                return;
            }

            var pending = hazards.Where(h => h.Until > now).ToArray();
            if (now >= nextFeedingRoute)
            {
                feedingRoute = MasterArenaMovement.Route(player, goal, SecondMasterGeometry.DrakeCenter, SecondMasterGeometry.InDrakeFloor, pending, now);
                nextFeedingRoute = now.AddMilliseconds(500);
            }

            while (feedingRoute.Length > 0 && V2.Distance(player, feedingRoute[0]) < .35f)
                feedingRoute = feedingRoute.Skip(1).ToArray();
            if (feedingRoute.Length == 0)
            {
                StopFeedingMovement();
                return;
            }

            CapabilityManager.Update(feeding, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Second Master feeding transit");
            feedingFacingHeld = true;
            feedingMoving = true;
            Navigator.PlayerMover.MoveTowards(new V3(feedingRoute[0].X, 0, feedingRoute[0].Y));
        }

        private static V2 Point(V3 p) => new(p.X, p.Z);
        private bool TickDrakeEdge(DateTime now)
        {
            var goal = SecondMasterGeometry.DrakeEdgeRecovery(Point(Core.Me.Location), drakeEdgeHeld, hazards.Where(h => h.Until > now));
            if (!goal.HasValue)
            {
                ReleaseDrakeEdge();
                return false;
            }

            // This sub-yalm repair has one movement owner. Withdraw the native
            // provider and wait for its previous escape to release before input;
            // normal avoidance resumes as soon as the interior margin is reached.
            if (!drakeEdgeHeld)
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Drake inner-edge recovery: {0} -> {1}.", Point(Core.Me.Location), goal.Value);
            drakeEdgeHeld = true;
            CapabilityManager.Update(drakeEdge, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Drake inner-edge recovery");
            CapabilityManager.Update(drakeEdge, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Drake inner-edge recovery");
            if (AvoidanceManager.IsRunningOutOfAvoid)
                return true;
            drakeEdgeMoving = true;
            Navigator.PlayerMover.MoveTowards(new V3(goal.Value.X, Core.Me.Location.Y, goal.Value.Y));
            return true;
        }

        private void ReleaseDrakeEdge()
        {
            if (drakeEdgeMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            drakeEdgeMoving = false;
            if (drakeEdgeHeld)
            {
                CapabilityManager.Clear(drakeEdge, CapabilityFlags.Movement, "Drake interior recovered");
                CapabilityManager.Clear(drakeEdge, CapabilityFlags.Facing, "Drake interior recovered");
            }

            drakeEdgeHeld = false;
        }

        private void RefreshDrakeCircle(BattleCharacter actor, uint action, float radius, DateTime now)
        {
            var field = hazards.FirstOrDefault(h => h.Actor == actor.ObjectId && h.Action == action);
            if (field == null)
            {
                field = new ThirdBoardHazard
                {
                    Actor = actor.ObjectId,
                    Action = action,
                    Points = ThirdBoardHazard.Circle(radius),
                    Persistent = true
                };
                hazards.Add(field);
            }

            field.Origin = Point(actor.Location);
            field.Until = now.AddSeconds(1);
        }

        private void TickDurga(BattleCharacter[] actors, DateTime now)
        {
            uint icon = (uint)Core.Me.Icon;
            bool parent = actors.Any(a => a.BaseId == 0x4CF6 && a.IsCasting && a.CastingSpellId == 49273);
            if (icon == 23 && previousIcon != 23 || parent && chargeUntil <= now && pushUntil <= now)
            {
                // Icon23 provides the early corner bait. Cast discovery is a
                // bounded recovery when entering mid-mechanic without its icon.
                chargeGoal = SecondMasterGeometry.DurgaBaits().OrderBy(p => V2.DistanceSquared(p, Point(Core.Me.Location))).First();
                chargeUntil = now.AddSeconds(10);
                nextChargeRoute = default;
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Durga corner bait {0}.", chargeGoal);
            }

            previousIcon = icon;
            bool pushing = pushUntil > now;
            if (!pushing && pushUntil != default)
            {
                // The resolving helper ends this transaction even if the icon
                // remains published. Do not drag the player back after landing.
                ReleaseCharge();
                return;
            }

            if (!pushing && chargeUntil <= now)
            {
                ReleaseCharge();
                TickSquareEscape(actors, now);
                return;
            }

            ReleaseSquareEscape();
            CapabilityManager.Update(charge, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Durga corner/push staging");
            chargeHeld = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                chargeMoving = false;
                StopChargeMovement();
                nextChargeRoute = default;
                return;
            }

            var player = Point(Core.Me.Location);
            var fields = hazards.Where(h => h.Until > now).ToArray();
            if (now >= nextChargeRoute)
            {
                var candidates = pushing ? SecondMasterGeometry.DurgaPushStands(pushOrigin).Where(p => MasterArenaMovement.KnockbackSafe(p, pushOrigin, 40, false, SecondMasterGeometry.InDurgaFloor, fields)) : new[]
                {
                    chargeGoal
                }.AsEnumerable();
                chargeRoute = Array.Empty<V2>();
                foreach (var goal in candidates.OrderBy(p => V2.DistanceSquared(player, p)))
                {
                    if (!SecondMasterGeometry.InDurgaFloor(goal) || fields.Any(h => h.Contains(goal)))
                        continue;
                    if (V2.Distance(player, goal) < .35f)
                    {
                        chargeGoal = goal;
                        break;
                    }

                    var route = MasterArenaMovement.Route(player, goal, SecondMasterGeometry.DurgaCenter, SecondMasterGeometry.InDurgaFloor, fields, now);
                    if (route.Length == 0)
                        continue;
                    chargeGoal = goal;
                    chargeRoute = route;
                    break;
                }

                // Bound graph work: this cannot rebuild a full graph every tick
                // during a short helper cast, as earlier Borgny landings did.
                nextChargeRoute = now.AddMilliseconds(400);
            }

            while (chargeRoute.Length > 0 && V2.Distance(player, chargeRoute[0]) < .3f)
                chargeRoute = chargeRoute.Skip(1).ToArray();
            if (chargeRoute.Length == 0)
            {
                StopChargeMovement();
                return;
            }

            CapabilityManager.Update(charge, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Durga staging transit");
            chargeFacingHeld = true;
            chargeMoving = true;
            Navigator.PlayerMover.MoveTowards(new V3(chargeRoute[0].X, 0, chargeRoute[0].Y));
        }

        private void StopChargeMovement()
        {
            if (chargeMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            chargeMoving = false;
            if (chargeFacingHeld)
                CapabilityManager.Clear(charge, CapabilityFlags.Facing, "Durga staging arrived");
            chargeFacingHeld = false;
        }

        private void ReleaseCharge()
        {
            StopChargeMovement();
            if (chargeHeld)
                CapabilityManager.Clear(charge, CapabilityFlags.Movement, "Durga charge resolved");
            chargeHeld = false;
            chargeUntil = pushUntil = default;
            chargeRoute = Array.Empty<V2>();
        }

        private void TickSquareEscape(BattleCharacter[] actors, DateTime now)
        {
            // Native navigation can lack a start SpanRef during Jolt and the
            // locked missiles. Use local egress only while no native escape is
            // running; charge staging retains priority over this fallback.
            var player = Point(Core.Me.Location);
            var fields = hazards.Where(h => h.Until > now).ToArray();
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                escapeMoving = false;
                ReleaseSquareEscape();
                return;
            }

            if (!fields.Any(h => h.Contains(player)))
            {
                ReleaseSquareEscape();
                return;
            }

            // RB indexes leases by one capability, despite the flags enum.
            // Combining them throws before the fallback can move at all.
            CapabilityManager.Update(squareEscape, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Second Master square escape after missing native span");
            CapabilityManager.Update(squareEscape, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Second Master square escape after missing native span");
            escapeHeld = true;
            if (now >= nextEscapeRoute)
            {
                var boss = actors.FirstOrDefault(a => a.BaseId == encounter && a.IsAlive);
                var target = boss == null ? SecondMasterGeometry.DurgaCenter : Point(boss.Location);
                float reach = boss == null ? 0 : boss.CombatReach + Core.Me.CombatReach + 3;
                // A single reachable-component search handles every refuge;
                // full melee range is a preference after safety, not a limit.
                var candidates = SecondMasterGeometry.DurgaRefuges().OrderBy(p => Math.Max(0, V2.Distance(p, target) - reach) * 2 + V2.Distance(p, player));
                escapeRoute = MasterArenaMovement.RouteToAny(player, candidates, SecondMasterGeometry.DurgaCenter, SecondMasterGeometry.InDurgaFloor, fields, now);
                nextEscapeRoute = now.AddMilliseconds(400);
                if (escapeRoute.Length > 0)
                    ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Square {0:X} local egress toward {1}.", encounter, escapeRoute.Last());
            }

            while (escapeRoute.Length > 0 && V2.Distance(player, escapeRoute[0]) < .3f)
                escapeRoute = escapeRoute.Skip(1).ToArray();
            // A 30y Jolt escape can exceed the 4.9s cast. Budget the entire
            // routed path, not just endpoint distance, when requesting Sprint.
            double remaining = actors.Where(a => a.IsCasting && (a.CastingSpellId == 49265 || a.CastingSpellId == 49266)).Select(a => a.SpellCastInfo.RemainingCastTime.TotalSeconds).DefaultIfEmpty(0).Min();
            float length = 0;
            var from = player;
            foreach (var point in escapeRoute)
            {
                length += V2.Distance(from, point);
                from = point;
            }

            if (now >= nextEscapeSprint && SecondMasterGeometry.DurgaEscapeNeedsSprint(length, remaining) && ActionManager.IsSprintReady)
            {
                nextEscapeSprint = now.AddSeconds(1);
                ActionManager.Sprint();
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Jolt Sprint requested: route={0:F1}, remaining={1:F2}s.", length, remaining);
            }

            if (escapeRoute.Length == 0)
            {
                if (escapeMoving)
                    MovementManager.MoveStop();
                escapeMoving = false;
                return;
            }

            escapeMoving = true;
            Navigator.PlayerMover.MoveTowards(new V3(escapeRoute[0].X, 0, escapeRoute[0].Y));
        }

        private void ReleaseSquareEscape()
        {
            if (escapeMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            escapeMoving = false;
            if (escapeHeld)
            {
                CapabilityManager.Clear(squareEscape, CapabilityFlags.Movement, "Durga escape complete");
                CapabilityManager.Clear(squareEscape, CapabilityFlags.Facing, "Durga escape complete");
            }

            escapeHeld = false;
            escapeRoute = Array.Empty<V2>();
            nextEscapeRoute = default;
        }

        private void StopFeedingMovement()
        {
            if (feedingMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            feedingMoving = false;
            // Clear only leases we acquired. The first Drake capture otherwise
            // emitted two release records every tick throughout ordinary combat.
            if (feedingFacingHeld)
                CapabilityManager.Clear(feeding, CapabilityFlags.Facing, "Second Master feeding transit ended");
            feedingFacingHeld = false;
        }

        private void ReleaseFeeding()
        {
            StopFeedingMovement();
            if (feedingHeld)
                CapabilityManager.Clear(feeding, CapabilityFlags.Movement, "Second Master consumption complete");
            feedingHeld = false;
            feedingTarget = 0;
            feedingRoute = Array.Empty<V2>();
        }

        private void OnSphinxMessage(object sender, ChatEventArgs args)
        {
            // Chat delivery may be off the bot thread. Copy only the bounded
            // semantic result; actor access and interaction remain in RunAsync.
            if (args.ChatLogEntry.MessageType != MessageType.NPCSay && args.ChatLogEntry.MessageType != MessageType.NPCAnnouncements)
                return;
            int prompt = SecondMasterGeometry.SphinxPrompt(args.ChatLogEntry.Contents);
            if (prompt != 0 && sphinxPrompts.Count < 8)
                sphinxPrompts.Enqueue(prompt);
        }

        private void ResolveForcedDirection()
        {
            forcedDirection = IntPtr.Zero;
            // Reuse the existing Ghidra-verified MOVSS [RIP+disp32],XMM1 /
            // TEST RBX,RBX anchor from the misdirection handler. Only the RIP
            // displacement is dynamic; preserve its ModRM register identity.
            // Prior validation: Global Aug14/Aug7/Jul28 and CN Jul31 2026.
            // Global Sep15: one .text match RVA8A57EC, extraction points to the
            // writable direction float. Resolve once at entry, never per input.
            try
            {
                using var finder = new LlamaLibrary.Memory.PatternFinders.GreyMagicPf();
                var matches = finder.SearchMany("Search F3 0F 11 0D ? ? ? ? 48 85 DB");
                if (matches.Length != 1)
                    throw new InvalidOperationException("Forced-direction anchor is not unique.");
                var site = Core.Memory.Process.MainModule.BaseAddress + matches[0].ToInt32();
                forcedDirection = site + 8 + Core.Memory.Read<int>(site + 4);
                if (!float.IsFinite(Core.Memory.Read<float>(forcedDirection)))
                    throw new InvalidOperationException("Invalid forced direction.");
            }
            catch (Exception error)
            {
                forcedDirection = IntPtr.Zero;
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Misdirection reader unavailable: {0}", error.Message);
            }
        }

        private void TickSphinx(BattleCharacter[] actors, DateTime now)
        {
            while (sphinxPrompts.TryDequeue(out int prompt))
            {
                if (prompt == 1)
                {
                    answerReady = true;
                    // The shuffle consumes much of the original45s watchdog.
                    // Start the bounded answer window from "Make your choice".
                    answerUntil = now.AddSeconds(30);
                }
                else
                {
                    answerSpecies = (uint)prompt;
                    answerReady = false;
                    answerUntil = now.AddSeconds(45);
                }

                nextRiddleRoute = default;
                ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Sphinx prompt={0:X} answerReady={1}.", prompt, answerReady);
            }

            if (now >= answerUntil)
            {
                answerSpecies = 0;
                answerReady = false;
            }

            var assignment = Core.Me.CharacterAuras.Where(a => a.Id >= 5148 && a.Id <= 5151).OrderBy(a => a.TimespanLeft).FirstOrDefault();
            var fields = hazards.Where(h => h.Until > now).ToArray();
            var player = Point(Core.Me.Location);
            var objects = GameObjectManager.GameObjects.Where(a => a.IsVisible && a.Distance2D(Core.Me.Location) < 60).ToArray();
            IEnumerable<V2> goals = Array.Empty<V2>();
            GameObject answer = null;
            if (assignment != null)
            {
                if (numericStatus != assignment.Id)
                {
                    numericStatus = assignment.Id;
                    nextRiddleRoute = default;
                }

                // Tile centers leave6y of nominal space. Sample only its inner
                // 8x8 square so arrival tolerance cannot select a wrong number.
                var tiles = objects.Where(a => a.BaseId >= 0x1EC105 && a.BaseId <= 0x1EC10D && SecondMasterGeometry.SphinxNumber(assignment.Id, a.BaseId - 0x1EC104)).ToArray();
                bool inside = tiles.Any(a => Math.Abs(a.Location.X - player.X) < 3.8f && Math.Abs(a.Location.Z - player.Y) < 3.8f);
                goals = inside && !fields.Any(h => h.Contains(player)) ? new[]
                {
                    player
                }

                : tiles.SelectMany(a => Enumerable.Range(-3, 7).SelectMany(x => Enumerable.Range(-3, 7).Select(z => Point(a.Location) + new V2(x, z))));
            }
            else if (answerReady && answerSpecies != 0)
            {
                // BaseId survives Transfiguration. Follow the original animal
                // through every shuffle, then use only its co-located answer
                // object. Never choose a mandragora by appearance or spawn order.
                var beast = actors.FirstOrDefault(a => a.BaseId == answerSpecies);
                if (beast != null)
                    answer = objects.Where(a => a.BaseId == 0x1EC0E3 && a.IsTargetable && a.Distance2D(beast.Location) < 2).OrderBy(a => a.Distance2D(beast.Location)).FirstOrDefault();
                if (answer != null)
                    goals = new[]
                    {
                        Point(answer.Location)
                    };
            }
            else if (Core.Me.HasAura(3909))
                goals = fields.Any(h => h.Contains(player)) ? SecondMasterGeometry.DurgaRefuges() : new[]
                {
                    player
                };
            else
            {
                numericStatus = 0;
                ReleaseRiddle();
                // Same square and proven missing-span failure as Durga. Retain
                // native ownership whenever it has a running escape.
                if (!Core.Me.HasAura(3909))
                    TickSquareEscape(actors, now);
                return;
            }

            ReleaseSquareEscape();
            CapabilityManager.Update(riddle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Sphinx riddle positioning");
            riddleHeld = true;
            if (answer != null && answer.IsWithinInteractRange && !fields.Any(h => h.Contains(player)))
            {
                StopRiddleMovement();
                // Answer submission casts; let it finish before retrying.
                if (!Core.Me.IsCasting && now >= nextAnswer)
                {
                    // Use RB's actual interaction range, not a guessed radius.
                    ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Sphinx answer {0:X}, animal={1:X}, distance={2:F2}.", answer.ObjectId, answerSpecies, V2.Distance(player, Point(answer.Location)));
                    answer.Interact();
                    nextAnswer = now.AddSeconds(2);
                }

                return;
            }

            if (AvoidanceManager.IsRunningOutOfAvoid && !Core.Me.HasAura(3909))
            {
                riddleMoving = false;
                StopRiddleMovement();
                nextRiddleRoute = default;
                return;
            }

            if (now >= nextRiddleRoute)
            {
                var boss = actors.FirstOrDefault(a => a.BaseId == 0x4CFE);
                float reach = boss == null ? 0 : boss.CombatReach + Core.Me.CombatReach + 3;
                var target = boss == null ? ArenaCenter : Point(boss.Location);
                var candidates = goals.Where(p => SecondMasterGeometry.InDurgaFloor(p) && !fields.Any(h => h.Contains(p))).OrderBy(p => V2.Distance(player, p) + Math.Max(0, V2.Distance(target, p) - reach));
                riddleRoute = MasterArenaMovement.RouteToAny(player, candidates, ArenaCenter, SecondMasterGeometry.InDurgaFloor, fields, now);
                nextRiddleRoute = now.AddMilliseconds(400);
            }

            while (riddleRoute.Length > 0 && V2.Distance(player, riddleRoute[0]) < .3f)
                riddleRoute = riddleRoute.Skip(1).ToArray();
            if (riddleRoute.Length == 0)
            {
                StopRiddleMovement();
                if (answer != null && answer.IsWithinInteractRange && !Core.Me.IsCasting && now >= nextAnswer)
                {
                    answer.Interact();
                    nextAnswer = now.AddSeconds(2);
                }

                return;
            }

            CapabilityManager.Update(riddle, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Sphinx riddle transit");
            riddleFacingHeld = true;
            if (Core.Me.HasAura(3909))
            {
                // The client ignores steering during Lost Hope. Gate short
                // forward pulses on its rotating pointer and the entire near
                // segment, so an aligned destination cannot send us off-floor.
                if (forcedDirection == IntPtr.Zero)
                {
                    StopRiddleMovement();
                    return;
                }

                float direction = Core.Memory.Read<float>(forcedDirection);
                var delta = riddleRoute[0] - player;
                var forward = ThirdBoardGeometry.Direction(direction);
                float travel = Math.Min(1, delta.Length());
                bool safe = float.IsFinite(direction) && V2.Dot(V2.Normalize(delta), forward) > .94f;
                for (float step = .25f; safe && step <= travel + .25f; step += .25f)
                {
                    var p = player + forward * step;
                    safe = SecondMasterGeometry.InDurgaFloor(p) && !fields.Any(h => !h.Contains(player) && h.Contains(p));
                }

                if (!safe)
                {
                    StopRiddleMovement();
                    return;
                }

                if (!riddleMoving)
                    Navigator.PlayerMover.MoveStop();
                MovementManager.Move(MovementDirection.Forward, TimeSpan.FromMilliseconds(100));
            }
            else
                Navigator.PlayerMover.MoveTowards(new V3(riddleRoute[0].X, 0, riddleRoute[0].Y));
            riddleMoving = true;
        }

        private void StopRiddleMovement()
        {
            if (riddleMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            riddleMoving = false;
            if (riddleFacingHeld)
                CapabilityManager.Clear(riddle, CapabilityFlags.Facing, "Sphinx riddle transit ended");
            riddleFacingHeld = false;
        }

        private void ReleaseRiddle()
        {
            StopRiddleMovement();
            if (riddleHeld)
                CapabilityManager.Clear(riddle, CapabilityFlags.Movement, "Sphinx riddle resolved");
            riddleHeld = false;
            riddleRoute = Array.Empty<V2>();
        }

        private void TickGigantis(BattleCharacter[] actors, DateTime now)
        {
            var boss = actors.First(a => a.BaseId == 0x4D03 && a.IsAlive);
            var slimes = actors.Where(a => a.IsAlive && a.IsVisible && (a.BaseId == 0x4D05 || a.BaseId == 0x4D07)).ToArray();
            bool newWave = slimes.Any(a => !slimeWave.Contains(a.ObjectId));
            if (newWave)
            {
                foreach (var slime in slimes)
                    slimeWave.Add(slime.ObjectId);
                // A wave can publish its two actors on adjacent pulses. Preserve
                // an already chosen bait, but unlock staging for genuinely new adds.
                stampLocked = false;
                nextStampRoute = default;
            }

            var bait = slimes.FirstOrDefault(a => a.ObjectId == slimeBait);
            if (bait == null && (newWave || slimeBait == 0 && !stampLocked))
            {
                uint element = boss.CharacterAuras.FirstOrDefault(a => a.Id == 2056)?.Value ?? 0;
                bait = slimes.Where(a => SecondMasterGeometry.GigantisBait(a.BaseId, element)).OrderBy(a => a.Distance2D(Core.Me.Location)).FirstOrDefault();
                if (bait != null)
                {
                    slimeBait = bait.ObjectId;
                    ff14bot.Helpers.Logging.Write("[CrucibleSecondMaster] Stamp bait {0:X}/{1:X}; club={2:X}.", slimeBait, bait.BaseId, element);
                }
            }

            // Keep the chosen identity until its removal. Re-evaluating against
            // the newly enchanted club would incorrectly save the leftover slime.
            AppDomain.CurrentDomain.SetData("Crucible.Gigantis.Bait.v1", Tuple.Create(bait?.ObjectId ?? 0, now.AddSeconds(1).Ticks));
            var tower = actors.FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 49364);
            V2? goal = tower != null ? Point(tower.SpellCastInfo.CastLocation) : bait != null && !stampLocked ? Point(bait.Location) : null;
            if (!goal.HasValue)
            {
                ReleaseStamp();
                return;
            }

            var player = Point(Core.Me.Location);
            var fields = hazards.Where(h => h.Until > now).ToArray();
            CapabilityManager.Update(stamp, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Gigantis bait or tower");
            stampHeld = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                stampMoving = false;
                StopStampMovement();
                nextStampRoute = default;
                return;
            }

            // Tower radius2 leaves a comfortable inner1y arrival region. Slime
            // bait uses0.6y so the short stamp crosses the selected actor.
            float arrival = tower != null ? 1 : .6f;
            if (V2.Distance(player, goal.Value) < arrival && !fields.Any(h => h.Contains(player)))
            {
                StopStampMovement();
                return;
            }

            if (now >= nextStampRoute)
            {
                stampRoute = MasterArenaMovement.Route(player, goal.Value, ArenaCenter, SecondMasterGeometry.InGigantisFloor, fields, now);
                nextStampRoute = now.AddMilliseconds(400);
            }

            while (stampRoute.Length > 0 && V2.Distance(player, stampRoute[0]) < .3f)
                stampRoute = stampRoute.Skip(1).ToArray();
            if (stampRoute.Length == 0)
            {
                StopStampMovement();
                return;
            }

            CapabilityManager.Update(stamp, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Gigantis staging transit");
            stampFacingHeld = stampMoving = true;
            Navigator.PlayerMover.MoveTowards(new V3(stampRoute[0].X, 0, stampRoute[0].Y));
        }

        private void StopStampMovement()
        {
            if (stampMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            stampMoving = false;
            if (stampFacingHeld)
                CapabilityManager.Clear(stamp, CapabilityFlags.Facing, "Gigantis transit ended");
            stampFacingHeld = false;
        }

        private void ReleaseStamp()
        {
            StopStampMovement();
            if (stampHeld)
                CapabilityManager.Clear(stamp, CapabilityFlags.Movement, "Gigantis staging resolved");
            stampHeld = false;
            stampRoute = Array.Empty<V2>();
        }

        private void Reset()
        {
            active = false;
            encounter = 0;
            ReleaseFeeding();
            ReleaseCharge();
            ReleaseDrakeEdge();
            ReleaseSquareEscape();
            ReleaseRiddle();
            ReleaseStamp();
            ResetLauda();
            slimeWave.Clear();
            slimeBait = 0;
            stampLocked = false;
            AppDomain.CurrentDomain.SetData("Crucible.Gigantis.Bait.v1", null);
            while (sphinxPrompts.TryDequeue(out _))
            {
            }

            answerSpecies = numericStatus = 0;
            answerReady = false;
            previousIcon = 0;
            nextDrakeSprint = default;
            drakeTornadoes.Clear();
            hazards.Clear();
            nativePoints.Clear();
            casts.Clear();
            if (previousSideStep.HasValue)
                SidestepPlugin.Enabled = previousSideStep.Value;
            previousSideStep = null;
        }

        private void OnStopped(ff14bot.AClasses.BotBase bot) => Reset();
        /// <inheritdoc/>
        protected override Task<bool> ExitDungeonAsync()
        {
            TreeRoot.OnStop -= OnStopped;
            GamelogManager.MessageRecevied -= OnSphinxMessage;
            Reset();
            return Task.FromResult(false);
        }
    }
}
