using System.Numerics;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons
{
    // Second Master geometry uses captured helper positions and action IDs.
    // Keep it independent of game memory so the same rules can be replayed offline.
    internal static class SecondMasterGeometry
    {
        internal static readonly Vector2 FlaurosCenter = new(120, -420);
        // Shared1.25y wall clearance reserves player size and movement tolerance.
        internal const float FlaurosRadius = 20 - ThirdBoardGeometry.EdgeClearance;
        internal static readonly Vector2 DrakeCenter = new(520, 0);
        internal static readonly Vector2 DurgaCenter = new(120, 0);
        // Lauda uses Guttler's authored gim05 floor at520,-420, including the
        // same six alcoves. Reuse its inset union so narrow pocket entrances
        // stay connected; separately shrinking rectangles disconnects them.
        internal static readonly Vector2 LaudaCenter = new(520, -420);
        internal static bool InLaudaFloor(Vector2 p) => ThirdBoardGeometry.InArena(p, 0x4CAA);
        // The floor is static; do not repeat its inset-union sampling on every
        // forced-movement candidate or publication pulse.
        private static readonly Vector2[] laudaRefuges = ThirdBoardGeometry.Candidates(0x4CAA, .5f).ToArray();
        internal static IEnumerable<Vector2> LaudaRefuges() => laudaRefuges;
        // Live49487 helpers sit0.1y behind the player and face along the throw.
        // Reject unrelated/stale helpers and quantization-sized zero vectors;
        // aim agreement preserves the same rule for a dagger thrown from front.
        internal static float? LaudaDaggerHeading(Vector2 player, Vector2 source, float heading)
        {
            var delta = player - source;
            float distance = delta.Length();
            if (distance < .04f || distance > 2 || Vector2.Dot(delta / distance, ThirdBoardGeometry.Direction(heading)) < .85f)
                return null;
            return heading;
        }

        // Molten's later circle can overlap every route out of a cage refuge.
        // Wait only on floor already safe from all earlier cages, then release
        // immediately after their impact fence; never hold inside a cage blast.
        internal static bool LaudaWaitForCages(Vector2 player, IEnumerable<ThirdBoardHazard> fields, System.DateTime now)
        {
            var live = fields.Where(h => h.Until > now).ToArray();
            var cages = live.Where(h => h.Action == 49457 || h.Action == 49460).ToArray();
            if (cages.Length == 0 || cages.Any(h => h.Contains(player)))
                return false;
            var molten = live.FirstOrDefault(h => h.Action == 49468 && h.Contains(player));
            return molten != null && cages.All(h => h.Until < molten.Until);
        }

        // Gyro starts just before Shockwave. Treating that later strip as an
        // immediate hazard caused sideways movement during the launch itself.
        // Keep future attacks retained for post-push escape, not launch routing;
        // persistent bodies remain forbidden throughout forced displacement.
        internal static bool LaudaAfterForce(ThirdBoardHazard field, System.DateTime now, System.DateTime forceUntil) => forceUntil > now && !field.Persistent && field.Until > forceUntil;
        // Advance within the current cage refuge while the later Molten circle
        // is still harmless. Ranking after hazard filtering preserves reachable
        // intermediate points when a full escape crosses an unresolved cage.
        internal static IEnumerable<Vector2> LaudaCageAdvance(Vector2 player, IEnumerable<ThirdBoardHazard> fields, System.DateTime now)
        {
            var live = fields.Where(h => h.Until > now).ToArray();
            var molten = live.First(h => h.Action == 49468 && h.Contains(player));
            var cages = live.Where(h => h.Action == 49457 || h.Action == 49460 || h.Persistent && h.Actor != 0).ToArray();
            // Moving farther from the bait can lead deeper into a dead-end
            // pocket. Stage toward the eventual exit, so the post-cage movement
            // continues forward instead of retracing the early step.
            var bodies = live.Where(h => h.Persistent && h.Actor != 0).ToArray();
            // A thin6.75-7.5y ring clears the buffered6.5y circle without
            // rewarding a distant exit or a shortcut through a pocket wall.
            var exits = LaudaRefuges().Where(p => Vector2.DistanceSquared(p, molten.Origin) >= 6.75f * 6.75f && Vector2.DistanceSquared(p, molten.Origin) <= 7.5f * 7.5f && bodies.All(h => !h.Contains(p))).ToArray();
            return LaudaRefuges().Where(p => Vector2.DistanceSquared(p, player) <= 64 && cages.All(h => !h.Contains(p))).OrderBy(p => exits.OrderBy(q => Vector2.DistanceSquared(p, q)).Where(q => ThirdBoardGeometry.Corridor(p, q, 0x4CAA, bodies)).Select(q => Vector2.Distance(p, q)).DefaultIfEmpty(100).First() + .1f * Vector2.Distance(p, player));
        }

        // Baiting at a pocket's inward edge delays the escape. Use its outermost safe row
        // on either end instead; retain both ends so travel and other hazards
        // can select the reachable one. Full early escape is not possible in
        // every yellow layout, so cage-resolution receipts still own release.
        internal static IEnumerable<Vector2> LaudaMoltenRefuges(ThirdBoardHazard[] cageForecasts)
        {
            var safe = LaudaRefuges().Where(p => cageForecasts.All(h => !h.Contains(p))).OrderByDescending(p => System.Math.Abs(p.Y - LaudaCenter.Y)).ToArray();
            return safe.GroupBy(p => p.Y < LaudaCenter.Y).SelectMany(end => end.Where(p => System.Math.Abs(p.Y - LaudaCenter.Y) >= end.Max(q => System.Math.Abs(q.Y - LaudaCenter.Y)) - .1f));
        }

        // Molten can overlap Gyro. Filter hazards before taking the farthest
        // samples, or blocked end pockets can crowd out the safe side alcoves.
        internal static IEnumerable<Vector2> LaudaProximityRefuges(Vector2 origin, ThirdBoardHazard[] fields) => LaudaRefuges().Where(p => fields.All(h => !h.Contains(p))).OrderByDescending(p => Vector2.DistanceSquared(p, origin)).Take(16);
        internal static IEnumerable<Vector2[]> LaudaShapes(uint action)
        {
            switch (action)
            {
                case 49483:
                    yield return ThirdBoardHazard.Rectangle(3, 50.5f);
                    break;
                case 49464:
                case 50848:
                    yield return ThirdBoardHazard.Rectangle(3.5f, 60.5f);
                    break;
                // The centered helper hits both halves of the longitudinal
                // strip despite facing south; side alcoves remain safe.
                case 49451:
                    yield return ThirdBoardHazard.Rectangle(10.5f, 40.5f, 40.5f);
                    break;
                case 49446:
                    yield return ThirdBoardHazard.Rectangle(20.5f, 50.5f);
                    break;
                case 49449:
                    yield return ThirdBoardHazard.Circle(40.5f);
                    break;
                case 49468:
                    yield return ThirdBoardHazard.Circle(6.5f);
                    break;
                case 49443:
                    yield return ThirdBoardHazard.Circle(8.5f);
                    break;
                case 49460:
                    yield return ThirdBoardHazard.Circle(12.5f);
                    break;
                case 49457:
                    yield return ThirdBoardHazard.Rectangle(5.5f, 15.5f, 15.5f);
                    yield return ThirdBoardHazard.Rectangle(15.5f, 5.5f, 5.5f);
                    break;
            // Thunderbolt is targeted mitigation; Aura/Unseen Force are
            // knockbacks. Proximity/raidwide casts need separate distance
            // policy rather than declaring the entire arena impassable.
            }
        }

        // Quarter-yalm samples cover the forced segment, not just its landing.
        // Future post-knockback attacks are checked at landing by the caller;
        // persistent contact fields still forbid crossing their bodies.
        internal static bool LaudaPushSafe(Vector2 start, Vector2 direction, float distance, IEnumerable<ThirdBoardHazard> contacts, IEnumerable<ThirdBoardHazard> landingFields)
        {
            if (direction.LengthSquared() < .9f)
                return false;
            direction = Vector2.Normalize(direction);
            var bodies = contacts.ToArray();
            for (float t = 0; t <= distance; t += .25f)
            {
                var p = start + direction * t;
                if (!InLaudaFloor(p) || bodies.Any(h => h.Contains(p)))
                    return false;
            }

            var end = start + direction * distance;
            return landingFields.All(h => !h.Contains(end));
        }

        // Gutting resolves after the push, leaving time to step behind its
        // caster. Requiring the landing itself to avoid that later rectangle
        // can reject every40y staging point. Allow a short connected egress
        // only within the observed helper deadline, reserving1s for motion and
        // server response. Goring and imminent fields still require safe landings.
        internal static bool LaudaLandingSafe(Vector2 landing, ThirdBoardHazard[] contacts, ThirdBoardHazard[] pending, System.DateTime landingTime)
        {
            if (pending.All(h => !h.Contains(landing)))
                return true;
            if (pending.Any(h => h.Action != 49446 && h.Contains(landing)))
                return false;
            float budget = System.Math.Min(12, (float)(pending.Min(h => h.Until) - landingTime).TotalSeconds * 6 - 6);
            if (budget <= 0)
                return false;
            return LaudaRefuges().Where(p => Vector2.Distance(p, landing) <= budget && pending.All(h => !h.Contains(p))).Any(p => Enumerable.Range(0, (int)System.Math.Ceiling(Vector2.Distance(p, landing) * 4) + 1).Select(i => Vector2.Lerp(landing, p, i / (float)System.Math.Max(1, (int)System.Math.Ceiling(Vector2.Distance(p, landing) * 4)))).All(q => InLaudaFloor(q) && contacts.All(h => !h.Contains(q))));
        }

        internal static IEnumerable<ThirdBoardHazard> LaudaBoundary()
        {
            // Merge forbidden half-yalm cells into row runs. One-time creation
            // preserves alcoves without rebuilding hundreds of points per pulse.
            for (float z = -28; z < 28; z += .5f)
            {
                float? begin = null;
                for (float x = -17; x <= 17; x += .5f)
                {
                    bool blocked = x < 17 && !InLaudaFloor(LaudaCenter + new Vector2(x + .25f, z + .25f));
                    if (blocked && !begin.HasValue)
                        begin = x;
                    if (!blocked && begin.HasValue)
                    {
                        yield return new ThirdBoardHazard
                        {
                            Origin = LaudaCenter,
                            Points = Box(begin.Value, x, z, z + .5f),
                            Persistent = true,
                            Until = System.DateTime.MaxValue
                        };
                        begin = null;
                    }
                }
            }

            // Seal the exterior too: native navigation may otherwise detour
            // around a finite floor mask through the bleeding arena boundary.
            foreach (var points in new[]
            {
                Box(-70, -17, -70, 70),
                Box(17, 70, -70, 70),
                Box(-17, 17, -70, -28),
                Box(-17, 17, 28, 70)
            }

            )
                yield return new ThirdBoardHazard
                {
                    Origin = LaudaCenter,
                    Points = points,
                    Persistent = true,
                    Until = System.DateTime.MaxValue
                };
        }

        // Gigantis shares Flauros's circular floor. Only helper snapshots are
        // damaging geometry: Rupture requires a kill/interrupt, and49364 is a
        // tower to occupy. Glower conservatively retains the reference3y half
        // width until a live footprint can resolve the sheet's width ambiguity.
        internal static Vector2[] GigantisShape(uint action) => action switch
        {
            49359 => ThirdBoardHazard.Circle(15.5f),
            49361 or 49363 => MasterArenaMovement.SweepingSlash(),
            49366 => ThirdBoardHazard.Circle(6.5f),
            49378 => ThirdBoardHazard.Rectangle(4.5f, 40.5f),
            49379 => ThirdBoardHazard.Rectangle(2.5f, 7.5f),
            49367 => ThirdBoardHazard.Rectangle(3.5f, 40.5f),
            _ => null
        };
        // WeaponElement2056 raw extras499/49A identify lightning/fire. A crush
        // must use the opposite element; the first unenchanted club accepts both.
        internal static bool GigantisBait(uint species, uint element) => species == 0x4D05 && element != 0x499 || species == 0x4D07 && element != 0x49A;
        internal static bool InGigantisFloor(Vector2 p) => Vector2.DistanceSquared(p, FlaurosCenter) <= FlaurosRadius * FlaurosRadius;
        // Sphinx uses the same authored square as Durga. Only damaging helper
        // casts define Banish; the numeric/memory raidwides are not floor AoEs.
        internal static IEnumerable<Vector2[]> SphinxShapes(uint action)
        {
            if (action == 49335)
                foreach (var shape in ThirdBoardHazard.Donut(9.5f, 60.5f))
                    yield return shape;
            if (action == 49337)
                yield return ThirdBoardHazard.Circle(18.5f);
            if (action == 49339 || action == 49341)
                yield return MasterArenaMovement.SweepingSlash();
        }

        // Assignments resolve independently in expiry order. Applying the
        // intersection of both debuffs can erase every legal tile.
        internal static bool SphinxNumber(uint status, uint number) => number >= 1 && number <= 9 && status switch
        {
            5148 => number % 2 == 0,
            5149 => number % 2 == 1,
            5150 => number == 2 || number == 3 || number == 5 || number == 7,
            5151 => number % 3 == 0,
            _ => false
        };
        // InstanceContentTextData45103–45106/45132, Global2026.09.15,
        // extracted EN/JA/DE/FR. RB exposes chat but no director-update event.
        // Match the complete localized prompt, never an English kin substring
        // or a player-supplied message. Unknown prompts must not guess an answer.
        internal static int SphinxPrompt(string text)
        {
            string value = (text ?? "").Replace("\r", "").Replace("\n", "").Trim();
            if (new[]
            {
                "Riddle me this─which child is kin of cloud?",
                "問おう……「有翼綱」の魔物はいずれなりや？",
                "Sag mir: Wer ist ein Kind der Wölklinge?",
                "Réponds à ma question... Laquelle de ces bêtes est ptérienne ?"
            }.Contains(value))
                return 0x4CFF;
            if (new[]
            {
                "Riddle me this─which child is kin of scale?",
                "問おう……「甲鱗綱」の魔物はいずれなりや？",
                "Sag mir: Wer ist ein Kind der Schupplinge?",
                "Réponds à ma question... Laquelle de ces bêtes est cuirassienne ?"
            }.Contains(value))
                return 0x4D02;
            if (new[]
            {
                "Riddle me this─which child is kin of beast?",
                "問おう……「百獣綱」の魔物はいずれなりや？",
                "Sag mir: Wer ist ein Kind der Biestlinge?",
                "Réponds à ma question... Laquelle de ces bêtes est thérienne ?"
            }.Contains(value))
                return 0x4D01;
            if (new[]
            {
                "Riddle me this─which child is kin of wave?",
                "問おう……「水棲綱」の魔物はいずれなりや？",
                "Sag mir: Wer ist ein Kind der Woglinge?",
                "Réponds à ma question... Laquelle de ces bêtes est hydride ?"
            }.Contains(value))
                return 0x4D00;
            if (new[]
            {
                "Make your choice. Care not to err.",
                "いざ選択せよ……汝が回答を。",
                "Triff deine Wahl.",
                "Choisis ta réponse, et choisis bien."
            }.Contains(value))
                return 1;
            return 0;
        }

        // Durga uses the40y square floor, not Drake's shallower rectangle.
        internal static bool InDurgaFloor(Vector2 p) => System.Math.Abs(p.X - 120) <= 18.75f && System.Math.Abs(p.Y) <= 18.75f;
        internal static IEnumerable<Vector2[]> DurgaBoundary()
        {
            yield return Box(-70, -18.75f, -70, 70);
            yield return Box(18.75f, 70, -70, 70);
            yield return Box(-18.75f, 18.75f, -70, -18.75f);
            yield return Box(-18.75f, 18.75f, 18.75f, 70);
        }

        // Helper casts own Jolt after the boss rotates, and red locked missile
        // lines own Voyage. Atomic Ray is unavoidable; Thermobaric is a40y push.
        // Neither belongs in this damaging-geometry mapping.
        internal static Vector2[] DurgaShape(uint action) => action switch
        {
            49265 => ThirdBoardHazard.Circle(20.5f),
            49266 => ThirdBoardHazard.Circle(6.5f),
            50688 => ThirdBoardHazard.Rectangle(2.5f, 100.5f),
            49276 => ThirdBoardHazard.Cone(30.5f, 61),
            _ => null
        };
        internal static IEnumerable<Vector2> DurgaBaits()
        {
            //18y matches the observed corner-bait reference and keeps2y from
            // the damaging boundary before the subsequent diagonal displacement.
            foreach (float x in new[]
            {
                -18f,
                18f
            }

            )
                foreach (float z in new[]
                {
                    -18f,
                    18f
                }

                )
                    yield return DurgaCenter + new Vector2(x, z);
        }

        internal static IEnumerable<Vector2> DurgaRefuges()
        {
            // Half-yalm samples preserve narrow Jolt corner pockets inside the
            // wall inset. RouteToAny searches the connected floor only once.
            for (float x = -18.5f; x <= 18.5f; x += .5f)
                for (float z = -18.5f; z <= 18.5f; z += .5f)
                    yield return DurgaCenter + new Vector2(x, z);
        }

        internal static IEnumerable<Vector2> DurgaPushStands(Vector2 origin)
        {
            // A small inward offset gives a stable direction while preserving
            // enough diagonal length for the full40y push. Validate every
            // candidate's complete displacement against floor and active fields.
            if (Vector2.DistanceSquared(DurgaCenter, origin) < 1)
                yield break;
            var inward = Vector2.Normalize(DurgaCenter - origin);
            float angle = System.MathF.Atan2(inward.Y, inward.X);
            for (float distance = 2; distance <= 5; distance += .5f)
                for (int step = -6; step <= 6; step++)
                {
                    float a = angle + step * System.MathF.PI / 180;
                    yield return origin + new Vector2(System.MathF.Cos(a), System.MathF.Sin(a)) * distance;
                }
        }

        // The authored Drake platform is40x30, including its damaging border.
        // Preserve corners rather than clipping this rectangle to a circle.
        internal static bool InDrakeFloor(Vector2 p) => System.Math.Abs(p.X - 520) <= 18.75f && System.Math.Abs(p.Y) <= 13.75f;
        // Native arrival tolerance can strand the player just inside the fence.
        // Recover only the nearby inner margin, toward 0.75y inside that fence.
        // Every sampled point must avoid actual attacks; the border polygons
        // alone may be crossed inward. No recovery from unmodeled outside floor.
        internal static Vector2? DrakeEdgeRecovery(Vector2 player, bool recovering, IEnumerable<ThirdBoardHazard> fields)
        {
            if (!recovering && InDrakeFloor(player) || System.Math.Abs(player.X - 520) > 19.5f || System.Math.Abs(player.Y) > 14.5f)
                return null;
            var goal = new Vector2(System.Math.Clamp(player.X, 502, 538), System.Math.Clamp(player.Y, -13, 13));
            if (Vector2.Distance(player, goal) <= .2f)
                return null;
            var attacks = fields.Where(h => h.Actor != 0 || !h.Persistent).ToArray();
            int steps = (int)System.Math.Ceiling(Vector2.Distance(player, goal) / .2f);
            for (int i = 0; i <= steps; i++)
                if (attacks.Any(h => h.Contains(Vector2.Lerp(player, goal, (float)i / steps))))
                    return null;
            return goal;
        }

        internal static IEnumerable<Vector2[]> DrakeBoundary()
        {
            yield return Box(-70, -18.75f, -70, 70);
            yield return Box(18.75f, 70, -70, 70);
            yield return Box(-18.75f, 18.75f, -70, -13.75f);
            yield return Box(-18.75f, 18.75f, 13.75f, 70);
        }

        // Walking covers about6y/s. Budget750ms for navigation and the server
        // snapshot, and aim2y from center (inside the2.5y buffered shelter).
        // This is a speed request, not permission to cross an unsafe route.
        internal static bool DrakeShelterNeedsSprint(float distance, double remaining) => remaining > 0 && distance > 2.5f && (distance - 2) / 6 > remaining - .75;
        // Burning Cyclone can require a 29y crossing during its 4.7s cast.
        // Perpendicular distance is a lower
        // bound on escape travel; if even that cannot fit, request Sprint.
        // This does not select a route or permit crossing the arena boundary.
        internal static bool DrakeCycloneNeedsSprint(Vector2 player, Vector2 origin, float heading, double remaining)
        {
            if (remaining <= 0)
                return false;
            var delta = player - origin;
            var forward = ThirdBoardGeometry.Direction(heading);
            float along = Vector2.Dot(delta, forward);
            float across = System.Math.Abs(delta.X * forward.Y - delta.Y * forward.X);
            double angle = 61 * System.Math.PI / 180;
            double escape = along * System.Math.Sin(angle) - across * System.Math.Cos(angle);
            return escape > 1 && escape > 6 * System.Math.Max(0, remaining - .75);
        }

        private static Vector2[] Box(float left, float right, float bottom, float top) => new[]
        {
            new Vector2(left, bottom),
            new Vector2(right, bottom),
            new Vector2(right, top),
            new Vector2(left, top)
        };
        // Current action sheets and independent encounter references agree on
        // these damaging helper shapes. Parent jumps and busters are excluded.
        internal static IEnumerable<Vector2[]> DrakeShapes(uint action)
        {
            if (action == 49256)
                yield return ThirdBoardHazard.Cone(50.5f, 61);
            if (action == 49252)
                yield return ThirdBoardHazard.Circle(20.5f);
            if (action == 49259)
                foreach (var shape in ThirdBoardHazard.Donut(2.5f, 50.5f))
                    yield return shape;
        }

        // Grounding Jolt snapshots before its delayed damage. Six yalms/sec
        // is ordinary running speed; reserve0.6s for publication/arrival rather
        // than counting the later effect grace as available travel time.
        internal static bool DurgaEscapeNeedsSprint(float pathLength, double remaining) => remaining > 0 && pathLength > 1 && pathLength > 6 * System.Math.Max(0, remaining - .6);
        internal static Vector2[] FlaurosShape(uint action) => action switch
        {
            // All damaging edges gain0.5y. Rectangle takes HALF width; using
            // the sheet's full width here would wrongly remove most of the floor.
            49186 => ThirdBoardHazard.Circle(6.5f),
            49191 => ThirdBoardHazard.Circle(16.5f),
            49193 => ThirdBoardHazard.Rectangle(20.5f, 50.5f),
            49194 => ThirdBoardHazard.Rectangle(1.5f, 100.5f),
            49195 => ThirdBoardHazard.Rectangle(3.5f, 100.5f),
            // Parent choreography, enhancement and targeted paralysis busters
            // must not become roomwide avoids merely because they have a cast.
            _ => null
        };
    }
}
