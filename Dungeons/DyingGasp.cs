using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>Owns trial readiness and boss-wide capability lifetimes for normal Hades.</summary>
public sealed class DyingGasp : AbstractDungeon
{
    private static DyingGasp current;
    private bool borrowedSideStep, previousSideStep, gapCloserLeased;
    private bool unsupportedMover;
    private ChrysalisMovement movement;
    private readonly CapabilityManagerHandle wailHandle = CapabilityManager.CreateNewHandle();
    private DateTime wailUntil;
    private bool wailCastSeen, wailOwned;
    private static readonly Vector3 WailEast = new(115, 0, 100);

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)847;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        // The base entry removes all avoids. Drop old handles so the existing
        // pulse registers its geometry again after that reset.
        DyingGaspMechanics.Reset();
        current = this;
        CheckLifecycle();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // A party ready check can arrive before combat or a target exists.
        await UsefulTasks.HandleReadyCheck();
        CheckLifecycle();
        // During targetless add waves GrindSafe repeatedly refreshed its hotspot
        // while native escape was active, leaving the player stationary through
        // two intersecting Ancient Aero lines. Let escape own that scheduling window.
        return movement?.IsEscaping == true || wailOwned && movement?.IsCurrent == true &&
            !AvoidanceManager.IsRunningOutOfAvoid && Core.Me.Location.Distance2D(WailEast) > 1.5f;
    }

    /// <summary>Renews the encounter's leases on the existing bot-thread pulse.</summary>
    public static void CheckLifecycle()
    {
        if (current == null)
            return;
        if (!TreeRoot.IsRunning || !DutyManager.InInstance || WorldManager.ZoneId != 847)
        {
            current.Release();
            return;
        }

        // Retain suppression through the untargetable add phases and same-visit wipes.
        if (!current.borrowedSideStep && SidestepPlugin != null)
        {
            current.previousSideStep = SidestepPlugin.Enabled;
            current.borrowedSideStep = true;
        }
        if (SidestepPlugin != null && SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = false;

        if (Core.Me != null && Core.Me.IsAlive && !CommonBehaviors.IsLoading)
        {
            if (current.movement != null && !current.movement.IsCurrent)
            {
                current.movement.Detach();
                current.movement = null;
            }
            if (current.movement == null && !current.unsupportedMover)
            {
                if (Navigator.PlayerMover?.GetType() == typeof(SlideMover))
                {
                    // Borrow the existing channel so native escape input is released
                    // and ordinary waypoints cannot immediately cross active avoids.
                    current.movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: OrdinarySegmentClear);
                    Navigator.PlayerMover = current.movement;
                }
                else
                    current.unsupportedMover = true;
            }
            current.UpdateWail();
            current.movement?.Update();
            CapabilityManager.Update(current.CapabilityHandle, CapabilityFlags.GapCloser,
                TimeSpan.FromSeconds(1), "Dying Gasp boss safety");
            current.gapCloserLeased = true;
        }
        else if (current.gapCloserLeased)
        {
            current.movement?.Detach();
            current.movement = null;
            current.ReleaseWail();
            CapabilityManager.Clear(current.CapabilityHandle, CapabilityFlags.GapCloser, "Dying Gasp interrupted");
            current.gapCloserLeased = false;
        }
    }

    private void UpdateWail()
    {
        var now = DateTime.UtcNow;
        // The opposite Wail can push players off the east edge; it is not handled yet.
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.BaseId == 0x294A && a.IsCasting && a.CastingSpellId == 16742);
        if (boss != null && !wailCastSeen)
        {
            wailCastSeen = true;
            // The observed 16742 cast pushed every sampled party member 30y west.
            // Its damage/knockback followed the cast by about 1.9s. Starting near
            // x=115 landed the party safely near x=85; the west-side player fell.
            wailUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2.5);
        }
        if (boss == null)
            wailCastSeen = false;
        if (MovementManager.ForceMoved)
        {
            // Once the knockback owns input, do not reacquire staging after landing.
            wailUntil = DateTime.MinValue;
            ReleaseWail();
            return;
        }
        if (now >= wailUntil || movement?.IsCurrent != true)
        {
            ReleaseWail();
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || !OrdinarySegmentClear(WailEast))
        {
            // Native emergency escape takes precedence; retry staging when the
            // currently known path and destination are clear.
            ReleaseWail();
            return;
        }
        foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
            CapabilityManager.Update(wailHandle, flag, TimeSpan.FromMilliseconds(500), "Dying Gasp Wail staging");
        movement.Request(WailEast, 1.5f);
        if (!wailOwned)
            Logger.Information("[DyingGasp] Wail staging from {0} to {1}", Core.Me.Location, WailEast);
        wailOwned = true;
    }

    private void ReleaseWail()
    {
        if (!wailOwned)
            return;
        movement?.Request(null, 0);
        CapabilityManager.Clear(wailHandle);
        Logger.Information("[DyingGasp] Wail staging released at {0}", Core.Me?.Location);
        wailOwned = false;
    }

    private static bool OrdinarySegmentClear(Vector3 destination)
    {
        var start = Core.Me.Location;
        float dx = destination.X - start.X, dz = destination.Z - start.Z;
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / 0.5f));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            var point = new Vector3(start.X + dx * t, start.Y, start.Z + dz * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)))
                return false;
        }
        return true;
    }

    private void Release()
    {
        ReleaseWail();
        wailUntil = DateTime.MinValue;
        wailCastSeen = false;
        movement?.Detach();
        movement = null;
        unsupportedMover = false;
        if (gapCloserLeased)
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Dying Gasp ended");
        gapCloserLeased = false;
        if (borrowedSideStep && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = previousSideStep;
        borrowedSideStep = false;
    }

    /// <summary>Releases the trial's own leases when the dungeon hook is bypassed.</summary>
    public static void ResetActive()
    {
        current?.Release();
        current = null;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        // The shared exit hook enables SideStep before this callback, so restore
        // the captured value here even when that base hook changed it first.
        if (borrowedSideStep && SidestepPlugin != null)
            SidestepPlugin.Enabled = previousSideStep;
        borrowedSideStep = false;
        ResetActive();
        return Task.FromResult(false);
    }
}
