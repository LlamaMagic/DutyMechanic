using Buddy.Coroutines;
using Clio.Common;
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
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100 normal-mode The Ageless Necropolis trial logic.
/// </summary>
/// <remarks>
/// Geometry and identifiers are based on the normal T05 Necron encounter rather than the Extreme
/// variant. DutyMechanic exclusively owns avoidance in this duty because SideStep also publishes the
/// Grand Cross helper casts, producing duplicate and sometimes contradictory forbidden geometry.
/// Mass Macabre tower assignments remain a diagnostic capture target because their decisive state is
/// delivered by map effects that the current dungeon abstraction does not expose safely. The prison
/// route instead uses the captured fixed platform layout and advances only after all local hands die.
/// </remarks>
public class AgelessNecropolis : AbstractDungeon
{
    private const float MainArenaSafeWidth = 33.0f;
    private const float MainArenaSafeHeight = 28.0f;
    private const float ArenaBoundaryOuterExtent = 90.0f;
    private const float GrandCrossSafeRadius = 8.0f;
    private const float AetherblightCircleAvoidRadius = 22.0f;
    private const float AetherblightDonutSafeRadius = 14.0f;
    private const float EncounterDetectionRadius = 60.0f;
    private const int NecronMovementLeaseMilliseconds = 1_500;
    private const float NavigationSafeHalfWidth = 15.0f;
    private const float NavigationSafeMinimumZ = 87.0f;
    private const float NavigationSafeMaximumZ = 113.0f;
    private const float PrisonMainPlatformSafeRadius = 8.5f;
    private const float PrisonTransferPlatformSafeRadius = 3.5f;
    private const float PrisonGoalPlatformSafeRadius = 2.25f;
    private const float PrisonHandDodgeMaximumRadius = 7.75f;
    private const float ChokingGraspLength = 24.0f;
    private const float ChokingGraspHalfWidthWithMargin = 4.25f;
    private const float ChokingGraspRearMargin = 1.25f;
    private const float PrisonHandDodgeArrivalRadius = 0.6f;

    // Grand Cross tether previews resolve 7.6s after Azure Aether 1 and 5.6s after Azure Aether 2.
    // RB avoidance has no future-activation timestamp, so publish each line only for its final two
    // seconds—enough to cross the radius-nine arena without turning every forecast into a phase-long
    // wall. The one-second grace covers a delayed cast packet and is discarded on the actual cast.
    private static readonly TimeSpan GrandCrossAzureAetherOneDelay = TimeSpan.FromSeconds(7.6);
    private static readonly TimeSpan GrandCrossAzureAetherTwoDelay = TimeSpan.FromSeconds(5.6);
    private static readonly TimeSpan GrandCrossPredictionLeadTime = TimeSpan.FromSeconds(2.0);
    private static readonly TimeSpan GrandCrossPredictionGraceTime = TimeSpan.FromSeconds(1.0);

    // The eight solo prisons share the same local four-platform layout, translated in X/Z. The
    // physical floor radii are 9.5, 4.5, 4.5, and 3.25 yalms; each boundary keeps one yalm inside the
    // fall edge while preserving every portal entrance. Y=-410 is the captured prison elevation.
    private static readonly Vector3[] PrisonCenters =
    [
        new(100.0f, -410.0f, -100.0f),
        new(300.0f, -410.0f, -100.0f),
        new(300.0f, -410.0f, 100.0f),
        new(300.0f, -410.0f, 300.0f),
        new(100.0f, -410.0f, 300.0f),
        new(-100.0f, -410.0f, 300.0f),
        new(-100.0f, -410.0f, 100.0f),
        new(-100.0f, -410.0f, -100.0f),
    ];

    private static readonly Vector3[] PrisonPlatformOffsets =
    [
        new(0.0f, 0.0f, 0.0f),
        new(-5.0f, 0.0f, -21.0f),
        new(14.0f, 0.0f, -14.0f),
        new(20.0f, 0.0f, 0.0f),
    ];

    // Each destination is the blue floor portal on the player's current platform. DutyMechanic keeps
    // walking through its center until the game's floor trigger transfers the player. It deliberately
    // does not use arrival-based stopping, which can halt on the circle's edge, or request a path
    // across the disconnected platform meshes.
    private static readonly Vector3[] PrisonPortalOffsets =
    [
        new(0.0f, 0.0f, -7.4f),
        new(-2.5f, 0.0f, -20.0f),
        new(15.0f, 0.0f, -11.5f),
        new(20.0f, 0.0f, 0.0f),
    ];

    // Necron's actor stands north of the platform at Z=78. Service navigation treats that actor as a
    // reachable combat destination and can route sideways off the platform. Melee jobs instead use a
    // fixed point inside the north wall; the boss's large combat reach keeps that point actionable.
    private static readonly Vector3 NecronMeleeAnchor = new(100.0f, 0.0f, 87.5f);

    // Aetherblight is centered on Necron's north-edge actor rather than the floor's visual center.
    // The live normal-mode helper also occupies this point; this captured fallback is used only for
    // the short actor-table gap around a model transition so the stored in/out preview cannot vanish.
    private static readonly Vector3 AetherblightOriginFallback = new(100.0f, 0.0f, 78.0f);

    // Memento Mori's center lane persists after its cast finishes and is removed by the following
    // Icy Hands sequence. The timeout is only a fail-safe for missed object lifecycle events so a
    // stale avoid cannot strand the bot after a wipe or unusual phase transition.
    private static readonly TimeSpan MementoMoriSafetyTimeout = TimeSpan.FromSeconds(20);
    private static readonly Vector3 MementoMoriLaneOrigin = new(100.0f, 0.0f, 85.0f);
    private static readonly Vector2[] MementoMoriLanePolygon =
    [
        new(6.0f, 37.0f),
        new(-6.0f, 37.0f),
        new(-6.0f, 0.0f),
        new(6.0f, 0.0f),
    ];

