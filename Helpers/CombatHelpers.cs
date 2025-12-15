using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Threading.Tasks;

namespace DutyMechanic.Helpers;

internal static class CombatHelpers
{
    private static uint reprisal = 7535;
    private static uint rampart = 7531;

    /// <summary>
    /// Logic for handling tank busters
    /// </summary>
    internal static async Task<bool> HandleTankBuster()
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

        return false;
    }

    /// <summary>
    /// Logic for using Level 3 Limit Break
    /// </summary>
    internal static async Task<bool> UseLB3()
    {
        var limitBreak = DataManager.GetSpellData((uint)ClassJobRoles.LimitBreak3[Core.Me.CurrentJob]);

        Logger.Information($"Using {limitBreak.Name} on {Core.Me.CurrentTarget.Name}.");

        if (limitBreak.GroundTarget)
        {
            ActionManager.DoActionLocation(limitBreak.Id, Core.Me.CurrentTarget.Location);
            await Coroutine.Wait(10000, () => !Core.Me.IsCasting);
        }
        else
        {
            ActionManager.DoAction(limitBreak.Id, Core.Me.CurrentTarget);
            await Coroutine.Wait(10000, () => !Core.Me.IsCasting);
        }

        return false;
    }
}
