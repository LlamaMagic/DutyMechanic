using Buddy.Coroutines;
using Clio.Common;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: The Meso Terminal dungeon logic.
/// </summary>
public class MesoTerminal : AbstractDungeon
{
    private readonly ChirurgeonGeneralState chirurgeonGeneralState = new();
    private readonly HoodedHeadsmenState hoodedHeadsmenState = new();
    private readonly ImmortalRemainsState immortalRemainsState = new();

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.MesoTerminal;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    // Boss handlers own follow movement in this duty.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } =
    [
        EnemyAction.ConcentratedDose,
        EnemyAction.RelentlessTorment,
        EnemyAction.MemoryOfThePyreCast,
    ];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        RegisterArenaBoundaries();
        RegisterChirurgeonGeneralAvoids();
        RegisterHoodedHeadsmenAvoids();
        RegisterImmortalRemainsAvoids();

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseDirectedMovement(chirurgeonGeneralState.PungentAerosol, "leaving The Meso Terminal");
        ReleaseHeadsmenMovement("leaving The Meso Terminal");
        ReleaseMemoryOfTheStormMovement("leaving The Meso Terminal");
        ReleaseDirectedMovement(immortalRemainsState.Impression, "leaving The Meso Terminal");
        ReleaseBombardmentTrustFallback("leaving The Meso Terminal");
        hoodedHeadsmenState.Clear();
        immortalRemainsState.Clear();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;
        UpdateBossOwnership(currentSubZoneId);
        await TankBusterSpells();

