using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Hell on Rails normal-trial logic for Doomtrain.
/// </summary>
/// <remarks>
/// The encounter changes between five rectangular train cars and a circular add arena. This
/// handler owns the height-sensitive signals, turrets, floor-wide attacks, tower soak, and car-four
/// escape so generic cast geometry cannot mistake a safe elevation for an unsafe floor. The
/// rotating Ghost Train tankbuster remains deliberately unmodeled: predicting its final cone
/// requires target-icon and status-extra evidence that has not yet been captured through
/// RebornBuddy. The initial 2026-08-25 implementation is therefore reference-derived and requires
/// a live normal-mode run before release confidence can be raised beyond strong.
/// </remarks>
public class HellOnRails : AbstractDungeon
{
    // Each positive-position mechanic owns an independent lease. Their casts are normally
    // sequential, but separate handles prevent a stale elevation or tower lifecycle from releasing
    // the movement suppression needed for the car-four escape.
    private readonly CapabilityManagerHandle elevationMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle lightningExpressMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle towerMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle derailMovementHandle = CapabilityManager.CreateNewHandle();

    private bool elevationMovementActive;
    private bool lightningExpressMovementActive;
    private bool towerMovementActive;
    private bool derailMovementActive;
    private bool movingForElevation;
    private bool movingForLightningExpress;
    private bool movingForTower;
    private bool movingForDerail;
    private bool lightningExpressMissingSignalsLogged;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.HellOnRails;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } =
    [
        // The five-second boss visual is the actionable window; the helper's targeted hit begins
        // only half a second before resolution.
        EnemyAction.LightningBurstVisual,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        EnemyAction.LightningExpress,
        EnemyAction.UnlimitedExpressVisual,
        EnemyAction.RunawayTrain,
        EnemyAction.DerailmentSiegeVisual,
        EnemyAction.BatteringArmsVisual,
    ];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Levin Signals appear about seven seconds before their one-second plasma casts. The
        // Lightning Express planner below consumes their lane and elevation as one semantic
        // mechanic, including the knockback landing. Publishing standalone rectangles would cover
        // car three's entire lower deck before RebornBuddy can traverse a transport panel.

        // Electray is authored as a five-yalm-wide line. Upper turrets only cover the platform
        // section in front of them, while the four lower variants retain their observed lengths.
        AddElectrayAvoid(EnemyAction.ElectrayShort, 10.0f);
        AddElectrayAvoid(EnemyAction.ElectrayMedium, 15.0f);
        AddElectrayAvoid(EnemyAction.ElectrayLong, 25.0f);
        AddElectrayAvoid(EnemyAction.ElectrayLonger, 20.0f);
        AddElectrayAvoid(EnemyAction.ElectrayUpper, 10.0f);

        // Blastpipe covers the front ten yalms across the full twenty-yalm car width after Windpipe
        // draws players forward. A half-yalm border covers cast and navigation latency.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInHellOnRailsCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.Blastpipe,
            width: 21.0f,
            length: 11.0f,
            yOffset: -0.5f,
            priority: AvoidancePriority.High);

        // Lightning Burst targets both tanks with five-yalm circles. The helper cast is only half a
        // second long, so this is a last-line geometric safeguard for non-targets; targeted tanks
        // remain in place and use the five-second visual mitigation window configured above.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInHellOnRailsCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.LightningBurst
                && caster.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: caster => 5.5f,
            locationProducer: caster => GameObjectManager.GetObjectByObjectId(caster.SpellCastInfo.TargetId)?.Location
                ?? caster.SpellCastInfo.CastLocation,
            priority: AvoidancePriority.High));

        // Four center helpers cast Aether Surge during the add phase. Expanding the 45-degree,
        // fifteen-yalm cone by two degrees per edge gives approximately a 0.5-yalm lateral margin
        // at the arena wall without consuming the adjacent safe wedges.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInHellOnRailsCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.AetherSurge,
            leashPointProducer: () => ArenaCenter.Intermission,
            leashRadius: IntermissionLeashRadius,
            rotationDegrees: 0.0f,
            radius: 15.5f,
            arcDegrees: 49.0f);

        // The moving red indicator forecasts each sixteen-yalm Hail of Thunder explosion several
        // seconds before the helper exposes its short cast. The cast-location avoid remains as a
        // fallback if the indicator actor is briefly absent during a transition.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInHellOnRailsCombat,
            objectSelector: actor => actor.NpcId == EnemyNpc.ArcaneRevelation,
            radiusProducer: actor => ArcaneRevelationAvoidRadius,
            // The indicator floats above the floor; flattening only its height preserves the live
            // X/Z forecast while keeping RebornBuddy's avoid on the player's navigable surface.
            locationProducer: actor => new Vector3(actor.X, Core.Player.Y, actor.Z),
            priority: AvoidancePriority.High));

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInHellOnRailsCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.HailOfThunder,
            radiusProducer: caster => ArcaneRevelationAvoidRadius,
            locationProducer: caster => caster.SpellCastInfo.CastLocation,
            priority: AvoidancePriority.High));

        // Every train car is twenty by thirty yalms. The half-yalm inset prevents fall-edge path
        // candidates while preserving the corner pockets needed for Arcane Revelation. The center
        // follows the player's current car and is absent in the separate intermission arena.
        AvoidanceHelpers.AddAvoidSquareDonut(
            canRun: () => IsInHellOnRailsCombat() && TryGetCurrentCar(out _),
            innerWidth: TrainCarSafeWidth,
            innerHeight: TrainCarSafeLength,
            outerWidth: 100.0f,
            outerHeight: 100.0f,
            collectionProducer: GetCurrentCarCenterCollection,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseAllMovement("Leaving Hell on Rails");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Whole-floor lower/upper casts and full-car knockbacks are valid combat actions but not
        // ordinary unsafe rectangles. SideStep cannot express elevation and would otherwise cover
        // every navigable point or duplicate the encounter-owned signal and turret lines.
        SidestepPlugin.Enabled = false;

        if (!IsInHellOnRailsCombat())
        {
            ReleaseAllMovement("Hell on Rails combat ended");
            return false;
        }

        await TankBusterSpells();
        await DamageMitigationSpells();

        // Resolution priority follows effect timing: escape a destroyed car first, stage the
        // signal-plus-knockback combination second, change elevation for a floor-wide hit third,
        // then hold the shared tower. Ordinary geometric avoidance retains emergency priority
        // inside each positive-position handler.
        if (await HandleDerailAsync())
        {
            return true;
        }

        if (await HandleLightningExpressAsync())
        {
            return true;
        }

        if (await HandleHeadOnEmissionAsync())
        {
            return true;
        }

        return await HandleDerailmentSiegeAsync();
    }

    /// <summary>
    /// Chooses a signal-safe lane and stages far enough forward for Lightning Express's
    /// sixteen-yalm knockback; cars with platforms use their transport-panel endpoints explicitly.
    /// </summary>
    /// <returns><see langword="true"/> while actively traveling to the planned lane or platform.</returns>
    private async Task<bool> HandleLightningExpressAsync()
    {
        BattleCharacter caster = FindCaster(EnemyAction.LightningExpress);
        if (!TryGetCurrentCar(out int car))
        {
            lightningExpressMissingSignalsLogged = false;
            ReleaseLightningExpressMovement("Lightning Express cast or train-car lifecycle ended");
            return false;
        }

        BattleCharacter[] signals = GetLevinSignals(car);
        if (caster == null && signals.Length == 0)
        {
            lightningExpressMissingSignalsLogged = false;
            ReleaseLightningExpressMovement("Lightning Express signal and cast lifecycle ended");
            return false;
        }

        lightningExpressMovementActive = true;
        CapabilityManager.Update(
            lightningExpressMovementHandle,
            CapabilityFlags.Movement,
            caster?.SpellCastInfo.RemainingCastTime ?? TimeSpan.FromSeconds(8.0),
            "Holding a signal-safe Lightning Express knockback lane");

        if (!TryBuildLightningExpressPlan(car, signals, out LightningExpressPlan plan))
        {
            StopOwnedMovement(ref movingForLightningExpress);
            if (caster != null && !lightningExpressMissingSignalsLogged)
            {
                Logger.Warning("Hell on Rails: Lightning Express is casting on car {0}, but no corroborated Levin Signal safe lane is available; capture signal actor positions and elevations.", car);
                lightningExpressMissingSignalsLogged = true;
            }

            return false;
        }

        lightningExpressMissingSignalsLogged = false;

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        bool playerIsUpperDeck = IsUpperDeck(Core.Player);
        Vector3 destination;

        if (plan.UpperDeck && !playerIsUpperDeck)
        {
            destination = WithPlayerHeight(plan.GroundPanel);
        }
        else if (!plan.UpperDeck && playerIsUpperDeck)
        {
            destination = GetNearestUpperPanel(car);
        }
        else if (plan.UpperDeck && IsOnOppositePlatform(plan.Staging))
        {
            // Platforms are disconnected. Return to ground through the current endpoint before
            // approaching the chosen platform's entrance on the next bot tick.
            destination = GetNearestUpperPanel(car);
        }
        else
        {
            destination = WithPlayerHeight(plan.Staging);
            if (Core.Player.Distance2D(destination) <= LightningExpressArrivalTolerance)
            {
                StopOwnedMovement(ref movingForLightningExpress);
                return false;
            }
        }

        movingForLightningExpress = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Moves through the south transport panel before car four is destroyed.
    /// </summary>
    /// <returns><see langword="true"/> while actively traveling to the panel.</returns>
    private async Task<bool> HandleDerailAsync()
    {
        BattleCharacter caster = FindCaster(EnemyAction.Derail, EnemyAction.DerailVisual);
        if (caster == null || !TryGetCurrentCar(out int car) || car != 4)
        {
            ReleaseDerailMovement("Derail cast or car-four lifecycle ended");
            return false;
        }

        derailMovementActive = true;
        CapabilityManager.Update(
            derailMovementHandle,
            CapabilityFlags.Movement,
            caster.SpellCastInfo.RemainingCastTime,
            "Moving through the car-four escape panel before Derail");

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        Vector3 destination = WithPlayerHeight(ArenaCenter.CarFourEscapePanel);
        movingForDerail = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Uses the nearest transport panel when Head-on Emission will strike the player's elevation.
    /// </summary>
    /// <returns><see langword="true"/> while actively traveling between elevations.</returns>
    private async Task<bool> HandleHeadOnEmissionAsync()
    {
        BattleCharacter lowerDeckCaster = FindCaster(EnemyAction.ThunderousBreathLowerDeck);
        BattleCharacter upperDeckCaster = FindCaster(EnemyAction.HeadlightUpperDeck);
        BattleCharacter caster = lowerDeckCaster ?? upperDeckCaster;

        if (caster == null || !TryGetCurrentCar(out int car) || car is not (3 or 5))
        {
            ReleaseElevationMovement("Head-on Emission cast or supported car lifecycle ended");
            return false;
        }

        bool lowerDeckIsUnsafe = lowerDeckCaster != null;
        bool playerIsUpperDeck = IsUpperDeck(Core.Player);
        bool playerIsAlreadySafe = lowerDeckIsUnsafe ? playerIsUpperDeck : !playerIsUpperDeck;

        elevationMovementActive = true;
        CapabilityManager.Update(
            elevationMovementHandle,
            CapabilityFlags.Movement,
            caster.SpellCastInfo.RemainingCastTime,
            lowerDeckIsUnsafe
                ? "Holding the upper deck for Thunderous Breath"
                : "Holding the lower deck for Headlight");

        if (playerIsAlreadySafe)
        {
            StopOwnedMovement(ref movingForElevation);
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        Vector3 destination = GetNearestElevationPanel(car, playerIsUpperDeck);
        movingForElevation = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Moves into and holds the multi-hit Derailment Siege or Battering Arms tower.
    /// </summary>
    /// <returns><see langword="true"/> while actively traveling into the tower.</returns>
    private async Task<bool> HandleDerailmentSiegeAsync()
    {
        BattleCharacter tower = FindCaster(EnemyAction.DerailmentSiegeTower);
        if (tower == null)
        {
            ReleaseTowerMovement("Shared tower cast ended");
            return false;
        }

        towerMovementActive = true;
        CapabilityManager.Update(
            towerMovementHandle,
            CapabilityFlags.Movement,
            tower.SpellCastInfo.RemainingCastTime,
            "Holding the shared Doomtrain tower");

        if (Core.Player.Distance2D(tower) <= TowerHoldRadius)
        {
            StopOwnedMovement(ref movingForTower);
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        movingForTower = true;
        Navigator.PlayerMover.MoveTowards(tower.Location);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Registers one same-elevation Electray cast rectangle.
    /// </summary>
    /// <param name="actionId">Turret action identifying the authored line length.</param>
    /// <param name="authoredLength">Observed forward line length in yalms.</param>
    private static void AddElectrayAvoid(uint actionId, float authoredLength)
    {
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInHellOnRailsCombat,
            objectSelector: turret => turret.CastingSpellId == actionId && IsSameElevation(turret, Core.Player),
            width: ElectrayWidthWithMargin,
            length: authoredLength + 1.0f,
            yOffset: -0.5f,
            priority: AvoidancePriority.High);
    }

    /// <summary>
    /// Finds the first live caster of any supplied action.
    /// </summary>
    /// <param name="actionIds">Action IDs whose cast state owns the mechanic lifecycle.</param>
    /// <returns>The matching caster, or <see langword="null"/> when none is casting.</returns>
    private static BattleCharacter FindCaster(params uint[] actionIds)
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsCasting && actionIds.Contains(actor.CastingSpellId));
    }

    /// <summary>
    /// Builds one elevation-aware Lightning Express destination from live Levin Signal actors.
    /// </summary>
    /// <param name="car">Current train car.</param>
    /// <param name="signals">Live signals already filtered to the current car.</param>
    /// <param name="plan">Safe elevation, staging point, and transport-panel endpoints.</param>
    /// <returns><see langword="true"/> only when the observed signal set proves the destination safe.</returns>
    private static bool TryBuildLightningExpressPlan(int car, BattleCharacter[] signals, out LightningExpressPlan plan)
    {
        plan = default;
        if (signals.Length == 0)
        {
            return false;
        }

        if (car is 1 or 2)
        {
            float[] safeGroundLanes = SignalLaneCenters
                .Where(lane => !signals.Any(signal => !IsUpperDeck(signal) && Math.Abs(signal.X - lane) <= SignalLaneTolerance))
                .ToArray();
            if (safeGroundLanes.Length == 0)
            {
                return false;
            }

            float lane = safeGroundLanes.OrderBy(candidate => Math.Abs(Core.Player.X - candidate)).First();
            float stagingZ = car * 50.0f + 40.0f;
            plan = new LightningExpressPlan(false, new(lane, 0.0f, stagingZ), default, default);
            return true;
        }

        if (car == 3)
        {
            List<LightningExpressPlan> candidates = [];
            AddUpperPlatformPlanIfSafe(
                candidates,
                signals,
                platformX: 92.4f,
                stagingZ: 197.0f,
                groundPanel: ArenaCenter.CarThreeGroundPanels[0],
                upperPanel: ArenaCenter.CarThreeUpperPanels[0]);
            AddUpperPlatformPlanIfSafe(
                candidates,
                signals,
                platformX: 107.6f,
                stagingZ: 197.0f,
                groundPanel: ArenaCenter.CarThreeGroundPanels[1],
                upperPanel: ArenaCenter.CarThreeUpperPanels[1]);

            return TrySelectClosestPlan(candidates, out plan);
        }

        if (car == 5)
        {
            // The northern/eastern platform begins near Z=295. Starting at Z=297 leaves room for
            // the observed sixteen-yalm southward knockback to land on the still-intact car. The
            // southern/western platform begins too far back to provide the same landing margin.
            List<LightningExpressPlan> candidates = [];
            AddUpperPlatformPlanIfSafe(
                candidates,
                signals,
                platformX: 107.6f,
                stagingZ: 297.0f,
                groundPanel: ArenaCenter.CarFiveGroundPanels[1],
                upperPanel: ArenaCenter.CarFiveUpperPanels[1]);

            return TrySelectClosestPlan(candidates, out plan);
        }

        return false;
    }

    /// <summary>
    /// Returns the live signal actors associated with one train car.
    /// </summary>
    private static BattleCharacter[] GetLevinSignals(int car) =>
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.LevinSignal)
            .Where(signal => IsSignalOnCar(signal, car))
            .ToArray();

    /// <summary>
    /// Adds a platform plan only when no upper Levin Signal occupies that lane.
    /// </summary>
    private static void AddUpperPlatformPlanIfSafe(
        List<LightningExpressPlan> candidates,
        BattleCharacter[] signals,
        float platformX,
        float stagingZ,
        Vector3 groundPanel,
        Vector3 upperPanel)
    {
        if (!signals.Any(signal => IsUpperDeck(signal) && Math.Abs(signal.X - platformX) <= SignalLaneTolerance))
        {
            candidates.Add(new(true, new(platformX, 0.0f, stagingZ), groundPanel, upperPanel));
        }
    }

    /// <summary>
    /// Selects the platform with the shortest current horizontal approach.
    /// </summary>
    private static bool TrySelectClosestPlan(List<LightningExpressPlan> candidates, out LightningExpressPlan plan)
    {
        if (candidates.Count == 0)
        {
            plan = default;
            return false;
        }

        plan = candidates
            .OrderBy(candidate => Core.Player.Distance2D(IsUpperDeck(Core.Player) ? candidate.UpperPanel : candidate.GroundPanel))
            .First();
        return true;
    }

    /// <summary>
    /// Resolves the player's current train car from the stable fifty-yalm center spacing.
    /// </summary>
    /// <param name="car">Resolved car number from one through five.</param>
    /// <returns><see langword="true"/> when the player is within a train-car footprint.</returns>
    private static bool TryGetCurrentCar(out int car)
    {
        car = 0;
        if (Core.Player == null)
        {
            return false;
        }

        float z = Core.Player.Z;
        for (int candidate = 1; candidate <= 5; candidate++)
        {
            float centerZ = candidate * 50.0f + 50.0f;
            if (Math.Abs(z - centerZ) <= TrainCarDetectionHalfLength)
            {
                car = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Produces the current car center for the dynamic rectangular arena boundary.
    /// </summary>
    /// <returns>A one-element collection on a train car; otherwise an empty collection.</returns>
    private static Vector3[] GetCurrentCarCenterCollection()
    {
        if (!TryGetCurrentCar(out int car))
        {
            return [];
        }

        return [new Vector3(100.0f, Core.Player.Y, car * 50.0f + 50.0f)];
    }

    /// <summary>
    /// Chooses the closest panel endpoint for the current car and elevation.
    /// </summary>
    /// <param name="car">Current car, either three or five.</param>
    /// <param name="fromUpperDeck">Whether to use the upper endpoints to return to ground.</param>
    /// <returns>The closest panel endpoint with the player's current height preserved.</returns>
    private static Vector3 GetNearestElevationPanel(int car, bool fromUpperDeck)
    {
        Vector3[] candidates = car switch
        {
            3 when fromUpperDeck => ArenaCenter.CarThreeUpperPanels,
            3 => ArenaCenter.CarThreeGroundPanels,
            5 when fromUpperDeck => ArenaCenter.CarFiveUpperPanels,
            _ => ArenaCenter.CarFiveGroundPanels,
        };

        Vector3 nearest = candidates.OrderBy(candidate => Core.Player.Distance2D(candidate)).First();
        return WithPlayerHeight(nearest);
    }

    /// <summary>
    /// Returns the closest upper transport endpoint on a car with raised platforms.
    /// </summary>
    private static Vector3 GetNearestUpperPanel(int car)
    {
        Vector3[] candidates = car == 3
            ? ArenaCenter.CarThreeUpperPanels
            : ArenaCenter.CarFiveUpperPanels;
        Vector3 nearest = candidates.OrderBy(candidate => Core.Player.Distance2D(candidate)).First();
        return WithPlayerHeight(nearest);
    }

    /// <summary>
    /// Determines whether the player is on the platform opposite the selected staging lane.
    /// </summary>
    private static bool IsOnOppositePlatform(Vector3 staging) =>
        IsUpperDeck(Core.Player)
        && ((Core.Player.X < 100.0f && staging.X > 100.0f)
            || (Core.Player.X > 100.0f && staging.X < 100.0f));

    /// <summary>
    /// Preserves the live elevation because transport-panel activation depends on horizontal
    /// contact and the movement target should not ask the navigation provider for a vertical path.
    /// </summary>
    /// <param name="location">Authored horizontal panel location.</param>
    /// <returns>The same X/Z location at the player's current Y.</returns>
    private static Vector3 WithPlayerHeight(Vector3 location) =>
        new(location.X, Core.Player.Y, location.Z);

    /// <summary>
    /// Stops movement only when this handler previously issued it and emergency avoidance is idle.
    /// </summary>
    /// <param name="ownedMovement">Movement ownership flag to clear.</param>
    private static void StopOwnedMovement(ref bool ownedMovement)
    {
        if (ownedMovement && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        ownedMovement = false;
    }

    private void ReleaseAllMovement(string reason)
    {
        ReleaseElevationMovement(reason);
        ReleaseLightningExpressMovement(reason);
        ReleaseTowerMovement(reason);
        ReleaseDerailMovement(reason);
    }

    private void ReleaseLightningExpressMovement(string reason)
    {
        if (lightningExpressMovementActive)
        {
            CapabilityManager.Clear(lightningExpressMovementHandle, CapabilityFlags.Movement, reason);
            lightningExpressMovementActive = false;
        }

        StopOwnedMovement(ref movingForLightningExpress);
    }

    private void ReleaseElevationMovement(string reason)
    {
        if (elevationMovementActive)
        {
            CapabilityManager.Clear(elevationMovementHandle, CapabilityFlags.Movement, reason);
            elevationMovementActive = false;
        }

        StopOwnedMovement(ref movingForElevation);
    }

    private void ReleaseTowerMovement(string reason)
    {
        if (towerMovementActive)
        {
            CapabilityManager.Clear(towerMovementHandle, CapabilityFlags.Movement, reason);
            towerMovementActive = false;
        }

        StopOwnedMovement(ref movingForTower);
    }

    private void ReleaseDerailMovement(string reason)
    {
        if (derailMovementActive)
        {
            CapabilityManager.Clear(derailMovementHandle, CapabilityFlags.Movement, reason);
            derailMovementActive = false;
        }

        StopOwnedMovement(ref movingForDerail);
    }

    private static bool IsInHellOnRailsCombat() =>
        Core.Player != null
        && Core.Player.InCombat
        && WorldManager.ZoneId == (uint)Data.ZoneId.HellOnRails;

    private static bool IsUpperDeck(BattleCharacter actor) => actor != null && actor.Y >= UpperDeckHeightThreshold;

    private static bool IsSameElevation(BattleCharacter first, BattleCharacter second) =>
        first != null && second != null && IsUpperDeck(first) == IsUpperDeck(second);

    private static bool IsSignalOnCar(BattleCharacter signal, int car) =>
        Math.Abs(signal.Z - (car * 50.0f + 50.0f)) <= SignalCarDetectionDistance;

    private const float UpperDeckHeightThreshold = 4.0f;
    private const float TrainCarDetectionHalfLength = 20.0f;
    private const float SignalCarDetectionDistance = 20.0f;
    private const float SignalLaneTolerance = 2.0f;
    private const float TrainCarSafeWidth = 19.0f;
    private const float TrainCarSafeLength = 29.0f;
    private const float ElectrayWidthWithMargin = 6.0f;
    private const float ArcaneRevelationAvoidRadius = 16.5f;
    private const float IntermissionLeashRadius = 14.5f;
    private const float TowerHoldRadius = 3.5f;
    private const float LightningExpressArrivalTolerance = 1.0f;

    // Four five-yalm columns span the twenty-yalm train-car width.
    private static readonly float[] SignalLaneCenters = [92.5f, 97.5f, 102.5f, 107.5f];

    private readonly record struct LightningExpressPlan(
        bool UpperDeck,
        Vector3 Staging,
        Vector3 GroundPanel,
        Vector3 UpperPanel);

    private static class ArenaCenter
    {
        internal static readonly Vector3 Intermission = new(-400.0f, 0.0f, -400.0f);
        internal static readonly Vector3 CarFourEscapePanel = new(100.0f, 0.0f, 262.5f);

        internal static readonly Vector3[] CarThreeGroundPanels =
        [
            new(96.1f, 0.0f, 204.9f),
            new(104.1f, 0.0f, 204.9f),
        ];

        internal static readonly Vector3[] CarThreeUpperPanels =
        [
            new(92.4f, 0.0f, 204.9f),
            new(107.6f, 0.0f, 204.9f),
        ];

        internal static readonly Vector3[] CarFiveGroundPanels =
        [
            new(96.1f, 0.0f, 310.0f),
            new(104.1f, 0.0f, 300.0f),
        ];

        internal static readonly Vector3[] CarFiveUpperPanels =
        [
            new(92.4f, 0.0f, 310.0f),
            new(107.6f, 0.0f, 300.0f),
        ];
    }

    private static class EnemyNpc
    {
        // Normal-mode actor IDs keep registration and early-warning geometry separate from Extreme.
        internal const uint LevinSignal = 0x4A31;
        internal const uint ArcaneRevelation = 0x4A36;
    }

    private static class EnemyAction
    {
        // Five-second boss visual and the physical ram/knockback it announces.
        internal const uint LightningBurstVisual = 45660;
        internal const uint LightningBurst = 45661;
        internal const uint LightningExpress = 45618;

        // Windpipe's helper-owned front-two-row impact.
        internal const uint Blastpipe = 45627;

        // Car-transition raidwide and intermission transition.
        internal const uint UnlimitedExpressVisual = 45623;
        internal const uint RunawayTrain = 45638;

        // Kinematic Turret lines: authored lengths are selected at registration above.
        internal const uint ElectrayLong = 45629;
        internal const uint ElectrayUpper = 45630;
        internal const uint ElectrayLonger = 45631;
        internal const uint ElectrayMedium = 45632;
        internal const uint ElectrayShort = 45633;

        // Height-exclusive full-floor attacks; these must drive transport-panel movement, not avoids.
        internal const uint ThunderousBreathLowerDeck = 45635;
        internal const uint HeadlightUpperDeck = 45637;

        // Add-phase center cones. The rotating Ghost Train's icon/status-driven Aetherial Ray is
        // intentionally absent until live RebornBuddy evidence exposes a safe prediction signal.
        internal const uint AetherSurge = 45643;

        // Arcane Revelation's short cast-location fallback and shared tower lifecycle.
        internal const uint HailOfThunder = 45659;
        internal const uint DerailmentSiegeVisual = 45648;
        internal const uint DerailmentSiegeTower = 45649;
        internal const uint BatteringArmsVisual = 47529;

        // Car-four destruction uses a boss visual plus a helper-owned lethal rectangle.
        internal const uint DerailVisual = 45653;
        internal const uint Derail = 45654;
    }
}
