using Buddy.Coroutines;
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
/// Lv. 100: Recollection normal-trial logic for Zelenia.
/// </summary>
/// <remarks>
/// This first pass deliberately owns only mechanics whose action caster, geometry, and response
/// are represented by normal RebornBuddy cast state. Rose-sector propagation, Roseblood Drop tile
/// selection, the persistent phase-two center bleed, lingering Shock puddles, and Stock Break's
/// marked stack target require a live RebornBuddy capture before they can be modeled without
/// guessing at map-effect or target-icon state.
/// </remarks>
public class Recollection : AbstractDungeon
{
    // Shock and Roseblood Bloom can overlap later in the fight. Independent leases prevent one
    // mechanic from releasing the combat routine's movement suppression while the other is active.
    private readonly CapabilityManagerHandle shockMovementHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle rosebloodBloomMovementHandle = CapabilityManager.CreateNewHandle();

    private DateTime shockSpreadEndsAtUtc = DateTime.MinValue;
    private bool shockVisualWasCasting;
    private bool shockMovementLeaseActive;
    private bool rosebloodBloomMovementLeaseActive;
    private bool movingToArenaCenter;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Recollection;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.SpecterOfTheLost];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        // Perfumed Quietus resolves after the party is knocked back and stunned, so Roseblood
        // Bloom is the last actionable cast window in which group mitigation can be applied.
        EnemyAction.RosebloodBloomKnockback,
        EnemyAction.ValorousAscensionFirstHit,
        EnemyAction.ThornedCatharsis,
    ];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Alexandrian Thunder IV helpers cast the circle and donut together but resolve them in
        // sequence. Publishing both at once removes the entire arena, so only the next resolving
        // helper is active; the remaining helper becomes active after the first cast finishes.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInRecollectionCombat,
            objectSelector: caster => EnemyAction.AlexandrianThunderCircleCasts.Contains(caster.CastingSpellId)
                && IsAmongNextCasters(caster, EnemyAction.AlexandrianThunderIVCasts, 1),
            radiusProducer: caster => 8.0f,
            priority: AvoidancePriority.High));

        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => EnemyAction.AlexandrianThunderDonutCasts.Contains(caster.CastingSpellId)
                && IsAmongNextCasters(caster, EnemyAction.AlexandrianThunderIVCasts, 1),
            outerRadius: 24.0f,
            innerRadius: 8.0f,
            priority: AvoidancePriority.High);

        // Power Break is a 24-yalm forward half-room cleave with a 64-yalm total width. The cast
        // heading, rather than the current target, owns which side is unsafe.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => EnemyAction.PowerBreakCasts.Contains(caster.CastingSpellId),
            width: 64.0f,
            length: 24.0f,
            priority: AvoidancePriority.High);

        // Holy Hazard resolves as two opposing cones followed by a second opposing pair. Selecting
        // the two earliest helpers preserves the next safe wedges instead of covering all six sectors.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => IsAmongNextCasters(caster, EnemyAction.HolyHazardCasts, 2),
            leashPointProducer: () => ArenaCenter.Zelenia,
            leashRadius: ArenaSafeRadius,
            rotationDegrees: 0.0f,
            radius: 24.0f,
            arcDegrees: 120.0f);

        // Specter of the Lost is aimed at both tanks. Non-targets avoid the 45-degree cones while
        // the targeted tank remains in place and relies on the dedicated mitigation path.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => caster.CastingSpellId == EnemyAction.SpecterOfTheLost
                && caster.SpellCastInfo.TargetId != Core.Player.ObjectId,
            leashPointProducer: () => ArenaCenter.Zelenia,
            leashRadius: ArenaSafeRadius,
            rotationDegrees: 0.0f,
            radius: 50.0f,
            arcDegrees: 45.0f);

        // Thunder Slash is three sequential opposing cone pairs. Only the earliest pair is unsafe;
        // the ordered Alexandrian Thunder IV handlers above remain active at the same time.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => IsAmongNextCasters(caster, EnemyAction.ThunderSlashCasts, 2),
            leashPointProducer: () => ArenaCenter.Zelenia,
            leashRadius: ArenaSafeRadius,
            rotationDegrees: 0.0f,
            radius: 24.0f,
            arcDegrees: 60.0f);

        // Alexandrian Thunder III is authored at the helper's cast location rather than its actor
        // location. These circles are safe to model independently; rose-sector chain reactions are
        // omitted until RebornBuddy captures expose the map-effect lifecycle.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: IsInRecollectionCombat,
            objectSelector: caster => EnemyAction.AlexandrianThunderIIICasts.Contains(caster.CastingSpellId),
            radiusProducer: caster => 4.0f,
            locationProducer: caster => caster.SpellCastInfo.CastLocation,
            priority: AvoidancePriority.High));

        // Valorous Ascension summons two Briar Thorn lines at a time. Keeping only the earliest pair
        // prevents the later pair from prematurely eliminating its still-valid lanes.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: IsInRecollectionCombat,
            objectSelector: caster => IsAmongNextCasters(caster, EnemyAction.ValorousAscensionLineCasts, 2),
            width: 8.0f,
            length: 40.0f,
            priority: AvoidancePriority.High);

        // The visible arena radius is 16 yalms. A one-yalm inset keeps path candidates away from the
        // lethal wall without clipping the six phase-two sectors more than necessary.
        AvoidanceHelpers.AddAvoidDonut(
            canRun: IsInRecollectionCombat,
            locationProducer: () => ArenaCenter.Zelenia,
            outerRadius: 90.0f,
            innerRadius: ArenaSafeRadius,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseShockMovement("Leaving Recollection");
        ReleaseRosebloodBloomMovement("Leaving Recollection");
        shockVisualWasCasting = false;
        shockSpreadEndsAtUtc = DateTime.MinValue;

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // The ordered helper casts above must have one geometry owner. SideStep can publish both
        // halves of an in/out or sequential cone set simultaneously, leaving no navigable point.
        SidestepPlugin.Enabled = false;

        if (!IsInRecollectionCombat())
        {
            ReleaseShockMovement("Recollection combat ended");
            ReleaseRosebloodBloomMovement("Recollection combat ended");
            shockVisualWasCasting = false;
            shockSpreadEndsAtUtc = DateTime.MinValue;
            return false;
        }

        await TankBusterSpells();
        await DamageMitigationSpells();
        await HandleShockSpreadAsync();

        return await HandleRosebloodBloomKnockbackAsync();
    }

    /// <summary>
    /// Spreads the party during Shock's eight-second overhead countdown.
    /// </summary>
    /// <returns>A task that completes after the spread avoid has been refreshed.</returns>
    private async Task HandleShockSpreadAsync()
    {
        DateTime now = DateTime.UtcNow;
        bool visualIsCasting = IsAnyActionCasting(EnemyAction.ShockVisualCasts);

        if (visualIsCasting && !shockVisualWasCasting)
        {
            // The observed mechanic definition uses an eight-second target-icon countdown. The
            // extra half-yalm and half-second cover actor radius and normal bot-tick latency.
            shockSpreadEndsAtUtc = now.AddMilliseconds(8_500);
            shockMovementLeaseActive = true;
            CapabilityManager.Update(
                shockMovementHandle,
                CapabilityFlags.Movement,
                8_500,
                "Holding combat-routine movement for Shock spread");
        }

        shockVisualWasCasting = visualIsCasting;

        if (now < shockSpreadEndsAtUtc)
        {
            await MovementHelpers.Spread(
                (shockSpreadEndsAtUtc - now).TotalMilliseconds,
                radius: 4.5f);
        }
        else
        {
            ReleaseShockMovement("Shock spread countdown ended");
        }
    }

    /// <summary>
    /// Moves inside the safe center radius before Roseblood Bloom's unavoidable knockback.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only while this handler is actively traveling to the center;
    /// otherwise <see langword="false"/> so healing, mitigation, and rotation remain schedulable.
    /// </returns>
    private async Task<bool> HandleRosebloodBloomKnockbackAsync()
    {
        BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(character => character.IsCasting
                && character.CastingSpellId == EnemyAction.RosebloodBloomKnockback);

        if (caster == null)
        {
            ReleaseRosebloodBloomMovement("Roseblood Bloom knockback ended");
            return false;
        }

        rosebloodBloomMovementLeaseActive = true;
        CapabilityManager.Update(
            rosebloodBloomMovementHandle,
            CapabilityFlags.Movement,
            caster.SpellCastInfo.RemainingCastTime,
            "Holding combat-routine movement for Roseblood Bloom knockback");

        // Active emergency avoidance retains ownership. The center remains the desired destination
        // and will be reacquired on the next tick if the knockback cast is still active.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        if (Core.Player.Distance2D(ArenaCenter.Zelenia) > KnockbackCenterRadius)
        {
            movingToArenaCenter = true;
            Navigator.PlayerMover.MoveTowards(ArenaCenter.Zelenia);
            await Coroutine.Yield();
            return true;
        }

        if (movingToArenaCenter)
        {
            Navigator.PlayerMover.MoveStop();
            movingToArenaCenter = false;
        }

        return false;
    }

    /// <summary>
    /// Selects only the next resolving members of a simultaneous helper-cast family.
    /// </summary>
    /// <param name="candidate">Candidate helper currently being evaluated by avoidance.</param>
    /// <param name="actionIds">Action family whose remaining cast times define resolution order.</param>
    /// <param name="count">Maximum number of same-stage helpers that resolve together.</param>
    /// <returns><see langword="true"/> when <paramref name="candidate"/> is in the next group.</returns>
    private static bool IsAmongNextCasters(BattleCharacter candidate, HashSet<uint> actionIds, int count)
    {
        if (candidate == null || !candidate.IsCasting || !actionIds.Contains(candidate.CastingSpellId))
        {
            return false;
        }

        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(character => character.IsCasting && actionIds.Contains(character.CastingSpellId))
            .OrderBy(character => character.SpellCastInfo.RemainingCastTime)
            .ThenBy(character => character.ObjectId)
            .Take(count)
            .Any(character => character.ObjectId == candidate.ObjectId);
    }

    /// <summary>
    /// Determines whether any actor is currently casting an action in the supplied family.
    /// </summary>
    /// <param name="actionIds">Action IDs to inspect.</param>
    /// <returns><see langword="true"/> when a matching live cast exists.</returns>
    private static bool IsAnyActionCasting(HashSet<uint> actionIds)
    {
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(character => character.IsCasting && actionIds.Contains(character.CastingSpellId));
    }

    /// <summary>
    /// Releases only the Shock movement lease.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded by the capability manager.</param>
    private void ReleaseShockMovement(string reason)
    {
        if (!shockMovementLeaseActive)
        {
            return;
        }

        CapabilityManager.Clear(shockMovementHandle, CapabilityFlags.Movement, reason);
        shockMovementLeaseActive = false;
    }

    /// <summary>
    /// Releases only the Roseblood Bloom movement lease and stops movement owned by this handler.
    /// </summary>
    /// <param name="reason">Diagnostic reason recorded by the capability manager.</param>
    private void ReleaseRosebloodBloomMovement(string reason)
    {
        if (rosebloodBloomMovementLeaseActive)
        {
            CapabilityManager.Clear(rosebloodBloomMovementHandle, CapabilityFlags.Movement, reason);
            rosebloodBloomMovementLeaseActive = false;
        }

        if (movingToArenaCenter && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        movingToArenaCenter = false;
    }

    /// <summary>
    /// Returns whether the dedicated Recollection territory is active and the player is in combat.
    /// </summary>
    /// <returns><see langword="true"/> only during combat in normal-mode Recollection.</returns>
    private static bool IsInRecollectionCombat()
    {
        return Core.Player != null
            && Core.Player.InCombat
            && WorldManager.ZoneId == (uint)Data.ZoneId.Recollection;
    }

    // Normal Recollection uses a circular arena centered at (100, 100) on the horizontal X/Z plane.
    private static class ArenaCenter
    {
        internal static readonly Vector3 Zelenia = new(100.0f, 0.0f, 100.0f);
    }

    // The visible wall is at radius 16.0; avoidance keeps a one-yalm inset for navigation latency.
    private const float ArenaSafeRadius = 15.0f;

    // A 10-yalm center-origin knockback is safe from within five yalms even with ordinary latency.
    private const float KnockbackCenterRadius = 5.0f;

    private static class EnemyAction
    {
        // Alexandrian Thunder IV: ordered 8-yalm circle and 8-to-24-yalm donut helper casts.
        internal static readonly HashSet<uint> AlexandrianThunderCircleCasts = [43084, 43446];
        internal static readonly HashSet<uint> AlexandrianThunderDonutCasts = [43085, 43447];
        internal static readonly HashSet<uint> AlexandrianThunderIVCasts = [43084, 43085, 43446, 43447];

        // Shock visual starts the player-targeted eight-second spread countdown.
        internal static readonly HashSet<uint> ShockVisualCasts = [43056];

        // Power Break is the boss-authored 24-by-64-yalm half-room cleave.
        internal static readonly HashSet<uint> PowerBreakCasts = [43112, 43113];

        // Holy Hazard helpers own the sequential 24-yalm, 120-degree cone pairs.
        internal static readonly HashSet<uint> HolyHazardCasts = [43126];

        // Specter of the Lost is the helper-authored 50-yalm conal tankbuster.
        internal const uint SpecterOfTheLost = 43129;

        // Thunder Slash helpers own the three sequential 24-yalm, 60-degree cone pairs.
        internal static readonly HashSet<uint> ThunderSlashCasts = [43083];

        // The helper-owned five-second cast performs the 10-yalm phase-transition knockback.
        internal const uint RosebloodBloomKnockback = 43479;

        // Alexandrian Thunder III helpers place four-yalm ground circles in phase two.
        internal static readonly HashSet<uint> AlexandrianThunderIIICasts = [43102, 43439];

        // Valorous Ascension begins a three-hit physical raidwide sequence.
        internal const uint ValorousAscensionFirstHit = 43071;

        // Briar Thorn helpers cast the phase-two 40-by-8-yalm line attacks.
        internal static readonly HashSet<uint> ValorousAscensionLineCasts = [43074];

        // Thorned Catharsis is the repeatable five-second magical raidwide.
        internal const uint ThornedCatharsis = 43127;
    }
}
