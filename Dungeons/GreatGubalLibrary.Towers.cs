using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class GreatGubalLibrary
{
    private const uint VoidCall = 3524, SummonPad = 0x1E99F3, PadLight = 0x1E991D;
    private DisclosureMover towerMover;
    private bool towerLeased;
    private uint towerCaster, selectedPad;
    private DateTime towerUntil;

    // One, two or three colocated lights correspond to the source's expected
    // participant count. Duty Support observations showed one NPC occupying a
    // two-light pad and two NPCs grouping at a three-light pad. One grouped NPC
    // stood about 1.7y from the center, outside the earlier 1.5y selection filter.
    // Use the source's 3y drawn pad radius to identify the group, not as proof
    // of the server's soak radius; Rowan still travels to the pad center.
    private bool UpdateTowers()
    {
        if (!ReferenceEquals(active, this) || !TreeRoot.IsRunning || CommonBehaviors.IsLoading ||
            QuestLogManager.InCutscene || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            !Core.Me.InCombat || WorldManager.SubZoneId != (uint)SubZoneId.RhapsodiesQuadrangle)
        {
            ReleaseTowers();
            return false;
        }
        DateTime now = DateTime.UtcNow;
        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(actor =>
            actor.IsValid && actor.IsAlive && actor.BaseId == 0xE84 && actor.IsCasting &&
            actor.CastingSpellId == VoidCall && actor.SpellCastInfo.IsValid);
        if (caster != null)
        {
            if (towerCaster != caster.ObjectId || now >= towerUntil)
            {
                ReleaseTowers();
                towerCaster = caster.ObjectId;
            }
            towerUntil = now + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }
        if (towerCaster == 0 || now >= towerUntil) { ReleaseTowers(); return false; }
        if (caster == null)
        {
            if (towerLeased) towerMover?.Hold();
            return false;
        }
        if (MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleaseTowerMovement();
            return false;
        }

        var objects = GameObjectManager.GetObjectsOfType<EventObject>(true, false)
            .Where(actor => actor.IsValid && actor.IsVisible && actor.BaseId is SummonPad or PadLight).ToArray();
        var allies = PartyManager.AllMembers?.Where(member => member.ObjectId != Core.Me.ObjectId)
            .Select(member => GameObjectManager.GetObjectByObjectId(member.ObjectId) as BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive).ToArray() ?? Array.Empty<BattleCharacter>();
        var pads = objects.Where(actor => actor.BaseId == SummonPad).Select(pad => new
        {
            Id = pad.ObjectId,
            Position = pad.Location,
            Required = objects.Count(light => light.BaseId == PadLight && light.Location.Distance2D(pad.Location) <= 2.5f),
            Occupied = allies.Count(ally => ally.Location.Distance2D(pad.Location) <= 3f)
        }).Where(pad => pad.Required is >= 1 and <= 3 && pad.Occupied == pad.Required - 1)
            .OrderBy(pad => pad.Id == selectedPad ? 0 : 1)
            .ThenBy(pad => Core.Me.Location.Distance2D(pad.Position)).ToArray();
        var choice = pads.FirstOrDefault(pad =>
        {
            float distance = Core.Me.Location.Distance2D(pad.Position);
            return distance - 1f <= (towerUntil - now).TotalSeconds * 5f && TowerPathClear(Core.Me.Location, pad.Position);
        });
        if (choice == null) { ReleaseTowerMovement(); selectedPad = 0; return false; }
        if (selectedPad != choice.Id)
            Logger.Information("[Gubal] Void Call pad=0x{0:X} required={1} nearby allies={2} position={3}.",
                choice.Id, choice.Required, choice.Occupied, choice.Position);
        selectedPad = choice.Id;
        if (towerMover == null)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover)) return false;
            towerMover = new DisclosureMover(Navigator.PlayerMover, () => towerLeased);
            Navigator.PlayerMover = towerMover;
        }
        if (!towerMover.IsCurrent) { ReleaseTowers(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Gubal Void Call pad");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Gubal Void Call pad");
        towerLeased = true;
        if (Core.Me.Location.Distance2D(choice.Position) <= 1.25f) { towerMover.Hold(); return false; }
        towerMover.RouteTo(choice.Position);
        return true;
    }

    private static bool TowerPathClear(Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 1; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var point = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            if (point.Distance2D(ArenaCenter.TheEverlivingBibliotaph) >= 22f ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private void ReleaseTowerMovement()
    {
        towerMover?.ReleaseRoute();
        if (!towerLeased) return;
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Gubal Void Call released");
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Gubal Void Call released");
        towerLeased = false;
    }

    private void ReleaseTowers()
    {
        ReleaseTowerMovement();
        towerMover?.Detach();
        towerMover = null;
        towerCaster = selectedPad = 0;
        towerUntil = DateTime.MinValue;
    }
}
