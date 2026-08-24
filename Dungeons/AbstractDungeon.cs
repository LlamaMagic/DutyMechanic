using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Abstract starting point for implementing specialized dungeon logic.
/// </summary>
public abstract class AbstractDungeon
{
    // These state roots retain scalar cast identity only. Keeping them on the dungeon instance makes
    // wipe/re-entry cleanup explicit and prevents one configured cast from suppressing another duty.
    private readonly MitigationCastState _tankBusterMitigationState = new();
    private readonly MitigationCastState _groupMitigationState = new();

    /// <summary>
    /// Gets zone ID for this dungeon.
    /// </summary>
    public abstract ZoneId ZoneId { get; }

    /// <summary>
    /// Gets a handle to signal the combat routine should not use certain features (e.g., prevent CR from moving).
    /// </summary>
    protected CapabilityManagerHandle CapabilityHandle { get; } = CapabilityManager.CreateNewHandle();

    /// <summary>
    /// Gets SideStep Plugin reference.
    /// </summary>
    protected static PluginContainer SidestepPlugin { get; } = PluginHelpers.GetSideStepPlugin();

    /// <summary>
    /// Gets spell IDs to follow-dodge while any contained spell is casting.
    /// </summary>
    protected abstract HashSet<uint> SpellsToFollowDodge { get; }

    /// <summary>
    /// Gets spell IDs for tank busting.
    /// </summary>
    protected abstract HashSet<uint> SpellsToTankBust { get; }

    /// <summary>
    /// Gets spell IDs for mitigating group-wide damage.
    /// </summary>
    protected abstract HashSet<uint> SpellsToMitigate { get; }

    /// <summary>
    /// Setup -- run once after entering the dungeon.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    public Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.RemoveAllAvoids(info => true);
        SidestepPlugin.Enabled = true;
        _tankBusterMitigationState.Reset();
        _groupMitigationState.Reset();

        return EnterDungeonAsync();
    }

    /// <summary>
    /// Setup -- run once after entering the dungeon.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected virtual Task<bool> EnterDungeonAsync()
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Tear-down -- run once after exiting the dungeon.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    public Task<bool> OnExitDungeonAsync()
    {
        AvoidanceManager.RemoveAllAvoids(info => true);
        SidestepPlugin.Enabled = true;
        _tankBusterMitigationState.Reset();
        _groupMitigationState.Reset();

        return ExitDungeonAsync();
    }

    /// <summary>
    /// Tear-down -- run once after exiting the dungeon.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected virtual Task<bool> ExitDungeonAsync()
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Executes dungeon logic.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    public abstract Task<bool> RunAsync();

    /// <summary>
    /// Follows closest safe ally while <see cref="SpellsToFollowDodge"/> are casting.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected async Task<bool> FollowDodgeSpells()
    {
        if (SpellsToFollowDodge == null || SpellsToFollowDodge.Count == 0)
        {
            return false;
        }

        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => SpellsToFollowDodge.Contains(bc.CastingSpellId));

        if (caster != null)
        {
            SpellCastInfo spell = caster.SpellCastInfo;
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, spell.RemainingCastTime, $"Follow-Dodge: ({caster.NpcId}) {caster.Name} is casting ({spell.ActionId}) {spell.Name} for {spell.RemainingCastTime.TotalMilliseconds:N0}ms");

            await MovementHelpers.GetClosestAlly.Follow();
        }

        return false;
    }

    /// <summary>
    /// Uses one job-appropriate defensive cooldown for a configured tank buster.
    /// </summary>
    /// <remarks>
    /// Cooldown selection resolves live player/caster wrappers on the bot thread and never sleeps,
    /// allowing the combat routine and encounter movement to resume on the next scheduler path.
    /// </remarks>
    /// <returns><see langword="true"/> only when RebornBuddy accepted a mitigation action this tick.</returns>
    protected Task<bool> TankBusterSpells()
    {
        bool actionAccepted = CombatHelpers.TryHandleTankBuster(
            SpellsToTankBust,
            _tankBusterMitigationState);
        return Task.FromResult(actionAccepted);
    }

    /// <summary>
    /// Uses one tank party-mitigation action for configured group-wide damage.
    /// </summary>
    /// <remarks>
    /// Group mitigation shares the same one-action and scalar-lifecycle guarantees as tank-buster
    /// handling, while remaining independently releasable when the two casts overlap.
    /// </remarks>
    /// <returns><see langword="true"/> only when RebornBuddy accepted a mitigation action this tick.</returns>
    protected Task<bool> DamageMitigationSpells()
    {
        bool actionAccepted = CombatHelpers.TryHandleGroupMitigation(
            SpellsToMitigate,
            _groupMitigationState);
        return Task.FromResult(actionAccepted);
    }
}
