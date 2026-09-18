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
    // Separates damaging circles from bait and forced-march positioning.
    // Actor gating is required because Tablitaur reuses this arena.
    internal sealed class VoidmancerMechanics
    {
        private static readonly Vector3 Center = new Vector3(120, 0, 0);
        private static readonly Vector2[] UnitCircle = Enumerable.Range(0, 64).Select(i => new Vector2((float)Math.Sin(i * Math.PI / 32), (float)Math.Cos(i * Math.PI / 32))).ToArray();
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly List<Circle> circles = new List<Circle>();
        private readonly Dictionary<uint, Circle> maladies = new Dictionary<uint, Circle>();
        private readonly CapabilityManagerHandle semantic = CapabilityManager.CreateNewHandle();
        private DateTime baitUntil, nextLog, nextMarchPlan;
        private Vector3? destination;
        private float? marchHeading;
        private bool owned, moving;
        private DateTime darkOrbUntil;
        private Vector3? darkOrbPoint;
        // Tablitaur reuses these coordinates in another sub-instance. Its
        // unique actor roster must not inherit Voidmancer's movement owner.
        private static bool InArena() => WorldManager.ZoneId == 1340 && Core.Me.Distance2D(Center) < 40 &&
            GameObjectManager.GameObjects.Any(a => (a.BaseId == 0x4C5C || a.BaseId == 0x4C5D) && a.Distance2D(Center) < 40);

        internal void Register()
        {
            // Reference square 40x 40, with 0.5 y wall inset. A circle would remove
            // the corners required to minimize Death Drive's revived zombies.
            AvoidanceHelpers.AddAvoidSquareDonut(InArena, 39, 39, 100, 100, () => new[] { Center });
            // Detached circle snapshots are not GameObject wrappers. Polygon
            // producers retain their identity and scale one shared unit circle.
            AvoidanceManager.AddAvoidPolygon<Circle>(InArena, null, 60, c => 0, c => c.Radius,
                c => 15, c => UnitCircle, c => c.Position, Hazards, priority: AvoidancePriority.High);
        }

        private IEnumerable<Circle> Hazards()
        {
            var now = DateTime.UtcNow;
            var seen = new HashSet<uint>();
            foreach (var actor in GameObjectManager.GameObjects.Where(a => a.BaseId == 0x4C5E && a.IsVisible && a.Distance2D(Center) < 40))
            {
                seen.Add(actor.ObjectId);
                if (!maladies.TryGetValue(actor.ObjectId, out var hazard))
                    maladies[actor.ObjectId] = hazard = new Circle { Radius = 8.5f };
                hazard.Position = actor.Location;
                hazard.Until = now.AddSeconds(1);
            }
            foreach (uint id in maladies.Keys.Where(id => !seen.Contains(id)).ToArray())
                maladies.Remove(id);
            return circles.Where(c => c.Until > now).Concat(maladies.Values).ToArray();
        }

        internal void Tick()
        {
            if (!InArena() || !Core.Me.IsAlive)
            {
                Release();
                casts.Clear();
                circles.Clear();
                maladies.Clear();
                baitUntil = darkOrbUntil = default;
                darkOrbPoint = null;
                return;
            }
            var now = DateTime.UtcNow;
            circles.RemoveAll(c => c.Until <= now);
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Center) < 40).ToArray();
            foreach (var actor in actors)
            {
                uint id = actor.IsCasting ? actor.CastingSpellId : 0;
                if (casts.TryGetValue(actor.ObjectId, out uint old) && old == id)
                    continue;
                casts[actor.ObjectId] = id;
                if (id == 0)
                    continue;
                var cast = actor.SpellCastInfo;
                var until = now + cast.RemainingCastTime + TimeSpan.FromSeconds(1.4);
                ff14bot.Helpers.Logging.Write("[CrucibleVoidCast] id={0} actor={1:X} base={2:X} pos={3} heading={4:F4} ground={5} remaining={6:F2}", id, actor.ObjectId, actor.BaseId, actor.Location, actor.Heading, cast.CastLocation, cast.RemainingCastTime.TotalSeconds);
                if (id == 48181 && actor.BaseId == 0x4C5C)
                {
                    baitUntil = until;
                    destination = null;
                }
                if (id == 48182)
                {
                    // Placed helper cast locks the bait. Release immediately
                    // and let native avoidance leave its actual ground circle.
                    baitUntil = default;
                    Release();
                    if (cast.CastLocation.Distance2D(Center) < 30)
                        circles.Add(new Circle { Position = cast.CastLocation, Radius = 10.5f, Until = until });
                    else
                        ff14bot.Helpers.Logging.Write("[CrucibleVoid] Death Drive ground point not populated; capture required.");
                }
                if (id == 48186 || id == 48184)
                    circles.Add(new Circle { Position = actor.Location, Radius = id == 48186 ? 18.5f : 8.5f, Until = until });
                if (id == 48186)
                {
                    darkOrbUntil = until;
                    darkOrbPoint = null;
                }
                // Evil Mist 48183 and Mindjack 48187 publish arena-sized rows:
                // these spawn hazards/apply status, not escapable full-room AoEs.
            }
            var hazards = Hazards().ToArray();
            // 33304 received About Face, absent from the initial reference's
            // two-direction list. Its 6 y/s,3 s march reversed heading 3.3796 to
            // 0.2380 and crossed Z 19.5. Native aura rows verify all four names.
            bool forward = Core.Me.HasAura(2161), backward = Core.Me.HasAura(2162);
            bool left = Core.Me.HasAura(2163), right = Core.Me.HasAura(2164), forced = Core.Me.HasAura(1257);
            int directions = (forward ? 1 : 0) + (backward ? 1 : 0) + (left ? 1 : 0) + (right ? 1 : 0);
            if (forced)
            {
                // Client owns motion after the march starts. Preserve facing
                // suppression, but never fight forced movement with MoveStop.
                Hold();
                moving = false;
            }
            else if (directions == 1)
                PrepareMarch(hazards, backward ? (float)Math.PI : left ? (float)Math.PI / 2 : right ? -(float)Math.PI / 2 : 0, left || right);
            else if (now < baitUntil)
                Bait(actors, hazards);
            else if (now < darkOrbUntil)
                PositionDarkOrb(hazards);
            else
            {
                darkOrbPoint = null;
                Release();
            }
            if (now >= nextLog)
            {
                nextLog = now.AddSeconds(1);
                ff14bot.Helpers.Logging.Write("[CrucibleVoidState] pos={0} heading={1:F4} hp={2:F1} left={3} right={4} forced={5} hazards={6} target={7}", Core.Me.Location, Core.Me.Heading, Core.Me.CurrentHealthPercent, left, right, forced, hazards.Length, Core.Me.CurrentTarget?.Name);
                foreach (var aura in Core.Me.CharacterAuras.Where(a => a.Id >= 2161 && a.Id <= 2164 || a.Id == 1257 || a.Id == 5424))
                    ff14bot.Helpers.Logging.Write("[CrucibleVoidAura] id={0} remaining={1}", aura.Id, aura.TimespanLeft);
                foreach (var zombie in actors.Where(a => a.BaseId == 0x4C5D))
                    ff14bot.Helpers.Logging.Write("[CrucibleVoidZombie] oid={0:X} pos={1} alive={2} targetable={3} hp={4}", zombie.ObjectId, zombie.Location, zombie.IsAlive, zombie.IsTargetable, zombie.CurrentHealth);
            }
        }

        private void PositionDarkOrb(Circle[] hazards)
        {
            // RB 103664 at 14:53:40-43 oscillated at the 18.5 y Dark Orb edge.
            // Hold a destination 1.5 y beyond registered circles (which already
            // include their 0.5 y damage margin); accept an unmoving point with
            // .75 y clearance. All maladies participate, and the square's real
            // corners remain available. Native escape and march keep priority.
            bool ClearPoint(Vector3 p, float margin) => Math.Abs(p.X - Center.X) <= 19 && Math.Abs(p.Z) <= 19 &&
                hazards.All(h => h.Position.Distance2D(p) >= h.Radius + margin);
            if (!darkOrbPoint.HasValue || !ClearPoint(darkOrbPoint.Value, .75f))
            {
                darkOrbPoint = ClearPoint(Core.Me.Location, .75f) ? Core.Me.Location :
                    Enumerable.Range(-19, 39).SelectMany(x => Enumerable.Range(-19, 39).Select(z => Center + new Vector3(x, 0, z)))
                        .Where(p => ClearPoint(p, 1.5f)).OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
                if (darkOrbPoint.HasValue)
                    ff14bot.Helpers.Logging.Write("[CrucibleDarkOrbHold] destination={0} hazards={1}", darkOrbPoint, hazards.Length);
            }
            if (!darkOrbPoint.HasValue)
            {
                Release();
                return;
            }
            if (!owned && !AvoidanceManager.IsRunningOutOfAvoid)
                Navigator.Stop();
            MoveAndHold(darkOrbPoint.Value);
        }

        private void Bait(BattleCharacter[] actors, Circle[] hazards)
        {
            var zombies = actors.Where(a => a.BaseId == 0x4C5D).ToArray();
            if (!destination.HasValue || !Safe(destination.Value, hazards))
            {
                // Rank the perimeter by intersected zombie hitboxes, then
                // travel distance. Guide NE/SW recommendations corroborate the
                // purpose; actual actors decide which edge is best this pull.
                destination = Enumerable.Range(-19, 39).SelectMany(i => new[] { new Vector3(101, 0, i), new Vector3(139, 0, i), new Vector3(120 + i, 0, -19), new Vector3(120 + i, 0, 19) })
                    .Where(p => Safe(p, hazards) && Clear(Core.Me.Location, p, hazards))
                    .OrderBy(p => zombies.Count(z => z.Distance2D(p) <= 10.75f))
                    .ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
                if (destination.HasValue)
                    ff14bot.Helpers.Logging.Write("[CrucibleVoid] bait destination={0} intersectedZombies={1}", destination, zombies.Count(z => z.Distance2D(destination.Value) <= 10.75f));
            }
            if (destination.HasValue)
                MoveAndHold(destination.Value);
        }

        private void PrepareMarch(Circle[] hazards, float turn, bool uncertainSide)
        {
            // Three seconds at observed ordinary 6 y/s, plus 0.5 y endpoint margin.
            // Prefer paths safe in BOTH directions while the first capture
            // establishes the client's Left/Right heading convention.
            bool MarchSafe(Vector3 p, float heading)
            {
                var offset = new Vector3((float)Math.Sin(heading) * 18.5f, 0, (float)Math.Cos(heading) * 18.5f);
                // Forward/backward are unambiguous and About Face's reversal
                // is live-confirmed. Keep both-axis safety only for left/right
                // until a capture verifies their sign in this client's heading.
                return Clear(p, p + offset, hazards) && (!uncertainSide || Clear(p, p - offset, hazards));
            }
            if (!destination.HasValue || !marchHeading.HasValue || !MarchSafe(destination.Value, marchHeading.Value))
            {
                // A missing bidirectional corridor is possible in this first
                // trial. Bound the grid search to 2Hz instead of repeating its
                // full segment sampling on every bot pulse during that state.
                if (DateTime.UtcNow < nextMarchPlan)
                {
                    Release();
                    return;
                }
                nextMarchPlan = DateTime.UtcNow.AddMilliseconds(500);
                var options = Enumerable.Range(-9, 19).SelectMany(x => Enumerable.Range(-9, 19).Select(z => Center + new Vector3(x * 2, 0, z * 2)))
                    .Where(p => Safe(p, hazards) && Clear(Core.Me.Location, p, hazards))
                    .SelectMany(p => Enumerable.Range(0, 16).Select(i => new { Point = p, Heading = i * (float)Math.PI / 8 }))
                    .Where(p => MarchSafe(p.Point, p.Heading)).OrderBy(p => p.Point.Distance2D(Core.Me.Location)).FirstOrDefault();
                destination = options?.Point;
                marchHeading = options?.Heading;
                if (options != null)
                    ff14bot.Helpers.Logging.Write("[CrucibleVoid] march staging={0} heading={1:F3} turn={2:F3} bidirectional={3}", destination, marchHeading, turn, uncertainSide);
                else
                    ff14bot.Helpers.Logging.Write("[CrucibleVoid] No verified march corridor; native avoidance retained, direction capture required.");
            }
            if (!destination.HasValue || !marchHeading.HasValue)
            {
                Release();
                return;
            }
            MoveAndHold(destination.Value);
            if (Core.Me.Distance2D(destination.Value) < .35f && !AvoidanceManager.IsRunningOutOfAvoid)
                Core.Me.SetFacing(marchHeading.Value - turn);
        }

        private static bool Safe(Vector3 p, Circle[] hazards) => Math.Abs(p.X - 120) <= 19.5f && Math.Abs(p.Z) <= 19.5f && !hazards.Any(h => h.Position.Distance2D(p) < h.Radius);
        private static bool Clear(Vector3 from, Vector3 to, Circle[] hazards)
        {
            int count = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .25));
            for (int i = 0; i <= count; i++)
            if (!Safe(from + (to - from) * (i / (float)count), hazards))
                return false;
            return true;
        }
        private void Hold()
        {
            owned = true;
            // RB Update accepts individual enum members, not a flags union.
            CapabilityManager.Update(semantic, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: Voidmancer bait/march");
            CapabilityManager.Update(semantic, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: Voidmancer bait/march");
        }
        private void MoveAndHold(Vector3 point)
        {
            Hold();
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }
            // A safe bait/march endpoint does not imply a clear direct route.
            // Keep its semantic destination but let native navigation route
            // around registered hazards, with emergency escape retaining priority.
            if (Core.Me.Distance2D(point) > .35f)
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(point, "Crucible: Voidmancer staging") { DistanceTolerance = .35f, UseMount = false });
                moving = true;
            }
            else if (moving)
            {
                Navigator.Stop();
                moving = false;
            }
            // Tick returns no scheduling success: rotation/healing must keep
            // running while the positioning capability is held at destination.
        }
        private void Release()
        {
            if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
                Navigator.Stop();
            if (owned)
            {
                CapabilityManager.Clear(semantic, CapabilityFlags.Movement, "Crucible: Voidmancer semantic resolved");
                CapabilityManager.Clear(semantic, CapabilityFlags.Facing, "Crucible: Voidmancer semantic resolved");
            }
            owned = false;
            moving = false;
            destination = null;
            marchHeading = null;
        }
        private sealed class Circle
        {
            internal Vector3 Position;
            internal float Radius;
            internal DateTime Until;
        }
    }
}
