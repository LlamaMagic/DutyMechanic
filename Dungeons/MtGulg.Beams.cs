using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using System;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class MtGulg
{
    private sealed class BeamRoute
    {
        internal Vector3 Hold, Exit;
        internal DateTime ArriveBy;
    }

    private readonly CapabilityManagerHandle beamHandle = CapabilityManager.CreateNewHandle();
    private BeamRoute beamRoute;
    private bool movingToBeamPosition, beamHoldReached, beamRouteWarningSent, beamPositionRequested;

    private static BeamRoute PlanBeamRoute(BeamLane[] lanes, Vector3 player, DateTime now)
    {
        // Native escape either took orb contact hits or oscillated at a beam
        // boundary. Use fixed lane centers for the six southward 5y columns.
        if (lanes.Length != 6 || lanes.Any(l => Math.Cos(l.Heading - Math.PI) < .999 ||
            l.Origin.Z < 240.8f || l.Origin.Z > 253.2f))
        {
            return null;
        }
        var sorted = lanes.OrderBy(l => l.Origin.X).ToArray();
        if (sorted.Where((l, i) => Math.Abs(l.Origin.X - (-252.5f + 5 * i)) > .2f).Any())
        {
            return null;
        }
        var late = lanes.Where(l => l.Late).OrderBy(l => player.Distance2D(
                    new Vector3(l.Origin.X, ObscenityCenter.Y, ObscenityCenter.Z))).ThenBy(l => l.Id).FirstOrDefault();
        if (late == null)
        {
            return null;
        }
        var neighbor = lanes.Where(l => !l.Late && Math.Abs(l.Origin.X - late.Origin.X) < 5.2f)
                    .OrderBy(l => Math.Abs(l.Origin.X - late.Origin.X)).FirstOrDefault();
        if (neighbor == null)
        {
            return null;
        }
        var hold = new Vector3(late.Origin.X, ObscenityCenter.Y, ObscenityCenter.Z);
        // The first beam fires about 7s after discovery. Reach the hold by 6s,
        // allowing 0.5s beyond travel at normal 6y/s speed; reject late attachment.
        var arriveBy = lanes.Min(l => l.Discovered).AddSeconds(6);
        if (now.AddSeconds(player.Distance2D(hold) / 6 + .5) >= arriveBy)
        {
            return null;
        }
        return new BeamRoute
        {
            Hold = hold,
            Exit = new(neighbor.Origin.X, ObscenityCenter.Y,
                    ObscenityCenter.Z),
            ArriveBy = arriveBy
        };
    }

    private void UpdateBeamPosition()
    {
        if (boss != 3 || solitaireUntil == DateTime.MinValue || movement == null)
        {
            return;
        }
        if (beamRoute == null && beamRouteWarningSent)
        {
            return;
        }        // The parent tell gives about 9s to center before the rings appear.
        var destination = beamRoute == null ? ObscenityCenter :
            lateLanesReleased ? beamRoute.Exit : beamRoute.Hold;
        if (beamRoute != null && !beamHoldReached)
        {
            beamHoldReached = Core.Me.Location.Distance2D(beamRoute.Hold) <= .35f;
            if (!beamHoldReached && DateTime.UtcNow >= beamRoute.ArriveBy)
            {
                ReleaseBeamPosition("initial crossing missed deadline");
                beamRoute = null;
                beamRouteWarningSent = true;
                Logger.Warning("[MtGulg] Beam staging deadline missed; native fallback retained, sequence unverified");
                return;
            }
        }
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleaseBeamPosition("yield to native escape");
            return;
        }
        if (!OrdinarySafe(destination) || !BeamApproachSafe(destination))
        {
            ReleaseBeamPosition("unsafe approach");
            if (beamRoute != null)
            {
                beamRoute = null;
                beamRouteWarningSent = true;
                Logger.Warning("[MtGulg] Beam approach invalidated; native fallback retained, sequence unverified");
            }
            return;
        }
        CapabilityManager.Update(beamHandle, CapabilityFlags.Movement, 1000, "Mt. Gulg verified beam positioning");
        movement.Request(destination, .35f);
        beamPositionRequested = true;
        movingToBeamPosition = !MovementManager.GenerallyOutOfControl && Core.Me.Location.Distance2D(destination) > .35f;
        // Return the routine tick once stationary so healing remains available.
    }

    private bool BeamApproachSafe(Vector3 destination)
    {
        var from = Core.Me.Location;
        float length = from.Distance2D(destination);
        int samples = Math.Max(1, (int)Math.Ceiling(length / .4f));
        var orbs = ForecastOrbs().ToArray();
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            var point = new Vector3(from.X + (destination.X - from.X) * t, from.Y,
                from.Z + (destination.Z - from.Z) * t);
            if (Math.Abs(point.X - ObscenityCenter.X) > 14.5f || Math.Abs(point.Z - ObscenityCenter.Z) > 19.5f ||
                FloorActive && point.Distance2D(ObscenityCenter) > 14.5f)
            {
                return false;
            }            // After staging, only latest or cleared columns may be crossed,
            // including when resuming after native escape preempts the request.
            if (beamRoute != null && DateTime.UtcNow >= beamRoute.ArriveBy &&
                beamLanes.Values.Any(l => !l.Late && Math.Abs(point.X - l.Origin.X) < 3 &&
                    (l.Resolved == DateTime.MinValue || DateTime.UtcNow < l.Resolved.AddMilliseconds(500))))
            {
                return false;
            }            // Contact helpers lead their orb by about 1.2y. Include that lead,
            // segment travel and a 0.5s margin when checking 3.5y clearance.
            float ahead = 1.25f + 3 * (t * length / 6 + .5f);
            if (orbs.Any(orb => point.Distance2D(new Vector3(
                orb.Origin.X + (float)Math.Sin(orb.Heading) * ahead, orb.Origin.Y,
                orb.Origin.Z + (float)Math.Cos(orb.Heading) * ahead)) < 3.5f))
            {
                return false;
            }
        }
        return true;
    }

    private void ReleaseBeamPosition(string reason)
    {
        // Retire our request through the mover; native escape and forced movement
        // retain their input. Only this sequence's capability handle is cleared.
        if (beamPositionRequested)
        {
            Logger.Information("[MtGulg] Beam positioning released reason={0} at={1}", reason, Core.Me?.Location);
            movement?.Request(null, 0);
            CapabilityManager.Clear(beamHandle, CapabilityFlags.Movement, "Mt. Gulg beam positioning released");
        }
        beamPositionRequested = false;
        movingToBeamPosition = false;
    }
}
