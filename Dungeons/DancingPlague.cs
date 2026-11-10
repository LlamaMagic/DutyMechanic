using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Coordinates the normal Dancing Plague trial and its encounter mechanics.
/// </summary>
public sealed partial class DancingPlague : AbstractDungeon
{
    private const uint Territory = 845, Titania = 0x27ED, Helper = 0x233C;
    private static readonly Vector3 Center = new(100, 0, 100);
    private static DancingPlague current;
    private ChrysalisMovement movement;
    private bool sideStepBorrowed, priorSideStep, unsupportedMover, servicingRoutine;
    private Vector3[] rebukeOrigins = [];
    private readonly Dictionary<uint, string> watchedObjects = new();
    private DateTime nextObjectSample, actorWatchUntil;
    private DateTime nextTargetlessHealingLog;
    private readonly CapabilityManagerHandle phantomHandle = CapabilityManager.CreateNewHandle();
    private uint phantomAction;
    private Vector3 phantomOrigin;
    private float phantomHeading;
    private DateTime phantomUntil;
    private Vector3? phantomDestination;
    private bool phantomOwned, phantomLeased, phantomArrivalLogged, phantomFailureLogged;

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)Territory;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private static bool InTrial => TreeRoot.IsRunning && DutyManager.InInstance && WorldManager.ZoneId == Territory;

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        // Reserve a half-yalm margin around Titania’s ten-yalm circle. The
        // inverse five-yalm safe pocket is handled by the shared movement owner.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => InTrial, objectSelector: a => a.IsValid && a.BaseId == Titania && a.IsCasting && a.CastingSpellId == 15709,
            radiusProducer: _ => 10.5f);
        // The donut and Divination cone use the encounter's shared movement
        // owner below: a synchronized capture showed native no-path loops for
        // both despite reachable floor, followed by a gain and a death.
        // Puck's center blast is separate from the arena-wide knockback. The
        // latter must never become a 60y avoid, as that blocked all navigation.
        // The add handler chooses a position with a valid knockback landing.
        // Snapshot locations instead of attracting navigation toward untargetable
        // helper actors; that coupling contributed to earlier no-path encounters.
        AvoidanceManager.AddAvoidLocation<Vector3>(() => InTrial, () => Center, 60f,
            _ => 5.5f, origin => origin, () => current?.addOwned == true ? [] : current?.rebukeOrigins ?? []);
        CheckLifecycle();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // CombatLogic may revisit this hook; never recursively service the routine.
        if (servicingRoutine) return false;
        await UsefulTasks.HandleReadyCheck();
        CheckLifecycle();
        if (current == this && NeedsTargetlessHealing())
        {
            // OrderBot can skip the healer routine during untargetable phases.
            // Offer its existing combat logic a tick without selecting its actions;
            // the encounter mover retains priority over native escape.
            servicingRoutine = true;
            if (LoggingHelpers.MechanicDiagnosticsEnabled && DateTime.UtcNow >= nextTargetlessHealingLog)
            {
                nextTargetlessHealingLog = DateTime.UtcNow.AddSeconds(5);
                Logger.Information("[DancingPlague] Servicing targetless healer routine.");
            }
            try { await CommonTasks.ExecuteCoroutine(BrainBehavior.CombatLogic); }
            finally { servicingRoutine = false; }
            // The routine may yield through a stop, loading or death transition.
            CheckLifecycle();
        }
        return false;
    }

    private bool NeedsTargetlessHealing()
    {
        if (!InTrial || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            !Core.Me.InCombat || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            movement?.IsEscaping == true || AvoidanceManager.IsRunningOutOfAvoid ||
            Core.Me.CurrentJob is not (ClassJobType.WhiteMage or ClassJobType.Scholar or
                ClassJobType.Astrologian or ClassJobType.Sage)) return false;

        var target = Core.Me.CurrentTarget;
        if (target != null && target.IsValid && target.CanAttack) return false;
        return Core.Me.CurrentHealthPercent < 90 ||
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
                actor.IsValid && actor.IsAlive && actor.CurrentHealthPercent < 90 &&
                PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId));
    }

    /// <summary>Maintains the trial's borrowed input and SideStep lifetimes on the bot thread.</summary>
    public static void CheckLifecycle()
    {
        if (current == null) return;
        if (!InTrial)
        {
            current.Release();
            return;
        }
        // Titania is untargetable during the add phase; territory plus instance
        // preserves the same owner through that transition and a same-visit wipe.
        if (!current.sideStepBorrowed && SidestepPlugin != null)
        {
            current.priorSideStep = SidestepPlugin.Enabled;
            current.sideStepBorrowed = true;
        }
        if (SidestepPlugin != null && SidestepPlugin.Enabled) SidestepPlugin.Enabled = false;
        if (Core.Me == null || !Core.Me.IsAlive || CommonBehaviors.IsLoading)
        {
            current.rebukeOrigins = [];
            current.ResetFrost();
            current.ResetAdds();
            current.ReleaseWater();
            current.ResetRoots();
            current.ResetPhantom();
            current.movement?.Detach();
            current.movement = null;
            return;
        }
        current.rebukeOrigins = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.BaseId == Helper && a.IsCasting && a.CastingSpellId == 15704)
            .Select(a => a.Location).ToArray();
        current.CaptureMechanicObjects();
        if (current.movement != null && !current.movement.IsCurrent)
        {
            current.movement.Detach();
            current.movement = null;
        }
        if (current.movement == null && !current.unsupportedMover)
        {
            if (Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            {
                // This shared owner releases only its own native escape input and
                // checks ordinary waypoints before combat can re-enter an active avoid.
                current.movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: OrdinarySegmentClear);
                Navigator.PlayerMover = current.movement;
            }
            else
            {
                current.unsupportedMover = true;
                Logger.Warning("[DancingPlague] Unsupported mover; native escape cleanup is unverified.");
            }
        }
        current.UpdateFrost();
        current.UpdateAdds();
        current.UpdateWater();
        current.UpdateRoots();
        current.UpdatePhantom();
        current.movement?.Update();
    }

    private static bool OrdinarySegmentClear(Vector3 destination)
    {
        var start = Core.Me.Location;
        float dx = destination.X - start.X, dz = destination.Z - start.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(1, (int)MathF.Ceiling(distance / 0.5f));
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            var point = new Vector3(start.X + dx * t, start.Y, start.Z + dz * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private void Release()
    {
        movement?.Detach();
        movement = null;
        ResetFrost();
        ResetAdds();
        ResetWater();
        ResetRoots();
        ResetPhantom();
        unsupportedMover = false;
        rebukeOrigins = [];
        watchedObjects.Clear();
        actorWatchUntil = nextObjectSample = default;
        nextTargetlessHealingLog = default;
        LoggingHelpers.ClearActorSignalWatch("DancingPlague");
        if (sideStepBorrowed && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = priorSideStep;
        sideStepBorrowed = false;
    }

    /// <summary>Releases only this trial's borrowed state on stop, disable or exit.</summary>
    public static void ResetActive()
    {
        current?.Release();
        current = null;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    private void CaptureMechanicObjects()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled) return;
        var now = DateTime.UtcNow;
        if (now < nextObjectSample) return;
        nextObjectSample = now.AddMilliseconds(500);
        // Water and root objects have no exposed animation-state API here.
        // Preserve their observed creation, location, visibility and removal,
        // then correlate those signals with casts and outcomes rather than
        // treating mere object presence as proof of a safe or unsafe floor.
        var objects = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(o => o.IsValid && o.Location.Distance2D(Center) < 35).ToArray();
        foreach (var item in objects)
        {
            string state = $"base=0x{item.BaseId:X} location={item.Location} heading={item.Heading:F3} visible={item.IsVisible} targetable={item.IsTargetable}";
            if (!watchedObjects.TryGetValue(item.ObjectId, out string prior) || prior != state)
            {
                watchedObjects[item.ObjectId] = state;
                Logger.Information($"[DancingPlague] OBJECT id=0x{item.ObjectId:X} {state}.");
            }
        }
        foreach (uint id in watchedObjects.Keys.Where(id => objects.All(o => o.ObjectId != id)).ToArray())
        {
            Logger.Information($"[DancingPlague] OBJECT_REMOVED id=0x{id:X} {watchedObjects[id]}.");
            watchedObjects.Remove(id);
        }
        if (GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(a => a.IsValid && a.IsCasting &&
                (a.BaseId == Titania && a.CastingSpellId is 15685 or 15687 or 15662 ||
                 a.BaseId == 0x27F4 && a.CastingSpellId == 15703)))
            // A November 8 death followed Puck's Breath after the selected
            // stack point had been reached. Capture party positions around the
            // cast and delayed hit so a moving carrier or insufficient stack
            // can be distinguished from unrelated damage on the next visit.
            actorWatchUntil = now.AddSeconds(25);
        // Include the add targets in the bounded stack watch: November 9 had
        // rapid, unattributed health loss after Puck's knockback. Actor target
        // changes can establish an aggro lead, but are not hit attribution.
        if (now < actorWatchUntil)
            LoggingHelpers.LogActorSignalChanges("DancingPlague", a => a.ObjectId == Core.Me.ObjectId ||
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                a.BaseId is 0x2878 or Titania or 0x27EE or 0x27EF or 0x27F0 or 0x27F2 or 0x27F3 or 0x27F4);
        else
            LoggingHelpers.ClearActorSignalWatch("DancingPlague");
    }

    private void UpdatePhantom()
    {
        if (movement == null || !movement.IsCurrent)
        {
            ResetPhantom(false);
            return;
        }
        var now = DateTime.UtcNow;
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.BaseId == Titania && a.IsCasting &&
                a.CastingSpellId is 15710 or 15707 && a.SpellCastInfo != null);
        if (boss != null)
        {
            if (phantomAction != boss.CastingSpellId)
            {
                phantomDestination = null;
                phantomArrivalLogged = false;
            }
            phantomAction = boss.CastingSpellId;
            phantomOrigin = boss.Location;
            phantomHeading = boss.Heading;
            // A captured donut gain and cone death arrived after cast end.
            // Retain the last observed geometry for a bounded 1.3s after it.
            phantomUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.3);
        }
        if (phantomAction == 0 || now >= phantomUntil)
        {
            ResetPhantom(!rootOwned && !waterOwned && !addOwned && !frostLeased);
            return;
        }
        if (addOwned)
        {
            // Those owners ran earlier on this tick; never clear their request.
            ResetPhantom(false);
            return;
        }
        var player = Core.Me.Location;
        Func<Vector3, bool> frostRoute = _ => true;
        if (frost.Count != 0)
        {
            var centerCast = frost.Values.FirstOrDefault(f => f.RingIndex == -1);
            var outer = frost.Values.Where(f => f.RingIndex >= 0).OrderBy(f => f.Impact).ToArray();
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
            var spreadPositions = SpreadPositions(actors);
            int delta = outer.Length >= 2 ? (outer[1].RingIndex - outer[0].RingIndex + 8) % 8 : 0;
            int direction = delta == 1 ? 1 : delta == 7 ? -1 : 0;
            if (centerCast == null || outer.Length < 2 || direction == 0 || spreadPositions == null ||
                outer.Where((f, i) => f.RingIndex != (outer[0].RingIndex + direction * i + 64) % 8 ||
                    Math.Abs((f.Impact - outer[0].Impact).TotalSeconds - 0.8 * i) > 0.8).Any())
            {
                // Preserve Frost's request when its order or party geometry is
                // incomplete; an unverified overlap cannot grant a new owner.
                ResetPhantom(false);
                return;
            }
            // November 9: waiting for Frost to release lost 0.6s of a long
            // donut approach. Reuse the actual timed Frost certificate and
            // spread snapshot before permitting this earlier shared-owner handoff.
            frostRoute = p => spreadPositions.All(position => position.Distance2D(p) >= 6.5f) &&
                FrostRouteSafe(player, p, now, centerCast, outer, direction, centerCast.Impact.AddSeconds(0.4), true);
        }
        var roots = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x1EABE4 && o.Location.Distance2D(Center) < 35)
            .Select(o => o.Location).ToArray();
        bool Valid(Vector3 p) => PhantomPointSafe(p) && OrdinarySegmentClear(p) &&
            RootRouteSafe(player, p, roots) && frostRoute(p);
        if (!phantomDestination.HasValue || !Valid(phantomDestination.Value))
        {
            var points = new List<Vector3> { player };
            for (int x = -18; x <= 18; x += 2)
                for (int z = -18; z <= 18; z += 2)
                    points.Add(new Vector3(Center.X + x, Center.Y, Center.Z + z));
            if (phantomAction == 15710) points.Add(phantomOrigin);
            phantomDestination = points.Where(Valid).OrderBy(p => p.Distance2D(player)).Cast<Vector3?>().FirstOrDefault();
            phantomArrivalLogged = false;
            if (phantomDestination.HasValue && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Phantom action={phantomAction} destination={phantomDestination} until={phantomUntil:O}.");
        }
        if (!phantomDestination.HasValue)
        {
            if (frost.Count != 0)
            {
                // Frost has already submitted its current request; do not
                // replace it with an unverified direct route or a stale hold.
                ResetPhantom(false);
                return;
            }
            if (phantomOwned) movement.Request(null, 0.5f);
            CapabilityManager.Update(phantomHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Dancing Plague: Phantom route unavailable");
            phantomLeased = true;
            if (!phantomFailureLogged && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Warning("[DancingPlague] No verified Phantom route; holding ordinary movement for reassessment.");
            phantomFailureLogged = true;
            return;
        }
        phantomFailureLogged = false;
        foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
            CapabilityManager.Update(phantomHandle, flag, TimeSpan.FromMilliseconds(500), "Dancing Plague: Phantom escape");
        phantomOwned = phantomLeased = true;
        movement.RequestHazardExit(phantomDestination.Value, 0.5f);
        if (!phantomArrivalLogged && player.Distance2D(phantomDestination.Value) <= 0.5f && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            Logger.Information($"[DancingPlague] Phantom arrived action={phantomAction} player={player} target={phantomDestination}.");
            phantomArrivalLogged = true;
        }
    }

    private bool PhantomPointSafe(Vector3 point)
    {
        if (MathF.Abs(point.X - Center.X) > 19 || MathF.Abs(point.Z - Center.Z) > 19) return false;
        float x = point.X - phantomOrigin.X, z = point.Z - phantomOrigin.Z;
        if (phantomAction == 15710) return x * x + z * z <= 4f * 4f;
        float forward = x * MathF.Sin(phantomHeading) + z * MathF.Cos(phantomHeading);
        float side = x * MathF.Cos(phantomHeading) - z * MathF.Sin(phantomHeading);
        return forward < -0.5f || MathF.Abs(side) > MathF.Max(0, forward) * MathF.Tan(48 * MathF.PI / 180) + 0.5f;
    }

    private void ResetPhantom(bool releaseRequest = true)
    {
        if (releaseRequest && phantomOwned) movement?.Request(null, 0.5f);
        if (phantomLeased) CapabilityManager.Clear(phantomHandle);
        phantomAction = 0;
        phantomUntil = default;
        phantomDestination = null;
        phantomOwned = phantomLeased = phantomArrivalLogged = phantomFailureLogged = false;
    }
}
