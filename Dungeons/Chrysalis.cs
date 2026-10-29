using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using P = DutyMechanic.Dungeons.ChrysalisPolicy;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Alternates dark aether pickups and delays portal entry until Blight expires.
/// The profile owns both rooms and their targets; native avoidance owns cast dodges.
/// </summary>
public sealed class Chrysalis : AbstractDungeon
{
    // Isten applies physical vulnerability; Salas applies magical vulnerability.
    // Sina spawns a sprite without changing either status.
    private const uint Nabriales = 0xE41;
    private const uint Isten = 0xE43;
    private const uint Salas = 0xE45;
    private const uint PhysicalVulnerability = 657;
    private const uint MagicalVulnerability = 658;
    private const uint Blight = 659;
    private const uint Extend = 660;
    private static readonly Vector3 RiftCenter = new(220, 0, 0);
    // Keep destinations inside the observed 25-yalm floor. The 24-yalm limit
    // leaves clearance at the edge; it is not an exact wall measurement.
    private const float TravelRadius = 24;
    private static readonly CapabilityFlags[] PositionCapabilities = [CapabilityFlags.Movement, CapabilityFlags.GapCloser, CapabilityFlags.MoveBehind];
    private static Chrysalis current;
    private ChrysalisMovement movement;
    private bool active;
    private bool positioning;
    private bool travelling;
    private bool lostMover;
    private uint orbTarget;
    private bool previousPhysical;
    private bool previousMagical;
    private DateTime healUntil;
    private Vector3? portalEdge;
    private string stage;
    private DateTime nextCapture;
    private readonly Dictionary<uint, string> objects = [];
    private readonly HashSet<uint> resolvedOrbs = [];
    private readonly Dictionary<uint, P.Tower> towers = [];
    private readonly HashSet<uint> resolvedTowers = [];
    private readonly Dictionary<uint, Vector3> meteorHelpers = [];
    private uint towerTarget;
    private readonly P.RoomGeometry roomGeometry = new();
    private DateTime blockedEscapeSince;
    private DateTime nextGeometryRetry;

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)426;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [3432];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [3437];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        // Orb bursts are instant. Position-based avoids prevent a combat-range goal
        // toward the orb. Add 2.5y to the 5/8y damage radii: the orbs move about
        // 2y/s, and a half-yalm margin proved too short for escape startup.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => active && Core.Me.HasAura(PhysicalVulnerability), () => Vector3.Zero, 25, _ => 7.5f, point => point, () => ForbiddenOrbs(Isten));
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => active && Core.Me.HasAura(MagicalVulnerability), () => Vector3.Zero, 25, _ => 10.5f, point => point, () => ForbiddenOrbs(Salas));
        Logger.Information("[Chrysalis] Handler registered: alternating orbs, Blight delay and room positioning.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    /// <summary>Releases borrowed input and subscriptions on stop, disable or handler replacement.</summary>
    public static void ResetActive()
    {
        if (current == null)
        {
            return;
        }

        TreeRoot.OnStop -= current.OnStop;
        GameEvents.OnMapChanged -= current.OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse -= current.OnPulse;
        current.Reset();
        LoggingHelpers.ClearActorSignalWatch("Chrysalis");
        current.objects.Clear();
        current = null;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        Update();
        if (!active)
        {
            return false;
        }

        await TankBusterSpells();
        await DamageMitigationSpells();
        // Once positioned, let the routine heal and attack. Pulses continue updating
        // moving orbs while a routine cast is awaiting completion.
        return travelling && !AvoidanceManager.IsRunningOutOfAvoid;
    }

    private void OnPulse(object sender, EventArgs args) => Update();
    private void OnStop(BotBase bot) => ResetActive();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != 426)
        {
            ResetActive();
        }
    }

    private void Update()
    {
        if (!TreeRoot.IsRunning || Core.Me == null || !Core.Me.IsAlive || WorldManager.ZoneId != 426 || !DutyManager.InInstance || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            Reset();
            return;
        }

        Capture();
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(actor => actor.IsValid && actor.IsAlive).ToArray();
        var boss = actors.FirstOrDefault(actor => actor.BaseId == Nabriales);
        // Nabriales leaves the object table before the party enters the rift. Status
        // and the live portal keep the transition alive without a combat target.
        active = P.EncounterActive(boss != null, boss?.CurrentHealthPercent ?? 100, Core.Me.InCombat, Core.Me.HasAura(Blight), Core.Me.HasAura(Extend), portalEdge.HasValue && actors.Any(actor => actor.BaseId == 0xE47));
        if (!active)
        {
            Reset();
            return;
        }

        if (movement != null && !movement.IsCurrent)
        {
            Release();
            movement.Detach();
            movement = null;
            lostMover = true;
            Logger.Information("[Chrysalis] Movement owner replaced; yielding until encounter reset.");
        }

        if (lostMover)
        {
            return;
        }

        if (movement == null)
        {
            // Only this captured host mover has a verified exclusive handoff. Never
            // wrap an unknown controller or fight a successor for global ownership.
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover) || Navigator.NavigationProvider == null)
            {
                return;
            }

            movement = new ChrysalisMovement(Navigator.PlayerMover);
            Navigator.PlayerMover = movement;
        }

        RefreshRoomGeometry();
        bool rift = Core.Me.HasAura(Extend);
        Vector3 center = rift ? RiftCenter : Vector3.Zero;
        if (Core.Me.Location.Distance2D(center) > 30)
        {
            // Position and status can update on adjacent frames at the teleport.
            // Do not send an old room's request across the 220y separation.
            Hold(Core.Me.Location, 0.5f, "Waiting for room transition");
            return;
        }

        if (rift)
        {
            portalEdge = null;
            orbTarget = 0;
            TrackTowers(actors);
            var partyInRift = PartyManager.VisibleMembers.Select(member => member.BattleCharacter).Append(Core.Me).Where(member => member != null && member.IsValid && member.IsAlive).DistinctBy(member => member.ObjectId).ToArray();
            var tower = P.ChooseTower(
                Core.Me.ObjectId, ToPoint(Core.Me.Location), IsTank(Core.Me),
                partyInRift.Select(member => new P.Member(member.ObjectId, ToPoint(member.Location), IsTank(member))).ToArray(),
                towers.Values.ToArray(), towerTarget, Now, 0.5f,
                point => SafeApproach(ToWorld(point), RiftCenter));
            towerTarget = tower?.Id ?? 0;
            if (tower != null)
            {
                Hold(ToWorld(tower.Position), 0.5f, $"Soaking meteor {tower.Id:X}");
            }
            else
            {
                if (IsHealer(Core.Me))
                {
                    Hold(RiftCenter, 3, "Healing from meteor room center");
                }
                else
                {
                    Release();
                }
            }

            return;
        }

        towers.Clear();
        resolvedTowers.Clear();
        meteorHelpers.Clear();
        towerTarget = 0;
        if (Core.Me.HasAura(Blight))
        {
            // Choose an outward ray once. Recomputing it during the pull creates
            // sideways jitter; the inset endpoint also avoids endless wall input.
            if (!portalEdge.HasValue)
            {
                portalEdge = ToWorld(P.PortalWaitPoint(ToPoint(Core.Me.Location), TravelRadius));
            }

            Hold(portalEdge.Value, 0.7f, "Waiting outside portal for Blight");
            return;
        }

        if (portalEdge.HasValue)
        {
            if (boss == null || !boss.IsTargetable)
            {
                Hold(Vector3.Zero, 1, "Entering portal after Blight");
                return;
            }

            // A canceled/reset transition must not retain a center hold into combat.
            portalEdge = null;
        }

        bool physical = Core.Me.HasAura(PhysicalVulnerability);
        bool magical = Core.Me.HasAura(MagicalVulnerability);
        if (physical != previousPhysical || magical != previousMagical)
        {
            orbTarget = 0;
            // A healer gets an ordinary casting window after a pickup. Low party
            // health below also takes precedence over optional further collections.
            healUntil = DateTime.UtcNow.AddSeconds(2.5);
        }

        previousPhysical = physical;
        previousMagical = magical;
        var party = PartyManager.VisibleMembers.Select(member => member.BattleCharacter).Append(Core.Me).Where(member => member != null && member.IsValid && member.IsAlive).DistinctBy(member => member.ObjectId).ToArray();
        // The vulnerability arrives before the orb wrapper becomes dead/despawns.
        // Its caster ID is a resolved burst, not a newly forbidden live orb. Without
        // this handoff native avoidance flees the pickup we just completed.
        foreach (var aura in party.SelectMany(member => member.Auras).Where(aura => aura.Id is PhysicalVulnerability or MagicalVulnerability))
        {
            resolvedOrbs.Add(aura.CasterId);
        }

        var orbs = actors.Where(actor => (actor.BaseId == Isten || actor.BaseId == Salas) && !resolvedOrbs.Contains(actor.ObjectId)).Select(actor => new P.Orb(actor.ObjectId, ToPoint(actor.Location), actor.BaseId == Isten ? P.OrbType.Physical : P.OrbType.Magical)).ToArray();
        if (orbs.Length > 0)
        {
            bool heal = IsHealer(Core.Me) && (DateTime.UtcNow < healUntil || party.Any(member => member.CurrentHealthPercent < 70));
            // 2.5y is a conservative approach/other-collector exclusion. It is not
            // the burst radius: those larger damage envelopes belong to avoidance.
            // Short local pickups leave time to heal and avoid chasing an orb
            // across the room while another party member reaches it first.
            var chosen = heal ? null : P.ChooseOrb(
                Core.Me.ObjectId,
                ToPoint(Core.Me.Location),
                party.Select(member => new P.Member(member.ObjectId, ToPoint(member.Location), false)).ToArray(),
                orbs, physical, magical, orbTarget, 2.5f,
                point => Core.Me.Location.Distance2D(ToWorld(point)) <= (IsHealer(Core.Me) ? 14 : 20)
                    && SafeApproach(ToWorld(point), Vector3.Zero));
            orbTarget = chosen?.Id ?? 0;
            if (chosen != null && !Core.Me.IsCasting)
            {
                Hold(ToWorld(chosen.Position), 0.5f, $"Collecting {chosen.Type} orb {chosen.Id:X}");
            }
            else
            {
                Hold(Core.Me.Location, 0.5f, heal ? "Healing during orb wave" : "Waiting for a safe orb");
            }

            return;
        }

        orbTarget = 0;
        if ((boss == null || !boss.IsTargetable) && !actors.Any(actor => actor.NpcId == 3292 && actor.IsTargetable))
        {
            Hold(new Vector3(0, 0, 9), 2, "Waiting for boss return");
        }
        else
        {
            Release();
        }
    }

    private static bool IsHealer(BattleCharacter actor) => actor.CurrentJob is ff14bot.Enums.ClassJobType.WhiteMage or ff14bot.Enums.ClassJobType.Scholar or ff14bot.Enums.ClassJobType.Astrologian or ff14bot.Enums.ClassJobType.Sage;
    private static bool IsTank(BattleCharacter actor) => actor.CurrentJob is ff14bot.Enums.ClassJobType.Paladin or ff14bot.Enums.ClassJobType.Warrior or ff14bot.Enums.ClassJobType.DarkKnight or ff14bot.Enums.ClassJobType.Gunbreaker;
    private static double Now => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;

    private void RefreshRoomGeometry()
    {
        // Both rooms share a territory. RB can cache the return floor before collision
        // loads, leaving End of Days unable to find a starting span. Refresh after
        // settling and retry stalled escapes without resetting a working path.
        bool escaping = AvoidanceManager.IsRunningOutOfAvoid;
        bool inHazard = AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location));
        var now = DateTime.UtcNow;
        if (!inHazard || escaping || MovementManager.GenerallyOutOfControl)
        {
            blockedEscapeSince = DateTime.MinValue;
        }
        else
        {
            if (blockedEscapeSince == DateTime.MinValue)
            {
                blockedEscapeSince = now;
            }
        }

        bool roomChanged = roomGeometry.Refresh(ToPoint(Core.Me.Location), Now, escaping);
        bool stalled = blockedEscapeSince != DateTime.MinValue && (now - blockedEscapeSince).TotalSeconds >= 0.4 && now >= nextGeometryRetry;
        if (roomChanged || stalled)
        {
            AvoidanceManager.ResetNavigation();
            nextGeometryRetry = now.AddSeconds(2);
            blockedEscapeSince = DateTime.MinValue;
            Logger.Information($"[Chrysalis] Refreshing room collision; transition={roomChanged}; stalledEscape={stalled}; player={Core.Me.Location}.");
        }

        // A suspended heal must not retain its cast through an established escape.
        // Native avoidance still selects and drives the path through our one owner.
        if (escaping && Core.Me.IsCasting)
        {
            ActionManager.StopCasting();
        }
    }

    private void TrackTowers(BattleCharacter[] actors)
    {
        // EObj 2005159 appears at the meteor center about 12 seconds before impact.
        // The fixed 2005198..2005208 objects remain visible throughout and their
        // authored interaction positions are offset from the VFX: never soak those.
        var markers = GameObjectManager.GetObjectsOfType<EventObject>().Where(actor => actor.IsValid && actor.IsVisible && actor.BaseId == 2005159 && actor.Location.Distance2D(RiftCenter) <= TravelRadius).ToArray();
        double now = Now;
        foreach (var marker in markers)
        {
            if (!towers.ContainsKey(marker.ObjectId) && !resolvedTowers.Contains(marker.ObjectId))
            {
                // Approach the center. 1.5y is a conservative occupancy core, not
                // a measured outer tower radius. Twelve seconds plus one sample is
                // a fallback bound; the resolving helper below normally releases it.
                towers[marker.ObjectId] = new P.Tower(marker.ObjectId, ToPoint(marker.Location), 1.5f, now + 12.5);
                Logger.Information($"[Chrysalis] Meteor appeared id={marker.ObjectId:X} position={marker.Location}.");
            }
        }

        foreach (var helper in actors.Where(actor => actor.BaseId == 0x1B2))
        {
            bool moved = !meteorHelpers.TryGetValue(helper.ObjectId, out var previous) || previous.Distance2D(helper.Location) > 0.1f;
            meteorHelpers[helper.ObjectId] = helper.Location;
            if (!moved || helper.Location.Distance2D(RiftCenter) < 2)
            {
                continue;
            }

            // The instant Comet helper relocates to the impact, coincident with
            // damage/first tear spawn. The visual object survives another ~2s.
            foreach (var hit in towers.Values.Where(tower => ToWorld(tower.Position).Distance2D(helper.Location) < 1).ToArray())
            {
                towers.Remove(hit.Id);
                resolvedTowers.Add(hit.Id);
                Logger.Information($"[Chrysalis] Meteor resolved id={hit.Id:X} helper={helper.ObjectId:X} position={helper.Location}.");
            }
        }

        foreach (var tower in towers.Values.Where(tower => tower.Finish <= now || markers.All(marker => marker.ObjectId != tower.Id)).ToArray())
        {
            towers.Remove(tower.Id);
            resolvedTowers.Add(tower.Id);
            Logger.Information($"[Chrysalis] Meteor retired id={tower.Id:X}; expired={tower.Finish <= now}.");
        }
    }

    private IEnumerable<Vector3> ForbiddenOrbs(uint type) => Core.Me == null || Core.Me.HasAura(Extend) ? [] : GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(actor => actor.IsValid && actor.IsAlive && actor.BaseId == type).Where(actor => !resolvedOrbs.Contains(actor.ObjectId)).Select(actor => actor.Location);
    private static Point ToPoint(Vector3 value) => new(value.X, value.Z);
    private static Vector3 ToWorld(Point value) => new(value.X, 0, value.Y);
    private static bool SafeApproach(Vector3 point, Vector3 center)
    {
        // Both endpoints lie on the observed open circular floor, so their segment
        // stays inside it. Sample native hazards at half-yalm spacing along the route.
        // Native escapes reached radius 26.4 on the floor. Allow inward recovery
        // from that band while keeping new destinations inside 24 yalms.
        if (point.Distance2D(center) > TravelRadius || Core.Me.Location.Distance2D(center) > 27)
        {
            return false;
        }

        int steps = Math.Max(1, (int)Math.Ceiling(Core.Me.Location.Distance2D(point) * 2));
        for (int i = 1; i <= steps; i++)
        {
            Vector3 sample = Core.Me.Location + (point - Core.Me.Location) * (i / (float)steps);
            if (AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(sample)))
            {
                return false;
            }
        }

        return true;
    }

    private void Hold(Vector3 point, float tolerance, string reason)
    {
        positioning = true;
        foreach (var capability in PositionCapabilities)
        {
            CapabilityManager.Update(CapabilityHandle, capability, () => positioning && active, reason);
        }

        bool safe = SafeApproach(point, Core.Me.HasAura(Extend) ? RiftCenter : Vector3.Zero);
        travelling = safe && Core.Me.Location.Distance2D(point) > tolerance;
        if (travelling && Core.Me.IsCasting)
        {
            ActionManager.StopCasting();
        }

        movement.Request(safe ? point : Core.Me.Location, tolerance);
        movement.Update();
        if (stage != reason)
        {
            Logger.Information($"[Chrysalis] {reason}; destination={point}; player={Core.Me.Location}; safe={safe}.");
            stage = reason;
        }
    }

    private void Release()
    {
        positioning = travelling = false;
        foreach (var capability in PositionCapabilities)
        {
            CapabilityManager.Clear(CapabilityHandle, capability, "Chrysalis positioning released");
        }

        movement?.Request(null, 0);
        movement?.Update();
        if (stage != null)
        {
            Logger.Information($"[Chrysalis] Released {stage}.");
        }

        stage = null;
    }

    private void Reset()
    {
        active = false;
        Release();
        movement?.Detach();
        movement = null;
        lostMover = false;
        portalEdge = null;
        orbTarget = 0;
        previousPhysical = previousMagical = false;
        resolvedOrbs.Clear();
        towers.Clear();
        resolvedTowers.Clear();
        meteorHelpers.Clear();
        towerTarget = 0;
        healUntil = DateTime.MinValue;
        roomGeometry.Reset();
        blockedEscapeSince = nextGeometryRetry = DateTime.MinValue;
    }

    private void Capture()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || DateTime.UtcNow < nextCapture)
        {
            return;
        }

        nextCapture = DateTime.UtcNow.AddMilliseconds(500);
        LoggingHelpers.LogActorSignalChanges("Chrysalis", actor => actor.IsNpc || actor.ObjectId == Core.Me.ObjectId || PartyManager.AllMembers.Any(member => member.ObjectId == actor.ObjectId));
        // Instant meteors need object-lifecycle evidence as well as cast records.
        // Keep this gated by the shared developer diagnostics switch.
        var present = GameObjectManager.GameObjects.Where(actor => actor.IsValid && actor is not BattleCharacter).ToArray();
        foreach (var actor in present)
        {
            string state = $"base={actor.BaseId:X} location={actor.Location} visible={actor.IsVisible} targetable={actor.IsTargetable}";
            if (!objects.TryGetValue(actor.ObjectId, out string previous) || previous != state)
            {
                objects[actor.ObjectId] = state;
                Logger.Information($"[Chrysalis] OBJECT id={actor.ObjectId:X} {state}.");
            }
        }

        foreach (uint id in objects.Keys.Where(id => present.All(actor => actor.ObjectId != id)).ToArray())
        {
            Logger.Information($"[Chrysalis] OBJECT_REMOVED id={id:X} {objects[id]}.");
            objects.Remove(id);
        }
    }
}
