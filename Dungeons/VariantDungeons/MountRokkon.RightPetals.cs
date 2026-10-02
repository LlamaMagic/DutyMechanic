using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private readonly CapabilityManagerHandle _petalsMovement = CapabilityManager.CreateNewHandle();
    private readonly Queue<Vector3> _petalsPath = new();
    private Impact _petalsCast, _petalsLanded;
    private DateTime _petalsPlanAt;
    private bool _petalsOwned, _petalsMoving, _petalsStaged, _petalsFailureReported;
    // Petals, persistent mud and moving wind are one positioning problem. This
    // owner checks the approach, the wait for knockback, and the landing together;
    // independent center/mud/wind avoids cannot express that positive destination.
    private void ObserveRightPetals()
    {
        var cast = InRightYozakura() ? RightPetals() : null;
        if (cast == null || ReferenceEquals(cast, _petalsLanded))
        {
            ReleaseRightPetals();
            return;
        }

        var now = DateTime.UtcNow;
        if (!ReferenceEquals(cast, _petalsCast))
        {
            ReleaseRightPetals();
            _petalsCast = cast;
            _petalsFailureReported = false;
        }

        if (_petalsStaged && now >= cast.End.AddMilliseconds(-1600) && Core.Me.Distance2D(cast.Location) > 7)
        {
            // Displacement after the native cast ends identifies knockback;
            // an earlier unrelated escape must not consume this mechanic. Release
            // promptly so ordinary wind escape can handle the landing wave.
            _petalsLanded = cast;
            ReleaseRightPetals();
            return;
        }

        if (now >= _petalsPlanAt)
        {
            _petalsPlanAt = now.AddMilliseconds(300);
            var mud = GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == MudPuddleBase).Select(o => o.Location).ToArray();
            var wind = GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x3EE3 && o.Distance2D(YozakuraRightCenter) < 35).Select(o => (point: o.Location, heading: o.Heading)).ToArray();
            var path = PlanRightPetals(Core.Me.Location, cast.Location, Math.Max(0, (cast.End - now).TotalSeconds - .5), mud, wind);
            _petalsPath.Clear();
            foreach (var point in path)
                _petalsPath.Enqueue(point);
            if (_petalsPath.Count == 0)
            {
                // No verified plan means ordinary escape remains available.
                // Never force an unchecked shortcut or turn this into a bot stop.
                if (!_petalsFailureReported)
                {
                    ff14bot.Helpers.Logging.Write("[Rokkon] No verified Petals approach and landing; retaining ordinary escape and retrying the local plan.");
                    _petalsFailureReported = true;
                }

                ReleaseRightPetals(false);
                return;
            }

            _petalsOwned = true;
        }

        if (!_petalsOwned || AvoidanceManager.IsRunningOutOfAvoid)
            return;
        CapabilityManager.Update(_petalsMovement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Stage for Petals through wind and mud");
        CapabilityManager.Update(_petalsMovement, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Preserve the safe knockback landing");
        while (_petalsPath.Count > 1 && Core.Me.Distance2D(_petalsPath.Peek()) < .4f)
            _petalsPath.Dequeue();
        if (_petalsPath.Count == 0 || Core.Me.Distance2D(_petalsPath.Peek()) < .3f)
        {
            _petalsStaged = true;
            if (_petalsMoving)
                Navigator.PlayerMover.MoveStop();
            _petalsMoving = false;
            return; // Keep the position lease while normal combat/healing runs.
        }

        if (Core.Me.IsCasting && now >= _nextAvoidCastCancel)
        {
            _nextAvoidCastCancel = now.AddMilliseconds(750);
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(_petalsPath.Peek());
        _petalsMoving = true;
    }

    private void ReleaseRightPetals(bool resetCast = true)
    {
        if (_petalsMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        if (_petalsOwned)
        {
            CapabilityManager.Clear(_petalsMovement, CapabilityFlags.Movement, "Petals position owner released");
            CapabilityManager.Clear(_petalsMovement, CapabilityFlags.GapCloser, "Petals position owner released");
        }

        _petalsOwned = _petalsMoving = false;
        _petalsPath.Clear();
        if (resetCast)
        {
            _petalsCast = null;
            _petalsStaged = false;
            _petalsPlanAt = DateTime.MinValue;
        }
    }

    // Managed snapshots only. The small arena grid resolves short bends around
    // puddles, with time sampled along each segment against observed 3y/s wind.
    // No forecast invents future actors: newly appearing wind triggers replanning.
    private static Vector3[] PlanRightPetals(Vector3 start, Vector3 origin, double impactIn, Vector3[] mud, (Vector3 point, float heading)[] wind)
    {
        const float speed = 5.5f;
        bool Safe(Vector3 p, double at, bool egress = false)
        {
            if (Math.Abs(p.X - 47) > 19.25f || Math.Abs(p.Z - 93) > 19.25f)
                return false;
            if (mud.Any(m => p.Distance2D(m) < (egress ? Math.Min(5.5f, start.Distance2D(m)) : 5.5f) - .01f))
                return false;
            foreach (var w in wind)
            {
                var future = w.point + Forward(w.heading) * (float)(3 * at);
                if (Math.Abs(future.X - 47) > 24 || Math.Abs(future.Z - 93) > 24)
                    continue;
                var minimum = egress ? Math.Min(3.5f, start.Distance2D(w.point)) : 3.5f;
                if (p.Distance2D(future) < minimum - .01f)
                    return false;
            }

            return true;
        }

        bool Segment(Vector3 a, Vector3 b, double at, bool egress)
        {
            var duration = a.Distance2D(b) / speed;
            for (var t = 0d; t <= duration + .049; t += .05)
            {
                var fraction = duration < .001 ? 1 : Math.Min(1, t / duration);
                if (!Safe(a + (b - a) * (float)fraction, at + Math.Min(t, duration), egress))
                    return false;
            }

            return true;
        }

        bool Goal(Vector3 p, double arrival)
        {
            var distance = p.Distance2D(origin);
            if (distance < 1.25f || distance > 4.25f || arrival > impactIn)
                return false;
            for (var t = arrival; t <= impactIn + .05; t += .1)
                if (!Safe(p, t))
                    return false;
            var landing = p + (p - origin) * (15f / distance);
            return Safe(landing, impactIn) && Safe(landing, impactIn + .3);
        }

        if (Goal(start, 0))
            return new[]
            {
                start
            };
        var points = new List<Vector3>
        {
            start
        };
        for (var x = 0; x < 25; x++)
            for (var z = 0; z < 25; z++)
                points.Add(new Vector3(29 + x * 1.5f, 309, 75 + z * 1.5f));
        var costs = Enumerable.Repeat(double.MaxValue, points.Count).ToArray();
        var previous = Enumerable.Repeat(-1, points.Count).ToArray();
        var open = new HashSet<int>
        {
            0
        };
        costs[0] = .15; // Observed movement startup allowance.
        while (open.Count > 0)
        {
            var current = open.OrderBy(i => costs[i] + Math.Max(0, points[i].Distance2D(origin) - 4.25) / speed).First();
            open.Remove(current);
            if (current != 0 && Goal(points[current], costs[current]))
            {
                var path = new List<Vector3>();
                for (var i = current; i > 0; i = previous[i])
                    path.Add(points[i]);
                path.Reverse();
                return path.ToArray();
            }

            for (var next = 1; next < points.Count; next++)
            {
                var distance = points[current].Distance2D(points[next]);
                if (distance > 2.2f)
                    continue;
                var arrival = costs[current] + distance / speed;
                if (arrival > impactIn || arrival >= costs[next] || !Segment(points[current], points[next], costs[current], current == 0))
                    continue;
                costs[next] = arrival;
                previous[next] = current;
                open.Add(next);
            }
        }

        return Array.Empty<Vector3>();
    }
}
