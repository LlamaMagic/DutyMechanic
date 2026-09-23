using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons
{
    // Publishes final-encounter hazards without taking ownership of bomb targeting.
    // A targetable bomb is not necessarily the bomb that should be pushed.
    internal sealed class LoosefroxMechanics
    {
        private static readonly Vector3 Center = new Vector3(520, 0, -420);
        private readonly Dictionary<uint, uint> casts = new Dictionary<uint, uint>();
        private readonly List<Shape> hazards = new List<Shape>();
        private DateTime nextLog;
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
        }

        private IEnumerable<Shape> Current()
        {
            var now = DateTime.UtcNow;
            return hazards.Where(h => h.Until > now).ToArray();
        }

        internal void Tick()
        {
            if (!InArena() || !Core.Me.IsAlive)
            {
                casts.Clear();
                hazards.Clear();
                return;
            }

            var now = DateTime.UtcNow;
            hazards.RemoveAll(h => h.Until <= now);
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.Distance2D(Center) < 65).ToArray();
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
                    hazards.Add(Circle(actor.Location, id == 48235 ? 12.5f : 8.5f, until));
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
                    hazards.Add(Rect(actor.Location, actor.Heading, 4.5f, 40.5f, until));
                    hazards.Add(Rect(actor.Location, actor.Heading + (float)Math.PI / 2, 4.5f, 40.5f, until));
                }

                if (id == 48231)
                {
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

                        hazards.Add(new Shape { Position = actor.Location, Points = points.ToArray(), Until = until });
                    }
                }
            // Earthquake 48239 is the survivor's raidwide/enrage. Marking
            // its whole arena unsafe would prevent the required damage.
            }

            if (now >= nextLog)
            {
                nextLog = now.AddSeconds(1);
                ff14bot.Helpers.Logging.Write("[CrucibleFinalState] hp={0:F1} pos={1} pits={2} hazards={3} actors={4}", Core.Me.CurrentHealthPercent, Core.Me.Location, GameObjectManager.GameObjects.Count(IsPit), hazards.Count, string.Join(";", actors.Where(a => a.BaseId >= 0x4C65 && a.BaseId <= 0x4C69 || a.Icon != 0).Select(a => a.ObjectId.ToString("X") + ":" + a.BaseId.ToString("X") + ":" + a.CurrentHealthPercent.ToString("F1") + ":targetable=" + a.IsTargetable + ":icon=" + a.Icon + ":pos=" + a.Location)));
            }
        }

        // Event-object identity and visibility delimit the persistent hazard;
        // targetability is irrelevant for noncombat quicksand actors.
        private static bool IsPit(GameObject actor) => actor.BaseId == 0x1EC026 && actor.IsVisible && actor.Distance2D(Center) < 35;
        private static Shape Circle(Vector3 position, float radius, DateTime until) => new Shape
        {
            Position = position,
            Until = until,
            Points = Enumerable.Range(0, 64).Select(i => new Vector2((float)Math.Sin(i * Math.PI / 32) * radius, (float)Math.Cos(i * Math.PI / 32) * radius)).ToArray()
        };
        private static Shape Rect(Vector3 position, float heading, float halfWidth, float halfLength, DateTime until) => new Shape
        {
            Position = position,
            Heading = heading,
            Until = until,
            Points = new[]
            {
                new Vector2(-halfWidth, -halfLength),
                new Vector2(halfWidth, -halfLength),
                new Vector2(halfWidth, halfLength),
                new Vector2(-halfWidth, halfLength)
            }
        };
        private sealed class Shape
        {
            internal Vector3 Position;
            internal float Heading;
            internal Vector2[] Points;
            internal DateTime Until;
        }
    }
}
