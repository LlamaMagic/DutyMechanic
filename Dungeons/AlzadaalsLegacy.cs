using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using GreyMagic;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 90.2: Alzadaal's Legacy dungeon logic.
/// </summary>
/// <remarks>
/// Action IDs, AOE shapes and arena geometry are taken from BossMod's D09AlzadaalsLegacy modules.
/// </remarks>
public class AlzadaalsLegacy : AbstractDungeon
{
    /// <summary>
    /// Tuning diagnostics: logs every enemy cast, Ambujam's map effects and arena actors, and Spin Out steering samples
    /// (4 per second). Very chatty; enable only to capture data for tuning these mechanics.
    /// </summary>
    private static readonly bool VerboseDiagnostics = false;

    private const int TentacleDigDuration = 18_000;
    private const float FountainAvoidRadius = 8.5f;

    /// <summary>
    /// Power Serge is a 6s cast; the tethered Mana Explosions resolve ~11.5-12.3s after the tether appears.
    /// </summary>
    private const int PowerSergeDuration = 13_000;

    /// <summary>
    /// Predictions outlive their cast by a margin; the cast-end edge normally retires them first.
    /// </summary>
    private const int TentacleAoeLifetime = 14_000;

    private const float TentacleAoeRadius = 21.0f;

    /// <summary>
    /// Usable radius of Ambujam's 19.5 yalm arena when searching for safe spots.
    /// </summary>
    private const float AmbujamUsableRadius = 18.5f;

    private const int GravitonCannonDuration = 8_500;

    /// <summary>
    /// Forward input pulse while Spinning, matching Yuweyawata's Temporary Misdirection pulse.
    /// </summary>
    private static readonly TimeSpan SpinOutInputPulse = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Client forced-movement direction (the pointing hand). Same pattern Yuweyawata uses; read-only here.
    /// </summary>
    private const string ForcedMovementDirectionPattern =
        "Search F3 0F 11 0D ? ? ? ? 48 85 DB Add 4 TraceRelative";
    private const int RotaryGaleDuration = 5_000;

    private static readonly HashSet<uint> TentacleDig =
    [
        EnemyAction.TentacleDigA, EnemyAction.TentacleDigB, EnemyAction.TentacleDigC,
    ];

    private static readonly HashSet<uint> TentacleAoe =
    [
        EnemyAction.ToxinShower, EnemyAction.CorrosiveVenom,
    ];

    /// <summary>
    /// Map-effect slots that pulse (State -> 0x0001) right after Tentacle Dig: 0x10 = cardinal spots, 0x11 = diagonal spots.
    /// RB doesn't expose which spot, only that the set fired.
    /// </summary>
    private static readonly int[] TentacleSlots = [0x10, 0x11];

    /// <summary>
    /// The eight places a 21 yalm Toxin Shower / Corrosive Venom can land (BossMod, confirmed by cast logs).
    /// </summary>
    private static readonly Vector3[] TentacleSpots =
    [
        new(117f, 303f, -97f), new(131f, 303f, -83f), new(131f, 303f, -97f), new(117f, 303f, -83f),
        new(109f, 303f, -90f), new(139f, 303f, -90f), new(124f, 303f, -75f), new(124f, 303f, -105f),
    ];

    private static readonly HashSet<uint> ArticulatedBits = [EnemyAction.ArticulatedBits];

    private static readonly HashSet<uint> GravitonCannon = [EnemyAction.GravitonCannon];

    private static readonly HashSet<uint> PowerSerge = [EnemyAction.PowerSerge];

    private static readonly HashSet<uint> RotaryGale = [EnemyAction.RotaryGale];

    private static readonly HashSet<uint> MagnitudeOpus = [EnemyAction.MagnitudeOpus];

    private static readonly HashSet<uint> BastingBlade = [EnemyAction.BastingBlade];

    private static readonly HashSet<uint> KapikuluArenaShrink =
    [
        EnemyAction.BillowingBolts, EnemyAction.BorderChange,
    ];

    private readonly Stopwatch powerSergeSw = new();

    /// <summary>
    /// Tentacle AOEs predicted from arena actors, before their cast starts.
    /// </summary>
    private readonly List<(Vector3 Location, DateTime Expires)> pendingTentacleAoes = [];

    /// <summary>
    /// Last map-effect State seen per tentacle slot, so only transitions count as a pulse.
    /// </summary>
    private readonly Dictionary<int, ushort> lastTentacleSlotStates = [];

    private string lastAmbujamMapEffects = string.Empty;

    /// <summary>
    /// Non-party actors in Ambujam's arena, to learn whether tentacles surface before their cast.
    /// </summary>
    private readonly Dictionary<uint, (Vector3 Location, bool Visible, bool Targetable)> ambujamArenaActors = [];

    /// <summary>
    /// Tentacle AOE locations casting last tick, to retire predictions when the cast resolves.
    /// </summary>
    private Vector3[] lastTentacleCastLocations = [];

    private Vector3[] fountainAvoidCache = [];
    private DateTime fountainAvoidCacheAt = DateTime.MinValue;

    /// <summary>
    /// Enemy casts already logged, keyed by caster and spell, so each cast is logged once.
    /// </summary>
    private readonly Dictionary<(uint ObjectId, uint SpellId), DateTime> loggedEnemyCasts = [];

    private DateTime tentacleDigEnds = DateTime.MinValue;
    private DateTime lastChariotNoSafeSpotLog = DateTime.MinValue;

    /// <summary>
    /// When each Armored Chariot cast was first seen at 0ms; finished casts keep reporting for several seconds.
    /// </summary>
    private readonly Dictionary<uint, DateTime> chariotCastsAtZero = [];
    private DateTime powerSergeEnds = DateTime.MinValue;

    /// <summary>
    /// Kapikulu's arena shrinks from 39x49 to 30x40 after the first Billowing Bolts / Border Change.
    /// </summary>
    private bool kapikuluArenaShrunk;

    private bool wasSpinning;

