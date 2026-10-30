using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

public partial class HellOnRails
{
    // October 29 captures showed three separate handoff failures: a crate corner stopped native
    // escape, upper platforms were outside ground-circle height, and the next turret wave resolved
    // after the floor attack. These requests use the existing mover, never a second input channel.
    private readonly CapabilityManagerHandle deckEscapeHandle = CapabilityManager.CreateNewHandle();
    private readonly Dictionary<uint, HellOnRailsGeometry.Beam> turretBeams = [];
    private readonly Dictionary<uint, DateTime> turretExpiry = [];
    private DateTime floorHoldUntil;
    private bool floorHoldUpper;
    private DateTime pullRecoveryUntil;
    private HellOnRailsPull pullMotion;
    private bool pullCasting;
    private List<Vector3> pullRoute;
    private int pullRouteIndex;
    private DateTime nextTargetAttempt;

    private bool OwnsPullRecovery => DateTime.UtcNow < pullRecoveryUntil;

    private void AcquireSelectedDoomtrain()
    {
        // RB941 selects a Kill POI only inside 30y center distance, but its approach stops at
        // PullRange + CombatReach. Doomtrain's 19y reach strands an unselected target at 36y.
        // Select only OrderBot's existing, engaged boss; the routine still owns every action.
        var boss = Poi.Current?.Unit as BattleCharacter;
        if (DateTime.UtcNow < nextTargetAttempt || Poi.Current?.Type != PoiType.Kill ||
            boss == null || !boss.IsValid || boss.BaseId != 0x4A30 || !boss.IsTargetable ||
            boss.IsDead || !Core.Me.InCombat || !boss.InCombat || boss.Distance2D(Core.Me) > 49 ||
            Core.Me.PrimaryTargetPtr == boss.Pointer)
            return;
        nextTargetAttempt = DateTime.UtcNow.AddSeconds(1);
        boss.Target();
    }

