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
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 95: Skydeep Cenote dungeon logic.
/// </summary>
public class SkydeepCenote : AbstractDungeon
{
    private const float CaptureRadius = 60f;
    private static readonly TimeSpan PlayerCaptureInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CaptureErrorInterval = TimeSpan.FromSeconds(10);

    // Feather Ray uses conventional geometry for its cast-time attacks, but Rolling Current is a
    // semantic exception: the 68x32 helper only moves large bubbles eight yalms and never damages
    // the player. The six-yalm explosions at their destinations are the actual hazards. These values
    // are corroborated by the live RB casts and both BossMod implementations inspected for Skydeep.
    private const float FeatherRayWorrisomeWaveRadius = 24f;
    private const float FeatherRayWorrisomeWaveArcDegrees = 30f;
    // Feather Ray's normal platform is a 30x30 square. Keep the same half-yalm wall inset used by
    // the other rectangular Skydeep arenas so navigation can use the corners without targeting
    // positions on the fall boundary itself.
    private const float FeatherRayNormalArenaSize = 29f;
    private const float FeatherRayHydroRingInnerRadius = 12f;
    private const float FeatherRayHydroRingOuterRadius = 24f;
    // RB does not expose Hydro Ring's map-effect clear event to this handler. The moving bubbles
    // are its observable lifetime owner; a short absence grace prevents a single visibility-frame
    // gap between bubbles from restoring the rectangular boundary while the water wall is active.
    private static readonly TimeSpan FeatherRayArenaRestoreGrace = TimeSpan.FromSeconds(1.5);
    private const float FeatherRayBurstRadius = 6f;
    private const float FeatherRayRollingCurrentBubbleOffset = 8f;
    private const float FeatherRayLargeBubbleMinimumCombatReach = 1.5f;
    private const float FeatherRaySmallBubbleHitboxRadius = 1.1f;
    // Small moving bubbles have a captured 1.1-yalm hitbox. Model one swept box from the rear of
    // that hitbox through the next five yalms of travel; registering a second current-position
    // circle made the avoidance solver alternate between overlapping owners during dense waves.
    private const float FeatherRaySmallBubblePredictionLength = 5f;
    private const float FeatherRayNuisanceSpreadRadius = 6f;
    // Nuisance's copied cone is 30 degrees wide. Sampling every five degrees is sufficient to
    // choose the center of the widest party-free angular gap without assuming a fixed formation.
    private const int FeatherRayNuisanceFacingSamples = 72;
    private const int FeatherRayNuisanceFacingLeaseMilliseconds = 750;
    private const float FeatherRayNuisanceFacingTargetDistance = 10f;

    // Emergent Artillery divides Firearms' 40-yalm arena into a 4x4 grid. Live capture shows
    // hidden helpers at the center of every unsafe 10x10 tile; the recording confirms that the
    // unoccupied helper slots, rather than any action-ID subtype, are the safe cells.
    private const float FirearmsArtilleryTileSize = 10f;

    // Thunderlight Burst traces an eight-yalm-wide laser through one or more mirrors, then detonates
    // the struck edge orb in a 35-yalm circle. Live helper headings match the late orange telegraphs;
    // BossMod independently records the four segment lengths and the same terminal circle radius.
    private const float FirearmsThunderlightBurstWidth = 8f;
    private const float FirearmsThunderlightBurstCircleRadius = 35f;

    // Maulskull's Impact helpers pair a ten-yalm lethal circle with a radial knockback. A normal
    // circle avoid exits by the shortest route, which live verification showed can put the player
    // due north of the origin and knock them past Z=-410. Keep a one-yalm computed safe disk for
    // every Impact variant: live Viper verification showed that even the former 1.25-yalm
    // Skullcrush tolerance survived, while the two-yalm Ringing/Colossal tolerance still allowed
    // enough angular drift to make the resulting landing less predictable. A nonzero disk lets RB
    // settle without requiring an exact floating-point coordinate while keeping all knockbacks tight.
    private const float MaulskullArenaHalfExtent = 20f;
    private const float MaulskullLandingInset = 1f;
    private const float MaulskullImpactPrepositionRadius = 13f;
    private const float MaulskullImpactPositionTolerance = 1f;
    private const float MaulskullRingingLandingSafetyMargin = 1f;
    private const int MaulskullKnockbackDirectionSamples = 360;

    // These dimensions are full widths/lengths for DutyMechanic's rectangle helper. They
    // are corroborated by the captured effect ranges and BossMod's half-width geometry. Maulwork's
    // side casters report Heading=0 in RB even though the network cast rotations are fixed at
    // -17/+17 degrees, so preserve the action-specific rotations instead of trusting Heading.
    private const float MaulskullStonecarverWidth = 20f;
    private const float MaulskullStonecarverLength = 40f;
    private const float MaulskullLandingRadius = 8f;
    private const float MaulskullShatterCenterWidth = 20f;
    private const float MaulskullShatterCenterLength = 40f;
    private const float MaulskullShatterSideWidth = 22f;
    private const float MaulskullShatterSideLength = 45f;
    private const float MaulskullShatterSideRotation = 17f * (float)(Math.PI / 180d);
    private const float MaulskullStonecarverTransitionInset = 3f;
    private const float MaulskullStonecarverTransitionTolerance = 1f;
    private const float MaulskullTowerPositionTolerance = 1.5f;
    private const float MaulskullStackPositionTolerance = 2f;
    // Destructive Heat is a six-yalm spread that starts while duty-support members are still
    // repositioning after Impact. Pick from a stable in-bounds ring, require one extra yalm of
    // separation, and rescore only when another member actually invalidates the latched point.
    private const float MaulskullDestructiveHeatSpreadRadius = 6f;
    private const float MaulskullDestructiveHeatMinimumSeparation = 7f;
    private const float MaulskullDestructiveHeatDestinationRadius = 15f;
    private const float MaulskullDestructiveHeatPositionTolerance = 1.5f;
    private const int MaulskullDestructiveHeatDirectionSamples = 72;
    // Landing's cast completion owns the damaging rock impact, but RB can declare its current
    // escape complete earlier. Keep combat-routine movement suppressed for a short visual grace
    // after the cast so it cannot immediately path back under a rock while the client resolves it.
    private static readonly TimeSpan MaulskullLandingMovementGrace = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    // Skydeep had no complete action/geometry capture when this handler was introduced. These
    // encounter-local caches support passive observation of friendly and hidden helper actors
    // without enabling SideStep or adding avoidance from unverified action semantics.
    private readonly Dictionary<uint, uint> capturedCasts = [];
    private readonly Dictionary<uint, string> capturedActorStates = [];
    private readonly Dictionary<uint, HashSet<ulong>> capturedVfx = [];
    private SubZoneId capturedSubZoneId = SubZoneId.NONE;
    private DateTime nextPlayerCaptureUtc = DateTime.MinValue;
    private DateTime nextCaptureErrorUtc = DateTime.MinValue;
    private bool? capturedPlayerAlive;
    private bool featherRayHydroRingActive;
    private bool featherRayHydroRingObservedSmallBubbles;
    private DateTime featherRayLastSmallBubbleSeenUtc = DateTime.MinValue;
    // Facing gets its own capability handle so releasing Nuisance cannot clear movement ownership
    // held by another encounter behavior through AbstractDungeon.CapabilityHandle.
    private readonly CapabilityManagerHandle featherRayNuisanceFacingHandle = CapabilityManager.CreateNewHandle();
    // Deep Thunder needs to suppress only combat-routine movement after the player reaches its
    // positive-position tower. A dedicated handle lets the helper-expiry cleanup release that hold
    // without clearing Impact, Ringing Blows, or Landing leases stored on CapabilityHandle.
    private readonly CapabilityManagerHandle maulskullDeepThunderMovementHandle = CapabilityManager.CreateNewHandle();
    // Destructive Heat uses the same scheduling split as Deep Thunder: encounter movement reaches
    // and holds a semantic destination while the combat routine remains free to attack or mitigate.
    private readonly CapabilityManagerHandle maulskullDestructiveHeatMovementHandle = CapabilityManager.CreateNewHandle();
    // Worrisome Wave needs a spread only until its copied cones resolve. Keep the exact avoid handle
    // encounter-local so Trouble Bubbles and aura consumption can remove it immediately instead of
    // inheriting MovementHelpers.Spread's duration, which RB reported as an incorrect 60 seconds.
    private AvoidInfo featherRayNuisanceSpreadInfo;
    private bool featherRayNuisanceConeArmed;
    private bool featherRayNuisanceFacingOwned;
    private bool maulskullDeepThunderMovementOwned;
    private bool maulskullDestructiveHeatMovementOwned;
    private Vector3? maulskullDestructiveHeatDestination;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.SkydeepCenote;