    /// <summary>
    /// SideStep's state before Spin Out paused it, restored when Spinning ends.
    /// </summary>
    private bool sideStepWasEnabledBeforeSpin;

    /// <summary>
    /// Spike trap helper positions from Traps casts; the spikes outlast the cast, so they're kept for a while.
    /// </summary>
    private readonly Dictionary<Vector3, DateTime> kapikuluTraps = [];

    private DateTime lastSpinLog = DateTime.MinValue;
    private Vector3? lastSpinLogLocation;
    private bool forcedDirectionResolved;
    private IntPtr forcedDirectionAddress = IntPtr.Zero;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.AlzadaalsLegacy;

    /// <inheritdoc/>
    /// <remarks>
    /// Multi-wave mechanics outlive their cast bars, so follow-dodging is timed per boss instead.
    /// </remarks>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } =
    [
        EnemyAction.RailCannon,
        EnemyAction.CrewelSlice,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } =
    [
        EnemyAction.BigWave,
        EnemyAction.DiffusionRay,
        EnemyAction.BillowingBolts,
    ];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        kapikuluArenaShrunk = false;

        // Boss 1 (Ambujam): Toxic / Corrosive Fountain 8 yalm circles, soonest first.
        // Overlapping waves can cover the whole arena, so later-resolving fountains are dropped until a safe spot exists.
        AvoidanceManager.AddAvoidLocation(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.UnderseaEntrance,
            radiusProducer: location => FountainAvoidRadius,
            locationProducer: location => location,
            collectionProducer: ActiveFountains);

        // Boss 1 (Ambujam): Toxin Shower / Corrosive Venom predicted from arena actors, before the cast
        AvoidanceManager.AddAvoidLocation(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.UnderseaEntrance,
            radiusProducer: location => TentacleAoeRadius + 0.5f,
            locationProducer: location => location,
            collectionProducer: ActiveTentacleAoes);

