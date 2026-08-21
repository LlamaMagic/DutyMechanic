using DutyMechanic.Logging;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DutyMechanic.Helpers;

/// <summary>
/// Convenience functions for logging data.
/// </summary>
public static class LoggingHelpers
{
    // Only stable scalar snapshots are retained because RB object and aura wrappers expire after
    // the current bot frame.
    private static readonly Dictionary<uint, uint> TrackedMechanicCastsByCaster = [];
    private static readonly Dictionary<uint, int> TrackedVulnerabilityStacksByAura = [];
    private static readonly Queue<RecentMechanicCast> RecentMechanicCasts = [];
    private const int MechanicContextWindowSeconds = 12;
    private const int MechanicContextMaximumEntries = 24;
    private static bool mechanicDiagnosticsWereEnabled;
    private static bool diagnosticPlayerWasAlive;
    private static ushort diagnosticZoneId;
    private static uint diagnosticSubZoneId;
    private static ushort lastZoneId = 0;
    private static uint lastSubZoneId = 0;

    /// <summary>
    /// Updates optional encounter diagnostics on the bot thread. The bounded cast history correlates
    /// helper and NPC actions with later vulnerability gains or player deaths without retaining
    /// frame-scoped RB wrappers.
    /// </summary>
    /// <param name="enabled">
    /// Whether developer diagnostics are enabled. Passing <see langword="false"/> clears all
    /// transient correlation state and produces no recurring diagnostic traffic.
    /// </param>
    public static void UpdateMechanicDiagnostics(bool enabled)
    {
        if (!enabled)
        {
            if (mechanicDiagnosticsWereEnabled)
            {
                Logger.Information("[MechanicDiag] Disabled; transient cast and vulnerability history cleared.");
            }

            ResetMechanicDiagnosticState();
            mechanicDiagnosticsWereEnabled = false;
            return;
        }

        if (!mechanicDiagnosticsWereEnabled)
        {
            ResetMechanicDiagnosticState();
            mechanicDiagnosticsWereEnabled = true;
            Logger.Information("[MechanicDiag] Enabled; recording encounter casts, vulnerability gains, and deaths.");
        }

        if (Core.Player == null || !Core.Player.IsValid)
        {
            TrackedMechanicCastsByCaster.Clear();
            TrackedVulnerabilityStacksByAura.Clear();
            RecentMechanicCasts.Clear();
            diagnosticPlayerWasAlive = false;
            return;
        }

        if (diagnosticZoneId != WorldManager.ZoneId || diagnosticSubZoneId != WorldManager.SubZoneId)
        {
            TrackedMechanicCastsByCaster.Clear();
            TrackedVulnerabilityStacksByAura.Clear();
            RecentMechanicCasts.Clear();
            diagnosticZoneId = WorldManager.ZoneId;
            diagnosticSubZoneId = WorldManager.SubZoneId;
            diagnosticPlayerWasAlive = Core.Player.IsAlive;
            Logger.Information(
                $"[MechanicDiag] Context reset for zone={diagnosticZoneId} subZone={diagnosticSubZoneId} " +
                $"player={Format(Core.Player.Location)}.");
        }

        DateTime nowUtc = DateTime.UtcNow;
        RemoveExpiredMechanicContext(nowUtc);
        LogMechanicCastStarts(nowUtc);
        LogVulnerabilityChanges(nowUtc);
        LogPlayerDeath(nowUtc);
    }

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

