using System;
using System.Linq;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using Clio.Utilities;
using PlanPoint = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

public partial class Vault
{
    private static Vault active;
    private VaultInputOwner escapeInput;
    private bool reportedUnsupportedMover, lostMover;
    private bool bossEncounterActive;
    private bool? savedSideStepEnabled;
    private enum SemanticOwner { None, Faith, Chain, Execution }
    private SemanticOwner semanticOwner;

    private bool RequestSemantic(SemanticOwner owner, Vector3 destination)
    {
        if (escapeInput?.IsCurrent != true || semanticOwner != SemanticOwner.None && semanticOwner != owner)
            return false;
        semanticOwner = owner;
        escapeInput.Request(destination, .5f);
        return true;
    }

    private void ReleaseSemantic(SemanticOwner owner)
    {
        if (semanticOwner != owner) return;
        escapeInput?.Request(null, 0);
        semanticOwner = SemanticOwner.None;
    }

    // Captured boss base IDs distinguish the transformed actors from same-name
    // helpers. Retain ownership through untargetable phases, but release it for
    // loading, death, stop, or travel outside the corresponding arena.
    private static bool BossEncounterActive()
    {
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            WorldManager.ZoneId != 1066 || Core.Me == null || !Core.Me.IsValid ||
            !Core.Me.IsAlive || !Core.Me.InCombat)
            return false;
        uint zone = WorldManager.SubZoneId;
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a =>
            a.IsValid && a.IsAlive && Math.Abs(a.Y - Core.Me.Y) < 10 &&
            (zone == 1570 && a.BaseId is 0x104E or 0x1051 ||
             zone == 1571 && a.BaseId is 0x1053 or 0x1054 ||
             zone == 1572 && a.BaseId == 0x1056));
    }

    /// <summary>Releases this encounter's input and capability leases on stop or replacement.</summary>
    public static void ResetActive()
    {
        active?.ReleaseFaith();
        active?.ReleaseChain();
        active?.ResetExecutionSpread();
        active?.ClearTimedCasts();
        active?.marchActors.Clear();
        active?.ReleaseSideStep();
        active?.ReleaseEscapeInput();
        active = null;
    }

    /// <summary>Maintains ownership through yielding profile and combat coroutines.</summary>
    public static void CheckLifecycle() => active?.PulseMovement();

    private void PulseMovement()
    {
        bossEncounterActive = BossEncounterActive();
        if (!bossEncounterActive)
        {
            ReleaseFaith();
            ReleaseChain();
            ResetExecutionSpread();
            ClearTimedCasts();
            marchActors.Clear();
            ReleaseSideStep();
            ReleaseEscapeInput();
            return;
        }
        if (escapeInput != null && !escapeInput.IsCurrent)
        {
            // A foreign replacement is the successor. Unwrap only our still-
            // installed surface and never acquire it again during this encounter.
            escapeInput.Detach();
            escapeInput = null;
            lostMover = true;
            ReleaseSideStep();
        }
        if (escapeInput == null)
        {
            // RB 1.0.941 can leave SlideMover input held after native escape.
            // Borrow only the captured mover; never stack onto another controller.
            if (lostMover || Navigator.PlayerMover?.GetType() != typeof(SlideMover) || Navigator.NavigationProvider == null)
            {
                if (!reportedUnsupportedMover)
                    Logger.Information("[Vault] Movement owner unavailable: another mover owns input.");
                reportedUnsupportedMover = true;
                UpdateFaith();
                UpdateChain();
                UpdateExecutionSpread();
                UpdateExecutionMovement();
                UpdateTimedCasts();
                UpdateMarchActors();
                return;
            }
            escapeInput = new VaultInputOwner(Navigator.PlayerMover, SafeOrdinaryWaypoint);
            Navigator.PlayerMover = escapeInput;
        }
        AcquireSideStep();
        UpdateTimedCasts();
        UpdateMarchActors();
        UpdateFaith();
        UpdateChain();
        UpdateExecutionSpread();
        UpdateExecutionMovement();
        escapeInput.Update();
    }

    private void ReleaseEscapeInput()
    {
        // A successor duty may already own the shared channel by the time a
        // delayed Vault cleanup runs. Unwrap our adapter, but never stop input
        // in a different active instance or while the client is loading.
        escapeInput?.Detach(CanRestoreSharedState());
        escapeInput = null;
        semanticOwner = SemanticOwner.None;
        reportedUnsupportedMover = false;
        lostMover = false;
    }

    // The actor scan is performed once per bot pulse; each native geometry
    // callback reads that snapshot instead of repeatedly enumerating game objects.
    private bool OwnsTelegraphs() => bossEncounterActive && TreeRoot.IsRunning &&
        savedSideStepEnabled.HasValue && escapeInput?.IsCurrent == true;

    private static bool CanRestoreSharedState() => VaultLeaseBoundary.MayRestore(
        CommonBehaviors.IsLoading, WorldManager.ZoneId);

    private void AcquireSideStep()
    {
        if (savedSideStepEnabled.HasValue || SidestepPlugin == null || escapeInput?.IsCurrent != true) return;
        savedSideStepEnabled = SidestepPlugin.Enabled;
        if (SidestepPlugin.Enabled) SidestepPlugin.Enabled = false;
        Logger.Information("[Vault] Boss telegraphs owned; SideStep suspended.");
    }

    private void ReleaseSideStep()
    {
        if (!savedSideStepEnabled.HasValue) return;
        // Do not overwrite a later explicit enable. Restore only the state that
        // this encounter actually suspended, then discard its lease.
        bool restored = savedSideStepEnabled.Value && SidestepPlugin != null &&
            !SidestepPlugin.Enabled && CanRestoreSharedState();
        if (restored)
            SidestepPlugin.Enabled = true;
        savedSideStepEnabled = null;
        Logger.Information(restored ? "[Vault] Boss ownership released; SideStep restored." :
            "[Vault] Boss ownership released; SideStep state unchanged.");
    }

    // Escape completion is not hazard expiry. Inspect the actual next waypoint
    // and its segment against active avoids, allowing navigation to offer a
    // safe detour instead of rejecting the final combat destination wholesale.
    private static bool SafeOrdinaryWaypoint(Vector3 waypoint)
    {
        var start = Core.Me.Location;
        if (Math.Abs(start.Y - waypoint.Y) > 2f) return false;
        var hazards = AvoidanceManager.Avoids.Where(a => a.IsValid && !a.ShouldIgnore).ToArray();
        return VaultSegmentSafety.Clear(new PlanPoint(start.X, start.Z), new PlanPoint(waypoint.X, waypoint.Z), p =>
            !hazards.Any(a => a.IsPointInAvoid(new Vector3(p.X, start.Y, p.Y))));
    }
}
