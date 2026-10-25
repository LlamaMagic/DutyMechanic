using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Grand Cosmos encounter responses. Native avoidance owns harmful regions; the
/// single positive-position owner joins stacks and transfers Mortal Flame to furniture.
/// Routes and duty recovery remain in the standalone XML profile.
/// </summary>
public class GrandCosmos : AbstractDungeon
{
    private static GrandCosmos active;
    private readonly List<AvoidInfo> avoids = new();
    private readonly HashSet<uint> overrides = new();
    private DateTime stackUntil, spreadUntil;
    private uint stackTarget, stackCaster;
    private bool moving, leased;
    private Vector3? destination;
    private static readonly Vector3 SeekerCenter = new(0, 0, 187);
    private static readonly Vector3 LeannanCenter = new(0, -12.5f, -60);
    private int encounter;
    private DateTime petalsUntil, farWindUntil, groundWindUntil;
    private readonly Dictionary<uint, Vector3> groundWindLocations = new();
    private Vector3 petalsCenter;
    private readonly HashSet<uint> farWindTargets = new();
    private DateTime nextObjectCapture;

    /// <inheritdoc />
    public override ZoneId ZoneId => Data.ZoneId.TheGrandCosmos;
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { 18281, 18203, 18276 };
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToMitigate { get; } = new() { 18851, 18204, 18277 };

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        DutyManager.InInstance && WorldManager.ZoneId == 884 && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive;
    private static bool SeekerActive() => Live() && Core.Me.InCombat &&
        Math.Abs(Core.Me.X) < 24 && Math.Abs(Core.Me.Z - 187) < 20;
    private static bool LeannanActive() => Live() && Core.Me.InCombat &&
        Core.Me.Location.Distance2D(LeannanCenter) < 32 &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(a => a.IsValid && a.IsAlive && a.IsVisible && a.BaseId == 0x2C04);
    private static BattleCharacter[] Allies(Vector3 center) => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
        .Where(a => a.IsValid && a.IsVisible && a.IsAlive && !a.IsMe &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)) &&
            a.Location.Distance2D(center) < 35).ToArray();

    /// <summary>Releases this encounter's leases and hazards after stop, hook replacement, or disable.</summary>
    public static void ResetActive() { active?.Reset(); active = null; }

    /// <summary>Clears stale state on the plugin pulse when loading/death prevents TreeStart execution.</summary>
    public static void CheckLifecycle() { if (!Live()) ResetActive(); }

    /// <inheritdoc />
    protected override Task<bool> ExitDungeonAsync() { Reset(); return Task.FromResult(false); }

    /// <inheritdoc />
    public override async Task<bool> RunAsync()
    {
        if (!Live()) { Reset(); return false; }
        active = this;
        CaptureObjectState();
        await TankBusterSpells();
        await DamageMitigationSpells();
        if (!Live()) { Reset(); return false; }
        int next = SeekerActive() ? 1 : LeannanActive() ? 2 : LugusActive() ? 3 : 0;
        if (encounter != next) { Reset(); encounter = next; }
        if (next == 0) return false;
        if (next == 2) { UpdateLeannan(); return false; }
        if (next == 3) return UpdateLugus();
        if (avoids.Count == 0) Register();
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid).ToArray();
        var stack = actors.FirstOrDefault(a => a.BaseId == 0x2C1A && a.CastingSpellId == 18282);
        var now = DateTime.UtcNow;
        if (stack != null)
        {
            // The captured 17:18:14 death followed the estimated cast end by1.3s.
            // Retain the group through1.5s after the bar; this is not a damage attribution.
            if (stackTarget != stack.SpellCastInfo.TargetId || stackCaster != stack.ObjectId || now >= stackUntil)
            {
                destination = null;
                Logger.Information("[Grand Cosmos] Dark Pulse stack target=0x{0:X8} caster=0x{1:X8}", stack.SpellCastInfo.TargetId, stack.ObjectId);
            }
            stackTarget = stack.SpellCastInfo.TargetId; stackCaster = stack.ObjectId;
            stackUntil = now + stack.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }
        var spreads = actors.Where(a => a.NpcId == 9041 && a.CastingSpellId == 18285).ToArray();
        if (spreads.Length > 0)
            spreadUntil = now + spreads.Max(a => a.SpellCastInfo.RemainingCastTime) + TimeSpan.FromSeconds(1.5);

        // These casts are sequential in the capture. If future timing overlaps them,
        // the earlier stack resolves first; the native spread avoid is suppressed until then.
        if (now >= stackUntil) { ReleaseStack(); return false; }
        if (AvoidanceManager.IsRunningOutOfAvoid) { moving = false; return false; }
        var allies = Allies(SeekerCenter);
        var target = stackTarget == Core.Me.ObjectId ? null : allies.FirstOrDefault(a => a.ObjectId == stackTarget);
        if (allies.Length == 0 || (stackTarget != Core.Me.ObjectId && target == null)) { ReleaseMovement(); return false; }
        var locations = allies.Select(a => a.Location).ToArray();
        bool Safe(Vector3 p) => InsideSeeker(p) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        var previous = destination;
        destination = SelectStackDestination(Core.Me.Location, locations, target?.Location, previous, Safe);
        if (destination != null && destination != previous) Logger.Information("[Grand Cosmos] Stack destination={0}", destination.Value);
        if (destination == null) { ReleaseMovement(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Grand Cosmos Dark Pulse stack");
        leased = true;
        if (Core.Me.Location.Distance2D(destination.Value) <= 1)
        {
            StopOwnedMovement();
            return false; // Rotation/heals remain scheduled while the movement lease holds the stack.
        }
        // Use the established navigator, not a direct line through a broom or puddle.
        Navigator.MoveTo(destination.Value);
        moving = true;
        return true;
    }

    private void Register()
    {
        // Stack18282 and targeted spread18285 are semantic, not cast-origin circles.
        // Preserve SideStep for its observed working Dark Shock/ordinary telegraphs.
        foreach (var id in new uint[] { 18282, 18285 }) if (SideStep.Override(id)) overrides.Add(id);
        avoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => SeekerActive() && DateTime.UtcNow < spreadUntil && DateTime.UtcNow >= stackUntil,
            objectSelector: a => a.IsValid && a.IsAlive && !a.IsMe && Allies(SeekerCenter).Any(p => p.ObjectId == a.ObjectId),
            radiusProducer: _ => 5.5f)); // Five-yalm spread plus0.5y margin.
        avoids.Add(AvoidanceManager.AddAvoidObject<GameObject>(
            canRun: SeekerActive,
            objectSelector: a => a.IsValid && a.IsVisible && a.BaseId == 0x2C1B && Math.Abs(a.Location.X) <= 23.5f,
            radiusProducer: _ => 4.5f)); // Four-yalm moving broom candidate plus0.5y; no guessed future path.
        // The17:48:49 capture shows the current-position circle activating only at
        // collision, then chasing Mira into the west wall. Brooms moved about4y/s
        // along their heading. Overlapping native circles reserve the next6y of
        // that lane (roughly1.5s), allowing lateral escape before contact. Native
        // avoidance remains the sole geometric movement owner; no timed follow.
        foreach (float ahead in new[] { 3f, 6f })
            avoids.Add(AvoidanceManager.AddAvoidObject<GameObject>(
                canRun: SeekerActive,
                objectSelector: a => a.IsValid && a.IsVisible && a.BaseId == 0x2C1B && Math.Abs(a.Location.X) <= 23.5f,
                radiusProducer: _ => 4.5f,
                locationProducer: a => BroomProjection(a.Location, a.Heading, ahead)));
        avoids.Add(AvoidanceManager.AddAvoidObject<GameObject>(
            canRun: SeekerActive,
            objectSelector: a => a.IsValid && a.IsVisible && a.BaseId == 0x1EAEAE,
            radiusProducer: _ => 6.5f)); // Reserve the full6y cleaning splash while dirt exists; no guessed broom timer.
        avoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(SeekerActive, 40, 28, 100, 100,
            () => new[] { SeekerCenter }, AvoidancePriority.High)); //41x29 room with0.5y wall inset.
        Logger.Information("[Grand Cosmos] Registered Seeker stack/spread, broom/dirt regions and room boundary.");
    }

    private void UpdateLeannan()
    {
        if (avoids.Count == 0)
        {
            // The18:08:31 capture decoded18768 with a24y inner radius; Mira was
            // hit at7.37y. Its actual safe center is5y. Override only that donut
            // and the targeted spread. Ground18211 locations are correct but their lifetime
            // ended before impact in the18:33:09->10 capture, so retain snapshots below.
            foreach (var id in new uint[] { 18768, 18212, 18211 }) if (SideStep.Override(id)) overrides.Add(id);
            avoids.Add(AvoidanceHelpers.AddAvoidDonut(
                () => LeannanActive() && DateTime.UtcNow < petalsUntil,
                () => petalsCenter, 60.5, 4.5)); //5–60y with0.5y safety margin at each edge.
            avoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(
                canRun: () => LeannanActive() && DateTime.UtcNow < farWindUntil,
                objectSelector: a => a.IsValid && a.IsAlive && !a.IsMe &&
                    Allies(LeannanCenter).Any(p => p.ObjectId == a.ObjectId) &&
                    (farWindTargets.Contains(Core.Me.ObjectId) || farWindTargets.Contains(a.ObjectId)),
                radiusProducer: _ => 5.5f)); // Targeted5y circles must follow party members, not the helper origin.
            // Ground and spread effects resolve together. Keep both native regions
            // through the observed1.1s post-bar impact;8y circles receive0.5y margin.
            // Scalar snapshots survive helper cast-end without retaining frame wrappers.
            avoids.Add(AvoidanceManager.AddAvoidLocation(
                canRun: () => LeannanActive() && DateTime.UtcNow < groundWindUntil,
                radiusProducer: (Vector3 _) => 8.5f,
                locationProducer: (Vector3 p) => p,
                collectionProducer: () => groundWindLocations.Values));
            avoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(LeannanActive, 39, 39, 150, 150,
                () => new[] { LeannanCenter }, AvoidancePriority.High)); //40y room with0.5y wall inset.
            Logger.Information("[Grand Cosmos] Registered Leannan five-yalm donut, targeted spread and room boundary.");
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid).ToArray();
        var now = DateTime.UtcNow;
        var petals = actors.FirstOrDefault(a => a.BaseId == 0x2C04 && a.CastingSpellId == 18768);
        if (petals != null)
        {
            if (now >= petalsUntil) Logger.Information("[Grand Cosmos] Fallen Petals center={0}; native safe radius4.5.", petals.Location);
            petalsCenter = petals.Location;
            // Captured vulnerability arrived1.16s after the estimated cast end.
            // Retain the native donut for1.5s after the bar, then release it without
            // stopping the successor movement. Rotation is never claimed here.
            petalsUntil = now + petals.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }
        if (now >= groundWindUntil) groundWindLocations.Clear();
        var groundCasts = actors.Where(a => a.CastingSpellId == 18211 && a.NpcId == 9044).ToArray();
        if (groundCasts.Length > 0)
        {
            foreach (var ground in groundCasts) groundWindLocations[ground.ObjectId] = ground.SpellCastInfo.CastLocation;
            groundWindUntil = now + groundCasts.Max(a => a.SpellCastInfo.RemainingCastTime) + TimeSpan.FromSeconds(1.5);
        }
        if (now >= farWindUntil) farWindTargets.Clear();
        var spreads = actors.Where(a => a.CastingSpellId == 18212).ToArray();
        if (spreads.Length > 0)
        {
            foreach (var spread in spreads) farWindTargets.Add(spread.SpellCastInfo.TargetId);
            farWindUntil = now + spreads.Max(a => a.SpellCastInfo.RemainingCastTime) + TimeSpan.FromSeconds(1.5);
        }
        // Ground circles and spreads resolve together in the captured sequence;
        // native avoidance intersects both constraints. Fallen Petals begins later,
        // after their effect hold has expired, so no competing positive-position owner exists.
    }

    // Lugus uses a different lifecycle from the first two bosses. All values below
    // are Global client/reference facts corroborated by the October25 Rowan casts.
    private const uint MortalFlameStatus = 2136;
    private static readonly Vector3 LugusCenter = new(0, 8, -340);
    private readonly Dictionary<uint, LugusCross> lugusCrosses = new();
    private DateTime heatBaitUntil, nextLugusLog;
    private uint flameFurniture;
    private bool hadMortalFlame;
    private sealed record LugusCross(Vector3 Position, float Heading, DateTime Expires);
    private readonly LugusChargeSequence chargeSequence = new();
    private readonly List<Vector3> chargeOrigins = new();
    private readonly List<LugusChargeLane> chargeLanes = new();
    private readonly List<Vector2[]> chargeExclusions = new();
    private sealed record LugusChargeLane(Vector3 Start, Vector3 End);
    private DateTime ireUntil;

    private static bool LugusActive() => Live() && Core.Me.InCombat &&
        Core.Me.Location.Distance2D(LugusCenter) < 38 &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            // Lugus becomes invisible during Domain/dashes in the October 25 log;
            // visibility is not an encounter-end signal while the unique boss lives.
            .Any(a => a.IsValid && a.IsAlive && a.BaseId == 0x2C13);

    private static BattleCharacter[] LugusFurniture() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsVisible && a.IsTargetable && a.IsAlive &&
                a.NpcId is 9047 or 9048 or 9049 or 9050 or 9065 &&
                !a.HasAura(MortalFlameStatus) && a.Location.Distance2D(LugusCenter) < 36).ToArray();

    private bool HeatBaitActive() => LugusActive() && DateTime.UtcNow < heatBaitUntil &&
        !Core.Me.HasAura(MortalFlameStatus);

    private bool UpdateLugus()
    {
        var now = DateTime.UtcNow;
        foreach (var key in lugusCrosses.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray())
            lugusCrosses.Remove(key);
        var casts = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid && a.NpcId == 9046).ToArray();
        var heat = casts.FirstOrDefault(a => a.CastingSpellId == 18267);
        if (heat != null) heatBaitUntil = now + heat.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.8);
        foreach (var cross in casts.Where(a => a.CastingSpellId == 18268))
        {
            // Damage19:36:32.593 followed the2.2s helper bar by0.84s. Keep a1.2s
            // effect grace, not the choreography cast. Snapshot only scalar values.
            lugusCrosses[cross.ObjectId] = new(cross.Location, cross.Heading,
                now + cross.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.2));
            heatBaitUntil = DateTime.MinValue; // Ground placement is now fixed.
        }
        UpdateLugusCharges(now);
        if (avoids.Count == 0) RegisterLugus();

        bool flame = Core.Me.HasAura(MortalFlameStatus);
        if (flame != hadMortalFlame)
        {
            Logger.Information("[Grand Cosmos] Mortal Flame {0}; player={1} furniture=0x{2:X8}",
                flame ? "gained" : "removed", Core.Me.Location, flameFurniture);
            hadMortalFlame = flame;
            if (!flame) { flameFurniture = 0; destination = null; ReleaseMovement(); }
        }
        if (!flame) return false;

        // This is a transfer mechanic, not an interact/cast. Touch an intact object
        // and wait for native status loss; never assume arrival alone removed it.
        // The guide and reference require furniture contact within1y. Visible,
        // targetable, living furniture is reselected if another ally consumes it.
        var furniture = LugusFurniture();
        bool Safe(Vector3 p) => InsideLugus(p) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        var target = furniture.FirstOrDefault(a => a.ObjectId == flameFurniture && Safe(a.Location)) ??
            furniture.Where(a => Safe(a.Location)).OrderBy(a => a.Distance2D(Core.Me.Location)).FirstOrDefault();
        if (target == null)
        {
            ReleaseMovement();
            if (now >= nextLugusLog)
            {
                nextLugusLog = now.AddSeconds(2);
                Logger.Information("[Grand Cosmos] Mortal Flame pending: no safe intact furniture; candidates={0}", furniture.Length);
            }
            return false; // Keep heals/avoidance alive; no terminal stop or guessed object.
        }
        if (flameFurniture != target.ObjectId)
        {
            flameFurniture = target.ObjectId;
            Logger.Information("[Grand Cosmos] Transfer Mortal Flame to {0} object=0x{1:X8} at {2}", target.Name, target.ObjectId, target.Location);
        }
        if (AvoidanceManager.IsRunningOutOfAvoid) { moving = false; return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Grand Cosmos Mortal Flame transfer");
        leased = true;
        destination = target.Location;
        float distance = Core.Me.Location.Distance2D(target.Location);
        if (distance <= 0.8f) { StopOwnedMovement(); return false; }
        // Transfer needs a sub-yalm endpoint, not the service's default5y arrival.
        // Keep all approach movement in Navigator with explicit precision.
        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(target.Location) { DistanceTolerance = 0.6f, UseMount = false });
        moving = true;
        return true;
    }

    private void RegisterLugus()
    {
        // Retain SideStep for the observed working cleaves, Ire and falling circles.
        // Replace only the heat choreography/cross response; native RB owns escape.
        foreach (var id in new uint[] { 18267, 18268, 18269, 18265, 18266 })
            if (SideStep.Override(id)) overrides.Add(id);
        avoids.Add(AvoidanceManager.AddAvoidPolygon<LugusCross>(
            condition: LugusActive, leashPointProducer: null, leashRadius: 60,
            rotationProducer: a => -a.Heading, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: _ => LugusCrossPolygon(10.5f, 2.5f), locationProducer: a => a.Position,
            collectionProducer: () => lugusCrosses.Values));
        // Bait the cross footprint clear of furniture, accounting for its own
        // hitbox, rather than merely avoiding the furniture center. Cross arms
        // are10x2; expand by the furniture radius and0.5y safety margin.
        avoids.Add(AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            condition: HeatBaitActive, leashPointProducer: null, leashRadius: 60,
            rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: a => LugusCrossPolygon(10.5f + a.CombatReach, 2.5f + a.CombatReach),
            locationProducer: a => a.Location, collectionProducer: LugusFurniture));
        avoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: HeatBaitActive,
            objectSelector: a => a.IsValid && a.IsAlive && !a.IsMe && Allies(LugusCenter).Any(p => p.ObjectId == a.ObjectId),
            radiusProducer: _ => 6.5f)); // Black Flame18269:6y spread plus0.5y.
        avoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(LugusActive, 48, 48, 150, 150,
            () => new[] { LugusCenter }, AvoidancePriority.High)); //49y square,0.5y wall inset.
        // Ordered charges require at least16y separation. Native avoidance owns
        // both that exclusion and the current4y-wide charge lane (+0.5y margin).
        avoids.Add(AvoidanceManager.AddAvoidLocation(canRun: LugusActive,
            radiusProducer: (Vector3 _) => 16.5f, locationProducer: (Vector3 p) => p,
            collectionProducer: () => chargeOrigins));
        avoids.Add(AvoidanceManager.AddAvoidPolygon<LugusChargeLane>(
            condition: LugusActive, leashPointProducer: null, leashRadius: 60,
            rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: a => LugusLanePolygon(a.Start, a.End, 2.5f),
            locationProducer: a => a.Start, collectionProducer: () => chargeLanes));
        avoids.Add(AvoidanceManager.AddAvoidPolygon<Vector2[]>(
            condition: LugusActive, leashPointProducer: null, leashRadius: 60,
            rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 15,
            pointsProducer: a => a, locationProducer: _ => LugusCenter,
            collectionProducer: () => chargeExclusions));
        Logger.Information("[Grand Cosmos] Registered Lugus heat/furniture preservation, fixed crosses, Mortal Flame transfer and room boundary.");
    }

    private void UpdateLugusCharges(DateTime now)
    {
        chargeOrigins.Clear(); chargeLanes.Clear(); chargeExclusions.Clear();
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.BaseId == 0x2C13);
        if (boss == null) { chargeSequence.Reset(); return; }
        var party = Allies(LugusCenter).Concat(new[] { Core.Me }).ToArray();
        // RB940 LockOns selects native lock-on slots, unlike Vfx which also
        // contains omens. Installed Global Lockon50..53 names are a1t..a4t.
        var markers = party.Where(a => a.VfxContainer.IsValid)
            .SelectMany(a => a.VfxContainer.LockOns.Where(v => v != null && v.IsValid && v.Id is >= 50 and <= 53)
                .Select(v => new KeyValuePair<int, uint>((int)v.Id - 50, a.ObjectId))).ToArray();
        uint action = boss.IsCasting && boss.SpellCastInfo.IsValid ? boss.CastingSpellId : 0;
        if (action == 18273)
            ireUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        string change = chargeSequence.Update(action, markers, now);
        if (change != null) Logger.Information("[Grand Cosmos] Fire's Domain {0}", change);
        // Each observed Ire starts after a charge landed. Its cone resolves before
        // the next distance/line constraints resume; SideStep retains that cone.
        if (!chargeSequence.Ready || now < ireUntil) return;
        var target = party.FirstOrDefault(a => a.ObjectId == chargeSequence.CurrentTarget);
        if (target == null) return; // Dead/missing target: no invented charge endpoint.
        int ownOrder = chargeSequence.IndexOf(Core.Me.ObjectId);
        if (ownOrder >= chargeSequence.Completed)
        {
            var origin = ownOrder == chargeSequence.Completed ? boss :
                party.FirstOrDefault(a => a.ObjectId == chargeSequence.TargetAt(ownOrder - 1));
            if (origin != null) chargeOrigins.Add(origin.Location);
        }
        if (target.ObjectId != Core.Me.ObjectId)
            chargeLanes.Add(new(boss.Location, target.Location));
        else
        {
            // If we own the imminent charge, exclude directions whose lane would
            // clip another party member. This changes geometry, not movement owner.
            foreach (var ally in party.Where(a => !a.IsMe))
            {
                var polygon = LugusAllyExclusion(boss.Location, ally.Location, 2.5f);
                if (polygon.Length > 0) chargeExclusions.Add(polygon);
            }
        }
    }

    internal static Vector2[] LugusLanePolygon(Vector3 from, Vector3 to, float halfWidth)
    {
        float x = to.X - from.X, z = to.Z - from.Z;
        float length = (float)Math.Sqrt(x * x + z * z);
        if (length < 0.01f) return Array.Empty<Vector2>();
        float nx = -z / length * halfWidth, nz = x / length * halfWidth;
        return new[] { new Vector2(nx,nz), new Vector2(x+nx,z+nz),
            new Vector2(x-nx,z-nz), new Vector2(-nx,-nz) };
    }

    internal static Vector2[] LugusAllyExclusion(Vector3 boss, Vector3 ally, float halfWidth)
    {
        float x = ally.X - boss.X, z = ally.Z - boss.Z;
        double distance = Math.Sqrt(x*x + z*z);
        // An ally at the charge origin cannot be avoided by choosing a different
        // heading; do not turn that ambiguity into an arena-wide forbidden region.
        if (distance <= halfWidth) return Array.Empty<Vector2>();
        double center = Math.Atan2(z,x), halfAngle = Math.Asin(halfWidth / distance);
        var origin = new Vector2(boss.X - LugusCenter.X, boss.Z - LugusCenter.Z);
        //100y extends beyond the square arena. The sampled arc encloses its bounds;
        // extra radius compensates polygon chords so the lane margin stays intact.
        var points = new List<Vector2> { origin };
        for (int i=0;i<=8;i++)
        {
            double angle = center - halfAngle + 2*halfAngle*i/8;
            points.Add(new Vector2(origin.X + (float)Math.Cos(angle)*110,
                origin.Y + (float)Math.Sin(angle)*110));
        }
        return points.ToArray();
    }

    internal static bool InsideLugus(Vector3 p) => Math.Abs(p.X) <= 24 && Math.Abs(p.Z + 340) <= 24;

    // Axis-aligned cross polygon, used for both damage and the inverse furniture
    // bait constraint. Dimensions are half-length and half-width in world yalms.
    internal static Vector2[] LugusCrossPolygon(float length, float halfWidth) => new[]
    {
        new Vector2(halfWidth,length), new Vector2(halfWidth,halfWidth), new Vector2(length,halfWidth),
        new Vector2(length,-halfWidth), new Vector2(halfWidth,-halfWidth), new Vector2(halfWidth,-length),
        new Vector2(-halfWidth,-length), new Vector2(-halfWidth,-halfWidth), new Vector2(-length,-halfWidth),
        new Vector2(-length,halfWidth), new Vector2(-halfWidth,halfWidth), new Vector2(-halfWidth,length)
    };


    private void CaptureObjectState()
    {
        // Cast records alone cannot identify the grass layout or unburned furniture.
        // Observe native object/map state under the existing developer switch before
        // assigning those semantics. This collector never interacts, pulses or moves.
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || !Core.Me.InCombat || DateTime.UtcNow < nextObjectCapture) return;
        var center = Core.Me.Z < -300 ? new Vector3(0, 8, -340) : LeannanCenter;
        if (Core.Me.Location.Distance2D(center) > 35) return;
        nextObjectCapture = DateTime.UtcNow.AddSeconds(3);
        var objects = GameObjectManager.GameObjects.Where(a => a.IsValid && a.Location.Distance2D(center) < 35).Take(64);
        Logger.Information("[Grand Cosmos] Object capture player={0} statuses=[{1}] objects=[{2}]", Core.Me.Location,
            string.Join(";", Core.Me.Auras.Select(a => a.Id)),
            string.Join(";", objects.Select(a => $"{a.Name}:object={a.ObjectId:X8}/base={a.BaseId:X}/npc={a.NpcId}/visible={a.IsVisible}/targetable={a.IsTargetable}/location={a.Location}")));
        if (LugusActive())
            Logger.Information("[Grand Cosmos] Lugus marker capture [{0}]", string.Join(";", objects.OfType<BattleCharacter>()
                .Where(a => a.VfxContainer.IsValid).Select(a => $"{a.ObjectId:X8}:npc={a.NpcId}/auras={string.Join(',',a.Auras.Select(x=>x.Id))}/lockons={string.Join(',',a.VfxContainer.LockOns.Where(v=>v!=null && v.IsValid).Select(v=>v.Id))}/tethers={string.Join(',',a.VfxContainer.Tethers.Select(t=>$"{t.Id}:{t.TargetId:X8}"))}")));
        if (DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
            Logger.Information("[Grand Cosmos] Map capture [{0}]", string.Join(";", director.MapEffects.Take(32)
                .Select(e => $"id={e.ID:X8}/state={e.State:X4}/flags={e.Flags:X2}/unk={e.unk:X8}")));
    }

    internal static bool InsideSeeker(Vector3 point) => Math.Abs(point.X) <= 20 && Math.Abs(point.Z - 187) <= 14;
    // Native headings observed on reset: east1.57, west4.71; Y stays on the floor.
    internal static Vector3 BroomProjection(Vector3 point, float heading, float ahead) =>
        point + new Vector3((float)Math.Sin(heading) * ahead, 0, (float)Math.Cos(heading) * ahead);
    // Pure scalar planning keeps the shared-six-yalm requirement testable. Retain a
    // valid prior point to avoid oscillation; reject unsafe/no-ally cases explicitly.
    internal static Vector3? SelectStackDestination(Vector3 player, Vector3[] allies, Vector3? target,
        Vector3? previous, Func<Vector3, bool> safe)
    {
        bool Valid(Vector3 p) => safe(p) && (target == null || p.Distance2D(target.Value) <= 4.5f) && allies.Any(a => a.Distance2D(p) <= 5);
        if (previous != null && Valid(previous.Value)) return previous;
        var candidates = new List<Vector3> { player };
        foreach (var anchor in target == null ? allies : new[] { target.Value })
        {
            candidates.Add(anchor);
            for (int i = 0; i < 12; i++)
            {
                double angle = i * Math.PI / 6;
                candidates.Add(anchor + new Vector3((float)Math.Cos(angle) * 2, 0, (float)Math.Sin(angle) * 2));
            }
        }
        return candidates.Where(Valid).OrderByDescending(p => allies.Count(a => a.Distance2D(p) <= 5))
            .ThenBy(p => p.Distance2D(player)).Select(p => (Vector3?)p).FirstOrDefault();
    }
    private void StopOwnedMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid) Navigator.PlayerMover.MoveStop();
        moving = false;
    }
    private void ReleaseMovement()
    {
        StopOwnedMovement();
        if (leased) CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Grand Cosmos semantic hold released");
        leased = false;
    }
    private void ReleaseStack()
    {
        ReleaseMovement();
        stackTarget = stackCaster = 0; destination = null; stackUntil = DateTime.MinValue;
    }
    private void Reset()
    {
        ReleaseStack(); spreadUntil = DateTime.MinValue;
        lugusCrosses.Clear(); heatBaitUntil = DateTime.MinValue; flameFurniture = 0; hadMortalFlame = false;
        chargeSequence.Reset(); chargeOrigins.Clear(); chargeLanes.Clear(); chargeExclusions.Clear(); ireUntil = DateTime.MinValue;
        petalsUntil = farWindUntil = groundWindUntil = DateTime.MinValue;
        groundWindLocations.Clear();
        farWindTargets.Clear(); encounter = 0;
        foreach (var avoid in avoids) AvoidanceManager.RemoveAvoid(avoid);
        avoids.Clear();
        foreach (var id in overrides) SideStep.RemoveHandler(id);
        overrides.Clear();
    }
}

