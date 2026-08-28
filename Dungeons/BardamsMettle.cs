using Buddy.Coroutines;
using Clio.Common;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
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
/// Provides encounter-local movement and avoidance for the level 65 dungeon Bardam's Mettle.
/// </summary>
/// <remarks>
/// The second boss is a non-combat trial whose three waves combine ordinary harmful geometry with
/// positive-position towers, a gaze, and line-of-sight shelter. DutyMechanic owns that trial while
/// its unique actors are present because a generic cast-type decoder can invert the tower and gaze
/// responses or publish every expanding Heavy Strike band at once. Bardam's Ring is handled as a
/// positive donut stack from its player-marker lifecycle rather than as independent
/// hazards that could push the marked pair apart. Geometry and semantic lifecycles were captured
/// and live-validated on 2026-08-27.
/// </remarks>
public sealed class BardamsMettle : AbstractDungeon
{
    // The physical circular wall is radius 19.5 at the confirmed Z=-14 center. The registered
    // 19-yalm boundary retains a 0.5-yalm wall margin.
    private const float BardamArenaSafeRadius = 19.0f;

    // Every directly damaging circle and rectangle adds the standard 0.5-yalm stopping margin.
    private const float TremblorCircleAvoidRadius = 10.5f;
    private const float TremblorDonutInnerRadius = 9.5f;
    private const float TremblorDonutOuterRadius = 20.5f;
    private const float ChargeAvoidWidth = 6.0f;
    private const float ChargeAvoidLength = 47.25f;
    private const float RectangleRearMargin = -0.5f;
    private const float CometAvoidRadius = 4.5f;
    private const float ReconstructAvoidRadius = 5.5f;
    private const float CometImpactAvoidRadius = 9.5f;

    // Capture evidence showed VFX 58 on exactly two party actors for 3.5 seconds. Each actor
    // owns a 10-to-20-yalm damaging ring, and the marked pair deliberately converges. Moving every
    // local role to their midpoint solves both cases: marked players join each other, while an
    // unmarked player enters both inner safe circles. The 9.5-yalm test keeps at least 0.5 yalm
    // inside the damaging edge. The short recurring lease expires promptly if either marker ends.
    private const float BardamsRingInnerSafeRadius = 9.5f;
    private static readonly TimeSpan BardamsRingMovementLease = TimeSpan.FromMilliseconds(500);

    // Live evidence showed a non-targeted player standing in Rush's path. Rush is an
    // eight-yalm-wide charge from Garula to the selected player. The target must retain the existing
    // positive movement toward a Steppe Sheep, so this rectangle is published only for non-targeted
    // players. A 0.5-yalm margin on each side and end provides normal navigation clearance without
    // extending the lane across unrelated parts of the arena.
    private const float RushLaneAvoidWidth = 9.0f;
    private const float RushLaneEndMargin = 0.5f;
    private const float RushSheepArrivalDistance = 2.0f;
    private static readonly TimeSpan RushMovementLease = TimeSpan.FromMilliseconds(500);

    // Heavy Strike's helper casts overlap in time but resolve in order. Publishing their union made
    // phase three unsolvable when the two Warriors faced inward. Capture evidence established
    // that each helper shares its parent's origin, so the next-resolving helper selects one retained
    // parent heading and one radial band at a time. The 6.5/12.5/18.5-yalm edges receive 0.5-yalm
    // navigation clearance on both sides of each band. The 271-degree sector adds 0.5 degree to
    // each edge of the observed 270-degree attack.
    private const float HeavyStrikeFirstOuterRadius = 7.0f;
    private const float HeavyStrikeSecondInnerRadius = 6.0f;
    private const float HeavyStrikeSecondOuterRadius = 13.0f;
    private const float HeavyStrikeThirdInnerRadius = 12.0f;
    private const float HeavyStrikeThirdOuterRadius = 19.0f;
    private const float HeavyStrikeAvoidArcDegrees = 271.0f;
    private const float HeavyStrikeHelperOriginTolerance = 1.0f;
    // The final helper resolves about 3.6 seconds after its parent ends. A 4.25-second retention
    // window preserves that origin through impact with roughly 0.65 second for actor-frame latency.
    private static readonly TimeSpan HeavyStrikePostCastHold = TimeSpan.FromMilliseconds(4_250);
    private static readonly Vector2[] HeavyStrikeFirstBandPolygon =
        CreateSectorBandPolygon(0.0f, HeavyStrikeFirstOuterRadius, HeavyStrikeAvoidArcDegrees);
    private static readonly Vector2[] HeavyStrikeSecondBandPolygon =
        CreateSectorBandPolygon(
            HeavyStrikeSecondInnerRadius,
            HeavyStrikeSecondOuterRadius,
            HeavyStrikeAvoidArcDegrees);
    private static readonly Vector2[] HeavyStrikeThirdBandPolygon =
        CreateSectorBandPolygon(
            HeavyStrikeThirdInnerRadius,
            HeavyStrikeThirdOuterRadius,
            HeavyStrikeAvoidArcDegrees);

    // Towers are radius three. Moving within 1.25 yalms of the center leaves ample tolerance while
    // the 2.5-yalm occupancy test avoids selecting a tower already claimed by another party member.
    private const float SacrificeTowerArrivalDistance = 1.25f;
    private const float SacrificeTowerOccupancyDistance = 2.5f;
    private static readonly TimeSpan SacrificeTowerImpactGrace = TimeSpan.FromMilliseconds(500);

    // The meteor's surviving Star Shard has a two-yalm blocker radius. Three yalms beyond its center
    // keeps the player directly opposite the Looming Shadow without standing inside the rock model.
    private const float MeteorShelterBehindRockDistance = 3.0f;
    private const float MeteorShelterArrivalDistance = 0.75f;
    private static readonly TimeSpan MeteorImpactGrace = TimeSpan.FromMilliseconds(750);

    // Reserve facing only near Empty Gaze's impact. Earlier ownership can fight the in/out avoidance
    // that must finish first, while a 1.5-second lead is long enough for a deterministic final turn.
    private static readonly TimeSpan EmptyGazeFacingLead = TimeSpan.FromMilliseconds(1_500);
    private static readonly TimeSpan EmptyGazeImpactGrace = TimeSpan.FromMilliseconds(750);