        return currentSubZoneId switch
        {
            SubZoneId.TriageModule => await HandleChirurgeonGeneral(),
            SubZoneId.PublicForum => await HandleHoodedHeadsmen(),
            SubZoneId.NonvolatileMemory => await HandleImmortalRemains(),
            _ => false,
        };
    }

    private void UpdateBossOwnership(SubZoneId currentSubZoneId)
    {
        bool chirurgeonActive = IsBossCombatActive(
            currentSubZoneId,
            SubZoneId.TriageModule,
            EnemyObjectId.ChirurgeonGeneral);
        bool headsmenActive = UpdateHeadsmenEncounterOwnership(currentSubZoneId);
        bool immortalActive = IsBossCombatActive(
            currentSubZoneId,
            SubZoneId.NonvolatileMemory,
            EnemyObjectId.ImmortalRemains);

        // SideStep remains useful for trash but conflicts with encounter-specific boss movement.
        SidestepPlugin.Enabled = !(chirurgeonActive || headsmenActive || immortalActive);

        if (!chirurgeonActive)
        {
            ReleaseDirectedMovement(chirurgeonGeneralState.PungentAerosol, "Chirurgeon General combat ended");
        }

        if (!headsmenActive)
        {
            ReleaseHeadsmenMovement("Hooded Headsmen combat ended");
            hoodedHeadsmenState.Clear();
        }

        if (!immortalActive)
        {
            ReleaseMemoryOfTheStormMovement("Immortal Remains combat ended");
            ReleaseDirectedMovement(immortalRemainsState.Impression, "Immortal Remains combat ended");
            ReleaseBombardmentTrustFallback("Immortal Remains combat ended");
            immortalRemainsState.ClearForecasts();
        }
    }

    private static void RegisterArenaBoundaries()
    {
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => IsInCombat(SubZoneId.TriageModule),
            innerWidth: ChirurgeonGeometry.ArenaSafeSize,
            innerHeight: ChirurgeonGeometry.ArenaSafeSize,
            outerWidth: ArenaGeometry.OuterBoundarySize,
            outerHeight: ArenaGeometry.OuterBoundarySize,
            collectionProducer: () => [ArenaCenter.ChirurgeonGeneral],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => IsInCombat(SubZoneId.PublicForum) && !HasAnyCellBlock(),
            innerWidth: HeadsmenGeometry.ArenaSafeWidth,
            innerHeight: HeadsmenGeometry.ArenaSafeHeight,
            outerWidth: ArenaGeometry.OuterBoundarySize,
            outerHeight: ArenaGeometry.OuterBoundarySize,
            collectionProducer: () => [ArenaCenter.HoodedHeadsmen],
            priority: AvoidancePriority.High);

        RegisterHeadsmanCellBoundary(PlayerAura.CellBlockA, ArenaCenter.CellBlockA);
        RegisterHeadsmanCellBoundary(PlayerAura.CellBlockB, ArenaCenter.CellBlockB);
        RegisterHeadsmanCellBoundary(PlayerAura.CellBlockC, ArenaCenter.CellBlockC);
        RegisterHeadsmanCellBoundary(PlayerAura.CellBlockD, ArenaCenter.CellBlockD);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => IsInCombat(SubZoneId.NonvolatileMemory),
            innerWidth: ImmortalGeometry.ArenaSafeSize,
            innerHeight: ImmortalGeometry.ArenaSafeSize,
            outerWidth: ArenaGeometry.OuterBoundarySize,
            outerHeight: ArenaGeometry.OuterBoundarySize,
            collectionProducer: () => [ArenaCenter.ImmortalRemains],
            priority: AvoidancePriority.High);
    }

    private static void RegisterHeadsmanCellBoundary(uint cellAuraId, Vector3 center)
    {
        AvoidanceHelpers.AddAvoidDonut(
            () => IsInCombat(SubZoneId.PublicForum) &&
                  Core.Player.HasAura(cellAuraId),
            () => center,
            outerRadius: ArenaGeometry.OuterBoundarySize,
            innerRadius: HeadsmenGeometry.CellSafeRadius,
            priority: AvoidancePriority.High);
    }

    private static void RegisterChirurgeonGeneralAvoids()
    {
        RegisterSelfCircleAvoid(
            SubZoneId.TriageModule,
            EnemyAction.SterileSphereSmall,
            ChirurgeonGeometry.SterileSphereSmallRadius);

        RegisterSelfCircleAvoid(
            SubZoneId.TriageModule,
            EnemyAction.SterileSphereLarge,
            ChirurgeonGeometry.SterileSphereLargeRadius);

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => IsInCombat(SubZoneId.TriageModule),
            objectSelector: actor => actor.CastingSpellId == EnemyAction.BiochemicalFront,
            width: ChirurgeonGeometry.BiochemicalFrontWidth,
            length: ChirurgeonGeometry.BiochemicalFrontLength,
            priority: AvoidancePriority.High);
    }

    private void RegisterHoodedHeadsmenAvoids()
    {
        AvoidanceHelpers.AddAvoidDonut(
            canRun: CanRunStandaloneHeadsmenAvoidance,
            collectionProducer: GetActiveFlayingFlailLocations,
            outerRadius: HeadsmenGeometry.FlayingFlailAvoidRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            canRun: CanRunStandaloneHeadsmenAvoidance,
            collectionProducer: GetActiveChoppingBlockLocations,
            outerRadius: HeadsmenGeometry.ChoppingBlockAvoidRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            canRun: CanRunStandaloneHeadsmenAvoidance,
            collectionProducer: GetActiveExecutionWheelLocations,
            outerRadius: HeadsmenGeometry.ExecutionWheelOuterRadius,
            innerRadius: HeadsmenGeometry.ExecutionWheelInnerRadius,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            condition: CanRunStandaloneHeadsmenAvoidance,
            leashPointProducer: () => GetCurrentHeadsmanCellCenter() ?? ArenaCenter.HoodedHeadsmen,
            leashRadius: HeadsmenGeometry.CellAvoidanceLeashRadius,
            rotationProducer: sword => -sword.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => ArenaGeometry.AvoidHeight,
            pointsProducer: _ => HeadsmenGeometry.PealOfJudgmentTravelRectangle,
            locationProducer: sword => sword.Location,
            collectionProducer: GetActivePealOfJudgmentSwords,
            objectValidator: sword => sword.IsValid,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);
    }

    private void RegisterImmortalRemainsAvoids()
    {
        AvoidanceManager.AddAvoidPolygon<TurmoilForecast>(
            condition: IsImmortalRemainsCombatActive,
            leashPointProducer: () => ArenaCenter.ImmortalRemains,
            leashRadius: ImmortalGeometry.ArenaLeashRadius,
            rotationProducer: _ => 0f,
            scaleProducer: _ => 1f,
            heightProducer: _ => ArenaGeometry.AvoidHeight,
            pointsProducer: _ => ImmortalGeometry.TurmoilHalfRectangle,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: GetActiveTurmoilForecasts,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoidPolygon<ElectrayLane>(
            condition: IsImmortalRemainsCombatActive,
            leashPointProducer: () => ArenaCenter.ImmortalRemains,
            leashRadius: ImmortalGeometry.ArenaLeashRadius,
            rotationProducer: lane => -lane.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => ArenaGeometry.AvoidHeight,
            pointsProducer: _ => ImmortalGeometry.ElectrayRectangle,
            locationProducer: lane => lane.Location,
            collectionProducer: GetActiveElectrayLanes,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoidPolygon<KeraunographyLane>(
            condition: IsImmortalRemainsCombatActive,
            leashPointProducer: () => ArenaCenter.ImmortalRemains,
            leashRadius: ImmortalGeometry.ArenaLeashRadius,
            rotationProducer: lane => -lane.Heading,
            scaleProducer: _ => 1f,
            heightProducer: _ => ArenaGeometry.AvoidHeight,
            pointsProducer: _ => ImmortalGeometry.KeraunographyRectangle,
            locationProducer: lane => lane.Location,
            collectionProducer: () => immortalRemainsState.Keraunography.Lanes
                .Where(lane => lane.ExpiresAtUtc > DateTime.UtcNow)
                .ToArray(),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            canRun: CanRunBombardmentAvoidance,
            collectionProducer: () => GetActiveBombardmentLocations(BombardmentShape.Small),
            outerRadius: ImmortalGeometry.BombardmentSmallRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(
            canRun: CanRunBombardmentAvoidance,
            collectionProducer: () => GetActiveBombardmentLocations(BombardmentShape.Large),
            outerRadius: ImmortalGeometry.BombardmentLargeRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            canRun: IsImmortalRemainsCombatActive,
            collectionProducer: GetActiveImpressionLocations,
            outerRadius: ImmortalGeometry.ImpressionVoidzoneRadius,
            innerRadius: 0f,
            priority: AvoidancePriority.High);
    }

    private static void RegisterSelfCircleAvoid(SubZoneId subZoneId, uint actionId, float radius)
    {
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => IsInCombat(subZoneId),
            objectSelector: actor => actor.CastingSpellId == actionId,
            radiusProducer: _ => radius,
            priority: AvoidancePriority.High));
    }

    private static bool IsInCombat(SubZoneId subZoneId) =>
        Core.Player != null &&
        Core.Player.IsAlive &&
        Core.Player.InCombat &&
        WorldManager.SubZoneId == (uint)subZoneId;

    private static bool IsBossCombatActive(
        SubZoneId currentSubZoneId,
        SubZoneId bossSubZoneId,
        uint bossBaseId) =>
        currentSubZoneId == bossSubZoneId &&
        IsInCombat(bossSubZoneId) &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.BaseId == bossBaseId);

    private static bool IsHeadsmenCombatActive(SubZoneId currentSubZoneId) =>
        currentSubZoneId == SubZoneId.PublicForum &&
        IsInCombat(SubZoneId.PublicForum) &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && EnemyObjectId.Headsmen.Contains(actor.BaseId));

    // Cell Block helpers continue resolving after an assigned Headsman dies, so ownership remains
    // latched until the party leaves the Public Forum.
    private bool UpdateHeadsmenEncounterOwnership(SubZoneId currentSubZoneId)
    {
        if (currentSubZoneId != SubZoneId.PublicForum)
        {
            return false;
        }

        if (!hoodedHeadsmenState.OwnsEncounter &&
            (IsHeadsmenCombatActive(currentSubZoneId) || HasAnyCellBlock()))
        {
            hoodedHeadsmenState.OwnsEncounter = true;
        }

        return hoodedHeadsmenState.OwnsEncounter;
    }

    private static bool IsImmortalRemainsCombatActive() =>
        IsBossCombatActive(
            (SubZoneId)WorldManager.SubZoneId,
            SubZoneId.NonvolatileMemory,
            EnemyObjectId.ImmortalRemains);

    // Impression staging has exclusive movement ownership.
    private bool CanRunBombardmentAvoidance() =>
        IsImmortalRemainsCombatActive() && !IsImpressionStagingActive();

    // RB avoidance handles the open arena; the cell planner owns isolated enclosures.
    private bool CanRunStandaloneHeadsmenAvoidance() =>
        hoodedHeadsmenState.OwnsEncounter &&
        WorldManager.SubZoneId == (uint)SubZoneId.PublicForum &&
        !IsHeadsmenCellPlannerActive();

    private bool IsHeadsmenCellPlannerActive()
    {
        if (GetCurrentHeadsmanCellCenter() == null)
        {
            return false;
        }

        DateTime now = DateTime.UtcNow;
        return GetActiveDismembermentLanes().Length != 0 ||
            GetActionableHeadsmenCircleHazards(now).Length != 0 ||
            GetActiveExecutionWheelHazards(now).Length != 0 ||
            GetActivePealOfJudgmentSwords().Length != 0;
    }

    private bool IsImpressionStagingActive()
    {
        DateTime now = DateTime.UtcNow;
        DirectedMovementState movement = immortalRemainsState.Impression;
        return (movement.HasDestination && now < movement.ExpiresAtUtc) ||
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Any(actor =>
                    actor.IsValid &&
                    actor.IsCasting &&
                    actor.CastingSpellId == EnemyAction.ImpressionAoe);
    }

    private DismembermentLane[] GetActiveDismembermentLanes() =>
        GetActiveDismembermentLanes(DateTime.UtcNow);

    private DismembermentLane[] GetActiveDismembermentLanes(DateTime now)
    {
        Vector3? cellCenter = GetCurrentHeadsmanCellCenter();
        if (cellCenter == null)
        {
            return [];
        }

        DismembermentLane[] relevantLanes = hoodedHeadsmenState.Dismemberment.LanesByCaster.Values
            .Where(lane =>
                lane.ExpiresAtUtc > now &&
                DoesDismembermentLaneIntersectCell(lane, cellCenter.Value))
            .ToArray();
        if (relevantLanes.Length == 0)
        {
            return [];
        }

        DateTime firstResolution = relevantLanes.Min(lane => lane.ResolvesAtUtc);
        return relevantLanes
            .Where(lane => lane.ResolvesAtUtc <=
                firstResolution + HeadsmenTiming.DismembermentWaveGroupingTolerance)
            .ToArray();
    }

    private BattleCharacter[] GetActivePealOfJudgmentSwords()
    {
        HashSet<uint> activeSwordIds = hoodedHeadsmenState.PealOfJudgment.ActiveSwordIds;
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor =>
                actor.IsValid &&
                actor.BaseId == EnemyObjectId.SwordOfJustice &&
                activeSwordIds.Contains(actor.ObjectId))
            .ToArray();
    }

    private PealOfJudgmentLane[] GetActivePealOfJudgmentLanes(DateTime now)
    {
        DateTime resolvesAtUtc = now + HeadsmenTiming.PealTravelForecastLead;
        DateTime expiresAtUtc = resolvesAtUtc + HeadsmenTiming.PealForecastLease;
        return GetActivePealOfJudgmentSwords()
            .Select(actor => new PealOfJudgmentLane(
                actor.Location,
                actor.Heading,
                resolvesAtUtc,
                expiresAtUtc))
            .ToArray();
    }

    private Vector3[] GetActiveFlayingFlailLocations() =>
        hoodedHeadsmenState.FlayingFlail.ForecastsByCaster.Values
            .Where(ShouldPublishHeadsmenCircleForecast)
            .Select(forecast => forecast.Location)
            .ToArray();

    private Vector3[] GetActiveChoppingBlockLocations() =>
        hoodedHeadsmenState.ChoppingBlock.ForecastsByCaster.Values
            .Where(ShouldPublishHeadsmenCircleForecast)
            .Select(forecast => forecast.Location)
            .ToArray();

    private Vector3[] GetActiveExecutionWheelLocations() =>
        GetActiveExecutionWheelHazards(DateTime.UtcNow)
            .Select(hazard => hazard.Location)
            .ToArray();

    // Do not publish a later circle ahead of the current Dismemberment wave.
    private bool ShouldPublishHeadsmenCircleForecast(HeadsmenCircleForecast forecast)
    {
        DateTime now = DateTime.UtcNow;
        if (forecast.ExpiresAtUtc <= now ||
            forecast.ResolvesAtUtc > now + HeadsmenTiming.CircleAvoidanceLead)
        {
            return false;
        }

        DismembermentLane[] dismembermentLanes = GetActiveDismembermentLanes(now);
        return dismembermentLanes.Length == 0 ||
            forecast.ResolvesAtUtc <=
            dismembermentLanes.Min(lane => lane.ResolvesAtUtc) +
            HeadsmenTiming.SequentialResolutionTolerance;
    }

    private HeadsmenCircleHazard[] GetActionableHeadsmenCircleHazards(DateTime now) =>
        GetRetainedHeadsmenCircleHazards(now)
            .Where(hazard => hazard.ResolvesAtUtc <= now + HeadsmenTiming.CircleAvoidanceLead)
            .ToArray();

    // Keep later circles in the context so the current destination can be reused when possible.
    private HeadsmenCircleHazard[] GetRetainedHeadsmenCircleHazards(DateTime now) =>
        hoodedHeadsmenState.FlayingFlail.ForecastsByCaster.Values
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .Select(forecast => new HeadsmenCircleHazard(
                forecast.Location,
                HeadsmenGeometry.FlayingFlailAvoidRadius,
                forecast.ResolvesAtUtc,
                forecast.ExpiresAtUtc))
            .Concat(hoodedHeadsmenState.ChoppingBlock.ForecastsByCaster.Values
                .Where(forecast => forecast.ExpiresAtUtc > now)
                .Select(forecast => new HeadsmenCircleHazard(
                    forecast.Location,
                    HeadsmenGeometry.ChoppingBlockAvoidRadius,
                    forecast.ResolvesAtUtc,
                    forecast.ExpiresAtUtc)))
            .ToArray();

    private HeadsmenDonutHazard[] GetActiveExecutionWheelHazards(DateTime now) =>
        hoodedHeadsmenState.ExecutionWheel.ForecastsByCaster.Values
            .Where(forecast =>
                forecast.ExpiresAtUtc > now &&
                forecast.ResolvesAtUtc <= now + HeadsmenTiming.ExecutionWheelAvoidanceLead)
            .Select(forecast => new HeadsmenDonutHazard(
                forecast.Location,
                HeadsmenGeometry.ExecutionWheelInnerRadius,
                forecast.ResolvesAtUtc,
                forecast.ExpiresAtUtc))
            .ToArray();

    private ElectrayLane[] GetActiveElectrayLanes()
    {
        DateTime now = DateTime.UtcNow;
        ElectrayLane[] activeLanes = immortalRemainsState.Electray.LanesByCaster.Values
            .Where(lane => lane.ExpiresAtUtc > now)
            .ToArray();
        if (activeLanes.Length == 0)
        {
            return [];
        }

        DateTime earliestResolution = activeLanes.Min(lane => lane.ResolvesAtUtc);
        return activeLanes
            .Where(lane => lane.ResolvesAtUtc <=
                earliestResolution + ImmortalTiming.ElectrayWaveGroupingTolerance)
            .ToArray();
    }

    private static Vector3[] GetActiveImpressionLocations() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.ImpressionAoe)
            .Select(actor => actor.SpellCastInfo.CastLocation)
            .Distinct()
            .ToArray();

    private static bool IsHazardNearCurrentCell(Vector3 location, float maximumDistance)
    {
        Vector3? center = GetCurrentHeadsmanCellCenter();
        return center == null || Distance2DSquared(location, center.Value) <= maximumDistance * maximumDistance;
    }

    private static bool HasAnyCellBlock() => GetCellBlockAuraId() != 0;

    private async Task<bool> HandleChirurgeonGeneral()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor =>
                actor.IsValid &&
                actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.PungentAerosol);
        DateTime now = DateTime.UtcNow;

        if (caster != null && !chirurgeonGeneralState.PungentAerosol.HasDestination)
        {
            Vector3? destination = FindPungentAerosolDestination(caster.SpellCastInfo.CastLocation);
            if (destination != null)
            {
                DirectedMovementState movement = chirurgeonGeneralState.PungentAerosol;
                movement.Destination = destination.Value;
                movement.HasDestination = true;
                movement.ExpiresAtUtc = now + caster.SpellCastInfo.RemainingCastTime +
                    ChirurgeonTiming.PungentAerosolResolutionGrace;
            }
        }

        if (!chirurgeonGeneralState.PungentAerosol.HasDestination)
        {
            return false;
        }

        if (now >= chirurgeonGeneralState.PungentAerosol.ExpiresAtUtc)
        {
            ReleaseDirectedMovement(chirurgeonGeneralState.PungentAerosol, "Pungent Aerosol resolved");
            return false;
        }

        return await MoveToDirectedDestination(
            chirurgeonGeneralState.PungentAerosol,
            ChirurgeonGeometry.PungentAerosolArrivalRadius,
            "Holding a wall-safe Pungent Aerosol landing");
    }

    private static Vector3? FindPungentAerosolDestination(Vector3 source)
    {
        Vector3? best = null;
        float bestScore = float.MinValue;

        for (float x = -ChirurgeonGeometry.MovementHalfWidth;
             x <= ChirurgeonGeometry.MovementHalfWidth;
             x += ChirurgeonGeometry.PungentAerosolCandidateStep)
        {
            for (float z = -ChirurgeonGeometry.MovementHalfWidth;
                 z <= ChirurgeonGeometry.MovementHalfWidth;
                 z += ChirurgeonGeometry.PungentAerosolCandidateStep)
            {
                Vector3 candidate = new(
                    ArenaCenter.ChirurgeonGeneral.X + x,
                    ArenaCenter.ChirurgeonGeneral.Y,
                    ArenaCenter.ChirurgeonGeneral.Z + z);
                Vector3? landing = ProjectKnockbackLanding(
                    candidate,
                    source,
                    ChirurgeonGeometry.PungentAerosolKnockbackDistance);
                if (landing == null ||
                    !IsInsideSquareArena(
                        landing.Value,
                        ArenaCenter.ChirurgeonGeneral,
                        ChirurgeonGeometry.MovementHalfWidth) ||
                    AvoidanceManager.Avoids.Any(avoid =>
                        avoid.IsPointInAvoid(candidate) || avoid.IsPointInAvoid(landing.Value)))
                {
                    continue;
                }

                float score = (GetSquareArenaMargin(
                                   landing.Value,
                                   ArenaCenter.ChirurgeonGeneral,
                                   ChirurgeonGeometry.MovementHalfWidth) * 10f) +
                    (GetSquareArenaMargin(
                         candidate,
                         ArenaCenter.ChirurgeonGeneral,
                         ChirurgeonGeometry.MovementHalfWidth) * 2f) -
                    (Core.Player.Distance2D(candidate) * 0.25f);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }
        }

        return best;
    }

    private async Task<bool> HandleHoodedHeadsmen()
    {
        UpdateDismembermentForecasts();
        UpdateFlayingFlailForecasts();
        UpdateChoppingBlockForecasts();
        UpdateExecutionWheelForecasts();
        UpdatePealOfJudgmentState();

        if (await CleanseDoom())
        {
            return true;
        }

        if (await HandleHeadsmenCellAvoidance())
        {
            return true;
        }

        if (await InterruptWillBreaker())
        {
            return true;
        }

        return false;
    }

    private void UpdateDismembermentForecasts()
    {
        DateTime now = DateTime.UtcNow;
        HeadsmenLaneForecastState state = hoodedHeadsmenState.Dismemberment;
        foreach (BattleCharacter caster in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor =>
                         actor.IsValid &&
                         actor.IsCasting &&
                         actor.CastingSpellId == EnemyAction.Dismemberment))
        {
            DateTime resolvesAtUtc = now + caster.SpellCastInfo.RemainingCastTime;
            state.LanesByCaster[caster.ObjectId] = new DismembermentLane(
                caster.Location,
                caster.Heading,
                resolvesAtUtc,
                resolvesAtUtc + HeadsmenTiming.DismembermentResolutionGrace);
        }

        foreach (uint casterId in state.LanesByCaster
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            state.LanesByCaster.Remove(casterId);
        }
    }

    private async Task<bool> HandleHeadsmenCellAvoidance()
    {
        DateTime now = DateTime.UtcNow;
        if (!TryCreateHeadsmenPlanningContext(now, out HeadsmenPlanningContext context))
        {
            ReleaseHeadsmenMovement("no Headsmen cell mechanic is active");
            return false;
        }

        DirectedMovementState movement = hoodedHeadsmenState.Movement;
        bool stageChanged = hoodedHeadsmenState.PlanningStage != context.Stage;
        bool mustReplaceDestination = !movement.HasDestination ||
            !IsHeadsmenDestinationSafe(movement.Destination, context);
        bool canImproveForUpcomingStage = !mustReplaceDestination &&
            context.StageResolvesAtUtc >
                now + HeadsmenTiming.UpcomingStageRepositionLock &&
            !IsSafeForUpcomingHeadsmenStages(movement.Destination, context);
        if (mustReplaceDestination || canImproveForUpcomingStage)
        {
            if (!TrySelectHeadsmenDestination(
                    context,
                    out Vector3 destination,
                    out bool destinationSupportsUpcomingStages))
            {
                ReleaseHeadsmenMovement("no safe Headsmen cell destination was found");
                return AvoidanceManager.IsRunningOutOfAvoid;
            }

            // Preserve a safe current-stage position when no point satisfies both resolution stages.
            if (mustReplaceDestination || destinationSupportsUpcomingStages)
            {
                movement.Destination = destination;
                movement.ExpiresAtUtc = context.ActiveUntilUtc;
                movement.HasDestination = true;
                hoodedHeadsmenState.PlanningStage = context.Stage;
            }
        }

        if (stageChanged && movement.HasDestination)
        {
            hoodedHeadsmenState.PlanningStage = context.Stage;
            movement.ExpiresAtUtc = context.ActiveUntilUtc;
        }

        return await MoveToHeadsmenDestination(movement, context.HoldReason);
    }

    private bool TryCreateHeadsmenPlanningContext(
        DateTime now,
        out HeadsmenPlanningContext context)
    {
        Vector3? cellCenter = GetCurrentHeadsmanCellCenter();
        DismembermentLane[] lanes = GetActiveDismembermentLanes(now);
        HeadsmenCircleHazard[] circles = GetRetainedHeadsmenCircleHazards(now);
        HeadsmenCircleHazard[] actionableCircles = circles
            .Where(hazard => hazard.ResolvesAtUtc <= now + HeadsmenTiming.CircleAvoidanceLead)
            .ToArray();
        HeadsmenDonutHazard[] executionWheels = GetActiveExecutionWheelHazards(now);
        PealOfJudgmentLane[] pealLanes = GetActivePealOfJudgmentLanes(now);
        if (cellCenter == null ||
            lanes.Length == 0 && actionableCircles.Length == 0 &&
            executionWheels.Length == 0 && pealLanes.Length == 0)
        {
            context = null;
            return false;
        }

        DateTime pealResolution = pealLanes.Length == 0
            ? DateTime.MaxValue
            : pealLanes.Min(lane => lane.ResolvesAtUtc);
        DateTime circleResolution = actionableCircles.Length == 0
            ? DateTime.MaxValue
            : actionableCircles.Min(hazard => hazard.ResolvesAtUtc);
        DateTime dismembermentResolution = lanes.Length == 0
            ? DateTime.MaxValue
            : lanes.Min(lane => lane.ResolvesAtUtc);
        DateTime executionResolution = executionWheels.Length == 0
            ? DateTime.MaxValue
            : executionWheels.Min(hazard => hazard.ResolvesAtUtc);
        DateTime primaryResolution = new[]
            {
                pealResolution,
                circleResolution,
                dismembermentResolution,
                executionResolution,
            }
            .Min();
        HeadsmenPlanningStage stage = SelectHeadsmenPlanningStage(
            now,
            primaryResolution,
            pealResolution,
            circleResolution,
            dismembermentResolution,
            executionResolution);
        DateTime stageResolution = stage switch
        {
            HeadsmenPlanningStage.PealOfJudgment => pealResolution,
            HeadsmenPlanningStage.Circle => circleResolution,
            HeadsmenPlanningStage.Dismemberment => dismembermentResolution,
            HeadsmenPlanningStage.ExecutionWheel => executionResolution,
            _ => primaryResolution,
        };

        DateTime activeUntilUtc = new[]
            {
                lanes.Length == 0 ? DateTime.MinValue : lanes.Max(lane => lane.ExpiresAtUtc),
                circles.Length == 0 ? DateTime.MinValue : circles.Max(hazard => hazard.ExpiresAtUtc),
                executionWheels.Length == 0
                    ? DateTime.MinValue
                    : executionWheels.Max(hazard => hazard.ExpiresAtUtc),
                pealLanes.Length == 0
                    ? DateTime.MinValue
                    : pealLanes.Max(lane => lane.ExpiresAtUtc),
            }
            .Max();
        HeadsmenCircleHazard[] primaryCircles = actionableCircles
            .Where(hazard => hazard.ResolvesAtUtc <=
                stageResolution + HeadsmenTiming.SequentialResolutionTolerance)
            .ToArray();
        HeadsmenDonutHazard[] primaryExecutionWheels = executionWheels
            .Where(hazard => hazard.ResolvesAtUtc <=
                stageResolution + HeadsmenTiming.SequentialResolutionTolerance)
            .ToArray();
        context = new HeadsmenPlanningContext(
            cellCenter.Value,
            stage,
            lanes,
            circles,
            executionWheels,
            pealLanes,
            primaryCircles,
            primaryExecutionWheels,
            stageResolution,
            activeUntilUtc);
        return true;
    }

    private static HeadsmenPlanningStage SelectHeadsmenPlanningStage(
        DateTime now,
        DateTime primaryResolution,
        DateTime pealResolution,
        DateTime circleResolution,
        DateTime dismembermentResolution,
        DateTime executionResolution)
    {
        DateTime simultaneousCutoff = primaryResolution + HeadsmenTiming.SequentialResolutionTolerance;

        // Peal is a rolling projection; fixed casts take over once they enter the movement deadline.
        DateTime fixedHandoffCutoff = now + HeadsmenTiming.FixedMechanicMovementHandoffLead;
        DateTime firstDueFixedResolution = new[]
        {
            dismembermentResolution <= fixedHandoffCutoff
                ? dismembermentResolution
                : DateTime.MaxValue,
            circleResolution <= fixedHandoffCutoff
                ? circleResolution
                : DateTime.MaxValue,
            executionResolution <= fixedHandoffCutoff
                ? executionResolution
                : DateTime.MaxValue,
        }.Min();
        if (firstDueFixedResolution != DateTime.MaxValue)
        {
            DateTime fixedSimultaneousCutoff =
                firstDueFixedResolution + HeadsmenTiming.SequentialResolutionTolerance;
            if (dismembermentResolution <= fixedSimultaneousCutoff)
            {
                return HeadsmenPlanningStage.Dismemberment;
            }

            if (circleResolution <= fixedSimultaneousCutoff)
            {
                return HeadsmenPlanningStage.Circle;
            }

            return HeadsmenPlanningStage.ExecutionWheel;
        }

        if (pealResolution <= simultaneousCutoff)
        {
            return HeadsmenPlanningStage.PealOfJudgment;
        }

        if (dismembermentResolution <= simultaneousCutoff)
        {
            return HeadsmenPlanningStage.Dismemberment;
        }

        if (circleResolution <= simultaneousCutoff)
        {
            return HeadsmenPlanningStage.Circle;
        }

        return HeadsmenPlanningStage.ExecutionWheel;
    }

    private static bool TrySelectHeadsmenDestination(
        HeadsmenPlanningContext context,
        out Vector3 destination,
        out bool destinationSupportsUpcomingStages)
    {
        Vector3 bestValid = default;
        float bestValidTravel = float.MaxValue;
        float bestValidClearance = float.MinValue;
        bool bestValidSupportsUpcomingStages = false;
        Vector3 bestFallback = default;
        float bestFallbackClearance = float.MinValue;
        float bestFallbackTravel = float.MaxValue;
        bool bestFallbackSupportsUpcomingStages = false;
        bool foundValid = false;
        bool foundFallback = false;

        foreach (Vector3 candidate in EnumerateHeadsmenCandidates(context.CellCenter))
        {
            float clearance = GetHeadsmenPlanningClearance(candidate, context);
            if (clearance < 0f)
            {
                continue;
            }

            float travel = Core.Player.Distance2D(candidate);
            bool supportsUpcomingStages = IsSafeForUpcomingHeadsmenStages(candidate, context);
            if (clearance >= HeadsmenGeometry.CellDestinationClearance &&
                (!foundValid ||
                 (supportsUpcomingStages && !bestValidSupportsUpcomingStages) ||
                 supportsUpcomingStages == bestValidSupportsUpcomingStages &&
                 (travel < bestValidTravel - HeadsmenGeometry.CellScoreTolerance ||
                  MathF.Abs(travel - bestValidTravel) <= HeadsmenGeometry.CellScoreTolerance &&
                  clearance > bestValidClearance)))
            {
                bestValid = candidate;
                bestValidTravel = travel;
                bestValidClearance = clearance;
                bestValidSupportsUpcomingStages = supportsUpcomingStages;
                foundValid = true;
            }

            if (!foundFallback ||
                clearance > bestFallbackClearance + HeadsmenGeometry.CellScoreTolerance ||
                MathF.Abs(clearance - bestFallbackClearance) <= HeadsmenGeometry.CellScoreTolerance &&
                ((supportsUpcomingStages && !bestFallbackSupportsUpcomingStages) ||
                 supportsUpcomingStages == bestFallbackSupportsUpcomingStages &&
                 travel < bestFallbackTravel))
            {
                bestFallback = candidate;
                bestFallbackClearance = clearance;
                bestFallbackTravel = travel;
                bestFallbackSupportsUpcomingStages = supportsUpcomingStages;
                foundFallback = true;
            }
        }

        destination = foundValid ? bestValid : bestFallback;
        destinationSupportsUpcomingStages = foundValid
            ? bestValidSupportsUpcomingStages
            : bestFallbackSupportsUpcomingStages;
        return foundValid || foundFallback;
    }

    // Only the active stage can reject a point; later stages are tie-breakers.
    private static float GetHeadsmenPlanningClearance(
        Vector3 point,
        HeadsmenPlanningContext context) => context.Stage switch
        {
            HeadsmenPlanningStage.PealOfJudgment =>
                GetMinimumPealOfJudgmentClearance(point, context.PealOfJudgmentLanes),
            HeadsmenPlanningStage.Circle =>
                GetMinimumCircleClearance(point, context.PrimaryCircles),
            HeadsmenPlanningStage.Dismemberment =>
                GetMinimumDismembermentClearance(point, context.DismembermentLanes),
            HeadsmenPlanningStage.ExecutionWheel =>
                GetMinimumExecutionWheelSafeClearance(point, context.PrimaryExecutionWheels),
            _ => float.MinValue,
        };

    private static bool IsSafeForUpcomingHeadsmenStages(
        Vector3 point,
        HeadsmenPlanningContext context) =>
        GetMinimumPealOfJudgmentClearance(point, context.PealOfJudgmentLanes) >= 0f &&
        GetMinimumCircleClearance(point, context.CircleHazards) >= 0f &&
        GetMinimumDismembermentClearance(point, context.DismembermentLanes) >= 0f &&
        GetMinimumExecutionWheelSafeClearance(point, context.ExecutionWheels) >= 0f;

    private static IEnumerable<Vector3> EnumerateHeadsmenCandidates(Vector3 cellCenter)
    {
        if (Distance2DSquared(Core.Player.Location, cellCenter) <=
            HeadsmenGeometry.CellCandidateRadiusSquared)
        {
            yield return Core.Player.Location;
        }

        float radius = HeadsmenGeometry.CellCandidateRadius;
        for (float xOffset = -radius;
             xOffset <= radius + HeadsmenGeometry.CellScoreTolerance;
             xOffset += HeadsmenGeometry.CellCandidateStep)
        {
            for (float zOffset = -radius;
                 zOffset <= radius + HeadsmenGeometry.CellScoreTolerance;
                 zOffset += HeadsmenGeometry.CellCandidateStep)
            {
                Vector3 candidate = new(
                    cellCenter.X + xOffset,
                    cellCenter.Y,
                    cellCenter.Z + zOffset);
                if (Distance2DSquared(candidate, cellCenter) <=
                    HeadsmenGeometry.CellCandidateRadiusSquared)
                {
                    yield return candidate;
                }
            }
        }
    }

    private static bool IsHeadsmenDestinationSafe(
        Vector3 destination,
        HeadsmenPlanningContext context) =>
        Distance2DSquared(destination, context.CellCenter) <=
            HeadsmenGeometry.CellCandidateRadiusSquared &&
        GetHeadsmenPlanningClearance(destination, context) >= 0f;

    private static float GetMinimumCircleClearance(
        Vector3 point,
        IReadOnlyCollection<HeadsmenCircleHazard> circles)
    {
        float minimumClearance = float.MaxValue;
        foreach (HeadsmenCircleHazard circle in circles)
        {
            minimumClearance = MathF.Min(
                minimumClearance,
                MathF.Sqrt(Distance2DSquared(point, circle.Location)) - circle.Radius);
        }

        return minimumClearance;
    }

    private static float GetMinimumExecutionWheelSafeClearance(
        Vector3 point,
        IReadOnlyCollection<HeadsmenDonutHazard> executionWheels)
    {
        float minimumClearance = float.MaxValue;
        foreach (HeadsmenDonutHazard executionWheel in executionWheels)
        {
            minimumClearance = MathF.Min(
                minimumClearance,
                executionWheel.InnerRadius -
                MathF.Sqrt(Distance2DSquared(point, executionWheel.Location)));
        }

        return minimumClearance;
    }

    private static float GetMinimumPealOfJudgmentClearance(
        Vector3 point,
        IReadOnlyCollection<PealOfJudgmentLane> lanes)
    {
        float minimumClearance = float.MaxValue;
        foreach (PealOfJudgmentLane lane in lanes)
        {
            minimumClearance = MathF.Min(
                minimumClearance,
                GetForwardRectangleClearance(
                    point,
                    lane.Location,
                    lane.Heading,
                    HeadsmenGeometry.PealOfJudgmentAvoidWidth,
                    HeadsmenGeometry.PealOfJudgmentAvoidTravelLength));
        }

        return minimumClearance;
    }

    private static float GetMinimumDismembermentClearance(
        Vector3 point,
        IReadOnlyCollection<DismembermentLane> lanes)
    {
        float minimumClearance = float.MaxValue;
        foreach (DismembermentLane lane in lanes)
        {
            minimumClearance = MathF.Min(
                minimumClearance,
                GetForwardRectangleClearance(
                    point,
                    lane.Location,
                    lane.Heading,
                    HeadsmenGeometry.DismembermentWidth,
                    HeadsmenGeometry.DismembermentLength));
        }

        return minimumClearance;
    }

    private static float GetForwardRectangleClearance(
        Vector3 point,
        Vector3 location,
        float heading,
        float width,
        float length)
    {
        GetRectangleLocalCoordinates(point, location, heading, out float lateral, out float forward);
        float lateralOutside = MathF.Abs(lateral) - (width / 2f);
        float forwardBefore = -forward;
        float forwardAfter = forward - length;
        if (lateralOutside <= 0f && forwardBefore <= 0f && forwardAfter <= 0f)
        {
            float insideClearance = MathF.Min(
                -lateralOutside,
                MathF.Min(-forwardBefore, -forwardAfter));
            return -insideClearance;
        }

        float lateralDistance = MathF.Max(0f, lateralOutside);
        float forwardDistance = MathF.Max(0f, MathF.Max(forwardBefore, forwardAfter));
        return MathF.Sqrt(
            (lateralDistance * lateralDistance) + (forwardDistance * forwardDistance));
    }

    // Ignore lanes outside the assigned cell so remote role mechanics cannot erase all candidates.
    private static bool DoesDismembermentLaneIntersectCell(DismembermentLane lane, Vector3 cellCenter)
    {
        GetDismembermentLocalCoordinates(cellCenter, lane, out float lateral, out float forward);
        float closestLateral = MathF.Max(
            -HeadsmenGeometry.DismembermentWidth / 2f,
            MathF.Min(HeadsmenGeometry.DismembermentWidth / 2f, lateral));
        float closestForward = MathF.Max(0f, MathF.Min(HeadsmenGeometry.DismembermentLength, forward));
        float deltaLateral = lateral - closestLateral;
        float deltaForward = forward - closestForward;
        float intersectionRadius = HeadsmenGeometry.CellSafeRadius +
            HeadsmenGeometry.DismembermentCellIntersectionMargin;
        return (deltaLateral * deltaLateral) + (deltaForward * deltaForward) <=
            intersectionRadius * intersectionRadius;
    }

    private static void GetDismembermentLocalCoordinates(
        Vector3 point,
        DismembermentLane lane,
        out float lateral,
        out float forward) =>
        GetRectangleLocalCoordinates(point, lane.Location, lane.Heading, out lateral, out forward);

    private static void GetRectangleLocalCoordinates(
        Vector3 point,
        Vector3 location,
        float heading,
        out float lateral,
        out float forward)
    {
        float deltaX = point.X - location.X;
        float deltaZ = point.Z - location.Z;
        float sine = MathF.Sin(heading);
        float cosine = MathF.Cos(heading);
        lateral = (deltaX * cosine) - (deltaZ * sine);
        forward = (deltaX * sine) + (deltaZ * cosine);
    }

    // RB avoidance pathing cannot reliably solve the disconnected lane geometry inside a role cell.
    private static async Task<bool> MoveToHeadsmenDestination(
        DirectedMovementState movement,
        string holdReason)
    {
        CapabilityManager.Update(
            movement.Handle,
            CapabilityFlags.Movement,
            EncounterTiming.DirectedMovementLeaseMilliseconds,
            holdReason);
        movement.Owned = true;

        if (Core.Player.Distance2D(movement.Destination) <= HeadsmenGeometry.CellArrivalRadius)
        {
            Navigator.PlayerMover.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(movement.Destination);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseHeadsmenMovement(string reason)
    {
        hoodedHeadsmenState.PlanningStage = null;
        ReleaseDirectedMovement(hoodedHeadsmenState.Movement, reason);
    }

    private void UpdateFlayingFlailForecasts()
    {
        DateTime now = DateTime.UtcNow;
        HeadsmenCircleForecastState state = hoodedHeadsmenState.FlayingFlail;

        foreach (BattleCharacter caster in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor =>
                         actor.IsValid &&
                         actor.IsCasting &&
                         actor.CastingSpellId == EnemyAction.FlayingFlail &&
                         IsHazardNearCurrentCell(
                             actor.SpellCastInfo.CastLocation,
                             HeadsmenGeometry.CellSafeRadius + HeadsmenGeometry.FlayingFlailAvoidRadius)))
        {
            DateTime resolvesAtUtc = now + caster.SpellCastInfo.RemainingCastTime;
            state.ForecastsByCaster[caster.ObjectId] = new HeadsmenCircleForecast(
                caster.SpellCastInfo.CastLocation,
                resolvesAtUtc,
                resolvesAtUtc + HeadsmenTiming.CircleResolutionGrace);
        }

        RemoveExpiredHeadsmenCircleForecasts(state, now);
    }

    private void UpdateChoppingBlockForecasts()
    {
        DateTime now = DateTime.UtcNow;
        HeadsmenCircleForecastState state = hoodedHeadsmenState.ChoppingBlock;
        uint assignedBaseId = GetAssignedHeadsmanBaseId();

        foreach (BattleCharacter caster in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor =>
                         IsRelevantHeadsman(actor, assignedBaseId) &&
                         actor.IsCasting &&
                         actor.CastingSpellId == EnemyAction.ChoppingBlock))
        {
            DateTime resolvesAtUtc = now + caster.SpellCastInfo.RemainingCastTime;
            state.ForecastsByCaster[caster.ObjectId] = new HeadsmenCircleForecast(
                caster.Location,
                resolvesAtUtc,
                resolvesAtUtc + HeadsmenTiming.CircleResolutionGrace);
        }

        RemoveExpiredHeadsmenCircleForecasts(state, now);
    }

    // Retain Execution Wheel after cast state clears so movement is not released before the hit.
    private void UpdateExecutionWheelForecasts()
    {
        DateTime now = DateTime.UtcNow;
        HeadsmenCircleForecastState state = hoodedHeadsmenState.ExecutionWheel;
        uint assignedBaseId = GetAssignedHeadsmanBaseId();

        foreach (BattleCharacter caster in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor =>
                         IsRelevantHeadsman(actor, assignedBaseId) &&
                         actor.IsCasting &&
                         actor.CastingSpellId == EnemyAction.ExecutionWheel))
        {
            DateTime resolvesAtUtc = now + caster.SpellCastInfo.RemainingCastTime;
            state.ForecastsByCaster[caster.ObjectId] = new HeadsmenCircleForecast(
                caster.Location,
                resolvesAtUtc,
                resolvesAtUtc + HeadsmenTiming.CircleResolutionGrace);
        }

        RemoveExpiredHeadsmenCircleForecasts(state, now);
    }

    private static void RemoveExpiredHeadsmenCircleForecasts(
        HeadsmenCircleForecastState state,
        DateTime now)
    {
        foreach (uint casterId in state.ForecastsByCaster
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            state.ForecastsByCaster.Remove(casterId);
        }
    }

    private void UpdatePealOfJudgmentState()
    {
        PealOfJudgmentState state = hoodedHeadsmenState.PealOfJudgment;
        BattleCharacter[] liveSwords = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.BaseId == EnemyObjectId.SwordOfJustice)
            .ToArray();
        HashSet<uint> liveIds = [.. liveSwords.Select(actor => actor.ObjectId)];
        Vector3? currentCellCenter = GetCurrentHeadsmanCellCenter();

        if (currentCellCenter != null)
        {
            foreach (BattleCharacter sword in liveSwords.Where(actor =>
                         actor.HasAura(EnemyAura.Activate) &&
                         !state.ActiveSwordIds.Contains(actor.ObjectId) &&
                         Distance2DSquared(actor.Location, currentCellCenter.Value) <=
                         HeadsmenGeometry.CellAssignmentRadiusSquared))
            {
                state.ActiveSwordIds.Add(sword.ObjectId);
            }
        }

        // The activation status ends before the traveling hit; actor destruction owns release.
        state.ActiveSwordIds.RemoveWhere(id => !liveIds.Contains(id));
    }

    private static async Task<bool> CleanseDoom()
    {
        if (!Core.Player.IsHealer() || !Core.Player.HasAura(PlayerAura.Doom))
        {
            return false;
        }

        SpellData esuna = DataManager.GetSpellData(PlayerAction.Esuna);
        if (esuna == null)
        {
            return false;
        }

        // Retain priority until Esuna starts without canceling an in-progress Esuna.
        if (Core.Player.IsCasting)
        {
            if (Core.Player.CastingSpellId != PlayerAction.Esuna)
            {
                ActionManager.StopCasting();
            }

            await Coroutine.Yield();
            return true;
        }

        if (!ActionManager.CanCast(PlayerAction.Esuna, Core.Player))
        {
            await Coroutine.Yield();
            return true;
        }

        ActionManager.DoAction(esuna, Core.Player);
        await Coroutine.Sleep(HeadsmenTiming.ActionSettleMilliseconds);
        return true;
    }

    private static async Task<bool> InterruptWillBreaker()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor =>
                actor.IsValid &&
                actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.WillBreaker &&
                actor.SpellCastInfo.TargetId == Core.Player.ObjectId);

        if (caster == null)
        {
            return false;
        }

        foreach (uint actionId in PlayerAction.Interrupts)
        {
            if (!ActionManager.CanCast(actionId, caster))
            {
                continue;
            }

            SpellData action = DataManager.GetSpellData(actionId);
            if (action == null)
            {
                continue;
            }

            ActionManager.DoAction(action, caster);
            await Coroutine.Sleep(HeadsmenTiming.ActionSettleMilliseconds);
            return true;
        }

        return false;
    }

    private async Task<bool> HandleImmortalRemains()
    {
        if (!IsImmortalRemainsCombatActive())
        {
            ReleaseMemoryOfTheStormMovement("Immortal Remains combat ended");
            ReleaseDirectedMovement(immortalRemainsState.Impression, "Immortal Remains combat ended");
            ReleaseBombardmentTrustFallback("Immortal Remains combat ended");
            immortalRemainsState.Clear();
            return false;
        }

        UpdateKeraunographyForecasts();
        UpdateBombardmentForecasts();
        UpdateElectrayForecasts();
        UpdateTurmoilForecast();
        UpdateMemoryOfTheStormStack();

        if (await HandleImpression())
        {
            return true;
        }

        if (await HandleMemoryOfTheStorm())
        {
            return true;
        }

        if (await HandleBombardmentTrustFallback())
        {
            return true;
        }

        return false;
    }

    private async Task<bool> HandleImpression()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor =>
                actor.IsValid &&
                actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.ImpressionAoe);

        if (caster != null && !immortalRemainsState.Impression.HasDestination)
        {
            Vector3? destination = FindImpressionDestination();
            if (destination != null)
            {
                DirectedMovementState movement = immortalRemainsState.Impression;
                movement.Destination = destination.Value;
                movement.HasDestination = true;
                movement.ExpiresAtUtc = now + caster.SpellCastInfo.RemainingCastTime +
                    ImmortalTiming.ImpressionResolutionGrace;
            }
        }

        if (!immortalRemainsState.Impression.HasDestination)
        {
            return false;
        }

        if (now >= immortalRemainsState.Impression.ExpiresAtUtc)
        {
            ReleaseDirectedMovement(immortalRemainsState.Impression, "Impression resolved");
            return false;
        }

        return await MoveToDirectedDestination(
            immortalRemainsState.Impression,
            ImmortalGeometry.ImpressionArrivalRadius,
            "Holding a diagonal Impression landing");
    }

    private Vector3? FindImpressionDestination()
    {
        Vector3[] largeBombardments = GetBombardmentLocations(BombardmentShape.Large);
        Vector3? best = null;
        float bestScore = float.MinValue;

        foreach (Vector3 direction in ImmortalGeometry.ImpressionDiagonalDirections)
        {
            Vector3 candidate = ArenaCenter.ImmortalRemains +
                (direction * ImmortalGeometry.ImpressionStagingRadius);
            Vector3 landing = candidate + (direction * ImmortalGeometry.ImpressionKnockbackDistance);
            if (!IsInsideSquareArena(
                    landing,
                    ArenaCenter.ImmortalRemains,
                    ImmortalGeometry.MovementHalfWidth) ||
                largeBombardments.Any(location =>
                    DistancePointToSegmentSquared(location, candidate, landing) <=
                    ImmortalGeometry.ImpressionBombardmentClearanceSquared) ||
                AvoidanceManager.Avoids.Any(avoid =>
                    avoid.IsPointInAvoid(candidate) || avoid.IsPointInAvoid(landing)))
            {
                continue;
            }

            float bombardmentClearance = largeBombardments.Length == 0
                ? ImmortalGeometry.ArenaSafeSize
                : MathF.Sqrt(largeBombardments.Min(location => DistancePointToSegmentSquared(
                    location,
                    candidate,
                    landing)));
            float score = (bombardmentClearance * 4f) - Core.Player.Distance2D(candidate);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best ?? ImmortalGeometry.ImpressionDiagonalDirections
            .Select(direction => ArenaCenter.ImmortalRemains +
                (direction * ImmortalGeometry.ImpressionStagingRadius))
            .OrderBy(candidate => Core.Player.Distance2D(candidate))
            .FirstOrDefault();
    }

    // The pre-cast lane remains dangerous until the helper resolves it.
    private void UpdateKeraunographyForecasts()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid)
            .ToArray();

        foreach (BattleCharacter caster in actors.Where(actor =>
                     actor.IsCasting && actor.CastingSpellId == EnemyAction.KeraunographyPre))
        {
            if (!immortalRemainsState.Keraunography.PreCasters.Add(caster.ObjectId))
            {
                continue;
            }

            DateTime expiresAtUtc = now + caster.SpellCastInfo.RemainingCastTime +
                ImmortalTiming.KeraunographyMaximumPostPrecastHold;
            immortalRemainsState.Keraunography.Lanes.Add(new KeraunographyLane(
                caster.Location,
                caster.Heading,
                expiresAtUtc));
        }

        BattleCharacter[] resolvingCasters = actors
            .Where(actor => actor.IsCasting && actor.CastingSpellId == EnemyAction.Keraunography)
            .ToArray();
        if (resolvingCasters.Length != 0)
        {
            TimeSpan longestRemainingCast = resolvingCasters.Max(actor => actor.SpellCastInfo.RemainingCastTime);
            immortalRemainsState.Keraunography.ClearAtUtc = now + longestRemainingCast +
                ImmortalTiming.KeraunographyResolutionGrace;
        }

        if (immortalRemainsState.Keraunography.ClearAtUtc != DateTime.MinValue &&
            now >= immortalRemainsState.Keraunography.ClearAtUtc)
        {
            immortalRemainsState.Keraunography.Clear();
            return;
        }

        immortalRemainsState.Keraunography.Lanes.RemoveAll(lane => lane.ExpiresAtUtc <= now);
        if (immortalRemainsState.Keraunography.Lanes.Count == 0)
        {
            immortalRemainsState.Keraunography.Clear();
        }
    }

    // Electray damage lands after the helper's cast wrapper disappears.
    private void UpdateElectrayForecasts()
    {
        DateTime now = DateTime.UtcNow;
        ElectrayState state = immortalRemainsState.Electray;
        foreach (uint casterId in state.LanesByCaster
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            state.LanesByCaster.Remove(casterId);
        }

        BattleCharacter[] casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor =>
                actor.IsValid &&
                actor.IsCasting &&
                actor.CastingSpellId == EnemyAction.Electray)
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .ThenBy(actor => actor.ObjectId)
            .ToArray();
        if (casters.Length == 0)
        {
            return;
        }

        foreach (BattleCharacter caster in casters)
        {
            // Include server-effect lag so the next wave cannot replace unresolved lanes.
            DateTime resolvesAtUtc = now + caster.SpellCastInfo.RemainingCastTime +
                ImmortalTiming.ElectrayActionEffectDelay;
            DateTime expiresAtUtc = resolvesAtUtc +
                ImmortalTiming.ElectrayResolutionGrace;
            state.LanesByCaster[caster.ObjectId] = new ElectrayLane(
                caster.Location,
                caster.Heading,
                resolvesAtUtc,
                expiresAtUtc);
        }
    }

    // Preserved Terror tethers expose Bombardment before its visual telegraph appears.
    private void UpdateBombardmentForecasts()
    {
        DateTime now = DateTime.UtcNow;
        immortalRemainsState.Bombardment.Forecasts.RemoveAll(forecast => forecast.ExpiresAtUtc <= now);

        BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid)
            .ToArray();
        bool mementoCasting = actors.Any(actor =>
            actor.BaseId == EnemyObjectId.ImmortalRemains &&
            actor.IsCasting &&
            actor.CastingSpellId == EnemyAction.Memento);
        if (mementoCasting && !immortalRemainsState.Bombardment.MementoCastActive)
        {
            immortalRemainsState.Bombardment.TrustFallback.Begin(
                now + ImmortalTiming.BombardmentTrustFallbackWindow);
        }

        immortalRemainsState.Bombardment.MementoCastActive = mementoCasting;

        bool differentShadeMechanicVisible = actors.Any(actor =>
            actor.IsCasting &&
            (actor.CastingSpellId == EnemyAction.Electray ||
             actor.CastingSpellId == EnemyAction.KeraunographyPre));
        if (differentShadeMechanicVisible)
        {
            ReleaseBombardmentTrustFallback("Memento resolved as a different shade mechanic");
        }
        else if (!immortalRemainsState.Bombardment.TrustFallback.IsActive(now))
        {
            ReleaseBombardmentTrustFallback("Bombardment fallback window expired");
        }

        BattleCharacter[] preservedTerrors = actors
            .Where(actor => actor.BaseId == EnemyObjectId.PreservedTerror)
            .ToArray();
        BattleCharacter[] selectedTerrors = GetSelectedPreservedTerrors(preservedTerrors);

        if (selectedTerrors.Length != 0)
        {
            immortalRemainsState.Bombardment.TetherBatchActive = true;
            foreach (BattleCharacter terror in selectedTerrors)
            {
                immortalRemainsState.Bombardment.TetheredShades[terror.ObjectId] =
                    new PreservedTerrorSnapshot(terror.Location, terror.Heading);
            }

            return;
        }

        if (!immortalRemainsState.Bombardment.TetherBatchActive)
        {
            return;
        }

        int activeStatusCount = preservedTerrors.Count(actor => actor.HasAura(EnemyAura.PreservedTerror));
        CreateBombardmentForecasts(preservedTerrors, activeStatusCount, now);
        if (HasRetainedBombardmentForecast())
        {
            ReleaseBombardmentTrustFallback("early Bombardment geometry available");
        }

        immortalRemainsState.Bombardment.ClearTethers();
    }

    private bool HasRetainedBombardmentForecast() =>
        immortalRemainsState.Bombardment.Forecasts.Any(forecast => forecast.ExpiresAtUtc > DateTime.UtcNow);

    // Follow the Trusts only until Memento exposes reliable Bombardment geometry.
    private async Task<bool> HandleBombardmentTrustFallback()
    {
        BombardmentState state = immortalRemainsState.Bombardment;
        DateTime now = DateTime.UtcNow;
        if (!state.TrustFallback.IsActive(now) || HasRetainedBombardmentForecast())
        {
            ReleaseBombardmentTrustFallback("Bombardment Trust fallback inactive");
            return false;
        }

        return await HandleTrustMovement(
            state.TrustFallback,
            "Following the early Bombardment Trust movement");
    }

    private static async Task<bool> HandleTrustMovement(TrustMovementState state, string reason)
    {
        BattleCharacter anchor = GetTrustAnchor(state);
        if (anchor == null)
        {
            ReleaseTrustMovement(state, "no living Trust anchor available");
            return false;
        }

        CapabilityManager.Update(
            state.Handle,
            CapabilityFlags.Movement,
            EncounterTiming.DirectedMovementLeaseMilliseconds,
            reason);
        state.Owned = true;

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(anchor.Location) <= ImmortalGeometry.TrustFollowRadius)
        {
            MovementManager.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        await CommonTasks.MoveTo(anchor.Location);
        return true;
    }

    private static BattleCharacter GetTrustAnchor(TrustMovementState state)
    {
        BattleCharacter[] trusts = GetLivingTrusts();
        BattleCharacter anchor = trusts.FirstOrDefault(actor => actor.ObjectId == state.AnchorObjectId);
        if (anchor != null)
        {
            return anchor;
        }

        anchor = trusts
            .OrderBy(actor => Core.Player.Distance2D(actor.Location))
            .FirstOrDefault();
        state.AnchorObjectId = anchor?.ObjectId ?? 0;
        return anchor;
    }

    private void ReleaseBombardmentTrustFallback(string reason)
    {
        ReleaseTrustMovement(immortalRemainsState.Bombardment.TrustFallback, reason);
    }

    private static void ReleaseTrustMovement(TrustMovementState state, string reason)
    {
        if (state.Owned)
        {
            CapabilityManager.Clear(state.Handle, CapabilityFlags.Movement, reason);
        }

        state.Clear();
    }

    // Direct actions are preferred; Trust consensus covers the first cast when no action is visible.
    private void UpdateTurmoilForecast()
    {
        DateTime now = DateTime.UtcNow;
        TurmoilState state = immortalRemainsState.Turmoil;
        BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid)
            .ToArray();
        BattleCharacter[] electrayCasters = actors
            .Where(actor => actor.IsCasting && actor.CastingSpellId == EnemyAction.Electray)
            .ToArray();
        BattleCharacter[] signalActors = actors
            .Where(actor =>
                actor.BaseId == EnemyObjectId.ImmortalRemains || actor.BaseId == EnemyObjectId.Helper)
            .ToArray();
        bool electrayCasting = electrayCasters.Length != 0;
        if (electrayCasting && !state.ElectrayCastActive)
        {
            state.BeginSequence(now);
        }

        if (electrayCasting)
        {
            TimeSpan longestRemainingCast = electrayCasters.Max(actor => actor.SpellCastInfo.RemainingCastTime);
            state.SignalWatchUntilUtc = now + longestRemainingCast + ImmortalTiming.TurmoilPostElectraySignalWindow;
        }

        if (!electrayCasting && state.ElectrayCastActive)
        {
            state.BeginPostElectrayInference(now);
        }

        state.ElectrayCastActive = electrayCasting;
        foreach (BattleCharacter actor in signalActors)
        {
            uint actionId = actor.CastingSpellId;
            state.LastObservedActionByActor.TryGetValue(actor.ObjectId, out uint previousActionId);
            state.LastObservedActionByActor[actor.ObjectId] = actionId;
            if (actionId == previousActionId || !state.IsSignalWatchActive(now))
            {
                continue;
            }

            switch (actionId)
            {
                case EnemyAction.TurmoilRightHand:
                    ArmTurmoilForecast(
                        ArenaHalf.Left,
                        now + ImmortalTiming.TurmoilDirectForecastLifetime);
                    state.DirectForecastSelected = true;
                    break;
                case EnemyAction.TurmoilLeftHand:
                    ArmTurmoilForecast(
                        ArenaHalf.Right,
                        now + ImmortalTiming.TurmoilDirectForecastLifetime);
                    state.DirectForecastSelected = true;
                    break;
                case EnemyAction.TurmoilHit:
                    state.TrimForecastAfterHit(now + ImmortalTiming.TurmoilResolutionGrace);
                    state.SignalWatchUntilUtc = now;
                    state.ClearTrustCandidate();
                    break;
            }
        }

        TryArmTurmoilFromTrustConsensus(state, now);
        state.RemoveMissingActors(signalActors.Select(actor => actor.ObjectId));
        if (state.Forecast?.ExpiresAtUtc <= now)
        {
            state.Forecast = null;
        }
    }

    private void TryArmTurmoilFromTrustConsensus(TurmoilState state, DateTime now)
    {
        if (state.DirectForecastSelected || state.ElectrayCastActive ||
            !state.IsSignalWatchActive(now) ||
            now < state.TrustInferenceNotBeforeUtc)
        {
            return;
        }

        BattleCharacter[] trusts = GetLivingTrusts();
        int leftVotes = trusts.Count(actor =>
            actor.Location.X - ArenaCenter.ImmortalRemains.X <= -ImmortalGeometry.TurmoilTrustSideMinimumOffset);
        int rightVotes = trusts.Count(actor =>
            actor.Location.X - ArenaCenter.ImmortalRemains.X >= ImmortalGeometry.TurmoilTrustSideMinimumOffset);
        if (Math.Max(leftVotes, rightVotes) < ImmortalGeometry.TurmoilTrustConsensusCount ||
            leftVotes == rightVotes)
        {
            state.ClearTrustCandidate();
            return;
        }

        ArenaHalf safeHalf = leftVotes > rightVotes
            ? ArenaHalf.Left
            : ArenaHalf.Right;
        if (state.TrustSafeHalfCandidate != safeHalf)
        {
            state.BeginTrustCandidate(safeHalf, now);
            return;
        }

        TimeSpan requiredStability = state.SelectedTrustSafeHalf == null
            ? ImmortalTiming.TurmoilTrustInitialConsensusStability
            : ImmortalTiming.TurmoilTrustCorrectionConsensusStability;
        if (now - state.TrustCandidateSinceUtc < requiredStability ||
            state.SelectedTrustSafeHalf == safeHalf)
        {
            return;
        }

        ArenaHalf unsafeHalf = safeHalf == ArenaHalf.Left
            ? ArenaHalf.Right
            : ArenaHalf.Left;
        ArmTurmoilForecast(
            unsafeHalf,
            now + ImmortalTiming.TurmoilTrustForecastLifetime);
        state.SelectedTrustSafeHalf = safeHalf;
    }

    private static BattleCharacter[] GetLivingTrusts() =>
        PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(actor =>
                actor != null &&
                actor.IsValid &&
                actor.IsAlive &&
                !actor.IsMe)
            .ToArray();

    private void ArmTurmoilForecast(ArenaHalf unsafeHalf, DateTime expiresAtUtc)
    {
        float xOffset = unsafeHalf == ArenaHalf.Left
            ? -ImmortalGeometry.TurmoilHalfOriginOffset
            : ImmortalGeometry.TurmoilHalfOriginOffset;
        Vector3 origin = ArenaCenter.ImmortalRemains +
            new Vector3(xOffset, 0f, -ImmortalGeometry.TurmoilHalfLength / 2f);
        immortalRemainsState.Turmoil.Forecast = new TurmoilForecast(
            origin,
            expiresAtUtc);
    }

    private TurmoilForecast[] GetActiveTurmoilForecasts()
    {
        TurmoilForecast forecast = immortalRemainsState.Turmoil.Forecast;
        return forecast is not null && forecast.ExpiresAtUtc > DateTime.UtcNow
            ? [forecast]
            : [];
    }

    private static BattleCharacter[] GetSelectedPreservedTerrors(BattleCharacter[] preservedTerrors)
    {
        if (preservedTerrors.Length == 0)
        {
            return [];
        }

        Dictionary<uint, BattleCharacter> terrorById = preservedTerrors.ToDictionary(actor => actor.ObjectId);
        var selectedIds = new HashSet<uint>();

        foreach (BattleCharacter actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor => actor.IsValid))
        {
            if (actor.VfxContainer.IsValid)
            {
                foreach (VfxContainer.Tether tether in actor.VfxContainer.Tethers ?? [])
                {
                    if (tether.Id != EnemyTether.PreservedTerrorSelection)
                    {
                        continue;
                    }

                    if (actor.BaseId == EnemyObjectId.PreservedTerror)
                    {
                        selectedIds.Add(actor.ObjectId);
                    }

                    if (terrorById.ContainsKey(tether.TargetId))
                    {
                        selectedIds.Add(tether.TargetId);
                    }
                }
            }
        }

        return selectedIds
            .Where(terrorById.ContainsKey)
            .Select(objectId => terrorById[objectId])
            .ToArray();
    }

    private void CreateBombardmentForecasts(
        BattleCharacter[] preservedTerrors,
        int activeStatusCount,
        DateTime observedAtUtc)
    {
        int tetherCount = immortalRemainsState.Bombardment.TetheredShades.Count;
        bool isBombardmentBatch = tetherCount == ImmortalGeometry.StandardBombardmentTetherCount ||
            (tetherCount == ImmortalGeometry.ImpressionBombardmentTetherCount &&
             activeStatusCount > ImmortalGeometry.ImpressionBombardmentTetherCount);
        if (!isBombardmentBatch)
        {
            return;
        }

        Vector3[] activeShadeLocations = preservedTerrors
            .Where(actor => actor.HasAura(EnemyAura.PreservedTerror))
            .Select(actor => actor.Location)
            .ToArray();
        DateTime resolutionUtc = observedAtUtc + ImmortalTiming.BombardmentResolutionDelay;
        DateTime expiresAtUtc = resolutionUtc + ImmortalTiming.BombardmentResolutionGrace;

        foreach (PreservedTerrorSnapshot shade in immortalRemainsState.Bombardment.TetheredShades.Values)
        {
            Vector3 groupCenter = GetPreservedTerrorGroupCenter(shade);
            int nearbyShadeCount = activeShadeLocations.Count(location =>
                Distance2DSquared(location, groupCenter) <= ImmortalGeometry.BombardmentGroupRadiusSquared);
            BombardmentShape shape = nearbyShadeCount >= ImmortalGeometry.BombardmentLargeGroupSize
                ? BombardmentShape.Large
                : BombardmentShape.Small;
            Vector3 location = shape == BombardmentShape.Large ? groupCenter : shade.Location;

            bool duplicate = immortalRemainsState.Bombardment.Forecasts.Any(existing =>
                existing.Shape == shape &&
                Distance2DSquared(existing.Location, location) <= ImmortalGeometry.BombardmentMergeRadiusSquared);
            if (!duplicate)
            {
                immortalRemainsState.Bombardment.Forecasts.Add(new BombardmentForecast(
                    shape,
                    location,
                    expiresAtUtc));
            }
        }
    }

    private Vector3[] GetActiveBombardmentLocations(BombardmentShape shape)
    {
        if (IsImpressionStagingActive())
        {
            return [];
        }

        return GetBombardmentLocations(shape);
    }

    private Vector3[] GetBombardmentLocations(BombardmentShape shape)
    {
        DateTime now = DateTime.UtcNow;
        uint actionId = shape == BombardmentShape.Small
            ? EnemyAction.BombardmentSmall
            : EnemyAction.BombardmentLarge;
        List<Vector3> locations = immortalRemainsState.Bombardment.Forecasts
            .Where(forecast => forecast.Shape == shape &&
                forecast.ExpiresAtUtc > now)
            .Select(forecast => forecast.Location)
            .ToList();

        foreach (Vector3 liveLocation in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == actionId)
                     .Select(actor => actor.SpellCastInfo.CastLocation))
        {
            if (!locations.Any(existing =>
                    Distance2DSquared(existing, liveLocation) <= ImmortalGeometry.BombardmentMergeRadiusSquared))
            {
                locations.Add(liveLocation);
            }
        }

        return locations.ToArray();
    }

    private static Vector3 GetPreservedTerrorGroupCenter(PreservedTerrorSnapshot shade) =>
        new(
            shade.Location.X + (MathF.Sin(shade.Heading) * ImmortalGeometry.BombardmentGroupCenterOffset),
            shade.Location.Y,
            shade.Location.Z + (MathF.Cos(shade.Heading) * ImmortalGeometry.BombardmentGroupCenterOffset));

    private static float Distance2DSquared(Vector3 first, Vector3 second)
    {
        float deltaX = first.X - second.X;
        float deltaZ = first.Z - second.Z;
        return (deltaX * deltaX) + (deltaZ * deltaZ);
    }

    private static Vector3? ProjectKnockbackLanding(Vector3 position, Vector3 source, float distance)
    {
        float deltaX = position.X - source.X;
        float deltaZ = position.Z - source.Z;
        float length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (length <= ArenaGeometry.MinimumDirectionLength)
        {
            return null;
        }

        return new Vector3(
            position.X + ((deltaX / length) * distance),
            position.Y,
            position.Z + ((deltaZ / length) * distance));
    }

    private static bool IsInsideSquareArena(Vector3 point, Vector3 center, float halfWidth) =>
        MathF.Abs(point.X - center.X) <= halfWidth &&
        MathF.Abs(point.Z - center.Z) <= halfWidth;

    private static float GetSquareArenaMargin(Vector3 point, Vector3 center, float halfWidth) =>
        MathF.Min(
            halfWidth - MathF.Abs(point.X - center.X),
            halfWidth - MathF.Abs(point.Z - center.Z));

    private static float DistancePointToSegmentSquared(Vector3 point, Vector3 start, Vector3 end)
    {
        float segmentX = end.X - start.X;
        float segmentZ = end.Z - start.Z;
        float segmentLengthSquared = (segmentX * segmentX) + (segmentZ * segmentZ);
        if (segmentLengthSquared <= ArenaGeometry.MinimumDirectionLengthSquared)
        {
            return Distance2DSquared(point, start);
        }

        float projection = (((point.X - start.X) * segmentX) + ((point.Z - start.Z) * segmentZ)) /
            segmentLengthSquared;
        projection = MathF.Max(0f, MathF.Min(1f, projection));
        Vector3 closest = new(
            start.X + (segmentX * projection),
            start.Y,
            start.Z + (segmentZ * projection));
        return Distance2DSquared(point, closest);
    }

    private static async Task<bool> MoveToDirectedDestination(
        DirectedMovementState state,
        float arrivalRadius,
        string reason)
    {
        CapabilityManager.Update(
            state.Handle,
            CapabilityFlags.Movement,
            EncounterTiming.DirectedMovementLeaseMilliseconds,
            reason);
        state.Owned = true;

        if (Core.Player.Distance2D(state.Destination) <= arrivalRadius)
        {
            MovementManager.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        await CommonTasks.MoveTo(state.Destination);
        return true;
    }

    private static void ReleaseDirectedMovement(DirectedMovementState state, string reason)
    {
        if (state.Owned)
        {
            CapabilityManager.Clear(state.Handle, CapabilityFlags.Movement, reason);
        }

        state.Clear();
    }

    // Hold the stack from its first signal through the next Memento handoff.
    private void UpdateMemoryOfTheStormStack()
    {
        DateTime now = DateTime.UtcNow;
        MemoryOfTheStormState state = immortalRemainsState.MemoryOfTheStorm;
        BattleCharacter boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid && actor.BaseId == EnemyObjectId.ImmortalRemains);
        bool markerActive = HasActorVfx(boss, PlayerVfx.MemoryOfTheStormLineStack);
        bool castActive = boss != null && boss.IsCasting &&
            boss.CastingSpellId == EnemyAction.MemoryOfTheStormCast;
        bool nextMementoActive = boss != null && boss.IsCasting &&
            boss.CastingSpellId == EnemyAction.Memento;
        bool signalActive = markerActive || castActive;

        if (nextMementoActive)
        {
            ReleaseTrustMovement(state.StackMovement, "Memory of the Storm handed movement to Memento");
        }
        else
        {
            if (signalActive && !state.SignalActive)
            {
                state.StackMovement.Begin(now + ImmortalTiming.MemoryOfTheStormSignalWindow);
            }

            if (castActive && !state.CastActive)
            {
                // Replace the icon fallback with the observed cast-to-effect deadline.
                state.StackMovement.SetExpiry(
                    now + boss.SpellCastInfo.RemainingCastTime +
                    ImmortalTiming.MemoryOfTheStormActionEffectDelay +
                    ImmortalTiming.MemoryOfTheStormResolutionGrace);
            }

            if (!state.StackMovement.IsActive(now))
            {
                ReleaseTrustMovement(state.StackMovement, "Memory of the Storm resolved");
            }
        }

        state.SignalActive = signalActive;
        state.CastActive = castActive;
    }

    private Task<bool> HandleMemoryOfTheStorm()
    {
        MemoryOfTheStormState state = immortalRemainsState.MemoryOfTheStorm;
        return state.StackMovement.IsActive(DateTime.UtcNow)
            ? HandleTrustMovement(state.StackMovement, "Joining the Memory of the Storm line stack")
            : Task.FromResult(false);
    }

    private static bool HasActorVfx(BattleCharacter actor, uint vfxId)
    {
        if (actor == null || !actor.IsValid || !actor.VfxContainer.IsValid)
        {
            return false;
        }

        return actor.VfxContainer.Vfx.Any(vfx =>
            vfx != null && vfx.IsValid && vfx.Id == vfxId);
    }

    private void ReleaseMemoryOfTheStormMovement(string reason)
    {
        MemoryOfTheStormState state = immortalRemainsState.MemoryOfTheStorm;
        ReleaseTrustMovement(state.StackMovement, reason);
        state.ResetSignals();
    }

    private static uint GetCellBlockAuraId()
    {
        if (Core.Player == null)
        {
            return 0;
        }

        if (Core.Player.HasAura(PlayerAura.CellBlockA))
        {
            return PlayerAura.CellBlockA;
        }

        if (Core.Player.HasAura(PlayerAura.CellBlockB))
        {
            return PlayerAura.CellBlockB;
        }

        if (Core.Player.HasAura(PlayerAura.CellBlockC))
        {
            return PlayerAura.CellBlockC;
        }

        return Core.Player.HasAura(PlayerAura.CellBlockD) ? PlayerAura.CellBlockD : 0;
    }

    private static Vector3? GetCurrentHeadsmanCellCenter() => GetCellBlockAuraId() switch
    {
        PlayerAura.CellBlockA => ArenaCenter.CellBlockA,
        PlayerAura.CellBlockB => ArenaCenter.CellBlockB,
        PlayerAura.CellBlockC => ArenaCenter.CellBlockC,
        PlayerAura.CellBlockD => ArenaCenter.CellBlockD,
        _ => null,
    };

    private static uint GetAssignedHeadsmanBaseId() => GetCellBlockAuraId() switch
    {
        PlayerAura.CellBlockA => EnemyObjectId.BloodyHeadsman,
        PlayerAura.CellBlockB => EnemyObjectId.PaleHeadsman,
        PlayerAura.CellBlockC => EnemyObjectId.RavenousHeadsman,
        PlayerAura.CellBlockD => EnemyObjectId.PestilentHeadsman,
        _ => 0,
    };

    private static bool IsRelevantHeadsman(BattleCharacter actor, uint assignedBaseId) =>
        actor.IsValid &&
        actor.IsAlive &&
        EnemyObjectId.Headsmen.Contains(actor.BaseId) &&
        (assignedBaseId == 0 || actor.BaseId == assignedBaseId);

    private static class ArenaGeometry
    {
        // Finite exterior boundary for encounter navigation.
        public const float OuterBoundarySize = 90f;
        public const float AvoidHeight = 15f;
        public const float MinimumDirectionLength = 0.01f;
        public const float MinimumDirectionLengthSquared = MinimumDirectionLength * MinimumDirectionLength;
    }

    private static class ChirurgeonGeometry
    {
        // 40x40 platform with a half-yalm wall inset.
        public const float ArenaSafeSize = 39f;
        public const float SterileSphereSmallRadius = 8f;
        public const float SterileSphereLargeRadius = 15f;
        public const float BiochemicalFrontWidth = 65f;
        public const float BiochemicalFrontLength = 40f;

        // Pungent Aerosol staging and 24-yalm knockback landing dimensions.
        public const float MovementHalfWidth = 18.5f;
        public const float PungentAerosolKnockbackDistance = 24f;
        public const float PungentAerosolCandidateStep = 1f;
        public const float PungentAerosolArrivalRadius = 0.75f;
    }

    private static class HeadsmenGeometry
    {
        // 59x40 arena, inset half a yalm from the wall.
        public const float ArenaSafeWidth = 58f;
        public const float ArenaSafeHeight = 39f;

        // Cell destinations remain half a yalm inside the eight-yalm floor boundary.
        public const float CellSafeRadius = 8f;
        public const float CellWallInset = 0.5f;
        public const float CellCandidateRadius = CellSafeRadius - CellWallInset;
        public const float CellCandidateRadiusSquared = CellCandidateRadius * CellCandidateRadius;

        public const float CellAvoidanceLeashRadius = 10f;
        public const float CellAssignmentRadius = 8.5f;
        public const float CellAssignmentRadiusSquared = CellAssignmentRadius * CellAssignmentRadius;

        // Four-yalm damage lane plus a half-yalm margin on each edge.
        public const float DismembermentWidth = 5f;
        public const float DismembermentLength = 16f;
        public const float DismembermentCellIntersectionMargin = 0.25f;
        public const float CellCandidateStep = 0.5f;
        public const float CellDestinationClearance = 0.5f;
        public const float CellArrivalRadius = 0.5f;
        public const float CellScoreTolerance = 0.05f;

        // Damage geometry plus the standard half-yalm safety margin.
        public const float FlayingFlailDamageRadius = 5f;
        public const float FlayingFlailAvoidanceMargin = 0.5f;
        public const float FlayingFlailAvoidRadius =
            FlayingFlailDamageRadius + FlayingFlailAvoidanceMargin;

        public const float ChoppingBlockDamageRadius = 6f;
        public const float ChoppingBlockAvoidanceMargin = 0.5f;
        public const float ChoppingBlockAvoidRadius =
            ChoppingBlockDamageRadius + ChoppingBlockAvoidanceMargin;

        // Execution Wheel requires an inward margin rather than an expanded radius.
        public const float ExecutionWheelDamageInnerRadius = 4f;
        public const float ExecutionWheelInnerSafetyMargin = 0.5f;
        public const float ExecutionWheelInnerRadius =
            ExecutionWheelDamageInnerRadius - ExecutionWheelInnerSafetyMargin;
        public const float ExecutionWheelOuterRadius = 9f;
        public const float PealOfJudgmentDamageWidth = 4f;
        public const float PealOfJudgmentLateralSafetyMargin = 0.5f;
        public const float PealOfJudgmentAvoidWidth =
            PealOfJudgmentDamageWidth + (PealOfJudgmentLateralSafetyMargin * 2f);
        public const float PealOfJudgmentDamageTravelLength = 7.5f;
        public const float PealOfJudgmentLongitudinalSafetyMargin = 0.5f;
        public const float PealOfJudgmentAvoidTravelLength =
            PealOfJudgmentDamageTravelLength + PealOfJudgmentLongitudinalSafetyMargin;

        // Eight yalms prevents the traveling sword from overtaking the player during overlaps.
        public static readonly Vector2[] PealOfJudgmentTravelRectangle =
        [
            new(PealOfJudgmentAvoidWidth / 2f, PealOfJudgmentAvoidTravelLength),
            new(-PealOfJudgmentAvoidWidth / 2f, PealOfJudgmentAvoidTravelLength),
            new(-PealOfJudgmentAvoidWidth / 2f, 0f),
            new(PealOfJudgmentAvoidWidth / 2f, 0f),
        ];
    }

    private static class ImmortalGeometry
    {
        // 40x40 platform with a one-yalm wall inset.
        public const float ArenaSafeSize = 38f;
        public const float ArenaLeashRadius = 20f;
        public const float BombardmentSmallRadius = 3f;
        public const float BombardmentLargeRadius = 14f;
        public const float TrustFollowRadius = 2f;
        public const float BombardmentGroupCenterOffset = 3.5f;
        public const float BombardmentGroupRadiusSquared = 16f;
        public const float BombardmentMergeRadiusSquared = 1f;
        public const int BombardmentLargeGroupSize = 5;
        public const int StandardBombardmentTetherCount = 8;
        public const int ImpressionBombardmentTetherCount = 6;
        // Twenty-yalm Keraunography lane plus one yalm per edge.
        public const float KeraunographyWidth = 22f;
        public const float KeraunographyLength = 60f;
        // Eight-yalm Electray lane plus a half-yalm margin per side.
        public const float ElectrayWidth = 9f;
        public const float ElectrayLength = 45f;

        // Half-room Turmoil rectangle with two yalms of interior clearance.
        public const float TurmoilHalfWidth = 24f;
        public const float TurmoilHalfLength = 40f;
        public const float TurmoilHalfOriginOffset = 10f;

        public const float TurmoilTrustSideMinimumOffset = 2.5f;
        public const int TurmoilTrustConsensusCount = 2;

        public const float MovementHalfWidth = 18.5f;
        public const float ImpressionVoidzoneRadius = 10f;
        public const float ImpressionStagingRadius = 12f;
        public const float ImpressionKnockbackDistance = 11f;
        public const float ImpressionArrivalRadius = 0.75f;
        public const float ImpressionBombardmentClearance = BombardmentLargeRadius + 0.75f;
        public const float ImpressionBombardmentClearanceSquared =
            ImpressionBombardmentClearance * ImpressionBombardmentClearance;

        public static readonly Vector2[] KeraunographyRectangle =
        [
            new(KeraunographyWidth / 2f, KeraunographyLength),
            new(-KeraunographyWidth / 2f, KeraunographyLength),
            new(-KeraunographyWidth / 2f, 0f),
            new(KeraunographyWidth / 2f, 0f),
        ];

        public static readonly Vector2[] ElectrayRectangle =
        [
            new(ElectrayWidth / 2f, ElectrayLength),
            new(-ElectrayWidth / 2f, ElectrayLength),
            new(-ElectrayWidth / 2f, 0f),
            new(ElectrayWidth / 2f, 0f),
        ];

        public static readonly Vector2[] TurmoilHalfRectangle =
        [
            new(TurmoilHalfWidth / 2f, TurmoilHalfLength),
            new(-TurmoilHalfWidth / 2f, TurmoilHalfLength),
            new(-TurmoilHalfWidth / 2f, 0f),
            new(TurmoilHalfWidth / 2f, 0f),
        ];

        public static readonly Vector3[] ImpressionDiagonalDirections =
        [
            new(0.70710677f, 0f, 0.70710677f),
            new(0.70710677f, 0f, -0.70710677f),
            new(-0.70710677f, 0f, 0.70710677f),
            new(-0.70710677f, 0f, -0.70710677f),
        ];
    }

    private static class EncounterTiming
    {
        public const int DirectedMovementLeaseMilliseconds = 750;
    }

    private static class ChirurgeonTiming
    {
        public static readonly TimeSpan PungentAerosolResolutionGrace = TimeSpan.FromMilliseconds(750);
    }

    private static class HeadsmenTiming
    {
        public const int ActionSettleMilliseconds = 500;

        // Includes the observed post-cast action-effect delay.
        public static readonly TimeSpan DismembermentResolutionGrace = TimeSpan.FromMilliseconds(1250);
        public static readonly TimeSpan DismembermentWaveGroupingTolerance = TimeSpan.FromMilliseconds(500);

        // Full cast windows allow the planner to choose compatible overlap destinations.
        public static readonly TimeSpan CircleAvoidanceLead = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan ExecutionWheelAvoidanceLead = TimeSpan.FromSeconds(4);

        // Fixed casts take ownership on their first observed tick instead of waiting on rolling Peal data.
        public static readonly TimeSpan FixedMechanicMovementHandoffLead = TimeSpan.FromSeconds(3.5);

        // Prevent late replanning from crossing a still-active current-stage hazard.
        public static readonly TimeSpan UpcomingStageRepositionLock = TimeSpan.FromSeconds(1.5);

        // Flail and Chopping effects outlive RB cast state by up to 0.69 seconds.
        public static readonly TimeSpan CircleResolutionGrace = TimeSpan.FromMilliseconds(750);

        // Moving Peal corridors refresh while their sword actors remain alive.
        public static readonly TimeSpan PealTravelForecastLead = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan PealForecastLease = TimeSpan.FromMilliseconds(500);
        public static readonly TimeSpan SequentialResolutionTolerance = TimeSpan.FromMilliseconds(250);
    }

    private static class ImmortalTiming
    {
        public static readonly TimeSpan ElectrayWaveGroupingTolerance = TimeSpan.FromMilliseconds(500);

        // RB observed Electray damage about one second after helper cast completion.
        public static readonly TimeSpan ElectrayActionEffectDelay = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan ElectrayResolutionGrace = TimeSpan.FromMilliseconds(250);

        public static readonly TimeSpan ImpressionResolutionGrace = TimeSpan.FromMilliseconds(750);

        // Icon fallback and observed cast-to-effect delay.
        public static readonly TimeSpan MemoryOfTheStormSignalWindow = TimeSpan.FromSeconds(7);
        public static readonly TimeSpan MemoryOfTheStormActionEffectDelay = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan MemoryOfTheStormResolutionGrace = TimeSpan.FromMilliseconds(500);

        public static readonly TimeSpan KeraunographyMaximumPostPrecastHold = TimeSpan.FromSeconds(6);
        public static readonly TimeSpan KeraunographyResolutionGrace = TimeSpan.FromMilliseconds(500);

        public static readonly TimeSpan BombardmentResolutionDelay = TimeSpan.FromSeconds(10.8);
        public static readonly TimeSpan BombardmentResolutionGrace = TimeSpan.FromSeconds(2);

        public static readonly TimeSpan BombardmentTrustFallbackWindow = TimeSpan.FromSeconds(45);

        public static readonly TimeSpan TurmoilPostElectraySignalWindow = TimeSpan.FromSeconds(12);
        public static readonly TimeSpan TurmoilDirectForecastLifetime = TimeSpan.FromSeconds(5.25);
        public static readonly TimeSpan TurmoilPostElectrayInferenceDelay = TimeSpan.FromMilliseconds(750);
        public static readonly TimeSpan TurmoilTrustInitialConsensusStability = TimeSpan.FromMilliseconds(750);
        public static readonly TimeSpan TurmoilTrustCorrectionConsensusStability = TimeSpan.FromMilliseconds(250);
        public static readonly TimeSpan TurmoilTrustForecastLifetime = TimeSpan.FromSeconds(7);
        public static readonly TimeSpan TurmoilResolutionGrace = TimeSpan.FromMilliseconds(750);
    }

    private static class ArenaCenter
    {
        // Observed platform centers used by the arena boundaries and directional mechanics.
        public static readonly Vector3 ChirurgeonGeneral = new(270f, -582.5f, 12f);
        public static readonly Vector3 HoodedHeadsmen = new(60f, -490f, -258f);
        // Cell Block centers in A/B/C/D order.
        public static readonly Vector3 CellBlockA = new(60f, -490f, -268f);
        public static readonly Vector3 CellBlockB = new(60f, -490f, -248f);
        public static readonly Vector3 CellBlockC = new(42.5f, -490f, -258f);
        public static readonly Vector3 CellBlockD = new(77.5f, -490f, -258f);
        public static readonly Vector3 ImmortalRemains = new(0f, 320f, 0f);
    }

    private static class EnemyObjectId
    {
        // BNpcBase IDs consumed through BaseId.
        public const uint ChirurgeonGeneral = 0x488F;
        public const uint BloodyHeadsman = 0x4890;
        public const uint PaleHeadsman = 0x4891;
        public const uint RavenousHeadsman = 0x4892;
        public const uint PestilentHeadsman = 0x4893;
        public const uint SwordOfJustice = 0x4894;
        public const uint ImmortalRemains = 0x48BE;
        public const uint PreservedTerror = 0x48C0;
        public const uint Helper = 0x233C;

        public static readonly HashSet<uint> Headsmen =
        [
            BloodyHeadsman,
            PaleHeadsman,
            RavenousHeadsman,
            PestilentHeadsman,
        ];
    }

    private static class EnemyAction
    {
        // Chirurgeon General actions.
        public const uint SterileSphereSmall = 43806;
        public const uint SterileSphereLarge = 43805;
        public const uint BiochemicalFront = 43802;
        public const uint ConcentratedDose = 43799;
        public const uint PungentAerosol = 43807;

        // Hooded Headsmen actions.
        public const uint Dismemberment = 43587;
        public const uint FlayingFlail = 43592;
        public const uint ChoppingBlock = 43595;
        public const uint ExecutionWheel = 43596;
        public const uint RelentlessTorment = 43589;
        public const uint WillBreaker = 44856;

        // Immortal Remains actions.
        public const uint Memento = 43809;
        public const uint Electray = 43810;
        public const uint BombardmentSmall = 43811;
        public const uint BombardmentLarge = 43812;
        public const uint ImpressionAoe = 43818;
        public const uint KeraunographyPre = 43813;
        public const uint Keraunography = 45176;
        // Turmoil event actions and helper resolution.
        public const uint TurmoilRightHand = 43814;
        public const uint TurmoilLeftHand = 43815;
        public const uint TurmoilHit = 43816;
        public const uint MemoryOfTheStormCast = 43821;
        public const uint MemoryOfThePyreCast = 43823;
    }

    private static class EnemyAura
    {
        // Shared activation status with encounter-specific aliases.
        public const uint Activate = 2552;
        public const uint PreservedTerror = Activate;
    }

    private static class EnemyTether
    {
        // Preserved Terror selection tether.
        public const uint PreservedTerrorSelection = 340;
    }

    private static class PlayerAura
    {
        public const uint CellBlockA = 4542;
        public const uint CellBlockB = 4543;
        public const uint CellBlockC = 4544;
        public const uint CellBlockD = 4545;
        public const uint Doom = 5185;
    }

    private static class PlayerAction
    {
        // Player role actions issued by DutyMechanic.
        public const uint Interject = 7538;
        public const uint HeadGraze = 7551;
        public const uint Esuna = 7568;

        public static readonly uint[] Interrupts = [Interject, HeadGraze];
    }

    private static class PlayerVfx
    {
        // Memory of the Storm line-stack icon.
        public const uint MemoryOfTheStormLineStack = 525;
    }

    private sealed class DirectedMovementState
    {
        public CapabilityManagerHandle Handle { get; } = CapabilityManager.CreateNewHandle();
        public Vector3 Destination { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public bool HasDestination { get; set; }
        public bool Owned { get; set; }

        public void Clear()
        {
            Destination = default;
            ExpiresAtUtc = DateTime.MinValue;
            HasDestination = false;
            Owned = false;
        }
    }

    private sealed class ChirurgeonGeneralState
    {
        public DirectedMovementState PungentAerosol { get; } = new();
    }

    private sealed class PealOfJudgmentState
    {
        public HashSet<uint> ActiveSwordIds { get; } = [];

        public void Clear() => ActiveSwordIds.Clear();
    }

    private sealed class HeadsmenLaneForecastState
    {
        public Dictionary<uint, DismembermentLane> LanesByCaster { get; } = [];

        public void Clear() => LanesByCaster.Clear();
    }

    private sealed class HeadsmenCircleForecastState
    {
        public Dictionary<uint, HeadsmenCircleForecast> ForecastsByCaster { get; } = [];

        public void Clear() => ForecastsByCaster.Clear();
    }

    private sealed class HoodedHeadsmenState
    {
        public DirectedMovementState Movement { get; } = new();
        public PealOfJudgmentState PealOfJudgment { get; } = new();
        public HeadsmenLaneForecastState Dismemberment { get; } = new();
        public HeadsmenCircleForecastState FlayingFlail { get; } = new();
        public HeadsmenCircleForecastState ChoppingBlock { get; } = new();
        public HeadsmenCircleForecastState ExecutionWheel { get; } = new();
        public HeadsmenPlanningStage? PlanningStage { get; set; }
        public bool OwnsEncounter { get; set; }

        public void Clear()
        {
            Movement.Clear();
            PealOfJudgment.Clear();
            Dismemberment.Clear();
            FlayingFlail.Clear();
            ChoppingBlock.Clear();
            ExecutionWheel.Clear();
            PlanningStage = null;
            OwnsEncounter = false;
        }
    }

    private sealed record HeadsmenCircleForecast(
        Vector3 Location,
        DateTime ResolvesAtUtc,
        DateTime ExpiresAtUtc);

    private sealed record HeadsmenCircleHazard(
        Vector3 Location,
        float Radius,
        DateTime ResolvesAtUtc,
        DateTime ExpiresAtUtc);

    private sealed record HeadsmenDonutHazard(
        Vector3 Location,
        float InnerRadius,
        DateTime ResolvesAtUtc,
        DateTime ExpiresAtUtc);

    private sealed record PealOfJudgmentLane(
        Vector3 Location,
        float Heading,
        DateTime ResolvesAtUtc,
        DateTime ExpiresAtUtc);

    private enum HeadsmenPlanningStage
    {
        PealOfJudgment,
        Circle,
        Dismemberment,
        ExecutionWheel,
    }

    private sealed record HeadsmenPlanningContext(
        Vector3 CellCenter,
        HeadsmenPlanningStage Stage,
        DismembermentLane[] DismembermentLanes,
        HeadsmenCircleHazard[] CircleHazards,
        HeadsmenDonutHazard[] ExecutionWheels,
        PealOfJudgmentLane[] PealOfJudgmentLanes,
        HeadsmenCircleHazard[] PrimaryCircles,
        HeadsmenDonutHazard[] PrimaryExecutionWheels,
        DateTime StageResolvesAtUtc,
        DateTime ActiveUntilUtc)
    {
        public string HoldReason => Stage switch
        {
            HeadsmenPlanningStage.PealOfJudgment => "Holding a Peal of Judgment safe spot",
            HeadsmenPlanningStage.Circle => "Holding a Serial Torture safe spot",
            HeadsmenPlanningStage.Dismemberment => "Holding a Dismemberment safe spot",
            HeadsmenPlanningStage.ExecutionWheel => "Holding inside Execution Wheel",
            _ => "Holding a Headsmen safe spot",
        };
    }

    private sealed record DismembermentLane(
        Vector3 Location,
        float Heading,
        DateTime ResolvesAtUtc,
        DateTime ExpiresAtUtc);

    private sealed record ElectrayLane(
        Vector3 Location,
        float Heading,
        DateTime ResolvesAtUtc,
        DateTime ExpiresAtUtc);

    private sealed record KeraunographyLane(Vector3 Location, float Heading, DateTime ExpiresAtUtc);

    private sealed record PreservedTerrorSnapshot(Vector3 Location, float Heading);

    private enum BombardmentShape
    {
        Small,
        Large,
    }

    private enum ArenaHalf
    {
        Left,
        Right,
    }

    private sealed record BombardmentForecast(
        BombardmentShape Shape,
        Vector3 Location,
        DateTime ExpiresAtUtc);

    private sealed record TurmoilForecast(
        Vector3 Origin,
        DateTime ExpiresAtUtc);

    private sealed class ImmortalRemainsState
    {
        public DirectedMovementState Impression { get; } = new();
        public MemoryOfTheStormState MemoryOfTheStorm { get; } = new();
        public ElectrayState Electray { get; } = new();
        public KeraunographyState Keraunography { get; } = new();
        public BombardmentState Bombardment { get; } = new();
        public TurmoilState Turmoil { get; } = new();

        public void ClearForecasts()
        {
            Electray.Clear();
            Keraunography.Clear();
            Bombardment.Clear();
            Turmoil.Clear();
        }

        public void Clear()
        {
            Impression.Clear();
            MemoryOfTheStorm.Clear();
            ClearForecasts();
        }
    }

    private sealed class ElectrayState
    {
        public Dictionary<uint, ElectrayLane> LanesByCaster { get; } = [];

        public void Clear() => LanesByCaster.Clear();
    }

    private sealed class MemoryOfTheStormState
    {
        public TrustMovementState StackMovement { get; } = new();
        public bool SignalActive { get; set; }
        public bool CastActive { get; set; }

        public void ResetSignals()
        {
            SignalActive = false;
            CastActive = false;
        }

        public void Clear()
        {
            StackMovement.Clear();
            ResetSignals();
        }
    }

    private sealed class KeraunographyState
    {
        public List<KeraunographyLane> Lanes { get; } = [];
        public HashSet<uint> PreCasters { get; } = [];
        public DateTime ClearAtUtc { get; set; }

        public void Clear()
        {
            Lanes.Clear();
            PreCasters.Clear();
            ClearAtUtc = DateTime.MinValue;
        }
    }

    private sealed class BombardmentState
    {
        public TrustMovementState TrustFallback { get; } = new();
        public Dictionary<uint, PreservedTerrorSnapshot> TetheredShades { get; } = [];
        public List<BombardmentForecast> Forecasts { get; } = [];
        public bool MementoCastActive { get; set; }
        public bool TetherBatchActive { get; set; }

        public void ClearTethers()
        {
            TetheredShades.Clear();
            TetherBatchActive = false;
        }

        public void Clear()
        {
            ClearTethers();
            Forecasts.Clear();
            TrustFallback.Clear();
            MementoCastActive = false;
        }
    }

    private sealed class TurmoilState
    {
        public Dictionary<uint, uint> LastObservedActionByActor { get; } = [];
        public TurmoilForecast Forecast { get; set; }
        public DateTime SignalWatchUntilUtc { get; set; }
        public DateTime TrustInferenceNotBeforeUtc { get; set; }
        public DateTime TrustCandidateSinceUtc { get; set; }
        public ArenaHalf? TrustSafeHalfCandidate { get; set; }
        public ArenaHalf? SelectedTrustSafeHalf { get; set; }
        public bool ElectrayCastActive { get; set; }
        public bool DirectForecastSelected { get; set; }

        public bool IsSignalWatchActive(DateTime now) => SignalWatchUntilUtc > now;

        public void BeginSequence(DateTime now)
        {
            Forecast = null;
            SignalWatchUntilUtc = now + ImmortalTiming.TurmoilPostElectraySignalWindow;
            TrustInferenceNotBeforeUtc = DateTime.MaxValue;
            ClearTrustCandidate();
            SelectedTrustSafeHalf = null;
            DirectForecastSelected = false;
        }

        public void BeginPostElectrayInference(DateTime now)
        {
            TrustInferenceNotBeforeUtc = now + ImmortalTiming.TurmoilPostElectrayInferenceDelay;
            ClearTrustCandidate();
        }

        public void BeginTrustCandidate(ArenaHalf safeHalf, DateTime now)
        {
            TrustSafeHalfCandidate = safeHalf;
            TrustCandidateSinceUtc = now;
        }

        public void ClearTrustCandidate()
        {
            TrustSafeHalfCandidate = null;
            TrustCandidateSinceUtc = DateTime.MinValue;
        }

        public void TrimForecastAfterHit(DateTime clearAtUtc)
        {
            if (Forecast is not null && Forecast.ExpiresAtUtc > clearAtUtc)
            {
                Forecast = Forecast with { ExpiresAtUtc = clearAtUtc };
            }
        }

        public void RemoveMissingActors(IEnumerable<uint> currentObjectIds)
        {
            HashSet<uint> current = currentObjectIds.ToHashSet();
            foreach (uint objectId in LastObservedActionByActor.Keys.Where(id => !current.Contains(id)).ToArray())
            {
                LastObservedActionByActor.Remove(objectId);
            }
        }

        public void Clear()
        {
            LastObservedActionByActor.Clear();
            Forecast = null;
            SignalWatchUntilUtc = DateTime.MinValue;
            TrustInferenceNotBeforeUtc = DateTime.MinValue;
            ClearTrustCandidate();
            SelectedTrustSafeHalf = null;
            ElectrayCastActive = false;
            DirectForecastSelected = false;
        }
    }

    private sealed class TrustMovementState
    {
        public CapabilityManagerHandle Handle { get; } = CapabilityManager.CreateNewHandle();
        public DateTime UntilUtc { get; private set; }
        public uint AnchorObjectId { get; set; }
        public bool Owned { get; set; }

        public bool IsActive(DateTime now) => UntilUtc > now;

        public void Begin(DateTime expiresAtUtc)
        {
            UntilUtc = expiresAtUtc;
            AnchorObjectId = 0;
        }

        // Preserve the selected anchor when replacing a fallback lifetime with the cast deadline.
        public void SetExpiry(DateTime expiresAtUtc) => UntilUtc = expiresAtUtc;

        public void Clear()
        {
            UntilUtc = DateTime.MinValue;
            AnchorObjectId = 0;
            Owned = false;
        }
    }

}
