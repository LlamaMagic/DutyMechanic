using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using ff14bot.Pathing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 67: Emanation.
/// </summary>
public class Emanation : AbstractDungeon
{
    private DateTime denialSafetyUntil;
    private static Emanation current;
    private ChrysalisMovement movement;
    private uint vrilOrbId;
    private uint deferredVrilOrbId;
    private DateTime deferredVrilUntil;
    private DateTime vrilReachedAt;
    private uint pallTarget;
    private DateTime pallUntil;
    private Vector3? pallDestination;
    private bool pallOwned;
    private DateTime nextPallRouteAttempt;
    private const string PallRoute = "Emanation Pall stack";
    // Live captures identify Pall cast 9361 and its target marker 62; Grace
    // and Beauty use player markers 107 and 109 respectively.
    private const uint PallOfLightAction = 9361, PallStackMarker = 62, GraceMarker = 107, BeautyMarker = 109;
    private const string BeautyRoute = "Emanation Beauty edge";
    private const string GraceRoute = "Emanation Grace edge";
    private bool graceOwned, graceHolding;
    private Vector3? graceDestination;
    private Vector3? failedGraceDestination;
    private DateTime failedGraceUntil;
    private DateTime nextGraceRouteAttempt;
    private bool beautyOwned;
    private bool beautyHolding;
    private Vector3? beautyDestination;
    private DateTime nextBeautyRouteAttempt;
    private DateTime nextVrilRouteAttempt;
    private const string VrilRoute = "Emanation Vril";
    private bool reportedUnsupportedMover;
    private bool sideStepBorrowed, previousSideStep;
    private bool encounterActive;
    private bool hadSelfBeautyMarker;
    private DateTime lastSelfBeautySeen;
    private Vector3? pendingBeautyPuddle;
    private DateTime pendingBeautyPuddleUntil;
    private readonly HashSet<uint> puddlesBeforeBeauty = [];
    private Vector3[] otherCrosses = [], otherCircles = [];
    // Grace's world-aligned cross is 8y wide and 40y long; Beauty's circle
    // is 7y. The extra half yalm is the normal damaging-avoid safety margin.
    private static readonly Vector2[] FlowerCross =
    [
        new(4.5f, 40.5f), new(4.5f, 4.5f), new(40.5f, 4.5f), new(40.5f, -4.5f),
        new(4.5f, -4.5f), new(4.5f, -40.5f), new(-4.5f, -40.5f), new(-4.5f, -4.5f),
        new(-40.5f, -4.5f), new(-40.5f, 4.5f), new(-4.5f, 4.5f), new(-4.5f, 40.5f),
    ];
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Emanation;

    private static BattleCharacter LakshmiTarget => GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(obj => obj.IsVisible && obj.IsTargetable && obj.NpcId == EnemyNpc.Lakshmi);

    private static BattleCharacter DreamingKshatriyTarget => GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(obj => obj.IsVisible && obj.IsTargetable && obj.NpcId == EnemyNpc.DreamingKshatriya);

