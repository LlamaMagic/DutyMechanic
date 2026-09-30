using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Directors;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;
using LlamaLibrary.Memory;
using LlamaLibrary.Memory.Attributes;
using LlamaLibrary.Memory.PatternFinders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using V2 = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns captured Variant mechanics whose semantics cannot be inferred from generic
/// telegraphs. This territory is separate from both Criterion difficulties. The accompanying
/// profile owns route choices, enemy targeting, loot, and wipe recovery.
/// </summary>
public sealed class SildihnSubterrane : AbstractDungeon
{
    // Geryon reuses actor 14731 across three arenas. The 2026-09-28 captures place
    // the left and right floors at (-213,-32,101) and (183,-21,177). Select a fixed
    // floor center so shared mechanics work without following the boss's movement.
    private static Vector3 GeryonCenter => Core.Me.Location.X < -150 ? new Vector3(
        -213,
        -32,
        101) : Core.Me.Location.X > 120 ? new Vector3(
            183,
            -21,
            177) : Vector3.Zero;

    private static readonly Vector3 GladiatorCenter = new(-35, 0, -271);
    // Landing is proximity damage. Use conservative 20-yalm separation plus a
    // 0.5-yalm margin; the exact damage falloff is not
    // measured. 2026-09-30's50.5-yalm generic circles excluded the whole floor.
    private const float GladiatorLandingClearance = 20.5f;
    private static readonly uint[] OwnedActions =
    {
        29908,
        29909,
        29897,
        29898,
        29899,
        29900,
        29901,
        29904,
        29905,
        // 2026-09-28 left capture: Runoff 29911 is knockback, not SideStep's60-yalm
        // damaging circle. That circle made escape impossible at 15:49. Suppressing it
        // at 15:50:57 preserved the safe-keg target; native avoidance returned from the
        // north-wall landing before the following explosion without vulnerability.
        // Keep the barrel/arena owners active; do not add a duplicate whole-floor avoid.
        29911,
        30266,
        30267,
        30268,
        30269,
        30270,
        30271,
        30272,
        30273,
        30274,
        30275,
        30276,
        30655,
        30281,
        30282,
        30278,
        // Shattering Steel is a shelter check. 2026-09-28 01:32:53 SideStep's60-yalm
        // circle covered the entire floor and prevented entry to the observed safe updraft.
        // Suppress that semantic inversion in both wind and boulder variants.
        30283,
        30285,
        31222,
        30290,
        30291,
        30292,
        // Own only this boulder's proximity cast; shelter30283 remains a separate mechanic.
        30288
    };
    private readonly Dictionary<uint, Forecast> _gladiatorCasts = new();
    private readonly Dictionary<uint, Forecast[]> _silverFlames = new();
    private readonly HashSet<uint> _silverFallbacks = new();
    private readonly Dictionary<uint, Keg> _kegs = new();
    private readonly Dictionary<uint, Vector3> _chargedKegPositions = new();
    private uint _chargeCast;
    private DateTime _chargePositionsUntil;
    private bool _flipCast;
    private uint _barrelTarget;
    private uint _millCast;
    private DateTime _millFirst;
    private float _millInitialHeading;
    private float _millStep;
    private int _lastMillStep = -1;
    private Vector3[] _largeWinds = Array.Empty<Vector3>();
    private Vector3[] _smallWinds = Array.Empty<Vector3>();
    private Vector3 _windCenter;
    private DateTime _windImpact;
    private Forecast _slam;
    private uint _slamCancelledCast;
    private uint _ringCancelledCast;
    private uint _rushCancelledCast;
    private readonly CapabilityManagerHandle _mechanicGapCloserHandle = CapabilityManager.CreateNewHandle();
    private bool _ownsGapCloserBlock;
    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)1069;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
    /// <inheritdoc/>
     // Thorne's Cog Cleaver28906 was captured 2026-09-29; its unavoidable targeted
    // strike belongs to mitigation, never a movement avoidance shape.
    protected override HashSet<uint> SpellsToTankBust { get; } = new()
    {
        29903,
        30295,
        30507,
        29868,
        28906
    };
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = new()
    {
        29895,
        29906,
        29896,
        30284,
        30294,
        30287,
        30508,
        29870,
        28907
    }; // September29 Thorne's Cogwheel is unavoidable room damage, not an escape circle.

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        _silverFallbacks.Clear();
        foreach (var action in OwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        foreach (var action in SilkieOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        foreach (var action in ZelessOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        foreach (var action in ThorneOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        ResetSequence();
        // Geryon's floor is square: preserve its corners. The reference half-width is19.5;
        // keep a0.5-yalm inward allowance instead of imposing a circular arena.
        AvoidanceHelpers.AddAvoidSquareDonut(InGeryon, 38, 38, 120, 120, () => new[] { GeryonCenter });
        // The September 26 capture exposed raw type 8 without a SideStep charge avoid and
        // a resulting vulnerability. The lane is 14 wide from caster to opposite wall;
        // extend each edge by 0.5 and let native avoidance choose either side of the lane.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            InGeryon,
            b => b.NpcId == 11442
            && b.IsCasting
            && b.CastingSpellId is 29900 or 29901,
            15,
            41,
            priority: AvoidancePriority.High);
        // Live Colossal Slam (raw type 13) produced no SideStep shape and hit at 23:43:52.
        // Actor-relative cones need their actual origin even when CastLocation is stale.
        // Shift the vertex back by 0.5/sin(half-angle) to pad both sides by 0.5;
        // extend reach by the same offset plus 0.5 for forward edge clearance.
        // 2026-09-28 01:46:41 Slam resolved before the barrels'01:46:45 explosion.
        // Other overlaps resolve in the opposite order. Share a safe keg outside the cone
        // when possible; otherwise gate by observed cast deadlines, not barrel existence.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InGeryon,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            c => ConePoints(
                SlamDegrees(
                    c)),
            c => c.Location - new Vector3(
                (float)Math.Sin(
                    c.Heading) * ConePadding(
                        SlamDegrees(
                            c)),
                0,
                (float)Math.Cos(
                    c.Heading) * ConePadding(
                        SlamDegrees(
                            c))),
            () => AvoidSlam(
            ) ? new[] { _slam } : Array.Empty<Forecast>(
            ),
            priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Keg>(
            InGeryon,
            _ => 15.5f,
            k => k.Location,
            () => _kegs.Values.Where(
                k => k.Casting
            && !k.Blue
            && k.End > DateTime.UtcNow));
        // Live29909 punished positions inside SideStep's inferred6.8-yalm hole. Its authored
        // hole is3: shrink to 2.5 and expand the outer 17 to 17.5, using one geometry owner.
        AvoidanceHelpers.AddAvoidDonut(
            InGeryon,
            () => _kegs.Values.Where(
                k => k.Casting
            && k.Blue
            && k.End > DateTime.UtcNow).Select(
                k => k.Location).ToArray(
            ),
            17.5,
            2.5,
            AvoidancePriority.High);
        // A safe keg is a positive destination constraint in the same native planner,
        // not a competing MoveTo loop. Charge forecasts reserve the safe side while its
        // eight-second warning is still active, before the displaced keg begins exploding.
        AvoidanceHelpers.AddAvoidDonut(InGeryon, SafeKegCenters, 65, 2, AvoidancePriority.High);
        // The left clogged-drain branch leaves radius 9 sludge after 29910's cast. The
        // transient cast telegraph remains SideStep-owned; visible event object2013168
        // owns the persistent pool. Pad0.5 and gate to this boss so old route scenery
        // cannot divert travel. Actor lifecycle/visibility still needs left-run verification.
        AvoidanceManager.AddAvoid(
            new AvoidObjectInfo<EventObject>(
                condition: InGeryon,
                objectSelector: o => o.IsValid
            && o.IsVisible
            && o.NpcId == 2013168,
                radiusProducer: _ => 9.5f,
                priority: AvoidancePriority.High));
        // Barrels explode before the overlapping first Gigantomill impact. Delay the cross
        // until that earlier snapshot resolves; simultaneous independent avoids can erase every
        // route to the blue barrel. Later impacts rotate the same cross, never accumulate copies.
        AvoidanceHelpers.AddAvoidCross<BattleCharacter>(
            () => InGeryon(
            )
            && MillActive(
            )
            && !HasPendingKegs(
            ),
            b => b.NpcId == 11442
            && b.IsTargetable
            && b.IsAlive,
            11,
            72.5f,
            _ => GeryonCenter,
            _ => -MillHeading(
            ),
            AvoidancePriority.High);
        RegisterGladiatorGeometry();
        RegisterSilkieGeometry();
        // Right-only floor warnings retain their captured order; registering every
        // future quadrant simultaneously would erase all safe space in this arena.
        RegisterRightGeryonGeometry();
        // The right final boss needs spawn-time bomb/font warnings: its captured
        // short casts begin too late for an escape from the overlapping shapes.
        RegisterZelessGeometry();
        RegisterThorneGeometry();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetSequence();
        foreach (var action in OwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        foreach (var action in SilkieOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        foreach (var action in ZelessOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        foreach (var action in ThorneOwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        // Release this independent positive-position lease on death or arena handoff.
        if (!InGladiator() || Core.Me.IsDead)
        {
            ReleaseBoulderShelter();
        }

        if (InThorne())
        {
            UpdateThorneCasts();
            SetMechanicGapCloserBlock(_thorneCasts.Count != 0);
            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        ResetThorne();
        if (InZeless() && !Core.Me.IsDead)
        {
            UpdateZelessHazards();
            SetMechanicGapCloserBlock(ZelessHazardsPending());
            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        ResetZeless();
        // Alternate final bosses share this territory but have independent cast ledgers.
        // Keep Silkie out of the middle-route reset path while its sequence is active.
        if (InSilkie() && !Core.Me.IsDead)
        {
            UpdateSilkieCasts();
            SetMechanicGapCloserBlock(PendingSilkieCasts().Any());
            if (await HandleSilkieKnockback())
            {
                return true;
            }

            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        _silkieCasts.Clear();
        ReleaseSilkieKnockback();
        if (InGladiator() && !Core.Me.IsDead)
        {
            UpdateGladiatorCasts();
            CancelRingCastForEscape();
            CancelRushCastForEscape();
            UpdateGladiatorWind();
            UpdateBoulderShelter();
            SetMechanicGapCloserBlock(WindShelterActive() || _boulderShelterPoint.HasValue || PendingCasts().Any());
            if (HandleBoulderShelter())
            {
                return true;
            }

            if (await TankBusterSpells())
            {
                return true;
            }

            return await DamageMitigationSpells();
        }

        _gladiatorCasts.Clear();
        _ringCancelledCast = 0;
        _rushCancelledCast = 0;
        if (!InGeryon() || Core.Me.IsDead)
        {
            ResetSequence();
            return false;
        }

        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.NpcId == 11442 && b.IsTargetable && b.IsAlive);
        if (boss == null)
        {
            return false;
        }

        UpdateRightGeryonSewage();
        UpdateKegs(boss);
        UpdateCharge(boss);
        UpdateSlam(boss);
        CancelSlamCastForEscape();
        UpdateMill(boss);
        UpdateRunoff();
        SetMechanicGapCloserBlock(HasPendingKegs() || SlamActive() || MillActive() || DateTime.UtcNow < _chargePositionsUntil);
        if (await HandleRunoff())
        {
            return true;
        }

        if (await TankBusterSpells())
        {
            return true;
        }

        return await DamageMitigationSpells();
    }

    private static bool InGeryon() => WorldManager.ZoneId == 1069 && Core.Me.InCombat && Core.Me.Distance2D(GeryonCenter) < 40;
    private static bool InGladiator() => WorldManager.ZoneId == 1069 && Core.Me.InCombat && Core.Me.Distance2D(GladiatorCenter) < 40;
    private void RegisterGladiatorGeometry()
    {
        // Middle-route arena is a 39-yalm square. Use the player's elevation because the
        // reference supplies XZ only; the gate limits this boundary to the final arena.
        AvoidanceHelpers.AddAvoidSquareDonut(InGladiator, 38, 38, 120, 120, () => new[] { new Vector3(-35, Core.Me.Location.Y, -271) });
        // Four landings begin four seconds apart with 6.7s remaining in RB. Keep
        // concurrent casts together so native avoidance finds their shared far side;
        // release each forecast at its existing cast-impact fence. Do not retain
        // landed boulders as proximity hazards during the subsequent shelter check.
        AvoidanceManager.AddAvoidLocation<Forecast>(
            InGladiator,
            _ => GladiatorLandingClearance,
            c => c.Location,
            () => PendingCasts(
            ).Where(
                c => c.Action == 30288));
        // 2026-09-30 02:49:32.456 released Sculptor's line at cast completion;
        // the boss-sourced vulnerability followed at 33.397 during Regret's line waves.
        // Retain the actor-origin60x8 beam through its delayed impact, with 0.5-yalm
        // padding on every edge. Native avoidance intersects it with the selected
        // Regret wave; no extra destination or movement owner may override that overlap.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InGladiator,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -4.5f,
                -.5f), new Vector2(
                    4.5f,
                    -.5f), new Vector2(
                        4.5f,
                        60.5f), new Vector2(
                            -4.5f,
                            60.5f) },
            c => c.Location,
            () => PendingCasts(
            ).Where(
                c => c.Action == 30282));
        // 2026-09-28 Golden Flame30290 exposed zero omen/cast origin, while eight
        // visage actors supplied the actual 60x10 lanes. Generic raw-type 12 avoidance
        // left the player in the east lane at 08:40:42. Use actor origin/initial heading,
        // padded0.5 on every edge, through its retained impact. All simultaneous beams
        // and the overlapping Sculptor line share native avoidance and a safe quadrant.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InGladiator,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => FlamePoints(
            ),
            c => c.Location,
            () => PendingCasts(
            ).Where(
                c => c.Action is 30290 or 30291 or 30292),
            priority: AvoidancePriority.High);
        // Live Regret groups begin 2.57s apart and each lasts 4s. Their interlaced lanes
        // must resolve in order: publishing both fills the floor before the first set ends.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InGladiator,
            null,
            50,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -3,
                -.5f), new Vector2(
                    3,
                    -.5f), new Vector2(
                        3,
                        40.5f), new Vector2(
                            -3,
                            40.5f) },
            c => c.Location,
            () => SelectedWave(
                30278,
                250),
            priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(InGladiator, b => b.IsCasting && b.CastingSpellId is 30266 or 30267 or 30268, 4, 25.5f);
        // Ring's inner hit precedes its complementary donut by two seconds. Publishing both
        // simultaneously removes every safe point. Retain detached casts through the host's
        // 0.3-second early cast-end, then select the next impact. The extra outside constraint
        // stages within two yalms of the circle so a melee/ranged routine can return in time.
        foreach (var radius in new[]
        {
            8f,
            13f,
            18f
        }

        )
        {
            var r = radius;
            AvoidanceManager.AddAvoidLocation<Forecast>(
                InGladiator,
                _ => r + .5f,
                c => c.Location,
                () => SelectedRing(
                ).Where(
                    c => IsInnerRing(
                        c.Action)
                && RingRadius(
                    c.Action) == r));
            AvoidanceHelpers.AddAvoidDonut(
                InGladiator,
                () => SelectedRing(
                ).Where(
                    c => IsInnerRing(
                        c.Action)
                && RingRadius(
                    c.Action) == r).Select(
                        c => c.Location).ToArray(
                ),
                65,
                r + 2);
            AvoidanceHelpers.AddAvoidDonut(
                InGladiator,
                () => SelectedRing(
                ).Where(
                    c => !IsInnerRing(
                        c.Action)
                && RingRadius(
                    c.Action) == r).Select(
                        c => c.Location).ToArray(
                ),
                65,
                r - .5f);
        }

        // Sundered needs early-half staging, then a shared return to center before Ring.
        // SelectedSundered owns that resolution order rather than registering every circle.
        AvoidanceManager.AddAvoidLocation<Forecast>(InGladiator, _ => 10.5f, c => c.Location, () => SelectedSundered());
        // Rush helpers expose the landing origin, distinct from the boss's dash origin.
        // Front and reverse half-room casts resolve separately; retain only the earliest.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InGladiator,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => ConePoints(
                180),
            c => c.Location - new Vector3(
                (float)Math.Sin(
                    c.Heading) * .5f,
                0,
                (float)Math.Cos(
                    c.Heading) * .5f),
            () => SelectedRush(
            ),
            priority: AvoidancePriority.High);
        // The02:00 and 02:01 captures escaped the forward cleave but stood 18+ yalms
        // behind its pivot, too far to cross for the reverse hit two seconds later.
        // Keep native avoidance near that pivot while resolving the first half-room.
        AvoidanceHelpers.AddAvoidDonut(InGladiator, () => SelectedRush().Where(c => c.Action == 30269).Select(c => c.Location).ToArray(), 65, 4);
        RegisterGladiatorWind();
    }

    private void RegisterGladiatorWind()
    {
        // 2026-09-28 placement helpers at the arena's four corners cast 30285(radius 6)
        // for the large updraft and 31222(radius 4) for the three small winds. Their caster
        // positions matched the live omens. Placement hurts; only the later large wind
        // becomes positive shelter during Shattering Steel's last five seconds.
        AvoidanceManager.AddAvoidLocation<Forecast>(InGladiator, _ => 6.5f, c => c.Location, () => PendingCasts().Where(c => c.Action == 30285));
        AvoidanceManager.AddAvoidLocation<Forecast>(InGladiator, _ => 4.5f, c => c.Location, () => PendingCasts().Where(c => c.Action == 31222));
        AvoidanceManager.AddAvoidLocation<Vector3>(() => InGladiator() && !EnterUpdraft(), _ => 6.5f, p => p, () => _largeWinds);
        AvoidanceManager.AddAvoidLocation<Vector3>(InGladiator, _ => 4.5f, p => p, () => _smallWinds);
        // Stage nearby without lifting early, then enter and remain through impact. Both
        // constraints use the existing native planner and cannot fight a manual movement loop.
        AvoidanceHelpers.AddAvoidDonut(() => WindShelterActive() && !EnterUpdraft(), () => _windCenter, 65, 12);
        AvoidanceHelpers.AddAvoidDonut(EnterUpdraft, () => _windCenter, 65, 5.5);
    }

    private void UpdateGladiatorWind()
    {
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(GladiatorCenter) < 40).ToArray();
        // The live winds are untargetable NPC11391:14754 is the unique large updraft,
        // 15084 the small hazards. Hidden spawn placeholders become visible after placement.
        _largeWinds = actors.Where(b => b.BaseId == 14754 && b.IsVisible).Select(b => b.Location).ToArray();
        _smallWinds = actors.Where(b => b.BaseId == 15084 && b.IsVisible).Select(b => b.Location).ToArray();
        var steel = actors.FirstOrDefault(b => b.BaseId == 14751 && b.IsCasting && b.CastingSpellId == 30283);
        if (steel == null || _largeWinds.Length != 1)
        {
            return;
        }

        _windCenter = _largeWinds[0];
        // First captured Steel began01:04:35.289 with 11.7s remaining and applied its hit
        // at 01:04:47.797. Retain the destination through finish+0.8s; do not release on
        // RB's early cast-end. Subtract5.8 below to enter at nominal finish-5s.
        _windImpact = DateTime.UtcNow + steel.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(800);
    }

    private bool WindShelterActive() => InGladiator() && DateTime.UtcNow < _windImpact;
    private bool EnterUpdraft() => WindShelterActive() && DateTime.UtcNow >= _windImpact.AddSeconds(-5.8);
    private void UpdateGladiatorCasts()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _gladiatorCasts.Keys.Where(k => _gladiatorCasts[k].End <= now).ToArray())
        {
            _gladiatorCasts.Remove(key);
        }

        foreach (var key in _silverFlames.Keys.Where(k => _silverFlames[k][0].End <= now).ToArray())
        {
            _silverFlames.Remove(key);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsCasting && b.Distance2D(GladiatorCenter) < 65))
        {
            var action = actor.CastingSpellId;
            if (action is 30291 or 30292)
            {
                CaptureSilver(actor, now);
                continue;
            }

            if (!(action is >= 30269 and <= 30276 or 30278 or 30281 or 30282 or 30285 or 30288 or 30290 or 31222))
            {
                continue;
            }

            // Observed hidden boulders cast before becoming visible. Require their
            // actual owner IDs, but do not discard a valid cast for visibility.
            if (action == 30288 && (!actor.IsValid || actor.BaseId != 14755 || actor.NpcId != 11392))
            {
                continue;
            }

            var info = actor.SpellCastInfo;
            var location = actor.Location;
            // Three captured Rush sequences exposed zero CastLocation, but the helper actor
            // itself occupied the dash landing. Both helper headings initially face forward;
            // back30270 reverses that direction. Snapshot once so a late visual turn cannot
            // rotate the forecast a second time. This correction still requires live retest.
            if (location.Distance2D(GladiatorCenter) > 60)
            {
                continue;
            }

            // Preserve managed shape identity for the lifetime of this cast. The failed
            // 00:13 overlap recreated Forecast every frame, causing continuous native escape
            // reselection. Update detached scalars in place while retaining the initial heading.
            if (!_gladiatorCasts.TryGetValue(actor.ObjectId, out var forecast) || forecast.Action != action)
            {
                _gladiatorCasts[actor.ObjectId] = forecast = new Forecast
                {
                    Action = action,
                    Heading = actor.Heading + (action == 30270 ? (float)Math.PI : 0)
                };
            }

            forecast.Location = location;
            // Sundered/Rush damage is observed about 0.7s after RB's early cast completion.
            // Retain their geometry through that snapshot before exposing the next stage.
            // Golden's two captured effects arrived1.106/1.068s after the reported finish.
            // Sculptor's 2026-09-30 vulnerability arrived1.003s after reported finish;
            // use the same bounded fence without changing Regret's wave handoff timing.
            // Retain 1.2s so neither the routine nor the next forecast re-enters a late beam.
            var impactDelay = action is 30290 or 30282 ? 1200 : action is 30281 or 30269 or 30270 ? 700 : 300;
            forecast.End = now + info.RemainingCastTime + TimeSpan.FromMilliseconds(impactDelay);
        }
    }

    private void CaptureSilver(BattleCharacter actor, DateTime now)
    {
        var action = actor.CastingSpellId;
        if (_silverFallbacks.Contains(action) || _silverFlames.ContainsKey(actor.ObjectId))
        {
            return;
        }

        var matrix = actor.OmenMatrix;
        var width = Math.Sqrt(matrix.M00 * matrix.M00 + matrix.M02 * matrix.M02);
        var length = Math.Sqrt(matrix.M20 * matrix.M20 + matrix.M22 * matrix.M22);
        // 2026-09-28 09:43 captured half-width 5/length 60 with beam direction opposite
        // the two-faced model's heading. Use the exposed omen basis, never a guessed pi
        // correction. Unknown transforms retain SideStep for this action for the duty;
        // remove our same-action forecasts too, preserving one initial-telegraph owner.
        if (Math.Abs(width - 5) > .5 || Math.Abs(length - 60) > .5 || double.IsNaN(width + length) || matrix.Center.Distance2D(actor.Location) > 1)
        {
            _silverFallbacks.Add(action);
            foreach (var key in _silverFlames.Keys.Where(k => _silverFlames[k][0].Action == action).ToArray())
            {
                _silverFlames.Remove(key);
            }

            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write(
                    "[Sildihn] Silver{0} has an unrecognized omen; retaining generic initial avoidance, follow-ups unverified.",
                    action);
            }

            return;
        }

        // Four more ten-degree turns follow at two-second intervals. Same-direction
        // statues hit twice at 09:43:10 after generic avoidance ended. Reserve a point
        // outside the complete sweep during the initial warning: two seconds is too
        // short to cross from the first safe pocket. Both sweeps and Sculptor use the
        // same native planner; detached tests must preserve a common destination.
        // Retain through last impact with the visage family's captured1.2s impact fence.
        var end = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(9200);
        var heading = (float)Math.Atan2(matrix.M20, matrix.M22);
        _silverFlames[actor.ObjectId] = SilverForecasts(matrix.Center, heading, action, end);
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write("[Sildihn] Silver{0} sweep at{1}, heading={2}, until{3:O}", action, matrix.Center, heading, end);
        }
    }

    private static Forecast[] SilverForecasts(Vector3 origin, float heading, uint action, DateTime end) => Enumerable.Range(
        0,
        5).Select(
            i => new Forecast { Action = action, Location = origin, End = end, Heading = heading + i * (action == 30291 ? -1 : 1) * (float)Math.PI / 18 }).ToArray(
        );
    private IEnumerable<Forecast> SelectedRing()
    {
        var ring = PendingCasts().Where(c => c.Action is >= 30271 and <= 30276).OrderBy(c => c.End).FirstOrDefault();
        if (ring != null && !PendingCasts().Any(c => c.Action == 30281 && c.End < ring.End))
        {
            yield return ring;
        }
    }

    private IEnumerable<Forecast> SelectedSundered()
    {
        var casts = PendingCasts().Where(c => c.Action == 30281).OrderBy(c => c.End).ToArray();
        var center = casts.FirstOrDefault(c => c.Location.Distance2D(GladiatorCenter) < 2);
        // Outer circles follow every 0.5s, faster than crossing their diameter. 2026-09-28
        // 08:11 staged west while avoiding only center+first south; the third outer (west)
        // hit during the return. Reserve the early half of the wheel (four outer impacts)
        // with the center, leaving the later side for staging. Once the center resolves,
        // publish all remaining outer circles together so they share a safe-center return.
        return center == null ? casts : casts.Take(5);
    }

    // Avoid providers pulse independently of RunAsync (which can yield for mitigation).
    // Filter expiration here as well so a suspended coroutine cannot retain a stale wave.
    private IEnumerable<Forecast> PendingCasts() => _gladiatorCasts.Values.Concat(_silverFlames.Values.SelectMany(s => s)).Where(c => c.End > DateTime.UtcNow);
    private IEnumerable<Forecast> SelectedWave(uint action, int simultaneousMilliseconds)
    {
        var casts = PendingCasts().Where(c => c.Action == action).OrderBy(c => c.End).ToArray();
        if (casts.Length == 0)
        {
            return Array.Empty<Forecast>();
        }

        var deadline = casts[0].End.AddMilliseconds(simultaneousMilliseconds);
        return casts.Where(c => c.End <= deadline);
    }

    private IEnumerable<Forecast> SelectedRush()
    {
        var rush = PendingCasts().Where(c => c.Action is 30269 or 30270).OrderBy(c => c.End).FirstOrDefault();
        // Regret's two line waves finish before the front half-room. Keep one impact owner
        // until those lanes resolve, then expose the next Rush half-room within five seconds.
        if (rush != null && rush.End <= DateTime.UtcNow.AddSeconds(5) && !PendingCasts().Any(c => c.Action == 30278 && c.End < rush.End))
        {
            yield return rush;
        }
    }

    // 2026-09-28 23:28:37.419 Jolt continued through Ring 30275's native escape,
    // consuming its two-second return window and causing42,935 damage. Cancel only
    // inside the currently selected damaging ring with native escape already active.
    // Earlier Sundered/circle stages and ordinary safe casts retain their ownership.
    private void CancelRingCastForEscape()
    {
        if (!Core.Me.IsCasting)
        {
            _ringCancelledCast = 0;
            return;
        }

        var action = Core.Me.CastingSpellId;
        if (_ringCancelledCast == action
            || !AvoidanceManager.IsRunningOutOfAvoid
            || !SelectedRing(
            ).Any(
                c => InsideRingFootprint(
                    Core.Me.Location.X,
                    Core.Me.Location.Z,
                    c.Location.X,
                    c.Location.Z,
                    c.Action)))
        {
            return;
        }

        _ringCancelledCast = action;
        ActionManager.StopCasting();
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write("[Sildihn] Cancelled cast {0} inside Ring of Might; native avoidance retains movement.", action);
        }
    }

    // Match the published0.5-yalm clearance, excluding the inner phase's staging-only
    // outer limit. The finite65-yalm outer boundary is the registered donut's limit.
    private static bool InsideRingFootprint(float x, float z, float ox, float oz, uint action)
    {
        if (action is < 30271 or > 30276 || !float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(ox) || !float.IsFinite(oz))
        {
            return false;
        }

        var dx = x - ox;
        var dz = z - oz;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        var radius = RingRadius(action);
        return IsInnerRing(action) ? distance <= radius + .5f : distance >= radius - .5f && distance <= 65;
    }

    // The same record11 capture held Verfire through reverse Rush30270 at 23:32:35.706,
    // with 922ms remaining on the attack and 1583ms on the player cast. The44,389 hit
    // followed at 37.438. Only the selected, already-published180-degree half-room
    // may interrupt the cast; earlier Regret lanes retain SelectedRush's priority.
    private void CancelRushCastForEscape()
    {
        if (!Core.Me.IsCasting)
        {
            _rushCancelledCast = 0;
            return;
        }

        var action = Core.Me.CastingSpellId;
        if (_rushCancelledCast == action
            || !AvoidanceManager.IsRunningOutOfAvoid
            || !SelectedRush(
            ).Any(
                c => InsideSlamFootprint(
                    Core.Me.Location.X,
                    Core.Me.Location.Z,
                    c.Location.X,
                    c.Location.Z,
                    c.Heading,
                    180)))
        {
            return;
        }

        _rushCancelledCast = action;
        ActionManager.StopCasting();
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write("[Sildihn] Cancelled cast {0} inside Rush of Might; native avoidance retains movement.", action);
        }
    }

    private static bool IsInnerRing(uint action) => action is >= 30271 and <= 30273;
    private static float RingRadius(uint action) => 8 + 5 * (action <= 30273 ? action - 30271 : action - 30274);
    // Actor-relative forward lane with the same half-yalm margin used by other avoids.
    // A named shape also lets detached capture tests check the exact production polygon.
    private static Vector2[] FlamePoints() => new[]
    {
        new Vector2(-5.5f, -.5f),
        new Vector2(5.5f, -.5f),
        new Vector2(5.5f, 60.5f),
        new Vector2(-5.5f, 60.5f)
    };
    private void UpdateKegs(BattleCharacter boss)
    {
        var now = DateTime.UtcNow;
        var live = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsVisible
            && b.NpcId == 11443
            && b.Distance2D(
                GeryonCenter) < 30).ToArray(
            );
        var ids = new HashSet<uint>(live.Select(b => b.ObjectId));
        foreach (var id in _kegs.Keys.Where(id => !ids.Contains(id) && _kegs[id].End <= now).ToArray())
        {
            _kegs.Remove(id);
        }

        foreach (var actor in live)
        {
            if (!_kegs.TryGetValue(actor.ObjectId, out var keg))
            {
                // Live actor BaseIds identify initial colours only. Colossal Launch flips them
                // without changing BaseId, so track that choreography and trust actual casts last.
                if (actor.BaseId != 0x39C9 && actor.BaseId != 0x398C)
                {
                    continue;
                }

                keg = new Keg
                {
                    Id = actor.ObjectId,
                    Blue = actor.BaseId == 0x39C9
                };
                _kegs.Add(actor.ObjectId, keg);
            }

            keg.Location = actor.Location;
            if (boss.CastingSpellId == 29896 && !_flipCast && !keg.Casting)
            {
                keg.Blue = !keg.Blue;
            }

            if (actor.IsCasting && actor.CastingSpellId is 29908 or 29909)
            {
                keg.Blue = actor.CastingSpellId == 29909;
                keg.Casting = true;
                // 2026-09-28 cast reported finish18:57:44.337 but its donut damage
                // arrived45.730. Retain 1.6s, covering that1.393s delay plus sampling margin;
                // the former0.3s fence released both avoidance and routine hold too early.
                keg.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1600);
            }
        }

        _flipCast = boss.CastingSpellId == 29896;
    }

    private bool HasPendingKegs() => _kegs.Values.Any(k => !k.Casting || k.End > DateTime.UtcNow);
    private Vector3 KegPosition(Keg keg) => DateTime.UtcNow < _chargePositionsUntil
        && _chargedKegPositions.TryGetValue(
            keg.Id,
            out var predicted) ? predicted : keg.Location;
    private Keg[] SafeKegCandidates()
    {
        var pending = _kegs.Values.Where(k => !k.Casting || k.End > DateTime.UtcNow).ToArray();
        var center = GeryonCenter;
        return pending.Where(
            k => k.Blue
            && Math.Abs(
                KegPosition(
                    k).X - center.X) < 19
            && Math.Abs(
                KegPosition(
                    k).Z - center.Z) < 19).Where(
                        k => pending.All(
                            other => other.Id == k.Id
            || (other.Blue ? KegPosition(
                k).Distance2D(
                    KegPosition(
                        other)) > 17.5 : KegPosition(
                            k).Distance2D(
                                KegPosition(
                                    other)) > 15.5))).OrderBy(
                                        k => Core.Me.Distance2D(
                                            KegPosition(
                                                k))).ToArray(
            );
    }

    private Vector3[] SafeKegCenters()
    {
        // Aim the radial displacement into the keg, not away from it. After reported
        // finish, the dedicated movement lease holds still through the impact fence;
        // returning to this old starting point during the push would undo its landing.
        if (RunoffActive() && _runoffStage.HasValue)
        {
            return RunoffBeforeFinish() ? new[]
            {
                _runoffStage.Value
            }

            : Array.Empty<Vector3>();
        }

        var candidates = SafeKegCandidates();
        if (SlamActive())
        {
            var shared = candidates.Where(k => OutsideSlam(KegPosition(k))).ToArray();
            if (shared.Length > 0)
            {
                candidates = shared;
            }
            // No common destination: let the earlier Slam own movement, then return to
            // keg staging after its retained impact. Casting barrels can take priority
            // only when their captured deadline actually precedes that Slam.
            else if (SlamPrecedesKegs())
            {
                _barrelTarget = 0;
                return Array.Empty<Vector3>();
            }
        }

        // The fan pulls25yalms horizontally before barrels, leaving only about 4s to
        // return. Stage on the pull-side blue keg first so the pull preserves its row;
        // choosing the nearest opposite-side keg led to a late diagonal after the pull.
        var fanSide = ActiveFanSide();
        if (fanSide != 0 && candidates.Any(k => KegPosition(k).X * fanSide > 0))
        {
            candidates = candidates.Where(k => KegPosition(k).X * fanSide > 0).ToArray();
        }

        var blue = candidates.FirstOrDefault();
        // The fan pull can put the player on the opposite side. The 23:41 capture retained
        // a now-distant barrel and fought native egress. Keep hysteresis only within 3 yalms
        // of the nearest valid choice, then let active emergency avoidance finish its path.
        var previous = candidates.FirstOrDefault(k => k.Id == _barrelTarget);
        if (previous != null && blue != null && Core.Me.Distance2D(KegPosition(previous)) <= Core.Me.Distance2D(KegPosition(blue)) + 3)
        {
            blue = previous;
        }

        if (blue == null)
        {
            _barrelTarget = 0;
            return Array.Empty<Vector3>();
        }

        if (_barrelTarget != blue.Id)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[Sildihn] Barrel safe center {0}: {1}; preposition={2}", blue.Id, KegPosition(blue), !blue.Casting);
            }
        }

        _barrelTarget = blue.Id;
        return new[]
        {
            KegPosition(blue)
        };
    }

    private void UpdateSlam(BattleCharacter boss)
    {
        if (!boss.IsCasting || boss.CastingSpellId is not (29904 or 29905))
        {
            return;
        }

        if (!SlamActive() || _slam.Action != boss.CastingSpellId)
        {
            _slam = new Forecast
            {
                Action = boss.CastingSpellId,
                Location = boss.Location,
                Heading = boss.Heading
            };
        }

        // The01:46 capture reported finish01:46:40.32, then damage at 01:46:41.35.
        // Retain 1.1s across RB's early cast-end and diagnostic sampling so barrel
        // preposition cannot pull the player back into a just-finished cone.
        _slam.End = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1100);
    }

    private bool SlamActive() => _slam != null && _slam.End > DateTime.UtcNow;
    private static float SlamDegrees(Forecast slam) => slam.Action == 29904 ? 60 : 180;
    // 2026-09-28 22:59:16.968 native avoidance requested the correct Slam escape,
    // but Verfire continued until18.563. Arrival20.776 missed the 20.113 snapshot,
    // taking45,722 damage. Cancel only inside the currently published cone when native
    // escape already owns movement; leave barrel-first staging and ordinary casts alone.
    private void CancelSlamCastForEscape()
    {
        if (!Core.Me.IsCasting)
        {
            _slamCancelledCast = 0;
            return;
        }

        var action = Core.Me.CastingSpellId;
        if (_slamCancelledCast == action
            || !AvoidanceManager.IsRunningOutOfAvoid
            || !AvoidSlam(
            )
            || !InsideSlamFootprint(
                Core.Me.Location.X,
                Core.Me.Location.Z,
                _slam.Location.X,
                _slam.Location.Z,
                _slam.Heading,
                SlamDegrees(
                    _slam)))
        {
            return;
        }

        _slamCancelledCast = action;
        ActionManager.StopCasting();
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write("[Sildihn] Cancelled cast {0} inside Colossal Slam; native avoidance retains movement.", action);
        }
    }

    // Match the published padded sector, not OutsideSlam's larger two-yalm staging disk.
    // Unknown/nonfinite input cannot authorize a cast cancellation. The sector's arc is
    // tessellated by ConePoints; this analytical check differs only at its distant edge.
    private static bool InsideSlamFootprint(float x, float z, float ox, float oz, float heading, float degrees)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(ox) || !float.IsFinite(oz) || !float.IsFinite(heading) || degrees is not (60 or 180))
        {
            return false;
        }

        var padding = ConePadding(degrees);
        var dx = x - ox + (float)Math.Sin(heading) * padding;
        var dz = z - oz + (float)Math.Cos(heading) * padding;
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance > 60.5 + padding)
        {
            return false;
        }

        if (distance < .0001)
        {
            return true;
        }

        var forward = dx * Math.Sin(heading) + dz * Math.Cos(heading);
        return forward / distance >= Math.Cos(degrees * Math.PI / 360);
    }

    private bool OutsideSlam(Vector3 point)
    {
        if (!SlamActive())
        {
            return true;
        }

        var padding = ConePadding(SlamDegrees(_slam));
        var dx = point.X - _slam.Location.X + (float)Math.Sin(_slam.Heading) * padding;
        var dz = point.Z - _slam.Location.Z + (float)Math.Cos(_slam.Heading) * padding;
        // Reserve the entire two-yalm staging disk outside the padded cone. Otherwise
        // the native planner could settle on its near edge inside the impending Slam.
        var distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance <= 2)
        {
            return false;
        }

        var forward = dx * Math.Sin(_slam.Heading) + dz * Math.Cos(_slam.Heading);
        var angle = Math.Acos(Math.Max(-1, Math.Min(1, forward / distance)));
        return angle > SlamDegrees(_slam) * Math.PI / 360 + Math.Asin(2 / distance);
    }

    private bool SlamPrecedesKegs() => !_kegs.Values.Any(k => k.Casting && k.End > DateTime.UtcNow && k.End < _slam.End);
    private bool AvoidSlam() => SlamActive() && (SlamPrecedesKegs() || SafeKegCandidates().Any(k => OutsideSlam(KegPosition(k))));
    private static int ActiveFanSide()
    {
        // Shipped layout9321785 is the middle arena's fan assembly. 2026-09-28 captures
        // changed4->16 at 00:32:51 and 16->4 at 00:33:05 when the eastward pull occurred.
        // The opposite activation is1;4/2048 are inactive/setup. This is an exposed map
        // record, not a guessed memory offset or a persistent route-completion flag.
        // Fans belong to the middle branch. Its retained layout state must never influence
        // staging in the separate left arena, which instead has drain hazards.
        if (GeryonCenter != Vector3.Zero || DirectorManager.ActiveDirector is not InstanceContentDirector director)
        {
            return 0;
        }

        var effect = director.MapEffects.FirstOrDefault(m => m.ID == 9321785);
        return effect.State switch
        {
            16 => 1,
            1 => -1,
            _ => 0
        };
    }

    private void UpdateCharge(BattleCharacter boss)
    {
        var action = boss.IsCasting ? boss.CastingSpellId : 0;
        if (action is 29900 or 29901 && _chargeCast != action)
        {
            _chargedKegPositions.Clear();
            var forward = new Vector3((float)Math.Sin(boss.Heading), 0, (float)Math.Cos(boss.Heading));
            var lateral = new Vector3(forward.Z, 0, -forward.X);
            foreach (var keg in _kegs.Values)
            {
                var delta = keg.Location - boss.Location;
                var along = delta.X * forward.X + delta.Z * forward.Z;
                var across = delta.X * lateral.X + delta.Z * lateral.Z;
                // The14-wide charge moves intersected kegs9yalms sideways. 2026-09-28
                // 00:34 action 29901, heading 0, moved the center keg from(0,0) to(8.99,0),
                // making the southwest blue safe before the late2.2s explosion warning.
                // Snapshot once: using already-displaced positions would apply the shift twice.
                _chargedKegPositions[keg.Id] = keg.Location + (along >= 0
                    && along <= 40
                    && Math.Abs(
                        across) <= 7 ? lateral * (action == 29901 ? 9 : -9) : Vector3.Zero);
            }

            // Actor translation completed about 1.7s after RB's reported cast finish in
            // that capture. Keep the forecast until the observed two-second handoff.
            _chargePositionsUntil = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2);
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write(
                    "[Sildihn] Charge{0} forecasts {1} keg positions until{2:O}",
                    action,
                    _chargedKegPositions.Count,
                    _chargePositionsUntil);
            }
        }

        _chargeCast = action;
    }

    private void UpdateMill(BattleCharacter boss)
    {
        var action = boss.CastingSpellId;
        var initialCast = boss.IsCasting && action is 29897 or 29898;
        if (initialCast && _millCast != action)
        {
            _millInitialHeading = boss.Heading;
            _millStep = action == 29898 ? -(float)Math.PI / 8 : (float)Math.PI / 8;
            _millFirst = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(300);
            _lastMillStep = -1;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[Sildihn] Gigantomill action={0} first={1:O} heading={2}", action, _millFirst, _millInitialHeading);
            }
        }

        // 2026-09-28 right capture21:56:29: the first frame faced3.5207 while the boss
        // was still turning to 6.2831. Freezing that frame rotated every subsequent cross
        // incorrectly during the player's fatal sequence. Follow the telegraphed heading while
        // that cast is active; after it ends, only the captured22.5-degree sequence owns
        // rotation. Never refresh from the boss's later animated turns during the hits.
        if (initialCast)
        {
            _millInitialHeading = boss.Heading;
        }
        else if (_millCast is 29897 or 29898)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[Sildihn] Gigantomill settled initial heading={0}", _millInitialHeading);
            }
        }

        _millCast = initialCast ? action : 0;
        if (MillActive() && _lastMillStep != MillIndex())
        {
            _lastMillStep = MillIndex();
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[Sildihn] Gigantomill step={0} heading={1} barrelsFirst={2}", _lastMillStep, MillHeading(), HasPendingKegs());
            }
        }
    }

    // Five successive impacts rotate22.5 degrees at 1.7s intervals. The next sector becomes the
    // movement constraint immediately after the preceding snapshot, not for the whole sequence.
    private int MillIndex() => Math.Max(0, 1 + (int)Math.Floor((DateTime.UtcNow - _millFirst).TotalSeconds / 1.7));
    private float MillHeading() => _millInitialHeading + _millStep * MillIndex();
    private bool MillActive() => _millFirst != default && DateTime.UtcNow < _millFirst.AddSeconds(6.8);
    private void ResetSequence()
    {
        ResetThorne();
        ResetZeless();
        ResetRightGeryonSewage();
        ReleaseRunoff();
        ReleaseBoulderShelter();
        _runoff = null;
        _runoffStage = null;
        ReleaseSilkieKnockback();
        SetMechanicGapCloserBlock(false);
        _barrelTarget = _chargeCast = 0;
        _chargePositionsUntil = default;
        _chargedKegPositions.Clear();
        _kegs.Clear();
        _flipCast = false;
        _millCast = 0;
        _millFirst = default;
        _lastMillStep = -1;
        _gladiatorCasts.Clear();
        _silverFlames.Clear();
        _silkieCasts.Clear();
        _largeWinds = _smallWinds = Array.Empty<Vector3>();
        _windImpact = default;
        _slam = null;
        _slamCancelledCast = 0;
        _ringCancelledCast = 0;
        _rushCancelledCast = 0;
    }

    private void SetMechanicGapCloserBlock(bool active)
    {
        // 2026-09-28 01:48:22 Intervene16461 dashed from the correct northwest keg
        // into Charge29900 after avoidance had settled. The charge forecast was correct;
        // the dash broke it. Reuse the routine's supported GapCloser capability only,
        // preserving normal attacks, healing and native evasive walking. Expiry protects
        // stop/unload; encounter exit releases this independently owned lease immediately.
        if (active)
        {
            CapabilityManager.Update(_mechanicGapCloserHandle, CapabilityFlags.GapCloser, 1000, "Sildihn mechanic forecast: preserve staging between dodges");
            _ownsGapCloserBlock = true;
        }
        else if (_ownsGapCloserBlock)
        {
            CapabilityManager.Clear(_mechanicGapCloserHandle, CapabilityFlags.GapCloser, "Sildihn mechanic forecast ended");
            _ownsGapCloserBlock = false;
        }
    }

    // Local sectors face south before RB applies its negative-heading polygon convention.
    // A padded backward vertex expands both side edges without inventing an angular margin.
    private static Vector2[] ConePoints(float degrees)
    {
        var points = new List<Vector2>
        {
            Vector2.Zero
        };
        for (var i = 0; i <= 64; i++)
        {
            var angle = (degrees / 2 - degrees * i / 64) * Math.PI / 180;
            var radius = 60.5f + ConePadding(degrees);
            points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
        }

        return points.ToArray();
    }

    private static float ConePadding(float degrees) => .5f / (float)Math.Sin(degrees * Math.PI / 360);
    // Only detached IDs, positions, and timing survive ticks; native wrappers are reacquired.
    private sealed class Keg
    {
        internal uint Id;
        internal Vector3 Location;
        internal bool Blue;
        internal bool Casting;
        internal DateTime End;
    }

    // Detached helpers survive only until the predicted impact; no remote pointer crosses ticks.
    private sealed class Forecast
    {
        internal uint Action;
        internal Vector3 Location;
        internal float Heading;
        // Charge distance is captured from the native cast target, not actor facing;
        // retaining it through the impact fence prevents a moving caster shrinking its lane.
        internal float Length;
        internal DateTime End;
    }

    private static readonly Vector3 SilkieCenter = new(-335, -29, -155);
    private static readonly uint[] SilkieOwnedActions =
    {
        30511,
        30512,
        30513,
        30514,
        30515,
        30516,
        30520,
        30521,
        30522,
        30523,
        30524,
        30527,
        30528,
        30532,
        30534
    };
    private readonly Dictionary<uint, Forecast> _silkieCasts = new();
    private readonly CapabilityManagerHandle _silkieKnockbackHandle = CapabilityManager.CreateNewHandle();
    private V2? _silkieDestination;
    private uint _silkieKnockbackAction;
    private bool _silkieKnockbackOwned;
    private bool _silkieMoving;
    private DateTime _silkiePlanLog;
    private static bool InSilkie() => WorldManager.ZoneId == 1069 && Core.Me.InCombat && Core.Me.Distance2D(SilkieCenter) < 50;
    private void RegisterSilkieGeometry()
    {
        // Total Wash reduces the initial29.5 half-width floor to 20. Use the inner19
        // throughout combat so early positioning cannot stand on the disappearing edge.
        AvoidanceHelpers.AddAvoidSquareDonut(InSilkie, 38, 38, 120, 120, () => new[] { SilkieCenter });
        // 2026-09-28 repeats were hit while changing from the second90-degree sweep to
        // the 225-degree finisher. These helpers share an origin and the wide helper turns
        // +/-67.5 degrees: its135-degree safe wedge is also outside both earlier cones.
        // Publish the compatible forecasts together so native avoidance can stay in that
        // shared wedge, rather than waiting until the final1.5 seconds to cross the boss.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InSilkie,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            c => ConePoints(
                SilkieConeDegrees(
                    c)),
            c => c.Location - new Vector3(
                (float)Math.Sin(
                    c.Heading) * ConePadding(
                        SilkieConeDegrees(
                            c)),
                0,
                (float)Math.Cos(
                    c.Heading) * ConePadding(
                        SilkieConeDegrees(
                            c))),
            SelectedSilkieSweeps,
            priority: AvoidancePriority.High);
        // The first failed wide-sweep escape stopped less than one yalm from the pivot.
        // Reserve a small pivot disk to prevent corner snapping at the sector's vertex;
        // radius 1.5 includes the ordinary0.5 clearance and leaves the shared wedge open.
        AvoidanceManager.AddAvoidLocation<Forecast>(InSilkie, _ => 1.5f, c => c.Location, SelectedSilkieSweeps);
        // 2026-09-28 22:03:41 Chilling Duster hit at 4.862 yalms across its arm after
        // SideStep removed the cross0.56s earlier. Its60-long,5-half-width arms need
        // the same0.5 clearance and 1.2s late-impact fence as the other retained dusters.
        // Two perpendicular rectangles form one cross per helper; latest cast heading
        // remains authoritative, including the boss's rotated central cross.
        foreach (var quarterTurn in new[]
        {
            0f,
            (float)Math.PI / 2
        }

        )
        {
            AvoidanceManager.AddAvoidPolygon<Forecast>(
                InSilkie,
                null,
                70,
                c => -c.Heading + quarterTurn,
                _ => 1,
                _ => 15,
                _ => new[] { new Vector2(
                    -5.5f,
                    -60.5f), new Vector2(
                        5.5f,
                        -60.5f), new Vector2(
                            5.5f,
                            60.5f), new Vector2(
                                -5.5f,
                                60.5f) },
                c => c.Location,
                () => PendingSilkieCasts(
                ).Where(
                    c => c.Action is 30520 or 30523 or 30527),
                priority: AvoidancePriority.High);
        }

        // 2026-09-28 19:09:23 Slippery Soap hit with raw type 8 and no SideStep avoid.
        // Its caster heading 1.682 differed from the target bearing2.935. Build the
        // reference half-width 5 charge from the actual cast endpoints, padding0.5 on
        // every edge, and retain it through the observed0.89s late damage snapshot.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InSilkie,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            c => new[] { new Vector2(
                -5.5f,
                -.5f), new Vector2(
                    5.5f,
                    -.5f), new Vector2(
                        5.5f,
                        c.Length + .5f), new Vector2(
                            -5.5f,
                            c.Length + .5f) },
            c => c.Location,
            () => PendingSilkieCasts(
            ).Where(
                c => c.Action == 30522
            && c.Length > 0),
            priority: AvoidancePriority.High);
        // Live30521/30524 used an erroneous24-yalm hole and hit at 7.25+ yalms. The
        // actual safe hole is5; shrink it to 4.5 and expand the outer 60 by 0.5. The
        // Slippery Soap charge resolves first. At18:04:43 the old handoff began roughly
        // 20 yalms from the safe center and could not arrive before 18:04:46 damage. Stage
        // within 9 yalms during the charge while retaining its lane avoid, then tighten
        // to 4.5 after impact. Record2 at 22:46:48 started31.5 yalms from that center:
        // the old9-yalm staging disk forced a long escape across the charge, arriving
        // after its snapshot and taking37,172 damage. Stage within 14 instead: the
        // captured3.5s charge-to-duster interval, minus the 1.2s charge fence, leaves
        // 2.3s for at most9.5 yalms at ordinary6-yalm/s movement (about 1.59s).
        // This preserves post-charge arrival time while avoiding that premature squeeze.
        AvoidanceHelpers.AddAvoidDonut(
            InSilkie,
            () => PendingSilkieCasts(
            ).Any(
                c => c.Action == 30522) ? Array.Empty<Vector3>(
            ) : PendingSilkieCasts(
            ).Where(
                c => c.Action is 30521 or 30524 or 30528).Select(
                    c => c.Location).ToArray(
            ),
            60.5,
            4.5,
            AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(
            InSilkie,
            () => PendingSilkieCasts(
            ).Any(
                c => c.Action == 30522) ? PendingSilkieCasts(
            ).Where(
                c => c.Action == 30524).Select(
                    c => c.Location).ToArray(
            ) : Array.Empty<Vector3>(
            ),
            60.5,
            14,
            AvoidancePriority.High);
        // Slippery Suds30531 leaves event-object2003504 water. The initial cast stays
        // SideStep-owned; visible persistent pools need their own radius 5+0.5 owner.
        AvoidanceManager.AddAvoid(
            new AvoidObjectInfo<EventObject>(
                condition: InSilkie,
                objectSelector: o => o.IsValid
            && o.IsVisible
            && o.NpcId == 2003504,
                radiusProducer: _ => 5.5f,
                priority: AvoidancePriority.High));
        // Record1's moving Ewers advance 3.38788 yalms along +Z every 0.8s, ending at-135
        // with two terminal hits. 2026-09-28 19:41:12 the radius-only avoid allowed a
        // parallel escape that was overtaken (16,344 damage): reserve the next two steps
        // as a swept capsule, rather than chasing a constantly moving safe endpoint.
        // Radius4 damage +1.75 observed visual/helper phase +0.5 clearance also protects
        // the capsule ends. Clamp prediction at the terminal line; actor visibility owns
        // its lifetime, so no wall-clock timer can leave a stale or premature forecast.
        // Initial Brim Over remains SideStep-owned until the actor becomes visible.
        AvoidanceManager.AddAvoidLocation<Vector3>(InSilkie, _ => 6.25f, p => p, () => SilkieEwers().SelectMany(p => new[] { p, SilkieEwerAhead(p) }));
        AvoidanceManager.AddAvoidPolygon<Vector3>(
            InSilkie,
            null,
            70,
            _ => 0,
            _ => 1,
            _ => 15,
            p => new[] { new Vector2(
                -6.25f,
                0), new Vector2(
                    6.25f,
                    0), new Vector2(
                        6.25f,
                        SilkieEwerAhead(
                            p).Z - p.Z), new Vector2(
                                -6.25f,
                                SilkieEwerAhead(
                                    p).Z - p.Z) },
            p => p,
            () => SilkieEwers(
            ).Where(
                p => p.Z < -135),
            priority: AvoidancePriority.High);
        // All-cotton capture20:06:53: visible11370/14832 brooms traversed seven lanes
        // without casts; Sweep dealt31,293 plus vulnerability while only the arena avoid
        // existed. Cotton2013038 distinguishes these movers from stationary suds puffs.
        // Reserve the radius 3 contact plus 0.5 clearance and next 5 yalms of travel (about
        // 1.1s at the captured speed). Cotton makes individual brooms pause, so actor
        // position/heading must own the moving capsule instead of one sweep-wide timer.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            InSilkie,
            _ => 3.5f,
            p => p,
            () => SilkieBrooms(
            ).SelectMany(
                b => new[] { b.Location, SilkieBroomAhead(
                    b) }));
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            InSilkie,
            null,
            70,
            b => -b.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -3.5f,
                0), new Vector2(
                    3.5f,
                    0), new Vector2(
                        3.5f,
                        5), new Vector2(
                            -3.5f,
                            5) },
            b => b.Location,
            SilkieBrooms,
            priority: AvoidancePriority.High);
    }

    private static IEnumerable<Vector3> SilkieEwers() => GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Where(
            b => b.IsValid
        && b.IsVisible
        && b.IsAlive
        && b.BaseId == 14833
        && b.NpcId == 11371
        && b.Location.Distance2D(
            SilkieCenter) < 35).Select(
                b => b.Location);
    private static Vector3 SilkieEwerAhead(Vector3 location) => new(location.X, location.Y, Math.Max(location.Z, Math.Min(-135, location.Z + 2 * 3.38788f)));
    private static IEnumerable<BattleCharacter> SilkieBrooms() => GameObjectManager.GetObjectsOfType<EventObject>(
        ).Any(
            o => o.IsValid
        && o.IsVisible
        && o.NpcId == 2013038) ? GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Where(
            b => b.IsValid
        && b.IsVisible
        && b.IsAlive
        && b.BaseId == 14832
        && b.NpcId == 11370
        && b.Location.Distance2D(
            SilkieCenter) < 40) : Enumerable.Empty<BattleCharacter>(
        );
    private static Vector3 SilkieBroomAhead(BattleCharacter broom) => broom.Location + new Vector3(
        (float)Math.Sin(
            broom.Heading) * 5,
        0,
        (float)Math.Cos(
            broom.Heading) * 5);
    private void UpdateSilkieCasts()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _silkieCasts.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _silkieCasts.Remove(id);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.NpcId == 11369))
        {
            var action = actor.CastingSpellId;
            if (!SilkieOwnedActions.Contains(action))
            {
                continue;
            }

            if (actor.Location.Distance2D(SilkieCenter) > 60)
            {
                continue;
            }

            if (!_silkieCasts.TryGetValue(actor.ObjectId, out var cast) || cast.Action != action)
            {
                _silkieCasts[actor.ObjectId] = cast = new Forecast
                {
                    Action = action
                };
            }

            // Helpers initially share headings and may turn before their own impact;
            // retain their latest observed orientation, then freeze it after cast end.
            // Dust Bluster is radial from its cast target(-335,-155), not the boss's
            // displaced actor(-333.85,-148.58). Wash Out uses the helper's direction.
            if (action == 30522)
            {
                // Snapshot the first valid endpoints. A late translation toward the
                // target must not rotate or shorten the still-dangerous original lane.
                if (cast.Length <= 0)
                {
                    var destination = actor.SpellCastInfo.CastLocation;
                    var delta = destination - actor.Location;
                    var length = actor.Location.Distance2D(destination);
                    if (destination.Distance2D(SilkieCenter) < 40 && length > .1f && length < 60)
                    {
                        cast.Location = actor.Location;
                        cast.Heading = (float)Math.Atan2(delta.X, delta.Z);
                        cast.Length = length;
                    }
                }
            }
            else
            {
                cast.Location = action == 30532 ? actor.SpellCastInfo.CastLocation : actor.Location;
                cast.Heading = actor.Heading;
            }

            // The final sweep and duster damage landed0.76–0.99s after the reported
            // finish. A1.2s fence keeps the routine from re-entering that late hit.
            cast.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1200);
        }
    }

    private IEnumerable<Forecast> PendingSilkieCasts() => _silkieCasts.Values.Where(c => c.End > DateTime.UtcNow);
    private IEnumerable<Forecast> SelectedSilkieSweeps()
    {
        return PendingSilkieCasts().Where(c => c.Action is >= 30511 and <= 30516);
    }

    private static float SilkieConeDegrees(Forecast cast) => cast.Action is 30513 or 30516 ? 225 : 90;
    private async Task<bool> HandleSilkieKnockback()
    {
        var cast = PendingSilkieCasts().Where(c => c.Action is 30532 or 30534).OrderBy(c => c.End).FirstOrDefault();
        if (cast == null)
        {
            ReleaseSilkieKnockback();
            return false;
        }

        if (_silkieKnockbackAction != cast.Action)
        {
            _silkieDestination = null;
            _silkieKnockbackAction = cast.Action;
        }

        var pools = GameObjectManager.GetObjectsOfType<EventObject>(
            ).Where(
                o => o.IsValid
            && o.IsVisible
            && o.NpcId == 2003504).Select(
                o => SilkieXZ(
                    o.Location)).ToArray(
            );
        var source = SilkieXZ(cast.Location);
        // An unloaded/zero target must not manufacture a radial staging point. Normal
        // emergency avoidance and the routine stay schedulable on unknown evidence.
        if (cast.Action == 30532 && source.LengthSquared() > 40 * 40)
        {
            ReleaseSilkieKnockback();
            return false;
        }

        var avoids = AvoidanceManager.Avoids.ToArray();
        _silkieDestination = SilkieKnockbackPlan.Choose(
            SilkieXZ(
                Core.Me.Location),
            _silkieDestination,
            source,
            cast.Heading,
            cast.Action == 30534,
            pools,
            p => avoids.Any(
                a => a.IsPointInAvoid(
                    SilkieWorld(
                        p))));
        if (!_silkieDestination.HasValue)
        {
            ReleaseSilkieKnockback();
            if (DateTime.UtcNow >= _silkiePlanLog)
            {
                _silkiePlanLog = DateTime.UtcNow.AddSeconds(2);
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    ff14bot.Helpers.Logging.Write("[Sildihn] No corroborated Silkie knockback landing; yielding to native avoidance.");
                }
            }

            return false;
        }

        CapabilityManager.Update(
            _silkieKnockbackHandle,
            CapabilityFlags.Movement,
            1000,
            "Holding Silkie knockback staging with a wall- and water-safe landing");
        _silkieKnockbackOwned = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _silkieMoving = false;
            return false;
        }

        // Do not chase the old staging point once the reported cast has finished: retain
        // the routine hold through the 1.2s impact fence, allowing the forced displacement.
        // Repositioning back during the push caused baseline no-path loops at the edge.
        if (DateTime.UtcNow >= cast.End.AddMilliseconds(-1200) || Core.Me.Distance2D(SilkieWorld(_silkieDestination.Value)) <= .35f)
        {
            if (_silkieMoving)
            {
                Navigator.PlayerMover.MoveStop();
                _silkieMoving = false;
            }

            return false; // Rotation/healing remain schedulable while holding safety.
        }

        if (!_silkieMoving)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write(
                    "[Sildihn] Knockback {0} stage={1} source={2} heading={3}",
                    cast.Action,
                    SilkieWorld(
                        _silkieDestination.Value),
                    cast.Location,
                    cast.Heading);
            }
        }

        _silkieMoving = true;
        Navigator.PlayerMover.MoveTowards(SilkieWorld(_silkieDestination.Value));
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseSilkieKnockback()
    {
        if (_silkieKnockbackOwned)
        {
            CapabilityManager.Clear(_silkieKnockbackHandle, CapabilityFlags.Movement, "Silkie knockback staging ended");
        }

        if (_silkieMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _silkieKnockbackOwned = _silkieMoving = false;
        _silkieDestination = null;
        _silkieKnockbackAction = 0;
    }

    private static V2 SilkieXZ(Vector3 world) => new(world.X - SilkieCenter.X, world.Z - SilkieCenter.Z);
    private static Vector3 SilkieWorld(V2 local) => new(SilkieCenter.X + local.X, SilkieCenter.Y, SilkieCenter.Z + local.Y);
    private Forecast _runoff;
    private Vector3? _runoffStage;
    private readonly CapabilityManagerHandle _runoffHandle = CapabilityManager.CreateNewHandle();
    private bool _runoffOwned;
    private bool _runoffMoving;
    private bool RunoffActive() => _runoff != null && _runoff.End > DateTime.UtcNow;
    private bool RunoffBeforeFinish() => RunoffActive() && DateTime.UtcNow < _runoff.End.AddMilliseconds(-1200);
    private void UpdateRunoff()
    {
        if (!RunoffActive())
        {
            ReleaseRunoff();
            _runoff = null;
            _runoffStage = null;
        }

        var actor = GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).FirstOrDefault(
                b => b.IsValid
            && b.NpcId == 11442
            && b.IsCasting
            && b.CastingSpellId == 29911
            && b.Distance2D(
                GeryonCenter) < 40);
        if (actor == null)
        {
            return;
        }

        var fresh = _runoff == null;
        _runoff ??= new Forecast
        {
            Action = 29911
        };
        _runoff.Location = actor.Location;
        // Captured cast finished18:57:39.848; the actual push began40.625. Hold the
        // routine through that late displacement without walking back to the old start.
        _runoff.End = DateTime.UtcNow + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1200);
        if (_runoffStage.HasValue)
        {
            return;
        }

        var center = GeryonCenter;
        foreach (var keg in SafeKegCandidates())
        {
            var landing = KegPosition(keg);
            var start = GeryonRunoffPlan.Stage(
                new V2(
                    actor.Location.X - center.X,
                    actor.Location.Z - center.Z),
                new V2(
                    landing.X - center.X,
                    landing.Z - center.Z));
            if (!start.HasValue)
            {
                continue;
            }

            var world = new Vector3(center.X + start.Value.X, center.Y, center.Z + start.Value.Y);
            if (SlamActive() && (!OutsideSlam(world) || !OutsideSlam(landing)))
            {
                continue;
            }

            _runoffStage = world;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[Sildihn] Runoff stage={0} source={1} landing={2}", world, actor.Location, landing);
            }

            break;
        }

        if (fresh && !_runoffStage.HasValue)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[Sildihn] No corroborated Runoff landing; retaining native emergency avoidance.");
            }
        }
    }

    private async Task<bool> HandleRunoff()
    {
        if (!RunoffActive() || !_runoffStage.HasValue)
        {
            ReleaseRunoff();
            return false;
        }

        CapabilityManager.Update(_runoffHandle, CapabilityFlags.Movement, 1000, "Holding Runoff start for a safe keg landing");
        _runoffOwned = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _runoffMoving = false;
            return false;
        }

        // Native avoidance reaches the broad staging disk first. Finish precisely:
        // angular error near the radial source grows during the 18-yalm displacement.
        if (!RunoffBeforeFinish() || Core.Me.Distance2D(_runoffStage.Value) <= .15f)
        {
            if (_runoffMoving)
            {
                Navigator.PlayerMover.MoveStop();
                _runoffMoving = false;
            }

            return false;
        }

        _runoffMoving = true;
        Navigator.PlayerMover.MoveTowards(_runoffStage.Value);
        await Coroutine.Yield();
        return true;
    }

    private void ReleaseRunoff()
    {
        if (_runoffOwned)
        {
            CapabilityManager.Clear(_runoffHandle, CapabilityFlags.Movement, "Runoff staging ended");
        }

        if (_runoffMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _runoffOwned = _runoffMoving = false;
    }

    // Shipped layout centers and the 2026-09-28 native transitions agree: these four
    // 20x20 quadrants forecast no-cast Suddenly Sewage29912. First owned impact was at
    // 18:52:03.311,~8s after its warning, and the pair returned to 4 after that impact.
    private static readonly uint[] RightSewageLayouts =
    {
        9322512,
        9322520,
        9322521,
        9322522
    };
    private static readonly Vector3[] RightSewageCenters =
    {
        new(173, -21, 167),
        new(193, -21, 167),
        new(173, -21, 187),
        new(193, -21, 187)
    };
    private readonly SildihnRightSewageSequence _rightSewage = new();
    private int[] _rightFloodedQuadrants = Array.Empty<int>();
    private static bool InRightGeryon() => InGeryon() && GeryonCenter.X > 120;
    private void UpdateRightGeryonSewage()
    {
        if (!InRightGeryon() || DirectorManager.ActiveDirector is not InstanceContentDirector director)
        {
            _rightSewage.Reset();
            _rightFloodedQuadrants = Array.Empty<int>();
            return;
        }

        var maps = director.MapEffects.ToArray();
        // Duplicate IDs are ambiguous during director transitions; invalidate rather than choose an arbitrary state.
        var states = RightSewageLayouts.Select(id => maps.Count(m => m.ID == id) == 1 ? (int)maps.First(m => m.ID == id).State : -1).ToArray();
        _rightSewage.Update(states, DateTime.UtcNow);
        _rightFloodedQuadrants = FloodedRightQuadrants(states);
    }

    private void RegisterRightGeryonGeometry()
    {
        // A0.5-yalm margin pads each floor quadrant. Publish only the earliest pair;
        // registering both warned pairs covers the entire floor before the first impact.
        // The existing Slam/Swing owner remains registered, so native avoidance chooses
        // the common safe quadrant during sewage→Swing→second-sewage overlap.
        AvoidanceManager.AddAvoidPolygon<Vector3>(
            InRightGeryon,
            null,
            50,
            _ => 0,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -10.5f,
                -10.5f), new Vector2(
                    10.5f,
                    -10.5f), new Vector2(
                        10.5f,
                        10.5f), new Vector2(
                            -10.5f,
                            10.5f) },
            p => p,
            () => _rightSewage.EarliestPair(
            ).Concat(
                _rightFloodedQuadrants).Distinct(
            ).Select(
                i => RightSewageCenters[i]),
            priority: AvoidancePriority.High);
        // The second pair resolves~2s later. Staying within 6 yalms of center while both
        // pairs are queued leaves enough travel time between safe quadrants. Keep this
        // optional proximity preference out of barrel/mill overlaps; actual hazards retain
        // their owners and can never be suppressed to satisfy a staging preference.
        AvoidanceHelpers.AddAvoidDonut(
            () => InRightGeryon(
            )
            && _rightSewage.PendingCount == 4
            && !HasPendingKegs(
            )
            && !MillActive(
            ),
            () => new[] { GeryonCenter },
            65,
            6,
            AvoidancePriority.Low);
    }

    // 2026-09-28 22:11:39: west quadrants transitioned4→64→1024→4. The warning
    // lasted3 seconds; flooded1024 lasted19 seconds and caused Dropsy2921 while inside.
    // East quadrants repeated the same sequence22:13:46. This persistent floor loss is
    // distinct from the 1→16→4 impact sequence. Avoid warning and active floor immediately,
    // retain until native4, and reject partial/unknown snapshots rather than guessing IDs.
    private static int[] FloodedRightQuadrants(int[] states)
    {
        if (states.Length != 4 || states.Any(s => s is not (1 or 4 or 16 or 64 or 1024)))
        {
            return Array.Empty<int>();
        }

        return Enumerable.Range(0, 4).Where(i => states[i] is 64 or 1024).ToArray();
    }

    private void ResetRightGeryonSewage()
    {
        _rightSewage.Reset();
        _rightFloodedQuadrants = Array.Empty<int>();
    }

    // September 28 capture: Zeless is the real Base 14761 actor at (289,33,-115),
    // distinct from the many Base 9020 helpers sharing NPC11393. Show of Strength shrinks
    // the floor to this inner rectangle; keeping its half-yalm inset throughout combat
    // preserves safe destinations before the outer floor disappears.
    private static readonly Vector3 ZelessCenter = new(289, 33, -105);
    // A15-yalm pull followed by radius 8 damage requires23 yalms before the pull;
    // keep the usual half-yalm margin for native movement/position sampling.
    private const float ZelessWellPullClearance = 23.5f;
    private static readonly uint[] ZelessOwnedActions =
    {
        29839,
        29861,
        29851,
        29853,
        29866
    };
    private readonly Dictionary<uint, ZelessHazard> _zelessHazards = new();
    private readonly Dictionary<uint, Vector3> _zelessActorPositions = new();
    private readonly Dictionary<uint, Forecast> _zelessCones = new();
    private readonly Dictionary<uint, Forecast> _zelessWellBursts = new();
    private Vector3? _zelessFireLane;
    private float _zelessFireLaneWidth;
    private float _zelessFireLaneHeight;
    private DateTime _zelessFireLaneUntil;
    private uint _zelessCancelledCast;
    private Vector3? _zelessBombStage;
    private DateTime _zelessWellBurstUntil;
    private sealed class ZelessHazard
    {
        internal uint BaseId;
        internal Vector3 Position;
        internal float Heading;
        internal DateTime End;
        internal bool Casting;
        internal bool Relocated;
    }

    private static bool InZeless() => WorldManager.ZoneId == 1069
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            ZelessCenter) < 55
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.IsValid
        && b.BaseId == 14761
        && b.IsAlive);
    private void RegisterZelessGeometry()
    {
        AvoidanceHelpers.AddAvoidSquareDonut(InZeless, 29, 39, 140, 140, () => new[] { ZelessCenter });
        // 2026-09-30 08:45:12.624 exposed brand aura2397/value459 at the actual
        // later burst center,7.5s before its cast. Starting17.88 yalms away still
        // pulled the player into damage despite retained burst avoidance. Move beyond
        // pull+blast reach while that observed warning is present; aura loss hands
        // off to the real helper cast below. Never infer an unseen portal position.
        AvoidanceManager.AddAvoidLocation<BattleCharacter>(
            InZeless,
            _ => ZelessWellPullClearance,
            b => b.Location,
            () => GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                IsZelessWellPull));
        // 2026-09-30 08:14:14.817: the cast-only circle disappeared while the player
        // was still6.37 yalms from the helper; movement stopped and damage landed
        // at 15.714. Retain the actual radius 8 burst (+0.5 margin) through the same
        // observed post-cast fence used to suppress bomb staging. Only the real
        // helper cast authorizes geometry; this does not guess the earlier pull.
        AvoidanceManager.AddAvoidLocation<Forecast>(InZeless, _ => 8.5f, c => c.Location, () => ActiveZelessWellBursts(DateTime.UtcNow));
        // Four bombs were visible 8.5 seconds before their 1.2-second cast; waiting for
        // that cast killed the player at 19:24:50. Publish the captured radius 12 (+0.5 margin)
        // from actor creation on ordinary waves. Portal waves use observed actor relocation plus actual-cast fallback
        // until the actor physically relocates to its observed destination.
        AvoidanceManager.AddAvoidLocation<ZelessHazard>(InZeless, _ => 12.5f, h => h.Position, () => ZelessHazards(14767));
        // 2026-09-29 01:10:10–26: native circle escape returned no-path for sixteen
        // seconds while the player was inside both stationary bombs. No cast blocked
        // an active escape. Give the same native planner an explicit safe disk rather
        // than adding a second movement owner. Its complete disk clears every retained
        // bomb and the inset floor; actual relocation invalidates and reselects it.
        AvoidanceHelpers.AddAvoidDonut(
            InZeless,
            () => _zelessBombStage.HasValue ? new[] { _zelessBombStage.Value } : Array.Empty<Vector3>(
            ),
            70,
            1,
            AvoidancePriority.High);
        // Laser brands expose their warning/active state through aura2397 values449/499.
        // The 2026-09-28 Pure Fire dodge crossed a live lane and triggered29848; keeping
        // these directional width 2 (+1 margin) lanes active gives both mechanics one planner.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(InZeless, IsZelessLaser, 3, 100, priority: AvoidancePriority.High);
        // 2026-09-28 22:24/25: choosing only inner vertical gaps crossed a live beam
        // from a safe outer gap; the next six-beam horizontal layout had no constraint.
        // Keep the current safe gap in either observed orientation, leaving movement
        // along it to native avoidance. Descriptor dimensions are static in this helper;
        // inactive producers must be empty because SideStep enumerates them as well.
        foreach (var dimensions in new[]
        {
            (4f, 39f),
            (2.5f, 39f),
            (29f, 4f)
        }

        )
        {
            var width = dimensions.Item1;
            var height = dimensions.Item2;
            AvoidanceHelpers.AddAvoidSquareDonut(
                () => InZeless(
                )
                && _zelessFireLane.HasValue
                && _zelessFireLaneWidth == width
                && _zelessFireLaneHeight == height,
                width,
                height,
                140,
                140,
                () => _zelessFireLane.HasValue
                && _zelessFireLaneWidth == width
                && _zelessFireLaneHeight == height ? new[] { _zelessFireLane.Value } : Array.Empty<Vector3>(
                ),
                AvoidancePriority.High);
        }

        // Cast Shadow helpers begin together but resolve in two six-cone waves two
        // seconds apart. Publishing all twelve removes every safe wedge. Native type 13
        // did not supply either wave in the 20:03 capture, which hit the second group.
        AvoidanceManager.AddAvoidPolygon<Forecast>(
            InZeless,
            null,
            70,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => ConePoints(
                30),
            c => c.Location - new Vector3(
                (float)Math.Sin(
                    c.Heading) * ConePadding(
                        30),
                0,
                (float)Math.Cos(
                    c.Heading) * ConePadding(
                        30)),
            SelectedZelessCones,
            priority: AvoidancePriority.High);
        // A thirty-degree switch near the wall is over ten yalms. Stage within eight
        // so the later wedge can be reached during the two-second inter-wave interval.
        AvoidanceHelpers.AddAvoidDonut(
            InZeless,
            () => SelectedZelessCones(
            ).Any(
            ) ? new[] { ZelessCenter } : Array.Empty<Vector3>(
            ),
            70,
            8,
            AvoidancePriority.Low);
        // Fonts are centered beams, not actor-forward rays: the captured east-facing
        // fonts span the room even when spawned at X299. Pad the ten-yalm width by one.
        AvoidanceManager.AddAvoidPolygon<ZelessHazard>(
            InZeless,
            null,
            65,
            h => -h.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -5.5f,
                -50.5f), new Vector2(
                    5.5f,
                    -50.5f), new Vector2(
                        5.5f,
                        50.5f), new Vector2(
                            -5.5f,
                            50.5f) },
            h => h.Position,
            () => ZelessHazards(
                14769),
            priority: AvoidancePriority.High);
    }

    private IEnumerable<ZelessHazard> ZelessHazards(uint baseId) => _zelessHazards.Values.Where(h => h.BaseId == baseId && h.End > DateTime.UtcNow);
    private void UpdateZelessHazards()
    {
        var now = DateTime.UtcNow;
        // 2026-09-30 02:15:32-35: the bomb staging disk was wholly inside Infern
        // Well's radius 8 burst and native escape returned no-path. Release only that
        // staging preference through the actual helper cast plus 1.5s (observed damage
        // arrived up to 1.15s after cast end). The retained native burst circle now
        // owns29866 because the cast-only warning ended before that damage arrived.
        // This does not infer the earlier pull from the boss visual.
        foreach (var well in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.BaseId == 9020
            && b.NpcId == 11393
            && b.IsCasting
            && b.CastingSpellId == 29866
            && b.Distance2D(
                ZelessCenter) < 60))
        {
            var end = now + well.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1500);
            _zelessWellBursts[well.ObjectId] = new Forecast
            {
                Action = 29866,
                Location = well.Location,
                End = end
            };
            if (end > _zelessWellBurstUntil)
            {
                _zelessWellBurstUntil = end;
            }
        }

        foreach (var id in _zelessWellBursts.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _zelessWellBursts.Remove(id);
        }

        UpdateZelessFireLane(now);
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.BaseId == 9020
            && b.NpcId == 11393
            && b.IsCasting
            && b.CastingSpellId is 29851 or 29853
            && b.Distance2D(
                ZelessCenter) < 5))
        {
            _zelessCones[actor.ObjectId] = new Forecast
            {
                Action = actor.CastingSpellId,
                Location = actor.Location,
                Heading = actor.Heading,
                // The observed second-wave aura was first seen0.74s after cast end.
                // Retain 0.9s, including observation jitter, before handing off the wedge.
                End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(900)
            };
        }

        foreach (var id in _zelessCones.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _zelessCones.Remove(id);
        }

        // 2026-09-28 22:49/22:51: only the bombs at the two visible portal origins
        // moved; the stationary X280/298 bombs still exploded at Z-105. A room-wide
        // portal flag incorrectly removed their early warnings and caused two Burn hits.
        // Suppress only the matching origin. Destinations still require actual relocation,
        // because portal heading alone does not expose the animation-dependent transform.
        var portals = GameObjectManager.GameObjects.Where(
            o => o.IsValid
            && o.IsVisible
            && o.BaseId == 2013025
            && o.Distance2D(
                ZelessCenter) < 60).Select(
                    o => o.Location).ToArray(
            );
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && (b.BaseId == 14767
            || b.BaseId == 14769)
            && b.Distance2D(
                ZelessCenter) < 60))
        {
            var casting = actor.IsCasting && actor.CastingSpellId is 29839 or 29861;
            var first = !_zelessActorPositions.TryGetValue(actor.ObjectId, out var previous);
            // Both portal fonts and bombs actually moved ten yalms before their short cast.
            // A five-yalm threshold excludes float jitter while using the real destination;
            // no animation, portal rotation or client-memory offset is inferred.
            var relocated = !first && previous.Distance2D(actor.Location) >= 5;
            var atPortal = IsZelessPortalOrigin(actor.Location.X, actor.Location.Z, portals.Select(p => p.X).ToArray(), portals.Select(p => p.Z).ToArray());
            _zelessActorPositions[actor.ObjectId] = actor.Location;
            if (!_zelessHazards.TryGetValue(actor.ObjectId, out var h))
            {
                if (!casting && !relocated && (!first || atPortal))
                {
                    continue;
                }

                // Portal bomb spawn01:10:10.693 preceded impact01:10:26.622 by 16s.
                // The old12s pre-cast cap dropped stationary warnings before the cast;
                // bound this provisional window at 20s, then use actual cast timing below.
                h = new ZelessHazard
                {
                    BaseId = actor.BaseId,
                    Position = actor.Location,
                    Heading = actor.Heading,
                    End = now.AddSeconds(20),
                    Relocated = relocated
                };
                _zelessHazards[actor.ObjectId] = h;
            }

            if (atPortal && !casting && !h.Casting && !h.Relocated && !relocated)
            {
                _zelessHazards.Remove(actor.ObjectId);
                continue;
            }

            if (relocated)
            {
                h.Position = actor.Location;
                h.Heading = actor.Heading;
                h.Relocated = true;
                h.End = now.AddSeconds(6); // Both observed relocation-to-impact intervals were under four seconds.
            }

            if (casting)
            {
                h.Position = actor.Location;
                h.Heading = actor.Heading;
                h.Casting = true;
                // The lethal first Burn landed 2.4 seconds after its 1.2-second cast
                // started. Keep geometry through the observed delayed damage snapshot.
                h.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1500);
            }
        }

        foreach (var id in _zelessHazards.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _zelessHazards.Remove(id);
        }

        UpdateZelessBombStage();
        CancelZelessCastForEmergencyEscape(now);
    }

    private void UpdateZelessBombStage()
    {
        // Other overlapping mechanics keep their existing planner. This preference
        // is only for the captured two-to-four-circle bomb phase, never for fonts,
        // laser corridors, rotating cones, or Well bursts whose safe intersection is
        // different. The retained Well deadline prevents the old disk returning between
        // the short cast's disappearance and its delayed damage snapshot.
        var bombs = ZelessHazards(14767).ToArray();
        if (DateTime.UtcNow < _zelessWellBurstUntil
            || ZelessHazards(
                14769).Any(
            )
            || SelectedZelessCones(
            ).Any(
            )
            || GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Any(
                b => IsZelessLaser(
                    b)
            || IsZelessWellPull(
                b)))
        {
            _zelessBombStage = null;
            return;
        }

        var x = bombs.Select(b => b.Position.X).ToArray();
        var z = bombs.Select(b => b.Position.Z).ToArray();
        var stage = SelectZelessBombStage(x, z, Core.Me.Location.X, Core.Me.Location.Z, _zelessBombStage?.X ?? float.NaN, _zelessBombStage?.Z ?? float.NaN);
        if (stage.Length == 0)
        {
            _zelessBombStage = null;
            return;
        }

        var next = new Vector3(stage[0], 33, stage[1]);
        if (!_zelessBombStage.HasValue || _zelessBombStage.Value.Distance2D(next) > .1f)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[SildihnZeless] Native bomb safe-disk stage={0} bombs={1}", next, bombs.Length);
            }
        }

        _zelessBombStage = next;
    }

    // Search only the captured flat29x39 floor. A one-yalm staging disk plus an extra
    // half-yalm planning margin needs14 yalms from each padded12.5 circle. Preserve
    // a still-valid destination across ticks; never predict a portal's unseen rotation.
    // Unknown/missing/duplicate positions and a covered floor return no preference.
    private static float[] SelectZelessBombStage(float[] x, float[] z, float px, float pz, float oldX, float oldZ)
    {
        if (x.Length != z.Length
            || x.Length < 2
            || x.Length > 4
            || x.Concat(
                z).Concat(
                    new[] { px, pz }).Any(
                        v => !float.IsFinite(
                            v))
            || Enumerable.Range(
                0,
                x.Length).Any(
                    i => Enumerable.Range(
                        i + 1,
                        x.Length - i - 1).Any(
                            j => Math.Abs(
                                x[i] - x[j]) < .1f
            && Math.Abs(
                z[i] - z[j]) < .1f)))
        {
            return Array.Empty<float>();
        }

        bool Safe(float cx, float cz) => float.IsFinite(
            cx)
            && float.IsFinite(
                cz)
            && cx >= 276
            && cx <= 302
            && cz >= -123
            && cz <= -87
            && Enumerable.Range(
                0,
                x.Length).All(
                    i => (cx - x[i]) * (cx - x[i]) + (cz - z[i]) * (cz - z[i]) >= 196);
        if (Safe(oldX, oldZ))
        {
            return new[]
            {
                oldX,
                oldZ
            };
        }

        var best = Array.Empty<float>();
        var distance = float.MaxValue;
        for (var cx = 276f; cx <= 302; cx += .5f)
        {
            for (var cz = -123f; cz <= -87; cz += .5f)
            {
                if (!Safe(cx, cz))
                {
                    continue;
                }

                var d = (cx - px) * (cx - px) + (cz - pz) * (cz - pz);
                if (d >= distance)
                {
                    continue;
                }

                distance = d;
                best = new[]
                {
                    cx,
                    cz
                };
            }
        }

        return best;
    }

    // Both captured moving actors and their visible portal origins coincide exactly.
    // A0.5-yalm comparison margin tolerates float jitter without attributing another
    // actor's portal to the stationary bombs nine or more yalms away. Unknown snapshots
    // cannot remove a forecast; actual casting/relocation remains authoritative.
    private static bool IsZelessPortalOrigin(float x, float z, float[] px, float[] pz)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || px.Length != pz.Length || px.Concat(pz).Any(v => !float.IsFinite(v)))
        {
            return false;
        }

        return Enumerable.Range(0, px.Length).Any(i => (x - px[i]) * (x - px[i]) + (z - pz[i]) * (z - pz[i]) <= .25f);
    }

    // 2026-09-28 21:21:02: a portal bomb hit after the native escape was delayed by
    // Jolt. Relocation was known at 58.767, but movement began only 00.415, near slidecast.
    // Cancel only a cast physically inside that retained, relocated hazard when native
    // avoidance already owns escape. This also applies to healing/raising: completing
    // the cast cannot justify remaining in the imminent blast. Rotation resumes normally
    // afterward; no job action, destination, movement lease or generic routine rule changes.
    private void CancelZelessCastForEmergencyEscape(DateTime now)
    {
        if (!Core.Me.IsCasting)
        {
            _zelessCancelledCast = 0;
            return;
        }

        var cast = Core.Me.CastingSpellId;
        if (_zelessCancelledCast == cast
            || !AvoidanceManager.IsRunningOutOfAvoid
            || !_zelessHazards.Values.Any(
                h => h.Relocated
            && h.End > now
            && InsideZelessHazard(
                h.BaseId,
                Core.Me.Location.X,
                Core.Me.Location.Z,
                h.Position.X,
                h.Position.Z,
                h.Heading)))
        {
            return;
        }

        // The latch precedes the public synchronous stop request, preventing repeated
        // cancels while the same cast remains visible during the client's acknowledgement.
        _zelessCancelledCast = cast;
        ActionManager.StopCasting();
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write("[SildihnZeless] Cancelled cast " + cast + " inside relocated hazard; native avoidance retains movement.");
        }
    }

    // Match the exact published circle/centered-font geometry, including its0.5-yalm
    // padding. Unknown actors and nonfinite snapshots cannot authorize cancelling a cast.
    private static bool InsideZelessHazard(uint baseId, float x, float z, float hx, float hz, float heading)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) || !float.IsFinite(hx) || !float.IsFinite(hz) || !float.IsFinite(heading))
        {
            return false;
        }

        var dx = x - hx;
        var dz = z - hz;
        if (baseId == 14767)
        {
            return dx * dx + dz * dz <= 12.5f * 12.5f;
        }

        if (baseId != 14769)
        {
            return false;
        }

        var lateral = dx * Math.Cos(heading) - dz * Math.Sin(heading);
        var longitudinal = dx * Math.Sin(heading) + dz * Math.Cos(heading);
        return Math.Abs(lateral) <= 5.5 && Math.Abs(longitudinal) <= 50.5;
    }

    private IEnumerable<Forecast> SelectedZelessCones()
    {
        var pending = _zelessCones.Values.Where(c => c.End > DateTime.UtcNow).OrderBy(c => c.End).ToArray();
        if (pending.Length == 0)
        {
            return Array.Empty<Forecast>();
        }

        var wave = pending.Where(c => c.End <= pending[0].End.AddMilliseconds(350)).ToArray();
        // Six helpers are the observed complete wave. A partial/ambiguous packet must
        // not claim a safe gap which an unobserved cone could cover.
        return wave.Length == 6 ? wave : Array.Empty<Forecast>();
    }

    private void UpdateZelessFireLane(DateTime now)
    {
        var lasers = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(IsZelessLaser).ToArray();
        var lane = SelectZelessFireLane(
            lasers.Select(
                b => b.Location.X).ToArray(
            ),
            lasers.Select(
                b => b.Location.Z).ToArray(
            ),
            lasers.Select(
                b => b.Heading).ToArray(
            ),
            Core.Me.Location.X,
            Core.Me.Location.Z);
        if (lane.Length == 0)
        {
            _zelessFireLane = null;
            return;
        }

        var fire = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.IsValid && b.IsCasting && b.CastingSpellId is 29855 or 29856);
        if (fire != null)
        {
            if (!_zelessFireLane.HasValue || _zelessFireLaneHeight != lane[3])
            {
                _zelessFireLane = new Vector3(lane[0], 33, lane[1]);
                _zelessFireLaneWidth = lane[2];
                _zelessFireLaneHeight = lane[3];
            }

            // The visual precedes the helper by 3.9 seconds; retain the same lane
            // through the actual helper's cast and observed delayed damage snapshot.
            _zelessFireLaneUntil = now + fire.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(fire.CastingSpellId == 29855 ? 6000 : 1500);
        }
        else if (now >= _zelessFireLaneUntil)
        {
            _zelessFireLane = null;
        }
    }

    // Return center X/Z and full width/height only for the two captured complete grids.
    // Padded beams consume1.5 each side. Vertical outer gaps are2.5 wide and must remain
    // available; horizontal edge slivers are only 0.5 wide and cannot safely hold a player.
    // Nearest gap center preserves any already-safe gap, including the player's X274.576
    // outer position, rather than selecting a destination across an intervening laser.
    private static float[] SelectZelessFireLane(float[] x, float[] z, float[] heading, float px, float pz)
    {
        if (x.Length != z.Length || x.Length != heading.Length || x.Concat(z).Concat(heading).Concat(new[] { px, pz }).Any(v => !float.IsFinite(v)))
        {
            return Array.Empty<float>();
        }

        var order = Enumerable.Range(0, x.Length).OrderBy(i => x[i]).ToArray();
        if (IsZelessLongitudinalLaserLayout(
            order.Select(
                i => x[i]).ToArray(
            ),
            order.Select(
                i => z[i]).ToArray(
            ),
            order.Select(
                i => heading[i]).ToArray(
            ))
            && order.Select(
                (i, n) => Math.Abs(
                    x[i] - (278.5f + 7 * n)) <= .3f).All(
                        v => v))
        {
            var centers = new[]
            {
                275.75f,
                282f,
                289f,
                296f,
                302.25f
            };
            var index = Enumerable.Range(0, 5).OrderBy(i => Math.Abs(centers[i] - px)).First();
            return new[]
            {
                centers[index],
                -105f,
                index is 0 or 4 ? 2.5f : 4f,
                39f
            };
        }

        order = Enumerable.Range(0, z.Length).OrderBy(i => z[i]).ToArray();
        if (order.Length != 6
            || !order.Select(
                (i, n) => Math.Abs(
                    x[i] - 274) <= .3f
            && Math.Abs(
                z[i] - (-122.5f + 7 * n)) <= .3f
            && Math.Sin(
                heading[i]) > .999f).All(
                    v => v))
        {
            return Array.Empty<float>();
        }

        var horizontal = Enumerable.Range(0, 5).Select(i => -119f + 7 * i).OrderBy(v => Math.Abs(v - pz)).First();
        return new[]
        {
            289f,
            horizontal,
            29f,
            4f
        };
    }

    // 2026-09-28 21:44:17: the second live wave originated at Z-125, not Z-105.
    // Rejecting that captured back-edge layout disabled the corridor and native Pure
    // Fire escape crossed X292.5. Both observed rows have the same four longitudinal
    // beams; retain exact spacing, common-row and heading checks so an unknown rotated
    // arrangement cannot advertise a false safe corridor.
    private static bool IsZelessLongitudinalLaserLayout(float[] x, float[] z, float[] heading)
    {
        if (x.Length != 4 || z.Length != 4 || heading.Length != 4 || x.Concat(z).Concat(heading).Any(v => !float.IsFinite(v)))
        {
            return false;
        }

        if (Math.Abs(z[0] + 105) > .3f && Math.Abs(z[0] + 125) > .3f)
        {
            return false;
        }

        return Enumerable.Range(
            0,
            4).All(
                i => Math.Abs(
                    z[i] - z[0]) <= .3f
            && Math.Abs(
                Math.Sin(
                    heading[i])) < .01)
            && Enumerable.Range(
                1,
                3).All(
                    i => Math.Abs(
                        x[i] - x[i - 1] - 7) <= .3f);
    }

    private static bool IsZelessLaser(BattleCharacter b) => b.IsValid
        && b.IsVisible
        && b.BaseId is 14763 or 14765
        && b.Auras.Any(
            a => a.Id == 2397
        && a.Value is 449 or 499);
    // Scope the warning to the captured Well brand; another aura parameter or a
    // same-NPC helper cannot authorize this much larger pre-pull exclusion.
    private static bool IsZelessWellPull(BattleCharacter b) => b.IsValid
        && b.IsVisible
        && b.BaseId == 14765
        && b.NpcId == 11395
        && b.Auras.Any(
            a => a.Id == 2397
        && a.Value == 459);
    // Keep expired snapshots out of every consumer even between cleanup pulses;
    // a later cast from a reused helper replaces its center and deadline by identity.
    private IEnumerable<Forecast> ActiveZelessWellBursts(DateTime now) => _zelessWellBursts.Values.Where(c => c.End > now);
    private bool ZelessHazardsPending() => _zelessHazards.Values.Any(
        h => h.End > DateTime.UtcNow)
        || ActiveZelessWellBursts(
            DateTime.UtcNow).Any(
        )
        || GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => IsZelessLaser(
                b)
        || IsZelessWellPull(
            b))
        || SelectedZelessCones(
        ).Any(
        );
    private void ResetZeless()
    {
        _zelessHazards.Clear();
        _zelessActorPositions.Clear();
        _zelessCones.Clear();
        _zelessWellBursts.Clear();
        _zelessFireLane = null;
        _zelessFireLaneUntil = default;
        _zelessCancelledCast = 0;
        _zelessBombStage = null;
        _zelessWellBurstUntil = default;
    }

    // Shipped shared-group9300701 supplies Y27; the primary encounter reference supplies
    // XZ289,-230 and a17.5-half-width square rotated45degrees. The reward is on a separate
    // Y31 platform beyond the north jumppad, outside this combat-only boundary.
    private static readonly Vector3 ThorneCenter = new(289, 27, -230);
    private readonly Dictionary<uint, Forecast> _thorneCasts = new();
    private Forecast[] _thorneCarriages = Array.Empty<Forecast>();
    private string _thorneCarriageIdentity = string.Empty;
    private const uint ThorneCarriageBase = 14666;
    private const uint ThorneCarriageNpc = 11421;
    private const uint ThorneCarriageAction = 28920;
    // Second capture confirmed the later real28928/28929 telegraphs alongside the
    // harmless28926/28927 feints, plus fireball28925. Override feints without giving
    // them geometry: publishing their shapes would erase the genuine safe sectors.
    private static readonly uint[] ThorneOwnedActions =
    {
        28908,
        28921,
        28922,
        28923,
        28925,
        28926,
        28927,
        28928,
        28929
    };
    private uint _thorneCancelledCast;
    private static bool InThorne() => WorldManager.ZoneId == 1069 && Core.Me.InCombat && !Core.Me.IsDead && Core.Me.Distance2D(ThorneCenter) < 40;
    private void RegisterThorneGeometry()
    {
        AvoidanceHelpers.AddAvoidSquareDonut(
            InThorne,
            2 * ThorneGeometryPlan.ArenaHalfWidth,
            2 * ThorneGeometryPlan.ArenaHalfWidth,
            120,
            120,
            () => new[] { ThorneCenter },
            rotation: (float)Math.PI / 4);
        // Each cast may contribute two arms. Keeping actor/action identity in the
        // detached ledger permits precise removal after future captured impact evidence.
        AvoidanceManager.AddAvoidPolygon<Tuple<Forecast, System.Numerics.Vector2[]>>(
            InThorne,
            null,
            70,
            item => -item.Item1.Heading,
            _ => 1,
            _ => 15,
            item => item.Item2.Select(
                p => new Vector2(
                    p.X,
                    p.Y)).ToArray(
            ),
            item => item.Item1.Location - new Vector3(
                (float)Math.Sin(
                    item.Item1.Heading),
                0,
                (float)Math.Cos(
                    item.Item1.Heading)) * ThorneGeometryPlan.ConeBackshift(
                        item.Item1.Action),
            () => _thorneCasts.Values.Concat(
                _thorneCarriages).SelectMany(
                    c => ThorneGeometryPlan.Polygons(
                        c.Action).Select(
                            p => Tuple.Create(
                                c,
                                p))),
            priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Forecast>(InThorne, c => ThorneGeometryPlan.CircleRadius(c.Action), c => c.Location, SelectedThorneFlares);
    }

    private IEnumerable<Forecast> SelectedThorneFlares() => ThorneGeometryPlan.SelectFlareWarnings(
        _thorneCasts.Values.Where(
            c => c.Action == 28923),
        c => c.End,
        DateTime.UtcNow);
    private void UpdateThorneCasts()
    {
        var now = DateTime.UtcNow;
        UpdateThorneCarriages();
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && ((b.NpcId == 11419
            && b.BaseId is 14615 or 9020)
            || (b.NpcId == 11422
            && b.BaseId == 14729))
            && b.IsCasting
            && ThorneOwnedActions.Contains(
                b.CastingSpellId)
            && b.Distance2D(
                ThorneCenter) < 40))
        {
            _thorneCasts[actor.ObjectId] = new Forecast
            {
                Action = actor.CastingSpellId,
                Location = actor.Location,
                Heading = actor.Heading,
                // Retention follows each action's captured impact delay; later Flare
                // evidence requires a longer fence than the other cast shapes.
                End = now + actor.SpellCastInfo.RemainingCastTime + ThorneGeometryPlan.ImpactRetention(actor.CastingSpellId)
            };
        }

        foreach (var id in _thorneCasts.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
        {
            _thorneCasts.Remove(id);
        }

        if (!Core.Me.IsCasting)
        {
            _thorneCancelledCast = 0;
            return;
        }

        var cast = Core.Me.CastingSpellId;
        // Preserve native movement ownership. A hard cast must not hold the player in
        // a real Flay/Beacon/cross while the same planner already owns the escape.
        // Feints alone cannot authorize interruption because they have no shape.
        if (_thorneCancelledCast == cast
            || !AvoidanceManager.IsRunningOutOfAvoid
            || !(_thorneCarriages.Length != 0
            || _thorneCasts.Values.Any(
                c => ThorneGeometryPlan.Polygons(
                    c.Action).Length > 0)
            || SelectedThorneFlares(
            ).Any(
                c => Core.Me.Distance2D(
                    c.Location) < 10.5f)))
        {
            return;
        }

        _thorneCancelledCast = cast;
        ActionManager.StopCasting();
        if (LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            ff14bot.Helpers.Logging.Write("[SildihnThorne] Cancelled cast for native hazard escape.");
        }
    }

    private void ResetThorne()
    {
        _thorneCasts.Clear();
        _thorneCarriages = Array.Empty<Forecast>();
        _thorneCarriageIdentity = string.Empty;
        _thorneCancelledCast = 0;
    }

    private void UpdateThorneCarriages()
    {
        // Rebuild detached warnings from the current native snapshot, including on
        // mid-fight attachment. Return4, director loss, death and reset release them;
        // a cast-based timer would miss these no-cast volleys or expire before impact.
        var warnings = new List<Forecast>();
        var identities = new List<string>();
        if (InThorne() && DirectorManager.ActiveDirector is ff14bot.Directors.InstanceContentDirector director && director.IsValid)
        {
            var maps = director.MapEffects.ToArray();
            var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(
                ).Where(
                    b => b.IsValid
                && b.IsVisible
                && b.BaseId == ThorneCarriageBase
                && b.NpcId == ThorneCarriageNpc
                && b.Distance2D(
                    ThorneCenter) < 40).ToArray(
                );
            foreach (var group in maps.GroupBy(m => m.ID))
            {
                if (group.Count() != 1)
                {
                    continue; // Ambiguous director transition, never pick an arbitrary duplicate.
                }

                var heading = ThorneGeometryPlan.CarriageHeading(group.Key, (int)group.First().State);
                if (!heading.HasValue)
                {
                    continue;
                }

                var family = actors.Where(b => ThorneGeometryPlan.SameCarriageHeading(b.Heading, heading.Value)).ToArray();
                if (family.Length != 3)
                {
                    continue; // Shipped family and captured volleys both contain exactly three lanes.
                }

                foreach (var actor in family)
                {
                    // Some cannon pivots are below the floor atY24.5. Their damage
                    // crosses theY27 combat plane; no off-floor navigation is requested.
                    warnings.Add(
                        new Forecast
                        {
                            Action = ThorneCarriageAction,
                            Location = new Vector3(
                                actor.Location.X,
                                ThorneCenter.Y,
                                actor.Location.Z),
                            Heading = actor.Heading
                        });
                    identities.Add($"{group.Key}:{actor.ObjectId:X}");
                }
            }
        }

        _thorneCarriages = warnings.ToArray();
        var identity = string.Join(";", identities.OrderBy(s => s));
        if (identity != _thorneCarriageIdentity)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write("[SildihnThorne] Carriage warnings: {0}", identity.Length == 0 ? "released" : identity);
            }

            _thorneCarriageIdentity = identity;
        }
    }

    private readonly CapabilityManagerHandle _boulderShelterHandle = CapabilityManager.CreateNewHandle();
    private bool _boulderShelterOwned;
    private bool _boulderShelterMoving;
    private DateTime _boulderShelterUntil;
    private DateTime _boulderEvidenceLog;
    private Vector3? _boulderShelterPoint;
    private Vector3 _boulderShelterSource;
    private Vector3 _boulderShelterRock;
    private (int Pid, long Start, long Image) _boulderReaderProcess;
    private bool _boulderReaderAttempted;
    private int _boulderAnimationOffset;
    private int BoulderAnimation(BattleCharacter boulder)
    {
        try
        {
            var region = OffsetManager.ActiveRecord.RegionFlag;
            // No CN/KR binary was available. An unknown layout must never look intact.
            if (region != OffsetFlags.Global && region != OffsetFlags.TraditionalChinese)
            {
                return -1;
            }

            var process = Core.Memory.Process;
            var identity = (process.Id, process.StartTime.ToUniversalTime().Ticks, Core.Memory.ImageBase.ToInt64());
            if (_boulderReaderProcess != identity)
            {
                _boulderReaderProcess = identity;
                _boulderReaderAttempted = false;
                _boulderAnimationOffset = 0;
            }

            if (!_boulderReaderAttempted)
            {
                _boulderReaderAttempted = true;
                // Ghidra-validated character-state copy body: one.text match in Global
                // 5BBC501D/04766591/9483706D and TC9FB8DD46. Operand displacements
                // are wildcarded. Extract adjacent source fields, then require identical
                // destination fields. GreyMagic Add 14 is hexadecimal20, not decimal14.
                // Global resolves CF1/CF2; TC CE1/CE2. Never hard-code either layout.
                const string pattern = "Search 0F B6 86 ? ? ? ? 48 8B 4D ? 88 81 ? ? ? ? 0F B6 86 ? ? ? ? 88 81 ? ? ? ?";
                using var finder = new GreyMagicPf();
                var first = finder.FindSingle(pattern + " Add 3 Read32").ToInt32();
                var second = finder.FindSingle(pattern + " Add 14 Read32").ToInt32();
                var firstCopy = finder.FindSingle(pattern + " Add D Read32").ToInt32();
                var secondCopy = finder.FindSingle(pattern + " Add 1A Read32").ToInt32();
                if (first < 0x900 || first > 0x1200 || second != first + 1 || firstCopy != first || secondCopy != second)
                {
                    return -1;
                }

                _boulderAnimationOffset = second;
            }

            if (_boulderAnimationOffset == 0 || !boulder.IsValid || boulder.Pointer == IntPtr.Zero)
            {
                return -1;
            }

            return Core.Memory.Read<byte>(boulder.Pointer + _boulderAnimationOffset);
        }
        catch (Exception)
        {
            return -1;
        } // Unavailable reads are ambiguous, never state0.
    }

    private void UpdateBoulderShelter()
    {
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.Distance2D(GladiatorCenter) < 40).ToArray();
        var rocks = actors.Where(
            b => b.BaseId == 14755
            && b.NpcId == 11392
            && b.IsVisible).Select(
                b => new
            {
                b.ObjectId,
                b.Location,
                State = BoulderAnimation(
                    b)
            }).ToArray(
            );
        var steel = actors.FirstOrDefault(b => b.BaseId == 14751 && b.IsCasting && b.CastingSpellId == 30283);
        // Four visible rocks and the exact0/1 split prevent initial spawn placeholders
        // from selecting the wrong shelter. Disappearance releases the post-impact hold.
        var intact = rocks.Where(b => b.State == 0).ToArray();
        if (rocks.Length != 4 || intact.Length != 1 || rocks.Count(b => b.State == 1) != 3)
        {
            ReleaseBoulderShelter();
            if (steel != null && rocks.Length != 0 && now >= _boulderEvidenceLog)
            {
                _boulderEvidenceLog = now.AddSeconds(2);
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    ff14bot.Helpers.Logging.Write("[Sildihn] Boulder shelter ambiguous: {0}", string.Join(";", rocks.Select(b => $"{b.ObjectId:X}:{b.State}")));
                }
            }

            return;
        }

        if (steel != null)
        {
            _boulderShelterSource = steel.Location;
            _boulderShelterRock = intact[0].Location;
            // 2026-09-30 damage arrived2.16s after RB's reported finish. Keep the
            // hold through rock disappearance, with finish+3s only as a bounded fallback.
            _boulderShelterUntil = now + steel.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3);
        }

        if (now >= _boulderShelterUntil)
        {
            ReleaseBoulderShelter();
            return;
        }

        var point = GladiatorShelterPlan.Destination(BoulderXZ(_boulderShelterSource), BoulderXZ(_boulderShelterRock));
        _boulderShelterPoint = point.HasValue ? new Vector3(GladiatorCenter.X + point.Value.X, _boulderShelterRock.Y, GladiatorCenter.Z + point.Value.Y) : null;
    }

    private bool HandleBoulderShelter()
    {
        if (!_boulderShelterPoint.HasValue)
        {
            ReleaseBoulderShelter();
            return false;
        }

        // The final boulder can still be landing when Steel begins. Never walk into
        // that earlier impact to solve the later shelter, or fight emergency egress.
        if (PendingCasts().Any(c => c.Action == 30288) || AvoidanceManager.IsRunningOutOfAvoid)
        {
            _boulderShelterMoving = false;
            return false;
        }

        var point = _boulderShelterPoint.Value;
        if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(point)))
        {
            ReleaseBoulderShelter();
            return false;
        }

        CapabilityManager.Update(_boulderShelterHandle, CapabilityFlags.Movement, 1000, "Holding the intact-boulder shadow for Shattering Steel");
        _boulderShelterOwned = true;
        if (GladiatorShelterPlan.Protected(BoulderXZ(_boulderShelterSource), BoulderXZ(_boulderShelterRock), BoulderXZ(Core.Me.Location)))
        {
            if (_boulderShelterMoving)
            {
                Navigator.PlayerMover.MoveStop();
            }

            _boulderShelterMoving = false;
            return false; // Keep healing, mitigation and rotation schedulable in shelter.
        }

        if (!_boulderShelterMoving)
        {
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
            {
                ff14bot.Helpers.Logging.Write(
                    "[Sildihn] Intact boulder shelter rock={0} source={1} destination={2}",
                    _boulderShelterRock,
                    _boulderShelterSource,
                    point);
            }
        }

        _boulderShelterMoving = true;
        // Navigator defaults to 5 yalms, wider than the shadow. Use the tested
        // sub-half-yalm arrival tolerance and never attempt mounting in combat.
        var result = Navigator.MoveTo(new MoveToParameters(point) { DistanceTolerance = .45f, UseMount = false });
        if (result is MoveResult.Failed or MoveResult.PathGenerationFailed)
        {
            if (DateTime.UtcNow >= _boulderEvidenceLog)
            {
                _boulderEvidenceLog = DateTime.UtcNow.AddSeconds(2);
                if (LoggingHelpers.MechanicDiagnosticsEnabled)
                {
                    ff14bot.Helpers.Logging.Write("[Sildihn] Intact-boulder shelter navigation failed: {0}", result);
                }
            }

            ReleaseBoulderShelter();
            return false; // A failed path must not starve mitigation/healing.
        }

        return true;
    }

    private void ReleaseBoulderShelter()
    {
        if (_boulderShelterOwned)
        {
            CapabilityManager.Clear(_boulderShelterHandle, CapabilityFlags.Movement, "Boulder shelter ended or became ambiguous");
        }

        if (_boulderShelterMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        _boulderShelterOwned = _boulderShelterMoving = false;
        _boulderShelterPoint = null;
        _boulderShelterUntil = default;
    }

    private static V2 BoulderXZ(Vector3 p) => new(p.X - GladiatorCenter.X, p.Z - GladiatorCenter.Z);
}