    private void UpdateOverlapState()
    {
        var now = DateTime.UtcNow;
        foreach (var key in turretExpiry.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            turretExpiry.Remove(key);
            turretBeams.Remove(key);
        }
        foreach (var turret in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false))
        {
            float length = turret.CastingSpellId switch
            {
                EnemyAction.ElectrayLong => 25, EnemyAction.ElectrayLonger => 20,
                EnemyAction.ElectrayMedium => 15, EnemyAction.ElectrayShort or EnemyAction.ElectrayUpper => 10,
                _ => 0
            };
            if (!turret.IsCasting || length == 0 || turret.BaseId != 0x4A32)
                continue;
            turretBeams[turret.ObjectId] = new(turret.Location, turret.Heading, length);
            // Captured hits arrived about one second after the bar disappeared. Keep the
            // scalar cast snapshot through effect latency instead of reopening the row early.
            turretExpiry[turret.ObjectId] = now + turret.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.2);
        }
        var floor = FindCaster(EnemyAction.ThunderousBreathLowerDeck, EnemyAction.HeadlightUpperDeck);
        if (floor != null)
        {
            floorHoldUpper = floor.CastingSpellId == EnemyAction.ThunderousBreathLowerDeck;
            // Retain the required elevation through cast-to-effect latency before the later
            // turret escape drops to ground; cast disappearance alone is not impact confirmation.
            floorHoldUntil = now + floor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.8);
        }
        var pull = FindCaster(EnemyAction.Windpipe);
        bool casting = pull != null;
        if (casting && !pullCasting)
        {
            pullMotion = new HellOnRailsPull();
            pullRoute = null;
            pullRouteIndex = 0;
        }
        if (casting && TryGetCurrentCar(out int car) && car is 2 or 5)
        {
            // The turret impact follows the pull by about five seconds. The planner retains the
            // complete escape until that effect, while unrelated native hazards still take priority.
            pullRecoveryUntil = now + pull.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(5.5);
            pullMotion.CastEnds = now + pull.SpellCastInfo.RemainingCastTime;
        }
        if (OwnsPullRecovery) pullMotion?.Observe(now, Core.Me.Location);
        pullCasting = casting;
    }

    private void RegisterTurretAvoid()
    {
        AvoidanceManager.AddAvoidPolygon<HellOnRailsGeometry.Beam>(
            condition: () => IsInHellOnRailsCombat() && !OwnsPullRecovery && !IsUpperDeck(Core.Player)
                && !(OwnsGroundRoute && !groundRouteForHail)
                && DateTime.UtcNow >= lightningHoldUntil && DateTime.UtcNow >= windpipeHoldUntil,
            leashPointProducer: null, leashRadius: 50,
            rotationProducer: beam => -beam.Heading, scaleProducer: beam => 1,
            heightProducer: beam => 2,
            pointsProducer: beam => [new(-3.25f, -0.5f), new(3.25f, -0.5f),
                new(3.25f, beam.Length + 0.5f), new(-3.25f, beam.Length + 0.5f)],
            locationProducer: beam => beam.Origin,
            collectionProducer: () => turretBeams.Values.Where(beam => beam.Origin.Y < UpperDeckHeightThreshold),
            priority: AvoidancePriority.High);
    }

    private void RegisterGhostTrainAvoid()
    {
        // The train exposes its tank target and Stop(4176) about two seconds before Aetherial
        // Ray. Damage follows that stop, not the preceding Aether Surge casts. Aim the
        // 35-degree cone at the actual tank rather than the train's tangential travel heading.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            condition: IsInHellOnRailsCombat, leashPointProducer: () => ArenaCenter.Intermission,
            leashRadius: IntermissionLeashRadius,
            rotationProducer: train => -(float)Math.Atan2(train.TargetGameObject.X - train.X, train.TargetGameObject.Z - train.Z),
            scaleProducer: train => 1, heightProducer: train => 2,
            pointsProducer: train => HellOnRailsGeometry.RayCone,
            locationProducer: train => train.Location,
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(train => train.BaseId == 0x4B80 && train.HasAura(4176) && train.HasTarget &&
                    train.TargetGameObject.IsValid && train.TargetGameObject.ObjectId != Core.Me.ObjectId),
            priority: AvoidancePriority.High);
    }

    private async Task<bool> FollowPullRecoveryAsync(int car)
    {
        if (IsUpperDeck(Core.Me))
        {
            movement.Move(HellOnRailsMovement.Request.Windpipe, GetPlatformDescent());
            await Coroutine.Yield();
            return true;
        }
        if (pullRoute == null)
        {
            // Reject the first two rows even after the visual ends. The path must first escape
            // Blastpipe, then arrive behind a crate/platform before the later turret impact.
            pullRoute = HellOnRailsGeometry.FindPullRoute(car, Core.Me.Location, turretBeams.Values.ToArray());
            pullRouteIndex = 0;
        }
        if (pullRoute == null || pullRoute.Count == 0)
        {
            // Missing wave/collision evidence must return ownership to the native hazards,
            // never leave both manual travel and their registrations disabled indefinitely.
            Logger.Warning("Hell on Rails: no collision-safe post-pull route on car {0}; restoring native avoidance.", car);
            pullRecoveryUntil = DateTime.MinValue;
            movement.Release(HellOnRailsMovement.Request.Windpipe);
            return false;
        }
        while (pullRouteIndex < pullRoute.Count && Core.Me.Distance2D(pullRoute[pullRouteIndex]) < 0.45f)
            pullRouteIndex++;
        if (pullRouteIndex == pullRoute.Count)
        {
            movement.Release(HellOnRailsMovement.Request.Windpipe);
            return false;
        }
        movement.Move(HellOnRailsMovement.Request.Windpipe, WithPlayerHeight(pullRoute[pullRouteIndex]));
        await Coroutine.Yield();
        return true;
    }

    private async Task<bool> HandleDeckEscapeAsync()
    {
        bool unsafeDeck = IsUpperDeck(Core.Me) &&
            (turretBeams.Values.Any(beam => beam.Origin.Y >= UpperDeckHeightThreshold && beam.Contains(Core.Me.Location)) ||
             HailThreatensPlatform());
        // A lower-floor blast wins until its effect. The later turret is then escaped by dropping
        // across the inner edge; native pathfinding repeatedly failed to leave these platforms.
        if (!unsafeDeck || (DateTime.UtcNow < floorHoldUntil && floorHoldUpper) || lightningExpressMovementActive)
        {
            CapabilityManager.Clear(deckEscapeHandle, CapabilityFlags.Movement, "Platform escape released");
            movement.Release(HellOnRailsMovement.Request.DeckEscape);
            return false;
        }
        CapabilityManager.Update(deckEscapeHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Leaving an unsafe train platform");
        if (MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
            return false;
        movement.Move(HellOnRailsMovement.Request.DeckEscape, GetPlatformDescent());
        await Coroutine.Yield();
        return true;
    }

    private static bool HailThreatensPlatform()
    {
        var hail = FindCaster(EnemyAction.HailOfThunder);
        if (hail != null)
            return Core.Me.Distance2D(hail.SpellCastInfo.CastLocation) < ArcaneRevelationAvoidRadius;
        return false;
    }

    private void ReleaseOverlapState(string reason)
    {
        ResetGroundMechanics();
        turretBeams.Clear();
        turretExpiry.Clear();
        floorHoldUntil = pullRecoveryUntil = DateTime.MinValue;
        pullMotion = null;
        pullCasting = false;
        pullRoute = null;
        CapabilityManager.Clear(deckEscapeHandle, CapabilityFlags.Movement, reason);
        movement?.Release(HellOnRailsMovement.Request.DeckEscape);
    }
}
