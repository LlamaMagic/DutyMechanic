using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using System;
using System.Linq;
using ff14bot.AClasses;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using DutyMechanic.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 80.1: Amaurot dungeon logic.
/// </summary>
public class Amaurot : AbstractDungeon
{
    /// <summary>
    /// Set of boss-related monster IDs.
    /// </summary>
    private static readonly HashSet<uint> BossIds = [4385, 7864, 8925, // Brightsphere          :: 光明晶球
 8201, // The First Beast       :: 第一之兽
 8210, // Therion               :: 至大灾兽
 ];
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Amaurot;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [532, 1837, 2794, 5445, 7931, 9076, 9338, 9490, 9493, 10256, 10257, 11573, 11582, 12377, 12486, 12589, 12590, 12591, 12648, 12654, 12681, 12688, 12805, 12809, 12823, 12824, 12825, 13251, 13336, 13337, 13344, 13345, 13346, 15561, 15559, 15560, 15562, 15565, 15566, 15579, 15580, 15581, 15582, 15583, 15585, 16785, 16786, 18157, 17996, ];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Release shelter movement on death or loading so recovery can take over.
        if (CommonBehaviors.IsLoading || Core.Me == null || !Core.Me.IsValid || Core.Me.IsDead || !Core.Me.InCombat || WorldManager.ZoneId != 838)
        {
            firstBeastImpacts.Clear();
            ReleaseMeteorBait();
            ReleaseShelter();
            ReleaseTherion();
            return false;
        }

        var beast = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(a => a.IsValid && !a.IsDead && a.BaseId == 0x27B6 && a.Location.Distance2D(Core.Me.Location) < 65);
        if (beast != null)
        {
            UpdateFirstBeastImpacts();
            ReleaseTherion();
            SidestepPlugin.Enabled = true;
            // One encounter arbiter grants either bait placement or shelter, never
            // both. Native emergency avoidance retains movement during either phase.
            bool finalSky = beast.IsCasting && beast.CastingSpellId == 15563 || DateTime.UtcNow < shelterUntil;
            bool dropping = HasMeteorMarker(Core.Me) && !GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.BaseId == 0x29DC && a.IsCasting && a.CastingSpellId == 17108);
            if (!finalSky && dropping)
            {
                ReleaseShelter();
                return HandleMeteorBait();
            }

