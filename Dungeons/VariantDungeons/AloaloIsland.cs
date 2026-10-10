using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PlanPoint = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Supplies captured mechanics for Aloalo's solo routes. The profile owns progression,
/// interactions and personal rewards; OrderBot and the combat routine retain normal combat.
/// Only corroborated encounter actions replace SideStep's generic handling.
/// </summary>
public sealed class AloaloIsland : AbstractDungeon
{
    // 2026-09-27 live initial boss position; same-name Base 9020 actors are helpers.
    // Territory 1176 is normal Variant. Neither Criterion difficulty uses this handler.
    private const uint QuaquaBase = 16570;
    private const uint ChargeBase = 16571;
    private const uint Axe = 35722;
    private const uint Quoit = 35723;
    private const uint KetudukeBase = 16529;
    private const uint SphereCrystalBase = 16530;
    private const uint FlatCrystalBase = 16531;
    private const uint BubbleStrewerBase = 2013494;
    private static readonly Vector3 LeftQuaquaCenter = new(-538, 18, 94);
    private static readonly Vector3 MiddleQuaquaCenter = new(50, 50, -160);
    // The 2026-09-30 right-route capture confirms this center from actor 16570,
    // its helpers and the two arena barriers at Z=74 and Z=23.5.
    private static readonly Vector3 RightQuaquaCenter = new(850, 219, 49);
    // Use the nearest fixed arena, confirmed by the 2026-09-30 middle-route
    // capture. Charges and teleports must not move the boundary or forecasts.
    // InQuaqua separately requires proximity and a live boss.
    private static Vector3 QuaquaCenter
    {
        get
        {
            if (Core.Me == null)
            {
                return LeftQuaquaCenter;
            }

            var nearest = Core.Me.Distance2D(MiddleQuaquaCenter) < Core.Me.Distance2D(LeftQuaquaCenter) ? MiddleQuaquaCenter : LeftQuaquaCenter;
            return Core.Me.Distance2D(RightQuaquaCenter) < Core.Me.Distance2D(nearest) ? RightQuaquaCenter : nearest;
        }
    }

