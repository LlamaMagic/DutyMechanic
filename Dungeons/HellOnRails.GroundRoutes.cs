using Buddy.Coroutines;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

public partial class HellOnRails
{
    // Hail's preview travels through several spots; its current position is not the impact.
    // Central staging keeps every final pocket reachable during the short helper cast. Once
    // that cast identifies the real circle, use the same collision model as pull recovery.
    private readonly CapabilityManagerHandle groundRouteHandle = CapabilityManager.CreateNewHandle();
    private uint hailIndicatorId;
    private int hailCasts;
    private bool hailCasting;
    private bool hailSequenceActive;
    private Vector3? hailImpact;
    private DateTime hailImpactUntil;
    private List<Vector3> groundRoute;
    private int groundRouteIndex;
    private string groundRouteKey;
    private DateTime nextGroundRouteAttempt;
    private bool groundRouteForHail;
    private List<Vector3> lightningGroundRoute;
    private int lightningGroundRouteIndex;
    private Vector3 lightningGroundTarget;

    private bool OwnsGroundRoute => groundRoute != null;

    private void UpdateGroundRoute()
    {
        var now = DateTime.UtcNow;
        var indicator = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .LastOrDefault(actor => actor.BaseId == EnemyNpc.ArcaneRevelation && actor.IsVisible);
        if (indicator != null && indicator.ObjectId != hailIndicatorId)
        {
            hailIndicatorId = indicator.ObjectId;
            hailCasts = 0;
            hailSequenceActive = true;
        }
        var hail = FindCaster(EnemyAction.HailOfThunder);
        if (hail != null)
        {
            if (!hailCasting) hailCasts++;
            hailSequenceActive = true;
            hailImpact = hail.SpellCastInfo.CastLocation;
            // Keep the destination through the effect after the bar, rather than immediately
            // returning into the circle while the helper delivers damage.
            hailImpactUntil = now + hail.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.8);
        }
        hailCasting = hail != null;
        if (now >= hailImpactUntil)
        {
            hailImpact = null;
            if (hailCasts >= 2 || indicator == null) hailSequenceActive = false;
        }

        // Planning must continue while casting. RB includes that occupied state in
        // GenerallyOutOfControl; waiting for it lost half of Hail's 2.9-second warning.
        // The movement owner still rejects physical locks when it executes the route.
        if (!TryGetCurrentCar(out int car) || car is not (2 or 4 or 5) || OwnsPullRecovery ||
            now < lightningHoldUntil || now < windpipeHoldUntil ||
            (now < floorHoldUntil && (floorHoldUpper || IsUpperDeck(Core.Me))))
        {
            ReleaseGroundRoute();
            return;
        }

        bool hailActive = hailSequenceActive && car is 4 or 5;
        var beams = turretBeams.Values.Where(beam => beam.Origin.Y < UpperDeckHeightThreshold).ToArray();
        // The last car has the same short turret response window. Native escape walked off
        // its east edge after a Hail pocket; keep these paths inside the verified ground grid.
        // Headlight permits ground travel while it resolves, so only an upper-floor hold or
        // an unfinished descent may defer the route above.
        if (!hailActive && (car is not (2 or 5) || beams.Length == 0))
        {
            ReleaseGroundRoute();
            return;
        }

