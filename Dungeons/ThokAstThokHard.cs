using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns Ravana's story-trial encounter separately from the Extreme duty.
/// Native avoidance selects escape paths from scalar geometry; one input channel also
/// stages knockbacks toward surviving rails. OrderBot and the routine retain combat.
/// </summary>
public sealed class ThokAstThokHard : AbstractDungeon
{
    // October 30 Global capture: EB4 is the boss; 13D2/1201/1335 are untargetable
    // choreography helpers sharing name row 3660. Never use that name row as a boss filter.
    private const uint BossBase = 0xEB4;
    private const uint IronGate = 0x10A1;
    private const uint Prelude = 3719, Slaughter = 3723, SlaughterLine = 3725;
    private const uint PreludeLine = 5052, PreludeDonut = 3722, HelperDonut = 5059;
    private const uint SwiftDonut = 5066, SwiftIn = 4769, SwiftOut = 4770;
    private const uint Pillars = 3738, RoseLine = 3740;
    private const float ArenaRadius = 19.5f; // Reference 20y arena, half-yalm wall inset.
    private static ThokAstThokHard current;
    private readonly Dictionary<(uint, uint), Hazard> hazards = [];
    private readonly Dictionary<uint, uint> casting = [];
    private Hazard[] circles = [], polygons = [];
    private Vector3[] donutOrigins = [];
    private ChrysalisMovement movement;
    private bool active, sideStepBorrowed, previousSideStep, leased, unsupportedMover;
    private bool servicingRoutine, targetlessCombat, travelling;
    private DateTime nextSnapshot, knockbackUntil, navigationRefresh;
    private int previousGateCount = -1;
    private Vector3? knockbackPoint;
    private static readonly CapabilityFlags[] PositionFlags =
        [CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser];

    // These are detached scalar envelopes. Feeding BattleCharacter helpers to RB also
    // adds an implicit combat-range attraction toward their peripheral positions; the
    // baseline produced continuous no-path failures. Stable envelopes avoid that goal.
    private sealed class Hazard
    {
        internal Vector3 Origin;
        internal float Heading, Radius;
        internal Vector2[] Points;
        internal bool Donut;
        internal DateTime Expires;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)432;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        AvoidanceManager.AddAvoidLocation<Hazard>(() => active, () => Vector3.Zero,
            ArenaRadius, h => h.Radius, h => h.Origin, () => circles);
        AvoidanceManager.AddAvoidPolygon<Hazard>(() => active, () => Vector3.Zero,
            ArenaRadius, h => -h.Heading, _ => 1, _ => 8, h => h.Points,
            h => h.Origin, () => polygons);
        // Half a yalm inside the 6y inner boundary. Keep one envelope for the
        // coincident boss/helper donut instead of double-registering the same ring.
        AvoidanceHelpers.AddAvoidDonut(() => active, () => donutOrigins, 40, 5.5);
        AvoidanceHelpers.AddAvoidDonut(() => active, () => Vector3.Zero, 80, ArenaRadius);
        Logger.Information("[ThokHard] Registered scalar slaughter geometry, native-input cleanup and surviving-rail knockback staging.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (servicingRoutine)
            return false;
        Update();
        if (targetlessCombat && !travelling && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            // OrderBot does not schedule CombatLogic without a hostile target. Reuse
            // the established targetless bridge so the routine can heal/mitigate or
            // release its own input during Ravana's disappearances. Never select job
            // actions here, and never recursively enter this encounter through hooks.
            servicingRoutine = true;
            try { await CommonTasks.ExecuteCoroutine(BrainBehavior.CombatLogic); }
            finally { servicingRoutine = false; }
            Update(); // Stop, death, loading and target changes revoke the old phase.
        }
        return travelling && !AvoidanceManager.IsRunningOutOfAvoid;
    }

    /// <summary>Updates this encounter on the bot-thread pulse while a combat coroutine retains TreeStart.</summary>
    public static void CheckLifecycle() => current?.Update();

    /// <summary>Releases only this encounter's borrowed input and routine capabilities on stop or disable.</summary>
    public static void ResetActive()
    {
        current?.Reset();
        current = null;
    }