    // The four crystal rows/columns are at center +/-5/15. The captured wind helper stands
    // at (-810,14,-395), corroborating the reference 40-yalm square and its center.
    private static readonly Vector3 KetudukeCenter = new(-790, 14, -395);
    // 2026-09-30 Lala's helper 34931 confirms the fixed center, unlike the
    // primary's initial(135,182,-875.786) position. Reference arena is40x40.
    private static readonly Vector3 LalaCenter = new(135, 182, -870);
    // 2026-09-30 Statice baseline: primary 16464 and helper 9020 confirm this20y
    // floor. Story Statice16496 shares the name/NpcId and must not activate combat.
    private static readonly Vector3 StaticeCenter = new(650, 33, -833);
    private static readonly uint[] StaticeActions =
    {
        35116,
        35206,
        35119,
        35122,
        35131,
        35142,
        StaticeBoxBurst
    };
    private readonly Dictionary<uint, Cleave> _staticeCones = new(), _staticeWeights = new(), _staticeProximity = new();
    private Cleave _staticePop;
    private DateTime _staticeFlightUntil;
    private readonly CapabilityManagerHandle _staticePositionHandle = CapabilityManager.CreateNewHandle();
    private bool _staticePositionOwned, _staticePositionMoving, _staticeOverrides;
    private Vector3? _staticeDestination;
    private string _staticePositionKind;
    private readonly Dictionary<uint, StaticeFire> _staticeFire = new();
    private bool _staticeFireOverrides;
    // Route 11 coffers warn through motion/animation about 8-13s before their short
    // Burst cast. Retain detached observations, never BattleCharacter wrappers.
    private const uint StaticeBoxBase = 16470, StaticeBoxBurst = 35127;
    private readonly Dictionary<uint, StaticeBox> _staticeBoxes = new();
    private DateTime _staticeBoxPoll;
    // Same ring, different cast lifetimes: the four-seed tile overlap uses35435,
    // positively captured at 06:41:39 for 19.7s rather than the short34945/4.7s cast.
    private const uint LalaRollingSpout = 34945;
    private const uint LalaRollingSpoutLong = 35435;
    private static readonly uint[] LalaActions =
    {
        34931,
        34947,
        35436,
        LalaRollingSpout,
        LalaRollingSpoutLong
    };
    private readonly Dictionary<uint, Cleave> _lalaBlights = new();
    private readonly Dictionary<uint, Cleave> _lalaProximity = new();
    private readonly Dictionary<uint, Cleave> _lalaSpouts = new();
    private readonly Dictionary<uint, Cleave[]> _lalaTiles = new();
    private uint _lalaLastCast;
    private bool _lalaPlotOverrides, _lalaFacingOwned, _lalaPlotSeeded;
    private readonly CapabilityManagerHandle _lalaFacingHandle = CapabilityManager.CreateNewHandle();
    private Vector3 _lalaGazeOrigin;
    private DateTime _lalaGazeEnd, _lalaGazeStart;
    private float? _lalaUnseenOffset;
    private static readonly uint[] KetudukeActions =
    {
        35452,
        35453,
        35454,
        35455,
        35461,
        35467,
        35468,
        35471,
        35479,
        35485,
        35486,
        35487,
        35488,
        35490,
        36112,
        36113,
        36114,
        36115,
        36116
    };
    private readonly Dictionary<uint, Sphere> _spheres = new();
    private readonly Dictionary<uint, RotatingLance> _lances = new();
    private readonly Dictionary<uint, Cleave> _statueGazes = new();
    private readonly CapabilityManagerHandle _statueFacingHandle = CapabilityManager.CreateNewHandle();
    private bool _statueFacingOwned, _statueGazeOverrides;
    private bool _lanceOverrides;
    private uint _lanceCancelledCast;
    private readonly Dictionary<uint, Crystal> _crystals = new();
    private readonly Dictionary<uint, Cleave> _lashings = new();
    private readonly Dictionary<uint, Cleave> _hydrobombs = new();
    private readonly Dictionary<uint, Cleave> _waterProximity = new();
    private readonly Dictionary<uint, Bubble> _bubbles = new();
    private readonly HashSet<uint> _weatherSources = new();
    private readonly List<Wave> _waves = new();
    private readonly Dictionary<uint, Cleave> _initialWaves = new();
    private uint _weatherCancelledCast;
    private Tide _tide;
    // 2026-09-28 captures: each storm repeat advances six yalms in about 2.1s.
    // Keep only current/next circles; reserving a whole future lane removes valid escapes.
    private readonly Dictionary<uint, StormWave> _storm = new();
    private readonly List<ChargeLane> _chargeLanes = new();
    private readonly HashSet<uint> _chargePreviews = new();
    private uint _quaquaLastCast;
    private DateTime _chargeUntil;
    private DateTime _chargeStart;
    private int _chargeIndex;
    private bool _chargeOverrides;
    private readonly CapabilityManagerHandle _routHandle = CapabilityManager.CreateNewHandle();
    private List<PlanPoint> _routPath;
    private DateTime _routPathAt, _routReplanAt;
    private bool _routOwned, _routMoving;
    private bool _stormOverrides;
    private readonly CapabilityManagerHandle _hammerHandle = CapabilityManager.CreateNewHandle();
    private readonly List<Vector3> _hammerLandings = new();
    private DateTime _hammerFirstImpact, _hammerUntil, _hammerResumeAt;
    private int _hammerIndex;
    private bool _hammerOwned, _hammerMoving;
    private readonly CapabilityManagerHandle _bubbleTideHandle = CapabilityManager.CreateNewHandle();
    private List<PlanPoint> _bubbleTidePath;
    private DateTime _bubbleTidePathAt, _bubbleTideReplanAt;
    private bool _bubbleTideOwned, _bubbleTideMoving;
    // Rescued-Zozone capture 2026-09-30 03:54: the wave pushed the player into the wall,
    // then the routine crossed the persistent surge and missed every beneficial bubble.
    // Keep knockback staging/native hazards separate from the positive bubble pickup.
    private Cleave _tidalWave, _hydrosurge;
    private DateTime _hydrosurgeStarts;
    private readonly CapabilityManagerHandle _wavefoamHandle = CapabilityManager.CreateNewHandle();
    private bool _wavefoamOwned, _wavefoamMoving;
    private uint _wavefoamTarget;
    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)1176;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new()
    {
        35491,
        34942,
        35112,
        34750
    }; // Includes Loquloqui's captured Protective Will; routine owns mitigation.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = new()
    {
        35732,
        35493,
        35456,
        35475,
        34943,
        35113,
        34748
    }; // Includes Loquloqui's Long-lost Light; not an avoidable circle.

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        _weatherCancelledCast = 0;
        ClearLances();
        ClearLala();
        ClearRescueMechanics();
        _spheres.Clear();
        _weatherSources.Clear();
        _waves.Clear();
        _initialWaves.Clear();
        _crystals.Clear();
        _lashings.Clear();
        _hydrobombs.Clear();
        _waterProximity.Clear();
        _bubbles.Clear();
        _tide = null;
        LlamaLibrary.Helpers.SideStep.Override(Axe);
        LlamaLibrary.Helpers.SideStep.Override(Quoit);
        LlamaLibrary.Helpers.SideStep.Override(35735);
        foreach (var action in KetudukeActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        foreach (var action in LalaActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        // The circular floor is20yalms from the captured center; preserve0.5 inside its edge.
        // Hammer baseline was knocked beyond that floor. This boundary constrains ordinary
        // avoidance destinations; it does not itself solve knockback staging.
        AvoidanceHelpers.AddAvoidDonut(InQuaqua, () => new[] { QuaquaCenter }, 100, 19.5);
        // At14:18:50 the late two-second circles inflicted two vulnerabilities. Tethers were
        // visible ten seconds earlier. Predict all current spheres together, replacing generic
        // geometry, with 0.5 edge padding: radius 14.5 and donut hole 4.5/outer 18.5.
        AvoidanceManager.AddAvoidLocation<Sphere>(
            InQuaqua,
            _ => 14.5f,
            s => s.Location,
            () => _spheres.Values.Where(
                s => s.Action == Axe
            && s.End > DateTime.UtcNow));
        AvoidanceHelpers.AddAvoidDonut(
            InQuaqua,
            () => _spheres.Values.Where(
                s => s.Action == Quoit
            && s.End > DateTime.UtcNow).Select(
                s => s.Location).ToArray(
            ),
            18.5,
            4.5);
        // 2026-09-29 21:30:59: generic avoidance never moved a player 2.28 yalms from
        // the initial line's center. The helper exposes zero cast/omen origins, so use
        // its captured actor center. Width8 plus 0.5 clearance and a centered50-yalm
        // strip cover the room, including the observed hit just behind the helper.
        // Preserve the initial impact before publishing the narrower traveling waves;
        // reserving both stages at once can remove the central escape corridor.
        AvoidanceManager.AddAvoidPolygon<Cleave>(
            InQuaqua,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -4.5f,
                -25.5f), new Vector2(
                    4.5f,
                    -25.5f), new Vector2(
                        4.5f,
                        25.5f), new Vector2(
                            -4.5f,
                            25.5f) },
            c => c.Location,
            () => _initialWaves.Values.Where(
                c => c.End > DateTime.UtcNow),
            priority: AvoidancePriority.High);
        // Repeats have no cast bar: forecast only the next width 4 strip with 0.5
        // padding. Never combine every future strip, which would reserve the whole floor.
        AvoidanceManager.AddAvoidPolygon<Wave>(
            InQuaqua,
            null,
            80,
            w => -w.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -2.5f,
                -25.5f), new Vector2(
                    2.5f,
                    -25.5f), new Vector2(
                        2.5f,
                        25.5f), new Vector2(
                            -2.5f,
                            25.5f) },
            w => w.Location,
            () => _waves.Where(
                w => w.Active
            && !_initialWaves.Values.Any(
                c => c.End > DateTime.UtcNow)),
            priority: AvoidancePriority.High);
        RegisterKetuduke();
        RegisterQuaquaSequences();
        RegisterLances();
        RegisterLala();
        RegisterStatice();
        RegisterLoquloqui();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ClearLoquloqui();
        ClearStatice();
        ClearStatueGazes();
        _weatherCancelledCast = 0;
        ClearLances();
        ClearLala();
        ClearRescueMechanics();
        ReleaseBubbleTide();
        ClearQuaquaSequences();
        _spheres.Clear();
        _weatherSources.Clear();
        _waves.Clear();
        _initialWaves.Clear();
        _crystals.Clear();
        _lashings.Clear();
        _hydrobombs.Clear();
        _waterProximity.Clear();
        _bubbles.Clear();
        _tide = null;
        LlamaLibrary.Helpers.SideStep.RemoveHandler(Axe);
        LlamaLibrary.Helpers.SideStep.RemoveHandler(Quoit);
        LlamaLibrary.Helpers.SideStep.RemoveHandler(35735);
        foreach (var action in KetudukeActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        foreach (var action in LalaActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (InLoquloqui() && !Core.Me.IsDead)
        {
            RefreshLoquloqui();
            if (await HandleLoquloquiKnockback())
            {
                return true;
            }

            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        ClearLoquloqui();
        if (InStatice() && !Core.Me.IsDead)
        {
            UpdateStatice();
            if (await HandleStaticePosition())
            {
                return true;
            }

            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        ClearStatice();
        if (!InQuaqua() || Core.Me.IsDead)
        {
            ClearStatueGazes();
        }

        if (InLala() && !Core.Me.IsDead)
        {
            UpdateLala();
            if (HandleLalaFacing())
            {
                return true;
            }

            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        ClearLala();
        if (InKetuduke() && !Core.Me.IsDead)
        {
            UpdateRescueMechanics();
            UpdateCrystals();
            UpdateTides();
            UpdateLashings();
            UpdateHydrobombs();
            UpdateWaterProximity();
            UpdateBubbles();
            if (await HandleWavefoam())
            {
                return true;
            }

            if (await HandleBubbleTide())
            {
                return true;
            }

            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        _crystals.Clear();
        ClearRescueMechanics();
        ReleaseBubbleTide();
        _lashings.Clear();
        _hydrobombs.Clear();
        _waterProximity.Clear();
        _bubbles.Clear();
        _tide = null;
        if (!InQuaqua() || Core.Me.IsDead)
        {
            _weatherCancelledCast = 0;
            ClearLances();
            ClearQuaquaSequences();
            _spheres.Clear();
            _weatherSources.Clear();
            _waves.Clear();
            _initialWaves.Clear();
            return false;
        }

        UpdateSpheres();
        UpdateLances();
        CancelLanceCastForEmergencyEscape();
        UpdateWeather();
        CancelWeatherCastForEmergencyEscape();
        UpdateQuaquaSequences();
        if (HandleStatueGazes())
        {
            return true;
        }

        if (await HandleRout())
        {
            return true;
        }

        if (await HandleHammerLanding())
        {
            return true;
        }

        return await DamageMitigationSpells();
    }

    // 2026-09-30 captures162133/175811: primary 16342, helpers9020; all geometry
    // is centered on the fixed50x40 platform, narrowed to 40x30 by the first raidwide.
    // Own only this boss. Generic raw-type geometry missed enlarged animals/petals
    // and tried leaving the platform during Sanctuary. All unsafe shapes below feed
    // one native avoidance planner; only knockback staging owns direct movement.
    private static readonly Vector3 LoquloquiCenter = new(950, 100, -860);
    private static readonly uint[] LoquloquiActions =
    {
        34749,
        34752,
        34753,
        34754,
        34755,
        34758,
        34760,
        34762,
        36152,
        34763,
        34764,
        34765
    };
    private readonly Dictionary<uint, LoquHazard> _loquCasts = new();
    private readonly Dictionary<uint, uint> _loquLastCasts = new();
    private readonly List<LoquHazard> _loquPetals = new(), _loquTiles = new();
    private DateTime _loquPoll, _loquPetalFinish, _loquKnockbackEnd, _loquForcedUntil;
    private bool _loquOverrides, _loquOwned, _loquMoving;
    private Vector3? _loquStand, _loquLastPosition;
    private DateTime _loquLastPositionAt;
    private readonly CapabilityManagerHandle _loquHandle = CapabilityManager.CreateNewHandle();
    private sealed class LoquHazard
    {
        internal Vector3 Location;
        internal float Heading, Radius, HalfWidth, Length;
        internal DateTime Start, End;
        internal int Wave;
    }

    private static bool InLoquloqui() => Core.Me != null
        && !Core.Me.IsDead
        && WorldManager.ZoneId == 1176
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            LoquloquiCenter) < 40
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.BaseId == 16342
        && b.IsAlive);
    private bool LoquloquiGeometry()
    {
        if (!InLoquloqui())
        {
            return false;
        }

        // Native escape can suspend the dungeon coroutine. Its bot-thread geometry
        // producer must continue discovering subsequent waves while RB owns movement.
        RefreshLoquloqui();
        return true;
    }

    private void RegisterLoquloqui()
    {
        ClearLoquloqui();
        // Use the eventual safe rectangle from pull: a0.5y inset preserves corners.
        // This is a danger-floor change, not removed collision; no navigation reset.
        AvoidanceHelpers.AddAvoidSquareDonut(LoquloquiGeometry, 39, 29, 140, 140, () => new[] { LoquloquiCenter });
        AvoidanceManager.AddAvoidLocation<LoquHazard>(LoquloquiGeometry, h => h.Radius, h => h.Location, () => ActiveLoquHazards().Where(h => h.Radius > 0));
        AvoidanceManager.AddAvoidPolygon<LoquHazard>(
            LoquloquiGeometry,
            null,
            90,
            h => -h.Heading,
            _ => 1,
            _ => 15,
            h => new[] { new Vector2(
                -h.HalfWidth,
                -.5f), new Vector2(
                    h.HalfWidth,
                    -.5f), new Vector2(
                        h.HalfWidth,
                        h.Length), new Vector2(
                            -h.HalfWidth,
                            h.Length) },
            h => h.Location,
            () => ActiveLoquHazards(
            ).Where(
                h => h.Radius == 0),
            priority: AvoidancePriority.High);
    }

    private IEnumerable<LoquHazard> ActiveLoquHazards()
    {
        var now = DateTime.UtcNow;
        foreach (var h in _loquCasts.Values.Concat(_loquTiles).Where(h => h.Start <= now && h.End > now))
        {
            yield return h;
        }

        // Later petal circles overlap the first pair's only escape. Publish one
        // resolution wave at a time, retaining it through damage before advancing.
        var live = _loquPetals.Where(h => h.End > now).ToArray();
        if (live.Length == 0)
        {
            yield break;
        }

        var wave = live.Min(h => h.Wave);
        foreach (var h in live.Where(h => h.Wave == wave))
        {
            yield return h;
        }
    }

    private void RefreshLoquloqui()
    {
        var now = DateTime.UtcNow;
        if (now < _loquPoll)
        {
            return;
        }

        _loquPoll = now.AddMilliseconds(80);
        if (!_loquOverrides)
        {
            foreach (var action in LoquloquiActions)
            {
                LlamaLibrary.Helpers.SideStep.Override(action);
            }

            _loquOverrides = true;
        }

        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(LoquloquiCenter) < 55).ToArray();
        foreach (var actor in actors)
        {
            var action = actor.IsCasting ? actor.CastingSpellId : 0;
            _loquLastCasts.TryGetValue(actor.ObjectId, out var previous);
            _loquLastCasts[actor.ObjectId] = action;
            if (action == 0)
            {
                continue;
            }

            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            if (actor.BaseId == 16342 && action != previous)
            {
                if (action == 34757)
                {
                    _loquPetals.Clear();
                    _loquPetalFinish = finish;
                }

                if (action == 34759)
                {
                    _loquTiles.Clear();
                    // Captured18:15:37 second-tile hit agrees with the 12-cell
                    // serpentine sequence: first 11.5s after visual, then0.5s/cell.
                    // RB's cast time excludes0.3s; retain another0.3s damage grace.
                    for (var i = 0; i < 12; ++i)
                    {
                        var row = i / 4;
                        var col = row == 1 ? 3 - i % 4 : i % 4;
                        var impact = finish.AddSeconds(11.8 + i * .5);
                        _loquTiles.Add(
                            new LoquHazard
                            {
                                Location = LoquloquiCenter + new Vector3(
                                    -15 + 10 * col,
                                    0,
                                    -15 + 10 * row),
                                HalfWidth = 5.5f,
                                Length = 10.5f,
                                Start = impact.AddSeconds(
                                    -2.5),
                                End = impact.AddSeconds(
                                    .3)
                            });
                    }
                }

                if (action == 34763)
                {
                    // Live18:16:22/25/28/30/33 shows repeated8y pushes, not a
                    // single cast-end knockback. Re-stage throughout the 15s window;
                    // displacement detection releases input while each push travels.
                    _loquKnockbackEnd = finish.AddSeconds(15);
                    _loquStand = null;
                }
            }

            // Cast expiry precedes damage (Rush baseline by roughly 1s). Retain
            // the detached shape through 1.4s effect grace instead of dropping it
            // when the helper's cast flag clears and allowing an early re-entry.
            var h = new LoquHazard
            {
                Location = actor.Location,
                Heading = actor.Heading,
                Start = now,
                End = finish.AddSeconds(1.4)
            };
            if (actor.BaseId == 16343 && action is 34752 or 34753)
            {
                h.HalfWidth = action == 34752 ? 1.5f : 5.5f;
                h.Length = 35.5f;
            }
            else if (actor.BaseId == 16344 && action is 34754 or 34755)
            {
                h.Radius = action == 34754 ? 4.5f : 12.5f;
            }
            else if (actor.BaseId == 16342 && action == 34749)
            {
                h.HalfWidth = 20.5f;
                h.Length = 25.5f;
            }
            else if (actor.BaseId == 9020 && action == 36152)
            {
                h.Radius = 18.5f;
            }
            else if (actor.BaseId == 9020 && action == 34765)
            {
                h.Radius = 7.5f;
            }
            else if (actor.BaseId == 9020 && action == 34758)
            {
                // Helper actor center is verified; both cast/omen centers are zero.
                // Reconcile forecasts rather than duplicating the two wave owners.
                var petal = _loquPetals.FirstOrDefault(p => p.Location.Distance2D(actor.Location) < 1.5f);
                if (petal == null)
                {
                    _loquPetals.Add(petal = new LoquHazard { Location = actor.Location, Radius = 15.5f });
                }

                petal.End = h.End;
                continue;
            }
            else
            {
                continue;
            }

            _loquCasts[actor.ObjectId] = h;
        }

        if (_loquPetals.Count == 0 && _loquPetalFinish > now.AddSeconds(-4))
        {
            var landings = actors.Where(b => b.BaseId == 16345 && b.VfxContainer.Tethers.Any(t => t.Id == 259)).Select(b => b.Location).ToArray();
            var walls = actors.Where(b => b.BaseId == 16346 && b.VfxContainer.Tethers.Any(t => t.Id == 259)).Select(b => b.Location).ToList();
            if ((landings.Length == 2 || landings.Length == 4) && walls.Count == landings.Length)
            {
                // Matched active wall/landing pairs distinguish short from long
                // travel. Never include the many inactive marker actors. First
                // capture's2 petals resolve together; the 4-marker pattern resolves
                // the two nearest pairs first, then the remaining pair6s later.
                var remaining = landings.ToList();
                var pairs = new List<(Vector3 Point, float Distance)>();
                while (remaining.Count > 0)
                {
                    var pair = remaining.SelectMany(p => walls.Select(w => (Point: p, Wall: w, Distance: p.Distance2D(w)))).OrderBy(p => p.Distance).First();
                    pairs.Add((pair.Point, pair.Distance));
                    remaining.Remove(pair.Point);
                    walls.Remove(pair.Wall);
                }

                var ordered = pairs.OrderBy(p => p.Distance).ToArray();
                for (var i = 0; i < ordered.Length; ++i)
                {
                    var wave = ordered.Length == 4 && i >= 2 ? 1 : 0;
                    // Live175811:14.9/20.9s after native cast finish for four;
                    // two-marker pair20.7s. Add 0.5s to hold through the effect.
                    var seconds = ordered.Length == 2 || wave == 1 ? 21.4 : 15.4;
                    _loquPetals.Add(new LoquHazard { Location = ordered[i].Point, Radius = 15.5f, Wave = wave, End = _loquPetalFinish.AddSeconds(seconds) });
                }
            }
        }

        foreach (var id in _loquCasts.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _loquCasts.Remove(id);
        }

        _loquTiles.RemoveAll(p => p.End <= now);
    }

    private async Task<bool> HandleLoquloquiKnockback()
    {
        var now = DateTime.UtcNow;
        if (_loquKnockbackEnd <= now)
        {
            ReleaseLoquloquiPosition();
            return false;
        }

        // Puddles and knockback are one overlap: select a safe staging point AND
        // its8y landing. Native hazard escape retains priority over positive staging.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _loquMoving = false;
            return false;
        }

        var position = Core.Me.Location;
        if (_loquLastPosition.HasValue && (now - _loquLastPositionAt).TotalSeconds < .3 && position.Distance2D(_loquLastPosition.Value) > 2.5f)
        {
            _loquForcedUntil = now.AddMilliseconds(650);
        }

        _loquLastPosition = position;
        _loquLastPositionAt = now;
        if (now < _loquForcedUntil)
        {
            _loquMoving = false;
            return false;
        }

        var circles = _loquCasts.Values.Where(h => h.Radius > 0 && h.End > now).ToArray();
        float Clearance(Vector3 point) => Math.Min(19.5f - Math.Abs(point.X - 950), 14.5f - Math.Abs(point.Z + 860));
        bool Safe(Vector3 stand)
        {
            var delta = stand - LoquloquiCenter;
            var land = LoquloquiCenter + delta * 5; // stand2 + push8 = landing10.
            return Clearance(land) >= 1 && circles.All(h => stand.Distance2D(h.Location) > h.Radius + .3f && land.Distance2D(h.Location) > h.Radius + .3f);
        }

        if (!_loquStand.HasValue || !Safe(_loquStand.Value))
        {
            _loquStand = Enumerable.Range(
                0,
                72).Select(
                    i => LoquloquiCenter + new Vector3(
                        (float)Math.Sin(
                            i * Math.PI / 36) * 2,
                        0,
                        (float)Math.Cos(
                            i * Math.PI / 36) * 2)).Where(
                                Safe).OrderBy(
                                    p => Core.Me.Distance2D(
                                        p)).Select(
                                            p => (Vector3?)p).FirstOrDefault(
                );
        }

        if (!_loquStand.HasValue)
        {
            ReleaseLoquloquiPosition();
            return false;
        }

        CapabilityManager.Update(_loquHandle, CapabilityFlags.Movement, 1000, "Loquloqui repeated knockback staging");
        _loquOwned = true;
        if (Core.Me.Distance2D(_loquStand.Value) <= .25f)
        {
            if (_loquMoving)
            {
                Navigator.PlayerMover.MoveStop();
            }

            _loquMoving = false;
            return false; // Hold movement only; allow routine healing/attacks.
        }

        if (Core.Me.IsCasting)
        {
            ActionManager.StopCasting();
        }

        _loquMoving = true;
        Navigator.PlayerMover.MoveTowards(_loquStand.Value);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseLoquloquiPosition()
    {
        if (_loquOwned)
        {
            CapabilityManager.Clear(_loquHandle, CapabilityFlags.Movement, "Loquloqui knockback staging released");
        }

        if (_loquMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _loquOwned = _loquMoving = false;
        _loquStand = _loquLastPosition = null;
    }

    private void ClearLoquloqui()
    {
        ReleaseLoquloquiPosition();
        _loquCasts.Clear();
        _loquLastCasts.Clear();
        _loquPetals.Clear();
        _loquTiles.Clear();
        _loquPoll = _loquPetalFinish = _loquKnockbackEnd = _loquForcedUntil = default;
        if (_loquOverrides)
        {
            foreach (var action in LoquloquiActions)
            {
                LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
            }
        }

        _loquOverrides = false;
    }

    private static bool InStatice() => Core.Me != null
        && WorldManager.ZoneId == 1176
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            StaticeCenter) < 28
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.BaseId == 16464
        && b.IsAlive
        && b.Distance2D(
            StaticeCenter) < 30);
    private void RegisterStatice()
    {
        AvoidanceHelpers.AddAvoidDonut(InStatice, () => new[] { StaticeCenter }, 100, 19.5);
        // Baseline09:43 avoided all six slices, including two harmless35206 casts.
        // Publish only real35116 sectors. Shift the apex back0.5y and pad the two
        // edges; a40.5y triangle covers the room without reserving the fake wedges.
        AvoidanceManager.AddAvoidPolygon<Cleave>(
            InStatice,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                0,
                -1), new Vector2(
                    -24.3f,
                    40.5f), new Vector2(
                        24.3f,
                        40.5f) },
            c => c.Location,
            () => _staticeCones.Values.Where(
                c => c.End > DateTime.UtcNow),
            priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Cleave>(InStatice, _ => 4.5f, c => c.Location, () => _staticeWeights.Values.Where(c => c.End > DateTime.UtcNow));
        // 09:46 generic35131 reserved40y and tried leaving the sealed arena,
        // killing the player 7.6 yalms from the source. It is a proximity blast; corroborated
        // 15y separation plus 0.5 clearance leaves the far side of this20y floor.
        AvoidanceManager.AddAvoidLocation<Cleave>(InStatice, _ => 15.5f, c => c.Location, () => _staticeProximity.Values.Where(c => c.End > DateTime.UtcNow));
        // 2026-09-30 12:42/13:05: Burst is a15y circle, but its700ms cast is too
        // late to escape. Publish the captured early warnings with 0.5y clearance;
        // native avoidance also owns any simultaneous bullet/fire geometry.
        AvoidanceManager.AddAvoidLocation<StaticeBox>(InStatice, _ => 15.5f, b => b.Location, ActiveStaticeBoxes);
        // 2026-09-30 15:04 Present Box: six untethered16475 missiles crossed
        // straight through the floor;16473 followed player tether17 and reached
        // contact range;15:25 independently captured weapon16472 with the same
        // player tether. Do not require a missile tether. Both hazards join the
        // staff telegraphs in native avoidance, which owns their simultaneous escape.
        // Missile radius 1 +0.5 clearance and 2y forward anticipation cover movement
        // between pulses. The weapon needs3y separation +0.5 to avoid its knockback.
        for (var kind = 0; kind < 2; ++kind)
        {
            var weapon = kind == 1;
            var radius = weapon ? 3.5f : 1.5f;
            AvoidanceManager.AddAvoidPolygon<Cleave>(
                InStatice,
                null,
                80,
                c => -c.Heading,
                _ => 1,
                _ => 15,
                _ => new[] { new Vector2(
                    -radius,
                    -radius), new Vector2(
                        radius,
                        -radius), new Vector2(
                            radius,
                            radius + 2), new Vector2(
                                -radius,
                                radius + 2) },
                c => c.Location,
                () => ActiveStaticePresents(
                    weapon),
                priority: AvoidancePriority.High);
        }

        // 2026-09-30 omen matrices establish short0..5 and long8..20 strips,
        // both width 5. Keep the real gap with 0.5y clearance on every edge. The
        // forecast shares native avoidance with real bullet sectors during overlap.
        for (var part = 0; part < 2; ++part)
        {
            var longPart = part == 1;
            AvoidanceManager.AddAvoidPolygon<Cleave>(
                InStatice,
                null,
                80,
                c => -c.Heading,
                _ => 1,
                _ => 15,
                _ => longPart ? new[] { new Vector2(
                    -3,
                    7.5f), new Vector2(
                        3,
                        7.5f), new Vector2(
                            3,
                            20.5f), new Vector2(
                                -3,
                                20.5f) } : new[] { new Vector2(
                                    -3,
                                    -.5f), new Vector2(
                                        3,
                                        -.5f), new Vector2(
                                            3,
                                            5.5f), new Vector2(
                                                -3,
                                                5.5f) },
                c => c.Location,
                () => ActiveStaticeFire(
                    longPart),
                priority: AvoidancePriority.High);
        }
    }

    private static IEnumerable<Cleave> ActiveStaticePresents(bool weapon)
    {
        // Evaluate in the avoidance supplier, including while escape holds the
        // profile coroutine. Return scalars only; no native actor survives a pulse.
        var player = Core.Me;
        if (player == null)
        {
            yield break;
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsAlive
            && b.IsVisible
            && b.Distance2D(
                StaticeCenter) < 25
            && (weapon ? b.BaseId == 16471
            || b.BaseId == 16472
            || b.BaseId == 16473 : b.BaseId == 16475)))
        {
            if (weapon && !actor.VfxContainer.Tethers.Any(t => t.Id == 17 && t.TargetId == player.ObjectId))
            {
                continue;
            }

            var location = actor.Location;
            var heading = weapon ? (float)Math.Atan2(player.Location.X - location.X, player.Location.Z - location.Z) : actor.Heading;
            yield return new Cleave
            {
                Location = location,
                Heading = heading,
                End = DateTime.UtcNow.AddSeconds(1)
            };
        }
    }

    private IEnumerable<Cleave> ActiveStaticeFire(bool longPart)
    {
        // Do not duplicate a partially captured layout with SideStep's initial
        // telegraphs. Only a complete six-helper seed transfers geometry ownership.
        if (!_staticeFireOverrides)
        {
            yield break;
        }

        // Two independent captures expose ten12-degree heading changes over about
        // 11s after the initial cast, with VFX156 negative and 157 positive. Publish
        // current/next only, retaining1.1s effect grace. Advancing in this detached
        // supplier prevents native escape from freezing a forecast while it owns TreeStart.
        var now = DateTime.UtcNow;
        foreach (var fire in _staticeFire.Values.Where(f => f.Long == longPart))
        {
            var index = Math.Max(0, (int)Math.Floor((now - fire.FirstEnd).TotalSeconds / 1.1) + 1);
            for (var ahead = 0; ahead < 2 && index + ahead < 11; ++ahead)
            {
                var strip = fire.Strips[ahead];
                strip.Location = fire.Location;
                strip.Heading = fire.Heading + fire.Step * (index + ahead);
                yield return strip;
            }
        }
    }

    private IEnumerable<StaticeBox> ActiveStaticeBoxes()
    {
        // Native escape can hold the dungeon coroutine. Poll from its bot-thread
        // supplier so the other boxes still reveal themselves during that escape.
        var now = DateTime.UtcNow;
        if (now >= _staticeBoxPoll)
        {
            _staticeBoxPoll = now.AddMilliseconds(100);
            var boxes = GameObjectManager.GetObjectsOfType<BattleCharacter>(
                ).Where(
                    b => b.IsValid
                && b.BaseId == StaticeBoxBase
                && b.Distance2D(
                    StaticeCenter) < 25).ToArray(
                );
            foreach (var actor in boxes)
            {
                if (!_staticeBoxes.TryGetValue(actor.ObjectId, out var box))
                {
                    _staticeBoxes[actor.ObjectId] = box = new StaticeBox
                    {
                        Location = actor.Location,
                        Origin = actor.Location,
                        Heading = actor.Heading,
                        End = now.AddSeconds(30)
                    };
                }

                // Existing public Lua getter: no client offsets. On13:04:52 the
                // stationary damaging box changed idle 3 to 210, then0; moving box
                // used 13. The safe box remained3. Unknown/missing Lua leaves the
                // motion and real-cast fallbacks available, without guessing safety.
                var timeline = -1;
                if (!string.IsNullOrEmpty(actor.LuaString))
                {
                    var value = Lua.GetReturnVal<string>(
                        "local o=_G['" + actor.LuaString + "']; if not o or not o.GetActionTimelineId then return '-1' end; return tostring(o:GetActionTimelineId())");
                    int.TryParse(value, out timeline);
                }

                var casting = actor.IsCasting && actor.CastingSpellId == StaticeBoxBurst;
                var angle = Math.Abs((float)Math.Atan2(Math.Sin(actor.Heading - box.Heading), Math.Cos(actor.Heading - box.Heading)));
                if (!box.Finished
                    && !box.Dangerous
                    && (casting
                    || timeline == 210
                    || timeline == 13
                    || actor.Location.Distance2D(
                        box.Origin) > .05f
                    || angle > .05f))
                {
                    box.Dangerous = true;
                }

                box.Location = actor.Location;
                if (casting)
                {
                    box.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
                }

                // 4573 is the observed explosion, not an early warning. Keep one
                // second for effect delivery, then latch completion until despawn.
                if (timeline == 4573 && !box.Finished)
                {
                    box.Finished = true;
                    box.End = now.AddSeconds(1);
                }
            }

            foreach (var id in _staticeBoxes.Keys.Where(id => !boxes.Any(b => b.ObjectId == id)).ToArray())
            {
                _staticeBoxes.Remove(id);
            }
        }

        return _staticeBoxes.Values.Where(b => b.Dangerous && b.End > now);
    }

    private void UpdateStatice()
    {
        if (!_staticeOverrides)
        {
            foreach (var action in StaticeActions)
            {
                LlamaLibrary.Helpers.SideStep.Override(action);
            }

            _staticeOverrides = true;
        }

        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(StaticeCenter) < 45).ToArray();
        foreach (var actor in actors.Where(b => b.IsCasting))
        {
            var action = actor.CastingSpellId;
            var remaining = actor.SpellCastInfo.RemainingCastTime;
            // Hidden weights still cast; filtering IsVisible loses their landing hazards.
            var collection = action == 35116
                && actor.BaseId == 9020 ? _staticeCones : action == 35122
                && actor.BaseId == 16467 ? _staticeWeights : action == 35131
                && actor.BaseId == 9020 ? _staticeProximity : null;
            if (collection != null && remaining > TimeSpan.Zero)
            {
                collection[actor.ObjectId] = new Cleave
                {
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = now + remaining + TimeSpan.FromSeconds(1)
                };
            }

            if (action == 35119 && actor.BaseId == 9020 && remaining > TimeSpan.Zero)
            {
                _staticePop = new Cleave
                {
                    Location = actor.Location,
                    End = now + remaining + TimeSpan.FromSeconds(1)
                };
            }

            // Flight launch ends about 09:45:03; failed landing arrived06.997.
            // Retain the positive hold through 4.5s landing grace, not cast expiry.
            if (action == 35142 && actor.BaseId == 16464 && remaining > TimeSpan.Zero)
            {
                _staticeFlightUntil = now + remaining + TimeSpan.FromSeconds(4.5);
            }

            if ((action == 35124 || action == 35125) && actor.BaseId == 9020 && remaining > TimeSpan.Zero)
            {
                var ball = actors.FirstOrDefault(b => b.BaseId == 16469 && b.Distance2D(actor.Location) < 1);
                var rotation = ball?.VfxContainer.Vfx.FirstOrDefault(v => v != null && v.IsValid && (v.Id == 156 || v.Id == 157));
                var offset = actor.OmenMatrix.Center.Distance2D(actor.Location);
                // A missing/changed omen or direction leaves the initial native
                // telegraph intact. Do not invent a timer on a mid-sequence attach.
                if (rotation == null || Math.Abs(offset - (action == 35125 ? 8 : 0)) > .2f)
                {
                    continue;
                }

                if (!_staticeFire.TryGetValue(actor.ObjectId, out var fire) || now > fire.FirstEnd.AddSeconds(12))
                {
                    _staticeFire[actor.ObjectId] = fire = new StaticeFire
                    {
                        Location = actor.Location,
                        Heading = actor.Heading,
                        Long = action == 35125,
                        Step = (rotation.Id == 156 ? -1 : 1) * (float)Math.PI / 15
                    };
                }

                fire.FirstEnd = now + remaining + TimeSpan.FromSeconds(1.1);
            }
        }

        foreach (var collection in new[]
        {
            _staticeCones,
            _staticeWeights,
            _staticeProximity
        }

        )
        {
            foreach (var id in collection.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            {
                collection.Remove(id);
            }
        }

        foreach (var id in _staticeFire.Where(p => now > p.Value.FirstEnd.AddSeconds(12)).Select(p => p.Key).ToArray())
        {
            _staticeFire.Remove(id);
        }

        if (_staticeFire.Count == 6 && !_staticeFireOverrides)
        {
            foreach (var action in new uint[]
            {
                35124,
                35125,
                35318,
                35319
            }

            )
            {
                LlamaLibrary.Helpers.SideStep.Override(action);
            }

            _staticeFireOverrides = true;
        }

        if (_staticeFire.Count == 0 && _staticeFireOverrides)
        {
            foreach (var action in new uint[]
            {
                35124,
                35125,
                35318,
                35319
            }

            )
            {
                LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
            }

            _staticeFireOverrides = false;
        }
    }

    private async Task<bool> HandleStaticePosition()
    {
        var now = DateTime.UtcNow;
        var pop = _staticePop != null && _staticePop.End > now;
        var flight = _staticeFlightUntil > now;
        if (!pop && !flight)
        {
            ReleaseStaticePosition();
            return false;
        }

        // One semantic owner: Pop first, then cushion landing. Native geometric
        // escape has priority. Never issue a competing destination during escape.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _staticePositionMoving = false;
            return false;
        }

        var kind = pop ? "balloon landing" : "cushion landing";
        if (_staticePositionKind != kind)
        {
            ReleaseStaticePosition();
            _staticePositionKind = kind;
        }

        if (pop)
        {
            // Needle resolves before Pop, then weights. Select both a clear staging
            // point and its13y projected landing; hold only until forced movement
            // starts, so we do not run back into the room during the knockback.
            if (now >= _staticePop.End.AddSeconds(-1) && _staticeDestination.HasValue)
            {
                return false;
            }

            if (!_staticeDestination.HasValue)
            {
                var bestScore = float.NegativeInfinity;
                for (var i = 0; i < 72; ++i)
                {
                    var angle = i * Math.PI / 36;
                    var direction = new Vector3((float)Math.Sin(angle), 0, (float)Math.Cos(angle));
                    var stand = _staticePop.Location + direction * 3;
                    var land = _staticePop.Location + direction * 16;
                    var clearance = 19.5f - land.Distance2D(StaticeCenter);
                    foreach (var weight in _staticeWeights.Values.Where(w => w.End > now))
                    {
                        clearance = Math.Min(clearance, land.Distance2D(weight.Location) - 4.5f);
                    }

                    if (clearance < .5f || AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(stand)))
                    {
                        continue;
                    }

                    var score = clearance - .02f * Core.Me.Distance2D(stand);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        _staticeDestination = stand;
                    }
                }
            }
        }
        else if (!_staticeDestination.HasValue)
        {
            // 2026-09-30 eight visible2013490 objects remained through landing.
            // They are positive shelters, never loot or generic unsafe circles.
            var cushion = GameObjectManager.GameObjects.Where(
                o => o.IsValid
                && o.IsVisible
                && o.BaseId == 2013490
                && o.Distance2D(
                    StaticeCenter) < 19
                && !AvoidanceManager.Avoids.Any(
                    a => a.IsPointInAvoid(
                        o.Location))).OrderBy(
                            o => o.Distance2D(
                                Core.Me.Location)).FirstOrDefault(
                );
            if (cushion != null)
            {
                _staticeDestination = cushion.Location;
            }
        }

        if (!_staticeDestination.HasValue)
        {
            ReleaseStaticePosition();
            return false;
        }

        CapabilityManager.Update(_staticePositionHandle, CapabilityFlags.Movement, 1000, "Aloalo Statice " + kind);

        _staticePositionOwned = true;
        if (Core.Me.Distance2D(_staticeDestination.Value) < .3f)
        {
            if (_staticePositionMoving)
            {
                Navigator.PlayerMover.MoveStop();
            }

            _staticePositionMoving = false;
            return false; // Keep healing/rotation schedulable while holding the safe point.
        }

        if (Core.Me.IsCasting)
        {
            ActionManager.StopCasting();
        }

        _staticePositionMoving = true;
        Navigator.PlayerMover.MoveTowards(_staticeDestination.Value);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseStaticePosition()
    {
        if (_staticePositionOwned)
        {
            CapabilityManager.Clear(_staticePositionHandle, CapabilityFlags.Movement, "Aloalo Statice positioning ended");
        }

        if (_staticePositionMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _staticePositionOwned = _staticePositionMoving = false;
        _staticeDestination = null;
        _staticePositionKind = null;
    }

    private void ClearStatice()
    {
        ReleaseStaticePosition();
        _staticeCones.Clear();
        _staticeWeights.Clear();
        _staticeProximity.Clear();
        _staticePop = null;
        _staticeFlightUntil = default;
        _staticeFire.Clear();
        _staticeBoxes.Clear();
        _staticeBoxPoll = default;
        if (_staticeFireOverrides)
        {
            foreach (var action in new uint[]
            {
                35124,
                35125,
                35318,
                35319
            }

            )
            {
                LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
            }
        }

        _staticeFireOverrides = false;
        if (_staticeOverrides)
        {
            foreach (var action in StaticeActions)
            {
                LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
            }
        }

        _staticeOverrides = false;
    }

    private bool HandleStatueGazes()
    {
        var now = DateTime.UtcNow;
        // 2026-09-30 right helpers9020 cast 35758 for 4.7s in simultaneous pairs.
        // Both western sources hit08:49:49.577, about 0.93s after the announced
        // end. Retain detached origins through 1.3s grace; never model this gaze
        // as the raw cast-type 2 circle with zero CastLocation.
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.BaseId == 9020
            && b.IsCasting
            && b.CastingSpellId == 35758
            && b.Distance2D(
                QuaquaCenter) < 35))
        {
            var remaining = actor.SpellCastInfo.RemainingCastTime;
            if (remaining > TimeSpan.Zero || !_statueGazes.ContainsKey(actor.ObjectId))
            {
                _statueGazes[actor.ObjectId] = new Cleave
                {
                    Location = actor.Location,
                    End = now + remaining + TimeSpan.FromSeconds(1.3)
                };
            }
        }

        foreach (var id in _statueGazes.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _statueGazes.Remove(id);
        }

        if (_statueGazes.Count == 0)
        {
            ClearStatueGazes();
            return false;
        }

        if (!_statueGazeOverrides)
        {
            LlamaLibrary.Helpers.SideStep.Override(35758);
            _statueGazeOverrides = true;
        }

        var active = _statueGazes.Values.Where(g => now >= g.End.AddSeconds(-2.8)).ToArray();
        // Reserve facing only for the last1.5s plus observed effect grace. Native
        // hazard egress retains priority; this handler never selects a destination.
        if (active.Length == 0 || AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleaseStatueFacing();
            return false;
        }

        if (!AloaloStatueGazePlan.TryHeading(
            new PlanPoint(
                Core.Me.Location.X,
                Core.Me.Location.Z),
            active.Select(
                g => new PlanPoint(
                    g.Location.X,
                    g.Location.Z)).ToArray(
            ),
            out var heading))
        {
            ReleaseStatueFacing();
            return false;
        }

        CapabilityManager.Update(_statueFacingHandle, CapabilityFlags.Facing, 1000, "Aloalo simultaneous statue gazes");
        if (!_statueFacingOwned)
        {
            if (Core.Me.IsCasting)
            {
                ActionManager.StopCasting();
            }
        }

        _statueFacingOwned = true;
        // The08:59 retry retained its combat heading after StopCasting, despite
        // repeated facing requests. Match the validated Analysis hold by stopping
        // residual mover input before turning; never interrupt active avoid egress.
        Navigator.PlayerMover.MoveStop();

        Core.Me.SetFacing(heading);
        // Game actions can auto-face despite a routine lease. The bounded impact
        // window fences those actions, as with validated Lala facing, then releases.
        return true;
    }

    private void ReleaseStatueFacing()
    {
        if (_statueFacingOwned)
        {
            CapabilityManager.Clear(_statueFacingHandle, CapabilityFlags.Facing, "Aloalo statue gaze window ended");
        }

        _statueFacingOwned = false;
    }

    private void ClearStatueGazes()
    {
        ReleaseStatueFacing();
        _statueGazes.Clear();
        if (_statueGazeOverrides)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(35758);
        }

        _statueGazeOverrides = false;
    }

    private static bool InLala() => Core.Me != null
        && WorldManager.ZoneId == 1176
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            LalaCenter) < 45
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.BaseId == 16441
        && b.IsAlive);
    private void RegisterLala()
    {
        AvoidanceHelpers.AddAvoidSquareDonut(InLala, 39, 39, 160, 160, () => new[] { LalaCenter });
        // 2026-09-30 05:27:56: Forward March3715 became Forced March3719 for
        // three seconds at 4y/s; the unprepared player hit the wall and gained
        // Burns3066. Stage within 4y of center while preparation is present, so
        // every 12y direction stays inside the 39y square without guessing rotation
        // icons or facing. The05:59 retry exposed Right Face3718 and redirected
        // pulses still resolving during preparation. Preserve tile escape FIRST,
        // then center after every forecast expires; never reserve an unsafe center.
        // Require a captured Plot seed, and release on3719 instead of steering it.
        AvoidanceHelpers.AddAvoidDonut(
            () => InLala(
            )
            && _lalaPlotSeeded
            && Core.Me.CharacterAuras.Any(
                a => a.Id >= 3715
            && a.Id <= 3718)
            && !Core.Me.HasAura(
                3719)
            && !_lalaTiles.Values.SelectMany(
                t => t).Any(
                    c => c.End > DateTime.UtcNow),
            () => new[] { LalaCenter },
            100,
            4);
        // Raw type 13/omen0 registered no avoid in the 05:15 baseline. Split the
        // reference 270-degree cone into three convex quarter fans, with a small
        // overlap at their seams; never interpret its visual as a full-room circle.
        for (var sector = -1; sector <= 1; sector++)
        {
            var turn = sector * (float)Math.PI / 2;
            AvoidanceManager.AddAvoidPolygon<Cleave>(
                InLala,
                null,
                80,
                c => -c.Heading + turn,
                _ => 1,
                _ => 15,
                _ => new[] { new Vector2(
                    0,
                    -.5f), new Vector2(
                        -61,
                        60), new Vector2(
                            61,
                            60) },
                c => c.Location,
                () => _lalaBlights.Values.Where(
                    c => c.End > DateTime.UtcNow),
                priority: AvoidancePriority.High);
        }

        // Advancing Bright Pulse tiles have no repeat cast. The shipped arrow's
        // heading gives their direction; helper headings stayed 0 even for eastward
        // rows. Publishing remaining tiles preserves common safe squares throughout
        // a line instead of walking into its next unannounced eight-yalm step.
        AvoidanceManager.AddAvoidPolygon<Cleave>(
            InLala,
            null,
            80,
            _ => 0,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -4.5f,
                -4.5f), new Vector2(
                    4.5f,
                    -4.5f), new Vector2(
                        4.5f,
                        4.5f), new Vector2(
                            -4.5f,
                            4.5f) },
            c => c.Location,
            () => _lalaTiles.Values.SelectMany(
                t => t).Where(
                    c => c.End > DateTime.UtcNow),
            priority: AvoidancePriority.High);
        // Flail Smash killed the baseline at 17.632 while7.6y from one landing.
        // Use each captured ground target, not the armadillo's outside-arena origin.
        // Reference25y separation plus 0.5 clearance leaves opposite square corners.
        AvoidanceManager.AddAvoidLocation<Cleave>(InLala, _ => 25.5f, c => c.Location, () => _lalaProximity.Values.Where(c => c.End > DateTime.UtcNow));
        // 2026-09-30 06:39:54: generic Rolling Spout 34945 used a4.8y hole;
        // the player stopped 4.67y from Kapokapo 16443 and gained vulnerability. The
        // corroborated damaging ring is4-12y, so shrink the hole to 3.5 and expand
        // the outside to 12.5. Retain one second past the captured cast end. During
        // Arcane Plot overlap, publish both hazards to the same native planner:
        // a donut hole is not safe until the advancing tile there has expired.
        AvoidanceHelpers.AddAvoidDonut(InLala, () => _lalaSpouts.Values.Where(c => c.End > DateTime.UtcNow).Select(c => c.Location).ToArray(), 12.5, 3.5);
    }

    private void UpdateLala()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(LalaCenter) < 65).ToArray();
        var boss = actors.FirstOrDefault(b => b.BaseId == 16441);
        var cast = boss != null && boss.IsCasting ? boss.CastingSpellId : 0;
        if (cast != _lalaLastCast && (cast == 34933 || cast == 34934))
        {
            _lalaTiles.Clear();
            _lalaPlotSeeded = false;
        }

        _lalaLastCast = cast;
        foreach (var actor in actors.Where(b => b.IsCasting))
        {
            var action = actor.CastingSpellId;
            var end = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
            if ((action == LalaRollingSpout || action == LalaRollingSpoutLong) && actor.BaseId == 16443)
            {
                _lalaSpouts[actor.ObjectId] = new Cleave
                {
                    Location = actor.Location,
                    End = end
                };
            }

            var collection = action == 34931 ? _lalaBlights : action == 34947 || action == 35436 ? _lalaProximity : null;
            if (collection != null)
            {
                if (!collection.TryGetValue(actor.ObjectId, out var hazard))
                {
                    collection[actor.ObjectId] = hazard = new Cleave();
                }

                hazard.Location = action == 34931 ? actor.Location : actor.SpellCastInfo.CastLocation;
                hazard.Heading = actor.Heading;
                hazard.End = end;
            }

            if (action == 36061 && actor.SpellCastInfo.TargetId == Core.Me.ObjectId)
            {
                _lalaGazeOrigin = actor.Location;
                _lalaGazeEnd = end;
                _lalaGazeStart = end.AddSeconds(-2.5);
            }
        }

        var arrows = GameObjectManager.GameObjects.Where(
            o => o.IsValid
            && o.IsVisible
            && (o.BaseId == 2013505
            || o.BaseId == 2013506)
            && o.Distance2D(
                LalaCenter) < 30).ToArray(
            );
        foreach (var arrow in arrows.Where(o => o.BaseId == 2013505))
        {
            var helper = actors.FirstOrDefault(b => b.IsCasting && b.CastingSpellId == 34936 && b.Distance2D(arrow.Location) < 1);
            // A matching initial cast establishes timing. A mid-sequence attach with
            // no such cast retains generic handling instead of inventing a new timer.
            if (helper == null)
            {
                continue;
            }

            var first = now + helper.SpellCastInfo.RemainingCastTime;
            var tiles = new List<Cleave>();
            var location = arrow.Location;
            var heading = arrow.Heading;
            // 2026-09-30 05:59: the westbound row turned north at (119,-870)
            // then east at (119,-862), hitting the unmodeled return row at 46.452.
            // Redirects change the NEXT eight-yalm step at their own tile. Follow
            // the actual arrow graph until it leaves the five-by-five floor, with
            // 25 steps and repeated-cell rejection guarding malformed/looping data.
            // Rebuild during the initial cast so later-arriving arrows are included;
            // after it ends retain the detached forecast through its final impact.
            for (var i = 0; i < 25 && Math.Abs(location.X - LalaCenter.X) <= 16.5f && Math.Abs(location.Z - LalaCenter.Z) <= 16.5f; i++)
            {
                if (tiles.Any(t => t.Location.Distance2D(location) < 1))
                {
                    break;
                }

                tiles.Add(new Cleave { Location = location, End = first.AddSeconds(1.2 * i + 1) });
                var redirect = arrows.FirstOrDefault(o => o.BaseId == 2013506 && o.Distance2D(location) < 1);
                if (redirect != null)
                {
                    heading = redirect.Heading;
                }

                location += new Vector3((float)Math.Sin(heading), 0, (float)Math.Cos(heading)) * 8;
            }

            _lalaTiles[arrow.ObjectId] = tiles.ToArray();
            _lalaPlotSeeded = true;
        }

        foreach (var collection in new[]
        {
            _lalaBlights,
            _lalaProximity,
            _lalaSpouts
        }

        )
        {
            foreach (var id in collection.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            {
                collection.Remove(id);
            }
        }

        foreach (var id in _lalaTiles.Where(p => p.Value.All(c => c.End <= now)).Select(p => p.Key).ToArray())
        {
            _lalaTiles.Remove(id);
        }

        if (_lalaTiles.Count > 0 && !_lalaPlotOverrides)
        {
            LlamaLibrary.Helpers.SideStep.Override(34936);
            LlamaLibrary.Helpers.SideStep.Override(34937);
            _lalaPlotOverrides = true;
        }

        if (_lalaTiles.Count == 0 && _lalaPlotOverrides)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(34936);
            LlamaLibrary.Helpers.SideStep.RemoveHandler(34937);
            _lalaPlotOverrides = false;
        }

        // Follow the actual exposed-side aura, including a rotation: the 05:29:24
        // capture changed Right3728 to Front3726 before impact and this refreshed
        // facing passed. Do not apply the VFX turn a second time to an updated aura.
        // Triple/quintuple variants still require their own live validation.
        var unseen = Core.Me.CharacterAuras.FirstOrDefault(a => a.Id >= 3726 && a.Id <= 3729);
        if (unseen != null)
        {
            _lalaUnseenOffset = unseen.Id == 3726 ? 0 : unseen.Id == 3727 ? (float)Math.PI : unseen.Id == 3728 ? -(float)Math.PI / 2 : (float)Math.PI / 2;
        }
    }

    private bool HandleLalaFacing()
    {
        var now = DateTime.UtcNow;
        if (now < _lalaGazeStart || now >= _lalaGazeEnd || !_lalaUnseenOffset.HasValue || Core.Me.HasAura(3721) || Core.Me.HasAura(3790))
        {
            ReleaseLalaFacing();
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        // First Analysis gave Back Unseen3727. Facing the boss normally gained a
        // vulnerability at 05:16:52.126. Hold the exposed side toward its helper for
        // the final1.5s plus 1s impact grace; returning true fences auto-facing actions.
        CapabilityManager.Update(_lalaFacingHandle, CapabilityFlags.Facing, 1000, "Aloalo Analysis exposed-side facing");
        if (!_lalaFacingOwned)
        {
            if (Core.Me.IsCasting)
            {
                ActionManager.StopCasting();
            }
        }

        _lalaFacingOwned = true;
        Navigator.PlayerMover.MoveStop();
        var delta = _lalaGazeOrigin - Core.Me.Location;
        Core.Me.SetFacing((float)Math.Atan2(delta.X, delta.Z) - _lalaUnseenOffset.Value);
        return true;
    }

    private void ReleaseLalaFacing()
    {
        if (_lalaFacingOwned)
        {
            CapabilityManager.Clear(_lalaFacingHandle, CapabilityFlags.Facing, "Aloalo Analysis facing ended");
        }

        _lalaFacingOwned = false;
    }

    private void ClearLala()
    {
        ReleaseLalaFacing();
        _lalaBlights.Clear();
        _lalaProximity.Clear();
        _lalaSpouts.Clear();
        _lalaTiles.Clear();
        _lalaLastCast = 0;
        _lalaPlotSeeded = false;
        _lalaGazeEnd = _lalaGazeStart = default;
        _lalaUnseenOffset = null;
        if (_lalaPlotOverrides)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(34936);
            LlamaLibrary.Helpers.SideStep.RemoveHandler(34937);
        }

        _lalaPlotOverrides = false;
    }

    // 2026-09-30 middle baseline:35745/36049 expose the opposite rotation directions
    // during a7.7s cast;35746 repeats only announce0.7s before damage. Reacting to
    // those repeats gave two vulnerabilities and a death. Seven crosses at 2.1s
    // spacing and 15-degree steps match the captured helper headings. Forecast current
    // and next only; reserving all future rotations would falsely occupy the room.
    // Two centered rectangles form each cross (24 reach,6 half-width,0.5 clearance).
    private void RegisterLances()
    {
        // 2026-09-30 06:13: native escape chose the northeast outer pocket,
        // then ran against the wall as the next crosses closed it (hit15.432,
        // death17.637). The earlier opposite arrangement passed through center.
        // The captured seven-step forecasts retain a connected safe region within
        // 10y for both arrangements. Exclude the outer dead ends during this paired
        // sequence; RB still chooses and executes the escape, not a manual waypoint.
        AvoidanceHelpers.AddAvoidDonut(() => InQuaqua() && _lances.Count == 2 && ActiveLances().Any(), () => new[] { QuaquaCenter }, 100, 10);
        for (var arm = 0; arm < 2; arm++)
        {
            var quarterTurn = arm * (float)Math.PI / 2;
            AvoidanceManager.AddAvoidPolygon<Cleave>(
                InQuaqua,
                null,
                80,
                c => -c.Heading + quarterTurn,
                _ => 1,
                _ => 15,
                _ => new[] { new Vector2(
                    -6.5f,
                    -24.5f), new Vector2(
                        6.5f,
                        -24.5f), new Vector2(
                            6.5f,
                            24.5f), new Vector2(
                                -6.5f,
                                24.5f) },
                c => c.Location,
                ActiveLances,
                priority: AvoidancePriority.High);
        }
    }

    private IEnumerable<Cleave> ActiveLances()
    {
        // Native avoidance can retain TreeStart while escaping. Advance detached
        // forecasts here too, so that it cannot wait forever on an expired cross.
        var now = DateTime.UtcNow;
        foreach (var lance in _lances.Values)
        {
            var index = Math.Max(0, (int)Math.Floor((now - lance.FirstImpact).TotalSeconds / 2.1) + 1);
            for (var ahead = 0; ahead < 2 && index + ahead < 7; ahead++)
            {
                var cross = lance.Crosses[ahead];
                cross.Location = lance.Location;
                cross.Heading = lance.Heading + lance.Step * (index + ahead);
                yield return cross;
            }
        }
    }

    private void UpdateLances()
    {
        var now = DateTime.UtcNow;
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.Distance2D(QuaquaCenter) < 30))
        {
            var action = actor.CastingSpellId;
            if (action != 35745 && action != 36049 && action != 35746)
            {
                continue;
            }

            // Actor wrappers do not survive this tick. Persist scalar geometry only.
            // 2026-09-30 06:53:43.484: a repeat ending42.557 still damaged the
            // player after the old0.6s forecast expired43.157. Hold1.1s past finish
            // so native replanning cannot cross the just-expired arm before its
            // delayed effect; retain the same current/next geometry and 2.1s spacing.
            var impact = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.1);
            if (action == 35745 || action == 36049)
            {
                if (!_lances.TryGetValue(actor.ObjectId, out var lance) || now > lance.FirstImpact.AddSeconds(14))
                {
                    lance = new RotatingLance
                    {
                        Location = actor.Location,
                        Heading = actor.Heading,
                        Step = (action == 35745 ? -1 : 1) * (float)Math.PI / 12,
                        FirstImpact = impact
                    };
                    _lances[actor.ObjectId] = lance;
                }
            }
            else if (_lances.TryGetValue(actor.ObjectId, out var lance))
            {
                var difference = actor.Heading - lance.Heading;
                while (difference > Math.PI)
                {
                    difference -= 2 * (float)Math.PI;
                }

                while (difference < -Math.PI)
                {
                    difference += 2 * (float)Math.PI;
                }

                var index = (int)Math.Round(difference / lance.Step);
                if (index >= 1 && index < 7)
                {
                    lance.FirstImpact = impact.AddSeconds(-2.1 * index);
                }
            }
        }

        foreach (var id in _lances.Where(p => now > p.Value.FirstImpact.AddSeconds(12.6)).Select(p => p.Key).ToArray())
        {
            _lances.Remove(id);
        }

        // Preserve generic handling when attaching mid-sequence without a direction
        // seed; suppress it only while our forecast can replace its short repeats.
        if (_lances.Count > 0 && !_lanceOverrides)
        {
            foreach (var id in new uint[]
            {
                35745,
                36049,
                35746
            }

            )
            {
                LlamaLibrary.Helpers.SideStep.Override(id);
            }

            _lanceOverrides = true;
        }

        if (_lances.Count == 0)
        {
            ClearLances();
        }
    }

    private void CancelLanceCastForEmergencyEscape()
    {
        if (!Core.Me.IsCasting)
        {
            _lanceCancelledCast = 0;
            return;
        }

        var cast = Core.Me.CastingSpellId;
        if (_lanceCancelledCast == cast || !AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        var player = Core.Me.Location;
        // 2026-09-30 retry: native escape began04:57:45.792 but Verstone finished
        // only 46.889; the late detour left the floor and crossed a later lance. Apply
        // the already-verified weather policy only inside a published lance cross.
        // Cancel that emergency cast; native avoidance still owns all movement and
        // the routine retains every ordinary damage/healing decision.
        if (!ActiveLances().Any(c =>
        {
            var dx = player.X - c.Location.X;
            var dz = player.Z - c.Location.Z;
            var across = Math.Abs(dx * Math.Cos(c.Heading) - dz * Math.Sin(c.Heading));
            var along = Math.Abs(dx * Math.Sin(c.Heading) + dz * Math.Cos(c.Heading));
            return (across <= 6.5 && along <= 24.5) || (along <= 6.5 && across <= 24.5);
        }))
        {
            return;
        }

        _lanceCancelledCast = cast;
        ActionManager.StopCasting();
    }

    private void ClearLances()
    {
        _lances.Clear();
        _lanceCancelledCast = 0;
        if (_lanceOverrides)
        {
            foreach (var id in new uint[]
            {
                35745,
                36049,
                35746
            }

            )
            {
                LlamaLibrary.Helpers.SideStep.RemoveHandler(id);
            }
        }

        _lanceOverrides = false;
    }

    private sealed class RotatingLance
    {
        internal Vector3 Location;
        internal float Heading, Step;
        internal DateTime FirstImpact;
        internal readonly Cleave[] Crosses =
        {
            new(),
            new()
        };
    }

    private async Task<bool> HandleHammerLanding()
    {
        var now = DateTime.UtcNow;
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.IsValid && b.BaseId == QuaquaBase && b.IsAlive);
        if (boss == null)
        {
            ClearHammer();
            return false;
        }

        if (boss.IsCasting && boss.CastingSpellId == 35725)
        {
            if (_hammerUntil <= now)
            {
                ClearHammer();
                var origin = boss.SpellCastInfo.CastLocation;
                // CastLocation is valid for Hammer Landing, unlike Rout's zeroed
                // helper origins. Reject an unrelated/stale point rather than move.
                if (origin.Distance2D(QuaquaCenter) > 22)
                {
                    return false;
                }

                _hammerLandings.Add(origin);
            }

            _hammerFirstImpact = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.3);
            _hammerUntil = _hammerFirstImpact.AddSeconds(7);
        }

        if (_hammerUntil <= now || _hammerLandings.Count == 0)
        {
            ClearHammer();
            return false;
        }

        // 2026-09-29's beacons settle on the rim in jump order. The third begins
        // near the center before moving outward; exclude that provisional position.
        // Keep scalar destinations because native wrappers are only valid this tick.
        if (_hammerLandings.Count < 3)
        {
            foreach (var beacon in GameObjectManager.GetObjectsOfType<BattleCharacter>(
                ).Where(
                    b => b.IsValid
                && b.IsVisible
                && b.BaseId == 0x40E0
                && b.Distance2D(
                    QuaquaCenter) >= 13
                && b.Distance2D(
                    QuaquaCenter) < 17))
            {
                if (_hammerLandings.Count < 3 && !_hammerLandings.Any(p => p.Distance2D(beacon.Location) < 1))
                {
                    _hammerLandings.Add(beacon.Location);
                }
            }
        }

        // Recorded boss arrivals match the three knockback origins. Advance on the
        // arrival, not a guessed2.1s timer; allow0.5s for the forced displacement before
        // walking toward the next origin. A deadline releases ownership if samples stop.
        if (now >= _hammerFirstImpact && _hammerIndex < _hammerLandings.Count && boss.Distance2D(_hammerLandings[_hammerIndex]) < 3)
        {
            ++_hammerIndex;
            _hammerResumeAt = now.AddSeconds(.5);
            if (_hammerMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            _hammerMoving = false;
        }

        if (_hammerIndex >= 3)
        {
            ClearHammer();
            return false;
        }

        if (_hammerLandings.Count < 3)
        {
            ReleaseHammerMovement();
            return false;
        }

        var source = _hammerLandings[_hammerIndex];
        var aim = (_hammerIndex < 2 ? _hammerLandings[_hammerIndex + 1] : QuaquaCenter) - source;
        aim.Y = 0;
        var length = (float)Math.Sqrt(aim.X * aim.X + aim.Z * aim.Z);
        if (length < 1)
        {
            ClearHammer();
            return false;
        }

        var direction = aim / length;
        var destination = source + direction * 2;
        destination.Y = QuaquaCenter.Y;
        // Hammer pushes20yalms. Standing2 toward the next beacon lands4 before it;
        // the final push aims through center. Require a0.5-yalm wall clearance for
        // both the standing point and projected landing before acquiring movement.
        var landing = destination + direction * 20;
        if (destination.Distance2D(
            QuaquaCenter) > 19.5
            || landing.Distance2D(
                QuaquaCenter) > 19.5
            || AvoidanceManager.Avoids.Any(
                a => a.IsPointInAvoid(
                    destination)
            || a.IsPointInAvoid(
                landing)))
        {
            ReleaseHammerMovement();
            return false;
        }

        CapabilityManager.Update(_hammerHandle, CapabilityFlags.Movement, 1000, "Aloalo hammer knockback staging");
        _hammerOwned = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _hammerMoving = false;
            return false;
        }

        if (now < _hammerResumeAt || Core.Me.Distance2D(destination) <= .3f)
        {
            if (_hammerMoving)
            {
                Navigator.PlayerMover.MoveStop();
                _hammerMoving = false;
            }

            return false; // Holding position must not starve healing or damage rotation.
        }

        _hammerMoving = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    private void ClearHammer()
    {
        ReleaseHammerMovement();
        _hammerLandings.Clear();
        _hammerIndex = 0;
        _hammerFirstImpact = _hammerUntil = _hammerResumeAt = default;
    }

    // An unknown/unsafe plan releases only this mechanic's movement. Keep captured
    // sequence evidence available for a later pulse without leaving a stale move active.
    private void ReleaseHammerMovement()
    {
        if (_hammerOwned)
        {
            CapabilityManager.Clear(_hammerHandle, CapabilityFlags.Movement, "Aloalo hammer sequence ended");
        }

        if (_hammerMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _hammerMoving = _hammerOwned = false;
    }

    private void RegisterQuaquaSequences()
    {
        // Radius6 plus 0.5 clearance matches the damaging helper; ordinary SideStep
        // warnings end before the next 0.7-second reported repeat allows a full escape.
        // Once a real storm helper is captured, this owner replaces both cast shapes
        // as well as predicting repeats, avoiding duplicate geometry from SideStep.
        AvoidanceManager.AddAvoidLocation<StormWave>(InQuaqua, _ => 6.5f, w => w.Next, () => _storm.Values.Where(w => w.Active));
        AvoidanceManager.AddAvoidLocation<StormWave>(
            InQuaqua,
            _ => 6.5f,
            w => w.Ahead,
            () => _storm.Values.Where(
                w => w.Active
            && w.Ahead.Distance2D(
                QuaquaCenter) < 25));
        // Rout's four helpers preview width 10 but the real boss charge is width 16.
        // Before all previews exist, native avoidance stages outside the first two.
        // Three simultaneous lanes forced a cross of the still-live first charge in
        // the bot. Once the complete timed route is feasible, one scoped owner handles
        // every lane together; unknown/late state restores native emergency handling.
        AvoidanceManager.AddAvoidPolygon<ChargeLane>(
            InQuaqua,
            null,
            80,
            lane => -lane.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -8.5f,
                -.5f), new Vector2(
                    8.5f,
                    -.5f), new Vector2(
                        8.5f,
                        45.5f), new Vector2(
                            -8.5f,
                            45.5f) },
            lane => lane.Location,
            () => _chargeLanes.Skip(
                _chargeIndex).Take(
                    2).Where(
                        _ => !_routOwned
            && DateTime.UtcNow < _chargeUntil),
            priority: AvoidancePriority.High);
    }

    private void UpdateQuaquaSequences()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(QuaquaCenter) < 50).ToArray();
        var boss = actors.FirstOrDefault(b => b.BaseId == QuaquaBase && b.IsAlive);
        if (boss == null)
        {
            return;
        }

        var cast = boss.IsCasting ? boss.CastingSpellId : 0;
        if (cast == 35728 && _quaquaLastCast != cast)
        {
            ClearChargeSequence();
            _chargeUntil = now.AddSeconds(25); // Bounded arm/preview/charge lifecycle.
        }

        if (cast == 35729)
        {
            if (_chargeUntil <= now)
            {
                _chargeUntil = now.AddSeconds(16);
            }

            // IsCasting can linger at zero after the announced end; repeatedly
            // reanchoring then would silently postpone every later charge deadline.
            var remaining = boss.SpellCastInfo.RemainingCastTime;
            if (remaining > TimeSpan.Zero || _chargeStart == default)
            {
                _chargeStart = now + remaining;
            }
        }

        _quaquaLastCast = cast;
        if (now < _chargeUntil)
        {
            foreach (var helper in actors.Where(b => b.IsCasting && b.CastingSpellId == 35731).OrderBy(b => b.SpellCastInfo.RemainingCastTime))
            {
                if (!_chargePreviews.Add(helper.ObjectId))
                {
                    continue;
                }

                // Live castLocation was zero; actor position and heading describe the
                // preview, and the next preview origin is the previous dash endpoint.
                _chargeLanes.Add(new ChargeLane { Location = helper.Location, Heading = helper.Heading });
            }

            if (_chargeLanes.Count > 0 && !_chargeOverrides)
            {
                LlamaLibrary.Helpers.SideStep.Override(35729);
                LlamaLibrary.Helpers.SideStep.Override(35731);
                _chargeOverrides = true;
            }

            if (_chargeStart != default && now > _chargeStart)
            {
                // Actor landings, not a guessed fixed repeat timer, advance the queue.
                // Three yalms accommodates a sampled dash end during its short animation.
                while (_chargeIndex + 1 < _chargeLanes.Count && boss.Distance2D(_chargeLanes[_chargeIndex + 1].Location) < 3)
                {
                    ++_chargeIndex;
                }

                // The final lane has no following preview; wait for its far end or the
                // bounded expiry. Never clear it merely because all preview casts ended.
                if (_chargeLanes.Count == 4 && _chargeIndex == 3)
                {
                    var last = _chargeLanes[3];
                    var delta = boss.Location - last.Location;
                    var along = delta.X * Math.Sin(last.Heading) + delta.Z * Math.Cos(last.Heading);
                    if (along > 38)
                    {
                        ClearChargeSequence();
                    }
                }
            }
        }
        else
        {
            ClearChargeSequence();
        }

        foreach (var helper in actors.Where(b => b.BaseId == 0x4135 && b.IsCasting && b.CastingSpellId is 35740 or 35741))
        {
            if (!_stormOverrides)
            {
                LlamaLibrary.Helpers.SideStep.Override(35740);
                LlamaLibrary.Helpers.SideStep.Override(35741);
                _stormOverrides = true;
            }

            if (!_storm.TryGetValue(helper.ObjectId, out var wave))
            {
                _storm[helper.ObjectId] = wave = new StormWave();
            }

            wave.Origin = helper.Location;
            wave.Advance = new Vector3((float)Math.Sin(helper.Heading), 0, (float)Math.Cos(helper.Heading)) * 6;
            // Native cast timers omit0.3s, and the observed damage can arrive later.
            // Retain 0.8s through the impact before advancing; every observed repeat
            // reanchors the forecast, so late pulses never accumulate timing drift.
            wave.Impact = now + helper.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.8);
            wave.Expiry = now.AddSeconds(17);
        }

        foreach (var key in _storm.Keys.Where(k => _storm[k].Expiry <= now).ToArray())
        {
            _storm.Remove(key);
        }

        foreach (var wave in _storm.Values)
        {
            int step = now <= wave.Impact ? 0 : 1 + (int)((now - wave.Impact).TotalSeconds / 2.1);
            wave.Next = wave.Origin + wave.Advance * step;
            wave.Ahead = wave.Next + wave.Advance;
            wave.Active = wave.Next.Distance2D(QuaquaCenter) < 25;
        }
    }

    private async Task<bool> HandleRout()
    {
        var now = DateTime.UtcNow;
        // The captured second layout drove native avoidance through lane1 while
        // preparing for lane3. Manual ownership is restricted to that corroborated
        // four-lane choreography, never ordinary Quaqua movement or unknown overlaps.
        if (_chargeLanes.Count != 4
            || _chargeStart == default
            || now >= _chargeUntil
            || now > _chargeStart.AddSeconds(
                _chargeIndex * 1.6 + 1.5)
            || _spheres.Values.Any(
                s => s.End > now)
            || _initialWaves.Values.Any(
                w => w.End > now)
            || _waves.Any(
                w => w.Active)
            || _storm.Values.Any(
                w => w.Active))
        {
            ReleaseRoutMovement();
            return false;
        }

        if (_routPath == null && now < _routReplanAt)
        {
            return false;
        }

        if (_routPath == null || now >= _routReplanAt)
        {
            // 2026-09-30 right Rout fell back to native avoidance because the
            // detached planner still tested the left arena's absolute boundary.
            // Keep the entire temporal plan arena-relative, restoring world space
            // only at the movement boundary; rotations and timings are unchanged.
            var center = new PlanPoint(QuaquaCenter.X, QuaquaCenter.Z);
            var start = new PlanPoint(Core.Me.Location.X, Core.Me.Location.Z) - center;
            var lanes = _chargeLanes.Select(l => (new PlanPoint(l.Location.X, l.Location.Z) - center, l.Heading)).ToArray();
            var first = (float)(_chargeStart - now).TotalSeconds;
            var retain = _routPath != null && AloaloRoutPlan.CanFollow(_routPath, (float)(now - _routPathAt).TotalSeconds, start, lanes, first, _chargeIndex);
            var path = retain ? _routPath : AloaloRoutPlan.Solve(start, lanes, first, _chargeIndex);
            _routReplanAt = now.AddMilliseconds(500);
            if (path == null)
            {

                ReleaseRoutMovement();
                return false;
            }

            _routPath = path;
            if (!retain)
            {
                _routPathAt = now;
            }

            _routOwned = true;
        }

        CapabilityManager.Update(_routHandle, CapabilityFlags.Movement, 1000, "Aloalo ordered Rout impacts");
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _routMoving = false;
            return false;
        }

        var index = Math.Min(_routPath.Count - 1, 1 + (int)((now - _routPathAt).TotalSeconds / .25));
        var point = _routPath[index];
        var destination = new Vector3(point.X + QuaquaCenter.X, QuaquaCenter.Y, point.Y + QuaquaCenter.Z);
        if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(destination)))
        {
            ReleaseRoutMovement();
            return false;
        }

        if (Core.Me.Distance2D(destination) <= AloaloRoutPlan.ArrivalTolerance)
        {
            if (_routMoving)
            {
                Navigator.PlayerMover.MoveStop();
                _routMoving = false;
            }

            return false; // A safe hold must still permit the routine to heal and cast.
        }

        _routMoving = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseRoutMovement()
    {
        if (_routOwned)
        {
            CapabilityManager.Clear(_routHandle, CapabilityFlags.Movement, "Aloalo Rout plan ended");
        }

        if (_routMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _routOwned = _routMoving = false;
        _routPath = null;
    }

    private void ClearChargeSequence()
    {
        ReleaseRoutMovement();
        if (_chargeOverrides)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(35729);
            LlamaLibrary.Helpers.SideStep.RemoveHandler(35731);
        }

        _chargeOverrides = false;
        _chargeLanes.Clear();
        _chargePreviews.Clear();
        _chargeUntil = default;
        _chargeStart = default;
        _chargeIndex = 0;
        _routReplanAt = default;
    }

    private void ClearQuaquaSequences()
    {
        ClearHammer();
        ClearChargeSequence();
        if (_stormOverrides)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(35740);
            LlamaLibrary.Helpers.SideStep.RemoveHandler(35741);
            _stormOverrides = false;
        }

        _storm.Clear();
        _quaquaLastCast = 0;
    }

    // These stable managed records contain no frame-scoped native objects. Restarting
    // outside a captured sequence leaves generic avoidance enabled until new evidence.
    private sealed class ChargeLane
    {
        internal Vector3 Location;
        internal float Heading;
    }

    private sealed class StormWave
    {
        internal Vector3 Origin, Advance, Next, Ahead;
        internal DateTime Impact, Expiry;
        internal bool Active;
    }

    private void RegisterKetuduke()
    {
        AvoidanceHelpers.AddAvoidSquareDonut(InKetuduke, 39, 39, 140, 140, () => new[] { KetudukeCenter });
        // Helper36114 at (-813,14,-395), facing east, pushes27yalms. An
        // eight-yalm staging circle intersects the west floor safely; native RB
        // chooses a point satisfying both this inverted circle and the arena boundary.
        AvoidanceHelpers.AddAvoidDonut(
            InKetuduke,
            () => _tidalWave != null
            && _tidalWave.End > DateTime.UtcNow ? new[] { _tidalWave.Location } : Array.Empty<Vector3>(
            ),
            100,
            8);
        // Helper35468 leaves a40x10 centered water strip after its cast. Keep it until
        // Anila's wind resolves; riding status3744 is the intended safe crossing.
        AvoidanceManager.AddAvoidPolygon<Cleave>(
            InKetuduke,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -5.5f,
                -20.5f), new Vector2(
                    5.5f,
                    -20.5f), new Vector2(
                        5.5f,
                        20.5f), new Vector2(
                            -5.5f,
                            20.5f) },
            c => c.Location,
            () => HydrosurgeActive
            && !Core.Me.HasAura(
                3744) ? new[] { _hydrosurge } : Array.Empty<Cleave>(
            ),
            priority: AvoidancePriority.High);
        // 2026-09-29 21:40:19: the next baited bomb drove movement across the previous
        // circle after generic avoidance removed it. Damage followed its reported finish
        // by 0.88s. Retain all pending ground circles through one second, with 0.5 clearance,
        // so RB solves the overlap instead of treating each completed cast as safe ground.
        AvoidanceManager.AddAvoidLocation<Cleave>(InKetuduke, _ => 5.5f, c => c.Location, () => _hydrobombs.Values.Where(c => c.End > DateTime.UtcNow));
        // 2026-09-30 03:08:52–03:09:03: Apa16659 cast 36116 with short tether57.
        // the player stayed 9.08 yalms away and received vulnerability1789 after resolution.
        // Use20yalms to stretch the proximity tether; add 0.5 clearance and retain
        // through the captured1.4s late damage. RB combines this circle with the arena
        // boundary and other mechanics; ordinary combat owns all movement/actions.
        AvoidanceManager.AddAvoidLocation<Cleave>(InKetuduke, _ => 20.5f, c => c.Location, () => _waterProximity.Values.Where(c => c.End > DateTime.UtcNow));
        // The14:42 baseline stood 4.91 yalms beside a crystal and was hit despite generic
        // rectangle avoidance. Use centered length 76/width 10, padded0.5 on every edge,
        // and forecast from appearance so even the 1-second wind follow-up has travel time.
        AvoidanceManager.AddAvoidLocation<Crystal>(
            InKetuduke,
            _ => 8.5f,
            c => c.Location,
            () => _crystals.Values.Where(
                c => c.Base == SphereCrystalBase
            && c.End > DateTime.UtcNow
            && (!HydrosurgeActive
            || c.CastSeen)));
        AvoidanceManager.AddAvoidPolygon<Crystal>(
            InKetuduke,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -5.5f,
                -38.5f), new Vector2(
                    5.5f,
                    -38.5f), new Vector2(
                        5.5f,
                        38.5f), new Vector2(
                            -5.5f,
                            38.5f) },
            c => c.Location,
            () => _crystals.Values.Where(
                c => c.Base == FlatCrystalBase
            && c.End > DateTime.UtcNow
            && (!HydrosurgeActive
            || c.CastSeen)),
            priority: AvoidancePriority.High);
        // Strewn Bubbles has no damage cast. Captured event2013494 appeared14:43:06,
        // inflicted damage14:43:12 and disappeared14:43:13. Its heading points along the
        // 20x10 half-row. Visibility owns the lifetime; pad all edges0.5 and combine the
        // four simultaneous strips, never the entire row in both directions.
        AvoidanceManager.AddAvoidPolygon<GameObject>(
            InKetuduke,
            null,
            80,
            o => -o.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -5.5f,
                -.5f), new Vector2(
                    5.5f,
                    -.5f), new Vector2(
                        5.5f,
                        20.5f), new Vector2(
                            -5.5f,
                            20.5f) },
            o => o.Location,
            () => GameObjectManager.GameObjects.Where(
                o => o.IsValid
            && o.IsVisible
            && o.BaseId == BubbleStrewerBase
            && o.Distance2D(
                KetudukeCenter) < 30),
            priority: AvoidancePriority.High);
        // The first circle and its fast inverse hit at 14:43:21/26. Publish only the current
        // stage; simultaneously avoiding the circle and donut would remove the whole floor.
        // RB owns all movement, including overlaps with crystals and bubble strips.
        // The first corrected run still gained a Near Tide vulnerability at 14:53:22 with
        // a sampled center distance14.6. Increase this circle's margin from 0.5 to 1 yalm;
        // preserve the already successful donut hole and verify the next resolution.
        AvoidanceManager.AddAvoidLocation<Tide>(InKetuduke, _ => 15f, t => t.Location, () => ActiveTides().Where(t => t.CircleNow));
        AvoidanceHelpers.AddAvoidDonut(InKetuduke, () => ActiveTides().Where(t => !t.CircleNow).Select(t => t.Location).ToArray(), 60.5, 7.5);
        // Sand-route35479 is the grounded Zaratan's180-degree cleave. The 2026-09-27
        // caster at (-800,-395), heading 0, hit the south half at 14:44:05. Its partner35480
        // and elevated helper 35481 are harmless; Updraft36112 is also a lift visual.
        // The60-radius half-disc covers this entire square half. A finite half-plane with
        // 0.5 edge padding is equivalent within the arena and avoids the missing generic cone.
        AvoidanceManager.AddAvoidPolygon<Cleave>(
            InKetuduke,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -60.5f,
                -.5f), new Vector2(
                    60.5f,
                    -.5f), new Vector2(
                        60.5f,
                        60.5f), new Vector2(
                            -60.5f,
                            60.5f) },
            c => c.Location,
            () => _lashings.Values.Where(
                c => c.End > DateTime.UtcNow),
            priority: AvoidancePriority.High);
        // 2026-09-29 21:17:55 repeats the 2026-09-27 bubble/tide failure: a moving
        // bubble bound the player outside Far Tide's safe hole.0.6s of prediction did
        // not protect the inward path against a3-yalm/s opposing bubble. Use a swept
        // capsule covering1.5s (4.5 yalms), still combined with the currently resolving
        // tide in RB avoidance. Circumscribed arcs preserve the full0.5-yalm clearance
        // between vertices; two disjoint endpoint circles would leave scalloped gaps.
        var bubbleSweep = BubbleSweepPolygon();
        AvoidanceManager.AddAvoidPolygon<Bubble>(
            InKetuduke,
            null,
            80,
            b => -b.Heading,
            _ => 1,
            _ => 15,
            _ => bubbleSweep,
            b => b.Location,
            () => _bubbles.Values.Where(
                _ => !_bubbleTideOwned),
            priority: AvoidancePriority.High);
        // Fluke Typhoon35461 moves bubbled crystals, not the player. Its generic40x40
        // rectangle drove the baseline toward the west edge and the next Saturate; the
        // forecast crystal geometry above owns safety throughout this harmless wind visual.
    }

    private bool HydrosurgeActive => _hydrosurge != null && DateTime.UtcNow >= _hydrosurgeStarts && DateTime.UtcNow < _hydrosurge.End;

    private void UpdateRescueMechanics()
    {
        var now = DateTime.UtcNow;
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.Distance2D(KetudukeCenter) < 40))
        {
            if (actor.BaseId == 9020 && actor.CastingSpellId == 36114)
            {
                _tidalWave = new Cleave
                {
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.7)
                };
            }

            if (actor.BaseId == 9020 && actor.CastingSpellId == 35468)
            {
                _hydrosurgeStarts = now + actor.SpellCastInfo.RemainingCastTime;
                // Captured wind resolves 43s after surge activation.60s is only a
                // stale-observation escape hatch, shortened by the actual wind cast.
                _hydrosurge = new Cleave
                {
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = _hydrosurgeStarts.AddSeconds(60)
                };
            }

            if (actor.BaseId == 16535 && actor.CastingSpellId == 35471 && _hydrosurge != null)
            {
                _hydrosurge.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
            }
        }
    }

    private async Task<bool> HandleWavefoam()
    {
        // This branch's stationary16532 bubbles are helpful; moving16533 bubbles
        // elsewhere remain hazards. Do not infer pickup from disappearance:3744
        // is the server acknowledgement. Holding a bubble yields to the routine.
        if (!HydrosurgeActive)
        {
            ReleaseWavefoam();
            return false;
        }

        if (Core.Me.HasAura(3744))
        {
            CapabilityManager.Update(_wavefoamHandle, CapabilityFlags.Movement, 1000, "Aloalo bubble ride");
            _wavefoamOwned = true;
            if (_wavefoamMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            {
                Navigator.PlayerMover.MoveStop();
            }

            _wavefoamMoving = false;
            return false;
        }

        var wind = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).FirstOrDefault(
                b => b.IsValid
            && b.IsVisible
            && b.BaseId == 16535
            && b.Distance2D(
                KetudukeCenter) < 30);
        if (wind == null)
        {
            ReleaseWavefoam();
            return false;
        }

        var direction = new Vector3((float)Math.Sin(wind.Heading), 0, (float)Math.Cos(wind.Heading));
        var lateral = new Vector3((float)Math.Cos(_hydrosurge.Heading), 0, -(float)Math.Sin(_hydrosurge.Heading));
        Func<Vector3, float> bank = p => (p.X - _hydrosurge.Location.X) * lateral.X + (p.Z - _hydrosurge.Location.Z) * lateral.Z;
        var playerBank = bank(Core.Me.Location);
        // Both endpoint checks matter: the wrong bank can ride20yalms outside the
        // square. A same-bank pickup cannot walk across the active midline; native
        // avoidance still owns any emergency escape while the pickup is pending.
        var bubbles = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsVisible
            && b.BaseId == 16532
            && b.Distance2D(
                KetudukeCenter) < 30
            && bank(
                b.Location) * playerBank > 0).Where(
                    b => Math.Abs(
                        bank(
                            b.Location)) > 5.5f
            && Math.Abs(
                (b.Location + direction * 20).X - KetudukeCenter.X) < 19.5f
            && Math.Abs(
                (b.Location + direction * 20).Z - KetudukeCenter.Z) < 19.5f
            && !AvoidanceManager.Avoids.Any(
                a => a.IsPointInAvoid(
                    b.Location))).OrderBy(
                        b => b.ObjectId == _wavefoamTarget ? 0 : 1).ThenBy(
                            b => b.Distance(
            )).ToArray(
            );
        var bubble = bubbles.FirstOrDefault();
        if (bubble == null)
        {
            ReleaseWavefoam();
            return false;
        }

        CapabilityManager.Update(_wavefoamHandle, CapabilityFlags.Movement, 1000, "Aloalo beneficial bubble pickup");
        _wavefoamOwned = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _wavefoamMoving = false;
            return false;
        }

        _wavefoamTarget = bubble.ObjectId;
        if (bubble.Distance2D(Core.Me.Location) < .3f)
        {
            if (_wavefoamMoving)
            {
                Navigator.PlayerMover.MoveStop();
            }

            _wavefoamMoving = false;
            return false;
        }

        _wavefoamMoving = true;
        Navigator.PlayerMover.MoveTowards(bubble.Location);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseWavefoam()
    {
        if (_wavefoamOwned)
        {
            CapabilityManager.Clear(_wavefoamHandle, CapabilityFlags.Movement, "Aloalo bubble pickup ended");
        }

        if (_wavefoamMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _wavefoamOwned = _wavefoamMoving = false;
        _wavefoamTarget = 0;
    }

    private void ClearRescueMechanics()
    {
        ReleaseWavefoam();
        _tidalWave = _hydrosurge = null;
        _hydrosurgeStarts = default;
    }

    private void UpdateCrystals()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && (b.BaseId == SphereCrystalBase
            || b.BaseId == FlatCrystalBase)
            && b.Distance2D(
                KetudukeCenter) < 30).ToArray(
            );
        foreach (var id in _crystals.Keys.Where(id => !actors.Any(b => b.ObjectId == id)).ToArray())
        {
            _crystals.Remove(id);
        }

        foreach (var actor in actors.Where(b => b.IsVisible))
        {
            if (!_crystals.TryGetValue(actor.ObjectId, out var crystal))
            {
                // Bubbled crystals waited27seconds between appearance and damage. Keep a
                // bounded60-second fallback, shortened to the actual cast finish once seen.
                _crystals[actor.ObjectId] = crystal = new Crystal
                {
                    Base = actor.BaseId,
                    Origin = actor.Location,
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = now.AddSeconds(60)
                };
            }

            if (actor.IsCasting && actor.CastingSpellId is 35452 or 35453 or 35454 or 35455)
            {
                crystal.CastSeen = true;
                crystal.Location = actor.Location;
                crystal.Heading = actor.Heading;
                // The14:42:55 vulnerability's remaining duration places damage about 0.9s
                // after RB's reported finish. One second retains the shape through that hit.
                crystal.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
            }
            else if (!crystal.CastSeen)
            {
                // Status3745 appeared before the move fromX-805 toX-785. Translate from
                // the original placement exactly once; following the in-flight position
                // and adding20 repeatedly would overshoot the real damage location.
                if (actor.HasAura(3745))
                {
                    crystal.Bubbled = true;
                }

                crystal.Location = crystal.Origin + (crystal.Bubbled ? new Vector3(crystal.Origin.X < KetudukeCenter.X ? 20 : -20, 0, 0) : Vector3.Zero);
            }
        }
        // Resolved but still visible crystals remain as inactive scalar entries until
        // disappearance, preventing the same actor from being forecast again after damage.
    }

    private void UpdateLashings()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _lashings.Keys.Where(id => _lashings[id].End <= now).ToArray())
        {
            _lashings.Remove(id);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.BaseId == 16538
            && b.IsCasting
            && b.CastingSpellId == 35479
            && b.Distance2D(
                KetudukeCenter) < 30))
        {
            // Bubble3745 marks the lifted, harmless partner; do not force avoidance of both
            // opposite halves if a future client exposes its visual through the same action.
            if (actor.HasAura(3745))
            {
                _lashings.Remove(actor.ObjectId);
                continue;
            }

            if (!_lashings.TryGetValue(actor.ObjectId, out var cleave))
            {
                _lashings[actor.ObjectId] = cleave = new Cleave();
            }

            cleave.Location = actor.Location;
            cleave.Heading = actor.Heading;
            cleave.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(500);
        }
    }

    private void UpdateHydrobombs()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _hydrobombs.Keys.Where(id => _hydrobombs[id].End <= now).ToArray())
        {
            _hydrobombs.Remove(id);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsCasting
            && b.CastingSpellId == 35490
            && b.Distance2D(
                KetudukeCenter) < 40))
        {
            var location = actor.SpellCastInfo.CastLocation;
            if (location.Distance2D(KetudukeCenter) > 35)
            {
                continue; // Reject missing/zero ground targets.
            }

            if (!_hydrobombs.TryGetValue(actor.ObjectId, out var bomb))
            {
                _hydrobombs[actor.ObjectId] = bomb = new Cleave();
            }

            bomb.Location = location;
            bomb.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }
    }

    private void UpdateWaterProximity()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _waterProximity.Keys.Where(id => _waterProximity[id].End <= now).ToArray())
        {
            _waterProximity.Remove(id);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsVisible
            && b.BaseId == 16659
            && b.IsCasting
            && b.CastingSpellId == 36116
            && b.Distance2D(
                KetudukeCenter) < 35))
        {
            // Snapshot scalar evidence only. Keep the exclusion even after the tether
            // becomes safe, otherwise normal rotation could immediately move back in.
            if (!_waterProximity.TryGetValue(actor.ObjectId, out var proximity))
            {
                _waterProximity[actor.ObjectId] = proximity = new Cleave();
            }

            proximity.Location = actor.Location;
            proximity.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }
    }

    private void UpdateBubbles()
    {
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.BaseId == 16533
            && b.IsVisible
            && b.Distance2D(
                KetudukeCenter) < 35).ToArray(
            );
        foreach (var id in _bubbles.Keys.Where(id => !actors.Any(b => b.ObjectId == id)).ToArray())
        {
            _bubbles.Remove(id);
        }

        foreach (var actor in actors)
        {
            if (!_bubbles.TryGetValue(actor.ObjectId, out var bubble))
            {
                _bubbles[actor.ObjectId] = bubble = new Bubble();
            }

            bubble.Location = actor.Location;
            bubble.Heading = actor.Heading;
        }
    }

    private static Vector2[] BubbleSweepPolygon()
    {
        var points = new List<Vector2>();
        // Eight segments per semicircle; divide by cos(half segment angle) so the
        // polygon edges, not just its vertices, clear the five-yalm body by 0.5.
        var radius = 5.5 / Math.Cos(Math.PI / 16);
        for (int i = 0; i <= 8; ++i)
        {
            var angle = i * Math.PI / 8;
            points.Add(new Vector2((float)(radius * Math.Cos(angle)), (float)(4.5 + radius * Math.Sin(angle))));
        }

        for (int i = 0; i <= 8; ++i)
        {
            var angle = Math.PI + i * Math.PI / 8;
            points.Add(new Vector2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle))));
        }

        return points.ToArray();
    }

    private void UpdateTides()
    {
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.IsValid && b.BaseId == KetudukeBase && b.IsCasting);
        if (boss == null)
        {
            return;
        }

        var action = boss.CastingSpellId;
        var now = DateTime.UtcNow;
        if (action is 35485 or 35487)
        {
            if (_tide == null || _tide.Action != action || _tide.End < now)
            {
                _tide = new Tide
                {
                    Action = action,
                    FirstCircle = action == 35485
                };
            }

            _tide.Location = boss.Location;
            var remaining = boss.SpellCastInfo.RemainingCastTime;
            // IsCasting can linger with zero remaining time. Reanchoring that tail
            // would postpone both planned impacts on every pulse rather than preserve
            // the announced finish, which made the fair-weather transition too late.
            if (remaining > TimeSpan.Zero || _tide.FirstReportedEnd == default)
            {
                _tide.FirstEnd = now + remaining + TimeSpan.FromSeconds(1);
                _tide.FirstReportedEnd = _tide.FirstEnd.AddSeconds(-1);
            }

            // The baseline missed the short follow-up cast between routine ticks. Preserve
            // its known inverse until six seconds after the first reported finish, covering
            // both captured impacts; observing the follow-up replaces this bounded fallback.
            _tide.End = _tide.FirstEnd.AddSeconds(5);
        }
        else if (action is 35486 or 35488 && _tide != null)
        {
            _tide.Location = boss.Location;
            _tide.FirstEnd = DateTime.MinValue;
            _tide.FirstCircle = action != 35486;
            _tide.End = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }
    }

    private IEnumerable<Tide> ActiveTides()
    {
        if (!_bubbleTideOwned && _tide != null && _tide.End > DateTime.UtcNow)
        {
            yield return _tide;
        }
    }

    private async Task<bool> HandleBubbleTide()
    {
        var now = DateTime.UtcNow;
        // Three captured failures show independent avoids choose an outside point
        // whose return path is cut off by the moving row. Own only this verified
        // eight-bubble overlap; unknown actors, binds or additional hazards fall back.
        if (_tide == null
            || _tide.FirstReportedEnd == default
            || _bubbles.Count != 8
            || now > _tide.FirstReportedEnd.AddSeconds(
                4.8)
            || Core.Me.HasAura(
                2518)
            || Core.Me.HasAura(
                3746)
            || _crystals.Values.Any(
                c => c.End > now)
            || _lashings.Values.Any(
                c => c.End > now)
            || _hydrobombs.Values.Any(
                c => c.End > now)
            || GameObjectManager.GameObjects.Any(
                o => o.IsValid
            && o.IsVisible
            && o.BaseId == BubbleStrewerBase))
        {
            ReleaseBubbleTide();
            return false;
        }

        if (_bubbleTidePath == null && now < _bubbleTideReplanAt)
        {
            return false;
        }

        if (now >= _bubbleTideReplanAt || _bubbleTidePath == null)
        {
            var start = new PlanPoint(Core.Me.Location.X, Core.Me.Location.Z);
            var center = new PlanPoint(_tide.Location.X, _tide.Location.Z);
            var hazards = _bubbles.Values.Select(
                b => (new PlanPoint(
                    b.Location.X,
                    b.Location.Z), new PlanPoint(
                        (float)Math.Sin(
                            b.Heading) * 3,
                        (float)Math.Cos(
                            b.Heading) * 3))).ToArray(
                );
            var first = (float)(_tide.FirstReportedEnd - now).TotalSeconds;
            // The22:23 capture reproduced an artificial dead end when every replan
            // shifted the one-yalm search lattice to the player's slightly different
            // position. Preserve a still-safe timed route instead of changing its
            // destination every half-second; fresh actor positions still validate it.
            var retain = _bubbleTidePath != null
                && AloaloBubbleTidePlan.CanFollow(
                    _bubbleTidePath,
                    (float)(now - _bubbleTidePathAt).TotalSeconds,
                    start,
                    center,
                    hazards,
                    first,
                    _tide.Action == 35485);
            var path = retain ? _bubbleTidePath : AloaloBubbleTidePlan.Solve(start, center, hazards, first, _tide.Action == 35485);
            _bubbleTideReplanAt = now.AddMilliseconds(500);
            if (path == null)
            {

                ReleaseBubbleTide();
                return false;
            }

            _bubbleTidePath = path;
            if (!retain)
            {
                _bubbleTidePathAt = now;
            }

            _bubbleTideOwned = true;
        }

        CapabilityManager.Update(_bubbleTideHandle, CapabilityFlags.Movement, 1000, "Aloalo timed bubble/tide route");
        // The previous native escape can finish its pulse, and any unrelated native
        // emergency keeps priority. Own tide and bubble shapes are suppressed together.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _bubbleTideMoving = false;
            return false;
        }

        var index = Math.Min(_bubbleTidePath.Count - 1, 1 + (int)((now - _bubbleTidePathAt).TotalSeconds / .25));
        var point = _bubbleTidePath[index];
        var destination = new Vector3(point.X, KetudukeCenter.Y, point.Y);
        if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(destination)))
        {
            ReleaseBubbleTide();
            return false;
        }

        if (Core.Me.Distance2D(destination) <= .15f)
        {
            if (_bubbleTideMoving)
            {
                Navigator.PlayerMover.MoveStop();
                _bubbleTideMoving = false;
            }

            // A routine action yielded at a transit waypoint; Resolution's 802ms coroutine
            // then delayed the next leg until its route was infeasible. A waypoint
            // arrival is not a safe scheduled hold when travel resumes within one
            // second (the observed 0.938s pulse gap rounded upward). Yield only this
            // bounded departure window; genuine holds still run healing/rotation.
            if (!AloaloBubbleTidePlan.CanYieldToRoutine(
                _bubbleTidePath,
                (float)(now - _bubbleTidePathAt).TotalSeconds,
                new PlanPoint(
                    Core.Me.Location.X,
                    Core.Me.Location.Z)))
            {
                await Coroutine.Yield();
                return true;
            }

            return false; // Scheduled waits must leave healing and rotation available.
        }

        _bubbleTideMoving = true;
        Navigator.PlayerMover.MoveTowards(destination);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseBubbleTide()
    {
        if (_bubbleTideOwned)
        {
            CapabilityManager.Clear(_bubbleTideHandle, CapabilityFlags.Movement, "Aloalo bubble/tide plan ended");
        }

        if (_bubbleTideMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _bubbleTideOwned = _bubbleTideMoving = false;
        _bubbleTidePath = null;
    }

    private static bool InKetuduke() => WorldManager.ZoneId == 1176
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            KetudukeCenter) < 45
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.BaseId == KetudukeBase
        && b.IsAlive);
    private void UpdateWeather()
    {
        var now = DateTime.UtcNow;
        var casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsCasting
            && b.CastingSpellId == 35735
            && b.Distance2D(
                QuaquaCenter) < 25).ToArray(
            );
        foreach (var id in _initialWaves.Keys.Where(id => _initialWaves[id].End <= now).ToArray())
        {
            _initialWaves.Remove(id);
        }

        // Deduplicate only the current cast so a later Howl still works if the client reuses
        // a helper identity. Finished casts must not suppress future waves from that actor.
        _weatherSources.RemoveWhere(id => !casters.Any(b => b.ObjectId == id));
        foreach (var actor in casters)
        {
            if (!_initialWaves.TryGetValue(actor.ObjectId, out var initial))
            {
                _initialWaves[actor.ObjectId] = initial = new Cleave();
            }

            initial.Location = actor.Location;
            initial.Heading = actor.Heading;
            // The recorded vulnerability arrived0.83s beyond RB's reported finish.
            // One second retains the warning through that server-impact window.
            var remaining = actor.SpellCastInfo.RemainingCastTime;
            // A zero-timer casting tail persisted after the announced finish in the
            // 21:51 run. Extending this fence on those ticks delayed the repeat warning
            // until a two-second player cast had already begun, producing another hit.
            if (remaining > TimeSpan.Zero || initial.End == default)
            {
                initial.End = now + remaining + TimeSpan.FromSeconds(1);
            }

            if (!_weatherSources.Add(actor.ObjectId))
            {
                continue;
            }

            // Both captures placed repeats6yalms beside each original line, advancing4yalms
            // every 2.1 seconds. The first damage was about 7.9 seconds after the 5-second initial
            // cast began: add 300ms omitted by RB's cast timer, then the 2.8-second travel delay.
            var first = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3.1);
            var side = new Vector3((float)Math.Cos(actor.Heading), 0, -(float)Math.Sin(actor.Heading));
            foreach (var sign in new[]
            {
                -1,
                1
            }

            )
            {
                _waves.Add(new Wave { Origin = actor.Location + side * (6 * sign), Advance = side * (4 * sign), Heading = actor.Heading, First = first });
            }
        }

        _waves.RemoveAll(w => now > w.First.AddSeconds(16));
    }

    private void CancelWeatherCastForEmergencyEscape()
    {
        if (!Core.Me.IsCasting)
        {
            _weatherCancelledCast = 0;
            return;
        }

        var cast = Core.Me.CastingSpellId;
        if (_weatherCancelledCast == cast || !AvoidanceManager.IsRunningOutOfAvoid)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var player = Core.Me.Location;
        // 2026-09-30 02:40:26: RB selected the correct repeat-wave escape while
        // Verfire continued, holding the player until the damage snapshot. Cancel only
        // a cast inside our currently published strip when native avoidance is already
        // escaping. RB retains destination/movement ownership; rotation resumes normally.
        // The same 0.5y padding and centered length as the registered polygons must apply.
        bool Inside(Vector3 center, float heading, float halfWidth)
        {
            var dx = player.X - center.X;
            var dz = player.Z - center.Z;
            return Math.Abs(dx * Math.Cos(heading) - dz * Math.Sin(heading)) <= halfWidth && Math.Abs(dx * Math.Sin(heading) + dz * Math.Cos(heading)) <= 25.5;
        }

        var initialActive = _initialWaves.Values.Any(w => w.End > now);
        var inside = initialActive ? _initialWaves.Values.Any(
            w => w.End > now
            && Inside(
                w.Location,
                w.Heading,
                4.5f)) : _waves.Any(
                    w => w.Active
            && Inside(
                w.Location,
                w.Heading,
                2.5f));
        if (!inside)
        {
            return;
        }

        _weatherCancelledCast = cast; // Latch before the client's stop acknowledgement.
        ActionManager.StopCasting();
    }

    private void UpdateSpheres()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _spheres.Keys.Where(id => _spheres[id].End <= now).ToArray())
        {
            _spheres.Remove(id);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.BaseId == ChargeBase
            && b.IsVisible
            && b.Distance2D(
                QuaquaCenter) < 25))
        {
            // Live tether256 became35722, and 257 became35723. The tether disappears before
            // damage, so retain detached geometry and replace its expiry with the actual cast.
            // A15-second fallback bounds a missing cast; never keep a native wrapper across ticks.
            var tether = actor.VfxContainer.Tethers.Select(t => t.Id).FirstOrDefault(id => id is 256 or 257);
            var action = actor.IsCasting && actor.CastingSpellId is Axe or Quoit ? actor.CastingSpellId : tether == 256 ? Axe : tether == 257 ? Quoit : 0;
            if (action == 0)
            {
                continue;
            }

            if (!_spheres.TryGetValue(actor.ObjectId, out var sphere) || sphere.Action != action)
            {
                _spheres[actor.ObjectId] = sphere = new Sphere
                {
                    End = now.AddSeconds(15)
                };
            }

            sphere.Action = action;
            sphere.Location = actor.Location;
            // RB reports1.7 seconds for these2-second casts. Quoit damage at 14:19:09.126 was
            // about 0.8 seconds beyond the reported finish; preserve1second for the observed
            // server-impact delay so routine movement cannot leave its safe hole too early.
            // Do not extend the fallback every frame or re-extend it after the damage cast:
            // a lingering tether must not preserve stale geometry into the next pattern.
            if (actor.IsCasting)
            {
                sphere.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
            }
        }
    }

    private static bool InQuaqua() => WorldManager.ZoneId == 1176
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            QuaquaCenter) < 45
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.BaseId == QuaquaBase
        && b.IsAlive);
    // Stable managed identities keep avoidance from treating every pulse as a new hazard.
    private sealed class Sphere
    {
        internal uint Action;
        internal Vector3 Location;
        internal DateTime End;
    }

    // Detach placement and phase state from frame-scoped native wrappers.
    private sealed class Crystal
    {
        internal uint Base;
        internal Vector3 Origin;
        internal Vector3 Location;
        internal float Heading;
        internal bool Bubbled;
        internal bool CastSeen;
        internal DateTime End;
    }

    private sealed class Tide
    {
        internal uint Action;
        internal Vector3 Location;
        internal bool FirstCircle;
        internal DateTime FirstEnd;
        internal DateTime FirstReportedEnd;
        internal DateTime End;
        internal bool CircleNow => DateTime.UtcNow <= FirstEnd ? FirstCircle : !FirstCircle;
    }

    // Detached initial cast plus independently stable polygon identities; no live
    // actor wrapper survives a tick. The pair is current/next, not two movement owners.
    // A single spawn's warning persists after its short animation returns to idle;
    // Finished prevents the explosion or stale position from rearming the hazard.
    private sealed class StaticeBox
    {
        public Vector3 Location, Origin;
        public float Heading;
        public DateTime End;
        public bool Dangerous, Finished;
    }

    private sealed class StaticeFire
    {
        internal Vector3 Location;
        internal float Heading, Step;
        internal bool Long;
        internal DateTime FirstEnd;
        internal readonly Cleave[] Strips =
        {
            new(),
            new()
        };
    }

    private sealed class Cleave
    {
        internal Vector3 Location;
        internal float Heading;
        internal DateTime End;
    }

    private sealed class Bubble
    {
        internal Vector3 Location;
        internal float Heading;
    }

    // Managed forecast identities are stable while their next strip advances; inactive actors
    // are parked inside the room, so their visibility alone must never create wave avoidance.
    private sealed class Wave
    {
        internal Vector3 Origin;
        internal Vector3 Advance;
        internal float Heading;
        internal DateTime First;
        // Compute phase when native avoidance requests geometry. Cached Active/Location
        // values waited for the next encounter coroutine tick during an instant-action
        // animation lock, delaying the 21:51 repeat warning by roughly half a second.
        // These properties use only detached managed data, never game-memory wrappers.
        internal Vector3 Location => Origin + Advance * Math.Max(0, (int)Math.Floor((DateTime.UtcNow - First).TotalSeconds / 2.1 - .3 / 2.1) + 1);
        internal bool Active => DateTime.UtcNow >= First.AddSeconds(-2.1) && DateTime.UtcNow <= First.AddSeconds(16) && Location.Distance2D(QuaquaCenter) <= 23;
    }
}