    // Prefer the nearest visible orb so collection does not depend on object-table order.
    private static BattleCharacter VrilTarget => GameObjectManager.GetObjectsOfType<BattleCharacter>()
        .Where(obj => obj.IsValid && obj.IsVisible && obj.NpcId == EnemyNpc.Vril)
        .OrderBy(obj => obj.Location.Distance2D(Core.Me.Location)).FirstOrDefault();

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        // Nov 4 capture corroborated live party marker 107 for Grace and 109
        // for Beauty. Use detached positions so RB can pick an escape without
        // acquiring an unrelated party member as a navigation attraction.
        AvoidanceManager.AddAvoidPolygon<Vector3>(() => encounterActive, null, 60,
            _ => 0, _ => 1, _ => 15, _ => FlowerCross, p => p, () => otherCrosses, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Vector3>(() => encounterActive, () => Vector3.Zero,
            60, _ => 7.5f, p => p, () => otherCircles);


        // A self Beauty marker disappears at the impact, before the persistent
        // flame object becomes visible. Start escaping the fixed impact point now;
        // retire the prediction when the real puddle is available or after 3s.
        AvoidanceManager.AddAvoidLocation<Vector3>(() => encounterActive && pendingBeautyPuddle.HasValue &&
            DateTime.UtcNow < pendingBeautyPuddleUntil && !VisiblePuddleNear(pendingBeautyPuddle.Value),
            () => Vector3.Zero, 60, _ => 10.5f, p => p,
            () => pendingBeautyPuddle.HasValue ? new[] { pendingBeautyPuddle.Value } : Array.Empty<Vector3>());

        // Blue puddles of fire
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.Emanation,
            objectSelector: eo => eo.IsVisible && eo.NpcId is EnemyNpc.BlueCircle2 or EnemyNpc.BlueCircle1 or EnemyNpc.BlueCircle,
            // The common persistent voidzone reaches 10y; leave a half-yalm
            // margin so an early escape does not stop inside the growing flame.
            radiusProducer: eo => 10.5f,
            priority: AvoidancePriority.High));

        // Stay Close for Divine Denial
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.Emanation && !CanCollectNearbyVrilForDenial(),
            // The knockback lands after the cast bar disappears. Keep the
            // center fallback through that observed impact window.
            // Helpers share Lakshmi's NPC id; only the real boss may anchor
            // the delayed fallback or their overlapping donuts block all paths.
            objectSelector: c => c.IsValid && c.NpcId == EnemyNpc.Lakshmi && c.BaseId == 0x1E20 &&
                (c.CastingSpellId == EnemyAction.DivineDenialConstant || DateTime.UtcNow < denialSafetyUntil),
            outerRadius: 40.0f,
            innerRadius: 3.0F,
            priority: AvoidancePriority.Medium);

        // Blue puddles of fire
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.Emanation,
            objectSelector: eo => eo.IsVisible && eo.NpcId == EnemyNpc.BlueCircle2,
            radiusProducer: eo => 15.0f,
            priority: AvoidancePriority.High));

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => WorldManager.ZoneId == (uint)ZoneId.Emanation,
            () => ArenaCenter.Lakshmi,
            outerRadius: 90.0f,
            innerRadius: 17.0f,
            priority: AvoidancePriority.High);

        return false;
    }

    private bool VisiblePuddleNear(Vector3 center) => GameObjectManager.GetObjectsOfType<EventObject>()
        .Any(e => e.IsValid && e.IsVisible && !puddlesBeforeBeauty.Contains(e.ObjectId) &&
            e.NpcId is EnemyNpc.BlueCircle2 or EnemyNpc.BlueCircle1 or EnemyNpc.BlueCircle &&
            e.Location.Distance2D(center) < 0.25f);

    private bool CanCollectNearbyVrilForDenial()
    {
        // The Nov 4 capture reached Divine Denial with no charge while an orb
        // was 3.2y away; the center donut displaced the player before pickup.
        // Only yield that positioning avoid for a nearby live orb while enough
        // cast time remains to collect and activate Vril. Re-enable it on any
        // missing/changed signal so unrelated avoidance and late fallback remain.
        if (Core.Me == null || !Core.Me.IsAlive || Core.Me.HasAura(PlayerAura.Vril) ||
            ActionManager.CanCast(DutyManager.DutyAction1, Core.Me)) return false;
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.NpcId == EnemyNpc.Lakshmi && a.CastingSpellId == EnemyAction.DivineDenialConstant);
        var orb = GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .FirstOrDefault(a => a.IsValid && a.IsVisible && a.IsAlive && a.NpcId == EnemyNpc.Vril && a.ObjectId == vrilOrbId);
        return boss?.SpellCastInfo != null && boss.SpellCastInfo.RemainingCastTime.TotalSeconds >= 3 &&
            orb != null && orb.Location.Distance2D(Core.Me.Location) <= 7;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        UpdateMovement();
        var denialCasting = EnemyAction.DivineDenial.IsCasting();
        var denialBoss = denialCasting ? GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.NpcId == EnemyNpc.Lakshmi &&
                a.CastingSpellId == EnemyAction.DivineDenialConstant && a.SpellCastInfo != null) : null;
        if (denialBoss != null)
            denialSafetyUntil = DateTime.UtcNow + denialBoss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
        await FollowDodgeSpells();

        // Check the charge before navigation: awaiting an entire path can delay
        // activation after an orb is picked up until the knockback has resolved.
        bool canUseVril = ActionManager.CanCast(DutyManager.DutyAction1, Core.Me);
        bool shouldUseVril = !Core.Me.HasAura(PlayerAura.Vril) &&
            (denialCasting || DateTime.UtcNow < denialSafetyUntil ||
                DreamingKshatriyTarget == null && LakshmiTarget == null && Core.Player.InCombat);
        if (canUseVril && shouldUseVril)
        {
            ReleaseGraceMovement();
            ReleaseBeautyMovement();
            ReleasePallMovement();
            ReleaseVrilRoute();
            Logger.Information($"Using {DutyManager.DutyAction1.Name}");
            ActionManager.DoAction(DutyManager.DutyAction1, Core.Me);
            await Coroutine.Sleep(500);
            return false;
        }

        if (DateTime.UtcNow < denialSafetyUntil &&
            (denialBoss == null || denialBoss.SpellCastInfo.RemainingCastTime.TotalSeconds < 3))
        {
            ReleaseGraceMovement();
            ReleaseBeautyMovement();
            ReleasePallMovement();
            ReleaseVrilRoute();
            return false;
        }

        if (!denialCasting && PositionGraceAtEdge()) return false;
        ReleaseGraceMovement();
        if (!denialCasting && PositionBeautyAtEdge()) return false;
        ReleaseBeautyMovement();

        // The stack is positive positioning, not an avoidance shape. Resolve it
        // after any urgent Vril action and before ordinary orb collection.
        if (!denialCasting && PositionPallStack()) return false;
        ReleasePallMovement();

        if (!canUseVril && !Core.Me.HasAura(PlayerAura.Vril) && movement?.IsCurrent == true)
        {
            // Commit to one live orb, but retire a contested pickup promptly if
            // standing on it does not produce a charge. A short exclusion keeps
            // the same disappearing orb from immediately winning again.
            var orbs = GameObjectManager.GetObjectsOfType<BattleCharacter>()
                .Where(obj => obj.IsValid && obj.IsVisible && obj.IsAlive && obj.NpcId == EnemyNpc.Vril).ToArray();
            var orb = orbs.FirstOrDefault(obj => obj.ObjectId == vrilOrbId);
            if (orb == null) { vrilOrbId = 0; vrilReachedAt = default; }
            else if (orb.Location.Distance2D(Core.Me.Location) <= 1.5f)
            {
                if (vrilReachedAt == default) vrilReachedAt = DateTime.UtcNow;
                else if (DateTime.UtcNow - vrilReachedAt >= TimeSpan.FromSeconds(1))
                {
                    deferredVrilOrbId = orb.ObjectId;
                    deferredVrilUntil = DateTime.UtcNow.AddSeconds(2);
                    ReleaseVrilRoute();
                    vrilReachedAt = default;
                    orb = null;
                }
            }
            else vrilReachedAt = default;
            if (orb == null)
            {
                var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                    .Where(a => a != null && a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                        PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)).ToArray();
                orb = orbs.Where(o => o.ObjectId != deferredVrilOrbId || DateTime.UtcNow >= deferredVrilUntil)
                    .OrderBy(o => party.Any(a => a.Location.Distance2D(o.Location) + 1 < Core.Me.Location.Distance2D(o.Location)) ? 1 : 0)
                    .ThenBy(o => o.Location.Distance2D(Core.Me.Location)).FirstOrDefault();
            }
            if (orb != null)
            {
                vrilOrbId = orb.ObjectId;
                if (DateTime.UtcNow >= nextVrilRouteAttempt)
                {
                    movement.BeginNavigation(VrilRoute);
                    var result = movement.Navigate(orb.Location, 1.0f);
                    if (result is MoveResult.Failed or MoveResult.PathGenerationFailed)
                    {
                        // Give another live orb a chance after a failed path;
                        // the bounded exclusion permits recovery on a later retry.
                        deferredVrilOrbId = orb.ObjectId;
                        deferredVrilUntil = DateTime.UtcNow.AddSeconds(2);
                        ReleaseVrilRoute();
                        vrilReachedAt = default;
                        nextVrilRouteAttempt = DateTime.UtcNow.AddSeconds(1);
                    }
                    else if (result is MoveResult.Done or MoveResult.ReachedDestination)
                    {
                        // Retire input, not the orb commitment: arrival can precede
                        // charge delivery or another player's competing pickup.
                        // Keeping the commitment also avoids toggling the center
                        // fallback while the bounded pickup timer is running.
                        movement.EndNavigation();
                        nextVrilRouteAttempt = DateTime.UtcNow.AddSeconds(1);
                    }
                }
            }
            else ReleaseVrilRoute();
        }
        else if (!canUseVril && !Core.Me.HasAura(PlayerAura.Vril) && reportedUnsupportedMover && VrilTarget is BattleCharacter fallbackOrb)
        {
            // Preserve the pre-existing route through the already installed owner
            // when it cannot safely be wrapped; do not introduce a second mover.
            await CommonTasks.MoveTo(new MoveToParameters(fallbackOrb.Location, "Vril")
            {
                DistanceTolerance = 1.0f,
                UseMount = false,
            });
        }
        else ReleaseVrilRoute();

        return false;
    }

    /// <summary>Maintains the encounter's native escape input on the bot-thread pulse.</summary>
    public static void CheckLifecycle() => current?.UpdateMovement();

    /// <summary>Releases only the input owner borrowed by this encounter.</summary>
    public static void ResetActive()
    {
        current?.ReleaseMovement();
        current = null;
    }

    private void UpdateMovement()
    {
        // The November 4 baseline ran across the arena after native escape completed
        // during Lakshmi's untargetable phase. Keep the same authoritative channel
        // through that phase so an old escape cannot leave forward input held.
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)ZoneId.Emanation ||
            !DutyManager.InInstance || Core.Me == null || !Core.Me.IsAlive ||
            CommonBehaviors.IsLoading || !Core.Me.InCombat ||
            !GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a =>
                a.IsValid && a.IsAlive && a.NpcId == EnemyNpc.Lakshmi && a.BaseId == 0x1E20))
        {
            ReleaseMovement();
            return;
        }

        encounterActive = true;
        if (Core.Me.VfxContainer.IsValid)
        {
            bool selfBeauty = Core.Me.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == BeautyMarker);
            if (selfBeauty) lastSelfBeautySeen = DateTime.UtcNow;
            if (hadSelfBeautyMarker && !selfBeauty)
            {
                pendingBeautyPuddle = Core.Me.Location;
                puddlesBeforeBeauty.Clear();
                foreach (var puddle in GameObjectManager.GetObjectsOfType<EventObject>().Where(e => e.IsValid && e.IsVisible &&
                    e.NpcId is EnemyNpc.BlueCircle2 or EnemyNpc.BlueCircle1 or EnemyNpc.BlueCircle))
                    puddlesBeforeBeauty.Add(puddle.ObjectId);
                pendingBeautyPuddleUntil = DateTime.UtcNow.AddSeconds(3);
            }
            hadSelfBeautyMarker = selfBeauty;
        }
        // A brief invalid container must not erase the impact edge, but a long
        // outage cannot safely locate the impact at the player's later position.
        else if (DateTime.UtcNow - lastSelfBeautySeen > TimeSpan.FromSeconds(1)) hadSelfBeautyMarker = false;
        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a != null && a.IsValid && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) && a.VfxContainer.IsValid)
            .ToArray();
        otherCrosses = party.Where(a => a.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == GraceMarker))
            .Select(a => a.Location).ToArray();
        otherCircles = party.Where(a => a.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == BeautyMarker))
            .Select(a => a.Location).ToArray();

        if (movement != null && !movement.IsCurrent)
        {
            // A successor owns navigation; stale Pall cleanup must not end it.
            pallOwned = false;
            pallDestination = null;
            beautyOwned = false;
            beautyHolding = false;
            beautyDestination = null;
            graceOwned = graceHolding = false;
            graceDestination = null;
            movement.Detach();
            movement = null;
        }
        if (!sideStepBorrowed && SidestepPlugin != null)
        {
            // Player-centered flowers and Vril positioning require encounter-owned
            // semantics; generic boss telegraphs must not compete for this channel.
            previousSideStep = SidestepPlugin.Enabled;
            sideStepBorrowed = true;
            SidestepPlugin.Enabled = false;
        }
        if (movement == null && !reportedUnsupportedMover)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
            {
                reportedUnsupportedMover = true;
                Logger.Warning("[Emanation] Native escape cleanup unavailable: another mover owns input.");
                return;
            }
            movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: IsOrdinaryWaypointSafe);
            Navigator.PlayerMover = movement;
        }
        movement?.Update();
    }

    private bool IsOrdinaryWaypointSafe(Vector3 waypoint)
    {
        // A safe destination can still cross a marked player's moving flower.
        // This callback receives the actual next mover waypoint; the navigation
        // owner can still admit a different safe route or a later successor.
        var from = Core.Me.Location;
        foreach (var center in otherCrosses)
            if (SegmentHitsBox(from, waypoint, center, 4.5f, 40.5f) ||
                SegmentHitsBox(from, waypoint, center, 40.5f, 4.5f)) return false;
        foreach (var center in otherCircles)
        {
            float dx = waypoint.X - from.X, dz = waypoint.Z - from.Z;
            float length2 = dx * dx + dz * dz;
            float t = length2 <= 0.0001f ? 0 : Math.Clamp(((center.X - from.X) * dx + (center.Z - from.Z) * dz) / length2, 0, 1);
            float x = from.X + t * dx - center.X, z = from.Z + t * dz - center.Z;
            if (x * x + z * z <= 7.5f * 7.5f) return false;
        }
        // Mesh waypoints can differ from the selected destination and active
        // ground hazards can appear between stack-planning pulses. The owner
        // calls this guard again before forwarding ordinary input.
        int samples = Math.Max(1, (int)Math.Ceiling(from.Distance2D(waypoint) / 0.5f));
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            var point = new Vector3(from.X + (waypoint.X - from.X) * t, from.Y,
                from.Z + (waypoint.Z - from.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private static bool SegmentHitsBox(Vector3 from, Vector3 to, Vector3 center, float halfX, float halfZ)
    {
        float t0 = 0, t1 = 1;
        return ClipAxis(from.X - center.X, to.X - from.X, halfX, ref t0, ref t1) &&
            ClipAxis(from.Z - center.Z, to.Z - from.Z, halfZ, ref t0, ref t1);
    }

    private static bool ClipAxis(float start, float delta, float half, ref float t0, ref float t1)
    {
        if (Math.Abs(delta) < 0.0001f) return Math.Abs(start) <= half;
        float a = (-half - start) / delta, b = (half - start) / delta;
        if (a > b) (a, b) = (b, a);
        t0 = Math.Max(t0, a);
        t1 = Math.Min(t1, b);
        return t0 <= t1;
    }

    private bool PositionPallStack()
    {
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.NpcId == EnemyNpc.Lakshmi && a.CastingSpellId == PallOfLightAction && a.SpellCastInfo != null);
        if (boss != null)
        {
            uint target = boss.SpellCastInfo.TargetId;
            if (target != pallTarget) { ReleasePallMovement(); pallTarget = target; }
            // The Nov 4 marker persisted about 1.9 seconds beyond the cast.
            // Follow its disappearance, with a bounded fallback for invalid VFX.
            pallUntil = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3);
        }
        if (pallTarget == 0 || DateTime.UtcNow >= pallUntil)
        {
            ReleasePallMovement(); pallTarget = 0;
            return false;
        }
        if (movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleasePallMovement();
            return false;
        }

        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Concat(new BattleCharacter[] { Core.Me })
            .Where(a => a != null && a.IsValid && a.IsAlive && a.IsVisible &&
                (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
            .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
        var marked = party.FirstOrDefault(a => a.ObjectId == pallTarget);
        if (marked == null) { ReleasePallMovement(); return false; }
        if (marked.VfxContainer.IsValid)
        {
            bool hasMarker = marked.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == PallStackMarker);
            if (!hasMarker && boss == null)
            {
                // A seen marker ended, or the cast was interrupted before it appeared.
                ReleasePallMovement(); pallTarget = 0;
                return false;
            }
        }
        bool HasSpread(BattleCharacter a) => a.VfxContainer.IsValid &&
            a.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id is GraceMarker or BeautyMarker);
        // A player carrying a flower must not drag it into the stack.
        if (HasSpread(Core.Me)) { ReleasePallMovement(); return false; }
        bool self = marked.ObjectId == Core.Me.ObjectId;
        var participants = party.Where(a => a.ObjectId != Core.Me.ObjectId && !HasSpread(a))
            .Select(a => a.Location).ToArray();
        if (self && participants.Length == 0) { ReleasePallMovement(); return false; }

        // Pall is a 7y stack. Reserve 0.5y for the
        // hazard margin and 0.5y for arrival/target motion.
        int Coverage(Vector3 point) => self
            ? participants.Count(p => p.Distance2D(point) <= 6.0f)
            : point.Distance2D(marked.Location) <= 6.0f ? 1 : 0;
        bool Safe(Vector3 point) => point.Distance2D(ArenaCenter.Lakshmi) < 17.0f &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point));
        bool Reachable(Vector3 point)
        {
            var from = Core.Me.Location;
            if (!IsOrdinaryWaypointSafe(point)) return false;
            int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(point) / 0.5f));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                var sample = new Vector3(from.X + (point.X - from.X) * t, from.Y, from.Z + (point.Z - from.Z) * t);
                if (!Safe(sample)) return false;
            }
            return true;
        }
        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (var center in self ? participants : new[] { marked.Location })
        {
            candidates.Add(center);
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new Vector3(center.X + 5.5f * (float)Math.Sin(angle), Core.Me.Location.Y,
                    center.Z + 5.5f * (float)Math.Cos(angle)));
            }
        }
        var best = candidates.Where(p => Coverage(p) > 0 && Safe(p) && Reachable(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (!best.HasValue) { ReleasePallMovement(); return false; }
        if (!pallDestination.HasValue || Coverage(pallDestination.Value) < Coverage(best.Value) ||
            !Safe(pallDestination.Value) || !Reachable(pallDestination.Value)) pallDestination = best;
        if (!pallOwned) ReleaseVrilRoute();
        if (Core.Me.Location.Distance2D(pallDestination.Value) <= 0.5f)
        {
            // Arrival ends our input; combat remains schedulable, and a moving
            // target or hazard will cause a fresh validated approach if needed.
            if (pallOwned) { movement.EndNavigation(); pallOwned = false; }
            return true;
        }
        if (!pallOwned)
        {
            if (DateTime.UtcNow < nextPallRouteAttempt) return true;
            Logger.Information($"[Emanation] Pall stack target={pallTarget:X} self={self} destination={pallDestination.Value}.");
        }
        pallOwned = true;
        movement.BeginNavigation(PallRoute);
        var result = movement.Navigate(pallDestination.Value, 0.5f);
        if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.Done or MoveResult.ReachedDestination)
        {
            // A terminal route result must retire its own input even if the
            // player has not reached the tighter semantic position yet.
            ReleasePallMovement();
            nextPallRouteAttempt = DateTime.UtcNow.AddSeconds(1);
            return false;
        }
        return true;
    }

    private bool PositionGraceAtEdge()
    {
        if (!Core.Me.VfxContainer.IsValid || !Core.Me.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == GraceMarker) ||
            movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
            return false;

        var center = ArenaCenter.Lakshmi;
        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a != null && a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)).Select(a => a.Location).ToArray();
        // Grace's cross is world-aligned, so radial separation alone is not
        // enough: a party member must be clear of both arms at impact.
        int Hits(Vector3 p, float margin) => party.Count(a => Math.Abs(a.X - p.X) < margin || Math.Abs(a.Z - p.Z) < margin);
        var here = Core.Me.Location;
        if (here.Distance2D(center) >= 13.5f && here.Distance2D(center) < 16.0f && Hits(here, 5.0f) == 0 && IsOrdinaryWaypointSafe(here))
        {
            if (!graceHolding)
            {
                ReleaseBeautyMovement(); ReleasePallMovement(); ReleaseVrilRoute();
                if (graceOwned) movement.EndNavigation();
                movement.BeginNavigation(GraceRoute);
                graceOwned = graceHolding = true;
            }
            return true;
        }

        var candidates = Enumerable.Range(0, 64).Select(i =>
        {
            double angle = i * Math.PI / 32;
            return new Vector3(center.X + 14.5f * (float)Math.Sin(angle), here.Y,
                center.Z + 14.5f * (float)Math.Cos(angle));
        }).Where(p => (!failedGraceDestination.HasValue || DateTime.UtcNow >= failedGraceUntil ||
                p.Distance2D(failedGraceDestination.Value) > 0.5f) && IsOrdinaryWaypointSafe(p)).ToArray();
        if (candidates.Length == 0)
            return graceHolding && here.Distance2D(center) < 16 && IsOrdinaryWaypointSafe(here);
        // Prefer avoiding every party member; when that is impossible, choose
        // the least harmful improvement instead of forcing a worse cross.
        var best = candidates.OrderBy(p => Hits(p, 5.5f)).ThenBy(p => p.Distance2D(here)).First();
        int bestHits = Hits(best, 5.5f);
        if (bestHits > 0 && bestHits >= Hits(here, 5.0f))
            return graceHolding && here.Distance2D(center) < 16 && IsOrdinaryWaypointSafe(here);
        if (!graceDestination.HasValue || !IsOrdinaryWaypointSafe(graceDestination.Value) ||
            Hits(graceDestination.Value, 5.5f) > bestHits)
            graceDestination = best;
        if (!graceOwned) { ReleaseBeautyMovement(); ReleasePallMovement(); ReleaseVrilRoute(); }
        if (here.Distance2D(graceDestination.Value) <= 0.75f && Hits(here, 5.0f) <= bestHits)
        {
            if (!graceHolding)
            {
                if (graceOwned) movement.EndNavigation();
                movement.BeginNavigation(GraceRoute);
                graceOwned = graceHolding = true;
            }
            return true;
        }
        if (graceHolding) { movement.EndNavigation(); graceOwned = graceHolding = false; }
        if (!graceOwned && DateTime.UtcNow < nextGraceRouteAttempt) return true;
        graceOwned = true;
        movement.BeginNavigation(GraceRoute);
        var result = movement.Navigate(graceDestination.Value, 0.75f);
        if (result is MoveResult.Failed or MoveResult.PathGenerationFailed)
        {
            // A failed mesh destination should not monopolize the marker window.
            failedGraceDestination = graceDestination;
            failedGraceUntil = DateTime.UtcNow.AddSeconds(2);
            ReleaseGraceMovement();
            nextGraceRouteAttempt = DateTime.UtcNow.AddSeconds(1);
            return false;
        }
        if (result is MoveResult.Done or MoveResult.ReachedDestination)
        {
            ReleaseGraceMovement();
            nextGraceRouteAttempt = DateTime.UtcNow.AddSeconds(1);
            return false;
        }
        return true;
    }

    private void ReleaseGraceMovement()
    {
        if (graceOwned) movement?.EndNavigation();
        graceOwned = graceHolding = false;
        graceDestination = null;
    }

    private bool PositionBeautyAtEdge()
    {
        if (!Core.Me.VfxContainer.IsValid || !Core.Me.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == BeautyMarker) ||
            movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
            return false;

        var center = ArenaCenter.Lakshmi;
        // Keep circle baits near the boundary, but leave room for navigation and
        // preserve native hazard escapes and the urgent duty-action handoff.
        var candidates = Enumerable.Range(0, 32).Select(i =>
        {
            double angle = i * Math.PI / 16;
            return new Vector3(center.X + 14.5f * (float)Math.Sin(angle), Core.Me.Location.Y,
                center.Z + 14.5f * (float)Math.Cos(angle));
        }).Where(IsOrdinaryWaypointSafe).ToArray();
        var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a != null && a.IsValid && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)).Select(a => a.Location).ToArray();
        float Separation(Vector3 p) => party.Length == 0 ? 20 : party.Min(a => a.Distance2D(p));
        // Once safely placed at the edge, retain the same semantic owner with
        // no forward input. Otherwise combat repeatedly pulls the marked player
        // back toward the boss and the placement route reacquires each pulse.
        if (Core.Me.Location.Distance2D(center) >= 13.5f && Core.Me.Location.Distance2D(center) < 16.0f &&
            Separation(Core.Me.Location) >= 8.25f && IsOrdinaryWaypointSafe(Core.Me.Location))
        {
            if (!beautyHolding)
            {
                ReleasePallMovement();
                ReleaseVrilRoute();
                if (beautyOwned) movement.EndNavigation();
                movement.BeginNavigation(BeautyRoute);
                beautyOwned = beautyHolding = true;
            }
            return true;
        }
        // Reserve arrival tolerance plus room for a moving party member.
        candidates = candidates.Where(p => Separation(p) >= 9.0f).ToArray();
        if (candidates.Length == 0) return false;
        if (!beautyDestination.HasValue || !IsOrdinaryWaypointSafe(beautyDestination.Value) || Separation(beautyDestination.Value) < 9.0f)
            beautyDestination = candidates.OrderByDescending(p => Math.Min(Separation(p), 10))
                .ThenBy(p => p.Distance2D(Core.Me.Location)).First();
        if (!beautyOwned) { ReleasePallMovement(); ReleaseVrilRoute(); }
        if (Core.Me.Location.Distance2D(beautyDestination.Value) <= 0.75f && Separation(Core.Me.Location) >= 8.25f)
        {
            if (!beautyHolding)
            {
                if (beautyOwned) movement.EndNavigation();
                movement.BeginNavigation(BeautyRoute);
                beautyOwned = beautyHolding = true;
            }
            return true;
        }
        if (beautyHolding) { movement.EndNavigation(); beautyOwned = beautyHolding = false; }
        if (!beautyOwned && DateTime.UtcNow < nextBeautyRouteAttempt) return true;
        beautyOwned = true;
        movement.BeginNavigation(BeautyRoute);
        var result = movement.Navigate(beautyDestination.Value, 0.75f);
        if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.Done or MoveResult.ReachedDestination)
        {
            ReleaseBeautyMovement();
            nextBeautyRouteAttempt = DateTime.UtcNow.AddSeconds(1);
            return false;
        }
        return true;
    }

    private void ReleaseBeautyMovement()
    {
        if (beautyOwned) movement?.EndNavigation();
        beautyOwned = false;
        beautyHolding = false;
        beautyDestination = null;
    }

    private void ReleasePallMovement()
    {
        if (pallOwned) movement?.EndNavigation();
        pallOwned = false;
        pallDestination = null;
    }

    private void ReleaseVrilRoute()
    {
        movement?.EndNavigation();
        vrilOrbId = 0;
    }

    private void ReleaseMovement()
    {
        ReleaseGraceMovement();
        nextGraceRouteAttempt = default;
        failedGraceDestination = null;
        failedGraceUntil = default;
        ReleaseBeautyMovement();
        nextBeautyRouteAttempt = default;
        ReleasePallMovement();
        pallTarget = 0;
        pallUntil = default;

        nextPallRouteAttempt = default;
        ReleaseVrilRoute();
        movement?.Detach();
        movement = null;
        encounterActive = false;
        hadSelfBeautyMarker = false;
        lastSelfBeautySeen = default;
        pendingBeautyPuddle = null;
        pendingBeautyPuddleUntil = default;
        puddlesBeforeBeauty.Clear();
        otherCrosses = otherCircles = [];
        if (sideStepBorrowed && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = previousSideStep;
        sideStepBorrowed = false;
        reportedUnsupportedMover = false;
        nextVrilRouteAttempt = default;
        denialSafetyUntil = default;
        deferredVrilOrbId = 0;
        deferredVrilUntil = default;
        vrilReachedAt = default;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Boss: Lakshmi.
        /// </summary>
        public const uint Lakshmi = 6385;

        /// <summary>
        /// Dreaming Kshatriya.
        /// </summary>
        public const uint DreamingKshatriya = 6386;

        /// <summary>
        /// Vril.
        /// </summary>
        public const uint Vril = 6690;

        /// <summary>
        /// Blue Circle.
        /// </summary>
        public const uint BlueCircle = 2008940;

        /// <summary>
        /// Blue Circle 1.
        /// </summary>
        public const uint BlueCircle1 = 2008941;

        /// <summary>
        /// Blue Circle 2.
        /// </summary>
        public const uint BlueCircle2 = 2008942;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Lakshmi.
        /// </summary>
        public static readonly Vector3 Lakshmi = new(0f, 0f, 0f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// <see cref="EnemyNpc.Lakshmi"/>'s the Pull of Light.
        /// Tank buster
        ///
        /// </summary>
        public const uint thePullofLight = 9362;

        /// <summary>
        /// <see cref="EnemyNpc.Lakshmi"/>'s Hand of Beauty.
        ///
        ///
        /// </summary>
        public const uint HandofBeauty = 9351;

        /// <summary>
        /// <see cref="EnemyNpc.Lakshmi"/>'s Divine Denial.
        ///
        /// Large Pushback, need to use DutyAction to dodge
        /// </summary>
        internal static readonly HashSet<uint> DivineDenial = [9349];
        public const uint DivineDenialConstant = 9349;
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Seduced
        /// </summary>
        public const uint Seduced = 1389;

        /// <summary>
        /// Vril
        /// </summary>
        public const uint Vril = 1290;
    }
}
