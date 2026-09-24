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
    // Resolves paired cleaves in effect order, with separate knockback and spin
    // lifetimes. Helper timing remains an estimate; actor death releases the pair.
    internal sealed class TablitaurMechanics
    {
        private const uint Elder = 0x4C5F, Younger = 0x4C60;
        private const uint Swipe = 48190, SwipeLong = 48192, Swing = 48194, SwingLong = 48196;
        private const uint Stomp = 48198, StompLong = 48201, Shockwave = 48199, ShockwaveLong = 48202;
        private const uint Spin = 48205, FirstCone = 48210, NextCone = 48212, Slash = 48204;
        private static readonly Vector3 Center = new Vector3(120, 0, 0);
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly List<Hazard> hazards = new List<Hazard>();
        private readonly List<Hazard> knockbacks = new List<Hazard>();
        private readonly CapabilityManagerHandle movement = CapabilityManager.CreateNewHandle();
        // Keep polygon identity stable while its origin follows the spinner;
        // allocating a new hazard each pulse restarts native avoidance paths.
        private readonly Hazard spinHazard = Circle(Center, 8.5f, default);
        // Reuse the sampled one-second predictor used for moving contact fields.
        // A position-only disk let the pursuing brother catch Kember before the
        // next dodge, knocking him through the arena boundary on September24.
        // Chase-target direction handles turns; measured travel sets the minimum
        // one-second lookahead. Reset samples between actors/phases.
        private CloudMotionPrediction spinMotion = new CloudMotionPrediction();
        private System.Numerics.Vector2 spinLead;
        private uint spinActor, swipeActor;
        private int coneCount;
        private DateTime spinEnd, nextLog;
        private bool held, moving;
        private bool manualDodge;
        private System.Numerics.Vector2? dodgePoint;
        private Vector3? knockbackPoint;
        private Hazard nextCone;
        private float? lastConeHeading;
        private uint coneHelper;
        private uint slashActor;
        private DateTime slashUntil;
        private Vector3? slashPoint;
        // Voidmancer shares this sub-arena. Require a unique nearby brother;
        // territory/coordinates alone would activate the wrong encounter owner.
        private static bool InArena() => WorldManager.ZoneId == 1340 && Core.Me.Distance2D(Center) < 40 && GameObjectManager.GameObjects.Any(a => (a.BaseId == Elder || a.BaseId == Younger) && a.Distance2D(Center) < 40);
        internal void Register()
        {
            AvoidanceHelpers.AddAvoidSquareDonut(InArena, 39, 39, 100, 100, () => new[] { Center });
            AvoidanceManager.AddAvoidPolygon<Hazard>(InArena, null, 60, h => -h.Heading, h => 1, h => 15, h => h.Points, h => h.Position, NativeCurrent, priority: AvoidancePriority.High);
        }

        // Stable destination selection must not hide obstacles from the graph.
        // Native emergency escape has priority; we resume the same destination
        // through Navigator once it relinquishes movement.
        private IEnumerable<Hazard> NativeCurrent() => Current();
        private IEnumerable<Hazard> Current()
        {
            var now = DateTime.UtcNow;
            var ordered = hazards.Where(h => h.Until > now).OrderBy(h => h.Until).ToArray();
            // Simultaneous publication of opposing cleaves makes the entire
            // floor unsafe. Only the first effect owns avoidance until resolved.
            var current = ordered.Length == 0 ? new List<Hazard>() : ordered.Where(h => h.Until <= ordered[0].Until.AddMilliseconds(200)).ToList();
            var spinner = GameObjectManager.GetObjectByObjectId(spinActor) as BattleCharacter;
            if (spinner != null && spinner.IsAlive && now < spinEnd)
            {
                spinHazard.Position = spinner.Location;
                spinHazard.Until = spinEnd;
                var origin = new System.Numerics.Vector2(spinner.Location.X, spinner.Location.Z);
                var measured = spinMotion.Observe(origin, now);
                var lead = TablitaurSpinPrediction.Lead(origin,
                    new System.Numerics.Vector2(Core.Me.Location.X, Core.Me.Location.Z), measured);
                // Preserve the current 8.5y contact disk and its forward capsule.
                // Do not let stale velocity aim away from the player after a turn.
                // The shared sampler caps interpolation jumps at six yalms.
                // Both graph avoidance and manual goals consume this same shape.
                if (lead != spinLead)
                {
                    spinLead = lead;
                    spinHazard.Points = ThirdBoardHazard.SweptCircle(8.5f, spinLead).Select(p => new Vector2(p.X, p.Y)).ToArray();
                }
                current.Add(spinHazard);
            }

            // Forecast is concurrent with the current cone/spinner, not part of
            // the ordered paired-cleave queue, which would hide it until too late.
            if (nextCone != null && nextCone.Until > now && now < spinEnd)
                current.Add(nextCone);
            return current;
        }

        internal void Tick()
        {
            if (!InArena() || !Core.Me.IsAlive)
            {
                Release();
                casts.Clear();
                hazards.Clear();
                knockbacks.Clear();
                spinActor = swipeActor = 0;
                spinMotion = new CloudMotionPrediction();
                spinLead = default;
                coneCount = 0;
                nextCone = null;
                lastConeHeading = null;
                coneHelper = 0;
                slashActor = 0;
                slashUntil = default;
                slashPoint = null;
                return;
            }

            var now = DateTime.UtcNow;
            hazards.RemoveAll(h => h.Until <= now);
            knockbacks.RemoveAll(h => h.Until <= now);
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Center) < 45).ToArray();
            foreach (var actor in actors)
            {
                uint id = actor.IsCasting ? actor.CastingSpellId : 0;
                if (casts.TryGetValue(actor.ObjectId, out var old) && old == id)
                    continue;
                casts[actor.ObjectId] = id;
                if (id == 0)
                    continue;
                var cast = actor.SpellCastInfo;
                // 750 ms is the provisional helper effect fence. Slash 48204 is
                // a targeted tankbuster, not a fixed cast-start rectangle:
                // 46732 was its explicit target and took 417 damage while the
                // false avoid held it12y from the boss for9s. Bait it away from
                // the pet while the routine owns mitigation; do not evade a
                // rectangle that continuously tracks the player himself.
                var until = now + cast.RemainingCastTime + TimeSpan.FromMilliseconds(750);
                ff14bot.Helpers.Logging.Write("[CrucibleTablitaurCast] id={0} actor={1:X} base={2:X} pos={3} heading={4:F4} ground={5} remaining={6:F3}", id, actor.ObjectId, actor.BaseId, actor.Location, actor.Heading, cast.CastLocation, cast.RemainingCastTime.TotalSeconds);
                if (id == Slash && actor.BaseId == Elder && cast.TargetId == Core.Me.ObjectId)
                {
                    slashActor = actor.ObjectId;
                    // Captured damage arrived1.08s after the cast bar ended.
                    slashUntil = now + cast.RemainingCastTime + TimeSpan.FromSeconds(1.4);
                    slashPoint = null;
                }
                if (id == Swipe || id == SwipeLong)
                    hazards.Add(Rect(actor.Location, actor.Heading, 30.5f, 60, until));
                if (id == Swing || id == SwingLong || id == Stomp || id == StompLong)
                    hazards.Add(Circle(actor.Location, id == Swing || id == SwingLong ? 23.5f : 5.5f, until));
                if (id == Shockwave || id == ShockwaveLong)
                    knockbacks.Add(new Hazard { Position = actor.Location, Until = until });
                if (id == Spin && (actor.BaseId == Elder || actor.BaseId == Younger))
                {
                    spinActor = actor.ObjectId;
                    spinMotion = new CloudMotionPrediction();
                    spinLead = default;
                    spinHazard.Points = Circle(Center, 8.5f, default).Points;
                    coneCount = 0;
                    swipeActor = 0;
                    nextCone = null;
                    lastConeHeading = null;
                    coneHelper = 0;
                    // Reset by 13 observed cone casts or actor death;30 s only
                    // bounds stale state if a helper disappears during a wipe.
                    spinEnd = now.AddSeconds(30);
                }

                if (id == 48208 || id == 48209)
                    swipeActor = actor.ObjectId;
                if (id == FirstCone || id == NextCone)
                {
                    hazards.Add(Cone(actor.Location, actor.Heading, until));
                    if (++coneCount >= 13)
                        spinEnd = until;
                    // Arelia 1456 helper 48212 offered only 200 ms warning; successive
                    // headings decreased by 30 degrees every 1.5-1.67 s. Infer the
                    // direction only from two matching helper casts, then publish
                    // ONE next sector early. Unexpected steps disable prediction;
                    // the captured current sector always remains registered.
                    nextCone = null;
                    if (id == NextCone && coneHelper == actor.ObjectId && lastConeHeading.HasValue && coneCount < 13)
                    {
                        float step = (float)Math.Atan2(Math.Sin(actor.Heading - lastConeHeading.Value), Math.Cos(actor.Heading - lastConeHeading.Value));
                        if (Math.Abs(Math.Abs(step) - Math.PI / 6) < .06)
                        {
                            nextCone = Cone(actor.Location, actor.Heading + step, until.AddSeconds(1.5));
                            ff14bot.Helpers.Logging.Write("[CrucibleTablitaurForecast] count={0} heading={1:F4} next={2:F4}", coneCount, actor.Heading, nextCone.Heading);
                        }
                    }

                    coneHelper = actor.ObjectId;
                    lastConeHeading = actor.Heading;
                }
            }

            // Either brother's death cancels the paired sequence, even before
            // swipeActor is known. Keeping the spin then would block movement
            // during Endless Slashes.
            // Do not clear unrelated pending cast hazards/knockbacks here.
            if (spinActor != 0 && (now >= spinEnd || !actors.Any(a => a.BaseId == Elder && a.IsAlive) || !actors.Any(a => a.BaseId == Younger && a.IsAlive)))
            {
                ff14bot.Helpers.Logging.Write("[CrucibleTablitaurRelease] Paired spin ended: spinner={0:X}, swipe={1:X}, expired={2}.", spinActor, swipeActor, now >= spinEnd);
                spinActor = swipeActor = 0;
                spinEnd = default;
                nextCone = null;
            }

            if (now < slashUntil && PositionSlash())
            {
                // Positive bait positioning leaves rotation and mitigation
                // schedulable; it owns only movement/facing while traveling.
            }
            else if (hazards.Count > 0 || knockbacks.Count > 0 || now < spinEnd && spinActor != 0)
            {
                if (!held && !AvoidanceManager.IsRunningOutOfAvoid)
                    MovementManager.MoveStop();
                CapabilityManager.Update(movement, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: Tablitaur mechanic positioning");
                held = true;
                if (knockbacks.Count > 0)
                {
                    manualDodge = false;
                    dodgePoint = null;
                    PositionKnockback(now);
                }
                else
                    PositionDodge();
            }
            else
                Release();
            if (now >= nextLog)
            {
                nextLog = now.AddSeconds(1);
                ff14bot.Helpers.Logging.Write("[CrucibleTablitaurState] player={0:F1} pos={1} bosses={2} hazards={3} knockbacks={4} spinner={5:X} cones={6} spinPosition={7} oneSecondLead={8}", Core.Me.CurrentHealthPercent, Core.Me.Location, string.Join(";", actors.Where(a => a.BaseId == Elder || a.BaseId == Younger).Select(a => a.BaseId.ToString("X") + ":" + a.CurrentHealthPercent.ToString("F1"))), hazards.Count, knockbacks.Count, spinActor, coneCount, spinHazard.Position, spinLead);
            }
        }

        private bool PositionSlash()
        {
            var boss = GameObjectManager.GetObjectByObjectId(slashActor) as BattleCharacter;
            var pet = Core.Me.Pet;
            if (boss == null || !boss.IsAlive || pet == null || !pet.IsAlive) return false;
            var origin = new System.Numerics.Vector2(boss.Location.X, boss.Location.Z);
            var familiar = new System.Numerics.Vector2(pet.Location.X, pet.Location.Z);
            bool Safe(Vector3 point) => Math.Abs(point.X - Center.X) < 18.5f && Math.Abs(point.Z) < 18.5f &&
                TablitaurSlashBait.PetIsClear(origin, new System.Numerics.Vector2(point.X, point.Z), familiar, pet.CombatReach);
            if (!slashPoint.HasValue || !Safe(slashPoint.Value))
            {
                // Prefer the current melee point if already separated; otherwise
                // use the nearest safe angle within actual axe reach. This phase
                // overlaps Cheer, not the moving spin or the paired floor cleaves.
                float reach = Math.Max(3, boss.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range - .5f);
                slashPoint = Safe(Core.Me.Location) ? Core.Me.Location : Enumerable.Range(0, 32)
                    .Select(i => boss.Location + new Vector3((float)Math.Sin(i * Math.PI / 16) * reach, 0, (float)Math.Cos(i * Math.PI / 16) * reach))
                    .Where(Safe).OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
                if (slashPoint.HasValue)
                    ff14bot.Helpers.Logging.Write("[CrucibleSlashBait] destination={0} boss={1} pet={2}", slashPoint, boss.Location, pet.Location);
            }
            if (!slashPoint.HasValue) return false;
            CapabilityManager.Update(movement, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Crucible: separate Slash from pet");
            held = true;
            if (AvoidanceManager.IsRunningOutOfAvoid) { moving = false; return true; }
            if (Core.Me.Distance2D(slashPoint.Value) > .3f)
            {
                CapabilityManager.Update(movement, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: Slash bait transit");
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(slashPoint.Value, "Crucible: Slash pet separation") { DistanceTolerance = .3f, UseMount = false });
                moving = true;
            }
            else
            {
                if (moving) Navigator.Stop();
                moving = false;
                CapabilityManager.Clear(movement, CapabilityFlags.Facing, "Crucible: Slash bait reached");
            }
            return true;
        }

        private void PositionDodge()
        {
            // 104664 alternated native escape/arrival at frame rate despite the
            // CR movement lock. Use the replayed stable-position selector for
            // circles, swipes and the spinner as one destination owner. Native
            // graph navigation routes around the continuously registered avoids.
            // The 18.5 y disk fits inside this square;
            // no corner is required by these observed half-room/circle casts.
            var polygons = Current().Select(h => h.Points.Select(p => new System.Numerics.Vector2(h.Position.X + p.X * (float)Math.Cos(h.Heading) + p.Y * (float)Math.Sin(h.Heading), h.Position.Z - p.X * (float)Math.Sin(h.Heading) + p.Y * (float)Math.Cos(h.Heading))).ToArray()).ToArray();
            var start = new System.Numerics.Vector2(Core.Me.Location.X, Core.Me.Location.Z);
            // 1456 changed destination every~100 ms as the spinner chased its
            // minimum-clearance goal. New goals get 4 y clearance (0.67 s travel at
            // normal 6 y/s); retain them until the standard 1.5 y margin is breached.
            // If that extra space is unavailable, fall back to the proven margin.
            bool chasing = spinActor != 0 && DateTime.UtcNow < spinEnd;
            // Damage preference never reduces the chasing spinner's clearance.
            // September24's spin chose a new melee goal every100–300ms and
            // crossed the central swipe fan repeatedly. The chasing phase
            // requires continuous clearance, not promotion toward the other
            // brother. Preserve safe goals and validate the travel corridor;
            // native escape remains the fallback if no such corridor exists.
            var meleePreference = chasing ? null : CrucibleMeleePreference.Capture();
            var chosen = ManticoreSafePosition.Choose(start, new System.Numerics.Vector2(120, 0), 18.5f, polygons, dodgePoint, chasing, chasing ? 4f : .75f, chasing ? 4f : 1.5f, meleePreference);
            if (!chosen.HasValue && chasing)
                chosen = ManticoreSafePosition.Choose(start, new System.Numerics.Vector2(120, 0), 18.5f, polygons, dodgePoint, true);
            manualDodge = chosen.HasValue;
            if (!manualDodge)
            {
                if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
                    Navigator.Stop();
                dodgePoint = null;
                moving = false;
                return;
            }

            CapabilityManager.Update(movement, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: face stable Tablitaur dodge");
            // Native emergency escape owns movement until it resolves; the
            // semantic graph route resumes only after that handoff.
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }

            if (dodgePoint != chosen)
                ff14bot.Helpers.Logging.Write("[CrucibleTablitaurHold] from={0} destination={1} hazards={2}", start, chosen, polygons.Length);
            dodgePoint = chosen;
            if (System.Numerics.Vector2.DistanceSquared(start, chosen.Value) > .3f * .3f)
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(new Vector3(chosen.Value.X, 0, chosen.Value.Y), "Crucible: stable Tablitaur dodge") { DistanceTolerance = .3f, UseMount = false });
                moving = true;
            }
            else if (moving)
            {
                Navigator.Stop();
                moving = false;
            }
        }

        private void PositionKnockback(DateTime now)
        {
            var source = knockbacks.OrderBy(h => h.Until).FirstOrDefault();
            if (source == null)
            {
                knockbackPoint = null;
                return;
            }

            // The same forward mover as Wyvern requires facing ownership while
            // staging; the combat routine remains free to command pets/attacks.
            CapabilityManager.Update(movement, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Crucible: face Tablitaur knockback staging");
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                moving = false;
                return;
            }

            bool Safe(Vector3 p)
            {
                var delta = p - source.Position;
                float length = (float)Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
                if (length < 6.5f)
                    return false;
                var landing = p + delta * (20.5f / length);
                return Math.Abs(p.X - Center.X) < 19 && Math.Abs(p.Z) < 19 && Math.Abs(landing.X - Center.X) < 19 && Math.Abs(landing.Z) < 19;
            }

            if (!knockbackPoint.HasValue || !Safe(knockbackPoint.Value))
            {
                knockbackPoint = null;
                float best = float.MaxValue;
                for (int x = -18; x <= 18; x++)
                    for (int z = -18; z <= 18; z++)
                    {
                        var p = Center + new Vector3(x, 0, z);
                        float d = p.Distance2D(Core.Me.Location);
                        if (d >= best || !Safe(p))
                            continue;
                        best = d;
                        knockbackPoint = p;
                    }

                ff14bot.Helpers.Logging.Write("[CrucibleTablitaurKnockback] source={0} destination={1} remaining={2:F2}", source.Position, knockbackPoint, (source.Until - now).TotalSeconds);
            }

            if (!knockbackPoint.HasValue)
                return;
            if (Core.Me.Distance2D(knockbackPoint.Value) > .3f)
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(knockbackPoint.Value, "Crucible: Tablitaur knockback staging") { DistanceTolerance = .3f, UseMount = false });
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
                Navigator.Stop();
            moving = false;
            knockbackPoint = null;
            manualDodge = false;
            dodgePoint = null;
            if (held)
                CapabilityManager.Clear(movement, CapabilityFlags.Movement | CapabilityFlags.Facing, "Crucible: Tablitaur effect resolved");
            held = false;
        }

        private static Hazard Circle(Vector3 p, float radius, DateTime until) => new Hazard
        {
            Position = p,
            Until = until,
            Points = Enumerable.Range(0, 64).Select(i => new Vector2((float)Math.Sin(i * Math.PI / 32) * radius, (float)Math.Cos(i * Math.PI / 32) * radius)).ToArray()
        };
        private static Hazard Rect(Vector3 p, float heading, float width, float length, DateTime until) => new Hazard
        {
            Position = p,
            Heading = heading,
            Until = until,
            Points = new[]
            {
                new Vector2(-width, -.5f),
                new Vector2(width, -.5f),
                new Vector2(width, length + .5f),
                new Vector2(-width, length + .5f)
            }
        };
        private static Hazard Cone(Vector3 p, float heading, DateTime until) => new Hazard
        {
            Position = p,
            Heading = heading,
            Until = until,
            Points = new[]
            {
                new Vector2(0, -.5f)
            }.Concat(Enumerable.Range(0, 17).Select(i =>
            {
                double a = (-31 + 62.0 * i / 16) * Math.PI / 180;
                return new Vector2((float)Math.Sin(a) * 40.5f, (float)Math.Cos(a) * 40.5f);
            })).ToArray()
        };
        private sealed class Hazard
        {
            internal Vector3 Position;
            internal float Heading;
            internal DateTime Until;
            internal Vector2[] Points;
        }
    }
}
