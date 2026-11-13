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
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;

/// <summary>Handles Qitana's cast geometry and ordered positioning without borrowing NPC dodge decisions.</summary>
public partial class QitanaRavel : AbstractDungeon
{
    private static QitanaRavel active;
    private ChrysalisMovement movement;
    private bool? priorSideStep;
    private int boss;
    private readonly List<Cast> casts = new();
    private readonly Dictionary<uint, Cast> currentCasts = new();
    private readonly CapabilityManagerHandle positionHandle = CapabilityManager.CreateNewHandle();
    private Vector3? destination;
    private string positionKind;
    private bool moving;

    private static readonly Vector3 LozatlCenter = new(0, 5.35f, 315);
    private static readonly Vector3 BatsquatchCenter = new(62, -21, -35);
    private static readonly Vector3 ErosCenter = new(17, -77, -538);

    // Sun Toss and Hound damage arrived about 1.5 seconds after cast finish in capture.
    // Keep the avoid through that tail so combat approach cannot undo a dodge.
    private const double EffectTailSeconds = 1.8;
    private const float Clearance = .5f;
    private const string HandlerRevision = "qitana-release-20261112";

    // Boss base IDs differ from the untargetable helpers' shared name rows.
    private static class Actors
    {
        internal const uint Lozatl = 0x27AF;
        internal const uint Batsquatch = 0x27B0;
        internal const uint Eros = 0x27B1;
        internal const uint RonkanStatue = 0x28E8;
        internal const uint PoisonPuddle = 2004780;
    }

    // Parent casts announce the sequence; helpers own its targeted effects.
    private static class Actions
    {
        internal const uint SunToss = 15498;
        internal const uint FuryRight = 15503;
        internal const uint FuryLeft = 15504;
        internal const uint FallingRockSmall = 15509;
        internal const uint FallingRockLarge = 15510;
        internal const uint FallingBoulder = 15511;
        internal const uint Towerfall = 15512;
        internal const uint HoundOutOfHeaven = 15514;
        internal const uint ViperPoison = 15516;
        internal const uint ViperPoisonBait = 15517;
        internal const uint ViperPoisonGround = 15518;
        internal const uint HeavingBreath = 15520;
        internal const uint Inhale = 17168;
        internal const uint ConfessionCenter = 15522;
        internal const uint ConfessionSpread = 15523;
        internal const uint ConfessionStack = 15525;
        internal const uint ConfessionLeft = 15526;
        internal const uint ConfessionRight = 15527;
        internal const uint BurningBeam = 15923;
        internal const uint WrathOfTheRonka = 17223;
        internal const uint RonkanAbyss = 17387;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheQitanaRavel;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private sealed class Cast
    {
        internal uint Caster, Action, Target;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Finish, Until;
        internal bool Resolved;
        internal Vector3? Stage;
        internal Vector3? TargetPosition;
    }

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && DutyManager.InInstance && WorldManager.ZoneId == (uint)Data.ZoneId.TheQitanaRavel &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive;

    /// <summary>Releases only Qitana's borrowed input and plugin state on stop, disable or replacement.</summary>
    public static void ResetActive()
    {
        active?.Release();
        active = null;
    }

