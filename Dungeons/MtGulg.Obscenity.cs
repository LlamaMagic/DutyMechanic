using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class MtGulg
{
    private bool floorActive, floorActivated, floorWarningSent;
    private uint floorOwner;
    private DateTime floorDiscoveryUntil, floorReleaseAt;
    private readonly Dictionary<uint, int> floorStates = [];
    private readonly Dictionary<uint, BeamLane> beamLanes = [];
    private readonly HashSet<uint> spentOrbs = [];
    private DateTime solitaireUntil;
    private bool lateLanesReleased;
    private readonly CapabilityManagerHandle marionetteHandle = CapabilityManager.CreateNewHandle();
    private DateTime marionetteStageUntil;
    private bool movingToMarionetteStage;

    private void BeginMarionetteStage(DateTime now)
    {
        // A corner-to-corner dodge takes longer than Diminuendo's 3.7s tell.
        // Marionette announces it about 12s earlier, leaving time to stage centrally.
        marionetteStageUntil = now.AddSeconds(15);
        Logger.Information("[MtGulg] Marionette preparation acquired from={0} to={1}", Core.Me.Location, ObscenityCenter);
    }

    private void UpdateMarionetteStage()
    {
        if (marionetteStageUntil == DateTime.MinValue)
        {
            return;
        }
        if (Pending().Any(c => c.Action is 15638 or 15640 or 15649))
        {
            ReleaseMarionetteStage("Diminuendo telegraph handed to native avoidance");
            return;
        }
        if (DateTime.UtcNow >= marionetteStageUntil || boss != 3)
        {
            ReleaseMarionetteStage("bounded preparation expired");
            return;
        }
        if (movement == null)
        {
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || !OrdinarySafe(ObscenityCenter))
        {
            movement.Request(null, 0);
            CapabilityManager.Clear(marionetteHandle, CapabilityFlags.Movement, "Mt. Gulg preparation yields to hazard");
            movingToMarionetteStage = false;
            return;
        }
        CapabilityManager.Update(marionetteHandle, CapabilityFlags.Movement, 1000, "Mt. Gulg Marionette preparation");
        movement.Request(ObscenityCenter, .35f);
        // Keep healing available after arrival; release on the actual follow-up tell.
        movingToMarionetteStage = !MovementManager.GenerallyOutOfControl && Core.Me.Location.Distance2D(ObscenityCenter) > .35f;
    }

    private void ReleaseMarionetteStage(string reason)
    {
        if (marionetteStageUntil != DateTime.MinValue)
        {
            Logger.Information("[MtGulg] Marionette preparation released reason={0} at={1}", reason, Core.Me?.Location);
            movement?.Request(null, 0);
            CapabilityManager.Clear(marionetteHandle, CapabilityFlags.Movement, "Mt. Gulg preparation released");
        }
        marionetteStageUntil = DateTime.MinValue;
        movingToMarionetteStage = false;
    }

    private sealed class BeamLane
    {
        internal uint Id;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Resolved;
        internal DateTime Discovered;
        internal bool Late;
    }

    // Snapshot moving orb poses rather than retaining frame-scoped actor wrappers.
    private sealed class OrbForecast
    {
        internal Vector3 Origin;
        internal float Heading;
    }

    private bool FloorActive => floorActive && DateTime.UtcNow < floorReleaseAt;

    private void BeginPenance(Cast cast)
    {
        floorActive = true;
        floorActivated = false;
        floorWarningSent = false;
        floorReleaseAt = DateTime.MaxValue;
        // Two floor objects share this position. Only the early state edge belongs
        // to Penance; the other changes about eight seconds later for Marionette.
        floorDiscoveryUntil = cast.Finish.AddSeconds(2);
    }

    private void ResetFloor()
    {
        floorActive = floorActivated = false;
        floorOwner = 0;
        floorStates.Clear();
        floorReleaseAt = DateTime.MinValue;
    }

    private void BeginSolitaire(DateTime now)
    {
        ReleaseBeamPosition("new wave");
        beamRoute = null;
        beamHoldReached = false;
        beamRouteWarningSent = false;
        ReleaseMarionetteStage("Solitaire preparation superseded Marionette");
        beamLanes.Clear();
        spentOrbs.Clear();
        lateLanesReleased = false;
        // The final ring resolves about 21s after the parent; 30s bounds stale state.
        solitaireUntil = now.AddSeconds(30);
        Logger.Information("[MtGulg] Solitaire discovery started");
    }

    private void EndSolitaire(string reason)
    {
        if (solitaireUntil != DateTime.MinValue)
        {
            Logger.Information("[MtGulg] Solitaire released reason={0}", reason);
        }
        solitaireUntil = DateTime.MinValue;
        ReleaseBeamPosition(reason);
        beamRoute = null;
        beamLanes.Clear();
        lateLanesReleased = false;
    }

    private void UpdateObscenityEnvironment()
    {
        DateTime now = DateTime.UtcNow;
        var objects = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(a => a.IsValid && a.Distance2D(ObscenityCenter) < 45).ToArray();
        foreach (var actor in objects.Where(a => a.BaseId == 0x1EA1A1 && a.Distance2D(ObscenityCenter) < 1))
        {
            int? state = ReadTimeline(actor);
            if (!state.HasValue)
            {
                continue;
            }
            bool changed = !floorStates.TryGetValue(actor.ObjectId, out int previous) || previous != state.Value;
            if (floorActive && state == 1 && (actor.ObjectId == floorOwner ||
                changed && now <= floorDiscoveryUntil))
            {
                if (floorOwner != actor.ObjectId || !floorActivated)
                {
                    Logger.Information("[MtGulg] Penance floor active owner={0:X8}", actor.ObjectId);
                }
                floorOwner = actor.ObjectId;
                floorActivated = true;
            }
            if (changed && actor.ObjectId == floorOwner && floorActivated && state == 4)
            {
                // Only the identified floor owner may release this boundary.
                // Keep 0.5s beyond its reset for the effect tail.
                floorReleaseAt = now.AddMilliseconds(500);
                Logger.Information("[MtGulg] Penance floor reset owner={0:X8} release={1:O}", floorOwner, floorReleaseAt);
            }
            floorStates[actor.ObjectId] = state.Value;
        }
        if (floorActive && now >= floorReleaseAt)
        {
            floorActive = false;
            floorActivated = false;
        }
        if (floorActive && !floorActivated && !floorWarningSent && now > floorDiscoveryUntil)
        {
            floorWarningSent = true;
            Logger.Warning("[MtGulg] Penance state unresolved; conservative floor retained, lifecycle unverified");
        }

        if (solitaireUntil == DateTime.MinValue)
        {
            return;
        }
        foreach (var actor in objects.Where(a => a.BaseId == 0x1EAB62))
        {
            int? state = ReadTimeline(actor);
            if (!beamLanes.TryGetValue(actor.ObjectId, out var lane))
            {
                // State 4 can belong to the previous wave; discover only state 1.
                if (state != 1)
                {
                    continue;
                }
                lane = new BeamLane { Id = actor.ObjectId, Origin = actor.Location, Heading = actor.Heading, Discovered = now };
                beamLanes.Add(actor.ObjectId, lane);
                Logger.Information("[MtGulg] Ring discovered id={0:X8} origin={1} heading={2:F3}", lane.Id, lane.Origin, lane.Heading);
            }
            if (state == 4 && lane.Resolved == DateTime.MinValue)
            {
                lane.Resolved = now;
                // A reset retires the matching orb's contact hazard too, even if
                // its harmless trailing animation remains visible.
                foreach (var orb in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a.IsValid && a.BaseId == 0x27D0 &&
                        Math.Abs((a.X - lane.Origin.X) * Math.Cos(lane.Heading) -
                            (a.Z - lane.Origin.Z) * Math.Sin(lane.Heading)) < .5))
                {
                    spentOrbs.Add(orb.ObjectId);
                }
                Logger.Information("[MtGulg] Ring resolved id={0:X8} origin={1}", lane.Id, lane.Origin);
            }
        }
        UpdateBeamStage(now);
        if (beamLanes.Count == 6 && beamLanes.Values.All(l => l.Resolved != DateTime.MinValue &&
            now >= l.Resolved.AddMilliseconds(500)))
        {
            EndSolitaire("all six ring effects cleared");
        }
        else if (now >= solitaireUntil)
        {
            EndSolitaire("bounded observation expired; wave unverified");
        }
    }

    private void UpdateBeamStage(DateTime now)
    {
        if (beamLanes.Count != 6)
        {
            return;
        }
        var lanes = beamLanes.Values.ToArray();
        float heading = lanes[0].Heading;
        if (lanes.Any(l => Math.Cos(l.Heading - heading) < .999))
        {
            return;
        }
        float Forward(BeamLane lane) => (lane.Origin.X - ObscenityCenter.X) * (float)Math.Sin(heading) +
                    (lane.Origin.Z - ObscenityCenter.Z) * (float)Math.Cos(heading);
        float Lateral(BeamLane lane) => lane.Origin.X * (float)Math.Cos(heading) - lane.Origin.Z * (float)Math.Sin(heading);
        float latest = lanes.Max(Forward);
        foreach (var lane in lanes)
        {
            lane.Late = Forward(lane) > latest - .5f;
        }        // Acquire once per wave; unknown or late layouts retain native fallback.
        if (beamRoute == null && !beamRouteWarningSent)
        {
            beamRoute = PlanBeamRoute(lanes, Core.Me.Location, now);
            if (beamRoute != null)
            {
                Logger.Information("[MtGulg] Beam route acquired hold={0} exit={1} arriveBy={2:O}",
                                beamRoute.Hold, beamRoute.Exit, beamRoute.ArriveBy);
            }
            else
            {
                beamRouteWarningSent = true;
                Logger.Warning("[MtGulg] Beam route unavailable; native fallback retained, sequence unverified");
            }
        }
        // Ring depth sets firing order. Cross only after an adjacent column resets
        // plus 0.5s; the remaining 2-3s allows a normal-speed five-yalm crossing.
        if (!lateLanesReleased && lanes.Where(l => l.Late).All(late => lanes.Any(early =>
            !early.Late && Math.Abs(Lateral(early) - Lateral(late)) <= 5.2f &&
            early.Resolved != DateTime.MinValue && now >= early.Resolved.AddMilliseconds(500))))
        {
            lateLanesReleased = true;
            Logger.Information("[MtGulg] Solitaire stage=escape-latest after adjacent ring reset");
        }
    }

    private IEnumerable<BeamLane> BeamHazards()
    {
        if (beamLanes.Count != 6 || solitaireUntil == DateTime.MinValue)
        {
            return [];
        }
        DateTime now = DateTime.UtcNow;
        return beamLanes.Values.Where(lane =>
            lane.Resolved != DateTime.MinValue ? now < lane.Resolved.AddMilliseconds(500) :
            !lane.Late || lateLanesReleased).ToArray();
    }

    private IEnumerable<Vector3> OrbPositions() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
        .Where(a => a.IsValid && a.IsVisible && a.BaseId == 0x27D0 && !spentOrbs.Contains(a.ObjectId) &&
            a.Distance2D(ObscenityCenter) < 40)
        .Select(a => a.Location).ToArray();

    private IEnumerable<OrbForecast> ForecastOrbs() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
        .Where(a => a.IsValid && a.IsVisible && a.BaseId == 0x27D0 && !spentOrbs.Contains(a.ObjectId) &&
            a.Distance2D(ObscenityCenter) < 40)
        .Select(a => new OrbForecast { Origin = a.Location, Heading = a.Heading }).ToArray();
}
