using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;

public partial class HolminsterSwitch
{
    private static HolminsterSwitch active;
    private ChrysalisMovement bossMover;
    private bool? savedBossSideStep;
    private static readonly Vector3 DissonanceCenter = new(-15f, 0f, 240f);
    private readonly Dictionary<uint, OrbCross> orbCrosses = new();
    private readonly Dictionary<uint, OrbCross> orbLines = new();
    private OrbCross chargeWindow;
    private OrbCross gibbetWindow;
    private sealed class OrbCross
    {
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Opens;
        internal DateTime Until;
    }
    private static readonly Vector2[] CrossPoints =
    [
        new(-3.5f, 40.5f), new(3.5f, 40.5f), new(3.5f, 3.5f), new(40.5f, 3.5f),
        new(40.5f, -3.5f), new(3.5f, -3.5f), new(3.5f, -40.5f), new(-3.5f, -40.5f),
        new(-3.5f, -3.5f), new(-40.5f, -3.5f), new(-40.5f, 3.5f), new(-3.5f, 3.5f)
    ];
    private static readonly Vector2[] ChargePoints =
    [new(4.5f, 40.5f), new(-4.5f, 40.5f), new(-4.5f, -0.5f), new(4.5f, -0.5f)];
    private static readonly Vector2[] LinePoints =
    [new(2.5f, 40.5f), new(-2.5f, 40.5f), new(-2.5f, -0.5f), new(2.5f, -0.5f)];

    private static bool DissonanceCombat() => TreeRoot.IsRunning && DutyManager.InInstance &&
        WorldManager.ZoneId == (uint)ZoneId.HolminsterSwitch && WorldManager.SubZoneId == (uint)SubZoneId.TheWound &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat;

    private static bool Cast(BattleCharacter actor, uint baseId, uint action) => actor.IsValid &&
        actor.BaseId == baseId && actor.IsCasting && actor.CastingSpellId == action;