    /// <summary>Maintains the bot-thread owner while profile or combat coroutines retain TreeStart.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        if (!Live())
        {
            active.Release();
            return;
        }
        active.Pulse();
    }

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        Logger.Information("[Qitana] Handler revision={0}", HandlerRevision);
        // Register once per visit; the captured casts control each hazard's lifetime.
        AvoidanceManager.AddAvoidLocation<Cast>(Live, CircleRadius, Position,
            () => Pending().Where(c => CircleRadius(c) > 0));
        AvoidanceManager.AddAvoidPolygon<Cast>(Live, null, 65,
            c => -HazardHeading(c), _ => 1, _ => 15, Polygon, c => c.Origin,
            () => Pending().Where(c => c.Action is Actions.FuryRight or Actions.FuryLeft or Actions.Towerfall ||
                c.Action == Actions.ConfessionCenter && !fireOwned ||
                c.Action == Actions.BurningBeam && SidestepPlugin?.Enabled != true));
        AvoidanceManager.AddAvoidPolygon<Beam>(Live, null, 65,
            c => -c.Heading, _ => 1, _ => 15, c => Rectangle(4.5f, c.Length + .5f, -.5f), c => c.Origin,
            () => beams.Where(c => c.Until > DateTime.UtcNow));
        AvoidanceManager.AddAvoidPolygon<StatueLight>(Live, null, 65,
            c => c.East ? -(float)Math.PI / 2 : (float)Math.PI / 2,
            _ => 1, _ => 15, _ => Rectangle(65, 65, -.5f), _ => LozatlCenter,
            () => statueLights.Where(c => c.Until > DateTime.UtcNow));
        AvoidanceManager.AddAvoidLocation<EventObject>(
            () => Live() && boss == 3, _ => 6.5f, p => p.Location, PoisonObjects);
        // Inset each platform by 0.5 yalms. Eros needs a rectangle to preserve its corners.
        AvoidanceHelpers.AddAvoidDonut(() => Live() && boss == 1, () => LozatlCenter, 90, 19.5f, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(() => Live() && boss == 2, () => BatsquatchCenter, 90, 14.5f, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidSquareDonut(() => Live() && boss == 3,
            29, 39, 150, 150, () => new[] { ErosCenter }, AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        CheckLifecycle();
        return Task.FromResult(moving);
    }

    private void Pulse()
    {
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            // Decorative/untargetable casters need not expose ordinary HP.
            // Apply life checks to bosses and participants, not helper discovery.
            .Where(a => a.IsValid && a.Distance2D() < 85).ToArray();
        int nextBoss = actors.Any(a => a.BaseId == Actors.Lozatl && a.IsAlive && a.InCombat) ? 1 :
            actors.Any(a => a.BaseId == Actors.Batsquatch && a.IsAlive && a.InCombat) ? 2 :
            actors.Any(a => a.BaseId == Actors.Eros && a.IsAlive && a.InCombat) ? 3 : 0;
        if (nextBoss != boss)
        {
            ReleasePosition();
            boss = nextBoss;
            Logger.Information("[Qitana] Boss ownership={0} player={1}", boss, Core.Me.Location);
        }
        if (boss != 0 && priorSideStep == null && SidestepPlugin != null)
        {
            priorSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
        }
        if (boss == 0) RestoreSideStep();

        // Reuse the existing input owner. Attach only to the normal mover,
        // outside native escape and forced input; never wrap another owner.
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach(true);
            movement = null;
            ReleasePosition();
        }
        if (movement == null && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
            Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: OrdinarySafe,
                // A validated self-fire route owns the cone and spread together.
                prioritizeRequestedHazardExit: () => fireOwned);
            Navigator.PlayerMover = movement;
        }
        ObserveCasts(actors);
        ObserveBeams(actors);
        ObserveStatueLights();
        moving = false;
        if (boss == 3) UpdateEros(actors);
        else ReleasePosition();
        movement?.Update();
        ObservePosition();
        ObserveObjects();
        ObservePoisonObjects();
    }

    private void ObserveCasts(BattleCharacter[] actors)
    {
        DateTime now = DateTime.UtcNow;
        var seen = new HashSet<uint>();
        foreach (var a in actors.Where(a => a.IsNpc && a.IsCasting && a.SpellCastInfo.IsValid && Relevant(a.CastingSpellId)))
        {
            var spell = a.SpellCastInfo;
            seen.Add(a.ObjectId);
            DateTime finish = now + spell.RemainingCastTime;
            if (currentCasts.TryGetValue(a.ObjectId, out var prior) && prior.Action == spell.ActionId &&
                finish < prior.Finish.AddSeconds(1))
            {
                // Fury begins while Lozatl turns. Follow his facing during the
                // cast, then retain the final heading through the damage tail.
                if (prior.Action is Actions.FuryRight or Actions.FuryLeft)
                    prior.Heading = a.Heading;
                // Late target/tether information must not restart the deadline.
                uint oldTarget = prior.Target;
                if (prior.Action == Actions.HoundOutOfHeaven)
                    prior.Target = TetherTarget(a, 57) ?? TetherTarget(a, 1) ?? prior.Target;
                else if (prior.Target is 0 or 0xE0000000 && spell.TargetId is not (0 or 0xE0000000))
                    prior.Target = spell.TargetId;
                if (oldTarget != prior.Target)
                    Logger.Information("[Qitana] Target resolved action={0} target={1:X8}", prior.Action, prior.Target);
                continue;
            }
            if (prior != null && now < prior.Finish.AddMilliseconds(-300)) prior.Until = now;
            Vector3 origin = spell.ActionId is Actions.SunToss or Actions.RonkanAbyss or Actions.ViperPoisonGround ? spell.CastLocation : a.Location;
            if (origin == Vector3.Zero) continue;
            var cast = new Cast { Caster = a.ObjectId, Action = spell.ActionId, Target = spell.TargetId,
                Origin = origin, Heading = a.Heading, Finish = finish, Until = finish.AddSeconds(EffectTailSeconds) };
            // Keep pull preparation through the jump/Inhale sequence. Breath
            // consumes it; ordinary geometric hazards retain their shorter tail.
            if (cast.Action == Actions.ViperPoison) cast.Until = finish.AddSeconds(15);
            if (cast.Action == Actions.HoundOutOfHeaven)
            {
                // The threat target is not the carrier: tether 57 stretches to 1.
                cast.Target = TetherTarget(a, 57) ?? TetherTarget(a, 1) ?? 0;
            }
            casts.Add(cast);
            currentCasts[a.ObjectId] = cast;
            Logger.Information("[Qitana] Tell action={0} caster={1:X8} target={2:X8} origin={3} heading={4:F3} finish={5:O}",
                cast.Action, cast.Caster, cast.Target, cast.Origin, cast.Heading, cast.Finish);
        }
        foreach (var pair in currentCasts.ToArray())
        {
            if (seen.Contains(pair.Key)) continue;
            // Early disappearance cancels the forecast; normal completion keeps its tail.
            if (now < pair.Value.Finish.AddMilliseconds(-300)) pair.Value.Until = now;
            currentCasts.Remove(pair.Key);
        }
        casts.RemoveAll(c => c.Until <= now);
    }

