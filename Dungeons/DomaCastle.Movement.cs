using System;
using System.Linq;
using System.Collections.Generic;
using Clio.Utilities;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class DomaCastle
{
    // The exploratory capture's three-second moving windows had a 4.79 y/s
    // tenth percentile. Reserve one second and plan at 4.5 y/s; this remains an
    // estimate, not a guarantee under slow, forced movement or turning.
    private const float PlanningSpeed = 4.5f;
    private readonly CapabilityManagerHandle positioningHandle = CapabilityManager.CreateNewHandle();
    private ChrysalisMovement positioningMover;
    private bool positioningLeased;
    private Vector3? chargeOrigin, chargeDestination, chargeBaitDestination;
    private DateTime chargeCastEnd, chargeUntil;
    private Vector3[] towerPositions = Array.Empty<Vector3>();
    private Vector3? selectedTower, towerDestination;
    private DateTime towerCastEnd, towerUntil;
    private DateTime towerObservedAt, towerSampleAt;
    private readonly Dictionary<uint, Vector3> towerAllySample = new();
    private Vector3[] projectedTowerOccupants = Array.Empty<Vector3>();
    private DateTime retryChargeSearch, retryChargeBaitSearch;
    private readonly Queue<(DateTime Time, Vector3 Position)> travelSamples = new();
    private DateTime nextTravelSample, nextTravelRefresh;
    private Vector3? gunsawDestination;
    private int gunsawTurn;
    private DateTime gunsawUntil;
    private Vector3? loggedDestination;
    private Vector3? hexCirclePosition;
    private DateTime hexCircleCastEnd, hexCircleUntil;
    private static uint timelineActor;
    private static int timelineValue = -1;
    private static DateTime nextTimelineRead;
    private sealed class FireWindow
    {
        internal Vector3 Position;
        internal DateTime CastEnd;
        internal DateTime Until;
    }
    private readonly Dictionary<uint, FireWindow> fireWindows = new();
    // The public animation getter is read on the bot thread and bounded to
    // 10 Hz. Unknown values or failed reads must not create a phantom lane.
    private static bool HasGunsawTimeline(BattleCharacter actor)
    {
        DateTime now = DateTime.UtcNow;
        if (actor.ObjectId != timelineActor || now >= nextTimelineRead)
        {
            timelineActor = actor.ObjectId;
            nextTimelineRead = now.AddMilliseconds(100);
            timelineValue = -1;
            try
            {
                if (!string.IsNullOrEmpty(actor.LuaString))
                {
                    string actorKey = actor.LuaString.Replace("\\", "\\\\").Replace("'", "\\'");
                    string value = Lua.GetReturnVal<string>("local o=_G['" + actorKey +
                        "']; if not o or not o.GetActionTimelineId then return '-1' end; return tostring(o:GetActionTimelineId())");
                    if (!int.TryParse(value, out timelineValue)) timelineValue = -1;
                }
            }
            catch (Exception) { timelineValue = -1; }
        }
        return timelineValue == 5404 || timelineValue == 5406;
    }

    // The capture found unoccupied 8357 towers and a 8367 falloff hit at close
    // range. These requests share RB's mover with native escape, and are scoped
    // to the cast and effect window rather than owning input for the whole fight.
    private void UpdatePositioning()
    {
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            WorldManager.ZoneId != (uint)Data.ZoneId.DomaCastle || !DutyManager.InInstance || Core.Me == null ||
            !Core.Me.IsValid || !Core.Me.IsAlive)
        {
            ResetPositioning();
            return;
        }

        if (positioningMover != null && !positioningMover.IsCurrent)
        {
            positioningMover.Detach();
            positioningMover = null;
            positioningLeased = false;
            CapabilityManager.Clear(positioningHandle, CapabilityFlags.Movement, "Doma mover replaced");
            CapabilityManager.Clear(positioningHandle, CapabilityFlags.GapCloser, "Doma mover replaced");
        }

        // A November 11 trash escape ended while forward input continued and
        // moved Rowan away from the route. Keep this owner through the whole
        // instance so the native-escape cleanup also covers SideStep on trash.
        // Never take over in the middle of an existing escape or forced move.
        if (positioningMover == null && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
            Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            positioningMover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: OrdinarySegmentSafe);
            Navigator.PlayerMover = positioningMover;
        }

        if (!Core.Me.InCombat)
        {
            CheckOrdinaryTravel();
            chargeOrigin = chargeDestination = chargeBaitDestination = null;
            gunsawDestination = null;
            gunsawTurn = 0;
            gunsawUntil = DateTime.MinValue;
            hexCirclePosition = null;
            towerPositions = Array.Empty<Vector3>();
            fireWindows.Clear();
            ReleasePositioning();
            return;
        }

        travelSamples.Clear();
        nextTravelSample = DateTime.MinValue;
        var allActors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid).ToArray();
        var actors = allActors.Where(actor => actor.IsCasting && actor.SpellCastInfo.IsValid).ToArray();
        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        bool Safe(Vector3 point) => !avoids.Any(avoid => avoid.IsPointInAvoid(point));
        DateTime now = DateTime.UtcNow;
        if (WorldManager.SubZoneId == (uint)SubZoneId.TheThirdArmory)
        {
            foreach (var actor in actors.Where(actor => actor.BaseId == 0x1BCD && actor.CastingSpellId == 8351))
            {
                DateTime castEnd = now + actor.SpellCastInfo.RemainingCastTime;
                // On November 11 Rowan took the fire hit about one second after
                // its 2.7s helper cast, having returned to the expired circle.
                fireWindows[actor.ObjectId] = new FireWindow
                    { Position = actor.Location, CastEnd = castEnd, Until = castEnd.AddSeconds(1.5) };
            }
            foreach (var entry in fireWindows.ToArray())
            {
                var actor = allActors.FirstOrDefault(candidate => candidate.ObjectId == entry.Key);
                bool interrupted = actor != null && (!actor.IsCasting || actor.CastingSpellId != 8351) &&
                    now < entry.Value.CastEnd.AddMilliseconds(-300);
                if (interrupted || now >= entry.Value.Until) fireWindows.Remove(entry.Key);
            }
        }
        else fireWindows.Clear();
        Vector3? target = null;
        if (WorldManager.SubZoneId == (uint)SubZoneId.TrainingGrounds)
        {
            chargeOrigin = chargeDestination = chargeBaitDestination = null;
            var circle = actors.FirstOrDefault(actor => actor.BaseId == 0x1BD0 && actor.CastingSpellId == 8354);
            if (circle != null)
            {
                hexCirclePosition = circle.Location;
                hexCircleCastEnd = now + circle.SpellCastInfo.RemainingCastTime;
                // Four captured 8354 effects appeared around one second after
                // the 2.7s cast. Keep the locked footprint through that delay.
                hexCircleUntil = hexCircleCastEnd.AddSeconds(1.5);
            }
            else if (hexCirclePosition.HasValue && (now < hexCircleCastEnd.AddMilliseconds(-300) || now >= hexCircleUntil))
                hexCirclePosition = null;
            var towers = actors.Where(actor => actor.BaseId == 0x1BD1 && actor.CastingSpellId == 8357).ToArray();
            if (towers.Length > 0)
            {
                if (towerPositions.Length == 0 || now > towerUntil)
                {
                    towerObservedAt = now;
                    towerSampleAt = DateTime.MinValue;
                    towerAllySample.Clear();
                    projectedTowerOccupants = Array.Empty<Vector3>();
                    selectedTower = towerDestination = null;
                }
                towerPositions = towers.Select(actor => actor.Location).ToArray();
                towerCastEnd = now + towers.Min(actor => actor.SpellCastInfo.RemainingCastTime);
                // The missed tower's damage followed the cast by about 1.5s.
                towerUntil = towerCastEnd.AddSeconds(2);
            }
            else if (towerPositions.Length > 0 && (now < towerCastEnd.AddMilliseconds(-300) || now > towerUntil))
            {
                towerPositions = Array.Empty<Vector3>();
                selectedTower = towerDestination = null;
            }
            var party = PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
                .Where(actor => actor != null && actor.IsValid && actor.IsAlive && actor.ObjectId != Core.Me.ObjectId)
                .ToArray();
            // Unknown party positions cannot establish that a tower is unoccupied.
            if (towerPositions.Length > 0 && party.Length >= 3)
            {
                float seconds = (float)(towerCastEnd - now).TotalSeconds;
                if (towerPositions.Length > 1 && (now - towerSampleAt).TotalMilliseconds >= 500)
                {
                    float elapsed = towerSampleAt == DateTime.MinValue ? 0 : (float)(now - towerSampleAt).TotalSeconds;
                    projectedTowerOccupants = party.Where(actor => towerAllySample.ContainsKey(actor.ObjectId))
                        .Select(actor => DomaCastlePlanner.ApproachedTower(towerAllySample[actor.ObjectId], actor.Location,
                            elapsed, towerPositions, Math.Max(0, seconds)))
                        .Where(position => position.HasValue).Select(position => position.Value).ToArray();
                    towerAllySample.Clear();
                    foreach (var ally in party) towerAllySample[ally.ObjectId] = ally.Location;
                    towerSampleAt = now;
                }
                // In the captured two-tower pattern all three NPCs initially
                // stood closer to the tower they did not take. A short motion
                // sample avoids committing Rowan to their common destination.
                bool sampling = towerPositions.Length > 1 && (now - towerObservedAt).TotalSeconds < 1.5 && seconds > 4f;
                if (!sampling)
                {
                    float budget = Math.Max(0, seconds - 1f) * PlanningSpeed + 0.75f;
                    bool retained = selectedTower.HasValue && towerDestination.HasValue &&
                        towerPositions.Any(position => position.Distance2D(selectedTower.Value) < 1f) &&
                        !party.Any(ally => ally.Location.Distance2D(selectedTower.Value) <= 6f) &&
                        Core.Me.Location.Distance2D(towerDestination.Value) <= budget &&
                        DomaCastlePlanner.SegmentSafe(Core.Me.Location, towerDestination.Value, Safe);
                    if (!retained)
                    {
                        selectedTower = null;
                        towerDestination = DomaCastlePlanner.UnoccupiedTower(Core.Me.Location,
                            towerPositions, party.Select(actor => actor.Location).Concat(projectedTowerOccupants),
                            Safe, budget);
                        if (towerDestination.HasValue)
                            selectedTower = towerPositions.OrderBy(position => position.Distance2D(towerDestination.Value)).First();
                    }
                    // Do not reselect every pulse: the November 11 capture
                    // alternated adjacent safe candidates every 30–100 ms.
                    target = towerDestination;
                }
            }
        }
        else if (WorldManager.SubZoneId == (uint)SubZoneId.HalloftheScarletSwallow)
        {
            var caster = actors.FirstOrDefault(actor => actor.BaseId == 0x1BD5 && actor.CastingSpellId == 8367);
            // The November 11 capture showed self Prey 1253 about five seconds
            // before 8367 locked its origin at Rowan's position. Place that
            // origin southwest first, then escape it using the existing owner.
            bool selfPrey = Core.Me.Auras.Any(aura => aura.Id == 1253);
            if (selfPrey && caster == null && !chargeOrigin.HasValue)
            {
                if (chargeBaitDestination.HasValue &&
                    !DomaCastlePlanner.SegmentSafe(Core.Me.Location, chargeBaitDestination.Value, Safe))
                    chargeBaitDestination = null;
                var baitCorner = new Vector3(ArenaCenter.HypertunedGrynewaht.X - 18f, Core.Me.Location.Y,
                    ArenaCenter.HypertunedGrynewaht.Z + 18f);
                // A temporarily blocked path can leave the current position as
                // the safest candidate. Retry without searching every bot pulse.
                if (chargeBaitDestination.HasValue && Core.Me.Location.Distance2D(chargeBaitDestination.Value) <= 0.75f &&
                    Core.Me.Location.Distance2D(baitCorner) > 1f && now >= retryChargeBaitSearch)
                    chargeBaitDestination = null;
                if (!chargeBaitDestination.HasValue && now >= retryChargeBaitSearch)
                {
                    retryChargeBaitSearch = now.AddMilliseconds(250);
                    chargeBaitDestination = DomaCastlePlanner.ChargeBaitDestination(Core.Me.Location,
                        ArenaCenter.HypertunedGrynewaht, Safe);
                }
                target = chargeBaitDestination;
            }
            else chargeBaitDestination = null;
            if (caster != null)
            {
                Vector3 origin = caster.SpellCastInfo.CastLocation;
                // Some helpers expose an unset ground target for a pulse. The
                // captured charge helper itself occupied the actual impact point.
                if (origin.Distance2D(ArenaCenter.HypertunedGrynewaht) > 40f)
                    origin = caster.Location;
                if (origin.Distance2D(ArenaCenter.HypertunedGrynewaht) > 40f)
                {
                    chargeOrigin = chargeDestination = null;
                    ReleasePositioning();
                    return;
                }
                if (!chargeOrigin.HasValue || chargeOrigin.Value.Distance2D(origin) > 1f || now > chargeUntil)
                {
                    chargeOrigin = origin;
                    chargeDestination = null;
                    retryChargeSearch = DateTime.MinValue;
                }
                chargeCastEnd = now + caster.SpellCastInfo.RemainingCastTime;
                // The observed second charge damaged Rowan about one second after
                // the cast end. Retain the hold through that effect, with margin.
                chargeUntil = chargeCastEnd.AddSeconds(2);
            }
            else if (chargeOrigin.HasValue && now < chargeCastEnd.AddMilliseconds(-300))
            {
                // A vanished or interrupted cast is not evidence of a pending hit.
                chargeOrigin = chargeDestination = null;
            }
            if (chargeOrigin.HasValue && now <= chargeUntil)
            {
                if (chargeDestination.HasValue && !DomaCastlePlanner.SegmentSafe(Core.Me.Location, chargeDestination.Value, Safe))
                    chargeDestination = null;
                if (!chargeDestination.HasValue && now >= retryChargeSearch)
                {
                    retryChargeSearch = now.AddMilliseconds(250);
                    float seconds = Math.Max(0, (float)(chargeCastEnd - now).TotalSeconds - 1f);
                    chargeDestination = DomaCastlePlanner.ChargeDestination(Core.Me.Location, chargeOrigin.Value,
                        ArenaCenter.HypertunedGrynewaht, 19.5f, Math.Min(30f, seconds * PlanningSpeed), Safe);
                }
                target = chargeDestination;
            }
            else chargeOrigin = chargeDestination = null;

            var boss = allActors.FirstOrDefault(actor => actor.BaseId == 0x1BD4 && actor.IsAlive);
            bool sawGunsaw = boss != null && HasGunsawTimeline(boss);
            if (sawGunsaw && (gunsawDestination.HasValue ||
                DomaCastlePlanner.InGunsawBaitLine(Core.Me.Location, boss.Location, boss.Heading)))
                gunsawUntil = now.AddMilliseconds(500);
            if (gunsawUntil > now && boss != null && !target.HasValue)
            {
                if (gunsawDestination.HasValue &&
                    (Core.Me.Location.Distance2D(gunsawDestination.Value) <= 0.75f ||
                     !DomaCastlePlanner.SegmentSafe(Core.Me.Location, gunsawDestination.Value, Safe)))
                    gunsawDestination = null;
                if (!gunsawDestination.HasValue)
                {
                    if (gunsawTurn == 0)
                        gunsawTurn = DomaCastlePlanner.GunsawOrbitDirection(Core.Me.Location, boss.Location,
                            ArenaCenter.HypertunedGrynewaht, 19.5f, Safe);
                    gunsawDestination = DomaCastlePlanner.GunsawOrbitLeg(Core.Me.Location, boss.Location,
                        ArenaCenter.HypertunedGrynewaht, 19.5f, gunsawTurn, Safe);
                }
                // Native escape and charge positioning retain priority; keep
                // orbit direction until the sequence ends to avoid wall bounces.
                target = gunsawDestination;
            }
            else if (gunsawUntil <= now) { gunsawDestination = null; gunsawTurn = 0; }
        }
        else { chargeOrigin = chargeDestination = chargeBaitDestination = null; gunsawDestination = null; gunsawTurn = 0; gunsawUntil = DateTime.MinValue; }

        // GenerallyOutOfControl also becomes true during ordinary casts. The
        // November 11 charge hold released/reacquired on each Stone cast even
        // though Rowan had arrived. Retain the lease while casting at the
        // destination; real forced motion or an unfinished approach still yields.
        bool castingAtDestination = target.HasValue && Core.Me.IsCasting &&
            Core.Me.Location.Distance2D(target.Value) <= 0.75f;
        if (!target.HasValue || (MovementManager.GenerallyOutOfControl && !castingAtDestination) || AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleasePositioning();
            return;
        }
        if (positioningMover == null) return;
        CapabilityManager.Update(positioningHandle, CapabilityFlags.Movement, 1000, "Doma mechanic position");
        CapabilityManager.Update(positioningHandle, CapabilityFlags.GapCloser, 1000, "Doma mechanic position");
        positioningLeased = true;
        if (!loggedDestination.HasValue || loggedDestination.Value.Distance2D(target.Value) > 0.5f)
        {
            ff14bot.Helpers.Logging.WriteDiagnostic("[Doma] Positioning target={0} chargeOrigin={1}.", target.Value, chargeOrigin);
            loggedDestination = target;
        }
        positioningMover.Request(target, 0.75f);
        positioningMover.Update();
    }

    private void CheckOrdinaryTravel()
    {
        DateTime now = DateTime.UtcNow;
        if (positioningMover == null || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid)
        {
            travelSamples.Clear();
            nextTravelSample = DateTime.MinValue;
            return;
        }
        if (now < nextTravelSample) return;
        if (travelSamples.Count > 0 && (now - nextTravelSample).TotalSeconds > 3)
            travelSamples.Clear(); // A long observation gap cannot prove a loop.
        nextTravelSample = now.AddSeconds(1);
        travelSamples.Enqueue((now, Core.Me.Location));
        while (travelSamples.Count > 1 && (now - travelSamples.Peek().Time).TotalSeconds > 76)
            travelSamples.Dequeue();
        // The captured cannon loop repeated about every 12 seconds within an
        // 11-yalm envelope. Refresh after six cycles, never on a stationary wait.
        if ((now - travelSamples.Peek().Time).TotalSeconds < 75 || now < nextTravelRefresh ||
            !DomaCastlePlanner.ConfinedTravel(travelSamples.Select(sample => sample.Position).ToArray(), 20f, 30f)) return;
        if (!positioningMover.RefreshOrdinaryPath()) return; // Current hazard/owner keeps priority.
        ff14bot.Helpers.Logging.WriteDiagnostic("[Doma] Refreshed confined ordinary navigation; same route retained.");
        // A refresh is not a clear or a retry limit. Recheck and try again if
        // the provider remains confined, without spinning in a tight loop.
        nextTravelRefresh = now.AddSeconds(45);
        travelSamples.Clear();
    }

    private static bool OrdinarySegmentSafe(Vector3 waypoint)
    {
        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        return DomaCastlePlanner.SegmentSafe(Core.Me.Location, waypoint,
            point => !avoids.Any(avoid => avoid.IsPointInAvoid(point)));
    }

    private void ReleasePositioning()
    {
        if (positioningLeased)
        {
            CapabilityManager.Clear(positioningHandle, CapabilityFlags.Movement, "Doma positioning yielded");
            CapabilityManager.Clear(positioningHandle, CapabilityFlags.GapCloser, "Doma positioning yielded");
            ff14bot.Helpers.Logging.WriteDiagnostic("[Doma] Positioning released.");
        }
        positioningLeased = false;
        loggedDestination = null;
        if (positioningMover == null) return;
        positioningMover.Request(null, 0);
        positioningMover.Update();
        // Travel and combat still need native escape cleanup and waypoint
        // admission when no positive-position mechanic is active.
        if (TreeRoot.IsRunning && WorldManager.ZoneId == (uint)Data.ZoneId.DomaCastle && DutyManager.InInstance &&
            Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene) return;
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
        positioningMover.Detach();
        positioningMover = null;
    }

    private void ResetPositioning()
    {
        travelSamples.Clear();
        nextTravelSample = nextTravelRefresh = DateTime.MinValue;
        chargeOrigin = chargeDestination = chargeBaitDestination = null;
        retryChargeBaitSearch = DateTime.MinValue;
        fireWindows.Clear();
        hexCirclePosition = null;
        hexCircleCastEnd = hexCircleUntil = DateTime.MinValue;
        gunsawDestination = null;
        gunsawTurn = 0;
        gunsawUntil = DateTime.MinValue;
        timelineActor = 0;
        timelineValue = -1;
        nextTimelineRead = DateTime.MinValue;
        towerPositions = Array.Empty<Vector3>();
        selectedTower = towerDestination = null;
        towerCastEnd = towerUntil = DateTime.MinValue;
        towerObservedAt = towerSampleAt = DateTime.MinValue;
        towerAllySample.Clear();
        projectedTowerOccupants = Array.Empty<Vector3>();
        ReleasePositioning();
        if (positioningMover == null) return;
        positioningMover.Detach(MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid);
        positioningMover = null;
    }
}
