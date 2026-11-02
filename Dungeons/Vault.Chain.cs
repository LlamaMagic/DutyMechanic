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
    private readonly CapabilityManagerHandle chainHandle = CapabilityManager.CreateNewHandle();
    private bool chainFallback, chainMoving, chainAcquired, chainWaitingLogged;
    private DateTime nextChainSearch;
    private DateTime chainFirstSeen;
    private PlanPoint? chainDestination;

    private void UpdateChain()
    {
        if (!bossEncounterActive || WorldManager.SubZoneId != 1572 || !UpdateChainSeparation())
        {
            ReleaseChain();
            return;
        }
        if (chainFallback) return;
        if (escapeInput?.IsCurrent != true)
        {
            ReleaseChainPosition();
            chainFallback = true;
            return;
        }
        if (chainState.PartnerId == 0)
        {
            // The two party auras need not become readable in the same pulse.
            // Briefly wait for the unique partner before retaining the legacy
            // fallback; never select an arbitrary aura-bearing actor.
            if (chainFirstSeen == DateTime.MinValue) chainFirstSeen = DateTime.UtcNow;
            if (DateTime.UtcNow - chainFirstSeen < TimeSpan.FromMilliseconds(500)) return;
            ReleaseChainPosition();
            chainFallback = true;
            Logger.Information("[Vault] Holy Chain partner unavailable; using legacy separation fallback.");
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseChainPosition();
            return;
        }
        if (!chainDestination.HasValue && DateTime.UtcNow < nextChainSearch) return;
        var partner = GameObjectManager.GetObjectByObjectId(chainState.PartnerId) as BattleCharacter;
        if (partner == null || !partner.IsValid || !partner.IsAlive || !partner.HasAura(BurningChainsAura))
        {
            ReleaseChainPosition();
            return;
        }
        var player = new PlanPoint(Core.Me.X, Core.Me.Z);
        var paired = new PlanPoint(partner.X, partner.Z);
        var hazards = AvoidanceManager.Avoids.Where(a => a.IsValid && !a.ShouldIgnore).ToArray();
        Vector3 World(PlanPoint p) => new(p.X, Core.Me.Y, p.Y);
        bool Safe(PlanPoint p) => Math.Abs(p.X) < 19.5f && Math.Abs(p.Y - 4f) < 19.5f &&
            !hazards.Any(a => a.IsPointInAvoid(World(p)));
        bool Segment(PlanPoint a, PlanPoint b) => VaultSegmentSafety.Clear(a, b, Safe);
        var chosen = VaultChainPlan.Select(player, paired, chainState.RequiredRadius, chainDestination, Safe, Segment);
        if (!chosen.HasValue)
        {
            // Knight lanes and flames resolve sooner than the 15s chain expiry.
            // Do not drive through them when no shared path is presently safe;
            // release the request and re-evaluate after the earlier hazard.
            ReleaseChainPosition();
            if (!chainWaitingLogged) Logger.Information("[Vault] Holy Chain waiting for a safe shared path.");
            chainWaitingLogged = true;
            nextChainSearch = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }
        chainWaitingLogged = false;
        nextChainSearch = DateTime.MinValue;
        chainFirstSeen = DateTime.MinValue;
        if (!RequestSemantic(SemanticOwner.Chain, World(chosen.Value)))
        {
            ReleaseChainPosition();
            return;
        }
        chainDestination = chosen;
        CapabilityManager.Update(chainHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Vault Holy Chain separation");
        CapabilityManager.Update(chainHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Vault Holy Chain separation");
        if (!chainAcquired)
        {
            chainAcquired = true;
            Logger.Information((PlanPoint.Distance(chosen.Value, paired) >= chainState.RequiredRadius + .5f ? "[Vault] Holy Chain path acquired at " : "[Vault] Holy Chain safe progress acquired at ") + World(chosen.Value) + ".");
        }
        chainMoving = PlanPoint.Distance(player, chosen.Value) > .5f;
    }

    private void ReleaseChainPosition()
    {
        ReleaseSemantic(SemanticOwner.Chain);
        if (chainAcquired)
        {
            CapabilityManager.Clear(chainHandle, CapabilityFlags.Movement, "Vault Holy Chain released");
            CapabilityManager.Clear(chainHandle, CapabilityFlags.GapCloser, "Vault Holy Chain released");
            Logger.Information("[Vault] Holy Chain path released.");
        }
        chainAcquired = chainMoving = false;
        chainDestination = null;
    }

    private void ReleaseChain()
    {
        ReleaseChainPosition();
        chainFallback = chainWaitingLogged = false;
        nextChainSearch = DateTime.MinValue;
        chainFirstSeen = DateTime.MinValue;
        chainState.Reset();
    }
}
