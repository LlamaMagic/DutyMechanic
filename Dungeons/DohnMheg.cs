using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 73: Dohn Mheg dungeon logic.
/// </summary>
public partial class DohnMheg : AbstractDungeon
{
    private const int LordOfLingeringGaze = 8141;
    private const int Griaule = 8143;
    private const int PaintedSapling = 8144;
    private const int LordOfLengthsomeGait = 8146;
    private const int LiarsLyre = 8958;
    private const int Hydrofall = 8893;
    private const int LaughingLeapGround = 8852;
    private const int Swinge = 8906;
    private const uint GeyserPairBase = 0x1EAAA1;
    private const uint GeyserTripleBase = 0x1EAAA2;
    private readonly Dictionary<uint, int> geyserStates = new();
    private readonly List<(uint Actor, DateTime Discovered, DateTime Expires, Vector3 Center, AvoidInfo Avoid)> geyserAvoids = new();
    private DateTime nextGeyserRead;
    private bool? savedBossSideStep;
    private bool? boundarySideStep;
    private uint sideStepBossRoom;

    private const int ImpChoir = 13552;
    private const int ToadChoir = 13551;
    private const int Finale = 15723;

    private static readonly Vector3 LordOfLingeringGazeArenaCenter = new(0f, 6.85f, 30.16f);
    private static readonly Vector3 GriauleArenaCenter = new(7f, 23f, -339f);
    private static readonly Vector3 LordOfLengthsomeGaitArenaCenter = new(-128.5f, -144.5f, -244f);
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private readonly List<Vector3> tightRopeWalkPoints =
    [
        new Vector3(-142.8355f, -144.5264f, -232.6624f),
        new Vector3(-140.8284f, -144.5366f, -246.1443f),
        new Vector3(-130.1889f, -144.5366f, -242.3840f),
        new Vector3(-114.4550f, -144.5366f, -244.2632f),
        new Vector3(-125.6857f, -144.5238f, -249.2640f),
        new Vector3(-122.5055f, -144.5192f, -258.3726f),
        new Vector3(-128.1084f, -144.5226f, -258.0896f),
        new Vector3(-128.4761f, -144.5166f, -262.4524f),
    ];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.DohnMheg;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        RestoreBoundarySideStep();
        if (activeDungeon != null && !ReferenceEquals(activeDungeon, this))
            activeDungeon.ResetDungeonState();
        activeDungeon = this;
        // The November 12 capture showed Hydrofall's helper aims at a fixed
        // ground location, not its own center. Without SideStep the player
        // remained inside those six-yalm circles and gained vulnerability.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TeagGye,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == Hydrofall,
            radiusProducer: _ => 6.5f,
            locationProducer: actor => actor.SpellCastInfo.CastLocation);

        // The casted Leap lands at its recorded ground location. The instant
        // stack variant is a separate semantic mechanic and must not be avoided.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TeagGye,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == LaughingLeapGround,
            radiusProducer: _ => 4.5f,
            locationProducer: actor => actor.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TeagGye,
            () => LordOfLingeringGazeArenaCenter,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheAtelier,
            () => GriauleArenaCenter,
            outerRadius: 90.0f,
            innerRadius: 22.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThroneRoom,
            () => LordOfLengthsomeGaitArenaCenter,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        UpdateBossSideStep();
        await FollowDodgeSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;
        bool result = false;

        switch (currentSubZoneId)
        {
            case SubZoneId.TeagGye:
                result = await HandleLordOfLingeringGazeAsync();
                break;
            case SubZoneId.TheAtelier:
                result = await HandleGriauleAsync();
                break;
            case SubZoneId.TheThroneRoom:
                result = await HandleLordOfLengthsomeGaitAsync();
                break;
        }

        return result;
    }

    private async Task<bool> HandleLordOfLingeringGazeAsync()
    {
        UpdateFirstBoss();
        return false;
    }

    // Shared-group states are 16 (warning), 1 (eruption), then 4 (idle).
    // Snapshot the actor-relative pattern at the warning; object presence
    // alone does not identify an incoming wave.
    private void UpdateGeysers()
    {
        var now = DateTime.UtcNow;
        if (!Core.Me.IsAlive || !Core.Me.InCombat)
        {
            ResetGeysers();
            return;
        }
        foreach (var window in geyserAvoids.Where(w => now >= w.Expires).ToArray())
        {
            AvoidanceManager.RemoveAvoid(window.Avoid);
            geyserAvoids.Remove(window);
        }
        if (now < nextGeyserRead)
            return;
        nextGeyserRead = now.AddMilliseconds(100);
        foreach (var actor in GameObjectManager.GameObjects.Where(a => a.IsValid &&
                     (a.BaseId == GeyserPairBase || a.BaseId == GeyserTripleBase)))
        {
            int state = -1;
            try
            {
                state = Lua.GetReturnVal<int>("local o=_G['" + actor.LuaString + "']; if not o or not o.GetSharedGroupTimelineState then return -1 end; return o:GetSharedGroupTimelineState()");
            }
            catch (Exception) { /* An unreadable object must not create a forecast. */ }
            // A transient failed read is not a new warning generation. Preserve
            // the last confirmed edge so recovery cannot extend an old eruption.
            if (state < 0)
                continue;
            bool known = geyserStates.TryGetValue(actor.ObjectId, out int previous);
            bool changed = !known || previous != state;
            geyserStates[actor.ObjectId] = state;
            if (!changed)
                continue;
            // The live reset preceded the attributed hit by about 270 ms.
            // Keep the forecast through its effect margin, even after state 4.
            // On mid-warning attachment the elapsed time is unknown. Admit an
            // immediate conservative escape for either pending animation stage.
            if (state != 16 && !(state == 1 && !known))
                continue;
            uint actorId = actor.ObjectId;
            var origin = actor.Location;
            float heading = actor.Heading;
            var offsets = actor.BaseId == GeyserPairBase
                ? new[] { (X: 0f, Z: -16f), (X: -9f, Z: 15f) }
                : new[] { (X: 0f, Z: 5f), (X: -9f, Z: -15f), (X: 7f, Z: -7f) };
            foreach (var offset in offsets)
            {
                // Rotate the authored local pattern into world XZ once; a later
                // actor update must not move an already announced eruption.
                float c = (float)Math.Cos(heading), s = (float)Math.Sin(heading);
                // Shared-group offsets use this rotation convention. The opposite
                // sign mirrors the quarter-turn patterns while leaving 0/pi unchanged.
                var center = new Vector3(origin.X + offset.X * c + offset.Z * s,
                    origin.Y, origin.Z + offset.Z * c - offset.X * s);
                // Waves are announced about 2.6s apart and resolve after 5.1s.
                // Delayed activation leaves 2.6s to escape without blocking the
                // entire arena with all three future waves at once.
                var activates = known ? now.AddSeconds(2.5) : now;
                var expires = now.AddSeconds(state == 1 ? 3.3 : 5.8);
                // Six-yalm damage circles receive half a yalm clearance. The
                // extra expiry margin covers sampling/effect delay if reset is
                // missed; state 1 alone does not cancel the pending eruption.
                var avoid = AvoidanceManager.AddAvoidLocation(
                    () => Core.Me.IsAlive && Core.Me.InCombat && DateTime.UtcNow < expires &&
                          (DateTime.UtcNow >= activates || geyserStates.TryGetValue(actorId, out int current) && current == 1),
                    6.5f, () => center);
                geyserAvoids.Add((actor.ObjectId, now, expires, center, avoid));
                Logger.Information($"[Dohn Geyser] warning actor=0x{actor.ObjectId:X} state={state} center={center} expires={expires:O}");
            }
        }
    }

    private void ResetGeysers()
    {
        foreach (var window in geyserAvoids)
            AvoidanceManager.RemoveAvoid(window.Avoid);
        geyserAvoids.Clear();
        geyserStates.Clear();
        nextGeyserRead = DateTime.MinValue;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetDungeonState();
        RestoreBoundarySideStep();
        if (ReferenceEquals(activeDungeon, this))
            activeDungeon = null;
        return Task.FromResult(false);
    }

    private Task<bool> HandleGriauleAsync()
    {
        UpdateGriaule();
        return Task.FromResult(false);
    }

    private Task<bool> HandleLordOfLengthsomeGaitAsync()
    {
        UpdateFinalBoss();
        // Facing is held only during the gaze's short critical window. Routine
        // actions resume immediately afterwards; native escape retains priority.
        return Task.FromResult(finalGazeHolding && !AvoidanceManager.IsRunningOutOfAvoid);
    }

    private void ResetDungeonState()
    {
        ReleaseBossSideStep();
        ResetFirstBoss();
        ResetGriaule();
        ResetFinalBoss();
    }

    private void UpdateBossSideStep()
    {
        uint bossBase = (SubZoneId)WorldManager.SubZoneId switch
        {
            SubZoneId.TeagGye => LingeringGazeBossBase,
            SubZoneId.TheAtelier => GriauleBossBase,
            SubZoneId.TheThroneRoom => LengthsomeBossBase,
            _ => 0
        };
        uint room = WorldManager.SubZoneId;
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            WorldManager.ZoneId != (uint)Data.ZoneId.DohnMheg || Core.Me == null ||
            !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat || bossBase == 0 ||
            sideStepBossRoom != 0 && sideStepBossRoom != room)
        {
            ReleaseBossSideStep();
            return;
        }
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.BaseId == bossBase);
        if (boss != null && !boss.IsAlive)
        {
            ReleaseBossSideStep();
            return;
        }
        // Match the existing dungeon ownership pattern: a real boss admits the
        // lease, which then survives temporary disappearance/add phases. Profiles
        // remain responsible for routing, without toggling mechanic ownership.
        if (savedBossSideStep == null && boss?.IsAlive == true && SidestepPlugin != null)
        {
            savedBossSideStep = SidestepPlugin.Enabled;
            sideStepBossRoom = room;
            SidestepPlugin.Enabled = false;
            Logger.Information($"[Dohn SideStep] boss room={room}; suspended.");
        }
    }

    private void ReleaseBossSideStep()
    {
        if (savedBossSideStep == null)
            return;
        // DungeonManager releases this lease before activating another dungeon.
        // Preserve a later explicit enable; the plugin's boolean API cannot
        // distinguish an external owner assigning the same disabled value.
        if (SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = savedBossSideStep.Value;
        savedBossSideStep = null;
        sideStepBossRoom = 0;
        Logger.Information("[Dohn SideStep] boss ownership released; prior state restored.");
    }

    // The shared base enables SideStep before both dungeon callbacks. Match
    // Doma's boundary handoff so that inherited write cannot erase a borrowed
    // false state or a user's change made after the final boss.
    internal void PrepareBoundary(bool leaving)
    {
        if (leaving)
            ReleaseBossSideStep();
        boundarySideStep = SidestepPlugin?.Enabled;
    }

    private void RestoreBoundarySideStep()
    {
        if (boundarySideStep.HasValue && SidestepPlugin != null)
            SidestepPlugin.Enabled = boundarySideStep.Value;
        boundarySideStep = null;
    }

    private static bool BossCombat(int npc, uint bossBase, SubZoneId subZone) =>
        TreeRoot.IsRunning && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene &&
        WorldManager.ZoneId == (uint)Data.ZoneId.DohnMheg && WorldManager.SubZoneId == (uint)subZone &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat &&
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>((uint)npc)
            .Any(a => a.IsValid && a.BaseId == bossBase && a.IsAlive);

    // Effects can resolve after the cast bar. Retain scalar geometry until its
    // fixed deadline; later observations may refine the pose but cannot extend it.
    private sealed class CastHazard
    {
        internal uint Actor, Action;
        internal Vector3 Origin;
        internal float Heading;
        internal Vector2[] Points;
        internal DateTime Expires;
    }

    private static void ObserveCastHazard(Dictionary<(uint, uint), CastHazard> hazards,
        BattleCharacter caster, Vector3 origin, float heading, Vector2[] points, double effectTail)
    {
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z) || origin == Vector3.Zero)
            return;
        var key = (caster.ObjectId, caster.CastingSpellId);
        if (!hazards.TryGetValue(key, out var hazard))
        {
            hazard = new CastHazard
            {
                Actor = key.Item1,
                Action = key.Item2,
                Expires = DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(effectTail)
            };
            hazards[key] = hazard;
            Logger.Information($"[Dohn Hazard] action={hazard.Action} actor=0x{hazard.Actor:X} origin={origin} heading={heading:F3} until={hazard.Expires:O}");
        }
        hazard.Origin = origin;
        hazard.Heading = heading;
        hazard.Points = points;
    }

    private static void ExpireCastHazards(Dictionary<(uint, uint), CastHazard> hazards)
    {
        foreach (var key in hazards.Where(p => DateTime.UtcNow >= p.Value.Expires).Select(p => p.Key).ToArray())
            hazards.Remove(key);
    }

    private static void RegisterCastHazards(ref AvoidInfo registration,
        Dictionary<(uint, uint), CastHazard> hazards, Func<bool> owns, Vector3 center)
    {
        if (registration != null && AvoidanceManager.AvoidInfos.Contains(registration))
            return;
        registration = AvoidanceManager.AddAvoidPolygon<CastHazard>(condition: owns,
            leashPointProducer: () => center, leashRadius: 45f,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1f,
            heightProducer: _ => 12f, pointsProducer: h => h.Points,
            locationProducer: h => h.Origin,
            collectionProducer: () => hazards.Values.Where(h => DateTime.UtcNow < h.Expires).ToArray(),
            objectValidator: _ => true, ignoreIfBlocking: false, priority: AvoidancePriority.High);
    }

    private static Vector2[] ConePoints(float range, float arc)
    {
        var result = new List<Vector2> { new(0, 0) };
        for (int i = 0; i <= 32; ++i)
        {
            double angle = (-arc / 2 + arc * i / 32) * Math.PI / 180;
            result.Add(new Vector2(range * (float)Math.Sin(angle), range * (float)Math.Cos(angle)));
        }
        return result.ToArray();
    }

    private static Vector2[] CirclePoints(float radius) => Enumerable.Range(0, 40)
        .Select(i => new Vector2(radius * (float)Math.Sin(i * Math.PI / 20), radius * (float)Math.Cos(i * Math.PI / 20))).ToArray();

    private static bool OrdinarySegmentSafe(Vector3 destination, Vector3 center, float radius)
    {
        var start = Core.Me.Location;
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(destination) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var point = new Vector3(start.X + (destination.X - start.X) * t, start.Y,
                start.Z + (destination.Z - start.Z) * t);
            if (point.Distance2D(center) > radius || AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)))
                return false;
        }
        return true;
    }
}
