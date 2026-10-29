using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Captures unresolved tether transfer, prison and telegraph-origin signals without
/// introducing movement or retaining native wrappers. The normal CAST_START stream
/// does not include LockOns IDs, so those IDs require this gated diagnostic channel.
/// </summary>
public partial class Aery
{
    private readonly Dictionary<uint, string> signalState = new();
    private DateTime nextSignalSample;

    private void CaptureUnresolvedSignals(BattleCharacter[] actors, DateTime now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || now < nextSignalSample)
            return;
        nextSignalSample = now.AddMilliseconds(250);
        var observed = new HashSet<uint>();
        // The attached runtime omits the local player from BattleCharacter enumeration.
        // Include it explicitly so self-marked tether/prison evidence is not silently
        // lost; deduplicate for runtimes that already return the player. Observation only.
        foreach (var actor in actors.Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a != null && a.IsValid && (IsBoss(a) || PartyActor(a) || a.BaseId is 0xEA8 or 0x1144 or 0x3972 or 0x233C or 0x39CB or 0x39CC or 0x39CD))
            .GroupBy(a => a.ObjectId).Select(g => g.First()))
        {
            observed.Add(actor.ObjectId);
            var markers = actor.VfxContainer.IsValid
                ? string.Join(",", actor.VfxContainer.LockOns.Where(v => v != null && v.IsValid).Select(v => v.Id).OrderBy(v => v))
                : "invalid";
            var auras = actor.Auras != null && actor.Auras.IsValid
                ? string.Join(",", actor.Auras.OrderBy(a => a.Id).Select(a => $"{a.Id}:{a.Value}")) : "invalid";
            string signature = $"markers=[{markers}] auras=[{auras}] visible={actor.IsVisible} targetable={actor.IsTargetable} cast={actor.CastingSpellId}";
            bool changed = !signalState.TryGetValue(actor.ObjectId, out var old) || old != signature;
            // Marked and casting intervals need positions as well as edges to distinguish forced
            // tether transfer, imprisonment and actual stack participation from stale input.
            if (changed || HasLightningRod(actor) || Casting(actor) || (markers.Length > 0 && markers != "invalid"))
                Logger.Information($"[Aery] SIGNAL actor={actor.ObjectId} npc={actor.NpcId} base={actor.BaseId} {signature} position={actor.Location} heading={actor.Heading} target={actor.CurrentTargetId} castTarget={(Casting(actor) ? actor.SpellCastInfo.TargetId : 0)} omenOrigin={actor.OmenMatrix.Center} matrix=[{actor.OmenMatrix.M00},{actor.OmenMatrix.M02},{actor.OmenMatrix.M20},{actor.OmenMatrix.M22}] player={Core.Me.Location} forced={MovementManager.GenerallyOutOfControl} escaping={AvoidanceManager.IsRunningOutOfAvoid}.");
            signalState[actor.ObjectId] = signature;
        }
        foreach (uint missing in signalState.Keys.Where(id => !observed.Contains(id)).ToArray())
        {
            Logger.Information($"[Aery] SIGNAL actor={missing} disappeared.");
            signalState.Remove(missing);
        }
    }
}
