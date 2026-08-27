using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
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
/// The companion OrderBot profile owns entry, Susano targeting, the sword damage check, the
/// stone-prison target, and the blade-clash interaction; this handler deliberately performs no
/// interaction or target-lifecycle work. DutyMechanic owns the normal-only cast-time circles,
/// rectangles, and Seasplitter safe-band positioning while SideStep retains the shared Dark Cloud
/// line and any unmodeled signals. Yata-no-Kagami and Brightstorm are excluded because their
/// knockback and stack responses require evidence beyond harmful cast geometry.
/// </remarks>
public sealed class PoolOfTribute : AbstractDungeon
{
    private const string ActorWatchScope = "PoolOfTribute";

    // The Action table defines the damaging helper as a six-yalm target circle. The standard
    // half-yalm margin covers actor-center and navigation stopping tolerance without consuming
    // excessive space when all eight party circles are visible together.
    private const float RasenKaikyoAvoidRadius = 6.5f;

    // Every rectangle expands the authored width, forward length, and rear edge by 0.5 yalms.
    // AvoidanceHelpers interprets yOffset as the back edge and length + yOffset as the front edge.
    private const float RectangleRearMargin = -0.5f;

    // The 2026-08-27 normal-mode capture established that Seasplitter's two 8232 helpers share one
    // origin and face in opposite directions. Their common axis is the safe band, not harmful
    // geometry. A sub-yalm tolerance keeps the player near its middle without unnecessary movement
    // along the lane or relying on the helpers' zero CastLocation values.
    private const float SeasplitterCenterlineTolerance = 0.75f;

    private bool movingToSeasplitterCenterline;

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

        // Rasen Kaikyo's damaging helpers cast at each party member's selected ground location.
        // Use the cast location rather than following the target actor so a player who has already
        // left their telegraph cannot drag the registered hazard across the arena.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsEncounterCombatActive,
            objectSelector: caster => caster.IsValid && caster.IsCasting &&
                caster.CastingSpellId == EnemyAction.RasenKaikyoAoe,
            radiusProducer: _ => RasenKaikyoAvoidRadius,
            locationProducer: caster => caster.SpellCastInfo.CastLocation,
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
        StopSeasplitterMovement();
        LoggingHelpers.ClearActorSignalWatch(ActorWatchScope);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Selective overrides give the three registered normal-only casts one geometry owner. Keep
        // SideStep enabled for the shared Dark Cloud line and any other preliminary telegraph.
        SidestepPlugin.Enabled = true;

        if (!IsEncounterActive())
        {
            LoggingHelpers.ClearActorSignalWatch(ActorWatchScope);
            return false;
        }

        // This shared, compile-time-gated watch records targetability, targets, statuses, VFX,
        // tethers, and transforms for the three actors already established by the OrderBot profile.
        // It is observational only and supplies the missing evidence for marker-driven movement.
        LoggingHelpers.LogActorSignalChanges(ActorWatchScope, IsEncounterActor);

        if (await HandleSeasplitterAsync())
        {
            return true;
        }

        if (await TankBusterSpells())
        {
            return true;
        }

        return await DamageMitigationSpells();
    }

    /// <summary>
    /// Moves laterally to Seasplitter's safe centerline while preserving progress along the band.
    /// </summary>
    /// <returns><see langword="true"/> while the cast owns movement or holds the safe position.</returns>
    private async Task<bool> HandleSeasplitterAsync()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid && actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.Seasplitter);

        if (caster == null)
        {
            StopSeasplitterMovement();
            return false;
        }

        CapabilityManager.Update(
            CapabilityHandle,
            CapabilityFlags.Movement,
            caster.SpellCastInfo.RemainingCastTime,
            "Holding the middle of Seasplitter's safe band");

        Vector3 destination = ProjectOntoSeasplitterCenterline(
            Core.Player.Location,
            caster.Location,
            caster.Heading);

        if (Core.Player.Distance2D(destination) <= SeasplitterCenterlineTolerance)
        {
            StopSeasplitterMovement();
            await Coroutine.Yield();
            return true;
        }

        Navigator.PlayerMover.MoveTowards(destination);
        movingToSeasplitterCenterline = true;
        await Coroutine.Yield();
        return true;
    }

    private static Vector3 ProjectOntoSeasplitterCenterline(
        Vector3 playerLocation,
        Vector3 helperLocation,
        float helperHeading)
    {
        // RB's FFXIV heading convention maps forward to (sin(heading), cos(heading)) in X/Z.
        // Orthogonal projection chooses the closest point on the infinite band axis, preventing the
        // old rectangle avoid from sending the player farther toward an arena edge.
        float directionX = (float)Math.Sin(helperHeading);
        float directionZ = (float)Math.Cos(helperHeading);
        float deltaX = playerLocation.X - helperLocation.X;
        float deltaZ = playerLocation.Z - helperLocation.Z;
        float distanceAlongBand = (deltaX * directionX) + (deltaZ * directionZ);

        return new Vector3(
            helperLocation.X + (distanceAlongBand * directionX),
            playerLocation.Y,
            helperLocation.Z + (distanceAlongBand * directionZ));
    }

    private void StopSeasplitterMovement()
    {
        if (!movingToSeasplitterCenterline)
        {
            return;
        }

        Navigator.PlayerMover.MoveStop();
        movingToSeasplitterCenterline = false;
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
