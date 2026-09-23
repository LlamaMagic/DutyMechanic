using System;
using System.Collections.Generic;
using System.Linq;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using V2 = System.Numerics.Vector2;
using V3 = Clio.Utilities.Vector3;

namespace DutyMechanic.Dungeons
{
    public sealed partial class SecondMastersBoard
    {
        private readonly CapabilityManagerHandle laudaPosition = CapabilityManager.CreateNewHandle();
        private readonly HashSet<uint> laudaCages = new();
        private readonly Dictionary<uint, ThirdBoardHazard> daggers = new();
        private readonly Dictionary<uint, bool> bladeVisibility = new();
        private readonly Dictionary<uint, uint> forceResponses = new();
        private DateTime daggerUntil, pointUntil, moltenUntil, laudaPushUntil, laudaPushAt, forceUntil;
        private DateTime forcePreparingUntil;
        private uint laudaPushAction;
        private V2 laudaPushOrigin;
        private float laudaPushHeading, forceOffset;
        private float? forceBindFacing;
        private float? forceApplicationHeading;
        private bool forceObserved, laudaHeld, laudaMoving, laudaFacingHeld;
        private bool? laudaAutoFacing;
        private DateTime nextLaudaRoute, nextLaudaPublication, nextLaudaDiagnostic;
        private DateTime nextLaudaSprint;
        private DateTime nextLaudaTransitLog;
        private V2? laudaDestination;
        private V2[] laudaRoute = Array.Empty<V2>();
        private V2[] moltenBaits;
        private uint[] moltenCageActors = Array.Empty<uint>();
        private ThirdBoardHazard[] laudaPublished = Array.Empty<ThirdBoardHazard>();
        private void CaptureLaudaCast(BattleCharacter actor, uint action, DateTime now)
        {
            if (action == 0)
                return;
            var end = now + actor.SpellCastInfo.RemainingCastTime;
            // Bind follows the bar. Keep ownership across that packet gap so
            // arrival cannot expose a Challenge auto-face before the dagger.
            if (action == 49484)
            {
                forcePreparingUntil = end.AddSeconds(6);
                forceApplicationHeading = null;
            }

            if (action == 49481)
            {
                daggerUntil = now.AddSeconds(24);
                foreach (var field in daggers.Values)
                    hazards.Remove(field);
                daggers.Clear();
                // Actors exist before their display sequence. Observe the new
                // visible interval, retaining its geometry after disappearance.
                bladeVisibility.Clear();
            }

            if (action == 49483)
            {
                float heading = BorgnyCastReader.Rotation(actor) ?? actor.Heading;
                foreach (var entry in daggers.Where(e => V2.Distance(e.Value.Origin, Point(actor.Location)) < 2 && Math.Abs(MathF.IEEERemainder(e.Value.Heading - heading, MathF.PI * 2)) < .1f).ToArray())
                {
                    hazards.Remove(entry.Value);
                // Keep the actor marked as observed until the next sequence.
                // Rush makes the blade visible again; removing its key here
                // would rearm the resolved wave for another 15s.
                }
            }

            if (action == 49462)
                pointUntil = end.AddSeconds(8);
            if (action == 49464 || action == 50848)
                pointUntil = default;
            if (action == 49465)
            {
                moltenUntil = end.AddSeconds(3);
                moltenBaits = null;
            }

            if (action == 49468)
                moltenUntil = default;
            if (action == 49453 || action == 49473)
            {
                laudaPushAction = action;
                laudaPushAt = end;
                laudaPushUntil = end.AddMilliseconds(350);
                laudaPushOrigin = Point(actor.Location);
                laudaPushHeading = BorgnyCastReader.Rotation(actor) ?? actor.Heading;
            }

            // Actual helper casts replace uncertain cage forecasts. Bodies
            // remain reserved through impact so a short route cannot cut inside.
            if (action == 49457 || action == 49460)
                foreach (var h in hazards.Where(h => h.Action == 0 && h.Actor != 0 && V2.Distance(h.Origin, Point(actor.Location)) < 1))
                    h.Until = end.AddMilliseconds(750);
            nextLaudaPublication = nextLaudaRoute = default;
        }

