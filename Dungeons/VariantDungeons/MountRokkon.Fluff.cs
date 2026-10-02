using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private readonly CapabilityManagerHandle _fluffFacing = CapabilityManager.CreateNewHandle();
    private bool _fluffLeased;
    private bool _fluffMoving;
    private Impact _fluffBait;
    private Vector3? _fluffDestination;
    private Impact[] FluffWave()
    {
        var pending = PendingImpacts().Where(c => c.Action is 33693 or 33694).ToArray();
        if (pending.Length == 0)
            return Array.Empty<Impact>();
        var first = pending.Min(c => c.End);
        return pending.Where(c => c.End <= first.AddMilliseconds(350)).ToArray();
    }

    private bool FluffFacingWindow() => InYozakura() && FluffWave().Any(c => c.End <= DateTime.UtcNow.AddSeconds(3.2));
    private void RegisterFluff()
    {
        ReleaseFluff();
        // October 1 19:06: east-side gaze followed a bait circle by less than a
        // second. Generic circle escape faced the dogs and caused Seduced 3624.
        // Gaze resolves first: use the shared outward heading during its bait,
        // then release retained circles to RB avoidance. One overlap owner controls
        // that short movement; unrelated geometry and routine healing remain available.
        AvoidanceManager.AddAvoidLocation<Impact>(InYozakura, _ => 6.5f, c => c.Location, () => FluffFacingWindow() ? Array.Empty<Impact>() : PendingImpacts().Where(c => c.Action == 33696));
    }

    private void HandleFluffFacing()
    {
        if (!FluffFacingWindow())
        {
            ReleaseFluff();
            return;
        }

        // Geometry outside this overlap retains priority. We never cancel an
        // unrelated active escape to force a gaze hold.
        if (AvoidanceManager.IsRunningOutOfAvoid)
            return;
        var wave = FluffWave();
        var player = Core.Me.Location;
        var origin = new Clio.Utilities.Vector3(wave.Average(c => c.Location.X), player.Y, wave.Average(c => c.Location.Z));
        var away = player - origin;
        if (Math.Abs(away.X) + Math.Abs(away.Z) < .1f)
        {
            ReleaseFluff();
            return;
        }

        // RB 923 accepts one concrete capability per call, despite the flags enum.
        // A combined value threw during the first live gaze and hid circle release.
        CapabilityManager.Update(_fluffFacing, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Facing away until the dog gaze resolves before its bait circle");
        CapabilityManager.Update(_fluffFacing, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Facing away until the dog gaze resolves before its bait circle");
        if (!_fluffLeased)
        {
            // Acquire only normal movement, after confirming no avoidance owns it.
            Navigator.PlayerMover.MoveStop();
            ff14bot.Helpers.Logging.Write("[Rokkon] Holding gaze facing before Fireblossom Flare.");
        }

        _fluffLeased = true;
        // October 1 19:53/54: holding still avoided both gazes, but the first flare
        // hit twice before generic escape resumed. Move away from the dogs as soon
        // as their overlapping bait is placed: one heading can satisfy both. This
        // narrowly scoped movement is justified by the repeated handoff failure;
        // other mechanics continue through ordinary avoidance and combat scheduling.
        var bait = PendingImpacts().FirstOrDefault(c => c.Action == 33696);
        if (bait != null && !ReferenceEquals(_fluffBait, bait))
        {
            _fluffBait = bait;
            _fluffDestination = null;
            if (player.Distance2D(bait.Location) < 6.75f)
            {
                var heading = Math.Atan2(away.X, away.Z);
                var candidates = Enumerable.Range(-8, 17).Select(i =>
                {
                    var angle = heading + i * Math.PI / 18;
                    return player + new Vector3((float)Math.Sin(angle) * 8, 0, (float)Math.Cos(angle) * 8);
                }).Where(p => Math.Abs(p.X - YozakuraCenter.X) < 18.5f && Math.Abs(p.Z - YozakuraCenter.Z) < 18.5f && p.Distance2D(bait.Location) >= 7 && wave.All(g =>
                {
                    var movement = p - player;
                    var towardDog = g.Location - player;
                    // Every dog must be behind the travel heading, with a small
                    // angular margin. Checking only their average misses a side dog.
                    return movement.X * towardDog.X + movement.Z * towardDog.Z < -.1f * 8 * player.Distance2D(g.Location);
                })).OrderBy(p => p.Distance2D(player + away / player.Distance2D(origin) * 8)).ToArray();
                if (candidates.Length > 0)
                    _fluffDestination = candidates[0];
                else
                    ff14bot.Helpers.Logging.Write("[Rokkon] No shared gaze/flare travel heading; retaining gaze until ordinary avoidance resumes.");
            }
        }

        if (_fluffDestination is { } destination && player.Distance2D(destination) > .5f)
        {
            if (Core.Me.IsCasting && DateTime.UtcNow >= _nextAvoidCastCancel)
            {
                _nextAvoidCastCancel = DateTime.UtcNow.AddMilliseconds(750);
                ActionManager.StopCasting();
            }

            Navigator.PlayerMover.MoveTowards(destination);
            _fluffMoving = true;
            var travel = destination - player;
            Core.Me.SetFacing((float)Math.Atan2(travel.X, travel.Z));
            return;
        }

        if (_fluffMoving)
        {
            Navigator.PlayerMover.MoveStop();
            _fluffMoving = false;
        }

        Core.Me.SetFacing((float)Math.Atan2(away.X, away.Z));
    }

    private void ReleaseFluff()
    {
        if (_fluffMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        _fluffMoving = false;
        _fluffBait = null;
        _fluffDestination = null;
        if (_fluffLeased)
        {
            CapabilityManager.Clear(_fluffFacing, CapabilityFlags.Facing, "Dog gaze ended; facing resumes");
            CapabilityManager.Clear(_fluffFacing, CapabilityFlags.Movement, "Dog gaze ended; movement resumes");
        }

        _fluffLeased = false;
    }
}
