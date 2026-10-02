using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Navigation;
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
    private static readonly Dictionary<string, string> TrackedActorSignalSignatures = [];
    // Keep long casts through their estimated finish plus delayed follow-ups. Bounded
    // counts prevent helper bursts from growing memory; evictions are explicitly reported.
    private const int MechanicContextMaximumEntries = 128;
    private static readonly MechanicDiagnosticHistory<string> RecentMechanicCasts = new(MechanicContextMaximumEntries);
    private static readonly MechanicDiagnosticHistory<string> RecentMovement = new(32);
    private static DateTime nextMovementSampleUtc;
    private static DateTime movementAfterFailureUntilUtc;
    private static int failureSequence;
    private static DateTime nextMovementErrorUtc;
    private static bool mechanicDiagnosticsWereEnabled;
    private static bool diagnosticPlayerWasAlive;
    private static ushort diagnosticZoneId;
    private static uint diagnosticSubZoneId;
    private static ushort lastZoneId = 0;
    private static uint lastSubZoneId = 0;

    /// <summary>
    /// Gets whether the compile-time developer diagnostic collector is currently active on the
    /// bot thread. Encounter-local capture code uses this runtime state so expensive evidence
    /// gathering remains behind the same non-persisted switch and shared stop lifecycle.
    /// </summary>
    internal static bool MechanicDiagnosticsEnabled => mechanicDiagnosticsWereEnabled;

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
                Logger.Information("[MechanicDiag] Disabled; transient cast, vulnerability, and actor-watch history cleared.");
            }

            ResetMechanicDiagnosticState();
            mechanicDiagnosticsWereEnabled = false;
            return;
        }

        if (!mechanicDiagnosticsWereEnabled)
        {
            ResetMechanicDiagnosticState();
            mechanicDiagnosticsWereEnabled = true;
            Logger.Information(
                "[MechanicDiag] Enabled; recording encounter casts, vulnerability gains, deaths, and registered actor watches. Cast history: estimated finish + 30s (128 entries); failure movement: 15s before/3s after at 500ms.");
        }

        if (Core.Player == null || !Core.Player.IsValid)
        {
            TrackedMechanicCastsByCaster.Clear();
            TrackedVulnerabilityStacksByAura.Clear();
            TrackedActorSignalSignatures.Clear();
            RecentMechanicCasts.Clear();
            ResetMovementDiagnostics();
            diagnosticPlayerWasAlive = false;
            return;
        }

        if (diagnosticZoneId != WorldManager.ZoneId || diagnosticSubZoneId != WorldManager.SubZoneId)
        {
            TrackedMechanicCastsByCaster.Clear();
            TrackedVulnerabilityStacksByAura.Clear();
            TrackedActorSignalSignatures.Clear();
            RecentMechanicCasts.Clear();
            ResetMovementDiagnostics();
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
        CaptureMovementContext(nowUtc);
        LogVulnerabilityChanges(nowUtc);
        LogPlayerDeath(nowUtc);
    }

    /// <summary>
    /// Logs a change-only snapshot of selected actors under the shared mechanic-diagnostic switch.
    /// </summary>
    /// <remarks>
    /// Duty handlers provide only a stable scope and actor selector. This method owns current-frame
    /// enumeration, scalar serialization, party context, deduplication, and lifecycle cleanup so
    /// investigations can inspect targets, statuses, VFX, tethers, and transforms without retaining
    /// RB object wrappers. Callers should keep the observation window encounter-bounded because actor
    /// transforms can legitimately change every tick.
    /// </remarks>
    /// <param name="scope">Stable encounter-specific label used to deduplicate this actor watch.</param>
    /// <param name="actorSelector">Selects the current-frame actors whose mechanic state should be captured.</param>
    internal static void LogActorSignalChanges(string scope, Func<BattleCharacter, bool> actorSelector)
    {
        if (!mechanicDiagnosticsWereEnabled || Core.Player == null || !Core.Player.IsValid)
        {
            return;
        }

        List<BattleCharacter> watchedActors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor != null && actor.IsValid)
            .Where(actorSelector)
            .OrderBy(actor => actor.ObjectId)
            .ToList();

        // RebornBuddy's BattleCharacter enumeration can omit Core.Player even though marker VFX are
        // attached to that wrapper. Include it explicitly when selected, while de-duplicating by
        // ObjectId so encounter captures can distinguish self-target markers from party markers.
        if (actorSelector(Core.Player) && watchedActors.All(actor => actor.ObjectId != Core.Player.ObjectId))
        {
            watchedActors.Add(Core.Player);
        }

        string signature = string.Join("; ", watchedActors
            .OrderBy(actor => actor.ObjectId)
            .Select(DescribeActorSignal));
        if (TrackedActorSignalSignatures.TryGetValue(scope, out string previousSignature) &&
            signature == previousSignature)
        {
            return;
        }

        TrackedActorSignalSignatures[scope] = signature;
        string party = string.Join("; ", PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid)
            .OrderBy(actor => actor.ObjectId)
            .Select(actor => $"0x{actor.ObjectId:X8}@{Format(actor.Location)}"));
        Logger.Information(
            $"[MechanicDiag] ACTOR_STATE scope={scope} actors=[{signature}] " +
            $"player={Format(Core.Player.Location)} party=[{party}].");
    }

    /// <summary>
    /// Clears one reusable actor watch when its encounter-specific observation window closes.
    /// </summary>
    /// <param name="scope">The same stable label passed to <see cref="LogActorSignalChanges"/>.</param>
    internal static void ClearActorSignalWatch(string scope)
    {
        TrackedActorSignalSignatures.Remove(scope);
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

            RecentMechanicCasts.Add(nowUtc,
                MechanicDiagnosticHistory<string>.CastExpiry(nowUtc, spell.RemainingCastTime.TotalSeconds),
                summary + $" estimatedRemainingMs={spell.RemainingCastTime.TotalMilliseconds:F0}");
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
            LogFailureMovement(nowUtc, "vulnerability");
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
            LogFailureMovement(nowUtc, "death");
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
        RecentMechanicCasts.Prune(nowUtc);
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

        return $"capacityEvictions={RecentMechanicCasts.Dropped}; " + string.Join("; ", RecentMechanicCasts.Snapshot(nowUtc).Select(cast =>
            $"{(nowUtc - cast.ObservedAtUtc).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}msAgo {cast.Value}"));
    }

    /// <summary>
    /// Clears transient diagnostic snapshots without retaining frame-scoped RB wrappers.
    /// </summary>
    private static void ResetMechanicDiagnosticState()
    {
        TrackedMechanicCastsByCaster.Clear();
        TrackedVulnerabilityStacksByAura.Clear();
        TrackedActorSignalSignatures.Clear();
        RecentMechanicCasts.Clear();
        ResetMovementDiagnostics();
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
    /// Copies all generally useful actor-side mechanic signals into one stable scalar description.
    /// </summary>
    /// <param name="actor">Valid current-frame actor selected by a duty diagnostic watch.</param>
    /// <returns>A diffable actor description that does not retain any frame-scoped wrappers.</returns>
    private static string DescribeActorSignal(BattleCharacter actor)
    {
        string auras = actor.Auras == null || !actor.Auras.IsValid
            ? "invalid"
            : string.Join(",", actor.Auras
                .OrderBy(aura => aura.Id)
                .Select(aura => $"{aura.Id}:{aura.Value}:0x{aura.CasterId:X8}"));
        string vfx = actor.VfxContainer.IsValid
            ? string.Join(",", actor.VfxContainer.Vfx
                .Where(entry => entry != null && entry.IsValid)
                .OrderBy(entry => entry.Id)
                .Select(entry => entry.Id.ToString(CultureInfo.InvariantCulture)))
            : "invalid";
        string tethers = actor.VfxContainer.IsValid
            ? string.Join(",", (actor.VfxContainer.Tethers ?? [])
                .OrderBy(tether => tether.Id)
                .ThenBy(tether => tether.TargetId)
                .Select(tether => $"{tether.Id}:0x{tether.TargetId:X8}:{tether.Progress}"))
            : "invalid";

        return $"0x{actor.ObjectId:X8}/0x{actor.BaseId:X} " +
            $"loc={Format(actor.Location)} heading={Format(actor.Heading)} " +
            $"target=0x{actor.CurrentTargetId:X8} visible={actor.IsVisible} targetable={actor.IsTargetable} " +
            $"status=0x{Convert.ToUInt64(actor.StatusFlags, CultureInfo.InvariantCulture):X} " +
            $"character=0x{Convert.ToUInt64(actor.CharacterStatusFlags, CultureInfo.InvariantCulture):X} " +
            $"cast={actor.CastingSpellId} avfx={Format(actor.AVFX.Center)} " +
            $"omen={Format(actor.OmenMatrix.Center)} lockOn={Format(actor.LockOn.Center)} " +
            $"auras=[{auras}] vfx=[{vfx}] tethers=[{tethers}]";
    }

    // Sampling is passive and bot-thread only. SlideMover exposes its last commanded
    // point, not an authoritative active goal or movement owner; label it accordingly.
    // Existing avoidance/capability logs remain the authority for control transitions.
    private static void CaptureMovementContext(DateTime nowUtc)
    {
        if (nowUtc < nextMovementSampleUtc) return;
        nextMovementSampleUtc = nowUtc.AddMilliseconds(500);
        RecentMovement.Prune(nowUtc);
        if (!Core.Player.InCombat && nowUtc > movementAfterFailureUntilUtc && RecentMechanicCasts.Count == 0) return;
        try
        {
            string lastCommand = Navigator.PlayerMover is SlideMover slide
                ? Format(slide.LastMoveLocation) : "unavailable";
            var avoids = AvoidanceManager.Avoids;
            // Read cached scalar geometry only; never evaluate avoid producers or pulse managers.
            string shapes = string.Join(";", avoids.Take(8).Select(a =>
                $"{a.GetType().Name}/{a.Object?.GetType().Name ?? "none"}@{Format(a.Location)}"));
            string sample = $"player={Format(Core.Player.Location)} heading={Format(Core.Player.Heading)} " +
                $"hp={Core.Player.CurrentHealth}/{Core.Player.MaxHealth} combat={Core.Player.InCombat} " +
                $"target=0x{Core.Player.CurrentTargetId:X8} mover={Navigator.PlayerMover?.GetType().Name ?? "none"} " +
                $"lastMoveCommand={lastCommand} escapingAvoid={AvoidanceManager.IsRunningOutOfAvoid} " +
                $"avoidCount={avoids.Count} shapesFirst8=[{shapes}]";
            RecentMovement.Add(nowUtc, nowUtc.AddSeconds(15), sample);
            if (nowUtc <= movementAfterFailureUntilUtc)
                Logger.Information($"[MechanicDiag] MOVEMENT_AFTER failure={failureSequence} utc={nowUtc:O} {sample}");
        }
        catch (Exception ex)
        {
            // Diagnostics cannot interrupt the encounter if an optional host surface vanishes.
            if (nowUtc >= nextMovementErrorUtc)
            {
                nextMovementErrorUtc = nowUtc.AddSeconds(30);
                Logger.Warning($"[MechanicDiag] MOVEMENT_UNAVAILABLE {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void LogFailureMovement(DateTime nowUtc, string reason)
    {
        failureSequence++;
        Logger.Warning($"[MechanicDiag] MOVEMENT_CONTEXT failure={failureSequence} reason={reason} " +
            $"samples={RecentMovement.Count} capacityEvictions={RecentMovement.Dropped} " +
            "windowSeconds=15 sampleIntervalMs=500; lastMoveCommand may be stale; cast context is not hit attribution.");
        foreach (var entry in RecentMovement.Snapshot(nowUtc))
            Logger.Information($"[MechanicDiag] MOVEMENT_BEFORE failure={failureSequence} utc={entry.ObservedAtUtc:O} {entry.Value}");
        movementAfterFailureUntilUtc = nowUtc.AddSeconds(3);
    }

    private static void ResetMovementDiagnostics()
    {
        RecentMovement.Clear();
        nextMovementSampleUtc = DateTime.MinValue;
        movementAfterFailureUntilUtc = DateTime.MinValue;
        nextMovementErrorUtc = DateTime.MinValue;
        failureSequence = 0;
    }
}