        Vector3 center = new(100, 0, car * 50 + 50);
        bool Safe(Vector3 point) => hailActive
            ? hailImpact is { } impact ? point.Distance2D(impact) >= 17 : point.Distance2D(center) <= 0.5f
            : beams.All(beam => !beam.Contains(point));
        string key = hailActive ? $"hail:{hailIndicatorId}:{hailCasts}:{hailImpact.HasValue}"
            : "turrets:" + string.Join(",", turretBeams.Keys.OrderBy(id => id));
        // Arrival tolerance extends a destination slightly; use that same tolerance when
        // deciding to replan, otherwise central staging churns the capability every pulse.
        bool settledSafe = hailActive
            ? hailImpact is { } resolvedImpact
                ? Core.Me.Distance2D(resolvedImpact) >= 16.6f
                : Core.Me.Distance2D(center) <= 0.9f
            : Safe(Core.Me.Location);
        if (key != groundRouteKey || (groundRoute != null && groundRouteIndex >= groundRoute.Count && !settledSafe))
        {
            ReleaseGroundRoute();
            groundRouteKey = key;
            nextGroundRouteAttempt = DateTime.MinValue;
        }
        groundRouteForHail = hailActive;
        // Upper Hail staging first uses the verified inner-edge descent. No planar route may
        // pretend the top of a platform is ground or walk through its footprint.
        if (IsUpperDeck(Core.Me))
        {
            groundRoute = hailActive ? [] : null;
            return;
        }
        if (groundRoute == null && now >= nextGroundRouteAttempt)
        {
            nextGroundRouteAttempt = now.AddMilliseconds(500);
            groundRoute = HellOnRailsGeometry.FindGroundRoute(car, Core.Me.Location, Safe);
            groundRouteIndex = 0;
            // A missing route leaves the native hazard enabled; never suppress avoidance on
            // the strength of a desired destination that the collision model cannot reach.
        }
    }

    private void InterruptGroundRouteCast()
    {
        // Hail and the post-Express turrets both hit while the routine finished a cast
        // after a safe route was ready. Interrupt only a reachable escape from an actual
        // hazard, never neutral staging. Physical movement still passes through our owner.
        bool unsafeHere = groundRouteForHail
            ? hailImpact is { } impact && Core.Me.Distance2D(impact) < 16.6f
            : turretBeams.Values.Any(beam => beam.Origin.Y < UpperDeckHeightThreshold && beam.Contains(Core.Me.Location));
        if (OwnsGroundRoute && unsafeHere)
            movement?.InterruptCastForEscape();
    }

    private async Task<bool> HandleGroundRouteAsync()
    {
        if (!AdvanceGroundRoute()) return false;
        await Coroutine.Yield();
        return true;
    }

    // Short ground escapes cannot wait for an action coroutine to return: the October 29
    // capture lost 0.7 seconds on Hail and 1.5 seconds after Express while casting Vercure.
    // The bot pulse and encounter hook advance the same route through the same mover;
    // neither creates an independent destination or bypasses physical movement locks.
    private bool AdvanceGroundRoute()
    {
        if (!OwnsGroundRoute || movement == null || !movement.IsCurrent) return false;
        CapabilityManager.Update(groundRouteHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1),
            groundRouteForHail ? "Positioning for Hail of Thunder" : "Escaping train-car turrets");
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return false;
        if (IsUpperDeck(Core.Me))
        {
            movement.Move(HellOnRailsMovement.Request.GroundRoute, GetPlatformDescent());
            return true;
        }
        while (groundRouteIndex < groundRoute.Count && Core.Me.Distance2D(groundRoute[groundRouteIndex]) < 0.4f)
            groundRouteIndex++;
        if (groundRouteIndex == groundRoute.Count)
        {
            movement.Release(HellOnRailsMovement.Request.GroundRoute);
            return false;
        }
        movement.Move(HellOnRailsMovement.Request.GroundRoute, WithPlayerHeight(groundRoute[groundRouteIndex]));
        return true;
    }

    private void ReleaseGroundRoute()
    {
        groundRoute = null;
        groundRouteIndex = 0;
        groundRouteKey = null;
        CapabilityManager.Clear(groundRouteHandle, CapabilityFlags.Movement, "Ground mechanic route released");
        movement?.Release(HellOnRailsMovement.Request.GroundRoute);
    }

    private void ResetGroundMechanics()
    {
        ReleaseGroundRoute();
        hailIndicatorId = 0;
        hailCasts = 0;
        hailCasting = hailSequenceActive = false;
        hailImpact = null;
        hailImpactUntil = DateTime.MinValue;
    }

    private Vector3? CrateStagingWaypoint(Vector3 target)
    {
        // An inner signal lane is useful only if we can reach it around the first crate.
        // Cache corners for the current lane so recomputing nearest grid cells cannot jitter.
        if (lightningGroundRoute == null || target.Distance2D(lightningGroundTarget) > 0.1f)
        {
            lightningGroundTarget = target;
            lightningGroundRoute = HellOnRailsGeometry.FindGroundRoute(2, Core.Me.Location,
                point => point.Distance2D(target) <= 0.6f);
            lightningGroundRouteIndex = 0;
        }
        if (lightningGroundRoute == null) return null;
        while (lightningGroundRouteIndex < lightningGroundRoute.Count &&
            Core.Me.Distance2D(lightningGroundRoute[lightningGroundRouteIndex]) < 0.4f)
            lightningGroundRouteIndex++;
        return lightningGroundRouteIndex < lightningGroundRoute.Count
            ? lightningGroundRoute[lightningGroundRouteIndex] : target;
    }
}