        // Boss 1 (Ambujam): Toxin Shower / Corrosive Venom cast-time backstop (2.5s cast); the handler steers the escape itself
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.UnderseaEntrance,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.ToxinShower or EnemyAction.CorrosiveVenom,
            radiusProducer: bc => TentacleAoeRadius + 0.5f);

        // Boss 2 (Armored Chariot): Articulated Bits leaves a 6 yalm void zone at arena center
        AvoidanceManager.AddAvoidLocation(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThresholdOfBounty
                && (ArticulatedBits.IsCasting() || GameObjectManager.GameObjects.Any(o => o.NpcId == EnemyNpc.ArticulatedBitsVoidzone)),
            radius: 6.5f,
            locationProducer: () => ArenaCenter.ArmoredChariot);

        // Boss 3 (Kapikulu): Basting Blade, 15 wide line in front of the boss
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WeaversWarding && !Core.Player.HasAura(PartyAura.Spinning),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.BastingBlade,
            width: 15f,
            length: 60f,
            priority: AvoidancePriority.High);

        // Boss 3 (Kapikulu): Spike Traps, 6x6 squares in front of each helper
        // Cast-based only: the spikes stay out briefly after the cast bar ends.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WeaversWarding && !Core.Player.HasAura(PartyAura.Spinning),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Traps,
            width: 6f,
            length: 6f,
            priority: AvoidancePriority.Medium);

        // Boss 3 (Kapikulu): Mana Explosion 15 yalm circles (backstop for the Power Serge follow)
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WeaversWarding && !Core.Player.HasAura(PartyAura.Spinning),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.ManaExplosion,
            radiusProducer: bc => 15.0f);

        // Boss 3 (Kapikulu): Border Change, 5 deep strips along the arena edges
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WeaversWarding && !Core.Player.HasAura(PartyAura.Spinning),
            objectSelector: bc => bc.CastingSpellId == EnemyAction.BorderChange,
            width: 40f,
            length: 5f,
            priority: AvoidancePriority.High);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.UnderseaEntrance,
            () => ArenaCenter.Ambujam,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheThresholdOfBounty,
            innerWidth: 38.0f,
            innerHeight: 38.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.ArmoredChariot],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WeaversWarding && !Core.Player.HasAura(PartyAura.Spinning) && !kapikuluArenaShrunk,
            innerWidth: 38.0f,
            innerHeight: 48.0f,
            outerWidth: 90.0f,
            outerHeight: 100.0f,
            collectionProducer: () => [ArenaCenter.Kapikulu],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WeaversWarding && !Core.Player.HasAura(PartyAura.Spinning) && kapikuluArenaShrunk,
            innerWidth: 29.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 100.0f,
            collectionProducer: () => [ArenaCenter.Kapikulu],
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        _ = await TankBusterSpells();
        _ = await DamageMitigationSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        if (VerboseDiagnostics && currentSubZoneId is SubZoneId.UnderseaEntrance or SubZoneId.TheThresholdOfBounty or SubZoneId.WeaversWarding)
        {
            LogEnemyCasts();
        }

        return currentSubZoneId switch
        {
            SubZoneId.UnderseaEntrance => await HandleAmbujamAsync(),
            SubZoneId.TheThresholdOfBounty => await HandleArmoredChariotAsync(),
            SubZoneId.WeaversWarding => await HandleKapikuluAsync(),
            _ => false,
        };
    }

    /// <summary>
    /// Logs each enemy cast once during boss fights so a failed dodge can be matched to the exact spell and position.
    /// </summary>
    private void LogEnemyCasts()
    {
        if (!Core.Me.InCombat)
        {
            loggedEnemyCasts.Clear();
            return;
        }

        foreach (BattleCharacter bc in GameObjectManager.GetObjectsOfType<BattleCharacter>())
        {
            if (bc.IsMe || !bc.IsCasting || bc.CastingSpellId == 0
                || PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)bc.NpcId))
            {
                continue;
            }

            (uint, uint) key = (bc.ObjectId, bc.CastingSpellId);
            if (loggedEnemyCasts.TryGetValue(key, out DateTime loggedAt) && DateTime.Now - loggedAt < TimeSpan.FromSeconds(10))
            {
                continue;
            }

            loggedEnemyCasts[key] = DateTime.Now;
            SpellCastInfo spell = bc.SpellCastInfo;
            Logger.Information($"[Alzadaal] Cast ({spell.ActionId}) {spell.Name} by ({bc.NpcId}) {bc.Name} at {bc.Location} "
                + $"heading {bc.Heading:N2}, {spell.RemainingCastTime.TotalMilliseconds:N0}ms left, player at {Core.Me.Location} ({Core.Me.Distance2D(bc):N1}y away)");
        }
    }

    private async Task<bool> HandleAmbujamAsync()
    {
        if (!Core.Me.InCombat)
        {
            pendingTentacleAoes.Clear();
            lastTentacleSlotStates.Clear();
            lastAmbujamMapEffects = string.Empty;
            ambujamArenaActors.Clear();
            lastTentacleCastLocations = [];
            tentacleDigEnds = DateTime.MinValue;
            return false;
        }

        if (TentacleDig.IsCasting() && tentacleDigEnds < DateTime.Now)
        {
            ArmTentacleWindow("Tentacle Dig cast");
        }

        UpdateTentacleSlots();
        TrackAmbujamArenaActors();

        // Retire predictions once the cast at that spot has resolved.
        Vector3[] castingNow = [.. GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .Where(bc => TentacleAoe.Contains(bc.CastingSpellId))
            .Select(bc => bc.Location)];
        foreach (Vector3 resolved in lastTentacleCastLocations.Where(l => !castingNow.Any(c => c.Distance2D(l) < 2f)))
        {
            pendingTentacleAoes.RemoveAll(aoe => aoe.Location.Distance2D(resolved) < 2f);
        }

        lastTentacleCastLocations = castingNow;
        pendingTentacleAoes.RemoveAll(aoe => aoe.Expires < DateTime.Now);

        // Escape: steer straight to the nearest safe spot instead of leaving a thin crescent to the avoidance pather.
        Vector3[] threats = [.. castingNow.Concat(ActiveTentacleAoes())
            .GroupBy(l => TentacleSpots.OrderBy(s => s.Distance2D(l)).First())
            .Select(g => g.First())];
        if (threats.Length > 0)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Dodging Toxin Shower / Corrosive Venom");

            if (threats.Any(t => Core.Me.Distance2D(t) < TentacleAoeRadius + 0.5f))
            {
                Vector3? safe = NearestSafeSpot(threats, TentacleAoeRadius + 1.0f) ?? NearestSafeSpot([threats[0]], TentacleAoeRadius + 1.0f);
                if (safe.HasValue)
                {
                    if (castingNow.Length > 0 && ActionManager.IsSprintReady)
                    {
                        ActionManager.Sprint();
                    }

                    Navigator.PlayerMover.MoveTowards(safe.Value);
                }
            }
            else if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            return false;
        }

        // Between Tentacle Dig and the reveal, wait at the arena center: it is the spot with the shortest worst-case
        // escape to every possible tentacle (<= 12 yalms for one tentacle, ~14 for two).
        if (DateTime.Now < tentacleDigEnds)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Waiting at center for tentacles");

            if (Core.Me.Distance2D(ArenaCenter.Ambujam) > 2.0f)
            {
                Navigator.PlayerMover.MoveTowards(ArenaCenter.Ambujam);
            }
            else if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }
        }

        return false;
    }

    private void ArmTentacleWindow(string reason)
    {
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TentacleDigDuration, $"Dodging Tentacle Dig / Toxin Shower / Corrosive Venom");
        tentacleDigEnds = DateTime.Now.AddMilliseconds(TentacleDigDuration);
        Logger.Information($"[Ambujam] Tentacle window armed by {reason}.");
    }

    /// <summary>
    /// Logs Ambujam's map effects on change and arms the tentacle window when slot 0x10 / 0x11 pulses.
    /// </summary>
    private void UpdateTentacleSlots()
    {
        InstanceContentDirector instanceDirector = DirectorManager.ActiveDirector as InstanceContentDirector;
        if (instanceDirector == null || !instanceDirector.IsValid)
        {
            return;
        }

        MapEffect[] mapEffects = instanceDirector.MapEffects;

        if (VerboseDiagnostics)
        {
            string formatted = string.Join("; ", mapEffects.Select((effect, i) => FormattableString.Invariant(
                $"[{i:X2}] id=0x{effect.ID:X2} state=0x{effect.State:X4} flags=0x{effect.Flags:X2} unk=0x{effect.unk:X8}")));
            if (formatted != lastAmbujamMapEffects)
            {
                Logger.Information($"[Ambujam] Map effects changed: {formatted}");
                lastAmbujamMapEffects = formatted;
            }
        }

        foreach (int slot in TentacleSlots)
        {
            if (slot >= mapEffects.Length)
            {
                continue;
            }

            ushort state = mapEffects[slot].State;
            bool seenBefore = lastTentacleSlotStates.TryGetValue(slot, out ushort lastState);
            lastTentacleSlotStates[slot] = state;

            // The first reading only establishes a baseline, so a stale state left over from a wipe isn't a pulse.
            if (seenBefore && lastState != state && state == 0x0001)
            {
                string set = slot == 0x10 ? "cardinal" : "diagonal";
                if (tentacleDigEnds < DateTime.Now)
                {
                    ArmTentacleWindow($"map effect slot 0x{slot:X2} ({set})");
                }
                else
                {
                    Logger.Information($"[Ambujam] Tentacle set fired: slot 0x{slot:X2} ({set}).");
                }
            }
        }
    }

    /// <summary>
    /// Logs every non-party actor in Ambujam's arena as it appears, moves or changes visibility, and predicts a
    /// tentacle AOE when an actor newly appears or moves onto one of the eight tentacle spots during a dig window.
    /// </summary>
    private void TrackAmbujamArenaActors()
    {
        foreach (BattleCharacter bc in GameObjectManager.GetObjectsOfType<BattleCharacter>())
        {
            if (bc.IsMe || PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)bc.NpcId)
                || bc.Distance2D(ArenaCenter.Ambujam) > 25f)
            {
                continue;
            }

            (Vector3 Location, bool Visible, bool Targetable) now = (bc.Location, bc.IsVisible, bc.IsTargetable);
            bool known = ambujamArenaActors.TryGetValue(bc.ObjectId, out (Vector3 Location, bool Visible, bool Targetable) before);
            bool moved = !known || before.Location.Distance2D(now.Location) > 1f;

            if (!known || moved || before.Visible != now.Visible || before.Targetable != now.Targetable)
            {
                if (VerboseDiagnostics)
                {
                    Logger.Information($"[Ambujam] Actor {(known ? "changed" : "seen")}: ({bc.NpcId}) {bc.Name} obj={bc.ObjectId:X} at {bc.Location} "
                        + $"visible={bc.IsVisible} targetable={bc.IsTargetable} casting={bc.CastingSpellId}");
                }

                ambujamArenaActors[bc.ObjectId] = now;
            }

            // Only new or moved actors count, never mere presence, so parked helpers can't flood predictions.
            if (!moved || DateTime.Now >= tentacleDigEnds || (bc.NpcId == EnemyNpc.Ambujam && bc.IsTargetable))
            {
                continue;
            }

            Vector3 spot = TentacleSpots.OrderBy(s => s.Distance2D(bc.Location)).First();
            if (spot.Distance2D(bc.Location) < 2f && !pendingTentacleAoes.Any(aoe => aoe.Location.Distance2D(spot) < 2f))
            {
                Logger.Information($"[Ambujam] Tentacle AOE predicted at {spot} from ({bc.NpcId}) {bc.Name} obj={bc.ObjectId:X}.");
                pendingTentacleAoes.Add((spot, DateTime.Now.AddMilliseconds(TentacleAoeLifetime)));
            }
        }
    }

    /// <summary>
    /// Returns the predicted tentacle AOEs to avoid. When all of them together leave no standing room
    /// in the arena, only the next one to resolve is returned so avoidance never has zero safe spots.
    /// </summary>
    /// <returns>Centers of the AOEs to avoid.</returns>
    private Vector3[] ActiveTentacleAoes()
    {
        if (pendingTentacleAoes.Count == 0)
        {
            return [];
        }

        Vector3[] all = [.. pendingTentacleAoes.Select(aoe => aoe.Location)];
        if (all.Length == 1 || NearestSafeSpot(all, TentacleAoeRadius + 1.0f).HasValue)
        {
            return all;
        }

        return [all[0]];
    }

    /// <summary>
    /// Returns the fountains to avoid: all casting fountains in resolution order, dropping the latest-resolving
    /// ones until the rest leave a safe spot (BossMod shows the next ten for the same reason).
    /// </summary>
    /// <returns>Centers of the fountain circles to avoid.</returns>
    private Vector3[] ActiveFountains()
    {
        if (DateTime.Now - fountainAvoidCacheAt < TimeSpan.FromMilliseconds(200))
        {
            return fountainAvoidCache;
        }

        List<Vector3> fountains = [.. GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .Where(bc => bc.CastingSpellId is EnemyAction.ToxicFountain or EnemyAction.CorrosiveFountain)
            .OrderBy(bc => bc.SpellCastInfo.RemainingCastTime)
            .Select(bc => bc.Location)];

        while (fountains.Count > 1 && !NearestSafeSpot([.. fountains], FountainAvoidRadius).HasValue)
        {
            fountains.RemoveAt(fountains.Count - 1);
        }

        fountainAvoidCache = [.. fountains];
        fountainAvoidCacheAt = DateTime.Now;
        return fountainAvoidCache;
    }

    /// <summary>
    /// Finds the point inside Ambujam's arena nearest the player that is at least <paramref name="minDistance"/> from every center.
    /// </summary>
    /// <param name="centers">AOE centers.</param>
    /// <param name="minDistance">Required distance from each center.</param>
    /// <returns>Nearest safe point, or <see langword="null"/> when the circles cover the whole arena.</returns>
    private static Vector3? NearestSafeSpot(Vector3[] centers, float minDistance)
    {
        const float step = 1.0f;
        Vector3? best = null;
        float bestDistance = float.MaxValue;

        for (float x = -AmbujamUsableRadius; x <= AmbujamUsableRadius; x += step)
        {
            for (float z = -AmbujamUsableRadius; z <= AmbujamUsableRadius; z += step)
            {
                if ((x * x) + (z * z) > AmbujamUsableRadius * AmbujamUsableRadius)
                {
                    continue;
                }

                Vector3 point = new(ArenaCenter.Ambujam.X + x, ArenaCenter.Ambujam.Y, ArenaCenter.Ambujam.Z + z);
                if (!centers.All(c => point.Distance2D(c) >= minDistance))
                {
                    continue;
                }

                float distance = Core.Me.Distance2D(point);
                if (distance < bestDistance)
                {
                    best = point;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    private async Task<bool> HandleArmoredChariotAsync()
    {
        if (!Core.Me.InCombat)
        {
            lastChariotNoSafeSpotLog = DateTime.MinValue;
            chariotCastsAtZero.Clear();
            return false;
        }

        // Graviton Cannon: 6 yalm spread markers. Spreading and dodging fight each other, so spread wins.
        if (GravitonCannon.IsCasting())
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Spreading for Graviton Cannon");
            await MovementHelpers.Spread(GravitonCannonDuration, 7.0f);
            return false;
        }

        // Assault Cannon / Cannon Reflection: every wave is announced by casts whose position and heading give the
        // exact shapes, so walk to the nearest spot outside all of them rather than following a Trust NPC.
        List<Func<Vector3, bool>> hazards = ChariotHazards();
        if (hazards.Count == 0)
        {
            return false;
        }

        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Dodging Assault Cannon / Cannon Reflection");

        if (IsClearOf(Core.Me.Location, hazards))
        {
            if (!AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            return false;
        }

        Vector3? safe = NearestChariotSafeSpot(hazards);
        if (!safe.HasValue)
        {
            if (DateTime.Now - lastChariotNoSafeSpotLog > TimeSpan.FromSeconds(5))
            {
                Logger.Warning($"[Alzadaal] No safe spot found for {hazards.Count} Assault Cannon / Cannon Reflection shapes.");
                lastChariotNoSafeSpotLog = DateTime.Now;
            }

            return false;
        }

        if (Core.Me.Distance2D(safe.Value) > 6f && ActionManager.IsSprintReady)
        {
            ActionManager.Sprint();
        }

        Navigator.PlayerMover.MoveTowards(safe.Value);
        return false;
    }

    /// <summary>
    /// Builds point tests for Armored Chariot's active line and cone AOEs.
    /// </summary>
    /// <remarks>
    /// Cannon Reflection (28454) is cast by helpers at the arena center; the cast heading is the cone direction.
    /// Armored Drudges (28442 / 28443) fire an 8 wide line forward: 40 long from the edges, 28 from the corners.
    /// Finished casts keep reporting at 0ms for several seconds (and overlap the next wave's casts), so a cast at 0ms
    /// only counts for 1.5s, and only while nothing fresher is casting.
    /// </remarks>
    /// <returns>One test per AOE, <see langword="true"/> when a point is inside it.</returns>
    private List<Func<Vector3, bool>> ChariotHazards()
    {
        List<BattleCharacter> casts = [.. GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .Where(bc => bc.CastingSpellId is EnemyAction.CannonReflectionVisual or EnemyAction.AssaultCannonVisualA or EnemyAction.AssaultCannonVisualB)];

        foreach (BattleCharacter bc in casts)
        {
            if (bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds > 0)
            {
                chariotCastsAtZero.Remove(bc.ObjectId);
            }
            else if (!chariotCastsAtZero.ContainsKey(bc.ObjectId))
            {
                chariotCastsAtZero[bc.ObjectId] = DateTime.Now;
            }
        }

        List<BattleCharacter> fresh = [.. casts.Where(bc => bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds > 0)];
        casts = fresh.Count > 0
            ? fresh
            : [.. casts.Where(bc => DateTime.Now - chariotCastsAtZero[bc.ObjectId] < TimeSpan.FromMilliseconds(1_500))];

        List<Func<Vector3, bool>> hazards = [];
        foreach (BattleCharacter bc in casts)
        {
            Vector3 origin = bc.Location;
            float heading = bc.Heading;

            if (bc.CastingSpellId == EnemyAction.CannonReflectionVisual)
            {
                hazards.Add(p => InCone(p, ArenaCenter.ArmoredChariot, heading, 30f, 45f));
            }
            else
            {
                bool corner = Math.Abs(origin.X - ArenaCenter.ArmoredChariot.X) > 19f && Math.Abs(origin.Z - ArenaCenter.ArmoredChariot.Z) > 19f;
                float length = corner ? 28f : 40f;
                hazards.Add(p => InRect(p, origin, heading, 8f, length));
            }
        }

        if (hazards.Count > 0 && GameObjectManager.GameObjects.Any(o => o.NpcId == EnemyNpc.ArticulatedBitsVoidzone))
        {
            hazards.Add(p => p.Distance2D(ArenaCenter.ArmoredChariot) < 6.5f);
        }

        return hazards;
    }

    /// <summary>
    /// Nearest point inside Armored Chariot's square arena that is clear of every hazard (with a 1 yalm margin).
    /// </summary>
    private static Vector3? NearestChariotSafeSpot(List<Func<Vector3, bool>> hazards)
    {
        const float half = 18.5f;
        const float step = 1.0f;
        Vector3? best = null;
        float bestDistance = float.MaxValue;

        for (float x = -half; x <= half; x += step)
        {
            for (float z = -half; z <= half; z += step)
            {
                Vector3 point = new(ArenaCenter.ArmoredChariot.X + x, ArenaCenter.ArmoredChariot.Y, ArenaCenter.ArmoredChariot.Z + z);
                if (!IsClearOf(point, hazards))
                {
                    continue;
                }

                float distance = Core.Me.Distance2D(point);
                if (distance < bestDistance)
                {
                    best = point;
                    bestDistance = distance;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// <see langword="true"/> when the point and the ring 1 yalm around it are outside every hazard.
    /// </summary>
    private static bool IsClearOf(Vector3 point, List<Func<Vector3, bool>> hazards)
    {
        const float margin = 1.0f;
        Vector3[] probes =
        [
            point,
            new(point.X + margin, point.Y, point.Z),
            new(point.X - margin, point.Y, point.Z),
            new(point.X, point.Y, point.Z + margin),
            new(point.X, point.Y, point.Z - margin),
        ];

        return !probes.Any(p => hazards.Any(h => h(p)));
    }

    /// <summary>
    /// Point-in-rectangle for a line AOE starting at <paramref name="origin"/> and extending along <paramref name="heading"/>.
    /// FFXIV headings face (sin h, cos h) in X/Z.
    /// </summary>
    private static bool InRect(Vector3 point, Vector3 origin, float heading, float width, float length)
    {
        float dx = point.X - origin.X;
        float dz = point.Z - origin.Z;
        float forward = (dx * (float)Math.Sin(heading)) + (dz * (float)Math.Cos(heading));
        float side = (dx * (float)Math.Cos(heading)) - (dz * (float)Math.Sin(heading));
        return forward >= 0f && forward <= length && Math.Abs(side) <= width / 2f;
    }

    /// <summary>
    /// Point-in-cone for a cone AOE at <paramref name="origin"/> facing <paramref name="heading"/>.
    /// </summary>
    private static bool InCone(Vector3 point, Vector3 origin, float heading, float radius, float halfAngleDegrees)
    {
        float dx = point.X - origin.X;
        float dz = point.Z - origin.Z;
        float distance = (float)Math.Sqrt((dx * dx) + (dz * dz));
        if (distance > radius)
        {
            return false;
        }

        if (distance < 0.01f)
        {
            return true;
        }

        double cosAngle = ((dx * Math.Sin(heading)) + (dz * Math.Cos(heading))) / distance;
        return cosAngle >= Math.Cos(halfAngleDegrees * Math.PI / 180.0);
    }

    private async Task<bool> HandleKapikuluAsync()
    {
        if (!Core.Me.InCombat)
        {
            kapikuluArenaShrunk = false;
            powerSergeEnds = DateTime.MinValue;
            powerSergeSw.Reset();
            kapikuluTraps.Clear();
            lastSpinLogLocation = null;
            if (wasSpinning)
            {
                SidestepPlugin.Enabled = sideStepWasEnabledBeforeSpin;
            }

            wasSpinning = false;
            return false;
        }

        // Power Serge: cloth tethers pick which three 15 yalm Mana Explosions fire ~12s later; follow the Trust NPCs.
        // Armed before any other branch so Spin Out / spreads / stacks can't swallow the cast start.
        if (PowerSerge.IsCasting() && powerSergeEnds < DateTime.Now)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, PowerSergeDuration, "Dodging Power Serge / Mana Explosion");
            powerSergeEnds = DateTime.Now.AddMilliseconds(PowerSergeDuration);
            powerSergeSw.Reset();
        }

        if (!kapikuluArenaShrunk && KapikuluArenaShrink.IsCasting())
        {
            kapikuluArenaShrunk = true;
        }

        // Remember spike traps: they stay out after their cast, and Spin Out steering must keep clear of them.
        foreach (BattleCharacter trap in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(bc => bc.CastingSpellId == EnemyAction.Traps))
        {
            kapikuluTraps[trap.Location] = DateTime.Now;
        }

        foreach (Vector3 expired in kapikuluTraps.Where(t => DateTime.Now - t.Value > TimeSpan.FromSeconds(30)).Select(t => t.Key).ToList())
        {
            kapikuluTraps.Remove(expired);
        }

        // Spin Out: forced movement. Pause SideStep (its dodge runner can't steer a spinning player either) and steer.
        bool spinning = Core.Player.HasAura(PartyAura.Spinning);
        if (spinning && !wasSpinning)
        {
            sideStepWasEnabledBeforeSpin = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            lastSpinLogLocation = null;
            Logger.Information($"[Kapikulu] Spinning started at {Core.Me.Location}; SideStep paused, {kapikuluTraps.Count} spike traps known.");
        }
        else if (!spinning && wasSpinning)
        {
            SidestepPlugin.Enabled = sideStepWasEnabledBeforeSpin;
            MovementManager.MoveStop();
            Logger.Information($"[Kapikulu] Spinning ended at {Core.Me.Location}; SideStep restored.");
        }

        wasSpinning = spinning;

        if (spinning)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Steering Spin Out");
            await SteerSpinOutAsync();
            return false;
        }

        // Rotary Gale: 5 yalm spread markers.
        if (RotaryGale.IsCasting())
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Spreading for Rotary Gale");
            await MovementHelpers.Spread(RotaryGaleDuration, 6.0f);
            return false;
        }

        // Magnitude Opus: 6 yalm stack on a party member.
        if (MagnitudeOpus.IsCasting())
        {
            BattleCharacter caster = GameObjectManager.GetObjectsOfType<BattleCharacter>()
                .FirstOrDefault(bc => bc.CastingSpellId == EnemyAction.MagnitudeOpus);

            if (caster != null)
            {
                CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Stacking for Magnitude Opus");

                if (caster.SpellCastInfo.TargetId == Core.Player.ObjectId)
                {
                    Navigator.PlayerMover.MoveStop();
                }
                else if (GameObjectManager.GetObjectByObjectId(caster.SpellCastInfo.TargetId) is BattleCharacter stackTarget)
                {
                    await stackTarget.Follow(1.0f);
                }

                return false;
            }
        }

        if (DateTime.Now < powerSergeEnds)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 1_000, "Dodging Power Serge / Mana Explosion");
            await MovementHelpers.GetClosestAlly.FollowTimed(powerSergeSw, PowerSergeDuration);
        }

        return false;
    }

    /// <summary>
    /// Steers the forced movement of Spin Out. While Spinning the player moves on its own in the direction of a hand
    /// that turns gradually (~0.7 rad/s) toward the input direction, and can't stop. So nothing else may drive
    /// (SideStep and the Kapikulu avoids are paused) and the pather is never used.
    /// </summary>
    /// <remarks>
    /// Movement keys are camera-relative: a live run showed Forward turning the hand to the camera's heading and
    /// parking there, whatever the character faced. So the key is chosen from the camera heading: the one of
    /// Forward / Backward / StrafeLeft / StrafeRight whose world direction is closest to the goal bearing.
    /// The goal is the most open interior point (farthest from spike traps, the Basting Blade line and the border),
    /// because overshooting an orbit around an interior point is survivable and overshooting an edge is not.
    /// </remarks>
    private async Task SteerSpinOutAsync()
    {
        if (Core.Player.HasAura(PartyAura.Fetters))
        {
            LogSpin("fettered, no input", null, null, null, null);
            return;
        }

        Vector3 goal = SpinOutGoal();
        float desired = MathF.Atan2(goal.X - Core.Me.X, goal.Z - Core.Me.Z);

        Vector3 camera = CameraManager.CameraLocation;
        Vector3 focus = CameraManager.Focus;
        float cameraHeading = MathF.Atan2(focus.X - camera.X, focus.Z - camera.Z);

        // FFXIV headings: 0 = +Z (south), +PI/2 = +X (east). Facing a heading, left is heading + PI/2.
        (MovementDirection Key, float Offset)[] keys =
        [
            (MovementDirection.Forward, 0f),
            (MovementDirection.StrafeLeft, MathF.PI / 2f),
            (MovementDirection.Backward, MathF.PI),
            (MovementDirection.StrafeRight, -MathF.PI / 2f),
        ];
        (MovementDirection key, float offset) = keys
            .OrderBy(k => MathF.Abs(NormalizeAngle(cameraHeading + k.Offset - desired)))
            .First();

        MovementManager.Move(key, SpinOutInputPulse);
        LogSpin("steering", goal, desired, cameraHeading, key);
        await Coroutine.Yield();
    }

    private static float NormalizeAngle(float angle)
    {
        float fullTurn = 2f * MathF.PI;
        float normalized = angle % fullTurn;
        if (normalized > MathF.PI)
        {
            normalized -= fullTurn;
        }
        else if (normalized < -MathF.PI)
        {
            normalized += fullTurn;
        }

        return normalized;
    }

    /// <summary>
    /// Most open point inside Kapikulu's (shrunk) arena: maximizes clearance from spike traps, an active Basting Blade
    /// line and the border.
    /// </summary>
    private Vector3 SpinOutGoal()
    {
        float halfX = kapikuluArenaShrunk ? 15f : 19.5f;
        float halfZ = kapikuluArenaShrunk ? 20f : 24.5f;
        (Vector3 Origin, float Heading)[] blades = [.. GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .Where(bc => bc.CastingSpellId == EnemyAction.BastingBlade)
            .Select(bc => (bc.Location, bc.Heading))];

        Vector3 best = ArenaCenter.Kapikulu;
        float bestClearance = float.MinValue;
        for (float x = -halfX + 1f; x <= halfX - 1f; x += 1f)
        {
            for (float z = -halfZ + 1f; z <= halfZ - 1f; z += 1f)
            {
                Vector3 p = new(ArenaCenter.Kapikulu.X + x, ArenaCenter.Kapikulu.Y, ArenaCenter.Kapikulu.Z + z);
                float clearance = Math.Min(halfX - Math.Abs(x), halfZ - Math.Abs(z));

                foreach (Vector3 trap in kapikuluTraps.Keys)
                {
                    // Trap squares are 6x6, extending 6 yalms south (+Z) of the helper.
                    float dx = Math.Max(Math.Abs(p.X - trap.X) - 3f, 0f);
                    float dz = p.Z < trap.Z ? trap.Z - p.Z : Math.Max(p.Z - (trap.Z + 6f), 0f);
                    clearance = Math.Min(clearance, MathF.Sqrt((dx * dx) + (dz * dz)));
                }

                foreach ((Vector3 origin, float heading) in blades)
                {
                    float forward = ((p.X - origin.X) * MathF.Sin(heading)) + ((p.Z - origin.Z) * MathF.Cos(heading));
                    float side = ((p.X - origin.X) * MathF.Cos(heading)) - ((p.Z - origin.Z) * MathF.Sin(heading));
                    if (forward >= 0f && forward <= 60f)
                    {
                        clearance = Math.Min(clearance, Math.Abs(side) - 7.5f);
                    }
                }

                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    best = p;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Logs one Spin Out steering sample every 250ms: hand direction (client forced-movement float), facing,
    /// actual movement bearing since the last sample, commanded bearing and the Spin Out statuses.
    /// </summary>
    private void LogSpin(string action, Vector3? goal, float? desired, float? cameraHeading, MovementDirection? key)
    {
        if (!VerboseDiagnostics || DateTime.Now - lastSpinLog < TimeSpan.FromMilliseconds(250))
        {
            return;
        }

        Vector3 here = Core.Me.Location;
        string moved = "n/a";
        if (lastSpinLogLocation.HasValue)
        {
            float mx = here.X - lastSpinLogLocation.Value.X;
            float mz = here.Z - lastSpinLogLocation.Value.Z;
            moved = FormattableString.Invariant($"{MathF.Atan2(mx, mz):0.00} ({MathF.Sqrt((mx * mx) + (mz * mz)):0.0}y)");
        }

        string hand = TryReadForcedDirection(out float forced) ? forced.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "unreadable";
        string auras = string.Join(",", new[] { PartyAura.Spinning, PartyAura.Dizzy, PartyAura.Fetters }
            .Where(id => Core.Player.HasAura(id)));
        Logger.Information(FormattableString.Invariant(
            $"[Kapikulu] Spin {action}: at {here} hand={hand} facing={Core.Me.Heading:0.00} moved={moved} ")
            + (desired.HasValue ? FormattableString.Invariant($"want={desired.Value:0.00} goal={goal} ") : string.Empty)
            + (cameraHeading.HasValue ? FormattableString.Invariant($"camera={cameraHeading.Value:0.00} key={key} ") : string.Empty)
            + $"auras={auras} traps={kapikuluTraps.Count}");

        lastSpinLog = DateTime.Now;
        lastSpinLogLocation = here;
    }

    /// <summary>
    /// Reads the client's forced-movement direction (the same float Yuweyawata reads for Temporary Misdirection).
    /// Read-only and diagnostic here; a failure only drops the hand value from the log.
    /// </summary>
    private bool TryReadForcedDirection(out float direction)
    {
        direction = 0f;

        if (!forcedDirectionResolved)
        {
            forcedDirectionResolved = true;
            try
            {
                using PatternFinder patternFinder = new(Core.Memory);
                forcedDirectionAddress = patternFinder.Find(ForcedMovementDirectionPattern);
            }
            catch (Exception exception)
            {
                Logger.Warning($"[Kapikulu] Could not resolve the forced-movement direction ({exception.Message}).");
                forcedDirectionAddress = IntPtr.Zero;
            }
        }

        if (forcedDirectionAddress == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            direction = Core.Memory.Read<float>(forcedDirectionAddress);
            return !float.IsNaN(direction) && !float.IsInfinity(direction);
        }
        catch
        {
            return false;
        }
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Boss 1 main enemy.
        /// </summary>
        public const uint Ambujam = 11241;

        /// <summary>
        /// Boss 1 add for <see cref="EnemyAction.ToxinShower"/>.
        /// </summary>
        public const uint ScarletTentacle = 11242;

        /// <summary>
        /// Boss 1 add for <see cref="EnemyAction.CorrosiveVenom"/>.
        /// </summary>
        public const uint CyanTentacle = 11243;

        /// <summary>
        /// Boss 2 main enemy.
        /// </summary>
        public const uint ArmoredChariot = 11239;

        /// <summary>
        /// Boss 2 persistent void zone left by <see cref="EnemyAction.ArticulatedBits"/>.
        /// </summary>
        public const uint ArticulatedBitsVoidzone = 0x1EB69C;

        /// <summary>
        /// Boss 3 main enemy.
        /// </summary>
        public const uint Kapikulu = 11238;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Boss 1: <see cref="EnemyNpc.Ambujam"/>. 19.5 yalm circle.
        /// </summary>
        public static readonly Vector3 Ambujam = new(124f, 303f, -90f);

        /// <summary>
        /// Boss 2: <see cref="EnemyNpc.ArmoredChariot"/>. 39x39 square.
        /// </summary>
        public static readonly Vector3 ArmoredChariot = new(0f, -16f, -182f);

        /// <summary>
        /// Boss 3: <see cref="EnemyNpc.Kapikulu"/>. 39x49 rectangle, shrinking to 30x40.
        /// </summary>
        public static readonly Vector3 Kapikulu = new(110f, -350f, -68f);
    }

    private static class PartyAura
    {
        /// <summary>
        /// <see cref="EnemyAction.SpinOut"/>: player is spun after being pulled to <see cref="EnemyNpc.Kapikulu"/>.
        /// </summary>
        public const uint Spinning = 2973;

        /// <summary>
        /// Applied alongside <see cref="Spinning"/>.
        /// </summary>
        public const uint Dizzy = 2974;

        /// <summary>
        /// Bound in place during Spin Out; no input has any effect.
        /// </summary>
        public const uint Fetters = 2975;
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Raid-wide 40 yalm circle AOE with DOT.
        /// </summary>
        public const uint BigWave = 28512;

        /// <summary>
        /// Telegraphs later single-tentacle attack.
        /// </summary>
        public const uint TentacleDigA = 28501;

        /// <summary>
        /// Telegraphs later double-tentacle attack.
        /// </summary>
        public const uint TentacleDigB = 28503;

        /// <summary>
        /// Telegraphs later double-tentacle attack.
        /// </summary>
        public const uint TentacleDigC = 28505;

        /// <summary>
        /// <see cref="EnemyNpc.ScarletTentacle"/>'s 21 yalm circle AOE, centered on self.
        /// </summary>
        public const uint ToxinShower = 28508;

        /// <summary>
        /// <see cref="EnemyNpc.CyanTentacle"/>'s 21 yalm circle AOE, centered on self.
        /// </summary>
        public const uint CorrosiveVenom = 29158;

        /// <summary>
        /// Sequential 8 yalm circle AOE, paired with <see cref="CorrosiveFountain"/>.
        /// </summary>
        public const uint ToxicFountain = 29467;

        /// <summary>
        /// Sequential 8 yalm circle AOE, paired with <see cref="ToxicFountain"/>.
        /// </summary>
        public const uint CorrosiveFountain = 29556;

        /// <summary>
        /// <see cref="EnemyNpc.ArmoredChariot"/>'s 6 yalm persistent void zone at arena center.
        /// </summary>
        public const uint ArticulatedBits = 28441;

        /// <summary>
        /// Armored Drudge's 8s Assault Cannon visual (first wave).
        /// </summary>
        public const uint AssaultCannonVisualA = 28442;

        /// <summary>
        /// Armored Drudge's 8s Assault Cannon visual (second wave).
        /// </summary>
        public const uint AssaultCannonVisualB = 28443;

        /// <summary>
        /// 8s visual for reflected 30 yalm 90-degree cones off <see cref="EnemyNpc.ArmoredChariot"/>'s shield.
        /// </summary>
        public const uint CannonReflectionVisual = 28454;

        /// <summary>
        /// <see cref="EnemyNpc.ArmoredChariot"/>'s raid-wide.
        /// </summary>
        public const uint DiffusionRay = 28446;

        /// <summary>
        /// <see cref="EnemyNpc.ArmoredChariot"/>'s tank buster.
        /// </summary>
        public const uint RailCannon = 28447;

        /// <summary>
        /// 6 yalm spread markers on players, 8.5s cast.
        /// </summary>
        public const uint GravitonCannon = 29555;

        /// <summary>
        /// <see cref="EnemyNpc.Kapikulu"/>'s 60 long, 15 wide line AOE.
        /// </summary>
        public const uint BastingBlade = 28520;

        /// <summary>
        /// <see cref="EnemyNpc.Kapikulu"/>'s raid-wide; shrinks the arena.
        /// </summary>
        public const uint BillowingBolts = 28528;

        /// <summary>
        /// <see cref="EnemyNpc.Kapikulu"/>'s tank buster.
        /// </summary>
        public const uint CrewelSlice = 28530;

        /// <summary>
        /// 6 yalm stack on a player, cast by a helper.
        /// </summary>
        public const uint MagnitudeOpus = 28527;

        /// <summary>
        /// 15 yalm circles chosen by <see cref="PowerSerge"/>'s cloth tethers, cast by helpers.
        /// </summary>
        public const uint ManaExplosion = 28523;

        /// <summary>
        /// <see cref="EnemyNpc.Kapikulu"/>'s 6s cast that starts the cloth tether / Mana Explosion pattern.
        /// </summary>
        public const uint PowerSerge = 28522;

        /// <summary>
        /// <see cref="EnemyNpc.Kapikulu"/> tethers a player, pulls them in and applies <see cref="PartyAura.Spinning"/>.
        /// </summary>
        public const uint SpinOut = 28515;

        /// <summary>
        /// 6x6 spike trap squares, cast by helpers.
        /// </summary>
        public const uint Traps = 28519;

        /// <summary>
        /// 5 deep, 40 wide strips along the arena edge, cast by helpers.
        /// </summary>
        public const uint BorderChange = 28529;

        /// <summary>
        /// 5 yalm spread markers on players, cast by helpers.
        /// </summary>
        public const uint RotaryGale = 28525;
    }
}
