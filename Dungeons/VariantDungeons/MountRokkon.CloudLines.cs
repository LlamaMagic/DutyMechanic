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
    private readonly CapabilityManagerHandle _cloudMovement = CapabilityManager.CreateNewHandle();
    private DateTime _cloudPlanAt, _cloudDiagnosticAt;
    private Vector3 _cloudTarget, _cloudGoal;
    private bool _cloudOwned, _cloudMoving;
    private IEnumerable<Impact> CloudLines() => PendingImpacts().Concat(_retainedCloudLines).Where(c => (c.Action is 33763 or 33764 or 33765) && c.Length > 0 && c.End > DateTime.UtcNow);
    // Static rectangles failed twice: the next angled wave drove escape across
    // an unresolved earlier line. One timed owner handles their approach and
    // impact windows together, while unrelated avoidance retains priority.
    private void ObserveCloudLines()
    {
        if (!InShishio())
        {
            ReleaseCloudLines();
            return;
        }

        var lines = CloudLines().ToArray();
        if (lines.Length == 0)
        {
            ReleaseCloudLines();
            return;
        }

        var now = DateTime.UtcNow;
        if (now >= _cloudPlanAt)
        {
            _cloudPlanAt = now.AddMilliseconds(150);
            var snapshot = lines.Select(c => (origin: c.Location, heading: c.Heading, width: c.Action == 33763 ? 1.5f : c.Action == 33764 ? 3.5f : 6.5f, end: (c.End - now).TotalSeconds)).ToArray();
            var path = PlanCloudLines(Core.Me.Location, _cloudOwned ? _cloudGoal : Core.Me.Location, snapshot);
            if (path.Length == 0)
            {
                if (now >= _cloudDiagnosticAt)
                {
                    _cloudDiagnosticAt = now.AddSeconds(5);
                    ff14bot.Helpers.Logging.Write("[Rokkon] No timed cloud-line path; retaining ordinary escape while the next cast updates the plan.");
                }

                ReleaseCloudLines(false);
                return;
            }

            _cloudTarget = path[0];
            _cloudGoal = path[path.Length - 1];
            _cloudOwned = true;
        }

        if (!_cloudOwned || AvoidanceManager.IsRunningOutOfAvoid)
            return;
        CapabilityManager.Update(_cloudMovement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Respect the cloud-line impact order");
        CapabilityManager.Update(_cloudMovement, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Preserve the timed cloud escape");
        if (Core.Me.Distance2D(_cloudTarget) < .15f)
        {
            if (_cloudMoving)
                Navigator.PlayerMover.MoveStop();
            _cloudMoving = false;
            return; // A safe hold still allows normal routine healing and attacks.
        }

        if (Core.Me.IsCasting && now >= _nextAvoidCastCancel)
        {
            _nextAvoidCastCancel = now.AddMilliseconds(750);
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(_cloudTarget);
        _cloudMoving = true;
    }

    private void ReleaseCloudLines(bool resetClock = true)
    {
        if (_cloudMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        if (_cloudOwned)
        {
            CapabilityManager.Clear(_cloudMovement, CapabilityFlags.Movement, "Cloud-line owner released");
            CapabilityManager.Clear(_cloudMovement, CapabilityFlags.GapCloser, "Cloud-line owner released");
        }

        _cloudOwned = _cloudMoving = false;
        if (resetClock)
            _cloudPlanAt = DateTime.MinValue;
    }

    // A small space/time graph permits crossing a future line before its cast
    // finishes or waiting for an earlier impact to expire. Flattening these
    // intervals into one static union incorrectly removes the usable floor.
    // Only detached geometry enters this solver; no native wrappers are retained.
    private static Vector3[] PlanCloudLines(Vector3 start, Vector3 preferred, (Vector3 origin, float heading, float width, double end)[] lines)
    {
        const double quantum = .15, speed = 5.5;
        var center = new Vector3(-40, 360, -300);
        //35 ticks cover 5.25s: the longest four-second native cast plus its 1.1s fence.
        var horizon = Math.Min(35, (int)Math.Ceiling(lines.Max(l => l.end) / quantum) + 1);
        var initialRadius = Math.Max(19.25f, start.Distance2D(center));
        bool Safe(Vector3 point, double at, bool initial = false)
        {
            if (point.Distance2D(center) > (initial ? initialRadius : 19.25f) + .001f)
                return false;
            foreach (var line in lines)
            {
                // End includes the measured 1.1s impact fence. Begin checking
                //0.2s before native cast completion to cover movement reporting.
                if (at < line.end - 1.3 || at > line.end)
                    continue;
                var d = point - line.origin;
                var f = Forward(line.heading);
                var along = d.X * f.X + d.Z * f.Z;
                var side = Math.Abs(d.X * f.Z - d.Z * f.X);
                if (along >= -.5 && along <= 100.5 && side < line.width)
                    return false;
            }

            return true;
        }

        bool Segment(Vector3 a, Vector3 b, double at, double duration, bool initial)
        {
            var travel = a.Distance2D(b) / speed;
            for (double t = 0; t <= duration + .024; t += .025)
            {
                var time = Math.Min(t, duration);
                var movingFor = Math.Max(0, time - (initial ? .15 : 0));
                var p = travel < .001 ? b : a + (b - a) * (float)Math.Min(1, movingFor / travel);
                if (!Safe(p, at + time, initial))
                    return false;
            }

            return true;
        }

        bool Hold(Vector3 p, double from)
        {
            if (p.Distance2D(center) > 19.25f)
                return false;
            for (var t = from; t <= horizon * quantum; t += .025)
                if (!Safe(p, t))
                    return false;
            return true;
        }

        if (Hold(start, 0))
            return new[]
            {
                start
            };
        var directTime = start.Distance2D(preferred) / speed + .15;
        if (directTime <= horizon * quantum && Hold(preferred, directTime) && Segment(start, preferred, 0, directTime, true))
            return new[]
            {
                preferred
            };
        var points = new List<Vector3>
        {
            start
        };
        var grid = new int[31, 31];
        for (var x = 0; x < 31; x++)
            for (var z = 0; z < 31; z++)
            {
                var p = new Vector3(-58.75f + x * 1.25f, 360, -318.75f + z * 1.25f);
                if (p.Distance2D(center) > 19.25f)
                    continue;
                grid[x, z] = points.Count;
                points.Add(p);
            }

        var neighbors = Enumerable.Range(0, points.Count).Select(_ => new List<int>()).ToArray();
        for (var i = 1; i < points.Count; i++)
            if (points[i].Distance2D(start) <= 1.9f)
                neighbors[0].Add(i);
        for (var x = 0; x < 31; x++)
            for (var z = 0; z < 31; z++)
            {
                var id = grid[x, z];
                if (id == 0)
                    continue;
                for (var dx = -1; dx <= 1; dx++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        var nx = x + dx;
                        var nz = z + dz;
                        if (nx is < 0 or > 30 || nz is < 0 or > 30 || grid[nx, nz] == 0)
                            continue;
                        neighbors[id].Add(grid[nx, nz]); // Includes a one-quantum wait.
                    }
            }

        neighbors[0].Add(0);
        var size = points.Count;
        var previous = Enumerable.Repeat(-1, size * (horizon + 1)).ToArray();
        var queue = new PriorityQueue<int, double>();
        previous[0] = 0;
        queue.Enqueue(0, 0);
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            var id = state % size;
            var tick = state / size;
            if (Hold(points[id], tick * quantum))
            {
                var path = new List<Vector3>();
                for (var p = state; p != 0; p = previous[p])
                    path.Add(points[p % size]);
                path.Reverse();
                return path.Count == 0 ? new[]
                {
                    start
                }

                : path.ToArray();
            }

            foreach (var next in neighbors[id])
            {
                var steps = Math.Max(1, (int)Math.Ceiling(points[id].Distance2D(points[next]) / speed / quantum));
                if (id == 0 && next != 0)
                    steps++; // First movement includes startup/cast cancellation.
                var arrival = tick + steps;
                if (arrival > horizon)
                    continue;
                var nextState = arrival * size + next;
                if (previous[nextState] >= 0 || !Segment(points[id], points[next], tick * quantum, steps * quantum, id == 0))
                    continue;
                previous[nextState] = state;
                // Prefer the previous destination when equal-time paths exist,
                // without trading away earlier arrival or crossing a live line.
                queue.Enqueue(nextState, arrival + points[next].Distance2D(preferred) * .001);
            }
        }

        return Array.Empty<Vector3>();
    }
}