// Detached XZ planner for the captured16-yalm radial and 35-yalm directional pushes.
// Both the start and landing stay inside the reduced floor's19-yalm half-width, and
// the entire forced trajectory clears radius 5 water by 0.5. No native pointer is retained.
internal static class SilkieKnockbackPlan
{
    internal static System.Numerics.Vector2? Landing(System.Numerics.Vector2 start, System.Numerics.Vector2 source, float heading, bool directional)
    {
        var delta = directional ? new System.Numerics.Vector2((float)Math.Sin(heading), (float)Math.Cos(heading)) : start - source;
        if (delta.LengthSquared() < .25f)
        {
            return null; // Radial direction at the source is undefined.
        }

        return start + System.Numerics.Vector2.Normalize(delta) * (directional ? 35 : 16);
    }

    internal static bool Safe(System.Numerics.Vector2 start, System.Numerics.Vector2 source, float heading, bool directional, IReadOnlyList<System.Numerics.Vector2> pools, Func<System.Numerics.Vector2, bool> unsafePoint)
    {
        var landing = Landing(start, source, heading, directional);
        if (!Inside(start) || !landing.HasValue || !Inside(landing.Value) || unsafePoint(start) || unsafePoint(landing.Value))
        {
            return false;
        }

        var travel = landing.Value - start;
        foreach (var pool in pools)
        {
            var t = Math.Clamp(System.Numerics.Vector2.Dot(pool - start, travel) / travel.LengthSquared(), 0, 1);
            if (System.Numerics.Vector2.DistanceSquared(pool, start + travel * t) <= 5.5f * 5.5f)
            {
                return false;
            }
        }

        return true;
    }

