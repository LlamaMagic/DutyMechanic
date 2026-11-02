using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Provides preliminary normal-mode support for the level 63 trial The Pool of Tribute.
/// </summary>
/// <remarks>
/// The companion profile owns entry, Susano and add targeting, and completion. The encounter
/// coordinator owns the blade interaction, sword-finish and Seasplitter movement through one
/// input channel; the profile and AutoParty requester hand off through their existing adapter.
/// SideStep retains the shared Dark Cloud line and unmodeled signals. Yata-no-Kagami and
/// Brightstorm still require evidence beyond harmful cast geometry.
/// </remarks>
public sealed class PoolOfTribute : AbstractDungeon
{

    // The Action table defines the damaging helper as a six-yalm target circle. The standard
    // half-yalm margin covers actor-center and navigation stopping tolerance without consuming
    // excessive space when all eight party circles are visible together.
    private const float RasenKaikyoAvoidRadius = 6.5f;

    // Every rectangle expands the authored width, forward length, and rear edge by 0.5 yalms.
    // AvoidanceHelpers interprets yOffset as the back edge and length + yOffset as the front edge.
    private const float RectangleRearMargin = -0.5f;

    private DateTime clashTraceUntilUtc, nextClashTraceUtc;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.ThePoolOfTribute;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.Stormsplitter];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [EnemyAction.Ukehi];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // These three casts have encounter-local handling below. Suppressing only their SideStep
        // entries prevents duplicate or inverted interpretations while leaving SideStep available
        // for any unmodeled normal-mode helper that appears during the preliminary live run.
        SideStep.Override(EnemyAction.RasenKaikyoAoe);
        SideStep.Override(EnemyAction.Seasplitter);
        SideStep.Override(EnemyAction.Stormsplitter);

        // The 2026-11-02 synced capture and the earlier 2026-10-21 capture both show 8222
        // helpers placed at the selected ground circles while CastLocation is zero. Using
        // CastLocation collapses every avoid to the arena origin and leaves the real circles
        // unprotected; the helper's own location supplies each circle's ground origin.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsEncounterCombatActive,
            objectSelector: caster => caster.IsValid && caster.IsCasting &&
                caster.CastingSpellId == EnemyAction.RasenKaikyoAoe,
            radiusProducer: _ => RasenKaikyoAvoidRadius,
            locationProducer: caster => caster.Location,
            priority: AvoidancePriority.High));

        // Stormsplitter is a 4-by-20-yalm line tankbuster. Its target must hold position and use the
        // mitigation path below; every other player avoids the line so the targeted tank cannot be
        // forced to rotate or drag the cleave through the party.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsEncounterCombatActive,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.Stormsplitter &&
                caster.SpellCastInfo.TargetId != Core.Player.ObjectId,
            width: 5.0f,
            length: 21.0f,
            yOffset: RectangleRearMargin,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        PoolOfTributeCoordinator.Reset();
        clashTraceUntilUtc = nextClashTraceUtc = DateTime.MinValue;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        CaptureClashMovement();
        // Selective overrides give the three registered normal-only casts one geometry owner. Keep
        // SideStep enabled for the shared Dark Cloud line and any other preliminary telegraph.
        SidestepPlugin.Enabled = true;

        // The lotus can be available while Susano is untargetable and before the
        // sword is visible; the coordinator gates itself by this trial's lifecycle.
        if (PoolOfTributeCoordinator.Tick())
        {
            return true;
        }

        if (!IsEncounterActive())
        {
            return false;
        }

        if (await TankBusterSpells())
        {
            return true;
        }

        return await DamageMitigationSpells();
    }

    // The 2026-11-02 live report described continuous wall-running during the
    // lotus phase, but the failure-only movement buffer did not cover that window.
    // Sample throughout the trial so post-avoidance drift outside the lotus phase
    // remains visible. A destination alone does not prove the current input owner.
    private void CaptureClashMovement()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || Core.Player == null) return;
        DateTime now = DateTime.UtcNow;
        if (now < nextClashTraceUtc) return;
        nextClashTraceUtc = now.AddSeconds(1);
        try
        {
            var shadows = GameObjectManager.GetObjectsOfType<ff14bot.Objects.GameObject>(true, false)
                .Where(actor => actor.IsValid && actor.IsVisible && actor.NpcId == 2008185).ToArray();
            if (shadows.Length > 0) clashTraceUntilUtc = now.AddSeconds(5);
            if (!DutyManager.InInstance || WorldManager.ZoneId != (uint)Data.ZoneId.ThePoolOfTribute) return;
            var members = PartyManager.VisibleMembers.Where(member => member.IsInObjectManager)
                .Select(member => member.BattleCharacter).Where(member => member != null && member.IsValid)
                .Select(member => new PoolOfTributeClashPolicy.Member(member.ObjectId, member.ClassLevel,
                    ClassJobRoles.Tanks.Contains(member.CurrentJob), member.IsAlive)).ToArray();
            var election = PoolOfTributeClashPolicy.Select(members, (int)PartyManager.NumMembers);
            string destination = Navigator.PlayerMover is SlideMover slide ? slide.LastMoveLocation.ToString() : "unavailable";
            var target = Core.Player.CurrentTarget;
            Logger.Information($"[PoolOfTribute] CLASH_TRACE utc={now:O} player={Core.Player.Location} " +
                $"moving={MovementManager.IsMoving} mover={Navigator.PlayerMover?.GetType().Name ?? "none"} " +
                $"lastMoveCommand={destination} escaping={AvoidanceManager.IsRunningOutOfAvoid} " +
                $"target=0x{target?.ObjectId ?? 0:X8} targetable={target?.IsTargetable ?? false} " +
                $"shadows={shadows.Length} targetable={shadows.Count(actor => actor.IsTargetable)} " +
                $"partyMode={election.PartyMode} blocker=0x{election.BlockerId.GetValueOrDefault():X8} " +
                $"visibleMembers={members.Length} partyMembers={PartyManager.NumMembers} " +
                $"input=({PoolOfTributeCoordinator.InputSnapshot})");
        }
        catch (Exception ex)
        {
            // Optional telemetry must never interrupt combat if an object becomes invalid.
            nextClashTraceUtc = now.AddSeconds(30);
            Logger.Information("[PoolOfTribute] CLASH_TRACE unavailable: " + ex.GetType().Name);
        }
    }

    private static bool IsEncounterActive() =>
        WorldManager.ZoneId == (uint)Data.ZoneId.ThePoolOfTribute &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsVisible && actor.IsAlive && IsEncounterActor(actor));

    private static bool IsEncounterCombatActive() => Core.Player.InCombat && IsEncounterActive();

    private static bool IsEncounterActor(BattleCharacter actor) =>
        actor.NpcId is EnemyNpc.Susano or EnemyNpc.AmaNoIwato or EnemyNpc.AmeNoMurakumo;

    private static class EnemyNpc
    {
        // The OrderBot profile uses these normal-duty actors to keep Susano persistent while giving
        // the stone and sword higher target weight during their respective failure checks.
        internal const uint Susano = 6221;
        internal const uint AmaNoIwato = 6224;
        internal const uint AmeNoMurakumo = 6225;
    }

    private static class EnemyAction
    {
        // The normal boss sequence occupies action IDs 8220-8235. The near-duplicate 8236+ sequence
        // belongs to TerritoryType 677 and adds Extreme-only mechanics such as Levinbolt and
        // Churning Deep. Shared helper actions outside this block remain owned by SideStep so an
        // override cannot leak into another territory later in the same RB session.

        // The damaging Rasen Kaikyo helper exposes the visible six-yalm ground circle. The boss's
        // preceding 8221 cast carries choreography but no radius and must not publish a second avoid.
        internal const uint RasenKaikyoAoe = 8222;

        // Seasplitter's paired helpers define the safe band's common axis after Yata-no-Kagami.
        // SideStep must remain overridden because cast type 4 otherwise treats that safe band as a
        // damaging rectangle and drives the player toward the arena edge.
        internal const uint Seasplitter = 8232;

        // Stormsplitter is the phase-three cast-bar tankbuster. Assail (8220) has no cast bar, so it
        // cannot safely trigger proactive mitigation through the shared cast-state helper.
        internal const uint Stormsplitter = 8227;

        // Ukehi 8230 is the four-second raidwide cast. Action 8231 is its immediate resolution and
        // is intentionally omitted because it arrives too late for proactive party mitigation.
        internal const uint Ukehi = 8230;
    }
}
