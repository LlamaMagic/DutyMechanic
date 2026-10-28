using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot.Objects;
using System;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Forecasts the gunship's instantaneous fire trail from its observed preparation pose.
/// Only scalar geometry is published; native avoidance retains all route/input ownership.
/// </summary>
public partial class KeeperOfTheLake
{
    private Hazard fireTrail;
    private Vector3? trailSample, trailPause;
    private float trailPauseHeading;
    private DateTime trailSampleAt, trailArmedUntil, trailPauseAt;

    private void ResetFireTrail()
    {
        fireTrail = null;
        trailSample = trailPause = null;
        trailSampleAt = trailArmedUntil = trailPauseAt = DateTime.MinValue;
    }

    private void UpdateFireTrail(BattleCharacter boss, DateTime now)
    {
        if (boss == null || !boss.IsValid || !boss.IsAlive || Casting(boss))
        {
            ResetFireTrail();
            return;
        }

        // The October 28 follow-up pass hid the living gunship for1.6s after the
        // warning was acquired, before its first damaging pool became visible.
        // Visibility loss is not a pass-end signal: retain that fixed warning with
        // its original9s expiry. Do not acquire from hidden poses or bridge missing
        // samples into new movement history; a visible turn still releases normally.
        if (!boss.IsVisible)
        {
            trailSample = trailPause = null;
            trailSampleAt = trailArmedUntil = trailPauseAt = DateTime.MinValue;
            if (fireTrail != null && now >= fireTrail.Expires) ResetFireTrail();
            return;
        }

        var position = boss.Location;
        float radius = position.Distance2D(GunshipCenter);
        // October28 four-pass capture: scripted outward travel is about9y/s, then a
        // stationary17y edge pose faces the coming trail for about3s. Require that
        // history, not merely an actor standing near a wall or ordinary center combat.
        // A100ms sampling floor accommodates both lifecycle and coroutine pulses.
        double elapsed = (now - trailSampleAt).TotalSeconds;
        if (trailSample == null || elapsed >= .1)
        {
            if (trailSample != null && elapsed <= .75 && radius >= 12 &&
                radius - trailSample.Value.Distance2D(GunshipCenter) >= .2f &&
                position.Distance2D(trailSample.Value) / elapsed >= 6.5)
                trailArmedUntil = now.AddSeconds(5);
            trailSample = position;
            trailSampleAt = now;
        }

        if (fireTrail != null)
        {
            float dx = position.X - fireTrail.Origin.X, dz = position.Z - fireTrail.Origin.Z;
            double progress = dx * Math.Sin(fireTrail.Heading) + dz * Math.Cos(fireTrail.Heading);
            double lateral = dx * Math.Cos(fireTrail.Heading) - dz * Math.Sin(fireTrail.Heading);
            // A real pass travels over10y before turning at its endpoint. Release
            // that lane on the turn instead of unioning it with the random follow-up
            // chord; existing visible-pool circles still protect recent damaging floor.
            bool ended = progress > 10 && (Math.Cos(boss.Heading - fireTrail.Heading) < .96 || Math.Abs(lateral) > 1);
            if (!ended && now < fireTrail.Expires) return;
            Logger.Information("[Keeper] Fire trail forecast released at " + position + ".");
            fireTrail = null;
            trailPause = null;
        }

        double inward = radius > 0 ? ((GunshipCenter.X - position.X) * Math.Sin(boss.Heading) +
            (GunshipCenter.Z - position.Z) * Math.Cos(boss.Heading)) / radius : 0;
        // Captured cardinal and diagonal endpoints are~17y from center. The inward
        // dot allows the observed diagonal follow-up chord without assuming a diameter.
        if (now >= trailArmedUntil || radius < 15.5f || radius > 18.5f || inward < .5)
        {
            trailPause = null;
            return;
        }
        if (trailPause == null || position.Distance2D(trailPause.Value) > .25f ||
            Math.Cos(boss.Heading - trailPauseHeading) < Math.Cos(.04))
        {
            trailPause = position;
            trailPauseHeading = boss.Heading;
            trailPauseAt = now;
            return;
        }
        if ((now - trailPauseAt).TotalSeconds < .5) return;

        //8y damage pools plus0.5y clearance form a17y lane. Extend8.5y behind the
        // start to include the first pool's rear edge;47y forward covers the38y arena
        // diameter plus its last pool. Arena bounds clip off-platform portions.
        // Fixed pose prevents moving-actor attraction. Nine seconds bounds missing
        // turn observations (captured preparation+crossing takes less than8s).
        Vector2[] points = [new(-8.5f, -8.5f), new(8.5f, -8.5f), new(8.5f, 47), new(-8.5f, 47)];
        fireTrail = new(boss.ObjectId, ActionId.Pool, trailPause.Value, trailPauseHeading,
            points, now.AddSeconds(2.5), now.AddSeconds(9));
        trailPause = null;
        trailArmedUntil = DateTime.MinValue;
        Logger.Information("[Keeper] Fire trail forecast acquired origin={0} heading={1}.", fireTrail.Origin, fireTrail.Heading);
    }
}