// BEGIN DETACHED STATUE GAZE PLANNER
// A paired gaze needs one heading outside both forward hemispheres. Averaging
// normalized away directions maximizes their shared clearance without assuming
// which wall is active. 2026-09-30 diagonal pairs have opposing origins, so an
// away-vector sum can cancel or reject a valid perpendicular heading. Gaze checks
// use a45-degree forward cone; select the widest angular gap with 10 degrees extra
// clearance. Coincident origins or a fully covered circle fail closed.
internal static class AloaloStatueGazePlan
{
    internal static bool TryHeading(PlanPoint player, PlanPoint[] sources, out float heading)
    {
        heading = 0;
        if (sources.Length == 0)
        {
            return false;
        }

        var toward = new List<PlanPoint>();
        foreach (var source in sources)
        {
            var delta = source - player;
            if (delta.LengthSquared() < .01f)
            {
                return false;
            }

            toward.Add(PlanPoint.Normalize(delta));
        }

        var angles = toward.Select(direction => (float)Math.Atan2(direction.X, direction.Y)).OrderBy(a => a).ToArray();
        var widest = 0f;
        for (var i = 0; i < angles.Length; i++)
        {
            var next = i + 1 < angles.Length ? angles[i + 1] : angles[0] + 2 * (float)Math.PI;
            var gap = next - angles[i];
            if (gap <= widest)
            {
                continue;
            }

            widest = gap;
            heading = angles[i] + gap / 2;
        }

        return widest / 2 >= 55 * (float)Math.PI / 180;
    }
}

