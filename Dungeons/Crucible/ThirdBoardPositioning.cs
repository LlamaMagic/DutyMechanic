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
        private V2? waterEscapeDestination;

        private bool RecoverWaterEscape(V2 current, ThirdBoardHazard[] pending, DateTime now)
        {
            var water = pending.Where(h => h.Action == 48483).ToArray();
            if (encounter != 0x4C93 || water.Length == 0)
            {
                if (waterEscapeDestination.HasValue)
                {
                    Release();
                }

                waterEscapeDestination = null;
                return false;
            }
            // Native escape can repeatedly restart at the circle edge.
            // Keep one destination through the cast's effect fence, including
            // native escape pulses and the interval after first reaching safety.
            bool Safe(V2 point) => water.All(h => V2.Distance(point, h.Origin) >= 7.5f) &&
                ThirdBoardGeometry.WaterEscapeCorridor(current, point, pending) &&
                (knockbacks.Count == 0 || SafeKnockback(point, pending));
            if (!waterEscapeDestination.HasValue || !Safe(waterEscapeDestination.Value))
            {
                if (now < nextPlan)
                {
                    // A new helper can invalidate the old endpoint during the
                    // replan throttle. Keep pursuit suppressed in that gap;
                    // native emergency escape remains free to move us.
                    Hold(current);
                    return true;
                }
                nextPlan = now.AddMilliseconds(400);
                // Water's verified escape corridor remains mandatory; prefer
                // melee only among endpoints that satisfy that recovery path.
                var meleePreference = CrucibleMeleePreference.Capture();
                waterEscapeDestination = ThirdBoardGeometry.Candidates(encounter).Append(current).Where(Safe)
                    .OrderBy(meleePreference).ThenBy(p => V2.Distance(p, current) + .15f * V2.Distance(p, ThirdBoardGeometry.Center(encounter)))
                    .Select(p => (V2?)p).FirstOrDefault();
                if (waterEscapeDestination.HasValue)
                {
                    ff14bot.Helpers.Logging.Write("[CrucibleThirdWaterRecovery] Holding cast refuge={0} from={1} remainingMs={2:F0}",
                        waterEscapeDestination.Value, current, water.Max(h => (h.Until - now).TotalMilliseconds));
                }
            }
            if (!waterEscapeDestination.HasValue)
            {
                return false;
            }
            // Native avoidance and graph navigation share the same refuge.
            // Hold yields while native escape owns motion; it never resets the
            // plan simply because IsRunningOutOfAvoid became true.
            destination = waterEscapeDestination.Value;
            Hold(destination.Value);
            escapeAnchorValid = true;
            return true;
        }

        private void TickPositioning(BattleCharacter[] actors, DateTime now)
        {
            escapeAnchorValid = false;
            var current = Point(Core.Me.Location);
            var pending = hazards.Where(h => h.Until > now).ToArray();
            if (encounter == 0x4C93 && !ThirdBoardGeometry.InArena(current, encounter))
            {
                // Dreadwash can place us outside the safe floor. Do not let
                // ordinary pursuit resume along the wall after control returns.
                // Native avoidance still owns the path and all active hazards;
                // give it one inward refuge until the boundary is cleared.
                var wave = ThirdBoardGeometry.SelectWave(pending, encounter).ToArray();
                bool Safe(V2 p) => ThirdBoardGeometry.InArena(p, encounter, 2) &&
                    wave.All(h => !h.Contains(p)) &&
                    (knockbacks.Count == 0 || SafeKnockback(p, pending));
                ChooseAndHold(current, Safe, p => V2.Distance(p, current), now);
                escapeAnchorValid = holding && destination.HasValue && Safe(destination.Value);
                return;
            }
            if (RecoverWaterEscape(current, pending, now))
            {
                return;
            }

            bool forward = Core.Me.HasAura(2161), backward = Core.Me.HasAura(2162);
            bool left = Core.Me.HasAura(2163), right = Core.Me.HasAura(2164);
            if (Core.Me.HasAura(1257))
            {
                // The game owns forced motion. Keep combat from changing facing,
                // but issue neither MoveTo nor Stop during the march itself.
                CapabilityManager.Update(positioning, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Third Board forced march");
                CapabilityManager.Update(positioning, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Third Board forced march");
                holding = true;
                moving = false;
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
                    Hold(current);
                    return;
                }
                bool Safe(V2 p) => SafeKnockback(p, pending);
                ChooseAndHold(current, Safe, p => V2.Distance(p, current), now);
                return;
            }
            if (now < baitUntil)
            {
                var pet = Core.Me.Pet;
                var petPoint = pet == null ? (V2?)null : Point(pet.Location);
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
                ChooseAndHold(current, Eligible, p => baitAction == 48619 ? -V2.Distance(p, baitSource) : V2.Distance(p, current), now,
                    preferCurrent: baitAction != 48619);
                return;
            }
            var gaze = eyes.Values.Where(e => e.Gaze && !e.Resolved).Select(e => e.Position).ToArray();
            // Replanning between horse helpers caused escape/anti-stuck churn.
            // Preserve one safe point through Menace
            // and Valfodr, using the same wave ordering as native publication.
            // Every ordinary geometric wave uses the shared refuge policy.
            // Mandatory water recovery, baits, march and knockback branches
            // already returned above; their constraints cannot be bypassed here.
            if (pending.Length > 0 || encounter == 0x4CAA && !ThirdBoardGeometry.InArena(current, encounter))
            {
                // Retain one refuge through each wave and its effect fence.
                // Releasing between native escape pulses lets pursuit reclaim
                // the destination, causing wall-following or boundary oscillation.
                // Molten Metal transfers bait ownership in this same tick so
                // Shield Charge cannot run between the two movement leases.
                var wave = ThirdBoardGeometry.SelectWave(pending, encounter).ToArray();
                var boss = actors.FirstOrDefault(a => a.BaseId == encounter && a.IsAlive && a.IsTargetable);
                float meleeRange = boss == null ? 0 : boss.CombatReach + Core.Me.CombatReach +
                    (float)DataManager.GetSpellData(44879).Range - .5f;
                bool Safe(V2 point) => ThirdBoardGeometry.InArena(point, encounter, 1) &&
                    wave.All(h => !h.Contains(point)) && Enumerable.Range(0, 8).All(i =>
                        wave.All(h => !h.Contains(point + ThirdBoardGeometry.Direction(i * MathF.PI / 4) * .6f)));
                // Gyrocleave can alternate native escape and exact-point routing.
                // A safe alcove is a
                // region, not a required coordinate: accept the actual refuge
                // after escape instead of walking back to the old point. Check
                // every pending wave so Goring's later impact cannot be ignored
                // while the earlier Combusting Blades are resolving.
                if (encounter == 0x4CAA && !AvoidanceManager.IsRunningOutOfAvoid &&
                    // Preserve a selected melee approach across pulses; snapping
                    // it to the current point each tick would defeat its latch.
                    !(boss != null && destination.HasValue && Safe(destination.Value) &&
                        V2.Distance(destination.Value, Point(boss.Location)) <= meleeRange) &&
                    Safe(current) && pending.All(h => !h.Contains(current)) &&
                    Enumerable.Range(0, 8).All(i => pending.All(h =>
                        !h.Contains(current + ThirdBoardGeometry.Direction(i * MathF.PI / 4) * .6f))))
                {
                    destination = current;
                }
                // Geometric waves may retain melee uptime. Baits, knockbacks,
                // forced march and Melody use their earlier dedicated branches
                // and deliberately never receive this damage preference.
                if (boss != null && !AvoidanceManager.IsRunningOutOfAvoid)
                {
                    bool AllSafe(V2 p) => Safe(p) && pending.All(h => !h.Contains(p));
                    var melee = ThirdBoardGeometry.PreferMeleeRefuge(destination, current, Point(boss.Location),
                        meleeRange,
                        ThirdBoardGeometry.Candidates(encounter), AllSafe,
                        (from, to) => ThirdBoardGeometry.Corridor(from, to, encounter, pending));
                    if (melee.HasValue)
                    {
                        destination = melee;
                    }
                }
                ChooseAndHold(current, Safe, p => V2.Distance(p, current), now);
                // Compute validity once on the bot tick, not inside every
                // native polygon callback (a donut has 64 constituent wedges).
                escapeAnchorValid = holding && destination.HasValue && Safe(destination.Value);
                // Gaze facing is applied only after arrival, never while the
                // graph mover needs its heading to reach the selected refuge.
                if (holding && destination.HasValue && !moving && !AvoidanceManager.IsRunningOutOfAvoid && gaze.Length > 0)
                {
                    var heading = Enumerable.Range(0, 64).Select(i => i * MathF.PI / 32)
                        .Where(h => gaze.All(p => V2.Dot(ThirdBoardGeometry.Direction(h), V2.Normalize(p - current)) < -.1f))
                        .Select(h => (float?)h).FirstOrDefault();
                    if (heading.HasValue)
                    {
                        Hold(destination.Value, heading);
                    }
                }
                return;
            }
            if (gaze.Length > 0 && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                // A status-tagged eye is evidence; untethered decorative eyes
                // must not stop damage for the entire encounter. Require a
                // heading looking away from every currently tagged source.
                var facing = Enumerable.Range(0, 64).Select(i => i * MathF.PI / 32)
                    .Where(h => gaze.All(p => V2.Dot(ThirdBoardGeometry.Direction(h), V2.Normalize(p - current)) < -.1f))
                    .Select(h => (float?)h).FirstOrDefault();
                if (facing.HasValue)
                {
                    Hold(current, facing);
                    return;
                }
            }
            if (encounter == 0x4C93 && Core.Me.CurrentTarget is BattleCharacter sahagin && sahagin.BaseId == 0x4C95 && sahagin.HasAura(5434))
            {
                // Pet attacks do not trigger reflection. Keep the player beyond
                // autoattack reach while the routine continues pet commands.
                var source = Point(sahagin.Location);
                float reach = sahagin.CombatReach + Core.Me.CombatReach + 5;
                ChooseAndHold(current, p => ThirdBoardGeometry.InArena(p, encounter, .8f) &&
                    V2.Distance(p, source) >= reach && pending.All(h => !h.Contains(p)),
                    p => V2.Distance(p, current), now);
                return;
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
            if (!destination.HasValue || !safe(destination.Value))
            {
                if (now < nextPlan)
                {
                    // Do not reopen melee pursuit during the short Catoblepas
                    // replan delay. Native emergency avoidance still owns escape.
                    // Cage/helper arrivals can invalidate Guttler's refuge
                    // inside this same throttle. Keep pursuit leased out rather
                    // than alternating a melee approach with the next dodge.
                    if (encounter == 0x4C9B || encounter == 0x4CAA)
                    {
                        Hold(current);
                    }
                    else
                    {
                        Release();
                    }

                    return;
                }
                nextPlan = now.AddMilliseconds(400);
                // Positive mechanics supply their own safe predicate, including
                // knockback landing/march constraints. Distance-based baits keep
                // their primary score; otherwise melee wins among legal points.
                var preference = CrucibleMeleePreference.Capture();
                var candidates = ThirdBoardGeometry.Candidates(encounter).Append(current).Where(safe);
                destination = (preferCurrent ? candidates.OrderBy(preference).ThenBy(score) :
                    candidates.OrderBy(score).ThenBy(preference)).Select(p => (V2?)p).FirstOrDefault();
            }
            if (destination.HasValue)
            {
                Hold(destination.Value);
            }
            else
            {
                Release();
                if (now >= nextDiagnostic)
                {
                    nextDiagnostic = now.AddSeconds(2);
                    ff14bot.Helpers.Logging.Write("[CrucibleThird] No safe semantic destination: encounter={0:X} knockbacks={1} bait={2}. Native avoidance retains control.", encounter, knockbacks.Count, baitAction);
                }
            }
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
            // Native movement can stop 0.42 yalms short of the flank; its
            // avoid clearance prevented reaching our0.35y arrival threshold,
            // so facing was never prepared. Accept the actual nearby position
            // only after revalidating the full refuge and future march corridor.
            if (destination.HasValue && V2.Distance(current, destination.Value) <= 1 && Safe(current) &&
                !AvoidanceManager.IsRunningOutOfAvoid)
            {
                destination = current;
            }

            escapeAnchorValid = holding && destination.HasValue && Safe(destination.Value);
            if (escapeAnchorValid && marchTurn.HasValue)
            {
                marchHeading = ThirdBoardGeometry.Heading(ThirdBoardGeometry.Center(encounter) - destination.Value);
                Hold(destination.Value, marchHeading.Value - marchTurn.Value);
            }
            return true;
        }

        private void PrepareMarch(V2 current, ThirdBoardHazard[] pending, float turn, bool twoSided, DateTime now)
        {
            bool Safe(V2 p, float heading)
            {
                var offset = ThirdBoardGeometry.Direction(heading) * 18.5f;
                return ThirdBoardGeometry.Corridor(p, p + offset, encounter, pending) &&
                    (!twoSided || ThirdBoardGeometry.Corridor(p, p - offset, encounter, pending));
            }
            if (!destination.HasValue || !marchHeading.HasValue || !Safe(destination.Value, marchHeading.Value))
            {
                if (now < nextPlan)
                {
                    Release();
                    return;
                }
                nextPlan = now.AddMilliseconds(500);
                var candidate = new[] { current }.Concat(ThirdBoardGeometry.Candidates(encounter))
                    .OrderBy(CrucibleMeleePreference.Capture()).ThenBy(p => V2.Distance(p, current))
                    .SelectMany(p => Enumerable.Range(0, 32).Select(i => new { Point = p, Heading = i * MathF.PI / 16 }))
                    .FirstOrDefault(p => Safe(p.Point, p.Heading));
                destination = candidate?.Point;
                marchHeading = candidate?.Heading;
            }
            // As with the singing flank, a safe actual arrival must not wait
            // forever on the final fraction of a graph route before facing.
            if (destination.HasValue && marchHeading.HasValue && V2.Distance(current, destination.Value) <= 1 &&
                Safe(current, marchHeading.Value) && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                destination = current;
            }

            if (destination.HasValue && marchHeading.HasValue)
            {
                Hold(destination.Value, marchHeading.Value - turn);
            }
            else
            {
                Release();
            }
        }
    }
}
