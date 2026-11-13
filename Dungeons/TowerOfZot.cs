using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

/// <summary>Handles Zot's helper-owned attacks and ordered party positioning.</summary>
public class TowerOfZot : AbstractDungeon
{
    private static TowerOfZot active;
    private ChrysalisMovement movement;
    private bool? savedSideStep;
    private bool? boundarySideStep;
    private uint room;
    private readonly List<Cast> casts = new();
    private readonly Dictionary<uint, Cast> current = new();
    private readonly Dictionary<uint, (Vector3 Center, DateTime Until)> spheres = new();
    private readonly HashSet<uint> seenSpheres = new();
    private readonly CapabilityManagerHandle positionHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle bossGapCloserHandle = CapabilityManager.CreateNewHandle();
    private bool bossGapCloserLeased;
    private bool positionLeased, moving;
    private Vector3? destination;
    private static readonly Vector3 FirstCenter = new(68, -443, -124.5f);
    private static readonly Vector3 SecondCenter = new(-258, -169, -26);
    private static readonly Vector3 FinalCenter = new(-27.5f, 546.104f, -49.5f);

    private sealed class Cast
    {
        internal uint Actor, Action, Target;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Finish, Until;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => ZoneId.TheTowerOfZot;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && DutyManager.InInstance && WorldManager.ZoneId == (uint)ZoneId.TheTowerOfZot &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive;

    /// <summary>Retires the encounter-owned movement and plugin lease on stop or disable.</summary>
    public static void ResetActive()
    {
        active?.Release();
        active = null;
    }