    /// <inheritdoc/>
    // Skydeep's former follow entries now have encounter-local geometry. Following a duty-support
    // NPC during Rolling Current reacts to party movement rather than the future Burst locations.
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        // Only the 1.1-reach Airy Bubbles are moving collision hazards. Actor heading tracks travel,
        // so one rectangle covers the full captured 2.2-yalm width from 1.1 yalms behind the actor
        // through five yalms ahead. A single swept shape retains narrow side lanes and avoids the
        // sub-second capability churn observed when a circle and forward rectangle overlapped.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsFeatherRayCombat,
            objectSelector: IsVisibleSmallFeatherRayBubble,
            width: FeatherRaySmallBubbleHitboxRadius * 2f,
            length: FeatherRaySmallBubblePredictionLength + FeatherRaySmallBubbleHitboxRadius,
            yOffset: -FeatherRaySmallBubbleHitboxRadius,
            priority: AvoidancePriority.High);

        // Worrisome Wave is the boss's visible 30-degree cone. Troublesome Tail is an unavoidable
        // raidwide that grants Nuisance; FeatherRay() separately spreads the party so the mirrored
        // player cones do not overlap after this telegraph.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsFeatherRayCombat,
            objectSelector: actor => actor.NpcId == EnemyNpc.FeatherRay && actor.CastingSpellId == EnemyAction.WorrisomeWave,
            leashPointProducer: () => ArenaCenter.FeatherRay,
            leashRadius: 30f,
            rotationDegrees: 0f,
            radius: FeatherRayWorrisomeWaveRadius,
            arcDegrees: FeatherRayWorrisomeWaveArcDegrees,
            priority: AvoidancePriority.High);

        // Lock-on 514 makes every party member repeat Worrisome Wave from their live position and
        // heading. The 2026-08-19 capture recorded the player holding still outside the six-yalm
        // spread but taking 48,636 damage from Wuk Lamat's copied cone, proving that separation and
        // local facing alone are insufficient. Model the other marked actors with the boss's same
        // confirmed 24-yalm/30-degree geometry so avoidance can move first; FeatherRay() waits for
        // that egress to finish before stopping and aiming the player's own copied cone.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => IsFeatherRayCombat() && featherRayNuisanceConeArmed && HasFeatherRayNuisanceLockOn(),
            objectSelector: IsOtherFeatherRayNuisanceConeSource,
            leashPointProducer: () => ArenaCenter.FeatherRay,
            leashRadius: 30f,
            rotationDegrees: 0f,
            radius: FeatherRayWorrisomeWaveRadius,
            arcDegrees: FeatherRayWorrisomeWaveArcDegrees,
            priority: AvoidancePriority.High);

        // Hydro Ring leaves only the inner twelve yalms safe while the 12-24 band resolves.
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: IsFeatherRayCombat,
            objectSelector: actor => actor.NpcId == EnemyNpc.FeatherRay && actor.CastingSpellId == EnemyAction.HydroRing,
            outerRadius: FeatherRayHydroRingOuterRadius,
            innerRadius: FeatherRayHydroRingInnerRadius,
            priority: AvoidancePriority.High);

        // RB exposes the individual Burst casts very late in their 1.5-second lifetime, so retain
        // an exact cast-attached circle as the final handoff but also predict the destinations from
        // Rolling Current below. Burst is no longer delegated to arbitrary NPC-follow movement.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsFeatherRayCombat,
            objectSelector: actor => actor.NpcId == EnemyNpc.AiryBubble && actor.CastingSpellId == EnemyAction.Burst,
            radiusProducer: _ => FeatherRayBurstRadius,
            priority: AvoidancePriority.High));

        AvoidanceManager.AddAvoidLocation(
            canRun: () => IsFeatherRayCombat() && GetActiveFeatherRayRollingCurrentCaster() != null,
            radiusProducer: _ => FeatherRayBurstRadius,
            locationProducer: location => location,
            collectionProducer: GetPredictedFeatherRayBurstLocations);

        // Feather Ray begins on the full rectangular platform. Hydro Ring creates a circular water
        // wall, so these boundaries must be mutually exclusive; applying the circle for the whole
        // encounter unnecessarily discards all four safe corners before and after the wall exists.
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => IsFeatherRayCombat() && !featherRayHydroRingActive,
            innerWidth: FeatherRayNormalArenaSize,
            innerHeight: FeatherRayNormalArenaSize,
            outerWidth: 90f,
            outerHeight: 90f,
            collectionProducer: () => [ArenaCenter.FeatherRay],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => IsFeatherRayCombat() && featherRayHydroRingActive,
            () => ArenaCenter.FeatherRay,
            outerRadius: 90f,
            innerRadius: FeatherRayHydroRingInnerRadius,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.VurgarMettlegrounds,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Firearms],
            priority: AvoidancePriority.High);

        // RawCastType 12 only establishes that Artillery is a helper telegraph; it does not give
        // RebornBuddy square geometry. The video/log pairing proves each helper location is the
        // center of one axis-aligned 10x10 grid cell, so use centered rectangles and let missing
        // helpers naturally remain traversable. This also supports later one-safe-cell patterns.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.VurgarMettlegrounds,
            objectSelector: actor => actor.NpcId == EnemyNpc.Firearms && EnemyAction.Artillery.Contains(actor.CastingSpellId),
            width: FirearmsArtilleryTileSize,
            length: FirearmsArtilleryTileSize,
            yOffset: -FirearmsArtilleryTileSize / 2f,
            rotationProducer: _ => 0f,
            priority: AvoidancePriority.High);

        // Each reflected laser helper starts at an arena edge and faces along the next segment.
        // AddAvoidRectangle's default -Heading rotation projects forward from that helper, matching
        // the captured lines; keep separate registrations because each action has a distinct length.
        AddFirearmsThunderlightBurstRectangle(EnemyAction.ThunderlightBurstRect1, 42f);
        AddFirearmsThunderlightBurstRectangle(EnemyAction.ThunderlightBurstRect2, 49f);
        AddFirearmsThunderlightBurstRectangle(EnemyAction.ThunderlightBurstRect3, 35f);
        AddFirearmsThunderlightBurstRectangle(EnemyAction.ThunderlightBurstRect4, 36f);

        // The orb helper is a point-blank circle, not another reflected line. Registering it from
        // the helper cast exposes the safe opposite corner several seconds before the late telegraph.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.VurgarMettlegrounds,
            objectSelector: actor => actor.NpcId == EnemyNpc.Firearms && actor.CastingSpellId == EnemyAction.ThunderlightBurstCircle,
            radiusProducer: _ => FirearmsThunderlightBurstCircleRadius,
            priority: AvoidancePriority.High));

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GatekeepsAnvil,
            innerWidth: 35.0f,
            innerHeight: 35.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Maulskull],
            priority: AvoidancePriority.High);

        // Impact must own a destination, not merely repel the player from its inner circle. The
        // two-yalm safe disk is large enough for RB to settle without oscillating; centering it 13
        // yalms from the helper keeps its nearest edge 11 yalms from the ten-yalm Skullcrush AOE.
        // Register once so repeated Ringing Blows casts cannot leak permanent avoidance entries.
        AvoidanceHelpers.AddAvoidDonut(
            () => IsMaulskullCombat() && EnemyAction.Impact.IsCasting(),
            () => GetMaulskullKnockbackPreposition(GetActiveMaulskullImpactCaster()),
            outerRadius: 90f,
            innerRadius: MaulskullImpactPositionTolerance,
            priority: AvoidancePriority.High);

        // Stonecarver's helpers are 20-by-40 rectangular half-arena cleaves, not positive-position
        // markers. They resolve sequentially, so activating both rectangles at once would falsely
        // cover the whole platform; expose only the helper with the least cast time remaining and
        // let the second rectangle take over after the first cast ends.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsMaulskullCombat,
            objectSelector: actor => actor.ObjectId == GetFirstMaulskullStonecarverCaster()?.ObjectId,
            width: MaulskullStonecarverWidth,
            length: MaulskullStonecarverLength,
            priority: AvoidancePriority.High);

        // Destructive Heat targets all four members with six-yalm circles. Keep geometric egress
        // live around the other three members while Maulskull() supplies a stable positive spread
        // destination; delaying both until Impact ends preserves the verified knockback positioning.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => IsMaulskullCombat() &&
                          EnemyAction.DestructiveHeat.IsCasting() &&
                          !EnemyAction.Impact.IsCasting() &&
                          !EnemyAction.ColossalImpact.IsCasting(),
            objectSelector: IsOtherLivingPartyMember,
            radiusProducer: _ => MaulskullDestructiveHeatSpreadRadius);

        // Maulwork drops several independent eight-yalm circles per wave. Their helper locations
        // match CastLocation in the live capture, so every active helper is a separate hazard.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsMaulskullCombat,
            objectSelector: actor => actor.NpcId == EnemyNpc.Maulskull && actor.CastingSpellId == EnemyAction.Landing,
            radiusProducer: _ => MaulskullLandingRadius,
            priority: AvoidancePriority.High));

        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            // Landing and Shatter overlap briefly. The completed 2026-08-19 pull showed RB choose
            // a Shatter escape path through a still-active rock circle and take the Landing hit.
            // Shatter retains about two seconds after Landing resolves, so defer this rectangle
            // until the rocks impact instead of making the avoidance solver satisfy both at once.
            canRun: () => IsMaulskullCombat() && GetLastMaulskullLandingCaster() == null,
            objectSelector: actor => actor.NpcId == EnemyNpc.Maulskull && actor.CastingSpellId == EnemyAction.ShatterCenter,
            width: MaulskullShatterCenterWidth,
            length: MaulskullShatterCenterLength,
            priority: AvoidancePriority.High);

        // RB exposes both side helpers with Heading=0. BossModReborn records their actual fixed cast
        // rotations as -17 degrees for the west helper and +17 for the east helper. Polygon rotation
        // is the inverse of FFXIV heading in AddAvoidRectangle, hence the signs below.
        AddMaulskullShatterSideRectangle(EnemyAction.ShatterSideWest, MaulskullShatterSideRotation);
        AddMaulskullShatterSideRectangle(EnemyAction.ShatterSideEast, -MaulskullShatterSideRotation);

        // Wrought Fire targets the tank with a seven-yalm splash. Register this dynamic avoid once:
        // adding it from Maulskull() every tick leaked permanent avoid entries (74 in one live pull).
        // Non-tanks avoid the current target; the tank uses the encounter-local spread movement below.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat &&
                          WorldManager.SubZoneId == (uint)SubZoneId.GatekeepsAnvil &&
                          !Core.Player.IsTank() &&
                          EnemyAction.WroughtFire.IsCasting(),
            objectSelector: bc => EnemyAction.WroughtFire.Contains(bc.CastingSpellId) && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => 7f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        // The evidence capture remains available for scoped investigations, but keep it disabled
        // during normal farming because actor and player snapshots generate high-volume RB logs.
        // CaptureEncounterState(currentSubZoneId);
        UpdateFeatherRayArenaState();

        await FollowDodgeSpells();

        if (WorldManager.SubZoneId is (uint)SubZoneId.UnsungElegy or (uint)SubZoneId.VurgarMettlegrounds or (uint)SubZoneId.GatekeepsAnvil)
        {
            SidestepPlugin.Enabled = false;
        }
        else
        {
            SidestepPlugin.Enabled = true;
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.UnsungElegy => await FeatherRay(),
            SubZoneId.VurgarMettlegrounds => await Firearms(),
            SubZoneId.GatekeepsAnvil => await Maulskull(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Records passive, bot-thread snapshots for evidence-first Skydeep mechanic analysis.
    /// </summary>
    /// <param name="currentSubZoneId">The current sub-zone used to scope capture to a boss arena.</param>
    /// <remarks>
    /// This method deliberately does not infer telegraph geometry or issue movement. The 60-yalm
    /// radius is only an observation envelope large enough to retain off-arena helper actors; it
    /// must never be reused as a mechanic radius. Exceptions are throttled and swallowed so a
    /// telemetry incompatibility cannot interrupt dungeon behavior.
    /// </remarks>
    private void CaptureEncounterState(SubZoneId currentSubZoneId)
    {
        try
        {
            if (!TryGetArenaCenter(currentSubZoneId, out Vector3 arenaCenter))
            {
                EndCaptureSession();
                return;
            }

            if (capturedSubZoneId != currentSubZoneId)
            {
                EndCaptureSession();
                capturedSubZoneId = currentSubZoneId;
                Logger.Information($"[SkydeepCapture] SESSION+ subZone={(uint)currentSubZoneId}:{currentSubZoneId} arenaCenter={Format(arenaCenter)} captureRadius={CaptureRadius.ToString("F1", CultureInfo.InvariantCulture)} observationalOnly=true");
            }

            BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(actor => actor.IsValid && actor.Location.Distance3D(arenaCenter) <= CaptureRadius)
                .ToArray();

            CaptureActors(currentSubZoneId, actors);
            CapturePlayerState(currentSubZoneId);
        }
        catch (Exception exception)
        {
            DateTime now = DateTime.UtcNow;
            if (now >= nextCaptureErrorUtc)
            {
                nextCaptureErrorUtc = now + CaptureErrorInterval;
                Logger.Warning($"[SkydeepCapture] ERROR capture remains fail-open: {exception}");
            }
        }
    }

    /// <summary>
    /// Captures actor lifecycle, cast metadata, and VFX slot changes for every battle character in the observation envelope.
    /// </summary>
    /// <param name="currentSubZoneId">The boss sub-zone associated with the actor snapshot.</param>
    /// <param name="actors">The bot-thread actor snapshot to inspect.</param>
    private void CaptureActors(SubZoneId currentSubZoneId, IEnumerable<BattleCharacter> actors)
    {
        HashSet<uint> currentActorIds = [];

        foreach (BattleCharacter actor in actors)
        {
            currentActorIds.Add(actor.ObjectId);

            string state = $"{actor.NpcId}|{actor.IsAlive}|{actor.IsVisible}|{actor.IsTargetable}|{actor.CanAttack}|{actor.CurrentTargetId}";
            if (!capturedActorStates.TryGetValue(actor.ObjectId, out string previousState))
            {
                capturedActorStates[actor.ObjectId] = state;
                LogActor("ACTOR+", currentSubZoneId, actor);
            }
            else if (!string.Equals(previousState, state, StringComparison.Ordinal))
            {
                capturedActorStates[actor.ObjectId] = state;
                LogActor("ACTOR_STATE", currentSubZoneId, actor);
            }

            CaptureCast(currentSubZoneId, actor);
            CaptureVfx(currentSubZoneId, actor);
        }

        foreach (uint departedActorId in capturedActorStates.Keys.Where(actorId => !currentActorIds.Contains(actorId)).ToArray())
        {
            Logger.Information($"[SkydeepCapture] ACTOR- subZone={(uint)currentSubZoneId}:{currentSubZoneId} object=0x{departedActorId:X8}");
            capturedActorStates.Remove(departedActorId);
            capturedCasts.Remove(departedActorId);
            capturedVfx.Remove(departedActorId);
        }
    }

    /// <summary>
    /// Captures the first frame of each distinct cast by an actor, including data used by SideStep handler selection.
    /// </summary>
    /// <param name="currentSubZoneId">The boss sub-zone associated with the cast.</param>
    /// <param name="actor">The caster to inspect.</param>
    private void CaptureCast(SubZoneId currentSubZoneId, BattleCharacter actor)
    {
        // The player's own rotation is high-volume and does not describe encounter mechanics.
        // Friendly Duty Support actors remain included because they may reveal stack, shelter,
        // or scripted helper semantics that hostile-only capture would miss.
        if (actor.IsMe || !actor.IsCasting || actor.CastingSpellId == 0 || !actor.SpellCastInfo.IsValid)
        {
            capturedCasts.Remove(actor.ObjectId);
            return;
        }

        uint actionId = actor.SpellCastInfo.ActionId;
        if (capturedCasts.TryGetValue(actor.ObjectId, out uint previousActionId) && previousActionId == actionId)
        {
            return;
        }

        capturedCasts[actor.ObjectId] = actionId;
        Logger.Information(
            $"[SkydeepCapture] CAST subZone={(uint)currentSubZoneId}:{currentSubZoneId} " +
            $"npc={actor.NpcId} object=0x{actor.ObjectId:X8} name=\"{Escape(actor.Name)}\" " +
            $"action={actionId} actionName=\"{Escape(actor.SpellCastInfo.Name)}\" " +
            $"omen={actor.SpellCastInfo.SpellData.Omen} rawCastType={actor.SpellCastInfo.SpellData.RawCastType} effectRange={Format(actor.SpellCastInfo.SpellData.EffectRange)} " +
            $"target=0x{actor.SpellCastInfo.TargetId:X8} actorTarget=0x{actor.CurrentTargetId:X8} " +
            $"actorLocation={Format(actor.Location)} castLocation={Format(actor.SpellCastInfo.CastLocation)} heading={Format(actor.Heading)} " +
            $"remainingMs={actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} " +
            $"alive={actor.IsAlive} visible={actor.IsVisible} targetable={actor.IsTargetable} canAttack={actor.CanAttack}");
    }

    /// <summary>
    /// Captures newly observed VFX IDs and slots, including markers attached to friendly or non-targetable helpers.
    /// </summary>
    /// <param name="currentSubZoneId">The boss sub-zone associated with the VFX.</param>
    /// <param name="actor">The actor whose VFX container should be inspected.</param>
    private void CaptureVfx(SubZoneId currentSubZoneId, BattleCharacter actor)
    {
        HashSet<ulong> currentVfx = [];
        if (actor.VfxContainer.IsValid)
        {
            for (int index = 0; index < actor.VfxContainer.Vfx.Length; index++)
            {
                var vfx = actor.VfxContainer.Vfx[index];
                if (vfx == null || !vfx.IsValid)
                {
                    continue;
                }

                // Preserve player omen/lock-on markers while omitting the ordinary spell-animation
                // slot; that slot is dominated by rotation noise and can obscure boss evidence.
                if (actor.IsMe && index is not (6 or 7 or 10 or 11 or 12))
                {
                    continue;
                }

                ulong vfxId = Convert.ToUInt64(vfx.Id, CultureInfo.InvariantCulture);
                currentVfx.Add(vfxId);
                if (!capturedVfx.TryGetValue(actor.ObjectId, out HashSet<ulong> previousVfx) || !previousVfx.Contains(vfxId))
                {
                    Logger.Information(
                        $"[SkydeepCapture] VFX+ subZone={(uint)currentSubZoneId}:{currentSubZoneId} " +
                        $"npc={actor.NpcId} object=0x{actor.ObjectId:X8} name=\"{Escape(actor.Name)}\" " +
                        $"vfx={vfxId} slot={index} slotName=\"{GetVfxSlotName(index)}\"");
                }
            }
        }

        capturedVfx[actor.ObjectId] = currentVfx;
    }

    /// <summary>
    /// Captures the player's state at a bounded cadence so damage, death, targeting, and avoidance behavior can be correlated to casts.
    /// </summary>
    /// <param name="currentSubZoneId">The boss sub-zone associated with the player snapshot.</param>
    private void CapturePlayerState(SubZoneId currentSubZoneId)
    {
        if (Core.Player == null || !Core.Player.IsValid)
        {
            return;
        }

        bool isAlive = Core.Player.IsAlive;
        if (capturedPlayerAlive != isAlive)
        {
            capturedPlayerAlive = isAlive;
            Logger.Information($"[SkydeepCapture] PLAYER_LIFE subZone={(uint)currentSubZoneId}:{currentSubZoneId} alive={isAlive} location={Format(Core.Player.Location)} hp={Core.Player.CurrentHealth} hpPercent={Format(Core.Player.CurrentHealthPercent)}");
        }

        DateTime now = DateTime.UtcNow;
        if (!Core.Player.InCombat || now < nextPlayerCaptureUtc)
        {
            return;
        }

        nextPlayerCaptureUtc = now + PlayerCaptureInterval;
        uint targetId = Core.Player.CurrentTarget?.ObjectId ?? 0;
        Aura nuisance = Core.Player.GetAuraById(PlayerAura.Nuisance);
        Logger.Information(
            $"[SkydeepCapture] PLAYER subZone={(uint)currentSubZoneId}:{currentSubZoneId} " +
            $"location={Format(Core.Player.Location)} heading={Format(Core.Player.Heading)} hp={Core.Player.CurrentHealth} hpPercent={Format(Core.Player.CurrentHealthPercent)} " +
            $"target=0x{targetId:X8} moving={MovementManager.IsMoving} inCombat={Core.Player.InCombat} " +
            $"sideStepEnabled={SidestepPlugin.Enabled} avoidCount={AvoidanceManager.AvoidInfos.Count} runningOutOfAvoid={AvoidanceManager.IsRunningOutOfAvoid} " +
            $"nuisanceMs={((nuisance?.TimeLeft ?? 0f) * 1000f).ToString("F0", CultureInfo.InvariantCulture)} nuisanceLockOn={HasFeatherRayNuisanceLockOn()}");
    }

    /// <summary>
    /// Clears capture-only state when leaving a boss arena so object IDs can be safely reused later in the duty.
    /// </summary>
    private void EndCaptureSession()
    {
        if (capturedSubZoneId != SubZoneId.NONE)
        {
            Logger.Information($"[SkydeepCapture] SESSION- subZone={(uint)capturedSubZoneId}:{capturedSubZoneId}");
        }

        capturedCasts.Clear();
        capturedActorStates.Clear();
        capturedVfx.Clear();
        capturedSubZoneId = SubZoneId.NONE;
        capturedPlayerAlive = null;
        nextPlayerCaptureUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Resolves the capture anchor for a known Skydeep boss sub-zone.
    /// </summary>
    /// <param name="subZoneId">The sub-zone to resolve.</param>
    /// <param name="arenaCenter">The known arena center when the sub-zone is supported.</param>
    /// <returns><see langword="true"/> when the sub-zone belongs to a Skydeep boss arena.</returns>
    private static bool TryGetArenaCenter(SubZoneId subZoneId, out Vector3 arenaCenter)
    {
        arenaCenter = subZoneId switch
        {
            SubZoneId.UnsungElegy => ArenaCenter.FeatherRay,
            SubZoneId.VurgarMettlegrounds => ArenaCenter.Firearms,
            SubZoneId.GatekeepsAnvil => ArenaCenter.Maulskull,
            _ => Vector3.Zero,
        };

        return arenaCenter != Vector3.Zero;
    }

    /// <summary>
    /// Writes an actor state snapshot using a stable, grep-friendly record format.
    /// </summary>
    /// <param name="eventName">The lifecycle or state-change event name.</param>
    /// <param name="currentSubZoneId">The boss sub-zone associated with the actor.</param>
    /// <param name="actor">The actor to record.</param>
    private static void LogActor(string eventName, SubZoneId currentSubZoneId, BattleCharacter actor)
    {
        Logger.Information(
            $"[SkydeepCapture] {eventName} subZone={(uint)currentSubZoneId}:{currentSubZoneId} " +
            $"npc={actor.NpcId} object=0x{actor.ObjectId:X8} name=\"{Escape(actor.Name)}\" " +
            $"alive={actor.IsAlive} visible={actor.IsVisible} targetable={actor.IsTargetable} canAttack={actor.CanAttack} " +
            $"target=0x{actor.CurrentTargetId:X8} location={Format(actor.Location)} heading={Format(actor.Heading)}");
    }

    /// <summary>
    /// Formats a world position without depending on the machine's locale.
    /// </summary>
    /// <param name="location">The position to format.</param>
    /// <returns>A compact XYZ tuple.</returns>
    private static string Format(Vector3 location) =>
        $"({Format(location.X)},{Format(location.Y)},{Format(location.Z)})";

    /// <summary>
    /// Formats a numeric telemetry value without depending on the machine's locale.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <returns>The value rounded to three decimal places.</returns>
    private static string Format(float value) => value.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>
    /// Prevents actor and action names from terminating their quoted telemetry fields.
    /// </summary>
    /// <param name="value">The text to escape.</param>
    /// <returns>The escaped text.</returns>
    private static string Escape(string value) => value?.Replace('"', '\'') ?? string.Empty;

    /// <summary>
    /// Describes the client VFX slot without assuming that unknown slots have stable semantics.
    /// </summary>
    /// <param name="index">The zero-based VFX container slot.</param>
    /// <returns>A conservative slot label suitable for capture logs.</returns>
    private static string GetVfxSlotName(int index) => index switch
    {
        0 => "Spell",
        6 or 7 => "Omen",
        10 or 11 or 12 => "Lockon",
        _ => "Unknown",
    };

    /// <summary>
    /// Boss 1: Feather Ray.
    /// </summary>
    private Task<bool> FeatherRay()
    {
        if (!IsFeatherRayCombat())
        {
            ResetFeatherRayNuisanceState("Feather Ray combat ended");
            return Task.FromResult(false);
        }

        Aura nuisance = Core.Player.GetAuraById(PlayerAura.Nuisance);
        bool worrisomeWaveCasting = IsFeatherRayActionCasting(EnemyAction.WorrisomeWave);

        if (IsFeatherRayActionCasting(EnemyAction.TroubleBubbles))
        {
            // Trouble Bubbles consumes the same Nuisance aura but creates moving Airy Bubbles,
            // not player cones. Clear any stale cone state and leave motion to the registered
            // current-position and projected-corridor bubble avoids.
            ResetFeatherRayNuisanceState("Trouble Bubbles selected the moving-bubble copy");
            return Task.FromResult(false);
        }

        if (worrisomeWaveCasting)
        {
            if (!featherRayNuisanceConeArmed)
            {
                featherRayNuisanceConeArmed = true;
                Logger.Information(
                    $"[Skydeep] Nuisance armed for Worrisome Wave; aura={PlayerAura.Nuisance} " +
                    $"remainingMs={((nuisance?.TimeLeft ?? 0f) * 1000f).ToString("F0", CultureInfo.InvariantCulture)}.");
            }

            EnsureFeatherRayNuisanceSpread();
        }

        bool nuisanceLockOnActive = HasFeatherRayNuisanceLockOn();
        if (featherRayNuisanceConeArmed && nuisanceLockOnActive)
        {
            CapabilityManager.Update(
                featherRayNuisanceFacingHandle,
                CapabilityFlags.Facing,
                FeatherRayNuisanceFacingLeaseMilliseconds,
                "Facing Nuisance's copied Worrisome Wave away from party members");

            if (!featherRayNuisanceFacingOwned)
            {
                featherRayNuisanceFacingOwned = true;
                Logger.Information("[Skydeep] Nuisance lock-on detected; DutyMechanic owns facing until the copied cone resolves.");
            }

            // Do not compete with emergency egress. Once avoidance has reached safety, stop its
            // residual movement and aim the copied cone through the widest party-free direction.
            if (!AvoidanceManager.IsRunningOutOfAvoid && TryGetFeatherRayNuisanceFacingTarget(out Vector3 facingTarget))
            {
                MovementManager.MoveStop();
                MovementManager.SetFacing(facingTarget);
            }

            return Task.FromResult(true);
        }

        if (featherRayNuisanceConeArmed && nuisance == null && !nuisanceLockOnActive)
        {
            // Live capture shows the aura ending 1.53 seconds before copied-cone damage while
            // lock-on 514 remains present. Releasing on the aura alone drops both the remote-cone
            // avoids and facing ownership during the actual impact window.
            ResetFeatherRayNuisanceState("Nuisance lock-on ended after the copied cone");
        }

        return Task.FromResult(worrisomeWaveCasting);
    }

    /// <summary>
    /// Boss 2: Firearms.
    /// </summary>
    private static async Task<bool> Firearms()
    {
        if (EnemyAction.ThunderlightFlurry.IsCasting() && !EnemyAction.Artillery.IsCasting())
        {
            await MovementHelpers.Spread(6_000, 7f);
        }

        return false;
    }

    /// <summary>
    /// Boss 3: Maulskull.
    /// </summary>
    private async Task<bool> Maulskull()
    {
        BattleCharacter towerCaster = GetActiveMaulskullTowerCaster();
        if (towerCaster == null)
        {
            ReleaseMaulskullDeepThunderMovement("Deep Thunder tower helper ended");
        }

        BattleCharacter destructiveHeatCaster = GetActiveMaulskullDestructiveHeatCaster();
        if (destructiveHeatCaster == null)
        {
            ReleaseMaulskullDestructiveHeatMovement("Destructive Heat helper ended");
        }

        BattleCharacter impactCaster = GetActiveMaulskullImpactCaster();
        if (impactCaster != null)
        {
            // AvoidanceManager suppresses combat-routine movement only while it is actively running
            // out of a shape. Keep the encounter handle leased through the full Impact cast so
            // Magitek cannot pull the player back out after RB first reaches the positive safe disk.
            CapabilityManager.Update(
                CapabilityHandle,
                CapabilityFlags.Movement,
                impactCaster.SpellCastInfo.RemainingCastTime,
                $"Holding Maulskull knockback position for action {impactCaster.CastingSpellId}");

            // The inverted donut supplies path-safe geometry, while explicit positive movement
            // stops RB as soon as it enters the one-yalm disk. Live Ringing Blows captures showed
            // that releasing movement at the disk edge let navigation momentum carry the player
            // back out, causing repeated oscillation and an unstable knockback direction.
            return await MoveToMaulskullMechanicPosition(
                GetMaulskullKnockbackPreposition(impactCaster),
                MaulskullImpactPositionTolerance,
                holdOnArrivalWhileAvoidanceActive: true);
        }

        BattleCharacter stonecarverCaster = GetFirstMaulskullStonecarverCaster();
        if (stonecarverCaster != null)
        {
            if (EnemyAction.RingingStonecarverHelpers.Contains(stonecarverCaster.CastingSpellId))
            {
                // Continue movement ownership after the knockback. The helpers overlap and resolve
                // 2.5 seconds apart, so refreshing this timed lease on the currently earliest cast
                // prevents the combat routine from interrupting either safe-half transition.
                CapabilityManager.Update(
                    CapabilityHandle,
                    CapabilityFlags.Movement,
                    stonecarverCaster.SpellCastInfo.RemainingCastTime,
                    $"Transitioning between Ringing Blows Stonecarvers for action {stonecarverCaster.CastingSpellId}");

                // The two Ringing Blows cleaves swap unsafe halves only 2.5 seconds apart. Stage
                // just inside the current safe half so the transition is a six-yalm cross; the
                // fatal capture instead began the second transition near X=81.5 and was still at
                // X=98.8 when the west-half cleave resolved.
                return await MoveToMaulskullMechanicPosition(
                    GetMaulskullStonecarverTransitionPosition(stonecarverCaster),
                    MaulskullStonecarverTransitionTolerance);
            }

            // The registered rectangle owns ordinary Stonecarver movement until it resolves.
            return true;
        }

        BattleCharacter landingCaster = GetLastMaulskullLandingCaster();
        if (landingCaster != null)
        {
            // AvoidanceManager releases its own movement capability as soon as the player exits the
            // current circle, even while other Landing helpers are still counting down. Extend the
            // encounter lease through the last rock plus a short client-visual grace so Magitek
            // cannot pull the player back into the wave. Avoidance movement remains available.
            CapabilityManager.Update(
                CapabilityHandle,
                CapabilityFlags.Movement,
                landingCaster.SpellCastInfo.RemainingCastTime + MaulskullLandingMovementGrace,
                $"Holding Maulwork Landing safety for action {landingCaster.CastingSpellId}");

            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                // Cancel residual navigation only after avoidance reports that it has reached
                // safety; doing this while an escape is active would interrupt the rock dodge.
                MovementManager.MoveStop();
            }
        }

        if (towerCaster != null)
        {
            // Deep Thunder is a four-player tower, so following an arbitrary NPC is semantically
            // wrong and can walk out when that NPC reacts late. Move to the helper's six-yalm
            // center, then yield the TreeStart tick so Magitek can heal the repeated tower damage.
            // Live captures on 2026-08-19 showed correct positioning followed by HP lows of 0%,
            // 2.6%, and 25.1%; every actual heal began only after helper 36689 expired because this
            // handler previously returned true for the entire hold.
            bool hasArrived = Core.Player.Distance2D(towerCaster.Location) <= MaulskullTowerPositionTolerance;
            if (hasArrived && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                MovementManager.MoveStop();
                CapabilityManager.Update(
                    maulskullDeepThunderMovementHandle,
                    CapabilityFlags.Movement,
                    towerCaster.SpellCastInfo.RemainingCastTime,
                    $"Holding Deep Thunder tower for action {towerCaster.CastingSpellId} while allowing combat-routine healing");

                if (!maulskullDeepThunderMovementOwned)
                {
                    maulskullDeepThunderMovementOwned = true;
                    Logger.Information("[Skydeep] Deep Thunder tower reached; combat-routine movement is suppressed while healing remains available.");
                }

                return false;
            }

            return await MoveToMaulskullMechanicPosition(
                towerCaster.Location,
                MaulskullTowerPositionTolerance);
        }

        BattleCharacter stackTarget = GetActiveMaulskullStackTarget();
        if (stackTarget != null)
        {
            // Building Heat follows Colossal Impact and requires all four players on its selected
            // target. Resolve that target directly; when it is us, the same helper holds position
            // so the combat routine cannot drag the stack marker away from approaching allies.
            return await MoveToMaulskullMechanicPosition(
                stackTarget.Location,
                MaulskullStackPositionTolerance);
        }

        if (destructiveHeatCaster != null && !EnemyAction.Impact.IsCasting() && !EnemyAction.ColossalImpact.IsCasting())
        {
            Vector3 destination = GetMaulskullDestructiveHeatDestination();
            bool hasArrived = Core.Player.Distance2D(destination) <= MaulskullDestructiveHeatPositionTolerance;
            if (hasArrived && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                MovementManager.MoveStop();
                CapabilityManager.Update(
                    maulskullDestructiveHeatMovementHandle,
                    CapabilityFlags.Movement,
                    destructiveHeatCaster.SpellCastInfo.RemainingCastTime,
                    $"Holding Destructive Heat spread for action {destructiveHeatCaster.CastingSpellId} while allowing combat-routine actions");

                if (!maulskullDestructiveHeatMovementOwned)
                {
                    maulskullDestructiveHeatMovementOwned = true;
                    Logger.Information("[Skydeep] Destructive Heat destination reached; combat-routine movement is suppressed until the spread resolves.");
                }

                // Movement is already owned by the dedicated capability handle. Yield this tick so
                // melee attacks and mitigation continue while the player holds the positive slot.
                return false;
            }

            return await MoveToMaulskullMechanicPosition(
                destination,
                MaulskullDestructiveHeatPositionTolerance);
        }

        if (EnemyAction.WroughtFire.IsCasting() && Core.Player.IsTank())
        {
            // Wrought Fire is an AoE tankbuster; the target must separate while the registered
            // non-tank avoid keeps the remainder of the party outside the splash.
            await MovementHelpers.Spread(7_000, 7f);
        }

        return false;
    }

    /// <summary>
    /// Releases combat-routine movement ownership when Deep Thunder's tower helper disappears.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded by <see cref="CapabilityManager"/>.</param>
    private void ReleaseMaulskullDeepThunderMovement(string reason)
    {
        if (!maulskullDeepThunderMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(
            maulskullDeepThunderMovementHandle,
            CapabilityFlags.Movement,
            reason);
        maulskullDeepThunderMovementOwned = false;
    }

    /// <summary>
    /// Releases Destructive Heat's combat-routine movement lease and its latched spread destination.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded by <see cref="CapabilityManager"/>.</param>
    private void ReleaseMaulskullDestructiveHeatMovement(string reason)
    {
        maulskullDestructiveHeatDestination = null;
        if (!maulskullDestructiveHeatMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(
            maulskullDestructiveHeatMovementHandle,
            CapabilityFlags.Movement,
            reason);
        maulskullDestructiveHeatMovementOwned = false;
        Logger.Information($"[Skydeep] Released Destructive Heat movement ownership: {reason}.");
    }

    /// <summary>
    /// Indicates whether Feather Ray's encounter-local hazards should be active.
    /// </summary>
    /// <returns><see langword="true"/> only during combat in the Unsung Elegy arena.</returns>
    private static bool IsFeatherRayCombat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.UnsungElegy;

    /// <summary>
    /// Selects only the moving 1.1-reach Airy Bubble from each coincident small/large actor pair.
    /// </summary>
    /// <param name="actor">The actor being considered for current and projected collision avoidance.</param>
    /// <returns><see langword="true"/> when the actor is a visible small Airy Bubble.</returns>
    private static bool IsVisibleSmallFeatherRayBubble(BattleCharacter actor) =>
        actor.NpcId == EnemyNpc.AiryBubble &&
        actor.IsVisible &&
        actor.CombatReach <= FeatherRayLargeBubbleMinimumCombatReach;

    /// <summary>
    /// Checks for one Feather Ray action without assigning geometry to unrelated helper casts.
    /// </summary>
    /// <param name="actionId">The boss action to locate.</param>
    /// <returns><see langword="true"/> when Feather Ray is currently casting the requested action.</returns>
    private static bool IsFeatherRayActionCasting(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsCasting && actor.NpcId == EnemyNpc.FeatherRay && actor.CastingSpellId == actionId);

    /// <summary>
    /// Detects the shared Nuisance overhead marker on the player or any visible party member.
    /// </summary>
    /// <returns><see langword="true"/> when lock-on 514 shows that a copied special attack is counting down.</returns>
    /// <remarks>
    /// The local player is checked directly because RebornBuddy's generic battle-character
    /// enumeration did not include its lock-on in the 2026-08-19 capture. Duty Support actors all
    /// received the marker on the same frame, so any party marker is a valid fallback signal.
    /// </remarks>
    private static bool HasFeatherRayNuisanceLockOn()
    {
        if (HasFeatherRayNuisanceLockOn(Core.Player))
        {
            return true;
        }

        return PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Any(HasFeatherRayNuisanceLockOn);
    }

    /// <summary>
    /// Checks one character's VFX container for the Nuisance lock-on.
    /// </summary>
    /// <param name="actor">The player or party member whose overhead effects should be inspected.</param>
    /// <returns><see langword="true"/> when the character has lock-on ID 514.</returns>
    private static bool HasFeatherRayNuisanceLockOn(BattleCharacter actor)
    {
        if (actor == null || !actor.IsValid || !actor.VfxContainer.IsValid)
        {
            return false;
        }

        return actor.VfxContainer.Vfx.Any(vfx =>
            vfx != null &&
            vfx.IsValid &&
            Convert.ToUInt64(vfx.Id, CultureInfo.InvariantCulture) == PlayerVfx.NuisanceLockOn);
    }

    /// <summary>
    /// Identifies another marked party member whose copied Worrisome Wave cone can hit the player.
    /// </summary>
    /// <param name="actor">Candidate battle character supplied by the avoidance manager.</param>
    /// <returns>
    /// <see langword="true"/> when the actor is a living, visible party member other than the
    /// player and currently carries Nuisance lock-on 514.
    /// </returns>
    /// <remarks>
    /// Membership is matched by object ID rather than NPC ID because Duty Support party actors are
    /// ordinary friendly battle characters with distinct NPC IDs. Their live heading is the only
    /// observed orientation signal for the copied cone; no separate helper cast appeared before the
    /// captured damage event.
    /// </remarks>
    private static bool IsOtherFeatherRayNuisanceConeSource(BattleCharacter actor)
    {
        if (actor == null || !actor.IsValid || !actor.IsAlive || actor.IsMe || !HasFeatherRayNuisanceLockOn(actor))
        {
            return false;
        }

        return PartyManager.VisibleMembers.Any(member =>
            member.BattleCharacter != null && member.BattleCharacter.ObjectId == actor.ObjectId);
    }

    /// <summary>
    /// Identifies another living party member for encounter-local spread avoidance.
    /// </summary>
    /// <param name="actor">Candidate battle character supplied by the avoidance manager.</param>
    /// <returns><see langword="true"/> when the actor is a living visible party member other than the player.</returns>
    private static bool IsOtherLivingPartyMember(BattleCharacter actor)
    {
        if (actor == null || !actor.IsValid || !actor.IsAlive || actor.IsMe)
        {
            return false;
        }

        return PartyManager.VisibleMembers.Any(member =>
            member.BattleCharacter != null && member.BattleCharacter.ObjectId == actor.ObjectId);
    }

    /// <summary>
    /// Selects a facing direction whose copied Worrisome Wave cone avoids every living party member.
    /// </summary>
    /// <param name="facingTarget">A nearby world-space point suitable for <see cref="MovementManager.SetFacing(Vector3)"/>.</param>
    /// <returns><see langword="true"/> when at least one other living party member was available to score.</returns>
    private static bool TryGetFeatherRayNuisanceFacingTarget(out Vector3 facingTarget)
    {
        Vector3 origin = Core.Player.Location;
        BattleCharacter[] partyMembers = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && !actor.IsMe)
            .ToArray();

        if (partyMembers.Length == 0)
        {
            facingTarget = origin;
            return false;
        }

        float bestDirectionX = 0f;
        float bestDirectionZ = 1f;
        double bestMinimumSeparation = double.MinValue;

        for (int index = 0; index < FeatherRayNuisanceFacingSamples; index++)
        {
            double angle = index * Math.PI * 2d / FeatherRayNuisanceFacingSamples;
            float directionX = (float)Math.Cos(angle);
            float directionZ = (float)Math.Sin(angle);
            double minimumSeparation = Math.PI;

            foreach (BattleCharacter partyMember in partyMembers)
            {
                float deltaX = partyMember.Location.X - origin.X;
                float deltaZ = partyMember.Location.Z - origin.Z;
                double distance = Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
                if (distance < 0.1d)
                {
                    continue;
                }

                double dot = (directionX * deltaX + directionZ * deltaZ) / distance;
                double separation = Math.Acos(Math.Clamp(dot, -1d, 1d));
                minimumSeparation = Math.Min(minimumSeparation, separation);
            }

            if (minimumSeparation > bestMinimumSeparation)
            {
                bestMinimumSeparation = minimumSeparation;
                bestDirectionX = directionX;
                bestDirectionZ = directionZ;
            }
        }

        facingTarget = new Vector3(
            origin.X + bestDirectionX * FeatherRayNuisanceFacingTargetDistance,
            origin.Y,
            origin.Z + bestDirectionZ * FeatherRayNuisanceFacingTargetDistance);
        return true;
    }

    /// <summary>
    /// Creates the first Nuisance branch's party spread as one explicitly owned avoid.
    /// </summary>
    /// <remarks>
    /// RB reports the Nuisance aura with a 60-second duration even though Worrisome Wave consumes it
    /// roughly thirteen seconds later. Owning the avoid here lets the aura/branch lifecycle remove it
    /// immediately instead of leaving visible party circles active through later Feather Ray phases.
    /// </remarks>
    private void EnsureFeatherRayNuisanceSpread()
    {
        if (featherRayNuisanceSpreadInfo != default)
        {
            return;
        }

        uint[] partyMemberIds =
        [
            .. PartyManager.VisibleMembers
                .Select(member => member.BattleCharacter)
                .Where(actor => actor != null && actor.IsValid && !actor.IsMe)
                .Select(actor => actor.ObjectId),
        ];

        if (partyMemberIds.Length == 0)
        {
            return;
        }

        featherRayNuisanceSpreadInfo = AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => IsFeatherRayCombat() && featherRayNuisanceConeArmed,
            radius: FeatherRayNuisanceSpreadRadius,
            unitIds: partyMemberIds);
        Logger.Information("[Skydeep] Nuisance party spread activated for copied Worrisome Wave.");
    }

    /// <summary>
    /// Removes the copied-cone spread and facing ownership, then clears their encounter-local state.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded when active Nuisance behavior is released.</param>
    private void ResetFeatherRayNuisanceState(string reason)
    {
        featherRayNuisanceConeArmed = false;

        if (featherRayNuisanceSpreadInfo != default)
        {
            AvoidanceManager.RemoveAvoid(featherRayNuisanceSpreadInfo);
            featherRayNuisanceSpreadInfo = default;
            Logger.Information($"[Skydeep] Removed Nuisance party spread: {reason}.");
        }

        if (!featherRayNuisanceFacingOwned)
        {
            return;
        }

        CapabilityManager.Clear(featherRayNuisanceFacingHandle, CapabilityFlags.Facing, reason);
        featherRayNuisanceFacingOwned = false;
        Logger.Information($"[Skydeep] Released Nuisance facing ownership: {reason}.");
    }

    /// <summary>
    /// Switches Feather Ray between its normal rectangular platform and Hydro Ring's reduced circle.
    /// </summary>
    /// <remarks>
    /// BossMod identifies the authoritative transition as map-effect index 19, but RebornBuddy's
    /// encounter surface does not expose that event here. The live capture provides a conservative
    /// substitute: Hydro Ring starts the wall, and the associated Blowing Bubbles or Trouble Bubbles
    /// sequence owns visible small Airy Bubbles until the wall clears. If the wave is interrupted or
    /// never appears, retaining the smaller arena is safer than permitting movement through the wall;
    /// leaving combat or the sub-zone always resets the state for a retry.
    /// </remarks>
    private void UpdateFeatherRayArenaState()
    {
        if (!IsFeatherRayCombat())
        {
            ResetFeatherRayArenaState();
            return;
        }

        if (IsFeatherRayActionCasting(EnemyAction.HydroRing) && !featherRayHydroRingActive)
        {
            featherRayHydroRingActive = true;
            featherRayHydroRingObservedSmallBubbles = false;
            featherRayLastSmallBubbleSeenUtc = DateTime.MinValue;
            Logger.Information("[Skydeep] Feather Ray arena boundary changed to the Hydro Ring circle.");
        }

        if (!featherRayHydroRingActive)
        {
            return;
        }

        bool hasVisibleSmallBubble = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && IsVisibleSmallFeatherRayBubble(actor));

        if (hasVisibleSmallBubble)
        {
            featherRayHydroRingObservedSmallBubbles = true;
            featherRayLastSmallBubbleSeenUtc = DateTime.UtcNow;
            return;
        }

        bool bubbleCastActive = IsFeatherRayActionCasting(EnemyAction.BlowingBubbles) ||
                                IsFeatherRayActionCasting(EnemyAction.TroubleBubbles);

        if (featherRayHydroRingObservedSmallBubbles &&
            !bubbleCastActive &&
            DateTime.UtcNow - featherRayLastSmallBubbleSeenUtc >= FeatherRayArenaRestoreGrace)
        {
            ResetFeatherRayArenaState();
        }
    }

    /// <summary>
    /// Restores Feather Ray's normal arena state after a completed wave, wipe, or zone transition.
    /// </summary>
    private void ResetFeatherRayArenaState()
    {
        bool wasHydroRingActive = featherRayHydroRingActive;
        featherRayHydroRingActive = false;
        featherRayHydroRingObservedSmallBubbles = false;
        featherRayLastSmallBubbleSeenUtc = DateTime.MinValue;

        // Emit only on an actual transition so out-of-combat ticks remain quiet while still leaving
        // durable live evidence that the larger platform was restored at the end of the bubble wave.
        if (wasHydroRingActive)
        {
            Logger.Information("[Skydeep] Feather Ray arena boundary restored to the normal rectangle.");
        }
    }

    /// <summary>
    /// Locates the east- or west-moving Rolling Current visual cast.
    /// </summary>
    /// <returns>The Feather Ray caster while a Rolling Current variant is active, otherwise <see langword="null"/>.</returns>
    private static BattleCharacter GetActiveFeatherRayRollingCurrentCaster() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid &&
                                     actor.IsCasting &&
                                     actor.NpcId == EnemyNpc.FeatherRay &&
                                     actor.CastingSpellId is EnemyAction.RollingCurrentEast or EnemyAction.RollingCurrentWest);

    /// <summary>
    /// Predicts Bubble Bomb's explosion centers before RB exposes the short-lived Burst casts.
    /// </summary>
    /// <returns>The current large-bubble centers shifted eight yalms in Rolling Current's captured direction.</returns>
    private static IEnumerable<Vector3> GetPredictedFeatherRayBurstLocations()
    {
        BattleCharacter caster = GetActiveFeatherRayRollingCurrentCaster();
        if (caster == null)
        {
            return Array.Empty<Vector3>();
        }

        // BossMod's action-specific movement and the live east-current capture agree that 36737
        // shifts bubbles toward negative world X; 36736 is the mirrored positive-X variant.
        float xOffset = caster.CastingSpellId == EnemyAction.RollingCurrentEast
            ? -FeatherRayRollingCurrentBubbleOffset
            : FeatherRayRollingCurrentBubbleOffset;

        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid &&
                            actor.IsVisible &&
                            actor.NpcId == EnemyNpc.AiryBubble &&
                            actor.CombatReach > FeatherRayLargeBubbleMinimumCombatReach)
            .Select(actor => new Vector3(actor.Location.X + xOffset, actor.Location.Y, actor.Location.Z))
            .ToArray();
    }

    /// <summary>
    /// Registers one helper-driven segment of Firearms' reflected Thunderlight Burst laser.
    /// </summary>
    /// <param name="actionId">The helper action whose cast lifetime owns this segment.</param>
    /// <param name="length">The measured forward length of the rectangular segment.</param>
    private static void AddFirearmsThunderlightBurstRectangle(uint actionId, float length) =>
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.VurgarMettlegrounds,
            objectSelector: actor => actor.NpcId == EnemyNpc.Firearms && actor.CastingSpellId == actionId,
            width: FirearmsThunderlightBurstWidth,
            length: length,
            priority: AvoidancePriority.High);

    /// <summary>
    /// Registers one fixed-angle side line from Maulwork's Shatter pattern.
    /// </summary>
    /// <param name="actionId">The west- or east-side helper action that owns the line.</param>
    /// <param name="polygonRotation">The RB polygon rotation, already inverted from the FFXIV cast rotation.</param>
    private static void AddMaulskullShatterSideRectangle(uint actionId, float polygonRotation) =>
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            // Match the center line's Landing handoff: activating an angled line while the rocks
            // are live can send RB directly through a circle that is about to resolve.
            canRun: () => IsMaulskullCombat() && GetLastMaulskullLandingCaster() == null,
            objectSelector: actor => actor.NpcId == EnemyNpc.Maulskull && actor.CastingSpellId == actionId,
            width: MaulskullShatterSideWidth,
            length: MaulskullShatterSideLength,
            rotationProducer: _ => polygonRotation,
            priority: AvoidancePriority.High);

    /// <summary>
    /// Indicates whether encounter-local Maulskull hazards should be active.
    /// </summary>
    /// <returns><see langword="true"/> only during combat on Gatekeep's Anvil.</returns>
    private static bool IsMaulskullCombat() =>
        Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GatekeepsAnvil;

    /// <summary>
    /// Locates the hidden helper currently resolving one of Maulskull's radial knockbacks.
    /// </summary>
    /// <returns>The impact helper with the least cast time remaining, or <see langword="null"/> when no knockback is active.</returns>
    private static BattleCharacter GetActiveMaulskullImpactCaster() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid && EnemyAction.Impact.Contains(actor.CastingSpellId))
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    /// <summary>
    /// Locates the first half-arena cleave that will resolve after Ringing Blows' knockback.
    /// </summary>
    /// <returns>The Stonecarver helper with the least cast time remaining, or <see langword="null"/> when none is active.</returns>
    private static BattleCharacter GetFirstMaulskullStonecarverCaster() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid && EnemyAction.StonecarverHelpers.Contains(actor.CastingSpellId))
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    /// <summary>
    /// Locates the Landing helper whose rock will resolve last in the current Maulwork wave.
    /// </summary>
    /// <returns>The active Landing helper with the greatest remaining cast time, or <see langword="null"/> when no rocks are falling.</returns>
    private static BattleCharacter GetLastMaulskullLandingCaster() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == EnemyAction.Landing)
            .OrderByDescending(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    /// <summary>
    /// Locates the active Deep Thunder tower helper rather than guessing from party movement.
    /// </summary>
    /// <returns>The tower with the least cast time remaining, or <see langword="null"/> when no tower is active.</returns>
    private static BattleCharacter GetActiveMaulskullTowerCaster() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid && EnemyAction.DeepThunderTowers.Contains(actor.CastingSpellId))
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    /// <summary>
    /// Locates the active Destructive Heat helper, preferring the helper targeted at the player.
    /// </summary>
    /// <returns>The player's six-yalm spread helper, another active helper as fallback, or <see langword="null"/>.</returns>
    private static BattleCharacter GetActiveMaulskullDestructiveHeatCaster() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid &&
                            actor.IsCasting &&
                            actor.SpellCastInfo.IsValid &&
                            EnemyAction.DestructiveHeat.Contains(actor.CastingSpellId))
            .OrderByDescending(actor => actor.SpellCastInfo.TargetId == Core.Player.ObjectId)
            .ThenBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

    /// <summary>
    /// Resolves Building Heat's selected stack target from its hidden helper cast.
    /// </summary>
    /// <returns>The selected battle character, or <see langword="null"/> when the stack is inactive or unresolved.</returns>
    private static BattleCharacter GetActiveMaulskullStackTarget()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == EnemyAction.BuildingHeat)
            .OrderBy(actor => actor.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();

        return caster == null
            ? null
            : GameObjectManager.GetObjectByObjectId(caster.SpellCastInfo.TargetId) as BattleCharacter;
    }

    /// <summary>
    /// Selects and latches an in-bounds Destructive Heat position farthest from the live party.
    /// </summary>
    /// <returns>A point on the 15-yalm ring around the verified Maulskull arena center.</returns>
    /// <remarks>
    /// Duty-support actors choose their own spread positions after Impact, so fixed role slots are
    /// not reliable across player jobs. Sampling the known arena instead preserves the mechanic's
    /// six-yalm semantics without guessing those NPC decisions. The point remains stable until a
    /// party member moves within the seven-yalm safety threshold, preventing per-tick oscillation.
    /// </remarks>
    private Vector3 GetMaulskullDestructiveHeatDestination()
    {
        BattleCharacter[] partyMembers = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid && actor.IsAlive && !actor.IsMe)
            .ToArray();

        if (partyMembers.Length == 0)
        {
            // Losing party visibility removes the evidence needed to choose a new formation. Hold
            // the current position rather than manufacturing a slot from stale or absent actors.
            maulskullDestructiveHeatDestination = Core.Player.Location;
            return maulskullDestructiveHeatDestination.Value;
        }

        if (maulskullDestructiveHeatDestination.HasValue &&
            GetMinimumMaulskullDestructiveHeatSeparation(maulskullDestructiveHeatDestination.Value, partyMembers) >=
            MaulskullDestructiveHeatMinimumSeparation)
        {
            return maulskullDestructiveHeatDestination.Value;
        }

        Vector3 center = ArenaCenter.Maulskull;
        Vector3 playerLocation = Core.Player.Location;
        Vector3 bestDestination = playerLocation;
        float bestMinimumSeparation = float.MinValue;
        float bestTravelDistance = float.MaxValue;

        for (int index = 0; index < MaulskullDestructiveHeatDirectionSamples; index++)
        {
            double angle = index * Math.PI * 2d / MaulskullDestructiveHeatDirectionSamples;
            Vector3 candidate = new(
                center.X + (float)Math.Cos(angle) * MaulskullDestructiveHeatDestinationRadius,
                center.Y,
                center.Z + (float)Math.Sin(angle) * MaulskullDestructiveHeatDestinationRadius);
            float minimumSeparation = GetMinimumMaulskullDestructiveHeatSeparation(candidate, partyMembers);
            float travelDistance = GetPlanarDistance(candidate, playerLocation);

            if (minimumSeparation > bestMinimumSeparation ||
                (minimumSeparation == bestMinimumSeparation && travelDistance < bestTravelDistance))
            {
                bestDestination = candidate;
                bestMinimumSeparation = minimumSeparation;
                bestTravelDistance = travelDistance;
            }
        }

        maulskullDestructiveHeatDestination = bestDestination;
        Logger.Information($"[Skydeep] Selected Destructive Heat destination {bestDestination} with {bestMinimumSeparation:F1} yalms minimum live-party separation.");
        return maulskullDestructiveHeatDestination.Value;
    }

    /// <summary>
    /// Measures the nearest living party member to a candidate Destructive Heat destination.
    /// </summary>
    /// <param name="destination">The in-bounds point being scored.</param>
    /// <param name="partyMembers">Other living party members from the current bot-thread snapshot.</param>
    /// <returns>The shortest planar distance to another member.</returns>
    private static float GetMinimumMaulskullDestructiveHeatSeparation(
        Vector3 destination,
        IEnumerable<BattleCharacter> partyMembers) =>
        partyMembers.Min(member => GetPlanarDistance(destination, member.Location));

    /// <summary>
    /// Measures horizontal distance without depending on vertical arena offsets.
    /// </summary>
    /// <param name="first">First world-space point.</param>
    /// <param name="second">Second world-space point.</param>
    /// <returns>Distance in the X/Z plane.</returns>
    private static float GetPlanarDistance(Vector3 first, Vector3 second)
    {
        float deltaX = first.X - second.X;
        float deltaZ = first.Z - second.Z;
        return (float)Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
    }

    /// <summary>
    /// Moves to a positive mechanic destination without competing with active geometric avoidance.
    /// </summary>
    /// <param name="destination">The tower center, selected stack target, or latched spread position.</param>
    /// <param name="tolerance">Distance at which movement stops and the position is held.</param>
    /// <param name="holdOnArrivalWhileAvoidanceActive">
    /// Whether a verified positive-position destination may stop movement on AvoidanceManager's
    /// final active frame. Leave false for towers and stacks so unrelated hazards retain priority.
    /// </param>
    /// <returns><see langword="true"/> because an active mechanic owns movement for this tick.</returns>
    private static async Task<bool> MoveToMaulskullMechanicPosition(
        Vector3 destination,
        float tolerance,
        bool holdOnArrivalWhileAvoidanceActive = false)
    {
        bool hasArrived = Core.Player.Distance2D(destination) <= tolerance;
        if (hasArrived && holdOnArrivalWhileAvoidanceActive)
        {
            // Impact's computed disk is itself the verified geometric safe zone. AvoidanceManager
            // can still report its run on the entry frame, so stop before path momentum exits it.
            MovementManager.MoveStop();
            return true;
        }

        // AvoidanceManager owns emergency egress from a current AOE. Once it has reached geometric
        // safety, this explicit movement resumes toward the semantic destination on the next tick.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (hasArrived)
        {
            MovementManager.MoveStop();
            return true;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        await CommonTasks.MoveTo(destination);
        return true;
    }

    /// <summary>
    /// Selects a stable point near the dividing line on Stonecarver's currently safe half.
    /// </summary>
    /// <param name="stonecarverCaster">The Ringing Blows helper whose rectangle resolves next.</param>
    /// <returns>A point three yalms opposite the helper, with Z clamped safely inside the platform.</returns>
    private static Vector3 GetMaulskullStonecarverTransitionPosition(BattleCharacter stonecarverCaster)
    {
        Vector3 center = ArenaCenter.Maulskull;
        float helperXOffset = stonecarverCaster.Location.X - center.X;
        if (Math.Abs(helperXOffset) < 0.5f)
        {
            // An unexpected centered helper does not provide enough evidence to infer a safe half.
            return center;
        }

        float unsafeXDirection = Math.Sign(helperXOffset);
        float safeX = center.X - unsafeXDirection * MaulskullStonecarverTransitionInset;
        float safeHalfExtent = MaulskullArenaHalfExtent - MaulskullStonecarverTransitionInset;
        float safeZ = Math.Max(
            center.Z - safeHalfExtent,
            Math.Min(center.Z + safeHalfExtent, Core.Player.Location.Z));

        return new Vector3(safeX, center.Y, safeZ);
    }

    /// <summary>
    /// Chooses a pre-knockback point whose predicted landing remains inside Maulskull's square platform.
    /// </summary>
    /// <param name="impactCaster">The active Impact helper whose origin and action determine the knockback.</param>
    /// <returns>A world-space point three yalms outside the lethal impact circle.</returns>
    /// <remarks>
    /// The calculation uses the captured radial relationship
    /// <c>landing = origin + direction * (preposition radius + knockback distance)</c>. It samples
    /// one-degree directions and maximizes the smaller of the pre-position and landing margins.
    /// During Ringing Blows, the earliest Stonecarver helper identifies the first unsafe half; the
    /// selector then favors the safe landing closest to the centerline while retaining two yalms of
    /// physical edge clearance, shortening the transition to the second, opposite cleave.
    /// </remarks>
    private static Vector3 GetMaulskullKnockbackPreposition(BattleCharacter impactCaster)
    {
        if (impactCaster == null)
        {
            return ArenaCenter.Maulskull;
        }

        float knockbackDistance = impactCaster.CastingSpellId switch
        {
            EnemyAction.ImpactSkullcrush => 18f,
            EnemyAction.ImpactRingingBlows => 18f,
            EnemyAction.ImpactColossal => 20f,
            _ => 0f,
        };

        if (knockbackDistance <= 0f)
        {
            return ArenaCenter.Maulskull;
        }

        Vector3 origin = impactCaster.Location;
        Vector3 center = ArenaCenter.Maulskull;
        float prepositionRadius = MaulskullImpactPrepositionRadius;
        float landingRadius = prepositionRadius + knockbackDistance;
        float safeHalfExtent = MaulskullArenaHalfExtent - MaulskullLandingInset;
        BattleCharacter firstStonecarver = impactCaster.CastingSpellId == EnemyAction.ImpactRingingBlows
            ? GetFirstMaulskullStonecarverCaster()
            : null;
        float ringingSafeXDirection = firstStonecarver == null || Math.Abs(firstStonecarver.Location.X - center.X) < 0.5f
            ? 0f
            : firstStonecarver.Location.X > center.X ? -1f : 1f;
        Vector3 bestPreposition = center;
        float bestScore = float.MinValue;

        for (int index = 0; index < MaulskullKnockbackDirectionSamples; index++)
        {
            double radians = index * (Math.PI * 2d / MaulskullKnockbackDirectionSamples);
            float directionX = (float)Math.Cos(radians);
            float directionZ = (float)Math.Sin(radians);
            Vector3 preposition = new(
                origin.X + directionX * prepositionRadius,
                center.Y,
                origin.Z + directionZ * prepositionRadius);
            Vector3 landing = new(
                origin.X + directionX * landingRadius,
                center.Y,
                origin.Z + directionZ * landingRadius);

            // The first captured Ringing Blows helper at X=110 resolves before the X=90 helper;
            // the video confirms its east-half rectangle while surviving allies land west.
            if (ringingSafeXDirection != 0f && (landing.X - center.X) * ringingSafeXDirection <= 0f)
            {
                continue;
            }

            float prepositionMargin = GetSquareMargin(preposition, center, safeHalfExtent);
            float landingMargin = GetSquareMargin(landing, center, safeHalfExtent);
            float margin = Math.Min(prepositionMargin, landingMargin);
            if (margin < 0f)
            {
                continue;
            }

            float score = margin;
            if (ringingSafeXDirection != 0f)
            {
                // MaulskullLandingInset already reserves one yalm from the physical edge; requiring
                // one additional yalm here preserves the two-yalm landing clearance seen to survive
                // while making centerline distance, rather than maximal edge margin, the priority.
                if (landingMargin < MaulskullRingingLandingSafetyMargin)
                {
                    continue;
                }

                score = -Math.Abs(landing.X - center.X) + landingMargin * 0.001f;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPreposition = preposition;
            }
        }

        // Unknown helper placement should fail toward the platform center rather than emit an
        // out-of-bounds destination. All three captured Impact helper layouts have positive margin.
        return bestScore > float.MinValue ? bestPreposition : center;
    }

    /// <summary>
    /// Measures the shortest signed distance from a point to the selected square safety boundary.
    /// </summary>
    /// <param name="point">The pre-position or predicted landing point.</param>
    /// <param name="center">The arena center.</param>
    /// <param name="halfExtent">The platform half-extent after applying the safety inset.</param>
    /// <returns>A positive margin inside the square or a negative distance outside it.</returns>
    private static float GetSquareMargin(Vector3 point, Vector3 center, float halfExtent) =>
        halfExtent - Math.Max(Math.Abs(point.X - center.X), Math.Abs(point.Z - center.Z));

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Feather Ray.
        /// </summary>
        public const uint FeatherRay = 12755;

        /// <summary>
        /// First Boss: Airy Bubble.
        /// </summary>
        public const uint AiryBubble = 12756;

        /// <summary>
        /// Second Boss: Firearms.
        /// </summary>
        public const uint Firearms = 12888;

        /// <summary>
        /// Final Boss: Maulskull.
        /// </summary>
        public const uint Maulskull = 12728;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Feather Ray.
        /// </summary>
        public static readonly Vector3 FeatherRay = new(-105f, -52f, -160f);

        /// <summary>
        /// Second Boss: Firearms.
        /// </summary>
        public static readonly Vector3 Firearms = new(-85f, -210f, -155f);

        /// <summary>
        /// Third Boss: Maulskull.
        /// </summary>
        // Maulskull's 40x40 platform is centered at Z=-430: the previously correct live overlay,
        // the duty-support formation near Z=-430.015, and BossMod's independent arena definition
        // agree on this point. Impact helpers can spawn ten or more yalms away from center and must
        // not be reused as the boundary anchor.
        public static readonly Vector3 Maulskull = new(100f, -192f, -430f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Feather Ray
        /// Immersion is unavoidable party-wide damage; it intentionally has no movement avoid.
        /// </summary>
        public const uint Immersion = 36739;

        /// <summary>
        /// Feather Ray
        /// Troublesome Tail is unavoidable party-wide damage that grants the Nuisance mirror.
        /// </summary>
        public const uint TroublesomeTail = 36727;

        /// <summary>
        /// Feather Ray
        /// Worrisome Wave is a 24-yalm, 30-degree cone followed by each player's Nuisance mirror.
        /// </summary>
        public const uint WorrisomeWave = 36728;

        /// <summary>
        /// Feather Ray
        /// Hydro Ring is a 12-24-yalm donut that establishes the reduced circular arena.
        /// </summary>
        public const uint HydroRing = 36733;

        /// <summary>
        /// Feather Ray
        /// Blowing Bubbles begins Hydro Ring's first moving-bubble sequence.
        /// </summary>
        public const uint BlowingBubbles = 36732;

        /// <summary>
        /// Feather Ray
        /// Trouble Bubbles begins the later moving-bubble sequence paired with Nuisance.
        /// </summary>
        public const uint TroubleBubbles = 38787;

        /// <summary>
        /// Feather Ray
        /// Burst is the six-yalm explosion produced by a large Bubble Bomb after Rolling Current.
        /// </summary>
        public const uint Burst = 36738;

        /// <summary>
        /// Feather Ray
        /// Rolling Current East moves every large bubble eight yalms toward negative world X.
        /// </summary>
        public const uint RollingCurrentEast = 36737;

        /// <summary>
        /// Feather Ray
        /// Rolling Current West moves every large bubble eight yalms toward positive world X.
        /// </summary>
        public const uint RollingCurrentWest = 36736;

        /// <summary>
        /// Firearms
        /// Thunderlight Flurry
        /// Spread
        /// </summary>
        public static readonly HashSet<uint> ThunderlightFlurry = [36450];

        /// <summary>
        /// Firearms
        /// Thunderlight Burst
        /// Reflected laser segments and the terminal orb explosion are emitted by hidden helpers.
        /// </summary>
        public const uint ThunderlightBurstVisual = 36443;
        public const uint ThunderlightBurstCircle = 36445;
        public const uint ThunderlightBurstRect1 = 38581;
        public const uint ThunderlightBurstRect2 = 38582;
        public const uint ThunderlightBurstRect3 = 38583;
        public const uint ThunderlightBurstRect4 = 38584;

        /// <summary>
        /// Firearms
        /// Artillery
        /// Unsafe 10x10 grid tiles emitted by hidden helpers during Emergent Artillery.
        /// </summary>
        public static readonly HashSet<uint> Artillery = [38660, 38661, 38662, 38663];

        /// <summary>
        /// Maulskull's visual Deep Thunder cast. Movement is keyed from the helper tower casts
        /// instead, because this action carries no tower position.
        /// </summary>
        public const uint DeepThunder = 36687;

        /// <summary>
        /// Maulskull
        /// First Deep Thunder tower; all four duty members must remain together through this cast.
        /// </summary>
        public const uint DeepThunderTower1 = 36688;

        /// <summary>
        /// Maulskull
        /// Second Deep Thunder tower; all four duty members must remain together through this cast.
        /// </summary>
        public const uint DeepThunderTower2 = 36689;

        /// <summary>
        /// The two six-yalm Deep Thunder tower helpers used for positive positioning.
        /// </summary>
        public static readonly HashSet<uint> DeepThunderTowers = [DeepThunderTower1, DeepThunderTower2];

        /// <summary>
        /// Maulskull
        /// Destructive Heat
        /// Spread
        /// </summary>
        public static readonly HashSet<uint> DestructiveHeat = [36709];

        /// <summary>
        /// Maulskull
        /// Impact knockback helpers. These own the radial displacement and therefore the dynamic
        /// safe-zone calculation used for Skullcrush, Ringing Blows, and Colossal Impact.
        /// </summary>
        public static readonly HashSet<uint> Impact = [ImpactSkullcrush, ImpactRingingBlows, ImpactColossal];

        public const uint ImpactSkullcrush = 36677;
        public const uint ImpactRingingBlows = 36667;
        public const uint ImpactColossal = 36707;

        /// <summary>
        /// Maulskull
        /// Colossal Impact
        /// Line up around the blue circle to avoid pushback
        /// </summary>
        public static readonly HashSet<uint> ColossalImpact = [36704, 36705, 36706];

        /// <summary>
        /// Maulskull's hidden Stonecarver half-cleave helpers. Visual boss casts are intentionally
        /// excluded so the rectangle selector cannot attach geometry to the boss at arena center.
        /// </summary>
        public static readonly HashSet<uint> StonecarverHelpers =
        [
            36670,
            36671,
            RingingStonecarverEast,
            RingingStonecarverWest,
        ];

        /// <summary>
        /// The paired Stonecarver helpers following Ringing Blows. Their east-then-west ordering is
        /// used only for transition positioning; the verified rectangle geometry remains shared.
        /// </summary>
        public static readonly HashSet<uint> RingingStonecarverHelpers = [RingingStonecarverEast, RingingStonecarverWest];

        public const uint RingingStonecarverEast = 36696;
        public const uint RingingStonecarverWest = 36697;

        /// <summary>
        /// Maulwork's eight-yalm falling-rock circle helper.
        /// </summary>
        public const uint Landing = 36683;

        /// <summary>
        /// Maulwork's straight center line helper.
        /// </summary>
        public const uint ShatterCenter = 36684;

        /// <summary>
        /// Maulwork's west side line helper. Its actual cast rotation is -17 degrees even though RB
        /// reports a zero actor heading.
        /// </summary>
        public const uint ShatterSideWest = 36685;

        /// <summary>
        /// Maulwork's east side line helper. Its actual cast rotation is +17 degrees even though RB
        /// reports a zero actor heading.
        /// </summary>
        public const uint ShatterSideEast = 36686;

        /// <summary>
        /// Building Heat is the four-player stack that follows one Colossal Impact variant.
        /// </summary>
        public const uint BuildingHeat = 36710;

        /// <summary>
        /// Maulskull
        /// Wrought Fire
        /// AoE Tank Buster
        /// </summary>
        public static readonly HashSet<uint> WroughtFire = [39121, 39122];
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Feather Ray's Nuisance countdown. Troublesome Tail applies the same aura before either
        /// Worrisome Wave or Trouble Bubbles, so the following boss action selects the copied attack.
        /// </summary>
        public const uint Nuisance = 3950;

        /// <summary>
        /// Prey. Thermal Charge bomb
        /// </summary>
        public const uint Prey = 1253;
    }

    private static class PlayerVfx
    {
        /// <summary>
        /// Nuisance overhead lock-on observed on all three Duty Support actors immediately before
        /// the copied special attack; BossMod independently identifies it as icon ID 514.
        /// </summary>
        public const ulong NuisanceLockOn = 514;
    }
}