    internal static System.Numerics.Vector2? Choose(System.Numerics.Vector2 player, System.Numerics.Vector2? previous, System.Numerics.Vector2 source, float heading, bool directional, IReadOnlyList<System.Numerics.Vector2> pools, Func<System.Numerics.Vector2, bool> unsafePoint)
    {
        // Preserve a still-valid destination across frame noise. The grid is deterministic
        // and dense enough for Wash Out's three-yalm staging strip; prefer balanced wall
        // clearance, then shorter travel. Failed planning must not invent a fallback point.
        if (previous.HasValue && Safe(previous.Value, source, heading, directional, pools, unsafePoint))
        {
            return previous;
        }

        System.Numerics.Vector2? best = null;
        var score = float.MinValue;
        for (var x = -19; x <= 19; x++)
        {
            for (var z = -19; z <= 19; z++)
            {
                var candidate = new System.Numerics.Vector2(x, z);
                if (!Safe(candidate, source, heading, directional, pools, unsafePoint))
                {
                    continue;
                }

                var landing = Landing(candidate, source, heading, directional).Value;
                var margin = Math.Min(Margin(candidate), Margin(landing));
                var value = margin * 10 - System.Numerics.Vector2.Distance(player, candidate);
                if (value <= score)
                {
                    continue;
                }

                score = value;
                best = candidate;
            }
        }

        return best;
    }

