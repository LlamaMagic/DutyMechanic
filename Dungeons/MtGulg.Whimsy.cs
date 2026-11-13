using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class MtGulg
{
    private sealed class Tower
    {
        internal uint Id;
        internal Vector3 Center;
        internal DateTime FirstSeen;
    }

    private readonly Dictionary<uint, Tower> towers = [];
    private readonly HashSet<uint> expiredTowers = [];
    private readonly CapabilityManagerHandle towerHandle = CapabilityManager.CreateNewHandle();
    private Tower selectedTower;
    private bool movingToTower;

    private void UpdateTowers()
    {
        DateTime now = DateTime.UtcNow;
        var objects = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(a => a.IsValid && a.BaseId == 0x1EAACF && a.Distance2D(WhimsyCenter) < 22).ToArray();
        foreach (var actor in objects)
        {
            // States 28/44 describe the falling sphere, not tower occupancy.
            int? state = ReadTimeline(actor);
            if (state is not (28 or 44))
            {
                continue;
            }
            if (!expiredTowers.Contains(actor.ObjectId) && !towers.ContainsKey(actor.ObjectId))
            {
                towers[actor.ObjectId] = new Tower { Id = actor.ObjectId, Center = actor.Location, FirstSeen = now };
            }
        }
        foreach (uint id in towers.Keys.Where(id => objects.All(a => a.ObjectId != id) ||
            now - towers[id].FirstSeen > TimeSpan.FromSeconds(15)).ToArray())
        {
            // A timed-out object cannot immediately create another 15-second lease.
            if (objects.Any(a => a.ObjectId == id))
            {
                expiredTowers.Add(id);
            }
            towers.Remove(id);
        }
        expiredTowers.RemoveWhere(id => objects.All(a => a.ObjectId != id));

        // Towers last about 10s; expire stale holds at 15s. Another living party
        // member can take over while we yield to a hazard.
        var party = PartyManager.VisibleMembers.Select(p => p.BattleCharacter)
            .Where(a => a != null && a.IsValid && a.IsAlive && a.ObjectId != Core.Me.ObjectId).ToArray();
        bool Occupied(Tower tower) => party.Any(a => a.Distance2D(tower.Center) < 4.5f);
        if (selectedTower != null && (!towers.ContainsKey(selectedTower.Id) || Occupied(selectedTower)))
        {
            ReleaseTowerPosition("resolved, disappeared or another participant arrived");
        }
        if (selectedTower == null)
        {
            selectedTower = towers.Values.Where(t => !Occupied(t) && OrdinarySafe(t.Center))
                .OrderBy(t => Core.Me.Location.Distance2D(t.Center)).FirstOrDefault();
            if (selectedTower != null)
            {
                Logger.Information("[MtGulg] Tower acquired id={0:X8} center={1} from={2}",
                                selectedTower.Id, selectedTower.Center, Core.Me.Location);
            }
        }
        if (selectedTower == null || movement == null)
        {
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || !OrdinarySafe(selectedTower.Center))
        {
            // Keep the selected tower, but yield input until its approach is safe.
            movement.Request(null, 0);
            CapabilityManager.Clear(towerHandle, CapabilityFlags.Movement, "Mt. Gulg tower yields to hazard");
            movingToTower = false;
            return;
        }
        CapabilityManager.Update(towerHandle, CapabilityFlags.Movement, 1000, "Mt. Gulg Judgment Day participant");
        movement.Request(selectedTower.Center, .35f);
        // Hold through impact without consuming routine ticks once stationary.
        movingToTower = !MovementManager.GenerallyOutOfControl &&
            Core.Me.Location.Distance2D(selectedTower.Center) > .35f;
    }

    private void ReleaseTowerPosition(string reason)
    {
        if (selectedTower != null)
        {
            Logger.Information("[MtGulg] Tower released id={0:X8} reason={1} at={2}", selectedTower.Id, reason, Core.Me?.Location);
            movement?.Request(null, 0);
            CapabilityManager.Clear(towerHandle, CapabilityFlags.Movement, "Mt. Gulg tower released");
        }
        selectedTower = null;
        movingToTower = false;
    }
}