        private ThirdBoardHazard[] LaudaPublished()
        {
            var now = DateTime.UtcNow;
            if (now < nextLaudaPublication)
                return laudaPublished;
            nextLaudaPublication = now.AddMilliseconds(150);
            // Do not union successive disappearing-blade waves or expose the
            // post-push Gutting/Goring before Unseen Force can move the player.
            // Their landing safety is checked separately while staging.
            var pending = hazards.Where(h => h.Until > now && !SecondMasterGeometry.LaudaAfterForce(h, now, forceUntil) && !((forceUntil > now || h.Until > now.AddSeconds(5)) && (h.Action == 49446 || h.Action == 49449)) && !(h.Action == 49482 && h.Until > now.AddSeconds(6))).ToArray();
            // Static boundary polygons cannot change which floor sample is
            // safe: the cached refuge set already satisfies that union. Avoid
            // retesting200 masks against thousands of points every150ms.
            bool waitForCages = SecondMasterGeometry.LaudaWaitForCages(Point(Core.Me.Location), hazards, now);
            var dynamicFields = pending.Where(h => (!h.Persistent || h.Actor != 0) && !(waitForCages && h.Action == 49468)).ToArray();
            var first = dynamicFields.Where(h => !h.Persistent).OrderBy(h => h.Until).FirstOrDefault();
            var imminent = dynamicFields.Where(h => h.Persistent || first == null || h.Until <= first.Until.AddMilliseconds(350)).ToArray();
            // Blade rows are about 2.05s apart. Include a 300ms publication
            // margin so egress accounts for the next row around the centre cage.
            var combined = dynamicFields.Where(h => h.Persistent || first == null || h.Until <= first.Until.AddSeconds(2.3)).ToArray();
            var selected = combined.Length != imminent.Length && SecondMasterGeometry.LaudaRefuges().Any(p => combined.All(h => !h.Contains(p))) ? combined : imminent;
            return laudaPublished = pending.Where(h => h.Persistent && h.Actor == 0).Concat(selected).ToArray();
        }