// END DETACHED STATUE GAZE PLANNER
// BEGIN DETACHED ROUT PLANNER
// The22:41 2026-09-29 trace crosses a still-live first lane to avoid a future
// third lane. Plan all four resolutions on one time axis instead of treating
// preview order as simultaneous geometry. The replay extracts this exact class.
internal static class AloaloRoutPlan
{
    // the bot stopped 0.156 yalm from a held waypoint while the 0.15 check still
    // consumed combat ticks. Accept0.2 and reserve that same distance in planned
    // geometry, so yielding to the routine cannot spend the required0.5 safety margin.
    internal const float ArrivalTolerance = .2f;
    internal static List<PlanPoint> Solve(PlanPoint start, (PlanPoint Origin, float Heading)[] lanes, float first, int completed)
    {
        const float dt = .25f;
        // 2026-09-30 15:18 replay: a whole-yalm lattice reported no route from
        // (-12.623,5.854), although nearby starts were feasible. Half-yalm nodes
        // retain narrow corridors; a1.5y step matches the measured6y/s mover.
        // Keep segment intersection and arrival reserve unchanged: this expands
        // the search, not the allowed damage footprint or arena boundary.
        const float spacing = .5f;
        var steps = (int)Math.Ceiling((first + 6.3f) / dt);
        if (lanes.Length != 4 || completed < 0 || completed > 3 || steps <= 0 || steps > 48)
        {
            return null;
        }

        var layers = new List<Dictionary<(int X, int Z), Node>>
        {
            new()
            {
                [(0, 0)] = new Node(0, default)
            }
        };
        for (var step = 1; step <= steps; ++step)
        {
            var states = new Dictionary<(int X, int Z), Node>();
            foreach (var entry in layers[step - 1])
            {
                var a = start + new PlanPoint(entry.Key.X, entry.Key.Z) * spacing;
                for (var dx = -3; dx <= 3; ++dx)
                {
                    for (var dz = -3; dz <= 3; ++dz)
                    {
                        if (dx * dx + dz * dz > 9)
                        {
                            continue; // At most1.5y per quarter-second.
                        }

                        var key = (X: entry.Key.X + dx, Z: entry.Key.Z + dz);
                        var b = start + new PlanPoint(key.X, key.Z) * spacing;
                        var cost = entry.Value.Cost + (float)Math.Sqrt(dx * dx + dz * dz) * spacing * (1 + step * .03f);
                        if (states.TryGetValue(key, out var old) && old.Cost <= cost)
                        {
                            continue;
                        }

                        if (!SafeSegment(a, b, (step - 1) * dt, dt, lanes, first, completed, ArrivalTolerance))
                        {
                            continue;
                        }

                        states[key] = new Node(cost, entry.Key);
                    }
                }
            }

            if (states.Count == 0)
            {
                return null;
            }

            layers.Add(states);
        }

        var last = layers[layers.Count - 1].OrderBy(e => e.Value.Cost).First().Key;
        var path = new List<PlanPoint>();
        for (var step = steps; step >= 0; --step)
        {
            path.Add(start + new PlanPoint(last.X, last.Z) * spacing);
            last = layers[step][last].Previous;
        }

        path.Reverse();
        return path;
    }

