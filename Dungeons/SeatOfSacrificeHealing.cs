using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;
public static partial class SeatOfSacrifice
{
    private static object healSettings;
    private static readonly Dictionary<PropertyInfo, object> originalHealing = new();
    private static bool reportedHealingContract;
    // Installed Status2303 requires full recovery, not ordinary routine thresholds.
    // Temporarily raise the selected White Mage routine's existing healing thresholds;
    // it still chooses spells/targets and owns the action channel. Never save this override.
    private static void FullHealRequirement(BattleCharacter[] party)
    {
        if (Core.Me.CurrentJob != ClassJobType.WhiteMage || !party.Any(p => p.HasAura(2303)))
        {
            RestoreHealing();
            return;
        }

        if (healSettings != null)
        {
            return;
        }

        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Magitek.Models.WhiteMage.WhiteMageSettings", false)).FirstOrDefault(t => t != null);
        var instance = type?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetValue(null);
        string[] names =
        {
            "AoEHealHealthPercent",
            "Cure2HealthPercent",
            "MedicaHealthPercent",
            "Cure3HealthPercent",
            "AfflatusRaptureHealthPercent",
            "AfflatusSolaceHealthPercent",
            "InterruptHealingHealthPercent",
            "StopDpsIfPartyMemberBelowHealthPercent"
        };
        var props = names.Select(n => type?.GetProperty(n)).ToArray();
        if (instance == null || props.Any(p => p == null || !p.CanWrite || p.PropertyType != typeof(float)))
        {
            if (!reportedHealingContract)
            {
                Logger.Warning("[Seat] Routine does not expose full-heal thresholds; keeping its current settings.");
            }

            reportedHealingContract = true;
            return;
        }

        healSettings = instance;
        foreach (var p in props)
        {
            originalHealing[p] = p.GetValue(instance);
            p.SetValue(instance, 100f);
        }

        Logger.Information("[Seat] True Walking Dead: routine full-heal thresholds leased until aura clears.");
    }

    // A newer human setting must win. Restore only values still equal to our temporary
    // override, including normal stop/loading/death paths; no settings file is written.
    private static void RestoreHealing()
    {
        if (healSettings == null)
        {
            return;
        }

        foreach (var p in originalHealing)
        {
            if (Equals(p.Key.GetValue(healSettings), 100f))
            {
                p.Key.SetValue(healSettings, p.Value);
            }
        }

        originalHealing.Clear();
        healSettings = null;
        Logger.Information("[Seat] Full-heal routine thresholds released.");
    }
}