    /// <summary>
    /// Logs each encounter-owned cast once and stores an immutable summary for later correlation.
    /// Trust members' ordinary player actions are excluded to keep rotation traffic out of captures.
    /// </summary>
    /// <param name="nowUtc">Current bot-thread observation time.</param>
    private static void LogMechanicCastStarts(DateTime nowUtc)
    {
        HashSet<uint> currentCasterIds = [];
        foreach (BattleCharacter caster in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor => actor != null && actor.IsValid && actor.IsNpc && actor.IsCasting))
        {
            SpellCastInfo spell = caster.SpellCastInfo;
            if (!spell.IsValid || spell.ActionId == 0 || spell.RemainingCastTime <= TimeSpan.Zero)
            {
                continue;
            }

            bool isPartyMember = PartyManager.AllMembers?.Any(member => member.ObjectId == caster.ObjectId) == true;
            SpellData spellData = spell.SpellData;
            if (isPartyMember && spellData != null && spellData.IsValid && spellData.IsPlayerAction)
            {
                continue;
            }

            currentCasterIds.Add(caster.ObjectId);
            if (TrackedMechanicCastsByCaster.TryGetValue(caster.ObjectId, out uint previousActionId) &&
                previousActionId == spell.ActionId)
            {
                continue;
            }

            TrackedMechanicCastsByCaster[caster.ObjectId] = spell.ActionId;
            byte omen = spellData != null && spellData.IsValid ? spellData.Omen : (byte)0;
            byte rawCastType = spellData != null && spellData.IsValid ? spellData.RawCastType : (byte)0;
            string summary =
                $"{caster.Name}/{spell.Name} action={spell.ActionId} caster=0x{caster.ObjectId:X8} " +
                $"baseId=0x{caster.BaseId:X} npcId={caster.NpcId}";

            Logger.Information(
                $"[MechanicDiag] CAST_START {summary} party={isPartyMember} visible={caster.IsVisible} " +
                $"targetable={caster.IsTargetable} target=0x{spell.TargetId:X8} " +
                $"casterLocation={Format(caster.Location)} heading={Format(caster.Heading)} " +
                $"castLocation={Format(spell.CastLocation)} castMs={spell.CastTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"remainingMs={spell.RemainingCastTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"omen={omen} rawCastType={rawCastType} player={Format(Core.Player.Location)} " +
                $"hp={Core.Player.CurrentHealth}/{Core.Player.MaxHealth} avoids={AvoidanceManager.Avoids.Count} " +
                $"escapingAvoid={AvoidanceManager.IsRunningOutOfAvoid}.");

            RecentMechanicCasts.Enqueue(new RecentMechanicCast(nowUtc, summary));
            while (RecentMechanicCasts.Count > MechanicContextMaximumEntries)
            {
                RecentMechanicCasts.Dequeue();
            }
        }

        foreach (uint completedCasterId in TrackedMechanicCastsByCaster.Keys
                     .Where(objectId => !currentCasterIds.Contains(objectId))
                     .ToList())
        {
            TrackedMechanicCastsByCaster.Remove(completedCasterId);
        }
    }

    /// <summary>
    /// Logs increases to statuses whose English data name contains "Vulnerability Up". Name matching
    /// covers client status variants while every record retains its concrete status ID and raw value.
    /// </summary>
    /// <param name="nowUtc">Current bot-thread observation time used for cast correlation.</param>
    private static void LogVulnerabilityChanges(DateTime nowUtc)
    {
        Auras playerAuras = Core.Player.Auras;
        if (playerAuras == null || !playerAuras.IsValid)
        {
            return;
        }

        HashSet<uint> currentVulnerabilityAuraIds = [];
        foreach (Aura aura in playerAuras.AuraList.Where(IsVulnerabilityAura))
        {
            currentVulnerabilityAuraIds.Add(aura.Id);
            int reportedStacks = playerAuras.GetAuraStacksById(aura.Id);
            int currentStacks = reportedStacks > 0
                ? reportedStacks
                : Math.Max(1, unchecked((int)aura.Value));

            bool previouslyObserved = TrackedVulnerabilityStacksByAura.TryGetValue(aura.Id, out int previousStacks);
            TrackedVulnerabilityStacksByAura[aura.Id] = currentStacks;
            if (previouslyObserved && currentStacks <= previousStacks)
            {
                continue;
            }

            int gainedStacks = previouslyObserved ? currentStacks - previousStacks : currentStacks;
            Logger.Warning(
                $"[MechanicDiag] VULNERABILITY_GAIN name=\"{aura.Name}\" statusId={aura.Id} " +
                $"stacks={currentStacks} gained={gainedStacks} rawValue={aura.Value} " +
                $"remainingMs={aura.TimespanLeft.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} " +
                $"source=0x{aura.CasterId:X8} player={Format(Core.Player.Location)} " +
                $"hp={Core.Player.CurrentHealth}/{Core.Player.MaxHealth} avoids={AvoidanceManager.Avoids.Count} " +
                $"escapingAvoid={AvoidanceManager.IsRunningOutOfAvoid} recentCasts=[{FormatRecentMechanicContext(nowUtc)}].");
        }

        foreach (uint expiredAuraId in TrackedVulnerabilityStacksByAura.Keys
                     .Where(auraId => !currentVulnerabilityAuraIds.Contains(auraId))
                     .ToList())
        {
            TrackedVulnerabilityStacksByAura.Remove(expiredAuraId);
        }
    }

