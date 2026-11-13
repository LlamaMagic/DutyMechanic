using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using NVector2 = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

public partial class HolminsterSwitch
{
    private bool orbRouteOwned;
    private bool orbRouteSearching;
    private HolminsterOrbRoute.Route? orbRoute;
    private DateTime orbReleaseAt;
    private DateTime orbForecastCreated;
    private OrbCross orbForecast;
    private string orbForecastWave;
    private string orbSnapshotKey;
    private string orbAttemptedKey;
    private NVector2 orbSearchStart;
    private DateTime orbSearchAt;
    private Task<HolminsterOrbRoute.Route?> orbSearch;
    private CancellationTokenSource orbSearchCancel;
    private bool orbSecondStage;
    private DateTime orbMotionWarmUntil;
    private DateTime orbMotionSampleAt;
    private NVector2 orbMotionSample;
    private readonly HolminsterOrbRoute.SpeedEvidence orbSpeedEvidence = new();
    private readonly CapabilityManagerHandle orbRouteHandle = CapabilityManager.CreateNewHandle();

    private static NVector2 PlanPoint(Vector3 p) => new(p.X, p.Z);
    private static Vector3 WorldPoint(NVector2 p) => new(p.X, Core.Me.Location.Y, p.Y);

    private HolminsterOrbRoute.Hazard[] OrbHazards(DateTime now)
    {
        var hazards = new List<HolminsterOrbRoute.Hazard>();
        void Add(OrbCross shape, HolminsterOrbRoute.Shape kind)
        {
            if (shape != null && shape.Until > now)
                hazards.Add(new(kind, PlanPoint(shape.Origin), shape.Heading,
                    (shape.Opens - now).TotalSeconds, (shape.Until - now).TotalSeconds));
        }
        foreach (var line in orbLines.Values) Add(line, HolminsterOrbRoute.Shape.Line);
        foreach (var cross in orbCrosses.Values) Add(cross, HolminsterOrbRoute.Shape.Cross);
        Add(chargeWindow ?? orbForecast, HolminsterOrbRoute.Shape.Charge);
        return hazards.ToArray();
    }

    private string OrbHazardKey() => string.Join(",", orbLines.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.Until.Ticks}")) + "/" +
        string.Join(",", orbCrosses.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.Until.Ticks}")) + "/" +
        (chargeWindow != null ? $"charge:{chargeWindow.Origin.X:F1}:{chargeWindow.Origin.Z:F1}:{chargeWindow.Heading:F2}" :
            orbForecast != null ? "forecast" : "none");

