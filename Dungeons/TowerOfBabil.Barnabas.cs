using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;

public partial class TowerOfBabil
{
    private static TowerOfBabil activeBarnabas;
    private readonly CapabilityManagerHandle barnabasHandle = CapabilityManager.CreateNewHandle();
    private ChrysalisMovement barnabasMover;
    private object barnabasOriginalProvider;
    private bool barnabasRepelLine, barnabasRepelCircle, barnabasStackLease;
    private uint barnabasPoleAction, barnabasPoleMarker, barnabasStackTarget;
    private DateTime barnabasPoleUntil, barnabasStackUntil, barnabasStackCastEnded;
    private DateTime nextBarnabasStackSample;
    private Vector3? barnabasStackDestination;

    // Pulse the same input owner even while OrderBot retains its coroutine; stop,
    // loading, death and a foreign mover cannot leave an old stack request active.
    internal static void CheckBarnabasLifecycle() => activeBarnabas?.UpdateBarnabas();
    internal static void ResetBarnabasActive() => activeBarnabas?.ResetBarnabas();

    private static BattleCharacter FindBarnabas() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
        .FirstOrDefault(a => a.IsValid && a.IsVisible && a.IsTargetable && a.BaseId == 0x33F7 && a.NpcId == EnemyNpc.Barnabas);