        private void TickLauda(BattleCharacter[] actors, DateTime now)
        {
            var boss = actors.First(a => a.BaseId == 0x4D1E && a.IsAlive);
            var player = Point(Core.Me.Location);
            var visibleCages = GameObjectManager.GameObjects.Where(a => a.Distance2D(Core.Me.Location) < 65 && a.IsVisible && (a.BaseId == 0x1EC0C4 || a.BaseId == 0x1EC0DD)).ToArray();
            // Reusable event objects may return in later waves. Re-arm only
            // after both disappearance and the retained contact fence resolve.
            laudaCages.RemoveWhere(id => !visibleCages.Any(a => a.ObjectId == id) && !hazards.Any(h => h.Actor == id && h.Until > now));
            foreach (var actor in visibleCages)
            {
                if (!laudaCages.Add(actor.ObjectId))
                    continue;
                float half = actor.BaseId == 0x1EC0C4 ? 5.5f : 3;
                hazards.Add(new ThirdBoardHazard { Actor = actor.ObjectId, Origin = Point(actor.Location), Heading = actor.Heading, Points = ThirdBoardHazard.Rectangle(half, half, half), Persistent = true, Until = now.AddSeconds(15) });
                nextLaudaPublication = nextLaudaRoute = default;
                ff14bot.Helpers.Logging.Write("[CrucibleLauda] Cage {0:X}/{1:X} at {2}.", actor.ObjectId, actor.BaseId, actor.Location);
            }

            if (now < daggerUntil)
                foreach (var blade in actors.Where(a => a.BaseId == 0x4D21))
                {
                    bool wasVisible = bladeVisibility.TryGetValue(blade.ObjectId, out bool prior) && prior;
                    bladeVisibility[blade.ObjectId] = blade.IsVisible;
                    if (!blade.IsVisible || wasVisible || daggers.ContainsKey(blade.ObjectId))
                        continue;
                    // Reference timing is14.7s from appearance to Rush. The
                    // one-second helper later reconciles the exact snapshot.
                    // Live capture must validate visibility as an early signal.
                    var field = new ThirdBoardHazard
                    {
                        Actor = blade.ObjectId,
                        Action = 49482,
                        Origin = Point(blade.Location),
                        Heading = blade.Heading,
                        Points = ThirdBoardHazard.Rectangle(3, 50.5f),
                        Until = now.AddSeconds(15.2)
                    };
                    daggers[blade.ObjectId] = field;
                    hazards.Add(field);
                    ff14bot.Helpers.Logging.Write("[CrucibleLauda] Visible dagger {0:X} at {1}, heading={2:F3}.", blade.ObjectId, blade.Location, blade.Heading);
                }

            var force = Core.Me.CharacterAuras.FirstOrDefault(a => a.Id == 5341);
            foreach (var helper in actors.Where(a => a.BaseId == 0x233C))
            {
                var response = BorgnyCastReader.Response(helper);
                bool known = forceResponses.TryGetValue(helper.ObjectId, out uint previous);
                forceResponses[helper.ObjectId] = response.Sequence;
                // The750ms fallback protects casts with no receipt. A fresh
                // matching effect proves this cage has resolved sooner, leaving
                // the critical extra movement time before the later Molten hit.
                if (known && previous != response.Sequence && (response.Action == 49457 || response.Action == 49460))
                {
                    foreach (var field in hazards.Where(h => h.Until > now && (h.Actor == helper.ObjectId && h.Action == response.Action || h.Actor != 0 && h.Action == 0 && V2.Distance(h.Origin, Point(helper.Location)) < 1)))
                        field.Until = field.Until < now.AddMilliseconds(100) ? field.Until : now.AddMilliseconds(100);
                    nextLaudaPublication = nextLaudaRoute = default;
                    ff14bot.Helpers.Logging.Write("[CrucibleLauda] Cage resolved: actor={0:X} action={1}.", helper.ObjectId, response.Action);
                }

                if (!known || previous == response.Sequence || forceObserved || now >= forcePreparingUntil || (response.Action != 49486 && response.Action != 49487))
                    continue;
                // The boss can report default rotation during its jump.
                // The application helper sits just behind the throw
                // direction; require matching origin and aim before using it.
                var incoming = SecondMasterGeometry.LaudaDaggerHeading(player, Point(helper.Location), helper.Heading);
                if (!incoming.HasValue)
                    continue;
                forceApplicationHeading = incoming;
                ff14bot.Helpers.Logging.Write("[CrucibleLauda] Dagger receipt action={0} heading={1:F3} bindFacing={2:F3}.", response.Action, incoming.Value, forceBindFacing ?? Core.Me.Heading);
            }

            if (force == null && !forceObserved && Core.Me.HasAura(5555))
            {
                // Auto-facing an add during Bind reverses the dagger's stored
                // front/back offset. Preserve facing until the status arrives.
                forceBindFacing ??= Core.Me.Heading;
                if (laudaMoving)
                    MovementManager.MoveStop();
                laudaMoving = false;
                CapabilityManager.Update(laudaPosition, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Lauda dagger application");
                laudaHeld = true;
                HoldLaudaFacing(forceBindFacing.Value);
                return;
            }

            if (force != null && !forceObserved)
            {
                float applicationFacing = forceBindFacing ?? Core.Me.Heading;
                float incomingHeading = forceApplicationHeading ?? boss.Heading;
                forceOffset = V2.Dot(ThirdBoardGeometry.Direction(incomingHeading), ThirdBoardGeometry.Direction(applicationFacing)) < 0 ? MathF.PI : 0;
                // Displacement can begin a second after status 5341 expires.
                // Hold facing through that delay so attacks cannot redirect the push.
                forceUntil = now + force.TimespanLeft + TimeSpan.FromMilliseconds(1300);
                forceObserved = true;
                nextLaudaRoute = nextLaudaPublication = default;
                ff14bot.Helpers.Logging.Write("[CrucibleLauda] Unseen Force offset={0:F3}; remaining={1:F2}s player={2} incomingHeading={3:F3} bindFacing={4:F3} helperReceipt={5}.", forceOffset, force.TimespanLeft.TotalSeconds, Core.Me.Location, incomingHeading, applicationFacing, forceApplicationHeading.HasValue);
            }

            if (forceObserved && force == null && now > forceUntil)
            {
                forceObserved = false;
                RestoreLaudaFacing();
            }
            else if (forceObserved && force == null)
            {
                // Aura removal precedes interpolated displacement. Preserve
                // ownership briefly without sending MoveTo or Stop into the
                // game's motion, then release for the post-push floor attack.
                laudaMoving = false;
                // RB accepts individual capability keys, not a combined flag.
                CapabilityManager.Update(laudaPosition, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Lauda knockback resolving");
                CapabilityManager.Update(laudaPosition, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Lauda knockback resolving");
                laudaHeld = laudaFacingHeld = true;
                return;
            }

            // The analytic floor predicate already checks the boundary union.
            // Do not send its200+ static mask polygons through every route edge.
            var fields = LaudaPublished().Where(h => !h.Persistent || h.Actor != 0).ToArray();
            bool cageStaging = SecondMasterGeometry.LaudaWaitForCages(player, hazards, now);
            // These sequential circles leave less than a second after the
            // cage impact. Sprint supplements the same floor-safe route; it
            // never permits cutting through an unresolved cage or arena wall.
            if (cageStaging && now >= nextLaudaSprint && !Core.Me.IsCasting && ActionManager.IsSprintReady)
            {
                nextLaudaSprint = now.AddSeconds(1);
                ActionManager.Sprint();
                ff14bot.Helpers.Logging.Write("[CrucibleLauda] Sprint requested for cage/Molten escape.");
            }

            if (now >= laudaPushAt && now < laudaPushUntil)
            {
                // The cast bar ends before knockback interpolation. A new
                // approach during these350ms fights the game's displacement.
                laudaMoving = false;
                CapabilityManager.Update(laudaPosition, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Lauda knockback resolving");
                CapabilityManager.Update(laudaPosition, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Lauda knockback resolving");
                laudaHeld = laudaFacingHeld = true;
                return;
            }

            IEnumerable<V2> goals = Array.Empty<V2>();
            float? face = null;
            bool positive = false;
            var late = hazards.Where(h => h.Until > now && (h.Action == 49446 || h.Action == 49449)).ToArray();
            // Floor safety is checked analytically along the whole push. Only
            // actor bodies need polygon tests at every quarter-yalm sample.
            var contacts = fields.Where(h => h.Persistent && h.Actor != 0).ToArray();
            if (force != null || forceUntil > now)
            {
                positive = true;
                // The40y displacement needs the long north/south axis. Keep
                // the snapshot's front/back offset while choosing an inward
                // facing; combat must not auto-face and reverse the landing.
                goals = SecondMasterGeometry.LaudaRefuges().Where(p => Math.Abs(p.X - 520) < 1 && Math.Abs(p.Y + 420) >= 16).Where(p => SecondMasterGeometry.LaudaPushSafe(p, new V2(0, p.Y < -420 ? 1 : -1), 40, contacts, Array.Empty<ThirdBoardHazard>())).Where(p => SecondMasterGeometry.LaudaLandingSafe(p + new V2(0, p.Y < -420 ? 40 : -40), contacts, late, forceUntil));
            }
            else if (now < forcePreparingUntil && !forceObserved)
            {
                positive = true;
                goals = new[]
                {
                    new V2(520, -442),
                    new V2(520, -398)
                };
            }
            else if (cageStaging)
            {
                // Holding on the bait leaves only 1s to clear a 6y circle.
                // Use the remaining cage-safe space first, then
                // finish the escape once its earlier impact fence has elapsed.
                positive = true;
                goals = SecondMasterGeometry.LaudaCageAdvance(player, hazards, now);
            }
            else if (laudaPushUntil > now && (laudaPushAction != 49473 || laudaPushUntil < now.AddSeconds(6)))
            {
                positive = true;
                goals = SecondMasterGeometry.LaudaRefuges().Where(p =>
                {
                    V2 direction;
                    if (laudaPushAction == 49473)
                        direction = p - laudaPushOrigin;
                    else
                    {
                        var side = ThirdBoardGeometry.Direction(laudaPushHeading + MathF.PI / 2);
                        direction = side * (V2.Dot(p - laudaPushOrigin, side) >= 0 ? 1 : -1);
                    }

                    return SecondMasterGeometry.LaudaPushSafe(p, direction, 20, contacts, fields.Where(h => !h.Persistent));
                });
            }
            else if (now < moltenUntil)
            {
                positive = true;
                // Yellow cages can occupy both end pockets. Predict
                // their later footprints for the drop location so Molten uses
                // the open diagonal instead of freezing between blocked ends.
                // Cage actors are fixed for this placement. Cache the outer-row
                // search; new/removed actors invalidate it without making every
                // combat pulse refilter the complete floor candidate set.
                var cageActors = visibleCages.Select(a => a.ObjectId).OrderBy(id => id).ToArray();
                if (moltenBaits == null || !cageActors.SequenceEqual(moltenCageActors))
                {
                    var cageForecasts = visibleCages.SelectMany(a => SecondMasterGeometry.LaudaShapes(a.BaseId == 0x1EC0DD ? 49460u : 49457u).Select(shape => new ThirdBoardHazard { Origin = Point(a.Location), Heading = a.Heading, Points = shape })).ToArray();
                    moltenBaits = SecondMasterGeometry.LaudaMoltenRefuges(cageForecasts).ToArray();
                    moltenCageActors = cageActors;
                }

                goals = moltenBaits;
            }
            else if (now < pointUntil)
            {
                positive = true;
                goals = new[]
                {
                    new V2(520, -426),
                    new V2(520, -414)
                };
            }
            else
            {
                var proximity = actors.FirstOrDefault(a => a.IsCasting && (a.CastingSpellId == 49469 || a.CastingSpellId == 49477) && a.SpellCastInfo.RemainingCastTime.TotalSeconds < 6);
                if (proximity != null)
                {
                    positive = true;
                    goals = SecondMasterGeometry.LaudaProximityRefuges(Point(proximity.Location), fields);
                }
                // Molten escape can cut across an alcove's burning rim. Check
                // the whole corridor, as for knockback staging.
                else if (fields.Any(h => h.Contains(player)))
                    goals = SecondMasterGeometry.LaudaRefuges();
                else if (contacts.Length != 0 && daggers.Values.Any(h => h.Until > now))
                {
                    // Keep one planner between blade waves; switching to native
                    // escape can cut through the centre cage. Stationary holds
                    // release facing and leave the combat routine schedulable.
                    goals = SecondMasterGeometry.LaudaRefuges().Prepend(player);
                }
                else
                {
                    ReleaseLaudaPosition();
                    return;
                }
            }

            CapabilityManager.Update(laudaPosition, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(600), "Lauda mechanic positioning");
            laudaHeld = true;
            if (now >= nextLaudaRoute)
            {
                float reach = boss.CombatReach + Core.Me.CombatReach + 3;
                var intended = goals.ToArray();
                var legal = intended.Where(p => SecondMasterGeometry.InLaudaFloor(p) && !fields.Any(h => h.Contains(p))).ToArray();
                // Gutting can require the opposite launch end while temporary
                // Combustion circles still cover it. Stage at a safe approach
                // point rather than freezing in the middle of a40y knockback.
                bool intermediate = force != null && legal.Length == 0 && intended.Length != 0;
                if (intermediate)
                    legal = SecondMasterGeometry.LaudaRefuges().Where(p => !fields.Any(h => h.Contains(p))).OrderBy(p => intended.Min(q => V2.DistanceSquared(p, q))).Take(24).ToArray();
                else if (force == null && positive && legal.Length == 0)
                {
                    // Cages can cover both preferred Point Maker baits. An
                    // ordinary bait may use any safe floor; directional pushes
                    // must retain their validated landing positions.
                    if (now < pointUntil || now < moltenUntil)
                        legal = SecondMasterGeometry.LaudaRefuges().Where(p => !fields.Any(h => h.Contains(p))).ToArray();
                }

                if (!laudaDestination.HasValue || !legal.Any(p => V2.DistanceSquared(p, laudaDestination.Value) < .05f) || fields.Any(h => h.Contains(laudaDestination.Value)))
                    laudaDestination = null;
                var ranked = cageStaging ? legal.AsEnumerable() : legal.OrderBy(p => laudaDestination.HasValue && V2.DistanceSquared(p, laudaDestination.Value) < .05f ? -1000 : V2.Distance(player, p) + (intermediate ? intended.Min(q => V2.Distance(p, q)) * 3 : positive ? 0 : Math.Max(0, V2.Distance(p, Point(boss.Location)) - reach) * 2));
                laudaRoute = MasterArenaMovement.RouteToAny(player, ranked, ArenaCenter, SecondMasterGeometry.InLaudaFloor, fields, now);
                if (laudaRoute.Length > 0)
                    laudaDestination = laudaRoute.Last();
                nextLaudaRoute = now.AddMilliseconds(400);
                if (laudaRoute.Length == 0 && legal.Length == 0 && now >= nextLaudaDiagnostic)
                {
                    ff14bot.Helpers.Logging.Write("[CrucibleLauda] No legal mechanic refuge: force={0} push={1} fields={2} player={3}.", force != null, laudaPushAction, fields.Length, Core.Me.Location);
                    nextLaudaDiagnostic = now.AddSeconds(2);
                }
            }

            while (laudaRoute.Length > 0 && V2.Distance(player, laudaRoute[0]) < .25f)
                laudaRoute = laudaRoute.Skip(1).ToArray();
            if (force != null && laudaDestination.HasValue)
                face = (laudaDestination.Value.Y < -420 ? 0 : MathF.PI) - forceOffset;
            else if (now < forcePreparingUntil && !forceObserved && laudaDestination.HasValue)
                face = laudaDestination.Value.Y < -420 ? 0 : MathF.PI;
            // A bound/petrified client can display a local facing change that
            // the server has not accepted. Preserve the application frame until
            // Bind ends, then establish the actual knockback-facing direction.
            if (Core.Me.HasAura(5555) && forceBindFacing.HasValue)
                face = forceBindFacing;
            if (laudaRoute.Length == 0 || Core.Me.HasAura(5555))
            {
                if (face.HasValue)
                {
                    // Renew the same facing lease while stationary. Clearing
                    // and readding it every pulse floods logs and exposes gaps.
                    if (laudaMoving)
                        MovementManager.MoveStop();
                    laudaMoving = false;
                    HoldLaudaFacing(face.Value);
                }
                else
                    StopLaudaMovement();
                return;
            }

            // Movement owns facing only in transit; stationary shelters continue
            // normal damage unless the directional debuff requires a fixed aim.
            CapabilityManager.Update(laudaPosition, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Lauda mechanic transit");
            laudaFacingHeld = laudaMoving = true;
            if (now >= nextLaudaTransitLog)
            {
                nextLaudaTransitLog = now.AddSeconds(1);
                ff14bot.Helpers.Logging.Write("[CrucibleLauda] Local transit: player={0} next={1} destination={2} fields={3} contacts={4} nativeEscaping={5}.", player, laudaRoute[0], laudaDestination, fields.Length, contacts.Length, AvoidanceManager.IsRunningOutOfAvoid);
            }

            Navigator.PlayerMover.MoveTowards(new V3(laudaRoute[0].X, 0, laudaRoute[0].Y));
        }

        private void HoldLaudaFacing(float heading)
        {
            CapabilityManager.Update(laudaPosition, CapabilityFlags.Facing, TimeSpan.FromMilliseconds(600), "Lauda directional knockback");
            laudaFacingHeld = true;
            laudaAutoFacing ??= GameSettingsManager.FaceTargetOnAction;
            GameSettingsManager.FaceTargetOnAction = false;
            var p = Point(Core.Me.Location) + ThirdBoardGeometry.Direction(heading) * 5;
            MovementManager.SetFacing(new V3(p.X, 0, p.Y));
        }

        private void RestoreLaudaFacing()
        {
            if (laudaAutoFacing.HasValue)
                GameSettingsManager.FaceTargetOnAction = laudaAutoFacing.Value;
            laudaAutoFacing = null;
        }

        private void StopLaudaMovement()
        {
            if (laudaMoving && !AvoidanceManager.IsRunningOutOfAvoid)
                MovementManager.MoveStop();
            laudaMoving = false;
            if (laudaFacingHeld)
                CapabilityManager.Clear(laudaPosition, CapabilityFlags.Facing, "Lauda transit ended");
            laudaFacingHeld = false;
        }

        private void ReleaseLaudaPosition()
        {
            StopLaudaMovement();
            if (laudaHeld)
                CapabilityManager.Clear(laudaPosition, CapabilityFlags.Movement, "Lauda mechanic resolved");
            laudaHeld = false;
            laudaRoute = Array.Empty<V2>();
            laudaDestination = null;
            forceBindFacing = null;
            forceApplicationHeading = null;
            forcePreparingUntil = default;
            forceResponses.Clear();
            RestoreLaudaFacing();
        }

        private void ResetLauda()
        {
            ReleaseLaudaPosition();
            laudaCages.Clear();
            daggers.Clear();
            bladeVisibility.Clear();
            daggerUntil = pointUntil = moltenUntil = laudaPushUntil = laudaPushAt = forceUntil = default;
            nextLaudaRoute = nextLaudaPublication = nextLaudaDiagnostic = default;
            nextLaudaSprint = nextLaudaTransitLog = default;
            forceObserved = false;
            laudaPushAction = 0;
            forceBindFacing = null;
            laudaPublished = Array.Empty<ThirdBoardHazard>();
            moltenBaits = null;
            moltenCageActors = Array.Empty<uint>();
        }
    }
}
