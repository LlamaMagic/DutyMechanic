using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 91: Ihuykatumu dungeon logic.
/// </summary>
public partial class Ihuykatumu : AbstractDungeon
{
    private static Ihuykatumu activeDungeon;
    private bool? savedBossSideStep;
    private bool? boundarySideStep;
    private uint sideStepBossRoom;
    private DateTime nextWhirlwindObservation;
    private DateTime nextPoolObservation;
    private readonly Dictionary<uint, bool> observedPools = [];
    private uint whirlwindId;
    private DateTime whirlwindSeenAt;
    private Vector3[] whirlwindWaveCenters;
    private readonly CapabilityManagerHandle bossGapCloser = CapabilityManager.CreateNewHandle();
    private static readonly double[] WhirlwindWaveSeconds = [8.9, 16.9, 24.9];
    private static readonly Vector3[] NorthWhirlwindWaves =
    [
        new(-111.69f, -118f, 253.94f), new(-102.28f, -118f, 264.31f), new(-108.92f, -118f, 276.53f)
    ];
    private static readonly Vector3[] SouthWhirlwindWaves =
    [
        new(-102.93f, -118f, 274.36f), new(-108.68f, -118f, 262.22f), new(-105.73f, -118f, 252.34f)
    ];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Ihuykatumu;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
     [
        /*
         EnemyAction.Decay,
        EnemyAction.Bury,
        EnemyAction.Bury2,
        EnemyAction.Bury3,
        EnemyAction.Bury4,
        EnemyAction.Bury5,
        */
     ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    // The pulse and stop paths also call these methods because an OrderBot
    // coroutine can retain TreeStart while a boss or lifecycle transition runs.
    internal static void CheckLifecycle()
    {
        activeDungeon?.UpdateBossSideStep();
        activeDungeon?.ObserveWhirlwind();
        activeDungeon?.PulseBury();
    }
    internal static void ResetActive()
    {
        activeDungeon?.ReleaseBossSideStep();
        activeDungeon?.ResetWhirlwind();
        activeDungeon?.ResetPoolObservation();
        activeDungeon?.ResetBury();
    }

    private void ObserveWhirlwind()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)Data.ZoneId.Ihuykatumu ||
            WorldManager.SubZoneId != (uint)SubZoneId.Breathcatch || Core.Me == null ||
            !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
        {
            ResetWhirlwind();
            ResetPoolObservation();
            return;
        }

        ObserveLevinsicklePools();