    /// <summary>Observes helper casts even while another profile hook yields.</summary>
    public static void CheckLifecycle()
    {
        if (active == null)
            return;
        if (!Live())
        {
            active.Release();
            return;
        }
        active.Pulse();
    }

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        RestoreBoundarySideStep();
        Logger.Information("[Zot] Encounter mechanics ready");
        // The elemental helpers own their cast origins and timings. Register each
        // one so overlapping spells remain active until their effects resolve.
        AvoidanceManager.AddAvoidPolygon<Cast>(Live, null, 70, c => -c.Heading, _ => 1, _ => 15,
            Polygon, c => c.Origin, () => casts.Where(c => c.Until > DateTime.UtcNow && IsPolygon(c.Action)));
        AvoidanceManager.AddAvoidLocation<Cast>(Live, CircleRadius, c => c.Origin,
            () => casts.Where(c => c.Until > DateTime.UtcNow && CircleRadius(c) > 0));
        // The sphere is announced before its short shatter cast. Register the
        // visible 15y danger early enough to reach the edge gap; the bounded
        // lifetime and object identity prevent a lingering actor from rearming.
        AvoidanceManager.AddAvoidLocation<(uint Actor, Vector3 Center, DateTime Until)>(Live,
            _ => 15.5f, s => s.Center, () => spheres.Where(s => s.Value.Until > DateTime.UtcNow)
                .Select(s => (s.Key, s.Value.Center, s.Value.Until)));
        AvoidanceHelpers.AddAvoidDonut(() => Live() && room == 3733, () => FirstCenter, 90, 19, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(() => Live() && room == 3734, () => SecondCenter, 90, 19, AvoidancePriority.High);
        // The symmetric final-arena center is offset from the old boss hotspot.
        AvoidanceHelpers.AddAvoidDonut(() => Live() && room == 3735, () => FinalCenter, 90, 19, AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        RestoreBoundarySideStep();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        CheckLifecycle();
        return Task.FromResult(moving);
    }

    private void Pulse()
    {
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.Distance2D() < 90).ToArray();
        uint subzone = WorldManager.SubZoneId;
        uint expected = subzone switch
        {
            3733 => 0x33EEu,
            3734 => 0x33EFu,
            3735 => 0x33F1u,
            _ => 0
        };
        bool owns = Core.Me.InCombat && expected != 0 &&
            (room == subzone || actors.Any(a => a.BaseId == expected && a.IsAlive));
        if (!owns)
        {
            Release();
            return;
        }
        room = subzone;
        // Slither bypassed the waypoint guard during Delta Fire and returned
        // Viper to overlapping donut/spread damage. Keep routine gap closers
        // suppressed throughout each boss fight, independently of positioning.
        CapabilityManager.Update(bossGapCloserHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Zot boss fight");
        bossGapCloserLeased = true;
        if (!savedSideStep.HasValue && SidestepPlugin != null)
        {
            savedSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Zot] Boss room {0}: SideStep suspended", room);
        }
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach(true);
            movement = null;
            ReleasePosition();
        }
        if (movement == null && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
            Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: SegmentSafe);
            Navigator.PlayerMover = movement;
        }
        Observe(actors);
        ObserveSpheres(actors);
        UpdatePosition(actors);
        movement?.Update();
    }

    private void ObserveSpheres(BattleCharacter[] actors)
    {
        var now = DateTime.UtcNow;
        var live = actors.Where(a => a.BaseId == 0x33F0 && a.IsVisible).ToArray();
        foreach (var sphere in live)
        {
            if (seenSpheres.Add(sphere.ObjectId))
            {
                // Reference timing spans about 11s for the first wave and 20s
                // for the disguised wave. This cap is a missed-despawn fallback.
                spheres[sphere.ObjectId] = (sphere.Location, now.AddSeconds(22));
                Logger.Information("[Zot] sphere={0:X} center={1}", sphere.ObjectId, sphere.Location);
            }
            if (sphere.IsCasting && sphere.CastingSpellId is 25252 or 25279 &&
                spheres.TryGetValue(sphere.ObjectId, out var pending))
                spheres[sphere.ObjectId] = (pending.Center, now + sphere.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.2));
        }
        var liveIds = live.Select(a => a.ObjectId).ToHashSet();
        foreach (uint id in seenSpheres.Where(id => !liveIds.Contains(id)).ToArray())
        {
            seenSpheres.Remove(id);
            // A despawn after the cast can precede its effect; preserve its tail.
            if (spheres.TryGetValue(id, out var pending) && pending.Until > now.AddSeconds(1.2))
                spheres[id] = (pending.Center, now.AddSeconds(1.2));
        }
        foreach (uint id in spheres.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
            spheres.Remove(id);
    }

    private void Observe(BattleCharacter[] actors)
    {
        var now = DateTime.UtcNow;
        var seen = new HashSet<uint>();
        foreach (var actor in actors.Where(a => a.IsNpc && a.IsCasting && a.SpellCastInfo.IsValid && Relevant(a.CastingSpellId)))
        {
            var spell = actor.SpellCastInfo;
            seen.Add(actor.ObjectId);
            var finish = now + spell.RemainingCastTime;
            if (current.TryGetValue(actor.ObjectId, out var old) && old.Action == spell.ActionId && finish < old.Finish.AddSeconds(1))
            {
                // Prapti turns toward its target during the cast. Follow the live
                // pose, but keep the original deadline so it cannot linger.
                if (old.Action is 25256 or 25275)
                {
                    old.Heading = actor.Heading;
                    old.Origin = actor.Location;
                }
                continue;
            }
            if (old != null && now < old.Finish.AddMilliseconds(-300))
                old.Until = now;
            var origin = spell.ActionId is 25268 or 25269 or 25270 ? spell.CastLocation : actor.Location;
            if (origin == Vector3.Zero)
                continue;
            // Most effects resolve after the bar. The baseline's Blizzard hit
            // about 0.8s after its nominal finish; retain the captured tail.
            var cast = new Cast
            {
                Actor = actor.ObjectId,
                Action = spell.ActionId,
                Target = spell.TargetId,
                Origin = origin,
                Heading = actor.Heading,
                Finish = finish,
                Until = finish.AddSeconds(1.2)
            };
            casts.Add(cast);
            current[actor.ObjectId] = cast;
            Logger.Information("[Zot] cast={0} actor={1:X} target={2:X} origin={3} heading={4:F3} finish={5:O}",
                cast.Action, cast.Actor, cast.Target, cast.Origin, cast.Heading, cast.Finish);
        }
        foreach (var pair in current.ToArray())
        {
            if (seen.Contains(pair.Key))
                continue;
            if (now < pair.Value.Finish.AddMilliseconds(-300))
                pair.Value.Until = now;
            current.Remove(pair.Key);
        }
        casts.RemoveAll(c => c.Until <= now);
    }

    private static bool Relevant(uint a) => a is 25237 or 25238 or 25239 or 25240 or 25251 or 25256 or
        25263 or 25264 or 25265 or 25266 or 25267 or 25268 or 25269 or 25270 or 25271 or 25272 or
        25275 or 25278 or 25286;
    private static bool IsPolygon(uint a) => a is 25237 or 25238 or 25240 or 25256 or 25263 or 25264 or
        25266 or 25267 or 25271 or 25275 or 25286;

    private static float CircleRadius(Cast c) => c.Action switch
    {
        25239 or 25269 => 3.5f,
        25270 or 25251 or 25278 => 5.5f,
        25268 => 15.5f,
        _ => 0
    };

    private static Vector2[] Polygon(Cast c) => c.Action switch
    {
        25237 or 25263 => Donut(45, 4.5f),
        25238 or 25266 or 25286 => Cone(42, 22),
        25240 => Cone(42, 181),
        25256 or 25267 or 25275 => Rectangle(4.5f, 45),
        25264 or 25271 => Rectangle(10.5f, 45),
        _ => []
    };

    // Shapes use the same +Z / negative-heading convention as shared avoids.
    // The extra half-yalm (and one degree for fans) is the initial body margin.
    private static Vector2[] Circle(float r) => Enumerable.Range(0, 64).Select(i =>
        new Vector2(r * (float)Math.Sin(i * Math.Tau / 64), r * (float)Math.Cos(i * Math.Tau / 64))).ToArray();

    private static Vector2[] Donut(float outer, float inner)
    {
        var a = Circle(outer);
        var b = Circle(inner).Reverse().ToArray();
        return [.. a, a[0], .. b, b[0]];
    }

    private static Vector2[] Cone(float radius, float degrees) => [new(0, 0), .. Enumerable.Range(0, 33).Select(i =>
        new Vector2(radius * (float)Math.Sin((-degrees / 2 + degrees * i / 32) * Math.PI / 180),
            radius * (float)Math.Cos((-degrees / 2 + degrees * i / 32) * Math.PI / 180)))];

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -.5f), new(width / 2, -.5f), new(width / 2, length), new(-width / 2, length)];

    private bool SegmentSafe(Vector3 end)
    {
        var start = Core.Me.Location;
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(end) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var point = new Vector3(start.X + (end.X - start.X) * t, start.Y, start.Z + (end.Z - start.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)))
                return false;
        }
        return true;
    }

    private void UpdatePosition(BattleCharacter[] actors)
    {
        moving = false;
        // Cast targets express the stack/spread semantics; other players are
        // participants, never an unrelated proxy for a safe location.
        var stack = casts.FirstOrDefault(c => c.Action == 25272 && c.Until > DateTime.UtcNow);
        var spreads = casts.Where(c => c.Action == 25265 && c.Until > DateTime.UtcNow).ToArray();
        if (movement?.IsCurrent != true || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl ||
            stack == null && spreads.Length == 0)
        {
            ReleasePosition();
            return;
        }
        var party = actors.Append(Core.Me).Where(a => a.IsValid && a.IsAlive && a.IsVisible &&
            (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (var p in party)
            for (int i = 0; i < 12; i++)
                candidates.Add(new Vector3(p.X + 3 * (float)Math.Sin(i * Math.Tau / 12), Core.Me.Y, p.Z + 3 * (float)Math.Cos(i * Math.Tau / 12)));
        for (int i = 0; i < 24; i++)
            candidates.Add(new Vector3(FinalCenter.X + 17 * (float)Math.Sin(i * Math.Tau / 24), Core.Me.Y,
                FinalCenter.Z + 17 * (float)Math.Cos(i * Math.Tau / 24)));
        bool selfSpread = spreads.Any(c => c.Target == Core.Me.ObjectId);
        var carrier = stack == null ? null : party.FirstOrDefault(p => p.ObjectId == stack.Target);
        bool Safe(Vector3 p) => p.Distance2D(FinalCenter) < 19 && SegmentSafe(p) &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p)) &&
            (!selfSpread || party.Where(a => a.ObjectId != Core.Me.ObjectId).All(a => a.Location.Distance2D(p) > 6.5f)) &&
            spreads.Where(c => c.Target != Core.Me.ObjectId).All(c => party.FirstOrDefault(a => a.ObjectId == c.Target)?.Location.Distance2D(p) > 6.5f);
        int Coverage(Vector3 p) => carrier == null ? 0 : carrier.ObjectId == Core.Me.ObjectId
            ? party.Count(a => a.ObjectId != Core.Me.ObjectId && a.Location.Distance2D(p) < 4f)
            : carrier.Location.Distance2D(p) < 4f ? 1 : 0;
        var best = candidates.Where(p => Safe(p) && (stack == null || Coverage(p) > 0))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        if (!best.HasValue)
        {
            ReleasePosition();
            return;
        }
        if (!destination.HasValue || !Safe(destination.Value) || stack != null && Coverage(destination.Value) < Coverage(best.Value))
        {
            destination = best;
            Logger.Information("[Zot] position={0} stack={1} selfSpread={2}", destination, stack?.Target, selfSpread);
        }
        CapabilityManager.Update(positionHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Zot party mechanic");
        positionLeased = true;
        movement.Request(destination, .5f);
        moving = Core.Me.Location.Distance2D(destination.Value) > .5f;
    }

    private void ReleasePosition()
    {
        if (positionLeased && movement?.IsCurrent == true)
            movement.Request(null, 0);
        if (positionLeased)
            CapabilityManager.Clear(positionHandle, CapabilityFlags.Movement, "Zot positioning ended");
        positionLeased = moving = false;
        destination = null;
    }

    private void Release()
    {
        ReleasePosition();
        movement?.Detach();
        movement = null;
        if (bossGapCloserLeased)
            CapabilityManager.Clear(bossGapCloserHandle, CapabilityFlags.GapCloser, "Zot boss fight ended");
        bossGapCloserLeased = false;
        casts.Clear();
        current.Clear();
        spheres.Clear();
        seenSpheres.Clear();
        room = 0;
        if (savedSideStep.HasValue && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = savedSideStep.Value;
        savedSideStep = null;
    }

    // AbstractDungeon enables SideStep at both boundaries. Preserve the
    // borrowed state across those inherited writes without affecting other duties.
    internal void PrepareBoundary(bool leaving)
    {
        if (leaving)
            Release();
        boundarySideStep = SidestepPlugin?.Enabled;
    }

    private void RestoreBoundarySideStep()
    {
        if (boundarySideStep.HasValue && SidestepPlugin != null)
            SidestepPlugin.Enabled = boundarySideStep.Value;
        boundarySideStep = null;
    }
}