// Scalar state survives frame-cached actor wrappers. Only a witnessed Domain cast
// may arm a sequence; four unique lock-ons establish order, and Ire cast edges
// acknowledge charges. Elapsed time can discard stale state, never advance it.
internal sealed class LugusChargeSequence
{
    private readonly Dictionary<int,uint> targets = new();
    private uint previousAction;
    private bool armed, invalid;
    private DateTime expires;
    internal int Completed { get; private set; }
    internal bool Ready => armed && !invalid && targets.Count == 4 && Completed < 4;
    internal uint CurrentTarget => Ready ? targets[Completed] : 0;
    internal uint TargetAt(int index) => targets.TryGetValue(index, out var id) ? id : 0;
    internal int IndexOf(uint actor) => targets.Where(p => p.Value == actor).Select(p => p.Key).DefaultIfEmpty(-1).First();

    internal string Update(uint action, KeyValuePair<int,uint>[] markers, DateTime now)
    {
        string change = null;
        if (action == 18270 && previousAction != 18270)
        {
            Reset(); armed = true; expires = now.AddSeconds(45);
            change = "armed by native Domain cast";
        }
        if (armed && now >= expires) { Reset(); change = "discarded stale sequence; no guessed advancement"; }
        bool wasReady = Ready;
        if (armed && Completed == 0 && action == 18270)
        {
            foreach (var marker in markers)
            {
                if (marker.Key is < 0 or > 3 || marker.Value == 0) continue;
                if ((targets.TryGetValue(marker.Key,out var old) && old != marker.Value) ||
                    targets.Any(p => p.Key != marker.Key && p.Value == marker.Value)) invalid = true;
                targets[marker.Key] = marker.Value;
            }
            if (!wasReady && Ready) change = "order=" + string.Join(",", Enumerable.Range(0,4).Select(i => $"{i+1}:0x{targets[i]:X8}"));
            if (invalid) { armed = false; change = "ambiguous lock-ons; geometry suppressed"; }
        }
        if (armed && action == 18273 && previousAction != 18273)
        {
            if (!Ready) { armed = false; change = "incomplete lock-ons; geometry suppressed"; }
            else { Completed++; change = $"charge {Completed} acknowledged by Ire; next={CurrentTarget:X8}"; }
        }
        // A different boss mechanic after the choreography ends invalidates any
        // residual order; an interrupted attempt must not leak charge exclusions.
        if (armed && action != 0 && action != 18270 && action != 18273) Reset();
        previousAction = action;
        return change;
    }

    internal void Reset()
    {
        targets.Clear(); previousAction = 0; armed = invalid = false;
        expires = DateTime.MinValue; Completed = 0;
    }
}
