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
    private readonly VaultSpreadState execution = new();
    private bool executionAmbiguousReported;
    private readonly CapabilityManagerHandle executionHandle = CapabilityManager.CreateNewHandle();
    private bool executionFallback, executionMoving, executionAcquired, executionWaitingLogged;
    private DateTime nextExecutionSearch;
    private PlanPoint? executionDestination;

    // Captures put the five-yalm landing roughly 5.6 seconds after lock-on 32.
    // Retain a conservative 7.6-second window even if the visual disappears;
    // a lingering visual cannot repeatedly renew the same delivery.
    private static bool ExecutionMarker(BattleCharacter a) => a != null && a.IsValid &&
        a.VfxContainer.IsValid && a.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 32);

    private void UpdateExecutionSpread()
    {
        if (!bossEncounterActive || WorldManager.SubZoneId != 1570)
        {
            ResetExecutionSpread();
            return;
        }
        var members = PartyManager.AllMembers.Select(p => p.ObjectId).ToHashSet();
        var marked = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.ObjectId != Core.Me.ObjectId && members.Contains(a.ObjectId) && ExecutionMarker(a))
            .Select(a => a.ObjectId).ToList();
        if (ExecutionMarker(Core.Me)) marked.Add(Core.Me.ObjectId);
        var previous = execution.Target;
        bool wasResolved = execution.Resolved;
        execution.Observe(DateTime.UtcNow, marked);
        if (previous == 0 && execution.Target != 0)
            Logger.Information("[Vault] Execution spread target {0}.", execution.Target);
        if (!wasResolved && execution.Resolved)
            Logger.Information("[Vault] Execution spread delivery window closed.");
        if (execution.Target == 0 && marked.Count > 1 && !executionAmbiguousReported)
        {
            executionAmbiguousReported = true;
            Logger.Information("[Vault] Execution spread marker is ambiguous; no actor was selected.");
        }
        if (marked.Count == 0) executionAmbiguousReported = false;
    }

    private void ResetExecutionSpread()
    {
        ReleaseExecutionPosition();
        execution.Reset();
        executionAmbiguousReported = false;
        executionFallback = executionWaitingLogged = false;
        nextExecutionSearch = DateTime.MinValue;
    }

    private void UpdateExecutionMovement()
    {
        if (!execution.Active(DateTime.UtcNow))
        {
            ReleaseExecutionPosition();
            executionFallback = executionWaitingLogged = false;
            return;
        }
        if (executionFallback) return;
        if (escapeInput?.IsCurrent != true)
        {
            ReleaseExecutionPosition();
            executionFallback = true;
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseExecutionPosition();
            return;
        }
        if (!executionDestination.HasValue && DateTime.UtcNow < nextExecutionSearch) return;
        var ids = PartyManager.AllMembers.Select(p => p.ObjectId).ToHashSet();
        var others = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.ObjectId != Core.Me.ObjectId && ids.Contains(a.ObjectId)).ToArray();
        if (execution.Target != Core.Me.ObjectId && others.All(a => a.ObjectId != execution.Target))
        {
            ReleaseExecutionPosition();
            return;
        }
        var participants = others.Where(a => execution.Target == Core.Me.ObjectId || a.ObjectId == execution.Target)
            .Select(a => new PlanPoint(a.X, a.Z)).ToArray();
        var player = new PlanPoint(Core.Me.X, Core.Me.Z);
        var hazards = AvoidanceManager.Avoids.Where(a => a.IsValid && !a.ShouldIgnore).ToArray();
        Vector3 World(PlanPoint p) => new(p.X, Core.Me.Y, p.Y);
        bool Safe(PlanPoint p) => World(p).Distance2D(SerAdelphelArenaCenter) < 19f &&
            !hazards.Any(a => a.IsPointInAvoid(World(p)));
        bool Segment(PlanPoint a, PlanPoint b) => VaultSegmentSafety.Clear(a, b, Safe);
        var chosen = VaultSpreadPlan.Select(player, participants, executionDestination, Safe, Segment);
        if (!chosen.HasValue)
        {
            ReleaseExecutionPosition();
            if (!executionWaitingLogged) Logger.Information("[Vault] Execution spread waiting for a safe sphere path.");
            executionWaitingLogged = true;
            nextExecutionSearch = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }
        if (!RequestSemantic(SemanticOwner.Execution, World(chosen.Value)))
        {
            ReleaseExecutionPosition();
            return;
        }
        executionWaitingLogged = false;
        nextExecutionSearch = DateTime.MinValue;
        executionDestination = chosen;
        CapabilityManager.Update(executionHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Vault Execution spread");
        CapabilityManager.Update(executionHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Vault Execution spread");
        if (!executionAcquired)
        {
            executionAcquired = true;
            Logger.Information("[Vault] Execution spread path acquired at " + World(chosen.Value) + ".");
        }
        executionMoving = PlanPoint.Distance(player, chosen.Value) > .5f;
    }

    private void ReleaseExecutionPosition()
    {
        ReleaseSemantic(SemanticOwner.Execution);
        if (executionAcquired)
        {
            CapabilityManager.Clear(executionHandle, CapabilityFlags.Movement, "Vault Execution spread released");
            CapabilityManager.Clear(executionHandle, CapabilityFlags.GapCloser, "Vault Execution spread released");
            Logger.Information("[Vault] Execution spread path released.");
        }
        executionAcquired = executionMoving = false;
        executionDestination = null;
    }
}