    /// <summary>
    /// Logs the alive-to-dead edge with the latest vulnerability state and recent encounter casts.
    /// </summary>
    /// <param name="nowUtc">Current bot-thread observation time used for cast correlation.</param>
    private static void LogPlayerDeath(DateTime nowUtc)
    {
        if (diagnosticPlayerWasAlive && !Core.Player.IsAlive)
        {
            string vulnerabilityState = TrackedVulnerabilityStacksByAura.Count == 0
                ? "none"
                : string.Join(",", TrackedVulnerabilityStacksByAura
                    .OrderBy(pair => pair.Key)
                    .Select(pair => $"{pair.Key}:{pair.Value}"));
            Logger.Warning(
                $"[MechanicDiag] PLAYER_DEATH player={Format(Core.Player.Location)} " +
                $"vulnerability=[{vulnerabilityState}] recentCasts=[{FormatRecentMechanicContext(nowUtc)}].");
        }

        diagnosticPlayerWasAlive = Core.Player.IsAlive;
    }

    /// <summary>
    /// Determines whether an aura represents a vulnerability status.
    /// </summary>
    /// <param name="aura">Current-frame aura wrapper to classify.</param>
    /// <returns><see langword="true"/> when the English status name contains "Vulnerability Up".</returns>
    private static bool IsVulnerabilityAura(Aura aura)
    {
        return aura != null &&
               !string.IsNullOrWhiteSpace(aura.Name) &&
               aura.Name.Contains("Vulnerability Up", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes cast summaries older than the bounded attribution window.
    /// </summary>
    /// <param name="nowUtc">Current bot-thread observation time.</param>
    private static void RemoveExpiredMechanicContext(DateTime nowUtc)
    {
        DateTime oldestAllowed = nowUtc.AddSeconds(-MechanicContextWindowSeconds);
        while (RecentMechanicCasts.Count > 0 && RecentMechanicCasts.Peek().ObservedAtUtc < oldestAllowed)
        {
            RecentMechanicCasts.Dequeue();
        }
    }

    /// <summary>
    /// Formats recent encounter actions for a vulnerability or death marker.
    /// </summary>
    /// <param name="nowUtc">Current observation time used to report cast age.</param>
    /// <returns>A chronological context list, or "none" when no recent cast was observed.</returns>
    private static string FormatRecentMechanicContext(DateTime nowUtc)
    {
        if (RecentMechanicCasts.Count == 0)
        {
            return "none";
        }

        return string.Join("; ", RecentMechanicCasts.Select(cast =>
            $"{(nowUtc - cast.ObservedAtUtc).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}msAgo {cast.Summary}"));
    }

    /// <summary>
    /// Clears transient diagnostic snapshots without retaining frame-scoped RB wrappers.
    /// </summary>
    private static void ResetMechanicDiagnosticState()
    {
        TrackedMechanicCastsByCaster.Clear();
        TrackedVulnerabilityStacksByAura.Clear();
        RecentMechanicCasts.Clear();
        diagnosticPlayerWasAlive = false;
        diagnosticZoneId = 0;
        diagnosticSubZoneId = 0;
    }

    /// <summary>
    /// Formats a position with invariant decimals so captures remain diffable.
    /// </summary>
    /// <param name="value">World position to format.</param>
    /// <returns>The X/Y/Z coordinates rounded to three decimal places.</returns>
    private static string Format(Vector3 value)
    {
        return $"({value.X.ToString("F3", CultureInfo.InvariantCulture)}, " +
               $"{value.Y.ToString("F3", CultureInfo.InvariantCulture)}, " +
               $"{value.Z.ToString("F3", CultureInfo.InvariantCulture)})";
    }

    /// <summary>
    /// Formats a scalar with invariant decimals for stable heading evidence.
    /// </summary>
    /// <param name="value">Numeric value to format.</param>
    /// <returns>The value rounded to three decimal places.</returns>
    private static string Format(float value)
    {
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Immutable cast evidence retained beyond the frame that exposed the caster wrapper.
    /// </summary>
    /// <param name="ObservedAtUtc">UTC observation time used to age the entry.</param>
    /// <param name="Summary">Stable scalar cast identity copied from the current frame.</param>
    private readonly record struct RecentMechanicCast(DateTime ObservedAtUtc, string Summary);
}
