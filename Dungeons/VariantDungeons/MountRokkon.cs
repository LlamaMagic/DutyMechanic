using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
/// <summary>
/// Supplies solo Variant mechanics for Mount Rokkon's route arenas. OrderBot owns
/// targeting, route progression and rewards; the combat routine retains job actions.
/// Only encounter-owned actions replace generic avoidance, preserving trash handling.
/// </summary>
public sealed partial class MountRokkon : AbstractDungeon
{
    // September 27 capture identifies the actual boss separately from same-name Base 9020
    // helpers. Only territory 1137 is Variant; neither Criterion difficulty is registered.
    private const uint YozakuraBase = 0x3EDB;
    // Katana/statue routes use the full-size actor; the untouched-case route uses
    // another base row. Both own the same directional cleaves. Match BaseId and
    // arena rather than the shared NPC/display name also carried by helpers.
    private const uint MokoBase = 0x3F81;
    private const uint MokoOpenCaseBase = 0x3FB3;
    private const uint SealFire = 33653;
    private const uint SealWind = 33654;
    private const uint SealRain = 33655;
    private const uint SealLightning = 33656;
    private const uint MudPie = 33678;
    private const uint Icebloom = 33676;
    private const uint MudPuddleBase = 0x1EB907;
    private const uint Clearout = 34220;
    private const uint ScarletExplosion = 34203;
    private const uint ArtOfTheWindblossom = 33641;
    private const uint WindblossomWhirl = 33680;
    private const uint WindblossomWhirlFollowup = 34544;
    private const uint LevinblossomStrike = 33682;
    private const uint DriftingPetals = 33683;
    private const uint TatamiGaeshi = 33686;
    private const uint SpearmanOrdersPreview = 34198; // Harmless full-row preview; moving spear footprints own damage.
    // The same boss/action identities occur in three separate arenas. Resolve the
    // arena from the player's region, never from the boss's moving combat position.
    // Middle starts with a smaller floor; applying the left boundary there is unsafe.
    private static readonly Vector3 YozakuraLeftCenter = new(-775, 41, 16);
    private static readonly Vector3 YozakuraMiddleCenter = new(737, 46, 220);
    private static readonly Vector3 YozakuraRightCenter = new(47, 309, 93);
    private static Vector3 YozakuraCenter => Core.Me.Location.X < -500 ? YozakuraLeftCenter : Core.Me.Location.X > 500 ? YozakuraMiddleCenter : YozakuraRightCenter;

