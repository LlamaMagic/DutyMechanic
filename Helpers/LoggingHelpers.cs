using DutyMechanic.Logging;
using ff14bot.Managers;

namespace DutyMechanic.Helpers;

/// <summary>Records zone transitions without collecting per-frame encounter data.</summary>
public static class LoggingHelpers
{
    private static ushort lastZoneId;
    private static uint lastSubZoneId;

    // The protected Yuweyawata handler still references this guard. Keep it disabled
    // without a collector so removing development instrumentation cannot change its behavior.
    internal static bool MechanicDiagnosticsEnabled => false;

    /// <summary>Logs zone transitions once so support logs identify the active encounter.</summary>
    public static void LogZoneChanges()
    {
        if (lastZoneId == WorldManager.ZoneId && lastSubZoneId == WorldManager.SubZoneId)
            return;

        Logger.Information($"Zone changed from ({lastZoneId}, {lastSubZoneId}) to ({WorldManager.ZoneId}, {WorldManager.SubZoneId})");
        lastZoneId = WorldManager.ZoneId;
        lastSubZoneId = WorldManager.SubZoneId;
    }
}
