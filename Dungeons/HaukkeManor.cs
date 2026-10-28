using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns Haukke boss telegraphs and bounded gaze facing. OrderBot retains the
/// authored key/door route and add priorities; native avoidance owns travel.
/// </summary>
public class HaukkeManor : AbstractDungeon
{
    // Name IDs remain stable across the reworked territory. Helpers are collected
    // without targetability filtering because the gaze sentry cannot be attacked.
    private const uint Claviger = 423, Steward = 427, Jester = 426, Amandine = 422;
    private static HaukkeManor active;
    private bool? savedSideStepEnabled;
    private bool? savedAutoFacing;
    private readonly CapabilityManagerHandle gazeFacingHandle = CapabilityManager.CreateNewHandle();
    private DateTime gazeUntil;
    private Vector3 gazeOrigin;
    private bool gazeArmed;
    private bool gazeTurnAttempted;
    private bool gazeTurnOwned;
    private DateTime gazeTurnStarted;
    private DateTime nextGazeCastCancel;

    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1040 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter actor, uint action) => actor.IsValid && actor.IsAlive &&
        actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == action;

    /// <summary>Restores the previous SideStep state on stop, disable or hook replacement.</summary>
    public static void ResetActive()
    {
        active?.SetBossOwnership(false);
        active = null;
    }

    /// <summary>Releases boss ownership even when loading or death bypasses the encounter hook.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        // Aetherial Flow loads another floor without changing territory or
        // replacing DungeonManager's instance. Release inputs during that gap,
        // but retain this instance so it can reacquire after loading completes.
        // Stop/disable/replacement still call ResetActive explicitly.
        if (!InDuty()) { active.SetBossOwnership(false); active.ReleaseGaze(); }
        else { active.UpdateOwnership(); active.UpdateGaze(); }
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.HaukkeManor;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [28645];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Release the predecessor before assigning this instance so a stale exit
        // cannot restore SideStep over its successor after a hook replacement.
        ResetActive();
        active = this;
        // Installed Global2026.10.16 rows705/860 are type5:8y plus caster
        // reach. Historic October27 captures confirm their caster-centered circles.
        // Row28646 is type2 and already includes its full9y radius. The half-yalm
        // margin is clearance tolerance, not an extra model hitbox for type2.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => Casting(actor, 705) || Casting(actor, 860) || Casting(actor, 28646),
            radiusProducer: actor => actor.CastingSpellId == 28646 ? 9.5f : 8.5f + actor.CombatReach,
            locationProducer: actor => actor.Location);
        // Void Fire II855 belongs to its ground target, not the caster. All
        // geometric hazards share native avoidance's union and its movement owner.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => Casting(actor, 855), radiusProducer: _ => 5.5f,
            locationProducer: actor => actor.SpellCastInfo.CastLocation);
        // Petrifying Eye28648 is deliberately absent: its55y range is gaze reach,
        // not unsafe floor. Historic generic-circle handling repeatedly found no path.
        return Task.FromResult(false);
    }

    private void UpdateOwnership()
    {
        if (!Live()) { SetBossOwnership(false); return; }
        // Keep the lease while either second-room boss or final adds remain engaged. Only an engaged named boss can
        // acquire it; nearby corridor trash must retain SideStep. The40y/8y bounds
        // isolate the current room and floor without introducing a movement owner.
        if (savedSideStepEnabled != null) return;
        SetBossOwnership(GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.CanAttack &&
                actor.InCombat && actor.NpcId is Claviger or Steward or Jester or Amandine &&
                Math.Abs(actor.Y - Core.Me.Y) < 8 && actor.Distance2D() < 40));
    }

    private void SetBossOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Haukke] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            ReleaseGaze();
            Logger.Information("[Haukke] Boss ownership released; SideStep restored.");
        }
    }

    // Facing is a separate input channel from native path movement. Reserve only
    // its final1.5s plus1.5s impact grace; October28 captures placed status
    // application about1.2s after cast end. Earlier ownership could fight the
    // unavoidable Beguiling Mist attraction or starve healing. Do not move, stop
    // or follow an NPC to solve the gaze. Reapply after the forced turn releases.
    // A stationary turn additionally uses the normal rotation input below;
    // SetFacing alone did not prevent the first gaze in the October28 retry.
    private void UpdateGaze()
    {
        if (!OwnsHazards()) { ReleaseGaze(); return; }
        var now = DateTime.UtcNow;
        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => Casting(actor, 28648) && Math.Abs(actor.Y - Core.Me.Y) < 8);
        if (caster != null && caster.SpellCastInfo.RemainingCastTime.TotalMilliseconds <= 1500)
        {
            gazeOrigin = caster.Location;
            gazeUntil = now + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1500);
            gazeArmed = true;
        }
        if (!gazeArmed || now >= gazeUntil) { ReleaseGaze(); return; }
        if (savedAutoFacing == null)
        {
            savedAutoFacing = GameSettingsManager.FaceTargetOnAction;
            Logger.Information("[Haukke] Gaze facing acquired; native avoidance retains travel.");
        }
        // An already-running routine action can complete between pulses. Prevent
        // client auto-facing too; RunAsync holds new routine actions only during
        // this bounded semantic window, then restores the exact previous setting.
        GameSettingsManager.FaceTargetOnAction = false;
        CapabilityManager.Update(gazeFacingHandle, CapabilityFlags.Facing, 250,
            "Haukke Petrifying Eye look-away");
        // The October28 Black Mage capture started Fire just before this hold.
        // Its cast lock delayed normal turn input until only9ms remained, causing
        // petrification despite a locally correct heading. Cancel the in-flight
        // cast using RB's supported action API, then let the native control gate
        // confirm input is available. Never bypass charm or force-movement flags.
        // A200ms retry bounds transient cancellation without flooding the client;
        // it exists only inside the existing1.5s gaze window and impact grace.
        if (Core.Me.IsCasting && now >= nextGazeCastCancel)
        {
            ActionManager.StopCasting();
            nextGazeCastCancel = now + TimeSpan.FromMilliseconds(200);
            Logger.Information("[Haukke] Cancelling active cast for gaze turn input.");
        }
        var away = Core.Me.Location - gazeOrigin;
        if (Math.Abs(away.X) + Math.Abs(away.Z) > 0.1f)
            Core.Me.SetFacing((float)Math.Atan2(away.X, away.Z));
        UpdateGazeTurn(now);
    }

    private void UpdateGazeTurn(DateTime now)
    {
        // October28 correction1 held a locally correct heading through impact,
        // yet the stationary gaze petrified; the later movement-assisted gazes
        // passed. Brief native rotation makes the facing change a normal client
        // input. This is a testable correction, not proof of server packet behavior.
        // The existing gaze owner owns this facing input too; never translate,
        // stop Navigator, use a timed stop callback, or clear another input bit.
        bool travelHasPriority = MovementManager.IsMoving || MovementManager.GenerallyOutOfControl ||
            AvoidanceManager.IsRunningOutOfAvoid ||
            AvoidanceManager.Avoids.Any(a => a.IsValid && a.IsPointInAvoid(Core.Me.Location));
        if (gazeTurnOwned && (travelHasPriority || (now - gazeTurnStarted).TotalMilliseconds >= 150))
            ReleaseGazeTurn();
        if (gazeTurnAttempted || travelHasPriority) return;
        // A predecessor's native stop can be deferred by RB's 100ms input gate.
        // Do not inherit its still-set bit or claim an existing turning input.
        const MovementDirection turns = MovementDirection.RotateLeft | MovementDirection.RotateRight;
        if ((MovementManager.ActiveInputControl & turns) != MovementDirection.None) return;
        gazeTurnAttempted = true;
        gazeTurnOwned = true;
        gazeTurnStarted = now;
        // 150ms exceeds that gate while keeping the turn well inside the away
        // hemisphere. Every pulse still reapplies the measured away heading.
        MovementManager.Move(MovementDirection.RotateLeft);
        Logger.Information("[Haukke] Stationary gaze rotation input acquired.");
    }

    private void ReleaseGazeTurn()
    {
        if (!gazeTurnOwned) return;
        MovementManager.MoveStop(MovementDirection.RotateLeft);
        gazeTurnOwned = false;
        Logger.Information("[Haukke] Stationary gaze rotation input released.");
    }

    private void ReleaseGaze()
    {
        ReleaseGazeTurn();
        gazeTurnAttempted = false;
        nextGazeCastCancel = DateTime.MinValue;
        if (savedAutoFacing != null)
        {
            GameSettingsManager.FaceTargetOnAction = savedAutoFacing.Value;
            savedAutoFacing = null;
            CapabilityManager.Clear(gazeFacingHandle, CapabilityFlags.Facing, "Haukke gaze ended");
            Logger.Information("[Haukke] Gaze facing released; routine restored.");
        }
        gazeArmed = false;
        gazeUntil = DateTime.MinValue;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!InDuty()) { SetBossOwnership(false); ReleaseGaze(); return false; }
        if (!ReferenceEquals(active, this)) return false;
        UpdateOwnership();
        UpdateGaze();
        // Native avoidance keeps priority over facing/rotation if telegraphs
        // overlap. Outside the brief gaze hold normal healing and combat run.
        if (gazeArmed && !AvoidanceManager.Avoids.Any(a => a.IsValid && a.IsPointInAvoid(Core.Me.Location)))
            return true;
        await TankBusterSpells();
        return false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetBossOwnership(false);
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
