using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 17: Copperbell Mines dungeon logic.
/// </summary>
public class CopperbellMines : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.CopperbellMines;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    // Kottos: Grand Slam; Ichorous Ire: Fluid Spread; Gyges: Grand Slam.
    // These are single-target hits, not telegraphs to escape or party-wide damage.
    protected override HashSet<uint> SpellsToTankBust { get; } = [28545, 28461, 28764];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Own this cast before SideStep's generic omen/cast-type selection. Gyges's
        // actual safe center is only four yalms; keep half a yalm of clearance.
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (ushort)ZoneId && Core.Me != null && Core.Me.IsValid && Core.Me.InCombat,
            objectSelector: caster => caster.IsValid && caster.IsCasting && caster.CastingSpellId == 28762,
            outerRadius: 40.5,
            innerRadius: 3.5);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await TankBusterSpells();
        await FollowDodgeSpells();

        return false;
    }
}
