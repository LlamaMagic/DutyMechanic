using Buddy.Coroutines;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.RemoteWindows;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DutyMechanic.Helpers;

internal static class UsefulTasks
{

    /// <summary>
    /// This will press yes on ReadyCheck
    /// </summary>
    internal static async Task<bool> HandleReadyCheck()
    {
        if (LlamaLibrary.RemoteWindows.NotificationReadyCheck.Instance.IsOpen)
        {
            if (SelectYesno.IsOpen)
            {
                Logger.Information($"Selecting yes to ready check");
                SelectYesno.Yes();
            }
        }
        return false;
    }
}
