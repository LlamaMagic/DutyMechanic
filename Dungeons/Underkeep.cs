using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100 Underkeep encounter handling.
/// </summary>
public class Underkeep : AbstractDungeon
{
    // RB drops these short-lived cast wrappers before their action effects resolve. The retention
    // windows come from the 2026-08-27/28 captures and include a small allowance for bot-pulse jitter.
    private static readonly TimeSpan AerialAmbushRetention = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan AlmightyRacketRetention = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan FoundationalDebrisRetention = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SphereShatterRetention = TimeSpan.FromMilliseconds(1_250);
    private static readonly TimeSpan EnforcementRayRetention = TimeSpan.FromSeconds(2);

    // The third ray resolved 25.9 seconds after Coordinate March began in the 2026-08-20 capture.
    // Keep the Trust-follow fallback alive through that final resolution.
    private const int CoordinateMarchDurationMilliseconds = 27_000;

    // A damaging Coordinate Bit settles on an Exploding Orb at a tile center. These tolerances
    // distinguish that stop from ordinary crossings when the 0.2-second ray cast is missed.
    private const float CoordinateBitStoppedSpeedYalmsPerSecond = 0.5f;
    private const float CoordinateBitTileArrivalTolerance = 0.35f;
    private const float CoordinateBitCollisionOrbTolerance = 0.5f;
    private const string CoordinateMarchActorWatch = "Underkeep.CoordinateMarch";
    private const string SectorBisectorActorWatch = "Underkeep.SectorBisector";
    private static readonly TimeSpan CoordinateMarchCaptureInterval = TimeSpan.FromMilliseconds(250);

    // Polygons are counter-clockwise and face south before RB applies rotation. Dimensions include
    // their capture-backed safety margins; Sphere Shatter needs two yalms because smaller margins
    // repeatedly clipped the server snapshot while the character was already escaping.
    private static readonly Vector2[] AerialAmbushRectanglePolygon = CreateForwardRectanglePolygon(17.0f, 30.5f);
    private static readonly Vector2[] AlmightyRacketRectanglePolygon = CreateForwardRectanglePolygon(31.0f, 30.5f);
    private static readonly Vector2[] FoundationalDebrisCirclePolygon = CreateCirclePolygon(10.5f);
    private static readonly Vector2[] SphereShatterCirclePolygon = CreateCirclePolygon(8.0f);
    private static readonly Vector2[] EnforcementRayCrossPolygon = CreateCrossPolygon(10.0f, 36.5f);

    // The cone contours add 0.5 yalm of radial clearance and one degree of total angular clearance
    // to the damaging dimensions; the small margin avoids edge clips without erasing safe lanes.
    private static readonly Vector2[] SectorBisectorConePolygon = CreateConePolygon(45.5f, 181.0f);
    private static readonly Vector2[] StaticForceConePolygon = CreateConePolygon(60.5f, 31.0f);
    private static readonly Vector2[] ElectricFieldConePolygon = CreateConePolygon(26.5f, 51.0f);

    // Gargant
    private readonly Dictionary<uint, TimedDirectionalHazard> aerialAmbushForecasts = [];
    private readonly Dictionary<uint, TimedDirectionalHazard> almightyRacketForecasts = [];
    private readonly Dictionary<uint, TimedHazard> foundationalDebrisForecasts = [];
    private readonly Dictionary<uint, TimedHazard> sphereShatterForecasts = [];

    // Sphere Shatter owns a separate short lease so releasing a completed wave cannot restore
    // combat-routine movement during Coordinate March or another independently active mechanic.
    private readonly CapabilityManagerHandle sphereShatterCapabilityHandle = CapabilityManager.CreateNewHandle();
    private bool sphereShatterMovementHeld;

    // Soldier S0
    private readonly Dictionary<uint, SectorCloneSnapshot> sectorCloneSnapshots = [];
    private readonly Dictionary<uint, uint> sectorEndTetherTargets = [];
    private SectorBisectorDirection sectorBisectorDirection;
    private TimedDirectionalHazard sectorBisectorForecast;
    private bool sectorBisectorVisualCastActive;
    private bool sectorBisectorDamageObserved;
    private int sectorBisectorMaximumTetherCount;

    // Valia Pira
    private readonly Stopwatch coordinateMarchTimer = new();

    // Coordinate March must not clear the base handle used by Deterrent Pulse follow-dodge if their
    // lifecycles touch during transition or wipe cleanup.
    private readonly CapabilityManagerHandle coordinateMarchCapabilityHandle = CapabilityManager.CreateNewHandle();
    private readonly Dictionary<uint, TimedHazard> enforcementRayForecasts = [];
    private readonly Dictionary<uint, CoordinateBitMotionState> coordinateBitMotionStates = [];
    private DateTime nextCoordinateMarchCaptureUtc;

