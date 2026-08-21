using DutyMechanic.Logging;
using ff14bot.Managers;

namespace DutyMechanic.Helpers;

/// <summary>
/// Convenience functions for logging data.
/// </summary>
public static class LoggingHelpers
{
    private static ushort lastZoneId = 0;
    private static uint lastSubZoneId = 0;

    /// <summary>
    /// Logs changes to zone or sub-zone IDs.
    /// </summary>
    public static void LogZoneChanges()
    {
        if (lastZoneId != WorldManager.ZoneId || lastSubZoneId != WorldManager.SubZoneId)
        {
            Logger.Information($"Zone changed from ({lastZoneId}, {lastSubZoneId}) to ({WorldManager.ZoneId}, {WorldManager.SubZoneId})");
            lastZoneId = WorldManager.ZoneId;
            lastSubZoneId = WorldManager.SubZoneId;
        }
    }
}