    private static bool Inside(System.Numerics.Vector2 p) => Margin(p) >= 0;
    private static float Margin(System.Numerics.Vector2 p) => 19 - Math.Max(Math.Abs(p.X), Math.Abs(p.Y));
}

// Detached geometry for the captured18-yalm radial push into a future blue keg.
// Coordinates are relative to the actual branch arena, keeping this independent of RB.
internal static class GeryonRunoffPlan
{
    internal static System.Numerics.Vector2? Stage(System.Numerics.Vector2 source, System.Numerics.Vector2 landing)
    {
        var delta = landing - source;
        var distance = delta.Length();
        // A start too close to the source amplifies tiny movement error into a missed
        // three-yalm keg hole. Require five yalms before the push, and fail explicitly
        // when the intended keg cannot provide that rather than guessing a wall collision.
        if (!float.IsFinite(distance) || distance < 23 || !Inside(landing))
        {
            return null;
        }

        var start = landing - delta / distance * 18;
        return Inside(start) ? start : null;
    }

    private static bool Inside(System.Numerics.Vector2 p) => Math.Abs(p.X) <= 18.5f && Math.Abs(p.Y) <= 18.5f;
}

// Native right-Geryon quadrants have overlapping warnings but sequential impacts. Keeping
// this ledger detached from RB permits recovery/order tests without retaining game wrappers.
internal sealed class SildihnRightSewageSequence
{
    // Captures 2026-09-28:4 idle,1 warning,16 armed, then4 after impact. Pair warnings
    // began1.88–2.48s apart and resolved~8s after warning. The500ms grouping window absorbs
    // packet/frame separation within a pair while preserving the measured inter-pair gap.
    private readonly DateTime?[] _warnings = new DateTime?[4];
    private bool _armed;
    internal int PendingCount => _warnings.Count(t => t.HasValue);