    protected override Task<bool> EnterDungeonAsync()
    {
        active = this;
        // Gibbet resolves before the long cross. Keep its circle through the
        // delayed hit, then register the cross so the overlap stays traversable.
        AvoidanceManager.AddAvoidLocation(canRun: () => DissonanceCombat() && gibbetWindow != null && DateTime.UtcNow < gibbetWindow.Until,
            // SideStep can query location producers even while CanRun is false.
            radius: 8.5f, locationProducer: () => gibbetWindow?.Origin ?? DissonanceCenter);
        AvoidanceManager.AddAvoidPolygon<OrbCross>(
            condition: () => DissonanceCombat() && (gibbetWindow == null || DateTime.UtcNow >= gibbetWindow.Until),
            leashPointProducer: () => DissonanceCenter, leashRadius: 60f,
            rotationProducer: cross => -cross.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => CrossPoints, locationProducer: cross => cross.Origin,
            collectionProducer: () => orbCrosses.Values.Where(cross => DateTime.UtcNow < cross.Until).ToArray(),
            priority: AvoidancePriority.High);
        // Light Shot resolves about one second after the cast ends.
        AvoidanceManager.AddAvoidPolygon<OrbCross>(DissonanceCombat,
            leashPointProducer: () => DissonanceCenter, leashRadius: 60f,
            rotationProducer: line => -line.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => LinePoints, locationProducer: line => line.Origin,
            collectionProducer: () => orbLines.Values.Where(line => DateTime.UtcNow < line.Until).ToArray(),
            priority: AvoidancePriority.High);
        // Thumbscrew's cast location, not the boss heading at discovery, points
        // to the charge destination in the baseline. Native avoidance owns egress.
        AvoidanceManager.AddAvoidPolygon<OrbCross>(DissonanceCombat,
            leashPointProducer: () => DissonanceCenter, leashRadius: 60f,
            rotationProducer: charge => -charge.Heading, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: _ => ChargePoints, locationProducer: charge => charge.Origin,
            collectionProducer: () => chargeWindow != null && DateTime.UtcNow < chargeWindow.Until
                ? new[] { chargeWindow } : Array.Empty<OrbCross>(), priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(DissonanceCombat,
            actor => Cast(actor, 0x278A, 15815), () => DissonanceCenter, 50f, 0f, 40.5f, 92f);
        AvoidanceHelpers.AddAvoidDonut(DissonanceCombat, () => DissonanceCenter,
            outerRadius: 60f, innerRadius: 19f, priority: AvoidancePriority.High);
        RegisterTesleenAvoids();
        RegisterPhiliaAvoids();
        return Task.FromResult(false);
    }

    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    public static void CheckLifecycle()
    {
        if (active == null) return;
        active.UpdateOrbCrosses();
        if ((!DissonanceCombat() && !TesleenCombat() && !PhiliaCombat()) || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            active.ReleaseTesleenStack();
            active.ReleasePhilia();
            active.ReleaseBoss();
            return;
        }
        uint bossBase = DissonanceCombat() ? 0x278Au : TesleenCombat() ? 0x278Bu : 0x278Cu;
        if (!GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.BaseId == bossBase)) return;
        if (active.savedBossSideStep == null && SidestepPlugin != null)
        {
            active.savedBossSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Boss {0:X} acquired SideStep lease; previous={1}, enabled={2}.", bossBase, active.savedBossSideStep, SidestepPlugin.Enabled);
        }
        if (active.bossMover != null && !active.bossMover.IsCurrent)
        {
            active.bossMover.Detach();
            active.bossMover = null;
        }
        if (active.bossMover == null && !AvoidanceManager.IsRunningOutOfAvoid &&
            !MovementManager.GenerallyOutOfControl && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            // The existing channel arbitrates escape cleanup and prevents a
            // successor combat waypoint crossing an active hazard after a dodge.
            active.bossMover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: BossSegmentSafe,
                prioritizeRequestedHazardExit: () => active != null && (active.orbRouteOwned || active.knoutEscapeOwned),
                fenceNativeNavigationWhileOwned: true);
            Navigator.PlayerMover = active.bossMover;
        }
        if (TesleenCombat()) { active.ReleasePhilia(); active.UpdateTesleen(); }
        else if (DissonanceCombat()) { active.ReleasePhilia(); active.ReleaseTesleenStack(); active.UpdateOrbRoute(); }
        else { active.ReleaseTesleenStack(); active.ReleaseOrbRoute(); active.UpdatePhilia(); }
        active.bossMover?.Update();
    }

    private static bool BossSegmentSafe(Vector3 waypoint)
    {
        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        Vector3 from = Core.Me.Location;
        float dx = waypoint.X - from.X, dz = waypoint.Z - from.Z;
        int count = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.5));
        for (int i = 0; i <= count; i++)
        {
            float t = (float)i / count;
            var point = new Vector3(from.X + dx * t, from.Y, from.Z + dz * t);
            if (avoids.Any(avoid => avoid.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private void UpdateOrbCrosses()
    {
        if (!DissonanceCombat()) { orbCrosses.Clear(); orbLines.Clear(); chargeWindow = gibbetWindow = null; return; }
        DateTime now = DateTime.UtcNow;
        var gibbet = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => Cast(actor, 0x278A, 15816));
        if (gibbet != null && (gibbetWindow == null || now >= gibbetWindow.Until))
            gibbetWindow = new OrbCross { Origin = gibbet.Location,
                Until = now + gibbet.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5) };
        else if (gibbetWindow != null && now >= gibbetWindow.Until) gibbetWindow = null;
        // A cast started just before Gibbet can delay native avoidance
        // for most of the cast, leaving too little time to exit.
        if (gibbetWindow != null && Core.Me.IsCasting && Core.Me.Location.Distance2D(gibbetWindow.Origin) < 8.5f)
        {
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Interrupting cast inside Gibbet Cage at {0}.", Core.Me.Location);
            ActionManager.StopCasting();
        }
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => Cast(actor, 0x2896, 15822)))
        {
            // Orb status was observed about 0.8s after the cast's estimated end. Retain the scalar origin and
            // heading for 1.5s after that end so native pathing cannot treat the
            // delayed impact as empty floor. Confirm the tail against effects.
            if (!orbCrosses.TryGetValue(actor.ObjectId, out var prior) || now >= prior.Until)
            {
                orbCrosses[actor.ObjectId] = new OrbCross { Origin = actor.Location, Heading = actor.Heading,
                    Opens = now + actor.SpellCastInfo.RemainingCastTime - TimeSpan.FromSeconds(0.35),
                    Until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5) };
            }
        }
        foreach (uint id in orbCrosses.Where(pair => now >= pair.Value.Until).Select(pair => pair.Key).ToArray())
            orbCrosses.Remove(id);
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => Cast(actor, 0x2896, 15819)))
        {
            if (!orbLines.TryGetValue(actor.ObjectId, out var prior) || now >= prior.Until)
            {
                orbLines[actor.ObjectId] = new OrbCross { Origin = actor.Location, Heading = actor.Heading,
                    Opens = now + actor.SpellCastInfo.RemainingCastTime - TimeSpan.FromSeconds(0.35),
                    Until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5) };
            }
        }
        foreach (uint id in orbLines.Where(pair => now >= pair.Value.Until).Select(pair => pair.Key).ToArray())
            orbLines.Remove(id);
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => Cast(actor, 0x278A, 15814));
        if (boss != null && (chargeWindow == null || now >= chargeWindow.Until))
        {
            var target = boss.SpellCastInfo.CastLocation;
            // Two boss-sourced gains landed after this cast's estimated end,
            // one about 1.5s later. Retain the original lane for 2s to leave
            // half a second for sampling and delivery uncertainty.
            chargeWindow = new OrbCross { Origin = boss.Location,
                Heading = (float)Math.Atan2(target.X - boss.X, target.Z - boss.Z),
                Opens = now + boss.SpellCastInfo.RemainingCastTime - TimeSpan.FromSeconds(0.35),
                Until = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2) };

        }
        else if (chargeWindow != null && now >= chargeWindow.Until) chargeWindow = null;
    }

    private void ReleaseBoss()
    {
        ReleaseOrbRoute();
        orbForecast = null;
        orbForecastCreated = default;
        orbForecastWave = orbAttemptedKey = null;
        orbCrosses.Clear();
        orbLines.Clear();
        chargeWindow = null;
        gibbetWindow = null;

        bossMover?.Detach(MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid);
        bossMover = null;
        if (savedBossSideStep.HasValue && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = savedBossSideStep.Value;
        if (savedBossSideStep.HasValue)
            ff14bot.Helpers.Logging.WriteDiagnostic("[Holminster] Boss SideStep lease released; enabled={0}.", SidestepPlugin?.Enabled);
        savedBossSideStep = null;
    }

    public static void ResetActive()
    {
        if (active == null) return;
        active.ReleaseTesleenStack();
        active.ReleasePhilia();
        active.ReleaseBoss();
        active = null;
    }
}
