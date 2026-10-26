using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Publishes normal Hades hazards to RB's single avoidance owner. Plugin pulses are
/// required because AutoParty retains its coroutine and does not revisit TreeStart.
/// This class never moves, targets, casts, or ticks a manager itself.
/// </summary>
public static class DyingGaspMechanics
{
    private sealed class Shape
    {
        internal Vector3 Origin;
        internal Vector2[] Points;
        internal DateTime Until;
    }

    private static readonly Dictionary<(uint, uint), Shape> Shapes = new();
    private static readonly HashSet<(uint, uint)> Reported = new();
    private static readonly List<AvoidInfo> Avoids = new();
    private static readonly HashSet<uint> Overrides = new();
    private static DateTime captivityUntil;
    private static readonly Vector3 Center = new(100, 0, 100);
    private static readonly uint[] OwnedActions =
    {
        16715,
        16716,
        16726,
        16727,
        16732,
        16739,
        16740,
        16751,
        16753,
        16754
    };
    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance && WorldManager.ZoneId == 847 && Core.Me != null && Core.Me.IsValid;
    private static bool Active() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private static BattleCharacter[] Actors() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.NpcId == 8352).ToArray();
    /// <summary>Updates scalar hazard snapshots on the existing plugin bot-thread pulse.</summary>
    public static void Pulse()
    {
        if (!InDuty())
        {
            Reset();
            return;
        }

        if (Avoids.Count == 0)
        {
            Register();
        }

        if (!Active())
        {
            Shapes.Clear();
            Reported.Clear();
            captivityUntil = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var key in Shapes.Where(p => p.Value.Until < now).Select(p => p.Key).ToArray())
        {
            Shapes.Remove(key);
            Reported.Remove(key);
        }

        foreach (var actor in Actors().Where(a => a.IsCasting))
        {
            uint action = actor.CastingSpellId;
            if (action == 16744)
            {
                // Captivity's five-second cast precedes the eight-yalm prison snapshot
                // by another five seconds (14:04:36 -> 14:04:46 capture). Keep separation
                // through that snapshot, not merely until the cast bar disappears.
                captivityUntil = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(6);
            }

            if (!OwnedActions.Contains(action))
            {
                continue;
            }

            var key = (actor.ObjectId, action);
            var shape = CreateShape(actor, action);
            if (shape == null)
            {
                if (Reported.Add(key))
                {
                    Logger.Warning("[DyingGasp] No valid sweep matrix action={0}; sweep direction unavailable.", action);
                }

                continue;
            }

            // Echo damage was observed up to 1.8s after its cast bar; retain its
            // snapshot for two seconds so CR movement cannot step back into impact.
            shape.Until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(action is 16739 or 16740 ? 2 : .7);
            Shapes[key] = shape;
        }
    }

    private static void Register()
    {
        foreach (var id in OwnedActions)
        {
            if (SideStep.Override(id))
            {
                Overrides.Add(id);
            }
        }

        Avoids.Add(AvoidanceManager.AddAvoidPolygon<Shape>(Active, () => Center, 30, _ => 0, _ => 1, _ => 10, s => s.Points, s => s.Origin, () => Shapes.Values.ToArray(), priority: AvoidancePriority.High));
        // The normal arena is a 40y circle in both phases. The
        // 0.7y inset prevents the previous unrestricted Yawp escape reaching an edge.
        Avoids.Add(AvoidanceHelpers.AddAvoidDonut(Active, () => Center, 80, 19.3));
        Avoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => Active() && DateTime.UtcNow < captivityUntil, objectSelector: a => a.IsValid && a.IsAlive && !a.IsMe && PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId), radiusProducer: _ => 8.5f)); // Eight-yalm collateral prison plus0.5y clearance.
    }

    private static Shape CreateShape(BattleCharacter actor, uint action)
    {
        var origin = actor.Location;
        float heading = actor.Heading;
        if (action is 16739 or 16740)
        {
            // The actor keeps facing south for BOTH arm sweeps. Its cast's native
            // matrix carries the actual origin/direction; using Heading caused the
            // missing left/right distinction. Reject absent/stale-looking matrices.
            var m = actor.OmenMatrix;
            double length = Math.Sqrt(m.M20 * m.M20 + m.M22 * m.M22);
            if (!double.IsFinite(length) || length < 1 || length > 200 || m.Center.Distance2D(origin) > 45 || Math.Abs(m.Center.Y - origin.Y) > 5)
            {
                return null;
            }

            origin = m.Center;
            heading = (float)Math.Atan2(m.M20, m.M22);
        }

        var points = action switch
        {
            16715 or 16716 => Rectangle(21, 21, -.5f, heading),
            16732 => Rectangle(17, 101, -.5f, heading),
            // Both helpers occupy the boss origin in this client. Their authored
            // side lanes leave an8y central corridor; do not stack both at X100.
            16753 => Rectangle(17, 101, -.5f, heading),
            16754 => Rectangle(17, 101, -.5f, heading),
            16739 or 16740 => Cone(101, 46, heading),
            16751 => Cone(101, 31, heading),
            _ => Cone(41, 16, heading)};
        if (action == 16753)
        {
            origin.X -= 12;
        }

        if (action == 16754)
        {
            origin.X += 12;
        }

        return new Shape
        {
            Origin = origin,
            Points = points
        };
    }

    // Polygons use world-axis offsets. Rectangles add0.5y to each edge. Cones
    // expand by one degree and start0.7y behind the source for near-origin margin.
    internal static Vector2[] Rectangle(float width, float length, float back, float heading) => new[]
    {
        Rotate(-width / 2, back, heading),
        Rotate(width / 2, back, heading),
        Rotate(width / 2, length + back, heading),
        Rotate(-width / 2, length + back, heading)
    };
    internal static Vector2[] Cone(float radius, float halfDegrees, float heading)
    {
        var points = new List<Vector2>
        {
            Rotate(0, -.7f, heading)
        };
        for (int i = 0; i <= 24; i++)
        {
            float angle = heading + (-halfDegrees + 2 * halfDegrees * i / 24) * (float)Math.PI / 180;
            points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
        }

        return points.ToArray();
    }

    private static Vector2 Rotate(float x, float z, float heading) => new(x * (float)Math.Cos(heading) + z * (float)Math.Sin(heading), z * (float)Math.Cos(heading) - x * (float)Math.Sin(heading));
    /// <summary>Releases only this encounter's avoids and overrides on stop, loading, or exit.</summary>
    public static void Reset()
    {
        foreach (var avoid in Avoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        Avoids.Clear();
        Shapes.Clear();
        Reported.Clear();
        captivityUntil = DateTime.MinValue;
        foreach (var action in Overrides)
        {
            SideStep.RemoveHandler(action);
        }

        Overrides.Clear();
    }
}