    private void UpdateBarnabas()
    {
        var boss = TreeRoot.IsRunning && WorldManager.ZoneId == (uint)Data.ZoneId.TheTowerOfBabil &&
            WorldManager.SubZoneId == (uint)SubZoneId.MagitekServicing && Core.Me != null && Core.Me.IsValid &&
            Core.Me.IsAlive && Core.Me.InCombat && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene
                ? FindBarnabas() : null;
        if (boss == null || !boss.IsAlive)
        {
            ResetBarnabas();
            return;
        }

        UpdateBarnabasPolarity(boss);
        if (barnabasMover != null && !barnabasMover.IsCurrent)
        {
            ReleaseBarnabasStack();
            barnabasMover.Detach();
            barnabasMover = null;
        }
        if (barnabasMover == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover) &&
            (barnabasOriginalProvider == null || ReferenceEquals(barnabasOriginalProvider, Navigator.NavigationProvider)))
        {
            barnabasOriginalProvider ??= Navigator.NavigationProvider;
            barnabasMover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: BarnabasSegmentSafe);
            Navigator.PlayerMover = barnabasMover;
        }
        if (barnabasMover?.IsCurrent != true)
        {
            ReleaseBarnabasStack();
            return;
        }

        UpdateBarnabasStack(boss);
        barnabasMover.Update();
    }

    private void UpdateBarnabasPolarity(BattleCharacter boss)
    {
        var now = DateTime.UtcNow;
        uint action = boss.CastingSpellId;
        bool line = action is EnemyAction.DynamicPoundNegative or EnemyAction.DynamicPoundPositive;
        bool circle = action is EnemyAction.DynamicScraplineNegative or EnemyAction.DynamicScraplinePositive;
        var markers = Core.Me.VfxContainer.IsValid
            ? Core.Me.VfxContainer.LockOns.Where(v => v != null && v.IsValid && (v.Id == 162 || v.Id == 163))
                .Select(v => (uint)v.Id).Distinct().ToArray() : Array.Empty<uint>();
        uint marker = markers.Length == 1 ? markers[0] : 0;
        if (line || circle)
        {
            // Marker 162 is positive and 163 negative. Captured same-pole Pound
            // pushed nine yalms; opposite poles pulled inward. Keep the decision
            // through forced motion, which outlives the visual cast by ~3s.
            if (barnabasPoleAction != action) barnabasPoleMarker = 0;
            barnabasPoleAction = action;
            if (marker != 0) barnabasPoleMarker = marker;
            barnabasPoleUntil = now.AddSeconds(5);
        }
        else if (now >= barnabasPoleUntil || (Core.Me.VfxContainer.IsValid && marker == 0))
        {
            barnabasPoleAction = barnabasPoleMarker = 0;
        }
        bool positiveBoss = barnabasPoleAction is EnemyAction.DynamicPoundPositive or EnemyAction.DynamicScraplinePositive;
        bool negativeBoss = barnabasPoleAction is EnemyAction.DynamicPoundNegative or EnemyAction.DynamicScraplineNegative;
        bool repel = positiveBoss && barnabasPoleMarker == 162 || negativeBoss && barnabasPoleMarker == 163;
        bool repelLine = repel && (barnabasPoleAction is EnemyAction.DynamicPoundPositive or EnemyAction.DynamicPoundNegative);
        bool repelCircle = repel && (barnabasPoleAction is EnemyAction.DynamicScraplinePositive or EnemyAction.DynamicScraplineNegative);
        if ((repelLine != barnabasRepelLine || repelCircle != barnabasRepelCircle) && LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Information($"[Babil Polarity] action={barnabasPoleAction} marker={barnabasPoleMarker} repelLine={repelLine} repelCircle={repelCircle}.");
        barnabasRepelLine = repelLine;
        barnabasRepelCircle = repelCircle;
    }

    private void UpdateBarnabasStack(BattleCharacter boss)
    {
        var now = DateTime.UtcNow;
        bool casting = boss.CastingSpellId == EnemyAction.ShockingForce && boss.SpellCastInfo != null;
        if (casting && barnabasStackTarget == 0)
        {
            barnabasStackTarget = boss.SpellCastInfo.TargetId;
            barnabasStackUntil = now.AddSeconds(7);
            barnabasStackCastEnded = DateTime.MinValue;
            Logger.Information($"[Babil Stack] target=0x{barnabasStackTarget:X} self={barnabasStackTarget == Core.Me.ObjectId}");
        }
        if (barnabasStackTarget != 0 && !casting && barnabasStackCastEnded == DateTime.MinValue)
            barnabasStackCastEnded = now;
        if (barnabasStackTarget == 0 || now >= barnabasStackUntil ||
            barnabasStackCastEnded != DateTime.MinValue && now >= barnabasStackCastEnded.AddSeconds(.8))
        {
            ReleaseBarnabasStack();
            barnabasStackTarget = 0;
            return;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseBarnabasStack();
            return;
        }

        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Append(Core.Me)
            .Where(a => a != null && a.IsValid && a.IsAlive && a.IsVisible &&
                (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var carrier = party.FirstOrDefault(a => a.ObjectId == barnabasStackTarget);
        if (carrier == null) { ReleaseBarnabasStack(); return; }
        bool self = carrier.ObjectId == Core.Me.ObjectId;
        var participants = party.Where(a => a.ObjectId != Core.Me.ObjectId).Select(a => a.Location).ToArray();
        if (self && participants.Length == 0) { ReleaseBarnabasStack(); return; }

        // Stack radius is six yalms. A five-yalm target gives arrival tolerance;
        // for a self marker, maximize the number of living participants covered.
        int Coverage(Vector3 point) => self ? participants.Count(p => p.Distance2D(point) <= 5f) :
            point.Distance2D(carrier.Location) <= 5f ? 1 : 0;
        var centers = self ? participants : new[] { carrier.Location };
        var candidates = new List<Vector3> { Core.Me.Location };
        if (self) candidates.Add(new Vector3(centers.Average(p => p.X), Core.Me.Y, centers.Average(p => p.Z)));
        foreach (var center in centers)
        {
            candidates.Add(center);
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new Vector3(center.X + 4f * (float)Math.Sin(angle), Core.Me.Y,
                    center.Z + 4f * (float)Math.Cos(angle)));
            }
        }
        var best = candidates.Where(p => Coverage(p) > 0 && BarnabasSegmentSafe(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (!best.HasValue) { ReleaseBarnabasStack(); return; }
        if (!barnabasStackDestination.HasValue || !BarnabasSegmentSafe(barnabasStackDestination.Value) ||
            Coverage(barnabasStackDestination.Value) < Coverage(best.Value))
            barnabasStackDestination = best;
        CapabilityManager.Update(barnabasHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Babil Shocking Force stack");
        barnabasStackLease = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(barnabasStackDestination.Value) > .5f)
            ActionManager.StopCasting();
        barnabasMover.Request(barnabasStackDestination, .5f);
        if (LoggingHelpers.MechanicDiagnosticsEnabled && now >= nextBarnabasStackSample)
        {
            nextBarnabasStackSample = now.AddMilliseconds(500);
            Logger.Information($"[Babil Stack] target=0x{barnabasStackTarget:X} self={self} player={Core.Me.Location} destination={barnabasStackDestination} coverage={Coverage(Core.Me.Location)} party=" +
                string.Join(";", party.Select(a => $"{a.ObjectId:X}:{a.Location}")));
        }
    }

    private static bool BarnabasSegmentSafe(Vector3 destination)
    {
        var start = Core.Me.Location;
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(destination) / .5f));
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            var point = new Vector3(start.X + (destination.X - start.X) * t, start.Y,
                start.Z + (destination.Z - start.Z) * t);
            if (point.Distance2D(ArenaCenter.Barnabas) > 14.5f ||
                AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private void ReleaseBarnabasStack()
    {
        if (barnabasStackLease && barnabasMover?.IsCurrent == true) barnabasMover.Request(null, 0);
        if (barnabasStackLease) CapabilityManager.Clear(barnabasHandle, CapabilityFlags.Movement, "Babil stack released");
        barnabasStackLease = false;
        barnabasStackDestination = null;
    }

    private void ResetBarnabas()
    {
        if (barnabasMover == null && barnabasOriginalProvider == null && !barnabasStackLease && barnabasStackTarget == 0 && barnabasPoleAction == 0)
        {
            if (ReferenceEquals(activeBarnabas, this)) activeBarnabas = null;
            return;
        }
        ReleaseBarnabasStack();
        barnabasMover?.Detach();
        barnabasMover = null;
        barnabasOriginalProvider = null;
        CapabilityManager.Clear(barnabasHandle, reason: "Babil boss lifecycle ended");
        barnabasRepelLine = barnabasRepelCircle = false;
        barnabasPoleAction = barnabasPoleMarker = barnabasStackTarget = 0;
        barnabasPoleUntil = barnabasStackUntil = barnabasStackCastEnded = DateTime.MinValue;
        if (ReferenceEquals(activeBarnabas, this)) activeBarnabas = null;
    }
}