    private static readonly Vector3 MokoCenter = new(-700, -20, 540);
    private static readonly uint[] OwnedActions =
    {
        // Remaining ordinary telegraphs move with the boss-wide avoidance owner.
        33640,
        33664,
        33674,
        34196,
        34217,
        34218,
        34200,
        34210,
        34212,
        34214,
        34012,
        34021,
        34039,
        SealFire,
        SealWind,
        SealRain,
        SealLightning,
        MudPie,
        Icebloom,
        Clearout,
        ScarletExplosion,
        ArtOfTheWindblossom,
        WindblossomWhirl,
        WindblossomWhirlFollowup,
        LevinblossomStrike,
        DriftingPetals,
        SpearmanOrdersPreview,
        AzureAuspice,
        BoundlessAzure,
        BoundlessScarlet,
        UpwellFirst,
        UpwellRest,
        TatamiGaeshi,
        LanceFirst,
        LanceRest,
        34023,
        34024,
        34026,
        34027,
        34028,
        34035,
        34042,
        34043,
        34044,
        33693,
        33694,
        33696,
        32840,
        32841,
        32842,
        32843,
        32848,
        32850,
        32851,
        32855,
        32856,
        33758,
        33759,
        33760,
        33762,
        33763,
        33764,
        33765,
        34809,
        34811,
        33766,
        33769,
        33771,
        33772,
        33773,
        33774,
        33775,
        33776,
        33777,
        33780,
        34604,
        33778,
        33701,
        33702,
        33757,
        34725,
        34726,
        34732
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
        33705,
        34046,
        32854,
        33782
    }; // Also Splitting Cry: mitigate the assigned solo target rather than evade its facing.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = new()
    {
        33646,
        33706,
        34221,
        34219,
        34048,
        34030,
        34041,
        34045,
        32834,
        32852,
        33781
    }; // Includes Enenra's solo tether and Shishio's Enkyo; movement cannot avoid this assigned damage.

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        _impacts.Clear();
        RegisterSeasons();
        RegisterAzure();
        RegisterSpiritflames();
        RegisterGorai();
        RegisterGoraiPrayer();
        RegisterWorldlyPursuit();
        RegisterFluff();
        RegisterLance();
        RegisterEnenra();
        RegisterEnenraSmoke();
        RegisterRightYozakura();
        RegisterShishio();
        RegisterBossAvoidance();
        // Left/right shrink to a 40-yalm square; middle is 39 from the start.
        // A 0.5 inset on each edge preserves corners through the floor transition.
        AvoidanceHelpers.AddAvoidSquareDonut(() => InYozakura() && Core.Me.Location.X < 500, 39, 39, 140, 140, () => new[] { YozakuraCenter });
        AvoidanceHelpers.AddAvoidSquareDonut(() => InYozakura() && Core.Me.Location.X > 500, 38, 38, 140, 140, () => new[] { YozakuraCenter });
        // Live raw type 13 exposed four helpers but SideStep published no cones. Each helper
        // owns one 45-degree sector; actor origin/heading are valid while CastLocation is zero.
        // Move the cone vertex back by 0.5/sin(22.5deg), padding both side edges by 0.5 yalm.
        var sidePad = .5f / (float)Math.Sin(Math.PI / 8);
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Cone45(sidePad), c => c.Location - new Vector3((float)Math.Sin(c.Heading) * sidePad, 0, (float)Math.Cos(c.Heading) * sidePad), () => SealWave().Where(c => c.Action is SealRain or SealLightning), priority: AvoidancePriority.High);
        // Paired circle/donut and cones share the same earliest-impact wave. Never combine
        // the later inverse elemental pair, which would erase every valid destination.
        AvoidanceManager.AddAvoidLocation<Impact>(InYozakura, _ => 9.5f, c => c.Location, () => SealWave().Where(c => c.Action == SealFire));
        AvoidanceHelpers.AddAvoidDonut(InYozakura, () => SealWave().Where(c => c.Action == SealWind).Select(c => c.Location).ToArray(), 60.5, 4.5);
        // October 1: generic omen 247 left the player six yalms from the boss and
        // Art of the Windblossom hit. Its real safe hole is five yalms, without
        // adding the boss hitbox. Wind's four overlapping three-yalm circles leave
        // narrow diagonal pockets: quarter-yalm padding preserves those pockets.
        AvoidanceHelpers.AddAvoidDonut(InYozakura, () => PendingImpacts().Where(c => c.Action == ArtOfTheWindblossom).Select(c => c.Location).ToArray(), 60.5, 4.5);
        AvoidanceHelpers.AddAvoidDonut(InYozakura, () => PendingImpacts().Where(c => c.Action is WindblossomWhirl or WindblossomWhirlFollowup).Select(c => c.Location).ToArray(), 60.5, 4.75);
        AvoidanceManager.AddAvoidLocation<Impact>(InYozakura, _ => 3.25f, c => c.Location, () => PendingImpacts().Where(c => c.Action == LevinblossomStrike));
        // Drifting Petals is a 15-yalm knockback, not a 60-yalm damage circle.
        // Its last second follows the preceding circles: stage within 4.5 yalms
        // for an in-bounds landing, then let the next donut draw us back inward.
        AvoidanceHelpers.AddAvoidDonut(InYozakura, // The sentinel branch also needs time to choose a mud-free landing;
        // its moving wind cannot be solved by entering the center at the last instant.
        () => PendingImpacts().Where(c => c.Action == DriftingPetals && !_petalsOwned && (InRightYozakura() || c.End <= DateTime.UtcNow.AddSeconds(1.5))).Select(c => c.Location).ToArray(), 60.5, 4.5);
        // The rainy baseline gained two vulnerabilities after generic lines disappeared at
        // cast end (01:02:15 UTC). Keep all eight near-simultaneous lines through the server
        // impact fence; width 6 and length 60 each receive a 0.5-yalm edge allowance.
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => new[] { new Vector2(-3.5f, -.5f), new Vector2(3.5f, -.5f), new Vector2(3.5f, 60.5f), new Vector2(-3.5f, 60.5f) }, c => c.Location, () => PendingImpacts().Where(c => c.Action == MudPie), priority: AvoidancePriority.High);
        // Captured mud event objects remain visible after their placement casts and turn
        // invisible as bubbles take over. Use that supported lifecycle rather than an assumed
        // native EventState offset.
        AvoidanceManager.AddAvoidLocation<GameObject>(() => InYozakura() && !_petalsOwned, _ => 5.5f, o => o.Location, () => GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == MudPuddleBase));
        // October 1 17:40:14/17: Icebloom hit after generic completion removed its
        // warning. Keep the placed six-yalm circle, padded 0.5, through the measured
        // delayed impact. Its helper stands at arena center, not at the placement.
        AvoidanceManager.AddAvoidLocation<Impact>(InYozakura, _ => 6.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == Icebloom));
        // October 1 20:47:31: generic raw-type 12 registered but left the player
        // on the flipping south tile. Helpers stand on its east edge atX 757,
        // face west and cover 40x 10; their CastLocation is zero. Keep the captured
        // rectangle through impact, flattening animated helper height to the floor.
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(5.5f, .5f, 40.5f), c => new Vector3(c.Location.X, YozakuraCenter.Y, c.Location.Z), () => PendingImpacts().Where(c => c.Action == TatamiGaeshi), priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidSquareDonut(InMoko, 39, 39, 140, 140, () => new[] { MokoCenter });
        // Moko's final facing occurs after the snapshot. Derive the 270-degree cleave from
        // the pre-impact actor heading and the action's directional variant, retaining it
        // through the observed damage delay. A 0.5 side allowance protects both safe-wedge edges.
        var giriPad = .5f / (float)Math.Sin(135 * Math.PI / 180);
        AvoidanceManager.AddAvoidPolygon<Impact>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Sector(270, 60.5f + giriPad), c => c.Location - new Vector3((float)Math.Sin(c.Heading) * giriPad, 0, (float)Math.Cos(c.Heading) * giriPad), () => ActiveGiri(), priority: AvoidancePriority.High);
        // Both claw pairs hit the middle on September 27 despite actor-centered generic
        // cones leaving it clear. Captured omen centers sit five yalms inward from the actor;
        // the 22-yalm half-disc must originate there. A 0.5 margin and 1.5-second impact fence
        // cover the observed server damage about 1.3seconds after RB's cast disappeared.
        AvoidanceManager.AddAvoidPolygon<Impact>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Sector(180, 23), c => c.Location - Forward(c.Heading) * .5f, () => PendingImpacts().Where(c => c.Action == Clearout), priority: AvoidancePriority.High);
        // The three Scarlet lines struck at five-second intervals (01:26:36/41/46).
        // Their helpers stand at the line midpoint; the wide explosion extends across both
        // sides of the arena. Publish only the earliest unresolved 30-wide lane, padded 0.5.
        AvoidanceManager.AddAvoidPolygon<Impact>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(15.5f, 30.5f, 30.5f), c => c.Location, () => ScarletWave(), priority: AvoidancePriority.High);
        // Spear damage repeats every 0.6s without a cast. October 1 the old one-step
        // lookahead repeatedly sent the player ahead of a slow spear, where its next
        // hit caught up. The 17:47 record 4 capture repeated that failure with the
        // two-footprint slow horizon: escaping repeatedly picked a point ahead
        // instead of leaving its lane. Forecast four footprints for both speeds
        // to expose lateral passing gaps earlier. The 3.474/2.316 steps match observed
        // ~5.79/3.86y/s motion; include 0.35s report/snapshot allowance and 0.5padding.
        // Reserve only this short horizon, never the full remaining arena lane.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, c => Rectangle(2.5f, c.BaseId == 0x3F82 ? 1f : .85f, c.BaseId == 0x3F82 ? 3.5f + 3 * 3.474f + 5.79f * .35f : 2.5f + 3 * 2.316f + 3.86f * .35f), c => c.Location, () => GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(c => c.IsValid && c.IsVisible && (c.BaseId == 0x3F82 || c.BaseId == 0x3F83) && c.Distance2D(MokoCenter) < 32), priority: AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseBossAvoidance();
        ReleaseSeasons();
        ReleaseRightPetals();
        ReleaseCloudLines();
        _azure.Clear();
        _goraiShapes.Clear();
        _goraiOrbs.Clear();
        _goraiBalladActive = false;
        ReleaseGoraiPrayer();
        ReleaseGoraiOrbs();
        ReleaseFluff();
        ResetRootChase();
        RegisterShishioClouds();
        _lance = null;
        _pursuit = null;
        _enenraSmoke.Clear();
        _impacts.Clear();
        _giri = _nextGiri = null;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (InEnenra() || InShishio())
        {
            if (await TankBusterSpells())
                return true;
            return await DamageMitigationSpells();
        }

        if (InGorai() && !Core.Me.IsDead)
        {
            if (HandleGoraiPrayer())
                return true;
            if (await TankBusterSpells())
                return true;
            return await DamageMitigationSpells();
        }

        if (InMoko() && !Core.Me.IsDead)
        {
            return await DamageMitigationSpells();
        }

        _giri = _nextGiri = null;
        if (!InYozakura() || Core.Me.IsDead)
        {
            _impacts.Clear();
            return false;
        }

        if (await TankBusterSpells())
            return true;
        return await DamageMitigationSpells();
    }

    private void UpdateImpacts(Vector3 center)
    {
        var now = DateTime.UtcNow;
        _retainedCloudLines.RemoveAll(c => c.End <= now);
        foreach (var key in _impacts.Keys.Where(k => _impacts[k].End <= now).ToArray())
            _impacts.Remove(key);
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.Distance2D(center) < 75 && OwnedActions.Contains(b.CastingSpellId)))
        {
            // RB ends its cast report before the damage snapshot. October 1 paired
            // seals still hit after the old 350ms fence; retain 750ms while avoiding
            // the unrelated second pair. Clearout retains its measured longer fence.
            // Keep a stable managed identity during a cast; replacing it every tick makes
            // RB discard the current avoid object and repeatedly announce the same escape.
            _impacts.TryGetValue(actor.ObjectId, out var impact);
            // Clouds can begin their next differently aimed line before the previous
            // damage fence expires. Preserve that managed footprint independently.
            if (impact != null && impact.Action is 33763 or 33764 or 33765 && now + actor.SpellCastInfo.RemainingCastTime > impact.End)
            {
                _retainedCloudLines.Add(impact);
                impact = null;
            }

            if (impact == null || impact.Action != actor.CastingSpellId)
                _impacts[actor.ObjectId] = impact = new Impact();
            impact.Action = actor.CastingSpellId;
            // Enenra's Kiseru/Uplift and Yozakura's first root are placed circles; their casting actor may
            // remain elsewhere. Self-centered bedrock rings retain actor origin.
            // Unsagely Spin's actor animates away from its fixed omen center.
            if (impact.Action is not (33774 or 33766 or 33776 or 33777 or 33763 or 33764 or 33765))
            {
                var origin = impact.Action is
                    33674 or 34196 or 34217 or 34212 or 34214 or 34021 or 34039 or
                    AzureAuspice or Clearout or LevinblossomStrike or Icebloom or
                    33696 or 32840 or 32855 or 33701 or 33762 or 34809 or 34811 or
                    33771 or 33773 or 33775 or 34604 ? actor.OmenMatrix.Center : actor.Location;
                // These omens can disappear before their damage resolves. Retain
                // the last arena-local origin through the existing impact window;
                // 75 yalms matches the observer's actor discovery radius.
                if (impact.Action is not (LevinblossomStrike or Icebloom or 34196 or Clearout) || origin.Distance2D(center) < 75)
                    impact.Location = origin;
                impact.Heading = actor.Heading;
            }
            else
            {
                // Swipe omens already include the left/right quarter-turn, even
                // while the actor is still turning into its second cast. Read
                // the actual axis instead of applying another actor-facing turn.
                // The omen becomes zero while Noble Pursuit's cast report still
                // exists. Keep the last valid corridor through impact instead of
                // moving it to the charging boss or its newly animated facing.
                actor.OmenMatrix.Transform(new Vector3(0, 0, 1), out var tip);
                var origin = actor.OmenMatrix.Center;
                var axis = tip - origin;
                var length = origin.Distance2D(tip);
                if (length is> 1 and < 101)
                {
                    impact.Location = origin;
                    impact.Heading = (float)Math.Atan2(axis.X, axis.Z);
                    impact.Length = length;
                }
            }

            // Final wind circles hit at 15:37:22.780, about 3.64s after the 2.7s
            // reported start. Keep 1.1s beyond RB's end so returning from knockback
            // cannot enter a circle during that measured final damage interval.
            // Boundless Azure's 2.7s report hit at 4.02s in the same capture;
            // its delayed release needs the longer fence already used by claws.
            // Gorai's successive rings use the same delayed helper lifecycle as his
            // cones; retain the current ring before exposing the next, two seconds later.
            // Seduced 3624 first appeared 1.55s beyond the 19:06:54.646 gaze report.
            // Its 1.7s fence keeps the shared outward travel heading away from the
            // dogs until the gaze resolves; later baits return to ordinary avoidance.
            // Yoki Uzu's October 2 hit arrived 1.24s beyond its RB cast end. Its
            // shelter owner must not restore dry-ground movement before that hit.
            var retentionMs = impact.Action is 33693 or 33694 ? 1700 : impact.Action is Clearout or BoundlessAzure or BoundlessScarlet or 33776 or 33777 ? 1500 : impact.Action == 33772 ? 1400 : impact.Action == 32856 ? 1350 : impact.Action is 33774 or 33766 ? 1300 : impact.Action is LevinblossomStrike or Icebloom or 34023 or 34024 or 34026 or 34027 or 34028 or 33696 or 33757 or 34725 or 34732 ? 1100 : 750;
            impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(retentionMs);
            // Captured Levinblossom and Iron Rain damage followed cast end by
            // up to 1.212s. Retain 1.4s to include the sampling margin.
            if (impact.Action is LevinblossomStrike or 34196)
                impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1400);
            // Fireblossom hit 1.324s after cast end. Its 1.6s window prevents
            // routine movement returning to the circle before impact; helper
            // flares have separate timing and must not inherit this extension.
            if (impact.Action == 33640)
                impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1600);
            // October 2 11:11:10.389: Bedrock's 8.7s report ended 19.089,
            // but the recorded damage/launch began 20.216 and vulnerability 20.474.
            // Keep each concentric wave through that 1.385s observed delay; an
            // expired outer ring must not authorize combat movement before damage.
            // This timing fence does not claim to solve a late arrival at its edge.
            if (impact.Action is >= 32840 and <= 32843)
                impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1500);
            // Azure Auspice hit 0.78s after native cast completion on October 2.
            if (impact.Action == AzureAuspice)
                impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1100);
            // The October 2 line hit at 01:31:24.762 after its native cast ended
            // around 24.1; generic avoidance had already released the same lane.
            if (impact.Action is 33763 or 33764 or 33765)
                impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1100);
            // Right Petals needs a position owner through the delayed native
            // displacement; ordinary wind avoidance resumes as soon as it lands.
            if (impact.Action == DriftingPetals && InRightYozakura())
                impact.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(1600);
        }
    }

    // Avoidance predicates can outlive the frame that began a loading transition.
    // Reject unavailable player state before choosing an arena or reading location.
    private static bool InRokkonCombat() => !CommonBehaviors.IsLoading && WorldManager.ZoneId == 1137 && Core.Me is { IsValid: true, InCombat: true, IsDead: false };
    private static bool InYozakura() => InRokkonCombat() && Core.Me.Distance2D(YozakuraCenter) < 45 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.BaseId == YozakuraBase && b.IsAlive && b.Distance2D(YozakuraCenter) < 45);
    private static bool InMoko() => InRokkonCombat() && Core.Me.Distance2D(MokoCenter) < 45 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && IsMoko(b) && b.IsAlive);
    private static bool IsMoko(BattleCharacter actor) => actor.BaseId == MokoBase || actor.BaseId == MokoOpenCaseBase;
    private void UpdateGiri()
    {
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.IsValid && IsMoko(b) && b.IsCasting);
        if (boss == null || boss.CastingSpellId < 34183 || boss.CastingSpellId > 34194)
            return;
        // The 01:14 right-safe cast began facing pi, then turned to 3pi/2 after damage;
        // front-safe began at 0.04 and turned to 3.18. Recomputing from the live pre-impact
        // heading also lets the opening facing animation settle before the five-second hit.
        var direction = (boss.CastingSpellId - 34183) % 4;
        var turn = direction == 1 ? -(float)Math.PI / 2 : direction == 2 ? (float)Math.PI : direction == 3 ? (float)Math.PI / 2 : 0;
        if (_giri == null || _giri.Action != boss.CastingSpellId || _giri.End <= DateTime.UtcNow)
            _giri = new Impact();
        _giri.Action = boss.CastingSpellId;
        _giri.Location = boss.Location;
        _giri.Heading = boss.Heading + turn;
        _giri.End = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(750);
        if (boss.CastingSpellId is >= 34187 and <= 34190 && boss.SpellCastInfo.RemainingCastTime.TotalSeconds < 5.5)
        {
            // Both captured doubles replaced aura 2970's first direction around six seconds
            // into the 11-second cast. The second marker predicts the follow-up 3.5seconds
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
            _nextGiri = null;
    }

    private IEnumerable<Impact> ActiveGiri()
    {
        // One owner exposes only the earliest unresolved cleave. Showing both opposing
        //270-degree sectors simultaneously leaves no safe floor and reverses the intended order.
        if (_giri != null && _giri.End > DateTime.UtcNow)
            yield return _giri;
        else if (_nextGiri != null && _nextGiri.End > DateTime.UtcNow)
            yield return _nextGiri;
    }

    private IEnumerable<Impact> SealWave()
    {
        var pending = PendingImpacts().Where(c => c.Action is SealFire or SealWind or SealRain or SealLightning).OrderBy(c => c.End).ToArray();
        if (pending.Length == 0)
            return Array.Empty<Impact>();
        // Lightning has a 0.2-second shorter cast than its paired circle/donut. This tolerance
        // joins that single resolving pair while excluding the next pair about 8seconds later.
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
        // Full native omen axis for charges, detached before the actor travels.
        internal float Length;
        internal DateTime End;
    }
}