    // Retained only to clean up the arena being left on a sub-zone transition.
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Underkeep;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        EnemyAction.ChillingChirp,
        EnemyAction.Earthsong,
        EnemyAction.FieldOfScorn,
        EnemyAction.EntropicSphere,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.DeterrentPulse];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } =
    [
        EnemyAction.TrapJaws,
        EnemyAction.ThunderousSlash,
    ];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetAllEncounterState("Entered Underkeep");
        RegisterGargantAvoidance();
        RegisterSoldierS0Avoidance();
        RegisterValiaPiraAvoidance();
        RegisterArenaBoundaries();

        return Task.FromResult(false);
    }

    // Keep boss hazards ahead of the arena boundary; live runs validated this established ordering
    // for competing high-priority shapes.
    private void RegisterGargantAvoidance()
    {
        // Foundational Debris is a ten-yalm ground circle. It is intentionally separate from the
        // targeted spreads below: excluding the local player's target would incorrectly remove a
        // real floor hazard, while retaining the player's own spread would create an impossible
        // self-centered avoid. The immutable snapshot keeps its 0.5-yalm navigation margin active
        // through the delayed action effect instead of following the shorter RB cast lifecycle.
        AvoidanceManager.AddAvoidPolygon<TimedHazard>(
            condition: IsGargantCombat,
            leashPointProducer: () => ArenaCenter.Gargant,
            leashRadius: 14.2f,
            rotationProducer: _ => 0.0f,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => FoundationalDebrisCirclePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetActiveForecasts(foundationalDebrisForecasts),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // The 2026-08-20 capture exposed one helper per party target for these spreads. Publish only
        // the other targets so RB separates from the party instead of trying to outrun an AOE that
        // remains centered on the local player.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => IsGargantCombat() || IsSoldierS0Combat() || IsValiaPiraCombat(),
            objectSelector: bc =>
                (bc.CastingSpellId is
                    EnemyAction.SedimentaryDebris or
                    EnemyAction.ElectricExcess or
                    EnemyAction.HyperchargedLight) &&
                bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.CastingSpellId switch
            {
                EnemyAction.ElectricExcess => 6.5f,
                _ => 5.5f,
            },
            locationProducer: bc =>
                GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ??
                bc.SpellCastInfo.CastLocation);

        // Aerial Ambush is owned by a hidden helper casting a 30-by-15 forward lane. SideStep still
        // handles the live cast, while this immutable copy preserves the same helper geometry for a
        // bounded 0.75-second action-effect tail. The contour adds one yalm of lateral clearance and
        // 0.5 yalm at the far edge; the caster-origin edge remains exact so valid rear space survives.
        AvoidanceManager.AddAvoidPolygon<TimedDirectionalHazard>(
            condition: IsGargantCombat,
            leashPointProducer: () => ArenaCenter.Gargant,
            leashRadius: 14.2f,
            rotationProducer: forecast => forecast.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => AerialAmbushRectanglePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetActiveForecasts(aerialAmbushForecasts),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // Almighty Racket is a 30-by-30 forward rectangle. Snapshotting the boss's cast origin and
        // facing keeps the same half-yalm-padded geometry active through the delayed action effect;
        // retaining the frame-scoped boss wrapper would make the tail follow later boss movement.
        AvoidanceManager.AddAvoidPolygon<TimedDirectionalHazard>(
            condition: IsGargantCombat,
            leashPointProducer: () => ArenaCenter.Gargant,
            leashRadius: 14.2f,
            rotationProducer: forecast => forecast.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => AlmightyRacketRectanglePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetActiveForecasts(almightyRacketForecasts),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // Sphere Shatter resolves about 0.77 seconds after its Sand Sphere cast disappeared in the
        // 2026-08-27 capture. Preserve each actively casting sphere's six-yalm circle for 1.25
        // seconds after its last observed cast pulse. A two-yalm margin replaces the smaller
        // contours that still clipped five of thirty-six observed waves across both captures.
        // Inactive spheres are deliberately excluded because both staged waves can coexist and
        // treating every spawned sphere as immediately dangerous would erase the real safe floor.
        AvoidanceManager.AddAvoidPolygon<TimedHazard>(
            condition: IsGargantCombat,
            leashPointProducer: () => ArenaCenter.Gargant,
            leashRadius: 14.2f,
            rotationProducer: _ => 0.0f,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => SphereShatterCirclePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetActiveForecasts(sphereShatterForecasts),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);
    }

    private void RegisterSoldierS0Avoidance()
    {
        // Ordered Fire's Add Block helpers cast 55-by-8 forward lanes. The former five-degree cone
        // described neither the telegraph nor the damage and caused SideStep to defer to unusable
        // local geometry. Padding the rectangle by 0.5 yalm per side preserves real lane gaps.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsSoldierS0Combat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.OrderedFire,
            width: 9.0f,
            length: 55.5f,
            priority: AvoidancePriority.High);

        // Sector Bisector's final clone is known when the first disappearing clone's end tether
        // points to it. The tracker snapshots that target before wrappers expire and publishes the
        // future left/right half-room cleave several seconds before the 0.5-second damage helper.
        AvoidanceManager.AddAvoidPolygon<TimedDirectionalHazard>(
            condition: IsSoldierS0Combat,
            leashPointProducer: () => ArenaCenter.SoldierS0,
            leashRadius: 32.0f,
            rotationProducer: forecast => forecast.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => SectorBisectorConePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: GetActiveSectorBisectorForecasts,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // If RB misses the single-clone disappearance transition, retain last-moment fallbacks on
        // the damage helpers. Their headings point radially rather than along the cleave, so the
        // action side supplies a quarter-turn: left is +90 degrees and right is -90 degrees. The
        // 2026-08-27 run confirmed that an unrotated fallback covered the wrong half-room. Both
        // fallbacks are suppressed whenever the early forecast exists so the mechanic has one owner.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => IsSoldierS0Combat() && !IsSectorBisectorForecastActive(),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SectorBisectorLeft,
            leashPointProducer: () => ArenaCenter.SoldierS0,
            leashRadius: 32.0f,
            rotationDegrees: 90.0f,
            radius: 45.5f,
            arcDegrees: 181.0f,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => IsSoldierS0Combat() && !IsSectorBisectorForecastActive(),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SectorBisectorRight,
            leashPointProducer: () => ArenaCenter.SoldierS0,
            leashRadius: 32.0f,
            rotationDegrees: -90.0f,
            radius: 45.5f,
            arcDegrees: 181.0f,
            priority: AvoidancePriority.High);

        // Static Force fans one cone from Soldier S0 through each party member. The local player's
        // unavoidable personal cone is omitted; dynamically avoiding the other three directions
        // lets the same solver satisfy the overlapping Ordered Fire lanes.
        AvoidanceManager.AddAvoidPolygon<DirectionalHazard>(
            condition: () => IsSoldierS0Combat() && IsActionCasting(EnemyAction.StaticForceVisual),
            leashPointProducer: () => ArenaCenter.SoldierS0,
            leashRadius: 32.0f,
            rotationProducer: forecast => forecast.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => StaticForceConePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetOtherPartyConeForecasts(
                EnemyObjectId.SoldierS0,
                EnemyAction.StaticForceVisual),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);
    }

    private void RegisterValiaPiraAvoidance()
    {
        // Concurrent Field is a 26-yalm, 50-degree helper cone. The previous 40-by-45 geometry
        // overreached the arena while still under-covering the cone edges.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsValiaPiraCombat,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.ConcurrentField,
            leashPointProducer: () => ArenaCenter.ValiaPira,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 26.5f,
            arcDegrees: 51.0f,
            priority: AvoidancePriority.High);

        // Electric Field uses the same cone dimensions but aims one cone through each player; the
        // boss's visual cast is not itself a forward cone. Avoid only the three other bait directions
        // so DutyMechanic encodes the spread semantic instead of repelling everyone from the boss.
        AvoidanceManager.AddAvoidPolygon<DirectionalHazard>(
            condition: () => IsValiaPiraCombat() && IsActionCasting(EnemyAction.ElectricFieldVisual),
            leashPointProducer: () => ArenaCenter.ValiaPira,
            leashRadius: 40.0f,
            rotationProducer: forecast => forecast.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => ElectricFieldConePolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetOtherPartyConeForecasts(
                EnemyObjectId.ValiaPira,
                EnemyAction.ElectricFieldVisual),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        // Coordinate Bits begin their 0.2-second Enforcement Ray cast while still 1-2 yalms short
        // of the collision cell. Every observed hit came from using that transient actor position.
        // Snap the helper to the arena's fixed nine-yalm tile grid and retain the 36-by-9 cross
        // through its delayed action effect. Half a yalm of clearance is added to every edge.
        AvoidanceManager.AddAvoidPolygon<TimedHazard>(
            condition: IsValiaPiraCombat,
            leashPointProducer: () => ArenaCenter.ValiaPira,
            leashRadius: 34.0f,
            rotationProducer: _ => 0.0f,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => EnforcementRayCrossPolygon,
            locationProducer: forecast => forecast.Origin,
            collectionProducer: () => GetActiveForecasts(enforcementRayForecasts),
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);
    }

    private static void RegisterArenaBoundaries()
    {
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SedimentFunnel,
            () => ArenaCenter.Gargant,
            outerRadius: 90.0f,
            innerRadius: 14.2f,
            priority: AvoidancePriority.High);

        // The lethal wall is a 31-by-31 square. A 0.5-yalm inset on every edge prevents the solver
        // from treating the wall itself as an acceptable Ordered Fire safe point.
        AvoidanceHelpers.AddAvoidSquareDonut(
            IsSoldierS0Combat,
            innerWidth: 30.0f,
            innerHeight: 30.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.SoldierS0],
            priority: AvoidancePriority.High);

        // Valia Pira's floor is centered at Z=-331, not -330. Keep a 0.5-yalm inset from each side
        // of the measured 35-by-35 platform for line and cone pathing.
        AvoidanceHelpers.AddAvoidSquareDonut(
            IsValiaPiraCombat,
            innerWidth: 34.0f,
            innerHeight: 34.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.ValiaPira],
            priority: AvoidancePriority.High);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetAllEncounterState("Exited Underkeep");
        lastSubZoneId = SubZoneId.NONE;
        return Task.FromResult(false);
    }

    // Avoidances are registered once per entry; only their transient data is reset between fights.
    private void ResetAllEncounterState(string coordinateMarchReason)
    {
        ResetGargantState();
        ResetSectorBisectorState();
        ResetValiaPiraState(coordinateMarchReason);
    }

    // Clear only the arena being left. Resetting the destination here would discard mechanics that
    // appeared on the same pulse as the sub-zone transition.
    private void ResetDepartedArenaState()
    {
        switch (lastSubZoneId)
        {
            case SubZoneId.SedimentFunnel:
                ResetGargantState();
                break;
            case SubZoneId.ReceivingRoom:
                ResetSectorBisectorState();
                break;
            case SubZoneId.ChamberofPatience:
                ResetValiaPiraState("Left Valia Pira's arena");
                break;
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();
        await TankBusterSpells();
        await DamageMitigationSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (currentSubZoneId != lastSubZoneId)
        {
            ResetDepartedArenaState();
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.SedimentFunnel => await Gargant(),
            SubZoneId.ReceivingRoom => await SoldierS0(),
            SubZoneId.ChamberofPatience => await ValiaPira(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    private Task<bool> Gargant()
    {
        if (!IsGargantCombat())
        {
            ResetGargantState();
            return Task.FromResult(false);
        }

        BattleCharacter[] actors = GetValidBattleCharacters();
        UpdateAerialAmbushState(actors);
        UpdateAlmightyRacketState(actors);
        UpdateFoundationalDebrisState(actors);
        UpdateSphereShatterState(actors);
        HoldSphereShatterSafePosition();
        return Task.FromResult(false);
    }

    private Task<bool> SoldierS0()
    {
        if (!IsSoldierS0Combat())
        {
            ResetSectorBisectorState();
            return Task.FromResult(false);
        }

        UpdateSectorBisectorState(GetValidBattleCharacters());
        return Task.FromResult(false);
    }

    private async Task<bool> ValiaPira()
    {
        if (!IsValiaPiraCombat())
        {
            ResetValiaPiraState("Valia Pira combat ended");
            return false;
        }

        UpdateEnforcementRayState(GetValidBattleCharacters());

        if (EnemyAction.CoordinateMarchActions.IsCasting() || coordinateMarchTimer.IsRunning)
        {
            if (!coordinateMarchTimer.IsRunning)
            {
                CapabilityManager.Update(
                    coordinateMarchCapabilityHandle,
                    CapabilityFlags.Movement,
                    CoordinateMarchDurationMilliseconds,
                    "Coordinate March Avoid");
                coordinateMarchTimer.Start();
            }

            CaptureCoordinateMarchSignals();

            if (coordinateMarchTimer.ElapsedMilliseconds < CoordinateMarchDurationMilliseconds)
            {
                await MovementHelpers.GetClosestMelee.FollowTimed(
                    coordinateMarchTimer,
                    CoordinateMarchDurationMilliseconds,
                    0.5f,
                    useMesh: true);
            }

            if (coordinateMarchTimer.ElapsedMilliseconds >= CoordinateMarchDurationMilliseconds)
            {
                ResetCoordinateMarchState("Coordinate March observation window completed");
            }
        }
        else
        {
            LoggingHelpers.ClearActorSignalWatch(CoordinateMarchActorWatch);
        }

        return false;
    }

    // RB game-object wrappers are frame-scoped. Retain only the helper's scalar geometry for the
    // short delay between Aerial Ambush's cast wrapper and action effect.
    private void UpdateAerialAmbushState(IEnumerable<BattleCharacter> actors)
    {
        DateTime now = DateTime.UtcNow;
        foreach (BattleCharacter helper in actors.Where(actor =>
                     actor.CastingSpellId == EnemyAction.AerialAmbush))
        {
            aerialAmbushForecasts[helper.ObjectId] = new TimedDirectionalHazard(
                helper.Location,
                -helper.Heading,
                now.Add(AerialAmbushRetention));
        }

        RemoveExpiredForecasts(aerialAmbushForecasts, now);
    }

    // Snapshot scalar geometry so the delayed rectangle cannot follow Gargant after the cast ends.
    private void UpdateAlmightyRacketState(IEnumerable<BattleCharacter> actors)
    {
        DateTime now = DateTime.UtcNow;
        foreach (BattleCharacter boss in actors.Where(actor =>
                     actor.BaseId == EnemyObjectId.Gargant &&
                     actor.CastingSpellId == EnemyAction.AlmightyRacket))
        {
            almightyRacketForecasts[boss.ObjectId] = new TimedDirectionalHazard(
                boss.Location,
                -boss.Heading,
                now.Add(AlmightyRacketRetention));
        }

        RemoveExpiredForecasts(almightyRacketForecasts, now);
    }

    // Keep the cast location, not the frame-scoped helper, through the delayed action effect.
    private void UpdateFoundationalDebrisState(IEnumerable<BattleCharacter> actors)
    {
        DateTime now = DateTime.UtcNow;
        foreach (BattleCharacter helper in actors.Where(actor =>
                     actor.CastingSpellId == EnemyAction.FoundationalDebris))
        {
            foundationalDebrisForecasts[helper.ObjectId] = new TimedHazard(
                helper.SpellCastInfo.CastLocation,
                now.Add(FoundationalDebrisRetention));
        }

        RemoveExpiredForecasts(foundationalDebrisForecasts, now);
    }

    // Only casting spheres are armed. Treating every spawned sphere as live would erase the safe
    // floor between the two staged waves.
    private void UpdateSphereShatterState(IEnumerable<BattleCharacter> actors)
    {
        DateTime now = DateTime.UtcNow;
        foreach (BattleCharacter sphere in actors.Where(actor =>
                     actor.BaseId == EnemyObjectId.SandSphere &&
                     actor.CastingSpellId is EnemyAction.SphereShatterFirst or EnemyAction.SphereShatterSecond))
        {
            sphereShatterForecasts[sphere.ObjectId] = new TimedHazard(
                sphere.Location,
                now.Add(SphereShatterRetention));
        }

        RemoveExpiredForecasts(sphereShatterForecasts, now);
    }

    // The 2026-08-27 failure showed target-chase movement re-entering an active Sand Sphere after RB
    // found safe floor. Stop only residual navigation; active avoidance keeps movement priority.
    private void HoldSphereShatterSafePosition()
    {
        TimedHazard[] activeForecasts = GetActiveForecasts(sphereShatterForecasts);
        if (activeForecasts.Length == 0)
        {
            ReleaseSphereShatterMovement("Sphere Shatter hazards expired");
            return;
        }

        int remainingMilliseconds = Math.Max(
            250,
            (int)Math.Ceiling(activeForecasts.Max(forecast =>
                (forecast.ExpiresAtUtc - DateTime.UtcNow).TotalMilliseconds)));
        CapabilityManager.Update(
            sphereShatterCapabilityHandle,
            CapabilityFlags.Movement,
            remainingMilliseconds,
            "Holding Sphere Shatter safe position");
        sphereShatterMovementHeld = true;

        bool playerInsideSphere = activeForecasts.Any(forecast =>
            Core.Player.Location.Distance2D(forecast.Origin) < 8.0f);
        if (!playerInsideSphere && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }
    }

    private void ReleaseSphereShatterMovement(string reason)
    {
        if (!sphereShatterMovementHeld)
        {
            return;
        }

        sphereShatterMovementHeld = false;
        CapabilityManager.Clear(
            sphereShatterCapabilityHandle,
            CapabilityFlags.Movement,
            reason);
    }

    // The first disappearing tether source identifies the future caster. If multiple sources vanish
    // between pulses, the ordering is ambiguous and the exact damage-helper fallback remains in control.
    private void UpdateSectorBisectorState(BattleCharacter[] actors)
    {
        BattleCharacter visualCaster = actors.FirstOrDefault(actor =>
            actor.CastingSpellId is EnemyAction.SectorBisectorLeftVisual or EnemyAction.SectorBisectorRightVisual);

        if (visualCaster != null && !sectorBisectorVisualCastActive)
        {
            ResetSectorBisectorState();
            sectorBisectorVisualCastActive = true;
            sectorBisectorDirection = visualCaster.CastingSpellId == EnemyAction.SectorBisectorLeftVisual
                ? SectorBisectorDirection.Left
                : SectorBisectorDirection.Right;
        }
        else if (visualCaster == null)
        {
            sectorBisectorVisualCastActive = false;
        }

        bool damageCasting = actors.Any(actor =>
            actor.CastingSpellId is EnemyAction.SectorBisectorLeft or EnemyAction.SectorBisectorRight);
        if (damageCasting)
        {
            sectorBisectorDamageObserved = true;
        }
        else if (sectorBisectorDamageObserved)
        {
            ResetSectorBisectorState();
            return;
        }

        if (sectorBisectorDirection == SectorBisectorDirection.None)
        {
            return;
        }

        LoggingHelpers.LogActorSignalChanges(
            SectorBisectorActorWatch,
            actor => actor.BaseId == EnemyObjectId.SoldierS0Clone ||
                     actor.CastingSpellId is EnemyAction.SectorBisectorLeft or EnemyAction.SectorBisectorRight);

        Dictionary<uint, SectorCloneSnapshot> currentSnapshots = actors
            .Where(actor => actor.BaseId == EnemyObjectId.SoldierS0Clone)
            .ToDictionary(actor => actor.ObjectId, CreateSectorCloneSnapshot);

        sectorBisectorMaximumTetherCount = Math.Max(
            sectorBisectorMaximumTetherCount,
            sectorEndTetherTargets.Count);

        if (sectorBisectorForecast == null)
        {
            SectorCloneSnapshot[] departedTetherSources = sectorCloneSnapshots.Values
                .Where(previous =>
                    previous.Visible &&
                    previous.EndTetherTargetId != 0 &&
                    (!currentSnapshots.TryGetValue(
                         previous.ObjectId,
                         out SectorCloneSnapshot current) ||
                     !current.Visible))
                .ToArray();

            if (departedTetherSources.Length == 1)
            {
                SectorCloneSnapshot departed = departedTetherSources[0];
                if (currentSnapshots.TryGetValue(departed.EndTetherTargetId, out SectorCloneSnapshot finalClone) &&
                    finalClone.Visible)
                {
                    float turn = sectorBisectorDirection == SectorBisectorDirection.Left
                        ? MathF.PI / 2.0f
                        : -MathF.PI / 2.0f;
                    double activationDelaySeconds = sectorBisectorMaximumTetherCount == 6 ? 4.6 : 6.3;

                    sectorBisectorForecast = new TimedDirectionalHazard(
                        finalClone.Location,
                        -(finalClone.Heading + turn),
                        DateTime.UtcNow.AddSeconds(activationDelaySeconds + 0.75));
                }
            }
        }

        sectorCloneSnapshots.Clear();
        foreach ((uint objectId, SectorCloneSnapshot snapshot) in currentSnapshots)
        {
            sectorCloneSnapshots[objectId] = snapshot;
        }

        if (sectorBisectorForecast != null && sectorBisectorForecast.ExpiresAtUtc <= DateTime.UtcNow)
        {
            ResetSectorBisectorState();
        }
    }

    // Tether 327 is cleared before its source clone vanishes. Preserve its last nonzero target in
    // scalar state so the following pulse can identify the disappearance without retaining wrappers.
    private SectorCloneSnapshot CreateSectorCloneSnapshot(BattleCharacter actor)
    {
        uint endTetherTargetId = 0;
        if (actor.VfxContainer.IsValid)
        {
            endTetherTargetId = (uint)(actor.VfxContainer.Tethers ?? [])
                .Where(tether => tether.Id == TetherId.BisectorEnd)
                .Select(tether => tether.TargetId)
                .FirstOrDefault();
        }

        if (endTetherTargetId != 0)
        {
            sectorEndTetherTargets[actor.ObjectId] = endTetherTargetId;
        }
        else
        {
            sectorEndTetherTargets.TryGetValue(actor.ObjectId, out endTetherTargetId);
        }

        return new SectorCloneSnapshot(
            actor.ObjectId,
            actor.Location,
            actor.Heading,
            endTetherTargetId,
            actor.IsVisible);
    }

    private IEnumerable<TimedDirectionalHazard> GetActiveSectorBisectorForecasts()
    {
        return IsSectorBisectorForecastActive()
            ? [sectorBisectorForecast]
            : [];
    }

    private bool IsSectorBisectorForecastActive()
    {
        return sectorBisectorForecast != null && sectorBisectorForecast.ExpiresAtUtc > DateTime.UtcNow;
    }

    private void ResetSectorBisectorState()
    {
        sectorCloneSnapshots.Clear();
        sectorEndTetherTargets.Clear();
        sectorBisectorDirection = SectorBisectorDirection.None;
        sectorBisectorForecast = null;
        sectorBisectorVisualCastActive = false;
        sectorBisectorDamageObserved = false;
        sectorBisectorMaximumTetherCount = 0;
        LoggingHelpers.ClearActorSignalWatch(SectorBisectorActorWatch);
    }

    // Recompute party headings on every avoidance pulse so the cones follow Trust movement without
    // retaining frame-scoped party wrappers.
    private static IEnumerable<DirectionalHazard> GetOtherPartyConeForecasts(uint bossBaseId, uint visualActionId)
    {
        BattleCharacter boss = GetValidBattleCharacters()
            .FirstOrDefault(actor =>
                actor.BaseId == bossBaseId &&
                actor.CastingSpellId == visualActionId);
        if (boss == null)
        {
            return [];
        }

        Vector3 origin = boss.Location;
        return PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member =>
                member != null &&
                member.IsValid &&
                member.IsAlive &&
                member.ObjectId != Core.Player.ObjectId &&
                member.Location.Distance2D(origin) > 0.1f)
            .Select(member =>
            {
                float heading = MathF.Atan2(member.Location.X - origin.X, member.Location.Z - origin.Z);
                return new DirectionalHazard(origin, -heading);
            })
            .ToArray();
    }

    // A Coordinate Bit starts its 0.2-second ray cast before reaching the collision cell. Cast
    // discovery is preferred; a moving-to-stopped transition on an orb recovers casts missed between
    // DutyMechanic pulses. Both signals use the fixed tile center as the action-effect origin.
    private void UpdateEnforcementRayState(BattleCharacter[] actors)
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter[] bits = actors
            .Where(actor => actor.BaseId == EnemyObjectId.CoordinateBit)
            .ToArray();
        Vector3[] orbLocations = actors
            .Where(actor => actor.BaseId == EnemyObjectId.ExplodingOrb)
            .Select(actor => actor.Location)
            .ToArray();
        HashSet<uint> liveBitObjectIds = bits.Select(bit => bit.ObjectId).ToHashSet();

        foreach (BattleCharacter bit in bits)
        {
            Vector3 snappedOrigin = SnapCoordinateRayOrigin(bit.Location);
            bool rayCastObserved = bit.CastingSpellId == EnemyAction.EnforcementRay;
            bool arrivedAtCollisionTile = UpdateCoordinateBitMotion(
                bit,
                snappedOrigin,
                orbLocations,
                now);

            if (rayCastObserved || arrivedAtCollisionTile)
            {
                enforcementRayForecasts[bit.ObjectId] = new TimedHazard(
                    snappedOrigin,
                    now.Add(EnforcementRayRetention));
            }
        }

        foreach (uint staleObjectId in coordinateBitMotionStates.Keys
                     .Where(objectId => !liveBitObjectIds.Contains(objectId))
                     .ToArray())
        {
            coordinateBitMotionStates.Remove(staleObjectId);
        }

        RemoveExpiredForecasts(enforcementRayForecasts, now);
    }

    // Only a moving-to-stopped transition on an orb cell may recover a missed cast. Crossings, turns,
    // and route endpoints elsewhere must not arm a ray.
    private bool UpdateCoordinateBitMotion(
        BattleCharacter bit,
        Vector3 snappedOrigin,
        IEnumerable<Vector3> orbLocations,
        DateTime now)
    {
        if (!coordinateBitMotionStates.TryGetValue(bit.ObjectId, out CoordinateBitMotionState previousState))
        {
            coordinateBitMotionStates[bit.ObjectId] = new CoordinateBitMotionState(
                bit.Location,
                now,
                false);
            return false;
        }

        double elapsedSeconds = Math.Max(0.001, (now - previousState.ObservedAtUtc).TotalSeconds);
        float speed = bit.Location.Distance2D(previousState.Location) / (float)elapsedSeconds;
        bool isMoving = speed >= CoordinateBitStoppedSpeedYalmsPerSecond;
        bool arrivedAtCollisionTile =
            previousState.WasMoving &&
            !isMoving &&
            bit.Location.Distance2D(snappedOrigin) <= CoordinateBitTileArrivalTolerance &&
            orbLocations.Any(location =>
                location.Distance2D(snappedOrigin) <= CoordinateBitCollisionOrbTolerance);

        coordinateBitMotionStates[bit.ObjectId] = new CoordinateBitMotionState(
            bit.Location,
            now,
            isMoving);
        return arrivedAtCollisionTile;
    }

    // Coordinate March uses a fixed four-by-four grid with nine-yalm cells. Floor selects the cell
    // being entered; rounding to the nearest arbitrary coordinate can choose the adjacent ray origin.
    private static Vector3 SnapCoordinateRayOrigin(Vector3 position)
    {
        return new Vector3(
            SnapCoordinateTileAxis(position.X, ArenaCenter.ValiaPira.X),
            position.Y,
            SnapCoordinateTileAxis(position.Z, ArenaCenter.ValiaPira.Z));
    }

    private static float SnapCoordinateTileAxis(float coordinate, float arenaCenter)
    {
        int tileIndex = Math.Clamp(
            (int)MathF.Floor(((coordinate - arenaCenter) / 9.0f) + 2.0f),
            0,
            3);
        return arenaCenter - 13.5f + (tileIndex * 9.0f);
    }

    // Four samples per second preserve Coordinate March route timing without flooding diagnostics
    // with per-frame transforms.
    private void CaptureCoordinateMarchSignals()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || DateTime.UtcNow < nextCoordinateMarchCaptureUtc)
        {
            return;
        }

        nextCoordinateMarchCaptureUtc = DateTime.UtcNow + CoordinateMarchCaptureInterval;
        LoggingHelpers.LogActorSignalChanges(
            CoordinateMarchActorWatch,
            actor => actor.BaseId is
                EnemyObjectId.ExplodingOrb or
                EnemyObjectId.TetherSource or
                EnemyObjectId.CoordinateBit or
                EnemyObjectId.CoordinateTurret);
    }

    // Release the capability only when this handler owns an active timer; the base follow-dodge
    // handle has an independent lifecycle.
    private void ResetCoordinateMarchState(string reason)
    {
        if (coordinateMarchTimer.IsRunning)
        {
            coordinateMarchTimer.Reset();
            CapabilityManager.Clear(coordinateMarchCapabilityHandle, reason: reason);
        }

        nextCoordinateMarchCaptureUtc = DateTime.MinValue;
        LoggingHelpers.ClearActorSignalWatch(CoordinateMarchActorWatch);
    }

    private void ResetGargantState()
    {
        aerialAmbushForecasts.Clear();
        almightyRacketForecasts.Clear();
        foundationalDebrisForecasts.Clear();
        sphereShatterForecasts.Clear();
        ReleaseSphereShatterMovement("Sphere Shatter lifecycle ended");
    }

    // Coordinate March timer completion intentionally does not use this arena-level reset because a
    // ray's action-effect tail may still be active.
    private void ResetValiaPiraState(string coordinateMarchReason)
    {
        ResetCoordinateMarchState(coordinateMarchReason);
        enforcementRayForecasts.Clear();
        coordinateBitMotionStates.Clear();
    }

    private static T[] GetActiveForecasts<T>(Dictionary<uint, T> forecasts)
        where T : IExpiringHazard
    {
        DateTime now = DateTime.UtcNow;
        return forecasts.Values
            .Where(forecast => forecast.ExpiresAtUtc > now)
            .ToArray();
    }

    private static void RemoveExpiredForecasts<T>(Dictionary<uint, T> forecasts, DateTime now)
        where T : IExpiringHazard
    {
        foreach (uint objectId in forecasts
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            forecasts.Remove(objectId);
        }
    }

    // AvoidanceManager expects counter-clockwise local-space contours facing true south before rotation.
    private static Vector2[] CreateConePolygon(float radius, float fullAngleDegrees)
    {
        const int ArcSegments = 32;
        float halfAngle = fullAngleDegrees * 0.5f * (MathF.PI / 180.0f);
        List<Vector2> points = new(ArcSegments + 2)
        {
            Vector2.Zero,
        };

        for (int index = 0; index <= ArcSegments; index++)
        {
            float angle = halfAngle - ((halfAngle * 2.0f) * index / ArcSegments);
            points.Add(new Vector2(radius * MathF.Sin(angle), radius * MathF.Cos(angle)));
        }

        return [.. points];
    }

    private static Vector2[] CreateCirclePolygon(float radius)
    {
        const int CircleSegments = 32;
        Vector2[] points = new Vector2[CircleSegments];
        for (int index = 0; index < CircleSegments; index++)
        {
            float angle = -(MathF.PI * 2.0f * index / CircleSegments);
            points[index] = new Vector2(radius * MathF.Sin(angle), radius * MathF.Cos(angle));
        }

        return points;
    }

    private static Vector2[] CreateCrossPolygon(float thickness, float length)
    {
        float halfThickness = thickness * 0.5f;
        return
        [
            new(halfThickness, length),
            new(halfThickness, halfThickness),
            new(length, halfThickness),
            new(length, -halfThickness),
            new(halfThickness, -halfThickness),
            new(halfThickness, -length),
            new(-halfThickness, -length),
            new(-halfThickness, -halfThickness),
            new(-length, -halfThickness),
            new(-length, halfThickness),
            new(-halfThickness, halfThickness),
            new(-halfThickness, length),
        ];
    }

    // Match AvoidanceHelpers.AddAvoidRectangle's caster-origin convention so live and retained
    // helper lanes use the same contour.
    private static Vector2[] CreateForwardRectanglePolygon(float width, float length)
    {
        float halfWidth = width * 0.5f;
        return
        [
            new(halfWidth, length),
            new(-halfWidth, length),
            new(-halfWidth, 0.0f),
            new(halfWidth, 0.0f),
        ];
    }

    private static bool IsGargantCombat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SedimentFunnel;

    private static bool IsSoldierS0Combat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ReceivingRoom;

    private static bool IsValiaPiraCombat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChamberofPatience;

    private static BattleCharacter[] GetValidBattleCharacters() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor != null && actor.IsValid)
            .ToArray();

    private static bool IsActionCasting(uint actionId) =>
        GetValidBattleCharacters().Any(actor => actor.CastingSpellId == actionId);

    // Current-client BNpcBase IDs used to distinguish bosses, clones, and untargetable helpers.
    // They filter observed actors; no ID alone authorizes movement.
    private static class EnemyObjectId
    {
        public const uint Gargant = 0x4791;
        public const uint SandSphere = 0x4792;
        public const uint SoldierS0 = 0x47AD;
        public const uint SoldierS0Clone = 0x47AE;
        public const uint ValiaPira = 0x478E;
        public const uint ExplodingOrb = 0x478F;
        public const uint TetherSource = 0x47B5;
        public const uint CoordinateBit = 0x4789;
        public const uint CoordinateTurret = 0x4793;
    }

    private static class ArenaCenter
    {
        // Measured encounter centers; Valia Pira's Z=-331 is also the Coordinate March grid origin.
        public static readonly Vector3 Gargant = new(-248f, -70f, 122f);
        public static readonly Vector3 SoldierS0 = new(0f, -234f, -182f);
        public static readonly Vector3 ValiaPira = new(0f, -190f, -331f);
    }

    private static class EnemyAction
    {
        // Gargant
        public const uint ChillingChirp = 42547;
        public const uint AlmightyRacket = 42546;
        // The hidden helper, not the preceding boss visual, owns Aerial Ambush's 30-by-15 lane.
        public const uint AerialAmbush = 42543;
        public const uint Earthsong = 42544;
        // The two Sand Sphere waves use different IDs but identical six-yalm geometry.
        public const uint SphereShatterFirst = 42545;
        public const uint SphereShatterSecond = 43135;
        public const uint TrapJaws = 42548;
        public const uint SedimentaryDebris = 43160;
        public const uint FoundationalDebris = 43161;

        // Soldier S0
        public const uint ThunderousSlash = 43136;
        public const uint FieldOfScorn = 42579;
        // Visuals select the side used by the later half-room damage helper.
        public const uint SectorBisectorLeftVisual = 42562;
        public const uint SectorBisectorRightVisual = 42563;
        public const uint SectorBisectorLeft = 42566;
        public const uint SectorBisectorRight = 42567;
        public const uint ElectricExcess = 43139;
        public const uint OrderedFire = 42573;
        public const uint StaticForceVisual = 42574;

        // Valia Pira
        public const uint EntropicSphere = 42525;
        public const uint HyperchargedLight = 42524;
        public const uint DeterrentPulse = 42540;
        public const uint ConcurrentField = 42521;
        // Electric Field is only the visual for the player-baited cones.
        public const uint ElectricFieldVisual = 42519;
        public const uint EnforcementRay = 42737;
        public const uint CoordinateMarch = 42513;
        public static readonly HashSet<uint> CoordinateMarchActions = [CoordinateMarch];
    }

    private static class TetherId
    {
        // The first disappearing source of this clone-chain tether points to the eventual cleaver.
        public const uint BisectorEnd = 327;
    }

    private enum SectorBisectorDirection
    {
        None,
        Left,
        Right,
    }

    // These immutable scalar records are the bot-thread snapshot boundary. Never retain RB actor or
    // VFX wrappers between pulses.
    private interface IExpiringHazard
    {
        DateTime ExpiresAtUtc { get; }
    }

    private sealed record TimedHazard(
        Vector3 Origin,
        DateTime ExpiresAtUtc) : IExpiringHazard;

    private sealed record TimedDirectionalHazard(
        Vector3 Origin,
        float Rotation,
        DateTime ExpiresAtUtc) : IExpiringHazard;

    private sealed record CoordinateBitMotionState(
        Vector3 Location,
        DateTime ObservedAtUtc,
        bool WasMoving);

    private sealed record SectorCloneSnapshot(
        uint ObjectId,
        Vector3 Location,
        float Heading,
        uint EndTetherTargetId,
        bool Visible);

    private sealed record DirectionalHazard(
        Vector3 Origin,
        float Rotation);
}
