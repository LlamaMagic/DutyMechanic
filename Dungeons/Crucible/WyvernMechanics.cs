using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons
{
    // Combines sprite lanes, persistent pools and moving whirlwinds with knockback
    // staging. The crossing planner owns overlap movement when native avoidance
    // cannot route through the timed opening; other hazards use native avoidance.
    internal sealed class WyvernMechanics
    {
        private static readonly Vector3 Center = new Vector3(520, 0, 0);
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly List<Shape> hazards = new List<Shape>();
        private readonly Dictionary<uint, Shape> floor = new Dictionary<uint, Shape>();
        private readonly CapabilityManagerHandle knockbackHandle = CapabilityManager.CreateNewHandle();
        private Vector3 knockbackOrigin;
        private Vector3? destination;
        private DateTime knockbackUntil, nextHealth, nextWarning;
        private bool owned, moving;
        private readonly CapabilityManagerHandle crossingHandle = CapabilityManager.CreateNewHandle();
        private Shape crossing;
        private System.Numerics.Vector2[] crossingRoute;
        private int crossingIndex;
        private bool crossingOwned, crossingMoving;
        private static bool InArena() => WorldManager.ZoneId == 1340 && Core.Me.Distance2D(Center) < 45;

        internal void Register()
        {
            // Measured reference floor 40x 29.6, inset 0.5 y on each edge. Keep
            // corners available for behind-boss travel during Blazing Trail.
            AvoidanceHelpers.AddAvoidSquareDonut(InArena, 39, 28.6f, 100, 100, () => new[] { Center });
            AvoidanceManager.AddAvoidPolygon<Shape>(() => InArena() && !crossingOwned, null, 60,
                h => -h.Heading, h => 1, h => 15, h => h.Points, h => h.Origin,
                () => CurrentHazards().Where(h => !h.Persistent), priority: AvoidancePriority.Medium);
            // Floor damage is already active while a cast's footprint is still
            // traversable. Higher path cost for persistent pools prevents the
            // shortest escape from a large future cone cutting across live fire.
            AvoidanceManager.AddAvoidPolygon<Shape>(() => InArena() && !crossingOwned, null, 60,
                h => -h.Heading, h => 1, h => 15, h => h.Points, h => h.Origin,
                () => CurrentHazards().Where(h => h.Persistent), priority: AvoidancePriority.High);
        }

        private IEnumerable<Shape> CurrentHazards()
        {
            var now = DateTime.UtcNow;
            // Unlike Manticore's inverse halves, Wyvern's simultaneous sprite
            // lanes and Blazing Trail share safe ground behind the boss. Keeping
            // only the earliest lane hid the required crossing until <1 s remained
            // in 83392. Publish their union so native pathing can stage in time.
            var result = hazards.Where(h => h.Until > now).ToList();
            var seen = new HashSet<uint>();
            foreach (var actor in GameObjectManager.GameObjects.Where(a => a.IsVisible && a.Distance2D(Center) < 45))
            {
                if (actor.BaseId != 0x1EA66D && actor.BaseId != 0x4C59 && actor.BaseId != 0x4C5A)
                    continue;
                seen.Add(actor.ObjectId);
                // AvoidInfo.Collection uses source-object identity to retain its
                // active heightfield stamps. Recreating each Shape every pulse
                // discarded that continuity, producing repeated run-out handoffs
                // and Burns on both 83392 and 98488. Keep a stable per-actor object;
                // update only its position, and remove it when the actor vanishes.
                if (!floor.TryGetValue(actor.ObjectId, out var shape))
                    floor[actor.ObjectId] = shape = Circle(actor.Location, actor.BaseId == 0x1EA66D ? 5.5f : actor.BaseId == 0x4C59 ? 4 : 5.5f, now.AddSeconds(1));
                shape.Origin = actor.Location;
                shape.Persistent = true;
                shape.Until = now.AddSeconds(1);
                // Circumscribe the reference's forward capsules to cover the
                // moving body, including 0.5 y margin. Refresh from live position;
                // never leave an obsolete avoid after its actor disappears.
                if (actor.BaseId == 0x4C59 || actor.BaseId == 0x4C5A)
                {
                    float length = actor.BaseId == 0x4C59 ? 2 : 3;
                    var midpoint = actor.Location + new Vector3((float)Math.Sin(actor.Heading) * length / 2, 0, (float)Math.Cos(actor.Heading) * length / 2);
                    shape.Origin = midpoint;
                }
                result.Add(shape);
            }
            foreach (uint id in floor.Keys.Where(id => !seen.Contains(id)).ToArray())
                floor.Remove(id);
            return result;
        }

        internal void Tick()
        {
            if (!InArena() || !Core.Me.IsAlive)
            {
                ReleaseCrossing();
                Release();
                casts.Clear();
                hazards.Clear();
                floor.Clear();
                knockbackUntil = default;
                return;
            }
            var now = DateTime.UtcNow;
            hazards.RemoveAll(h => h.Until <= now);
            foreach (var a in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Center) < 45))
            {
                uint id = a.IsCasting ? a.CastingSpellId : 0;
                if (casts.TryGetValue(a.ObjectId, out uint old) && old == id)
                    continue;
                casts[a.ObjectId] = id;
                if (id == 0)
                    continue;
                var cast = a.SpellCastInfo;
                // 83392 effects arrived after RemainingCastTime reached zero:
                // Buffet+0.8 s, Typhoon+1.13 s, Blazing+0.51 s. The former 0.3 s
                // fence released movement before impact.1.4 s covers the largest
                // observed effect delay plus a small pulse allowance.
                var until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(1400);
                ff14bot.Helpers.Logging.Write("[CrucibleWyvernCast] id={0} actor={1:X} base={2:X} pos={3} heading={4:F4} destination={5} remaining={6:F2}",
                    id, a.ObjectId, a.BaseId, a.Location, a.Heading, cast.CastLocation, cast.RemainingCastTime.TotalSeconds);
                // Native rows: Buffet type 12/40 y/10 y width; Liquid Hell type 2/
                // 6 y; Blazing/Storm type 13/60 y and 25 y. Guide confirms behind-
                // boss safety and three narrow cones. Margins are 0.5 y/1degree.
                if (id == 48167)
                    hazards.Add(new Shape
                    {
                        Origin = a.Location,
                        Heading = a.Heading,
                        Until = until,
                        Points = new[] { new Vector2(-5.5f, -.5f), new Vector2(5.5f, -.5f), new Vector2(5.5f, 40.5f), new Vector2(-5.5f, 40.5f) }
                    });
                if (id == 48172)
                    hazards.Add(Circle(a.Location, 6.5f, until));
                if (id == 48175 || id == 48178)
                {
                    var cone = Cone(a.Location, a.Heading, id == 48175 ? 60.5f : 25.5f, id == 48175 ? 91 : 31, until);
                    hazards.Add(cone);
                    if (id == 48175)
                    {
                        ReleaseCrossing();
                        crossing = cone;
                    }
                }
                if (id == 48168)
                {
                    knockbackOrigin = a.Location;
                    knockbackUntil = until;
                    destination = null;
                }
                // Storm's Grip 48166 spawns persistent sprites. Its range 60
                // database row is not evidence of a lethal arena-wide avoid.
            }
            if (crossing != null && now < crossing.Until)
                StageCrossing();
            else
                ReleaseCrossing();
            if (!crossingOwned && now < knockbackUntil)
                StageKnockback();
            else
                Release();
            if (now >= nextHealth)
            {
                nextHealth = now.AddSeconds(2);
                ff14bot.Helpers.Logging.Write("[CrucibleWyvernHealth] player={0:F1} pet={1} petHP={2:F1} pos={3} knockback={4}",
                    Core.Me.CurrentHealthPercent, Core.Me.Pet?.Name, Core.Me.Pet?.CurrentHealthPercent, Core.Me.Location, now < knockbackUntil);
            }
        }

        private void StageCrossing()
        {
            var now = DateTime.UtcNow;
            if (crossingRoute == null)
            {
                // Four captures show native avoidance taking the straight line
                // through Liquid Hell.98964 also alternated run/done every 33 ms.
                // Only this overlap gets manual ownership; other Wyvern phases
                // retain native avoidance.33304 confirmed the later overlap's
                // horizontal whirlwinds at about 2 y/s; forecast those explicitly.
                var actors = GameObjectManager.GameObjects.Where(a => a.IsVisible && a.Distance2D(Center) < 45).ToArray();
                var movingHazards = actors.Where(a => a.BaseId == 0x4C59 || a.BaseId == 0x4C5A).Select(a =>
                {
                    var direction = new System.Numerics.Vector2((float)Math.Sin(a.Heading), (float)Math.Cos(a.Heading));
                    return new WyvernCrossingPlanner.MovingCircle
                    {
                        // Shape constructor semantics and the full 106808 replay:
                        // small radius 2/length 2.5; large radius 3/length 3.5.
                        // Keep the actual actor origin, not a bounding midpoint.
                        Origin = new System.Numerics.Vector2(a.Location.X, a.Location.Z),
                        Velocity = direction * 2,
                        Radius = a.BaseId == 0x4C59 ? 2 : 3,
                        Length = a.BaseId == 0x4C59 ? 2.5f : 3.5f
                    };
                }).ToArray();
                var pools = actors.Where(a => a.BaseId == 0x1EA66D)
                    .Select(a => new System.Numerics.Vector2(a.Location.X, a.Location.Z)).ToArray();
                var constraints = hazards.Where(h => h.Until > now).ToArray();
                double remaining = (crossing.Until - now).TotalSeconds - 1.4;
                var start = new System.Numerics.Vector2(Core.Me.Location.X, Core.Me.Location.Z);
                // 109080 returned no route although interpolation from 1Hz floor
                // capture passed offline. Record the exact same-frame solver
                // inputs once per crossing, including temporary cast shapes,
                // before blaming geometry or weakening a safety margin.
                ff14bot.Helpers.Logging.Write("[CrucibleWyvernPlanInput] start={0} remaining={1:R} pools={2} moving={3} constraints={4}",
                    start, remaining, string.Join(";", pools.Select(p => p.ToString())),
                    string.Join(";", movingHazards.Select(h => FormattableString.Invariant($"{h.Origin}/{h.Velocity}/r{h.Radius}/l{h.Length}"))),
                    string.Join(";", constraints.Select(h => FormattableString.Invariant($"{h.Origin}/h{h.Heading:R}/until{(h.Until - now).TotalSeconds:R}/points[{string.Join(",", h.Points.Select(p => p.ToString()))}]"))));
                crossingRoute = WyvernCrossingPlanner.Plan(start, pools,
                    (p, seconds) =>
                    {
                        var v = new Vector3(p.X, 0, p.Y);
                        // Both captured orientations announce the paired lanes
                        // about 0.9 s after Trail. Reserve the center strip 0.7 s
                        // before Trail's cast end, even before sprites announce.
                        if (seconds >= remaining - 1.0 && Math.Abs(p.Y) > 4.3f)
                            return false;
                        return !constraints.Any(h => seconds >= (h.Until - now).TotalSeconds - 1.6 && h.Contains(v));
                    },
                    p => Math.Abs(p.Y) < 4.3f && !constraints.Any(h => h.Contains(new Vector3(p.X, 0, p.Y))),
                    // Moving hazards require actual travel timing. The full
                    // replay failed at live 6 y/s when planned at 5.5: an earlier
                    // arrival hit a tornado that had not yet cleared the gap.
                    // The forecast retains 0.15 s timing and speed uncertainty.
                    remaining - .5, speed: movingHazards.Length == 0 ? 5.5f : 6f, moving: movingHazards);
                crossingIndex = 0;
                ff14bot.Helpers.Logging.Write("[CrucibleWyvern] Trail crossing planned nodes={0} remaining={1:F2}; native fallback if empty", crossingRoute.Length, remaining);
                if (crossingRoute.Length == 0)
                    return;
            }
            if (crossingRoute.Length == 0)
                return;
            // RB validates one capability per Update call.101436 threw for
            // the combined flags after hiding native hazards, leaving the old
            // movement input active. Acquire both before suppressing geometry.
            CapabilityManager.Update(crossingHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: Wyvern timed pool crossing");
            CapabilityManager.Update(crossingHandle, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: Wyvern timed pool crossing");
            crossingOwned = true;
            // Keep the same waypoint chain through the overlap. Recomputing the
            // shortest route every pulse recreates the direction oscillation.
            while (crossingIndex < crossingRoute.Length && Core.Me.Distance2D(new Vector3(crossingRoute[crossingIndex].X, 0, crossingRoute[crossingIndex].Y)) < .22f)
                crossingIndex++;
            if (crossingIndex < crossingRoute.Length)
            {
                var point = crossingRoute[crossingIndex];
                Navigator.PlayerMover.MoveTowards(new Vector3(point.X, 0, point.Y));
                crossingMoving = true;
            }
            else if (crossingMoving)
            {
                MovementManager.MoveStop();
                crossingMoving = false;
                ff14bot.Helpers.Logging.Write("[CrucibleWyvern] Trail crossing arrived; holding through effect while routine remains schedulable.");
            }
        }

        private void ReleaseCrossing()
        {
            if (crossingMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            if (crossingOwned)
            {
                CapabilityManager.Clear(crossingHandle, CapabilityFlags.Movement, "Crucible: Wyvern Trail resolved");
                CapabilityManager.Clear(crossingHandle, CapabilityFlags.Facing, "Crucible: Wyvern Trail resolved");
            }
            crossingOwned = false;
            crossingMoving = false;
            crossingRoute = null;
            crossing = null;
        }

        private void StageKnockback()
        {
            // Never cancel the native emergency escape. Keep one capability
            // lease so the routine cannot pull away from a safe launch point.
            owned = true;
            // Kember 83392 selected(518,0) during Typhoon yet remained at X 519.98:
            // Fight's SetFacing kept turning the forward mover toward the boss.
            // Reserve facing with movement until staging releases; rotation still
            // runs, and native emergency avoidance retains the earlier priority.
            CapabilityManager.Update(knockbackHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: Wyvern knockback staging");
            CapabilityManager.Update(knockbackHandle, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: Wyvern knockback staging");
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }
            var shapes = CurrentHazards().ToArray();
            Vector3 start = Core.Me.Location;
            if (!destination.HasValue || !SafeLaunch(destination.Value, shapes) || !ClearSegment(start, destination.Value, shapes, true))
            {
                destination = Enumerable.Range(-9, 19).SelectMany(x => Enumerable.Range(-6, 13).Select(z => Center + new Vector3(x * 2, 0, z * 2)))
                    // Landing and corridor safety precede melee launch preference.
                    .Where(p => SafeLaunch(p, shapes) && ClearSegment(start, p, shapes, true)).OrderBy(CrucibleMeleePreference.CaptureWorld()).ThenBy(p => p.Distance2D(start))
                    .Select(p => (Vector3?)p).FirstOrDefault();
                if (destination.HasValue)
                    ff14bot.Helpers.Logging.Write("[CrucibleWyvern] knockback launch={0} origin={1}", destination, knockbackOrigin);
            }
            if (!destination.HasValue)
            {
                if (moving)
                    MovementManager.MoveStop();
                moving = false;
                if (DateTime.UtcNow >= nextWarning)
                {
                    nextWarning = DateTime.UtcNow.AddSeconds(2);
                    ff14bot.Helpers.Logging.Write("[CrucibleWyvern] No verified knockback launch; retaining native avoidance.");
                }
                return;
            }
            // Staging is ordinary ground travel: route around active avoids.
            // The separately timed Trail crossing still follows its planned
            // waypoints because its safety depends on future hazard positions.
            if (start.Distance2D(destination.Value) > .3f)
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination.Value, "Crucible: Wyvern knockback staging") { DistanceTolerance = .3f, UseMount = false });
                moving = true;
            }
            else if (moving)
            {
                Navigator.Stop();
                moving = false;
            }
        }

        private bool SafeLaunch(Vector3 point, Shape[] shapes)
        {
            float dx = point.X - knockbackOrigin.X, dz = point.Z - knockbackOrigin.Z;
            float distance = (float)Math.Sqrt(dx * dx + dz * dz);
            if (distance < 1 || !Safe(point, shapes))
                return false;
            // Typhoon's reference knockback is 10 y. Validate the whole landing
            // segment, not only its endpoint, against persistent floor hazards.
            var landing = point + new Vector3(dx / distance * 10, 0, dz / distance * 10);
            return ClearSegment(point, landing, shapes, false);
        }

        private static bool Safe(Vector3 point, Shape[] shapes) => Math.Abs(point.X - Center.X) <= 19.5f && Math.Abs(point.Z - Center.Z) <= 14.3f && !shapes.Any(s => s.Contains(point));

        private static bool ClearSegment(Vector3 from, Vector3 to, Shape[] shapes, bool allowInitialEscape)
        {
            bool escaped = !allowInitialEscape || Safe(from, shapes);
            int count = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .25f));
            for (int i = 1; i <= count; i++)
            {
                bool safe = Safe(from + (to - from) * (i / (float)count), shapes);
                if (!safe && escaped)
                    return false;
                if (safe)
                    escaped = true;
            }
            return escaped;
        }

        private void Release()
        {
            if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            moving = false;
            destination = null;
            if (owned)
            {
                CapabilityManager.Clear(knockbackHandle, CapabilityFlags.Movement, "Crucible: Wyvern knockback resolved");
                CapabilityManager.Clear(knockbackHandle, CapabilityFlags.Facing, "Crucible: Wyvern knockback resolved");
            }
            owned = false;
        }

        private static Shape Circle(Vector3 origin, float radius, DateTime until) => new Shape
        {
            Origin = origin,
            Until = until,
            Points = Enumerable.Range(0, 64).Select(i => new Vector2((float)Math.Sin(i * Math.PI / 32) * radius, (float)Math.Cos(i * Math.PI / 32) * radius)).ToArray()
        };

        private static Shape Cone(Vector3 origin, float heading, float radius, float halfAngle, DateTime until)
        {
            var points = new List<Vector2> { new Vector2(0, -.5f) };
            for (int i = 0; i <= 48; i++)
            {
                double angle = (-halfAngle + 2 * halfAngle * i / 48) * Math.PI / 180;
                points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
            }
            return new Shape { Origin = origin, Heading = heading, Until = until, Points = points.ToArray() };
        }

        private sealed class Shape
        {
            internal Vector3 Origin;
            internal float Heading;
            internal DateTime Until;
            internal bool Persistent;
            internal Vector2[] Points;
            internal bool Contains(Vector3 point)
            {
                float dx = point.X - Origin.X, dz = point.Z - Origin.Z;
                float x = dx * (float)Math.Cos(Heading) - dz * (float)Math.Sin(Heading);
                float z = dx * (float)Math.Sin(Heading) + dz * (float)Math.Cos(Heading);
                bool inside = false;
                for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
                    if ((Points[i].Y > z) != (Points[j].Y > z) && x < (Points[j].X - Points[i].X) * (z - Points[i].Y) / (Points[j].Y - Points[i].Y) + Points[i].X)
                        inside = !inside;
                return inside;
            }
        }
    }
}
