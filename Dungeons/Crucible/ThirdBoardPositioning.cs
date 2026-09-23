using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Linq;
using V2 = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons
{
    public sealed partial class ThirdBoardOfUnbroken
    {
        private void TickPositioning(DateTime now)
        {
            var current = Point(Core.Me.Location);
            var pending = hazards.Where(h => h.Until > now).ToArray();
            bool forward = Core.Me.HasAura(2161), backward = Core.Me.HasAura(2162);
            bool left = Core.Me.HasAura(2163), right = Core.Me.HasAura(2164);
            if (Core.Me.HasAura(1257))
            {
                stagingMoving = false;
                // The game owns forced motion. Keep combat from changing facing,
                // but issue neither MoveTo nor Stop during the march itself.
                CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Third Board forced march");
                CapabilityManager.Update(positioning, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Third Board forced march");
                holding = true;
                if (!autoFacing.HasValue)
                {
                    autoFacing = GameSettingsManager.FaceTargetOnAction;
                }

                GameSettingsManager.FaceTargetOnAction = false;
                return;
            }

            if (encounter == 0x4CA1 && (forward || backward || left || right))
            {
                float turn = backward ? MathF.PI : left ? MathF.PI / 2 : right ? -MathF.PI / 2 : 0;
                if (PositionForMelody(current, pending, now, turn))
                {
                    return;
                }

                // Each aura selects one known direction relative to facing.
                // Requiring BOTH lateral directions from an edge flank would
                // invalidate the safe inward path and pull us back to center.
                PrepareMarch(current, pending, turn, false, now);
                return;
            }

            if (encounter == 0x4CA1 && PositionForMelody(current, pending, now, null))
            {
                return;
            }

            if (knockbacks.Count > 0)
            {
                // Once a Landslip bar ends, movement belongs to the effect.
                // Keep pursuit leased out but never reissue the staging route
                // during its interpolated displacement or delayed effect fence.
                // Detection releases this hold once directional motion is seen;
                // the bounded fence also releases a resisted/missed knockback.
                if (knockbacks.Any(k => k.Lane != null && now >= k.ResolveAt))
                {
                    StopStaging();
                    SuppressPursuit();
                    return;
                }

                bool Safe(V2 p) => SafeKnockback(p, pending);
                ChooseAndHold(current, Safe, p => V2.Distance(p, current), now);
                return;
            }

            if (now < baitUntil)
            {
                var pet = Core.Me.Pet;
                var petPoint = pet == null ? (V2? )null : Point(pet.Location);
                bool Safe(V2 p) => ThirdBoardGeometry.InArena(p, encounter, .8f) && !pending.Any(h => h.Contains(p));
                bool Eligible(V2 p)
                {
                    if (!Safe(p))
                    {
                        return false;
                    }

                    if (baitAction == 48615)
                    {
                        return Math.Abs(p.X - 520) < 1.5f && Math.Abs(p.Y + 420) >= 18;
                    }

                    if (baitAction == 48612)
                    {
                        return Math.Abs(p.X - 520) < 1.5f && V2.Distance(p, baitSource) > 7;
                    }

                    if (baitAction == 48557 && petPoint.HasValue)
                    {
                        var towardPet = V2.Normalize(petPoint.Value - baitSource);
                        var towardPlayer = V2.Normalize(p - baitSource);
                        // Earth Shaker's exact cone width is disputed. Opposite
                        // bearings separate player/pet without avoiding own bait.
                        return V2.Distance(p, baitSource) > 6 && V2.Dot(towardPet, towardPlayer) < -.3f;
                    }

                    return true;
                }

                ChooseAndHold(current, Eligible, p => baitAction == 48619 ? -V2.Distance(p, baitSource) : V2.Distance(p, current), now, preferCurrent: baitAction != 48619);
                return;
            }

            if (encounter == 0x4C93 && Core.Me.CurrentTarget is BattleCharacter sahagin && sahagin.BaseId == 0x4C95 && sahagin.HasAura(5434))
            {
                // Pet attacks do not trigger reflection. Keep the player beyond
                // autoattack reach while the routine continues pet commands.
                var source = Point(sahagin.Location);
                float reach = sahagin.CombatReach + Core.Me.CombatReach + 5;
                ChooseAndHold(current, p => ThirdBoardGeometry.InArena(p, encounter, .8f) && V2.Distance(p, source) >= reach && pending.All(h => !h.Contains(p)), p => V2.Distance(p, current), now);
                return;
            }

            var gaze = eyes.Values.Where(e => e.Gaze && !e.Resolved).Select(e => e.Position).ToArray();
            // RB alone owns geometric escapes. Keep pursuit suppressed through
            // the effect fence without routing back to an exact sampled point.
            if (pending.Length > 0 || !ThirdBoardGeometry.InArena(current, encounter))
            {
                StopStaging();
                destination = null;
                SuppressPursuit();
                FaceAwayFromGaze(current, gaze);
                return;
            }

            if (gaze.Length > 0 && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                // A status-tagged eye is evidence; untethered decorative eyes
                // must not stop damage for the entire encounter. Require a
                // heading looking away from every currently tagged source.
                var facing = Enumerable.Range(0, 64).Select(i => i * MathF.PI / 32).Where(h => gaze.All(p => V2.Dot(ThirdBoardGeometry.Direction(h), V2.Normalize(p - current)) < -.1f)).Select(h => (float? )h).FirstOrDefault();
                if (facing.HasValue)
                {
                    SuppressPursuit(facing);
                    return;
                }
            }

            Release();
        }

        private bool SafeKnockback(V2 point, ThirdBoardHazard[] pending)
        {
            if (!ThirdBoardGeometry.InArena(point, encounter, .8f))
            {
                return false;
            }

            var applicable = knockbacks.Where(k => k.Lane == null || k.Lane.Contains(point)).ToArray();
            if (applicable.Length != 1)
            {
                return false;
            }

            var knockback = applicable[0];
            V2 direction;
            if (knockback.Radial)
            {
                if (V2.Distance(point, knockback.Origin) < 3)
                {
                    return false;
                }

                direction = V2.Normalize(point - knockback.Origin);
            }
            else
            {
                direction = ThirdBoardGeometry.Direction(knockback.Heading);
                if (knockback.Bidirectional)
                {
                    float side = V2.Dot(point - knockback.Origin, direction);
                    if (Math.Abs(side) < .75f)
                    {
                        return false;
                    }

                    if (side < 0)
                    {
                        direction = -direction;
                    }
                }
            }

            var landing = point + direction * knockback.Distance;
            if (!ThirdBoardGeometry.InArena(landing, encounter, 1))
            {
                return false;
            }

            // Rockslide is evaluated at the Landslip destination. Avoiding its
            // pre-knockback lane as well would reject the intended solution.
            return pending.All(h => (h.Action == 48555 || !h.Contains(point)) && !h.Contains(landing));
        }

        private void ChooseAndHold(V2 current, Func<V2, bool> safe, Func<V2, float> score, DateTime now, bool preferCurrent = true)
        {
            // September21 staging masks left detected knockbacks without travel.
            // Restore latched destinations only for semantic mechanics. Half-yalm
            // sampling preserves Tsunami's narrow band; predicates already enforce
            // start/landing wall clearance, without requiring a large safe disk.
            if (!destination.HasValue || !safe(destination.Value))
            {
                var preference = CrucibleMeleePreference.Capture();
                var candidates = ThirdBoardGeometry.Candidates(encounter, .5f).Append(current).Where(safe);
                destination = (preferCurrent ? candidates.OrderBy(preference).ThenBy(score) : candidates.OrderBy(score).ThenBy(preference)).Select(p => (V2? )p).FirstOrDefault();
                if (destination.HasValue)
                    ff14bot.Helpers.Logging.Write("[CrucibleThirdStaging] Destination={0} encounter={1:X} knockbacks={2} bait={3}.", destination.Value, encounter, knockbacks.Count, baitAction);
            }

            if (destination.HasValue)
            {
                // Native routes can finish short. Accept that arrival only when
                // the full mechanic predicate passes at the actual position.
                if (V2.Distance(current, destination.Value) <= 1 && safe(current))
                    destination = current;
                Hold(destination.Value);
            }
            else
            {
                StopStaging();
                SuppressPursuit();
                if (now >= nextDiagnostic)
                {
                    nextDiagnostic = now.AddSeconds(2);
                    ff14bot.Helpers.Logging.Write("[CrucibleThird] No safe staging destination: encounter={0:X} knockbacks={1} bait={2}.", encounter, knockbacks.Count, baitAction);
                }
            }
        }

        private void FaceAwayFromGaze(V2 current, V2[] gaze)
        {
            if (gaze.Length == 0 || AvoidanceManager.IsRunningOutOfAvoid)
                return;
            var facing = Enumerable.Range(0, 64).Select(i => i * MathF.PI / 32).Where(h => gaze.All(p => V2.Dot(ThirdBoardGeometry.Direction(h), V2.Normalize(p - current)) < -.1f)).Select(h => (float? )h).FirstOrDefault();
            if (facing.HasValue)
                SuppressPursuit(facing);
        }

        private float? marchHeading;
        private bool PositionForMelody(V2 current, ThirdBoardHazard[] pending, DateTime now, float? marchTurn)
        {
            var melody = pending.FirstOrDefault(h => h.Action == 48566);
            if (melody == null)
            {
                return false;
            }

            bool Safe(V2 point) => ThirdBoardGeometry.MelodyRefuge(point, melody.Origin, pending, marchTurn.HasValue);
            // Get beside the boss, not merely to the farthest edge of her cone.
            // Retain that flank through the volley and prepare facing only once
            // arrived; movement still uses the native graph and its heading.
            ChooseAndHold(current, Safe, p => V2.Distance(p, current) + V2.Distance(p, melody.Origin), now);
            if (Safe(current) && marchTurn.HasValue && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                marchHeading = ThirdBoardGeometry.Heading(ThirdBoardGeometry.Center(encounter) - current);
                Hold(current, marchHeading.Value - marchTurn.Value);
            }

            return true;
        }

        private void PrepareMarch(V2 current, ThirdBoardHazard[] pending, float turn, bool twoSided, DateTime now)
        {
            bool Safe(V2 p, float heading)
            {
                var offset = ThirdBoardGeometry.Direction(heading) * 18.5f;
                return ThirdBoardGeometry.Corridor(p, p + offset, encounter, pending) && (!twoSided || ThirdBoardGeometry.Corridor(p, p - offset, encounter, pending));
            }

            if (!destination.HasValue || !marchHeading.HasValue || !Safe(destination.Value, marchHeading.Value))
            {
                var candidate = new[]
                {
                    current
                }.Concat(ThirdBoardGeometry.Candidates(encounter)).OrderBy(CrucibleMeleePreference.Capture()).ThenBy(p => V2.Distance(p, current)).SelectMany(p => Enumerable.Range(0, 32).Select(i => new { Point = p, Heading = i * MathF.PI / 16 })).FirstOrDefault(p => Safe(p.Point, p.Heading));
                destination = candidate?.Point;
                marchHeading = candidate?.Heading;
            }

            if (destination.HasValue && marchHeading.HasValue)
            {
                if (V2.Distance(current, destination.Value) <= 1 && Safe(current, marchHeading.Value))
                    destination = current;
                Hold(destination.Value, Safe(current, marchHeading.Value) ? marchHeading.Value - turn : (float? )null);
            }
            else
            {
                StopStaging();
                SuppressPursuit();
            }
        }
    }
}
