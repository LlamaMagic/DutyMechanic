using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Navigation;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons
{
    // Publishes final-encounter hazards without taking ownership of bomb targeting.
    // A targetable bomb is not necessarily the bomb that should be pushed.
    internal sealed class LoosefroxMechanics
    {
        private static readonly Vector3 Center = new Vector3(520, 0, -420);
        // Hammer's 8y circles leave sub-yalm gaps. September24's two captured
        // waves had no safe native grid cell with the usual .5y padding; .25y
        // preserves one or more cells across 100 grid phases, including pits.
        // Keep this exception local: ordinary circles still use .5y margins.
        internal const float HammerAvoidRadius = 8.25f;
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly List<Shape> hazards = new List<Shape>();
        private readonly Dictionary<uint, Shape[]> bombCrosses = new Dictionary<uint, Shape[]>();
        private DateTime nextLog;
        private DateTime donutUntil;
        private Vector3 donutOrigin;
        private readonly CapabilityManagerHandle refugeMovement = CapabilityManager.CreateNewHandle();
        private readonly List<System.Numerics.Vector2> refugeRoute = new List<System.Numerics.Vector2>();
        private bool refugeOwned, refugeMoving;
        private DateTime nextRefugePlan;
        private static bool InArena() => WorldManager.ZoneId == 1340 && Core.Me.Distance2D(Center) < 45 && GameObjectManager.GameObjects.Any(a => (a.BaseId == 0x4C65 || a.BaseId == 0x4C66) && a.Distance2D(Center) < 45);
        internal void Register()
        {
            // Reference floor radius 22; half-yard inset keeps path endpoints
            // inside the rim. Actual quicksand objects supply pit positions.
            AvoidanceHelpers.AddAvoidDonut(InArena, () => Center, 65, 21.5f);
            // Quicksand's six-yalm footprint plus a half-yalm margin. Follow
            // visible pit actors directly so burrows, wipe and exit cannot leave
            // stale circles. RB owns both escape and routing around these pits;
            // no second planner or stationary capability hold competes with it.
            AvoidanceManager.AddAvoidObject<GameObject>(canRun: InArena, objectSelector: IsPit, radiusProducer: _ => 6.5f, locationProducer: pit => pit.Location);
            AvoidanceManager.AddAvoidPolygon<Shape>(InArena, null, 65, h => -h.Heading, h => 1, h => 15, h => h.Points, h => h.Position, Current, priority: AvoidancePriority.High);
            // Native circles avoid the polygon edge-cell mismatch that kept
            // movement/facing locked during the September24 bomb pattern.
            AvoidanceManager.AddAvoidLocation<Shape>(InArena, () => Center, 65, h => h.Radius, h => h.Position, CurrentCircles);
        }

        private IEnumerable<Shape> Current()
        {
            var now = DateTime.UtcNow;
            return hazards.Where(h => h.Until > now && h.Radius == 0 && !(refugeOwned && h.Donut)).ToArray();
        }

        private IEnumerable<Shape> CurrentCircles()
        {
            var now = DateTime.UtcNow;
            return hazards.Where(h => h.Until > now && h.Radius > 0 &&
                LoosefroxHazardTiming.PublishBomb(h.ForecastOnly, now, donutUntil)).ToArray();
        }

        internal void Tick()
        {
            if (!InArena() || !Core.Me.IsAlive)
            {
                ReleaseRefuge();
                casts.Clear();
                hazards.Clear();
                bombCrosses.Clear();
                donutUntil = DateTime.MinValue;
                return;
            }

            var now = DateTime.UtcNow;
            hazards.RemoveAll(h => h.Until <= now);
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Center) < 65).ToArray();
            // September24: the four big bombs were present for several seconds,
            // but their 1.7s cast warning sent Kember across a pit and all four
            // explosions. These immovable bombs reveal their cross on spawn;
            // stamp it early, with the existing .5y margin. Retain object identity
            // through the cast so native avoidance does not restart its route.
            foreach (var bomb in actors.Where(a => a.BaseId == 0x4C67 && a.IsVisible && a.IsAlive))
            {
                if (bombCrosses.ContainsKey(bomb.ObjectId)) continue;
                var fence = now.AddSeconds(12);
                var shapes = Cross(bomb.Location, bomb.Heading, fence);
                foreach (var shape in shapes) shape.ForecastOnly = true;
                bombCrosses.Add(bomb.ObjectId, shapes);
                hazards.AddRange(shapes);
                ff14bot.Helpers.Logging.Write("[CrucibleBombForecast] actor={0:X} pos={1}; cross active before short cast.", bomb.ObjectId, bomb.Location);
            }
            foreach (var id in bombCrosses.Keys.Where(id => !actors.Any(a => a.ObjectId == id && a.IsVisible && a.IsAlive)).ToArray())
            {
                foreach (var shape in bombCrosses[id]) hazards.Remove(shape);
                bombCrosses.Remove(id);
            }
            foreach (var actor in actors)
            {
                uint id = actor.IsCasting ? actor.CastingSpellId : 0;
                if (casts.TryGetValue(actor.ObjectId, out var old) && old == id)
                    continue;
                casts[actor.ObjectId] = id;
                if (id == 0)
                    continue;
                var cast = actor.SpellCastInfo;
                // 750 ms is a conservative initial effect fence, not measured
                // final-boss timing. The companion combat capture checks it.
                var until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(750);
                ff14bot.Helpers.Logging.Write("[CrucibleFinalCast] id={0} actor={1:X} base={2:X} pos={3} heading={4:F4} ground={5} remaining={6:F3}", id, actor.ObjectId, actor.BaseId, actor.Location, actor.Heading, cast.CastLocation, cast.RemainingCastTime.TotalSeconds);
                if (id == 48229 || id == 48233 || id == 48235)
                    hazards.Add(Circle(actor.Location, id == 48235 ? 12.5f : id == 48233 ? HammerAvoidRadius : 8.5f, until));
                if (id == 48519 && cast.CastLocation.Distance2D(Center) < 40)
                    hazards.Add(Circle(cast.CastLocation, 6.5f, until));
                if (id == 48227)
                {
                    var points = new List<Vector2>
                    {
                        Vector2.Zero
                    };
                    for (int i = 0; i <= 32; i++)
                    {
                        double angle = (-46 + 92.0 * i / 32) * Math.PI / 180;
                        points.Add(new Vector2((float)Math.Sin(angle) * 60.5f, (float)Math.Cos(angle) * 60.5f));
                    }

                    hazards.Add(new Shape { Position = actor.Location, Heading = actor.Heading, Points = points.ToArray(), Until = until });
                }

                if (id == 48236)
                {
                    if (bombCrosses.TryGetValue(actor.ObjectId, out var shapes))
                    {
                        foreach (var shape in shapes)
                        {
                            shape.ForecastOnly = false;
                            shape.Until = until;
                            if (!hazards.Contains(shape)) hazards.Add(shape);
                        }
                    }
                    else
                    {
                        hazards.AddRange(Cross(actor.Location, actor.Heading, until));
                    }
                }

                if (id == 48231)
                {
                    // The cast helper fixes the donut origin. Keep its padded
                    // effect fence before restoring not-yet-casting bomb crosses;
                    // pits, boundaries and actual bomb casts remain registered.
                    donutUntil = until;
                    donutOrigin = actor.Location;
                    refugeRoute.Clear();
                    nextRefugePlan = default;
                    ff14bot.Helpers.Logging.Write("[CrucibleBombForecast] donut refuge takes precedence over future crosses until {0:O}; casting bombs remain active.", donutUntil);
                    // Polygon avoids have no holes. Tessellate the donut into
                    // eight annular sectors, preserving its 3.5 y safe center.
                    for (int sector = 0; sector < 8; sector++)
                    {
                        var points = new List<Vector2>();
                        foreach (float radius in new[]
                        {
                            40.5f,
                            3.5f
                        }

                        )
                            for (int j = 0; j <= 8; j++)
                            {
                                int step = radius > 4 ? j : 8 - j;
                                double angle = (sector + step / 8.0) * Math.PI / 4;
                                points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
                            }

                        hazards.Add(new Shape { Position = actor.Location, Points = points.ToArray(), Until = until, Donut = true });
                    }
                }
            // Earthquake 48239 is the survivor's raidwide/enrage. Marking
            // its whole arena unsafe would prevent the required damage.
            }

            PositionDonut(now);
            if (now >= nextLog)
            {
                nextLog = now.AddSeconds(1);
                ff14bot.Helpers.Logging.Write("[CrucibleFinalState] hp={0:F1} pos={1} pits={2} hazards={3} actors={4}", Core.Me.CurrentHealthPercent, Core.Me.Location, GameObjectManager.GameObjects.Count(IsPit), hazards.Count, string.Join(";", actors.Where(a => a.BaseId >= 0x4C65 && a.BaseId <= 0x4C69 || a.Icon != 0).Select(a => a.ObjectId.ToString("X") + ":" + a.BaseId.ToString("X") + ":" + a.CurrentHealthPercent.ToString("F1") + ":targetable=" + a.IsTargetable + ":icon=" + a.Icon + ":pos=" + a.Location)));
            }
        }

        private void PositionDonut(DateTime now)
        {
            if (now >= donutUntil) { ReleaseRefuge(); return; }
            var start = new System.Numerics.Vector2(Core.Me.Location.X, Core.Me.Location.Z);
            var center = new System.Numerics.Vector2(Center.X, Center.Z);
            var origin = new System.Numerics.Vector2(donutOrigin.X, donutOrigin.Z);
            var pits = GameObjectManager.GameObjects.Where(IsPit)
                .Select(p => new System.Numerics.Vector2(p.Location.X, p.Location.Z)).ToArray();
            // RB75548 crossed two pits while native donut escape repeatedly
            // found no route. This narrow semantic owner reuses the proven local
            // graph and validates EVERY edge, keeping pits and the rim intact.
            // Actual bomb casts and other live hazards remain hard exclusions;
            // only the already-deferred forecasts and this donut are omitted.
            var other = hazards.Where(h => h.Until > now && !h.Donut &&
                LoosefroxHazardTiming.PublishBomb(h.ForecastOnly, now, donutUntil)).ToArray();
            // Materialize detached geometry once, not for every graph sample.
            var round = other.Where(h => h.Radius > 0).ToArray();
            var polygons = other.Where(h => h.Radius == 0).Select(h => new ThirdBoardHazard {
                Origin = new System.Numerics.Vector2(h.Position.X, h.Position.Z), Heading = h.Heading,
                Points = h.Points.Select(v => new System.Numerics.Vector2(v.X, v.Y)).ToArray() }).ToArray();
            bool Floor(System.Numerics.Vector2 p) => System.Numerics.Vector2.Distance(p, center) <= 21.25f &&
                pits.All(pit => System.Numerics.Vector2.Distance(p, pit) >= 6.75f) &&
                round.All(h => System.Numerics.Vector2.Distance(p, new System.Numerics.Vector2(h.Position.X, h.Position.Z)) >= h.Radius + .25f) &&
                polygons.All(h => !h.Contains(p));
            if (!Floor(start)) { ReleaseRefuge(); return; }
            while (refugeRoute.Count > 0 && System.Numerics.Vector2.Distance(start, refugeRoute[0]) < .25f)
                refugeRoute.RemoveAt(0);
            if (refugeRoute.Count > 0 && !MasterArenaMovement.Corridor(start, refugeRoute[0], Floor, Array.Empty<ThirdBoardHazard>(), now))
                refugeRoute.Clear();
            bool arrived = System.Numerics.Vector2.Distance(start, origin) <= 3;
            if (refugeRoute.Count == 0 && !arrived && now >= nextRefugePlan)
            {
                nextRefugePlan = now.AddMilliseconds(500);
                var goals = Enumerable.Range(-6, 13).SelectMany(x => Enumerable.Range(-6, 13).Select(z => origin + new System.Numerics.Vector2(x * .5f, z * .5f)))
                    .Where(p => System.Numerics.Vector2.Distance(p, origin) <= 3 && Floor(p)).OrderBy(p => System.Numerics.Vector2.Distance(p, start));
                refugeRoute.AddRange(MasterArenaMovement.RouteToAny(start, goals, center, Floor, Array.Empty<ThirdBoardHazard>(), now));
                ff14bot.Helpers.Logging.Write("[CrucibleDonutRoute] from={0} origin={1} nodes={2} remaining={3:F2}", start, origin, refugeRoute.Count, (donutUntil - now).TotalSeconds);
            }
            if (refugeRoute.Count == 0 && !arrived) { ReleaseRefuge(); return; }
            // Suppress the native annulus only after a fully checked route is
            // available. Native pit/boundary emergencies still preempt transit.
            refugeOwned = true;
            CapabilityManager.Update(refugeMovement, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: donut refuge");
            if (AvoidanceManager.IsRunningOutOfAvoid) { refugeMoving = false; return; }
            if (arrived)
            {
                if (refugeMoving) Navigator.Stop();
                refugeMoving = false;
                CapabilityManager.Clear(refugeMovement, CapabilityFlags.Facing, "Crucible: donut refuge reached");
            }
            else
            {
                CapabilityManager.Update(refugeMovement, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: donut refuge transit");
                var next = refugeRoute[0];
                Navigator.PlayerMover.MoveTowards(new Vector3(next.X, Core.Me.Location.Y, next.Y));
                refugeMoving = true;
            }
        }

        private void ReleaseRefuge()
        {
            if (refugeMoving && !AvoidanceManager.IsRunningOutOfAvoid) Navigator.Stop();
            if (refugeOwned)
            {
                CapabilityManager.Clear(refugeMovement, CapabilityFlags.Movement, "Crucible: donut resolved");
                CapabilityManager.Clear(refugeMovement, CapabilityFlags.Facing, "Crucible: donut resolved");
            }
            refugeOwned = refugeMoving = false;
            refugeRoute.Clear();
        }

        // Event-object identity and visibility delimit the persistent hazard;
        // targetability is irrelevant for noncombat quicksand actors.
        private static bool IsPit(GameObject actor) => actor.BaseId == 0x1EC026 && actor.IsVisible && actor.Distance2D(Center) < 35;
        private static Shape Circle(Vector3 position, float radius, DateTime until) => new Shape
        {
            Position = position,
            Until = until,
            Radius = radius
        };

        private static Shape[] Cross(Vector3 position, float heading, DateTime until)
        {
            return BombCrossCover.Centers(new System.Numerics.Vector2(position.X, position.Z), heading,
                new System.Numerics.Vector2(Center.X, Center.Z), 21.5f)
                .Select(p => Circle(new Vector3(p.X, position.Y, p.Y), BombCrossCover.Radius, until)).ToArray();
        }
        private sealed class Shape
        {
            internal Vector3 Position;
            internal float Heading;
            internal Vector2[] Points;
            internal float Radius;
            internal bool ForecastOnly;
            internal bool Donut;
            internal DateTime Until;
        }
    }
}