    private void UpdateOrbRoute()
    {
        if (!DissonanceCombat() || bossMover?.IsCurrent != true)
        {
            ReleaseOrbRoute();
            return;
        }
        // A normal cast at the hold can set GenerallyOutOfControl and release
        // the lease before the hit.
        // Retain only a stationary, already-owned cast hold; forced movement
        // still preempts input and the route is revalidated below every tick.
        var heldTarget = orbRoute.HasValue ? (orbSecondStage ? orbRoute.Value.Final : orbRoute.Value.First) : default;
        bool castingHold = MovementManager.GenerallyOutOfControl && Core.Me.IsCasting && orbRouteOwned && orbRoute.HasValue &&
            NVector2.Distance(PlanPoint(Core.Me.Location), heldTarget) <= 0.3f;
        if (MovementManager.GenerallyOutOfControl && !castingHold)
        {
            ReleaseOrbRoute();
            return;
        }
        DateTime now = DateTime.UtcNow;
        string statuses = OrbStatuses();
        orbSpeedEvidence.Observe(PlanPoint(Core.Me.Location), now, statuses, MovementManager.Speed >= 4.5f);
        string lineWave = string.Join(",", orbLines.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value.Until.Ticks}"));
        if (orbForecast == null && chargeWindow == null && orbLines.Count == 4 && orbCrosses.Count >= 4)
        {
            var boss = GameObjectManager.GetObjectsOfType<ff14bot.Objects.BattleCharacter>(true, false)
                .FirstOrDefault(a => a.IsValid && a.IsAlive && a.BaseId == 0x278A);
            if (boss != null)
            {
                Vector3 p = boss.Location;
                // Four captures show the same short line wave followed about
                // 1.5s later by a charge through center, in both diagonal mirrors.
                // Restrict this forecast to those two sampled edge locations.
                bool sampledEdge = Math.Abs(Math.Abs(p.X + 15f) - 13.75f) < 1f &&
                    Math.Abs(Math.Abs(p.Z - 240f) - 13.75f) < 1f &&
                    Math.Sign(p.X + 15f) == Math.Sign(p.Z - 240f);
                if (sampledEdge && lineWave != orbForecastWave)
                {
                    orbForecastWave = lineWave;
                    orbForecastCreated = now;
                    orbForecast = new OrbCross { Origin = p,
                        Heading = (float)Math.Atan2(DissonanceCenter.X - p.X, DissonanceCenter.Z - p.Z),
                        Opens = now.AddSeconds(5.5), Until = now.AddSeconds(8.2) };
                }
            }
        }
        if (orbForecast != null && (chargeWindow != null || now > orbForecastCreated.AddSeconds(2.5)))
            orbForecast = null;
        var hazards = OrbHazards(now);
        // Waiting for the charge tell can leave too little time to exit the cross.
        // Start with captured crosses; new tells still invalidate or revalidate
        // the route and all non-orb avoids remain authoritative.
        if (hazards.Length == 0 || orbForecast == null && chargeWindow == null && orbCrosses.Count == 0 && !orbRouteOwned)
        {
            ReleaseOrbRoute();
            return;
        }
        string key = OrbHazardKey();
        var from = PlanPoint(Core.Me.Location);
        if (orbRouteOwned && orbRoute.HasValue)
        {
            var route = orbRoute.Value;
            if (!orbSecondStage && now >= orbReleaseAt && NVector2.Distance(from, route.First) <= 0.3f)
            {
                orbSecondStage = true;
                ResetOrbMotionSample(now);
            }
            var remaining = orbSecondStage ? new HolminsterOrbRoute.Route(route.Final, route.Final, 0, route.MinimumSpeed) :
                route with { ReleaseAt = Math.Max(0, (orbReleaseAt - now).TotalSeconds) };
            var target = orbSecondStage ? route.Final : route.First;
            if (!OrbRouteValid(from, remaining, hazards) || !OrbProgressSafe(now, from, target, remaining, hazards))
            {
                ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Orb route invalidated by current position or tell.");
                if (castingHold) ActionManager.StopCasting();
                ReleaseOrbRoute();
                if (MovementManager.GenerallyOutOfControl) return;
            }
            else
            {
                if (Core.Me.IsCasting && NVector2.Distance(from, target) > 0.3f) ActionManager.StopCasting();
                CapabilityManager.Update(orbRouteHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Dissonance ordered orb route");
                CapabilityManager.Update(orbRouteHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Dissonance ordered orb route");
                if (castingHold) return;
                bossMover.RequestHazardExit(WorldPoint(target), 0.2f);
                return;
            }
        }
        if (orbSearch != null)
        {
            if (!orbSearch.IsCompleted)
            {
                if (orbRouteSearching)
                {
                    CapabilityManager.Update(orbRouteHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Dissonance orb search hold");
                    CapabilityManager.Update(orbRouteHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Dissonance orb search hold");
                }
                return;
            }
            var completed = orbSearch;
            orbSearch = null;
            orbSearchCancel?.Dispose();
            orbSearchCancel = null;
            HolminsterOrbRoute.Route? route;
            try { route = completed.GetAwaiter().GetResult(); }
            catch (Exception error) { ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Orb search failed: {0}", error.Message); ReleaseOrbRoute(); return; }
            orbRouteSearching = false;
            if (key != orbSnapshotKey || NVector2.Distance(from, orbSearchStart) > 0.5f || !route.HasValue)
            {
                ReleaseOrbRoute();
                return;
            }
            var proposed = route.Value with { ReleaseAt = Math.Max(0, route.Value.ReleaseAt - (now - orbSearchAt).TotalSeconds) };
            if (!OrbRouteValid(from, proposed, hazards)) { ReleaseOrbRoute(); return; }
            orbRoute = proposed;
            orbReleaseAt = now.AddSeconds(proposed.ReleaseAt);
            orbSecondStage = false;
            orbRouteOwned = true;
            ResetOrbMotionSample(now);
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Orb route acquired first={0}, final={1}, release={2:F2}s, minimumSpeed={3:F2}.",
                proposed.First, proposed.Final, proposed.ReleaseAt, proposed.MinimumSpeed);
            bossMover.RequestHazardExit(WorldPoint(proposed.First), 0.2f);
            return;
        }
        // Native avoidance can cross an active charge toward the later cross.
        // Begin at the charge tell as well; revalidate each newly discovered cross.
        bool chargeFirst = chargeWindow != null && orbLines.Count == 0;
        bool crossFirst = chargeWindow == null && orbForecast == null && orbCrosses.Count > 0;
        if (orbAttemptedKey == key || !chargeFirst && !crossFirst && (orbLines.Count == 0 || orbCrosses.Count == 0)) return;
        // A long cross may already be resolving when a new
        // charge appeared. Holding until the later charge expired left no
        // complete route; hold the first-resolving hazard, then move for the next.
        double release = HolminsterOrbRoute.FirstRelease(hazards);
        if (release <= 0 || release > 7) return;
        orbAttemptedKey = key;
        orbSnapshotKey = key;
        orbSearchStart = from;
        orbSearchAt = now;
        orbSearchCancel = new CancellationTokenSource();
        var token = orbSearchCancel.Token;
        // Hold only if the captured position remains safe during calculation.
        bool hold = Enumerable.Range(0, 9).All(i => HolminsterOrbRoute.SafePoint(from, i * 0.1, hazards, 0.25f)) &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.Object is not OrbCross && a.IsPointInAvoid(Core.Me.Location));
        if (hold)
        {
            orbRouteSearching = true;
            orbRouteOwned = true;
            CapabilityManager.Update(orbRouteHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Dissonance orb search hold");
            CapabilityManager.Update(orbRouteHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Dissonance orb search hold");
            bossMover.RequestHazardExit(Core.Me.Location, 0.1f);
        }
        double measuredMinimum = orbSpeedEvidence.Minimum(now, statuses);
        orbSearch = Task.Run(() => HolminsterOrbRoute.Find(from, hazards, release, token, measuredMinimum), token);
    }

    private static string OrbStatuses()
    {
        var auras = Core.Player.Auras;
        return auras == null || !auras.IsValid ? null : string.Join(",", auras.OrderBy(a => a.Id)
            .ThenBy(a => a.Value).ThenBy(a => a.CasterId).Select(a => $"{a.Id}:{a.Value}:{a.CasterId}"));
    }

    private bool OrbRouteValid(NVector2 from, HolminsterOrbRoute.Route route, HolminsterOrbRoute.Hazard[] hazards)
    {
        // Unknown live avoids remain authoritative; only the snapshotted orb and
        // charge shapes may be crossed before their actual activation window.
        var other = AvoidanceManager.Avoids.Where(a => !a.ShouldIgnore && a.Object is not OrbCross).ToArray();
        bool Segment(NVector2 a, NVector2 b)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(NVector2.Distance(a, b) / 0.5));
            for (int i = 0; i <= steps; i++)
            {
                var point = NVector2.Lerp(a, b, (float)i / steps);
                var blocker = other.FirstOrDefault(avoid => avoid.IsPointInAvoid(WorldPoint(point)));
                if (blocker != null)
                {
                    return false;
                }
            }
            return true;
        }
        if (route.MinimumSpeed > orbSpeedEvidence.Minimum(DateTime.UtcNow, OrbStatuses()) + 0.001 ||
            !Segment(from, route.First) || !Segment(route.First, route.Final)) return false;
        foreach (double speed in HolminsterOrbRoute.SpeedRange(route.MinimumSpeed))
        foreach (double delay in new[] { 0.0, 0.3 })
            if (!HolminsterOrbRoute.Safe(from, route, hazards, speed, delay))
            {
                return false;
            }
        return true;
    }

    private void ResetOrbMotionSample(DateTime now)
    {
        // Do not classify starting, cast cancellation or a waypoint hold as a
        // slow debuff: the speed property briefly read 1.04 on startup,
        // while position samples in this encounter show ordinary ~6y/s travel.
        orbMotionWarmUntil = now.AddSeconds(0.6);
        orbMotionSampleAt = default;
    }

    private bool OrbProgressSafe(DateTime now, NVector2 from, NVector2 target,
        HolminsterOrbRoute.Route remaining, HolminsterOrbRoute.Hazard[] hazards)
    {
        if (NVector2.Distance(from, target) <= 0.35f || now < orbMotionWarmUntil)
        {
            orbMotionSampleAt = default;
            return true;
        }
        if (orbMotionSampleAt == default)
        {
            orbMotionSample = from;
            orbMotionSampleAt = now;
            return true;
        }
        double elapsed = (now - orbMotionSampleAt).TotalSeconds;
        if (elapsed < 0.6) return true;
        double speed = NVector2.Distance(from, orbMotionSample) / elapsed;
        orbMotionSample = from;
        orbMotionSampleAt = now;
        if (speed >= remaining.MinimumSpeed && speed <= 9.5) return true;
        // Recheck from the actual position at the measured slower speed. A
        // blocked route, or an unmodeled faster speed, cannot retain input.
        bool safe = remaining.MinimumSpeed <= 4.5 && speed >= 0.5 && speed < 4.5 &&
            HolminsterOrbRoute.Safe(from, remaining, hazards, Math.Max(0.25, speed - 0.2), 0.15);
        ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Orb displacement speed={0:F2} safe={1}.", speed, safe);
        return safe;
    }

    private void ReleaseOrbRoute()
    {
        orbSearchCancel?.Cancel();
        orbSearch = null;
        orbSearchCancel?.Dispose();
        orbSearchCancel = null;
        if ((orbRouteOwned || orbRouteSearching) && bossMover?.IsCurrent == true) bossMover.Request(null, 0);
        if (orbRouteOwned || orbRouteSearching)
        {
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Orb ownership released phase={0} position={1}.", orbSecondStage ? 2 : 1, Core.Me?.Location);
            CapabilityManager.Clear(orbRouteHandle, CapabilityFlags.Movement, "Dissonance orb route released");
            CapabilityManager.Clear(orbRouteHandle, CapabilityFlags.GapCloser, "Dissonance orb route released");
        }
        orbRouteOwned = orbRouteSearching = orbSecondStage = false;
        orbRoute = null;
        // A rejected route does not erase a still-live forecast. Otherwise a
        // second search for the same wave can ignore the predicted charge.
        // Lifecycle exits still clear it so it cannot survive the encounter.
        if (!DissonanceCombat())
        {
            orbForecast = null;
            orbForecastCreated = default;
            orbForecastWave = null;
        }
        orbMotionSampleAt = default;
    }
}
