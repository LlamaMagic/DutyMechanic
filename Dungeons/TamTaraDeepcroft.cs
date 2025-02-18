﻿using DutyMechanic.Data;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 16: The Tam-Tara Deepcroft dungeon logic.
/// </summary>
public class TamTaraDeepcroft : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheTamTaraDeepcroft;

    /// <inheritdoc/>
    public override DungeonId DungeonId => DungeonId.TheTamTaraDeepcroft;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        return false;
    }
}