    internal void Reset()
    {
        Array.Clear(_warnings, 0, _warnings.Length);
        _armed = false;
    }

    internal void Update(IReadOnlyList<int> states, DateTime now)
    {
        if (states == null || states.Count != 4 || states.Any(s => s != 1 && s != 4 && s != 16))
        {
            Reset();
            return;
        }

        if (states.All(s => s == 4))
        {
            Array.Clear(_warnings, 0, _warnings.Length);
            _armed = true;
            return;
        }

        // A mid-sequence load has no trustworthy pair order. Wait for a complete idle
        // frame instead of publishing four contradictory future quadrants at once.
        if (!_armed)
        {
            return;
        }

        for (var index = 0; index < 4; ++index)
        {
            if (states[index] == 4)
            {
                _warnings[index] = null;
            }
            else if (!_warnings[index].HasValue)
            {
                if (states[index] != 1)
                {
                    Reset();
                    return;
                }

                _warnings[index] = now;
            }
        }

        // Twelve seconds exceeds both observed warning-to-impact intervals. Unknown
        // persistent states must expire the forecast instead of trapping travel forever.
        if (_warnings.Any(t => t.HasValue && now - t.Value > TimeSpan.FromSeconds(12)))
        {
            Reset();
        }
    }

    internal int[] EarliestPair()
    {
        if (!_armed || PendingCount == 0)
        {
            return Array.Empty<int>();
        }

        var first = _warnings.Where(t => t.HasValue).Min(t => t.Value);
        var pair = Enumerable.Range(0, 4).Where(i => _warnings[i].HasValue && _warnings[i].Value <= first.AddMilliseconds(500)).ToArray();
        // A delayed sampling frame can merge both pairs. Unknown order must not publish all four hazards.
        return pair.Length <= 2 ? pair : Array.Empty<int>();
    }
}

