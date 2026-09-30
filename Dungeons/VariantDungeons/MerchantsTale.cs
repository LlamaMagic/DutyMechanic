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
/// Handles Genie and Pari mechanics on Merchant's Tale's solo residential route.
/// OrderBot and the combat routine retain combat; SideStep retains unrelated telegraphs.
/// Advanced and Criterion territories are deliberately outside this handler's registration.
/// </summary>
public sealed class MerchantsTale : AbstractDungeon
{
    // The 2026-09-27 capture distinguishes boss 18535 from same-name helper 9020.
    // Gate positions and cast origins establish a fixed square centered here;
    // the boss's initial position is offset from that center.
    private const uint GenieBase = 18535;
    private static readonly Vector3 GenieCenter = new(-750, -34, -415);
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
        45586
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
    }; // Rub Burn, observed in the final15:57 Genie cycle.
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
        SetMechanicGapCloserBlock(false);
        ClearForecasts();
        RegisterPariAvoidance();
        foreach (var action in OwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        // At15:27:08, generic Firecrackers avoidance choseZ-384.475 and ran through the
        // still-open entrance, resetting the boss. Constrain RB's shared path selection to
        // the 35x35 floor with 0.5-yalm inset on every edge; preserve both safe corner wedges.
        // This is a navigation constraint, not a competing manual movement owner.
        AvoidanceHelpers.AddAvoidSquareDonut(InGenie, 34, 34, 140, 140, () => new[] { GenieCenter });
        // All cast geometry goes through one earliest-impact set. Two cannon waves and the
        // following Firecrackers must not reserve their mutually exclusive future safe regions.
        // Voyage resolves before Fanning Flame; defer those later fans until the ship hold ends.
        AvoidanceManager.AddAvoidPolygon<CastShape>(
            InGenie,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            ShapePoints,
            c => c.Location,
            ActiveCasts,
            priority: AvoidancePriority.High);
        // Pyromagicks repeats have no cast bars. The live helpers stepped8yalms roughly 2.4s
        // apart, unlike a shorter reference cadence. Current plus the next observed-direction
        // position gives travel time before each hop; use radius 6 with 0.5 edge padding.
        AvoidanceManager.AddAvoidLocation<ExplosionLine>(InGenie, _ => 6.5f, e => e.Current, () => _explosions.Values.Where(e => e.End > DateTime.UtcNow));
        AvoidanceManager.AddAvoidLocation<ExplosionLine>(
            InGenie,
            _ => 6.5f,
            e => e.Next,
            () => _explosions.Values.Where(
                e => e.End > DateTime.UtcNow
            && e.PreviewNext));
        // The ship preview removes its starting half, the central crossing diamond, and the
        // tethered lever's side. The remaining far corner satisfies all Voyage segments.
        // Each edge is padded0.5; all shapes share one snapshot and one RB movement owner.
        AvoidanceManager.AddAvoidPolygon<Voyage>(
            VoyageActive,
            null,
            80,
            _ => 0,
            _ => 1,
            _ => 15,
            v => v.ShipsWest ? new[] { new Vector2(
                -70,
                -70), new Vector2(
                    0,
                    -70), new Vector2(
                        0,
                        70), new Vector2(
                            -70,
                            70) } : new[] { new Vector2(
                                0,
                                -70), new Vector2(
                                    70,
                                    -70), new Vector2(
                                        70,
                                        70), new Vector2(
                                            0,
                                            70) },
            _ => GenieCenter,
            ActiveVoyage,
            priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<Voyage>(
            VoyageActive,
            null,
            80,
            _ => (float)Math.PI / 4,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -14.5f,
                -14.5f), new Vector2(
                    14.5f,
                    -14.5f), new Vector2(
                        14.5f,
                        14.5f), new Vector2(
                            -14.5f,
                            14.5f) },
            _ => GenieCenter,
            ActiveVoyage,
            priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<Voyage>(
            VoyageActive,
            null,
            80,
            _ => 0,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -70.5f,
                -23.5f), new Vector2(
                    70.5f,
                    -23.5f), new Vector2(
                        70.5f,
                        23.5f), new Vector2(
                            -70.5f,
                            23.5f) },
            v => v.Lever,
            ActiveVoyage,
            priority: AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetMechanicGapCloserBlock(false);
        ClearForecasts();
        ClearPariForecasts();
        foreach (var action in PariOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        foreach (var action in OwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (InPari())
        {
            ClearForecasts();
            UpdatePariForecasts();
            var now = DateTime.UtcNow;
            SetMechanicGapCloserBlock(_flightExpires > now || _sunExpires > now || _pariCasts.Values.Any(s => s.End > now) || ActiveNightShape().Any());
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
        // Intervene16461 at 21:45:43.123 and 21:48:35.829 dashed from a safe point into
        // a charge/cross while shared avoidance was between escape movements. Magitek's
        // maintained CanUseGapCloser checks this capability independently of normal movement.
        // Keep attacks, walking and shared avoidance available; only dashes are suppressed
        // while captured hazards/previews remain. Refresh a one-second lease so a stopped
        // handler cannot leave the routine disabled, and clear immediately on encounter exit.
        if (active)
        {
            CapabilityManager.Update(
                _mechanicGapCloserHandle,
                CapabilityFlags.GapCloser,
                1000,
                "Merchant mechanic forecast: preserve the safe point between dodges");
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
            if (action is 43353 or 43354 or 43349 or 44254 or 44255 or 45586)
            {
                // The15:33 Firecrackers impact was about 0.56s beyond RB's reported finish.
                // Retain 0.7s through the effect, including each sequential cannon/fan stage.
                // Lamp Lighting45586 also hit at 22:06:08.74 when generic cast geometry
                // disappeared during Pyromagicks; freeze its last cast heading through impact.
                if (!_casts.TryGetValue(actor.ObjectId, out var shape))
                {
                    _casts[actor.ObjectId] = shape = new CastShape();
                }

                shape.Action = action;
                shape.Location = actor.Location;
                shape.Heading = actor.Heading;
                shape.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.7);
            }

            if (action == 44261)
            {
                explosionCasts.Add(actor.ObjectId);
                if (!_previousExplosionCasts.Contains(actor.ObjectId))
                {
                    // Five impacts traversed32yalms from the first origin. Twelve seconds
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
                // Never forecast a sixth explosion after the captured final32-yalm position.
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
            // ships18yalms west and a tethered lever16.75 yalms north/south. Require that
            // topology (including its mirrored east form) before applying the corner model.
            if (ships.Length == 3
                && lever != null
                && ships.All(
                    b => Math.Abs(
                        Math.Abs(
                            b.X - GenieCenter.X) - 18) < 1)
                && ships.All(
                    b => Math.Sign(
                        b.X - GenieCenter.X) == Math.Sign(
                            ships[0].X - GenieCenter.X))
                && Math.Abs(
                    Math.Abs(
                        lever.Z - GenieCenter.Z) - 16.75f) < 1)
            {
                _voyage = new Voyage
                {
                    ShipsWest = ships[0].X < GenieCenter.X,
                    Lever = lever.Location,
                    // The last fast follow-up at 15:35:36.859 followed the first ship finish
                    // by about 0.66s. Hold1.4s for its effect, then release before Fanning hits.
                    End = now + ships.Max(b => b.SpellCastInfo.RemainingCastTime) + TimeSpan.FromSeconds(1.4)
                };
                // Suppress late duplicate ship/helper rectangles only after a validated
                // preview exists. An unfamiliar topology retains generic avoidance as fallback.
                foreach (var action in VoyageActions)
                {
                    LlamaLibrary.Helpers.SideStep.Override(action);
                }

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
        // A0.5s group tolerance merges simultaneously sampled helpers, while the captured
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

        foreach (var action in VoyageActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        _ownsVoyage = false;
    }

    private static Vector2[] ShapePoints(CastShape shape)
    {
        // Lamp Lighting is a forward60x8 line from the real boss, overlapping moving fire.
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

        // All fan origins are at arena center. Their30-yalm reach exceeds every corner of
        // the 35-yalm square, so finite40-yalm wedges/half-planes are equivalent inside it.
        // Offset each wedge tip by 0.5/sin(half-angle), padding both straight edges0.5 yalm.
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

    private static bool InGenie() => WorldManager.ZoneId == 1315
        && Core.Me.InCombat
        && !Core.Me.IsDead
        && Core.Me.Distance2D(
            GenieCenter) < 60
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.IsAlive
        && b.BaseId == GenieBase
        && b.Distance2D(
            GenieCenter) < 30);
    // 2026-09-27 live boss/helper origins confirm the reference 40x40 square at floorY-54.
    // The entrance atZ-780 lies outside it; never let an escape leave the actual combat floor.
    private static readonly Vector3 PariCenter = new(-760, -54, -805);
    // A14-yalm preview leaves a usable cap beyond the padded cross and final charge.
    // The old12-yalm preview leaves only a thin intersection near the arena edge.
    private const float SunStagingRadius = 14;
    // Sun's generic omen advertises a24-yalm hole, but the captured safe radius is8.
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
        45460
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
        ClearPariForecasts();
        foreach (var action in PariOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        AvoidanceHelpers.AddAvoidSquareDonut(InPari, 39, 39, 140, 140, () => new[] { PariCenter });
        // Publish only the earliest impact group. Crosses and charge segments overlap in
        // time; reserving all future mutually exclusive regions can erase every safe point.
        AvoidanceManager.AddAvoidPolygon<PariShape>(
            InPari,
            null,
            100,
            s => -s.Heading,
            _ => 1,
            _ => 15,
            PariShapePoints,
            s => s.Position,
            ActivePariShapes,
            priority: AvoidancePriority.High);
        // A fourteen-yalm staging circle permits maneuvering during earlier charges/crosses.
        // Tighten to 7.5 only after earlier crosses end; disabling staging entirely let
        // routine pursuit run25yalms from the endpoint before the short Sun cast at 22:40.
        // This remains a constraint in the same avoidance owner, never a manual MoveTo loop.
        AvoidanceHelpers.AddAvoidDonut(() => PariSunActive() && PariSunRadius() == SunStagingRadius, () => _sunCenter, 60, SunStagingRadius);
        AvoidanceHelpers.AddAvoidDonut(() => PariSunActive() && PariSunRadius() < SunStagingRadius, () => _sunCenter, 60, 7.5);
        AvoidanceManager.AddAvoidPolygon<PariShape>(
            InPari,
            null,
            100,
            s => -s.Heading,
            _ => 1,
            _ => 15,
            PariShapePoints,
            s => s.Position,
            ActiveNightShape,
            priority: AvoidancePriority.High);
    }

    private static bool InPari() => WorldManager.ZoneId == 1315
        && Core.Me != null
        && Core.Me.InCombat
        && !Core.Me.IsDead
        && Core.Me.Distance2D(
            PariCenter) < 60
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.IsAlive
        && b.BaseId == 19048
        && b.Distance2D(
            PariCenter) < 40);
    private void ClearPariForecasts()
    {
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
                // The two625 markers appeared at remaining 6.56s/2.52s, with only a0.267s
                // empty-slot gap. Bucket by the four-second preview cadence so equal IDs
                // cannot collapse into one event when a combat coroutine spans that gap.
                // Later observations overwrite the bucket before its cleave is due.
                var index = nights - 1 - (int)Math.Floor((boss.SpellCastInfo.RemainingCastTime.TotalSeconds + 1) / 4);
                if (index >= 0 && index < nights)
                {
                    var reverseOnEven = marker.Id is 625 or 644;
                    var heading = boss.Heading - ((index % 2 == 0) == reverseOnEven ? (float)Math.PI : 0);
                    if (LoggingHelpers.MechanicDiagnosticsEnabled
                        && (!_nightHeadings.TryGetValue(
                            index,
                            out var previous)
                        || Math.Abs(
                            previous - heading) > .05))
                    {
                        ff14bot.Helpers.Logging.Write(
                            $"[Merchant.Pari] Night preview index={index} marker={marker.Id} heading={heading:F3} finish={_nightFinish:O}");
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
                // At20:04:16.6 the second cross damaged after its reported15.826 finish.
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
            // At20:36:14 all four concealing carpets occupied X-745/-755/-765/-775,Z-800.
            // Three visible baubles carried2056:1096 until conceal. Hidden/reused actors
            // elsewhere in the arena are not part of this arrangement.
            var baubles = actors.Where(
                a => a.BaseId == 19049
                && a.IsVisible
                && Math.Abs(
                    a.Z + 800) < 1
                && a.CharacterAuras.Any(
                    s => s.Id == 2056
                && s.Value == 1096)).ToArray(
                );
            if (baubles.Length == 3 && carpets.All(c => Math.Abs(c.Z + 800) < 1))
            {
                var matches = baubles.Select(
                    b => new
                    {
                        Bauble = b.ObjectId,
                        Carpet = carpets.SingleOrDefault(
                            c => c.Distance2D(
                                b.Location) < 1)?.ObjectId ?? 0
                    }).ToArray(
                    );
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

    private DateTime FlightImpact(FlightEdge edge, int index) => edge.ObservedEnd != DateTime.MinValue ? edge.ObservedEnd : _flightFinish.AddSeconds(
        3.3 + index * 2.1);
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
    private bool EarlierCrossActive() => // At20:24:00 the south bauble cross covered every point in the early donut
    // shelter. Its effect precedes the last charge and Sun Circlet. Release that
    // tight shelter until EARLIER crosses finish. At20:35:51 a fourth
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
        // At23:41:09 Right Three Nights faced north (actor heading pi), but its actual
        // omen forward axis was (40,0): east. Actor facing, including its initial turn,
        // cannot locate this attack. Matrix row2 is the transformed local forward axis;
        // row0 is the half-width. Read only the exposed RB matrix, never a guessed offset.
        var omen = boss.OmenMatrix;
        var length = Math.Sqrt(omen.M20 * omen.M20 + omen.M22 * omen.M22);
        var halfWidth = Math.Sqrt(omen.M00 * omen.M00 + omen.M02 * omen.M02);
        var alignment = omen.M20 * omen.M00 + omen.M22 * omen.M02;
        // Only accept the captured40x4 rectangular omen near this caster. Missing/zero
        // or unrelated matrices retain the last valid scalar sample for this cast;
        // a new cast has no line forecast until its own valid omen arrives.
        if (!double.IsFinite(
            length)
            || !double.IsFinite(
                halfWidth)
            || length < 39
            || length > 41
            || halfWidth < 1.5
            || halfWidth > 2.5
            || !float.IsFinite(
                alignment)
            || Math.Abs(
                alignment) > length * halfWidth * .05
            || !float.IsFinite(
                omen.M30)
            || !float.IsFinite(
                omen.M32)
            || omen.Center.Distance2D(
                boss.Location) > 1)
        {
            return;
        }

        _nightOrigin = new Vector3(omen.M30, boss.Location.Y, omen.M32);
        _nightInitialHeading = (float)Math.Atan2(omen.M20, omen.M22);
        if (!_nightLineReady && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write(
                $"[Merchant.Pari] Night line action={boss.CastingSpellId} origin={_nightOrigin} heading={_nightInitialHeading:F3} finish={_nightFinish:O}");
        }

        _nightLineReady = true;
    }

    private IEnumerable<PariShape> ActiveNightShape()
    {
        // First damage arrived2.37s after the reported finish. A2.7s first hold covers
        // that handoff; subsequent two-second-plus cleaves are staged individually.
        var now = DateTime.UtcNow;
        // Every Nights cast first fires a40x4 forward line. At23:10:44-54, publishing
        // the future half-room together with generic line geometry oscillated at arena
        // center and the initial line hit at 55.42 (reported finish54.59). Own that line
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
            var end = _nightFinish.AddSeconds(2.7 + i * 2.4);
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
        // Nights' narrow opening line precedes the marker cleaves; pad its40x4 edges0.5.
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

        // Expand every straight edge0.5 yalm. Cross reach40 and charge width 10 come from
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
}
