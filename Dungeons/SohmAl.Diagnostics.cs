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
/// Captures unresolved Sweet Scent, meteor and persistent-object signals without
/// introducing movement or retaining native wrappers. The normal CAST_START stream
/// does not include LockOns IDs, so those IDs require this gated diagnostic channel.
/// </summary>
public partial class SohmAl
{
    private readonly Dictionary<uint, string> signalState = new();
    private DateTime nextSignalSample;

    private void CaptureUnresolvedSignals(BattleCharacter[] actors, DateTime now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || now < nextSignalSample) return;
        nextSignalSample = now.AddMilliseconds(250);
        var observed = new HashSet<uint>();
        // The attached runtime omits the local player from BattleCharacter enumeration.
        // Include it explicitly so self-marked draw-in/meteor evidence is not silently
        // lost; deduplicate for runtimes that already return the player. Observation only.
        foreach (var actor in actors.Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a != null && a.IsValid && (IsBoss(a) || PartyActor(a)))
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
            // Marked intervals need positions as well as edges to distinguish forced
            // draw-in, bait travel and actual stack participation from stale input.
            if (changed || (markers.Length > 0 && markers != "invalid"))
                Logger.Information($"[SohmAl] SIGNAL actor={actor.ObjectId} npc={actor.NpcId} base={actor.BaseId} {signature} position={actor.Location} player={Core.Me.Location} forced={MovementManager.GenerallyOutOfControl} escaping={AvoidanceManager.IsRunningOutOfAvoid}.");
            signalState[actor.ObjectId] = signature;
        }
        foreach (var obj in GameObjectManager.GameObjects.Where(a => a.IsValid && a.NpcId == 2005287 && Math.Abs(a.Y - Core.Me.Y) < 10))
        {
            observed.Add(obj.ObjectId);
            string signature = $"object2005287 visible={obj.IsVisible} targetable={obj.IsTargetable} position={obj.Location}";
            if (!signalState.TryGetValue(obj.ObjectId, out var old) || old != signature)
                Logger.Information($"[SohmAl] SIGNAL actor={obj.ObjectId} {signature}.");
            signalState[obj.ObjectId] = signature;
        }
        foreach (uint missing in signalState.Keys.Where(id => !observed.Contains(id)).ToArray())
        {
            Logger.Information($"[SohmAl] SIGNAL actor={missing} disappeared.");
            signalState.Remove(missing);
        }
    }
}