    // Grand Cross's rotating line preview is encoded by the Azure Aether actor variant and tether.
    // These fixed offsets convert the tether-time center-to-actor bearing into RB radians. Each angle
    // must be snapshotted on the tether edge because the source visibly spins afterward.
    private const float AzureAetherOneRotationOffset = 41.0f * (float)(Math.PI / 180.0);
    private const float AzureAetherTwoRotationOffset = -153.0f * (float)(Math.PI / 180.0);
    private static readonly Vector2[] GrandCrossPredictionPolygon =
    [
        new(2.25f, 50.0f),
        new(-2.25f, 50.0f),
        new(-2.25f, -50.0f),
        new(2.25f, -50.0f),
    ];

    private bool mementoMoriWasCasting;
    private bool mementoMoriLaneActive;
    private bool mementoMoriHandsSeen;
    private DateTime mementoMoriActivatedAtUtc;

    private bool grandCrossActive;
    private bool grandCrossCompletedSinceLastTransition;
    private bool grandCrossTransitionWasCasting;
    private bool neutronRingWasCasting;
    private readonly List<GrandCrossLaserPrediction> grandCrossLaserPredictions = [];
    private readonly HashSet<(uint SourceObjectId, ushort TetherId, uint TargetObjectId)> activeGrandCrossTethers = [];
    private readonly HashSet<uint> activeGrandCrossLineCasterObjectIds = [];
    private AetherblightShape pendingAetherblightShape;
    private ulong lastVisibleAetherblightMarkerId;
    private bool aetherblightResolutionWasCasting;

