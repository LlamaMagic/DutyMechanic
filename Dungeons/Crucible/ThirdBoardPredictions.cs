using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using V2 = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons
{
    public sealed partial class ThirdBoardOfUnbroken
    {
        private readonly HashSet<uint> spawned = new HashSet<uint>();
        private readonly Dictionary<uint, Eye> eyes = new Dictionary<uint, Eye>();
        private V2? lastSirenPosition;
        private DateTime sirenPositionTime;
        private bool sirenEdgeSeen;

        private void CapturePredictions(BattleCharacter[] actors, BattleCharacter boss, DateTime now)
        {
            var visible = GameObjectManager.GameObjects.Where(a => a.IsVisible && a.Distance2D(Core.Me.Location) < 80).ToArray();
            var present = new HashSet<uint>(visible.Select(a => a.ObjectId));
            // Cage visibility can change during its animation. Retain its
            // bounded contact/forecast lifetime; Life Claim reconciles timing.
            // A visibility flicker must not reopen a route through a live cage.
            foreach (uint id in eyes.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                hazards.RemoveAll(h => h.Actor == id);
                eyes.Remove(id);
            }
            foreach (var actor in visible)
            {
                var p = Point(actor.Location);
                bool first = spawned.Add(actor.ObjectId);
                if (first && actor.BaseId == 0x4CA0)
                {
                    Add(actor.ObjectId, 48562, p, 0, ThirdBoardHazard.Circle(12.5f), now.AddSeconds(6.35));
                }

                if (first && actor.BaseId == 0x4CA2)
                {
                    Add(actor.ObjectId, 48577, p, 0, ThirdBoardHazard.Circle(9.5f), now.AddSeconds(6.15));
                }

                if (first && actor.BaseId == 0x1EC0C3 && !hazards.Any(h => h.Actor == actor.ObjectId && h.Action == 48609))
                {
                    // Late escape can cross the central cage. Predict its cross
                    // at spawn instead of waiting for the
                    // final 2.7s cast. The observed helper replaces this bounded
                    // 12.7s spawn-to-impact estimate plus a .5s effect fence.
                    Add(actor.ObjectId, 48609, p, actor.Heading,
                        ThirdBoardHazard.Rectangle(5.5f, 5.5f, 5.5f), now.AddSeconds(13.2), true);
                    hazards.AddRange(ThirdBoardGeometry.CageForecast(actor.ObjectId, p, actor.Heading, now.AddSeconds(13.2)));
                    destination = null;
                    nextPublication = default;
                    ff14bot.Helpers.Logging.Write("[CrucibleThirdCage] Discovered actor={0:X} origin={1}; reserving body and Life Claim cross.", actor.ObjectId, p);
                }
                if (actor.BaseId != 0x4C9C && actor.BaseId != 0x4C9D)
                {
                    continue;
                }

                if (!eyes.TryGetValue(actor.ObjectId, out var eye))
                {
                    eyes[actor.ObjectId] = eye = new Eye { Start = p, Donut = actor.BaseId == 0x4C9D };
                }

                eye.Position = p;
                eye.Gaze = actor is BattleCharacter battle && battle.HasAura(2056);
                var contact = hazards.FirstOrDefault(h => h.Actor == actor.ObjectId && h.Action == 0);
                if (contact == null)
                {
                    Add(actor.ObjectId, 0, p, 0, ThirdBoardHazard.Circle(2.5f), now.AddSeconds(1), true);
                }
                else
                {
                    contact.Origin = p;
                    contact.Until = now.AddSeconds(1);
                }
                if (!eye.Predicted && V2.Distance(eye.Start, p) > .5f)
                {
                    var center = ThirdBoardGeometry.Center(encounter);
                    float start = ThirdBoardGeometry.Heading(eye.Start - center);
                    float delta = MathF.IEEERemainder(ThirdBoardGeometry.Heading(p - center) - start, 2 * MathF.PI);
                    // Predict rotating eyes from movement,
                    // not spawn order. Native cast snapshots replace predictions
                    // if contact detonates an eye before its nominal endpoint.
                    // Eyes finish on the21y actor ring, outside the20y floor.
                    // RB51900's refuge was4.34y from the old20y prediction but
                    //5.15y from the real Farburst origin, so it was hit before
                    // the200ms cast could correct the forecast.
                    var finish = ThirdBoardGeometry.EyeEndpoint(start + Math.Sign(delta) * MathF.PI * .75f);
                    var until = now.AddSeconds(19.45);
                    // Gaze resolves with this eye's detonation, not throughout
                    // its travel. Keep an effect fence for delayed helper hits.
                    eye.GazeAt = until;
                    if (eye.Donut)
                    {
                        AddRing(actor.ObjectId, 48508, finish, 4.5f, 50.5f, until);
                    }
                    else
                    {
                        Add(actor.ObjectId, 48506, finish, 0, ThirdBoardHazard.Circle(25.5f), until);
                    }

                    eye.Predicted = true;
                }
            }
            spawned.IntersectWith(present);
            if (boss.BaseId == 0x4CA1)
            {
                var p = Point(boss.Location);
                var activeMelody = hazards.FirstOrDefault(h => h.Action == 48566);
                // The first teleport sample can still be interpolating.52656
                // latched a flank1.6y from the settled endpoint. Refine only
                // during the initial750ms and preserve the volley expiry;
                // later boss motion must not rotate an already resolving cone.
                if (activeMelody != null && activeMelody.Until - now > TimeSpan.FromSeconds(13.25) &&
                    ThirdBoardGeometry.SirenAtMelodyEdge(p))
                {
                    var settled = ThirdBoardGeometry.SirenEdgeOrigin(p);
                    float delta = V2.Distance(activeMelody.Origin, settled);
                    if (delta > .5f && delta <= 3)
                    {
                        activeMelody.Origin = settled;
                        activeMelody.Heading = ThirdBoardGeometry.Heading(ThirdBoardGeometry.Center(encounter) - settled);
                        destination = null;
                        nextPublication = default;
                    }
                }
                if (V2.Distance(p, ThirdBoardGeometry.Center(0x4CA1)) < 18.5f) sirenEdgeSeen = false;
                if ((!sirenEdgeSeen && ThirdBoardGeometry.SirenAtMelodyEdge(p) ||
                    lastSirenPosition.HasValue && ThirdBoardGeometry.SirenRelocated(
                    lastSirenPosition.Value, p, (now - sirenPositionTime).TotalSeconds)) && !boss.IsCasting)
                {
                    // Latch the endpoint until she returns to the room. A later
                    // cast clearing Melody must not repeatedly recreate it while
                    // she remains stationary on that same outer edge.
                    sirenEdgeSeen = true;
                    var origin = ThirdBoardGeometry.SirenEdgeOrigin(p);
                    var previous = hazards.FirstOrDefault(h => h.Action == 48566);
                    // The manual trace jumps to the edge before the instant
                    // Melody action. Its twelve pulses have no readable cast bar.
                    // Keep the cone through the volley, bounded by the next cast
                    // or a 14s timeout rather than losing it after the first hit.
                    // Ignore the second interpolated sample of the same jump;
                    // do not restart the volley timeout or churn its destination.
                    if (previous == null || V2.Distance(previous.Origin, origin) > 3)
                    {
                        hazards.RemoveAll(h => h.Action == 48566);
                        Add(boss.ObjectId, 48566, origin, ThirdBoardGeometry.Heading(ThirdBoardGeometry.Center(encounter) - origin),
                            ThirdBoardHazard.Cone(50.5f, 46), now.AddSeconds(14), true);
                        destination = null;
                        nextPublication = default;
                        ff14bot.Helpers.Logging.Write("[CrucibleThirdMelody] Edge origin={0}; preparing flank and subsequent inward march.", origin);
                    }
                    lastSirenPosition = p;
                    sirenPositionTime = now;
                }
                if (boss.IsCasting)
                {
                    hazards.RemoveAll(h => h.Action == 48566);
                }

                if (!lastSirenPosition.HasValue || now - sirenPositionTime >= TimeSpan.FromMilliseconds(400))
                {
                    lastSirenPosition = p;
                    sirenPositionTime = now;
                }
            }
        }

        private void RetireEye(V2 origin, bool donut)
        {
            var closest = eyes.Where(pair => pair.Value.Donut == donut && !pair.Value.Resolved)
                .OrderBy(pair => V2.Distance(pair.Value.Position, origin)).FirstOrDefault();
            if (closest.Value == null || V2.Distance(closest.Value.Position, origin) > 8)
            {
                return;
            }

            closest.Value.Resolved = true;
            // Helper cast-start precedes the actual gaze effect. Retiring its
            // movement forecast must not release facing in that final window.
            closest.Value.GazeAt = DateTime.UtcNow.AddMilliseconds(650);
            hazards.RemoveAll(h => h.Actor == closest.Key && h.Action != 0);
        }

        private void ResetPredictions()
        {
            spawned.Clear();
            eyes.Clear();
            lastSirenPosition = null;
            sirenEdgeSeen = false;
        }

        private sealed class Eye
        {
            internal V2 Start, Position;
            internal bool Donut, Predicted, Resolved, Gaze;
            internal DateTime GazeAt;
        }
    }
}
