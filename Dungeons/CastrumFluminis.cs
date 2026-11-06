using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>Coordinates normal Tsukuyomi's trial lifecycle.</summary>
public sealed class CastrumFluminis : AbstractDungeon
{
    private static CastrumFluminis current;
    private bool ownsSideStep;
    private bool previousSideStep;
    private ChrysalisMovement movement;
    private bool unsupportedMoverReported;
    // Timed fan routes yield to stack, moon, halo and blade overlaps.
    private static readonly bool EnableExperimentalTimedMovement = true;
    private bool timedFanOwned;
    private bool fanSearchHoldAllowed;
    private enum PositionOwner { None, Moon, Fan, Stack }
    private PositionOwner positionOwner;
    // Plan conservatively to leave margin for movement and input latency.
    private const float TimedPlanningSpeed = 5f;
    // Planning at 5y/s reserves margin for the first input pulse. The captured
    // actor covered roughly 2.6y in 0.5s while running, so the live feasibility
    // check permits up to 6y/s instead of cancelling the route after one pulse.
    // This is an upper bound for deadline validation, not proof of arrival.
    private const float TimedExecutionSpeed = 6f;
    private const uint FanAction = 11245;
    private static readonly Vector3 Arena = new(100, 0, 100);
    private DateTime centerImpact;
    private DateTime sequenceUntil;
    private DateTime steelUntil;
    private DateTime nextFanCastInterrupt;
    private DateTime bladeWatchUntil;
    private Vector3 steelOrigin;
    private float steelHeading;
    private Vector3? stagePoint;
    private Vector3 firstEdge;
    private readonly List<(uint Id, Vector3 Location)> earlyFans = new();
    private readonly List<(uint Id, Vector3 Location, DateTime Until)> unresolvedFans = new();
    private readonly List<Vector3> possibleEarlyFans = new();
    private Task<(string Detail, IReadOnlyList<CastrumFluminisTimedPlanner.Step> Route,
        CastrumFluminisTimedGeometry.Circle[] Hazards, CastrumFluminisTimedGeometry.Cone[] Cones)> fanShadowTask;
    private CastrumFluminisTimedGeometry.Circle[] fanShadowHazards;
    private CastrumFluminisTimedGeometry.Cone[] fanShadowCones;
    private readonly CastrumFluminisPlanLease fanShadowLease = new();
    private long fanShadowToken;
    private CastrumFluminisPlanLease.Progress? fanShadowProgress;
    private bool? fanShadowRemainingValid;
    private int fanShadowObserved = -1;
    private bool fanShadowSteel;
    private bool fanShadowStack;
    private bool fanShadowMoon;
    private bool fanShadowHalo, fanShadowBlade;
    private DateTime fanShadowHaloUntil, fanShadowBladeUntil;
    private string fanShadowHaloSignature = "";
    private string fanShadowBladeSignature = "";
    private readonly Dictionary<uint, DateTime> fanShadowHaloActors = new();
    private readonly Dictionary<uint, DateTime> fanShadowBladeActors = new();
    private int fanShadowMoonDirection;
    private uint fanShadowStackAction, fanShadowStackTarget;
    private Vector3? fanShadowMarked;
    private (uint Id, System.Numerics.Vector2 Position)[] fanShadowParty = Array.Empty<(uint, System.Numerics.Vector2)>();
    private float fanShadowSteelHeading;
    private Vector3 fanShadowSteelOrigin;
    private DateTime fanShadowSnapshotAt;
    private DateTime nextFanShadowAt;
    private Vector3 fanShadowStart;
    private bool sequenceOwned;
    private bool previousStage;
    private bool stageIsFallback;
    private bool moonWatchActive;
    private readonly CapabilityManagerHandle moonHandle = CapabilityManager.CreateNewHandle();
    private bool moonOwned;
    private uint moonStatusId;
    private Vector3? moonPoint;
    private uint stackTarget, stackAction, stackBossId;
    private DateTime stackUntil;
    private Vector3? stackPoint;

