using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class HellOnRails
{
    private List<Vector3> externalGroundRoute;
    private Vector3 externalGroundDestination;
    private int externalGroundCar;
    private int externalGroundIndex;

    // Service navigation can aim at Doomtrain's off-car center, then wall-follow a crate
    // away from that destination. Resolve those external steps on known floor before the
    // single input owner forwards them. This does not own a duty route or combat actions.
    private Vector3? ConstrainGroundMove(Vector3 requested)
    {
        if (MovementManager.GenerallyOutOfControl) return null;
        if (!TryGetCurrentCar(out int car) || Core.Me.Y > 1 || Core.Me.Y < -0.2f)
            return requested;

        var start = Core.Me.Location;
        float center = car * 50 + 50;
        var bounded = new Vector3(Math.Clamp(requested.X, 91, 109), start.Y,
            Math.Clamp(requested.Z, center - 14, center + 14));
        if (car is not (2 or 5))
            return start.Distance2D(bounded) > 0.35f ? bounded : null;

        if (HellOnRailsGeometry.ClearSegment(car, start, bounded))
        {
            externalGroundRoute = null;
            return start.Distance2D(bounded) > 0.35f ? bounded : null;
        }
        if (externalGroundRoute == null || car != externalGroundCar ||
            externalGroundDestination.Distance2D(bounded) > 1)
        {
            externalGroundCar = car;
            externalGroundDestination = bounded;
            externalGroundIndex = 0;
            // The nearest usable floor point replaces an off-floor/crate waypoint. Native
            // avoidance still supplies its safe destination; do not replace it with a hazard.
            bool Safe(Vector3 point) => !AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(point));
            externalGroundRoute = HellOnRailsGeometry.FindApproachRoute(car, start, bounded, Safe);
        }
        if (externalGroundRoute == null) return null;
        while (externalGroundIndex < externalGroundRoute.Count && start.Distance2D(externalGroundRoute[externalGroundIndex]) < 0.4f)
            externalGroundIndex++;
        if (externalGroundIndex == externalGroundRoute.Count) return null;
        var step = externalGroundRoute[externalGroundIndex];
        // A displacement or a changed obstacle invalidates the cached leg. Replan next call
        // instead of forwarding a stale segment; stopping input leaves combat and raises live.
        if (!HellOnRailsGeometry.ClearSegment(car, start, step))
        {
            externalGroundRoute = null;
            return null;
        }
        return new Vector3(step.X, start.Y, step.Z);
    }
}
