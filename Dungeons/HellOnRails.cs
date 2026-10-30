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
/// stopped Ghost Train exposes its tank target for the intermission cone; non-targets avoid it
/// while the routine retains tank mitigation.
/// </remarks>
public partial class HellOnRails : AbstractDungeon
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
    private bool lightningCastObserved;
    private bool lightningKnockbackResolved;
    private DateTime lightningHoldUntil;
    private DateTime lightningSignalImpactUntil;
    private DateTime windpipeHoldUntil;
    private HellOnRailsMovement movement;
    private bool movementReplaced;
    private int lastCar = -1;
    private readonly CapabilityManagerHandle windpipeMovementHandle = CapabilityManager.CreateNewHandle();
    private bool windpipeActive;

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
        // Observe the same bot-thread movement channel even while a routine coroutine
        // owns scheduling. Stops and hook resets release this instance's input only.
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        TreeRoot.OnStop += OnStop;
        ff14bot.Behavior.TreeHooks.Instance.OnHooksCleared += OnHooksCleared;
        // Levin Signals appear about seven seconds before their one-second plasma casts. The
        // Lightning Express planner below consumes their lane and elevation as one semantic
        // mechanic, including the knockback landing. Publishing standalone rectangles would cover
        // car three's entire lower deck before RebornBuddy can traverse a transport panel.

        // Electray is authored as a five-yalm-wide line. Upper turrets only cover the platform
        // section in front of them, while the four lower variants retain their observed lengths.
        RegisterTurretAvoid();

        // The captured helper stands ten yalms ahead of the car. Blastpipe starts at
        // the car's front edge, not at that actor; the old origin missed the pulled
        // player at Z94.5 on car one and Z291.2 on car five. Keep a half-yalm border.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => IsInHellOnRailsCombat() && !OwnsPullRecovery,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.Blastpipe,
            width: 21.0f,
            length: 11.0f,
            yOffset: -0.5f,
            locationProducer: caster => new Vector3(100.0f, caster.Y, caster.Z + 10.0f),
            priority: AvoidancePriority.High);

        // After the knockback, turret escape must also respect the still-pending
        // signal beams; a turret-only escape can cross an unresolved ground signal.
        // Before the knockback, the semantic planner
        // retains ownership so ground-wide beams cannot block ascent panels.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            condition: () => IsInHellOnRailsCombat() && lightningKnockbackResolved && DateTime.UtcNow >= lightningHoldUntil,
            leashPointProducer: null,
            leashRadius: 50.0f,
            rotationProducer: signal => -signal.Heading,
            scaleProducer: signal => 1.0f,
            heightProducer: signal => 2.0f,
            pointsProducer: GetSignalRectangle,
            locationProducer: signal => signal.Location,
            collectionProducer: () => TryGetCurrentCar(out int car)
                ? GetLevinSignals(car).Where(signal => IsSameElevation(signal, Core.Player))
                : Enumerable.Empty<BattleCharacter>(),
            priority: AvoidancePriority.High);

        RegisterLightningBurstAvoid();

        // Four center helpers cast Aether Surge during the add phase. Expanding the 45-degree,
        // thirty-yalm cone by two degrees per edge gives approximately a 0.5-yalm lateral margin
        // at the arena wall without consuming the adjacent safe wedges.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInHellOnRailsCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.AetherSurge,
            leashPointProducer: () => ArenaCenter.Intermission,
            leashRadius: IntermissionLeashRadius,
            rotationDegrees: 0.0f,
            radius: 30.5f,
            arcDegrees: 49.0f);

        // The moving preview is not an explosion center. Stage centrally, then route from the
        // real cast location; retain the native circle only when no collision-safe route exists.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => IsInHellOnRailsCombat() && !IsUpperDeck(Core.Player)
                && !(OwnsGroundRoute && groundRouteForHail),
            objectSelector: caster => caster.CastingSpellId == EnemyAction.HailOfThunder,
            radiusProducer: caster => ArcaneRevelationAvoidRadius,
            locationProducer: caster => WithPlayerHeight(caster.SpellCastInfo.CastLocation),
            priority: AvoidancePriority.High));

        // Every train car is twenty by thirty yalms. The half-yalm inset prevents fall-edge path
        // candidates while preserving the corner pockets needed for Arcane Revelation. The center
        // follows the player's current car and is absent in the separate intermission arena.
        AvoidanceHelpers.AddAvoidSquareDonut(
            // Forced car transfers leave the floor temporarily; an airborne
            // boundary escape has no valid starting span and must not compete.
            canRun: () => IsInHellOnRailsCombat() && !MovementManager.GenerallyOutOfControl && TryGetCurrentCar(out _),
            innerWidth: TrainCarSafeWidth,
            innerHeight: TrainCarSafeLength,
            outerWidth: 100.0f,
            outerHeight: 100.0f,
            collectionProducer: GetCurrentCarCenterCollection,
            priority: AvoidancePriority.High);

        RegisterGhostTrainAvoid();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseLifecycle();
        return Task.FromResult(false);
    }

    private void OnStop(ff14bot.AClasses.BotBase bot) => ReleaseLifecycle();
    private void OnHooksCleared(object sender, EventArgs args) => ReleaseLifecycle();

    private void ReleaseLifecycle()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= OnPulse;
        TreeRoot.OnStop -= OnStop;
        ff14bot.Behavior.TreeHooks.Instance.OnHooksCleared -= OnHooksCleared;
        ReleaseAllMovement("Hell on Rails lifecycle ended");
        movement?.Detach();
        movement = null;
    }

    private void OnPulse(object sender, EventArgs args)
    {
        if (!PluginManager.Plugins.Any(p => p.Plugin is DutyMechanicPlugin && p.Enabled))
        {
            ReleaseLifecycle();
            return;
        }
        if (!IsInHellOnRailsCombat())
        {
            ReleaseAllMovement("Hell on Rails inactive");
            movement?.Detach();
            movement = null;
            lastCar = -1;
            return;
        }
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
            movementReplaced = true;
        }
        if (!movementReplaced && movement == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new HellOnRailsMovement(Navigator.PlayerMover, ConstrainGroundMove);
            Navigator.PlayerMover = movement;
        }
        movement?.CompleteEscape();
        movement?.ReleaseExpiredExternalInput(DateTime.UtcNow);
        UpdateLightningBurst();
        UpdateOverlapState();
        int car = TryGetCurrentCar(out int currentCar) ? currentCar : 0;
        var express = FindCaster(EnemyAction.LightningExpress);
        var pull = FindCaster(EnemyAction.Windpipe);
        // On October 29 the car-two turret cast started before the knockback/pull
        // effect. Native escape abandoned the correct lane/crate before displacement.
        // Keep that shelter through the effect, then expose the later turret wave.
        // Plasma hit 2.9s after Express's bar ended; the pull arrived 0.5s after its
        // helper bar. These small tails include observed effect latency, not just casts.
        if (express != null)
            lightningHoldUntil = DateTime.UtcNow + express.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3.2);
        if (pull != null)
            windpipeHoldUntil = DateTime.UtcNow + pull.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.0);
        if (express != null)
        {
            lightningCastObserved = true;
            lightningKnockbackResolved = false;
            lightningSignalImpactUntil = DateTime.MinValue;
        }
        else if (car != 0 && GetLevinSignals(car).Length != 0)
        {
            lightningKnockbackResolved = lightningCastObserved;
        }
        else
        {
            lightningCastObserved = false;
            lightningKnockbackResolved = false;
        }
        if (car == 5 && lightningKnockbackResolved)
        {
            // The final car's rear landing needs about two seconds to clear the later turret.
            // A fixed hold after Express delayed that route until damage. Once the actual
            // signal bar is seen, its end plus the observed effect tail is the better handoff;
            // retain the longer original hold when the short signal cast was not observed.
            // These four action IDs are the ground/upper Plasma signal variants. Use the
            // latest simultaneous bar so enumeration order cannot reopen a live lane.
            var signalEnds = GetLevinSignals(car)
                .Where(signal => signal.IsCasting && signal.CastingSpellId is >= 45619 and <= 45622)
                .Select(signal => DateTime.UtcNow + signal.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.7));
            foreach (var end in signalEnds)
                if (end > lightningSignalImpactUntil) lightningSignalImpactUntil = end;
            if (lightningSignalImpactUntil != DateTime.MinValue && lightningSignalImpactUntil < lightningHoldUntil)
                lightningHoldUntil = lightningSignalImpactUntil;
        }
        // Decide ground ownership only after updating displacement holds on this pulse.
        UpdateGroundRoute();
        InterruptGroundRouteCast();
        if (car != lastCar && !MovementManager.GenerallyOutOfControl)
        {
            // A landing loads a different physical floor, including crates/platforms.
            // Rebuild the local collision heightfield once, never on each cast or tick.
            movement?.ClearNavigationRecovery(true);
            externalGroundRoute = null;
            AvoidanceManager.ResetNavigation();
            lastCar = car;
        }
        movement?.ClearNavigationRecovery(false);
        // Execute an already planned ground escape even while the routine awaits an action.
        // Displacement/floor holds were resolved above; destroyed-car escape still wins.
        if (OwnsGroundRoute &&
            FindCaster(EnemyAction.Derail, EnemyAction.DerailVisual) == null)
            AdvanceGroundRoute();
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        OnPulse(null, EventArgs.Empty);
        // Whole-floor lower/upper casts and full-car knockbacks are valid combat actions but not
        // ordinary unsafe rectangles. SideStep cannot express elevation and would otherwise cover
        // every navigable point or duplicate the encounter-owned signal and turret lines.
        SidestepPlugin.Enabled = false;

        if (!IsInHellOnRailsCombat() || movement == null || !movement.IsCurrent)
        {
            ReleaseAllMovement("Hell on Rails combat ended");
            return false;
        }

        await TankBusterSpells();
        await DamageMitigationSpells();
        AcquireSelectedDoomtrain();

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

        if (await HandleWindpipeAsync())
        {
            return true;
        }

        if (await HandleHeadOnEmissionAsync())
        {
            return true;
        }

        if (await HandleGroundRouteAsync())
            return true;

        if (await HandleDeckEscapeAsync())
            return true;

        return await HandleDerailmentSiegeAsync();
    }

    // The pull resolves before the front-row explosion. Start at the rear on the
    // bare first car, behind car two's northern crate, or at car five's rear. The captured
    // pull-to-turret overlap then follows a collision-checked route through the same owner.
    private async Task<bool> HandleWindpipeAsync()
    {
        var caster = FindCaster(EnemyAction.WindpipeVisual, EnemyAction.Windpipe);
        if (!TryGetCurrentCar(out int car) || car is not (1 or 2 or 5) ||
            (caster == null && DateTime.UtcNow >= windpipeHoldUntil && !OwnsPullRecovery))
        {
            if (windpipeActive)
                CapabilityManager.Clear(windpipeMovementHandle, CapabilityFlags.Movement, "Windpipe pull resolved");
            windpipeActive = false;
            movement?.Release(HellOnRailsMovement.Request.Windpipe);
            return false;
        }
        windpipeActive = true;
        CapabilityManager.Update(windpipeMovementHandle, CapabilityFlags.Movement,
            TimeSpan.FromSeconds(1) + (caster?.SpellCastInfo.RemainingCastTime ?? TimeSpan.Zero), "Staging behind Windpipe");
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
            return false;
        if (OwnsPullRecovery && pullMotion != null && (pullMotion.Displaced || pullMotion.Settled))
        {
            if (pullMotion.Settled) return await FollowPullRecoveryAsync(car);
            // The game is still drawing us toward the engine. Release our old staging input
            // and wait for the observed motion to settle before caching recovery waypoints.
            movement.Release(HellOnRailsMovement.Request.Windpipe);
            await Coroutine.Yield();
            return true;
        }
        var destination = car == 1
            ? new Vector3(Math.Clamp(Core.Me.X, 91, 109), Core.Me.Y, 113.8f)
            : car == 2 ? new Vector3(102.5f, Core.Me.Y, 151.5f)
            : IsUpperDeck(Core.Player) ? GetPlatformDescent() : new Vector3(100, Core.Me.Y, 313.8f);
        if (Core.Me.Distance2D(destination) <= 0.5f)
        {
            movement.Release(HellOnRailsMovement.Request.Windpipe);
            return false;
        }
        movement.Move(HellOnRailsMovement.Request.Windpipe, destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Chooses a signal-safe lane and stages far enough forward for Lightning Express's
    /// wall-stopped knockback; cars with platforms use their transport-panel endpoints explicitly.
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
        if (lightningKnockbackResolved && DateTime.UtcNow >= lightningHoldUntil)
        {
            // The staging point is only valid before the push. Returning toward it
            // afterward crossed car two's beam while the turret wave was resolving.
            ReleaseLightningExpressMovement("Lightning Express handed off to beam and turret avoidance");
            return false;
        }
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
            TimeSpan.FromSeconds(1) + (caster?.SpellCastInfo.RemainingCastTime ?? TimeSpan.Zero),
            "Holding a signal-safe Lightning Express knockback lane");

        if (!TryBuildLightningExpressPlan(car, signals, out LightningExpressPlan plan))
        {
            StopOwnedMovement(ref movingForLightningExpress, HellOnRailsMovement.Request.Lightning);
            if (caster != null && !lightningExpressMissingSignalsLogged)
            {
                Logger.Warning("Hell on Rails: no safe Levin Signal lane found on car {0}; waiting for a complete signal layout.", car);
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
            destination = GetPlatformDescent();
        }
        else if (plan.UpperDeck && IsOnOppositePlatform(plan.Staging))
        {
            // Platforms are disconnected. Return to ground through the current endpoint before
            // approaching the chosen platform's entrance on the next bot tick.
            destination = GetPlatformDescent();
        }
        else
        {
            // After the push, preserve the safe column at the landing depth until
            // Plasma Beam resolves. Returning to the pre-push depth crosses crates.
            destination = lightningKnockbackResolved
                ? new Vector3(plan.Staging.X, Core.Player.Y, Core.Player.Z)
                : WithPlayerHeight(plan.Staging);
            if (Core.Player.Distance2D(destination) <= LightningExpressArrivalTolerance)
            {
                StopOwnedMovement(ref movingForLightningExpress, HellOnRailsMovement.Request.Lightning);
                return false;
            }
        }

        if (car == 2 && !lightningKnockbackResolved)
        {
            var waypoint = CrateStagingWaypoint(destination);
            if (!waypoint.HasValue) return false;
            destination = WithPlayerHeight(waypoint.Value);
        }

        movingForLightningExpress = true;
        movement.Move(HellOnRailsMovement.Request.Lightning, destination);
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
        movement.Move(HellOnRailsMovement.Request.Derail, destination);
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

        if ((caster == null && DateTime.UtcNow >= floorHoldUntil) || !TryGetCurrentCar(out int car) || car is not (3 or 5))
        {
            ReleaseElevationMovement("Head-on Emission cast or supported car lifecycle ended");
            return false;
        }

        bool lowerDeckIsUnsafe = lowerDeckCaster != null || (caster == null && floorHoldUpper);
        bool playerIsUpperDeck = IsUpperDeck(Core.Player);
        bool playerIsAlreadySafe = lowerDeckIsUnsafe ? playerIsUpperDeck : !playerIsUpperDeck;

        elevationMovementActive = true;
        CapabilityManager.Update(
            elevationMovementHandle,
            CapabilityFlags.Movement,
            TimeSpan.FromSeconds(1) + (caster?.SpellCastInfo.RemainingCastTime ?? TimeSpan.Zero),
            lowerDeckIsUnsafe
                ? "Holding the upper deck for Thunderous Breath"
                : "Holding the lower deck for Headlight");

        if (playerIsAlreadySafe)
        {
            StopOwnedMovement(ref movingForElevation, HellOnRailsMovement.Request.Elevation);
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        // Upper coordinates are arrival points, not return portals. Walk across the inner
        // edge onto the central deck; targeting the arrival point causes oscillation.
        Vector3 destination = playerIsUpperDeck ? GetPlatformDescent() : GetNearestElevationPanel(car, false);
        movingForElevation = true;
        movement.Move(HellOnRailsMovement.Request.Elevation, destination);
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

        if (!IsUpperDeck(Core.Player) && Core.Player.Distance2D(tower) <= TowerHoldRadius)
        {
            StopOwnedMovement(ref movingForTower, HellOnRailsMovement.Request.Tower);
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        movingForTower = true;
        // Descend toward the central deck first, keeping away from the car's
        // outside edge before approaching a ground-level tower.
        var destination = IsUpperDeck(Core.Player) && TryGetCurrentCar(out int car) && car is 3 or 5
            ? GetPlatformDescent() : tower.Location;
        movement.Move(HellOnRailsMovement.Request.Tower, destination);
        await Coroutine.Yield();
        return true;
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
        // Signals spawn over several frames. Absence in a partially observed row
        // does not prove safety; wait for all four authored columns to be present.
        if (!SignalLaneCenters.All(lane => signals.Any(signal => Math.Abs(signal.X - lane) <= SignalLaneTolerance)))
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

            // On car two, an outer landing leaves a long cross-car escape before the following
            // turrets. A safe inner lane lands near crate cover; proximity breaks remaining ties.
            float lane = safeGroundLanes.OrderBy(candidate => car == 2 ? Math.Abs(candidate - 100) : 0)
                .ThenBy(candidate => Math.Abs(Core.Player.X - candidate)).First();
            float stagingZ = car * 50.0f + 40.0f;
            plan = new LightningExpressPlan(false, new(lane, 0.0f, stagingZ), default, default);
            return true;
        }

        if (car == 3)
        {
            List<LightningExpressPlan> candidates = [];
            AddCentralGroundPlans(candidates, signals, car);
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
            // Both platforms stop the backward knockback at the car wall. Their
            // different north edges determine where the ground beam ends.
            List<LightningExpressPlan> candidates = [];
            AddCentralGroundPlans(candidates, signals, car);
            AddUpperPlatformPlanIfSafe(candidates, signals, 92.4f, 307.0f,
                ArenaCenter.CarFiveGroundPanels[0], ArenaCenter.CarFiveUpperPanels[0]);
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

    // Both upper platforms can carry beams simultaneously while a central ground column is safe.
    // Central columns have uninterrupted floor for the full thirty-yalm push;
    // outer ground columns need platform collision modeling and are not candidates.
    private static void AddCentralGroundPlans(List<LightningExpressPlan> candidates, BattleCharacter[] signals, int car)
    {
        foreach (float lane in new[] { 97.5f, 102.5f })
        {
            if (!signals.Any(signal => !IsUpperDeck(signal) && Math.Abs(signal.X - lane) <= SignalLaneTolerance))
                candidates.Add(new(false, new(lane, 0, car * 50.0f + 40.0f), default, default));
        }
    }

    /// <summary>
    /// Returns the live signal actors associated with one train car.
    /// </summary>
    private static BattleCharacter[] GetLevinSignals(int car) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(signal => signal.IsValid && signal.IsVisible && signal.BaseId == EnemyNpc.LevinSignal && IsSignalOnCar(signal, car))
            .ToArray();

    // Crates and raised platforms truncate each signal lane. These extents match
    // the captured helper rows; elevation filtering keeps upper beams off the deck.
    private static Vector2[] GetSignalRectangle(BattleCharacter signal)
    {
        int car = (int)Math.Round((signal.Z - 35.0f) / 50.0f);
        bool upper = IsUpperDeck(signal);
        float start = 0.0f;
        float length = 30.0f;
        if (car == 2 && !upper)
            length = signal.X is > 95.0f and < 100.0f ? 20.0f
                : signal.X is > 100.0f and < 105.0f ? 10.0f : 30.0f;
        else if (car == 3 && (signal.X < 95.0f || signal.X > 105.0f))
        {
            start = upper ? 10.0f : 0.0f;
            length = upper ? 20.0f : 10.0f;
        }
        else if (car == 5 && (signal.X < 95.0f || signal.X > 105.0f))
        {
            float platformStart = signal.X < 95.0f ? 20.0f : 10.0f;
            start = upper ? platformStart : 0.0f;
            length = upper ? 10.0f : platformStart;
        }
        return [new(-3.0f, start - 0.5f), new(3.0f, start - 0.5f),
            new(3.0f, start + length + 0.5f), new(-3.0f, start + length + 0.5f)];
    }

    // Both platforms have an inner edge at X95/105. Leave the arrival lane before
    // crossing it, otherwise the descent lands on the one-yalm ascent trigger.
    // All input still passes through the same encounter movement owner.
    private static Vector3 GetPlatformDescent()
    {
        TryGetCurrentCar(out int car);
        Vector3 arrival = GetNearestUpperPanel(car);
        float offset = Core.Player.Z - arrival.Z;
        if (Math.Abs(offset) < 2.0f)
            return new(Core.Player.X, Core.Player.Y, arrival.Z + (offset < 0.0f ? -3.0f : 3.0f));
        return new(Core.Player.X < 100.0f ? 97.0f : 103.0f, Core.Player.Y, Core.Player.Z);
    }

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
            .OrderBy(candidate => Core.Player.Distance2D(candidate.UpperDeck
                ? (IsUpperDeck(Core.Player) ? candidate.UpperPanel : candidate.GroundPanel)
                : candidate.Staging))
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
        if (Core.Player == null || Math.Abs(Core.Player.X - 100.0f) > 15.0f)
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
    private void StopOwnedMovement(ref bool ownedMovement, HellOnRailsMovement.Request request)
    {
        if (ownedMovement && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            movement?.Release(request);
        }

        ownedMovement = false;
    }

    private void ReleaseAllMovement(string reason)
    {
        externalGroundRoute = null;
        ResetLightningBurst();
        ReleaseOverlapState(reason);
        lightningHoldUntil = DateTime.MinValue;
        lightningSignalImpactUntil = DateTime.MinValue;
        windpipeHoldUntil = DateTime.MinValue;
        lightningCastObserved = false;
        lightningKnockbackResolved = false;
        if (windpipeActive)
            CapabilityManager.Clear(windpipeMovementHandle, CapabilityFlags.Movement, reason);
        windpipeActive = false;
        movement?.Release(HellOnRailsMovement.Request.Windpipe);
        ReleaseElevationMovement(reason);
        ReleaseLightningExpressMovement(reason);
        ReleaseTowerMovement(reason);
        ReleaseDerailMovement(reason);
    }

    private void ReleaseLightningExpressMovement(string reason)
    {
        lightningGroundRoute = null;
        lightningGroundRouteIndex = 0;
        if (lightningExpressMovementActive)
        {
            CapabilityManager.Clear(lightningExpressMovementHandle, CapabilityFlags.Movement, reason);
            lightningExpressMovementActive = false;
        }

        StopOwnedMovement(ref movingForLightningExpress, HellOnRailsMovement.Request.Lightning);
    }

    private void ReleaseElevationMovement(string reason)
    {
        if (elevationMovementActive)
        {
            CapabilityManager.Clear(elevationMovementHandle, CapabilityFlags.Movement, reason);
            elevationMovementActive = false;
        }

        StopOwnedMovement(ref movingForElevation, HellOnRailsMovement.Request.Elevation);
    }

    private void ReleaseTowerMovement(string reason)
    {
        if (towerMovementActive)
        {
            CapabilityManager.Clear(towerMovementHandle, CapabilityFlags.Movement, reason);
            towerMovementActive = false;
        }

        StopOwnedMovement(ref movingForTower, HellOnRailsMovement.Request.Tower);
    }

    private void ReleaseDerailMovement(string reason)
    {
        if (derailMovementActive)
        {
            CapabilityManager.Clear(derailMovementHandle, CapabilityFlags.Movement, reason);
            derailMovementActive = false;
        }

        StopOwnedMovement(ref movingForDerail, HellOnRailsMovement.Request.Derail);
    }

    private static bool IsInHellOnRailsCombat() =>
        Core.Player != null
        && Core.Player.IsAlive && TreeRoot.IsRunning && DutyManager.InInstance
        && WorldManager.ZoneId == (uint)Data.ZoneId.HellOnRails
        // The clear can leave live helper actors behind while LLLeaveDuty waits. They no longer
        // own movement once the director has ended the fight.
        && (DirectorManager.ActiveDirector is not ff14bot.Directors.InstanceContentDirector director || !director.InstanceEnded)
        && !ff14bot.Behavior.CommonBehaviors.IsLoading && !QuestLogManager.InCutscene;

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
        // Arena transfers and Aether helpers place the separate intermission floor at Y-900.
        internal static readonly Vector3 Intermission = new(-400.0f, -900.0f, -400.0f);
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
        internal const uint WindpipeVisual = 45625;
        internal const uint Windpipe = 45667;

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

        // Add-phase center cones. Ghost Train's Aetherial Ray uses its stopped state and tank
        // target instead of a cast warning; see RegisterGhostTrainAvoid.
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
