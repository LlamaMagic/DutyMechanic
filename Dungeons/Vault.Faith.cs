using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using PlanPoint = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

public partial class Vault
{
    private readonly CapabilityManagerHandle faithHandle = CapabilityManager.CreateNewHandle();
    private bool faithFallback, faithMoving, faithHolding, faithAcquired, faithResolved, faithWaitingLogged;
    private DateTime faithUntil;
    private DateTime nextFaithSearch;
    private uint faithCaster;
    private Vector3? faithLastCastingPosition;
    private PlanPoint? faithDestination;

    // The November 2 captures show the forced movement resolving about 0.6s
    // after the cast bar ends. Retain the chosen safe staging point through a
    // one-second delivery tail, while death/loading/stop release it immediately.
    private void UpdateFaith()
    {
        if (!bossEncounterActive || WorldManager.SubZoneId != 1571)
        {
            ReleaseFaith();
            return;
        }
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.BaseId == 0x1054);
        bool casting = boss != null && boss.IsCasting && boss.CastingSpellId == FaithUnmovingSpell && boss.SpellCastInfo.IsValid;
        if (casting)
        {
            faithCaster = boss.ObjectId;
            faithLastCastingPosition = Core.Me.Location;
            faithUntil = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }
        if (boss == null || !casting && DateTime.UtcNow >= faithUntil)
        {
            ReleaseFaith();
            return;
        }
        // Once the cast has ended and game-owned forced movement starts, never
        // steer back toward the staging point during the effect-delivery tail.
        // The second November 2 capture showed the forced flag missing at the
        // landing pulse. A displacement over five yalms from the final cast-bar
        // position is also an effect handoff, not a request to restage.
        PlanPoint? lastCasting = faithLastCastingPosition.HasValue
            ? new PlanPoint(faithLastCastingPosition.Value.X, faithLastCastingPosition.Value.Z) : null;
        if (VaultKnockbackPlan.Delivered(casting, MovementManager.GenerallyOutOfControl,
            lastCasting, new PlanPoint(Core.Me.X, Core.Me.Z)))
        {
            ReleaseFaithPosition();
            faithFallback = false;
            faithResolved = true;
            return;
        }
        if (faithResolved) return;
        if (faithFallback) return;
        if (escapeInput?.IsCurrent != true)
        {
            faithFallback = true;
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            // Emergency avoidance and game-owned knockback retain priority. An
            // earlier semantic request cannot cancel their newer mover command.
            faithMoving = false;
            faithHolding = false;
            ReleaseSemantic(SemanticOwner.Faith);
            return;
        }
        if (!faithDestination.HasValue && DateTime.UtcNow < nextFaithSearch) return;

        var center = new PlanPoint(boss.X, boss.Z);
        var player = new PlanPoint(Core.Me.X, Core.Me.Z);
        var hazards = AvoidanceManager.Avoids.Where(a => a.IsValid && !a.ShouldIgnore).ToArray();
        Vector3 World(PlanPoint p) => new(p.X, boss.Y, p.Y);
        bool Safe(PlanPoint p)
        {
            var point = World(p);
            return point.Distance2D(SerGrinnauxArenaCenter) < 19f &&
                !hazards.Any(a => a.IsPointInAvoid(point));
        }
        bool SafeSegment(PlanPoint a, PlanPoint b)
            => VaultSegmentSafety.Clear(a, b, Safe);
        var chosen = VaultKnockbackPlan.Select(player, center, faithDestination, Safe, SafeSegment);
        if (!chosen.HasValue)
        {
            // Center-seeking alone landed Rowan beside a tear in the live capture.
            // With no safe route and landing, yield to earlier hazards and retry
            // as geometry changes rather than issuing an unverified destination.
            ReleaseFaithPosition();
            if (!faithWaitingLogged) Logger.Information("[Vault] Faith Unmoving waiting for a safe staging and landing path.");
            faithWaitingLogged = true;
            // An impossible overlap can otherwise rescan the arena every frame.
            // Native avoidance remains active while we retry as hazards change.
            nextFaithSearch = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }

        faithWaitingLogged = false;
        nextFaithSearch = DateTime.MinValue;
        faithDestination = chosen;
        CapabilityManager.Update(faithHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Vault Faith Unmoving staging");
        CapabilityManager.Update(faithHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Vault Faith Unmoving staging");
        if (!faithAcquired)
        {
            faithAcquired = true;
            Logger.Information("[Vault] Faith Unmoving staging acquired at " + World(chosen.Value) + ".");
        }
        faithMoving = PlanPoint.Distance(player, chosen.Value) > .5f;
        if (faithMoving)
        {
            faithHolding = false;
            RequestSemantic(SemanticOwner.Faith, World(chosen.Value));
        }
        else if (!faithHolding)
        {
            RequestSemantic(SemanticOwner.Faith, World(chosen.Value));
            faithHolding = true;
        }
    }

    private void ReleaseFaithPosition()
    {
        ReleaseSemantic(SemanticOwner.Faith);
        if (faithAcquired)
        {
            CapabilityManager.Clear(faithHandle, CapabilityFlags.Movement, "Vault Faith Unmoving released");
            CapabilityManager.Clear(faithHandle, CapabilityFlags.GapCloser, "Vault Faith Unmoving released");
            Logger.Information("[Vault] Faith Unmoving staging released.");
        }
        faithAcquired = faithMoving = faithHolding = false;
        faithDestination = null;
    }

    private void ReleaseFaith()
    {
        ReleaseFaithPosition();
        faithFallback = false;
        faithResolved = false;
        faithWaitingLogged = false;
        faithUntil = DateTime.MinValue;
        nextFaithSearch = DateTime.MinValue;
        faithCaster = 0;
        faithLastCastingPosition = null;
    }
}