    private static bool Relevant(uint id) => id is
        Actions.SunToss or Actions.FuryRight or Actions.FuryLeft or
        Actions.FallingRockSmall or Actions.FallingRockLarge or Actions.FallingBoulder or Actions.Towerfall or
        Actions.HoundOutOfHeaven or Actions.ViperPoison or Actions.ViperPoisonBait or Actions.ViperPoisonGround or
        Actions.Inhale or Actions.HeavingBreath or Actions.ConfessionCenter or Actions.ConfessionSpread or
        Actions.ConfessionStack or Actions.ConfessionLeft or Actions.ConfessionRight or
        Actions.BurningBeam or Actions.WrathOfTheRonka or Actions.RonkanAbyss;

    private IEnumerable<Cast> Pending() => casts.Where(c => !c.Resolved && c.Until > DateTime.UtcNow);

    private static uint? TetherTarget(BattleCharacter actor, uint id) => actor.VfxContainer.IsValid
        ? actor.VfxContainer.Tethers?.Where(t => t.Id == id && t.TargetId != 0 && t.TargetId != 0xE0000000)
            .Select(t => (uint?)t.TargetId).FirstOrDefault() : null;

    private float CircleRadius(Cast c) => c.Action switch
    {
        Actions.SunToss => 5.5f,
        // SideStep already decodes ordinary trash circles/lines. Supply those
        // shapes only when it is absent, avoiding duplicate geometry owners.
        Actions.RonkanAbyss when SidestepPlugin?.Enabled != true => 6.5f,
        Actions.FallingRockSmall => 2.5f,
        Actions.FallingRockLarge => 3.5f,
        Actions.FallingBoulder => 4.5f,
        Actions.ViperPoisonGround => 6.5f,
        Actions.ViperPoisonBait when OtherTargetKnown(c) => 6.5f,
        // Moving carriers need 1.5 yalms of snapshot/travel margin around the 5 yalms blast.
        // Self-fire owns this overlap as one timed route; duplicate native
        // cone/spread requests would compete with that route.
        Actions.ConfessionSpread when !fireOwned && OtherTargetKnown(c) => 6.5f,
        Actions.HoundOutOfHeaven when c.Target == Core.Me.ObjectId => 15.5f,
        _ => 0
    };

    private static bool OtherTargetKnown(Cast c) => c.Target is not (0 or 0xE0000000) &&
        c.Target != Core.Me.ObjectId && (c.TargetPosition.HasValue ||
            GameObjectManager.GetObjectByObjectId(c.Target)?.IsValid == true);

    private Vector3 Position(Cast c)
    {
        if (c.Action is Actions.ViperPoisonBait or Actions.ConfessionSpread or Actions.ConfessionStack)
        {
            if (c.Target == Core.Me.ObjectId) return Core.Me.Location;
            var target = GameObjectManager.GetObjectByObjectId(c.Target);
            if (target != null && target.IsValid) c.TargetPosition = target.Location;
            if (c.TargetPosition.HasValue) return c.TargetPosition.Value;
        }
        return c.Origin;
    }

    private static float HazardHeading(Cast c) => c.Heading + (c.Action == Actions.FuryLeft ? (float)Math.PI / 2 :
        c.Action == Actions.FuryRight ? -(float)Math.PI / 2 : 0);