// Geometry consumed by the 2026-09-29 captured Thorne cast handler.
// Sacred Flay's45-degree half-angle means a90-degree sector; interpreting it as the
// full angle would leave half the dangerous region unprotected. These detached
// shapes contain no actor/map activation guesses and cannot move the player themselves.
internal static class ThorneGeometryPlan
{
    // Reuse the encounter handlers'0.5-yalm edge allowance. This is clearance, not a
    // claim about additional damage reach. The caller supplies captured cast origins,
    // headings and deadlines. Artillery requires its observed native warning;
    // puppet activation remains unverified and cannot obtain geometry here.
    internal const float Clearance = .5f;
    internal const float ArenaHalfWidth = 17.5f - Clearance;
    // Four shipped copies of sgbg_w5d1_bb_gmc01 rotate the same three cannon
    // lanes. These are layout IDs, never network map-effect indices. Full Euler
    // transforms yield headings45/315/135/225 degrees; 2026-09-30 hits corroborate
    // 9316108 at (271.5,-243.5),heading 45 and 9316111 at (298,-208),heading 225.
    // State1 warns before impact; captured return4 follows impact. Unknown states
    // or layouts cannot authorize an arbitrary lane family.
    internal static float? CarriageHeading(uint layout, int state) => state != 1 ? null : layout switch
    {
        9316108 => (float)Math.PI / 4,
        9316109 => 7 * (float)Math.PI / 4,
        9316110 => 3 * (float)Math.PI / 4,
        9316111 => 5 * (float)Math.PI / 4,
        _ => null
    };
    // Actual actors supply origins and headings. The small angular tolerance only
    // accommodates observed float rounding, not an inferred cone of valid sources.
    internal static bool SameCarriageHeading(float actual, float expected) => float.IsFinite(
        actual)
        && Math.Abs(
            Math.IEEERemainder(
                actual - expected,
                2 * Math.PI)) < .01;
    // 2026-09-30 Signal Flare damage was sampled864ms after reported cast finish,
    // with a fresh60000ms status. Keep the circle for 1s so a cleared wave cannot be
    // re-entered before impact; other captured shapes retain their existing600ms fence.
    internal static TimeSpan ImpactRetention(uint action) => TimeSpan.FromMilliseconds(action == 28923 ? 1000 : 600);
    // 2026-09-29 three groups of three arrive1.54s apart. Publishing all nine covers
    // the floor; the first six leave the last group as temporary safe space, then hand
    // off to the vacated first group. Expiry includes the caller's captured damage delay.
    internal static IEnumerable<T> SelectFlareWarnings<T>(IEnumerable<T> warnings, Func<T, DateTime> end, DateTime now) => warnings.Where(
        w => end(
            w) > now).OrderBy(
                end).Take(
                    6);
    internal static float ConeDegrees(uint action) => action switch
    {
        28908 => 180, // Fore Honor is the front half-plane.
        28922 or 28929 => 90, // Real Sacred Flay; fake28927 must never own avoidance.
        _ => 0
    };
    internal static float ConeBackshift(uint action) => ConeDegrees(
        action) is var degrees
        && degrees > 0 ? Clearance / (float)Math.Sin(
            degrees * Math.PI / 360) : 0;
    internal static System.Numerics.Vector2[][] Polygons(uint action)
    {
        if (action == 28920)
        {
            return new[]
            {
                Rectangle(3, 45)
            }; // Native-warning-owned carriage,45x6.
        }

        if (action is 28921 or 28928)
        {
            return new[]
            {
                Rectangle(8, 50)
            };
        }

        if (action == 28925)
        {
            return new[]
            {
                CrossArm(),
                RotateQuarterTurn(CrossArm())
            };
        }

        var degrees = ConeDegrees(action);
        if (degrees == 0)
        {
            return Array.Empty<System.Numerics.Vector2[]>();
        }

        // Shift the world origin back by ConeBackshift when consuming this local sector.
        // Extra reach compensates that translation, preserving the true50-yalm far edge.
        var points = new System.Numerics.Vector2[66];
        var radius = 50 + Clearance + ConeBackshift(action);
        for (var i = 0; i <= 64; i++)
        {
            var angle = (degrees / 2 - degrees * i / 64) * Math.PI / 180;
            points[i + 1] = new System.Numerics.Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius);
        }

