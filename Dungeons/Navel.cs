using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot.Managers;
using System.Collections.Generic;
using System.Threading.Tasks;
using DutyMechanic.Extensions;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 34: The Navel dungeon logic.
/// </summary>
public class Navel : AbstractDungeon
{
    private const int Titan = 1801;

    private static readonly HashSet<uint> Spells = new()
    {
        651,
    };

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheNavel;

    /// <inheritdoc/>
    public override DungeonId DungeonId => DungeonId.TheNavel;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { };
    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        /*
         * [12:14:39.575 V] [SideStep] Landslide [CastType][Id: 650][Omen: 9][RawCastType: 4][ObjId: 1073996108]
         *    Handled by SideStep
         * [12:15:07.346 V] [SideStep] Geocrush [CastType][Id: 651][Omen: 152][RawCastType: 2][ObjId: 1073996108]
         *    Need to follow NPC here.
         * [12:38:36.865 V] [SideStep] Weight of the Land [CastType][Id: 973][Omen: 8][RawCastType: 2][ObjId: 1073851629]
         *    Handled by SideStep
         */

        if (GameObjectManager.GetObjectByNPCId(Titan) != null)
        {
            if (Spells.IsCasting())
            {
                SidestepPlugin.Enabled = false;
                AvoidanceManager.RemoveAllAvoids(i => i.CanRun);
                await MovementHelpers.GetClosestAlly.Follow();
                SidestepPlugin.Enabled = true;

                Logger.Information("Resetting navigation");
                AvoidanceManager.ResetNavigation();
            }
        }

        await Coroutine.Yield();

        return false;
    }
}
