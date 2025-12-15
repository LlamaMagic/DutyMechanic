using Buddy.Coroutines;
using Clio.Common;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 70.5: Hells' Lid dungeon logic.
/// </summary>
public class HellsLid : AbstractDungeon
{
    private static readonly HashSet<uint> HellOfWater =
    [
        11541,
        10192,
    ];

    private static readonly HashSet<uint> HellOfWaste2 =
    [
        10194,
        10193,
    ];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.HellsLid;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
    [
        11541,
        10192, // Hell of Water by Genbu
        10193,
        10194, // Hell of Waste by Genbu
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        if (HellOfWater.IsCasting() || HellOfWaste2.IsCasting())
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 4_000, "Hell of Water/Waste");
            await CommonTasks.MoveTo(MathEx.GetRandomPointInCircle(Core.Player.Location, 3f));
            await Coroutine.Yield();

            await Coroutine.Sleep(3_000);

            if (ActionManager.IsSprintReady)
            {
                ActionManager.Sprint();
                await Coroutine.Wait(1_000, () => !ActionManager.IsSprintReady);
            }

            await Coroutine.Sleep(1_000);
            await Coroutine.Yield();
        }

        return false;
    }
}
