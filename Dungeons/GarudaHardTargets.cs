using System.Collections.Generic;
using System.Linq;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.NeoProfiles;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public sealed partial class GarudaHard
{
    private AddTargeting targets;
    private bool targetingLost;
    private uint mainTank;

    private void UpdateTargets()
    {
        var boss = Actors().FirstOrDefault(a => a.BaseId == Garuda);
        // Visual casts target Garuda herself. Preserve the last player victim so
        // a choreography cast cannot turn the main tank into an add-chasing DPS.
        if (boss != null && PartyManager.VisibleMembers.Any(m => m.ObjectId == boss.CurrentTargetId))
            mainTank = boss.CurrentTargetId;
        if (targets != null && !ReferenceEquals(CombatTargeting.Instance.Provider, targets))
        {
            targets = null;
            targetingLost = true;
        }
        if (targets == null && !targetingLost && !CombatTargeting.Instance.Locked)
        {
            targets = new AddTargeting(this, CombatTargeting.Instance.Provider);
            CombatTargeting.Instance.Provider = targets;
        }
    }

    private void ReleaseTargets()
    {
        if (targets != null && ReferenceEquals(CombatTargeting.Instance.Provider, targets))
        {
            bool locked = CombatTargeting.Instance.Locked;
            CombatTargeting.Instance.Locked = false;
            CombatTargeting.Instance.Provider = targets.Previous;
            CombatTargeting.Instance.Locked = locked;
        }
        targets = null;
        targetingLost = false;
        mainTank = 0;
    }

    private sealed class AddTargeting(GarudaHard owner, ITargetingProvider previous) : ITargetingProvider
    {
        internal ITargetingProvider Previous => previous;

        /// <inheritdoc/>
        public List<BattleCharacter> GetObjectsByWeight()
        {
            var ranked = previous?.GetObjectsByWeight() ?? [];
            if (!EncounterActive())
                return ranked;
            // Reorder only profile-eligible actors; never introduce nearby helpers.
            // Ties preserve the underlying provider's order when a tether is absent.
            return ranked.OrderByDescending(Priority).ToList();
        }

        private int Priority(BattleCharacter actor)
        {
            bool tank = ClassJobRoles.Tanks.Contains(Core.Me.CurrentJob);
            if (tank && owner.mainTank == Core.Me.ObjectId && actor.BaseId == Garuda)
                return 500;
            bool sister = actor.BaseId is Chirada or Suparna;
            bool green = sister && actor.VfxContainer.IsValid && actor.VfxContainer.Tethers.Any(t => t.Id == 4);
            bool red = sister && actor.VfxContainer.IsValid && actor.VfxContainer.Tethers.Any(t => t.Id == 11);
            // Rehabilitation (4) heals Garuda; Damage Up (11) belongs on the off-tank.
            if (tank && owner.mainTank != 0 && owner.mainTank != Core.Me.ObjectId && red)
                return 450;
            if (actor.BaseId == 0x5FF) return 400; // Satin Plume: party sleep.
            if (green) return 350;
            if (sister) return 300;
            if (actor.BaseId == 0xF1) return 200; // Razor Plume: pillar damage.
            return 0;
        }
    }
}
