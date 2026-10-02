using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Navigation;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private readonly CapabilityManagerHandle _rootMovement = CapabilityManager.CreateNewHandle();
    private readonly Queue<Vector3> _rootPath = new();
    private DateTime _rootChaseUntil;
    private bool _rootVisual, _rootRunning, _rootMoving, _rootLeased, _rootPlanFailed;
    private Vector3 _rootCorner;
    private static bool InRightYozakura() => InYozakura() && Core.Me.Location.X is> -500 and < 500;
    private void RegisterRightYozakura()
    {
        ResetRootChase();
        ReleaseRightPetals();
        // Unknown layouts retain first-circle escape instead of trusting an
        // unverified straight path through traps. Normal combat/death recovery
        // remains available; a failed planner never stops the bot.
        AvoidanceManager.AddAvoidLocation<Impact>(() => InRightYozakura() && _rootPlanFailed && !Core.Me.HasAura(3625), _ => 4.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == 33701));
        // Mebuki's seedlings hurt on contact without a long cast. Their own
        // visible actor lifecycle supplies persistent 4y footprints; an enemy
        // display name or one completed cast cannot represent these traps.
        // While imprisoned, leaving this contact circle is impossible. Release
        // it so normal combat can break the targetable gaol instead of endlessly
        // requesting movement against the bind (observed status 3625).
        AvoidanceManager.AddAvoidLocation<GameObject>(() => InRightYozakura() && !Core.Me.HasAura(3625), _ => 4.5f, o => o.Location, () => GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x3EE1 && o.Distance2D(YozakuraRightCenter) < 35));
        // October 2's circles let escape run ahead of the moving wind and take
        // repeated hits. Captured straight travel is 3y/s: reserve its next two
        // seconds plus 0.5y padding so escape chooses a lateral gap early enough.
        // Keep the live actor lifecycle; vanished waves must release their lanes.
        AvoidanceManager.AddAvoidPolygon<GameObject>(() => InRightYozakura() && !_petalsOwned, null, 80, o => -o.Heading, _ => 1, _ => 15, _ => WitherwindFootprint(), o => o.Location, () => GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x3EE3 && o.Distance2D(YozakuraRightCenter) < 35), priority: AvoidancePriority.High);
        // Petals pushes 15y into persistent mud. The reference's 20-degree
        // exclusion is widened to 22 for the padded 5.5y puddle. Read actual
        // visible placements, not a fixed pattern or the helper's central origin.
        AvoidanceManager.AddAvoidPolygon<GameObject>(() => InRightYozakura() && !_petalsOwned && RightPetals() != null, null, 80, o => -(float)Math.Atan2(o.Location.X - RightPetalsOrigin.X, o.Location.Z - RightPetalsOrigin.Z), _ => 1, _ => 15, _ => Sector(44, 25), _ => RightPetalsOrigin, () => GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == MudPuddleBase && RightPetals() != null && o.Distance2D(RightPetalsOrigin) >= 9.5f && o.Distance2D(RightPetalsOrigin) <= 25), priority: AvoidancePriority.High);
    }

    private Impact RightPetals() => PendingImpacts().FirstOrDefault(c => c.Action == DriftingPetals);
    // Native providers may query after the predicate's impact has expired.
    private Vector3 RightPetalsOrigin => RightPetals()?.Location ?? YozakuraRightCenter;

    private static Vector2[] WitherwindFootprint() => Rectangle(3.5f, 3.5f, 9.5f);
    private void ObserveRootChase()
    {
        if (!InRightYozakura() || Core.Me.HasAura(3625))
        {
            ResetRootChase();
            return;
        }

        var now = DateTime.UtcNow;
        var casts = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.Distance2D(YozakuraRightCenter) < 35).ToArray();
        var visual = casts.FirstOrDefault(b => b.CastingSpellId == 33700);
        if (visual != null && !_rootVisual)
        {
            ResetRootChase();
            // Both observed seed layouts leave the east edge clear. Stage at
            // its nearer corner during the five-second warning, then run its
            // length without reversing. Self-following avoidance circles caused
            // zigzags and four hits at 22:57:52–56, despite continuous movement.
            _rootCorner = new Vector3(65.5f, 309, Core.Me.Location.Z < 93 ? 74.5f : 111.5f);
            foreach (var point in RootApproach(Core.Me.Location, _rootCorner))
                _rootPath.Enqueue(point);
            _rootPlanFailed = _rootPath.Count == 0;
            if (_rootPlanFailed)
                ff14bot.Helpers.Logging.Write("[Rokkon] No clear root staging path; retaining ordinary combat and first-circle escape.");
            _rootChaseUntil = now + visual.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(8);
        }

        _rootVisual = visual != null;
        var first = casts.FirstOrDefault(b => b.CastingSpellId == 33701);
        if (first != null)
        {
            if (!_rootRunning)
            {
                _rootRunning = true;
                _rootPath.Clear();
                var other = new Vector3(65.5f, 309, _rootCorner.Z < 93 ? 111.5f : 74.5f);
                // Recompute a safe approach if the warning began mid-movement.
                foreach (var point in RootApproach(Core.Me.Location, other))
                    _rootPath.Enqueue(point);
                // Seven seconds of running can exceed the 37-yalm edge. The
                // short corner extension stays east of the northern seed at X 41.5.
                if (_rootPath.Count > 0)
                {
                    _rootPlanFailed = false;
                    _rootPath.Enqueue(new Vector3(55, 309, other.Z));
                }
                else
                {
                    ResetRootChase();
                    _rootRunning = _rootPlanFailed = true;
                    ff14bot.Helpers.Logging.Write("[Rokkon] No clear root running path; retaining ordinary combat and first-circle escape.");
                }
            }

            _rootChaseUntil = now + first.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(4.6);
        }

        if (now >= _rootChaseUntil)
        {
            ResetRootChase();
            return;
        }

        if (_rootPlanFailed)
            return;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _rootMoving = false;
            return;
        }

        CapabilityManager.Update(_rootMovement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Run the root bait along the clear edge");
        CapabilityManager.Update(_rootMovement, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Preserve the root bait path");
        _rootLeased = true;
        while (_rootPath.Count > 0 && Core.Me.Distance2D(_rootPath.Peek()) < .4f)
            _rootPath.Dequeue();
        if (_rootPath.Count == 0)
        {
            if (_rootMoving)
                Navigator.PlayerMover.MoveStop();
            _rootMoving = false;
            return;
        }

        if (Core.Me.IsCasting && now >= _nextAvoidCastCancel)
        {
            _nextAvoidCastCancel = now.AddMilliseconds(750);
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(_rootPath.Peek());
        _rootMoving = true;
    }

    private void ResetRootChase()
    {
        if (_rootMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        _rootPath.Clear();
        _rootChaseUntil = DateTime.MinValue;
        _rootVisual = _rootRunning = _rootMoving = _rootPlanFailed = false;
        if (_rootLeased)
        {
            CapabilityManager.Clear(_rootMovement, CapabilityFlags.Movement, "Root sequence ended");
            CapabilityManager.Clear(_rootMovement, CapabilityFlags.GapCloser, "Root sequence ended");
        }

        _rootLeased = false;
    }

    private static IEnumerable<Vector3> RootApproach(Vector3 start, Vector3 goal)
    {
        var seeds = GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x3EE1).Select(o => o.Location).ToArray();
        return RootApproach(start, goal, seeds);
    }

    // A small arena-local visibility graph is built only when a new bait starts.
    // Its one-yalm grid and segment checks avoid cutting across a seed while
    // staging; native wrappers never escape the bot-thread snapshot above.
    private static IEnumerable<Vector3> RootApproach(Vector3 start, Vector3 goal, Vector3[] seeds)
    {
        if (seeds.Any(s => start.Distance2D(s) < 4))
            return Array.Empty<Vector3>();
        bool Clear(Vector3 a, Vector3 b) => seeds.All(s =>
        {
            var d = b - a;
            var length = d.X * d.X + d.Z * d.Z;
            var t = length < .001f ? 0 : Math.Max(0, Math.Min(1, ((s.X - a.X) * d.X + (s.Z - a.Z) * d.Z) / length));
            // A new seed can appear inside our padding but outside its actual
            // four-yalm trigger. Permit only outward egress from that initial
            // point; all subsequent vertices retain the full safety margin.
            var minimum = a.Distance2D(start) < .001f ? Math.Min(4.75f, start.Distance2D(s)) : 4.75f;
            return (a + d * t).Distance2D(s) >= minimum - .001f;
        });
        if (Clear(start, goal))
            return new[]
            {
                goal
            };
        var points = new List<Vector3>
        {
            start,
            goal
        };
        for (var x = 0; x <= 37; x++)
            for (var z = 0; z <= 37; z++)
            {
                var p = new Vector3(28.5f + x, 309, 74.5f + z);
                if (Clear(p, p))
                    points.Add(p);
            }

        var previous = Enumerable.Repeat(-1, points.Count).ToArray();
        var cost = Enumerable.Repeat(float.MaxValue, points.Count).ToArray();
        var open = new HashSet<int>
        {
            0
        };
        cost[0] = 0;
        while (open.Count > 0)
        {
            var current = open.OrderBy(i => cost[i] + points[i].Distance2D(goal)).First();
            open.Remove(current);
            if (current == 1)
                break;
            for (var next = 1; next < points.Count; next++)
            {
                var distance = points[current].Distance2D(points[next]);
                if ((next != 1 && distance > 1.5f) || cost[current] + distance >= cost[next] || !Clear(points[current], points[next]))
                    continue;
                previous[next] = current;
                cost[next] = cost[current] + distance;
                open.Add(next);
            }
        }

        if (previous[1] < 0)
            return Array.Empty<Vector3>();
        var path = new List<Vector3>();
        for (var i = 1; i > 0; i = previous[i])
            path.Add(points[i]);
        path.Reverse();
        // Smooth only segments already verified against every seed.
        var result = new List<Vector3>();
        var from = start;
        while (path.Count > 0)
        {
            var last = path.FindLastIndex(p => Clear(from, p));
            if (last < 0)
                return Array.Empty<Vector3>();
            from = path[last];
            result.Add(from);
            path.RemoveRange(0, last + 1);
        }

        return result;
    }

    private static void PrioritizeLivingGaol()
    {
        if (!InRightYozakura() || (AvoidanceManager.IsRunningOutOfAvoid && !Core.Me.HasAura(3625)))
            return;
        var gaol = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.BaseId == 0x3EE2 && b.IsAlive && b.IsTargetable && b.CanAttack && b.Distance2D(YozakuraRightCenter) < 35).OrderBy(b => b.Distance()).FirstOrDefault();
        // A touched seed traps its victim and explodes after 15s. Recover through
        // ordinary combat against the real gaol instead of stopping or continually
        // forcing the game target pointer. The brain clears the dead Kill POI.
        if (gaol != null && Poi.Current?.Unit?.ObjectId != gaol.ObjectId)
            Poi.Current = new Poi(gaol, PoiType.Kill);
    }
}