        var actor = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => bc.IsValid && bc.BaseId == EnemyNpc.WhirlwindBase);
        if (actor == null)
        {
            // A missed actor sample must not restart the same forecast when
            // the wrapper returns. Expire it only after the observed sequence.
            if (whirlwindId != 0 && (DateTime.UtcNow - whirlwindSeenAt).TotalSeconds > 30)
                ResetWhirlwind();
            return;
        }
        if (whirlwindId != actor.ObjectId)
        {
            whirlwindId = actor.ObjectId;
            whirlwindSeenAt = DateTime.UtcNow;
            // Both captured spawns begin near opposite arena corners. An actor
            // first seen mid-route has unknown age and cannot seed a forecast.
            whirlwindWaveCenters = Math.Abs(actor.Location.Z - ArenaCenter.Apollyon.Z) >= 10f &&
                Math.Abs(actor.Location.X - ArenaCenter.Apollyon.X) >= 10f
                ? (actor.Location.Z < ArenaCenter.Apollyon.Z ? NorthWhirlwindWaves : SouthWhirlwindWaves)
                : null;
            Logger.Information($"[Ihuykatumu] Whirlwind 0x{whirlwindId:X} spawn={actor.Location} forecast={(whirlwindWaveCenters != null)}.");
        }
        if (DateTime.UtcNow < nextWhirlwindObservation) return;
        // The moving tornado's uncast lines need actor positions and timing;
        // this observer is scalar-only and does not own gameplay input.
        nextWhirlwindObservation = DateTime.UtcNow.AddMilliseconds(500);
        LoggingHelpers.LogActorSignalChanges("Ihuykatumu.Whirlwind", bc => bc.BaseId == EnemyNpc.WhirlwindBase);
    }

    private void ResetWhirlwind()
    {
        whirlwindId = 0;
        whirlwindSeenAt = DateTime.MinValue;
        whirlwindWaveCenters = null;
        nextWhirlwindObservation = DateTime.MinValue;
        LoggingHelpers.ClearActorSignalWatch("Ihuykatumu.Whirlwind");
    }

    private void ObserveLevinsicklePools()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || DateTime.UtcNow < nextPoolObservation) return;
        nextPoolObservation = DateTime.UtcNow.AddMilliseconds(500);
        var pools = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(e => e.IsValid && e.BaseId == EnemyNpc.LevinsicklePool).ToArray();
        foreach (var pool in pools)
        {
            if (observedPools.TryGetValue(pool.ObjectId, out bool visible) && visible == pool.IsVisible) continue;
            observedPools[pool.ObjectId] = pool.IsVisible;
            Logger.Information("[Ihuykatumu] Levinsickle pool 0x{0:X} visible={1} position={2}.", pool.ObjectId, pool.IsVisible, pool.Location);
        }
        foreach (uint missing in observedPools.Keys.Except(pools.Select(p => p.ObjectId)).ToArray())
        {
            observedPools.Remove(missing);
            Logger.Information("[Ihuykatumu] Levinsickle pool 0x{0:X} removed.", missing);
        }
    }

    private void ResetPoolObservation()
    {
        observedPools.Clear();
        nextPoolObservation = DateTime.MinValue;
    }

    private bool IsWhirlwindWaveActive(int wave)
    {
        if (whirlwindId == 0 || whirlwindWaveCenters == null) return false;
        double elapsed = (DateTime.UtcNow - whirlwindSeenAt).TotalSeconds;
        // Both observed directions hit at about 9, 17 and 25 seconds after
        // discovery. Open each 4 seconds early and retain a 1-second effect tail.
        return elapsed >= WhirlwindWaveSeconds[wave] - 4 && elapsed <= WhirlwindWaveSeconds[wave] + 1;
    }

    private void UpdateBossSideStep()
    {
        uint room = WorldManager.SubZoneId;
        uint bossId = (SubZoneId)room switch
        {
            SubZoneId.PunutiyPool => EnemyNpc.PrimePunutiy,
            SubZoneId.DrowsiesGrotto => EnemyNpc.Drowsie,
            SubZoneId.Breathcatch => EnemyNpc.Apollyon,
            _ => 0
        };
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            WorldManager.ZoneId != (uint)Data.ZoneId.Ihuykatumu || Core.Me == null ||
            !Core.Me.IsValid || !Core.Me.IsAlive || bossId == 0 ||
            sideStepBossRoom != 0 && sideStepBossRoom != room)
        {
            ReleaseBossSideStep();
            return;
        }

        var boss = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(bossId)
            .FirstOrDefault(actor => actor.IsValid);
        if (boss != null && !boss.IsAlive)
        {
            ReleaseBossSideStep();
            return;
        }
        // Keep the lease during an add phase or brief boss disappearance; a
        // sampled absent actor does not establish that the encounter ended.
        if (savedBossSideStep == null && boss?.IsAlive == true && SidestepPlugin != null)
        {
            savedBossSideStep = SidestepPlugin.Enabled;
            sideStepBossRoom = room;
            SidestepPlugin.Enabled = false;
            Logger.Information($"[Ihuykatumu] SideStep suspended for boss room {room}.");
        }
        // Gap closers can bypass normal avoid admission; keep this lease
        // independent of mechanic-specific movement and release it with the boss.
        if (savedBossSideStep.HasValue && Core.Me.InCombat)
            CapabilityManager.Update(bossGapCloser, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Ihuykatumu boss hazards");
        else
            CapabilityManager.Clear(bossGapCloser, CapabilityFlags.GapCloser, "Ihuykatumu boss inactive");
    }

    private void ReleaseBossSideStep()
    {
        CapabilityManager.Clear(bossGapCloser, CapabilityFlags.GapCloser, "Ihuykatumu boss released");
        if (!savedBossSideStep.HasValue) return;
        if (SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = savedBossSideStep.Value;
        savedBossSideStep = null;
        sideStepBossRoom = 0;
        Logger.Information("[Ihuykatumu] Boss SideStep state restored.");
    }

    // The shared base enables SideStep at each boundary. Capture the prior
    // state around those callbacks so this encounter does not overwrite it.
    internal void PrepareBoundary(bool leaving)
    {
        if (leaving) ReleaseBossSideStep();
        boundarySideStep = SidestepPlugin?.Enabled;
    }

    private void RestoreBoundarySideStep()
    {
        if (boundarySideStep.HasValue && SidestepPlugin != null)
            SidestepPlugin.Enabled = boundarySideStep.Value;
        boundarySideStep = null;
    }

    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseBossSideStep();
        ResetWhirlwind();
        ResetPoolObservation();
        ResetBury();
        RestoreBoundarySideStep();
        if (ReferenceEquals(activeDungeon, this)) activeDungeon = null;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        RestoreBoundarySideStep();
        if (activeDungeon != null && !ReferenceEquals(activeDungeon, this))
            activeDungeon.ReleaseBossSideStep();
        activeDungeon = this;

        // Drowsie's four ivy actors cast their emergence circles, then face
        // narrow and wider Wallops in turn. SideStep is suspended in boss rooms,
        // so these actor-relative hazards must remain available to native avoidance.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.DrowsiesGrotto,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.Arise or EnemyAction.FlagrantSpreadGreen,
            radiusProducer: bc => bc.CastingSpellId == EnemyAction.Arise ? 8.5f : 6.5f);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.DrowsiesGrotto,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.WallopNarrow,
            width: 11f, length: 40.5f);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.DrowsiesGrotto,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.WallopWide,
            width: 17f, length: 40.5f);
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.DrowsiesGrotto,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Sneeze,
            leashPointProducer: () => ArenaCenter.Drowsie,
            leashRadius: 40f, rotationDegrees: 0f, radius: 60f, arcDegrees: 150f);

        // Bury uses the mapped, timed movement owner in Ihuykatumu.Bury.cs.

        // Boss 1: Decay
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool,
            objectSelector: c => c.CastingSpellId == EnemyAction.Decay && !EnemyAction.Bury.IsCasting() && !buryActive,
            outerRadius: 40.0f,
            innerRadius: 4.0F,
            priority: AvoidancePriority.Medium);

        // Shore Shaker's helpers start together at arena center and resolve
        // circle, middle ring, then outer ring two seconds apart. Admit each
        // ring only after the earlier cast has finished so an exit remains.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.ShoreShakerMiddle,
            radiusProducer: eo => 10.5f,
            priority: AvoidancePriority.High));

        // Boss 1: Shore Shaker Middle
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool && !EnemyAction.ShoreShakerMiddleHash.IsCasting(),
            objectSelector: c => c.CastingSpellId == EnemyAction.ShoreShakerOuter,
            outerRadius: 20.5f,
            innerRadius: 9.5f,
            priority: AvoidancePriority.Medium);

        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool &&
                !EnemyAction.ShoreShakerMiddleHash.IsCasting() && !EnemyAction.ShoreShakerOuterHash.IsCasting(),
            objectSelector: c => c.CastingSpellId == EnemyAction.ShoreShakerLast,
            outerRadius: 30.5f,
            innerRadius: 19.5f,
            priority: AvoidancePriority.Medium);

        // Boss 1: Hydrowave
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool,
            objectSelector: (bc) => bc.CastingSpellId == EnemyAction.Hydrowave,
            leashPointProducer: () => ArenaCenter.PrimePunutiy,
            leashRadius: 40.0f,
            rotationDegrees: 0f,
            radius: 40.0f,
            arcDegrees: 10f);

        // Boss 1: Hydrowave Small
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool,
            objectSelector: (bc) => bc.CastingSpellId == EnemyAction.HydrowaveSmall,
            leashPointProducer: () => ArenaCenter.PrimePunutiy,
            leashRadius: 40.0f,
            rotationDegrees: 0f,
            radius: 40.0f,
            arcDegrees: 45f);

        // Boss 1: Resurface
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool,
            objectSelector: (bc) => bc.CastingSpellId == EnemyAction.Resurface,
            leashPointProducer: () => ArenaCenter.PrimePunutiy,
            leashRadius: 40.0f,
            rotationDegrees: 0f,
            radius: 40.0f,
            arcDegrees: 70f);

        // Boss 3: Wind Sickle
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: c => c.CastingSpellId == EnemyAction.WindSickle,
            outerRadius: 40.0f,
            innerRadius: 4.0F,
            priority: AvoidancePriority.Medium);

        // Boss 3: Razor Storm
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: (bc) => bc.CastingSpellId == EnemyAction.RazorStorm,
            width: 41f, length: 40.5f);

        // Boss 3: Blades of Famine
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.BladesofFamine,
            width: 12.5f,
            length: 50f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Boss 3: Razor Zephyr
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.RazorZephyr,
            width: 12.5f,
            length: 50.5f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // The two lightning-cone groups begin four seconds apart. Expose only
        // the earliest resolving group so the later wave cannot block its safe
        // region before the first impact has resolved.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.WingofLightningAction && IsEarliestLightning(bc),
            leashPointProducer: () => ArenaCenter.Apollyon,
            leashRadius: 40f, rotationDegrees: 0f, radius: 40f, arcDegrees: 45f);

        // Spark circles leave persistent electrified pools after the cast.
        // The November 13 recording showed Electrocution while crossing the
        // earlier spark location, after its cast avoid had already ended.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: eo => eo.IsValid && eo.IsVisible && eo.BaseId == EnemyNpc.LevinsicklePool,
            radiusProducer: _ => 4.5f,
            priority: AvoidancePriority.High));

        // Helpers share the tornado's NPC ID; its base ID distinguishes the
        // moving contact from the stationary helper actors in the capture.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: bc => bc.BaseId == EnemyNpc.WhirlwindBase && bc.IsVisible,
            radiusProducer: _ => 4.5f);

        // The two captured corner spawns follow mirrored three-point paths.
        // Their uncast star lines resolve about eight seconds apart; use one
        // snapshot center and one wave at a time so later lines do not block
        // the current escape. Width includes half a yalm on each side.
        for (int wave = 0; wave < 3; wave++)
        {
            int waveIndex = wave;
            for (int direction = 0; direction < 4; direction++)
            {
                float rotation = direction * (float)Math.PI / 4f;
                Vector2[] line = [new(4.5f, 36f), new(-4.5f, 36f), new(-4.5f, -36f), new(4.5f, -36f)];
                AvoidanceManager.AddAvoidPolygon<Vector3>(
                    condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch && IsWhirlwindWaveActive(waveIndex),
                    leashPointProducer: null, leashRadius: 50f,
                    rotationProducer: _ => rotation, scaleProducer: _ => 1f, heightProducer: _ => 15f,
                    pointsProducer: _ => line, locationProducer: point => point,
                    collectionProducer: () => whirlwindWaveCenters == null ? [] : [whirlwindWaveCenters[waveIndex]],
                    priority: AvoidancePriority.High);
            }
        }

        // The enhanced Blade is a tank-targeted circle followed by a donut;
        // a targeted player must separate from allies instead of avoiding self.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            objectSelector: bc => bc.CastingSpellId == 36357 && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: _ => 6.5f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch &&
                GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(bc => bc.CastingSpellId == 36357 && bc.SpellCastInfo.TargetId == Core.Player.ObjectId),
            objectSelector: bc => bc.ObjectId != Core.Player.ObjectId && PartyManager.VisibleMembers.Any(member => member.BattleCharacter?.ObjectId == bc.ObjectId),
            radiusProducer: _ => 6.5f);

        // Boss 1: Punutiy Flop
        // Boss 2: FlagrantSpread
        // Boss 3: Levinsickle
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.Breathcatch or (uint)SubZoneId.PunutiyPool or (uint)SubZoneId.DrowsiesGrotto,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.Levinsickle or EnemyAction.Levinsickle2 or EnemyAction.PunutiyFlopBig or EnemyAction.PunutiyFlopSmall or EnemyAction.FlagrantSpread or EnemyAction.ThunderIIIAction && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.PunutiyPool,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.PrimePunutiy],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.DrowsiesGrotto,
            () => ArenaCenter.Drowsie,
            outerRadius: 90.0f,
            innerRadius: 20.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Breathcatch,
            () => ArenaCenter.Apollyon,
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

        bool result = currentSubZoneId switch
        {
            SubZoneId.PunutiyPool => await PrimePunutiy(),
            SubZoneId.DrowsiesGrotto => await Drowsie(),
            SubZoneId.Breathcatch => await Apollyon(),
            _ => false,
        };

        // While a required Bury waypoint begins, briefly prevent a routine
        // from starting another cast before the game reports movement. Holds,
        // established movement, forced input and recovery leave combat free.
        return result || BuryMovementStarting();
    }

    /// <summary>
    /// Boss 1: Prime Punutiy.
    /// </summary>
    private static Task<bool> PrimePunutiy()
    {
        return Task.FromResult(false);
    }

    /// <summary>
    /// Boss 2: Drowsie.
    /// </summary>
    private static async Task<bool> Drowsie()
    {
        return false;
    }

    /// <summary>
    /// Boss 3: Apollyon.
    /// </summary>
    private Task<bool> Apollyon()
    {
        return Task.FromResult(false);
    }

    private static bool IsEarliestLightning(BattleCharacter actor)
    {
        double remaining = actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds;
        double earliest = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(bc => bc.IsValid && bc.CastingSpellId == EnemyAction.WingofLightningAction)
            .Select(bc => bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds)
            .DefaultIfEmpty(remaining).Min();
        return remaining <= earliest + 500;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Prime Punutiy.
        /// </summary>
        public const uint PrimePunutiy = 12723;

        /// <summary>
        /// Second Boss: Drowsie.
        /// </summary>
        public const uint Drowsie = 12716;

        /// <summary>
        /// Final Boss: Apollyon.
        /// </summary>
        public const uint Apollyon = 12711;

        /// <summary>
        /// Final Boss: Apollyon.
        /// Whirlwind ability
        /// </summary>
        public const uint Whirlwind = 12715;
        public const uint WhirlwindBase = 0x416C;
        public const uint LevinsicklePool = 0x1EBA21;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Prime Punutiy.
        /// </summary>
        public static readonly Vector3 PrimePunutiy = new(35f, -203f, -95f);

        /// <summary>
        /// Second Boss: Drowsie.
        /// </summary>
        public static readonly Vector3 Drowsie = new(80f, -134f, 53f);

        /// <summary>
        /// Third Boss: Apollyon.
        /// </summary>
        public static readonly Vector3 Apollyon = new(-107f, -118f, 265f);
    }

    private static class EnemyAction
    {
        // Ivy emergence and the two facing-dependent Wallop stages observed
        // in the November 13 Drowsie capture.
        public const uint Arise = 36478;
        public const uint WallopNarrow = 36479;
        public const uint WallopWide = 36482;
        public const uint FlagrantSpreadGreen = 36485;
        public const uint WingofLightningAction = 36351;
        public const uint ThunderIIIAction = 36353;

        /// <summary>
        /// Prime Punutiy
        /// Punutiy Flop
        /// Run away
        /// </summary>
        public static readonly HashSet<uint> ThermobaricCharge = [8357];

        /// <summary>
        /// Prime Punutiy
        /// Hydrowave
        /// Small cone AoE
        /// </summary>
        public const uint HydrowaveSmall = 36493;

        /// <summary>
        /// Prime Punutiy
        /// Bury
        /// Massive amounts of debris drop from the ceiling.
        /// Might have to follow npc on these as too many are happening at once for sidestep to dodgge affectively
        /// 36497,36498,36499,36500,36503
        /// </summary>
        public static readonly HashSet<uint> Bury =
        [
            36497,
            36498,
            36499,
            36500,
            36501,
            36502,
            36503,
            36504,
        ];

        /// <summary>
        /// Prime Punutiy
        /// Resurface
        /// This ability is cast right before debris starts to fall
        /// </summary>
        public const uint Resurface = 36494;

        /// <summary>
        /// Prime Punutiy
        /// Shore Shaker
        /// Following the NPCs here so we don't fight the other avoids.
        /// </summary>
        public const uint ShoreShaker = 36514;

        /// <summary>
        /// Prime Punutiy
        /// Decay
        /// AoE Donut around mob
        /// </summary>
        public const uint Decay = 36505;

        /// <summary>
        /// Prime Punutiy
        /// Punutiy Flop
        /// Big AoE avoid centered on players
        /// </summary>
        public const uint Hydrowave = 36509;

        /// <summary>
        /// Prime Punutiy
        /// Shore Shaker
        /// Three wave attack
        /// </summary>
        public const uint ShoreShakerInner = 36514;

        public static readonly HashSet<uint> ShoreShakerMiddleHash = [36515];
        public static readonly HashSet<uint> ShoreShakerOuterHash = [36516];

        /// <summary>
        /// Prime Punutiy
        /// Shore Shaker
        /// Three wave attack
        /// </summary>
        public const uint ShoreShakerMiddle = 36515;

        /// <summary>
        /// Prime Punutiy
        /// Shore Shaker
        /// Three wave attack
        /// </summary>
        public const uint ShoreShakerOuter = 36516;
        public const uint ShoreShakerLast = 36517;

        /// <summary>
        /// Prime Punutiy
        /// Punutiy Flop
        /// Big AoE avoid centered on players
        /// </summary>
        public const uint PunutiyFlopBig = 36513;

        /// <summary>
        /// Prime Punutiy
        /// Punutiy Flop
        /// Small AoE avoid centered on players
        /// </summary>
        public const uint PunutiyFlopSmall = 36508;

        /// <summary>
        /// Drowsie
        /// Sneeze
        /// Follow Dodge
        /// </summary>
        public const uint Sneeze = 36475;

        /// <summary>
        /// Drowsie
        /// Flagrant Spread
        /// Dodge
        /// </summary>
        public const uint FlagrantSpread = 36522;

        /// <summary>
        /// Apollyon
        /// Razor Storm
        /// Large cone AoE
        /// </summary>
        public const uint RazorStorm = 36355;

        /// <summary>
        /// Apollyon
        /// Blades of Famine
        /// Large line AoE
        /// </summary>
        public const uint BladesofFamine = 36346;

        /// <summary>
        /// Apollyon
        /// Levinsickle
        /// Small AoE ground targets
        /// </summary>
        public const uint Levinsickle = 36349;

        /// <summary>
        /// Apollyon
        /// Levinsickle
        /// Small AoE ground targets
        /// </summary>
        public const uint Levinsickle2 = 36350;

        /// <summary>
        /// Apollyon
        /// Levinsickle
        /// Small AoE ground targets
        /// </summary>
        public const uint Levinsickle3 = 36348;

        /// <summary>
        /// Apollyon
        /// Razor Zephyr
        /// Straight Line AOE
        /// </summary>
        public const uint RazorZephyr = 36340;

        /// <summary>
        /// Apollyon
        /// Wind Sickle
        /// Donut AoE
        /// </summary>
        public const uint WindSickle = 36358;

        /// <summary>
        /// Apollyon
        /// Blade
        /// AoE tank buster
        /// </summary>
        public static readonly HashSet<uint> Blade = [36356, 36357];

        /// <summary>
        /// Apollyon
        /// Bladedance
        /// Follow NPC
        /// </summary>
        public static readonly HashSet<uint> Bladedance = [17998];

        /// <summary>
        /// Apollyon
        /// Wing of Lightning
        /// Follow NPC
        /// </summary>
        public static readonly HashSet<uint> WingofLightning = [36351];

        /// <summary>
        /// Apollyon
        /// Thunder III
        /// AoE Spread
        /// </summary>
        public static readonly HashSet<uint> ThunderIII = [36353];
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Prey. Thermal Charge bomb
        /// </summary>
        public const uint Prey = 1253;
    }
}
