using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles confirmed movement mechanics and captures later-phase evidence for the level 94
/// quest instance The Feat of the Brotherhood.
/// </summary>
/// <remarks>
/// This solo instance reuses Yak T'el's territory ID, so every avoid also requires the instance
/// boss to be present. SideStep remains enabled because the first captured run proved it can
/// decode Roaring Star, Coiled Strike, Dual Pyres, Steelfold Strike, and Outer Wake telegraphs.
/// DutyMechanic complements that geometry with a bounded arena and the semantic Fallen Star
/// spread, while logging uncaptured phases before a future maintainer adds stateful handling.
/// </remarks>
public sealed class FeatOfBrotherhood : AbstractDungeon
{
    // The red wall and captured edge position show an axis-aligned square arena extending about
    // 21 yalms from this center on both world axes. Keep a one-yalm inset so RB does not select a
    // point on the lethal wall, while retaining the corner space that a circular boundary removed.
    private static readonly Vector3 ArenaCenter = new(353.5f, -113.97447f, 596.0f);
    private const float ArenaSafeWidth = 40f;
    private const float ArenaSafeHeight = 40f;
    private const float ArenaBoundaryOuterWidth = 160f;
    private const float ArenaBoundaryOuterHeight = 160f;

    // Standard spread markers in this encounter are approximately six yalms. The half-yalm
    // margin accounts for actor hitbox centers and RB navigation stopping tolerance.
    private const float FallenStarSeparationRadius = 6.5f;

    private readonly HashSet<string> _activeCastKeys = [];
    private string _actorLifecycleFingerprint = string.Empty;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.YakTel;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Quest instances may share an open-world territory and may not expose a unique subzone.
        // Boss presence is therefore part of every condition, in addition to the plugin-wide
        // InstanceContentDirector gate, so no Yak T'el field combat inherits this arena boundary.
        AvoidanceHelpers.AddAvoidSquareDonut(
            canRun: IsEncounterCombatActive,
            innerWidth: ArenaSafeWidth,
            innerHeight: ArenaSafeHeight,
            outerWidth: ArenaBoundaryOuterWidth,
            outerHeight: ArenaBoundaryOuterHeight,
            collectionProducer: () => [ArenaCenter],
            priority: AvoidancePriority.High);

        // Fallen Star is a spread, not the later Wuk Lamat shelter. The failed 2026-08-18 run
        // showed generic ally convergence pulling the player into the group. Avoiding each live
        // friendly duty NPC during the cast encodes the semantic requirement without choosing a
        // blind destination; RB's avoidance planner remains responsible for the safe point.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => IsEncounterCombatActive() && IsActionCasting(EnemyAction.FallenStar),
            objectSelector: IsFriendlyDutyNpc,
            radiusProducer: _ => FallenStarSeparationRadius,
            priority: AvoidancePriority.High));

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        // SideStep successfully decoded the observed cast-type and omen geometry. Keeping it
        // enabled avoids duplicating or approximating complex rotating cones and sequential
        // half-room cleaves until logs prove that a specific telegraph needs replacement.
        SidestepPlugin.Enabled = true;

        if (!IsEncounterActive())
        {
            _activeCastKeys.Clear();
            _actorLifecycleFingerprint = string.Empty;
            return Task.FromResult(false);
        }

        LogActorLifecycleChanges();
        LogNewEncounterCasts();
        return Task.FromResult(false);
    }

    private static bool IsEncounterCombatActive() => Core.Player.InCombat && IsEncounterActive();

    private static bool IsEncounterActive() =>
        WorldManager.ZoneId == (uint)Data.ZoneId.YakTel &&
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.GuloolJaJasGlory)
            .Any(actor => actor.IsValid && actor.IsVisible && actor.IsAlive);

    private static bool IsActionCasting(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == actionId);

    private static bool IsFriendlyDutyNpc(BattleCharacter actor) =>
        actor.IsValid && actor.IsVisible && actor.IsAlive && actor.IsNpc &&
        actor.ObjectId != Core.Player.ObjectId && actor.NpcId != EnemyNpc.GuloolJaJasGlory &&
        !actor.CanAttack;

    private void LogActorLifecycleChanges()
    {
        var actors = EncounterActors().ToArray();
        var fingerprint = string.Join("|", actors.Select(actor =>
            $"{actor.ObjectId}:{actor.NpcId}:{actor.IsAlive}:{actor.IsTargetable}:{actor.CanAttack}"));
        if (fingerprint == _actorLifecycleFingerprint)
            return;

        _actorLifecycleFingerprint = fingerprint;
        Logger.Information(
            "[FeatOfBrotherhood Capture] actor lifecycle changed: {0}",
            actors.Length == 0 ? "none" : string.Join(", ", actors.Select(FormatActor)));
    }

    private void LogNewEncounterCasts()
    {
        var casters = EncounterActors()
            .Where(actor => actor.IsCasting && actor.CastingSpellId != 0 &&
                actor.NpcId is EnemyNpc.GuloolJaJasGlory or EnemyNpc.OathOfFire)
            .ToArray();
        var currentKeys = casters
            .Select(actor => $"{actor.ObjectId}:{actor.CastingSpellId}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var caster in casters)
        {
            var key = $"{caster.ObjectId}:{caster.CastingSpellId}";
            if (_activeCastKeys.Contains(key))
                continue;

            var cast = caster.SpellCastInfo;
            Logger.Information(
                "[FeatOfBrotherhood Capture] cast name='{0}' action={1} caster={2}/{3} " +
                "location={4} castLocation={5} target={6} heading={7:0.000}; actors=[{8}]",
                cast?.Name ?? "unknown",
                caster.CastingSpellId,
                caster.ObjectId,
                caster.NpcId,
                caster.Location,
                cast?.CastLocation ?? caster.Location,
                cast?.TargetId ?? 0,
                caster.Heading,
                string.Join(", ", EncounterActors().Select(FormatActor)));
        }

        _activeCastKeys.RemoveWhere(key => !currentKeys.Contains(key));
        _activeCastKeys.UnionWith(currentKeys);
    }

    private static IEnumerable<BattleCharacter> EncounterActors() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsVisible &&
                actor.Location.Distance2D(ArenaCenter) <= 45f)
            .OrderBy(actor => actor.NpcId)
            .ThenBy(actor => actor.ObjectId);

    private static string FormatActor(BattleCharacter actor) =>
        $"{actor.ObjectId}/{actor.NpcId}[name='{actor.Name}',alive={actor.IsAlive}," +
        $"targetable={actor.IsTargetable},attackable={actor.CanAttack},target={actor.CurrentTargetId}," +
        $"cast={actor.CastingSpellId},location={actor.Location}]";

    private static class EnemyNpc
    {
        // These IDs are encounter-specific by design. SoloDuty's reusable target planner still
        // discovers adds by lifecycle and does not depend on either value.
        internal const uint GuloolJaJasGlory = 12734;
        internal const uint OathOfFire = 12738;
    }

    private static class EnemyAction
    {
        // Captured as a targeted cast after Outer Wake in the 2026-08-18 RB log. Unlike the later
        // shelter, this action requires separation from allies.
        internal const uint FallenStar = 37205;
    }
}
