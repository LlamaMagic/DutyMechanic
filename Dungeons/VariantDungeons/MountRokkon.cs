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
/// Supplies captured solo Variant mechanics for Mount Rokkon's left route. OrderBot owns
/// targeting, route progression and rewards; the combat routine retains job actions.
/// Only encounter-owned actions replace generic avoidance, preserving trash handling.
/// </summary>
public sealed class MountRokkon : AbstractDungeon
{
    // 2026-09-27 capture identifies the actual boss separately from same-name Base 9020
    // helpers. Only territory 1137 is Variant; neither Criterion difficulty is registered.
    private const uint YozakuraBase = 0x3EDB;
    private const uint SealFire = 33653;
    private const uint SealWind = 33654;
    private const uint SealRain = 33655;
    private const uint SealLightning = 33656;
    private const uint MudPie = 33678;
    private const uint MudPuddleBase = 0x1EB907;
    private const uint Clearout = 34220;
    private const uint ScarletExplosion = 34203;
    private static readonly Vector3 YozakuraCenter = new(-775, 41, 16);
    private static readonly Vector3 MokoCenter = new(-700, -20, 540);
    private static readonly uint[] OwnedActions =
    {
        SealFire,
        SealWind,
        SealRain,
        SealLightning,
        MudPie,
        Clearout,
        ScarletExplosion
    };
    private readonly Dictionary<uint, Impact> _impacts = new();
    private Impact _giri;
    private Impact _nextGiri;
    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)1137;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new()
    {
        33705
    }; // Glory Neverlasting, captured targeted buster.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = new()
    {
        33646,
        33706,
        34221,
        34219
    }; // Oka Ranman / Kuge Rantsui / Kenki Release / Moonless Night.

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        _impacts.Clear();
        foreach (var action in OwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        // Single, first-double, and second-double variants each encode back/left/front/right
        // safety in four consecutive rows. Generic omen15 followed the initial facing and
        // missed right34186 and front34185 in the 01:14 baseline; own all directional rows.
        for (uint action = 34183; action <= 34194; action++)
        {
            LlamaLibrary.Helpers.SideStep.Override(action);
        }

        // The left floor shrinks to a40-yalm square. A0.5 inset preserves its usable corners
        // and also remains safe during the larger opening floor; this is not a circular arena.
        AvoidanceHelpers.AddAvoidSquareDonut(InYozakura, 39, 39, 140, 140, () => new[] { YozakuraCenter });
        // Live raw type 13 exposed four helpers but SideStep published no cones. Each helper
        // owns one45-degree sector; actor origin/heading are valid while CastLocation is zero.
        // Move the cone vertex back by 0.5/sin(22.5deg), padding both side edges by 0.5 yalm.
        var sidePad = .5f / (float)Math.Sin(Math.PI / 8);
        AvoidanceManager.AddAvoidPolygon<Impact>(
            InYozakura,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => Cone45(
                sidePad),
            c => c.Location - new Vector3(
                (float)Math.Sin(
                    c.Heading) * sidePad,
                0,
                (float)Math.Cos(
                    c.Heading) * sidePad),
            () => SealWave(
            ).Where(
                c => c.Action is SealRain or SealLightning),
            priority: AvoidancePriority.High);
        // Paired circle/donut and cones share the same earliest-impact wave. Never combine
        // the later inverse elemental pair, which would erase every valid destination.
        AvoidanceManager.AddAvoidLocation<Impact>(InYozakura, _ => 9.5f, c => c.Location, () => SealWave().Where(c => c.Action == SealFire));
        AvoidanceHelpers.AddAvoidDonut(InYozakura, () => SealWave().Where(c => c.Action == SealWind).Select(c => c.Location).ToArray(), 60.5, 4.5);
        // The rainy baseline gained two vulnerabilities after generic lines disappeared at
        // cast end (01:02:15 UTC). Keep all eight near-simultaneous lines through the server
        // impact fence; width 6 and length 60 each receive a0.5-yalm edge allowance.
        AvoidanceManager.AddAvoidPolygon<Impact>(
            InYozakura,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => new[] { new Vector2(
                -3.5f,
                -.5f), new Vector2(
                    3.5f,
                    -.5f), new Vector2(
                        3.5f,
                        60.5f), new Vector2(
                            -3.5f,
                            60.5f) },
            c => c.Location,
            () => PendingImpacts(
            ).Where(
                c => c.Action == MudPie),
            priority: AvoidancePriority.High);
        // Captured mud event objects remain visible after their placement casts and turn
        // invisible as bubbles take over. Use that supported lifecycle rather than an assumed
        // native EventState offset. Icebloom's correctly decoded circles remain with SideStep.
        AvoidanceManager.AddAvoidLocation<GameObject>(
            InYozakura,
            _ => 5.5f,
            o => o.Location,
            () => GameObjectManager.GameObjects.Where(
                o => o.IsValid
            && o.IsVisible
            && o.BaseId == MudPuddleBase));
        AvoidanceHelpers.AddAvoidSquareDonut(InMoko, 39, 39, 140, 140, () => new[] { MokoCenter });
        // Moko's final facing occurs after the snapshot. Derive the 270-degree cleave from
        // the pre-impact actor heading and the action's directional variant, retaining it
        // through the observed damage delay. A0.5 side allowance protects both safe-wedge edges.
        var giriPad = .5f / (float)Math.Sin(135 * Math.PI / 180);
        AvoidanceManager.AddAvoidPolygon<Impact>(
            InMoko,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => Sector(
                270,
                60.5f + giriPad),
            c => c.Location - new Vector3(
                (float)Math.Sin(
                    c.Heading) * giriPad,
                0,
                (float)Math.Cos(
                    c.Heading) * giriPad),
            () => ActiveGiri(
            ),
            priority: AvoidancePriority.High);
        // Both claw pairs hit the middle on 2026-09-27 despite actor-centered generic
        // cones leaving it clear. Captured omen centers sit five yalms inward from the actor;
        // the 22-yalm half-disc must originate there. A0.5 margin and 1.5-second impact fence
        // cover the observed server damage about 1.3 seconds after RB's cast disappeared.
        AvoidanceManager.AddAvoidPolygon<Impact>(
            InMoko,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => Sector(
                180,
                23),
            c => c.Location - Forward(
                c.Heading) * .5f,
            () => PendingImpacts(
            ).Where(
                c => c.Action == Clearout),
            priority: AvoidancePriority.High);
        // The three Scarlet lines struck at five-second intervals (01:26:36/41/46).
        // Their helpers stand at the line midpoint; the wide explosion extends across both
        // sides of the arena. Publish only the earliest unresolved30-wide lane, padded0.5.
        AvoidanceManager.AddAvoidPolygon<Impact>(
            InMoko,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            _ => Rectangle(
                15.5f,
                30.5f,
                30.5f),
            c => c.Location,
            () => ScarletWave(
            ),
            priority: AvoidancePriority.High);
        // Spear damage has no cast. Captured live actors travel at about 6/4 yalms per second
        // and caused a hit at 01:27:33. Track their visible moving bodies with the corroborated
        // four-wide footprint and one0.6-second pulse of forward lookahead, plus 0.5margin.
        // Do not reserve their entire future lane: the staggered speeds create the escape gaps.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            InMoko,
            null,
            80,
            c => -c.Heading,
            _ => 1,
            _ => 15,
            c => Rectangle(
                2.5f,
                c.BaseId == 0x3F82 ? 1f : .85f,
                c.BaseId == 0x3F82 ? 7f : 5f),
            c => c.Location,
            () => GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                c => c.IsValid
            && c.IsVisible
            && (c.BaseId == 0x3F82
            || c.BaseId == 0x3F83)
            && c.Distance2D(
                MokoCenter) < 32),
            priority: AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        _impacts.Clear();
        _giri = _nextGiri = null;
        foreach (var action in OwnedActions)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        for (uint action = 34183; action <= 34194; action++)
        {
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (InMoko() && !Core.Me.IsDead)
        {
            UpdateGiri();
            UpdateImpacts(MokoCenter);
            return await DamageMitigationSpells();
        }

        _giri = _nextGiri = null;
        if (!InYozakura() || Core.Me.IsDead)
        {
            _impacts.Clear();
            return false;
        }

        UpdateImpacts(YozakuraCenter);
        if (await TankBusterSpells())
        {
            return true;
        }

        return await DamageMitigationSpells();
    }

    private void UpdateImpacts(Vector3 center)
    {
        var now = DateTime.UtcNow;
        foreach (var key in _impacts.Keys.Where(k => _impacts[k].End <= now).ToArray())
        {
            _impacts.Remove(key);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(
            ).Where(
                b => b.IsValid
            && b.IsCasting
            && b.Distance2D(
                center) < 75
            && OwnedActions.Contains(
                b.CastingSpellId)))
        {
            // RB's1.7-second report belongs to a2-second action. Retain the detached shape
            // through the missing300ms so routine movement cannot leave safety before impact.
            // Keep a stable managed identity during a cast; replacing it every tick makes
            // RB discard the current avoid object and repeatedly announce the same escape.
            if (!_impacts.TryGetValue(actor.ObjectId, out var impact) || impact.Action != actor.CastingSpellId)
            {
                _impacts[actor.ObjectId] = impact = new Impact();
            }

            impact.Action = actor.CastingSpellId;
            impact.Location = impact.Action == Clearout ? actor.OmenMatrix.Center : actor.Location;
            impact.Heading = actor.Heading;
            impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(impact.Action == Clearout ? 1500 : 350);
        }
    }

    private static bool InYozakura() => WorldManager.ZoneId == 1137
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            YozakuraCenter) < 45
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.BaseId == YozakuraBase
        && b.IsAlive);
    private static bool InMoko() => WorldManager.ZoneId == 1137
        && Core.Me.InCombat
        && Core.Me.Distance2D(
            MokoCenter) < 45
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(
        ).Any(
            b => b.BaseId == 0x3FB3
        && b.IsAlive);
    private void UpdateGiri()
    {
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.BaseId == 0x3FB3 && b.IsCasting);
        if (boss == null || boss.CastingSpellId < 34183 || boss.CastingSpellId > 34194)
        {
            return;
        }

        // The01:14 right-safe cast began facing pi, then turned to 3pi/2 after damage;
        // front-safe began at 0.04 and turned to 3.18. Recomputing from the live pre-impact
        // heading also lets the opening facing animation settle before the five-second hit.
        var direction = (boss.CastingSpellId - 34183) % 4;
        var turn = direction == 1 ? -(float)Math.PI / 2 : direction == 2 ? (float)Math.PI : direction == 3 ? (float)Math.PI / 2 : 0;
        if (_giri == null || _giri.Action != boss.CastingSpellId || _giri.End <= DateTime.UtcNow)
        {
            _giri = new Impact();
        }

        _giri.Action = boss.CastingSpellId;
        _giri.Location = boss.Location;
        _giri.Heading = boss.Heading + turn;
        _giri.End = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(750);
        if (boss.CastingSpellId is >= 34187 and <= 34190 && boss.SpellCastInfo.RemainingCastTime.TotalSeconds < 5.5)
        {
            // Both captured doubles replaced aura2970's first direction around six seconds
            // into the 11-second cast. The second marker predicts the follow-up3.5 seconds
            // after the first, relative to its resulting facing. Read only this later window
            // so the first marker cannot masquerade as a second instruction.
            var marker = boss.CharacterAuras.FirstOrDefault(a => a.Id == 2970);
            if (marker != null && marker.Value >= 0x248 && marker.Value <= 0x24B)
            {
                var next = marker.Value - 0x248;
                var nextTurn = next == 1 ? -(float)Math.PI / 2 : next == 2 ? (float)Math.PI : next == 3 ? (float)Math.PI / 2 : 0;
                _nextGiri = new Impact
                {
                    Action = (uint)(34191 + next),
                    Location = _giri.Location,
                    Heading = _giri.Heading + nextTurn,
                    End = _giri.End.AddSeconds(3.5)
                };
            }
        }
        else if (boss.CastingSpellId is >= 34191 and <= 34194)
        {
            _nextGiri = null;
        }
    }

    private IEnumerable<Impact> ActiveGiri()
    {
        // One owner exposes only the earliest unresolved cleave. Showing both opposing
        // 270-degree sectors simultaneously leaves no safe floor and reverses the intended order.
        if (_giri != null && _giri.End > DateTime.UtcNow)
        {
            yield return _giri;
        }
        else if (_nextGiri != null && _nextGiri.End > DateTime.UtcNow)
        {
            yield return _nextGiri;
        }
    }

    private IEnumerable<Impact> SealWave()
    {
        var pending = PendingImpacts().Where(c => c.Action is SealFire or SealWind or SealRain or SealLightning).OrderBy(c => c.End).ToArray();
        if (pending.Length == 0)
        {
            return Array.Empty<Impact>();
        }

        // Lightning has a0.2-second shorter cast than its paired circle/donut. This tolerance
        // joins that single resolving pair while excluding the next pair about 8 seconds later.
        return pending.Where(c => c.End <= pending[0].End.AddMilliseconds(350));
    }

    private static Vector2[] Cone45(float sidePad) => Sector(45, 70.5f + sidePad);
    private IEnumerable<Impact> ScarletWave()
    {
        var pending = PendingImpacts().Where(c => c.Action == ScarletExplosion).OrderBy(c => c.End).ToArray();
        return pending.Length == 0 ? Array.Empty<Impact>() : pending.Where(c => c.End <= pending[0].End.AddMilliseconds(350));
    }

    // Native avoid providers can pulse while RunAsync yields for mitigation. Expire at
    // selection time too, so a completed lane cannot hide the next resolving wave.
    private IEnumerable<Impact> PendingImpacts() => _impacts.Values.Where(c => c.End > DateTime.UtcNow);
    private static Vector3 Forward(float heading) => new((float)Math.Sin(heading), 0, (float)Math.Cos(heading));
    private static Vector2[] Rectangle(float halfWidth, float back, float front) => new[]
    {
        new Vector2(-halfWidth, -back),
        new Vector2(halfWidth, -back),
        new Vector2(halfWidth, front),
        new Vector2(-halfWidth, front)
    };
    private static Vector2[] Sector(float degrees, float radius)
    {
        var points = new List<Vector2>
        {
            Vector2.Zero
        };
        for (var i = 0; i <= 64; i++)
        {
            var angle = (degrees / 2 - degrees * i / 64) * Math.PI / 180;
            points.Add(new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius));
        }

        return points.ToArray();
    }

    // Frame-independent predictions expire on impact or reset; never retain native wrappers.
    private sealed class Impact
    {
        internal uint Action;
        internal Vector3 Location;
        internal float Heading;
        internal DateTime End;
    }
}