    // This lease is independent from mechanic-specific avoidance ownership. It suppresses only the
    // combat routine's unsafe target chase and must never stop AvoidanceManager's emergency movement.
    private readonly CapabilityManagerHandle necronNavigationMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle prisonHandDodgeMovementHandle = CapabilityManager.CreateNewHandle();
    private bool necronNavigationMovementOwned;
    private bool prisonHandDodgeMovementOwned;
    private bool movingToNecronSafeAnchor;
    private bool movingFromPrisonHandAttack;
    private bool movingThroughPrisonPortal;
    private Vector3? prisonHandDodgeDestination;
    private readonly HashSet<uint> prisonHandObjectIdsSeen = [];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AgelessNecropolis;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        EnemyAction.FearOfDeath,
        EnemyAction.GrandCross,
        EnemyAction.GrandCrossProximity,
        EnemyAction.NeutronRingVisual,
        EnemyAction.DarknessOfEternityVisual,
        EnemyAction.SpreadingFear,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.BlueShockwaveVisual];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetEncounterState();

        // This encounter is fully modeled below. SideStep recognizes several of the same helper casts
        // and would add a second owner for Grand Cross and Aetherblight movement.
        SidestepPlugin.Enabled = false;

        // Fear of Death places two generations of cast-location puddles.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.FearOfDeathPuddleOne or EnemyAction.FearOfDeathPuddleTwo,
            radiusProducer: _ => 3.25f,
            locationProducer: bc => bc.SpellCastInfo.CastLocation);

        // Helper-owned hand attacks are registered individually because their actor headings contain
        // the lane choice; following an ally here can lead the whole group into the next hand.
        AddCastRectangle(EnemyAction.ChokingGrasp, width: 6.0f, length: 24.0f);
        AddCastRectangle(EnemyAction.ColdGrip, width: 12.0f, length: 30.0f);
        AddCastRectangle(EnemyAction.ExistentialDread, width: 24.0f, length: 30.0f);
        AddCastRectangle(EnemyAction.MementoMori, width: 12.0f, length: 37.0f);

        // The prison is a chain of disconnected circular meshes. Select exactly one exterior donut
        // from the player's nearest component so the boundary follows teleports instead of forbidding
        // the next platform or remaining centered on the initial Icy Hands arena.
        AddPrisonPlatformBoundary(segmentIndex: 0, PrisonMainPlatformSafeRadius);
        AddPrisonPlatformBoundary(segmentIndex: 1, PrisonTransferPlatformSafeRadius);
        AddPrisonPlatformBoundary(segmentIndex: 2, PrisonTransferPlatformSafeRadius);
        AddPrisonPlatformBoundary(segmentIndex: 3, PrisonGoalPlatformSafeRadius);

        // Existential Dread exposes only a 700ms helper cast in RB. The long Cold Grip visual tells
        // which two-thirds follow next, so publish that future rectangle during its last two seconds
        // and hand off seamlessly to the actual 44526 cast instead of attempting a cross-arena dodge.
        // Width 27 adds 1.5 yalms per side after the live run was clipped at X=106.44 against the
        // nominal X=105 edge, accounting for the player hitbox and movement latency.
        AddColdGripFollowupPrediction(
            EnemyAction.ColdGripVisualLeftSafe,
            new Vector3(107.0f, 0.0f, 85.0f));
        AddColdGripFollowupPrediction(
            EnemyAction.ColdGripVisualRightSafe,
            new Vector3(93.0f, 0.0f, 85.0f));

        // Unlike the visible Memento Mori cast, its center-lane bleed remains lethal into the hand
        // sequence. This state-backed polygon preserves that mechanic after the cast object clears.
        AvoidanceManager.AddAvoidPolygon(
            condition: () => IsEncounterActive() && mementoMoriLaneActive,
            leashPointProducer: () => ArenaCenter.Necron,
            leashRadius: 60.0f,
            rotationProducer: _ => 0.0f,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => MementoMoriLanePolygon,
            locationProducer: location => location,
            collectionProducer: () => new[] { MementoMoriLaneOrigin },
            priority: AvoidancePriority.High);

        // Blue Shockwave is a wide tank-targeted cone. Non-tanks stay out of the boss-facing cone;
        // the long visual cast is available directly, so this remains deterministic without SideStep.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => IsEncounterActive() && !Core.Me.IsTank(),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.BlueShockwaveVisual,
            leashPointProducer: () => ArenaCenter.Necron,
            leashRadius: 60.0f,
            rotationDegrees: 0.0f,
            radius: 100.0f,
            arcDegrees: 100.0f,
            priority: AvoidancePriority.High);

        // Relentless Reaping stores an in/out shape and later resolves it through these helper casts.
        // Their SpellCastInfo.CastLocation is a point on the telegraph rather than its origin; the
        // live 45181 hit at (107.975, 90.360) confirmed that the damage remains caster-centered.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.AetherblightCircle,
            radiusProducer: _ => AetherblightCircleAvoidRadius,
            locationProducer: bc => bc.Location);
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.AetherblightDonut,
            locationProducer: bc => bc.Location,
            outerRadius: 60.0f,
            innerRadius: AetherblightDonutSafeRadius,
            priority: AvoidancePriority.High);

        // The resolving helpers above cast for only 700ms in the captured RB client. Necron's
        // overhead VFX (normal icon IDs 604/621 for circle and 605/622 for donut) is the earlier,
        // semantic store signal. Activate the stored shape during the five-second Aetherblight cast
        // at Necron's live north-edge location, not the arena center; circle requires moving deeper
        // into the platform while donut requires approaching the boss.
        // The avoidance radii deliberately include two yalms of hitbox, pathing, and latency margin:
        // the live run was hit at 15.67 on the nominal 16-yalm donut boundary and at 17.96 inside the
        // nominal 20-yalm circle after navigation stopped at the exact edge.
        AvoidanceHelpers.AddAvoidDonut(
            () => IsEncounterActive() &&
                  pendingAetherblightShape == AetherblightShape.Circle &&
                  EnemyAction.AetherblightVisualHash.IsCasting(),
            GetAetherblightOrigin,
            outerRadius: AetherblightCircleAvoidRadius,
            innerRadius: 0.0f,
            priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(
            () => IsEncounterActive() &&
                  pendingAetherblightShape == AetherblightShape.Donut &&
                  EnemyAction.AetherblightVisualHash.IsCasting(),
            GetAetherblightOrigin,
            outerRadius: 60.0f,
            innerRadius: AetherblightDonutSafeRadius,
            priority: AvoidancePriority.High);

        // Grand Cross first compresses the rectangular arena into a circle. Keep the transition donut
        // active on the cast itself so movement begins before the wall becomes lethal.
        AvoidanceHelpers.AddAvoidDonut(
            () => IsEncounterActive() && EnemyAction.GrandCrossTransition.IsCasting(),
            () => ArenaCenter.Necron,
            outerRadius: 60.0f,
            innerRadius: GrandCrossSafeRadius,
            priority: AvoidancePriority.High);

        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => IsEncounterActive() && grandCrossActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.GrandCrossPuddle,
            radiusProducer: _ => 3.25f,
            locationProducer: bc => bc.SpellCastInfo.CastLocation);

        // Azure tethers announce one future laser at a time. The collection contains only forecasts
        // inside their activation window; the exact 44534 cast below replaces the next forecast.
        AvoidanceManager.AddAvoidPolygon(
            condition: () => IsEncounterActive() && grandCrossActive,
            leashPointProducer: () => ArenaCenter.Necron,
            leashRadius: 60.0f,
            rotationProducer: prediction => prediction.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: _ => GrandCrossPredictionPolygon,
            locationProducer: _ => ArenaCenter.Necron,
            collectionProducer: GetActiveGrandCrossLaserPredictions,
            priority: AvoidancePriority.High);

        AddCenteredGrandCrossRectangle(EnemyAction.GrandCrossLine, width: 4.0f);
        AddCenteredGrandCrossRectangle(EnemyAction.GrandCrossProximity, width: 10.0f);

        // Looming Specter leaves one lane open; both the visible invitation and its damage cast are
        // included because client timing can expose either one to RB for longer.
        AddCastRectangle(EnemyAction.InvitationVisual, width: 10.0f, length: 36.0f);
        AddCastRectangle(EnemyAction.InvitationHit, width: 10.0f, length: 36.0f);

        // The normal arena is a 36x30 rectangle. A small navigation inset avoids pathing along the
        // fall-off edge; Grand Cross temporarily replaces it with a radius-nine circle.
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => IsEncounterActive() && !grandCrossActive,
            innerWidth: MainArenaSafeWidth,
            innerHeight: MainArenaSafeHeight,
            outerWidth: ArenaBoundaryOuterExtent,
            outerHeight: ArenaBoundaryOuterExtent,
            collectionProducer: () => [ArenaCenter.Necron],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => IsEncounterActive() && grandCrossActive,
            () => ArenaCenter.Necron,
            outerRadius: ArenaBoundaryOuterExtent,
            innerRadius: GrandCrossSafeRadius,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetEncounterState();
        SidestepPlugin.Enabled = true;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Retake exclusive ownership on every tick in case another plugin or a settings reload enabled
        // SideStep after dungeon entry. OnExitDungeonAsync restores SideStep after leaving this duty.
        SidestepPlugin.Enabled = false;

        if (IsPrisonIntermissionActive())
        {
            ReleaseNecronNavigation("Player entered the isolated Icy Hands prison");
            return await HandlePrisonIntermissionAsync();
        }

        ReleasePrisonNavigation();
        ReleasePrisonHandDodge("Player left the isolated Icy Hands prison");

        if (!IsEncounterActive())
        {
            ResetEncounterState();
            return false;
        }

        UpdateMementoMoriState();
        UpdateGrandCrossState();
        UpdateAetherblightState();

        _ = await TankBusterSpells();
        _ = await DamageMitigationSpells();
        return await HandleNecronNavigationAsync();
    }

    private static bool IsEncounterActive()
    {
        return Core.Player.InCombat &&
               WorldManager.ZoneId == (uint)Data.ZoneId.AgelessNecropolis &&
               Core.Player.Location.Distance2D(ArenaCenter.Necron) <= EncounterDetectionRadius;
    }

    private static bool IsPrisonIntermissionActive()
    {
        return WorldManager.ZoneId == (uint)Data.ZoneId.AgelessNecropolis &&
               Core.Player.Location.Y < -100.0f;
    }

    private static void AddPrisonPlatformBoundary(int segmentIndex, float safeRadius)
    {
        AvoidanceHelpers.AddAvoidDonut(
            () => IsPrisonIntermissionActive() && GetCurrentPrisonSegmentIndex() == segmentIndex,
            () => GetCurrentPrisonSegmentCenter(segmentIndex),
            outerRadius: 90.0f,
            innerRadius: safeRadius,
            priority: AvoidancePriority.High);
    }

    private static void AddCastRectangle(uint actionId, float width, float length)
    {
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: bc => bc.CastingSpellId == actionId,
            width: width,
            length: length,
            priority: AvoidancePriority.High);
    }

    private static void AddCenteredGrandCrossRectangle(uint actionId, float width)
    {
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: bc => bc.CastingSpellId == actionId,
            width: width,
            length: 100.0f,
            yOffset: -50.0f,
            locationProducer: _ => ArenaCenter.Necron,
            priority: AvoidancePriority.High);
    }

    private static void AddColdGripFollowupPrediction(uint visualActionId, Vector3 origin)
    {
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: bc =>
                bc.CastingSpellId == visualActionId &&
                bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds <= 2_000.0,
            width: 27.0f,
            length: 30.0f,
            rotationProducer: _ => 0.0f,
            locationProducer: _ => origin,
            priority: AvoidancePriority.High);
    }

    private GrandCrossLaserPrediction[] GetActiveGrandCrossLaserPredictions()
    {
        DateTime now = DateTime.UtcNow;
        return grandCrossLaserPredictions
            .Where(prediction =>
                now >= prediction.ActivationUtc - GrandCrossPredictionLeadTime &&
                now <= prediction.ActivationUtc + GrandCrossPredictionGraceTime)
            .ToArray();
    }

    /// <summary>
    /// Keeps combat scheduling available while the three local hands live, then walks continuously
    /// through each blue portal in the fixed disconnected-platform chain.
    /// </summary>
    /// <returns><see langword="true"/> while DutyMechanic owns post-combat portal movement.</returns>
    private async Task<bool> HandlePrisonIntermissionAsync()
    {
        BattleCharacter[] localHands = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(bc =>
                bc.IsValid &&
                IsIcyHands(bc.BaseId) &&
                bc.Location.Y < -100.0f &&
                bc.Location.Distance2D(Core.Player.Location) <= 50.0f)
            .ToArray();

        foreach (BattleCharacter hand in localHands)
        {
            prisonHandObjectIdsSeen.Add(hand.ObjectId);
        }

        bool livingHandPresent = localHands.Any(hand => hand.IsVisible && hand.IsTargetable && hand.CurrentHealth > 0);
        if (livingHandPresent || prisonHandObjectIdsSeen.Count < 3)
        {
            // The normal prison always contains three hands. Waiting until all three distinct actors
            // have been observed prevents a brief spawn gap from starting portal movement early.
            ReleasePrisonNavigation();
            return await HandlePrisonHandAvoidanceAsync(localHands);
        }

        ReleasePrisonHandDodge("All local Icy Hands are dead");

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // Boundary recovery has priority over the portal approach. Still consume this profile
            // tick so OrderBot cannot inject its unreachable main-arena destination underneath the
            // avoidance movement; portal control resumes after the player is safely inset again.
            await Coroutine.Yield();
            return true;
        }

        int segmentIndex = GetCurrentPrisonSegmentIndex();
        Vector3 destination = GetCurrentPrisonCenter() + PrisonPortalOffsets[segmentIndex];

        Navigator.Stop();
        MovementManager.MoveStop();
        Navigator.PlayerMover.MoveTowards(destination);
        movingThroughPrisonPortal = true;

        // Keep consuming the profile movement tick and holding forward movement until the client
        // relocates the player. Stopping at the destination can leave the player on the floor
        // trigger's rim, while yielding control lets the profile request an impossible cross-island
        // path back to the main arena.
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Dodges Choking Grasp directly on the isolated prison floor, whose disconnected collision mesh
    /// is not represented by RB's avoidance navigator.
    /// </summary>
    /// <param name="localHands">The Icy Hands actors on the player's current prison platform.</param>
    /// <returns>
    /// <see langword="true"/> while direct movement is required; otherwise <see langword="false"/> so
    /// combat actions continue while the movement capability lease holds the chosen safe position.
    /// </returns>
    private async Task<bool> HandlePrisonHandAvoidanceAsync(BattleCharacter[] localHands)
    {
        BattleCharacter[] castingHands = localHands
            .Where(hand => hand.IsValid && hand.CastingSpellId == EnemyAction.ChokingGrasp)
            .ToArray();

        if (castingHands.Length == 0)
        {
            ReleasePrisonHandDodge("No prison Choking Grasp cast is active");
            return false;
        }

        // The normal RB avoid is intentionally not used here: the live run repeatedly reported
        // "Couldn't find SpanRef at start location" at Y=-410. A movement-only lease prevents the
        // combat routine from walking back into a lane without suppressing attacks from a safe point.
        CapabilityManager.Update(
            prisonHandDodgeMovementHandle,
            CapabilityFlags.Movement,
            NecronMovementLeaseMilliseconds,
            "Holding a direct safe point during prison Choking Grasp");
        prisonHandDodgeMovementOwned = true;

        Vector3 platformCenter = GetCurrentPrisonSegmentCenter(0);
        if (!prisonHandDodgeDestination.HasValue ||
            !IsPrisonHandDodgePointSafe(prisonHandDodgeDestination.Value, platformCenter, castingHands))
        {
            prisonHandDodgeDestination = FindPrisonHandDodgeDestination(
                Core.Player.Location,
                platformCenter,
                castingHands);
        }

        Navigator.Stop();
        MovementManager.MoveStop();
        if (!prisonHandDodgeDestination.HasValue)
        {
            // Overlapping lanes should still leave a valid point on the radius-9.5 platform. If an
            // unexpected layout does not, stopping is safer than guessing a direction across its edge.
            Navigator.PlayerMover.MoveStop();
            movingFromPrisonHandAttack = false;
            return false;
        }

        if (Core.Player.Distance2D(prisonHandDodgeDestination.Value) <= PrisonHandDodgeArrivalRadius)
        {
            Navigator.PlayerMover.MoveStop();
            movingFromPrisonHandAttack = false;
            return false;
        }

        Navigator.PlayerMover.MoveTowards(prisonHandDodgeDestination.Value);
        movingFromPrisonHandAttack = true;
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Finds the closest sampled point that remains inside the prison floor and outside every active
    /// forward Choking Grasp lane.
    /// </summary>
    /// <param name="playerLocation">Current player position used to minimize dodge distance.</param>
    /// <param name="platformCenter">Center of the translated prison platform.</param>
    /// <param name="castingHands">Hands whose snapshotted headings define the active lanes.</param>
    /// <returns>A safe world position, or <see langword="null"/> if the captured layout is not present.</returns>
    private static Vector3? FindPrisonHandDodgeDestination(
        Vector3 playerLocation,
        Vector3 platformCenter,
        BattleCharacter[] castingHands)
    {
        if (IsPrisonHandDodgePointSafe(playerLocation, platformCenter, castingHands))
        {
            return playerLocation;
        }

        Vector3? bestDestination = null;
        float bestDistance = float.MaxValue;
        const int ringCount = 4;
        const int pointsPerRing = 48;
        for (int ring = 1; ring <= ringCount; ring++)
        {
            float radius = PrisonHandDodgeMaximumRadius * ring / ringCount;
            for (int point = 0; point < pointsPerRing; point++)
            {
                double angle = 2.0 * Math.PI * point / pointsPerRing;
                Vector3 candidate = new(
                    platformCenter.X + (float)Math.Sin(angle) * radius,
                    platformCenter.Y,
                    platformCenter.Z + (float)Math.Cos(angle) * radius);

                if (!IsPrisonHandDodgePointSafe(candidate, platformCenter, castingHands))
                {
                    continue;
                }

                float distance = playerLocation.Distance2D(candidate);
                if (distance < bestDistance)
                {
                    bestDestination = candidate;
                    bestDistance = distance;
                }
            }
        }

        return bestDestination;
    }

    private static bool IsPrisonHandDodgePointSafe(
        Vector3 location,
        Vector3 platformCenter,
        IEnumerable<BattleCharacter> castingHands)
    {
        return location.Distance2D(platformCenter) <= PrisonHandDodgeMaximumRadius &&
               castingHands.All(hand => !IsInsideChokingGraspLane(location, hand));
    }

    private static bool IsInsideChokingGraspLane(Vector3 location, BattleCharacter hand)
    {
        float deltaX = location.X - hand.Location.X;
        float deltaZ = location.Z - hand.Location.Z;
        float sin = (float)Math.Sin(hand.Heading);
        float cos = (float)Math.Cos(hand.Heading);
        float forward = (deltaX * sin) + (deltaZ * cos);
        float lateral = (deltaX * cos) - (deltaZ * sin);

        // Choking Grasp is a six-yalm-wide, 24-yalm-long forward rectangle. The extra 1.25
        // yalms on each side account for the player's collision radius and direct-movement latency.
        return forward >= -ChokingGraspRearMargin &&
               forward <= ChokingGraspLength + ChokingGraspRearMargin &&
               Math.Abs(lateral) <= ChokingGraspHalfWidthWithMargin;
    }

    private void ReleasePrisonHandDodge(string reason)
    {
        if (movingFromPrisonHandAttack)
        {
            Navigator.PlayerMover.MoveStop();
        }

        movingFromPrisonHandAttack = false;
        prisonHandDodgeDestination = null;
        if (!prisonHandDodgeMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(prisonHandDodgeMovementHandle, CapabilityFlags.Movement, reason);
        prisonHandDodgeMovementOwned = false;
    }

    private static bool IsIcyHands(uint baseId)
    {
        return baseId is
            EnemyObjectId.IcyHandsOne or
            EnemyObjectId.IcyHandsTwo or
            EnemyObjectId.IcyHandsThree or
            EnemyObjectId.IcyHandsFour or
            EnemyObjectId.IcyHandsFive or
            EnemyObjectId.IcyHandsSix;
    }

    private static Vector3 GetAetherblightOrigin()
    {
        BattleCharacter necron = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => bc.IsValid && bc.BaseId == EnemyObjectId.Necron);

        return necron?.Location ?? AetherblightOriginFallback;
    }

    private static Vector3 GetCurrentPrisonCenter()
    {
        Vector3 playerLocation = Core.Player.Location;
        Vector3 nearest = PrisonCenters[0];
        float nearestDistance = playerLocation.Distance2D(nearest);
        for (int index = 1; index < PrisonCenters.Length; index++)
        {
            float distance = playerLocation.Distance2D(PrisonCenters[index]);
            if (distance < nearestDistance)
            {
                nearest = PrisonCenters[index];
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private static int GetCurrentPrisonSegmentIndex()
    {
        Vector3 prisonCenter = GetCurrentPrisonCenter();
        Vector3 playerLocation = Core.Player.Location;
        int nearestIndex = 0;
        float nearestDistance = playerLocation.Distance2D(prisonCenter + PrisonPlatformOffsets[0]);
        for (int index = 1; index < PrisonPlatformOffsets.Length; index++)
        {
            float distance = playerLocation.Distance2D(prisonCenter + PrisonPlatformOffsets[index]);
            if (distance < nearestDistance)
            {
                nearestIndex = index;
                nearestDistance = distance;
            }
        }

        return nearestIndex;
    }

    private static Vector3 GetCurrentPrisonSegmentCenter(int segmentIndex)
    {
        return GetCurrentPrisonCenter() + PrisonPlatformOffsets[segmentIndex];
    }

    private void ReleasePrisonNavigation()
    {
        if (movingThroughPrisonPortal && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        movingThroughPrisonPortal = false;
    }

    /// <summary>
    /// Prevents service navigation from chasing Necron's off-platform actor while leaving verified
    /// avoidance movement and the combat routine's actions schedulable.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only while DutyMechanic is actively moving a melee job or recovering an
    /// out-of-bounds player; otherwise <see langword="false"/> so healing and rotation continue.
    /// </returns>
    private async Task<bool> HandleNecronNavigationAsync()
    {
        BattleCharacter necron = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc =>
                bc.IsValid &&
                bc.IsVisible &&
                bc.IsTargetable &&
                bc.BaseId == EnemyObjectId.Necron);

        if (necron == null)
        {
            ReleaseNecronNavigation("Necron is not a targetable off-platform movement owner");
            return false;
        }

        CapabilityManager.Update(
            necronNavigationMovementHandle,
            CapabilityFlags.Movement,
            NecronMovementLeaseMilliseconds,
            "Preventing combat-routine navigation to off-platform Necron");
        necronNavigationMovementOwned = true;

        // AvoidanceManager directly owns emergency egress. Do not cancel or replace its destination;
        // the safe anchor is reacquired on the next tick if the boss remains targetable.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        Vector3 playerLocation = Core.Player.Location;
        Vector3? destination = null;

        if (grandCrossActive)
        {
            destination = ArenaCenter.Necron;
        }
        else if (Core.Player.IsMelee())
        {
            destination = NecronMeleeAnchor;
        }
        else if (!IsInsideNavigationSafeArea(playerLocation))
        {
            // Clamp ranged jobs back inside a stronger inset than the general arena outline. This is
            // recovery-only; ranged jobs already inside the inset are held where mechanics left them.
            destination = new Vector3(
                Math.Clamp(playerLocation.X, ArenaCenter.Necron.X - NavigationSafeHalfWidth, ArenaCenter.Necron.X + NavigationSafeHalfWidth),
                0.0f,
                Math.Clamp(playerLocation.Z, NavigationSafeMinimumZ, NavigationSafeMaximumZ));
        }

        if (!destination.HasValue || Core.Player.Distance2D(destination.Value) <= 1.0f)
        {
            // Cancel any service-navigation path that was already active before this tick. The
            // capability lease prevents Magitek from immediately recreating the off-platform path.
            Navigator.Stop();
            Navigator.PlayerMover.MoveStop();
            MovementManager.MoveStop();
            movingToNecronSafeAnchor = false;
            return false;
        }

        Navigator.Stop();
        MovementManager.MoveStop();
        Navigator.PlayerMover.MoveTowards(destination.Value);
        movingToNecronSafeAnchor = true;
        await Coroutine.Yield();
        return true;
    }

    private static bool IsInsideNavigationSafeArea(Vector3 location)
    {
        return Math.Abs(location.X - ArenaCenter.Necron.X) <= NavigationSafeHalfWidth &&
               location.Z >= NavigationSafeMinimumZ &&
               location.Z <= NavigationSafeMaximumZ;
    }

    private void ReleaseNecronNavigation(string reason)
    {
        if (movingToNecronSafeAnchor && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        movingToNecronSafeAnchor = false;
        if (!necronNavigationMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(necronNavigationMovementHandle, CapabilityFlags.Movement, reason);
        necronNavigationMovementOwned = false;
    }

    private void UpdateMementoMoriState()
    {
        bool isCasting = EnemyAction.MementoMoriHash.IsCasting();
        if (isCasting && !mementoMoriWasCasting)
        {
            mementoMoriLaneActive = true;
            mementoMoriHandsSeen = false;
            mementoMoriActivatedAtUtc = DateTime.UtcNow;
        }

        mementoMoriWasCasting = isCasting;
        if (!mementoMoriLaneActive)
        {
            return;
        }

        bool icyHandsVisible = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(bc => bc.BaseId == EnemyObjectId.IcyHandsOne && bc.IsVisible);

        mementoMoriHandsSeen |= icyHandsVisible;
        if ((mementoMoriHandsSeen && !icyHandsVisible) || DateTime.UtcNow - mementoMoriActivatedAtUtc > MementoMoriSafetyTimeout)
        {
            ClearMementoMoriState();
        }
    }

    private void UpdateAetherblightState()
    {
        BattleCharacter necron = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc => bc.IsValid && bc.BaseId == EnemyObjectId.Necron);

        ulong markerId = 0;
        if (necron != null && necron.VfxContainer.IsValid)
        {
            markerId = necron.VfxContainer.Vfx
                .Where(vfx => vfx != null && vfx.IsValid)
                .Select(vfx => Convert.ToUInt64(vfx.Id))
                .FirstOrDefault(PlayerVfx.IsAetherblightMarker);
        }

        if (markerId != 0 && markerId != lastVisibleAetherblightMarkerId)
        {
            pendingAetherblightShape = PlayerVfx.IsAetherblightCircle(markerId)
                ? AetherblightShape.Circle
                : AetherblightShape.Donut;
        }

        lastVisibleAetherblightMarkerId = markerId;

        bool resolutionCasting = EnemyAction.AetherblightResolutionHash.IsCasting();
        if (aetherblightResolutionWasCasting && !resolutionCasting)
        {
            pendingAetherblightShape = AetherblightShape.None;
        }

        aetherblightResolutionWasCasting = resolutionCasting;
    }

    private void UpdateGrandCrossState()
    {
        bool transitionCasting = EnemyAction.GrandCrossTransition.IsCasting();
        if (transitionCasting && !grandCrossTransitionWasCasting)
        {
            grandCrossActive = true;
            grandCrossCompletedSinceLastTransition = false;
            ClearGrandCrossLaserState();
        }

        grandCrossTransitionWasCasting = transitionCasting;

        // Azure actors and phase-only helper casts allow recovery if the plugin was enabled after the
        // transition cast. Once Neutron Ring completes, they cannot reactivate a stale circular wall.
        bool phaseEvidenceVisible = EnemyAction.GrandCrossPhaseActions.IsCasting() ||
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Any(bc => bc.IsVisible && bc.BaseId is EnemyObjectId.AzureAetherOne or EnemyObjectId.AzureAetherTwo);

        if (phaseEvidenceVisible && !grandCrossCompletedSinceLastTransition)
        {
            grandCrossActive = true;
        }

        if (grandCrossActive)
        {
            BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(bc => bc.IsValid && bc.IsVisible)
                .ToArray();

            // VfxContainer exposes the same tether lifecycle that announces each forecast. A key is
            // retained only while that tether is present, allowing the same Azure actor and tether ID
            // to re-arm after it disappears without sampling the actor's continuously changing angle.
            var observedTethers = new HashSet<(uint SourceObjectId, ushort TetherId, uint TargetObjectId)>();
            foreach (BattleCharacter azureAether in actors.Where(bc =>
                         bc.BaseId is EnemyObjectId.AzureAetherOne or EnemyObjectId.AzureAetherTwo))
            {
                if (!azureAether.VfxContainer.IsValid)
                {
                    continue;
                }

                foreach (var tether in azureAether.VfxContainer.Tethers.Where(tether => tether.TargetId != 0))
                {
                    var key = (azureAether.ObjectId, tether.Id, tether.TargetId);
                    observedTethers.Add(key);
                    if (!activeGrandCrossTethers.Add(key))
                    {
                        continue;
                    }

                    float offset = azureAether.BaseId == EnemyObjectId.AzureAetherOne
                        ? AzureAetherOneRotationOffset
                        : AzureAetherTwoRotationOffset;
                    TimeSpan delay = azureAether.BaseId == EnemyObjectId.AzureAetherOne
                        ? GrandCrossAzureAetherOneDelay
                        : GrandCrossAzureAetherTwoDelay;
                    grandCrossLaserPredictions.Add(new GrandCrossLaserPrediction(
                        rotation: -(MathEx.CalculateNeededFacing(ArenaCenter.Necron, azureAether.Location) + offset),
                        activationUtc: DateTime.UtcNow + delay));
                }
            }

            activeGrandCrossTethers.RemoveWhere(key => !observedTethers.Contains(key));
            grandCrossLaserPredictions.Sort((left, right) => left.ActivationUtc.CompareTo(right.ActivationUtc));

            // The real 44534 cast is the authoritative geometry. Consume exactly one queued forecast
            // on each cast edge so a fired line cannot remain forbidden during later puddles or lasers.
            var currentLineCasterObjectIds = actors
                .Where(bc => bc.CastingSpellId == EnemyAction.GrandCrossLine)
                .Select(bc => bc.ObjectId)
                .ToHashSet();
            foreach (uint objectId in currentLineCasterObjectIds.Where(id => activeGrandCrossLineCasterObjectIds.Add(id)))
            {
                if (grandCrossLaserPredictions.Count != 0)
                {
                    grandCrossLaserPredictions.RemoveAt(0);
                }
            }

            activeGrandCrossLineCasterObjectIds.RemoveWhere(id => !currentLineCasterObjectIds.Contains(id));

            // A missing cast-end observation must not recreate the original phase-long wall. The
            // exact cast registration still handles any line that arrives after this grace period.
            DateTime staleBefore = DateTime.UtcNow - GrandCrossPredictionGraceTime;
            grandCrossLaserPredictions.RemoveAll(prediction => prediction.ActivationUtc < staleBefore);
        }

        bool neutronRingCasting = EnemyAction.NeutronRingVisualHash.IsCasting();
        if (neutronRingWasCasting && !neutronRingCasting)
        {
            grandCrossActive = false;
            grandCrossCompletedSinceLastTransition = true;
            ClearGrandCrossLaserState();
        }

        neutronRingWasCasting = neutronRingCasting;
    }

    private void ClearGrandCrossLaserState()
    {
        grandCrossLaserPredictions.Clear();
        activeGrandCrossTethers.Clear();
        activeGrandCrossLineCasterObjectIds.Clear();
    }

    private void ResetEncounterState()
    {
        ReleaseNecronNavigation("Ageless Necropolis encounter state reset");
        ReleasePrisonNavigation();
        ReleasePrisonHandDodge("Ageless Necropolis encounter state reset");
        prisonHandObjectIdsSeen.Clear();
        ClearMementoMoriState();
        grandCrossActive = false;
        grandCrossCompletedSinceLastTransition = false;
        grandCrossTransitionWasCasting = false;
        neutronRingWasCasting = false;
        ClearGrandCrossLaserState();
        pendingAetherblightShape = AetherblightShape.None;
        lastVisibleAetherblightMarkerId = 0;
        aetherblightResolutionWasCasting = false;
    }

    private void ClearMementoMoriState()
    {
        mementoMoriWasCasting = false;
        mementoMoriLaneActive = false;
        mementoMoriHandsSeen = false;
        mementoMoriActivatedAtUtc = default;
    }

    private static class EnemyObjectId
    {
        // These are BNpcBase/Object IDs, not the similarly named BNpcName IDs used by GetObjectsByNPCId.
        public const uint Necron = 0x4870;
        public const uint IcyHandsOne = 0x4903;
        public const uint IcyHandsTwo = 0x4908;
        public const uint IcyHandsThree = 0x4904;
        public const uint IcyHandsFour = 0x4905;
        public const uint IcyHandsFive = 0x4906;
        public const uint IcyHandsSix = 0x4909;
        public const uint AzureAetherOne = 0x4948;
        public const uint AzureAetherTwo = 0x490A;
    }

    private enum AetherblightShape
    {
        None,
        Circle,
        Donut,
    }

    /// <summary>
    /// Stores the stable tether-time geometry and one-shot activation for a future Grand Cross line.
    /// Keeping only scalar data avoids retaining frame-scoped actor wrappers after the preview moves.
    /// </summary>
    private sealed class GrandCrossLaserPrediction
    {
        public GrandCrossLaserPrediction(float rotation, DateTime activationUtc)
        {
            Rotation = rotation;
            ActivationUtc = activationUtc;
        }

        public float Rotation { get; }

        public DateTime ActivationUtc { get; }
    }

    private static class PlayerVfx
    {
        // Normal-mode target icons captured by the current BossMod T05 module. They appear on
        // Necron early enough to preposition for helper casts 45181/45182.
        private const ulong AetherblightCircleOne = 604;
        private const ulong AetherblightCircleTwo = 621;
        private const ulong AetherblightDonutOne = 605;
        private const ulong AetherblightDonutTwo = 622;

        public static bool IsAetherblightMarker(ulong id) =>
            IsAetherblightCircle(id) || id is AetherblightDonutOne or AetherblightDonutTwo;

        public static bool IsAetherblightCircle(ulong id) =>
            id is AetherblightCircleOne or AetherblightCircleTwo;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Gets the fixed center of Necron's normal-mode arena.
        /// </summary>
        public static readonly Vector3 Necron = new(100.0f, 0.0f, 100.0f);
    }

    private static class EnemyAction
    {
        public const uint FearOfDeath = 44521;
        public const uint FearOfDeathPuddleOne = 44522;
        public const uint ChokingGrasp = 44523;
        public const uint ColdGripVisualLeftSafe = 44524;
        public const uint ColdGripVisualRightSafe = 44525;
        public const uint ExistentialDread = 44526;
        public const uint Aetherblight = 44528;
        public const uint MementoMori = 44532;
        public const uint GrandCross = 44533;
        public const uint GrandCrossLine = 44534;
        public const uint GrandCrossProximity = 44535;
        public const uint GrandCrossPuddle = 44536;
        public const uint NeutronRingVisual = 44538;
        public const uint FearOfDeathPuddleTwo = 44540;
        public const uint DarknessOfEternityVisual = 44541;
        public const uint InvitationVisual = 44545;
        public const uint BlueShockwaveVisual = 44546;
        public const uint SpreadingFear = 44549;
        public const uint GrandCrossArenaTransition = 44603;
        public const uint ColdGrip = 44611;
        public const uint InvitationHit = 44817;
        public const uint AetherblightCircle = 45181;
        public const uint AetherblightDonut = 45182;

        // Hash sets reuse the repository's cast lookup extension and keep phase-state tests allocation-free.
        public static readonly HashSet<uint> MementoMoriHash = [MementoMori];
        public static readonly HashSet<uint> AetherblightVisualHash = [Aetherblight];
        public static readonly HashSet<uint> AetherblightResolutionHash = [AetherblightCircle, AetherblightDonut];
        public static readonly HashSet<uint> GrandCrossTransition = [GrandCross, GrandCrossArenaTransition];
        public static readonly HashSet<uint> GrandCrossPhaseActions =
        [
            GrandCrossLine,
            GrandCrossProximity,
            GrandCrossPuddle,
            NeutronRingVisual,
        ];
        public static readonly HashSet<uint> NeutronRingVisualHash = [NeutronRingVisual];
    }
}