            ReleaseMeteorBait();
            return HandleFirstBeast(beast);
        }

        ReleaseMeteorBait();
        ReleaseShelter();
        firstBeastImpacts.Clear();
        var therion = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(a => a.IsValid && !a.IsDead && a.BaseId == 0x27C1 && a.Distance() < 100);
        if (therion != null)
        {
            // Preserve this encounter's existing SideStep exclusion: its beam and
            // collapsing-alcove semantics are owned by the local support-follow path.
            if (SidestepPlugin.Enabled)
            {
                SidestepPlugin.Enabled = false;
                therionDisabledSideStep = true;
            }

            return HandleTherion(therion);
        }

        ReleaseTherion();
        await FollowDodgeSpells();
        // SideStep (回避)
        if (WorldManager.SubZoneId != 2996)
        {
            BossIds.ToggleSideStep([8210]);
        }
        else
        {
            BossIds.ToggleSideStep();
        }

        return false;
    }

    // Cross alcove mouths laterally before changing Z; diagonal paths can leave the floor.
    private readonly CapabilityManagerHandle therionHandle = CapabilityManager.CreateNewHandle();
    private uint therionCast;
    private DateTime therionUntil;
    private bool therionMoving, therionLeased;
    // Apokalypsis repeats beyond its cast window. Hold shelter until the next phase cue.
    private bool therionSheltering;
    // Restore SideStep only when this encounter changed it, preserving a pre-existing user disable.
    private bool therionDisabledSideStep;
    private int observedTherionFloor = -1;
    // Retain the 60-by-6 beam through its instant follow-up hits; the next phase cue retires it.
    private bool therionRayActive;
    private uint therionRayCastActor;
    private Vector3 therionRayOrigin;
    private float therionRayHeading;
    private static readonly Vector2[] TherionRayPolygon =
    {
        new(3.5f, 60.5f),
        new(-3.5f, 60.5f),
        new(-3.5f, -.5f),
        new(3.5f, -.5f)
    }; //0.5y clearance on every side of the confirmed damaging lane.
    private bool HandleTherion(BattleCharacter boss)
    {
        // A completed charge removes the old front section. Invalidate RB's
        // navigation once per confirmed boss-position stage so circle escapes
        // cannot reuse a path through the destroyed floor. Unknown stages wait.
        int floor = TherionFloorStage();
        if (floor >= 0 && floor != observedTherionFloor)
        {
            if (observedTherionFloor >= 0)
            {
                AvoidanceManager.ResetNavigation();
            }

            observedTherionFloor = floor;
        }

        uint cast = boss.IsCasting ? boss.CastingSpellId : 0;
        if (cast != therionCast && cast is 15575 or 15578 or 15579 or 15582 or 15587)
        {
            therionSheltering = cast == 15575;
        }

        // Only confirmed phase cues retire a lane; an autoattack/unknown action
        // must not cancel protection while the helper's repeated hits continue.
        if (cast != therionCast && cast is 15575 or 15578 or 15579 or 15582 or 15587 && therionRayActive)
        {
            therionRayActive = false;
        }

        var ray = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(a => a.IsValid && a.BaseId == 0x233C && a.IsCasting && a.CastingSpellId == 17107);
        uint rayActor = ray?.ObjectId ?? 0;
        if (rayActor != 0 && rayActor != therionRayCastActor)
        {
            therionRayOrigin = ray.Location;
            therionRayHeading = ray.Heading;
            therionRayActive = true;
        }

        therionRayCastActor = rayActor;
        if (cast != therionCast && cast is 15575 or 15578)
        {
            // Keep the existing ten/eight-second support windows while yielding between movement ticks.
            therionUntil = DateTime.UtcNow.AddSeconds(cast == 15575 ? 10 : 8);
        }

        therionCast = cast;
        // Faces remain through both volleys. Continue support positioning until they disappear.
        bool facesPresent = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsVisible && a.BaseId == 0x27C3);
        bool active = therionSheltering || DateTime.UtcNow < therionUntil || SpellsToFollowDodge.IsCasting() || facesPresent;
        var pos = Core.Me.Location;
        // Keep ownership while in an alcove even between casts: the combat routine
        // must not shortcut its mouth when it resumes ordinary range adjustment.
        if (!active && MathF.Abs(pos.X) <= 9)
        {
            ReleaseTherionMovement();
            return false;
        }

        CapabilityManager.Update(therionHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Therion alcove-safe support follow");
        therionLeased = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            therionMoving = false;
            return false;
        }

        var ally = MovementHelpers.GetClosestAlly;
        if (ally == null || !ally.IsValid || ally.IsDead || ally.Distance() > 50 || MathF.Abs(ally.Y - pos.Y) > 1)
        {
            StopTherionMovement();
            return false;
        }

        var destination = TherionWaypoint(pos, ally.Location, therionSheltering);
        if (pos.Distance2D(destination) <= .3f)
        {
            StopTherionMovement();
            return false;
        }

        Navigator.PlayerMover.MoveTowards(destination);
        therionMoving = true;
        return true;
    }

    // Follow directly only within the same alcove. Otherwise return to X=8 before changing Z.
    private static readonly float[] TherionAlcoveRows =
    {
        -71,
        -51,
        -31,
        -17
    };
    private static Vector3 TherionWaypoint(Vector3 from, Vector3 to, bool shelter)
    {
        // Aim at the alcove center during shelter, allowing for the 0.3-yalm arrival tolerance.
        if (shelter && MathF.Abs(to.X) >= 10.5f && MathF.Abs(to.X) <= 12.75f)
        {
            foreach (var row in TherionAlcoveRows)
            {
                if (MathF.Abs(to.Z - row) <= 1f)
                {
                    to = new Vector3(to.X, to.Y, row);
                    break;
                }
            }
        }

        bool targetInAlcove = MathF.Abs(to.X) >= 10.5f && MathF.Abs(to.X) <= 12.75f && TherionAlcoveRows.Any(z => MathF.Abs(to.Z - z) <= .75f);
        bool sameRow = targetInAlcove && TherionAlcoveRows.Any(z => MathF.Abs(from.Z - z) <= .75f && MathF.Abs(to.Z - z) <= .75f);
        bool sameAlcove = sameRow && MathF.Sign(from.X) == MathF.Sign(to.X);
        if (MathF.Abs(from.X) > 9 && !sameAlcove)
        {
            return new Vector3(MathF.Sign(from.X) * 8, from.Y, from.Z);
        }

        // Align to the same row used by the exit check before entering an alcove.
        if (MathF.Abs(to.X) > 9 && (!sameRow || MathF.Abs(from.Z - to.Z) > .5f))
        {
            return new Vector3(MathF.Sign(to.X) * 8, from.Y, to.Z);
        }

        return to;
    }

    // Misfortune has six-yalm ground circles. Restrict escape to the intact main floor; boss Z identifies the three supported charge stages.
    private static BattleCharacter TherionActor() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(a => a.IsValid && !a.IsDead && a.BaseId == 0x27C1 && a.Distance() < 100);
    private static int TherionFloorStage()
    {
        if (WorldManager.ZoneId != 838 || Core.Me == null || !Core.Me.IsValid || Core.Me.IsDead || !Core.Me.InCombat || CommonBehaviors.IsLoading)
        {
            return -1;
        }

        var boss = TherionActor();
        if (boss == null)
        {
            return -1;
        }

        for (int stage = 0; stage < 3; stage++)
        {
            if (MathF.Abs(boss.Z - (-90 + 20 * stage)) < 1)
            {
                return stage;
            }
        }

        return -1;
    }

    private static bool MisfortuneActive() => TherionFloorStage() >= 0 && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsCasting && a.CastingSpellId == 15586);
    private void StopTherionMovement()
    {
        if (therionMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        therionMoving = false;
    }

    private void ReleaseTherionMovement()
    {
        StopTherionMovement();
        if (therionLeased)
        {
            CapabilityManager.Clear(therionHandle, CapabilityFlags.Movement, "Therion support follow ended");
        }

        therionLeased = false;
    }

    private void ReleaseTherion()
    {
        ReleaseTherionMovement();
        if (therionDisabledSideStep)
        {
            SidestepPlugin.Enabled = true;
        }

        therionDisabledSideStep = false;
        therionCast = 0;
        therionSheltering = false;
        therionUntil = DateTime.MinValue;
        observedTherionFloor = -1;
        therionRayActive = false;
        therionRayCastActor = 0;
    }

    // Final Sky is a 70-yalm blast blocked by Fallen Star rocks. Cosmic Kiss damages a ten-yalm footprint despite its 50-yalm raw range.
    private static readonly Vector3 FirstArena = new(-80, 748.233f, 82);
    // Retain ground impacts for 1.5 seconds after the cast bar to cover delayed damage.
    private readonly Dictionary<uint, (Vector3 Origin, DateTime Until)> firstBeastImpacts = new();
    private void UpdateFirstBeastImpacts()
    {
        var now = DateTime.UtcNow;
        foreach (var id in firstBeastImpacts.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
        {
            firstBeastImpacts.Remove(id);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsCasting && (a.BaseId == 0x29DC && a.CastingSpellId == 17108 || a.BaseId == 0x233C && a.CastingSpellId == 15562)))
        {
            var origin = actor.CastingSpellId == 17108 ? actor.Location : actor.SpellCastInfo.CastLocation;
            if (InsideArena(origin))
            {
                firstBeastImpacts[actor.ObjectId] = (origin, now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1500));
            }
        }
    }

    private readonly HashSet<uint> ownedOverrides = new();
    private static Amaurot activeHandler;
    /// <summary>Releases Amaurot's movement leases and owned overrides on plugin disable or hook replacement.</summary>
    public static void ResetActive() => activeHandler?.Cleanup();
    private uint shelterRock;
    private Vector3 shelterPoint;
    private DateTime shelterUntil;
    private bool moving;
    private bool leased;
    // Lock-on 57 marks meteor drops. Cosmic Kiss ends placement even if the marker remains visible.
    private static readonly Vector3[] MeteorDropPoints =
    {
        new(-64, 748.233f, 71),
        new(-96, 748.233f, 71),
        new(-80, 748.233f, 98),
        // Try inset edge alternatives when the primary drops are blocked; preserve twelve-yalm separation.
        new(-64, 748.233f, 84),
        new(-64, 748.233f, 96),
        new(-96, 748.233f, 86),
        new(-80, 748.233f, 66)
    };
    private readonly CapabilityManagerHandle meteorHandle = CapabilityManager.CreateNewHandle();
    private int meteorSpot = -1;
    private bool meteorMoving, meteorLeased;
    private static bool HasMeteorMarker(BattleCharacter actor) => actor != null && actor.IsValid && actor.VfxContainer.IsValid && actor.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 57);
    private bool HandleMeteorBait()
    {
        var otherTargets = PartyManager.VisibleMembers.Select(m => m.BattleCharacter).Where(a => a != null && a.ObjectId != Core.Me.ObjectId && HasMeteorMarker(a)).Select(a => a.Location).ToArray();
        int selected = ChooseMeteorSpot(otherTargets, meteorSpot, UnsafeShelterPoint);
        CapabilityManager.Update(meteorHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Place marked meteor away from party");
        meteorLeased = true;
        if (selected != meteorSpot)
        {
            meteorSpot = selected;
            Logger.Information(selected < 0 ? "[Amaurot] No unclaimed safe meteor placement; yielding to avoidance." : $"[Amaurot] Meteor placement destination={MeteorDropPoints[selected]}");
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            meteorMoving = false;
            return false;
        }

        if (selected < 0 || Core.Me.Location.Distance2D(MeteorDropPoints[selected]) <= .6f)
        {
            StopMeteorMovement();
            return false; // Holding placement must not suppress the routine's attacks/heals.
        }

        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(MeteorDropPoints[selected]) { DistanceTolerance = .4f, UseMount = false });
        meteorMoving = true;
        return true;
    }

    // Reserve nearby primary drops for marked allies. Keep twelve yalms from those drops and retain a safe assignment to avoid oscillation.
    private static int ChooseMeteorSpot(Vector3[] others, int current, Func<Vector3, bool> unsafePoint)
    {
        var reserved = others.Select(p => Enumerable.Range(0, 3).OrderBy(i => p.Distance2D(MeteorDropPoints[i])).First()).ToHashSet();
        bool Available(int i) => i >= 0 && i < MeteorDropPoints.Length && !unsafePoint(MeteorDropPoints[i]) && reserved.All(r => MeteorDropPoints[i].Distance2D(MeteorDropPoints[r]) >= 12) && others.All(p => p.Distance2D(MeteorDropPoints[i]) >= 12);
        if (Available(current))
        {
            return current;
        }

        return new[]
        {
            0,
            3,
            4,
            5,
            6,
            1,
            2
        }.Where(Available).DefaultIfEmpty(-1).First();
    }

    private void StopMeteorMovement()
    {
        if (meteorMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        meteorMoving = false;
    }

    private void ReleaseMeteorBait()
    {
        StopMeteorMovement();
        if (meteorLeased)
        {
            CapabilityManager.Clear(meteorHandle, CapabilityFlags.Movement, "Meteor placed or marker ended");
        }

        meteorLeased = false;
        meteorSpot = -1;
    }

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Suppress shelter, rock blast, spread and the friendly Skyshard limit break. Release only overrides acquired by this encounter.
        foreach (uint action in new uint[]
        {
            15563,
            17108,
            15560,
            17995,
            15571,
            15562
        }

        )
        {
            if (LlamaLibrary.Helpers.SideStep.Override(action))
            {
                ownedOverrides.Add(action);
            }
        }

        activeHandler = this;
        TreeRoot.OnStop += OnStopped;
        // Sprinter is a 40-by-8 line. Follow the current caster transform with half a yalm of clearance.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(canRun: () => WorldManager.ZoneId == 838 && !CommonBehaviors.IsLoading && Core.Me != null && Core.Me.IsValid && Core.Me.InCombat && !Core.Me.IsDead, objectSelector: a => a.IsValid && a.BaseId == 0x2879 && a.IsCasting && a.CastingSpellId == 15571, width: 9, length: 41, yOffset: -.5f, priority: AvoidancePriority.High);
        // Ground locations avoid the object-following safe-distance expansion and
        // retain each10y blast plus0.5y clearance through its delayed impact.
        AvoidanceManager.AddAvoidLocation(canRun: FirstArenaActive, leashPointProducer: () => FirstArena, leashRadius: 40f, radiusProducer: (Vector3 _) => 10.5f, locationProducer: p => p, collectionProducer: () => firstBeastImpacts.Values.Where(p => p.Until > DateTime.UtcNow).Select(p => p.Origin).ToArray(), objectValidator: null, ignoreIfBlocking: false);
        // Meteor Rain is a six-yalm target spread. CastLocation can be stale; avoid other targets and exclude the player.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: FirstArenaActive, objectSelector: a => a.IsValid && a.BaseId == 0x233C && a.IsCasting && a.CastingSpellId == 15560 && a.SpellCastInfo.TargetId != Core.Me.ObjectId && GameObjectManager.GetObjectByObjectId(a.SpellCastInfo.TargetId)is BattleCharacter target && target.IsValid && target.IsAlive, radiusProducer: _ => 6.5f, locationProducer: a => GameObjectManager.GetObjectByObjectId(a.SpellCastInfo.TargetId)?.Location ?? a.Location);
        // Native circle avoidance precedes support following. No independent manual
        // destination is issued for Misfortune;0.5y clearance covers arrival error.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => TherionFloorStage() >= 0, objectSelector: a => a.IsValid && a.IsCasting && a.CastingSpellId == 15586, radiusProducer: _ => 6.5f, locationProducer: a => a.SpellCastInfo.CastLocation);
        // Frozen scalar geometry survives the helper cast bar without retaining
        // native wrappers. RB owns beam/circle escape together; no second mover.
        AvoidanceManager.AddAvoidPolygon(condition: () => therionRayActive && TherionFloorStage() >= 0, leashPointProducer: () => Core.Me.Location, leashRadius: 40f, rotationProducer: origin => -therionRayHeading, scaleProducer: origin => 1f, heightProducer: origin => 15f, pointsProducer: origin => TherionRayPolygon, locationProducer: origin => origin, collectionProducer: () => new[] { therionRayOrigin }, priority: AvoidancePriority.High);
        for (int stage = 0; stage < 3; stage++)
        {
            int capturedStage = stage;
            float length = 60 - 20 * stage;
            var center = new Vector3(0, -720, -45 + 10 * stage);
            // Register per-stage rectangles because this helper captures dimensions.
            // Circle/beam escapes share intact floor bounds. The boss's next
            // shelter cue retires the beam before its side-alcove approach.
            AvoidanceHelpers.AddAvoidSquareDonut(() => (MisfortuneActive() || therionRayActive) && TherionFloorStage() == capturedStage, 19, length - 1, 150, 150, () => new[] { center }, AvoidancePriority.High);
        }

        // Square39y wide, inset0.5y. A circle would incorrectly clip valid corners.
        AvoidanceHelpers.AddAvoidSquareDonut(FirstArenaActive, 38, 38, 120, 120, () => new[] { FirstArena }, AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Cleanup();
        return Task.FromResult(false);
    }

    private static bool FirstArenaActive() => WorldManager.ZoneId == 838 && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Location.Distance2D(FirstArena) < 40;
    private void OnStopped(BotBase bot) => Cleanup();
    private void Cleanup()
    {
        firstBeastImpacts.Clear();
        ReleaseMeteorBait();
        ReleaseShelter();
        ReleaseTherion();
        TreeRoot.OnStop -= OnStopped;
        foreach (uint action in ownedOverrides)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        ownedOverrides.Clear();
        if (ReferenceEquals(activeHandler, this))
        {
            activeHandler = null;
        }
    }

    // Hold cover through the cast and 750 ms impact grace, but yield if a later meteor covers it.
    private bool HandleFirstBeast(BattleCharacter beast)
    {
        var now = DateTime.UtcNow;
        if (beast.IsCasting && beast.CastingSpellId == 15563)
        {
            shelterUntil = now + beast.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(750);
            var rocks = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && !a.IsDead && a.BaseId == 0x29DC).ToArray();
            var points = rocks.Select(a => ShelterPoint(beast.Location, a.Location)).ToArray();
            int selected = ChooseShelterIndex(Core.Me.Location, points, Array.FindIndex(rocks, a => a.ObjectId == shelterRock), UnsafeShelterPoint);
            if (selected >= 0)
            {
                if (shelterRock != rocks[selected].ObjectId || shelterPoint.Distance2D(points[selected]) > .25f)
                {
                    shelterRock = rocks[selected].ObjectId;
                    shelterPoint = points[selected];
                }
            }
            else if (shelterRock != 0)
            {
                shelterRock = 0;
                StopOwnedMovement();
                Logger.Information("[Amaurot] Final Sky has no currently safe rock shadow; yielding to native avoidance.");
            }
        }
        else if (now >= shelterUntil)
        {
            ReleaseShelter();
        }

        if (now >= shelterUntil)
        {
            return false;
        }

        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Final Sky rock shelter");
        leased = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // Avoidance replaced this command; old cleanup must not stop its successor.
            moving = false;
            return false;
        }

        if (shelterRock == 0 || UnsafeShelterPoint(shelterPoint))
        {
            StopOwnedMovement();
            return false;
        }

        if (Core.Me.Location.Distance2D(shelterPoint) <= 0.75f)
        {
            StopOwnedMovement();
            return false;
        }

        // Mesh navigation observes the same active avoids. Direct input toward cover
        // could cross a meteor even when the destination itself passes the checks.
        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(shelterPoint) { DistanceTolerance = .5f, UseMount = false });
        moving = true;
        return true;
    }

    // Keep a still-safe shadow instead of switching to a nearer rock every tick.
    // Return no destination when all cover is unsafe; never resume a rejected point.
    private static int ChooseShelterIndex(Vector3 player, Vector3[] points, int current, Func<Vector3, bool> unsafePoint)
    {
        if (current >= 0 && current < points.Length && InsideArena(points[current]) && !unsafePoint(points[current]))
        {
            return current;
        }

        return Enumerable.Range(0, points.Length).Where(i => InsideArena(points[i]) && !unsafePoint(points[i])).OrderBy(i => player.Distance2D(points[i])).DefaultIfEmpty(-1).First();
    }

    private static bool UnsafeShelterPoint(Vector3 point)
    {
        if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(point)))
        {
            return true;
        }

        // Native avoids are refreshed by another hook. Read these confirmed helper
        // casts too so newly placed6y meteors/10y falling stars reject cover before
        // the next avoid pulse;0.5y clearance matches the encounter safety margin.
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.BaseId == 0x233C && a.IsCasting && (a.CastingSpellId == 15558 || a.CastingSpellId == 15562) && point.Distance2D(a.SpellCastInfo.CastLocation) <= (a.CastingSpellId == 15558 ? 6.5f : 10.5f));
    }

    // Rock radius2.4y plus1y clearance, on the source-to-rock ray. A degenerate ray
    // cannot establish cover. Arena rejection prevents selecting an unreachable wall shadow.
    private static Vector3 ShelterPoint(Vector3 source, Vector3 rock)
    {
        float dx = rock.X - source.X, dz = rock.Z - source.Z;
        float length = MathF.Sqrt(dx * dx + dz * dz);
        return length < 1 ? new Vector3(float.NaN, rock.Y, float.NaN) : new Vector3(rock.X + 3.4f * dx / length, rock.Y, rock.Z + 3.4f * dz / length);
    }

    private static bool InsideArena(Vector3 p) => MathF.Abs(p.X + 80) <= 19 && MathF.Abs(p.Z - 82) <= 19;
    private void StopOwnedMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        moving = false;
    }

    private void ReleaseShelter()
    {
        StopOwnedMovement();
        if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Final Sky ended or interrupted");
        }

        leased = false;
        shelterRock = 0;
        shelterUntil = DateTime.MinValue;
    }
}
