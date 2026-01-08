using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;
public sealed class DefensiveCooldown
{
    public uint SpellId { get; }
    public GameObject TargetType { get; }

    public DefensiveCooldown(uint spellId, GameObject targetType)
    {
        SpellId = spellId;
        TargetType = targetType;
    }
}

/// <summary>
/// Abstract starting point for implementing specialized dungeon logic.
/// </summary>
public abstract class AbstractDungeon
{
    // Ordered list = priority order
    private readonly List<DefensiveCooldown> defensiveCooldowns = new()
    {
        new DefensiveCooldown(7535, Core.Me.CurrentTarget), // Reprisal
        new DefensiveCooldown(7531, Core.Me), // Rampart
        // Paladin
        new DefensiveCooldown(36920, Core.Me), // Guardian
        new DefensiveCooldown(22, Core.Me), // Bulwark
        // Warrior
        new DefensiveCooldown(44, Core.Me), // Vengeance
        new DefensiveCooldown(36923, Core.Me), // Damnation
        // Gunbreaker
        new DefensiveCooldown(36935, Core.Me), // Great Nebula
        new DefensiveCooldown(16140, Core.Me), // Camouflage
        // Dark Knight
        new DefensiveCooldown(3634, Core.Me), // Dark Mind
        new DefensiveCooldown(7393, Core.Me), // The Blackest Night
        new DefensiveCooldown(25754, Core.Me), // Oblation
    };

    private readonly List<DefensiveCooldown> groupMitigations = new()
    {
        // Paladin
        new DefensiveCooldown(3540, Core.Me), // Divine Veil
        new DefensiveCooldown(7385, Core.Me), // Passage of Arms
        // Warrior
        new DefensiveCooldown(7388, Core.Me), // Shake It Off
        // Gunbreaker
        new DefensiveCooldown(16160, Core.Me), // Heart of Light

    };

    private uint _lastLoggedTankbusterSpellId = 0;
    private uint _lastCasterNpcId = 0;
    private uint _lastLoggedMitigatedSpellId = 0;
    private uint _lastMitigatedCasterNpcId = 0;

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
    /// Gets spell IDs for mitigating group wide damage
    /// </summary>
    protected abstract HashSet<uint> SpellsToMitigate { get; }

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
    /// Uses defensive cooldowns to mitigate damage from Tank Busters <see cref="SpellsToTankBust"/>.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected async Task<bool> TankBusterSpells()
    {
        if (SpellsToTankBust == null || SpellsToTankBust.Count == 0 || !Core.Me.IsTank())
        {
            return false;
        }

        BattleCharacter caster = GameObjectManager
            .GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => SpellsToTankBust.Contains(bc.CastingSpellId));

        if (caster == null)
        {
            return false;

        }

        bool castAny = false;
        if (caster.IsCasting)
        {
            if (caster.CastingSpellId != 0 &&
                (caster.CastingSpellId != _lastLoggedTankbusterSpellId ||
                 caster.NpcId != _lastCasterNpcId))
            {
                _lastLoggedTankbusterSpellId = caster.CastingSpellId;
                _lastCasterNpcId = caster.NpcId;

                Logger.Information(
                    $"Tankbuster detected: NPC {caster.Name} casting spell {caster.SpellCastInfo.Name} {caster.CastingSpellId}");
            }
        }
        else
        {
            // Reset once the cast finishes
            _lastLoggedTankbusterSpellId = 0;
            _lastCasterNpcId = 0;
        }

        foreach (var cd in defensiveCooldowns)
        {
            GameObject target = cd.TargetType;

            // If Reprisal is first but we don't have a valid target, skip it
            if (target == null)
            {
                continue;
            }

            if (!ActionManager.CanCast(cd.SpellId, target))
            {
                continue;
            }

            SpellData action = DataManager.GetSpellData(cd.SpellId);
            if (action == null)
            {
                continue;
            }

            Logger.Information($"Casting {action.Name} ({action.Id}) on {cd.TargetType}");
            ActionManager.DoAction(action, target);

            castAny = true;

            // Prevent action spam in the same tick
            await Coroutine.Sleep(150);
        }

        return castAny;
    }

    /// <summary>
    /// Uses defensive cooldowns to mitigate damage group wide damage <see cref="SpellsToMitigate"/>.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    protected async Task<bool> DamageMitigationSpells()
    {
        if (SpellsToTankBust == null || SpellsToTankBust.Count == 0)
        {
            return false;
        }

        BattleCharacter caster = GameObjectManager
            .GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => SpellsToMitigate.Contains(bc.CastingSpellId));

        if (caster == null)
        {
            return false;

        }

        bool castAny = false;
        if (caster.IsCasting)
        {
            if (caster.CastingSpellId != 0 &&
                (caster.CastingSpellId != _lastLoggedMitigatedSpellId ||
                 caster.NpcId != _lastMitigatedCasterNpcId))
            {
                _lastLoggedMitigatedSpellId = caster.CastingSpellId;
                _lastMitigatedCasterNpcId = caster.NpcId;

                Logger.Information(
                    $"Group wide damage detected: NPC {caster.Name} casting spell {caster.SpellCastInfo.Name} {caster.CastingSpellId}");
            }
        }
        else
        {
            // Reset once the cast finishes
            _lastLoggedMitigatedSpellId = 0;
            _lastMitigatedCasterNpcId = 0;
        }

        foreach (var cd in groupMitigations)
        {
            GameObject target = cd.TargetType;

            // If Reprisal is first but we don't have a valid target, skip it
            if (target == null)
            {
                continue;
            }

            if (!ActionManager.CanCast(cd.SpellId, target))
            {
                continue;
            }

            SpellData action = DataManager.GetSpellData(cd.SpellId);
            if (action == null)
            {
                continue;
            }

            Logger.Information($"Casting {action.Name} ({action.Id}) on {cd.TargetType}");
            ActionManager.DoAction(action, target);

            castAny = true;

            // Prevent action spam in the same tick
            await Coroutine.Sleep(150);
        }

        return castAny;
    }
}
