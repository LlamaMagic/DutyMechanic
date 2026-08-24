using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Helpers;

/// <summary>
/// Retains scalar identity and completion state for one observed mitigation cast.
/// </summary>
/// <remarks>
/// RebornBuddy game objects are frame-scoped wrappers, so encounter state must never retain the
/// caster or target object itself. Object/action IDs are sufficient to suppress duplicate action
/// requests until the owning enemy cast disappears.
/// </remarks>
internal sealed class MitigationCastState
{
    internal uint CasterObjectId { get; private set; }

    internal uint EnemyActionId { get; private set; }

    internal uint TargetObjectId { get; private set; }

    internal bool ActionAccepted { get; private set; }

    internal bool Matches(BattleCharacter caster)
    {
        return CasterObjectId == caster.ObjectId
            && EnemyActionId == caster.CastingSpellId;
    }

    internal void Begin(BattleCharacter caster)
    {
        CasterObjectId = caster.ObjectId;
        EnemyActionId = caster.CastingSpellId;
        TargetObjectId = caster.SpellCastInfo.TargetId;
        ActionAccepted = false;
    }

    internal void MarkActionAccepted()
    {
        ActionAccepted = true;
    }

    internal void Reset()
    {
        CasterObjectId = 0;
        EnemyActionId = 0;
        TargetObjectId = 0;
        ActionAccepted = false;
    }
}

internal static class CombatHelpers
{
    // Five seconds keeps every selected mitigation active through ordinary dungeon cast bars while
    // avoiding very early use when an encounter exposes a long choreography cast before the hit.
    private static readonly TimeSpan MitigationLeadTime = TimeSpan.FromSeconds(5);

    // The legacy per-dungeon callers are mutually exclusive at runtime. A single scalar state root
    // therefore prevents duplicate requests without retaining an RB object across zones or frames.
    private static readonly MitigationCastState LegacyTankBusterState = new();

