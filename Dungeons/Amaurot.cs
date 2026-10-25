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
    private static readonly HashSet<uint> BossIds =
    [
        4385, 7864, 8925, // Brightsphere          :: 光明晶球
        8201,             // The First Beast       :: 第一之兽
        8210,             // Therion               :: 至大灾兽
    ];


    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Amaurot;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
    [
        532, 1837, 2794, 5445, 7931, 9076, 9338, 9490, 9493, 10256, 10257, 11573,
        11582, 12377, 12486, 12589, 12590, 12591, 12648, 12654, 12681, 12688, 12805,
        12809, 12823, 12824, 12825, 13251, 13336, 13337, 13344, 13345, 13346, 15561,
        15559, 15560, 15562, 15565, 15566, 15579, 15580, 15581, 15582, 15583, 15585,
        16785, 16786, 18157, 17996,
    ];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // The 2026-10-25 capture showed the old meteor coroutine walking after a wipe.
        // A tick-scoped shelter owner yields to RB's harmful-area avoidance.
        if (CommonBehaviors.IsLoading || Core.Me == null || !Core.Me.IsValid || Core.Me.IsDead || !Core.Me.InCombat || WorldManager.ZoneId != 838)
        {
            ReleaseShelter();
            ReleaseTherion();
            return false;
        }
        var beast = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && !a.IsDead && a.BaseId == 0x27B6 && a.Location.Distance2D(Core.Me.Location) < 65);
        if (beast != null)
        {
            ReleaseTherion();
            SidestepPlugin.Enabled = true;
            return HandleFirstBeast(beast);
        }
        ReleaseShelter();
        var therion = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && !a.IsDead && a.BaseId == 0x27C1 && a.Distance() < 100);
        if (therion != null)
        {
            // Preserve this encounter's existing SideStep exclusion: its beam and
            // collapsing-alcove semantics are owned by the local support-follow path.
            if (SidestepPlugin.Enabled) { SidestepPlugin.Enabled = false; therionDisabledSideStep = true; }
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

    // The 07:00UTC capture survived Apokalypsis in the left alcove(-12.205,-71.180),
    // then the mesh-follow diagonal toward(-9.167,-66.855) fell at(-10.287,-68.208).
    // This owner crosses the narrow alcove mouth laterally before changing Z. It
    // also stages entry on the main floor before stepping outward, using the same
    // observed main half-width10 and side-platform centers at X+/-12.
    private readonly CapabilityManagerHandle therionHandle = CapabilityManager.CreateNewHandle();
    private uint therionCast;
    private DateTime therionUntil;
    private bool therionMoving, therionLeased;
    // Restore SideStep only when this encounter changed it, preserving a pre-existing user disable.
    private bool therionDisabledSideStep;
    private int therionLeg;

    private bool HandleTherion(BattleCharacter boss)
    {
        uint cast = boss.IsCasting ? boss.CastingSpellId : 0;
        if (cast != therionCast && cast is 15575 or 15578)
        {
            // Retain the baseline ten/eight-second support-follow windows, now as
            // cooperative ticks rather than coroutines that starve the rotation.
            // Detailed follow-up impact/late-platform lifetimes remain a capture gap.
            therionUntil = DateTime.UtcNow.AddSeconds(cast == 15575 ? 10 : 8);
            Logger.Information($"[Amaurot] Therion support window action={cast} until={therionUntil:O}");
        }
        therionCast = cast;
        bool active = DateTime.UtcNow < therionUntil || SpellsToFollowDodge.IsCasting();
        var pos = Core.Me.Location;
        // Keep ownership while in an alcove even between casts: the combat routine
        // must not shortcut its mouth when it resumes ordinary range adjustment.
        if (!active && MathF.Abs(pos.X) <= 9) { ReleaseTherionMovement(); return false; }
        CapabilityManager.Update(therionHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Therion alcove-safe support follow");
        therionLeased = true;
        if (AvoidanceManager.IsRunningOutOfAvoid) { therionMoving = false; return false; }
        var ally = MovementHelpers.GetClosestAlly;
        if (ally == null || !ally.IsValid || ally.IsDead || ally.Distance() > 50 || MathF.Abs(ally.Y - pos.Y) > 1)
        { StopTherionMovement(); return false; }
        var destination = TherionWaypoint(pos, ally.Location);
        int leg = MathF.Abs(pos.X) > 9 && MathF.Abs(pos.Z - ally.Z) > .5f ? 1 :
            MathF.Abs(ally.X) > 9 && MathF.Abs(pos.Z - ally.Z) > .5f ? 2 : 3;
        if (leg != therionLeg)
        {
            therionLeg = leg;
            Logger.Information($"[Amaurot] Therion follow leg={leg} from={pos} via={destination} ally={ally.Location}");
        }
        if (pos.Distance2D(destination) <= .3f) { StopTherionMovement(); return false; }
        Navigator.PlayerMover.MoveTowards(destination);
        therionMoving = true;
        return true;
    }

    // X8 staging leaves2y inside the main floor. Same-Z lateral crossings stay
    // within the observed narrow alcove mouth; never diagonally clip the void.
    private static Vector3 TherionWaypoint(Vector3 from, Vector3 to)
    {
        if (MathF.Abs(from.X) > 9 && MathF.Abs(from.Z - to.Z) > .5f)
            return new Vector3(MathF.Sign(from.X) * 8, from.Y, from.Z);
        if (MathF.Abs(to.X) > 9 && MathF.Abs(from.Z - to.Z) > .5f)
            return new Vector3(MathF.Sign(to.X) * 8, from.Y, to.Z);
        return to;
    }

    // Global15586 and repeated07:10–17UTC hits confirm6y ground circles. The old
    // ally follow stopped3.8–5.8y from their centers. Native avoidance now owns this
    // geometric escape; the semantic owner yields without canceling its successor.
    // Only the inset main rectangle is eligible during these casts, so RB cannot
    // choose a missing alcove or cut across a gap. Boss Z identifies the three
    // observed charge stages(-90/-70/-50); the front edge is15y beyond the boss,
    // the fixed rear edge Z-15. Unrecognized phases disable this inference.
    private static BattleCharacter TherionActor() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && !a.IsDead && a.BaseId == 0x27C1 && a.Distance() < 100);

    private static int TherionFloorStage()
    {
        if (WorldManager.ZoneId != 838 || Core.Me == null || !Core.Me.IsValid ||
            Core.Me.IsDead || !Core.Me.InCombat || CommonBehaviors.IsLoading) return -1;
        var boss = TherionActor();
        if (boss == null) return -1;
        for (int stage = 0; stage < 3; stage++)
            if (MathF.Abs(boss.Z - (-90 + 20 * stage)) < 1) return stage;
        return -1;
    }

    private static bool MisfortuneActive() => TherionFloorStage() >= 0 &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(a => a.IsValid && a.IsCasting && a.CastingSpellId == 15586);

    private void StopTherionMovement()
    {
        if (therionMoving && !AvoidanceManager.IsRunningOutOfAvoid) Navigator.PlayerMover.MoveStop();
        therionMoving = false;
    }
    private void ReleaseTherionMovement()
    {
        StopTherionMovement();
        if (therionLeased) CapabilityManager.Clear(therionHandle, CapabilityFlags.Movement, "Therion support follow ended");
        therionLeased = false;
        therionLeg = 0;
    }
    private void ReleaseTherion()
    {
        ReleaseTherionMovement();
        if (therionDisabledSideStep) SidestepPlugin.Enabled = true;
        therionDisabledSideStep = false;
        therionCast = 0;
        therionUntil = DateTime.MinValue;
    }
    // Global 2026.10.16 action15563 is a70y circle blocked by FallenStar29DC.
    // Live06:07/06:09 captures put rocks at(-96,71)/(-80,98), before Final Sky.
    // Cosmic Kiss17108 has a raw50y range but a corroborated10y damaging footprint.
    private static readonly Vector3 FirstArena = new(-80, 748.233f, 82);
    private readonly HashSet<uint> ownedOverrides = new();
    private static Amaurot activeHandler;

    /// <summary>Releases only Amaurot's shelter and overrides on plugin disable or hook replacement.</summary>
    public static void ResetActive() => activeHandler?.Cleanup();

    private uint shelterRock;
    private Vector3 shelterPoint;
    private DateTime shelterUntil;
    private bool moving;
    private bool leased;

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Only suppress the two semantic/data exceptions; SideStep keeps ordinary hazards.
        // Remove only overrides acquired by this instance, including explicit bot stops.
        foreach (uint action in new uint[] {15563, 17108})
            if (LlamaLibrary.Helpers.SideStep.Override(action)) ownedOverrides.Add(action);
        activeHandler = this;
        TreeRoot.OnStop += OnStopped;
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: FirstArenaActive,
            objectSelector: a => a.IsValid && a.IsCasting && a.CastingSpellId == 17108,
            radiusProducer: _ => 10.5f, //10y blast plus0.5y clearance
            priority: AvoidancePriority.High));
        // Native circle avoidance precedes support following. No independent manual
        // destination is issued for Misfortune;0.5y clearance covers arrival error.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => TherionFloorStage() >= 0,
            objectSelector: a => a.IsValid && a.IsCasting && a.CastingSpellId == 15586,
            radiusProducer: _ => 6.5f,
            locationProducer: a => a.SpellCastInfo.CastLocation);
        for (int stage = 0; stage < 3; stage++)
        {
            int capturedStage = stage;
            float length = 60 - 20 * stage;
            var center = new Vector3(0, -720, -45 + 10 * stage);
            // Register per-stage rectangles because this helper captures dimensions.
            // Activate only during circle escapes: shelter movement needs side alcoves.
            AvoidanceHelpers.AddAvoidSquareDonut(
                () => MisfortuneActive() && TherionFloorStage() == capturedStage,
                19, length - 1, 150, 150, () => new[] { center }, AvoidancePriority.High);
        }
        // Square39y wide, inset0.5y. A circle would incorrectly clip valid corners.
        AvoidanceHelpers.AddAvoidSquareDonut(FirstArenaActive,38,38,120,120,()=>new[]{FirstArena},AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Cleanup();
        return Task.FromResult(false);
    }

    private static bool FirstArenaActive() => WorldManager.ZoneId == 838 && Core.Me.InCombat &&
        !Core.Me.IsDead && Core.Me.Location.Distance2D(FirstArena) < 40;

    private void OnStopped(BotBase bot) => Cleanup();

    private void Cleanup()
    {
        ReleaseShelter();
        ReleaseTherion();
        TreeRoot.OnStop -= OnStopped;
        foreach (uint action in ownedOverrides) LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        ownedOverrides.Clear();
        if (ReferenceEquals(activeHandler,this)) activeHandler=null;
    }

    // Pin a scalar destination to its rock/cast, with no cached frame wrappers. Keep it
    // through750ms impact grace; allow routine actions during the stationary hold.
    private bool HandleFirstBeast(BattleCharacter beast)
    {
        var now=DateTime.UtcNow;
        if (beast.IsCasting && beast.CastingSpellId == 15563)
        {
            shelterUntil=now+beast.SpellCastInfo.RemainingCastTime+TimeSpan.FromMilliseconds(750);
            var rocks=GameObjectManager.GetObjectsOfType<BattleCharacter>(true,false)
                .Where(a=>a.IsValid && !a.IsDead && a.BaseId==0x29DC).ToArray();
            if (shelterRock==0 || !rocks.Any(a=>a.ObjectId==shelterRock))
            {
                // Never clamp a shadow to the wall: it could leave the rock's line-of-sight cover.
                var candidates=rocks.Select(a=>new {Actor=a, Point=ShelterPoint(beast.Location,a.Location)})
                    .Where(a=>InsideArena(a.Point)).OrderBy(a=>Core.Me.Location.Distance2D(a.Point)).ToArray();
                if(candidates.Length>0)
                {
                    shelterRock=candidates[0].Actor.ObjectId;
                    shelterPoint=candidates[0].Point;
                    Logger.Information($"[Amaurot] Final Sky shelter rock=0x{shelterRock:X8} destination={shelterPoint}");
                }
            }
        }
        else if(now>=shelterUntil) ReleaseShelter();
        if(shelterRock==0) return false;
        CapabilityManager.Update(CapabilityHandle,CapabilityFlags.Movement,TimeSpan.FromSeconds(1),"Final Sky rock shelter");
        leased=true;
        if(AvoidanceManager.IsRunningOutOfAvoid)
        {
            // Avoidance replaced this command; old cleanup must not stop its successor.
            moving=false;
            return false;
        }
        if(Core.Me.Location.Distance2D(shelterPoint)<=0.75f)
        {
            StopOwnedMovement();
            return false;
        }
        Navigator.PlayerMover.MoveTowards(shelterPoint);
        moving=true;
        return true;
    }

    // Rock radius2.4y plus1y clearance, on the source-to-rock ray. A degenerate ray
    // cannot establish cover. Arena rejection prevents selecting an unreachable wall shadow.
    private static Vector3 ShelterPoint(Vector3 source,Vector3 rock)
    {
        float dx=rock.X-source.X,dz=rock.Z-source.Z;
        float length=MathF.Sqrt(dx*dx+dz*dz);
        return length<1 ? new Vector3(float.NaN,rock.Y,float.NaN) :
            new Vector3(rock.X+3.4f*dx/length,rock.Y,rock.Z+3.4f*dz/length);
    }
    private static bool InsideArena(Vector3 p) => MathF.Abs(p.X+80)<=19 && MathF.Abs(p.Z-82)<=19;
    private void StopOwnedMovement()
    {
        if(moving && !AvoidanceManager.IsRunningOutOfAvoid) Navigator.PlayerMover.MoveStop();
        moving=false;
    }
    private void ReleaseShelter()
    {
        StopOwnedMovement();
        if(leased) CapabilityManager.Clear(CapabilityHandle,CapabilityFlags.Movement,"Final Sky ended or interrupted");
        if(shelterRock!=0) Logger.Information("[Amaurot] Final Sky shelter released.");
        leased=false;
        shelterRock=0;
        shelterUntil=DateTime.MinValue;
    }}
