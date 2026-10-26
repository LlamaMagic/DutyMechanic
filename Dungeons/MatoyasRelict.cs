using DutyMechanic.Data;
using System.Collections.Generic;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 80.5: Matoya's Relict dungeon logic.
/// </summary>
public class MatoyasRelict : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.MatoyasRelict;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (WorldManager.ZoneId != 933 || CommonBehaviors.IsLoading || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
        {
            return false;
        }

        await FollowDodgeSpells();
        return false;
    }
}
