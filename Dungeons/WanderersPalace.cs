using Buddy.Coroutines;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 50: The Wanderer's Palace dungeon logic.
/// </summary>
public class WanderersPalace : AbstractDungeon
{
    private const int TonberryStalker = 1556;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheWanderersPalace;

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

        GameObject tStalker = GameObjectManager.GetObjectsByNPCId<GameObject>(NpcId: TonberryStalker)
            .FirstOrDefault(bc => bc.Distance() < 10 && bc.IsVisible);

        if (tStalker != null && Core.Me.ClassLevel < 89)
        {
            AvoidanceManager.AddAvoidObject<GameObject>(() => true, 8f, tStalker.ObjectId);
        }

        await Coroutine.Yield();

        return false;
    }
}