    // Retain a safe path to avoid the lattice-origin instability established in
    // the bubble/tide capture. Updated player position and observed completed lanes
    // can invalidate it; this validation never advances a native completion index.
    internal static bool CanFollow(List<PlanPoint> path, float age, PlanPoint start, (PlanPoint Origin, float Heading)[] lanes, float first, int completed)
    {
        if (path == null || path.Count < 2 || lanes.Length != 4 || age < 0 || first + 6.3f > 12)
        {
            return false;
        }

        const float tick = 1f / 30;
        var point = start;
        for (var elapsed = 0f; elapsed < first + 6.3f; elapsed += tick)
        {
            var duration = Math.Min(tick, first + 6.3f - elapsed);
            var index = Math.Min(path.Count - 1, 1 + (int)((age + elapsed) / .25f));
            var delta = path[index] - point;
            var next = delta.Length() > ArrivalTolerance ? point + PlanPoint.Normalize(delta) * Math.Min(delta.Length(), 6 * duration) : point;
            if (!SafeSegment(point, next, elapsed, duration, lanes, first, completed))
            {
                return false;
            }

            point = next;
        }

        return true;
    }

    private static bool SafeSegment(PlanPoint a, PlanPoint b, float time, float duration, (PlanPoint Origin, float Heading)[] lanes, float first, int completed, float arrivalReserve = 0)
    {
        var radius = 19.5f - arrivalReserve;
        // Inputs are relative to the captured arena center, including right-route
        // helper origins; an absolute left-only bound silently rejects other lanes.
        if (b.LengthSquared() > radius * radius)
        {
            return false;
        }

        for (var i = completed; i < lanes.Length; ++i)
        {
            // Both 2026-09-29 layouts land roughly 1.6s apart. Reserve each lane
            // from 0.25s before its announced start through 1.5s afterward, covering
            // the charge animation, delayed impact and sampled landing. Observed
            // actor arrival retires completed lanes; unknown timing fails closed.
            var begin = first + i * 1.6f - .25f;
            var end = first + i * 1.6f + 1.5f;
            if (end < time || begin > time + duration)
            {
                continue;
            }

            var p = PlanPoint.Lerp(a, b, Math.Clamp((begin - time) / duration, 0, 1)) - lanes[i].Origin;
            var q = PlanPoint.Lerp(a, b, Math.Clamp((end - time) / duration, 0, 1)) - lanes[i].Origin;
            var forward = new PlanPoint((float)Math.Sin(lanes[i].Heading), (float)Math.Cos(lanes[i].Heading));
            var side = new PlanPoint(forward.Y, -forward.X);
            var localA = new PlanPoint(PlanPoint.Dot(p, side), PlanPoint.Dot(p, forward));
            var localB = new PlanPoint(PlanPoint.Dot(q, side), PlanPoint.Dot(q, forward));
            // The width 16 real charge needs0.5 clearance; width 10 previews are not
            // the damage footprint.45.5 forward covers either captured layout.
            var lo = 0f;
            var hi = 1f;
            var width = 8.5f + arrivalReserve;
            if (IntersectsSlab(
                localA.X,
                localB.X - localA.X,
                -width,
                width,
                ref lo,
                ref hi)
                && IntersectsSlab(
                    localA.Y,
                    localB.Y - localA.Y,
                    -.5f - arrivalReserve,
                    45.5f + arrivalReserve,
                    ref lo,
                    ref hi))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IntersectsSlab(float origin, float delta, float min, float max, ref float lo, ref float hi)
    {
        if (Math.Abs(delta) < .00001f)
        {
            return origin >= min && origin <= max;
        }

        var a = (min - origin) / delta;
        var b = (max - origin) / delta;
        lo = Math.Max(lo, Math.Min(a, b));
        hi = Math.Min(hi, Math.Max(a, b));
        return lo <= hi;
    }

    private readonly struct Node
    {
        internal readonly float Cost;
        internal readonly (int X, int Z) Previous;
        internal Node(float cost, (int X, int Z) previous)
        {
            Cost = cost;
            Previous = previous;
        }
    }
}

// END DETACHED ROUT PLANNER
// BEGIN DETACHED BUBBLE/TIDE PLANNER
// Kept in the dungeon's single source file. The offline harness extracts this exact
// class, so replay checks exercise production planning without loading native RB APIs.
internal static class AloaloBubbleTidePlan
{
    // Routine actions can occupy the TreeStart scheduling path for 0.938s in the
    // captured host even when their game action is instant. Reserve one second
    // before a timed departure, not the whole overlap or an arbitrary combat pause.
    internal static bool CanYieldToRoutine(List<PlanPoint> path, float age, PlanPoint position)
    {
        if (path == null || path.Count == 0 || age < 0)
        {
            return false;
        }

        var first = Math.Min(path.Count - 1, 1 + (int)(age / .25f));
        var last = Math.Min(path.Count - 1, 1 + (int)((age + 1) / .25f));
        for (var i = first; i <= last; ++i)
        {
            if (PlanPoint.Distance(path[i], position) > .15f)
            {
                return false;
            }
        }

        return true;
    }

    // Replay the existing follower from the measured player position with current
    // bubble positions. Keeping its original time axis avoids a re-anchored search
    // lattice inventing a dead end, but never retains a route merely because an old
    // forecast was safe. Thirty-Hz segments check the complete remaining overlap;
    // displacement or changed actor motion can still require a new route/fallback.
    internal static bool CanFollow(List<PlanPoint> path, float age, PlanPoint start, PlanPoint center, (PlanPoint Position, PlanPoint Velocity)[] hazards, float first, bool firstCircle)
    {
        if (path == null || path.Count < 2 || age < 0 || hazards.Length != 8 || first + 4.8f > 12)
        {
            return false;
        }

        const float tick = 1f / 30;
        var point = start;
        for (var elapsed = 0f; elapsed < first + 4.8f; elapsed += tick)
        {
            var duration = Math.Min(tick, first + 4.8f - elapsed);
            var index = Math.Min(path.Count - 1, 1 + (int)((age + elapsed) / .25f));
            var delta = path[index] - point;
            var next = delta.Length() > .15f ? point + PlanPoint.Normalize(delta) * Math.Min(delta.Length(), 6 * duration) : point;
            if (!Allowed(
                next,
                center,
                elapsed + duration,
                first,
                firstCircle)
                || !ClearTideSegment(
                    point,
                    next,
                    center,
                    elapsed,
                    first,
                    firstCircle,
                    duration)
                || !ClearSegment(
                    point,
                    next,
                    elapsed,
                    hazards,
                    duration))
            {
                return false;
            }

            point = next;
        }

        return true;
    }

    // Quarter-second layers and one-yalm cardinal/diagonal moves never exceed normal
    // six-yalm/s running speed. Every segment checks relative bubble motion, not just
    // its endpoints. The two impact windows preserve the observed server delay while
    // allowing travel before an announced attack actually resolves.
    internal static List<PlanPoint> Solve(PlanPoint start, PlanPoint center, (PlanPoint Position, PlanPoint Velocity)[] hazards, float first, bool firstCircle)
    {
        const float dt = .25f;
        var steps = (int)Math.Ceiling((first + 4.8f) / dt);
        if (steps <= 0 || steps > 48 || hazards.Length != 8)
        {
            return null;
        }

        var layers = new List<Dictionary<(int X, int Z), Node>>
        {
            new()
            {
                [(0, 0)] = new Node(0, default)
            }
        };
        for (var step = 1; step <= steps; ++step)
        {
            var states = new Dictionary<(int X, int Z), Node>();
            foreach (var entry in layers[step - 1])
            {
                var a = start + new PlanPoint(entry.Key.X, entry.Key.Z);
                for (var dx = -1; dx <= 1; ++dx)
                {
                    for (var dz = -1; dz <= 1; ++dz)
                    {
                        var key = (X: entry.Key.X + dx, Z: entry.Key.Z + dz);
                        var b = start + new PlanPoint(key.X, key.Z);
                        // Prefer completing necessary travel early. The22:02 live route
                        // delayed a short move until a routine animation lock consumed its
                        // remaining slack, forcing a safe but avoidable fallback/reacquire.
                        // A small time penalty breaks equal-distance ties toward early staging.
                        var cost = entry.Value.Cost + (float)Math.Sqrt(dx * dx + dz * dz) * (1 + step * .03f);
                        if (states.TryGetValue(key, out var old) && old.Cost <= cost)
                        {
                            continue;
                        }

                        if (!Allowed(
                            b,
                            center,
                            step * dt,
                            first,
                            firstCircle)
                            || !ClearTideSegment(
                                a,
                                b,
                                center,
                                (step - 1) * dt,
                                first,
                                firstCircle)
                            || !ClearSegment(
                                a,
                                b,
                                (step - 1) * dt,
                                hazards))
                        {
                            continue;
                        }

                        states[key] = new Node(cost, entry.Key);
                    }
                }
            }

            if (states.Count == 0)
            {
                return null;
            }

            layers.Add(states);
        }

        var last = layers[layers.Count - 1].OrderBy(e => e.Value.Cost).First().Key;
        var path = new List<PlanPoint>();
        for (var step = steps; step >= 0; --step)
        {
            path.Add(start + new PlanPoint(last.X, last.Z));
            last = layers[step][last].Previous;
        }

        path.Reverse();
        return path;
    }

    private static bool Allowed(PlanPoint point, PlanPoint center, float time, float first, bool firstCircle)
    {
        // Ketuduke's captured40x40 floor, inset0.5; preserve square corners.
        if (Math.Abs(point.X + 790) > 19.5 || Math.Abs(point.Y + 395) > 19.5)
        {
            return false;
        }

        var radius = PlanPoint.Distance(point, center);
        if (time >= first + .2f && time <= first + 1 && (firstCircle ? radius < 15 : radius > 7.5))
        {
            return false;
        }

        if (time >= first + 3.8f && (firstCircle ? radius > 7.5 : radius < 15))
        {
            return false;
        }

        return true;
    }

    private static bool ClearSegment(PlanPoint a, PlanPoint b, float time, (PlanPoint Position, PlanPoint Velocity)[] hazards, float duration = .25f)
    {
        foreach (var hazard in hazards)
        {
            var relative = a - hazard.Position - hazard.Velocity * time;
            var delta = b - a - hazard.Velocity * duration;
            var length = delta.LengthSquared();
            var u = length > 0 ? Math.Clamp(-PlanPoint.Dot(relative, delta) / length, 0, 1) : 0;
            // Five-yalm contact body plus 0.5 measured clearance, including movement
            // between samples; endpoint-only checks allow crossing a bubble unnoticed.
            if ((relative + u * delta).LengthSquared() < 5.5f * 5.5f)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ClearTideSegment(PlanPoint a, PlanPoint b, PlanPoint center, float time, float first, bool firstCircle, float duration = .25f)
    {
        // A damage window can begin between grid samples. Check that partial segment
        // analytically; checking only discrete destinations permitted a late circle exit.
        return SafeWindow(first + .2f, first + 1, firstCircle) && SafeWindow(first + 3.8f, float.MaxValue, !firstCircle);
        bool SafeWindow(float begin, float end, bool outside)
        {
            if (end < time || begin > time + duration)
            {
                return true;
            }

            var p = PlanPoint.Lerp(a, b, Math.Clamp((begin - time) / duration, 0, 1)) - center;
            var q = PlanPoint.Lerp(a, b, Math.Clamp((end - time) / duration, 0, 1)) - center;
            if (!outside)
            {
                return Math.Max(p.LengthSquared(), q.LengthSquared()) <= 7.5f * 7.5f;
            }

            var delta = q - p;
            var length = delta.LengthSquared();
            var u = length > 0 ? Math.Clamp(-PlanPoint.Dot(p, delta) / length, 0, 1) : 0;
            return (p + u * delta).LengthSquared() >= 15 * 15;
        }
    }

    private readonly struct Node
    {
        internal readonly float Cost;
        internal readonly (int X, int Z) Previous;
        internal Node(float cost, (int X, int Z) previous)
        {
            Cost = cost;
            Previous = previous;
        }
    }
}