    private static Vector2[] Polygon(Cast c)
    {
        // Native polygons face local +Z: captured Fury left=heading+90,
        // right=heading-90. Pad the shoulder's dividing line by 0.5 yalms.
        if (c.Action is Actions.FuryRight or Actions.FuryLeft) return Rectangle(65, 65, -.5f);
        if (c.Action == Actions.BurningBeam) return Rectangle(2.5f, 15.5f, -.5f);
        // Towerfall is 15 yalms/30 degrees; central Confession is 60 yalms/80 degrees.
        // A captured hit landed 0.76 yalms beyond the cone edge near its tip. Use
        // a 1.5 yalms fence there for snapshot/near-body uncertainty, not a wider angle.
        return Sector(c.Action == Actions.Towerfall ? 15 : 60, c.Action == Actions.Towerfall ? 15 : 40,
            c.Action == Actions.ConfessionCenter ? 1.5f : Clearance);
    }

    private static Vector2[] Rectangle(float halfWidth, float front, float back) =>
        [new(-halfWidth, back), new(halfWidth, back), new(halfWidth, front), new(-halfWidth, front)];

    private static Vector2[] Sector(float radius, float halfDegrees, float clearance)
    {
        double half = halfDegrees * Math.PI / 180;
        float back = clearance / (float)Math.Sin(half);
        var points = new List<Vector2> { new(0, -back) };
        for (int i = 0; i <= 24; i++)
        {
            // Match the counterclockwise outer contour used by the native
            // rectangle/donut helpers; preserve winding before tessellation.
            double angle = half - 2 * half * i / 24;
            points.Add(new Vector2((radius + back + clearance) * (float)Math.Sin(angle),
                (radius + back + clearance) * (float)Math.Cos(angle) - back));
        }
        return points.ToArray();
    }

    private static IEnumerable<EventObject> PoisonObjects() => GameObjectManager.GetObjectsOfType<EventObject>()
        .Where(p => p.IsValid && p.IsVisible && p.NpcId == Actors.PoisonPuddle);
    private bool Safe(Vector3 p) => !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));
    private bool OrdinarySafe(Vector3 to) => SegmentSafe(Core.Me.Location, to);

    private bool SegmentSafe(Vector3 from, Vector3 to)
    {
        int count = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .4f));
        for (int i = 0; i <= count; i++)
        {
            float t = (float)i / count;
            if (!Safe(new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t))) return false;
        }
        return true;
    }

    private void SetPosition(string kind, Vector3 point)
    {
        if (movement == null) return;
        if (positionKind != kind || !destination.HasValue || destination.Value.Distance2D(point) > .5f)
            Logger.Information("[Qitana] Position kind={0} from={1} to={2}", kind, Core.Me.Location, point);
        positionKind = kind;
        destination = point;
        CapabilityManager.Update(positionHandle, CapabilityFlags.Movement, 1000, "Qitana " + kind);
        CapabilityManager.Update(positionHandle, CapabilityFlags.GapCloser, 1000, "Qitana " + kind);
        movement.Request(point, .35f);
        moving = !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
            Core.Me.Location.Distance2D(point) > .35f;
    }

    private void ReleasePosition()
    {
        fireOwned = false;
        if (positionKind != null) Logger.Information("[Qitana] Position released kind={0} at={1}", positionKind, Core.Me?.Location);
        positionKind = null;
        destination = null;
        moving = false;
        movement?.Request(null, 0);
        CapabilityManager.Clear(positionHandle, CapabilityFlags.Movement, "Qitana positioning released");
        CapabilityManager.Clear(positionHandle, CapabilityFlags.GapCloser, "Qitana positioning released");
    }

    private void RestoreSideStep()
    {
        if (priorSideStep.HasValue && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = priorSideStep.Value;
        priorSideStep = null;
    }

    private void Release()
    {
        ReleasePosition();
        movement?.Detach(MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid);
        movement = null;
        RestoreSideStep();
        casts.Clear();
        currentCasts.Clear();
        beams.Clear();
        statueLights.Clear();
        litStatues.Clear();
        rejectedStages.Clear();
        boss = 0;
    }

    private DateTime nextPositionSample;

    private void ObservePosition()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || positionKind == null ||
            DateTime.UtcNow < nextPositionSample) return;
        nextPositionSample = DateTime.UtcNow.AddMilliseconds(250);
        // Actual displacement and held input distinguish a requested safe point
        // from execution; request/release transitions above are never throttled.
        Logger.Information("[Qitana] Position sample kind={0} player={1} goal={2} moving={3} native={4} inputLocked={5} current={6} forceMoved={7} movementLocked={8} occupied={9}",
            positionKind, Core.Me.Location, destination, MovementManager.IsMoving,
            AvoidanceManager.IsRunningOutOfAvoid, MovementManager.GenerallyOutOfControl, movement?.IsCurrent,
            MovementManager.ForceMoved, MovementManager.MovementLocked, MovementManager.IsOccupied);
    }
}
