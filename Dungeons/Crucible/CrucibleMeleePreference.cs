using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Linq;
using V2 = System.Numerics.Vector2;
using V3 = Clio.Utilities.Vector3;

namespace DutyMechanic.Dungeons
{
    // Common tie-breaker for every Crucible planner: apply only AFTER its
    // mechanic-specific safety/route filters. Snapshots avoid retaining native
    // wrappers or reading moving targets repeatedly inside candidate loops.
    // A missing target makes every candidate equal and preserves old behavior.
    internal static class CrucibleMeleePreference
    {
        internal static Func<V2, int> Capture()
        {
            var target = Core.Me.CurrentTarget as BattleCharacter;
            if (target == null || !target.IsAlive || !target.IsTargetable)
            {
                return _ => 0;
            }
            // Ymir's Sahagin reflection requires pet-only damage from outside
            // autoattack reach. A geometric safe point is not melee-safe there.
            if (target.HasAura(5434))
            {
                return _ => 1;
            }

            var position = new V2(target.Location.X, target.Location.Z);
            // Match the routine's native axe range, with its half-yalm arrival
            // inset. Combat reach matters for large bosses such as Chewchum.
            float range = target.CombatReach + Core.Me.CombatReach +
                (float)DataManager.GetSpellData(44879).Range - .5f;
            return p => V2.Distance(p, position) <= range ? 0 : 1;
        }

        internal static Func<V3, int> CaptureWorld()
        {
            var rank = Capture();
            return p => rank(new V2(p.X, p.Z));
        }

        // Native-only dodges have no authored candidate grid. After escape,
        // permit a melee improvement only across wholly clear registered space,
        // with the caller's mechanic constraints also sampled along the route.
        // This function selects a goal; the existing owner still controls motion.
        internal static V3? ReachableMelee(V3? held, Func<V3, bool> safe)
        {
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                return null;
            }

            var target = Core.Me.CurrentTarget as BattleCharacter;
            if (target == null || !target.IsAlive || !target.IsTargetable || target.HasAura(5434))
            {
                return null;
            }

            var start = Core.Me.Location;
            var origin = target.Location;
            float range = target.CombatReach + Core.Me.CombatReach + (float)DataManager.GetSpellData(44879).Range - .5f;
            var avoids = AvoidanceManager.Avoids.ToArray();
            bool Clear(V3 p) => safe(p) && !avoids.Any(a => a.IsPointInAvoid(p));
            bool Route(V3 p)
            {
                int count = Math.Max(1, (int)Math.Ceiling(start.Distance2D(p) * 4));
                return Enumerable.Range(0, count + 1).All(i => Clear(start + (p - start) * (i / (float)count)));
            }
            if (held.HasValue && held.Value.Distance2D(origin) <= range && Clear(held.Value) && Route(held.Value))
            {
                return held;
            }

            if (start.Distance2D(origin) <= range && Clear(start))
            {
                return start;
            }

            return Enumerable.Range(0, 32).Select(i => new V3(origin.X + MathF.Sin(i * MathF.PI / 16) * range,
                    start.Y, origin.Z + MathF.Cos(i * MathF.PI / 16) * range))
                .Where(Clear).Where(Route).OrderBy(p => p.Distance2D(start)).Select(p => (V3?)p).FirstOrDefault();
        }
    }
}