    private readonly Dictionary<uint, HeavyStrikeSequenceForecast> heavyStrikeForecasts = [];
    private readonly OwnedMovementState rushMovement = new();
    private readonly OwnedMovementState bardamsRingMovement = new();
    private readonly OwnedMovementState sacrificeTowerMovement = new();
    private readonly OwnedMovementState meteorShelterMovement = new();
    private readonly CapabilityManagerHandle emptyGazeFacingHandle = CapabilityManager.CreateNewHandle();

    private bool bardamTrialWasActive;
    private bool bardamTrialCombatWasActive;

    private uint rushCasterObjectId;
    private Vector3 rushSheepDestination;
    private bool rushArrivalLogged;
    private bool rushSheepUnavailableLogged;

    private uint sacrificeTowerCasterObjectId;
    private Vector3 sacrificeTowerDestination;
    private DateTime sacrificeTowerHoldUntilUtc = DateTime.MinValue;
    private bool sacrificeTowerDestinationActive;
    private bool sacrificeTowerDestinationUnavailableLogged;

    private ulong bardamsRingTargetSignature;
    private bool bardamsRingArrivalLogged;
    private bool bardamsRingIncompleteTargetSetLogged;

    private uint meteorShelterRockObjectId;
    private Vector3 meteorShelterDestination;
    private DateTime meteorShelterHoldUntilUtc = DateTime.MinValue;
    private bool meteorShelterDestinationActive;
    private bool meteorShelterUnavailableLogged;

    private Vector3 emptyGazeOrigin;
    private DateTime emptyGazeHoldUntilUtc = DateTime.MinValue;
    private bool emptyGazeFacingArmed;
    private bool emptyGazeFacingOwned;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.BardamsMettle;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetEncounterState("entering Bardam's Mettle");
        RegisterGarulaAvoidance();
        RegisterBardamTrialAvoidance();