    // These base Action row IDs are deliberately resolved through GetMaskedAction where a trait can
    // replace them. That gives level-synced duties the base action and current-level duties the
    // upgraded action without duplicating compiler/client-era upgrade IDs in the policy.
    private static readonly MitigationAction[] PaladinShortCooldowns =
    [
        new(PlayerAction.Sheltron, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] PaladinLongCooldowns =
    [
        new(PlayerAction.Bulwark, MitigationTarget.Player),
        new(PlayerAction.Sentinel, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] WarriorShortCooldowns =
    [
        new(PlayerAction.RawIntuition, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] WarriorLongCooldowns =
    [
        new(PlayerAction.Vengeance, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] GunbreakerShortCooldowns =
    [
        new(PlayerAction.HeartOfStone, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] GunbreakerLongCooldowns =
    [
        new(PlayerAction.Camouflage, MitigationTarget.Player),
        new(PlayerAction.Nebula, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] DarkKnightShortCooldowns =
    [
        new(PlayerAction.TheBlackestNight, MitigationTarget.Player),
        new(PlayerAction.Oblation, MitigationTarget.Player),
    ];

    private static readonly MitigationAction[] DarkKnightLongCooldowns =
    [
        new(PlayerAction.ShadowWall, MitigationTarget.Player, ResolveMaskedAction: true),
    ];

    private static readonly MitigationAction[] SharedTankCooldowns =
    [
        new(PlayerAction.Rampart, MitigationTarget.Player),
        new(PlayerAction.Reprisal, MitigationTarget.Caster),
    ];

    private static readonly MitigationAction[] GroupMitigations =
    [
        new(PlayerAction.DivineVeil, MitigationTarget.Player),
        new(PlayerAction.PassageOfArms, MitigationTarget.Player),
        new(PlayerAction.ShakeItOff, MitigationTarget.Player),
        new(PlayerAction.HeartOfLight, MitigationTarget.Player),
    ];

    /// <summary>
    /// Handles one of the explicitly configured legacy tank-buster casts.
    /// </summary>
    /// <param name="tankBusterActionIds">Enemy action IDs already confirmed as tank busters by the owning dungeon.</param>
    /// <returns><see langword="true"/> only when RebornBuddy accepted one mitigation action this tick.</returns>
    internal static Task<bool> HandleTankBuster(HashSet<uint> tankBusterActionIds)
    {
        return Task.FromResult(TryHandleTankBuster(tankBusterActionIds, LegacyTankBusterState));
    }

    /// <summary>
    /// Attempts one job-appropriate mitigation for the active configured tank buster.
    /// </summary>
    /// <param name="tankBusterActionIds">Enemy action IDs confirmed by the current dungeon.</param>
    /// <param name="state">Per-dungeon scalar state used to suppress duplicate requests for one cast.</param>
    /// <returns><see langword="true"/> only when RebornBuddy accepted one mitigation action this tick.</returns>
    internal static bool TryHandleTankBuster(HashSet<uint> tankBusterActionIds, MitigationCastState state)
    {
        if (tankBusterActionIds == null || tankBusterActionIds.Count == 0 || !Core.Player.IsTank())
        {
            state.Reset();
            return false;
        }

        BattleCharacter caster = FindActiveCast(tankBusterActionIds, requirePlayerTarget: true);
        if (caster == null)
        {
            state.Reset();
            return false;
        }

        BeginCastIfNeeded(caster, state, "Tankbuster");
        if (state.ActionAccepted || caster.SpellCastInfo.RemainingCastTime > MitigationLeadTime)
        {
            return false;
        }

        (IReadOnlyList<MitigationAction> shortCooldowns, IReadOnlyList<MitigationAction> longCooldowns) =
            GetJobCooldowns(Core.Player.CurrentJob);

        // One short-recast personal action is the conservative default for ordinary dungeon
        // busters. Shared and long cooldowns are fallbacks, never stacks; exceptional hits should
        // retain an encounter-local handler that explicitly documents stronger requirements.
        bool actionAccepted = TryUseFirstAvailable(shortCooldowns, caster, "Tank mitigation")
            || TryUseFirstAvailable(SharedTankCooldowns, caster, "Tank mitigation")
            || TryUseFirstAvailable(longCooldowns, caster, "Tank mitigation");

        if (actionAccepted)
        {
            state.MarkActionAccepted();
        }

        return actionAccepted;
    }

    /// <summary>
    /// Attempts one party mitigation for an explicitly configured unavoidable-damage cast.
    /// </summary>
    /// <param name="damageActionIds">Enemy action IDs confirmed by the current dungeon.</param>
    /// <param name="state">Per-dungeon scalar state used to suppress duplicate requests for one cast.</param>
    /// <returns><see langword="true"/> only when RebornBuddy accepted one mitigation action this tick.</returns>
    internal static bool TryHandleGroupMitigation(HashSet<uint> damageActionIds, MitigationCastState state)
    {
        if (damageActionIds == null || damageActionIds.Count == 0 || !Core.Player.IsTank())
        {
            state.Reset();
            return false;
        }

        BattleCharacter caster = FindActiveCast(damageActionIds, requirePlayerTarget: false);
        if (caster == null)
        {
            state.Reset();
            return false;
        }

        BeginCastIfNeeded(caster, state, "Group-wide damage");
        if (state.ActionAccepted || caster.SpellCastInfo.RemainingCastTime > MitigationLeadTime)
        {
            return false;
        }

        bool actionAccepted = TryUseFirstAvailable(GroupMitigations, caster, "Group mitigation");
        if (actionAccepted)
        {
            state.MarkActionAccepted();
        }

        return actionAccepted;
    }

    private static BattleCharacter FindActiveCast(HashSet<uint> actionIds, bool requirePlayerTarget)
    {
        uint playerObjectId = Core.Player.ObjectId;

        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(candidate => candidate.IsValid
                && candidate.IsCasting
                && actionIds.Contains(candidate.CastingSpellId)
                && candidate.SpellCastInfo.IsValid)
            .Where(candidate => !requirePlayerTarget || TargetsPlayerOrHasNoTarget(candidate, playerObjectId))
            .OrderByDescending(candidate => candidate.SpellCastInfo.TargetId == playerObjectId)
            .FirstOrDefault();
    }

    private static bool TargetsPlayerOrHasNoTarget(BattleCharacter caster, uint playerObjectId)
    {
        uint castTargetId = caster.SpellCastInfo.TargetId;
        if (castTargetId == playerObjectId)
        {
            return true;
        }

        // Some helper-authored or point-blank casts publish no cast target. Preserve those existing
        // allowlists only when the helper also has no target evidence or is currently targeting us;
        // an explicit different target must never spend this tank's cooldowns.
        if (castTargetId == 0)
        {
            return caster.CurrentTargetId == 0 || caster.CurrentTargetId == playerObjectId;
        }

        // Self-authored cones commonly report the caster as the spell target while threat identifies
        // the tank who will actually receive the hit.
        return castTargetId == caster.ObjectId && caster.CurrentTargetId == playerObjectId;
    }

    private static void BeginCastIfNeeded(BattleCharacter caster, MitigationCastState state, string mechanicName)
    {
        if (state.Matches(caster))
        {
            return;
        }

        state.Begin(caster);
        string targetDescription = caster.SpellCastInfo.TargetId == 0
            ? "an unavailable/untargeted cast target"
            : $"target 0x{caster.SpellCastInfo.TargetId:X8}";

        Logger.Information(
            $"{mechanicName} detected: ({caster.NpcId}) {caster.Name} is casting "
            + $"({caster.CastingSpellId}) {caster.SpellCastInfo.Name} at {targetDescription}.");
    }

    private static bool TryUseFirstAvailable(
        IReadOnlyList<MitigationAction> actions,
        BattleCharacter caster,
        string logCategory)
    {
        foreach (MitigationAction definition in actions)
        {
            SpellData action = ResolvePlayerAction(definition);
            GameObject target = definition.Target == MitigationTarget.Caster ? caster : Core.Player;

            if (action == null || target == null || !target.IsValid || !ActionManager.CanCast(action, target))
            {
                continue;
            }

            if (!ActionManager.DoAction(action, target))
            {
                continue;
            }

            Logger.Information(
                $"{logCategory} accepted: {action.Name} ({action.Id}) on {target.Name} "
                + $"for enemy action {caster.CastingSpellId}.");
            return true;
        }

        return false;
    }

    private static SpellData ResolvePlayerAction(MitigationAction action)
    {
        if (action.ResolveMaskedAction)
        {
            SpellData maskedAction = ActionManager.GetMaskedAction(action.ActionId);
            if (maskedAction != null && maskedAction.Id != 0)
            {
                return maskedAction;
            }
        }

        return DataManager.GetSpellData(action.ActionId);
    }

    private static (IReadOnlyList<MitigationAction> Short, IReadOnlyList<MitigationAction> Long)
        GetJobCooldowns(ClassJobType job)
    {
        return job switch
        {
            ClassJobType.Gladiator or ClassJobType.Paladin => (PaladinShortCooldowns, PaladinLongCooldowns),
            ClassJobType.Marauder or ClassJobType.Warrior => (WarriorShortCooldowns, WarriorLongCooldowns),
            ClassJobType.Gunbreaker => (GunbreakerShortCooldowns, GunbreakerLongCooldowns),
            ClassJobType.DarkKnight => (DarkKnightShortCooldowns, DarkKnightLongCooldowns),
            _ => (Array.Empty<MitigationAction>(), Array.Empty<MitigationAction>()),
        };
    }

    /// <summary>
    /// Logic for using Level 3 Limit Break.
    /// </summary>
    internal static async Task<bool> UseLB3()
    {
        var limitBreak = DataManager.GetSpellData((uint)ClassJobRoles.LimitBreak3[Core.Me.CurrentJob]);

        Logger.Information($"Using {limitBreak.Name} on {Core.Me.CurrentTarget.Name}.");

        if (limitBreak.GroundTarget)
        {
            ActionManager.DoActionLocation(limitBreak.Id, Core.Me.CurrentTarget.Location);
            await Coroutine.Wait(10000, () => !Core.Me.IsCasting);
        }
        else
        {
            ActionManager.DoAction(limitBreak.Id, Core.Me.CurrentTarget);
            await Coroutine.Wait(10000, () => !Core.Me.IsCasting);
        }

        return false;
    }

    private enum MitigationTarget
    {
        Player,

        Caster,
    }

    private sealed record MitigationAction(
        uint ActionId,
        MitigationTarget Target,
        bool ResolveMaskedAction = false);

    private static class PlayerAction
    {
        // Tank role actions shared by every job.
        public const uint Rampart = 7531;
        public const uint Reprisal = 7535;

        // Paladin base actions. Sheltron and Sentinel are masked to their trait upgrades when active.
        public const uint Sentinel = 17;
        public const uint Bulwark = 22;
        public const uint Sheltron = 3542;

        // Warrior base actions. Both acquire current trait upgrades through GetMaskedAction.
        public const uint Vengeance = 44;
        public const uint RawIntuition = 3551;

        // Gunbreaker base actions. Heart of Stone and Nebula resolve their current upgrades.
        public const uint Camouflage = 16140;
        public const uint Nebula = 16148;
        public const uint HeartOfStone = 16161;

        // Dark Knight's magic-only Dark Mind is intentionally absent: the generic dungeon list has
        // no damage-type evidence. Known magical busters can request it encounter-locally instead.
        public const uint ShadowWall = 3636;
        public const uint TheBlackestNight = 7393;
        public const uint Oblation = 25754;

        // Existing party mitigations. Only one may be accepted for each configured damage cast.
        public const uint DivineVeil = 3540;
        public const uint PassageOfArms = 7385;
        public const uint ShakeItOff = 7388;
        public const uint HeartOfLight = 16160;
    }
}
