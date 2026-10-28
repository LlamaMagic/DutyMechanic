using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DutyMechanic.Dungeons;

public sealed partial class WhorleaterHard
{
    // Use the tail's base ID: untargetable helpers can share the head's name ID,
    // and both pieces change position during the fight.
    private const uint Tail = 0xA86;
    private readonly WhorleaterHardPolicy.TankAssignment tankAssignment = new();
    private TankTargetingProvider tankTargeting;
    private bool tankTargetingSuperseded, tailSeen;
    private uint tankPiece, otherTankId;
    private readonly OffTargetPullSettings offTargetPullSettings = new();

    private bool TankCoordinationLive => active && TreeRoot.IsRunning && Core.Player != null
        && Core.Player.IsAlive && ClassJobRoles.Tanks.Contains(Core.Player.CurrentJob)
        && DutyManager.InInstance && WorldManager.ZoneId == 281
        && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene;

    private void UpdateTankCoordination(BattleCharacter[] actors, BattleCharacter head, DateTime now)
    {
        if (!TankCoordinationLive)
        {
            ReleaseTankCoordination(true);
            return;
        }
        // Sixty yalms covers the verified 28x39 deck diagonal with room for the
        // overhanging bosses; an unrelated loaded actor must not become a co-tank.
        var other = PartyManager.VisibleMembers.Select(m => m.BattleCharacter)
            .Where(m => m != null && m.IsValid && m.IsAlive && m.ObjectId != Core.Player.ObjectId
                && ClassJobRoles.Tanks.Contains(m.CurrentJob) && m.Location.Distance2D(Core.Player.Location) < 60)
            .OrderBy(m => m.ObjectId).FirstOrDefault();
        if (other == null)
        {
            ReleaseTankCoordination(true);
            return;
        }
        var tail = actors.FirstOrDefault(a => a.BaseId == Tail && a.IsAlive);
        tailSeen |= tail != null;
        uint selected = tankAssignment.Select(Core.Player.ObjectId, other.ObjectId, head.ObjectId,
            tail?.ObjectId ?? 0, head.CurrentTargetId, tail?.CurrentTargetId ?? 0, other.CurrentTargetId,
            CombatReady(head) && tail != null && CombatReady(tail), now);
        if (selected != tankPiece || otherTankId != other.ObjectId)
            Logger.Information($"[Whorleater] Tank assignment={(selected == WhorleaterHardPolicy.TankTail ? "tail" : "head")}; other={other.ObjectId:X}; otherTarget={other.CurrentTargetId:X}; headVictim={head.CurrentTargetId:X}; tailVictim={tail?.CurrentTargetId ?? 0:X}.");
        tankPiece = selected;
        otherTankId = other.ObjectId;
        if (tankTargeting != null && !ReferenceEquals(CombatTargeting.Instance.Provider, tankTargeting))
        {
            // A replacement provider owns targeting until the next encounter. Never
            // wrap it repeatedly or undo its choice during our later cleanup.
            tankTargetingSuperseded = true;
            ReleaseTankCoordination(false);
        }
        if (tankTargetingSuperseded)
            return;
        if (tankTargeting == null)
        {
            if (CombatTargeting.Instance.Locked)
                return;
            tankTargeting = new TankTargetingProvider(this, CombatTargeting.Instance.Provider);
            CombatTargeting.Instance.Provider = tankTargeting;
        }
        // Magitek can pull off-target enemies despite the correct Kill POI. Temporarily
        // disable those pulls so it does not take the other tank's piece; do not save settings.
        offTargetPullSettings.Apply(tailSeen);
    }

    private void ReleaseTankCoordination(bool reset)
    {
        if (tankTargeting != null && ReferenceEquals(CombatTargeting.Instance.Provider, tankTargeting))
        {
            bool locked = CombatTargeting.Instance.Locked;
            CombatTargeting.Instance.Locked = false;
            CombatTargeting.Instance.Provider = tankTargeting.Previous;
            CombatTargeting.Instance.Locked = locked;
        }
        tankTargeting = null;
        offTargetPullSettings.Restore();
        if (reset)
        {
            tankAssignment.Reset();
            tankPiece = otherTankId = 0;
            tailSeen = tankTargetingSuperseded = false;
        }
    }

    // Eligibility and add ranking remain with the profile's existing provider. Filtering
    // only the other piece lets OrderBot update Kill POIs and movement normally. Keep
    // the initial head until the tail first exists; never chase the other piece just
    // because our assigned piece temporarily submerges.
    private sealed class TankTargetingProvider(WhorleaterHard owner, ITargetingProvider previous) : ITargetingProvider
    {
        internal ITargetingProvider Previous => previous;

        /// <inheritdoc/>
        public List<BattleCharacter> GetObjectsByWeight()
        {
            var ranked = previous?.GetObjectsByWeight() ?? [];
            if (!owner.TankCoordinationLive || !ReferenceEquals(owner.tankTargeting, this))
                return ranked;
            return ranked.Where(a => WhorleaterHardPolicy.KeepTankCandidate(a.NpcId, owner.tankPiece, owner.tailSeen)).ToList();
        }
    }

    // Magitek is optional and loader-hosted, so inspect its public settings only when
    // the active routine is Magitek. Exact property names are from the installed API;
    // unknown/ambiguous versions produce one diagnostic instead of guessed members.
    private sealed class OffTargetPullSettings
    {
        private readonly List<(object Instance, PropertyInfo Property, bool Value)> owned = new();
        private bool attempted;

        internal void Apply(bool split)
        {
            if (!split || attempted)
                return;
            attempted = true;
            if (RoutineManager.Current?.GetType().FullName?.Contains("Magitek") != true)
                return;
            string job = Core.Player.CurrentJob.ToString();
            string attack = job == "Paladin" ? "ShieldLob" : job == "Warrior" ? "Tomahawk" : null;
            if (attack == null)
                return; // Other tank jobs have no verified off-target pull switch.
            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType($"Magitek.Models.{job}.{job}Settings")).Where(t => t != null).ToArray();
                if (types.Length != 1)
                    throw new InvalidOperationException("Missing or ambiguous Magitek settings type");
                var type = types[0];
                var instance = type.GetProperty("Instance")?.GetValue(null)
                    ?? throw new InvalidOperationException("Magitek settings instance unavailable");
                foreach (string name in new[] { $"Use{attack}OnLostAggro", $"Use{attack}ToPullExtraEnemies" })
                {
                    var property = type.GetProperty(name);
                    if (property?.PropertyType != typeof(bool) || !property.CanWrite)
                        throw new InvalidOperationException($"Unsupported Magitek setting {name}");
                    bool original = (bool)property.GetValue(instance);
                    owned.Add((instance, property, original));
                    property.SetValue(instance, false);
                }
                Logger.Information("[Whorleater] Scoped off-target ranged pulls disabled while splitting tanks.");
            }
            catch (Exception ex)
            {
                Restore();
                attempted = true;
                Logger.Information($"[Whorleater] Cannot scope off-target pulls: {ex.Message}; tank target assignment remains active.");
            }
        }

        internal void Restore()
        {
            foreach (var entry in owned)
            {
                try
                {
                    // Do not overwrite an explicit change away from our forced value.
                    if (entry.Property.GetValue(entry.Instance) is false)
                        entry.Property.SetValue(entry.Instance, entry.Value);
                }
                catch (Exception ex) { Logger.Information($"[Whorleater] Pull-setting restore failed: {ex.Message}"); }
            }
            if (owned.Count > 0)
                Logger.Information("[Whorleater] Scoped off-target pull settings restored.");
            owned.Clear();
            attempted = false;
        }
    }
}
