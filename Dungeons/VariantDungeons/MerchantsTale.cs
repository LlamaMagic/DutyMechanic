using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;
/// <summary>
/// Handles Merchant's Tale's solo Variant bosses across its route arenas.
/// OrderBot and the combat routine retain combat; DutyMechanic owns boss hazards and restores SideStep for trash.
/// Advanced and Criterion territories are deliberately outside this handler's registration.
/// </summary>
public sealed class MerchantsTale : AbstractDungeon
{
    // The 2026-09-27 capture distinguishes boss 18535 from same-name helper 9020.
    // Gate positions and cast origins establish a fixed square centered here;
    // the boss's initial position is offset from that center.
    private const uint GenieBase = 18535;
    private static readonly Vector3 ResidentialGenieCenter = new(-750, -34, -415);
    // Shipped market floor center: gatesZ 769/805 and the same 35y square. Resolve
    // the nearby floor before applying common relative forecasts; InGenie also
    // requires the real living boss. Branch-specific spells keep their owner.
    private static readonly Vector3 MarketGenieCenter = new(160, -38, 787);
    private static Vector3 GenieCenter => Core.Me != null && Core.Me.Distance2D(MarketGenieCenter) < 60 ? MarketGenieCenter : ResidentialGenieCenter;

    // These helpers were observed in the 2026-09-27 bounded baseline. Generic fans inflate
    // angles by 1.55 and remove all safe wedges; late Voyage casts cannot express the preview.
    private static readonly uint[] OwnedActions =
    {
        43353,
        43354,
        43349,
        44254,
        44255,
        44261,
        45586,
        44771,
        44821
    };
    private static readonly uint[] VoyageActions =
    {
        47042,
        43360,
        43361,
        43362,
        43363,
        43364,
        43646,
        43674,
        43679,
        43729
    };
    private readonly Dictionary<uint, CastShape> _casts = new();
    private readonly Dictionary<uint, ExplosionLine> _explosions = new();
    private readonly HashSet<uint> _previousExplosionCasts = new();
    private Voyage _voyage;
    private bool _ownsVoyage;
    private readonly CapabilityManagerHandle _mechanicGapCloserHandle = CapabilityManager.CreateNewHandle();
    private bool _ownsGapCloserBlock;
    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)1315;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new()
    {
        43344
    }; // Rub Burn, observed in the final 15:57 Genie cycle.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = new()
    {
        43343,
        43345,
        43346,
        45515,
        45480,
        45489
    }; // Captured Genie raidwides; Pari Heat Burst, Spurning Flames and Scouring Scorn.

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        RegisterNativeBossAvoidance();
        SetMechanicGapCloserBlock(false);
        ClearForecasts();
        RegisterPariAvoidance();
        RegisterDaryaAvoidance();
        RegisterRukhkhAvoidance();
        RegisterSwordmasterAvoidance();
        RegisterDandanAvoidance();
        // At 15:27:08, generic Firecrackers avoidance choseZ-384.475 and ran through the
        // still-open entrance, resetting the boss. Constrain RB's shared path selection to
        // the 35x 35 floor with 0.5-yalm inset on every edge; preserve both safe corner wedges.
        // This is a navigation constraint, not a competing manual movement owner.
        AvoidanceHelpers.AddAvoidSquareDonut(InGenie, 34, 34, 140, 140, () => new[] { GenieCenter });
        // All cast geometry goes through one earliest-impact set. Two cannon waves and the
        // following Firecrackers must not reserve their mutually exclusive future safe regions.
        // Voyage resolves before Fanning Flame; defer those later fans until the ship hold ends.
        AvoidanceManager.AddAvoidPolygon<CastShape>(InGenie, null, 80, c => -c.Heading, _ => 1, _ => 15, ShapePoints, c => c.Location, ActiveCasts, priority: AvoidancePriority.High);
        // Pyromagicks repeats have no cast bars. The live helpers stepped 8yalms roughly 2.4s
        // apart, unlike a shorter reference cadence. Current plus the next observed-direction
        // position gives travel time before each hop; use radius 6 with 0.5 edge padding.
        AvoidanceManager.AddAvoidLocation<ExplosionLine>(InGenie, _ => 6.5f, e => e.Current, () => _explosions.Values.Where(e => e.End > DateTime.UtcNow));
        AvoidanceManager.AddAvoidLocation<ExplosionLine>(InGenie, _ => 6.5f, e => e.Next, () => _explosions.Values.Where(e => e.End > DateTime.UtcNow && e.PreviewNext));
        // The ship preview removes its starting half, the central crossing diamond, and the
        // tethered lever's side. The remaining far corner satisfies all Voyage segments.
        // Each edge is padded 0.5; all shapes share one snapshot and one RB movement owner.
        AvoidanceManager.AddAvoidPolygon<Voyage>(VoyageActive, null, 80, _ => 0, _ => 1, _ => 15, v => v.ShipsWest ? new[] { new Vector2(-70, -70), new Vector2(0, -70), new Vector2(0, 70), new Vector2(-70, 70) } : new[] { new Vector2(0, -70), new Vector2(70, -70), new Vector2(70, 70), new Vector2(0, 70) }, _ => GenieCenter, ActiveVoyage, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<Voyage>(VoyageActive, null, 80, _ => (float)Math.PI / 4, _ => 1, _ => 15, _ => new[] { new Vector2(-14.5f, -14.5f), new Vector2(14.5f, -14.5f), new Vector2(14.5f, 14.5f), new Vector2(-14.5f, 14.5f) }, _ => GenieCenter, ActiveVoyage, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<Voyage>(VoyageActive, null, 80, _ => 0, _ => 1, _ => 15, _ => new[] { new Vector2(-70.5f, -23.5f), new Vector2(70.5f, -23.5f), new Vector2(70.5f, 23.5f), new Vector2(-70.5f, 23.5f) }, v => v.Lever, ActiveVoyage, priority: AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseNativeBossAvoidance();
        ReleaseDandan();
        ReleaseSwordmaster();
        ReleaseRukhkh();
        ClearDaryaForecasts();
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveDaryaMarch;
        SetMechanicGapCloserBlock(false);
        ClearForecasts();
        ClearPariForecasts();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (InDandan())
        {
            ClearForecasts();
            ClearPariForecasts();
            ClearDaryaForecasts();
            ClearRukhkhForecasts();
            ClearSwordmasterForecasts();
            // The boss center remains outside the floor; a gap closer must never
            // replace the safe hitbox-side approach with a dash to that center.
            SetMechanicGapCloserBlock(true);
            if (await RunDandanCone())
                return true;
            return await DamageMitigationSpells();
        }

        ClearDandanForecasts();
        if (InSwordmaster())
        {
            ClearForecasts();
            ClearPariForecasts();
            ClearDaryaForecasts();
            ClearRukhkhForecasts();
            UpdateSwordmasterForecasts();
            SetMechanicGapCloserBlock(_swordmasterCasts.Count != 0 || _swordmasterTether.Count != 0 || _swordmasterWounds.Count != 0 || _swordmasterConfluence.Count != 0 || _swordmasterMeteors.Count != 0 || _swordmasterShelters.Count != 0 || _swordmasterMagnet != null || _swordmasterMaw.Count != 0 || _swordmasterFloor.Count != 0 || _swordmasterCrosses.Count != 0);
            if (HandleSwordmasterMagnet())
                return true;
            return await DamageMitigationSpells();
        }

        ClearSwordmasterForecasts();
        if (InGateRukhkh())
        {
            ClearForecasts();
            ClearPariForecasts();
            ClearDaryaForecasts();
            UpdateRukhkhForecasts();
            SetMechanicGapCloserBlock(_rukhkhFans.Count != 0 || DateTime.UtcNow < _rukhkhPearlEnd || _rukhkhRockEnds.Any(p => p.Value > DateTime.UtcNow) || DateTime.UtcNow < _rukhkhHowlPrepareUntil || _rukhkhHowls.Count != 0 || DateTime.UtcNow < _rukhkhHowlReturnUntil);
            if (HandleRukhkhHowlPreparation())
                return true;
            return await DamageMitigationSpells();
        }

        ClearRukhkhForecasts();
        if (InDarya())
        {
            ClearForecasts();
            ClearPariForecasts();
            UpdateDaryaForecasts();
            SetMechanicGapCloserBlock(DateTime.UtcNow < _daryaPreparationEnd || _daryaFamiliarLines.Count != 0 || _daryaMarchOwned || _daryaTiles.Count != 0 || _daryaDelayedBalls.Count != 0 || _daryaSurges.Count != 0 || _daryaBubbles.Count != 0 || _daryaTreasure.Count != 0 || _daryaTreasureOrbs.Count != 0 || _daryaCurrents.Count != 0 || DateTime.UtcNow < _daryaTideEnd);
            if (await HandleDaryaWave())
                return true;
            return await DamageMitigationSpells();
        }

        ClearDaryaForecasts();
        if (InPari())
        {
            ClearForecasts();
            UpdatePariForecasts();
            var now = DateTime.UtcNow;
            SetMechanicGapCloserBlock(_flightExpires > now || _sunExpires > now || _pariCasts.Values.Any(s => s.End > now) || ActiveNightShape().Any() || ActiveGales().Any() || ActiveWinds().Any());
            return await DamageMitigationSpells();
        }

        ClearPariForecasts();
        if (!InGenie())
        {
            SetMechanicGapCloserBlock(false);
            ClearForecasts();
            return false;
        }

        UpdateForecasts();
        SetMechanicGapCloserBlock(_casts.Count != 0 || _explosions.Count != 0 || VoyageActive());
        return await DamageMitigationSpells();
    }

    private void SetMechanicGapCloserBlock(bool active)
    {
        // Intervene 16461 at 21:45:43.123 and 21:48:35.829 dashed from a safe point into
        // a charge/cross while shared avoidance was between escape movements. Magitek's
        // maintained CanUseGapCloser checks this capability independently of normal movement.
        // Keep attacks, walking and shared avoidance available; only dashes are suppressed
        // while captured hazards/previews remain. Refresh a one-second lease so a stopped
        // handler cannot leave the routine disabled, and clear immediately on encounter exit.
        if (active)
        {
            CapabilityManager.Update(_mechanicGapCloserHandle, CapabilityFlags.GapCloser, 1000, "Merchant mechanic forecast: preserve the safe point between dodges");
            _ownsGapCloserBlock = true;
        }
        else if (_ownsGapCloserBlock)
        {
            CapabilityManager.Clear(_mechanicGapCloserHandle, CapabilityFlags.GapCloser, "Merchant mechanic forecast ended");
            _ownsGapCloserBlock = false;
        }
    }

    private void ClearForecasts()
    {
        _casts.Clear();
        _explosions.Clear();
        _previousExplosionCasts.Clear();
        _voyage = null;
        ReleaseVoyage();
    }

    private void UpdateForecasts()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(GenieCenter) < 50).ToArray();
        if (_voyage != null && _voyage.End <= now)
        {
            _voyage = null;
            ReleaseVoyage();
        }

        foreach (var id in _casts.Keys.Where(id => _casts[id].End <= now).ToArray())
        {
            _casts.Remove(id);
        }

        foreach (var id in _explosions.Keys.Where(id => _explosions[id].End <= now).ToArray())
        {
            _explosions.Remove(id);
        }

        var explosionCasts = new HashSet<uint>();
        foreach (var actor in actors)
        {
            var action = actor.IsCasting ? actor.CastingSpellId : 0;
            if (action is 43353 or 43354 or 43349 or 44254 or 44255 or 45586 or 44771 or 44821)
            {
                // The 15:33 Firecrackers impact was about 0.56s beyond RB's reported finish.
                // Retain 0.7s through the effect, including each sequential cannon/fan stage.
                // Lamp Lighting 45586 also hit at 22:06:08.74 when generic cast geometry
                // disappeared during Pyromagicks; freeze its last cast heading through impact.
                if (!_casts.TryGetValue(actor.ObjectId, out var shape))
                {
                    _casts[actor.ObjectId] = shape = new CastShape();
                }

                shape.Action = action;
                shape.Location = actor.Location;
                shape.Heading = actor.Heading;
                // The captured Rainbow effect arrives 0.733s after the reported cast finish.
                // Keep 0.95s for this family; preserve other accepted handoff timings.
                shape.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(action is 44771 or 44821 ? .95 : .7);
            }

            if (action == 44261)
            {
                explosionCasts.Add(actor.ObjectId);
                if (!_previousExplosionCasts.Contains(actor.ObjectId))
                {
                    // Five impacts traversed 32yalms from the first origin. Twelve seconds
                    // beyond the initial finish bounds the last impact plus capture jitter;
                    // a newly observed cast resets a reused helper's old forecast.
                    _explosions[actor.ObjectId] = new ExplosionLine
                    {
                        Origin = actor.Location,
                        Advance = new Vector3((float)Math.Sin(actor.Heading) * 8, 0, (float)Math.Cos(actor.Heading) * 8),
                        End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(12)
                    };
                }
            }

            if (_explosions.TryGetValue(actor.ObjectId, out var line))
            {
                line.Current = actor.Location;
                line.Next = actor.Location + line.Advance;
                // Look one hop ahead only near the first impact or during the moving repeats.
                // Never forecast a sixth explosion after the captured final 32-yalm position.
                line.PreviewNext = actor.Distance2D(line.Origin) < 31 && (!actor.IsCasting || actor.SpellCastInfo.RemainingCastTime.TotalSeconds < 2.4);
            }
        }

        _previousExplosionCasts.Clear();
        _previousExplosionCasts.UnionWith(explosionCasts);
        if (_voyage == null || _voyage.End <= now)
        {
            var ships = actors.Where(b => b.BaseId == 18537 && b.IsVisible && b.IsCasting && b.CastingSpellId == 47042).ToArray();
            var lever = actors.FirstOrDefault(b => b.BaseId == 18538 && b.VfxContainer.Tethers.Any(t => t.Id == 86));
            // Both captured previews had three horizontal lanes atZ(center +/-12/0),
            // ships 18yalms west and a tethered lever 16.75 yalms north/south. Require that
            // topology (including its mirrored east form) before applying the corner model.
            if (ships.Length == 3 && lever != null && ships.All(b => Math.Abs(Math.Abs(b.X - GenieCenter.X) - 18) < 1) && ships.All(b => Math.Sign(b.X - GenieCenter.X) == Math.Sign(ships[0].X - GenieCenter.X)) && Math.Abs(Math.Abs(lever.Z - GenieCenter.Z) - 16.75f) < 1)
            {
                _voyage = new Voyage
                {
                    ShipsWest = ships[0].X < GenieCenter.X,
                    Lever = lever.Location,
                    // The last fast follow-up at 15:35:36.859 followed the first ship finish
                    // by about 0.66s. Hold 1.4s for its effect, then release before Fanning hits.
                    End = now + ships.Max(b => b.SpellCastInfo.RemainingCastTime) + TimeSpan.FromSeconds(1.4)
                };
                // Suppress late duplicate ship/helper rectangles only after a validated
                // preview exists. An unfamiliar topology retains native cast rectangles as fallback.
                _ownsVoyage = true;
            }
        }
    }

    private IEnumerable<CastShape> ActiveCasts()
    {
        if (VoyageActive())
        {
            return Array.Empty<CastShape>();
        }

        var active = _casts.Values.Where(c => c.End > DateTime.UtcNow).ToArray();
        if (active.Length == 0)
        {
            return active;
        }

        var first = active.Min(c => c.End);
        // A 0.5s group tolerance merges simultaneously sampled helpers, while the captured
        // two-second cannon and fan intervals remain separate. Later shapes stay pending.
        return active.Where(c => c.End <= first.AddSeconds(.5));
    }

    private bool VoyageActive() => InGenie() && _voyage != null && _voyage.End > DateTime.UtcNow;
    private IEnumerable<Voyage> ActiveVoyage() => VoyageActive() ? new[]
    {
        _voyage
    }

    : Array.Empty<Voyage>();
    private void ReleaseVoyage()
    {
        if (!_ownsVoyage)
        {
            return;
        }

        _ownsVoyage = false;
    }

    // Reserve only the earliest Rainbow wave through the shared cast planner. Both
    // waves together cover the entire floor. Circumscribed 64-gon: radius 15+0.5y padding.
    private static readonly Vector2[] RainbowCircle = Enumerable.Range(0, 64).Select(i => new Vector2((float)(15.5 / Math.Cos(Math.PI / 64) * Math.Cos(i * Math.PI / 32)), (float)(15.5 / Math.Cos(Math.PI / 64) * Math.Sin(i * Math.PI / 32)))).ToArray();
    private static Vector2[] ShapePoints(CastShape shape)
    {
        if (shape.Action is 44771 or 44821)
            return RainbowCircle;
        // Lamp Lighting is a forward 60x 8 line from the real boss, overlapping moving fire.
        // Its half-width 4 plus 0.5 edge padding preserves the verified off-axis safe lanes.
        if (shape.Action == 45586)
        {
            return new[]
            {
                new Vector2(-4.5f, -.5f),
                new Vector2(4.5f, -.5f),
                new Vector2(4.5f, 60.5f),
                new Vector2(-4.5f, 60.5f)
            };
        }

        // All fan origins are at arena center. Their 30-yalm reach exceeds every corner of
        // the 35-yalm square, so finite 40-yalm wedges/half-planes are equivalent inside it.
        // Offset each wedge tip by 0.5/sin(half-angle), padding both straight edges 0.5 yalm.
        if (shape.Action == 43353)
        {
            return new[]
            {
                new Vector2(-40, -.5f),
                new Vector2(40, -.5f),
                new Vector2(40, 40),
                new Vector2(-40, 40)
            };
        }

        if (shape.Action == 43349)
        {
            return new[]
            {
                new Vector2(-3.5f, -.5f),
                new Vector2(3.5f, -.5f),
                new Vector2(3.5f, 36.5f),
                new Vector2(-3.5f, 36.5f)
            };
        }

        var halfAngle = shape.Action == 43354 ? Math.PI / 4 : Math.PI / 8;
        var tip = (float)(-.5 / Math.Sin(halfAngle));
        var farWidth = (float)((40 - tip) * Math.Tan(halfAngle));
        return new[]
        {
            new Vector2(0, tip),
            new Vector2(farWidth, 40),
            new Vector2(-farWidth, 40)
        };
    }

    // Forecast state contains only managed scalars. Native wrappers are reacquired on each
    // bot tick and none survives a coroutine yield, encounter exit, wipe or actor reuse.
    private sealed class CastShape
    {
        internal uint Action;
        internal Vector3 Location;
        internal float Heading;
        internal DateTime End;
    }

    private sealed class ExplosionLine
    {
        internal Vector3 Origin;
        internal Vector3 Advance;
        internal Vector3 Current;
        internal Vector3 Next;
        internal DateTime End;
        internal bool PreviewNext;
    }

    private sealed class Voyage
    {
        internal bool ShipsWest;
        internal Vector3 Lever;
        internal DateTime End;
    }

    private static bool InGenie() => WorldManager.ZoneId == 1315 && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Distance2D(GenieCenter) < 60 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.IsAlive && b.BaseId == GenieBase && b.Distance2D(GenieCenter) < 30);
    // 2026-09-27 live boss/helper origins confirm the reference 40x 40 square at floorY-54.
    // The entrance atZ-780 lies outside it; never let an escape leave the actual combat floor.
    private static readonly Vector3 PariCenter = new(-760, -54, -805);
    // A 14-yalm preview leaves a usable cap beyond the padded cross and final charge.
    // The old 12-yalm preview leaves only a thin intersection near the arena edge.
    private const float SunStagingRadius = 14;
    // Sun's generic omen advertises a 24-yalm hole, but the captured safe radius is 8.
    // Quick Gleam needs the earlier carpet memory preview; both use the same local owner.
    private static readonly uint[] PariOwnedActions =
    {
        45424,
        45446,
        45498,
        46809,
        45547,
        45918,
        45919,
        45459,
        45460,
        // Friendly Cannon cancels spirits; only the surviving Gale circles are hazards.
        45513,
        45514,
        // Strong Wind remains hazardous after its preview cast finishes.
        45508,
        46755
    };
    private readonly Dictionary<uint, PariShape> _pariCasts = new();
    private readonly List<FlightEdge> _flightEdges = new();
    private readonly HashSet<string> _flightLinks = new();
    private readonly Dictionary<int, float> _nightHeadings = new();
    private readonly Dictionary<uint, uint> _carpetBaubles = new();
    private DateTime _carpetExpires;
    private uint _previousPariCast;
    private Vector3 _flightOrigin;
    private DateTime _flightFinish;
    private DateTime _flightExpires;
    private DateTime _sunExpires;
    private Vector3 _sunCenter;
    private DateTime _nightFinish;
    private int _nightCount;
    private Vector3 _nightOrigin;
    private float _nightInitialHeading;
    private bool _nightLineReady;
    private void RegisterPariAvoidance()
    {
        RegisterGaleAvoidance();
        RegisterWindAvoidance();
        ClearPariForecasts();
        AvoidanceHelpers.AddAvoidSquareDonut(InPari, 39, 39, 140, 140, () => new[] { PariCenter });
        // Publish only the earliest impact group. Crosses and charge segments overlap in
        // time; reserving all future mutually exclusive regions can erase every safe point.
        AvoidanceManager.AddAvoidPolygon<PariShape>(InPari, null, 100, s => -s.Heading, _ => 1, _ => 15, PariShapePoints, s => s.Position, ActivePariShapes, priority: AvoidancePriority.High);
        // A fourteen-yalm staging circle permits maneuvering during earlier charges/crosses.
        // Tighten to 7.5 only after earlier crosses end; disabling staging entirely let
        // routine pursuit run 25yalms from the endpoint before the short Sun cast at 22:40.
        // This remains a constraint in the same avoidance owner, never a manual MoveTo loop.
        AvoidanceHelpers.AddAvoidDonut(() => PariSunActive() && PariSunRadius() == SunStagingRadius, () => _sunCenter, 60, SunStagingRadius);
        AvoidanceHelpers.AddAvoidDonut(() => PariSunActive() && PariSunRadius() < SunStagingRadius, () => _sunCenter, 60, 7.5);
        AvoidanceManager.AddAvoidPolygon<PariShape>(InPari, null, 100, s => -s.Heading, _ => 1, _ => 15, PariShapePoints, s => s.Position, ActiveNightShape, priority: AvoidancePriority.High);
    }

    private static bool InPari() => WorldManager.ZoneId == 1315 && Core.Me != null && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Distance2D(PariCenter) < 60 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.IsAlive && b.BaseId == 19048 && b.Distance2D(PariCenter) < 40);
    private void ClearPariForecasts()
    {
        ClearGales();
        _winds.Clear();
        _pariCasts.Clear();
        _flightEdges.Clear();
        _flightLinks.Clear();
        _nightHeadings.Clear();
        _previousPariCast = 0;
        _flightFinish = _flightExpires = _sunExpires = _nightFinish = DateTime.MinValue;
        _nightCount = 0;
        _nightLineReady = false;
        _carpetBaubles.Clear();
        _carpetExpires = DateTime.MinValue;
    }

    private void UpdatePariForecasts()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(PariCenter) < 65).ToArray();
        var boss = actors.FirstOrDefault(b => b.BaseId == 19048 && b.IsAlive);
        if (boss == null)
        {
            return;
        }

        UpdateGales(actors, now);
        UpdateWinds(actors, now);
        var action = boss.IsCasting ? boss.CastingSpellId : 0;
        if (action == 45422 && _previousPariCast != action)
        {
            _flightOrigin = boss.Location;
            _flightFinish = now + boss.SpellCastInfo.RemainingCastTime;
            _flightExpires = _flightFinish.AddSeconds(12);
            _flightEdges.Clear();
            _flightLinks.Clear();
            _sunExpires = DateTime.MinValue;
        }

        if (action == 45446)
        {
            _sunCenter = boss.Location;
            // The baseline's damage/status update followed the reported short cast finish.
            // Keep shelter through finish+1s, including the client/server effect handoff.
            _sunExpires = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }

        if (action == 46753 && _previousPariCast != action)
        {
            // Thieves' Weave reuses the same carpet actors. Forget the previous arrangement
            // before matching this wave's visible baubles to their concealing carpets.
            _carpetBaubles.Clear();
            _carpetExpires = now.AddSeconds(30);
        }

        UpdateCarpetMemory(actors, now);
        var nights = action is 45919 or 45918 ? 2 : action is 45460 or 45459 ? 3 : 0;
        if (nights != 0 && _previousPariCast != action)
        {
            _nightCount = nights;
            _nightHeadings.Clear();
            _nightFinish = now + boss.SpellCastInfo.RemainingCastTime;
            _nightLineReady = false;
        }

        if (nights != 0)
        {
            UpdateNightLine(boss);
        }

        if (nights != 0 && boss.VfxContainer.IsValid)
        {
            var marker = boss.VfxContainer.LockOns.FirstOrDefault(v => v != null && v.IsValid && v.Id is 624 or 625 or 644 or 645);
            if (marker != null)
            {
                // The two 625 markers appeared at remaining 6.56s/2.52s, with only a 0.267s
                // empty-slot gap. Bucket by the four-second preview cadence so equal IDs
                // cannot collapse into one event when a combat coroutine spans that gap.
                // Later observations overwrite the bucket before its cleave is due.
                var index = nights - 1 - (int)Math.Floor((boss.SpellCastInfo.RemainingCastTime.TotalSeconds + 1) / 4);
                if (index >= 0 && index < nights)
                {
                    var reverseOnEven = marker.Id is 625 or 644;
                    var heading = boss.Heading - ((index % 2 == 0) == reverseOnEven ? (float)Math.PI : 0);
                    if (LoggingHelpers.MechanicDiagnosticsEnabled && (!_nightHeadings.TryGetValue(index, out var previous) || Math.Abs(previous - heading) > .05))
                    {
                        ff14bot.Helpers.Logging.Write($"[Merchant.Pari] Night preview index={index} marker={marker.Id} heading={heading:F3} finish={_nightFinish:O}");
                    }

                    _nightHeadings[index] = heading;
                }
            }
        }

        _previousPariCast = action;
        if (_flightExpires > now)
        {
            foreach (var source in actors.Where(a => a.BaseId == 19061 && a.VfxContainer.IsValid))
            {
                foreach (var tether in source.VfxContainer.Tethers.Where(t => t.Id == 355))
                {
                    var target = actors.FirstOrDefault(a => a.ObjectId == tether.TargetId);
                    if (target == null)
                    {
                        continue;
                    }

                    var key = source.ObjectId + ":" + tether.TargetId;
                    if (_flightLinks.Add(key))
                    {
                        _flightEdges.Add(new FlightEdge { Source = source.ObjectId, Target = target.ObjectId, Appeared = now });
                    }
                    else
                    {
                        // Tether creation and helper placement can arrive in adjacent frames.
                        // Resolve coordinates after creation instead of freezing a spawn origin.
                        var edge = _flightEdges.First(e => e.Source == source.ObjectId && e.Target == target.ObjectId);
                        if (!edge.Ready && now > edge.Appeared)
                        {
                            edge.From = source.Location;
                            edge.To = target.Location;
                            edge.Ready = true;
                        }
                    }
                }
            }

            var chain = FlightChain();
            if (chain.Count == 3 && _sunExpires == DateTime.MinValue)
            {
                _sunCenter = chain[2].To;
                _sunExpires = _flightFinish.AddSeconds(11);
                // Keep preview selection inspectable when the existing diagnostics switch
                // is enabled; geometry validation must not rely on a successful clear alone.
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    ff14bot.Helpers.Logging.Write($"[Merchant.Pari] Three-link flight end={_sunCenter} finish={_flightFinish:O}");
                }
            }
        }

        foreach (var id in _pariCasts.Keys.Where(id => _pariCasts[id].End <= now).ToArray())
        {
            _pariCasts.Remove(id);
        }

        foreach (var actor in actors.Where(a => a.IsCasting))
        {
            var cast = actor.CastingSpellId;
            if (cast == 45424)
            {
                var end = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.6);
                var destination = actor.SpellCastInfo.CastLocation;
                var edge = FlightChain().FirstOrDefault(e => e.From.Distance2D(actor.Location) < 1 && e.To.Distance2D(destination) < 1);
                if (edge != null)
                {
                    edge.ObservedEnd = end;
                }

                _pariCasts[actor.ObjectId] = FlightShape(actor.Location, destination, end);
            }
            else if (cast is 45498 or 46809 or 45547)
            {
                // At 20:04:16.6 the second cross damaged after its reported 15.826 finish.
                // Retain 0.95s, rather than releasing a telegraph before its actual impact.
                _pariCasts[actor.ObjectId] = new PariShape
                {
                    Kind = PariShapeKind.Cross,
                    Position = actor.Location,
                    Heading = actor.Heading,
                    End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.95)
                };
            }
        }
    }

    private void UpdateCarpetMemory(BattleCharacter[] actors, DateTime now)
    {
        if (_carpetExpires <= now)
        {
            _carpetBaubles.Clear();
            return;
        }

        var carpets = actors.Where(a => a.BaseId == 19060 && a.IsCasting && a.CastingSpellId == 45504).ToArray();
        if (_carpetBaubles.Count == 0 && carpets.Length == 4)
        {
            // At 20:36:14 all four concealing carpets occupied X-745/-755/-765/-775,Z-800.
            // Three visible baubles carried 2056:1096 until conceal. Hidden/reused actors
            // elsewhere in the arena are not part of this arrangement.
            var baubles = actors.Where(a => a.BaseId == 19049 && a.IsVisible && Math.Abs(a.Z + 800) < 1 && a.CharacterAuras.Any(s => s.Id == 2056 && s.Value == 1096)).ToArray();
            if (baubles.Length == 3 && carpets.All(c => Math.Abs(c.Z + 800) < 1))
            {
                var matches = baubles.Select(b => new { Bauble = b.ObjectId, Carpet = carpets.SingleOrDefault(c => c.Distance2D(b.Location) < 1)?.ObjectId ?? 0 }).ToArray();
                if (matches.All(m => m.Carpet != 0) && matches.Select(m => m.Carpet).Distinct().Count() == 3)
                {
                    foreach (var match in matches)
                    {
                        _carpetBaubles[match.Carpet] = match.Bauble;
                    }

                    if (LoggingHelpers.MechanicDiagnosticsEnabled)
                    {
                        ff14bot.Helpers.Logging.Write("[Merchant.Pari] Memorized three occupied carpets for Thieves' Weave.");
                    }
                }
            }
        }

        // Object IDs persisted through the swaps. Their final positions at Unravel exactly
        // matched the later quick-cross origins in the 20:36:22/27 capture. Forecast by bauble
        // ID so its real quick cast updates the same shape rather than duplicating geometry.
        foreach (var carpet in actors.Where(a => a.BaseId == 19060 && a.IsCasting && a.CastingSpellId == 45506))
        {
            if (!_carpetBaubles.TryGetValue(carpet.ObjectId, out var bauble))
            {
                continue;
            }

            _pariCasts[bauble] = new PariShape
            {
                Kind = PariShapeKind.Cross,
                Position = carpet.Location,
                Heading = 0,
                End = now + carpet.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3.5)
            };
        }
    }

    private List<FlightEdge> FlightChain()
    {
        var chain = new List<FlightEdge>();
        var from = _flightOrigin;
        // Later previews can include decoy carpets. Only three connected links starting
        // at the real boss's captured origin describe its path; actor names alone do not.
        while (chain.Count < 3)
        {
            var next = _flightEdges.FirstOrDefault(e => e.Ready && !chain.Contains(e) && e.From.Distance2D(from) < 1);
            if (next == null)
            {
                break;
            }

            chain.Add(next);
            from = next.To;
        }

        return chain;
    }

    private DateTime FlightImpact(FlightEdge edge, int index) => edge.ObservedEnd != DateTime.MinValue ? edge.ObservedEnd : _flightFinish.AddSeconds(3.3 + index * 2.1);
    private IEnumerable<PariShape> ActivePariShapes()
    {
        var now = DateTime.UtcNow;
        var shapes = _pariCasts.Values.Where(s => s.End > now).ToList();
        if (_flightExpires > now)
        {
            var chain = FlightChain();
            for (var i = 0; i < chain.Count; i++)
            {
                var end = FlightImpact(chain[i], i);
                if (end > now)
                {
                    shapes.Add(FlightShape(chain[i].From, chain[i].To, end));
                    break;
                }
            }
        }

        if (shapes.Count == 0)
        {
            return shapes;
        }

        var first = shapes.Min(s => s.End);
        return shapes.Where(s => s.End <= first.AddSeconds(1));
    }

    private bool PariSunActive() => InPari() && _sunExpires > DateTime.UtcNow;
    private bool EarlierCrossActive() => // At 20:24:00 the south bauble cross covered every point in the early donut
 // shelter. Its effect precedes the last charge and Sun Circlet. Release that
    // tight shelter until EARLIER crosses finish. At 20:35:51 a fourth
    // cross started after the charges but resolved after Sun; allowing that later
    // cast to delay shelter caused a donut hit. Compare impacts, not cast presence.
    // Keep 14-yalm staging instead: the 22:40 cross at (-745,-820) leaves a safe cap
    // north ofZ-814.5 around the south endpoint(-760,-824.9). A point such as
    // (-767,-813) also clears the padded final charge atX-760. The earlier
    // blanket deferral allowed pursuit back toZ-799 and made timely shelter impossible.
    // Shared avoidance selects within the combined staging/cross/charge constraints.
    _pariCasts.Values.Any(s => s.Kind == PariShapeKind.Cross && s.End > DateTime.UtcNow && s.End <= _sunExpires);
    private float PariSunRadius()
    {
        if (EarlierCrossActive())
        {
            return SunStagingRadius;
        }

        var chain = FlightChain();
        return chain.Count == 3 && FlightImpact(chain[1], 1) > DateTime.UtcNow ? SunStagingRadius : 7.5f;
    }

    private void UpdateNightLine(BattleCharacter boss)
    {
        // At 23:41:09 Right Three Nights faced north (actor heading pi), but its actual
        // omen forward axis was (40,0): east. Actor facing, including its initial turn,
        // cannot locate this attack. Matrix row 2 is the transformed local forward axis;
        // row 0 is the half-width. Read only the exposed RB matrix, never a guessed offset.
        var omen = boss.OmenMatrix;
        var length = Math.Sqrt(omen.M20 * omen.M20 + omen.M22 * omen.M22);
        var halfWidth = Math.Sqrt(omen.M00 * omen.M00 + omen.M02 * omen.M02);
        var alignment = omen.M20 * omen.M00 + omen.M22 * omen.M02;
        // Only accept the captured 40x 4 rectangular omen near this caster. Missing/zero
        // or unrelated matrices retain the last valid scalar sample for this cast;
        // a new cast has no line forecast until its own valid omen arrives.
        if (!double.IsFinite(length) || !double.IsFinite(halfWidth) || length < 39 || length > 41 || halfWidth < 1.5 || halfWidth > 2.5 || !float.IsFinite(alignment) || Math.Abs(alignment) > length * halfWidth * .05 || !float.IsFinite(omen.M30) || !float.IsFinite(omen.M32) || omen.Center.Distance2D(boss.Location) > 1)
        {
            return;
        }

        _nightOrigin = new Vector3(omen.M30, boss.Location.Y, omen.M32);
        _nightInitialHeading = (float)Math.Atan2(omen.M20, omen.M22);
        if (!_nightLineReady && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write($"[Merchant.Pari] Night line action={boss.CastingSpellId} origin={_nightOrigin} heading={_nightInitialHeading:F3} finish={_nightFinish:O}");
        }

        _nightLineReady = true;
    }

    private IEnumerable<PariShape> ActiveNightShape()
    {
        // October 1 first-cleave damage resolved 2.917s after the reported finish;
        // the old 2.7s handoff sent the player across the still-dangerous half. Hold 3.25s
        // (0.33s beyond that captured effect), retaining the observed 2.4s cadence.
        var now = DateTime.UtcNow;
        // Every Nights cast first fires a 40x 4 forward line. At 23:10:44-54, publishing
        // the future half-room together with generic line geometry oscillated at arena
        // center and the initial line hit at 55.42 (reported finish 54.59). Own that line
        // through finish+0.95, then publish the remembered halves in their existing order.
        // This gives shared avoidance one current stage. Retain the last valid omen
        // orientation after the cast ends, when the live matrix becomes unavailable.
        if (_nightCount != 0 && now < _nightFinish.AddSeconds(.95))
        {
            if (_nightLineReady)
            {
                yield return new PariShape
                {
                    Kind = PariShapeKind.NightLine,
                    Position = _nightOrigin,
                    Heading = _nightInitialHeading,
                    End = _nightFinish.AddSeconds(.95)
                };
            }

            yield break;
        }

        for (var i = 0; i < _nightCount; i++)
        {
            var end = _nightFinish.AddSeconds(3.25 + i * 2.4);
            if (end <= now)
            {
                continue;
            }

            if (_nightHeadings.TryGetValue(i, out var heading))
            {
                yield return new PariShape
                {
                    Kind = PariShapeKind.Half,
                    Position = PariCenter,
                    Heading = heading,
                    End = end
                };
            }

            yield break;
        }
    }

    private static PariShape FlightShape(Vector3 from, Vector3 to, DateTime end) => new()
    {
        Kind = PariShapeKind.Charge,
        Position = from,
        Heading = (float)Math.Atan2(to.X - from.X, to.Z - from.Z),
        Length = from.Distance2D(to),
        End = end
    };
    private static Vector2[] PariShapePoints(PariShape shape)
    {
        // Nights' narrow opening line precedes the marker cleaves; pad its 40x 4 edges 0.5.
        if (shape.Kind == PariShapeKind.NightLine)
        {
            return new[]
            {
                new Vector2(-2.5f, -.5f),
                new Vector2(2.5f, -.5f),
                new Vector2(2.5f, 40.5f),
                new Vector2(-2.5f, 40.5f)
            };
        }

        // Expand every straight edge 0.5 yalm. Cross reach 40 and charge width 10 come from
        // the captured actor/action family; rotating half-room cleaves cover the full square.
        if (shape.Kind == PariShapeKind.Half)
        {
            return new[]
            {
                new Vector2(-60, -.5f),
                new Vector2(60, -.5f),
                new Vector2(60, 60),
                new Vector2(-60, 60)
            };
        }

        if (shape.Kind == PariShapeKind.Charge)
        {
            return new[]
            {
                new Vector2(-5.5f, -.5f),
                new Vector2(5.5f, -.5f),
                new Vector2(5.5f, shape.Length + .5f),
                new Vector2(-5.5f, shape.Length + .5f)
            };
        }

        return new[]
        {
            new Vector2(-5.5f, -40.5f),
            new Vector2(5.5f, -40.5f),
            new Vector2(5.5f, -5.5f),
            new Vector2(40.5f, -5.5f),
            new Vector2(40.5f, 5.5f),
            new Vector2(5.5f, 5.5f),
            new Vector2(5.5f, 40.5f),
            new Vector2(-5.5f, 40.5f),
            new Vector2(-5.5f, 5.5f),
            new Vector2(-40.5f, 5.5f),
            new Vector2(-40.5f, -5.5f),
            new Vector2(-5.5f, -5.5f)
        };
    }

    private enum PariShapeKind
    {
        Charge,
        Cross,
        Half,
        NightLine
    }

    private sealed class PariShape
    {
        internal PariShapeKind Kind;
        internal Vector3 Position;
        internal float Heading;
        internal float Length;
        internal DateTime End;
    }

    private sealed class FlightEdge
    {
        internal uint Source;
        internal uint Target;
        internal bool Ready;
        internal DateTime Appeared;
        internal Vector3 From;
        internal Vector3 To;
        internal DateTime ObservedEnd;
    }

    private readonly Dictionary<uint, GaleForecast> _gales = new();
    private bool _galeResolved;
    // A circumscribed polygon preserves the full radius 15 plus the standard 0.5y margin.
    private static readonly Vector2[] GaleCircle = Enumerable.Range(0, 64).Select(i => new Vector2((float)(15.5 / Math.Cos(Math.PI / 64) * Math.Sin(i * Math.PI / 32)), (float)(15.5 / Math.Cos(Math.PI / 64) * Math.Cos(i * Math.PI / 32)))).ToArray();
    private void RegisterGaleAvoidance() => AvoidanceManager.AddAvoidPolygon<GaleForecast>(InPari, null, 100, _ => 0, _ => 1, _ => 15, _ => GaleCircle, g => g.Position, ActiveGales, priority: AvoidancePriority.High);
    private IEnumerable<GaleForecast> ActiveGales() => _galeResolved ? _gales.Values.Where(g => !g.Cancelled && g.Finish.AddSeconds(.8) > DateTime.UtcNow) : Enumerable.Empty<GaleForecast>();
    private void ClearGales()
    {
        _gales.Clear();
        _galeResolved = false;
    }

    private void UpdateGales(BattleCharacter[] actors, DateTime now)
    {
        foreach (var id in _gales.Keys.Where(id => _gales[id].Finish.AddSeconds(.8) <= now).ToArray())
            _gales.Remove(id);
        if (_gales.Count == 0)
            _galeResolved = false;
        var casting = actors.Where(a => a.BaseId == 19050 && a.IsCasting && a.CastingSpellId == 45513).ToArray();
        foreach (var actor in casting)
        {
            if (!_gales.ContainsKey(actor.ObjectId))
                _gales[actor.ObjectId] = new GaleForecast
                {
                    Position = actor.Location,
                    Finish = now + actor.SpellCastInfo.RemainingCastTime
                };
        }

        foreach (var pair in _gales)
        {
            // October 1 cancellation precedes the spirit finish by about 1.3s. Genuine
            // impacts occurred finish+0.33s; retain 0.8s after a normal cast completes.
            if (now < pair.Value.Finish.AddSeconds(-1) && !casting.Any(a => a.ObjectId == pair.Key))
                pair.Value.Cancelled = true;
        }

        if (_gales.Count == 5 && !_galeResolved)
        {
            var birds = actors.Where(a => a.BaseId == 19080 && a.IsCasting && a.CastingSpellId == 45514).ToArray();
            if (birds.Length == 1 && _gales.Values.Count(g => g.Position.Distance2D(PariCenter) < 1) == 1)
            {
                // The captured five-spirit wave has four corners and a center. The
                // friendly bird crosses the center and cancels exactly three collinear
                // spirits. Use position rather than its still-turning initial heading.
                // A unique three-point line is required; otherwise wait for native
                // cancellation instead of guessing which of two diagonals is safe.
                var origin = birds[0].Location;
                var dx = PariCenter.X - origin.X;
                var dz = PariCenter.Z - origin.Z;
                var length = Math.Sqrt(dx * dx + dz * dz);
                var aligned = _gales.Values.Where(g => length > 15 && ((g.Position.X - origin.X) * dx + (g.Position.Z - origin.Z) * dz) > 0 && Math.Abs((g.Position.X - origin.X) * dz - (g.Position.Z - origin.Z) * dx) / length < 1).ToArray();
                if (aligned.Length == 3)
                {
                    foreach (var gale in aligned)
                        gale.Cancelled = true;
                    _galeResolved = true;
                    if (LoggingHelpers.MechanicDiagnosticsEnabled)
                        ff14bot.Helpers.Logging.Write("[Merchant.Pari] Friendly Gale Cannon: forecast three cancellations; avoid the two surviving spirits.");
                }
            }

            if (_gales.Values.Count(g => g.Cancelled) == 3)
                _galeResolved = true;
        }

        // The second wave has only two spirits; their native early cancellations
        // remove each circle. Never publish five simultaneous room-covering hazards.
        // Do not mistake two actors arriving before the other three for wave two.
        if (_gales.Count == 2 && _gales.Values.All(g => g.Finish < now.AddSeconds(9)))
            _galeResolved = true;
    }

    private sealed class GaleForecast
    {
        internal Vector3 Position;
        internal DateTime Finish;
        internal bool Cancelled;
    }

    private readonly List<WindForecast> _winds = new();
    // October 1's helper 46755 heading 4.110 matched the whirlwind's southwest travel.
    // Its radius 22 damage continued after cast completion and killed the stationary
    // player. Reserve the full previewed sweep with 0.5y padding, including the
    // starting circle's rear cap, until its actual whirlwind disappears.
    private static readonly Vector2[] WindSweep =
    {
        new(-22.5f, -22.5f),
        new(22.5f, -22.5f),
        new(22.5f, 40.5f),
        new(-22.5f, 40.5f)
    };
    private void RegisterWindAvoidance() => AvoidanceManager.AddAvoidPolygon<WindForecast>(InPari, null, 100, w => -w.Heading, _ => 1, _ => 15, _ => WindSweep, w => w.Origin, ActiveWinds, priority: AvoidancePriority.High);
    private IEnumerable<WindForecast> ActiveWinds() => _winds.Where(w => w.Expires > DateTime.UtcNow).OrderBy(w => w.Finish).Take(1);
    private void UpdateWinds(BattleCharacter[] actors, DateTime now)
    {
        foreach (var source in actors.Where(a => a.IsCasting && a.CastingSpellId == 46755 && a.NpcId == 14281))
        {
            var finish = now + source.SpellCastInfo.RemainingCastTime;
            // A preview helper may be reused while its previous whirlwind still
            // travels. Match the cast finish as well as the source, preserving both
            // lifetimes until the earlier moving actor actually disappears.
            var forecast = _winds.LastOrDefault(w => w.Source == source.ObjectId && Math.Abs((w.Finish - finish).TotalSeconds) < 1);
            if (forecast == null)
            {
                forecast = new WindForecast
                {
                    Source = source.ObjectId,
                    Finish = finish
                };
                // A missed disappearance must not retain geometry indefinitely. The
                // captured spawn was finish+2s and movement began atapproximately+3.5s.
                forecast.Expires = forecast.Finish.AddSeconds(15);
                _winds.Add(forecast);
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                    ff14bot.Helpers.Logging.Write("[Merchant.Pari] Strong Wind sweep acquired; lifetime follows its whirlwind.");
            }

            forecast.Origin = source.Location;
            forecast.Heading = source.Heading;
        }

        foreach (var forecast in _winds.ToArray())
        {
            if (forecast.WindId == 0)
            {
                // Actor 19062 appears at the preview origin before moving. Match a
                // unique actor there, never the central choreography spirit 19052.
                var matches = actors.Where(a => a.BaseId == 19062 && a.IsVisible && a.Distance2D(forecast.Origin) < 2 && !_winds.Any(w => w.WindId == a.ObjectId)).ToArray();
                if (matches.Length == 1)
                    forecast.WindId = matches[0].ObjectId;
            }

            if (forecast.Expires <= now || (forecast.WindId != 0 && !actors.Any(a => a.ObjectId == forecast.WindId && a.IsVisible)))
                _winds.Remove(forecast);
        }
    // Sequential wind previews can overlap. The earlier moving sweep resolves
    // first; publishing both whole corridors can erase the next safe corner.
    // ActiveWinds exposes one stage to RB avoidance alongside current baubles.
    }

    private sealed class WindForecast
    {
        internal uint Source;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Finish;
        internal DateTime Expires;
        internal uint WindId;
    }

    // October 2 UTC: boss 19151 stands 30y from(805,-30,670), outside the 19.5y
    // circular floor. A 0.5y wall inset keeps routine movement on the platform.
    private static readonly Vector3 DandanCenter = new(805, -30, 670);
    private static readonly uint[] DandanOwnedActions =
    {
        45598,
        45599,
        45600,
        45601,
        45602,
        45616,
        45608,
        45610,
        48044,
        48045,
        48046,
        48047
    };
    private readonly List<DandanShape> _dandanCharges = new();
    private readonly List<DandanShape> _dandanTentacles = new();
    private readonly HashSet<uint> _dandanTentacleHelpers = new();
    private readonly Dictionary<uint, List<(DateTime Time, double Angle)>> _dandanTentacleMotion = new();
    private bool _dandanTentaclePredicted;
    private readonly Dictionary<ulong, DandanShape> _dandanCasts = new();
    // 01:20:10-11 native samples never came closer than 3.55y during the
    // crossing that gained Bind. The former 2.6+.5 reservation is insufficient
    // for observed contact/notification latency. Reserve 4.1y (an additional
    //1y approach buffer), without claiming this is the server hitbox radius.
    // Combined Maw/bubble replay retains a refuge in all 116 unbound frames.
    private static readonly Vector2[] DandanBubblePoints = DandanDisc(4.1f);
    private readonly Dictionary<uint, (Vector3 Position, Vector3 Velocity, DateTime Sample)> _dandanBubbles = new();
    private bool? _dandanReverse;
    private DateTime _dandanCueUntil, _dandanChargeFinish;
    private readonly CapabilityManagerHandle _dandanConeHandle = CapabilityManager.CreateNewHandle();
    private DandanShape _dandanConeShape;
    private Vector3? _dandanConeGoal;
    private bool _dandanConeOwned, _dandanConeMoving;
    private DateTime _dandanGuillotineFinish;
    private readonly CapabilityManagerHandle _dandanRingHandle = CapabilityManager.CreateNewHandle();
    private Vector3? _dandanRingGoal;
    private DateTime _dandanRingStage;
    private bool _dandanRingOwned, _dandanRingMoving;
    private readonly CapabilityManagerHandle _dandanRefugeHandle = CapabilityManager.CreateNewHandle();
    private bool _dandanRefugeOwned, _dandanWasEscaping;
    private sealed class DandanShape
    {
        internal Vector3 Origin;
        internal float Heading;
        internal Vector2[] Points;
        internal DateTime End;
        internal DateTime Resolve;
        internal bool Sequential;
        internal bool Maw;
        internal bool FrontalCone;
        internal bool Bubble;
        internal int RingStage;
    }

    private static bool InDandan() => WorldManager.ZoneId == 1315 && Core.Me != null && !Core.Me.IsDead && Core.Me.InCombat && Core.Me.Distance2D(DandanCenter) < 60 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.IsAlive && b.BaseId == 19151 && b.Distance2D(DandanCenter) < 70);
    private void RegisterDandanAvoidance()
    {
        // Murky Waters 45615 targeted arena center in the 00:21:43 capture.
        // It is unavoidable damage; preserve routine healing/mitigation ownership.
        SpellsToMitigate.Add(45615);
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveDandan;
        ff14bot.NeoProfile.BotEvents.OnPulse += ObserveDandan;
        AvoidanceHelpers.AddAvoidDonut(InDandan, () => DandanCenter, 100, 19);
        // Outside the isolated Swallowed Sea fallback, stages share RB's native
        // movement owner. Both charge chords remain
        // reserved together because a common refuge avoids crossing the second
        // chord during the rapid follow-up. Actual cones join only when cast.
        AvoidanceManager.AddAvoidPolygon<DandanShape>(InDandan, null, 100, s => -s.Heading, _ => 1, _ => 15, s => s.Points, s => s.Origin, () => ActiveDandanShapes().Where(s => !s.Sequential && s != _dandanConeShape && (!s.Bubble || !_dandanRingGoal.HasValue)), priority: AvoidancePriority.High);
        // 01:20 and 01:37 escapes crossed bubbles while exiting a future Maw
        // region. Native run-out paths can traverse avoids at weighted cost;
        // equal priority cannot distinguish immediate binding from timed waves.
        // Keep bubbles/charges/cones high and staged circles/rings medium. Both
        // registrations still use RB's sole movement owner and common geometry.
        AvoidanceManager.AddAvoidPolygon<DandanShape>(InDandan, null, 100, s => -s.Heading, _ => 1, _ => 15, s => s.Points, s => s.Origin, () => ActiveDandanShapes().Where(s => s.Sequential && (!_dandanRingGoal.HasValue || s.RingStage == 0)), priority: AvoidancePriority.Medium);
    }

    private IEnumerable<DandanShape> ActiveDandanShapes()
    {
        var now = DateTime.UtcNow;
        var live = _dandanCasts.Values.Where(s => s.End > now).ToArray();
        // October 2 00:50:29: Maw's three five-circle sets finish two seconds
        // apart; the two orbs announce all four expanding rings simultaneously.
        // Publishing every future region removes every refuge. Maw must reserve
        // the first TWO sets: the 01:00 pull had too little travel time when the
        // second set first became unsafe after the first impact. Its 2.5s lookahead
        // excludes the third set(~4s later). Rings group only simultaneous helpers
        // within 0.5s. Charges/cones stay in the same native owner's region union.
        var first = live.Where(s => s.Sequential).Select(s => s.Resolve).DefaultIfEmpty(DateTime.MaxValue).Min();
        return _dandanCharges.Where(s => s.End > now).Concat(_dandanTentacles.Where(s => s.End > now)).Concat(live.Where(s => !s.Sequential || s.Resolve <= first.AddSeconds(first == DateTime.MaxValue ? 0 : s.Maw ? 2.5 : .5))) // 01:04:49: current-position-only avoidance crossed a bubble's path,
        // causing Bind 2518, BubbleGaol 5047 and death while otherwise clean.
        // Reserve one second of measured travel as overlapping circles; this
        // shares Maw's owner so it cannot choose a refuge across that lane.
        .Concat(_dandanBubbles.Values.SelectMany(b => new[] { 0f, .5f, 1f }.Select(t => new DandanShape { Origin = b.Position + b.Velocity * t, Points = DandanBubblePoints, Bubble = true })));
    }

    // Circumscribe the 32-sided approximation so the 0.5y safety margin is never
    // eroded between vertices. Rings use the existing RB outer/inner convention.
    private static Vector2[] DandanDisc(float radius) => Enumerable.Range(0, 32).Select(i => new Vector2((float)(radius / Math.Cos(Math.PI / 32) * Math.Cos(i * Math.PI / 16)), (float)(radius / Math.Cos(Math.PI / 32) * Math.Sin(i * Math.PI / 16)))).ToArray();
    private static Vector2[] DandanRing(float inner, float outer) => DandanDisc(outer).Concat(Enumerable.Range(0, 32).Select(i => new Vector2((float)(inner * Math.Cos(i * Math.PI / 16)), (float)(inner * Math.Sin(i * Math.PI / 16))))).ToArray();
    private void ObserveDandan(object sender, EventArgs args)
    {
        if (!TreeRoot.IsRunning || ff14bot.Behavior.CommonBehaviors.IsLoading || !InDandan())
        {
            ClearDandanForecasts();
            return;
        }

        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(DandanCenter) < 80).ToArray();
        var boss = actors.FirstOrDefault(b => b.BaseId == 19151 && b.IsAlive);
        if (boss == null)
            return;
        var guillotine = actors.FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 45605);
        if (guillotine != null)
        {
            var finish = now + guillotine.SpellCastInfo.RemainingCastTime;
            if (Math.Abs((finish - _dandanGuillotineFinish).TotalSeconds) > 1)
                _dandanGuillotineFinish = finish;
        }

        var bubbles = actors.Where(a => a.BaseId == 19153 && a.IsVisible && a.IsAlive && a.Distance2D(DandanCenter) < 35).ToArray();
        foreach (var bubble in bubbles)
        {
            var position = bubble.Location;
            var velocity = Vector3.Zero;
            if (_dandanBubbles.TryGetValue(bubble.ObjectId, out var prior))
            {
                var elapsed = (now - prior.Sample).TotalSeconds;
                // Native positions can repeat across adjacent bot ticks. Sample
                // over at least 100ms so a zero/spike derivative cannot flicker
                // the forecast on and off while the bubble moves smoothly.
                if (elapsed < .1)
                    continue;
                // Reject stale frames/teleports instead of extrapolating a wipe
                // or carried-player relocation into an arena-spanning obstacle.
                var distance = position.Distance2D(prior.Position);
                if (elapsed < .5 && distance / elapsed > .2 && distance / elapsed < 6)
                    // Spawn acceleration underpredicted by up to 2.24y. The
                    // captured cruise is 3y/s (median 2.978); reserve its whole
                    // one-second lane in the measured direction.536 native
                    // forecasts stayed within 0.132y at the 95th percentile.
                    velocity = (position - prior.Position) * (3f / distance);
            }

            _dandanBubbles[bubble.ObjectId] = (position, velocity, now);
        }

        foreach (var key in _dandanBubbles.Keys.Where(id => !bubbles.Any(b => b.ObjectId == id)).ToArray())
            _dandanBubbles.Remove(key);
        if (boss.IsCasting && boss.CastingSpellId == 45607 && !_dandanTentacles.Any(s => s.End > now))
        {
            var tentacles = actors.Where(a => a.BaseId == 19152 && a.IsVisible && a.IsAlive).ToArray();
            if (tentacles.Length == 2)
            {
                _dandanTentacleHelpers.Clear();
                _dandanTentacleMotion.Clear();
                _dandanTentaclePredicted = false;
                // 00:44 and 00:54 captures established the inward-crossing
                //0..27deg sweep relative to fixed tentacle 19152. Reserve that
                // sweep for early staging; the late part-heading handoff below
                // also handles outward near-parallel finishes seen later.
                // Three-degree samples with 7.5y half-width overlap throughout
                // the 50y line, including the 0.5y edge margin. Early staging
                // avoids chasing the full rotation before the final approach.
                // The later 01:15 overlap applied damage 1.64s after the parent
                // finish. Two seconds prevents routine return just before impact.
                var end = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
                for (var i = 0; i < 2; ++i)
                {
                    var self = tentacles[i];
                    var other = tentacles[1 - i];
                    var toward = Math.Atan2(other.Location.X - self.Location.X, other.Location.Z - self.Location.Z);
                    var delta = Math.Atan2(Math.Sin(toward - self.Heading), Math.Cos(toward - self.Heading));
                    for (var step = 0; step <= 9; ++step)
                        _dandanTentacles.Add(DandanRectangle(self.Location, self.Heading + Math.Sign(delta) * step * Math.PI / 60, 50, 7.5f, end));
                }
            }
        }

        if (boss.IsCasting && boss.CastingSpellId == 45607)
            ForecastDandanTentacles(actors, boss, now);
        if (!_dandanTentaclePredicted && boss.IsCasting && boss.CastingSpellId == 45607 && boss.SpellCastInfo.RemainingCastTime.TotalSeconds <= 2 && _dandanTentacles.Count > 0)
        {
            // 01:21:58 and 01:24:01: near-parallel helpers finish about 9.3deg
            // OUTWARD, outside the early inward envelope. Reserving both full
            // sweeps covers the entire floor. During the final 2s, follow the
            // actual rotating parts instead, then retain their settled headings
            // through impact. This gives up to 3.6s to leave an early refuge;
            // waiting for the 200ms helpers allows too little time for 12.5y egress.
            var parts = actors.Where(a => a.BaseId == 19350 && a.IsVisible && a.IsAlive).ToArray();
            if (parts.Length == 2)
            {
                var end = _dandanTentacles.Max(s => s.End);
                _dandanTentacles.Clear();
                foreach (var part in parts)
                    _dandanTentacles.Add(DandanRectangle(part.Location, part.Heading, 50, 7.5f, end));
            }
        }

        // The cue disappears before the cast:00:21:55 status 2195=1017, gone
        // 00:22:04, cast 00:22:06. Retain the scalar through the rotation, not
        // the frame-scoped aura wrapper. Unknown cues never select a guessed side.
        foreach (var aura in boss.CharacterAuras.Where(a => a.Id == 2195))
        {
            if (aura.Value == 1016 || aura.Value == 1017)
            {
                _dandanReverse = aura.Value == 1017;
                _dandanCueUntil = now.AddSeconds(30);
            }
        }

        if (boss.IsCasting && boss.CastingSpellId == 45589)
        {
            var finish = now + boss.SpellCastInfo.RemainingCastTime;
            if (Math.Abs((finish - _dandanChargeFinish).TotalSeconds) > 1)
            {
                _dandanChargeFinish = finish;
                _dandanCharges.Clear();
                if (_dandanReverse.HasValue && now < _dandanCueUntil)
                {
                    // Reverse cue 1017 at(830.960,684.993) predicted the observed
                    // first endpoint(797.235,641.022) and next heading 0.262.
                    // The first inward chord turns 22.5deg, the second 157.5deg.
                    var origin = boss.Location;
                    var inward = Math.Atan2(DandanCenter.X - origin.X, DandanCenter.Z - origin.Z);
                    var first = inward + (_dandanReverse.Value ? -1 : 1) * Math.PI / 8;
                    var length = DandanChordLength(origin, first);
                    var middle = new Vector3(origin.X + (float)Math.Sin(first) * length, -30, origin.Z + (float)Math.Cos(first) * length);
                    var second = first + (_dandanReverse.Value ? 1 : -1) * Math.PI * 7 / 8;
                    // Helpers finish about 4.2s after the parent in the first pull.
                    // Six seconds retains both impacts without reaching the next
                    // Spit cast; expiry is fixed at first observation, never renewed.
                    var end = finish.AddSeconds(6);
                    _dandanCharges.Add(DandanRectangle(origin, first, length, 10.5f, end));
                    _dandanCharges.Add(DandanRectangle(middle, second, DandanChordLength(middle, second), 10.5f, end));
                }
                else
                    ff14bot.Helpers.Logging.Write("[Merchant Dandan] Devour cue unavailable; retaining actual helper geometry until the next observed preview.");
            }
        }

        foreach (var actor in actors.Where(a => a.IsCasting))
        {
            var action = actor.CastingSpellId;
            if (!DandanOwnedActions.Contains(action))
                continue;
            var key = ((ulong)actor.ObjectId << 32) | action;
            if (_dandanCasts.ContainsKey(key))
                continue;
            // The short helper rectangles are a fallback for mid-cast attachment;
            // the observed early forecast owns the normal sequence without duplicates.
            if (action is 45598 or 45599 or 45600 or 45601 && _dandanCharges.Any(s => s.End > now))
                continue;
            if (action == 45608 && _dandanTentacles.Any(s => s.End > now))
            {
                // 01:37:11 damage/status observation outlasted the parent's
                //2s tail. The real helper owns the final lifetime: extend once
                // from its native finish, never by a sliding now-based timeout.
                if (_dandanTentacleHelpers.Add(actor.ObjectId))
                {
                    var helperEnd = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
                    foreach (var shape in _dandanTentacles)
                        if (shape.End < helperEnd)
                            shape.End = helperEnd;
                }

                continue;
            }

            var end = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
            if (action is 45610 or 48044 or 48045 or 48046 or 48047)
            {
                // The native helper destination is zero for these self-centered
                // circles/rings. Use its frozen body location, unlike Spit below.
                // 01:11:41: the second Maw hit arrived 0.87s after native finish;
                // the former 0.65s tail released it before damage. Hold 1.2s while
                // still releasing before the next 2.5s ring expansion resolves.
                var finish = now + actor.SpellCastInfo.RemainingCastTime;
                var points = action switch
                {
                    48045 => DandanRing(7.5f, 16.5f),
                    48046 => DandanRing(15.5f, 24.5f),
                    48047 => DandanRing(23.5f, 36.5f),
                    _ => DandanDisc(8.5f)};
                _dandanCasts[key] = new DandanShape
                {
                    Origin = actor.Location,
                    Points = points,
                    Resolve = finish,
                    End = finish.AddSeconds(1.2),
                    Sequential = true,
                    Maw = action == 45610,
                    RingStage = action >= 48044 && action <= 48047 ? (int)action - 48043 : 0
                };
            }
            else if (action is 45602 or 45616)
            {
                // October 2 00:55:51: the rendered origin(810.147,689.296) is
                //10y forward of the body, not the director-centered CastLocation.
                // Both earlier anchors failed live: body covered the whole floor;
                // CastLocation falsely marked damaging positions safe. Read the
                // live omen axes, freezing only scalar geometry. A missing first-
                // frame projection is retried next pulse rather than guessed.
                var omen = actor.OmenMatrix;
                var origin = omen.Center;
                omen.Transform(new Vector3(0, 0, 1), out var forward);
                if (origin.Distance2D(DandanCenter) > 45 || forward.Distance2D(origin) < 20)
                    continue;
                var heading = (float)Math.Atan2(forward.X - origin.X, forward.Z - origin.Z);
                var tip = (float)(-.5 / Math.Sin(Math.PI / 3));
                var width = (float)((55 - tip) * Math.Tan(Math.PI / 3));
                _dandanCasts[key] = new DandanShape
                {
                    Origin = origin,
                    Heading = heading,
                    End = end,
                    FrontalCone = true,
                    Points = new[]
                    {
                        new Vector2(0, tip),
                        new Vector2(width, 55),
                        new Vector2(-width, 55)
                    }
                };
            }
            else
                _dandanCasts[key] = DandanRectangle(actor.Location, actor.Heading, action == 45608 ? 50 : 15, action == 45608 ? 7.5f : 10.5f, end);
        }

        _dandanCharges.RemoveAll(s => s.End <= now);
        _dandanTentacles.RemoveAll(s => s.End <= now);
        foreach (var key in _dandanCasts.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            _dandanCasts.Remove(key);
        UpdateDandanConeGoal();
        UpdateDandanRingGoal();
        UpdateDandanRefugeHold();
    }

    private void UpdateDandanRefugeHold()
    {
        // October 2 11:42:07–13: each native escape completion was followed by
        // another boss-center chase while the tentacle preview still existed.
        // Hold only routine movement across that gap; native avoidance and the
        // dedicated ring/cone movers retain priority, and attacks/heals still run.
        var escaping = AvoidanceManager.IsRunningOutOfAvoid;
        var active = ActiveDandanShapes().Any() || DateTime.UtcNow < _dandanGuillotineFinish.AddSeconds(2);
        if (active)
        {
            CapabilityManager.Update(_dandanRefugeHandle, CapabilityFlags.Movement, 1000, "Preserve Dandan refuge until the active hazard resolves");
            if (!_dandanRefugeOwned)
                ff14bot.Helpers.Logging.Write("[Merchant Dandan] Holding routine movement through active hazards.");
            _dandanRefugeOwned = true;
            // Native escape can finish with forward input still held. Stop once
            // at the handoff, never while escaping or while a direct owner moves.
            if (_dandanWasEscaping && !escaping && !_dandanConeMoving && !_dandanRingMoving)
                ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        }
        else
            ReleaseDandanRefugeHold();
        _dandanWasEscaping = escaping;
    }

    private void ReleaseDandanRefugeHold()
    {
        if (_dandanRefugeOwned)
        {
            CapabilityManager.Clear(_dandanRefugeHandle, CapabilityFlags.Movement, "Dandan hazards resolved or encounter ended");
            ff14bot.Helpers.Logging.Write("[Merchant Dandan] Routine movement released.");
        }

        _dandanRefugeOwned = _dandanWasEscaping = false;
    }

    private void ForecastDandanTentacles(BattleCharacter[] actors, BattleCharacter boss, DateTime now)
    {
        if (_dandanTentaclePredicted || _dandanTentacles.Count == 0)
            return;
        var fixedParts = actors.Where(a => a.BaseId == 19152 && a.IsVisible && a.IsAlive).ToArray();
        var rotating = actors.Where(a => a.BaseId == 19350 && a.IsVisible && a.IsAlive).ToArray();
        if (fixedParts.Length != 2 || rotating.Length != 2)
            return;
        var remaining = boss.SpellCastInfo.RemainingCastTime.TotalSeconds;
        var forecast = new List<DandanShape>();
        var estimates = new List<double>();
        for (var i = 0; i < 2; ++i)
        {
            var fixedPart = fixedParts[i];
            var moving = rotating.OrderBy(a => a.Location.Distance2DSqr(fixedPart.Location)).First();
            if (moving.Location.Distance2D(fixedPart.Location) > 1)
                return;
            var other = fixedParts[1 - i];
            var toward = Math.Atan2(other.Location.X - fixedPart.Location.X, other.Location.Z - fixedPart.Location.Z);
            var sign = Math.Sign(Math.Sin(toward - fixedPart.Heading));
            var angle = Math.Atan2(Math.Sin(moving.Heading - fixedPart.Heading), Math.Cos(moving.Heading - fixedPart.Heading)) * 180 / Math.PI * sign;
            if (!_dandanTentacleMotion.TryGetValue(fixedPart.ObjectId, out var samples))
                _dandanTentacleMotion[fixedPart.ObjectId] = samples = new();
            samples.Add((now, angle));
            samples.RemoveAll(s => (now - s.Time).TotalSeconds > .7);
            // Capture 30 complete helper pairs: cruise 13deg/s, a large apex
            // followed by a +/-10deg oscillation, settling about 0.6s before
            // the parent finish. Constant-velocity extrapolation failed at
            // both turns. Forecast only in the captured 3s window with a
            // measured trend; unfamiliar motion keeps the live-part fallback.
            if (remaining > 3 || remaining < 2.85)
                continue;
            var prior = samples.OrderBy(s => Math.Abs((now - s.Time).TotalSeconds - .35)).First();
            var elapsed = (now - prior.Time).TotalSeconds;
            if (elapsed < .2 || angle < -12 || angle > 61)
                continue;
            var velocity = (angle - prior.Angle) / elapsed;
            if (angle <= 50 && !(velocity < -8 || velocity > 8 && angle > 10))
                continue;
            var direction = angle > 50 || velocity < -8 ? -1 : 1;
            var estimate = angle + direction * 13 * (remaining - .6);
            if (direction < 0 && estimate < -10)
            {
                estimate = -20 - estimate;
                if (estimate > 10)
                    estimate = 20 - estimate;
            }

            estimates.Add(estimate);
            // Replay maximum error 1.87deg; reserve +/-3deg plus the ordinary
            //0.5y line margin. Two-degree samples overlap throughout 50y.
            // All 30 pairs retained a refuge, with maximum 2.88s travel budget.
            var end = _dandanTentacles.Max(s => s.End);
            for (var delta = -3; delta <= 3; delta += 2)
                forecast.Add(DandanRectangle(fixedPart.Location, fixedPart.Heading + sign * (estimate + delta) * Math.PI / 180, 50, 7.5f, end));
        }

        if (forecast.Count != 8)
            return;
        _dandanTentacles.Clear();
        _dandanTentacles.AddRange(forecast);
        _dandanTentaclePredicted = true;
        ff14bot.Helpers.Logging.Write("[Merchant Dandan] Tentacle settled-angle forecast: " + string.Join(", ", estimates.Select(a => a.ToString("F2"))));
    }

    private void UpdateDandanRingGoal()
    {
        // 01:42 and 02:31: native independent ring escapes reached the next
        // ring after its snapshot. One owner stages for both orbs and the next
        // wave, including the moving bubbles. Favor nearby shared
        // refuges, otherwise hold near the next refuge until the current
        // wave's existing impact tail ends. Unrelated overlaps restore native avoidance.
        // 11:58:27–40: delegating the ring/bubble overlap to independent native
        // escapes chased a bubble to the wall, caused Bubble Gaol 5047 and death.
        // Treat bubble contact as a hard constraint on the entire timed segment,
        // not a weighted avoidance cost; suppress its duplicate native geometry
        // only while this joint planner has a validated goal.
        var active = ActiveDandanShapes().ToArray();
        // 12:14:27 Bind preceded the tentacle impact: native bubble-only escape
        // kept running ahead of the moving lane, reaching the wall before the
        // forecast could help. Use the same hard-contact planner during the
        // quiet bubble window, then hand off to the joint ring plan or native
        // owner as soon as a different mechanic appears.
        if (active.Length > 0 && active.All(s => s.Bubble))
        {
            var now = DateTime.UtcNow;
            // Prefer a five-second lane refuge: a two-second-only choice in
            // the 12:14 replay delayed escape until the outer lanes boxed it in.
            var horizon = now.AddSeconds(3.8);
            if (_dandanRingGoal.HasValue && DandanBubblePathClear(_dandanRingGoal.Value, horizon))
                return;
            var rankedBubbles = DandanFloorPoints().OrderBy(p => p.Distance2DSqr(Core.Me.Location)).ToArray();
            var refuge = rankedBubbles.Where(p => DandanBubblePathClear(p, horizon)).Take(1).ToArray();
            if (refuge.Length == 0)
                refuge = rankedBubbles.Where(p => DandanBubblePathClear(p, now)).Take(1).ToArray();
            if (refuge.Length == 0)
                refuge = rankedBubbles.Where(p => DandanBubblePathClear(p, now.AddSeconds(-1.2), .5f)).Take(1).ToArray();
            if (refuge.Length == 0)
            {
                ReleaseDandanRings();
                return;
            }

            _dandanRingGoal = refuge[0];
            _dandanRingStage = DateTime.MinValue;
            ff14bot.Helpers.Logging.Write("[Merchant Dandan] Bubble lane refuge: " + refuge[0]);
            return;
        }

        if (!active.Any(s => s.RingStage > 0) || active.Any(s => s.RingStage == 0 && !s.Bubble))
        {
            ReleaseDandanRings();
            return;
        }

        active = active.Where(s => s.RingStage > 0).ToArray();
        var stage = active.Min(s => s.Resolve);
        if (_dandanRingGoal.HasValue && _dandanRingStage == stage && DandanBubblePathClear(_dandanRingGoal.Value, stage))
            return;
        var future = _dandanCasts.Values.Where(s => s.RingStage > 0 && s.Resolve > stage.AddSeconds(.5)).ToArray();
        var nextTime = future.Select(s => s.Resolve).DefaultIfEmpty(DateTime.MaxValue).Min();
        var next = future.Where(s => s.Resolve <= nextTime.AddSeconds(nextTime == DateTime.MaxValue ? 0 : .5)).ToArray();
        var floor = DandanFloorPoints().ToArray();
        var safe = floor.Where(p => !active.Any(s => DandanRingContains(s, p))).ToArray();
        if (safe.Length == 0)
        {
            ReleaseDandanRings();
            return;
        }

        // 02:39:53: an attractive future pocket 12.6y away missed the current
        // snapshot. Restrict scoring to points reachable before that snapshot
        // at measured 6y/s, reserving 0.2s startup slack. If already too late,
        // take the nearest current refuge rather than optimize a later wave.
        var budget = Math.Max(0, (stage - DateTime.UtcNow).TotalSeconds - .2) * 6;
        var reachable = safe.Where(p => p.Distance2D(Core.Me.Location) <= budget).ToArray();
        if (reachable.Length > 0)
            safe = reachable;
        var nextSafe = floor.Where(p => !next.Any(s => DandanRingContains(s, p))).ToArray();
        // The next-wave distance dominates ordinary travel preference so the
        // short 2.5s interval is spent crossing the ring edge, not the arena.
        // Do not unconditionally select a shared refuge: a far side pocket
        // can cost more travel than staging at the adjacent changing edge.
        var ranked = reachable.Length == 0 ? safe.OrderBy(p => p.Distance2DSqr(Core.Me.Location)) : safe.OrderBy(p => (nextSafe.Length == 0 ? 0 : 4 * nextSafe.Min(q => q.Distance2D(p))) + p.Distance2D(Core.Me.Location));
        var clear = ranked.Where(p => DandanBubblePathClear(p, stage)).Take(1).ToArray();
        // Crossing lane fronts sometimes leave no stationary long-horizon
        // refuge. A short safe waypoint can navigate that gap; retain the same
        //4.4y swept margin and current ring-safe endpoints, and replan next pulse.
        // Never relax the contact radius or count an unsafe segment as progress.
        if (clear.Length == 0)
            clear = ranked.Where(p => DandanBubblePathClear(p, DateTime.UtcNow.AddSeconds(-1.2), .5f)).Take(1).ToArray();
        if (clear.Length == 0)
        {
            ReleaseDandanRings();
            return;
        }

        _dandanRingGoal = clear[0];
        _dandanRingStage = stage;
        ff14bot.Helpers.Logging.Write("[Merchant Dandan] Ring stage " + active[0].RingStage + " refuge: " + _dandanRingGoal.Value);
    }

    private static IEnumerable<Vector3> DandanFloorPoints()
    {
        // The convex 19.5y platform permits straight segments between these
        // inset endpoints; half-yalm candidates preserve narrow shared refuges.
        for (var x = -18.5f; x <= 18.5f; x += .5f)
            for (var z = -18.5f; z <= 18.5f; z += .5f)
                if (x * x + z * z <= 18.5f * 18.5f)
                    yield return DandanCenter + new Vector3(x, 0, z);
    }

    private bool DandanBubblePathClear(Vector3 goal, DateTime stage, float minimumHold = 2)
    {
        // Observed cruise is 3y/s along a fixed lane. Model player travel at 6y/s
        // plus 0.2s startup, then reserve the destination through the current
        // impact tail (at least two seconds). The 11:52 replay showed a short
        // horizon selecting a pocket that a bubble reaches before the next
        // ring handoff; holding through that handoff removes the trap.
        // Closest approach of each pair of linear segments is exact, avoiding
        // missed contacts between sampled points. Recheck every pulse; a stale
        // bubble sample rejects manual ownership and restores native avoidance.
        var start = Core.Me.Location;
        var duration = start.Distance2D(goal) / 6f;
        foreach (var bubble in _dandanBubbles.Values)
        {
            if ((DateTime.UtcNow - bubble.Sample).TotalSeconds > .5)
                return false;
            var origin = bubble.Position;
            if (!DandanRelativeSegmentClear(start - origin, -bubble.Velocity, .2f))
                return false;
            origin += bubble.Velocity * .2f;
            var velocity = duration > .001f ? (goal - start) / duration : Vector3.Zero;
            if (!DandanRelativeSegmentClear(start - origin, velocity - bubble.Velocity, duration))
                return false;
            origin += bubble.Velocity * duration;
            var hold = Math.Max(minimumHold, (stage.AddSeconds(1.2) - DateTime.UtcNow).TotalSeconds - .2 - duration);
            if (!DandanRelativeSegmentClear(goal - origin, -bubble.Velocity, (float)hold))
                return false;
        }

        return true;
    }

    private static bool DandanRelativeSegmentClear(Vector3 relative, Vector3 velocity, float duration)
    {
        var speed = velocity.X * velocity.X + velocity.Z * velocity.Z;
        var time = speed < .0001f ? 0 : Math.Max(0, Math.Min(duration, -(relative.X * velocity.X + relative.Z * velocity.Z) / speed));
        var nearest = relative + velocity * time;
        // Add 0.3y to the native 4.1y reservation: the 11:58 replay's live position
        // quantization eroded a tangent by 0.026y between replans. This leaves
        // clearance for the next pulse without shrinking the native margin.
        return nearest.X * nearest.X + nearest.Z * nearest.Z >= 4.4f * 4.4f;
    }

    private static bool DandanRingContains(DandanShape shape, Vector3 point)
    {
        // Add 0.2y to the normal 0.5y edge margin for movement arrival tolerance.
        var radius = point.Distance2D(shape.Origin);
        var inner = shape.RingStage == 1 ? 0 : (shape.RingStage - 1) * 8 - .7f;
        var outer = (shape.RingStage == 4 ? 36 : shape.RingStage * 8) + .7f;
        return radius >= inner && radius <= outer;
    }

    private void ReleaseDandanRings()
    {
        if (_dandanRingOwned)
            CapabilityManager.Clear(_dandanRingHandle, CapabilityFlags.Movement, "Dandan ring sequence ended");
        if (_dandanRingMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        _dandanRingOwned = _dandanRingMoving = false;
        _dandanRingGoal = null;
        _dandanRingStage = DateTime.MinValue;
    }

    private void UpdateDandanConeGoal()
    {
        // 01:32 and 01:43: native cone egress repeatedly jumped in place for the
        // entire cast despite a safe endpoint and no Bind. Bypass service-path
        // traversal only for an isolated frontal cone. The 12:22:21 Spit capture
        // took a detour and missed its snapshot; the same rendered cone admits
        // a 4.05s direct escape. Twelve fresh cone captures fit their deadlines.
        // Both Spit 45602 and Swallowed Sea 45616 use this narrow fallback. The floor is
        // convex, so a direct segment to an inset refuge remains on the floor.
        // Any overlap or missing refuge restores the normal avoidance owner.
        var shapes = ActiveDandanShapes().ToArray();
        // 02:23:53: remaining at Guillotine's far-edge refuge required 30.5y
        // of cone travel, exceeding the 4.7s snapshot deadline. Regroup only
        // after the native Guillotine helper finish plus a 2s impact tail.
        // Its following quiet window permits center staging without predicting
        // a cone heading. Any new cast/hazard cancels staging; the actual cone
        // then replaces this goal. Wipe/context cleanup clears the finish latch.
        var now = DateTime.UtcNow;
        if (shapes.Length == 0 && now > _dandanGuillotineFinish.AddSeconds(2) && now < _dandanGuillotineFinish.AddSeconds(12) && !GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(a => a.IsValid && a.BaseId == 19151 && a.IsCasting))
        {
            if (!_dandanConeGoal.HasValue || _dandanConeShape != null)
                ff14bot.Helpers.Logging.Write("[Merchant Dandan] Guillotine resolved; regrouping for the next cone.");
            _dandanConeShape = null;
            _dandanConeGoal = DandanCenter;
            return;
        }

        if (shapes.Length != 1 || !shapes[0].FrontalCone)
        {
            ReleaseDandanCone();
            return;
        }

        var cone = shapes[0];
        if (_dandanConeShape == cone && _dandanConeGoal.HasValue)
            return;
        var candidates = new List<Vector3>();
        for (var x = -18.5f; x <= 18.5f; x += .5f)
            for (var z = -18.5f; z <= 18.5f; z += .5f)
            {
                if (x * x + z * z > 18.5f * 18.5f)
                    continue;
                var point = DandanCenter + new Vector3(x, 0, z);
                var dx = point.X - cone.Origin.X;
                var dz = point.Z - cone.Origin.Z;
                var along = dx * Math.Sin(cone.Heading) + dz * Math.Cos(cone.Heading);
                var across = dx * Math.Cos(cone.Heading) - dz * Math.Sin(cone.Heading);
                // Extra 0.2y perpendicular clearance covers the arrival tolerance;
                // the cone polygon already includes its ordinary 0.5y edge margin.
                if (along < -.5 / Math.Sin(Math.PI / 3) || Math.Abs(across) > (along + .5 / Math.Sin(Math.PI / 3)) * Math.Tan(Math.PI / 3) + .4)
                    candidates.Add(point);
            }

        if (candidates.Count == 0)
        {
            ReleaseDandanCone();
            return;
        }

        _dandanConeGoal = candidates.OrderBy(p => p.Distance2DSqr(Core.Me.Location)).First();
        _dandanConeShape = cone;
        ff14bot.Helpers.Logging.Write("[Merchant Dandan] Frontal cone direct refuge: " + _dandanConeGoal.Value);
    }

    private async System.Threading.Tasks.Task<bool> RunDandanCone()
    {
        if (_dandanRingGoal.HasValue)
        {
            CapabilityManager.Update(_dandanRingHandle, CapabilityFlags.Movement, 1000, "Holding the shared Dandan ring refuge between sequential impacts");
            _dandanRingOwned = true;
            if (AvoidanceManager.IsRunningOutOfAvoid)
            {
                _dandanRingMoving = false;
                return false;
            }

            if (Core.Me.Distance2D(_dandanRingGoal.Value) <= .2f)
            {
                if (_dandanRingMoving)
                    ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
                _dandanRingMoving = false;
                return false;
            }

            _dandanRingMoving = true;
            ff14bot.Navigation.Navigator.PlayerMover.MoveTowards(_dandanRingGoal.Value);
            await Buddy.Coroutines.Coroutine.Yield();
            return true;
        }

        if (!_dandanConeGoal.HasValue)
        {
            ReleaseDandanCone();
            return false;
        }

        // Let unrelated emergency avoidance finish first. Suppress only routine
        // movement after arrival, returning false so healing and rotation run.
        CapabilityManager.Update(_dandanConeHandle, CapabilityFlags.Movement, 1000, "Holding frontal-cone refuge after repeated service-path stalls");
        _dandanConeOwned = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _dandanConeMoving = false;
            return false;
        }

        if (Core.Me.Distance2D(_dandanConeGoal.Value) <= .2f)
        {
            if (_dandanConeMoving)
                ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
            _dandanConeMoving = false;
            return false;
        }

        _dandanConeMoving = true;
        ff14bot.Navigation.Navigator.PlayerMover.MoveTowards(_dandanConeGoal.Value);
        await Buddy.Coroutines.Coroutine.Yield();
        return true;
    }

    private void ReleaseDandanCone()
    {
        if (_dandanConeOwned)
            CapabilityManager.Clear(_dandanConeHandle, CapabilityFlags.Movement, "Frontal-cone direct refuge ended");
        if (_dandanConeMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        _dandanConeOwned = _dandanConeMoving = false;
        _dandanConeShape = null;
        _dandanConeGoal = null;
    }

    private static float DandanChordLength(Vector3 origin, double heading)
    {
        // Charges follow a 30y orbit, not the smaller walkable floor. Solve the
        // positive ray/circle intersection; rounding may put the start just outside.
        var x = origin.X - DandanCenter.X;
        var z = origin.Z - DandanCenter.Z;
        var dot = x * Math.Sin(heading) + z * Math.Cos(heading);
        return (float)(-dot + Math.Sqrt(Math.Max(0, dot * dot + 900 - x * x - z * z)));
    }

    private static DandanShape DandanRectangle(Vector3 origin, double heading, float length, float halfWidth, DateTime end) => new()
    {
        Origin = origin,
        Heading = (float)heading,
        End = end,
        Points = new[]
        {
            new Vector2(-halfWidth, -.5f),
            new Vector2(halfWidth, -.5f),
            new Vector2(halfWidth, length + .5f),
            new Vector2(-halfWidth, length + .5f)
        }
    };
    private void ReleaseDandan()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveDandan;
        ClearDandanForecasts();
    }

    private void ClearDandanForecasts()
    {
        ReleaseDandanRefugeHold();
        ReleaseDandanCone();
        ReleaseDandanRings();
        _dandanCharges.Clear();
        _dandanTentacles.Clear();
        _dandanTentacleHelpers.Clear();
        _dandanTentacleMotion.Clear();
        _dandanTentaclePredicted = false;
        _dandanCasts.Clear();
        _dandanBubbles.Clear();
        _dandanReverse = null;
        _dandanCueUntil = _dandanChargeFinish = DateTime.MinValue;
        _dandanGuillotineFinish = DateTime.MinValue;
    }

    // October 1: normal boss 19218 and helpers use(170,-16,-815); the shipped arena
    // effect has that center and the reference floor is 40y square. Keep 0.5y inset.
    private static readonly Vector3 SwordmasterCenter = new(170, -16, -815);
    private static readonly uint[] SwordmasterOwnedActions =
    {
        47571,
        47994,
        46614,
        48651,
        46622,
        46625,
        46626,
        46627,
        46628,
        46641,
        46615,
        47233,
        46643,
        46644,
        46620
    };
    private readonly Dictionary<ulong, CastShape> _swordmasterCasts = new();
    private readonly List<SwordmasterRegion> _swordmasterTether = new();
    private readonly Dictionary<uint, CastShape> _swordmasterWounds = new();
    private readonly Dictionary<uint, CastShape> _swordmasterConfluence = new();
    private readonly Dictionary<uint, CastShape> _swordmasterMeteors = new();
    private readonly Dictionary<uint, SwordmasterRegion[]> _swordmasterShelters = new();
    private CastShape _swordmasterMagnet;
    private readonly Dictionary<uint, CastShape> _swordmasterMaw = new();
    private readonly List<SwordmasterRegion> _swordmasterFloor = new();
    private DateTime _swordmasterFloorEnd;
    private readonly Dictionary<uint, CastShape> _swordmasterCrosses = new();
    private readonly CapabilityManagerHandle _swordmasterMagnetHandle = CapabilityManager.CreateNewHandle();
    private bool _swordmasterMagnetOwned, _swordmasterMagnetMoving;
    private sealed class SwordmasterRegion
    {
        internal Vector3 Origin;
        internal float Heading;
        internal Vector2[] Points;
        internal DateTime End;
    }

    private static bool InSwordmaster() => WorldManager.ZoneId == 1315 && Core.Me != null && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Distance2D(SwordmasterCenter) < 55 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.IsAlive && b.BaseId == 19218 && b.Distance2D(SwordmasterCenter) < 45);
    private void RegisterSwordmasterAvoidance()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveSwordmaster;
        ff14bot.NeoProfile.BotEvents.OnPulse += ObserveSwordmaster;
        // Malefic Quartering 46608 applies the captured directional status after its
        // raidwide. Mitigation remains with the routine; the debuff changes geometry.
        SpellsToMitigate.Add(46608);
        // The captured Steelsbreath Release deals raidwide damage before Plummet.
        // Leave action selection to the routine while the floor hazards resolve.
        SpellsToMitigate.Add(46632);
        SpellsToMitigate.Add(46638); // Captured floating-branch follow-up raidwide at 21:11:39.
        SpellsToTankBust.Add(46645); // Sting of the Scorpion killed a near-full-health player at 21:37:42.
        AvoidanceHelpers.AddAvoidSquareDonut(InSwordmaster, 39, 39, 140, 140, () => new[] { SwordmasterCenter });
        var tip = (float)(-.5 / Math.Sin(Math.PI / 4));
        var cone = new[]
        {
            new Vector2(0, tip),
            new Vector2(50 - tip, 50),
            new Vector2(tip - 50, 50)
        };
        var wall = new[]
        {
            new Vector2(-10.5f, -.5f),
            new Vector2(10.5f, -.5f),
            new Vector2(10.5f, 40.5f),
            new Vector2(-10.5f, 40.5f)
        };
        AvoidanceManager.AddAvoidPolygon<CastShape>(InSwordmaster, null, 80, c => -c.Heading, _ => 1, _ => 15, c => c.Action == 47571 ? wall : cone, c => c.Location, ActiveSwordmasterCasts, priority: AvoidancePriority.High);
        // The reversed Lash sequence hit at 20:40:08 because a distant first-wave
        // refuge left too much travel after the effect tail. While both pairs are
        // visible, reserve the interior of the NEXT cone with a 2y inset.
        // This stages beside the crossing without entering the active first cone;
        // the normal full cone takes over after the first pair resolves.
        // The 1y inset passed some pulls but left only 0.5y after active padding;
        // at 21:49:10 RB found no staging point for 3s. Restore a 1.5y navigable
        // strip with the corrected fixed deadlines, retaining a short handoff.
        var nextTip = (float)(2 / Math.Sin(Math.PI / 4));
        var nextCone = new[]
        {
            new Vector2(0, nextTip),
            new Vector2(50 - nextTip, 50),
            new Vector2(nextTip - 50, 50)
        };
        AvoidanceManager.AddAvoidPolygon<CastShape>(InSwordmaster, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => nextCone, c => c.Location, UpcomingSwordmasterLash, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<SwordmasterRegion>(InSwordmaster, null, 80, r => -r.Heading, _ => 1, _ => 15, r => r.Points, r => r.Origin, () => _swordmasterTether.Where(r => r.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        // October 1 22:19:33: negative 4822 in NE gained Levitation 4837;
        // Concentrativity 46644 then threw the player 30y off the floor. Choose
        // the opposite-polarity quadrants before impact, rather than treating its
        // range 60 raidwide as a circle to flee. Half-yalm seams avoid boundary flips.
        AvoidanceManager.AddAvoidPolygon<SwordmasterRegion>(InSwordmaster, null, 80, _ => 0, _ => 1, _ => 15, r => r.Points, r => r.Origin, () => _swordmasterFloor.Where(r => r.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        // Earth-rending Eight 46620 crosses use the live helper origin, not its
        // stale Waiting Wounds CastLocation. Generic avoidance expired 22:28:30.16
        // before damage 22:28:31.06 and permitted a return into the horizontal arm.
        // Two rectangles express each 8y-wide cross with 0.5y edge clearance.
        foreach (var arm in new[]
        {
            new[]
            {
                new Vector2(-4.5f, -40.5f),
                new Vector2(4.5f, -40.5f),
                new Vector2(4.5f, 40.5f),
                new Vector2(-4.5f, 40.5f)
            },
            new[]
            {
                new Vector2(-40.5f, -4.5f),
                new Vector2(40.5f, -4.5f),
                new Vector2(40.5f, 4.5f),
                new Vector2(-40.5f, 4.5f)
            }
        }

        )
            AvoidanceManager.AddAvoidPolygon<CastShape>(InSwordmaster, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => arm, c => c.Location, () => _swordmasterCrosses.Values.Where(c => c.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        // The floating branch's narrow blades require shelter behind a rock that
        // survives its incoming direction, not generic rectangular avoidance.
        AvoidanceManager.AddAvoidPolygon<SwordmasterRegion>(InSwordmaster, null, 80, r => -r.Heading, _ => 1, _ => 15, r => r.Points, r => r.Origin, () => _swordmasterShelters.Values.SelectMany(r => r).Where(r => r.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        // Nine Waiting Wounds circles resolve in three rows~1.5s apart. All nine
        // cover the floor (20:28:18 no-path failure). Reserve the first two rows;
        // after the first effect, its cleared row becomes the final wave's safe space.
        AvoidanceManager.AddAvoidLocation<CastShape>(InSwordmaster, _ => 10.5f, c => c.Location, () => _swordmasterWounds.Values.Where(c => c.End > DateTime.UtcNow).OrderBy(c => c.End).Take(6));
        // Confluence helpers are centered on the boss; their stale CastLocation
        // still contained Waiting Wounds coordinates in the 20:28:36 wipe. Use the
        // actual helper position and expose one resolving circle/donut at a time.
        AvoidanceManager.AddAvoidLocation<CastShape>(InSwordmaster, _ => 8.5f, c => c.Location, () => ActiveSwordmasterConfluence().Where(c => c.Action is 46625 or 46627));
        AvoidanceHelpers.AddAvoidDonut(() => InSwordmaster() && ActiveSwordmasterConfluence().Any(c => c.Action is 46626 or 46628), () => ActiveSwordmasterConfluence().FirstOrDefault(c => c.Action is 46626 or 46628)?.Location ?? SwordmasterCenter, 80, 7.5);
        // The reverse sequence killed a player starting 3y from center: waiting
        // through the donut's effect tail left too little time to leave the circle.
        // Keep the first refuge near the 8y boundary, on its currently safe side.
        AvoidanceManager.AddAvoidLocation<CastShape>(InSwordmaster, _ => 6.5f, c => c.Location, () => ActiveSwordmasterConfluence().Where(c => c.Action == 46626));
        AvoidanceHelpers.AddAvoidDonut(() => InSwordmaster() && ActiveSwordmasterConfluence().Any(c => c.Action == 46625), () => ActiveSwordmasterConfluence().FirstOrDefault(c => c.Action == 46625)?.Location ?? SwordmasterCenter, 80, 10);
        // Plummet's meteor is distance-scaled damage: the sheet's 60y reach is not
        // an exclusion radius. It covered the entire floor at 20:38:44 and blocked
        // escape from ordinary circles. Reserve the reference 26y safe-distance
        // threshold plus 0.5y padding; the native cast collector owns the smaller circles.
        AvoidanceManager.AddAvoidLocation<CastShape>(InSwordmaster, _ => 26.5f, c => c.Location, () => _swordmasterMeteors.Values.Where(c => c.End > DateTime.UtcNow));
        // A tethered rock displaces the player 20y and stuns them. Stage 6y inward
        // of the appropriate corner, leaving the landing inside the 39y floor.
        // Concentrativity 47233 is the subsequent radial knockback, not a 60y refuge
        // search: generic avoidance tried to leave the arena in the 21:36 capture.
        AvoidanceHelpers.AddAvoidDonut(() => InSwordmaster() && _swordmasterMagnet != null && _swordmasterMagnet.End > DateTime.UtcNow, () => _swordmasterMagnet?.Location ?? SwordmasterCenter, 80, 1.5);
        // Maw is 80y FORWARD by 80y wide, not centered on the helper. After the
        // forced landing, cross behind its observed heading; suppressing the
        // whole cast caused the 21:43:54 hit despite a successful Attract landing.
        AvoidanceManager.AddAvoidPolygon<CastShape>(InSwordmaster, null, 90, c => -c.Heading, _ => 1, _ => 15, _ => new[] { new Vector2(-40.5f, -.5f), new Vector2(40.5f, -.5f), new Vector2(40.5f, 80.5f), new Vector2(-40.5f, 80.5f) }, c => c.Location, () => _swordmasterMaw.Values.Where(c => c.End > DateTime.UtcNow), priority: AvoidancePriority.High);
    }

    private IEnumerable<CastShape> ActiveSwordmasterConfluence()
    {
        var pending = _swordmasterConfluence.Values.Where(c => c.End > DateTime.UtcNow).ToArray();
        if (pending.Length == 0)
            return pending;
        var first = pending.Min(c => c.End);
        return pending.Where(c => (c.End - first).TotalSeconds < .3);
    }

    private IEnumerable<CastShape> ActiveSwordmasterCasts()
    {
        // Shifting Horizon's second pair starts 2s before the first pair resolves.
        // Generic simultaneous cones covered the floor and caused two hits and a
        // wipe at 20:09:04. Publish only the earliest impact through its effect tail.
        var pending = _swordmasterCasts.Values.Where(c => c.End > DateTime.UtcNow).ToArray();
        if (pending.Length == 0)
            return pending;
        var first = pending.Min(c => c.End);
        return pending.Where(c => (c.End - first).TotalSeconds < .3);
    }

    private IEnumerable<CastShape> UpcomingSwordmasterLash()
    {
        var pending = _swordmasterCasts.Values.Where(c => c.Action == 46614 && c.End > DateTime.UtcNow).ToArray();
        if (pending.Length == 0)
            return pending;
        var first = pending.Min(c => c.End);
        return pending.Where(c => (c.End - first).TotalSeconds > .3);
    }

    // The statuses describe the world direction an attack comes FROM, independent
    // of player facing. At 20:07:43, East 4773 plus standing west of the center took
    // Crusher 47994 vulnerability. Thus the westward helper is unsafe for East.
    // Bit order N/E/S/W keeps combined client statuses explicit and reviewable.
    private static int SwordmasterForbiddenDirections(uint status) => status switch
    {
        4780 => 1,
        4773 => 2,
        4776 => 4,
        4774 => 8,
        4781 => 3,
        4782 => 9,
        4777 => 6,
        4778 => 12,
        4775 => 10,
        4784 => 5,
        4783 => 11,
        4779 => 14,
        4785 => 7,
        4786 => 13,
        _ => 0
    };
    private void ObserveSwordmaster(object sender, EventArgs args)
    {
        if (!TreeRoot.IsRunning || ff14bot.Behavior.CommonBehaviors.IsLoading || !InSwordmaster())
        {
            ClearSwordmasterForecasts();
            return;
        }

        UpdateSwordmasterForecasts();
    }

    private void UpdateSwordmasterForecasts()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _swordmasterCasts.Keys.Where(k => _swordmasterCasts[k].End <= now).ToArray())
            _swordmasterCasts.Remove(key);
        var forbidden = Core.Me.CharacterAuras.Aggregate(0, (mask, aura) => mask | SwordmasterForbiddenDirections(aura.Id));
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(a => a.IsValid && a.Distance2D(SwordmasterCenter) < 45).ToArray();
        UpdateSwordmasterFloor(actors, now);
        UpdateSwordmasterShelters(actors, now);
        UpdateSwordmasterMagnet(actors, now);
        foreach (var key in _swordmasterWounds.Keys.Where(k => _swordmasterWounds[k].End <= now).ToArray())
            _swordmasterWounds.Remove(key);
        foreach (var key in _swordmasterConfluence.Keys.Where(k => _swordmasterConfluence[k].End <= now).ToArray())
            _swordmasterConfluence.Remove(key);
        foreach (var key in _swordmasterMeteors.Keys.Where(k => _swordmasterMeteors[k].End <= now).ToArray())
            _swordmasterMeteors.Remove(key);
        foreach (var key in _swordmasterMaw.Keys.Where(k => _swordmasterMaw[k].End <= now).ToArray())
            _swordmasterMaw.Remove(key);
        foreach (var key in _swordmasterCrosses.Keys.Where(k => _swordmasterCrosses[k].End <= now).ToArray())
            _swordmasterCrosses.Remove(key);
        foreach (var helper in actors.Where(a => a.BaseId == 9020 && a.IsCasting))
        {
            var finish = now + helper.SpellCastInfo.RemainingCastTime;
            if (helper.CastingSpellId == 46620)
                _swordmasterCrosses[helper.ObjectId] = new CastShape
                {
                    Action = 46620,
                    Location = helper.Location,
                    Heading = helper.Heading,
                    End = _swordmasterCrosses.TryGetValue(helper.ObjectId, out var cross) ? cross.End : finish.AddSeconds(1.4)
                };
            if (helper.CastingSpellId == 46643)
                _swordmasterMaw[helper.ObjectId] = new CastShape
                {
                    Action = 46643,
                    Location = helper.Location,
                    Heading = helper.Heading,
                    End = _swordmasterMaw.TryGetValue(helper.ObjectId, out var maw) ? maw.End : finish.AddSeconds(1.4)
                };
            if (helper.CastingSpellId == 46641)
                _swordmasterMeteors[helper.ObjectId] = new CastShape
                {
                    Action = 46641,
                    Location = helper.SpellCastInfo.CastLocation,
                    End = _swordmasterMeteors.TryGetValue(helper.ObjectId, out var meteor) ? meteor.End : finish.AddSeconds(1.4)
                };
            if (helper.CastingSpellId == 46622)
                // Observed damage lagged the first row's cast finish by 0.744s.
                _swordmasterWounds[helper.ObjectId] = new CastShape
                {
                    Action = 46622,
                    Location = helper.SpellCastInfo.CastLocation,
                    End = _swordmasterWounds.TryGetValue(helper.ObjectId, out var wounds) ? wounds.End : finish.AddSeconds(.95)
                };
            else if (helper.CastingSpellId is 46625 or 46626 or 46627 or 46628)
                // The donut effect arrived~1.24s after finish. Keep 1.4s and
                // preserve the two-second handoff; both margins include 0.5y.
                // As with Lash, retain the first deadline when the native timer
                // reaches zero before its IsCasting flag clears.
                _swordmasterConfluence[helper.ObjectId] = new CastShape
                {
                    Action = helper.CastingSpellId,
                    Location = helper.Location,
                    End = _swordmasterConfluence.TryGetValue(helper.ObjectId, out var confluence) ? confluence.End : finish.AddSeconds(1.4)
                };
        }

        UpdateSwordmasterTether(actors, forbidden, now);
        foreach (var actor in actors.Where(a => a.IsCasting && ((a.BaseId == 9020 && a.CastingSpellId is 47994 or 46614) || (a.BaseId == 19516 && a.CastingSpellId == 47571))))
        {
            var heading = actor.Heading;
            var quarter = ((int)Math.Round(heading / (Math.PI / 2)) % 4 + 4) % 4;
            // Heading 0 travels south (source north); heading pi/2 travels east
            // (source west). The helpers are cardinal; reject an intermediate turn.
            var delta = Math.Atan2(Math.Sin(heading - quarter * Math.PI / 2), Math.Cos(heading - quarter * Math.PI / 2));
            if (Math.Abs(delta) > .1)
                continue;
            var incoming = quarter switch
            {
                0 => 1,
                1 => 8,
                2 => 4,
                _ => 2
            };
            var key = ((ulong)actor.ObjectId << 32) | actor.CastingSpellId;
            if (actor.CastingSpellId != 46614 && (forbidden & incoming) == 0)
            {
                _swordmasterCasts.Remove(key);
                continue;
            }

            // The captured Crusher status arrived ~1.0s after reported cast finish.
            // Keep the first observed deadline: RemainingCastTime clamps to zero
            // while IsCasting lingers, which otherwise extends the tail every pulse.
            // The 20:46:15 Lash handoff lost 0.2s to that extension and was too late.
            // At 22:24:47 the 1.4s tail left only 0.6s before the second Lash's
            // cast finished. the player crossed before damage arrived but was still
            // hit, showing that damage notification is too late for the handoff.
            // Lash's observed notification delay is~0.75s; retain 0.95s for it,
            // leaving the slower wall/Crusher tail unchanged.
            var deadline = _swordmasterCasts.TryGetValue(key, out var observed) ? observed.End : (now + actor.SpellCastInfo.RemainingCastTime).AddSeconds(actor.CastingSpellId == 46614 ? .95 : 1.4);
            _swordmasterCasts[key] = new CastShape
            {
                Action = actor.CastingSpellId,
                Location = actor.Location,
                Heading = heading,
                End = deadline
            };
        }
    }

    private void UpdateSwordmasterMagnet(BattleCharacter[] actors, DateTime now)
    {
        if (_swordmasterMagnet != null && _swordmasterMagnet.End <= now)
            _swordmasterMagnet = null;
        var rocks = actors.Where(a => a.BaseId == 19222 && a.IsCasting && a.CastingSpellId == 46634).ToArray();
        var paired = rocks.Where(a => a.VfxContainer.IsValid && a.VfxContainer.Tethers.Any(t => t.Id == 38 && t.TargetId == Core.Me.ObjectId)).ToArray();
        if (rocks.Length != 4 || paired.Length != 1)
            return;
        var positive = Core.Me.HasAura(4821);
        var negative = Core.Me.HasAura(4822);
        // Installed Global Status rows: player 4821/4822 and rock 4823/4824 are
        // positive/negative respectively. The 21:36 positive-positive pair cast
        // Repel 46635 and moved 20y away; the reference's rock labels were reversed.
        var rockPositive = paired[0].HasAura(4823);
        var rockNegative = paired[0].HasAura(4824);
        if (positive == negative || rockPositive == rockNegative)
            return;
        var repel = positive == rockPositive;
        var anchor = repel ? paired[0] : rocks.OrderByDescending(r => r.Distance2D(paired[0].Location)).First();
        var dx = SwordmasterCenter.X - anchor.Location.X;
        var dz = SwordmasterCenter.Z - anchor.Location.Z;
        var length = (float)Math.Sqrt(dx * dx + dz * dz);
        if (length < 10)
            return;
        _swordmasterMagnet = new CastShape
        {
            Action = repel ? 46635u : 46636u,
            Location = new Vector3(anchor.Location.X + 6 * dx / length, SwordmasterCenter.Y, anchor.Location.Z + 6 * dz / length),
            // Freeze the deadline before RemainingCastTime clamps to zero. The
            // observed displacement begins~0.5s after finish; release before stun ends.
            End = _swordmasterMagnet?.End ?? (now + paired[0].SpellCastInfo.RemainingCastTime).AddSeconds(1)
        };
    }

    private bool HandleSwordmasterMagnet()
    {
        var now = DateTime.UtcNow;
        if (_swordmasterMagnet == null || _swordmasterMagnet.Action != 46635 || now >= _swordmasterMagnet.End)
        {
            ReleaseSwordmasterMagnet();
            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // Shared avoidance first brings us into the broad staging disk.
            // Never stop its mover while releasing this refinement's lease.
            _swordmasterMagnetMoving = false;
            ReleaseSwordmasterMagnet();
            return false;
        }

        if (Core.Me.Distance2D(_swordmasterMagnet.Location) > 2)
        {
            ReleaseSwordmasterMagnet();
            return false;
        }

        // Repel amplifies angular error because the source is only 6y away.
        // Independent replay of BOTH displacements found that the 1.5y disk can
        // reach the wall after Concentrativity. Refine the final unobstructed 2y
        // to 0.2y; sampled 0.25y errors retain the arena inset. Attract's distant
        // source does not need this stricter alignment.
        CapabilityManager.Update(_swordmasterMagnetHandle, CapabilityFlags.Movement, 1000, "Align magnetic Repel with the opposite corner");
        _swordmasterMagnetOwned = true;
        if (Core.Me.Distance2D(_swordmasterMagnet.Location) <= .2f || now >= _swordmasterMagnet.End.AddSeconds(-1))
        {
            if (_swordmasterMagnetMoving)
                ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
            _swordmasterMagnetMoving = false;
            return false; // Keep healing available while awaiting forced movement.
        }

        ff14bot.Navigation.Navigator.PlayerMover.MoveTowards(_swordmasterMagnet.Location);
        _swordmasterMagnetMoving = true;
        return true;
    }

    private void ReleaseSwordmasterMagnet()
    {
        if (_swordmasterMagnetOwned)
            CapabilityManager.Clear(_swordmasterMagnetHandle, CapabilityFlags.Movement, "Magnetic alignment ended");
        if (_swordmasterMagnetMoving && !ff14bot.Behavior.CommonBehaviors.IsLoading && Core.Me != null && !Core.Me.IsDead && !AvoidanceManager.IsRunningOutOfAvoid)
            ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        _swordmasterMagnetOwned = _swordmasterMagnetMoving = false;
    }

    private void UpdateSwordmasterTether(BattleCharacter[] actors, int forbidden, DateTime now)
    {
        _swordmasterTether.RemoveAll(r => r.End <= now);
        // Captured chain 371: wall 19220 -> joint 19230 -> player. At 20:08:55 a
        // north-west vulnerability and a south-going final leg caused vulnerability.
        // Predict the last leg before Bind 2518 fixes it; do not avoid every tether.
        var roots = actors.Where(a => a.BaseId == 19220 && a.IsCasting && a.CastingSpellId == 48651 && a.VfxContainer.IsValid).Where(a => a.VfxContainer.Tethers.Any(t => t.Id == 371 && (t.TargetId == Core.Me.ObjectId || actors.Any(j => j.BaseId == 19230 && j.ObjectId == t.TargetId && j.VfxContainer.IsValid && j.VfxContainer.Tethers.Any(last => last.Id == 371 && last.TargetId == Core.Me.ObjectId))))).ToArray();
        if (roots.Length != 1 || forbidden == 0)
            return;
        var root = roots[0];
        var quarter = ((int)Math.Round(root.Heading / (Math.PI / 2)) % 4 + 4) % 4;
        var angle = quarter * Math.PI / 2;
        if (Math.Abs(Math.Atan2(Math.Sin(root.Heading - angle), Math.Cos(root.Heading - angle))) > .1)
            return;
        _swordmasterTether.Clear();
        var end = (now + root.SpellCastInfo.RemainingCastTime).AddSeconds(1.4);
        void Add(float left, float right, float back, float front) => _swordmasterTether.Add(new SwordmasterRegion { Origin = root.Location, Heading = root.Heading, End = end, Points = new[] { new Vector2(left, back), new Vector2(right, back), new Vector2(right, front), new Vector2(left, front) } });
        var forwardIncoming = quarter switch
        {
            0 => 1,
            1 => 8,
            2 => 4,
            _ => 2
        };
        var rightIncoming = quarter switch
        {
            0 => 8,
            1 => 4,
            2 => 2,
            _ => 1
        };
        var leftIncoming = quarter switch
        {
            0 => 2,
            1 => 1,
            2 => 8,
            _ => 4
        };
        // The straight leg is 4y wide. Its 0.5y margin and the side regions'1.5y
        // thresholds retain a safe center strip only when the straight hit is allowed.
        if ((forbidden & forwardIncoming) != 0)
            Add(-2.5f, 2.5f, -.5f, 60);
        if ((forbidden & rightIncoming) != 0)
            Add(1.5f, 60, -60, 60);
        if ((forbidden & leftIncoming) != 0)
            Add(-60, -1.5f, -60, 60);
    }

    private void UpdateSwordmasterShelters(BattleCharacter[] actors, DateTime now)
    {
        foreach (var key in _swordmasterShelters.Keys.Where(k => _swordmasterShelters[k][0].End <= now).ToArray())
            _swordmasterShelters.Remove(key);
        foreach (var attack in actors.Where(a => a.BaseId == 19219 && a.IsCasting && a.CastingSpellId == 46615))
        {
            // 21:11:30: four horizontal rows, with wall blades at X 150/190 and
            // one Fallen Rock 19221 in each row. East 4966/West 4967 describes which
            // incoming direction destroys that rock; an ambiguous row is unsafe.
            var eastward = Math.Sin(attack.Heading) > 0;
            var expected = eastward ? Math.PI / 2 : 3 * Math.PI / 2;
            if (Math.Abs(Math.Atan2(Math.Sin(attack.Heading - expected), Math.Cos(attack.Heading - expected))) > .1)
                continue;
            var rocks = actors.Where(a => a.BaseId == 19221 && Math.Abs(a.Z - attack.Z) < 1).ToArray();
            var rock = rocks.Length == 1 ? rocks[0] : null;
            var known = rock != null && rock.CharacterAuras.Any(a => a.Id is 4966 or 4967);
            var broken = !known || rock.CharacterAuras.Any(a => a.Id == (eastward ? 4967u : 4966u));
            var origin = rock?.Location ?? attack.Location;
            var end = _swordmasterShelters.TryGetValue(attack.ObjectId, out var previous) ? previous[0].End : (now + attack.SpellCastInfo.RemainingCastTime).AddSeconds(1.4);
            var regions = new List<SwordmasterRegion>();
            void Add(float left, float right, float back, float front) => regions.Add(new SwordmasterRegion { Origin = origin, Heading = attack.Heading, End = end, Points = new[] { new Vector2(left, back), new Vector2(right, back), new Vector2(right, front), new Vector2(left, front) } });
            if (broken)
                Add(-5.5f, 5.5f, -45, 45);
            else
            {
                // Reference shelter is 3.3y wide. Keep 0.5y on each edge and
                // start 2.5y behind the rock to avoid navigating into its solid model.
                Add(-5.5f, 5.5f, -45, 2.5f);
                Add(-5.5f, -1.15f, 0, 45);
                Add(1.15f, 5.5f, 0, 45);
            }

            _swordmasterShelters[attack.ObjectId] = regions.ToArray();
        }
    }

    private void UpdateSwordmasterFloor(BattleCharacter[] actors, DateTime now)
    {
        var cast = actors.FirstOrDefault(a => a.BaseId == 19218 && a.IsCasting && a.CastingSpellId == 46644);
        // Freeze the first finish time: IsCasting can linger with zero remaining.
        // Keep the geometry through the observed ~1s server effect delay.
        if (cast != null && _swordmasterFloorEnd == default)
            _swordmasterFloorEnd = now + cast.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.4);
        _swordmasterFloor.Clear();
        if (_swordmasterFloorEnd <= now)
        {
            if (cast == null)
                _swordmasterFloorEnd = default;
            return;
        }

        var positive = Core.Me.CharacterAuras.Any(a => a.Id == 4821);
        var negative = Core.Me.CharacterAuras.Any(a => a.Id == 4822);
        if (positive == negative)
            return; // No polarity evidence cannot select a quadrant.
        foreach (var sign in new[]
        {
            -1,
            1
        }

        )
        {
            var x = 15 * sign;
            var z = positive ? x : -x;
            _swordmasterFloor.Add(new SwordmasterRegion { Origin = SwordmasterCenter, End = _swordmasterFloorEnd, Points = new[] { new Vector2(x - 15.5f, z - 15.5f), new Vector2(x + 15.5f, z - 15.5f), new Vector2(x + 15.5f, z + 15.5f), new Vector2(x - 15.5f, z + 15.5f) } });
        }
    }

    private void ReleaseSwordmaster()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveSwordmaster;
        ClearSwordmasterForecasts();
    }

    private void ClearSwordmasterForecasts()
    {
        ReleaseSwordmasterMagnet();
        _swordmasterTether.Clear();
        _swordmasterWounds.Clear();
        _swordmasterConfluence.Clear();
        _swordmasterMeteors.Clear();
        _swordmasterShelters.Clear();
        _swordmasterMagnet = null;
        _swordmasterMaw.Clear();
        _swordmasterFloor.Clear();
        _swordmasterFloorEnd = default;
        _swordmasterCrosses.Clear();
        _swordmasterCasts.Clear();
    }

    // October 1: parent/helper center(5,-50,-530), pearls 10y from center, and the
    // shipped six rock lanes agree with the 18y circular floor. Keep 0.5y wall inset.
    // Residential Rukhkh is deliberately outside this gate-only acceptance scope.
    private static readonly Vector3 GateRukhkhCenter = new(5, -50, -530);
    private static readonly uint[] RukhkhOwnedActions =
    {
        45752,
        46836,
        45749,
        45750,
        45763,
        45761
    };
    private readonly Dictionary<ulong, CastShape> _rukhkhFans = new();
    private readonly Dictionary<uint, RukhkhPearl> _rukhkhPearls = new();
    private DateTime _rukhkhPearlEnd, _rukhkhSphereFinish;
    private bool _rukhkhShatterSeen;
    private readonly Dictionary<uint, uint> _rukhkhRockStates = new();
    private readonly Dictionary<uint, DateTime> _rukhkhRockEnds = new();
    private readonly Dictionary<uint, CastShape> _rukhkhHowls = new();
    private readonly CapabilityManagerHandle _rukhkhHowlHandle = CapabilityManager.CreateNewHandle();
    private DateTime _rukhkhMistFinish, _rukhkhHowlPrepareUntil;
    private DateTime _rukhkhHowlReturnAfter, _rukhkhHowlReturnUntil;
    private bool _rukhkhHowlOwned, _rukhkhHowlMoving;
    // Native wrappers never survive a yield; hidden pearls retain the last measured
    // scalar position/radius until their shared effect or a new sphere sequence.
    private sealed class RukhkhPearl
    {
        internal Vector3 Location;
        internal float Radius;
    }

    private static bool InGateRukhkh() => WorldManager.ZoneId == 1315 && Core.Me != null && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Distance2D(GateRukhkhCenter) < 60 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.IsAlive && b.BaseId == 19157 && b.Distance2D(GateRukhkhCenter) < 90);
    private void RegisterRukhkhAvoidance()
    {
        // Streaming Sands 45764 is the captured 40y raidwide (October 1,20:00:28).
        // Let the routine's mitigation path handle damage instead of avoiding the floor.
        SpellsToMitigate.Add(45764);
        // TreeStart/RunAsync can be held behind an avoidance move or routine await.
        // BotEvents.OnPulse is still the bot thread and can retain short previews.
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveRukhkh;
        ff14bot.NeoProfile.BotEvents.OnPulse += ObserveRukhkh;
        AvoidanceHelpers.AddAvoidDonut(InGateRukhkh, () => GateRukhkhCenter, 100, 17.5);
        // Helpers originate at center;20y reaches beyond the entire 18y floor.
        // The 45-degree fan's straight edges are expanded 0.5y by shifting its tip.
        var tip = (float)(-.5 / Math.Sin(Math.PI / 8));
        var width = (float)((25 - tip) * Math.Tan(Math.PI / 8));
        var fan = new[]
        {
            new Vector2(0, tip),
            new Vector2(width, 25),
            new Vector2(-width, 25)
        };
        var scratchTip = (float)(-.5 / Math.Sin(Math.PI / 4));
        var scratch = new[]
        {
            new Vector2(0, scratchTip),
            new Vector2(50 - scratchTip, 50),
            new Vector2(scratchTip - 50, 50)
        };
        AvoidanceManager.AddAvoidPolygon<CastShape>(InGateRukhkh, null, 80, c => -c.Heading, _ => 1, _ => 15, c => c.Action == 45763 ? scratch : fan, c => c.Location, ActiveRukhkhFans, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<RukhkhPearl>(InGateRukhkh, p => p.Radius, p => p.Location, () => DateTime.UtcNow < _rukhkhPearlEnd ? _rukhkhPearls.Values : Enumerable.Empty<RukhkhPearl>());
        // The 2.7s Howl cast requires prior center staging. Once revealed, the normal
        // avoidance owner escapes its 24y circle with 0.5y padding and an effect tail.
        AvoidanceManager.AddAvoidLocation<CastShape>(InGateRukhkh, _ => 24.5f, c => c.Location, () => _rukhkhHowls.Values.Where(c => c.End > DateTime.UtcNow));
        // Shipped gmc 01 identities 12015284..89 span Z-515,-521,...,-545.
        // October 1 helper 14487 at(5,-50,-521) hit the matching warning lane after
        // state 4 cleared its preview. Reserve the full 36x 6 strip with 0.5y padding.
        AvoidanceManager.AddAvoidPolygon<Vector3>(InGateRukhkh, null, 80, _ => 0, _ => 1, _ => 15, _ => new[] { new Vector2(-18.5f, -3.5f), new Vector2(18.5f, -3.5f), new Vector2(18.5f, 3.5f), new Vector2(-18.5f, 3.5f) }, p => p, () => _rukhkhRockEnds.Where(p => p.Value > DateTime.UtcNow).Select(p => new Vector3(5, -50, -515 - 6 * (p.Key - 12015284))), priority: AvoidancePriority.High);
    }

    private void ObserveRukhkh(object sender, EventArgs args)
    {
        if (!TreeRoot.IsRunning || ff14bot.Behavior.CommonBehaviors.IsLoading || !InGateRukhkh())
        {
            ClearRukhkhForecasts();
            return;
        }

        UpdateRukhkhForecasts();
    }

    private void ReleaseRukhkh()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveRukhkh;
        ClearRukhkhForecasts();
    }

    private IEnumerable<CastShape> ActiveRukhkhFans()
    {
        // The same helpers turn for the short follow-up before the first effect
        // has been observed. Preserve separate action keys and resolve the earlier
        // wave first, rather than replacing its orientation or reserving both waves.
        var pending = _rukhkhFans.Values.Where(c => c.End > DateTime.UtcNow).ToArray();
        if (pending.Length == 0)
            return pending;
        var first = pending.Min(c => c.End);
        return pending.Where(c => (c.End - first).TotalSeconds < .3);
    }

    private void UpdateRukhkhForecasts()
    {
        var now = DateTime.UtcNow;
        var director = DirectorManager.ActiveDirector as ff14bot.Directors.InstanceContentDirector;
        if (director != null && director.IsValid)
        {
            foreach (var map in director.MapEffects.Where(m => m.ID >= 12015284 && m.ID <= 12015289))
            {
                if (_rukhkhRockStates.TryGetValue(map.ID, out var previous) && previous == map.State)
                    continue;
                _rukhkhRockStates[map.ID] = map.State;
                // The warning lasted~5s; damage was observed up to 2.4s after its
                // clear. Edge-trigger 3s grace covers that late effect. Unchanged
                // stale warnings expire after 12s instead of renewing indefinitely.
                if (map.State == 1)
                    _rukhkhRockEnds[map.ID] = now.AddSeconds(12);
                else if (map.State == 4 && _rukhkhRockEnds.ContainsKey(map.ID))
                    _rukhkhRockEnds[map.ID] = now.AddSeconds(3);
            }
        }

        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(a => a.IsValid && a.Distance2D(GateRukhkhCenter) < 65).ToArray();
        foreach (var key in _rukhkhFans.Keys.Where(k => _rukhkhFans[k].End <= now).ToArray())
            _rukhkhFans.Remove(key);
        foreach (var actor in actors.Where(a => a.IsCasting))
        {
            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            if (actor.BaseId == 19157 && actor.CastingSpellId == 45754 && Math.Abs((finish - _rukhkhMistFinish).TotalSeconds) > 1)
            {
                // October 1: mist preceded Howl by~17s. Center staging keeps every
                // possible reveal within the short escape budget;25s bounds stale state.
                _rukhkhMistFinish = finish;
                _rukhkhHowlPrepareUntil = finish.AddSeconds(22);
            }

            if (actor.BaseId == 9020 && actor.CastingSpellId == 45761)
            {
                _rukhkhHowlPrepareUntil = default;
                if (!_rukhkhHowls.ContainsKey(actor.ObjectId))
                {
                    var end = finish.AddSeconds(1.4);
                    _rukhkhHowls[actor.ObjectId] = new CastShape
                    {
                        Action = 45761,
                        Location = actor.Location,
                        End = end
                    };
                    // The 21:04:32 Scratch caught the player at the rim after a
                    // clean Howl escape; navigation repeatedly hit the boundary.
                    // After Howl resolves, recover central room before the next
                    // cone begins. Never return while the Howl circle is active.
                    _rukhkhHowlReturnAfter = end;
                    _rukhkhHowlReturnUntil = end.AddSeconds(5);
                }
            }

            // Biting Scratch's animated turn continued~0.7s after cast start
            // (heading 2.351 ->3.664). Generic avoidance retained the early heading
            // and took a direct hit. Refresh the 90-degree cone on every bot pulse;
            // its 40y reach covers the whole floor from either captured boss position.
            if (actor.BaseId == 19157 && actor.CastingSpellId == 45763)
            {
                _rukhkhHowlReturnUntil = default; // Shared avoidance owns the actual cone.
                _rukhkhFans[((ulong)actor.ObjectId << 32) | actor.CastingSpellId] = new CastShape
                {
                    Action = actor.CastingSpellId,
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = finish.AddSeconds(1.4)
                };
            }

            if (actor.BaseId == 9020 && actor.CastingSpellId is 45752 or 46836)
                // Captured effects/status updates lagged the reported finish by
                // up to 1.318s. Retain 1.4s, then publish the already captured next wave.
                _rukhkhFans[((ulong)actor.ObjectId << 32) | actor.CastingSpellId] = new CastShape
                {
                    Action = actor.CastingSpellId,
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = finish.AddSeconds(1.4)
                };
            if (actor.BaseId == 19157 && actor.CastingSpellId == 45748 && Math.Abs((finish - _rukhkhSphereFinish).TotalSeconds) > 1)
            {
                _rukhkhSphereFinish = finish;
                _rukhkhPearls.Clear();
                _rukhkhPearlEnd = default;
                _rukhkhShatterSeen = false;
            }

            // Sand Burst follows the final growing cone. Earlier reservation would
            // incorrectly combine future giant circles with the still-resolving fans.
            if (actor.BaseId == 19157 && actor.CastingSpellId == 46835 && !_rukhkhShatterSeen)
                _rukhkhPearlEnd = finish.AddSeconds(3);
            if (actor.BaseId == 19158 && actor.CastingSpellId is 45749 or 45750)
            {
                _rukhkhShatterSeen = true;
                _rukhkhPearlEnd = finish.AddSeconds(1.4);
                _rukhkhPearls[actor.ObjectId] = new RukhkhPearl
                {
                    Location = actor.Location,
                    Radius = actor.CastingSpellId == 45750 ? 17.5f : 5.5f
                };
            }
        }

        foreach (var key in _rukhkhHowls.Keys.Where(k => _rukhkhHowls[k].End <= now).ToArray())
            _rukhkhHowls.Remove(key);
        if (_rukhkhShatterSeen)
            return; // Cast-confirmed geometry takes precedence.
        foreach (var pearl in actors.Where(a => a.BaseId == 19158 && a.Location.Distance2D(GateRukhkhCenter) > 2 && a.Location.Distance2D(GateRukhkhCenter) < 20))
        {
            // All three settled at reach 2.25; only the two grown pearls rose to 4.5
            // before 45750, while 2.25 cast 45749. Any growth beyond 2.3 selects the
            // larger 17y radius conservatively. Both damaging circles include 0.5y.
            _rukhkhPearls[pearl.ObjectId] = new RukhkhPearl
            {
                Location = pearl.Location,
                Radius = pearl.CombatReach > 2.3f ? 17.5f : 5.5f
            };
        }
    }

    private bool HandleRukhkhHowlPreparation()
    {
        var now = DateTime.UtcNow;
        var returning = now >= _rukhkhHowlReturnAfter && now < _rukhkhHowlReturnUntil;
        if (now >= _rukhkhHowlPrepareUntil && !returning)
        {
            ReleaseRukhkhHowlPreparation();
            return false;
        }

        // At 20:20:05 the player started 17+y from safety and could not escape before
        // Howl hit. This positive preparation is restricted to Banishing Mist's
        // disappearance window and the observed post-Howl recovery interval;
        // actual damage geometry still uses RB avoidance.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _rukhkhHowlMoving = false;
            if (_rukhkhHowlOwned)
                CapabilityManager.Clear(_rukhkhHowlHandle, CapabilityFlags.Movement, "Yield Howl preparation to avoidance");
            _rukhkhHowlOwned = false;
            return false;
        }

        CapabilityManager.Update(_rukhkhHowlHandle, CapabilityFlags.Movement, 1000, returning ? "Recover central room after Sonic Howl" : "Prepare at center for Sonic Howl reveal");
        _rukhkhHowlOwned = true;
        if (Core.Me.Distance2D(GateRukhkhCenter) <= .75f)
        {
            if (_rukhkhHowlMoving)
                ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
            _rukhkhHowlMoving = false;
            if (returning)
            {
                _rukhkhHowlReturnUntil = default;
                ReleaseRukhkhHowlPreparation();
            }

            return false; // Keep healing and mitigation schedulable while waiting.
        }

        // The verified circular floor has an unobstructed segment to its center.
        // Move directly so a nav provider's arrival tolerance cannot stall outside
        // the tighter preparation radius while consuming the routine's tick.
        ff14bot.Navigation.Navigator.PlayerMover.MoveTowards(GateRukhkhCenter);
        _rukhkhHowlMoving = true;
        return true;
    }

    private void ReleaseRukhkhHowlPreparation()
    {
        if (_rukhkhHowlOwned)
            CapabilityManager.Clear(_rukhkhHowlHandle, CapabilityFlags.Movement, "Sonic Howl preparation ended");
        if (_rukhkhHowlMoving && !ff14bot.Behavior.CommonBehaviors.IsLoading && Core.Me != null && !Core.Me.IsDead && !AvoidanceManager.IsRunningOutOfAvoid)
            ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        _rukhkhHowlOwned = _rukhkhHowlMoving = false;
    }

    private void ClearRukhkhForecasts()
    {
        _rukhkhFans.Clear();
        _rukhkhPearls.Clear();
        _rukhkhPearlEnd = _rukhkhSphereFinish = default;
        _rukhkhRockStates.Clear();
        _rukhkhRockEnds.Clear();
        _rukhkhHowls.Clear();
        _rukhkhMistFinish = _rukhkhHowlPrepareUntil = default;
        _rukhkhHowlReturnAfter = _rukhkhHowlReturnUntil = default;
        ReleaseRukhkhHowlPreparation();
        _rukhkhShatterSeen = false;
    }

    // October 1: boss 19087 stands at(374.990,-29.5,529.992); helpers span the 40y
    // square. A 0.5y inset keeps shared avoidance inside its lethal outer edge.
    private static readonly Vector3 DaryaCenter = new(375, -29.5f, 530);
    private static readonly uint[] DaryaOwnedActions =
    {
        47052,
        45777,
        45779,
        45794,
        45797,
        45789,
        45786,
        45787,
        45800,
        45774,
        45775
    };
    private sealed class DaryaFamiliarLine
    {
        internal Vector3 Location;
        internal float Heading;
        internal DateTime Start, End;
    }

    private readonly Dictionary<uint, DaryaFamiliarLine> _daryaFamiliarLines = new();
    private readonly List<float> _daryaFamiliarSeams = new();
    private readonly List<Vector2[]> _daryaPreparationAreas = new();
    private DateTime _daryaSerenadeFinish, _daryaPreparationEnd;
    private void ObserveDaryaFamiliars(DateTime now)
    {
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(DaryaCenter) < 45).ToArray();
        var boss = actors.FirstOrDefault(b => b.BaseId == 19087 && b.IsCasting && b.CastingSpellId is 45772 or 45773);
        var familiars = actors.Where(b => b.BaseId is 19088 or 19089).OrderBy(b => b.X).ToArray();
        if (boss != null)
        {
            var finish = now + boss.SpellCastInfo.RemainingCastTime;
            if (Math.Abs((finish - _daryaSerenadeFinish).TotalSeconds) > 1)
            {
                _daryaSerenadeFinish = finish;
                // The longer 23:47 sequence contained four waves through finish+13s.
                // Actor disappearance ends preparation early;18s bounds stale data.
                _daryaPreparationEnd = finish.AddSeconds(18);
                _daryaFamiliarLines.Clear();
                _daryaFamiliarSeams.Clear();
                _daryaPreparationAreas.Clear();
                // October 1's one-second casts hit before a center-lane escape could
                // finish. Prepare beside a boundary between the two actor families,
                // without guessing which family the unavailable visual score names.
                // Restrict this preparation to the captured five north-wall lanes.
                if (familiars.Length == 5 && familiars.All(b => Math.Abs(b.Z - 510) < .5f) && familiars.Select((b, i) => Math.Abs(b.X - (359 + i * 8)) < .5f).All(v => v))
                    for (var i = 1; i < familiars.Length; i++)
                        if (familiars[i - 1].BaseId != familiars[i].BaseId)
                            _daryaFamiliarSeams.Add((familiars[i - 1].X + familiars[i].X) / 2);
                // Keep source identities stable across avoidance pulses. Recreating
                // the arrays per enumeration needlessly replaces otherwise fixed areas.
                if (_daryaFamiliarSeams.Count != 0)
                    _daryaPreparationAreas.AddRange(BuildDaryaPreparationPolygons());
            }
        }

        foreach (var caster in familiars.Where(b => b.IsCasting && b.CastingSpellId is 45774 or 45775))
        {
            var finish = now + caster.SpellCastInfo.RemainingCastTime;
            if (!_daryaFamiliarLines.TryGetValue(caster.ObjectId, out var line) || line.End <= now)
                _daryaFamiliarLines[caster.ObjectId] = new DaryaFamiliarLine
                {
                    Location = caster.Location,
                    Heading = caster.Heading,
                    Start = now,
                    // Fresh 23:44:14 capture hit after generic cast removal. The
                    // fixed one-second tail preserves the 0.5y padded lane to impact.
                    End = finish.AddSeconds(1.0)
                };
        }

        foreach (var key in _daryaFamiliarLines.Keys.Where(k => _daryaFamiliarLines[k].End <= now).ToArray())
            _daryaFamiliarLines.Remove(key);
        if (familiars.Length == 0)
            _daryaPreparationEnd = default;
    }

    private IEnumerable<Vector2[]> DaryaPreparationPolygons()
    {
        if (DateTime.UtcNow >= _daryaPreparationEnd || _daryaFamiliarSeams.Count == 0)
            yield break;
        // Actual wave geometry has exclusive priority through its damage tail.
        // Between waves return to a seam; never infer the unseen score's next color.
        if (_daryaFamiliarLines.Values.Any(line => line.End > DateTime.UtcNow))
            yield break;
        foreach (var polygon in _daryaPreparationAreas)
            yield return polygon;
    }

    private IEnumerable<Vector2[]> BuildDaryaPreparationPolygons()
    {
        // A two-yalm staging corridor needs at most 1.5y of movement once the first
        // wave identifies itself. RB owns the path and the later actual line dodge.
        float left = -19.5f;
        foreach (var seam in _daryaFamiliarSeams)
        {
            var right = seam - 375 - 1;
            if (right > left)
                yield return new[]
                {
                    new Vector2(left, -20),
                    new Vector2(right, -20),
                    new Vector2(right, 20),
                    new Vector2(left, 20)
                };
            left = seam - 375 + 1;
        }

        if (left < 19.5f)
            yield return new[]
            {
                new Vector2(left, -20),
                new Vector2(19.5f, -20),
                new Vector2(19.5f, 20),
                new Vector2(left, 20)
            };
    }

    private readonly Dictionary<uint, DaryaTreasureOrb> _daryaTiles = new();
    private readonly CapabilityManagerHandle _daryaMarchHandle = CapabilityManager.CreateNewHandle();
    private bool _daryaMarchOwned;
    private bool? _daryaMarchAutoFace;
    private DateTime _daryaMarchLog;
    private void UpdateDaryaTiles(DateTime now)
    {
        ResolveDaryaTimeline();
        var tiles = GameObjectManager.GetObjectsOfType<EventObject>().Where(o => o.IsValid && o.BaseId == 2015006 && o.Distance2D(DaryaCenter) < 30).ToArray();
        var present = new HashSet<uint>(tiles.Select(o => o.ObjectId));
        foreach (var key in _daryaTiles.Keys.Where(k => !present.Contains(k)).ToArray())
            _daryaTiles.Remove(key);
        foreach (var tile in tiles)
        {
            // October 1: these centered 8x 8 tiles spawn after Aqua Spear, remain
            // visible in timeline 2 through march, then change to 4 before despawn.
            // No new offset is needed: reuse the validated shared-event timeline.
            if (!tile.IsVisible)
                continue;
            var state = _daryaTimelineOffset == 0 ? (ushort)2 : Core.Memory.Read<ushort>(tile.Pointer + _daryaTimelineOffset);
            if (!_daryaTiles.TryGetValue(tile.ObjectId, out var item))
                _daryaTiles[tile.ObjectId] = item = new DaryaTreasureOrb
                {
                    End = now.AddSeconds(45)
                };
            item.Location = tile.Location;
            if (item.Timeline == state)
                continue;
            item.Timeline = state;
            // Damage preceded state 4 in the capture. Retain 0.5s through that
            // transition; unchanged stale states cannot renew the 45s upper bound.
            if (state == 4)
                item.End = now.AddSeconds(.5);
        }
    }

    private void ObserveDaryaMarch(object sender, EventArgs args)
    {
        if (ff14bot.Behavior.CommonBehaviors.IsLoading || !ff14bot.TreeRoot.IsRunning || !InDarya())
        {
            ReleaseDaryaMarch();
            return;
        }

        var now = DateTime.UtcNow;
        ObserveDaryaFamiliars(now);
        UpdateDaryaTiles(now);
        // Once forced movement 1257 begins, keep routine facing/movement disabled
        // through the native walk without issuing movement or changing its heading.
        if (_daryaMarchOwned && Core.Me.HasAura(1257))
        {
            HoldDaryaMarch();
            return;
        }

        var aura = Core.Me.CharacterAuras.FirstOrDefault(a => a.Id >= 2161 && a.Id <= 2164);
        if (aura == null || aura.TimespanLeft.TotalSeconds > 1.2)
        {
            ReleaseDaryaMarch();
            return;
        }

        var position = Core.Me.Location;
        var tiles = _daryaTiles.Values.Where(t => t.End > now).Select(t => t.Location).ToArray();
        bool Safe(Vector3 p) => Math.Abs(p.X - 375) <= 19 && Math.Abs(p.Z - 530) <= 19 && tiles.All(t => Math.Abs(p.X - t.X) > 4.5f || Math.Abs(p.Z - t.Z) > 4.5f);
        // The captured three-second walk traveled about 19y including boundary
        // interpolation; the reference upper bound is 24y. Validate the entire
        //0.5y-sampled corridor through 24y, so either travel length remains safe.
        // The two baited balls resolve before march activation; ordinary avoidance
        // owns those impacts, while this facing owner plans persistent tiles/floor.
        var turn = aura.Id == 2162 ? MathF.PI : aura.Id == 2163 ? MathF.PI / 2 : aura.Id == 2164 ? -MathF.PI / 2 : 0;
        float? bestHeading = null;
        var bestScore = float.MaxValue;
        for (var i = 0; i < 144; i++)
        {
            var direction = i * MathF.PI / 72;
            var vector = new Vector3(MathF.Sin(direction), 0, MathF.Cos(direction));
            var valid = true;
            for (float distance = 0; distance <= 24; distance += .5f)
                if (!Safe(position + vector * distance))
                {
                    valid = false;
                    break;
                }

            if (!valid)
                continue;
            var heading = direction - turn;
            var difference = heading - Core.Me.Heading;
            var score = Math.Abs(MathF.Atan2(MathF.Sin(difference), MathF.Cos(difference)));
            if (score >= bestScore)
                continue;
            bestScore = score;
            bestHeading = heading;
        }

        if (!bestHeading.HasValue)
        {
            ReleaseDaryaMarch();
            if (now >= _daryaMarchLog)
            {
                _daryaMarchLog = now.AddSeconds(1);
                ff14bot.Helpers.Logging.Write("[Merchant] No verified march corridor at the current position; retaining ordinary avoidance.");
            }

            return;
        }

        var newlyOwned = !_daryaMarchOwned;
        HoldDaryaMarch();
        // Acquiring a movement capability prevents later routine movement; it
        // does not release an already held forward input. Stop that ordinary
        // input once, while preserving any active shared-avoidance escape.
        if (newlyOwned && !AvoidanceManager.IsRunningOutOfAvoid)
            ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        Core.Me.SetFacing(bestHeading.Value);
        if (now >= _daryaMarchLog)
        {
            _daryaMarchLog = now.AddSeconds(1);
            ff14bot.Helpers.Logging.Write("[Merchant] March stage={0} aura={1} remaining={2:F2} facing={3:F4}", position, aura.Id, aura.TimespanLeft.TotalSeconds, bestHeading.Value);
        }
    }

    private void HoldDaryaMarch()
    {
        // October 2 capture: Update accepts one flag; Clear accepts a mask.
        // Share the owner and one-second expiry so both leases end with march.
        CapabilityManager.Update(_daryaMarchHandle, CapabilityFlags.Facing, 1000, "Preserve Darya's verified forced-march corridor");
        CapabilityManager.Update(_daryaMarchHandle, CapabilityFlags.Movement, 1000, "Preserve Darya's verified forced-march corridor");
        _daryaMarchOwned = true;
        if (!_daryaMarchAutoFace.HasValue)
            _daryaMarchAutoFace = GameSettingsManager.FaceTargetOnAction;
        GameSettingsManager.FaceTargetOnAction = false;
        SetMechanicGapCloserBlock(true);
    }

    private void ReleaseDaryaMarch()
    {
        if (_daryaMarchOwned)
            CapabilityManager.Clear(_daryaMarchHandle, CapabilityFlags.Facing | CapabilityFlags.Movement, "Darya forced march ended");
        if (_daryaMarchAutoFace.HasValue)
            GameSettingsManager.FaceTargetOnAction = _daryaMarchAutoFace.Value;
        _daryaMarchOwned = false;
        _daryaMarchAutoFace = null;
    }

    private readonly Dictionary<uint, CastShape> _daryaDelayedBalls = new();
    private readonly CapabilityManagerHandle _daryaWaveHandle = CapabilityManager.CreateNewHandle();
    private DateTime _daryaWaveFinish, _daryaWaveLog;
    private Vector3 _daryaWaveDirection;
    private Vector3? _daryaWaveGoal;
    private uint _daryaWaveSource;
    private bool _daryaWaveOwned, _daryaWaveMoving;
    private readonly Dictionary<uint, CastShape> _daryaSurges = new();
    private readonly Dictionary<uint, Vector3> _daryaBubbles = new();
    private DateTime _daryaSwimFinish;
    private DateTime _daryaBubbleEnd;
    private bool _daryaBubbleCastSeen;
    private readonly Dictionary<uint, CastShape> _daryaTreasure = new();
    private readonly Dictionary<uint, DaryaTreasureOrb> _daryaTreasureOrbs = new();
    private int _daryaTimelineOffset;
    private bool _daryaTimelineAttempted;
    private readonly Dictionary<uint, DaryaCurrent> _daryaCurrents = new();
    private Vector3 _daryaTideCenter;
    private DateTime _daryaTideSwitch, _daryaTideEnd;
    private bool _daryaTideStartsNear;
    private async System.Threading.Tasks.Task<bool> HandleDaryaWave()
    {
        var now = DateTime.UtcNow;
        if (_daryaWaveSource == 0 || now > _daryaWaveFinish.AddSeconds(2.2))
        {
            ReleaseDaryaWave();
            return false;
        }

        // October 1 east/west pushes displaced 35y (356.4007 ->391.4007), while
        // the reference describes 32y. Require safe landings throughout 32..35y;
        // this also covers snapshot uncertainty without approaching the lethal rim.
        // Generic omen 102 staged inside delayed Aqua Ball on the second push.
        // One semantic owner therefore plans both the push and its later circles.
        var avoids = AvoidanceManager.Avoids.ToArray();
        var balls = _daryaDelayedBalls.Values.Where(b => b.End > now).ToArray();
        bool Safe(Vector3 point) => Math.Abs(point.X - 375) <= 19 && Math.Abs(point.Z - 530) <= 19 && balls.All(b => b.Location.Distance2D(point) > 5.5f) && !avoids.Any(a => a.IsPointInAvoid(point));
        bool Valid(Vector3 point)
        {
            if (!Safe(point))
                return false;
            for (var distance = 32; distance <= 35; distance++)
                if (!Safe(point + _daryaWaveDirection * distance))
                    return false;
            return true;
        }

        // Freeze the selected stage through displacement. Revalidating it against
        // newly reached positions would otherwise send the mover back into the push.
        if (now < _daryaWaveFinish && (!_daryaWaveGoal.HasValue || !Valid(_daryaWaveGoal.Value)))
        {
            _daryaWaveGoal = null;
            var best = float.MaxValue;
            for (var x = -19; x <= 19; x++)
                for (var z = -19; z <= 19; z++)
                {
                    var candidate = DaryaCenter + new Vector3(x, 0, z);
                    if (!Valid(candidate))
                        continue;
                    var score = candidate.Distance2D(Core.Me.Location);
                    if (score >= best)
                        continue;
                    best = score;
                    _daryaWaveGoal = candidate;
                }

            if (_daryaWaveGoal.HasValue && LoggingHelpers.MechanicDiagnosticsEnabled)
                ff14bot.Helpers.Logging.Write("[Merchant] Big Wave stage={0} landing={1}", _daryaWaveGoal.Value, _daryaWaveGoal.Value + _daryaWaveDirection * 35);
        }

        if (!_daryaWaveGoal.HasValue)
        {
            ReleaseDaryaWave(false);
            if (now >= _daryaWaveLog)
            {
                _daryaWaveLog = now.AddSeconds(2);
                ff14bot.Helpers.Logging.Write("[Merchant] Big Wave has no verified landing; yielding to ordinary avoidance.");
            }

            return false;
        }

        CapabilityManager.Update(_daryaWaveHandle, CapabilityFlags.Movement, 1000, "Holding Big Wave stage for a safe landing between delayed Aqua Balls");
        _daryaWaveOwned = true;
        // Refresh the dash lease even while travel consumes this coroutine tick.
        SetMechanicGapCloserBlock(true);
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _daryaWaveMoving = false;
            return false;
        }

        if (now >= _daryaWaveFinish || Core.Me.Distance2D(_daryaWaveGoal.Value) <= .25f)
        {
            if (_daryaWaveMoving)
                ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
            _daryaWaveMoving = false;
            return false; // Preserve healing and rotation throughout the held stage.
        }

        _daryaWaveMoving = true;
        ff14bot.Navigation.Navigator.PlayerMover.MoveTowards(_daryaWaveGoal.Value);
        await Buddy.Coroutines.Coroutine.Yield();
        return true;
    }

    private void ReleaseDaryaWave(bool clearCast = true)
    {
        if (_daryaWaveOwned)
            CapabilityManager.Clear(_daryaWaveHandle, CapabilityFlags.Movement, "Big Wave staging ended");
        if (_daryaWaveMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            ff14bot.Navigation.Navigator.PlayerMover.MoveStop();
        _daryaWaveOwned = _daryaWaveMoving = false;
        _daryaWaveGoal = null;
        if (clearCast)
            _daryaWaveSource = 0;
    }

    // Store scalar wave state: native helpers move between impacts and are reused.
    // Their observed movement, rather than a fixed cadence, advances the forecast.
    private sealed class DaryaCurrent
    {
        internal Vector3 Origin, Location;
        internal float Heading;
        internal DateTime End;
    }

    // Scalar event-object state survives the warning-to-impact transition without
    // retaining native wrappers. Unchanged stale states cannot renew the timeout.
    private sealed class DaryaTreasureOrb
    {
        internal Vector3 Location;
        internal ushort Timeline;
        internal DateTime End;
    }

    private void ResolveDaryaTimeline()
    {
        if (_daryaTimelineAttempted)
            return;
        _daryaTimelineAttempted = true;
        // Ghidra: load old ushort, compare requested state, store the same field.
        // Wildcards cover both displacements and the conditional branch operand.
        // One executable match and agreeing Read 32 results in Global 5BBC 501D,
        // CN 7BA 28760, TC 9FB 8DD 46 and prior Global 9483706D (October 1 validation).
        // Add is hexadecimal: 1C selects byte 28, the store displacement. Resolve
        // from the attached executable; never substitute the observed field offset.
        const string pattern = "Search 0F B7 B1 ? ? ? ? 49 8B E9 45 0F B6 F0 0F B7 FA 48 8B D9 66 3B F2 74 ? 66 89 91 ? ? ? ?";
        try
        {
            using var finder = new LlamaLibrary.Memory.PatternFinders.GreyMagicPf();
            if (finder.SearchMany(pattern).Length != 1)
                throw new InvalidOperationException("nonunique timeline anchor");
            var read = finder.FindSingle(pattern + " Add 3 Read32").ToInt32();
            var write = finder.FindSingle(pattern + " Add 1C Read32").ToInt32();
            if (read != write || read < 0x180 || read > 0x300)
                throw new InvalidOperationException("timeline fields disagree");
            _daryaTimelineOffset = read;
        }
        catch (Exception ex)
        {
            // A changed client keeps ordinary cast avoidance and combat available.
            ff14bot.Helpers.Logging.Write("[Merchant] Treasure preview unavailable; retaining cast warnings: " + ex.Message);
        }
    }

    private void UpdateDaryaTreasure(DateTime now)
    {
        ResolveDaryaTimeline();
        if (_daryaTimelineOffset == 0)
            return;
        var markers = GameObjectManager.GetObjectsOfType<EventObject>().Where(o => o.IsValid && o.BaseId == 2015004 && o.Distance2D(DaryaCenter) < 30).ToArray();
        var present = new HashSet<uint>(markers.Select(o => o.ObjectId));
        foreach (var key in _daryaTreasureOrbs.Keys.Where(k => !present.Contains(k)).ToArray())
            _daryaTreasureOrbs.Remove(key);
        foreach (var marker in markers)
        {
            var state = Core.Memory.Read<ushort>(marker.Pointer + _daryaTimelineOffset);
            if (!_daryaTreasureOrbs.TryGetValue(marker.ObjectId, out var orb))
                _daryaTreasureOrbs[marker.ObjectId] = orb = new DaryaTreasureOrb();
            orb.Location = marker.Location;
            if (state == orb.Timeline)
                continue;
            orb.Timeline = state;
            // October 1 19:03:42: first trio 16, then 64, then 4 at 49.927;
            // fourth orb 16 at 48.934 and 4 at 55.985. State 16 is the early warning,
            //64 its later phase, and 4 the effect. Keep 0.5s after 4 for the observed
            // next-frame damage. Fifteen seconds bounds an interrupted warning.
            orb.End = state is 16 or 64 ? now.AddSeconds(15) : state == 4 ? now.AddSeconds(.5) : DateTime.MinValue;
        }
    }

    private IEnumerable<Vector3> ActiveDaryaTreasure()
    {
        var now = DateTime.UtcNow;
        var pending = _daryaTreasureOrbs.Values.Where(o => o.End > now).ToArray();
        // Actual native stages order the waves even when attaching mid-preview:
        // hold a just-resolved wave, otherwise 64 precedes 16. Never reserve all four
        // corners together; an unfamiliar full-floor state retains the cast fallback.
        var stage = pending.Any(o => o.Timeline == 4) ? 4 : pending.Any(o => o.Timeline == 64) ? 64 : 16;
        var next = pending.Where(o => o.Timeline == stage).ToArray();
        if (next.Length is> 0 and < 4)
            foreach (var orb in next)
                yield return orb.Location;
        foreach (var cast in _daryaTreasure.Values.Where(c => c.End > now))
        {
            // Merge helper warnings into the same owner instead of duplicating the
            // preview circle or reviving a wave whose native effect already resolved.
            if (_daryaTreasureOrbs.Values.Any(o => o.Location.Distance2D(cast.Location) < 1 && (o.Timeline == 4 || (o.End > now && next.Length is> 0 and < 4))))
                continue;
            yield return cast.Location;
        }
    }

    private bool DaryaNearTide() => DateTime.UtcNow < _daryaTideEnd && ((DateTime.UtcNow < _daryaTideSwitch) == _daryaTideStartsNear);
    private static bool InDarya() => WorldManager.ZoneId == 1315 && Core.Me != null && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Distance2D(DaryaCenter) < 60 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.IsAlive && b.BaseId == 19087 && b.Distance2D(DaryaCenter) < 30);
    private void RegisterDaryaAvoidance()
    {
        // Pulse observes the final countdown even while another coroutine awaits
        // an ordinary dodge. It never drives the mover or consumes a combat tick.
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveDaryaMarch;
        ff14bot.NeoProfile.BotEvents.OnPulse += ObserveDaryaMarch;
        // Record 5's twelve spheres 19091 spawn hidden at the side walls, become
        // visible in three groups, then cross the floor (23:47 capture). A sphere
        // vanished on contact at 23:47:16.35. Reserve 2.5y plus 0.5y around each live
        // visible center, including while it crosses the middle. Shared avoidance
        // intersects these circles with the current familiar wave and Twin Tides.
        AvoidanceManager.AddAvoidLocation<Vector3>(InDarya, _ => 3f, p => p, () => GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.BaseId == 19091 && b.IsVisible && b.Distance2D(DaryaCenter) < 35).Select(b => b.Location));
        AvoidanceManager.AddAvoidPolygon<Vector2[]>(InDarya, null, 80, _ => 0, _ => 1, _ => 15, polygon => polygon, _ => DaryaCenter, DaryaPreparationPolygons, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<DaryaFamiliarLine>(InDarya, null, 80, line => -line.Heading, _ => 1, _ => 15, _ => new[] { new Vector2(-4.5f, -.5f), new Vector2(4.5f, -.5f), new Vector2(4.5f, 40.5f), new Vector2(-4.5f, 40.5f) }, line => line.Location, () => _daryaFamiliarLines.Values.Where(line => line.Start <= DateTime.UtcNow && line.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<DaryaTreasureOrb>(InDarya, null, 80, _ => 0, _ => 1, _ => 15, _ => new[] { new Vector2(-4.5f, -4.5f), new Vector2(4.5f, -4.5f), new Vector2(4.5f, 4.5f), new Vector2(-4.5f, 4.5f) }, t => t.Location, () => _daryaTiles.Values.Where(t => t.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        // Delayed balls share the wave planner's 0.5y margin and remain active
        // through damage. Override their generic duplicate and the knockback omen.
        AvoidanceManager.AddAvoidLocation<Vector3>(InDarya, _ => 5.5f, p => p, () => _daryaDelayedBalls.Values.Where(b => b.End > DateTime.UtcNow).Select(b => b.Location));
        AvoidanceHelpers.AddAvoidSquareDonut(InDarya, 39, 39, 140, 140, () => new[] { DaryaCenter });
        // Surging Current 47052 has no omen and received no generic avoid. Its helper
        // shares the boss's forward direction. Reserve the conservative frontal half
        // with 0.5y padding; the opposite half remains safely reachable.
        AvoidanceManager.AddAvoidPolygon<CastShape>(InDarya, null, 100, c => -c.Heading, _ => 1, _ => 15, _ => new[] { new Vector2(-80, -.5f), new Vector2(80, -.5f), new Vector2(80, 80), new Vector2(-80, 80) }, c => c.Location, () => _daryaSurges.Values.Where(c => c.End > DateTime.UtcNow), priority: AvoidancePriority.High);
        // Swimming's event markers 2015003 appear well before the 1.7s Hydrofall bars.
        // The October 1 cast-only baseline died while still crossing overlapping circles.
        // Cache each observed marker until the shared effect, radius 12 plus 0.5y margin.
        AvoidanceManager.AddAvoidLocation<Vector3>(InDarya, _ => 12.5f, p => p, () => _daryaBubbleEnd > DateTime.UtcNow ? _daryaBubbles.Values : Enumerable.Empty<Vector3>());
        // Sphere Shatter is self-centered: its cast destination retained unrelated
        // old coordinates in two captures. Keep the actual helper circle through damage.
        // Native warnings supply the early wave; the helper remains a fallback on
        // unreadable previews. Both share radius 18 plus the 0.5y safety margin.
        AvoidanceManager.AddAvoidLocation<Vector3>(InDarya, _ => 18.5f, p => p, ActiveDaryaTreasure);
        // Twin Tides resolves outside then inside (or the reverse). The second hit
        // has no cast bar. October 1 showed it 3.32s after the first reported finish;
        // hold the first shape 0.75s through impact, then reserve the inverse until 4.1s.
        AvoidanceManager.AddAvoidLocation<Vector3>(() => InDarya() && DaryaNearTide(), _ => 10.5f, p => p, () => new[] { _daryaTideCenter });
        AvoidanceHelpers.AddAvoidDonut(() => InDarya() && DateTime.UtcNow < _daryaTideEnd && !DaryaNearTide(), () => _daryaTideCenter, 80, 9.5);
        // Current's helper advances 8y between impacts; observed cadence is roughly
        //1.8s, not a reliable timer. Reserve its current centered 8y strip and next
        // strip with 0.5y padding. Once it advances, the previous strip becomes usable.
        AvoidanceManager.AddAvoidPolygon<DaryaCurrent>(InDarya, null, 100, c => -c.Heading, _ => 1, _ => 15, c => new[] { new Vector2(-40.5f, -4.5f), new Vector2(40.5f, -4.5f), new Vector2(40.5f, c.Location.Distance2D(c.Origin) < 28 ? 12.5f : 4.5f), new Vector2(-40.5f, c.Location.Distance2D(c.Origin) < 28 ? 12.5f : 4.5f) }, c => c.Location, () => _daryaCurrents.Values.Where(c => c.End > DateTime.UtcNow), priority: AvoidancePriority.High);
    }

    private void UpdateDaryaForecasts()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _daryaDelayedBalls.Keys.Where(k => _daryaDelayedBalls[k].End <= now).ToArray())
            _daryaDelayedBalls.Remove(key);
        UpdateDaryaTreasure(now);
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(a => a.IsValid && a.Distance2D(DaryaCenter) < 65).ToArray();
        foreach (var key in _daryaSurges.Keys.Where(k => _daryaSurges[k].End <= now).ToArray())
            _daryaSurges.Remove(key);
        foreach (var key in _daryaTreasure.Keys.Where(k => _daryaTreasure[k].End <= now).ToArray())
            _daryaTreasure.Remove(key);
        foreach (var key in _daryaCurrents.Keys.ToArray())
        {
            var current = _daryaCurrents[key];
            var helper = actors.FirstOrDefault(a => a.ObjectId == key);
            if (current.End <= now || helper == null)
            {
                _daryaCurrents.Remove(key);
                continue;
            }

            // Drop reused helpers that teleport away from their captured straight line.
            var delta = helper.Location - current.Origin;
            var along = delta.X * (float)Math.Sin(current.Heading) + delta.Z * (float)Math.Cos(current.Heading);
            var across = delta.X * (float)Math.Cos(current.Heading) - delta.Z * (float)Math.Sin(current.Heading);
            if (along < -1 || along > 33 || Math.Abs(across) > 1)
            {
                _daryaCurrents.Remove(key);
                continue;
            }

            if (helper.Location.Distance2D(current.Location) > 1)
            {
                current.Location = helper.Location;
                if (along > 28)
                    current.End = now.AddSeconds(1.1);
            }
        }

        if (_daryaBubbleEnd <= now)
            _daryaBubbles.Clear();
        foreach (var actor in actors.Where(a => a.IsCasting))
        {
            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            if (actor.BaseId == 19087 && actor.CastingSpellId == 45776 && Math.Abs((finish - _daryaSwimFinish).TotalSeconds) > 1)
            {
                _daryaSwimFinish = finish;
                _daryaBubbleEnd = finish.AddSeconds(35);
                _daryaBubbleCastSeen = false;
                _daryaBubbles.Clear();
            }

            if (actor.BaseId != 9020)
                continue;
            // At 23:15:08 the first baited ball hit 0.91s after its reported finish,
            // after generic avoidance had released it. Freeze the first deadline
            // and preserve this 5y circle plus 0.5y padding through 1.2s of effect tail.
            if (actor.CastingSpellId == 45800 && !_daryaDelayedBalls.ContainsKey(actor.ObjectId))
                _daryaDelayedBalls[actor.ObjectId] = new CastShape
                {
                    Location = actor.SpellCastInfo.CastLocation,
                    End = finish.AddSeconds(1.2)
                };
            if (actor.CastingSpellId == 45786)
            {
                if (_daryaWaveSource != actor.ObjectId)
                {
                    ReleaseDaryaWave();
                    _daryaWaveSource = actor.ObjectId;
                }

                _daryaWaveFinish = finish;
                _daryaWaveDirection = new Vector3((float)Math.Sin(actor.Heading), 0, (float)Math.Cos(actor.Heading));
            }

            if (actor.CastingSpellId == 45787)
                _daryaDelayedBalls[actor.ObjectId] = new CastShape
                {
                    Location = actor.SpellCastInfo.CastLocation,
                    End = finish.AddSeconds(.95)
                };
            if (actor.CastingSpellId == 45779)
                _daryaTreasure[actor.ObjectId] = new CastShape
                {
                    Action = 45779,
                    Location = actor.Location,
                    End = finish.AddSeconds(.95)
                };
            if (actor.CastingSpellId is 45794 or 45797)
            {
                _daryaTideCenter = actor.Location;
                _daryaTideStartsNear = actor.CastingSpellId == 45794;
                _daryaTideSwitch = finish.AddSeconds(.75);
                _daryaTideEnd = finish.AddSeconds(4.1);
            }

            if (actor.CastingSpellId == 45789 && !_daryaCurrents.ContainsKey(actor.ObjectId))
                _daryaCurrents[actor.ObjectId] = new DaryaCurrent
                {
                    Origin = actor.Location,
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = finish.AddSeconds(12)
                };
            if (actor.CastingSpellId == 47052)
            {
                // Damage was seen 0.46s after its reported finish (status read a frame
                // later). Keep 0.95s through the effect and discard state on encounter exit.
                _daryaSurges[actor.ObjectId] = new CastShape
                {
                    Action = 47052,
                    Location = actor.Location,
                    Heading = actor.Heading,
                    End = finish.AddSeconds(.95)
                };
            }

            if (actor.CastingSpellId == 45777 && !_daryaBubbleCastSeen)
            {
                _daryaBubbleCastSeen = true;
                _daryaBubbleEnd = finish.AddSeconds(.95);
            }
        }

        if (_daryaBubbleEnd > now)
        {
            foreach (var marker in GameObjectManager.GameObjects.Where(o => o.IsValid && o.BaseId == 2015003 && o.IsVisible && o.Distance2D(DaryaCenter) < 30))
                _daryaBubbles[marker.ObjectId] = marker.Location;
        }
    }

    private void ClearDaryaForecasts()
    {
        _daryaFamiliarLines.Clear();
        _daryaFamiliarSeams.Clear();
        _daryaPreparationAreas.Clear();
        _daryaSerenadeFinish = _daryaPreparationEnd = default;
        ReleaseDaryaMarch();
        _daryaTiles.Clear();
        ReleaseDaryaWave();
        _daryaDelayedBalls.Clear();
        _daryaSurges.Clear();
        _daryaBubbles.Clear();
        _daryaTreasure.Clear();
        _daryaTreasureOrbs.Clear();
        _daryaCurrents.Clear();
        _daryaTideSwitch = _daryaTideEnd = default;
        _daryaSwimFinish = _daryaBubbleEnd = default;
        _daryaBubbleCastSeen = false;
    }

    // Normal Variant casts formerly delegated to generic telegraph decoding. Dimensions
    // are the normal encounter values plus 0.5y; ground attacks use cast destinations.
    // Visual casts, raidwides, self-targeted solo spreads and tankbusters are not avoids.
    private sealed record NativeBossCast(uint Action, float Radius, float Length, float HalfWidth, bool Ground = false, bool OtherTarget = false);
    private static readonly NativeBossCast[] NativeBossCasts =
    {
        new(44252, 8.5f, 0, 0), // Lamp Oil helper, not its visual parent.
        new(44344, 0, 36.5f, 2.5f), // Dousing Spirit's Aetherial Blizzard.
        new(45765, 11.5f, 0, 0, true), // Beaksbane ground circles.
        new(45758, 10.5f, 0, 0), // Big Burst proximity: 10y refuge, not the 30y damage falloff.
        new(45510, 12.5f, 0, 0), // Swoop is self-centered; captured CastLocation is zero.
        new(45512, 12.5f, 0, 0, true), // Transcendent Flight destination.
        new(45486, 8.5f, 0, 0), // Sparks damage; 45485 is only the short preview.
        new(46619, 8.5f, 0, 0, true), // Earth-rending Eight circle before crosses.
        new(46639, 5.5f, 0, 0, true),
        new(46640, 10.5f, 0, 0, true),
        new(46633, 3.5f, 0, 0, true), // Plummet boulders; preserve later shelter logic.
        new(45605, 20.5f, 0, 0), // Tidal Guillotine; existing cone planner retains sequencing.
        new(47398, 5.5f, 0, 0, false, true), // Dropsea on another player only.
        new(45802, 0, 70.5f, 3.5f, false, true), // Hydrocannon aimed at another player.
        // Unfamiliar ship topology still needs actual cast rectangles as a fallback.
        // Suppress these while the validated Voyage preview owns the complete path.
        new(47042, 0, 8.5f, 6.5f),
        new(43360, 0, 12.5f, 6.5f),
        new(43361, 0, 12.5f, 6.5f),
        new(43362, 0, 12.5f, 6.5f),
        new(43363, 0, 8.5f, 6.5f),
        new(43364, 0, 22.5f, 6.5f),
        new(43646, 0, 21.5f, 6.5f),
        new(43674, 0, 20.5f, 6.5f),
        new(43679, 0, 22.5f, 6.5f),
        new(43729, 0, 20.5f, 6.5f)
    };
    private sealed class NativeBossHazard
    {
        internal NativeBossCast Spec;
        internal Vector3 Position;
        internal float Heading;
        internal DateTime Finish, End;
        internal Vector2[] Points;
    }

    private readonly Dictionary<ulong, NativeBossHazard> _nativeBossHazards = new();
    private readonly CapabilityManagerHandle _sparksMovementHandle = CapabilityManager.CreateNewHandle();
    private bool _sparksMovementOwned;
    private PluginContainer _merchantSideStep, _merchantDutyPlugin;
    private bool _sideStepSuspended, _changingSideStep;
    private DateTime _ownershipErrorAfter;
    private bool MerchantBossActive() => InGenie() || InPari() || InGateRukhkh() || InSwordmaster() || InDarya() || InDandan();
    private void RegisterNativeBossAvoidance()
    {
        // Registration stays dungeon-local; the pulse sees casts even when a combat
        // coroutine is awaiting. No UI/HTTP thread pulses any native manager.
        _merchantSideStep = PluginHelpers.GetSideStepPlugin();
        _merchantDutyPlugin = PluginManager.Plugins.FirstOrDefault(p => p.Plugin.GetType().Assembly == GetType().Assembly);
        if (_merchantDutyPlugin != null)
            _merchantDutyPlugin.PropertyChanged += MerchantPluginChanged;
        if (_merchantSideStep != null)
            _merchantSideStep.PropertyChanged += MerchantSideStepChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse += ObserveNativeBossAvoidance;
        TreeRoot.OnStop += MerchantBossStopped;
        AvoidanceManager.AddAvoidPolygon<NativeBossHazard>(MerchantBossActive, null, 100, h => -h.Heading, _ => 1, _ => 15, h => h.Points, h => h.Position, ActiveNativeBossHazards, priority: AvoidancePriority.High);
        SpellsToTankBust.UnionWith(new uint[] { 46645, 45802 });
        SpellsToMitigate.UnionWith(new uint[] { 45748, 45764, 45803, 45772, 45773, 45804, 46632, 46638, 45615 });
    }

    private void MerchantBossStopped(ff14bot.AClasses.BotBase bot) => ReleaseNativeBossAvoidance();
    private void MerchantPluginChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == "Enabled" && _merchantDutyPlugin != null && !_merchantDutyPlugin.Enabled)
            ReleaseNativeBossAvoidance();
    }

    private void MerchantSideStepChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // A deliberate user toggle supersedes our saved enable state.
        if (!_changingSideStep && args.PropertyName == "Enabled")
            _sideStepSuspended = false;
    }

    private void RestoreMerchantSideStep()
    {
        if (!_sideStepSuspended)
            return;
        _sideStepSuspended = false;
        _changingSideStep = true;
        try
        {
            if (_merchantSideStep != null)
                _merchantSideStep.Enabled = true;
        }
        finally
        {
            _changingSideStep = false;
        }

        ff14bot.Helpers.Logging.Write("[Merchant] Boss ended; SideStep restored for traversal and trash.");
    }

    private void ReleaseNativeBossAvoidance()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveNativeBossAvoidance;
        TreeRoot.OnStop -= MerchantBossStopped;
        if (_merchantDutyPlugin != null)
            _merchantDutyPlugin.PropertyChanged -= MerchantPluginChanged;
        if (_merchantSideStep != null)
            _merchantSideStep.PropertyChanged -= MerchantSideStepChanged;
        _nativeBossHazards.Clear();
        ReleaseSparksMovement();
        RestoreMerchantSideStep();
    }

    private void SuspendMerchantSideStep()
    {
        if (_merchantSideStep == null || !_merchantSideStep.Enabled)
            return;
        // SideStep 7.4 OnDisabled clears its tracking list WITHOUT removing its
        // AvoidInfos. Snapshot that exact ownership before disabling; never Clear()
        // or RemoveAllAvoids, which would erase this dungeon's registrations too.
        // This version-specific adapter fails closed on an unfamiliar plugin layout.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var type = _merchantSideStep.Plugin.GetType();
        var tracked = type.GetField("_tracked", flags)?.GetValue(_merchantSideStep.Plugin) as IEnumerable<AvoidInfo>;
        var zone = type.GetField("_zoneTracked", flags)?.GetValue(_merchantSideStep.Plugin) as IEnumerable<AvoidInfo>;
        if (tracked == null || zone == null)
            throw new InvalidOperationException("SideStep ownership lists unavailable; refusing an unsafe handoff.");
        var owned = tracked.Concat(zone).Distinct().ToArray();
        _changingSideStep = true;
        try
        {
            _merchantSideStep.Enabled = false;
            _sideStepSuspended = true;
        }
        finally
        {
            _changingSideStep = false;
        }

        foreach (var avoid in owned)
            AvoidanceManager.RemoveAvoid(avoid);
        ff14bot.Helpers.Logging.Write($"[Merchant] DutyMechanic owns boss avoidance; SideStep suspended, removed {owned.Length} SideStep hazards.");
    }

    private void ObserveNativeBossAvoidance(object sender, EventArgs args)
    {
        if (!TreeRoot.IsRunning || ff14bot.Behavior.CommonBehaviors.IsLoading || Core.Me == null || !Core.Me.IsValid || !MerchantBossActive())
        {
            _nativeBossHazards.Clear();
            ReleaseSparksMovement();
            RestoreMerchantSideStep();
            return;
        }

        var now = DateTime.UtcNow;
        try
        {
            SuspendMerchantSideStep();
        }
        catch (Exception ex)
        {
            if (now >= _ownershipErrorAfter)
            {
                _ownershipErrorAfter = now.AddSeconds(30);
                ff14bot.Helpers.Logging.Write($"[Merchant] SideStep handoff unavailable: {ex.Message}");
            }

            return;
        }

        foreach (var key in _nativeBossHazards.Where(p => p.Value.End < now).Select(p => p.Key).ToArray())
            _nativeBossHazards.Remove(key);
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(a => a.IsValid && a.Distance2D(Core.Me.Location) < 110).ToArray();
        foreach (var actor in actors.Where(a => a.IsCasting))
        {
            var spec = NativeBossCasts.FirstOrDefault(s => s.Action == actor.CastingSpellId);
            if (spec == null)
                continue;
            var cast = actor.SpellCastInfo;
            var key = ((ulong)actor.ObjectId << 32) | spec.Action;
            var finish = now + cast.RemainingCastTime;
            // Same helper may repeat the same action; compare estimated finish, not
            // merely action ID. Retain scalar geometry through a 1s effect handoff.
            if (_nativeBossHazards.TryGetValue(key, out var existing) && Math.Abs((finish - existing.Finish).TotalSeconds) < .75)
                continue;
            var position = spec.Ground ? cast.CastLocation : actor.Location;
            if (spec.Ground && position == Vector3.Zero)
                continue; // Do not invent a ground destination.
            if (spec.OtherTarget)
            {
                if (cast.TargetId == Core.Me.ObjectId)
                    continue; // Unavoidable solo hit; keep mitigation schedulable.
                var target = actors.FirstOrDefault(a => a.ObjectId == cast.TargetId);
                if (target == null)
                    continue;
                if (spec.Radius > 0)
                    position = target.Location;
            }

            _nativeBossHazards[key] = new NativeBossHazard
            {
                Spec = spec,
                Position = position,
                Heading = spec.Radius > 0 ? 0 : actor.Heading,
                Finish = finish,
                End = finish.AddSeconds(1),
                Points = spec.Radius > 0 ? DandanDisc(spec.Radius) : new[]
                {
                    new Vector2(-spec.HalfWidth, -.5f),
                    new Vector2(spec.HalfWidth, -.5f),
                    new Vector2(spec.HalfWidth, spec.Length),
                    new Vector2(-spec.HalfWidth, spec.Length)
                }
            };
            ff14bot.Helpers.Logging.Write($"[Merchant] Native boss hazard action={spec.Action} caster=0x{actor.ObjectId:X8} position={position} finish={finish:O}");
        }

        // The October 2 Pari death showed two returns toward the boss between Sparks
        // escapes. Lease ONLY routine movement across the wave; RB still owns dodging
        // and the routine continues rotation/healing. No manual safe-point controller.
        if (InPari() && _nativeBossHazards.Values.Any(h => h.Spec.Action == 45486 && h.End > now))
        {
            CapabilityManager.Update(_sparksMovementHandle, CapabilityFlags.Movement, 1000, "Pari Sparks: retain refuge between helper waves");
            _sparksMovementOwned = true;
        }
        else
            ReleaseSparksMovement();
    }

    private void ReleaseSparksMovement()
    {
        if (_sparksMovementOwned)
            CapabilityManager.Clear(_sparksMovementHandle, CapabilityFlags.Movement, "Pari Sparks ended");
        _sparksMovementOwned = false;
    }

    private IEnumerable<NativeBossHazard> ActiveNativeBossHazards()
    {
        var active = _nativeBossHazards.Values.Where(h => h.End > DateTime.UtcNow && !(VoyageActions.Contains(h.Spec.Action) && VoyageActive())).ToArray();
        var sparks = active.Where(h => h.Spec.Action == 45486).ToArray();
        var first = sparks.Length == 0 ? DateTime.MaxValue : sparks.Min(h => h.Finish);
        // Four staggered Sparks groups must resolve in order. Reserving every future
        // circle simultaneously would remove the gaps required to dodge the first wave.
        return active.Where(h => h.Spec.Action != 45486 || h.Finish <= first.AddSeconds(.5));
    }
}
