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
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Clyteum dungeon logic.
/// </summary>
public class Clyteum : AbstractDungeon
{
    // Keep sampled destinations one yalm inside the square arena wall.
    private const float EyeArenaNavigationHalfExtent = 19.0f;
    // Keep sampled destinations half a yalm inside Chort's arena wall.
    private const float ChortArenaNavigationRadius = 14.5f;
    // Keep sampled destinations one yalm inside Malphas's arena wall.
    private const float MalphasArenaNavigationRadius = 19.0f;
    // Preposition six yalms from the source before Bodyweight's eight-yalm knockback.
    private const float BodyweightPrepositionRadius = 6.0f;
    private const float BodyweightPositionTolerance = 0.5f;
    private const int BodyweightMovementLeaseMinimumMilliseconds = 250;
    private const int PositivePositionMovementLeaseMinimumMilliseconds = 250;
    // Hold stack positions briefly after the helper cast disappears to cover delayed damage.
    private static readonly TimeSpan TargetedStackResolutionGrace = TimeSpan.FromSeconds(1);
    // Latch one spread destination and replan once only if it becomes unsafe.
    private const float TargetedSpreadSafetyBuffer = 1.5f;
    private const float TargetedSpreadReplanBuffer = 0.5f;
    private const float TargetedSpreadArrivalTolerance = 0.5f;
    private const float TargetedSpreadCandidateStep = 1.0f;
    private const float TargetedSpreadWallInset = 1.0f;
    private const int TargetedSpreadMovementLeaseMinimumMilliseconds = 250;
    private const int TargetedSpreadMaximumReplans = 1;
    private static readonly TimeSpan TargetedSpreadResolutionGrace = TimeSpan.FromMilliseconds(750);
    // Expand Petrifying Beam's 70-yalm, 100-degree cone for sampling and turn tolerance.
    private const float PetrifyingBeamAvoidRadius = 72.0f;
    private const float PetrifyingBeamAvoidArcDegrees = 105.0f;
    // Sample Shadow Play inside the arena with a half-yalm separation buffer.
    private const float ShadowPlaySpreadRadius = 6.5f;
    private const float ShadowPlayRequiredSeparation = 7.0f;
    private const float ShadowPlayPositionTolerance = 0.75f;
    private const float ShadowPlayArenaInsetRadius = 17.0f;
    private const int ShadowPlayCandidateCountPerRing = 48;
    private const int ShadowPlayMovementLeaseMinimumMilliseconds = 250;
    // Model Goekinesis helpers as bidirectional five-yalm-wide lines with extra destination clearance.
    private const float GoekinesisLineHalfWidth = 2.5f;
    private const float GoekinesisRequiredClearance = 0.75f;
    private const float GoekinesisCandidateStep = 0.5f;
    private const float GoekinesisCandidateRadius = MalphasArenaNavigationRadius - 1.0f;
    private const float GoekinesisArrivalTolerance = 0.5f;
    private const int GoekinesisMovementLeaseMinimumMilliseconds = 250;
    private static readonly TimeSpan GoekinesisResolutionGrace = TimeSpan.FromMilliseconds(400);
    // Keep moving through String Up on a fixed six-waypoint arena orbit.
    private const float StringUpOrbitRadius = 10.5f;
    private const float StringUpWaypointStepRadians = (float)(Math.PI / 3.0);
    private const float StringUpWaypointTolerance = 1.25f;
    private const int StringUpMovementLeaseMilliseconds = 1_000;
    private static readonly TimeSpan ShadowPlayResolutionGrace = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan BodyweightResolutionGrace = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StringUpPostCastFallback = TimeSpan.FromSeconds(5);
    private static readonly float[] ShadowPlayCandidateRadii = [8.0f, 12.5f, ShadowPlayArenaInsetRadius];

    // Positive-position mechanics follow their target or cast location and hold inside the safe radius.
    private readonly PositivePositionState penetratorMissilePosition = new(
        "Penetrator Missile",
        EnemyAction.PenetratorMissile,
        PositivePositionSource.CastTarget,
        arrivalDistance: 4.0f,
        regroupWhenSelfTargeted: true,
        resolutionGrace: TargetedStackResolutionGrace);
    private readonly PositivePositionState bodyweightTowerPosition = new(
        "Bodyweight Exorcism tower",
        EnemyAction.BodyweightExorcismTowers,
        PositivePositionSource.CastLocation,
        arrivalDistance: 3.0f,
        regroupWhenSelfTargeted: false,
        resolutionGrace: TimeSpan.Zero);
    private readonly PositivePositionState profanePressurePosition = new(
        "Profane Pressure",
        EnemyAction.ProfanePressure,
        PositivePositionSource.CastTarget,
        arrivalDistance: 4.0f,
        regroupWhenSelfTargeted: true,
        resolutionGrace: TargetedStackResolutionGrace);
    private readonly PositivePositionState gluttonousWirePosition = new(
        "Gluttonous Wire",
        EnemyAction.GluttonousWire,
        PositivePositionSource.CastTarget,
        arrivalDistance: 5.0f,
        regroupWhenSelfTargeted: true,
        resolutionGrace: TargetedStackResolutionGrace);

    // Player-targeted spreads latch the nearest sufficiently separated point inside their arena.
    private readonly TargetedSpreadState antiPersonnelMissileSpread = new(
        "Anti-personnel Missile",
        EnemyAction.AntipersonnelMissile,
        ArenaCenter.EyeoftheScorpion,
        SpreadArenaShape.Square,
        EyeArenaNavigationHalfExtent - TargetedSpreadWallInset,
        damageRadius: 6.0f);
    private readonly TargetedSpreadState evilEmissionSpread = new(
        "Evil Emission",
        EnemyAction.EvilEmission,
        ArenaCenter.Chort,
        SpreadArenaShape.Circle,
        ChortArenaNavigationRadius - TargetedSpreadWallInset,
        damageRadius: 5.0f);
    private readonly TargetedSpreadState wrathfulWireSpread = new(
        "Wrathful Wire",
        EnemyAction.WrathfulWire,
        ArenaCenter.Malphas,
        SpreadArenaShape.Circle,
        MalphasArenaNavigationRadius - TargetedSpreadWallInset,
        damageRadius: 5.0f);

    // Identify scanners by BaseId and use only their arena-wide forward coordinate.
    private const uint MotionScannerBaseId = 0x4C2D;
    private const float MotionScannerForwardLength = 9.5f;
    private const float MotionScannerBackwardLength = 7.5f;
    private const float MotionScannerLongitudinalSafetyMargin = 2.5f;
    // Arm only a scanner that changes state after the parent cast; expire stale cycles after 24 seconds.
    private const int MotionScannerCycleWindowMilliseconds = 24_000;
    private const float MotionScannerActivationMovementThreshold = 0.25f;
    private const int MotionTrackerMovementLeaseMilliseconds = 1_000;

    // Motion Tracker suppresses movement and consumes the tick so the routine cannot restart an action.
    private readonly CapabilityManagerHandle motionTrackerMovementHandle = CapabilityManager.CreateNewHandle();
    // Compare scalar cast-start snapshots to find the scanner activated for the current cycle.
    private readonly Dictionary<uint, Vector3> motionScannerCycleStartLocations = [];
    private readonly Dictionary<uint, bool> motionScannerCycleStartVisibility = [];
    private uint motionScannerLatchedObjectId;
    private bool motionTrackerAuraObserved;
    private bool motionScannerCastActive;
    private DateTime motionScannerCycleExpiresAtUtc = DateTime.MinValue;
    private bool motionTrackerMovementOwned;

    // Stop Mortifying Flesh's stale low-level move command after its avoid resolves.
    private bool mortifyingFleshAvoidanceObserved;

    // Bodyweight holds one knockback-safe destination instead of publishing a donut avoid.
    private readonly CapabilityManagerHandle bodyweightMovementHandle = CapabilityManager.CreateNewHandle();
    private Vector3 bodyweightDestination;
    private DateTime bodyweightHoldUntilUtc = DateTime.MinValue;
    private bool bodyweightDestinationActive;
    private bool bodyweightMovementOwned;

    // Shadow Play holds one isolation point under its own movement lease.
    private readonly CapabilityManagerHandle shadowPlayMovementHandle = CapabilityManager.CreateNewHandle();
    private Vector3 shadowPlayDestination;
    private DateTime shadowPlayHoldUntilUtc = DateTime.MinValue;
    private bool shadowPlayDestinationActive;
    private bool shadowPlayMovementOwned;

    // Goekinesis plans every helper line as one wave and holds one destination under its own lease.
    private readonly CapabilityManagerHandle goekinesisMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly List<GoekinesisLineSnapshot> goekinesisLines = [];
    private Vector3 goekinesisDestination;
    private DateTime goekinesisHoldUntilUtc = DateTime.MinValue;
    private string goekinesisHelperSignature;
    private bool goekinesisWaveActive;
    private bool goekinesisDestinationActive;
    private bool goekinesisMovementOwned;
    private bool goekinesisDestinationUnavailableLogged;

