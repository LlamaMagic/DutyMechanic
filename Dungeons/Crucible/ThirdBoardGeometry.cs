using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons
{
    // Detached X/Z geometry is shared with the replay harness. In particular,
    // Guttler's alcoves are part of the floor, not obstacles to simplify away.
    internal static class ThirdBoardGeometry
    {
        // Bleeding at the arena rim is persistent damage, not harmless wall
        // contact. Reserve player radius(.5), arrival tolerance(.35), and .4y
        // of pulse travel. Keep this shared by native boundaries and planners.
        internal const float EdgeClearance = 1.25f;
        // Staging leaves a .75-yalm navigable interior, validated with another
        // .5 yalm of clearance. Sample the interior as well as the perimeter so
        // a narrow hazard cannot hide between the center and outer ring.
        internal static bool RefugeFits(Vector2 center, float radius, Func<Vector2, bool> safe) => safe(center) && Enumerable.Range(1, 5).All(r => Enumerable.Range(0, 32).All(i => safe(center + Direction(i * MathF.PI / 16) * (radius * r / 5))));
        internal static ThirdBoardHazard[] RefugeMask(Vector2 center) => // The 140-yalm outer radius covers every position in the encounter
        // activation area even when the refuge is at the opposite edge.
        // Existing arena boundaries remain published independently; leaving
        // the ring's exterior can never become an alternative safe escape.
        ThirdBoardHazard.Donut(.75f, 140).Select(points => new ThirdBoardHazard { Origin = center, Points = points, Persistent = true }).ToArray();
        internal static bool InArena(Vector2 p, uint boss, float inset = .5f)
        {
            // Negative insets are observation envelopes for cast origins, not
            // movement destinations. Preserve them for helpers outside the wall.
            if (inset >= 0)
            {
                inset = Math.Max(inset, EdgeClearance);
            }

            if (boss == 0x4CAA)
            {
                // Inset the UNION, not its component rectangles: separately
                // shrinking rectangles disconnects each alcove from the room.
                return Floor(p) && Enumerable.Range(0, 16).All(i => Floor(p + Direction(i * MathF.PI / 8) * inset));
            }

            if (boss == 0x4C9B || boss == 0x4CA1)
            {
                return Vector2.Distance(p, new Vector2(120, -420)) <= 20 - inset;
            }

            return Math.Abs(p.X - 120) <= 20 - inset && Math.Abs(p.Y) <= 20 - inset;
        }

        private static bool Floor(Vector2 p) => Box(p, 520, -420, 10, 20) || Box(p, 520, -397.5f, 2.5f, 2.5f) || Box(p, 520, -442.5f, 2.5f, 2.5f) || Box(p, 508, -412.5f, 2.5f, 2.5f) || Box(p, 508, -422.5f, 2.5f, 2.5f) || Box(p, 532, -417.5f, 2.5f, 2.5f) || Box(p, 532, -427.5f, 2.5f, 2.5f);
        private static bool Box(Vector2 p, float x, float z, float halfX, float halfZ) => Math.Abs(p.X - x) <= halfX && Math.Abs(p.Y - z) <= halfZ;
        internal static Vector2 Center(uint boss) => boss == 0x4CAA ? new Vector2(520, -420) : boss == 0x4C9B || boss == 0x4CA1 ? new Vector2(120, -420) : new Vector2(120, 0);
        internal static Vector2 Direction(float heading) => new Vector2(MathF.Sin(heading), MathF.Cos(heading));
        internal static float Heading(Vector2 direction) => MathF.Atan2(direction.X, direction.Y);
        // Damage uptime is a preference after mechanic safety, never an excuse
        // to cross a hazard. Callers validate the full pending hazard set along
        // the approach, not merely its endpoint. Retaining an eligible held point
        // prevents moving bosses and nearly equal candidates from causing jitter.
        // The caller supplies native melee reach with its normal arrival inset.
        internal static Vector2? PreferMeleeRefuge(Vector2? held, Vector2 current, Vector2 target, float range, IEnumerable<Vector2> candidates, Func<Vector2, bool> safe, Func<Vector2, Vector2, bool> corridor)
        {
            bool Melee(Vector2 p) => Vector2.Distance(p, target) <= range;
            if (held.HasValue && Melee(held.Value) && safe(held.Value))
            {
                return held;
            }

            if (Melee(current) && safe(current))
            {
                return current;
            }

            // Add a melee-ring sample set so a coarse one-yalm arena grid cannot
            // miss the narrow safe strip outside a persistent field like Sludge.
            var ring = Enumerable.Range(0, 32).Select(i => target + Direction(i * MathF.PI / 16) * range);
            return ring.Concat(candidates).Where(p => Melee(p) && safe(p) && corridor(current, p)).OrderBy(p => Vector2.Distance(p, current)).Select(p => (Vector2? )p).FirstOrDefault();
        }

        // A cage binds immediately, then Life Claim hits its own and four
        // adjacent ten-yalm tiles. Reserve both arms on discovery so positioning
        // cannot choose a refuge just outside the body but inside the explosion.
        // The half-yalm margin is shared by prediction and the observed cast.
        internal static ThirdBoardHazard[] CageForecast(uint actor, Vector2 origin, float heading, DateTime until) => new[]
        {
            heading,
            heading + MathF.PI / 2
        }.Select(angle => new ThirdBoardHazard { Actor = actor, Action = 48611, Origin = origin, Heading = angle, Points = ThirdBoardHazard.Rectangle(5.5f, 15.5f, 15.5f), Until = until, Persistent = true }).ToArray();
        // Landslip's observed 13.7-yalm displacement spans several ticks, so an
        // adjacent-tick threshold misses it. Compare with the last
        // pre-effect position and require displacement along the authored lane.
        internal static bool LandslipMoved(Vector2 before, Vector2 after, float heading)
        {
            var direction = Direction(heading);
            var delta = after - before;
            return Vector2.Dot(delta, direction) >= 3 && Math.Abs(delta.X * direction.Y - delta.Y * direction.X) < 3;
        }

        // Compare a bounded movement window: client interpolation can split a
        // teleport across ticks. Six yalms in at most half a second exceeds
        // ordinary pursuit speed. The 14y gate accepts an interpolated edge
        // arrival without treating ordinary movement around arena center as Melody.
        internal static bool SirenRelocated(Vector2 before, Vector2 after, double seconds) => seconds > 0 && seconds <= .5 && Vector2.Distance(before, after) > 6 && Vector2.Distance(after, Center(0x4CA1)) > 14;
        // Captured cardinal/diagonal Siren teleport endpoints are 21y from
        // center. Project interpolated positions onto that ring so a second
        // movement sample cannot drag the cone/refuge through the arena.
        internal static Vector2 SirenEdgeOrigin(Vector2 position) => Center(0x4CA1) + Vector2.Normalize(position - Center(0x4CA1)) * 21;
        internal static bool MelodyRefuge(Vector2 point, Vector2 origin, ThirdBoardHazard[] hazards, bool prepareMarch)
        {
            // The old 12y proximity gate removed the usable flank once the
            // complete staging disk and bleeding inset were applied (including
            // the second Melody at 120.870,-440.982). Proximity is a ranking
            // preference, not a mechanic safety condition.
            if (!InArena(point, 0x4CA1) || hazards.Any(h => h.Contains(point)))
            {
                return false;
            }

            if (!Enumerable.Range(0, 8).All(i => hazards.All(h => !h.Contains(point + Direction(i * MathF.PI / 4) * .6f))))
            {
                return false;
            }

            if (!prepareMarch)
            {
                return true;
            }

            var inward = Vector2.Normalize(Center(0x4CA1) - point);
            // Melody ends BEFORE march. Requiring its cone to remain clear
            // along the future march rejected the intended inward runway.
            return Corridor(point, point + inward * 18.5f, 0x4CA1, hazards.Where(h => h.Action != 48566));
        }

        internal static ThirdBoardHazard[] SelectWave(ThirdBoardHazard[] pending, uint boss)
        {
            var first = pending.Where(h => !h.Persistent).OrderBy(h => h.Until).FirstOrDefault();
            var imminent = pending.Where(h => h.Persistent || first == null || h.Until <= first.Until.AddMilliseconds(350)).ToArray();
            var combined = pending.Where(h => h.Persistent || first == null || h.Until <= first.Until.AddSeconds(2)).ToArray();
            return combined.Length != imminent.Length && Candidates(boss).Any(p => combined.All(h => !h.Contains(p))) ? combined : imminent;
        }

        internal static IEnumerable<Vector2> Candidates(uint boss, float spacing = 1)
        {
            var center = Center(boss);
            for (float x = -24; x <= 24; x += spacing)
            {
                for (float z = -24; z <= 24; z += spacing)
                {
                    var point = center + new Vector2(x, z);
                    if (InArena(point, boss, .8f))
                    {
                        yield return point;
                    }
                }
            }
        }

        internal static bool Corridor(Vector2 start, Vector2 end, uint boss, IEnumerable<ThirdBoardHazard> hazards)
        {
            var shapes = hazards.ToArray();
            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(start, end) * 4));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(start, end, i / (float)steps);
                if (!InArena(p, boss, .8f) || shapes.Any(h => h.Contains(p)))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool WaterEscapeCorridor(Vector2 start, Vector2 end, ThirdBoardHazard[] hazards)
        {
            // Water II can leave the player inside a circle when escape starts.
            // Permit leaving that circle, never
            // entering another hazard or moving deeper into the occupied circle.
            if (hazards.Any(h => h.Contains(end)))
            {
                return false;
            }

            foreach (var hazard in hazards.Where(h => h.Contains(start)))
            {
                if (hazard.Action != 48483 || Vector2.Distance(start, hazard.Origin) > .25f && Vector2.Dot(start - hazard.Origin, end - start) < -.01f)
                {
                    return false;
                }
            }

            int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(start, end) * 4));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(start, end, i / (float)steps);
                if (!InArena(p, 0x4C93, .8f) || hazards.Any(h => !h.Contains(start) && h.Contains(p)))
                {
                    return false;
                }
            }

            return true;
        }
    }

    // RB invalidates its escape path whenever an obstacle moves. Keep each eye's
    // published identity and center stable between updates instead of exposing
    // the per-tick prediction object through the nominal 200ms publication cache.
    // The extra .5y covers the maximum retained displacement without consuming
    // the existing 2.5y contact circle's safety margin. Unexpected fast movement
    // bypasses the timer; expiry/removal remains owned by the live source list.
    internal sealed class ThirdBoardEyePublication
    {
        private DateTime nextUpdate;
        internal ThirdBoardHazard Hazard { get; }

        internal ThirdBoardEyePublication(ThirdBoardHazard source, DateTime now)
        {
            Hazard = new ThirdBoardHazard
            {
                Actor = source.Actor,
                Action = source.Action,
                Origin = source.Origin,
                Points = ThirdBoardHazard.Circle(3.01f),
                Persistent = true
            };
            // .01y compensates for the inscribed 96-sided circle approximation.
            nextUpdate = now.AddMilliseconds(200);
        }

        internal ThirdBoardHazard Read(ThirdBoardHazard source, DateTime now)
        {
            float displacement = Vector2.Distance(source.Origin, Hazard.Origin);
            if (displacement > .5f || now >= nextUpdate && displacement >= .25f)
            {
                Hazard.Origin = source.Origin;
                nextUpdate = now.AddMilliseconds(200);
            }

            Hazard.Until = source.Until;
            return Hazard;
        }
    }

    // Values here are snapshots, never retained RB object wrappers. Polygons
    // and point containment use the same dimensions to avoid planner/graph drift.
    internal sealed class ThirdBoardHazard
    {
        internal uint Actor, Action;
        internal Vector2 Origin;
        internal float Heading;
        internal Vector2[] Points;
        internal DateTime Until;
        internal bool Persistent;
        internal bool Contains(Vector2 point)
        {
            var delta = point - Origin;
            float c = MathF.Cos(Heading), s = MathF.Sin(Heading);
            var p = new Vector2(delta.X * c - delta.Y * s, delta.X * s + delta.Y * c);
            bool inside = false;
            for (int i = 0, j = Points.Length - 1; i < Points.Length; j = i++)
            {
                var a = Points[i];
                var b = Points[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        internal static Vector2[] Circle(float radius) => Enumerable.Range(0, 96).Select(i => ThirdBoardGeometry.Direction(i * MathF.PI / 48) * radius).ToArray();
        // A moving contact hazard must cover its present body and the full path
        // to the predicted center, not only the destination. Two semicircles
        // form one convex capsule usable by both RB and the bounded planner.
        // Circumscribe the 96-segment approximation so its chords do not shave
        // the caller's contact radius or safety margin. Zero motion stays a disk.
        internal static Vector2[] SweptCircle(float radius, Vector2 travel)
        {
            float outer = radius / MathF.Cos(MathF.PI / 96);
            if (travel.LengthSquared() < .000001f)
                return Circle(outer);
            float angle = MathF.Atan2(travel.Y, travel.X);
            var points = new Vector2[98];
            for (int i = 0; i <= 48; i++)
            {
                float forward = angle - MathF.PI / 2 + i * MathF.PI / 48;
                float rear = angle + MathF.PI / 2 + i * MathF.PI / 48;
                points[i] = travel + new Vector2(MathF.Cos(forward), MathF.Sin(forward)) * outer;
                points[49 + i] = new Vector2(MathF.Cos(rear), MathF.Sin(rear)) * outer;
            }

            return points;
        }

        internal static Vector2[] Rectangle(float halfWidth, float length, float back = .5f) => new[]
        {
            new Vector2(-halfWidth, -back),
            new Vector2(halfWidth, -back),
            new Vector2(halfWidth, length),
            new Vector2(-halfWidth, length)
        };
        internal static Vector2[] Cone(float radius, float halfAngle) => new[]
        {
            Vector2.Zero
        }.Concat(Enumerable.Range(0, 65).Select(i => ThirdBoardGeometry.Direction((-halfAngle + halfAngle * 2 * i / 64) * MathF.PI / 180) * radius)).ToArray();
        // A ring is emitted as independent convex wedges. A polygon with a
        // bridged hole is not a supported navigation obstacle representation.
        internal static IEnumerable<Vector2[]> Donut(float inner, float outer)
        {
            for (int i = 0; i < 64; i++)
            {
                var a = ThirdBoardGeometry.Direction(i * MathF.PI / 32);
                var b = ThirdBoardGeometry.Direction((i + 1) * MathF.PI / 32);
                yield return new[]
                {
                    a * inner,
                    a * outer,
                    b * outer,
                    b * inner
                };
            }
        }
    }
}