    /// <inheritdoc/>
    public override ZoneId ZoneId => ZoneId.CastrumFluminis;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        // The 2026-11-05 capture showed the center fan hitting at (100,100),
        // followed by eight edge fans in sequence. Publish only the earliest
        // resolving cast: marking every future circle simultaneously can erase
        // the safe center before the first impact. Radius includes 0.5y margin.
        AvoidanceManager.AddAvoidLocation<BattleCharacter>(
            () => current == this && !sequenceOwned && DutyManager.InInstance && WorldManager.ZoneId == 778,
            () => Vector3.Zero, 60f, _ => 10.5f, fan => fan.Location,
            () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(fan => fan.IsValid && fan.BaseId == 0x2241 && fan.IsCasting &&
                    fan.CastingSpellId == FanAction && fan.SpellCastInfo != null)
                .OrderBy(fan => fan.SpellCastInfo.RemainingCastTime).Take(1));
        // November 5 synchronized capture: both Moonlights cast 11379 from
        // opposite points; the player's two-stack gain named the nearer Moonlight.
        // Their 2-15y rings leave separate center pockets. The half-yalm inset and
        // outer margin keep the selected escape clear of either damaging edge.
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            () => current == this && !sequenceOwned && DutyManager.InInstance && WorldManager.ZoneId == 778,
            a => a.IsValid && a.BaseId == 0x2278 && a.IsCasting && a.CastingSpellId == 11379,
            outerRadius: 15.5, innerRadius: 1.5, priority: ff14bot.Pathing.Avoidance.AvoidancePriority.High);
        // November 6: at three 1539 stacks native Halo escape selected the
        // eastern hole, after which the same status reached five and Doom.
        // Close only the hole on the accumulating side while Halo is active,
        // so native pathing can choose the opposite hole without a competing
        // manual mover. The 2y circle overlaps the inset 1.5y donut boundary.
        AvoidanceManager.AddAvoidLocation<BattleCharacter>(
            () => current == this && !sequenceOwned && DutyManager.InInstance && WorldManager.ZoneId == 778,
            () => Arena, 60f, _ => 2f, moon => moon.Location,
            () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(moon => moon.IsValid && moon.BaseId == 0x2278 && moon.IsCasting &&
                    moon.CastingSpellId == 11379 && IsWrongMoonPocket(moon.Location)));
        // The same visit gained vulnerability twice from standalone Bright Blade.
        // Both mirrored casts are 210-degree front sectors; retain a small angular
        // margin and let the existing movement owner clean up native escape input.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            () => current == this && !sequenceOwned && DutyManager.InInstance && WorldManager.ZoneId == 778,
            a => a.IsValid && a.BaseId == 0x2210 && a.IsCasting && a.CastingSpellId is 11257 or 11258,
            () => Arena, 60f, 0f, 44f, 216f);
        // Steel can also overlap Lunar Halo without a fan sequence. The
        // November 6 captures showed no Steel avoid in that situation; a
        // 96-degree cone adds a small heading margin to its 90-degree front
        // attack. The fan owner retains its explicit Steel arbitration.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            () => current == this && !sequenceOwned && DutyManager.InInstance && WorldManager.ZoneId == 778,
            a => a.IsValid && a.BaseId == 0x2210 && a.IsCasting && a.CastingSpellId == 11239,
            () => Arena, 60f, 0f, 70f, 96f);
        CheckLifecycle();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Party checks can arrive before combat; leaving the tick available preserves healing.
        await UsefulTasks.HandleReadyCheck();
        CheckLifecycle();
        return false;
    }

    /// <summary>Maintains the trial's scoped SideStep state even when a profile yields the hook.</summary>
    public static void CheckLifecycle()
    {
        if (current == null) return;
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != 778 || !DutyManager.InInstance)
        {
            current.ReleaseMovement();
            current.RestoreSideStep();
            return;
        }

        // The trial is entirely a boss encounter, including its untargetable add phase.
        // Keep generic SideStep geometry out of the encounter's eventual ordered movement owner.
        // SideStep is optional. Its absence must not prevent the encounter
        // controller, death cleanup, or mechanic updates from running.
        if (SidestepPlugin != null)
        {
            if (!current.ownsSideStep)
            {
                current.previousSideStep = SidestepPlugin.Enabled;
                current.ownsSideStep = true;
            }
            if (SidestepPlugin.Enabled) SidestepPlugin.Enabled = false;
        }
        // A death or loading transition ends our movement lease, but remains inside
        // the boss encounter. Restoring SideStep there let it publish competing
        // avoids while the player was dead in the November 5 synchronized visit.
        if (Core.Me == null || !Core.Me.IsAlive || CommonBehaviors.IsLoading)
        {
            current.ReleaseMovement();
            return;
        }
        current.UpdateMovement();
        current.UpdateMoonSide();
        current.UpdateFanSequence();
        current.UpdateStack();
        // Apply only the final request after every mechanic has refreshed its
        // geometry and ownership. Acting earlier can briefly resume an expired
        // stack or fan destination before the later handler invalidates it.
        current.movement?.Update();
        // Several native blade escapes ended near the cone boundary before a
        // boss-sourced vulnerability gain. Capture the boss heading through the
        // cast and its delayed effect, rather than assuming the cast-start facing
        // is still authoritative when the hit resolves.
        var blade = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.BaseId == 0x2210 && a.IsCasting &&
                a.SpellCastInfo != null && a.CastingSpellId is 11257 or 11258);
        if (blade != null)
            current.bladeWatchUntil = DateTime.UtcNow + blade.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
        if (DateTime.UtcNow < current.bladeWatchUntil)
            LoggingHelpers.LogActorSignalChanges("CastrumFluminis.Blade", a => a.BaseId == 0x2210);
        else
            LoggingHelpers.ClearActorSignalWatch("CastrumFluminis.Blade");
        // The generic capture records vulnerability but not the opposing moon
        // stacks. Keep a bounded actor watch only while either status is present
        // so the next run can correlate side, stacks and overlapping party moves.
        if (Core.Player.Auras != null && Core.Player.Auras.IsValid)
        {
            bool moonStatus = Core.Player.Auras.AuraList.Any(a => a.Id is 1538 or 1539);
            if (moonStatus || current.moonWatchActive)
                LoggingHelpers.LogActorSignalChanges("CastrumFluminis.Moon", a => a.ObjectId == Core.Player.ObjectId);
            // Capture the first status-free sample as well: disappearance is the
            // evidence that a side change actually cleared the accumulated stacks.
            if (!moonStatus && current.moonWatchActive)
                LoggingHelpers.ClearActorSignalWatch("CastrumFluminis.Moon");
            current.moonWatchActive = moonStatus;
        }
    }

    /// <summary>Releases the borrowed plugin state on stop, disable or trial exit.</summary>
    public static void ResetActive()
    {
        current?.ReleaseMovement();
        current?.RestoreSideStep();
        LoggingHelpers.ClearActorSignalWatch("CastrumFluminis.Moon");
        LoggingHelpers.ClearActorSignalWatch("CastrumFluminis.Blade");
        current = null;
    }

    private void UpdateMovement()
    {
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
            positionOwner = PositionOwner.None;
        }
        if (movement == null)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
            {
                if (!unsupportedMoverReported)
                {
                    unsupportedMoverReported = true;
                    Logger.Warning("[Castrum Fluminis] Native escape cleanup unavailable: another mover owns input.");
                }
                return;
            }
            // A temporary owner must not disable this encounter for the rest of
            // the pull. Retry acquisition after it releases, logging only once.
            unsupportedMoverReported = false;
            // Reuse the existing input owner so an ended native escape cannot leave
            // forward held or cancel a newer combat request. It owns no safe-point plan.
            movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: IsOrdinaryWaypointSafe);
            Navigator.PlayerMover = movement;
        }
    }

    private static bool IsOrdinaryWaypointSafe(Vector3 waypoint)
    {
        // Check the actual waypoint against registered active hazards; safe endpoints
        // alone cannot stop combat travel from crossing back through a live fan.
        Vector3 from = Core.Me.Location;
        float distance = from.Distance2D(waypoint);
        int samples = System.Math.Max(1, (int)System.Math.Ceiling(distance / 0.5f));
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            var point = new Vector3(from.X + (waypoint.X - from.X) * t, from.Y,
                from.Z + (waypoint.Z - from.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)))
                return false;
        }
        return true;
    }

    private static bool IsWrongMoonPocket(Vector3 location)
    {
        var auras = Core.Player?.Auras;
        if (auras == null || !auras.IsValid) return false;
        int lit = auras.GetAuraStacksById(1538);
        int shadow = auras.GetAuraStacksById(1539);
        if (lit >= 3 && shadow < 0) return location.X < Arena.X - 1f;
        if (shadow >= 3 && lit < 0) return location.X > Arena.X + 1f;
        return false;
    }

    private void ReleaseMovement()
    {
        ReleaseMoonSide();
        ReleaseFanSequence();
        ReleaseStack();
        movement?.Detach();
        movement = null;
        positionOwner = PositionOwner.None;
        unsupportedMoverReported = false;
    }

    // All encounter positioning shares one borrowed mover. A mechanic may
    // retire only its own request after a later mechanic has taken over.
    private void RequestPosition(PositionOwner owner, Vector3? point, float tolerance)
    {
        if (movement?.IsCurrent != true) return;
        positionOwner = owner;
        movement.Request(point, tolerance);
    }

    private void ClearPosition(PositionOwner owner)
    {
        if (positionOwner != owner) return;
        positionOwner = PositionOwner.None;
        movement?.Request(null, 0);
    }

    private void UpdateMoonSide()
    {
        if (movement?.IsCurrent != true || Core.Player.Auras == null || !Core.Player.Auras.IsValid || !Core.Me.InCombat)
        {
            ReleaseMoonSide();
            return;
        }

        var auras = Core.Player.Auras;
        bool lit = auras.AuraList.Any(a => a.Id == 1538);
        bool shadow = auras.AuraList.Any(a => a.Id == 1539);
        if (lit == shadow)
        {
            ReleaseMoonSide();
            return;
        }
        uint id = lit ? 1538u : 1539u;
        int stacks = auras.GetAuraStacksById(id);
        if (stacks < 3 || moonOwned && moonStatusId != id)
        {
            ReleaseMoonSide();
            return;
        }

        // Fan and stack requests are submitted after this method on the same
        // owner. Once either is active, retire only our lease and let that
        // mechanic preserve or clear its own destination.
        if (sequenceOwned || stackTarget != 0 || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseMoonSide();
            return;
        }

        // November 6: 1538 rose to five and Doom while x=99.244; 1539 rose
        // on the other side. Begin at three stacks to leave a response window.
        // The 2y inset crosses the observed x=100 division with margin; the
        // precise boundary and simultaneous hazard behavior remain live tests.
        float destinationX = id == 1538 ? Arena.X + 2f : Arena.X - 2f;
        if (!moonPoint.HasValue || !IsOrdinaryWaypointSafe(moonPoint.Value))
        {
            var from = Core.Me.Location;
            moonPoint = new float[] { 0, -2, 2, -4, 4, -6, 6 }
                .Select(offset => new Vector3(destinationX, from.Y, from.Z + offset))
                .Where(p => p.Distance2D(Arena) < 18f && IsOrdinaryWaypointSafe(p))
                .OrderBy(p => p.Distance2D(from)).Select(p => (Vector3?)p).FirstOrDefault();
            if (!moonPoint.HasValue)
            {
                ReleaseMoonSide();
                return;
            }
            Logger.Information("[Castrum Fluminis] Moon side status={0} stacks={1} destination={2}", id, stacks, moonPoint.Value);
        }

        moonOwned = true;
        moonStatusId = id;
        CapabilityManager.Update(moonHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Tsukuyomi moon side");
        RequestPosition(PositionOwner.Moon, moonPoint, 0.5f);
    }

    private void ReleaseMoonSide()
    {
        if (moonOwned)
        {
            ClearPosition(PositionOwner.Moon);
            CapabilityManager.Clear(moonHandle, CapabilityFlags.Movement, "Moon side ended");
        }
        moonOwned = false;
        moonStatusId = 0;
        moonPoint = null;
    }

    private void UpdateStack()
    {
        if (movement?.IsCurrent != true) return;
        DateTime now = DateTime.UtcNow;
        var bosses = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.BaseId == 0x2210).ToArray();
        var castingBoss = bosses.FirstOrDefault(a => a.IsCasting &&
                a.SpellCastInfo != null && a.CastingSpellId is 11238 or 11259);
        if (castingBoss != null)
        {
            uint target = castingBoss.SpellCastInfo.TargetId;
            if (target != stackTarget || castingBoss.CastingSpellId != stackAction ||
                castingBoss.ObjectId != stackBossId)
            {
                // Retire the previous stack request before discarding its
                // bookkeeping. Otherwise a missing new target can leave the
                // old movement request alive even though no point is tracked.
                ClearStackPosition();
            }
            stackTarget = target;
            stackAction = castingBoss.CastingSpellId;
            stackBossId = castingBoss.ObjectId;
            // Lunacy repeats after its initial cast; retain the request through
            // the reference multi-hit window. Lead's captured death followed its
            // cast by several seconds, so do not release on the cast-bar edge.
            stackUntil = now + castingBoss.SpellCastInfo.RemainingCastTime +
                TimeSpan.FromSeconds(stackAction == 11259 ? 5 : 3);
        }
        if (stackTarget == 0 || now >= stackUntil || !Core.Me.InCombat)
        {
            ReleaseStack();
            return;
        }
        // First resolve the central fan. Once it has resolved, admit a stack
        // destination only when it also satisfies the currently owned fan stage.
        if (sequenceOwned && now < centerImpact || AvoidanceManager.IsRunningOutOfAvoid ||
            MovementManager.GenerallyOutOfControl) return;

        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a != null && a.IsValid && a.IsAlive && a.IsVisible &&
                (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var marked = party.FirstOrDefault(a => a.ObjectId == stackTarget);
        if (marked == null) { ClearStackPosition(); return; }
        // The response outlives the cast. Substituting arena center when the
        // cast bar disappears silently changes the line-stack geometry.
        var boss = castingBoss ?? bosses.FirstOrDefault(a => a.ObjectId == stackBossId);
        if (stackAction == 11238 && boss == null) { ClearStackPosition(); return; }
        Vector3 origin = boss != null ? boss.Location : Arena;
        bool self = marked.ObjectId == Core.Me.ObjectId;
        var others = party.Where(a => a.ObjectId != Core.Me.ObjectId).ToArray();
        if (self && others.Length == 0) { ClearStackPosition(); return; }

        bool Safe(Vector3 p) => p.Distance2D(Arena) < 19 && IsOrdinaryWaypointSafe(p) &&
            (!sequenceOwned || p.Distance2D(Arena) <= 8.5f && p.Distance2D(firstEdge) >= 10.5f &&
                unresolvedFans.Where(f => now < f.Until).All(f =>
                    p.Distance2D(f.Location) >= 10.5f && CircleExitSegmentSafe(Core.Me.Location, p, f.Location, 10.5f)) &&
                (now >= steelUntil || !InSteel(p) && SteelSegmentSafe(Core.Me.Location, p)));
        int Coverage(Vector3 p)
        {
            if (stackAction == 11259)
                return self ? others.Count(a => a.Location.Distance2D(p) <= 5f) :
                    p.Distance2D(marked.Location) <= 5f ? 1 : 0;
            Vector3 end = self ? p : marked.Location;
            float dx = end.X - origin.X, dz = end.Z - origin.Z;
            float length2 = dx * dx + dz * dz;
            if (length2 < 1) return 0;
            bool InLine(Vector3 member)
            {
                float x = member.X - origin.X, z = member.Z - origin.Z;
                float t = (x * dx + z * dz) / length2;
                float cross = Math.Abs(x * dz - z * dx) / (float)Math.Sqrt(length2);
                return t >= 0 && t <= 1.05f && cross <= 3.5f;
            }
            return self ? others.Count(a => InLine(a.Location)) : InLine(p) ? 1 : 0;
        }

        var candidates = new List<Vector3> { Core.Me.Location };
        if (self)
        {
            foreach (var member in others)
            {
                var p = member.Location;
                float dx = p.X - origin.X, dz = p.Z - origin.Z;
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                candidates.Add(len > 0.1f && stackAction == 11238
                    ? new Vector3(p.X + 2 * dx / len, Core.Me.Y, p.Z + 2 * dz / len) : p);
            }
        }
        else if (stackAction == 11238)
        {
            for (int i = 1; i <= 4; i++)
            {
                float t = i / 5f;
                candidates.Add(new Vector3(origin.X + (marked.X - origin.X) * t, Core.Me.Y,
                    origin.Z + (marked.Z - origin.Z) * t));
            }
        }
        else candidates.Add(marked.Location);
        var best = candidates.Where(p => Coverage(p) > 0 && Safe(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (!best.HasValue) { ClearStackPosition(); return; }
        if (!stackPoint.HasValue || Coverage(stackPoint.Value) < Coverage(best.Value) || !Safe(stackPoint.Value))
        {
            stackPoint = best;
            Logger.Information("[Castrum Fluminis] Stack action={0} target={1:X} self={2} destination={3}", stackAction, stackTarget, self, best.Value);
        }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Tsukuyomi stack");
        RequestPosition(PositionOwner.Stack, stackPoint, 0.5f);
    }

    private void ReleaseStack()
    {
        if (stackTarget != 0)
        {
            ClearPosition(PositionOwner.Stack);
            if (positionOwner != PositionOwner.Fan)
                CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Stack ended");
        }
        stackTarget = stackAction = 0;
        stackBossId = 0;
        stackUntil = default;
        stackPoint = null;
    }

    private void ClearStackPosition()
    {
        if (stackPoint.HasValue)
        {
            ClearPosition(PositionOwner.Stack);
            if (positionOwner != PositionOwner.Fan)
                CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Stack destination unavailable");
        }
        stackPoint = null;
    }

    private void UpdateFanSequence()
    {
        if (movement == null || !movement.IsCurrent) return;
        DateTime now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsCasting && a.SpellCastInfo != null).ToArray();
        var fans = actors.Where(a => a.BaseId == 0x2241 && a.CastingSpellId == FanAction)
            .OrderBy(a => a.SpellCastInfo.RemainingCastTime).ToArray();
        var center = fans.FirstOrDefault(a => a.Location.Distance2D(Arena) < 1f);
        var edge = fans.FirstOrDefault(a => a.Location.Distance2D(Arena) > 12f);
        if (centerImpact == default && center != null && edge != null)
        {
            // Two November 5 traces put the central impact about 0.9s after the
            // displayed 4.7s cast. Stage opposite the first edge, which fires
            // half a second later, then return inward once the center resolves.
            centerImpact = now + center.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1100);
            firstEdge = edge.Location;
            // Captures show the outer fans on eight 16y positions, advancing one
            // position at a time. At the first cast the direction is not yet
            // known, so stage clear of the first two possible successors in
            // either direction. Waiting for their casts allowed the player to
            // enter the third circle before a safe redirect was available.
            float x = firstEdge.X - Arena.X, z = firstEdge.Z - Arena.Z;
            float length = (float)Math.Sqrt(x * x + z * z);
            if (length >= 15.5f && length <= 16.5f)
                for (int step = -2; step <= 2; step++)
                {
                    double angle = step * Math.PI / 4;
                    possibleEarlyFans.Add(new Vector3(Arena.X + x * (float)Math.Cos(angle) - z * (float)Math.Sin(angle),
                        firstEdge.Y, Arena.Z + x * (float)Math.Sin(angle) + z * (float)Math.Cos(angle)));
                }
            sequenceUntil = centerImpact.AddSeconds(6);
            sequenceOwned = true;
            Logger.Information("[Castrum Fluminis] Fan staging first={0} centerImpact={1:O}", firstEdge, centerImpact);
        }
        if (!sequenceOwned) return;
        // In the November 6 capture, a Lunacy destination passed the first-fan
        // check but moved into the still-unresolved eighth circle. Keep every
        // observed edge until its estimated impact, including the measured cast
        // to effect delay, and require both the stack endpoint and approach to
        // clear each remaining circle. If none is safe, retain fan priority.
        foreach (var fan in fans.Where(a => a.Location.Distance2D(Arena) > 12f))
            if (unresolvedFans.All(f => f.Id != fan.ObjectId))
                unresolvedFans.Add((fan.ObjectId, fan.Location,
                    now + fan.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1100)));
        // The November 6 synchronized capture staged clear of the first edge,
        // but the next two circles hit while the player returned to center.
        // Retain the first three cast owners as they appear: all three must be
        // clear of the staging point, including arrival tolerance, before the
        // center-to-edge handoff. Missing later casts cannot be inferred here.
        foreach (var fan in fans.Where(a => a.Location.Distance2D(Arena) > 12f))
        {
            if (earlyFans.Count == 3) break;
            if (earlyFans.All(f => f.Id != fan.ObjectId))
                earlyFans.Add((fan.ObjectId, fan.Location));
        }
        // An observed cast outside the predicted early positions invalidates
        // the pattern assumption; retain observed actors as the fallback.
        if (possibleEarlyFans.Count != 0 && earlyFans.Any(f =>
            possibleEarlyFans.All(p => p.Distance2D(f.Location) > 1f)))
            possibleEarlyFans.Clear();
        if (now >= sequenceUntil || !Core.Me.InCombat)
        {
            ReleaseFanSequence();
            return;
        }

        var steel = actors.FirstOrDefault(a => a.BaseId == 0x2210 && a.CastingSpellId == 11239);
        if (steel != null)
        {
            steelOrigin = steel.Location;
            steelHeading = steel.Heading;
            steelUntil = now + steel.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1000);
        }
        UpdateFanShadow(now);
        if (TryUseTimedFanRoute(now))
        {
            timedFanOwned = true;
            return;
        }
        if (timedFanOwned)
        {
            // The old timed waypoint is no longer authorized. Only clear the
            // fan's request; stack or native successors must keep their input.
            ClearPosition(PositionOwner.Fan);
            timedFanOwned = false;
        }
        bool stage = now < centerImpact;
        // Keep a valid destination stable; recomputing from every player sample
        // changes the scoring origin and can turn a straight run into stutter.
        Vector3? next = stagePoint.HasValue && previousStage == stage &&
            (now >= steelUntil || !InSteel(stagePoint.Value)) &&
            (!stage || (stageIsFallback
                ? earlyFans.Take(1).All(f => stagePoint.Value.Distance2D(f.Location) >= 11.5f)
                : earlyFans.All(f => stagePoint.Value.Distance2D(f.Location) >= 11.5f) &&
                    possibleEarlyFans.All(p => stagePoint.Value.Distance2D(p) >= 11.5f)))
            ? stagePoint : null;
        if (!next.HasValue)
        {
            stageIsFallback = false;
            next = FindFanPoint(stage, now < steelUntil, false);
            if (!next.HasValue && stage && earlyFans.Count > 0)
            {
                // November 6: holding for a forecast-safe point left the player
                // inside the first outer circle until it hit. If the full forecast
                // is unreachable, escape the known first circle without crossing
                // back through the center. Later circles remain a live risk and
                // the ordered center handoff must still run.
                next = FindFanPoint(true, now < steelUntil, true);
                stageIsFallback = next.HasValue;
                if (stageIsFallback)
                    Logger.Information("[Castrum Fluminis] Fan staging uses first-circle fallback; later circles remain unverified.");
            }
        }
        if (!next.HasValue)
        {
            // Unknown combined geometry must not keep issuing a stale destination
            // or allow combat travel through the first unresolved hazard. Retry
            // selection on subsequent pulses while healing stays schedulable.
            RequestPosition(PositionOwner.Fan, null, 0);
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Fan route unavailable");
            stagePoint = null;
            return;
        }
        if (!stagePoint.HasValue || stagePoint.Value.Distance2D(next.Value) > 0.5f)
        {
            stagePoint = next;
            Logger.Information("[Castrum Fluminis] Fan {0} destination={1} steel={2}", stage ? "stage" : "center", next.Value, now < steelUntil);
        }
        previousStage = stage;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Tsukuyomi ordered fans");
        // In the November 6 capture Physick began immediately before the center
        // handoff; the player stayed at the stage for about a second and two
        // outer fans hit during the return. Interrupt only while a known outer
        // circle is imminent and the owned escape still requires movement. A
        // short throttle avoids repeatedly cancelling unrelated healing pulses.
        if (!stage && Core.Me.IsCasting && !MovementManager.ForceMoved && !MovementManager.IsOccupied &&
            Core.Me.Location.Distance2D(stagePoint.Value) > 0.7f && now >= nextFanCastInterrupt &&
            unresolvedFans.Any(f => now < f.Until && (f.Until - now).TotalSeconds <= 2 &&
                Core.Me.Location.Distance2D(f.Location) < 10.5f))
        {
            nextFanCastInterrupt = now.AddMilliseconds(250);
            ActionManager.StopCasting();
        }
        // Once the center resolves, a validated shared stack point may replace
        // this goal through the same owner; never issue both on every pulse.
        if (now < centerImpact || stackPoint == null || now >= stackUntil)
        {
            RequestPosition(PositionOwner.Fan, stagePoint, 0.7f);
        }
    }

    // Timed waypoints share the encounter movement owner. Drift, preemption,
    // expiry or an unsafe remaining route returns control to the fan handler.
    private bool TryUseTimedFanRoute(DateTime now)
    {
        if (!EnableExperimentalTimedMovement || movement?.IsCurrent != true ||
            MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid) return false;
        // Stop the previous semantic request while a short early search is in
        // flight, so its captured starting point is not immediately obsolete.
        // Never spend this time holding if the center impact is already near or
        // another mechanic requires its own response.
        if (fanShadowTask != null && (now - fanShadowSnapshotAt).TotalMilliseconds < 250 &&
            (centerImpact - now).TotalSeconds > 3.1 && fanSearchHoldAllowed)
        {
            RequestPosition(PositionOwner.Fan, null, 0);
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500),
                "Tsukuyomi timed fan search");
            return true;
        }
        if (fanShadowHazards == null) return false;
        double elapsed = (now - fanShadowSnapshotAt).TotalSeconds;
        var actual = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
        var progress = fanShadowLease.Assess(fanShadowToken, now, elapsed, actual, 0.3f);
        if (progress == CastrumFluminisPlanLease.Progress.Waiting)
        {
            RequestPosition(PositionOwner.Fan, null, 0);
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500),
                "Tsukuyomi timed fan wait");
            return true;
        }
        if (!fanShadowLease.TryNextValidatedWaypoint(fanShadowToken, now, elapsed, actual, 0.3f,
            new System.Numerics.Vector2(Arena.X, Arena.Z), 18.5f, TimedExecutionSpeed, fanShadowHazards, fanShadowCones,
            null, null, out var waypoint)) return false;
        var destination = new Vector3(waypoint.Position.X, Core.Me.Y, waypoint.Position.Y);
        RequestPosition(PositionOwner.Fan, destination, 0.1f);
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500),
            "Tsukuyomi timed fan route");
        return true;
    }

    // Workers receive snapshots only; a route is usable after its constraints and age are rechecked.
    private void UpdateFanShadow(DateTime now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled && !EnableExperimentalTimedMovement) return;
        var activeActors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsCasting && a.SpellCastInfo != null).ToArray();
        foreach (var actor in activeActors)
        {
            if (actor.BaseId == 0x2278 && actor.CastingSpellId == 11379)
            {
                var until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
                if (until > fanShadowHaloUntil) fanShadowHaloUntil = until;
                if (!fanShadowHaloActors.TryGetValue(actor.ObjectId, out var previousUntil) || until > previousUntil)
                    fanShadowHaloActors[actor.ObjectId] = until;
            }
            if (actor.BaseId == 0x2210 && actor.CastingSpellId is 11257 or 11258)
            {
                var until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
                if (until > fanShadowBladeUntil) fanShadowBladeUntil = until;
                if (!fanShadowBladeActors.TryGetValue(actor.ObjectId, out var previousUntil) || until > previousUntil)
                    fanShadowBladeActors[actor.ObjectId] = until;
            }
        }
        // Retain an explicit post-cast uncertainty window. This conservative
        // diagnostic guard is not a calibrated server damage-snapshot bound.
        bool haloOverlap = now < fanShadowHaloUntil;
        bool bladeOverlap = now < fanShadowBladeUntil;
        var haloActors = activeActors.Where(a => a.BaseId == 0x2278 && a.CastingSpellId == 11379)
            .OrderBy(a => a.ObjectId).ToArray();
        foreach (var id in fanShadowHaloActors.Where(pair => now >= pair.Value).Select(pair => pair.Key).ToArray())
            fanShadowHaloActors.Remove(id);
        // Once a seen actor disappears, its provisional ring remains unmodeled
        // until its retained window expires; a remaining actor is not enough.
        bool haloGeometryAvailable = !haloOverlap || haloActors.Length > 0 &&
            fanShadowHaloActors.Keys.All(id => haloActors.Any(a => a.ObjectId == id));
        string haloSignature = string.Join(";", haloActors.Select(a => $"{a.ObjectId:X}:{a.X:F2},{a.Z:F2}"));
        var haloProbeShapes = haloActors.Select(a => new CastrumFluminisTimedGeometry.Donut(
            new System.Numerics.Vector2(a.X, a.Z), 1.5f, 15.5f, 0,
            a.SpellCastInfo.RemainingCastTime.TotalSeconds + 2)).ToArray();
        var bladeActors = activeActors.Where(a => a.BaseId == 0x2210 && a.CastingSpellId is 11257 or 11258)
            .OrderBy(a => a.ObjectId).ToArray();
        foreach (var id in fanShadowBladeActors.Where(pair => now >= pair.Value).Select(pair => pair.Key).ToArray())
            fanShadowBladeActors.Remove(id);
        bool bladeGeometryAvailable = !bladeOverlap || bladeActors.Length == 1 &&
            fanShadowBladeActors.Keys.All(id => bladeActors.Any(a => a.ObjectId == id));
        string bladeSignature = string.Join(";", bladeActors.Select(a =>
            $"{a.ObjectId:X}:{a.CastingSpellId}:{a.X:F2},{a.Z:F2}:{a.Heading:F3}"));
        var bladeProbeShapes = bladeActors.Select(a => new CastrumFluminisTimedGeometry.Cone(
            new System.Numerics.Vector2(a.X, a.Z), a.Heading, 44, (float)(108 * Math.PI / 180),
            0, a.SpellCastInfo.RemainingCastTime.TotalSeconds + 2)).ToArray();
        var stackCast = activeActors
            .FirstOrDefault(a => a.IsValid && a.BaseId == 0x2210 && a.IsCasting &&
                a.SpellCastInfo != null && a.CastingSpellId is 11238 or 11259);
        // The live stack response deliberately outlives the cast bar. Keep the
        // shadow lease invalid through that same retained effect window.
        bool retainedStack = stackTarget != 0 && now < stackUntil;
        bool stackOverlap = stackCast != null || retainedStack;
        uint shadowStackAction = stackCast?.CastingSpellId ?? (retainedStack ? stackAction : 0);
        uint shadowStackTarget = stackCast?.SpellCastInfo?.TargetId ?? (retainedStack ? stackTarget : 0);
        double shadowStackRemaining = stackCast?.SpellCastInfo?.RemainingCastTime.TotalSeconds ?? 0;
        BattleCharacter marked = shadowStackTarget == 0 ? null : shadowStackTarget == Core.Me.ObjectId ? Core.Me :
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .FirstOrDefault(a => a.IsValid && a.ObjectId == shadowStackTarget);
        Vector3? markedPosition = marked != null && marked.IsValid ? marked.Location : null;
        var shadowMarkedPosition = markedPosition.HasValue ? $"{markedPosition.Value.X:F2},{markedPosition.Value.Z:F2}" : "unavailable";
        var shadowMarked = markedPosition.HasValue ? new System.Numerics.Vector2(markedPosition.Value.X, markedPosition.Value.Z) : (System.Numerics.Vector2?)null;
        var shadowBoss = stackCast != null ? new System.Numerics.Vector2(stackCast.X, stackCast.Z) : new System.Numerics.Vector2(100, 100);
        bool shadowSelfTarget = shadowStackTarget == Core.Me.ObjectId;
        var position = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
        // A self-targeted stack needs another participant. Capture only living,
        // visible party locations on this thread; the worker never reads RB objects.
        var partySnapshot = shadowSelfTarget && shadowStackAction is 11238 or 11259
            ? GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                    PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId))
                .Select(a => (Id: a.ObjectId, Position: new System.Numerics.Vector2(a.X, a.Z)))
                .OrderBy(a => a.Id).ToArray()
            : Array.Empty<(uint Id, System.Numerics.Vector2 Position)>();
        var shadowParty = partySnapshot.Select(p => p.Position)
            .OrderBy(p => System.Numerics.Vector2.DistanceSquared(position, p)).ToArray();
        bool partyChanged = partySnapshot.Length != fanShadowParty.Length ||
            partySnapshot.Where((p, i) => i >= fanShadowParty.Length || p.Id != fanShadowParty[i].Id ||
                System.Numerics.Vector2.DistanceSquared(p.Position, fanShadowParty[i].Position) > 0.25f).Any();
        bool stackChanged = shadowStackAction != fanShadowStackAction || shadowStackTarget != fanShadowStackTarget ||
            markedPosition.HasValue != fanShadowMarked.HasValue ||
            (markedPosition.HasValue && fanShadowMarked.HasValue && markedPosition.Value.Distance2D(fanShadowMarked.Value) > 0.5f);
        bool moonStateKnown = Core.Player.Auras != null && Core.Player.Auras.IsValid;
        int lightStacks = moonStateKnown ? Core.Player.Auras.GetAuraStacksById(1538) : -1;
        int darkStacks = moonStateKnown ? Core.Player.Auras.GetAuraStacksById(1539) : -1;
        // This RB runtime returns -1 for an absent aura. Both auras present or
        // unavailable aura state cannot establish a safe side for a route.
        bool moonOverlap = !moonStateKnown || lightStacks >= 3 || darkStacks >= 3;
        int shadowMoonDirection = moonStateKnown && lightStacks >= 3 && darkStacks < 0 ? 1 :
            moonStateKnown && darkStacks >= 3 && lightStacks < 0 ? -1 : 0;
        fanSearchHoldAllowed = !stackOverlap && !moonOverlap && !haloOverlap && !bladeOverlap && now >= steelUntil;
        if (fanShadowTask != null && fanShadowTask.IsCompleted)
        {
            bool workerFailed = !fanShadowTask.IsCompletedSuccessfully;
            // Observe a fault before dropping the task; a failed search should
            // be retried after the existing throttle, not suppress this snapshot.
            if (fanShadowTask.IsFaulted) _ = fanShadowTask.Exception;
            var result = fanShadowTask.IsCompletedSuccessfully ? fanShadowTask.Result : ("search-fault", null, null, null);
            // Async search output belongs to the captured position and geometry,
            // not to the tick on which it completes. Mark drift rather than
            // presenting a stale route as applicable to the current state.
            bool steelNow = now < steelUntil;
            bool steelDrift = steelNow && (Math.Abs(Math.IEEERemainder(steelHeading - fanShadowSteelHeading,
                2 * Math.PI)) > 0.05 || steelOrigin.Distance2D(fanShadowSteelOrigin) > 0.5f);
            bool stale = unresolvedFans.Count != fanShadowObserved || steelNow != fanShadowSteel || steelDrift ||
                stackOverlap != fanShadowStack || stackChanged || partyChanged || moonOverlap != fanShadowMoon ||
                haloOverlap != fanShadowHalo || bladeOverlap != fanShadowBlade || haloSignature != fanShadowHaloSignature ||
                bladeSignature != fanShadowBladeSignature ||
                shadowMoonDirection != fanShadowMoonDirection ||
                Core.Me.Location.Distance2D(fanShadowStart) > 0.5f || (now - fanShadowSnapshotAt).TotalMilliseconds > 250;
            var candidate = result.Item2;
            bool rebaseRejected = false;
            if (EnableExperimentalTimedMovement && !stale && candidate != null &&
                !stackOverlap && !moonOverlap && !haloOverlap && !bladeOverlap)
            {
                candidate = CastrumFluminisTimedPlanner.RebaseInitialWait(candidate,
                    (now - fanShadowSnapshotAt).TotalSeconds, new System.Numerics.Vector2(Core.Me.X, Core.Me.Z),
                    new System.Numerics.Vector2(Arena.X, Arena.Z), 18.5f, TimedPlanningSpeed, result.Item3, result.Item4);
                rebaseRejected = candidate == null;
            }
            bool accepted = !stale && !stackOverlap && !moonOverlap && !haloOverlap && !bladeOverlap &&
                fanShadowLease.TryPublish(fanShadowToken, candidate,
                fanShadowSnapshotAt.AddSeconds(11), now);
            fanShadowHazards = accepted ? result.Item3 : null;
            fanShadowCones = accepted ? result.Item4 : null;
            Logger.Information("[Castrum Fluminis] Fan shadow stale={0} leaseAccepted={1} rebaseRejected={2} ageMs={3:F0} {4}", stale,
                accepted, rebaseRejected, (now - fanShadowSnapshotAt).TotalMilliseconds, result.Item1);
            fanShadowTask = null;
            // If the player moved during calculation, another snapshot may be
            // useful even without a new cast. Throttle retries so diagnostics
            // cannot continuously allocate worker searches on a moving player.
            if (stale || workerFailed || rebaseRejected) fanShadowObserved = -1;
        }
        // A published shadow route cannot remain comparable after its modeled
        // constraints change. Clearing its own generation does not touch the
        // real mover or a newer request.
        bool currentSteel = now < steelUntil;
        bool currentSteelDrift = currentSteel && (Math.Abs(Math.IEEERemainder(steelHeading - fanShadowSteelHeading,
            2 * Math.PI)) > 0.05 || steelOrigin.Distance2D(fanShadowSteelOrigin) > 0.5f);
        bool otherConstraintsChanged = currentSteel != fanShadowSteel ||
            currentSteelDrift || stackOverlap != fanShadowStack || stackChanged || partyChanged ||
            moonOverlap != fanShadowMoon || shadowMoonDirection != fanShadowMoonDirection ||
            haloOverlap != fanShadowHalo || bladeOverlap != fanShadowBlade || haloSignature != fanShadowHaloSignature ||
            bladeSignature != fanShadowBladeSignature;
        bool fansChanged = unresolvedFans.Count != fanShadowObserved;
        var activeRoute = fanShadowLease.Get(fanShadowToken, now);
        if (fansChanged && !otherConstraintsChanged && activeRoute != null && fanShadowHazards != null &&
            unresolvedFans.Count > fanShadowObserved && fanShadowObserved >= 2)
        {
            // The observed half-second fan cadence previously restarted the
            // half-second startup wait before any waypoint could be issued.
            // Preserve a route only when all tells still match the anchored
            // pattern and the actual remaining route clears each new window.
            var freshTells = unresolvedFans.OrderBy(f => f.Until).Select(f =>
                new CastrumFluminisTimedPlanner.FanTell(new System.Numerics.Vector2(f.Location.X, f.Location.Z),
                    (f.Until - fanShadowSnapshotAt).TotalSeconds - 1.1)).ToArray();
            var arena = new System.Numerics.Vector2(Arena.X, Arena.Z);
            var updatedHazards = CastrumFluminisTimedPlanner.ReconcileFanTells(arena, freshTells,
                (centerImpact - fanShadowSnapshotAt).TotalSeconds - 1.1, fanShadowHazards);
            double elapsed = (now - fanShadowSnapshotAt).TotalSeconds;
            var actual = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
            if (updatedHazards != null && CastrumFluminisTimedPlanner.ValidateRemainingRoute(activeRoute,
                    elapsed, actual, arena, 18.5f, TimedExecutionSpeed, updatedHazards, fanShadowCones, null, null))
            {
                fanShadowHazards = updatedHazards;
                fanShadowObserved = unresolvedFans.Count;
                fansChanged = false;
                Logger.Information("[Castrum Fluminis] Timed fan route retained after tell {0}", fanShadowObserved);
            }
        }
        bool constraintsChanged = fansChanged || otherConstraintsChanged;
        if (constraintsChanged && fanShadowLease.Get(fanShadowToken, now) != null)
        {
            fanShadowLease.TryClear(fanShadowToken);
            fanShadowProgress = null;
            fanShadowRemainingValid = null;
        }
        if (fanShadowLease.Get(fanShadowToken, now) != null)
        {
            var actual = new System.Numerics.Vector2(Core.Me.X, Core.Me.Z);
            var progress = fanShadowLease.Assess(fanShadowToken, now,
                (now - fanShadowSnapshotAt).TotalSeconds, actual, 0.7f);
            bool remainingValid = fanShadowLease.TryNextValidatedWaypoint(fanShadowToken, now,
                (now - fanShadowSnapshotAt).TotalSeconds, actual, 0.7f,
                new System.Numerics.Vector2(Arena.X, Arena.Z), 18.5f, TimedExecutionSpeed,
                fanShadowHazards, fanShadowCones, null, null, out _);
            if (fanShadowProgress != progress || fanShadowRemainingValid != remainingValid)
            {
                fanShadowProgress = progress;
                fanShadowRemainingValid = remainingValid;
                Logger.Information("[Castrum Fluminis] Fan shadow comparison={0} elapsed={1:F2} actual={2:F2},{3:F2} remainingRouteValid={4}",
                    progress, (now - fanShadowSnapshotAt).TotalSeconds, actual.X, actual.Y, remainingValid);
            }
        }
        if (fanShadowTask != null || now < nextFanShadowAt || earlyFans.Count < 2 || unresolvedFans.Count == 0) return;
        bool hasSteel = now < steelUntil;
        bool steelChanged = hasSteel && (Math.Abs(Math.IEEERemainder(steelHeading - fanShadowSteelHeading,
            2 * Math.PI)) > 0.05 || steelOrigin.Distance2D(fanShadowSteelOrigin) > 0.5f);
        if (fanShadowObserved == unresolvedFans.Count && fanShadowSteel == hasSteel && !steelChanged &&
            fanShadowStack == stackOverlap && !stackChanged && !partyChanged && fanShadowMoon == moonOverlap &&
            fanShadowMoonDirection == shadowMoonDirection && fanShadowHalo == haloOverlap &&
            fanShadowBlade == bladeOverlap && fanShadowHaloSignature == haloSignature &&
            fanShadowBladeSignature == bladeSignature) return;
        var orderedFans = unresolvedFans.OrderBy(f => f.Until).ToArray();
        // Validate every newly discovered tell against the proposed rotation.
        // Restricting inference to the first three silently retained a forecast
        // after a later observed actor contradicted it.
        var tells = orderedFans.Select(f => new CastrumFluminisTimedPlanner.FanTell(
            new System.Numerics.Vector2(f.Location.X, f.Location.Z),
            (f.Until - now).TotalSeconds - 1.1)).ToArray();
        // Preserve every actor already seen, including helpers beyond the first
        // three. A forecast never overrides an actual position or cast timing.
        var observed = unresolvedFans.Select(f => new CastrumFluminisTimedGeometry.Circle(
            new System.Numerics.Vector2(f.Location.X, f.Location.Z), 10.5f,
            (f.Until - now).TotalSeconds - 1.25, (f.Until - now).TotalSeconds)).ToArray();
        // The current shadow model has no verified party-motion prediction or
        // moon deadline. Identify these overlaps instead of labeling a fan-only
        // route as feasible for the complete encounter.
        double centralImpact = (centerImpact - now).TotalSeconds - 1.1;
        var steelCenter = new System.Numerics.Vector2(steelOrigin.X, steelOrigin.Z);
        float heading = steelHeading;
        double steelEnd = (steelUntil - now).TotalSeconds;
        fanShadowObserved = observed.Length;
        fanShadowSteel = hasSteel;
        fanShadowStack = stackOverlap;
        fanShadowStackAction = shadowStackAction;
        fanShadowStackTarget = shadowStackTarget;
        fanShadowMarked = markedPosition;
        fanShadowParty = partySnapshot;
        fanShadowMoon = moonOverlap;
        fanShadowHalo = haloOverlap;
        fanShadowBlade = bladeOverlap;
        fanShadowHaloSignature = haloSignature;
        fanShadowBladeSignature = bladeSignature;
        fanShadowMoonDirection = shadowMoonDirection;
        fanShadowSteelHeading = heading;
        fanShadowSteelOrigin = steelOrigin;
        fanShadowSnapshotAt = now;
        nextFanShadowAt = now.AddMilliseconds(500);
        fanShadowStart = Core.Me.Location;
        fanShadowToken = fanShadowLease.BeginGeneration();
        fanShadowProgress = null;
        fanShadowRemainingValid = null;
        fanShadowTask = Task.Run(() =>
        {
            var arena = new System.Numerics.Vector2(100, 100);
            // The tell tolerance is provisional; observed positions take precedence over the forecast.
            var forecast = CastrumFluminisTimedPlanner.ForecastAnchoredFans(arena, tells, centralImpact,
                0.5, 0.2, 0.15, 1.1, 10.5f);
            if (forecast == null) return ("unknown-pattern", (IReadOnlyList<CastrumFluminisTimedPlanner.Step>)null,
                (CastrumFluminisTimedGeometry.Circle[])null, (CastrumFluminisTimedGeometry.Cone[])null);
            var circles = forecast.ToList();
            circles.AddRange(observed);
            circles.Add(new CastrumFluminisTimedGeometry.Circle(arena, 10.5f, centralImpact - 0.15, centralImpact + 1.1));
            var cones = hasSteel ? new[] { new CastrumFluminisTimedGeometry.Cone(steelCenter, heading, 70,
                (float)(48 * Math.PI / 180), 0, steelEnd) } : null;
            // Preserve the existing modeled native cone as an exploratory
            // shape only; live captures did not establish its safe boundary.
            var probeCones = (cones ?? Array.Empty<CastrumFluminisTimedGeometry.Cone>()).Concat(bladeProbeShapes).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var route = CastrumFluminisTimedPlanner.Search(position, arena, 18.5f, TimedPlanningSpeed, 0.5, 44, circles, cones);
            bool valid = route != null && CastrumFluminisTimedPlanner.ValidateHazardRoute(route, position,
                arena, 18.5f, TimedPlanningSpeed, circles, cones);
            string haloCandidate = "not-evaluated";
            if (haloProbeShapes.Length > 0 && haloGeometryAvailable && bladeGeometryAvailable)
            {
                // The ring is treated as active for the entire observed cast
                // plus an assumed post-cast window. This is a feasibility probe,
                // not a calibrated damage window or permission to move.
                var haloRoute = CastrumFluminisTimedPlanner.Search(position, arena, 18.5f, TimedPlanningSpeed, 0.5, 44,
                    circles, probeCones, null, null, haloProbeShapes);
                bool haloValid = haloRoute != null && CastrumFluminisTimedPlanner.ValidateHazardRoute(
                    haloRoute, position, arena, 18.5f, TimedPlanningSpeed, circles, probeCones, haloProbeShapes);
                haloCandidate = haloValid ? "yes-provisional-window" : "none-provisional-window";
            }
            string moonCandidate = "not-evaluated";
            CastrumFluminisTimedPlanner.RequiredHalfPlane[] moonSide = null;
            if (shadowMoonDirection != 0)
            {
                // The +2y side inset is observed; the 2s deadline is a probe,
                // not measured status timing. Do not treat this as a solution.
                moonSide = new[] { new CastrumFluminisTimedPlanner.RequiredHalfPlane(arena,
                    new System.Numerics.Vector2(shadowMoonDirection, 0), 2, 2, 2.5) };
                if (!stackOverlap && bladeGeometryAvailable && haloGeometryAvailable)
                {
                    var moonRoute = CastrumFluminisTimedPlanner.Search(position, arena, 18.5f, TimedPlanningSpeed, 0.5, 44,
                        circles, probeCones, null, moonSide, haloProbeShapes);
                    bool moonValid = moonRoute != null && CastrumFluminisTimedPlanner.ValidateHazardRoute(
                        moonRoute, position, arena, 18.5f, TimedPlanningSpeed, circles, probeCones, haloProbeShapes) &&
                        CastrumFluminisTimedPlanner.ValidatePositionRoute(moonRoute, null, moonSide);
                    moonCandidate = moonValid ? "yes-provisional-deadline" : "none-provisional-deadline";
                }
            }
            string stackCandidate = "not-evaluated";
            // If both moon statuses are present, the side mapping is ambiguous.
            // A stack-only candidate would falsely imply the combined overlap
            // was feasible, so leave it unevaluated until the side is known.
            bool stackProbeAllowed = (!moonOverlap || moonSide != null) && bladeGeometryAvailable &&
                haloGeometryAvailable;
            // This second diagnostic search assumes a stationary marked player
            // and a provisional single-hit window. It is not a claim that party
            // participation or repeated stack hits are modeled or safe.
            if (stackProbeAllowed && stackOverlap && !shadowSelfTarget && shadowMarked.HasValue && shadowStackRemaining >= 0.15 && shadowStackRemaining <= 10.5)
            {
                double from = shadowStackRemaining - 0.15, until = shadowStackRemaining + 0.5;
                var regions = shadowStackAction == 11259 ? new[] {
                    new CastrumFluminisTimedPlanner.RequiredCircle(shadowMarked.Value, 4.5f, from, until) } : null;
                var planes = shadowStackAction == 11238 ? CastrumFluminisTimedPlanner.LineStackRegion(
                    shadowBoss, shadowMarked.Value, 3f, 0.3f, from, until) : null;
                if (regions != null || planes != null)
                {
                    if (moonSide != null) planes = (planes ?? Array.Empty<CastrumFluminisTimedPlanner.RequiredHalfPlane>()).Concat(moonSide).ToArray();
                    var combined = CastrumFluminisTimedPlanner.Search(position, arena, 18.5f, TimedPlanningSpeed, 0.5, 44,
                        circles, probeCones, regions, planes, haloProbeShapes);
                    bool combinedValid = combined != null && CastrumFluminisTimedPlanner.ValidateHazardRoute(
                        combined, position, arena, 18.5f, TimedPlanningSpeed, circles, probeCones, haloProbeShapes) &&
                        CastrumFluminisTimedPlanner.ValidatePositionRoute(combined, regions, planes);
                    stackCandidate = combinedValid ? "yes-static-target" : "none-static-target";
                }
            }
            else if (stackProbeAllowed && stackOverlap && shadowSelfTarget && shadowStackAction == 11259 && shadowParty.Length > 0 &&
                     shadowStackRemaining >= 0.15 && shadowStackRemaining <= 10.5)
            {
                stackCandidate = "none-static-party";
                foreach (var member in shadowParty)
                {
                    var region = new[] { new CastrumFluminisTimedPlanner.RequiredCircle(member, 4.5f,
                        shadowStackRemaining - 0.15, shadowStackRemaining + 0.5) };
                    var combined = CastrumFluminisTimedPlanner.Search(position, arena, 18.5f, TimedPlanningSpeed, 0.5, 44,
                        circles, probeCones, region, moonSide, haloProbeShapes);
                    if (combined != null && CastrumFluminisTimedPlanner.ValidateHazardRoute(
                        combined, position, arena, 18.5f, TimedPlanningSpeed, circles, probeCones, haloProbeShapes) &&
                        CastrumFluminisTimedPlanner.ValidatePositionRoute(combined, region, moonSide))
                    {
                        stackCandidate = "yes-static-party";
                        break;
                    }
                }
            }
            else if (stackProbeAllowed && stackOverlap && shadowSelfTarget && shadowStackAction == 11238 && shadowParty.Length > 0 &&
                     shadowStackRemaining >= 0.15 && shadowStackRemaining <= 10.5)
            {
                // For a line marked on self, try standing beyond a party member
                // on the boss-to-member ray. These candidates approximate static
                // party positions and are never published as live movement.
                stackCandidate = "none-static-party-line";
                double from = shadowStackRemaining - 0.15, until = shadowStackRemaining + 0.5;
                foreach (var member in shadowParty)
                {
                    var delta = member - shadowBoss;
                    float distance = delta.Length();
                    if (!float.IsFinite(distance) || distance < 1) continue;
                    var direction = delta / distance;
                    var region = CastrumFluminisTimedPlanner.LineStackRegion(shadowBoss,
                        member + direction * 3, 3f, 0.3f, from, until);
                    if (region == null) continue;
                    var planes = region.Concat(new[] { new CastrumFluminisTimedPlanner.RequiredHalfPlane(
                        member, direction, 0.3f, from, until) }).ToArray();
                    if (moonSide != null) planes = planes.Concat(moonSide).ToArray();
                    var combined = CastrumFluminisTimedPlanner.Search(position, arena, 18.5f, TimedPlanningSpeed, 0.5, 44,
                        circles, probeCones, null, planes, haloProbeShapes);
                    if (combined != null && CastrumFluminisTimedPlanner.ValidateHazardRoute(
                        combined, position, arena, 18.5f, TimedPlanningSpeed, circles, probeCones, haloProbeShapes) &&
                        CastrumFluminisTimedPlanner.ValidatePositionRoute(combined, null, planes))
                    {
                        stackCandidate = "yes-static-party-line";
                        break;
                    }
                }
            }
            watch.Stop();
            string waypoints = valid ? string.Join(";", route.Take(9).Select(s =>
                $"{s.Time:F2}:{s.Position.X:F2},{s.Position.Y:F2}")) : "none";
            string observedWindows = string.Join(";", observed.Select(c =>
                $"{c.Center.X:F2},{c.Center.Y:F2}:{c.Start:F2}..{c.End:F2}"));
            double angle0 = Math.Atan2(tells[0].Position.Y - arena.Y, tells[0].Position.X - arena.X);
            double angle1 = Math.Atan2(tells[1].Position.Y - arena.Y, tells[1].Position.X - arena.X);
            double firstImpact = tells[0].Impact;
            int fanDirection = Math.IEEERemainder(angle1 - angle0, 2 * Math.PI) > 0 ? 1 : -1;
            return ($"candidate={valid} completeOverlapModel={!(stackOverlap || moonOverlap || haloOverlap || bladeOverlap)} haloUnmodeled={haloOverlap} haloFanSteelCandidate={haloCandidate} haloCount={haloProbeShapes.Length} bladeUnmodeled={bladeOverlap} stackUnmodeled={stackOverlap} stackCandidate={stackCandidate} stackAction={shadowStackAction} stackTarget={shadowStackTarget:X} stackRemaining={shadowStackRemaining:F2} marked={shadowMarkedPosition} moonUnmodeled={moonOverlap} moonCandidate={moonCandidate} moonDirection={shadowMoonDirection} elapsedMs={watch.Elapsed.TotalMilliseconds:F1} assumedWindows=true fanDirection={fanDirection} firstNominal={firstImpact:F2} centerNominal={centralImpact:F2} observedFans={observed.Length} observedWindows={observedWindows} steel={hasSteel} path={waypoints}", valid ? route : null, circles.ToArray(), cones);
        });
    }

    private Vector3? FindFanPoint(bool staging, bool steelActive, bool firstOnly)
    {
        // Sample the simple observed pattern: an inset ring outside the central
        // 10y blast, then a 3y central region. First-edge clearance avoids the
        // half-second handoff that native avoidance could not execute in time.
        Vector3 from = Core.Me.Location;
        Vector3? best = null;
        double bestScore = double.NegativeInfinity;
        float radius = staging ? 12.5f : 3f;
        for (int i = 0; i < 32; i++)
        {
            double angle = i * Math.PI / 16;
            var point = new Vector3(Arena.X + radius * (float)Math.Cos(angle), from.Y,
                Arena.Z + radius * (float)Math.Sin(angle));
            float edgeDistance = point.Distance2D(firstEdge);
            // Preserve clearance after the 0.7y arrival tolerance; avoid a long
            // trip across the arena merely to maximize distance from that fan.
            if (staging && (firstOnly ? earlyFans.Take(1) : earlyFans).Any(f => point.Distance2D(f.Location) < 11.5f)) continue;
            if (staging && !firstOnly && possibleEarlyFans.Any(p => point.Distance2D(p) < 11.5f)) continue;
            // November 5: Steel redirected the player through the still-live
            // center and first outer circle. Two safe endpoints did not make
            // that segment safe before the central impact.
            if (staging && (!CircleExitSegmentSafe(from, point, Arena, 10.5f) ||
                (firstOnly ? earlyFans.Take(1) : earlyFans).Any(f => !CircleExitSegmentSafe(from, point, f.Location, 10.5f)))) continue;
            // The synchronized capture showed four destination changes in 0.17s
            // as the boss turned. Select extra angular clearance so small turns
            // do not immediately invalidate a newly chosen point.
            if (steelActive && InSteel(point, 58)) continue;
            if (steelActive && !SteelSegmentSafe(from, point)) continue;
            double score = -point.Distance2D(from) + (staging ? edgeDistance * 0.05 : 0);
            if (score > bestScore) { bestScore = score; best = point; }
        }
        return best;
    }

    private static bool CircleExitSegmentSafe(Vector3 from, Vector3 to, Vector3 center, float radius)
    {
        bool outside = from.Distance2D(center) >= radius;
        int samples = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / 0.5f));
        for (int i = 1; i <= samples; i++)
        {
            float t = (float)i / samples;
            var p = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            bool inside = p.Distance2D(center) < radius;
            if (outside && inside) return false;
            if (!inside) outside = true;
        }
        return outside;
    }

    private bool SteelSegmentSafe(Vector3 from, Vector3 to)
    {
        bool outside = !InSteel(from);
        int samples = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / 0.5f));
        for (int i = 1; i <= samples; i++)
        {
            float t = (float)i / samples;
            var p = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            bool inside = InSteel(p);
            if (outside && inside) return false;
            if (!inside) outside = true;
        }
        return outside;
    }

    private bool InSteel(Vector3 point, float halfAngle = 48)
    {
        float x = point.X - steelOrigin.X, z = point.Z - steelOrigin.Z;
        float length = (float)Math.Sqrt(x * x + z * z);
        if (length < 0.01f) return true;
        // Steel is a 90-degree front cone; 48 degrees adds a small turn margin.
        return (x * (float)Math.Sin(steelHeading) + z * (float)Math.Cos(steelHeading)) / length > Math.Cos(halfAngle * Math.PI / 180);
    }

    private void ReleaseFanSequence()
    {
        if (sequenceOwned)
        {
            ClearPosition(PositionOwner.Fan);
            if (positionOwner != PositionOwner.Stack)
                CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Fan sequence ended");
        }
        sequenceOwned = false;
        timedFanOwned = false;
        fanSearchHoldAllowed = false;
        earlyFans.Clear();
        fanShadowTask = null;
        fanShadowHazards = null;
        fanShadowCones = null;
        fanShadowLease.TryClear(fanShadowToken);
        fanShadowProgress = null;
        fanShadowRemainingValid = null;
        fanShadowObserved = -1;
        fanShadowSteel = false;
        fanShadowStack = fanShadowMoon = false;
        fanShadowHalo = fanShadowBlade = false;
        fanShadowHaloUntil = fanShadowBladeUntil = default;
        fanShadowHaloSignature = "";
        fanShadowBladeSignature = "";
        fanShadowHaloActors.Clear();
        fanShadowBladeActors.Clear();
        fanShadowMoonDirection = 0;
        fanShadowStackAction = fanShadowStackTarget = 0;
        fanShadowMarked = null;
        fanShadowParty = Array.Empty<(uint, System.Numerics.Vector2)>();
        nextFanShadowAt = default;
        unresolvedFans.Clear();
        possibleEarlyFans.Clear();
        centerImpact = sequenceUntil = steelUntil = default;
        nextFanCastInterrupt = default;
        stagePoint = null;
        stageIsFallback = false;
    }

    private void RestoreSideStep()
    {
        if (ownsSideStep && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = previousSideStep;
        ownsSideStep = false;
    }
}