        // Preserve the existing final-boss gaze workaround outside this second-boss migration.
        // Yol remains SideStep-owned; this late circle only forces a last-moment turn away until
        // live evidence supports replacing it with a dedicated facing lifecycle.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheVoicelessMuse,
            objectSelector: actor => actor.CastingSpellId == EnemyAction.EyeOfTheFierce &&
                actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds <= 500,
            radiusProducer: _ => 18.0f,
            priority: AvoidancePriority.High));

        // Boss arenas retain a half-to-one-yalm wall inset so combined avoids cannot select the
        // lethal boundary itself. Only Bardam's trial changes ownership in this migration.
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.BardamsHunt,
            () => ArenaCenter.Garula,
            outerRadius: 90.0f,
            innerRadius: 21.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            IsBardamTrialCombatActive,
            () => ArenaCenter.BardamsTrial,
            outerRadius: 90.0f,
            innerRadius: BardamArenaSafeRadius,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheVoicelessMuse,
            () => ArenaCenter.Yol,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        // Retain only scalar parent snapshots because the Hunters and Warriors stop casting before
        // their helper impacts. The collection producer correlates current-frame helper origins and
        // publishes only the earliest unresolved band for each simultaneous parent sequence.
        AvoidanceManager.AddAvoidPolygon<HeavyStrikeBandForecast>(
            condition: IsBardamTrialCombatActive,
            leashPointProducer: () => ArenaCenter.BardamsTrial,
            leashRadius: BardamArenaSafeRadius,
            rotationProducer: forecast => forecast.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: forecast => GetHeavyStrikeBandPolygon(forecast.Stage),
            locationProducer: forecast => forecast.Origin,
            collectionProducer: GetActiveHeavyStrikeBandForecasts,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetEncounterState("leaving Bardam's Mettle");
        SidestepPlugin.Enabled = true;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        _ = await FollowDodgeSpells();

        bool bardamTrialActive = IsBardamTrialActive();
        UpdateBardamTrialOwnership(bardamTrialActive);
        if (bardamTrialActive && await HandleBardamTrialAsync())
        {
            return true;
        }

        if (await HandleRushAsync())
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Carries a player-targeted Rush to the Steppe Sheep without overriding emergency avoidance.
    /// </summary>
    /// <remarks>
    /// Rush is positive positioning for its selected player, whereas the registered lane is harmful
    /// to everyone else. A short recurring movement lease holds the target at the sheep but expires
    /// quickly when the cast disappears. Movement is stopped only when this handler issued it;
    /// avoidance movement is never cancelled during a handoff.
    /// </remarks>
    /// <returns><see langword="true"/> while traveling or yielding to harmful avoidance.</returns>
    private async Task<bool> HandleRushAsync()
    {
        BattleCharacter rushCaster = IsGarulaCombatActive()
            ? GetActiveCasters(EnemyAction.Rush)
                .FirstOrDefault(caster => caster.SpellCastInfo.TargetId == Core.Player.ObjectId)
            : null;
        if (rushCaster == null)
        {
            if (rushCasterObjectId != 0 || rushMovement.IsActive || rushSheepUnavailableLogged)
            {
                ReleaseRushMovement("player-targeted Rush ended");
            }

            return false;
        }

        BattleCharacter sheep = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.SteppeSheep)
            .FirstOrDefault(actor => actor.IsVisible && actor.IsValid);
        if (sheep == null)
        {
            rushCasterObjectId = rushCaster.ObjectId;
            if (!rushSheepUnavailableLogged)
            {
                Logger.Warning(
                    "[BardamsMettle] Rush targets the local player but no valid Steppe Sheep is visible; " +
                    "positive movement is withheld rather than guessing at a destination.");
                rushSheepUnavailableLogged = true;
            }

            rushMovement.Release("Steppe Sheep unavailable during player-targeted Rush");
            return false;
        }

        // Copy the destination before yielding because RB actor wrappers are frame-scoped. The sheep
        // is expected to remain fixed, but refreshing its scalar location also tolerates delayed spawn
        // placement without retaining the wrapper.
        rushSheepDestination = sheep.Location;
        if (rushCasterObjectId != rushCaster.ObjectId || rushSheepUnavailableLogged)
        {
            rushCasterObjectId = rushCaster.ObjectId;
            rushArrivalLogged = false;
            rushSheepUnavailableLogged = false;
            Logger.Information(
                $"[BardamsMettle] Rush targets the local player; moving to Steppe Sheep " +
                $"0x{sheep.ObjectId:X8} at {rushSheepDestination}.");
        }

        rushMovement.Lease(RushMovementLease, "Holding player-targeted Rush at the Steppe Sheep");
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        float distance = Core.Player.Distance2D(rushSheepDestination);
        if (distance <= RushSheepArrivalDistance)
        {
            if (!rushArrivalLogged)
            {
                Logger.Information(
                    $"[BardamsMettle] Reached the Steppe Sheep for Rush; distance={distance:F2}.");
                rushArrivalLogged = true;
            }

            rushMovement.Stop();
            return false;
        }

        rushMovement.MoveTowards(rushSheepDestination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Registers the first boss's targeted Rush lane for players who are not carrying its bait.
    /// </summary>
    /// <remarks>
    /// The lane terminates at the target's current position and is rebuilt from scalar snapshots on
    /// each avoidance pulse. Excluding the local target is essential: that player must extend Rush
    /// toward the sheep rather than have avoidance push them out of their own required charge line.
    /// </remarks>
    private static void RegisterGarulaAvoidance()
    {
        AvoidanceManager.AddAvoidPolygon<RushLane>(
            condition: IsGarulaCombatActive,
            leashPointProducer: () => ArenaCenter.Garula,
            leashRadius: 30.0f,
            rotationProducer: lane => lane.Rotation,
            scaleProducer: _ => 1.0f,
            heightProducer: _ => 15.0f,
            pointsProducer: lane => CreateRushLanePolygon(lane.Length),
            locationProducer: lane => lane.Origin,
            collectionProducer: GetActiveNonPlayerRushLanes,
            objectValidator: _ => true,
            ignoreIfBlocking: false,
            priority: AvoidancePriority.High);
    }

    /// <summary>
    /// Registers cast-owned hazards for all three Bardam trials.
    /// </summary>
    /// <remarks>
    /// Choreography-only actions such as Magnetism and Travail intentionally receive no geometry.
    /// Bardam's Ring is marker-owned positive positioning and is therefore handled by the semantic
    /// planner rather than registered here as geometry around two moving party members.
    /// </remarks>
    private static void RegisterBardamTrialAvoidance()
    {
        AddCastCircle(EnemyAction.TremblorCircle, TremblorCircleAvoidRadius, useCastLocation: false);

        // The circle begins first and resolves about 1.5 seconds before the donut. Their padded
        // radial edges overlap, and enabling both produced 74 no-path failures in one trial run.
        // Delay the donut until the earlier circle cast leaves the current object frame.
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => IsBardamTrialCombatActive() && !IsTremblorCircleActive(),
            objectSelector: actor => actor.IsCasting && actor.CastingSpellId == EnemyAction.TremblorDonut,
            outerRadius: TremblorDonutOuterRadius,
            innerRadius: TremblorDonutInnerRadius,
            priority: AvoidancePriority.High);

        // Throwing Spear's 45-yalm reach includes its 1.25-yalm actor radius, for a raw 46.25-yalm
        // forward extent. GenerateRectangle adds yOffset to its length, so 47.25 with a -0.5 offset
        // produces 46.75 forward and 0.5 behind: the standard clearance at both ends.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsBardamTrialCombatActive,
            objectSelector: actor => actor.IsCasting && actor.CastingSpellId == EnemyAction.Charge,
            width: ChargeAvoidWidth,
            length: ChargeAvoidLength,
            yOffset: RectangleRearMargin,
            priority: AvoidancePriority.High);

        AddCastCircle(EnemyAction.CometFirst, CometAvoidRadius, useCastLocation: true);
        AddCastCircle(EnemyAction.CometRest, CometAvoidRadius, useCastLocation: true);
        AddCastCircle(EnemyAction.Reconstruct, ReconstructAvoidRadius, useCastLocation: true);
        AddCastCircle(EnemyAction.CometImpact, CometImpactAvoidRadius, useCastLocation: false);
    }

    /// <summary>
    /// Transfers the second boss between SideStep and DutyMechanic only at encounter boundaries.
    /// </summary>
    /// <param name="isActive">
    /// Whether the unique Bardam trial actors currently establish encounter ownership.
    /// </param>
    /// <remarks>
    /// SideStep is re-disabled if another component enables it mid-trial, but cleanup and normal
    /// re-enabling happen only on the active-to-inactive transition. This avoids rewriting global
    /// plugin state and clearing encounter state on every unrelated dungeon pulse.
    /// </remarks>
    private void UpdateBardamTrialOwnership(bool isActive)
    {
        if (isActive)
        {
            if (!bardamTrialWasActive)
            {
                ResetBardamTrialState("Bardam trial started");
                bardamTrialWasActive = true;
                bardamTrialCombatWasActive = false;
            }

            if (SidestepPlugin.Enabled)
            {
                SidestepPlugin.Enabled = false;
            }

            return;
        }

        if (!bardamTrialWasActive)
        {
            return;
        }

        bardamTrialWasActive = false;
        bardamTrialCombatWasActive = false;
        SidestepPlugin.Enabled = true;
        ResetBardamTrialState("Bardam trial actors are no longer present");
    }

    /// <summary>
    /// Runs semantic movement in resolution order while RebornBuddy avoidance owns emergency egress.
    /// </summary>
    /// <returns><see langword="true"/> only while positive-position movement consumes this tick.</returns>
    private async Task<bool> HandleBardamTrialAsync()
    {
        if (!IsBardamTrialCombatActive())
        {
            if (bardamTrialCombatWasActive)
            {
                bardamTrialCombatWasActive = false;
                ResetBardamTrialState("Bardam trial combat ended");
            }

            return false;
        }

        bardamTrialCombatWasActive = true;

        UpdateHeavyStrikeForecasts();

        // These mechanics are phase-separated, but each owns a distinct capability handle so a
        // wipe or delayed actor lifecycle can never release another positive-position mechanic.
        if (await HandleBardamsRingAsync())
        {
            return true;
        }

        if (await HandleSacrificeTowerAsync())
        {
            return true;
        }

        if (await HandleMeteorShelterAsync())
        {
            return true;
        }

        HandleEmptyGazeFacing();
        return false;
    }

    /// <summary>
    /// Moves inside both Bardam's Ring donuts by joining the two marked party actors.
    /// </summary>
    /// <remarks>
    /// VFX 58 is the lifecycle owner; no cast bar exists. The destination is rebuilt from current
    /// scalar positions before every yield because Trust actors continue converging while marked.
    /// Once inside both padded inner circles, only routine movement remains suppressed and this
    /// method returns control so healing and other routine actions can continue.
    /// </remarks>
    /// <returns><see langword="true"/> while traveling or yielding to registered harmful avoidance.</returns>
    private async Task<bool> HandleBardamsRingAsync()
    {
        List<BattleCharacter> markedActors = GetBardamsRingTargets();
        if (markedActors.Count == 0)
        {
            ReleaseBardamsRingMovement("Bardam's Ring markers resolved");
            return false;
        }

        if (markedActors.Count != 2)
        {
            if (!bardamsRingIncompleteTargetSetLogged)
            {
                Logger.Warning(
                    $"[BardamsMettle] Bardam's Ring exposed {markedActors.Count} marked party actors; " +
                    "movement is waiting for the complete two-target marker set.");
                bardamsRingIncompleteTargetSetLogged = true;
            }

            ReleaseBardamsRingMovement("Bardam's Ring marker set is incomplete", clearDiagnosticState: false);
            return false;
        }

        bardamsRingIncompleteTargetSetLogged = false;
        uint firstTargetId = Math.Min(markedActors[0].ObjectId, markedActors[1].ObjectId);
        uint secondTargetId = Math.Max(markedActors[0].ObjectId, markedActors[1].ObjectId);
        ulong targetSignature = ((ulong)firstTargetId << 32) | secondTargetId;
        if (targetSignature != bardamsRingTargetSignature)
        {
            bardamsRingTargetSignature = targetSignature;
            bardamsRingArrivalLogged = false;
            bool localPlayerMarked = markedActors.Any(actor => actor.ObjectId == Core.Player.ObjectId);
            Logger.Information(
                $"[BardamsMettle] Bardam's Ring targets 0x{firstTargetId:X8} and 0x{secondTargetId:X8}; " +
                $"localMarked={localPlayerMarked}; joining their inner safe area.");
        }

        bardamsRingMovement.Lease(
            BardamsRingMovementLease,
            "Holding inside both Bardam's Ring donuts");

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        bool safelyInsideBoth = markedActors.All(
            actor => Core.Player.Distance2D(actor.Location) <= BardamsRingInnerSafeRadius);
        if (safelyInsideBoth)
        {
            // One acceptance marker preserves enough evidence to verify both marked and unmarked
            // local roles without restoring high-volume per-frame actor snapshots.
            if (!bardamsRingArrivalLogged)
            {
                float maximumTargetDistance = markedActors.Max(actor => Core.Player.Distance2D(actor.Location));
                Logger.Information(
                    $"[BardamsMettle] Reached Bardam's Ring inner overlap; " +
                    $"maximumTargetDistance={maximumTargetDistance:F2}.");
                bardamsRingArrivalLogged = true;
            }

            bardamsRingMovement.Stop();
            return false;
        }

        // Snapshot the moving midpoint before yielding; no BattleCharacter wrapper survives the
        // current bot frame. Using the player's elevation avoids ground-height noise between Trusts.
        Vector3 destination = new(
            (markedActors[0].Location.X + markedActors[1].Location.X) * 0.5f,
            Core.Player.Location.Y,
            (markedActors[0].Location.Z + markedActors[1].Location.Z) * 0.5f);
        bardamsRingMovement.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Moves to the nearest unclaimed Sacrifice tower and holds within its safe radius.
    /// </summary>
    /// <returns><see langword="true"/> while traveling or yielding to active harmful avoidance.</returns>
    private async Task<bool> HandleSacrificeTowerAsync()
    {
        DateTime now = DateTime.UtcNow;
        List<BattleCharacter> activeCasters = GetActiveCasters(EnemyAction.Sacrifice);
        List<BattleCharacter> casters = activeCasters
            .Where(caster => IsWorldPositionAvailable(caster.SpellCastInfo.CastLocation))
            .ToList();

        if (sacrificeTowerDestinationActive)
        {
            BattleCharacter latched = casters.FirstOrDefault(caster => caster.ObjectId == sacrificeTowerCasterObjectId);
            if (latched != null)
            {
                sacrificeTowerHoldUntilUtc = now + latched.SpellCastInfo.RemainingCastTime + SacrificeTowerImpactGrace;
            }
            else if (now >= sacrificeTowerHoldUntilUtc)
            {
                ReleaseSacrificeTower("Sacrifice tower cast and impact grace ended");
            }
        }

        if (!sacrificeTowerDestinationActive && casters.Count > 0)
        {
            BattleCharacter selected = casters
                .OrderBy(caster => CountOtherPartyMembersNear(caster.SpellCastInfo.CastLocation) > 0)
                .ThenBy(caster => Core.Player.Distance2D(caster.SpellCastInfo.CastLocation))
                .ThenBy(caster => caster.ObjectId)
                .First();

            sacrificeTowerCasterObjectId = selected.ObjectId;
            sacrificeTowerDestination = selected.SpellCastInfo.CastLocation;
            sacrificeTowerHoldUntilUtc = now + selected.SpellCastInfo.RemainingCastTime + SacrificeTowerImpactGrace;
            sacrificeTowerDestinationActive = true;
            sacrificeTowerDestinationUnavailableLogged = false;
            Logger.Information(
                $"[BardamsMettle] Claimed Sacrifice tower caster=0x{selected.ObjectId:X8} " +
                $"destination={sacrificeTowerDestination} otherOccupants={CountOtherPartyMembersNear(sacrificeTowerDestination)}.");
        }
        else if (!sacrificeTowerDestinationActive && activeCasters.Count > 0 &&
                 !sacrificeTowerDestinationUnavailableLogged)
        {
            sacrificeTowerDestinationUnavailableLogged = true;
            Logger.Warning(
                "[BardamsMettle] Sacrifice helpers are casting but expose no usable CastLocation; " +
                "tower movement is withheld rather than guessing at a destination.");
        }

        if (!sacrificeTowerDestinationActive)
        {
            return false;
        }

        TimeSpan lease = GetPositivePositionLease(sacrificeTowerHoldUntilUtc);
        sacrificeTowerMovement.Lease(lease, "Holding Bardam's Sacrifice tower");

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(sacrificeTowerDestination) <= SacrificeTowerArrivalDistance)
        {
            sacrificeTowerMovement.Stop();
            return false;
        }

        sacrificeTowerMovement.MoveTowards(sacrificeTowerDestination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Hides behind the unique surviving Star Shard during Meteor Impact.
    /// </summary>
    /// <returns><see langword="true"/> while traveling or yielding to active harmful avoidance.</returns>
    private async Task<bool> HandleMeteorShelterAsync()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter meteor = GetActiveCasters(EnemyAction.MeteorImpact)
            .FirstOrDefault(caster => caster.BaseId == EnemyObjectId.LoomingShadow);

        if (meteor != null)
        {
            meteorShelterHoldUntilUtc = now + meteor.SpellCastInfo.RemainingCastTime + MeteorImpactGrace;
            BattleCharacter[] survivingRocks = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                // A Star Shard is a blocker even though it is not a combat target. Match the
                // destruction lifecycle directly instead of requiring player-style IsAlive state.
                .Where(actor => actor.IsValid && !actor.IsDead && actor.BaseId == EnemyObjectId.StarShard)
                .ToArray();

            if (survivingRocks.Length == 1 &&
                TryCreateMeteorShelterDestination(meteor.Location, survivingRocks[0].Location, out Vector3 destination))
            {
                bool changedRock = meteorShelterRockObjectId != survivingRocks[0].ObjectId;
                meteorShelterRockObjectId = survivingRocks[0].ObjectId;
                meteorShelterDestination = destination;
                meteorShelterDestinationActive = true;
                meteorShelterUnavailableLogged = false;

                if (changedRock)
                {
                    Logger.Information(
                        $"[BardamsMettle] Meteor shelter selected Star Shard 0x{meteorShelterRockObjectId:X8} " +
                        $"at {survivingRocks[0].Location}; destination={meteorShelterDestination}.");
                }
            }
            else if (!meteorShelterDestinationActive && !meteorShelterUnavailableLogged)
            {
                meteorShelterUnavailableLogged = true;
                Logger.Warning(
                    $"[BardamsMettle] Meteor Impact is active with {survivingRocks.Length} surviving Star Shards; " +
                    "shelter movement will wait for exactly one validated blocker.");
            }
        }
        else if (meteorShelterDestinationActive && now >= meteorShelterHoldUntilUtc)
        {
            ReleaseMeteorShelter("Meteor Impact cast and impact grace ended");
        }

        if (!meteorShelterDestinationActive)
        {
            return false;
        }

        TimeSpan lease = GetPositivePositionLease(meteorShelterHoldUntilUtc);
        meteorShelterMovement.Lease(
            lease,
            "Holding line-of-sight shelter behind Bardam's surviving Star Shard");

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return true;
        }

        if (Core.Player.Distance2D(meteorShelterDestination) <= MeteorShelterArrivalDistance)
        {
            meteorShelterMovement.Stop();
            return false;
        }

        meteorShelterMovement.MoveTowards(meteorShelterDestination);
        await Coroutine.Yield();
        return true;
    }

    /// <summary>
    /// Reserves and reapplies a look-away heading during Empty Gaze's final impact window.
    /// </summary>
    /// <remarks>
    /// This method never owns movement. Registered Tremblor and Charge hazards therefore retain
    /// priority while the facing lease prevents the combat routine from turning back toward the
    /// Hunter at the last moment.
    /// </remarks>
    private void HandleEmptyGazeFacing()
    {
        DateTime now = DateTime.UtcNow;
        BattleCharacter gaze = GetActiveCasters(EnemyAction.EmptyGaze).FirstOrDefault();
        if (gaze != null)
        {
            emptyGazeOrigin = gaze.Location;
            emptyGazeHoldUntilUtc = now + gaze.SpellCastInfo.RemainingCastTime + EmptyGazeImpactGrace;
            emptyGazeFacingArmed |= gaze.SpellCastInfo.RemainingCastTime <= EmptyGazeFacingLead;
        }

        if (!emptyGazeFacingArmed)
        {
            return;
        }

        if (now >= emptyGazeHoldUntilUtc)
        {
            ReleaseEmptyGazeFacing("Empty Gaze impact grace ended");
            return;
        }

        TimeSpan lease = GetPositivePositionLease(emptyGazeHoldUntilUtc);
        CapabilityManager.Update(
            emptyGazeFacingHandle,
            CapabilityFlags.Facing,
            lease,
            "Holding look-away facing for Bardam's Empty Gaze");
        emptyGazeFacingOwned = true;

        Vector3 away = Core.Player.Location - emptyGazeOrigin;
        if (Math.Abs(away.X) + Math.Abs(away.Z) < 0.1f)
        {
            away = new Vector3(0.0f, 0.0f, 1.0f);
        }

        float desiredHeading = (float)Math.Atan2(away.X, away.Z);
        Core.Player.SetFacing(desiredHeading);
    }

    /// <summary>
    /// Latches each Heavy Strike parent cast so its origin and rotation survive the helper sequence.
    /// </summary>
    private void UpdateHeavyStrikeForecasts()
    {
        DateTime now = DateTime.UtcNow;
        foreach (BattleCharacter caster in GetActiveCasters(EnemyAction.HeavyStrike))
        {
            heavyStrikeForecasts[caster.ObjectId] = new HeavyStrikeSequenceForecast(
                caster.Location,
                -caster.Heading,
                now + caster.SpellCastInfo.RemainingCastTime + HeavyStrikePostCastHold);
        }

        foreach (uint expiredObjectId in heavyStrikeForecasts
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            heavyStrikeForecasts.Remove(expiredObjectId);
        }
    }

    /// <summary>
    /// Correlates current helper casts with retained parents and exposes only the next-resolving band.
    /// </summary>
    /// <remarks>
    /// Helpers for later bands begin before earlier casts finish. Selecting the lowest active stage
    /// reproduces the encounter's concentric sequence instead of treating concurrent cast bars as
    /// concurrent damage. Origin matching keeps the two phase-three Warrior sequences independent.
    /// </remarks>
    private IEnumerable<HeavyStrikeBandForecast> GetActiveHeavyStrikeBandForecasts()
    {
        DateTime now = DateTime.UtcNow;
        List<HeavyStrikeHelperForecast> activeHelpers =
        [
            .. GetActiveCasters(EnemyAction.HeavyStrikeFirstImpact)
                .Select(caster => new HeavyStrikeHelperForecast(caster.Location, HeavyStrikeStage.First)),
            .. GetActiveCasters(EnemyAction.HeavyStrikeSecondImpact)
                .Select(caster => new HeavyStrikeHelperForecast(caster.Location, HeavyStrikeStage.Second)),
            .. GetActiveCasters(EnemyAction.HeavyStrikeThirdImpact)
                .Select(caster => new HeavyStrikeHelperForecast(caster.Location, HeavyStrikeStage.Third)),
        ];

        foreach (HeavyStrikeSequenceForecast sequence in heavyStrikeForecasts.Values
                     .Where(forecast => forecast.ExpiresAtUtc > now))
        {
            HeavyStrikeHelperForecast helper = activeHelpers
                .Where(candidate => candidate.Origin.Distance2D(sequence.Origin) <= HeavyStrikeHelperOriginTolerance)
                .OrderBy(candidate => candidate.Stage)
                .FirstOrDefault();
            if (helper != null)
            {
                yield return new HeavyStrikeBandForecast(sequence.Origin, sequence.Rotation, helper.Stage);
            }
        }
    }

    /// <summary>
    /// Returns the immutable local-space polygon for one Heavy Strike helper stage.
    /// </summary>
    /// <param name="stage">Next-resolving radial band selected from current helper casts.</param>
    /// <returns>The padded 271-degree sector band for that stage.</returns>
    private static Vector2[] GetHeavyStrikeBandPolygon(HeavyStrikeStage stage) => stage switch
    {
        HeavyStrikeStage.First => HeavyStrikeFirstBandPolygon,
        HeavyStrikeStage.Second => HeavyStrikeSecondBandPolygon,
        HeavyStrikeStage.Third => HeavyStrikeThirdBandPolygon,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown Heavy Strike stage."),
    };

    /// <summary>
    /// Registers one cast-time circle with its documented actor or ground origin.
    /// </summary>
    /// <param name="actionId">Encounter action that owns the damaging cast.</param>
    /// <param name="radius">Damage radius including navigation clearance.</param>
    /// <param name="useCastLocation">Whether the action is authored at its ground cast location.</param>
    private static void AddCastCircle(uint actionId, float radius, bool useCastLocation)
    {
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsBardamTrialCombatActive,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == actionId,
            radiusProducer: _ => radius,
            locationProducer: actor => useCastLocation ? actor.SpellCastInfo.CastLocation : actor.Location,
            priority: AvoidancePriority.High));
    }

    /// <summary>
    /// Creates one counter-clockwise sector band facing true south before RB applies cast rotation.
    /// </summary>
    /// <param name="innerRadius">Padded inner safe edge, or zero for a cone from the origin.</param>
    /// <param name="outerRadius">Padded outer reach of the damaging band.</param>
    /// <param name="fullAngleDegrees">Padded full angular width in degrees.</param>
    /// <returns>A local-space polygon suitable for RebornBuddy avoidance.</returns>
    private static Vector2[] CreateSectorBandPolygon(
        float innerRadius,
        float outerRadius,
        float fullAngleDegrees)
    {
        const int ArcSegments = 32;
        float halfAngle = fullAngleDegrees * 0.5f * (MathF.PI / 180.0f);
        List<Vector2> points = new((ArcSegments * 2) + 2);

        if (innerRadius <= 0.0f)
        {
            points.Add(Vector2.Zero);
        }

        for (int index = 0; index <= ArcSegments; index++)
        {
            float angle = halfAngle - ((halfAngle * 2.0f) * index / ArcSegments);
            points.Add(new Vector2(outerRadius * MathF.Sin(angle), outerRadius * MathF.Cos(angle)));
        }

        if (innerRadius > 0.0f)
        {
            for (int index = 0; index <= ArcSegments; index++)
            {
                float angle = -halfAngle + ((halfAngle * 2.0f) * index / ArcSegments);
                points.Add(new Vector2(innerRadius * MathF.Sin(angle), innerRadius * MathF.Cos(angle)));
            }
        }

        return [.. points];
    }

    /// <summary>
    /// Creates a caster-to-target Rush rectangle with clearance at both endpoints.
    /// </summary>
    /// <param name="length">Current horizontal distance from Garula to the selected player.</param>
    /// <returns>A local-space polygon extending forward along the charge lane.</returns>
    private static Vector2[] CreateRushLanePolygon(float length)
    {
        float halfWidth = RushLaneAvoidWidth * 0.5f;
        return
        [
            new(halfWidth, length + RushLaneEndMargin),
            new(-halfWidth, length + RushLaneEndMargin),
            new(-halfWidth, -RushLaneEndMargin),
            new(halfWidth, -RushLaneEndMargin),
        ];
    }

    /// <summary>
    /// Builds a point directly behind a Star Shard from the Looming Shadow's perspective.
    /// </summary>
    private static bool TryCreateMeteorShelterDestination(
        Vector3 meteorLocation,
        Vector3 rockLocation,
        out Vector3 destination)
    {
        float deltaX = rockLocation.X - meteorLocation.X;
        float deltaZ = rockLocation.Z - meteorLocation.Z;
        float length = MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (length < 0.1f)
        {
            destination = default;
            return false;
        }

        destination = new Vector3(
            rockLocation.X + ((deltaX / length) * MeteorShelterBehindRockDistance),
            Core.Player.Location.Y,
            rockLocation.Z + ((deltaZ / length) * MeteorShelterBehindRockDistance));
        return true;
    }

    private static List<BattleCharacter> GetActiveCasters(uint actionId) =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == actionId &&
                actor.SpellCastInfo.IsValid)
            .ToList();

    private static bool IsTremblorCircleActive() =>
        GetActiveCasters(EnemyAction.TremblorCircle).Count > 0;

    /// <summary>
    /// Returns current party actors carrying the Bardam's Ring VFX.
    /// </summary>
    /// <remarks>
    /// Core.Player is added explicitly because RebornBuddy can omit the local player from both its
    /// BattleCharacter enumeration and visible-party wrappers. Callers must copy positions before
    /// yielding and must not retain any returned actor wrapper across bot frames.
    /// </remarks>
    private static List<BattleCharacter> GetBardamsRingTargets()
    {
        List<BattleCharacter> partyActors = PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Where(actor => actor != null && actor.IsValid)
            .ToList();
        if (Core.Player != null && Core.Player.IsValid)
        {
            // Prefer the LocalPlayer wrapper even when VisibleMembers supplies a same-ID generic
            // wrapper; only the local wrapper reliably exposes player-attached VFX state.
            partyActors.RemoveAll(actor => actor.ObjectId == Core.Player.ObjectId);
            partyActors.Add(Core.Player);
        }

        return partyActors
            .Where(HasBardamsRingVfx)
            .OrderBy(actor => actor.ObjectId)
            .ToList();
    }

    private static bool HasBardamsRingVfx(BattleCharacter actor) =>
        actor.VfxContainer.IsValid &&
        actor.VfxContainer.Vfx.Any(vfx =>
            vfx != null && vfx.IsValid && vfx.Id == PlayerVfx.BardamsRing);

    /// <summary>
    /// Returns current Rush lanes whose selected player is someone other than the local player.
    /// </summary>
    /// <remarks>
    /// Only scalar origin, rotation, and length leave this method. Neither the caster nor target RB
    /// wrapper is retained beyond the frame in which the avoidance collection is produced.
    /// </remarks>
    private static IEnumerable<RushLane> GetActiveNonPlayerRushLanes()
    {
        uint playerObjectId = Core.Player.ObjectId;
        foreach (BattleCharacter caster in GetActiveCasters(EnemyAction.Rush))
        {
            uint targetObjectId = caster.SpellCastInfo.TargetId;
            if (targetObjectId == 0 || targetObjectId == playerObjectId)
            {
                continue;
            }

            GameObject target = GameObjectManager.GetObjectByObjectId(targetObjectId);
            if (target == null || !target.IsValid)
            {
                continue;
            }

            Vector3 origin = caster.Location;
            Vector3 targetLocation = target.Location;
            float length = origin.Distance2D(targetLocation);
            if (length <= 0.1f)
            {
                continue;
            }

            // Avoidance polygons rotate opposite FFXIV heading, matching AddAvoidRectangle.
            float rotation = -MathEx.CalculateNeededFacing(origin, targetLocation);
            yield return new RushLane(origin, rotation, length);
        }
    }

    private static int CountOtherPartyMembersNear(Vector3 location) =>
        PartyManager.VisibleMembers
            .Select(member => member.BattleCharacter)
            .Count(actor => actor != null && actor.IsValid && actor.IsAlive && !actor.IsMe &&
                actor.Distance2D(location) <= SacrificeTowerOccupancyDistance);

    private static bool IsWorldPositionAvailable(Vector3 location) =>
        Math.Abs(location.X) + Math.Abs(location.Y) + Math.Abs(location.Z) > 0.01f;

    private static TimeSpan GetPositivePositionLease(DateTime holdUntilUtc) =>
        TimeSpan.FromMilliseconds(Math.Max(250.0, (holdUntilUtc - DateTime.UtcNow).TotalMilliseconds));

    private static bool IsGarulaCombatActive() =>
        Core.Player.InCombat &&
        WorldManager.ZoneId == (uint)Data.ZoneId.BardamsMettle &&
        WorldManager.SubZoneId == (uint)SubZoneId.BardamsHunt;

    private static bool IsBardamTrialActive() =>
        WorldManager.ZoneId == (uint)Data.ZoneId.BardamsMettle &&
        WorldManager.SubZoneId == (uint)SubZoneId.TheRebirthofBardamtheBrave &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsVisible && IsBardamEncounterObject(actor.BaseId));

    private static bool IsBardamTrialCombatActive() => Core.Player.InCombat && IsBardamTrialActive();

    private static bool IsBardamEncounterObject(uint baseId) =>
        baseId is EnemyObjectId.HunterOfBardam or EnemyObjectId.Bardam or EnemyObjectId.WarriorOfBardam or
            EnemyObjectId.ThrowingSpear or EnemyObjectId.StarShard or EnemyObjectId.LoomingShadow;

    /// <summary>
    /// Releases the first-boss Rush latch when its cast lifecycle ends or its destination is invalid.
    /// </summary>
    /// <param name="reason">Lifecycle reason recorded by the shared capability manager.</param>
    private void ReleaseRushMovement(string reason)
    {
        rushMovement.Release(reason);
        rushCasterObjectId = 0;
        rushSheepDestination = default;
        rushArrivalLogged = false;
        rushSheepUnavailableLogged = false;
    }

    private void ReleaseSacrificeTower(string reason)
    {
        sacrificeTowerMovement.Release(reason);

        sacrificeTowerCasterObjectId = 0;
        sacrificeTowerDestination = default;
        sacrificeTowerHoldUntilUtc = DateTime.MinValue;
        sacrificeTowerDestinationActive = false;
        sacrificeTowerDestinationUnavailableLogged = false;
    }

    /// <summary>
    /// Releases only Bardam's Ring movement ownership when its VFX lifecycle ends or is ambiguous.
    /// </summary>
    /// <param name="reason">Lifecycle reason recorded by the shared capability manager.</param>
    /// <param name="clearDiagnosticState">
    /// Whether a completed lifecycle should reset its one-shot target and incomplete-set logging.
    /// </param>
    private void ReleaseBardamsRingMovement(string reason, bool clearDiagnosticState = true)
    {
        bardamsRingMovement.Release(reason);

        if (clearDiagnosticState)
        {
            bardamsRingTargetSignature = 0;
            bardamsRingArrivalLogged = false;
            bardamsRingIncompleteTargetSetLogged = false;
        }
    }

    private void ReleaseMeteorShelter(string reason)
    {
        meteorShelterMovement.Release(reason);

        meteorShelterRockObjectId = 0;
        meteorShelterDestination = default;
        meteorShelterHoldUntilUtc = DateTime.MinValue;
        meteorShelterDestinationActive = false;
        meteorShelterUnavailableLogged = false;
    }

    private void ReleaseEmptyGazeFacing(string reason)
    {
        if (emptyGazeFacingOwned)
        {
            CapabilityManager.Clear(emptyGazeFacingHandle, CapabilityFlags.Facing, reason);
            emptyGazeFacingOwned = false;
        }

        emptyGazeOrigin = default;
        emptyGazeHoldUntilUtc = DateTime.MinValue;
        emptyGazeFacingArmed = false;
    }

    private void ResetBardamTrialState(string reason)
    {
        ReleaseBardamsRingMovement(reason);
        ReleaseSacrificeTower(reason);
        ReleaseMeteorShelter(reason);
        ReleaseEmptyGazeFacing(reason);
        heavyStrikeForecasts.Clear();
    }

    /// <summary>
    /// Clears every encounter-local latch when the dungeon lifecycle changes.
    /// </summary>
    /// <param name="reason">Lifecycle reason recorded by owned capability handles.</param>
    private void ResetEncounterState(string reason)
    {
        bardamTrialWasActive = false;
        bardamTrialCombatWasActive = false;
        ReleaseRushMovement(reason);
        ResetBardamTrialState(reason);
    }

    /// <summary>
    /// Owns one positive-position movement lifecycle without stopping avoidance-owned movement.
    /// </summary>
    /// <remarks>
    /// Each mechanic receives a separate instance so clearing one lease cannot release another.
    /// The moving flag records whether this owner actually issued movement through the player mover;
    /// release skips the corresponding stop while avoidance is escaping, because the
    /// current movement command then belongs to avoidance rather than to this state.
    /// </remarks>
    private sealed class OwnedMovementState
    {
        private readonly CapabilityManagerHandle handle = CapabilityManager.CreateNewHandle();
        private bool isOwned;
        private bool isMoving;

        /// <summary>Gets whether this lifecycle currently owns a lease or issued movement.</summary>
        internal bool IsActive => isOwned || isMoving;

        /// <summary>
        /// Acquires or refreshes this mechanic's movement capability lease.
        /// </summary>
        /// <param name="duration">Lease duration; callers refresh it while the mechanic remains valid.</param>
        /// <param name="reason">Human-readable ownership reason for capability diagnostics.</param>
        internal void Lease(TimeSpan duration, string reason)
        {
            CapabilityManager.Update(handle, CapabilityFlags.Movement, duration, reason);
            isOwned = true;
        }

        /// <summary>Issues owner-tracked movement toward a scalar world destination.</summary>
        /// <param name="destination">World position copied from the current RebornBuddy frame.</param>
        internal void MoveTowards(Vector3 destination)
        {
            Navigator.PlayerMover.MoveTowards(destination);
            isMoving = true;
        }

        /// <summary>Stops movement only when this owner previously issued the movement command.</summary>
        internal void Stop()
        {
            if (!isMoving)
            {
                return;
            }

            Navigator.PlayerMover.MoveStop();
            isMoving = false;
        }

        /// <summary>
        /// Releases this mechanic's movement lease without interrupting an active avoidance escape.
        /// </summary>
        /// <param name="reason">Lifecycle reason recorded by the shared capability manager.</param>
        internal void Release(string reason)
        {
            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Stop();
            }
            else
            {
                // Avoidance has replaced the owner's movement command, so only forget ownership.
                isMoving = false;
            }

            if (isOwned)
            {
                CapabilityManager.Clear(handle, CapabilityFlags.Movement, reason);
                isOwned = false;
            }
        }
    }

    private static class EnemyNpc
    {
        /// <summary>First-boss Steppe Sheep NPC-name ID used as Rush cover.</summary>
        internal const uint SteppeSheep = 6174;
    }

    private static class EnemyObjectId
    {
        // These BNpcBase IDs distinguish the non-targetable trial actors. They are consumed through
        // GameObject.BaseId; treating them as localized NpcId values would fail on RebornBuddy.
        internal const uint Bardam = 0x1AA3;
        internal const uint WarriorOfBardam = 0x1AA4;
        internal const uint HunterOfBardam = 0x1AA5;
        internal const uint ThrowingSpear = 0x1F49;
        internal const uint StarShard = 0x1F4A;
        internal const uint LoomingShadow = 0x1F4D;
    }

    private static class ArenaCenter
    {
        /// <summary>First-boss Garula arena center.</summary>
        internal static readonly Vector3 Garula = new(4.5f, -0.5f, 250f);

        /// <summary>Second-boss trial center at the confirmed Z=-14 location.</summary>
        internal static readonly Vector3 BardamsTrial = new(-28.5f, -45f, -14f);

        /// <summary>Final-boss Yol arena center.</summary>
        internal static readonly Vector3 Yol = new(24f, -167.5f, -475f);
    }

    private static class EnemyAction
    {
        // Phase-one hazards. Choreography-only casts intentionally have no constants here because
        // DutyMechanic neither detects nor owns them.
        internal const uint TremblorCircle = 9596;
        internal const uint TremblorDonut = 9595;
        internal const uint EmptyGaze = 7940;
        internal const uint Charge = 9599;

        // Phase-two positive positioning and ground hazards.
        internal const uint Sacrifice = 7937;
        internal const uint CometFirst = 9597;
        internal const uint CometRest = 9598;
        internal const uint HeavyStrike = 9591;
        internal const uint HeavyStrikeFirstImpact = 9592;
        internal const uint HeavyStrikeSecondImpact = 9593;
        internal const uint HeavyStrikeThirdImpact = 9594;
        internal const uint CometImpact = 9600;

        // Phase-three rock lifecycle and final line-of-sight check.
        internal const uint Reconstruct = 7934;
        internal const uint MeteorImpact = 9602;

        /// <summary>Final-boss gaze retained under the pre-existing last-moment workaround.</summary>
        internal const uint EyeOfTheFierce = 7949;

        /// <summary>First-boss targeted Rush from Garula to the selected player.</summary>
        internal const uint Rush = 7929;
    }

    private static class PlayerVfx
    {
        // RB exposes Bardam's Ring's network icon as VFX 58 on exactly two party actors for its
        // complete 3.5-second lifecycle.
        internal const uint BardamsRing = 58;
    }

    /// <summary>
    /// Identifies the resolution order of Heavy Strike's overlapping helper casts.
    /// </summary>
    private enum HeavyStrikeStage
    {
        First,
        Second,
        Third,
    }

    /// <summary>
    /// Immutable parent cast snapshot retained after the Heavy Strike actor stops casting.
    /// </summary>
    /// <param name="Origin">World origin copied from the casting Hunter or Warrior.</param>
    /// <param name="Rotation">RebornBuddy polygon rotation derived from the cast-time heading.</param>
    /// <param name="ExpiresAtUtc">Final impact deadline including navigation latency.</param>
    private sealed record HeavyStrikeSequenceForecast(Vector3 Origin, float Rotation, DateTime ExpiresAtUtc);

    /// <summary>
    /// Immutable current-frame helper identity used to advance one matching parent sequence.
    /// </summary>
    /// <param name="Origin">Helper origin, observed equal to its parent origin in both trial variants.</param>
    /// <param name="Stage">Radial band resolved by this helper action.</param>
    private sealed record HeavyStrikeHelperForecast(Vector3 Origin, HeavyStrikeStage Stage);

    /// <summary>
    /// Immutable Heavy Strike polygon instance published for the next-resolving helper only.
    /// </summary>
    /// <param name="Origin">Retained parent origin used for the radial band.</param>
    /// <param name="Rotation">Retained parent rotation; helper headings are not reliable in phase two.</param>
    /// <param name="Stage">Current radial band selected from matching active helpers.</param>
    private sealed record HeavyStrikeBandForecast(Vector3 Origin, float Rotation, HeavyStrikeStage Stage);

    /// <summary>
    /// Immutable first-boss charge lane used to keep non-targeted players out of Rush.
    /// </summary>
    /// <param name="Origin">Garula's current world position.</param>
    /// <param name="Rotation">RebornBuddy polygon rotation from Garula toward the selected player.</param>
    /// <param name="Length">Current horizontal distance from Garula to the selected player.</param>
    private sealed record RushLane(Vector3 Origin, float Rotation, float Length);
}
