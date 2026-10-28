using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Leviathan's deck hazards, converter, center staging and tank assignments.
/// The standalone profile selects combat targets; OrderBot and the routine handle combat.
/// </summary>
/// <remarks>
/// Uses the Hard-mode ship boundary and Global 2026.10.16 action dimensions.
/// Slam and dive predictions expire after their expected impact. These assumptions
/// do not apply to Extreme, where the ship's rails break.
/// </remarks>
public sealed partial class WhorleaterHard : AbstractDungeon
{
    // Name IDs belong in the profile; these are actor base IDs and event-object identities.
    private const uint Head = 0xA67;
    private const uint DiveHelper = 0xA87;
    private const uint Converter = 0x1E922A;
    private const uint HydroPuddle = 0x1E9230;
    private const uint FearPuddle = 0x1E9231;
    private const uint Slam = 1860;
    private const uint Dive = 1869;
    private const uint Hydro = 1864;
    private const uint Fear = 1865;
    private const uint Fall = 1873;
    private const float Margin = 0.5f;

    private sealed class Hazard
    {
        internal uint Owner, Action;
        internal Vector3 Origin;
        internal float Heading, Radius, Width, Length;
        internal DateTime Until;
    }

    private readonly Dictionary<(uint, uint), Hazard> hazards = new();
    private readonly HashSet<uint> seenDivers = new();
    private bool active;
    private bool moving;
    private bool leased;
    private bool sideStepOwned;
    private bool oldSideStep;
    private uint headId;
    private uint converterId;
    private float headZ;
    private DateTime nextInteract;
    private DateTime nextLog;
    private MovementGate movementGate;
    private readonly WhorleaterHardPolicy.ConverterAssignment assignment = new();
    private uint assignedMember;
    private readonly HashSet<uint> darknessCasters = new();
    private bool staging;
    private string positioningStage;

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)281;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        movementGate = new MovementGate(this);
        // The ship is rectangular. A half-yalm inset retains corner escape space; a circular
        // boundary would erase it. Geometry is delegated to native RB avoidance, never a second mover.
        AvoidanceHelpers.AddAvoidSquareDonut(() => active, 28, 39, 100, 100, () => [Vector3.Zero]);
        AvoidanceManager.AddAvoidLocation<Hazard>(() => active, h => h.Radius,
            h => h.Origin, () => hazards.Values.Where(h => h.Radius > 0 && h.Until > DateTime.UtcNow));
        AvoidanceManager.AddAvoidPolygon<Hazard>(condition: () => active,
            leashPointProducer: () => Vector3.Zero, leashRadius: 40,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: h => [new(h.Width / 2, h.Length), new(-h.Width / 2, h.Length),
                new(-h.Width / 2, -Margin), new(h.Width / 2, -Margin)],
            locationProducer: h => h.Origin,
            collectionProducer: () => hazards.Values.Where(h => h.Width > 0 && h.Until > DateTime.UtcNow),
            priority: AvoidancePriority.High);
        Logger.Information("[Whorleater] Handler registered.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        TreeRoot.OnStop -= OnStop;
        GameEvents.OnMapChanged -= OnMapChanged;
        Reset(true);
        RestoreSideStep();
        DetachMovement();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (Core.Player == null || !DutyManager.InInstance || WorldManager.ZoneId != 281)
        {
            Reset(true);
            RestoreSideStep();
            DetachMovement();
            return false;
        }
        if (CommonBehaviors.IsLoading || QuestLogManager.InCutscene || !Core.Player.IsAlive)
        {
            Reset(true);
            return false;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        var head = actors.FirstOrDefault(a => a.BaseId == Head && a.IsAlive);
        if (head == null)
        {
            Reset();
            RestoreSideStep();
            return false;
        }
        if (!sideStepOwned && SidestepPlugin != null)
        {
            oldSideStep = SidestepPlugin.Enabled;
            sideStepOwned = true;
            SidestepPlugin.Enabled = false;
        }
        if (!Core.Player.InCombat)
        {
            Reset();
            return false;
        }
        active = true;
        // A stopped/restarted run must not reuse a detached input gate.
        movementGate ??= new MovementGate(this);
        var now = DateTime.UtcNow;
        Snapshot(actors, head, now);
        UpdateTankCoordination(actors, head, now);
        var converter = GameObjectManager.GetObjectsOfType<EventObject>()
            .FirstOrDefault(e => e.IsValid && e.BaseId == Converter && e.IsVisible && e.IsTargetable);
        if (converter == null)
        {
            if (converterId != 0)
                Logger.Information("[Whorleater] Converter no longer operable; releasing interaction ownership.");
            converterId = assignedMember = 0;
            assignment.Reset();
            // When every authored opponent is submerged/unattackable, wait near the
            // deck center rather than pursuing the old off-deck head/tail coordinates.
            if (ShouldStage(actors, false))
                return await StageAtCenter(now);
            ReleaseMovement();
            return false;
        }
        if (converterId != converter.ObjectId)
        {
            converterId = converter.ObjectId;
            assignment.Reset();
            Logger.Information($"[Whorleater] Converter operable id={converterId:X}; location={converter.Location}; player={Core.Player.Location}.");
        }
        var members = PartyManager.VisibleMembers.Select(m => m.BattleCharacter).Append(Core.Player)
            .Where(m => m != null && m.IsValid && m.IsAlive && m.Location.Distance2D(Vector3.Zero) < 40)
            .DistinctBy(m => m.ObjectId)
            .Select(m => new WhorleaterHardPolicy.Member(m.ObjectId, m.Location.Distance2D(converter.Location))).ToArray();
        uint selected = assignment.Select(members, now);
        if (selected != assignedMember)
        {
            assignedMember = selected;
            Logger.Information($"[Whorleater] Converter assignee={selected:X}; self={Core.Player.ObjectId:X}; closest living member with progress-based fallback.");
        }
        if (selected != Core.Player.ObjectId)
        {
            if (ShouldStage(actors, true))
                return await StageAtCenter(now);
            ReleaseMovement();
            return false;
        }
        // Targetability is the encounter's activation signal. Do not guess a gauge offset or
        // rely on another party member eventually clicking. Native hazard escape always wins.
        SetPositioningStage("converter");
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            return false;
        }
        if (converter.Location.Distance2D(Core.Player.Location) > 3)
        {
            // Navigator owns the approach; never substitute a direct off-mesh crossing on failure.
            moving = true;
            var result = movementGate.Approach(converter.Location);
            if (now >= nextLog)
            {
                Logger.Information($"[Whorleater] Converter approach {result}; player={Core.Player.Location}.");
                nextLog = now.AddSeconds(3);
            }
            await Coroutine.Yield();
            return true;
        }
        StopOwned();
        if (now >= nextInteract && converter.IsValid && converter.IsTargetable)
        {
            converter.Interact();
            // A delayed/contested interaction is retryable. Reacquire the actor next tick and
            // retry at a bounded rate until targetability acknowledges activation or duty resets.
            nextInteract = now.AddSeconds(1);
            Logger.Information("[Whorleater] Converter interaction requested; awaiting targetability acknowledgment.");
        }
        return false; // Once positioned, keep healing and rotation scheduled during acknowledgment.
    }

    private static bool CombatReady(BattleCharacter actor) => actor.IsValid && actor.IsAlive
        && actor.IsVisible && actor.IsTargetable && actor.CanAttack;

    private static bool ShouldStage(BattleCharacter[] actors, bool converterAvailable)
    {
        bool adds = actors.Any(a => a.NpcId is 2552 or 2553 or 2554 && CombatReady(a));
        bool bosses = actors.Any(a => a.NpcId is 2550 or 2551 && CombatReady(a));
        return WhorleaterHardPolicy.ShouldStage(converterAvailable, adds, bosses);
    }

    private void SetPositioningStage(string stage)
    {
        if (positioningStage != stage)
        {
            // Clear only this navigator's path at an explicit semantic handoff; a
            // running native escape keeps its path and takes precedence below.
            movementGate?.BeginPositioning();
            positioningStage = stage;
            staging = false;
            Logger.Information($"[Whorleater] Positioning stage={stage}.");
        }
        leased = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Whorleater " + stage);
    }

    private async Task<bool> StageAtCenter(DateTime now)
    {
        SetPositioningStage("center staging");
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            return false;
        }
        // Gather near the party during shield and submerge phases, unless a known hazard
        // covers center. Arrive within 3y and resume beyond 5y to avoid small corrections.
        var center = Vector3.Zero;
        bool blocked = hazards.Values.Any(h => h.Until > now && Contains(h, center));
        if (blocked || !WhorleaterHardPolicy.TravelToCenter(Core.Player.Location.Distance2D(center), staging))
        {
            StopOwned();
            staging = true;
            return false;
        }
        staging = false;
        moving = true;
        var result = movementGate.Approach(center, "Whorleater center staging");
        if (now >= nextLog)
        {
            nextLog = now.AddSeconds(3);
            Logger.Information($"[Whorleater] Center staging {result}; player={Core.Player.Location}.");
        }
        await Coroutine.Yield();
        return true;
    }

    private static bool Contains(Hazard h, Vector3 point)
    {
        if (h.Radius > 0)
            return h.Origin.Distance2D(point) <= h.Radius;
        // Match the registered polygon's -heading rotation and half-yalm rear margin.
        float dx = point.X - h.Origin.X, dz = point.Z - h.Origin.Z;
        double forward = dx * Math.Sin(h.Heading) + dz * Math.Cos(h.Heading);
        double side = dx * Math.Cos(h.Heading) - dz * Math.Sin(h.Heading);
        return forward >= -Margin && forward <= h.Length && Math.Abs(side) <= h.Width / 2;
    }

    private void Snapshot(BattleCharacter[] actors, BattleCharacter head, DateTime now)
    {
        foreach (var key in hazards.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
            hazards.Remove(key);
        // Darkness has no usable omen and its cone angle is unverified. Keep pose capture
        // behind the shared developer switch until there is enough evidence for avoidance.
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            var darkness = actors.Where(a => a.IsCasting && a.CastingSpellId == 1875).ToArray();
            darknessCasters.IntersectWith(darkness.Select(a => a.ObjectId));
            foreach (var caster in darkness)
                if (darknessCasters.Add(caster.ObjectId))
                    Logger.Information($"[Whorleater] Darkness capture caster={caster.ObjectId:X}; origin={caster.Location}; heading={caster.Heading:F3}; reach={caster.CombatReach:F2}; omenCenter={caster.OmenMatrix.Center}; player={Core.Player.Location}; remaining={caster.SpellCastInfo.RemainingCastTime.TotalMilliseconds:F0}ms; geometry unverified.");
        }
        foreach (var actor in actors.Where(a => a.IsCasting && a.SpellCastInfo.IsValid))
        {
            uint action = actor.CastingSpellId;
            if (action is not (Hydro or Fear or Fall))
                continue;
            // Dreadstorm's initial cast is six, its lingering puddle is five. All damage adds
            // a half-yalm margin. Stable envelopes retain native escape-path identities per cast.
            Put(actor.ObjectId, action, actor.SpellCastInfo.CastLocation, 0,
                (action == Fall ? 8 : action == Fear ? 6 : 5) + Margin, 0, 0,
                now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.35));
        }
        foreach (var puddle in GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(e => e.IsValid && e.IsVisible && (e.BaseId == HydroPuddle || e.BaseId == FearPuddle)))
            Put(puddle.ObjectId, puddle.BaseId, puddle.Location, 0, 5 + Margin, 0, 0, now.AddSeconds(0.25));

        // The head changes Z before Body Slam. Ignore first observation and tiny float jitter;
        // snapshot its pose instead of following the animation. The 3s lifetime covers
        // the expected 2.6s impact plus 0.4s for delayed updates.
        if (headId == head.ObjectId && Math.Abs(head.Z - headZ) > 1 && Math.Abs(head.Z) > 1)
        {
            Put(head.ObjectId, Slam, head.Location, head.Heading, 0, 10 + 2 * Margin, 30 + Margin, now.AddSeconds(3));
            Logger.Information($"[Whorleater] Slam relocation {headZ:F2}->{head.Z:F2}; origin={head.Location}; heading={head.Heading:F3}.");
        }
        headId = head.ObjectId;
        headZ = head.Z;
        // Uncast helpers need not be exposed as BattleCharacter wrappers. Discover the
        // base ID across the object table rather than filtering by wrapper type.
        var divers = GameObjectManager.GameObjects.Where(a => a.IsValid && a.BaseId == DiveHelper).ToArray();
        seenDivers.IntersectWith(divers.Select(a => a.ObjectId));
        foreach (var diver in divers)
            if (seenDivers.Add(diver.ObjectId))
            {
                // Retain the helper's initial pose through its expected 1.4s impact,
                // plus 0.2s for delayed updates. Water-column prediction is not implemented.
                Put(diver.ObjectId, Dive, diver.Location, diver.Heading, 0, 16 + 2 * Margin, 46 + Margin, now.AddSeconds(1.6));
                Logger.Information($"[Whorleater] Dive helper={diver.ObjectId:X}; type={diver.GetType().Name}; origin={diver.Location}; heading={diver.Heading:F3}.");
            }
    }

    private void Put(uint owner, uint action, Vector3 origin, float heading, float radius, float width, float length, DateTime until)
    {
        if (!hazards.TryGetValue((owner, action), out var h))
            hazards[(owner, action)] = h = new();
        h.Owner = owner;
        h.Action = action;
        h.Origin = origin;
        h.Heading = heading;
        h.Radius = radius;
        h.Width = width;
        h.Length = length;
        h.Until = until;
    }
    private void StopOwned()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
            movementGate?.Brake();
        moving = false;
    }
    private void ReleaseMovement()
    {
        StopOwned();
        if (leased)
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Whorleater converter released");
        leased = false;
        staging = false;
        positioningStage = null;
    }
    private void Reset(bool resetTanks = false)
    {
        // Submerge/combat gaps release borrowed services but retain logical tank
        // sides. Death, stop, map exit and instance teardown discard the assignment.
        ReleaseTankCoordination(resetTanks);
        active = false;
        ReleaseMovement();
        hazards.Clear();
        seenDivers.Clear();
        darknessCasters.Clear();
        assignment.Reset();
        headId = converterId = assignedMember = 0;
        headZ = 0;
        nextInteract = nextLog = default;
    }
    private void RestoreSideStep()
    {
        if (sideStepOwned && SidestepPlugin != null)
            SidestepPlugin.Enabled = oldSideStep;
        sideStepOwned = false;
    }
    private void DetachMovement()
    {
        movementGate?.Detach();
        movementGate = null;
    }
    private void OnStop(BotBase bot)
    {
        Reset(true);
        RestoreSideStep();
        DetachMovement();
    }
    private void OnMapChanged(object sender, EventArgs e)
    {
        if (WorldManager.ZoneId != 281)
        {
            Reset(true);
            RestoreSideStep();
            DetachMovement();
        }
    }
}
