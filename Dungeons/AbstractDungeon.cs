using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DutyMechanic.Extensions;
using DutyMechanic.Logging;
using ff14bot;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Abstract starting point for implementing specialized dungeon logic.
/// </summary>
public abstract class AbstractDungeon
{
    /// <summary>
    /// Spell IDs for Reprisal and Rampart.
    /// </summary>
    private readonly uint reprisal = 7535;

    private readonly uint rampart = 7531;

    /// <summary>
    /// Gets zone ID for this dungeon.
    /// </summary>
    public abstract ZoneId ZoneId { get; }

    /// <summary>
    /// Gets <see cref="DungeonId"/> for this dungeon.
    /// </summary>
    public abstract DungeonId DungeonId { get; }

    /// <summary>
    /// Gets a handle to signal the combat routine should not use certain features (e.g., prevent CR from moving).
    /// </summary>
    protected CapabilityManagerHandle CapabilityHandle { get; } = CapabilityManager.CreateNewHandle();

    /// <summary>
    /// Gets SideStep Plugin reference.
    /// </summary>
    protected PluginContainer SidestepPlugin { get; } = PluginHelpers.GetSideStepPlugin();

    /// <summary>
    /// Gets spell IDs to follow-dodge while any contained spell is casting.
    /// </summary>
    protected abstract HashSet<uint> SpellsToFollowDodge { get; }

    /// <summary>
    /// Gets spell IDs for tank busting
    /// </summary>
    protected abstract HashSet<uint> SpellsToTankBust { get; }

    /// <summary>
    /// Setup -- run once after entering the dungeon.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    public virtual Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();
        SidestepPlugin.Enabled = true;

        return Task.FromResult(false);
    }

    /// <summary>
    /// Tear-down -- run once after exiting the dungeon.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    public virtual Task<bool> OnExitDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();
        SidestepPlugin.Enabled = true;

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
        if (SpellsToFollowDodge == null || !SpellsToFollowDodge.Any())
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
    /// Uses Rampart or Reprisal to mitigate damage from Tank Busters <see cref="SpellsToTankBust"/>.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected async Task<bool> TankBusterSpells()
    {
        if (SpellsToTankBust == null || !SpellsToTankBust.Any() || !Core.Me.IsTank())
        {
            return false;
        }

        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => SpellsToTankBust.Contains(bc.CastingSpellId));

        if (caster != null)
        {
            if (ActionManager.CanCast(rampart, Core.Player))
            {
                SpellData action = DataManager.GetSpellData(rampart);
                Logger.Information($"Casting {action.Name} ({action.Id})");
                ActionManager.DoAction(action, Core.Player);
                await Coroutine.Sleep(1_500);
            }

            if (ActionManager.CanCast(reprisal, Core.Player.CurrentTarget))
            {
                SpellData action = DataManager.GetSpellData(reprisal);
                Logger.Information($"Casting {action.Name} ({action.Id})");
                ActionManager.DoAction(action, Core.Player.CurrentTarget);
                await Coroutine.Sleep(1_500);
            }
        }

        return false;
    }
}