        return new[]
        {
            points
        };
    }

    // Signal Flare's staggered cast groups require live deadline ordering before any
    // caller publishes all circles. Neither raidwide28907 nor fake28926 gets a radius.
    internal static float CircleRadius(uint action) => action == 28923 ? 10 + Clearance : 0;
    private static System.Numerics.Vector2[] Rectangle(float halfWidth, float length) => new[]
    {
        new System.Numerics.Vector2(-halfWidth - Clearance, -Clearance),
        new System.Numerics.Vector2(halfWidth + Clearance, -Clearance),
        new System.Numerics.Vector2(halfWidth + Clearance, length + Clearance),
        new System.Numerics.Vector2(-halfWidth - Clearance, length + Clearance)
    };
    private static System.Numerics.Vector2[] CrossArm() => new[]
    {
        new System.Numerics.Vector2(-3 - Clearance, -50 - Clearance),
        new System.Numerics.Vector2(3 + Clearance, -50 - Clearance),
        new System.Numerics.Vector2(3 + Clearance, 50 + Clearance),
        new System.Numerics.Vector2(-3 - Clearance, 50 + Clearance)
    };
    private static System.Numerics.Vector2[] RotateQuarterTurn(System.Numerics.Vector2[] points)
    {
        var rotated = new System.Numerics.Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            rotated[i] = new System.Numerics.Vector2(-points[i].Y, points[i].X);
        }

        return rotated;
    }
}

