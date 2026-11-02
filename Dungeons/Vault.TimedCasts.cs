using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using DutyMechanic.Logging;
using PlanPoint = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

public partial class Vault
{
    private sealed class TimedCast
    {
        internal uint Action;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime ExpectedEnd;
        internal DateTime Until;
    }
    private readonly Dictionary<(uint Actor, uint Action), TimedCast> timedCasts = new();
    private readonly VaultTearForecast tearForecast = new();

    private void RegisterTimedCasts()
    {
        AvoidanceManager.AddAvoidPolygon<TimedCast>(
            condition: () => bossEncounterActive && TreeRoot.IsRunning,
            leashPointProducer: null, leashRadius: 50f,
            rotationProducer: cast => -cast.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: cast => CastPoints(cast.Action), locationProducer: cast => cast.Origin,
            collectionProducer: () => timedCasts.Values.Where(c => c.Until > DateTime.UtcNow &&
                (c.Action is DimensionalCollapseSmallSpell or DimensionalCollapseMediumSpell or DimensionalCollapseLargeSpell or
                    WhiteKnightsTourSpell or BlackKnightsTourSpell || OwnsTelegraphs())).ToArray(),
            priority: AvoidancePriority.High);
        // The new tear can tether Rowan immediately on spawn, before a normal
        // actor avoid can move her out. Project from the last stable slash facing
        // and cover observed projection error until the real actor takes over.
        AvoidanceManager.AddAvoidLocation(
            canRun: () => bossEncounterActive && TreeRoot.IsRunning && WorldManager.SubZoneId == 1571,
            leashPointProducer: () => SerGrinnauxArenaCenter, leashRadius: 40f,
            radiusProducer: (Vector3 _) => 10f,
            locationProducer: p => p,
            collectionProducer: () => tearForecast.Active.Select(f => new Vector3(f.Origin.X, SerGrinnauxArenaCenter.Y, f.Origin.Y)).ToArray(),
            objectValidator: null, ignoreIfBlocking: false);
    }

    // Snapshots use scalar positions/headings, not long-lived game wrappers. The
    // November 2 traces show collapse and Rip effects about 0.7s after cast end,
    // and Hyperdimensional Slash about 1.6s after. Keep the last observed cast
    // geometry through those delivery windows, with extra scheduling margin.
    private void UpdateTimedCasts()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        var tears = actors.Where(a => a.NpcId == AetherialTearNpc)
            .Select(a => (a.ObjectId, new PlanPoint(a.X, a.Z))).ToArray();
        var present = actors.Select(a => a.ObjectId).ToHashSet();
        var seen = new HashSet<(uint Actor, uint Action)>();
        foreach (var actor in actors)
        {
            if (!actor.IsValid || !actor.IsCasting || !actor.SpellCastInfo.IsValid) continue;
            uint action = actor.CastingSpellId;
            uint zone = WorldManager.SubZoneId;
            bool supported = zone == 1570 && action == 1099 && actor.BaseId == 0x104E ||
                zone == 1571 && (action == 1135 && actor.BaseId == 0x1053 ||
                    action is 4134 or 4140 && actor.BaseId == 0x1054 ||
                    action is 4137 or 4138 or 4139 && actor.BaseId == 0xD25) ||
                zone == 1572 && (action == 4146 && actor.BaseId == 0xD25 ||
                    action == 4152 && actor.BaseId == 0x1057 || action == 4153 && actor.BaseId == 0x1058);
            if (!supported) continue;
            if (action == 4134 && actor.Location.Distance2D(SerGrinnauxArenaCenter) < 3f &&
                tearForecast.Observe(actor.ObjectId, actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds,
                    new PlanPoint(actor.X, actor.Z), actor.Heading, tears.Select(t => t.ObjectId), now))
                Logger.Information("[Vault] Forecast tear origin " + tearForecast.Active.Last().Origin + ".");
            var origin = action is 4140 or 4146 ? actor.SpellCastInfo.CastLocation : actor.Location;
            // Placed casts can briefly expose zero or stale ground coordinates.
            // Only a point on this arena's elevation and bounded floor is evidence.
            if (action is 4140 or 4146)
            {
                var center = action == 4140 ? SerGrinnauxArenaCenter : SerCharibertArenaCenter;
                if (Math.Abs(origin.Y - center.Y) > 5 || origin.Distance2D(center) > 30) continue;
            }
            var key = (actor.ObjectId, action);
            seen.Add(key);
            if (!timedCasts.TryGetValue(key, out var snapshot))
                timedCasts[key] = snapshot = new TimedCast { Action = action };
            // Placed Rip and Heavensflame use the confirmed cast location;
            // the remaining shapes originate at their current actor position.
            snapshot.Origin = origin;
            snapshot.Heading = actor.Heading;
            snapshot.ExpectedEnd = now + actor.SpellCastInfo.RemainingCastTime;
            snapshot.Until = VaultCastWindow.Until(snapshot.ExpectedEnd, action);
        }
        // A still-present actor that stopped much earlier than its expected bar
        // end was interrupted; an absent helper is retained for its delivery tail.
        foreach (var key in timedCasts.Where(p => p.Value.Until <= now ||
            VaultCastWindow.Interrupted(now, p.Value.ExpectedEnd, seen.Contains(p.Key), present.Contains(p.Key.Actor)))
            .Select(p => p.Key).ToArray())
            timedCasts.Remove(key);
        int forecastsBefore = tearForecast.Active.Count;
        tearForecast.Reconcile(tears.Select(t => (t.ObjectId, t.Item2)), now);
        if (tearForecast.Active.Count < forecastsBefore)
            Logger.Information("[Vault] Tear forecast released after actor handoff or timeout.");
    }

    private void ClearTimedCasts()
    {
        timedCasts.Clear();
        tearForecast.Reset();
    }
}
