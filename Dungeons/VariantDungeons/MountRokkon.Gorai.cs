using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    // October 1 live Gorai is Npc 12373/Base 16220. His helper row 9020 must not
    // establish encounter ownership; the reference arena and captured objects
    // share this fixed center even while the real boss moves toward the player.
    private static readonly Vector3 GoraiCenter = new(741, 91, -190);
    private readonly Dictionary<string, Impact> _goraiShapes = new();
    private readonly Dictionary<uint, LevinOrb> _goraiOrbs = new();
    private bool _goraiBalladActive;
    private ushort _goraiFloorState;
    private void ObserveGoraiFloor()
    {
        if (WorldManager.ZoneId != 1137 || Core.Me.Distance2D(GoraiCenter) > 45 || DirectorManager.ActiveDirector is not ff14bot.Directors.InstanceContentDirector d || !d.IsValid)
            return;
        var floor = d.MapEffects.Where(m => m.ID == 9802908).ToArray();
        if (floor.Length != 1)
            return;
        // Fresh traversal October 1:9802908 changed 4->1 after Unenlightenment;
        // both native stone and generic orb avoids then failed to find paths.
        // One collision rebuild restored ordinary escape. Rebuild on the actual
        // floor transition, never on every cast or failed path. Retain the latch
        // through combat ending; state 4 on a wipe rearms the next physical change.
        if (floor[0].State == 1 && _goraiFloorState != 1)
        {
            AvoidanceManager.ResetNavigation();
            ff14bot.Helpers.Logging.Write("[Rokkon] Gorai floor changed; refreshing local collision once.");
        }

        _goraiFloorState = floor[0].State;
    }

    private static bool InGorai() => InRokkonCombat() && Core.Me.Distance2D(GoraiCenter) < 45 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.BaseId == 0x3F5C && b.IsAlive && b.Distance2D(GoraiCenter) < 45);
    private void RegisterGorai()
    {
        // October 1 18:23–24: visible event objects 2013331/2013332 own the
        // telegraphs, but damage has no helper cast and generic avoidance stayed
        // empty. Plectrum 34008 expands strips/circles; Melody 34009 splits strips
        // and makes stone rings. These are one simultaneous parent-owned group.
        _goraiShapes.Clear();
        _goraiOrbs.Clear();
        _goraiBalladActive = false;
        _goraiFloorState = 0;
        // Unenlightenment reduces the initial 45y floor to 40y. Reserve a 0.5y
        // wall inset from the start; the outer 140y ring contains the local mesh.
        AvoidanceHelpers.AddAvoidSquareDonut(InGorai, 39, 39, 140, 140, () => new[] { GoraiCenter });
        AvoidanceManager.AddAvoidPolygon<Impact>(InGorai, null, 80, c => -c.Heading, _ => 1, _ => 15, c => Rectangle(c.Action == 34010 ? 5.5f : 3, .5f, 46.5f), c => c.Location, () => GoraiShapes().Where(c => c.Action is 34010 or 34011), priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Impact>(InGorai, _ => 11.5f, c => c.Location, () => GoraiShapes().Where(c => c.Action == 34013));
        AvoidanceHelpers.AddAvoidDonut(InGorai, () => GoraiShapes().Where(c => c.Action == 34014).Select(c => c.Location).ToArray(), 16.5, 4.5);
        // October 1 18:54: three helpers cast together but resolve two seconds
        // apart. Publishing all three concentric regions at once covers the floor.
        // Actor origins share the arena center; one helper's CastLocation retained
        // an earlier bait, so it cannot define this self-centered sequence.
        AvoidanceManager.AddAvoidLocation<Impact>(InGorai, _ => 10.5f, c => c.Location, () => GoraiRingWave().Where(c => c.Action == 34026));
        AvoidanceHelpers.AddAvoidDonut(InGorai, () => GoraiRingWave().Where(c => c.Action == 34027).Select(c => c.Location).ToArray(), 20.5, 9.5);
        AvoidanceHelpers.AddAvoidDonut(InGorai, () => GoraiRingWave().Where(c => c.Action == 34028).Select(c => c.Location).ToArray(), 30.5, 19.5);
        // October 1 20:11:32: generic 34035 treated all four orbs as radius 8 and
        // allowed a large-orb hit. Exactly one gains 2970/value 609 about nine seconds
        // before impact. Only then publish the simultaneous 18/8-yalm set, padded 0.5;
        // publishing four large circles before the shrink would erase all safe floor.
        AvoidanceManager.AddAvoidLocation<LevinOrb>(InGorai, o => o.Radius, o => o.Location, () => _goraiOrbs.Values.Where(o => o.End > DateTime.UtcNow));
    }

    private IEnumerable<Impact> GoraiRingWave()
    {
        var pending = PendingImpacts().Where(c => c.Action is 34026 or 34027 or 34028).ToArray();
        if (pending.Length == 0)
            return Array.Empty<Impact>();
        var first = pending.Min(c => c.End);
        return pending.Where(c => c.End <= first.AddMilliseconds(350));
    }

    private IEnumerable<Impact> GoraiShapes() => _goraiShapes.Values.Where(s => s.End > DateTime.UtcNow);
    private void ObserveGorai()
    {
        ObserveGoraiOrbs();
        var now = DateTime.UtcNow;
        foreach (var key in _goraiShapes.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            _goraiShapes.Remove(key);
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.IsValid && b.BaseId == 0x3F5C && b.IsCasting && b.CastingSpellId is 34008 or 34009);
        if (boss == null)
        {
            _goraiBalladActive = false;
            return;
        }

        if (!_goraiBalladActive)
            _goraiShapes.Clear();
        _goraiBalladActive = true;
        var split = boss.CastingSpellId == 34009;
        // The 18:23:32 flame hit and 18:24:38 fatal rings resolved about 3.45s after
        // RB's reported parent end. Retain 3.85s, including 0.4s impact padding.
        var end = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3.85);
        foreach (var actor in GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId is 0x1EB893 or 0x1EB894 && o.Distance2D(GoraiCenter) < 34))
        {
            if (actor.BaseId == 0x1EB894)
                RetainGoraiShape(actor.ObjectId + ":stone", split ? 34014u : 34013u, actor.Location, actor.Heading, end);
            else if (!split)
                RetainGoraiShape(actor.ObjectId + ":wide", 34010, actor.Location, actor.Heading, end);
            else
            {
                // Split strips move 7.5y to either side of the original flame axis.
                // Width 5 plus 0.5y on both sides preserves the central safe lane.
                var side = new Vector3((float)Math.Cos(actor.Heading), 0, -(float)Math.Sin(actor.Heading)) * 7.5f;
                RetainGoraiShape(actor.ObjectId + ":left", 34011, actor.Location + side, actor.Heading, end);
                RetainGoraiShape(actor.ObjectId + ":right", 34011, actor.Location - side, actor.Heading, end);
            }
        }
    }

    private void ObserveGoraiOrbs()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _goraiOrbs.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            _goraiOrbs.Remove(key);
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsVisible && b.BaseId == 16224 && b.IsCasting && b.CastingSpellId == 34035 && b.Distance2D(GoraiCenter) < 30).ToArray();
        if (actors.Length != 4 || actors.Count(b => b.CharacterAuras.Any(a => a.Id == 2970 && a.Value == 609)) != 1)
            return;
        foreach (var actor in actors)
        {
            if (!_goraiOrbs.TryGetValue(actor.ObjectId, out var orb))
                _goraiOrbs[actor.ObjectId] = orb = new LevinOrb();
            orb.Location = actor.Location; // These self-centered casts report a zero cast location.
            orb.Radius = actor.CharacterAuras.Any(a => a.Id == 2970 && a.Value == 609) ? 8.5f : 18.5f;
            // Observed damage followed the reported end by~0.95s; keep 1.1s so a
            // disappeared cast cannot authorize moving back into the resolving orb.
            orb.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.1);
        }
    }

    private sealed class LevinOrb
    {
        internal Vector3 Location;
        internal float Radius;
        internal DateTime End;
    }

    private void RetainGoraiShape(string key, uint action, Vector3 location, float heading, DateTime end)
    {
        // Stable managed objects prevent repeated avoidance replacement while the
        // cast report refreshes. No native actor survives the current bot frame.
        if (!_goraiShapes.TryGetValue(key, out var shape))
            _goraiShapes[key] = shape = new Impact();
        shape.Action = action;
        shape.Location = location;
        shape.Heading = heading;
        shape.End = end;
    }
}