// Shattering Steel is blocked by the intact1.8-yalm boulder. Work in arena-local
// X/Z coordinates; shrink its shadow by 0.5 and preserve the existing19-yalm floor
// inset. A point behind a different rock or in front of this rock is never shelter.
internal static class GladiatorShelterPlan
{
    internal static System.Numerics.Vector2? Destination(System.Numerics.Vector2 source, System.Numerics.Vector2 boulder)
    {
        var direction = boulder - source;
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.LengthSquared() < 9)
        {
            return null;
        }

        // 2.8 clears the physical1.8 radius, default0.5 margin and 0.5 arrival tolerance.
        var point = boulder + System.Numerics.Vector2.Normalize(direction) * 2.8f;
        return Math.Abs(point.X) <= 19 && Math.Abs(point.Y) <= 19 && Protected(source, boulder, point) ? point : null;
    }

    internal static bool Protected(System.Numerics.Vector2 source, System.Numerics.Vector2 boulder, System.Numerics.Vector2 player)
    {
        var ray = player - source;
        var length = ray.LengthSquared();
        if (!float.IsFinite(length) || length < 1)
        {
            return false;
        }

        var t = System.Numerics.Vector2.Dot(boulder - source, ray) / length;
        return t > 0
            && t < 1
            && System.Numerics.Vector2.DistanceSquared(
                boulder,
                source + ray * t) <= 1.3f * 1.3f
            && System.Numerics.Vector2.DistanceSquared(
                player,
                boulder) >= 2.3f * 2.3f;
    }
}