    // String Up advances fixed waypoints under its own movement lease.
    private readonly CapabilityManagerHandle stringUpMovementHandle = CapabilityManager.CreateNewHandle();
    private Vector3 stringUpWaypoint;
    private DateTime stringUpKeepMovingUntilUtc = DateTime.MinValue;
    private bool stringUpCountdownObserved;
    private bool stringUpFailureLogged;
    private bool stringUpMovementActive;
    private bool stringUpMovementOwned;

    /// <summary>Tracks the previous sub-zone for encounter cleanup.</summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Clyteum;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        EnemyAction.EyesOnMe,
        EnemyAction.RipplesOfGloom,
        EnemyAction.RubbishDisposal,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.ShadowPlay];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {


        SideStep.Override(EnemyAction.BodyweightExorcism);
        SideStep.Override(EnemyAction.BodyweightExorcismTowers);
        SideStep.Override(17995); // Skyshard LB

        // DutyMechanic owns these latched spread destinations.
        SideStep.Override(EnemyAction.AntipersonnelMissile);
        SideStep.Override(EnemyAction.EvilEmission);
        SideStep.Override(EnemyAction.WrathfulWire);

        // DutyMechanic handles these unavoidable raidwides as mitigation events.
        SideStep.Override(EnemyAction.RipplesOfGloom);
        SideStep.Override(EnemyAction.RubbishDisposal);

        // DutyMechanic owns Void Dark; SideStep retains Puppet Strings helper geometry.
        SideStep.Override(EnemyAction.VoidDark);

        // DutyMechanic owns Goekinesis as one multi-line wave.
        SideStep.Override(EnemyAction.Goekinesis);

        // Boss 1: Petrifying Beam
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ForumFloricomum,
            // Only the helper casts own Petrifying Beam geometry.
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.PetrifyingBeamFirst or EnemyAction.PetrifyingBeamSecond,
            leashPointProducer: () => ArenaCenter.EyeoftheScorpion,
            leashRadius: 80.0f,
            rotationDegrees: 0.0f,
            radius: PetrifyingBeamAvoidRadius,
            arcDegrees: PetrifyingBeamAvoidArcDegrees);

        // Boss 3: Void Dark
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.VoidDark,
            // Keep cone path sampling centered on Malphas's arena.
            leashPointProducer: () => ArenaCenter.Malphas,
            leashRadius: 60.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 185.0f);

        // Shadow Play helper 50314 owns the target-specific avoid.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            objectSelector: bc =>
                bc.IsCasting &&
                bc.CastingSpellId == EnemyAction.ShadowPlay &&
                bc.SpellCastInfo.IsValid &&
                bc.SpellCastInfo.TargetId != 0 &&
                bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: _ => ShadowPlaySpreadRadius,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ForumFloricomum,
            innerWidth: EyeArenaNavigationHalfExtent * 2.0f,
            innerHeight: EyeArenaNavigationHalfExtent * 2.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.EyeoftheScorpion],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Stockyard,
            () => ArenaCenter.Chort,
            outerRadius: 90.0f,
            innerRadius: ChortArenaNavigationRadius,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            () => ArenaCenter.Malphas,
            outerRadius: 90.0f,
            innerRadius: MalphasArenaNavigationRadius,
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetPositivePositioning("leaving Clyteum");
        ResetTargetedSpreads("leaving Clyteum");
        ResetMotionTrackerState("leaving Clyteum");
        ResetChortState("leaving Clyteum");
        ResetGoekinesisState("leaving Clyteum");
        ResetShadowPlayState("leaving Clyteum");
        ResetStringUpMovement("leaving Clyteum");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await TankBusterSpells();
        await DamageMitigationSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;
        if (lastSubZoneId != SubZoneId.NONE && currentSubZoneId != lastSubZoneId)
        {
            ResetPositivePositioning($"sub-zone changed from {lastSubZoneId} to {currentSubZoneId}");
            ResetTargetedSpreads($"sub-zone changed from {lastSubZoneId} to {currentSubZoneId}");
            ResetMotionTrackerState($"sub-zone changed from {lastSubZoneId} to {currentSubZoneId}");
            ResetGoekinesisState($"sub-zone changed from {lastSubZoneId} to {currentSubZoneId}");
        }

        bool result = currentSubZoneId switch
        {
            SubZoneId.ForumFloricomum => await EyeoftheScorpion(),
            SubZoneId.Stockyard => await Chort(),
            SubZoneId.SecureTestSite => await Malphas(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>Moves to and holds a stack target or tower cast location.</summary>
    private async Task<bool> HandlePositivePositionAsync(PositivePositionState state)
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter caster = GetPositivePositionCaster(state);
        Vector3 destination;

        if (caster != null)
        {
            if (state.ActiveCasterObjectId != 0 && state.ActiveCasterObjectId != caster.ObjectId)
            {
                ReleasePositivePosition(state, $"{state.Name} changed caster");
            }

            state.ActiveCasterObjectId = caster.ObjectId;
            state.HoldUntilUtc = now + caster.SpellCastInfo.RemainingCastTime + state.ResolutionGrace;

            if (TryGetPositivePositionDestination(caster, state, out destination))
            {
                state.LastDestination = destination;
                state.HasLastDestination = true;
            }
            else if (state.HasLastDestination)
            {
                destination = state.LastDestination;
            }
            else
            {
                StopPositivePositionMovement(state, $"{state.Name} destination unavailable");
                if (!state.DestinationUnavailableLogged)
                {
                    state.DestinationUnavailableLogged = true;
                    Logger.Warning(
                        $"[Clyteum] {state.Name} action={state.ActionId} caster=0x{caster.ObjectId:X8} " +
                        $"target=0x{caster.SpellCastInfo.TargetId:X8} has no resolvable semantic destination; movement left schedulable.");
                }

                return false;
            }
        }
        else
        {
            if (state.ActiveCasterObjectId == 0)
            {
                return false;
            }

            if (!state.HasLastDestination || now >= state.HoldUntilUtc)
            {
                ReleasePositivePosition(state, $"{state.Name} cast and resolution grace ended");
                return false;
            }

            // Hold the last verified group point through delayed damage.
            destination = state.LastDestination;
        }

        if (!state.MovementOwned)
        {
            state.DestinationUnavailableLogged = false;
        }

        int leaseMilliseconds = Math.Max(
            PositivePositionMovementLeaseMinimumMilliseconds,
            (int)Math.Ceiling((state.HoldUntilUtc - now).TotalMilliseconds));
        CapabilityManager.Update(
            state.MovementHandle,
            CapabilityFlags.Movement,
            leaseMilliseconds,
            $"Holding {state.Name}'s cast-defined positive position");
        state.MovementOwned = true;

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(destination) <= state.ArrivalDistance)
        {
            Navigator.PlayerMover.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>Latches the current caster, choosing staggered towers by resolution order.</summary>
    private static BattleCharacter GetPositivePositionCaster(PositivePositionState state)
    {
        List<BattleCharacter> casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(bc =>
                bc.IsValid &&
                bc.IsCasting &&
                bc.CastingSpellId == state.ActionId &&
                bc.SpellCastInfo.IsValid)
            .ToList();

        BattleCharacter latched = casters.FirstOrDefault(bc => bc.ObjectId == state.ActiveCasterObjectId);
        if (latched != null)
        {
            return latched;
        }

        return state.Source == PositivePositionSource.CastLocation
            ? casters
                .OrderBy(bc => bc.SpellCastInfo.RemainingCastTime)
                .ThenBy(bc => Core.Player.Distance2D(bc.SpellCastInfo.CastLocation))
                .FirstOrDefault()
            : casters
                .OrderBy(bc => bc.SpellCastInfo.RemainingCastTime)
                .FirstOrDefault();
    }

    /// <summary>Resolves a stack target or ground-cast tower destination.</summary>
    private static bool TryGetPositivePositionDestination(
        BattleCharacter caster,
        PositivePositionState state,
        out Vector3 destination)
    {
        if (state.Source == PositivePositionSource.CastLocation)
        {
            destination = caster.SpellCastInfo.CastLocation;
            return Math.Abs(destination.X) + Math.Abs(destination.Y) + Math.Abs(destination.Z) > 0.01f;
        }

        if (state.RegroupWhenSelfTargeted && caster.SpellCastInfo.TargetId == Core.Player.ObjectId)
        {
            BattleCharacter partyAnchor = GetSelfTargetStackPartyAnchor(state);
            if (partyAnchor == null)
            {
                destination = default;
                return false;
            }

            destination = partyAnchor.Location;
            return true;
        }

        GameObject target = GameObjectManager.GetObjectByObjectId(caster.SpellCastInfo.TargetId);
        if (target == null || !target.IsValid)
        {
            destination = default;
            return false;
        }

        destination = target.Location;
        return true;
    }

    /// <summary>Latches the living party member closest to the party's center.</summary>
    private static BattleCharacter GetSelfTargetStackPartyAnchor(PositivePositionState state)
    {
        List<BattleCharacter> livingPartyMembers = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member =>
                member != null &&
                member.IsValid &&
                member.IsAlive &&
                member.ObjectId != Core.Player.ObjectId)
            .ToList();

        BattleCharacter latched = livingPartyMembers
            .FirstOrDefault(member => member.ObjectId == state.SelfTargetPartyAnchorObjectId);
        if (latched != null)
        {
            return latched;
        }

        BattleCharacter selected = livingPartyMembers
            .OrderBy(candidate => livingPartyMembers.Sum(other => candidate.Distance2D(other)))
            .ThenBy(candidate => Core.Player.Distance2D(candidate))
            .ThenBy(candidate => candidate.ObjectId)
            .FirstOrDefault();

        state.SelfTargetPartyAnchorObjectId = selected?.ObjectId ?? 0;
        return selected;
    }

    /// <summary>Clears all positive-position mechanic state.</summary>
    private void ResetPositivePositioning(string reason)
    {
        ReleasePositivePosition(penetratorMissilePosition, reason);
        ReleasePositivePosition(bodyweightTowerPosition, reason);
        ReleasePositivePosition(profanePressurePosition, reason);
        ReleasePositivePosition(gluttonousWirePosition, reason);
    }

    /// <summary>Releases one positive-position mechanic.</summary>
    private static void ReleasePositivePosition(PositivePositionState state, string reason)
    {
        StopPositivePositionMovement(state, reason);
        state.ActiveCasterObjectId = 0;
        state.SelfTargetPartyAnchorObjectId = 0;
        state.HoldUntilUtc = DateTime.MinValue;
        state.LastDestination = default;
        state.HasLastDestination = false;
        state.DestinationUnavailableLogged = false;
    }

    /// <summary>Stops movement owned by one positive-position mechanic.</summary>
    private static void StopPositivePositionMovement(PositivePositionState state, string reason)
    {
        if (!state.MovementOwned)
        {
            return;
        }

        if (!AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        CapabilityManager.Clear(state.MovementHandle, CapabilityFlags.Movement, reason);
        state.MovementOwned = false;
    }

    /// <summary>Moves a player-targeted spread to one latched arena-local destination.</summary>
    private async Task<bool> HandleTargetedSpreadAsync(TargetedSpreadState state)
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter caster = GetSelfTargetedSpreadCaster(state.ActionId);

        if (caster != null)
        {
            if (state.ActiveCasterObjectId != 0 && state.ActiveCasterObjectId != caster.ObjectId)
            {
                ReleaseTargetedSpread(state, $"{state.Name} changed caster");
            }

            state.ActiveCasterObjectId = caster.ObjectId;
            state.HoldUntilUtc = now + caster.SpellCastInfo.RemainingCastTime + TargetedSpreadResolutionGrace;

            bool mustSelect = !state.DestinationActive;
            if (!mustSelect && state.ReplanCount < TargetedSpreadMaximumReplans &&
                IsTargetedSpreadDestinationInvalid(state))
            {
                mustSelect = true;
            }

            if (mustSelect)
            {
                if (!TrySelectTargetedSpreadDestination(
                        state,
                        out Vector3 destination))
                {
                    StopTargetedSpreadMovement(state, $"{state.Name} has no arena-local candidate");
                    if (!state.DestinationUnavailableLogged)
                    {
                        state.DestinationUnavailableLogged = true;
                        Logger.Warning(
                            $"[Clyteum] {state.Name} action={state.ActionId} has no arena-local spread destination; " +
                            "movement left schedulable for registered avoidance.");
                    }

                    return false;
                }

                bool replacingDestination = state.DestinationActive;
                state.Destination = destination;
                state.DestinationActive = true;
                state.DestinationUnavailableLogged = false;
                if (replacingDestination)
                {
                    state.ReplanCount++;
                }
            }
        }
        else if (!state.DestinationActive)
        {
            return false;
        }

        if (now >= state.HoldUntilUtc)
        {
            ReleaseTargetedSpread(state, $"{state.Name} cast and resolution grace ended");
            return false;
        }

        int leaseMilliseconds = Math.Max(
            TargetedSpreadMovementLeaseMinimumMilliseconds,
            (int)Math.Ceiling((state.HoldUntilUtc - now).TotalMilliseconds));
        CapabilityManager.Update(
            state.MovementHandle,
            CapabilityFlags.Movement,
            leaseMilliseconds,
            $"Holding {state.Name}'s stable spread destination");
        state.MovementOwned = true;

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(state.Destination) <= TargetedSpreadArrivalTolerance)
        {
            Navigator.PlayerMover.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(state.Destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>Finds the earliest spread helper targeting the local player.</summary>
    private static BattleCharacter GetSelfTargetedSpreadCaster(uint actionId)
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(bc =>
                bc.IsValid &&
                bc.IsCasting &&
                bc.CastingSpellId == actionId &&
                bc.SpellCastInfo.IsValid &&
                bc.SpellCastInfo.TargetId == Core.Player.ObjectId)
            .OrderBy(bc => bc.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();
    }

    /// <summary>Selects the nearest sufficiently separated point, or the best available fallback.</summary>
    private static bool TrySelectTargetedSpreadDestination(
        TargetedSpreadState state,
        out Vector3 destination)
    {
        List<Vector3> partyLocations = GetOtherLivingPartyLocations();
        Vector3 bestValid = default;
        float bestValidTravel = float.MaxValue;
        float bestValidSeparation = float.MinValue;
        Vector3 bestFallback = default;
        float bestFallbackSeparation = float.MinValue;
        float bestFallbackTravel = float.MaxValue;
        bool foundCandidate = false;
        bool foundValid = false;

        foreach (Vector3 candidate in EnumerateTargetedSpreadCandidates(state))
        {
            if (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(candidate)))
            {
                continue;
            }

            foundCandidate = true;
            float separation = GetMinimumSeparation(candidate, partyLocations);
            float travel = Core.Player.Distance2D(candidate);

            // Prefer the shortest point that meets the full separation reserve.
            if (separation >= state.RequiredSeparation &&
                (!foundValid || travel < bestValidTravel - 0.05f ||
                 (Math.Abs(travel - bestValidTravel) <= 0.05f && separation > bestValidSeparation)))
            {
                bestValid = candidate;
                bestValidTravel = travel;
                bestValidSeparation = separation;
                foundValid = true;
            }

            if (separation > bestFallbackSeparation ||
                (Math.Abs(separation - bestFallbackSeparation) <= 0.05f && travel < bestFallbackTravel))
            {
                bestFallback = candidate;
                bestFallbackSeparation = separation;
                bestFallbackTravel = travel;
            }
        }

        destination = foundValid ? bestValid : bestFallback;
        return foundCandidate;
    }

    /// <summary>Enumerates the current point followed by the arena's one-yalm candidate grid.</summary>
    private static IEnumerable<Vector3> EnumerateTargetedSpreadCandidates(TargetedSpreadState state)
    {
        if (IsInsideTargetedSpreadArena(Core.Player.Location, state))
        {
            yield return Core.Player.Location;
        }

        for (float xOffset = -state.CandidateExtent;
             xOffset <= state.CandidateExtent + 0.01f;
             xOffset += TargetedSpreadCandidateStep)
        {
            for (float zOffset = -state.CandidateExtent;
                 zOffset <= state.CandidateExtent + 0.01f;
                 zOffset += TargetedSpreadCandidateStep)
            {
                Vector3 candidate = new(
                    state.ArenaCenter.X + xOffset,
                    state.ArenaCenter.Y,
                    state.ArenaCenter.Z + zOffset);
                if (IsInsideTargetedSpreadArena(candidate, state))
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Checks whether a point is inside the configured inset arena.</summary>
    private static bool IsInsideTargetedSpreadArena(Vector3 point, TargetedSpreadState state)
    {
        if (state.ArenaShape == SpreadArenaShape.Square)
        {
            return Math.Abs(point.X - state.ArenaCenter.X) <= state.CandidateExtent &&
                   Math.Abs(point.Z - state.ArenaCenter.Z) <= state.CandidateExtent;
        }

        return point.Distance2D(state.ArenaCenter) <= state.CandidateExtent;
    }

    /// <summary>Reports whether a spread destination is covered or no longer separated.</summary>
    private static bool IsTargetedSpreadDestinationInvalid(TargetedSpreadState state)
    {
        if (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(state.Destination)))
        {
            return true;
        }

        float separation = GetMinimumSeparation(state.Destination, GetOtherLivingPartyLocations());
        if (separation < state.DamageRadius + TargetedSpreadReplanBuffer)
        {
            return true;
        }

        return false;
    }

    /// <summary>Snapshots the positions of other visible living party members.</summary>
    private static List<Vector3> GetOtherLivingPartyLocations()
    {
        return PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member =>
                member != null &&
                member.IsValid &&
                member.IsAlive &&
                member.ObjectId != Core.Player.ObjectId)
            .Select(member => member.Location)
            .ToList();
    }

    /// <summary>Returns the nearest party-member distance from a candidate point.</summary>
    private static float GetMinimumSeparation(Vector3 point, IReadOnlyCollection<Vector3> partyLocations)
    {
        return partyLocations.Count == 0
            ? float.PositiveInfinity
            : partyLocations.Min(location => point.Distance2D(location));
    }

    /// <summary>Clears all targeted-spread state.</summary>
    private void ResetTargetedSpreads(string reason)
    {
        ReleaseTargetedSpread(antiPersonnelMissileSpread, reason);
        ReleaseTargetedSpread(evilEmissionSpread, reason);
        ReleaseTargetedSpread(wrathfulWireSpread, reason);
    }

    /// <summary>Releases one targeted spread.</summary>
    private static void ReleaseTargetedSpread(TargetedSpreadState state, string reason)
    {
        StopTargetedSpreadMovement(state, reason);
        state.ActiveCasterObjectId = 0;
        state.HoldUntilUtc = DateTime.MinValue;
        state.Destination = default;
        state.DestinationActive = false;
        state.DestinationUnavailableLogged = false;
        state.ReplanCount = 0;
    }

    /// <summary>Stops movement owned by one targeted spread.</summary>
    private static void StopTargetedSpreadMovement(TargetedSpreadState state, string reason)
    {
        if (!state.MovementOwned)
        {
            return;
        }

        if (!AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        CapabilityManager.Clear(state.MovementHandle, CapabilityFlags.Movement, reason);
        state.MovementOwned = false;
    }

    /// <summary>
    /// Boss 1: EyeoftheScorpion.
    /// </summary>
    private async Task<bool> EyeoftheScorpion()
    {
        if (!Core.Player.InCombat || !Core.Player.IsAlive)
        {
            ReleasePositivePosition(penetratorMissilePosition, "Eye of the Scorpion combat ended");
            ReleaseTargetedSpread(antiPersonnelMissileSpread, "Eye of the Scorpion combat ended");
            ResetMotionTrackerState("Eye of the Scorpion combat ended");
            return false;
        }

        // Motion Tracker consumes the tick because both actions and movement must remain stopped.
        if (HandleMotionTrackerHold())
        {
            return true;
        }

        if (await HandlePositivePositionAsync(penetratorMissilePosition))
        {
            return true;
        }

        if (await HandleTargetedSpreadAsync(antiPersonnelMissileSpread))
        {
            return true;
        }

        return false;
    }

    /// <summary>Stops movement and actions while the active scanner or fallback aura covers the player.</summary>
    private bool HandleMotionTrackerHold()
    {
        DateTime nowUtc = DateTime.UtcNow;
        bool auraActive = Core.Me.HasAura(PlayerAura.MotionTracker);
        List<GameObject> scannerCandidates = GetMotionScannerCandidates();

        bool scannerCastActive = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.MotionScanner);
        if (scannerCastActive && !motionScannerCastActive)
        {
            motionScannerCastActive = true;
            BeginMotionScannerCycle(nowUtc, scannerCandidates);
        }
        else if (!scannerCastActive)
        {
            motionScannerCastActive = false;
        }

        bool scannerCycleActive = nowUtc < motionScannerCycleExpiresAtUtc;
        if (!scannerCycleActive && motionScannerCycleExpiresAtUtc != DateTime.MinValue)
        {
            ClearMotionScannerCycleEvidence();
        }

        if (scannerCycleActive && !scannerCastActive && motionScannerLatchedObjectId == 0)
        {
            TryLatchActivatedMotionScanner(scannerCandidates);
        }

        GameObject scanner = GetMotionScannerCoveringPlayer(scannerCandidates);

        if (auraActive)
        {
            motionTrackerAuraObserved = true;
        }
        else if (motionTrackerAuraObserved)
        {
            // Status loss completes the scanner cycle.
            motionTrackerAuraObserved = false;
            ClearMotionScannerCycleEvidence();
            ReleaseMotionTrackerHold("Motion Tracker status cleared");
            return false;
        }

        if (!auraActive && scanner == null)
        {
            ReleaseMotionTrackerHold("Motion Tracker status and scanner sweep cleared");
            return false;
        }

        CapabilityManager.Update(
            motionTrackerMovementHandle,
            CapabilityFlags.Movement,
            MotionTrackerMovementLeaseMilliseconds,
            "Holding still for Eye of the Scorpion's Motion Tracker");

        motionTrackerMovementOwned = true;

        // Cancel the current action and every movement layer for this tick.
        ActionManager.StopCasting();
        Core.Me.ClearTarget();
        Navigator.Stop();
        Navigator.PlayerMover.MoveStop();
        MovementManager.MoveStop();
        return true;
    }

    /// <summary>Snapshots scanner state when the parent cast begins.</summary>
    private void BeginMotionScannerCycle(DateTime nowUtc, IEnumerable<GameObject> candidates)
    {
        motionScannerCycleStartLocations.Clear();
        motionScannerCycleStartVisibility.Clear();
        motionScannerLatchedObjectId = 0;
        motionScannerCycleExpiresAtUtc = nowUtc.AddMilliseconds(MotionScannerCycleWindowMilliseconds);

        foreach (GameObject scanner in candidates.Where(candidate => candidate.BaseId == MotionScannerBaseId))
        {
            motionScannerCycleStartLocations[scanner.ObjectId] = scanner.Location;
            motionScannerCycleStartVisibility[scanner.ObjectId] = scanner.IsVisible;
        }
    }

    /// <summary>Latches the scanner that appears, moves, or becomes visible after the parent cast.</summary>
    private void TryLatchActivatedMotionScanner(IEnumerable<GameObject> candidates)
    {
        GameObject bestCandidate = null;
        int bestEvidencePriority = 0;
        float bestDisplacement = 0.0f;

        foreach (GameObject candidate in candidates.Where(scanner => scanner.BaseId == MotionScannerBaseId))
        {
            bool hadStartLocation = motionScannerCycleStartLocations.TryGetValue(candidate.ObjectId, out Vector3 startLocation);
            bool hadStartVisibility = motionScannerCycleStartVisibility.TryGetValue(candidate.ObjectId, out bool wasVisible);
            float displacement = hadStartLocation
                ? HorizontalDistance(startLocation, candidate.Location)
                : 0.0f;

            int evidencePriority;
            if (!hadStartLocation)
            {
                evidencePriority = 3;
            }
            else if (displacement >= MotionScannerActivationMovementThreshold)
            {
                evidencePriority = 2;
            }
            else if (hadStartVisibility && !wasVisible && candidate.IsVisible)
            {
                evidencePriority = 1;
            }
            else
            {
                continue;
            }

            if (bestCandidate == null ||
                evidencePriority > bestEvidencePriority ||
                evidencePriority == bestEvidencePriority && displacement > bestDisplacement)
            {
                bestCandidate = candidate;
                bestEvidencePriority = evidencePriority;
                bestDisplacement = displacement;
            }
        }

        if (bestCandidate == null)
        {
            return;
        }

        motionScannerLatchedObjectId = bestCandidate.ObjectId;
    }

    /// <summary>Calculates horizontal X/Z displacement.</summary>
    private static float HorizontalDistance(Vector3 from, Vector3 to)
    {
        float deltaX = to.X - from.X;
        float deltaZ = to.Z - from.Z;
        return (float)Math.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
    }

    /// <summary>Returns active scanner objects.</summary>
    private static List<GameObject> GetMotionScannerCandidates()
    {
        return GameObjectManager.GameObjects
            .Where(obj =>
                obj != null &&
                obj.IsValid &&
                obj.BaseId == MotionScannerBaseId)
            .OrderBy(obj => obj.ObjectId)
            .ToList();
    }

    /// <summary>Finds the active scanner when its arena-wide forward window reaches the player.</summary>
    private GameObject GetMotionScannerCoveringPlayer(IEnumerable<GameObject> candidates)
    {
        GameObject scanner = candidates.FirstOrDefault(obj =>
            obj.BaseId == MotionScannerBaseId && obj.ObjectId == motionScannerLatchedObjectId);
        if (scanner != null)
        {
            float forward = GetMotionScannerForwardCoordinate(scanner);
            if (IsWithinMotionScannerPredictiveWindow(forward))
            {
                return scanner;
            }
        }

        return null;
    }

    /// <summary>Returns the player's scanner-relative forward coordinate.</summary>
    private static float GetMotionScannerForwardCoordinate(GameObject scanner)
    {
        float deltaX = Core.Player.Location.X - scanner.Location.X;
        float deltaZ = Core.Player.Location.Z - scanner.Location.Z;
        float sine = (float)Math.Sin(scanner.Heading);
        float cosine = (float)Math.Cos(scanner.Heading);
        return deltaX * sine + deltaZ * cosine;
    }

    /// <summary>Tests whether the arena-wide line is within the player's longitudinal hold window.</summary>
    private static bool IsWithinMotionScannerPredictiveWindow(float localForward)
    {
        return localForward >= -MotionScannerBackwardLength - MotionScannerLongitudinalSafetyMargin &&
               localForward <= MotionScannerForwardLength + MotionScannerLongitudinalSafetyMargin;
    }

    /// <summary>Clears scanner activation state.</summary>
    private void ClearMotionScannerCycleEvidence()
    {
        motionScannerCycleStartLocations.Clear();
        motionScannerCycleStartVisibility.Clear();
        motionScannerLatchedObjectId = 0;
        motionScannerCycleExpiresAtUtc = DateTime.MinValue;
    }

    /// <summary>Releases the Motion Tracker movement hold.</summary>
    private void ReleaseMotionTrackerHold(string reason)
    {
        if (!motionTrackerMovementOwned)
        {
            return;
        }

        CapabilityManager.Clear(motionTrackerMovementHandle, CapabilityFlags.Movement, reason);
        motionTrackerMovementOwned = false;
    }

    /// <summary>Clears all Motion Tracker and scanner-cycle state.</summary>
    private void ResetMotionTrackerState(string reason)
    {
        ReleaseMotionTrackerHold(reason);
        ClearMotionScannerCycleEvidence();
        motionTrackerAuraObserved = false;
        motionScannerCastActive = false;
    }

    /// <summary>
    /// Boss 2: Chort.
    /// </summary>
    private async Task<bool> Chort()
    {
        if (!Core.Player.InCombat)
        {
            ResetChortState("Chort combat ended");
            ReleasePositivePosition(bodyweightTowerPosition, "Chort combat ended");
            ReleasePositivePosition(profanePressurePosition, "Chort combat ended");
            ReleaseTargetedSpread(evilEmissionSpread, "Chort combat ended");
            return false;
        }

        if (await HandlePositivePositionAsync(bodyweightTowerPosition))
        {
            return true;
        }

        if (await HandlePositivePositionAsync(profanePressurePosition))
        {
            return true;
        }

        if (await HandleTargetedSpreadAsync(evilEmissionSpread))
        {
            return true;
        }

        if (StopResolvedMortifyingFleshMover())
        {
            return true;
        }

        return await HandleBodyweightExorcismAsync();
    }

    /// <summary>Stops Mortifying Flesh's low-level move command after the avoid resolves.</summary>
    private bool StopResolvedMortifyingFleshMover()
    {
        bool helperActive = IsMortifyingFleshRectangleActive();

        if (helperActive && AvoidanceManager.IsRunningOutOfAvoid)
        {
            mortifyingFleshAvoidanceObserved = true;
            return false;
        }

        if (!mortifyingFleshAvoidanceObserved || AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        Navigator.PlayerMover.MoveStop();
        mortifyingFleshAvoidanceObserved = false;
        return true;
    }

    /// <summary>Prepositions inside Bodyweight's knockback-safe radius and holds through resolution.</summary>
    private async Task<bool> HandleBodyweightExorcismAsync()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter helper = GetActiveBodyweightExorcismHelper();

        if (helper != null)
        {
            bodyweightHoldUntilUtc = now + helper.SpellCastInfo.RemainingCastTime + BodyweightResolutionGrace;
            if (!bodyweightDestinationActive)
            {
                bodyweightDestination = SelectBodyweightPreposition(helper.Location);
                bodyweightDestinationActive = true;
            }
        }
        else if (!bodyweightDestinationActive)
        {
            return false;
        }

        if (now >= bodyweightHoldUntilUtc)
        {
            ResetBodyweightMovement("knockback cast and resolution grace ended");
            return false;
        }

        int leaseMilliseconds = Math.Max(
            BodyweightMovementLeaseMinimumMilliseconds,
            (int)Math.Ceiling((bodyweightHoldUntilUtc - now).TotalMilliseconds));
        CapabilityManager.Update(
            bodyweightMovementHandle,
            CapabilityFlags.Movement,
            leaseMilliseconds,
            "Holding Bodyweight Exorcism knockback preposition");
        bodyweightMovementOwned = true;

        // Wait for Mortifying Flesh to finish before starting the knockback preposition.
        if (IsMortifyingFleshRectangleActive() || AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(bodyweightDestination) <= BodyweightPositionTolerance)
        {
            Navigator.PlayerMover.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(bodyweightDestination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>Reports whether either Mortifying Flesh rectangle is still casting.</summary>
    private static bool IsMortifyingFleshRectangleActive()
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(bc =>
                bc.IsValid &&
                bc.IsCasting &&
                bc.CastingSpellId is EnemyAction.MortifyingFleshFirst or EnemyAction.MortifyingFleshSecond);
    }

    /// <summary>Finds the active Bodyweight Exorcism knockback helper.</summary>
    private static BattleCharacter GetActiveBodyweightExorcismHelper()
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc =>
                bc.IsValid &&
                bc.IsCasting &&
                bc.CastingSpellId == EnemyAction.BodyweightExorcism &&
                bc.SpellCastInfo.IsValid);
    }

    /// <summary>Keeps the current point inside six yalms or projects it onto that radius.</summary>
    private static Vector3 SelectBodyweightPreposition(Vector3 source)
    {
        float offsetX = Core.Player.Location.X - source.X;
        float offsetZ = Core.Player.Location.Z - source.Z;
        float distance = (float)Math.Sqrt((offsetX * offsetX) + (offsetZ * offsetZ));
        if (distance <= BodyweightPrepositionRadius || distance < 0.01f)
        {
            return Core.Player.Location;
        }

        float scale = BodyweightPrepositionRadius / distance;
        return new Vector3(
            source.X + (offsetX * scale),
            ArenaCenter.Chort.Y,
            source.Z + (offsetZ * scale));
    }

    /// <summary>Clears Chort-specific movement state.</summary>
    private void ResetChortState(string reason)
    {
        mortifyingFleshAvoidanceObserved = false;
        ResetBodyweightMovement(reason);
    }

    /// <summary>Releases Bodyweight's destination and movement lease.</summary>
    private void ResetBodyweightMovement(string reason)
    {
        if (bodyweightMovementOwned)
        {
            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            CapabilityManager.Clear(bodyweightMovementHandle, CapabilityFlags.Movement, reason);
            bodyweightMovementOwned = false;
        }

        bodyweightDestinationActive = false;
        bodyweightHoldUntilUtc = DateTime.MinValue;
        bodyweightDestination = default;
    }

    /// <summary>
    /// Boss 3: Malphas
    /// </summary>
    private async Task<bool> Malphas()
    {
        if (!Core.Player.InCombat)
        {
            ReleasePositivePosition(gluttonousWirePosition, "Malphas combat ended");
            ReleaseTargetedSpread(wrathfulWireSpread, "Malphas combat ended");
            ResetGoekinesisState("Malphas combat ended");
            ResetShadowPlayState("Malphas combat ended");
            ResetStringUpMovement("Malphas combat ended");
            return false;
        }

        if (await HandleGoekinesisAsync())
        {
            return true;
        }

        if (HandleStringUpMovement())
        {
            return true;
        }

        if (await HandlePositivePositionAsync(gluttonousWirePosition))
        {
            return true;
        }

        if (await HandleTargetedSpreadAsync(wrathfulWireSpread))
        {
            return true;
        }

        return await HandleShadowPlayAsync();
    }

    /// <summary>Plans one stable destination against every active bidirectional Goekinesis line.</summary>
    private async Task<bool> HandleGoekinesisAsync()
    {
        DateTime now = DateTime.UtcNow;
        List<BattleCharacter> helpers = GetActiveGoekinesisHelpers();
        bool destinationNeedsSelection = false;

        if (helpers.Count > 0)
        {
            List<GoekinesisLineSnapshot> activeLines = helpers
                .Select(helper => new GoekinesisLineSnapshot(helper.ObjectId, helper.Location, helper.Heading))
                .ToList();
            string helperSignature = string.Join(",", activeLines.Select(line => line.ObjectId.ToString("X8", CultureInfo.InvariantCulture)));
            DateTime observedHoldUntil = now + helpers.Max(helper => helper.SpellCastInfo.RemainingCastTime) + GoekinesisResolutionGrace;
            if (observedHoldUntil > goekinesisHoldUntilUtc)
            {
                goekinesisHoldUntilUtc = observedHoldUntil;
            }

            bool helperSetChanged = !string.Equals(goekinesisHelperSignature, helperSignature, StringComparison.Ordinal);
            if (!goekinesisWaveActive)
            {
                goekinesisWaveActive = true;
                destinationNeedsSelection = true;
            }
            else if (helperSetChanged &&
                     (!goekinesisDestinationActive ||
                      GetMinimumGoekinesisClearance(goekinesisDestination, activeLines) < GoekinesisRequiredClearance))
            {
                destinationNeedsSelection = true;
            }

            if (helperSetChanged)
            {
                goekinesisHelperSignature = helperSignature;
                goekinesisLines.Clear();
                goekinesisLines.AddRange(activeLines);
            }
        }
        else if (!goekinesisWaveActive)
        {
            return false;
        }

        if (now >= goekinesisHoldUntilUtc)
        {
            ResetGoekinesisState("helper casts and resolution grace ended");
            return false;
        }

        int leaseMilliseconds = Math.Max(
            GoekinesisMovementLeaseMinimumMilliseconds,
            (int)Math.Ceiling((goekinesisHoldUntilUtc - now).TotalMilliseconds));
        CapabilityManager.Update(
            goekinesisMovementHandle,
            CapabilityFlags.Movement,
            leaseMilliseconds,
            "Holding Goekinesis's stable wave destination");
        goekinesisMovementOwned = true;

        if (!destinationNeedsSelection && goekinesisDestinationActive &&
            AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(goekinesisDestination)))
        {
            destinationNeedsSelection = true;
        }

        if (!goekinesisDestinationActive)
        {
            destinationNeedsSelection = true;
        }

        if (destinationNeedsSelection)
        {
            if (!TrySelectGoekinesisDestination(
                    goekinesisLines,
                    out Vector3 destination))
            {
                if (!AvoidanceManager.IsRunningOutOfAvoid)
                {
                    Navigator.PlayerMover.MoveStop();
                }

                if (!goekinesisDestinationUnavailableLogged)
                {
                    goekinesisDestinationUnavailableLogged = true;
                    Logger.Warning(
                        $"[Clyteum] Goekinesis has no arena-local destination outside registered hazards; " +
                        $"helpers={goekinesisLines.Count} helperSet={DescribeGoekinesisLines(goekinesisLines)}. " +
                        "Holding position while other avoidance remains authoritative.");
                }

                return AvoidanceManager.IsRunningOutOfAvoid;
            }

            goekinesisDestination = destination;
            goekinesisDestinationActive = true;
            goekinesisDestinationUnavailableLogged = false;
        }

        // Resume the same destination after registered avoidance clears.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(goekinesisDestination) <= GoekinesisArrivalTolerance)
        {
            Navigator.PlayerMover.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(goekinesisDestination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>Snapshots active Goekinesis helpers in deterministic order.</summary>
    private static List<BattleCharacter> GetActiveGoekinesisHelpers()
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(helper =>
                helper.IsValid &&
                helper.IsCasting &&
                helper.CastingSpellId == EnemyAction.Goekinesis &&
                helper.SpellCastInfo.IsValid)
            .OrderBy(helper => helper.ObjectId)
            .ToList();
    }

    /// <summary>Selects the nearest sufficiently clear point, or the best available fallback.</summary>
    private static bool TrySelectGoekinesisDestination(
        IReadOnlyCollection<GoekinesisLineSnapshot> lines,
        out Vector3 destination)
    {
        Vector3 bestValid = default;
        float bestValidTravel = float.MaxValue;
        float bestValidClearance = float.MinValue;
        Vector3 bestFallback = default;
        float bestFallbackClearance = float.MinValue;
        float bestFallbackTravel = float.MaxValue;
        bool foundCandidate = false;
        bool foundValid = false;

        foreach (Vector3 candidate in EnumerateGoekinesisCandidates())
        {
            if (AvoidanceManager.Avoids.Any(avoid => avoid.IsPointInAvoid(candidate)))
            {
                continue;
            }

            foundCandidate = true;
            float clearance = GetMinimumGoekinesisClearance(candidate, lines);
            float travel = Core.Player.Distance2D(candidate);

            if (clearance >= GoekinesisRequiredClearance &&
                (!foundValid || travel < bestValidTravel - 0.05f ||
                 (Math.Abs(travel - bestValidTravel) <= 0.05f && clearance > bestValidClearance)))
            {
                bestValid = candidate;
                bestValidTravel = travel;
                bestValidClearance = clearance;
                foundValid = true;
            }

            if (clearance > bestFallbackClearance ||
                (Math.Abs(clearance - bestFallbackClearance) <= 0.05f && travel < bestFallbackTravel))
            {
                bestFallback = candidate;
                bestFallbackClearance = clearance;
                bestFallbackTravel = travel;
            }
        }

        destination = foundValid ? bestValid : bestFallback;
        return foundCandidate;
    }

    /// <summary>Enumerates the current point followed by Malphas's half-yalm arena grid.</summary>
    private static IEnumerable<Vector3> EnumerateGoekinesisCandidates()
    {
        if (Core.Player.Distance2D(ArenaCenter.Malphas) <= GoekinesisCandidateRadius)
        {
            yield return Core.Player.Location;
        }

        for (float xOffset = -GoekinesisCandidateRadius;
             xOffset <= GoekinesisCandidateRadius + 0.01f;
             xOffset += GoekinesisCandidateStep)
        {
            for (float zOffset = -GoekinesisCandidateRadius;
                 zOffset <= GoekinesisCandidateRadius + 0.01f;
                 zOffset += GoekinesisCandidateStep)
            {
                Vector3 candidate = new(
                    ArenaCenter.Malphas.X + xOffset,
                    ArenaCenter.Malphas.Y,
                    ArenaCenter.Malphas.Z + zOffset);
                if (candidate.Distance2D(ArenaCenter.Malphas) <= GoekinesisCandidateRadius)
                {
                    yield return candidate;
                }
            }
        }
    }

    /// <summary>Returns perpendicular clearance from the nearest bidirectional line edge.</summary>
    private static float GetMinimumGoekinesisClearance(
        Vector3 point,
        IReadOnlyCollection<GoekinesisLineSnapshot> lines)
    {
        if (lines.Count == 0)
        {
            return float.PositiveInfinity;
        }

        float minimumClearance = float.MaxValue;
        foreach (GoekinesisLineSnapshot line in lines)
        {
            float deltaX = point.X - line.Location.X;
            float deltaZ = point.Z - line.Location.Z;
            float sine = (float)Math.Sin(line.Heading);
            float cosine = (float)Math.Cos(line.Heading);
            float lateral = deltaX * cosine - deltaZ * sine;
            minimumClearance = Math.Min(minimumClearance, Math.Abs(lateral) - GoekinesisLineHalfWidth);
        }

        return minimumClearance;
    }

    /// <summary>Formats one wave's helper geometry.</summary>
    private static string DescribeGoekinesisLines(IEnumerable<GoekinesisLineSnapshot> lines)
    {
        return string.Join(
            "|",
            lines.Select(line =>
                $"0x{line.ObjectId:X8}@{Format(line.Location)}/h={Format(line.Heading)}"));
    }

    /// <summary>Releases Goekinesis movement and clears its wave snapshot.</summary>
    private void ResetGoekinesisState(string reason)
    {
        if (goekinesisMovementOwned)
        {
            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            CapabilityManager.Clear(goekinesisMovementHandle, CapabilityFlags.Movement, reason);
            goekinesisMovementOwned = false;
        }

        goekinesisLines.Clear();
        goekinesisDestination = default;
        goekinesisHoldUntilUtc = DateTime.MinValue;
        goekinesisHelperSignature = null;
        goekinesisWaveActive = false;
        goekinesisDestinationActive = false;
        goekinesisDestinationUnavailableLogged = false;
    }

    /// <summary>Moves a self-targeted Shadow Play to one stable isolation point.</summary>
    private async Task<bool> HandleShadowPlayAsync()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter helper = GetActiveShadowPlayHelper();

        if (helper != null)
        {
            uint targetId = helper.SpellCastInfo.TargetId;
            if (targetId != Core.Player.ObjectId)
            {
                ResetShadowPlayState($"helper 0x{helper.ObjectId:X8} targets 0x{targetId:X8}");
                return false;
            }

            shadowPlayHoldUntilUtc = now + helper.SpellCastInfo.RemainingCastTime + ShadowPlayResolutionGrace;
            if (!shadowPlayDestinationActive)
            {
                shadowPlayDestination = SelectShadowPlayIsolationDestination();
                shadowPlayDestinationActive = true;
            }
        }
        else if (!shadowPlayDestinationActive)
        {
            return false;
        }

        if (now >= shadowPlayHoldUntilUtc)
        {
            ResetShadowPlayState("helper cast and resolution grace ended");
            return false;
        }

        int leaseMilliseconds = Math.Max(
            ShadowPlayMovementLeaseMinimumMilliseconds,
            (int)Math.Ceiling((shadowPlayHoldUntilUtc - now).TotalMilliseconds));
        CapabilityManager.Update(
            shadowPlayMovementHandle,
            CapabilityFlags.Movement,
            leaseMilliseconds,
            "Holding a stable Shadow Play isolation point");
        shadowPlayMovementOwned = true;

        // Resume the same isolation point after registered avoidance clears.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(shadowPlayDestination) <= ShadowPlayPositionTolerance)
        {
            MovementManager.MoveStop();
            return false;
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        await CommonTasks.MoveTo(shadowPlayDestination);
        return true;
    }

    /// <summary>Finds the active Shadow Play helper, preferring one that targets the player.</summary>
    private static BattleCharacter GetActiveShadowPlayHelper()
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(bc =>
                bc.IsValid &&
                bc.IsCasting &&
                bc.CastingSpellId == EnemyAction.ShadowPlay &&
                bc.SpellCastInfo.IsValid)
            .OrderByDescending(bc => bc.SpellCastInfo.TargetId == Core.Player.ObjectId)
            .ThenBy(bc => bc.SpellCastInfo.RemainingCastTime)
            .FirstOrDefault();
    }

    /// <summary>Selects the nearest separated point, or the best-separated fallback.</summary>
    private static Vector3 SelectShadowPlayIsolationDestination()
    {
        List<Vector3> partyLocations = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(member => member != null && member.IsValid && member.IsAlive && member.ObjectId != Core.Player.ObjectId)
            .Select(member => member.Location)
            .ToList();

        List<Vector3> candidates = [];
        if (Core.Player.Distance2D(ArenaCenter.Malphas) <= ShadowPlayArenaInsetRadius)
        {
            candidates.Add(Core.Player.Location);
        }

        foreach (float radius in ShadowPlayCandidateRadii)
        {
            for (int index = 0; index < ShadowPlayCandidateCountPerRing; index++)
            {
                double angle = index * Math.PI * 2.0 / ShadowPlayCandidateCountPerRing;
                candidates.Add(new Vector3(
                    ArenaCenter.Malphas.X + (float)(Math.Cos(angle) * radius),
                    ArenaCenter.Malphas.Y,
                    ArenaCenter.Malphas.Z + (float)(Math.Sin(angle) * radius)));
            }
        }

        Vector3 bestValid = ArenaCenter.Malphas;
        float bestValidTravel = float.MaxValue;
        Vector3 bestFallback = ArenaCenter.Malphas;
        float bestFallbackSeparation = float.MinValue;
        float bestFallbackTravel = float.MaxValue;

        foreach (Vector3 candidate in candidates)
        {
            float minimumSeparation = partyLocations.Count == 0
                ? float.MaxValue
                : partyLocations.Min(location => candidate.Distance2D(location));
            float travelDistance = Core.Player.Distance2D(candidate);

            if (minimumSeparation >= ShadowPlayRequiredSeparation && travelDistance < bestValidTravel)
            {
                bestValid = candidate;
                bestValidTravel = travelDistance;
            }

            if (minimumSeparation > bestFallbackSeparation ||
                (Math.Abs(minimumSeparation - bestFallbackSeparation) < 0.01f && travelDistance < bestFallbackTravel))
            {
                bestFallback = candidate;
                bestFallbackSeparation = minimumSeparation;
                bestFallbackTravel = travelDistance;
            }
        }

        return bestValidTravel < float.MaxValue ? bestValid : bestFallback;
    }

    /// <summary>Releases Shadow Play movement and clears its destination.</summary>
    private void ResetShadowPlayState(string reason)
    {
        if (shadowPlayMovementOwned)
        {
            CapabilityManager.Clear(shadowPlayMovementHandle, CapabilityFlags.Movement, reason);
            shadowPlayMovementOwned = false;
        }

        shadowPlayDestinationActive = false;
        shadowPlayHoldUntilUtc = DateTime.MinValue;
    }

    /// <summary>Moves along a fixed orbit from the String Up cast through its countdown.</summary>
    private bool HandleStringUpMovement()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter caster = GetActiveStringUpCaster();
        bool countdownActive = HasStringUpCountdownVfx();

        if (Core.Player.HasAura(PlayerAura.StrungUp))
        {
            if (!stringUpFailureLogged)
            {
                stringUpFailureLogged = true;
                Logger.Warning($"[Clyteum] STRING_UP_FAILURE aura={PlayerAura.StrungUp} location={Format(Core.Player.Location)}.");
            }
        }
        else if (!stringUpMovementActive)
        {
            stringUpFailureLogged = false;
        }

        if (caster != null)
        {
            // Continue through the cast-to-marker handoff with a bounded fallback.
            stringUpKeepMovingUntilUtc = now + caster.SpellCastInfo.RemainingCastTime + StringUpPostCastFallback;
            if (!stringUpMovementActive)
            {
                stringUpMovementActive = true;
                stringUpCountdownObserved = false;
                stringUpWaypoint = GetNextStringUpWaypoint(Core.Player.Location);
            }
        }

        if (countdownActive)
        {
            stringUpCountdownObserved = true;
        }

        if (!stringUpMovementActive)
        {
            return false;
        }

        if (caster == null && !countdownActive &&
            (stringUpCountdownObserved || now >= stringUpKeepMovingUntilUtc))
        {
            // Release immediately when the observed countdown marker disappears.
            ResetStringUpMovement(stringUpCountdownObserved
                ? "countdown VFX resolved"
                : "post-cast VFX fallback expired");
            return false;
        }

        CapabilityManager.Update(
            stringUpMovementHandle,
            CapabilityFlags.Movement,
            StringUpMovementLeaseMilliseconds,
            "Maintaining String Up arena-local movement");
        stringUpMovementOwned = true;

        // Registered avoidance owns emergency movement; resume the orbit afterward.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(stringUpWaypoint) <= StringUpWaypointTolerance)
        {
            stringUpWaypoint = GetNextStringUpWaypoint(Core.Player.Location);
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(stringUpWaypoint);
        return true;
    }

    /// <summary>Finds the active String Up parent cast.</summary>
    private static BattleCharacter GetActiveStringUpCaster()
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(bc =>
                bc.IsValid &&
                bc.IsCasting &&
                bc.CastingSpellId == EnemyAction.StringUp &&
                bc.SpellCastInfo.IsValid);
    }

    /// <summary>Reports whether String Up countdown VFX 136 is attached to the player.</summary>
    private static bool HasStringUpCountdownVfx()
    {
        if (Core.Player == null || !Core.Player.IsValid || !Core.Player.VfxContainer.IsValid)
        {
            return false;
        }

        return Core.Player.VfxContainer.Vfx.Any(vfx =>
            vfx != null &&
            vfx.IsValid &&
            Convert.ToUInt64(vfx.Id, CultureInfo.InvariantCulture) == PlayerVfx.StringUpCountdown);
    }

    /// <summary>Returns the next counter-clockwise waypoint on Malphas's orbit.</summary>
    private static Vector3 GetNextStringUpWaypoint(Vector3 location)
    {
        float offsetX = location.X - ArenaCenter.Malphas.X;
        float offsetZ = location.Z - ArenaCenter.Malphas.Z;
        float currentAngle = Math.Abs(offsetX) < 0.1f && Math.Abs(offsetZ) < 0.1f
            ? 0.0f
            : (float)Math.Atan2(offsetZ, offsetX);
        float nextAngle = currentAngle + StringUpWaypointStepRadians;

        return new Vector3(
            ArenaCenter.Malphas.X + (float)Math.Cos(nextAngle) * StringUpOrbitRadius,
            ArenaCenter.Malphas.Y,
            ArenaCenter.Malphas.Z + (float)Math.Sin(nextAngle) * StringUpOrbitRadius);
    }

    /// <summary>Releases String Up movement and clears its orbit state.</summary>
    private void ResetStringUpMovement(string reason)
    {
        if (stringUpMovementOwned)
        {
            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            CapabilityManager.Clear(stringUpMovementHandle, CapabilityFlags.Movement, reason);
            stringUpMovementOwned = false;
        }

        stringUpMovementActive = false;
        stringUpCountdownObserved = false;
        stringUpKeepMovingUntilUtc = DateTime.MinValue;
    }

    /// <summary>Stores one helper's immutable line origin and heading.</summary>
    private sealed class GoekinesisLineSnapshot
    {
        /// <summary>Initializes a Goekinesis helper snapshot.</summary>
        internal GoekinesisLineSnapshot(uint objectId, Vector3 location, float heading)
        {
            ObjectId = objectId;
            Location = location;
            Heading = heading;
        }

        /// <summary>Gets the helper identity used for diagnostics and change detection.</summary>
        internal uint ObjectId { get; }

        /// <summary>Gets the helper's world-space line origin.</summary>
        internal Vector3 Location { get; }

        /// <summary>Gets the helper heading; reversing it leaves the bidirectional strip unchanged.</summary>
        internal float Heading { get; }
    }

    /// <summary>Selects whether a positive position comes from the cast target or location.</summary>
    private enum PositivePositionSource
    {
        CastTarget,
        CastLocation,
    }

    /// <summary>
    /// Identifies the horizontal arena boundary used when sampling a targeted-spread destination.
    /// </summary>
    private enum SpreadArenaShape
    {
        /// <summary>An axis-aligned square using <see cref="TargetedSpreadState.CandidateExtent"/> as half-extent.</summary>
        Square,

        /// <summary>A circle using <see cref="TargetedSpreadState.CandidateExtent"/> as radius.</summary>
        Circle,
    }

    /// <summary>Stores one positive-position mechanic and its movement state.</summary>
    private sealed class PositivePositionState
    {
        internal PositivePositionState(
            string name,
            uint actionId,
            PositivePositionSource source,
            float arrivalDistance,
            bool regroupWhenSelfTargeted,
            TimeSpan resolutionGrace)
        {
            Name = name;
            ActionId = actionId;
            Source = source;
            ArrivalDistance = arrivalDistance;
            RegroupWhenSelfTargeted = regroupWhenSelfTargeted;
            ResolutionGrace = resolutionGrace;
        }

        internal string Name { get; }
        internal uint ActionId { get; }
        internal PositivePositionSource Source { get; }
        internal float ArrivalDistance { get; }
        internal bool RegroupWhenSelfTargeted { get; }
        internal TimeSpan ResolutionGrace { get; }
        internal CapabilityManagerHandle MovementHandle { get; } = CapabilityManager.CreateNewHandle();
        internal uint ActiveCasterObjectId { get; set; }
        internal uint SelfTargetPartyAnchorObjectId { get; set; }
        internal DateTime HoldUntilUtc { get; set; } = DateTime.MinValue;
        internal Vector3 LastDestination { get; set; }
        internal bool HasLastDestination { get; set; }
        internal bool MovementOwned { get; set; }
        internal bool DestinationUnavailableLogged { get; set; }
    }

    /// <summary>Stores one targeted spread's arena, separation, destination, and movement state.</summary>
    private sealed class TargetedSpreadState
    {
        /// <summary>Initializes targeted-spread geometry and required separation.</summary>
        internal TargetedSpreadState(
            string name,
            uint actionId,
            Vector3 arenaCenter,
            SpreadArenaShape arenaShape,
            float candidateExtent,
            float damageRadius)
        {
            Name = name;
            ActionId = actionId;
            ArenaCenter = arenaCenter;
            ArenaShape = arenaShape;
            CandidateExtent = candidateExtent;
            DamageRadius = damageRadius;
            RequiredSeparation = damageRadius + TargetedSpreadSafetyBuffer;
        }

        internal string Name { get; }
        internal uint ActionId { get; }
        internal Vector3 ArenaCenter { get; }
        internal SpreadArenaShape ArenaShape { get; }
        internal float CandidateExtent { get; }
        internal float DamageRadius { get; }
        internal float RequiredSeparation { get; }
        internal CapabilityManagerHandle MovementHandle { get; } = CapabilityManager.CreateNewHandle();
        internal uint ActiveCasterObjectId { get; set; }
        internal DateTime HoldUntilUtc { get; set; } = DateTime.MinValue;
        internal Vector3 Destination { get; set; }
        internal int ReplanCount { get; set; }
        internal bool DestinationActive { get; set; }
        internal bool MovementOwned { get; set; }
        internal bool DestinationUnavailableLogged { get; set; }
    }

    /// <summary>Formats a position with invariant three-decimal coordinates.</summary>
    private static string Format(Vector3 value)
    {
        return $"({value.X.ToString("F3", CultureInfo.InvariantCulture)}, " +
               $"{value.Y.ToString("F3", CultureInfo.InvariantCulture)}, " +
               $"{value.Z.ToString("F3", CultureInfo.InvariantCulture)})";
    }

    /// <summary>Formats a scalar with invariant three-decimal precision.</summary>
    private static string Format(float value)
    {
        return value.ToString("F3", CultureInfo.InvariantCulture);
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Eye of the Scorpion
        /// </summary>
        public const uint EyeoftheScorpion = 14716;

        /// <summary>
        /// Second Boss: Chort
        /// </summary>
        public const uint Chort = 14734;

        /// <summary>
        /// Final Boss: Malphas
        /// </summary>
        public const uint Malphas = 14758;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.EyeoftheScorpion"/>.
        /// </summary>
        public static readonly Vector3 EyeoftheScorpion = new(-615f, -1.1920929E-07f, 575f);

        /// <summary>
        /// Second Boss: <see cref="EnemyNpc.Chort"/>.
        /// </summary>
        public static readonly Vector3 Chort = new(660f, -15.000002f, -141f);

        /// <summary>
        /// Third Boss: <see cref="EnemyNpc.Malphas"/>.
        /// </summary>
        public static readonly Vector3 Malphas = new(760f, 61f, -803f);
    }

    private static class EnemyAction
    {
        /// <summary>Eye of the Scorpion raidwide.</summary>
        public const uint EyesOnMe = 48896;

        /// <summary>Chort raidwide helper cast.</summary>
        public const uint RipplesOfGloom = 50408;

        /// <summary>Malphas raidwide.</summary>
        public const uint RubbishDisposal = 48920;

        /// <summary>Motion Scanner parent cast; moving scanner objects own the sweep.</summary>
        public const uint MotionScanner = 48893;

        /// <summary>Eye of the Scorpion spread.</summary>
        public const uint AntipersonnelMissile = 48899;

        /// <summary>Eye of the Scorpion stack.</summary>
        public const uint PenetratorMissile = 48901;

        /// <summary>First Petrifying Beam choreography cast.</summary>
        public const uint PetrifyingBeamCastFirst = 50175;

        /// <summary>Second Petrifying Beam choreography cast.</summary>
        public const uint PetrifyingBeamCastSecond = 50176;

        /// <summary>First 70-yalm, 100-degree Petrifying Beam helper cone.</summary>
        public const uint PetrifyingBeamFirst = 50177;

        /// <summary>Second 70-yalm, 100-degree Petrifying Beam helper cone.</summary>
        public const uint PetrifyingBeamSecond = 50178;

        /// <summary>First 40-by-16 Mortifying Flesh helper line.</summary>
        public const uint MortifyingFleshFirst = 50400;

        /// <summary>Second 40-by-16 Mortifying Flesh helper line.</summary>
        public const uint MortifyingFleshSecond = 48876;

        /// <summary>Bodyweight Exorcism's eight-yalm knockback helper.</summary>
        public const uint BodyweightExorcism = 48878;

        /// <summary>Bodyweight Exorcism tower cast.</summary>
        public const uint BodyweightExorcismTowers = 48882;

        /// <summary>Chort spread.</summary>
        public const uint EvilEmission = 48885;

        /// <summary>Chort stack.</summary>
        public const uint ProfanePressure = 48887;


        /// <summary>Puppet Strings parent cast; spawned helpers own the cone geometry.</summary>
        public const uint PuppetStrings = 48922;

        /// <summary>Malphas spread.</summary>
        public const uint WrathfulWire = 48928;

        /// <summary>Malphas stack.</summary>
        public const uint GluttonousWire = 48930;

        /// <summary>String Up parent cast; movement continues through the countdown lock-on.</summary>
        public const uint StringUp = 48931;

        /// <summary>Helper-owned bidirectional line AOE resolved as one wave.</summary>
        public const uint Goekinesis = 48933;

        /// <summary>Malphas 180-degree cone.</summary>
        public const uint VoidDark = 50313;

        /// <summary>Shadow Play helper cast that owns the target and resolution.</summary>
        public const uint ShadowPlay = 50314;

        /// <summary>Shadow Play parent cast; the helper owns the target.</summary>
        public const uint ShadowPlayParent = 50315;
    }

    private static class PlayerVfx
    {
        /// <summary>String Up countdown lock-on.</summary>
        public const ulong StringUpCountdown = 136;
    }

    private static class PlayerAura
    {
        /// <summary>Motion Tracker; do not move or act.</summary>
        public const uint MotionTracker = 5191;

        /// <summary>String Up failure status.</summary>
        public const uint StrungUp = 5407;
    }
}
