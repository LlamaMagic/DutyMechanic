using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class GreatGubalLibrary
{
    private const uint HeadDown = 4163;
    private DisclosureMover chargeMover;
    private bool chargeLeased;
    private uint chargeCaster, chargeTarget, chargePartner;
    private DateTime chargeUntil;

    // Head Down is a target-directed charge. The captured two-recipient hits
    // were about half the solo hits, consistent with sharing its line damage.
    // Moving toward the marked actor (or a partner when self-marked) expresses
    // that mechanic directly; it is not a proxy-following safety decision.
    private bool UpdateHeadDown()
    {
        if (!ReferenceEquals(active, this) || !TreeRoot.IsRunning || CommonBehaviors.IsLoading ||
            QuestLogManager.InCutscene || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            !Core.Me.InCombat || WorldManager.SubZoneId != (uint)SubZoneId.AstrologyandAstromancyCamera)
        {
            ReleaseHeadDown();
            return false;
        }
        DateTime now = DateTime.UtcNow;
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(actor =>
            actor.IsValid && actor.BaseId == 0xE83 && actor.IsCasting && actor.CastingSpellId == HeadDown && actor.SpellCastInfo.IsValid);
        if (boss != null)
        {
            uint target = boss.SpellCastInfo.TargetId;
            if (target != chargeTarget || boss.ObjectId != chargeCaster || now >= chargeUntil)
            {
                ReleaseHeadDown();
                chargeCaster = boss.ObjectId;
                chargeTarget = target;
                chargePartner = 0;
            }
            chargeUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.5);
        }
        if (chargeCaster == 0 || now >= chargeUntil) { ReleaseHeadDown(); return false; }
        // Once the cast wrapper disappears, retain an already acquired hold
        // through the short effect tail but never chase the moving boss or ally.
        if (boss == null)
        {
            if (chargeLeased) chargeMover?.Hold();
            return false;
        }
        if (HasPlayerOilTether() || MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleaseHeadDownMovement();
            return false;
        }

        var party = PartyManager.AllMembers?.Select(member => GameObjectManager.GetObjectByObjectId(member.ObjectId) as BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && actor.ObjectId != Core.Me.ObjectId).ToArray()
            ?? Array.Empty<BattleCharacter>();
        BattleCharacter partner = chargeTarget == Core.Me.ObjectId
            ? party.Where(actor => actor.ObjectId == chargePartner).FirstOrDefault()
                ?? party.OrderBy(actor => actor.Location.Distance2D(Core.Me.Location)).FirstOrDefault()
            : party.FirstOrDefault(actor => actor.ObjectId == chargeTarget);
        if (partner == null) { ReleaseHeadDownMovement(); return false; }
        chargePartner = partner.ObjectId;

        var source = boss ?? GameObjectManager.GetObjectByObjectId(chargeCaster) as BattleCharacter;
        if (source == null || !source.IsValid || !source.IsAlive) { ReleaseHeadDown(); return false; }
        float dx = partner.X - source.X, dz = partner.Z - source.Z;
        float length = (float)Math.Sqrt(dx * dx + dz * dz);
        if (length < 1f) { ReleaseHeadDownMovement(); return false; }
        // At 08:26:17 Rowan was within 2.5y of Pero, but closer to
        // Byblos; the charge ended at Rowan and missed Pero. Put a self-marked
        // player beyond the partner, or an unmarked player before the target.
        float offset = chargeTarget == Core.Me.ObjectId ? 2f : -Math.Min(2f, length * .5f);
        var destination = new Vector3(partner.X + dx / length * offset, partner.Y, partner.Z + dz / length * offset);
        float distance = Core.Me.Location.Distance2D(destination);
        // Reject a late, infeasible chase rather than starting a move that
        // cannot reach the intended interception point before resolution.
        if (distance > 1f && (distance > 15f || distance - 1f > (chargeUntil - now).TotalSeconds * 5f))
        {
            ReleaseHeadDownMovement();
            return false;
        }
        if (!ChargePathClear(Core.Me.Location, destination)) { ReleaseHeadDownMovement(); return false; }
        if (chargeMover == null)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover)) return false;
            chargeMover = new DisclosureMover(Navigator.PlayerMover, () => chargeLeased);
            Navigator.PlayerMover = chargeMover;
            Logger.Information("[Gubal] Head Down share target=0x{0:X} partner=0x{1:X}.", chargeTarget, chargePartner);
        }
        if (!chargeMover.IsCurrent) { ReleaseHeadDown(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Gubal Head Down share");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Gubal Head Down share");
        chargeLeased = true;
        if (distance <= 1f) { chargeMover.Hold(); return false; }
        chargeMover.RouteTo(destination);
        return true;
    }

    private static bool ChargePathClear(Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 1; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var point = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            if (point.Distance2D(ArenaCenter.Byblos) >= 22f ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private void ReleaseHeadDownMovement()
    {
        chargeMover?.ReleaseRoute();
        if (!chargeLeased) return;
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Gubal Head Down released");
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Gubal Head Down released");
        chargeLeased = false;
    }

    private void ReleaseHeadDown()
    {
        ReleaseHeadDownMovement();
        chargeMover?.Detach();
        chargeMover = null;
        chargeCaster = chargeTarget = chargePartner = 0;
        chargeUntil = DateTime.MinValue;
    }
}
