using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class HolminsterSwitch
{
    private static readonly Vector3 TesleenCenter = new(78f, 0f, -82f);
    private readonly CapabilityManagerHandle tesleenStackHandle = CapabilityManager.CreateNewHandle();
    private readonly Dictionary<uint, WaterWindow> waterWindows = new();
    private uint exorciseTarget;
    private DateTime exorciseCastEnd, exorciseUntil;
    private bool exorciseMarkerSeen, stackLeased;
    private Vector3? stackDestination;

    private sealed class WaterWindow
    {
        internal Vector3 Origin;
        internal DateTime Until;
        internal bool SeenObject;
    }

    private static bool TesleenCombat() => TreeRoot.IsRunning && DutyManager.InInstance &&
        WorldManager.ZoneId == (uint)ZoneId.HolminsterSwitch && WorldManager.SubZoneId == (uint)SubZoneId.TheAuction &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat;

    private static GameObject[] WaterObjects() => GameObjectManager.GetObjectsOfType<GameObject>(true, false)
        .Where(actor => actor.IsValid && actor.BaseId == 0x1EABF9 && actor.IsVisible).ToArray();

    private void RegisterTesleenAvoids()
    {
        // There are five 6y puddles (one stack and four
        // helpers). The visible event objects own their lifetime. Before an outer
        // object appears, retain its cast location through the observed spawn gap;
        // a bounded 26s fallback covers the captured 25s cast-to-disappearance.
        AvoidanceManager.AddAvoidObject<GameObject>(canRun: TesleenCombat,
            objectSelector: actor => actor.IsValid && actor.BaseId == 0x1EABF9 && actor.IsVisible,
            radiusProducer: _ => 6.5f);
        AvoidanceManager.AddAvoidLocation<WaterWindow>(
            canRun: () => TesleenCombat() && exorciseTarget == 0,
            leashPointProducer: () => TesleenCenter, leashRadius: 45f,
            radiusProducer: _ => 6.5f, locationProducer: water => water.Origin,
            collectionProducer: () => waterWindows.Values.Where(water => !water.SeenObject && DateTime.UtcNow < water.Until).ToArray());
    }

    private void UpdateTesleen()
    {
        DateTime now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid).ToArray();
        var puddles = WaterObjects();
        foreach (var helper in actors.Where(actor => Cast(actor, 0x233C, 15828)))
        {
            if (!waterWindows.ContainsKey(helper.ObjectId))
                waterWindows.Add(helper.ObjectId, new WaterWindow { Origin = helper.SpellCastInfo.CastLocation,
                    Until = now + helper.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(26) });
        }
        foreach (var pair in waterWindows.ToArray())
        {
            if (puddles.Any(puddle => puddle.Location.Distance2D(pair.Value.Origin) < 1f)) pair.Value.SeenObject = true;
            else if (pair.Value.SeenObject || now >= pair.Value.Until) waterWindows.Remove(pair.Key);
        }

        var boss = actors.FirstOrDefault(actor => Cast(actor, 0x278B, 15826));
        if (boss != null && exorciseTarget == 0)
        {
            exorciseTarget = boss.SpellCastInfo.TargetId;
            exorciseCastEnd = now + boss.SpellCastInfo.RemainingCastTime;
            // The captured action resolved about 1.4s after the bar. Retain the
            // stack until its marker disappears, with a bounded 2s fallback.
            exorciseUntil = exorciseCastEnd.AddSeconds(2);
            exorciseMarkerSeen = false;
            stackDestination = null;
        }
        if (exorciseTarget == 0) { ReleaseTesleenStack(); return; }
        var party = actors.Append(Core.Me).Where(actor => actor.IsAlive && actor.IsVisible &&
            (actor.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)actor.NpcId)))
            .GroupBy(actor => actor.ObjectId).Select(group => group.First()).ToArray();
        var marked = party.FirstOrDefault(actor => actor.ObjectId == exorciseTarget);
        if (marked == null) { ReleaseTesleenStack(); return; }
        bool markerReadable = marked.VfxContainer.IsValid;
        bool marker = markerReadable && marked.VfxContainer.LockOns.Any(icon => icon != null && icon.IsValid && icon.Id == 62);
        exorciseMarkerSeen |= marker;
        // A transiently unreadable VFX container is not proof that the stack
        // resolved; retain the existing bounded deadline in that case.
        if (now >= exorciseUntil || exorciseMarkerSeen && markerReadable && !marker && now >= exorciseCastEnd.AddMilliseconds(500))
        {
            // The marker's removal follows the stack impact. The observed puddle
            // normally appears at that point; retain its last origin if object
            // publication lags, without treating an unknown floor as safe.
            waterWindows[exorciseTarget] = new WaterWindow { Origin = marked.Location, Until = now.AddSeconds(26) };
            ReleaseTesleenStack();
            return;
        }
        if (bossMover?.IsCurrent != true || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseTesleenPosition();
            return;
        }

        bool self = exorciseTarget == Core.Me.ObjectId;
        var participants = party.Where(actor => actor.ObjectId != Core.Me.ObjectId).Select(actor => actor.Location).ToArray();
        if (self && participants.Length == 0) { ReleaseTesleenPosition(); return; }
        int Coverage(Vector3 point) => self ? participants.Count(p => p.Distance2D(point) <= 4.5f) :
            point.Distance2D(marked.Location) <= 4.5f ? 1 : 0;
        bool SafeDestination(Vector3 point) => point.Distance2D(TesleenCenter) < 19f &&
            waterWindows.Values.All(water => point.Distance2D(water.Origin) > 6.5f) &&
            !AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(point));
        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (Vector3 center in self ? participants : new[] { marked.Location })
        {
            candidates.Add(center);
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new Vector3(center.X + 4f * (float)Math.Sin(angle), Core.Me.Y,
                    center.Z + 4f * (float)Math.Cos(angle)));
            }
        }
        // With an NPC marked, the party can hold on the side facing
        // two incoming water circles. Prefer a stack point with room to leave
        // after impact; self-marked stacks keep companion coverage first.
        double WaterClearance(Vector3 point) => waterWindows.Values
            .Select(water => (double)point.Distance2D(water.Origin)).DefaultIfEmpty(double.PositiveInfinity).Min();
        var best = candidates.Where(point => Coverage(point) > 0 && SafeDestination(point) && BossSegmentSafe(point) &&
                now.AddSeconds(0.3 + Core.Me.Location.Distance2D(point) / 4.5) < exorciseCastEnd.AddSeconds(1.4))
            .OrderByDescending(Coverage).ThenByDescending(point => self ? 0 : WaterClearance(point))
            .ThenBy(point => point.Distance2D(Core.Me.Location))
            .Select(point => (Vector3?)point).FirstOrDefault();
        if (!best.HasValue) { ReleaseTesleenPosition(); return; }
        if (!stackDestination.HasValue || Coverage(stackDestination.Value) < Coverage(best.Value) ||
            !self && WaterClearance(best.Value) > WaterClearance(stackDestination.Value) + 1.0 ||
            !SafeDestination(stackDestination.Value) || !BossSegmentSafe(stackDestination.Value))
        {
            stackDestination = best;
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Exorcise target={0:X} self={1} destination={2} coverage={3}.",
                exorciseTarget, self, stackDestination, Coverage(best.Value));
        }
        CapabilityManager.Update(tesleenStackHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Tesleen stack participation");
        CapabilityManager.Update(tesleenStackHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Tesleen stack participation");
        stackLeased = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(stackDestination.Value) > 1f) ActionManager.StopCasting();
        // The marker can move after selection; a one-yalm arrival tolerance
        // can leave the player outside the intended 4.5-yalm coverage.
        bossMover.Request(stackDestination, 0.25f);
    }

    private void ReleaseTesleenPosition()
    {
        if (stackLeased && bossMover?.IsCurrent == true) bossMover.Request(null, 0);
        if (stackLeased)
        {
            CapabilityManager.Clear(tesleenStackHandle, CapabilityFlags.Movement, "Tesleen stack released");
            CapabilityManager.Clear(tesleenStackHandle, CapabilityFlags.GapCloser, "Tesleen stack released");
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Exorcise movement released.");
        }
        stackLeased = false;
        stackDestination = null;
    }

    private void ReleaseTesleenStack()
    {
        ReleaseTesleenPosition();
        exorciseTarget = 0;
        exorciseMarkerSeen = false;
        if (!TesleenCombat()) waterWindows.Clear();
    }
}
