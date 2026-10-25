using DutyMechanic.Data;
using ff14bot;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 15: Sastasha dungeon logic.
/// </summary>
public class Sastasha : AbstractDungeon
{
    private uint priorityTargetId;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Sastaha;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        // The profile's boss grind lists omit Baleen Guards and prefer Madison over
        // his reavers. Give encounter adds a Kill POI, leaving combat and movement
        // with OrderBot/SideStep. Keep the current add until it is no longer eligible.
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsTargetable && a.CanAttack
                && Math.Abs(a.Y - Core.Me.Y) < 8 && a.Distance2D() < 40).ToArray();
        var boss = Core.Me.InCombat
            ? actors.FirstOrDefault(a => a.InCombat && a.NpcId is 1382 or 1206)
            : null;
        var adds = actors.Where(a => boss != null && a.Distance2D(boss.Location) < 30
            && (boss.NpcId == 1382 ? a.NpcId is 342 or 1205 : a.NpcId == 1207)
            && a.InLineOfSight()).ToArray();
        var add = adds.FirstOrDefault(a => a.ObjectId == Poi.Current?.Unit?.ObjectId)
            ?? adds.OrderBy(a => a.Distance2D()).FirstOrDefault();
        if (add != null)
        {
            if (Poi.Current?.Unit?.ObjectId != add.ObjectId)
            {
                Poi.Current = new Poi(add, PoiType.Kill);
                priorityTargetId = add.ObjectId;
            }
        }
        else
        {
            ReleasePriorityTarget();
        }

        return false;
    }

    private void ReleasePriorityTarget()
    {
        if (priorityTargetId != 0 && Poi.Current?.Unit?.ObjectId == priorityTargetId)
            Poi.Clear("Sastasha priority add is no longer eligible");
        priorityTargetId = 0;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleasePriorityTarget();
        return Task.FromResult(false);
    }
}