    private void Update()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != 432 || !DutyManager.InInstance
            || Core.Me == null || !Core.Me.IsAlive || CommonBehaviors.IsLoading)
        {
            Reset();
            return;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid).ToArray();
        var boss = actors.FirstOrDefault(a => a.BaseId == BossBase && a.IsAlive);
        active = boss != null && Core.Me.InCombat;
        if (!active)
        {
            Reset();
            return;
        }
        targetlessCombat = (!boss.IsTargetable || !boss.CanAttack) &&
            (Core.Me.CurrentTarget == null || !Core.Me.CurrentTarget.IsValid || !Core.Me.CurrentTarget.CanAttack);
        if (!sideStepBorrowed && SidestepPlugin != null)
        {
            previousSideStep = SidestepPlugin.Enabled;
            sideStepBorrowed = true;
            SidestepPlugin.Enabled = false;
        }
        if (movement == null && !unsupportedMover)
        {
            if (Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            {
                // Reuse the established single input/provider owner. Its navigation
                // gate also fences anti-stuck pulses and suspended route callbacks;
                // a mover-only wrapper cannot arbitrate those direct input paths.
                movement = new ChrysalisMovement(Navigator.PlayerMover);
                Navigator.PlayerMover = movement;
            }
            else
            {
                unsupportedMover = true;
                Logger.Warning("[ThokHard] Unsupported input owner; do not stack another movement controller.");
            }
        }
        var now = DateTime.UtcNow;
        if (now >= nextSnapshot)
        {
            nextSnapshot = now.AddMilliseconds(100);
            Snapshot(actors, boss, now);
        }
        if (navigationRefresh != default && now >= navigationRefresh)
        {
            navigationRefresh = default;
            AvoidanceManager.ResetNavigation();
            Logger.Information("[ThokHard] Refreshed local collision after rail lifecycle change.");
        }
        if (now >= knockbackUntil)
            knockbackPoint = null;
        Vector3? request = knockbackPoint.HasValue && ApproachClear(knockbackPoint.Value) ? knockbackPoint : null;
        UpdatePositionLease(request.HasValue);
        movement?.Request(request, 0.6f);
        movement?.Update();
        travelling = request.HasValue && movement?.IsCurrent == true && Core.Me.Location.Distance2D(request.Value) > 0.6f;
    }

    private void Snapshot(BattleCharacter[] actors, BattleCharacter boss, DateTime now)
    {
        foreach (var key in hazards.Where(h => h.Value.Expires <= now).Select(h => h.Key).ToArray())
            hazards.Remove(key);
        var gates = actors.Where(a => a.BaseId == IronGate && a.IsAlive).ToArray();
        // The baseline lost the north and northwest rail actors at the ultimate, then
        // Pillars knocked the player through that exact gap. Rebuild collision once
        // after a stable lifecycle change, never once per avoid or movement request.
        if (previousGateCount >= 0 && gates.Length != previousGateCount)
            navigationRefresh = now.AddMilliseconds(250);
        previousGateCount = gates.Length;
        foreach (var actor in actors)
        {
            uint action = actor.IsCasting ? actor.CastingSpellId : 0;
            if (action == 0)
            {
                casting.Remove(actor.ObjectId);
                continue;
            }
            bool first = !casting.TryGetValue(actor.ObjectId, out uint old) || old != action;
            casting[actor.ObjectId] = action;
            if (!first || actor.NpcId != 3660)
                continue;
            DateTime finish = now + actor.SpellCastInfo.RemainingCastTime;
            var h = new Hazard { Origin = actor.Location, Heading = actor.Heading,
                Expires = finish.AddSeconds(2) };
            switch (action)
            {
                case Prelude:
                case SwiftOut:
                    h.Radius = 15.5f; // 15y circle plus the default half-yalm margin.
                    break;
                case Slaughter:
                    // The captured rear position survived; the 270-degree sector leaves
                    // a rear wedge. Add two degrees and half a yalm to its outer reach.
                    h.Points = Sector(40.5f, 137);
                    break;
                case PreludeLine:
                case SlaughterLine:
                case RoseLine:
                    // 8y width + 0.5y on each side. The first gain occurred after generic
                    // cast cleanup; keep the envelope through delayed damage application.
                    h.Points = Rectangle(9, action == SlaughterLine ? 44.5f : 40.5f);
                    if (action == SlaughterLine)
                        h.Expires = finish.AddSeconds(10);
                    break;
                case PreludeDonut:
                case HelperDonut:
                case SwiftDonut:
                case SwiftIn:
                    // Live 3722/5059 and 4769/5066 are coincident parent/helper pairs.
                    h.Donut = true;
                    break;
                case Pillars:
                    // A 3y ray toward a surviving gate lands against its intact rail
                    // under the 20y knockback. Do not follow an unrelated party member.
                    knockbackPoint = gates.Select(g => RailPosition(boss.Location, g.Location))
                        .Where(p => !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p)))
                        .OrderBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
                    knockbackUntil = finish.AddSeconds(2);
                    if (LoggingHelpers.MechanicDiagnosticsEnabled)
                        Logger.Information($"[ThokHard] Pillars staging={knockbackPoint} survivingRails={gates.Length} finish={finish:O}.");
                    continue;
                default:
                    continue; // Instant or unverified actions remain capture targets.
            }
            hazards[(actor.ObjectId, action)] = h;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[ThokHard] Hazard action={action} actor={actor.ObjectId:X} origin={h.Origin} heading={h.Heading:F3} expires={h.Expires:O}.");
        }
        circles = hazards.Values.Where(h => h.Radius > 0).ToArray();
        polygons = hazards.Values.Where(h => h.Points != null).ToArray();
        donutOrigins = hazards.Values.Where(h => h.Donut).Select(h => h.Origin).Distinct().ToArray();
    }

    private static Vector3 RailPosition(Vector3 origin, Vector3 gate)
    {
        float dx = gate.X - origin.X, dz = gate.Z - origin.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        return new Vector3(origin.X + dx * 3 / distance, origin.Y, origin.Z + dz * 3 / distance);
    }

    private static bool ApproachClear(Vector3 destination)
    {
        // Rail staging crosses a convex, verified floor, but a concurrent native
        // hazard can still block its approach. Check the whole segment at 0.5y.
        Vector3 start = Core.Me.Location;
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(destination) / 0.5f));
        for (int i = 1; i <= steps; ++i)
        {
            float t = i / (float)steps;
            var p = new Vector3(start.X + (destination.X - start.X) * t, start.Y,
                start.Z + (destination.Z - start.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p)))
                return false;
        }
        return true;
    }

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -0.5f), new(width / 2, -0.5f), new(width / 2, length), new(-width / 2, length)];

    private static Vector2[] Sector(float radius, float halfAngleDegrees)
    {
        var points = new List<Vector2> { Vector2.Zero };
        // Forward is local +Z in RB's polygon plane; -Heading rotates it to the
        // captured actor heading. Two-degree segments keep arc error below 0.03y.
        for (float degree = -halfAngleDegrees; degree <= halfAngleDegrees; degree += 2)
        {
            float radians = degree * MathF.PI / 180;
            points.Add(new Vector2(radius * MathF.Sin(radians), radius * MathF.Cos(radians)));
        }
        points.Add(Vector2.Zero);
        return points.ToArray();
    }

    private void UpdatePositionLease(bool position)
    {
        if (position)
        {
            foreach (var flag in PositionFlags)
                CapabilityManager.Update(CapabilityHandle, flag, TimeSpan.FromMilliseconds(400), "Thok Hard: Pillars rail staging");
            leased = true;
        }
        else if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle);
            leased = false;
        }
    }

    private void Reset()
    {
        active = false;
        targetlessCombat = travelling = false;
        hazards.Clear();
        casting.Clear();
        circles = polygons = [];
        donutOrigins = [];
        knockbackPoint = null;
        previousGateCount = -1;
        navigationRefresh = default;
        UpdatePositionLease(false);
        movement?.Detach();
        movement = null;
        unsupportedMover = false;
        if (sideStepBorrowed && SidestepPlugin != null)
            SidestepPlugin.Enabled = previousSideStep;
        sideStepBorrowed = false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }
}
