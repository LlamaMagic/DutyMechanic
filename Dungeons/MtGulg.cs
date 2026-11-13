using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Supplies Mt. Gulg boss geometry and bounded semantic positioning through one
/// movement owner. OrderBot owns the route/targets; the routine owns job actions.
/// </summary>
public partial class MtGulg : AbstractDungeon
{
    internal const string RuntimeRevision = "mtgulg-release-20261113";
    // High-volume actor and party snapshots are only needed during investigation.
    private const bool DevelopmentCapture = false;
    private const uint CrueltyBase = 0x27CA, WhimsyBase = 0x27CC, ObscenityBase = 0x27CE;
    private static readonly Vector3 CrueltyCenter = new(188, -48, -170);
    private static readonly Vector3 WhimsyCenter = new(-240, 210, -50);
    private static readonly Vector3 ObscenityCenter = new(-240, 210, 237);
    private static MtGulg active;
    private readonly List<Cast> casts = [];
    private readonly Dictionary<uint, Cast> casting = [];
    private readonly List<AvoidInfo> registrations = [];
    private ChrysalisMovement movement;
    private SlideMover originalMover;
    private bool? priorSideStep;
    private int boss;
    private DateTime nextCaptureUtc, nextEnvironmentUtc;
    private string lastEnvironment, lastMovement;

    private sealed class Cast
    {
        internal uint Caster, Action;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Finish, Until;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.MtGulg;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [16247, 16248, 16249, 16250, 16521];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && DutyManager.InInstance && WorldManager.ZoneId == 822 &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && boss != 0;

    /// <summary>Releases only Mt. Gulg's input, avoid registrations and SideStep lease.</summary>
    public static void ResetActive()
    {
        if (active == null)
        {
            return;
        }
        active.ReleaseBoss();
        foreach (var avoid in active.registrations)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }
        active.registrations.Clear();
        active = null;
    }

    /// <summary>Maintains cleanup and cast discovery through yielding profile hooks.</summary>
    public static void CheckLifecycle()
    {
        if (active == null)
        {
            return;
        }
        if (!Live())
        {
            active.ReleaseBoss();
        }
        else
        {
            active.Pulse();
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        Logger.Information("[MtGulg] Handler revision={0}", RuntimeRevision);
        // Add 0.5y clearance. Casts disappear before their effects arrive, so keep
        // each hazard for 0.8s beyond the estimated cast finish.
        registrations.Add(AvoidanceManager.AddAvoidLocation<Cast>(OwnsHazards, CircleRadius,
            c => c.Origin, () => Pending().Where(c => CircleRadius(c) > 0)));
        registrations.Add(AvoidanceManager.AddAvoidPolygon<Cast>(OwnsHazards, null, 65,
            c => -c.Heading, _ => 1, _ => 15, Polygon, c => c.Origin,
            () => Pending().Where(c => c.Action is 16818 or 15616 or 17153 or 15642 or 15643 or 15648)));
        // Register overlaps together so an escape satisfies every active shape.
        AddDonut(15630, 15.5, 4.5);
        AddDonut(15641, 16.5, 9.5);
        AddDonut(18025, 32.5, 17.5);
        // Tile helper 15623 appears too late. The parent action identifies the
        // fixed 10y grid; the boss's off-platform position must not move that grid.
        registrations.Add(AvoidanceManager.AddAvoidPolygon<Vector3>(OwnsHazards, null, 60,
            _ => 0, _ => 1, _ => 15, _ => Rectangle(5.5f, 5.5f, -5.5f), p => p,
            () => Pending().SelectMany(Tiles).ToArray()));
        registrations.Add(AvoidanceHelpers.AddAvoidDonut(() => OwnsHazards() && boss == 1,
            () => CrueltyCenter, 90, 19.5, AvoidancePriority.High));
        // Preserve the square/rectangular corners with a 0.5y wall inset.
        registrations.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => OwnsHazards() && boss == 2,
            29, 29, 150, 150, () => [WhimsyCenter], AvoidancePriority.High));
        registrations.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => OwnsHazards() && boss == 3,
            29, 39, 150, 150, () => [ObscenityCenter], AvoidancePriority.High));
        registrations.Add(AvoidanceHelpers.AddAvoidDonut(
            () => OwnsHazards() && boss == 3 && FloorActive,
            () => ObscenityCenter, 90, 14.5, AvoidancePriority.High));
        // Incomplete or late layouts fall back to staged avoids. All six padded
        // columns cannot be active together: their union covers the entire floor.
        registrations.Add(AvoidanceManager.AddAvoidPolygon<BeamLane>(
            () => OwnsHazards() && boss == 3 && beamRoute == null, null, 65,
            lane => -lane.Heading, _ => 1, _ => 15, _ => Rectangle(3, 100, -100),
            lane => lane.Origin, BeamHazards));
        registrations.Add(AvoidanceManager.AddAvoidLocation<Vector3>(
            () => OwnsHazards() && boss == 3, _ => 3.5f, p => p, OrbPositions));
        // Moving orbs overtake reactive contact avoidance. Reserve two seconds
        // of travel in the fallback; the dedicated route checks its own segments.
        registrations.Add(AvoidanceManager.AddAvoidPolygon<OrbForecast>(
            () => OwnsHazards() && boss == 3 && beamRoute == null, null, 65,
            orb => -orb.Heading, _ => 1, _ => 15, _ => Rectangle(3.5f, 9.5f, -3.5f),
            orb => orb.Origin, ForecastOrbs));
        return Task.FromResult(false);
    }

    private void AddDonut(uint action, double outer, double inner) => registrations.Add(
        AvoidanceHelpers.AddAvoidDonut(OwnsHazards,
            () => Pending().Where(c => c.Action == action).Select(c => c.Origin).ToArray(), outer, inner));

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        CheckLifecycle();
        if (boss == 0)
        {
            // Corridor casts continue to use the existing trash response.
            await FollowDodgeSpells();
            return false;
        }
        return movingToTower || movingToMarionetteStage || movingToBeamPosition;
    }

    private void Pulse()
    {
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.Distance2D() < 85 && Math.Abs(a.Y - Core.Me.Y) < 8).ToArray();
        int next = actors.Any(a => a.BaseId == CrueltyBase && a.IsAlive && a.InCombat) ? 1 :
            actors.Any(a => a.BaseId == WhimsyBase && a.IsAlive && a.InCombat) ? 2 :
            actors.Any(a => a.BaseId == ObscenityBase && a.IsAlive && a.InCombat) ? 3 : 0;
        // Keep ownership through untargetable phases. NPC 8262 is also used by
        // trash/helpers, so acquiring the boss requires its base identity.
        uint previousBase = boss == 1 ? CrueltyBase : boss == 2 ? WhimsyBase : ObscenityBase;
        if (next == 0 && boss != 0 && actors.Any(a => a.BaseId == previousBase && a.IsAlive) &&
            WorldManager.SubZoneId == (uint)(2997 + boss))
        {
            next = boss;
        }
        if (next != boss)
        {
            // Sample before completion despawns party NPCs; missing reads stay unknown.
            if (DevelopmentCapture && boss != 0)
            {
                Logger.Information("[MtGulgCapture] Party outcome boss={0} members=[{1}]", boss, SnapshotPartyHealth());
            }
            ReleaseBoss();
            boss = next;
            Logger.Information("[MtGulg] Boss ownership={0} at={1}", boss, Core.Me.Location);
        }
        if (boss == 0)
        {
            return;
        }
        if (priorSideStep == null && SidestepPlugin != null)
        {
            priorSideStep = SidestepPlugin.Enabled;
        }
        if (SidestepPlugin != null && SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = false;
        }
        if (movement != null && !movement.IsCurrent)
        {
            ReleaseTowerPosition("movement owner replaced");
            ReleaseMarionetteStage("movement owner replaced");
            ReleaseBeamPosition("movement owner replaced");
            beamRoute = null;
            movement.Detach(true);
            movement = null;
            originalMover = null;
        }
        // One mover arbitrates mechanic requests, native escape and ordinary travel.
        if (movement == null && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
            Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            originalMover = (SlideMover)Navigator.PlayerMover;
            movement = new ChrysalisMovement(originalMover, canMoveOrdinary: OrdinarySafe);
            Navigator.PlayerMover = movement;
        }
        ObserveCasts(actors);
        UpdateMarionetteStage();
        if (DateTime.UtcNow >= nextEnvironmentUtc)
        {
            nextEnvironmentUtc = DateTime.UtcNow.AddMilliseconds(100);
            if (boss == 2)
            {
                UpdateTowers();
            }
            if (boss == 3)
            {
                UpdateObscenityEnvironment();
            }
        }
        UpdateBeamPosition();
        movement?.Update();
        if (DevelopmentCapture)
        {
            CaptureBossSignals();
        }
    }

    private void ObserveCasts(BattleCharacter[] actors)
    {
        DateTime now = DateTime.UtcNow;
        var seen = new HashSet<uint>();
        foreach (var actor in actors.Where(a => a.IsNpc && a.IsCasting && a.SpellCastInfo.IsValid && Relevant(a.CastingSpellId)))
        {
            var spell = actor.SpellCastInfo;
            seen.Add(actor.ObjectId);
            var finish = now + spell.RemainingCastTime;
            if (casting.TryGetValue(actor.ObjectId, out var previous) && previous.Action == spell.ActionId &&
                finish < previous.Finish.AddSeconds(1))
            {
                previous.Origin = actor.Location;
                previous.Heading = actor.Heading;
                continue;
            }
            var cast = new Cast
            {
                Caster = actor.ObjectId,
                Action = spell.ActionId,
                Origin = actor.Location,
                Heading = actor.Heading,
                Finish = finish,
                Until = finish.AddMilliseconds(800)
            };
            casting[actor.ObjectId] = cast;
            casts.Add(cast);
            if (cast.Action == 15644)
            {
                BeginPenance(cast);
            }
            if (cast.Action == 17066)
            {
                BeginSolitaire(now);
            }
            if (cast.Action == 15645)
            {
                BeginMarionetteStage(now);
            }
            Logger.Information("[MtGulg] Cast action={0} caster={1:X8} base={2:X} origin={3} heading={4:F3} finish={5:O}",
                            cast.Action, cast.Caster, actor.BaseId, cast.Origin, cast.Heading, cast.Finish);
        }
        foreach (uint id in casting.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            var cast = casting[id];
            // Interruptions cancel the forecast; normal completion retains its tail.
            if (now < cast.Finish.AddMilliseconds(-350))
            {
                cast.Until = now;
                if (cast.Action == 17066)
                {
                    EndSolitaire("cast interrupted");
                }
                if (cast.Action == 15644 && !floorActivated)
                {
                    ResetFloor();
                }
                if (cast.Action == 15645)
                {
                    ReleaseMarionetteStage("cast interrupted");
                }
            }
            casting.Remove(id);
        }
        casts.RemoveAll(c => c.Until <= now);
    }

    private static bool Relevant(uint action) => action is 16818 or 15616 or 17153 or 15619 or
        15622 or 16987 or 16988 or 16989 or 15630 or 15638 or 15640 or 15641 or 18025 or
        15649 or 15642 or 15643 or 15648 or 15644 or 17066 or 15645;
    private IEnumerable<Cast> Pending() => casts.Where(c => c.Until > DateTime.UtcNow);
    private static float CircleRadius(Cast cast) => cast.Action switch
    {
        15619 => 10.5f,
        15638 or 15640 or 15649 => 8.5f,
        _ => 0
    };
    private static Vector2[] Polygon(Cast cast) => cast.Action is 15616 or 17153
        ? Sector(25, 30, .5f) : Rectangle(3, 40.5f, -.5f);
    private static Vector2[] Rectangle(float halfWidth, float front, float back) =>
        [new(-halfWidth, back), new(halfWidth, back), new(halfWidth, front), new(-halfWidth, front)];

    private static Vector2[] Sector(float radius, float halfDegrees, float margin)
    {
        double half = halfDegrees * Math.PI / 180;
        float back = margin / (float)Math.Sin(half);
        var points = new List<Vector2> { new(0, -back) };
        for (int i = 0; i <= 24; i++)
        {
            double angle = half - 2 * half * i / 24;
            points.Add(new((radius + back + margin) * (float)Math.Sin(angle),
                (radius + back + margin) * (float)Math.Cos(angle) - back));
        }
        return points.ToArray();
    }

    private static IEnumerable<Vector3> Tiles(Cast cast)
    {
        // The grid is fixed to the platform, not the off-floor boss's heading.
        for (int x = -1; x <= 1; x++)
        {
            for (int z = -1; z <= 1; z++)
            {
                bool hit = cast.Action switch
                {
                    16989 => x == 0 && z == 0 || x != 0 && z != 0,
                    16988 => x == 0 || z == 0,
                    16987 => z == 0 && x != 0,
                    15622 => x == 0 && z != 0,
                    _ => false
                };
                if (hit)
                {
                    yield return new(WhimsyCenter.X + 10 * x, WhimsyCenter.Y, WhimsyCenter.Z + 10 * z);
                }
            }
        }
    }

    private bool OrdinarySafe(Vector3 destination)
    {
        // Check the next waypoint so safe navmesh detours remain available, while
        // combat movement cannot walk back through a hazard after a dodge.
        var from = Core.Me.Location;
        int samples = Math.Max(1, (int)Math.Ceiling(from.Distance2D(destination) / .4f));
        for (int i = 0; i <= samples; i++)
        {
            float t = (float)i / samples;
            var point = new Vector3(from.X + (destination.X - from.X) * t, from.Y,
                from.Z + (destination.Z - from.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)))
            {
                return false;
            }
        }
        return true;
    }

    private void ReleaseBoss()
    {
        ReleaseTowerPosition("boss lifecycle");
        ReleaseMarionetteStage("boss lifecycle");
        towers.Clear();
        expiredTowers.Clear();
        ResetFloor();
        EndSolitaire("boss lifecycle");
        spentOrbs.Clear();
        movement?.Detach();
        movement = null;
        originalMover = null;
        if (priorSideStep.HasValue && SidestepPlugin != null && !SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = priorSideStep.Value;
        }
        priorSideStep = null;
        casts.Clear();
        casting.Clear();
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Mt. Gulg lifecycle released");
        LoggingHelpers.ClearActorSignalWatch("MtGulg");
        boss = 0;
    }

    private void CaptureBossSignals()
    {
        if (DateTime.UtcNow < nextCaptureUtc)
        {
            return;
        }
        nextCaptureUtc = DateTime.UtcNow.AddMilliseconds(500);
        LoggingHelpers.LogActorSignalChanges("MtGulg", a => a.Distance2D() < 60);
        // Keep unreadable timeline state distinct from an observed reset.
        string environment = string.Join(";", GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(a => a.IsValid && a.Distance2D() < 60)
            .OrderBy(a => a.ObjectId).Select(a =>
            {
                string state = ReadTimeline(a)?.ToString() ?? "unavailable";
                return $"{a.ObjectId:X8}/{a.NpcId:X}/{a.BaseId:X}@{a.Location}/h={a.Heading:F3}/visible={a.IsVisible}/state={state}";
            }));
        if (environment != lastEnvironment)
        {
            Logger.Information("[MtGulgCapture] Environment boss={0} objects=[{1}]", boss, environment);
            lastEnvironment = environment;
        }
        string input = $"boss={boss} player={Core.Me.Location} heading={Core.Me.Heading:F3} " +
            $"moving={MovementManager.IsMoving} escape={AvoidanceManager.IsRunningOutOfAvoid} " +
            $"lastForwarded={originalMover?.LastMoveLocation} sideStep={SidestepPlugin?.Enabled} " +
            $"avoids={AvoidanceManager.Avoids.Count} owner={Navigator.PlayerMover?.GetType().Name} " +
            $"partyHealth=[{SnapshotPartyHealth()}]";
        if (input != lastMovement)
        {
            Logger.Information("[MtGulgCapture] Movement {0}", input);
            lastMovement = input;
        }
    }

    private static string SnapshotPartyHealth()
    {
        try
        {
            return string.Join(";", PartyManager.VisibleMembers.Select(p => p.BattleCharacter)
                .Where(a => a != null && a.IsValid).OrderBy(a => a.ObjectId)
                .Select(a => $"{a.ObjectId:X8}/npc={a.NpcId}/hp={a.CurrentHealth}/{a.MaxHealth}/alive={a.IsAlive}"));
        }
        catch (Exception)
        {
            // A failed diagnostic read must not affect movement or imply good health.
            return "unavailable";
        }
    }

    // Missing objects/APIs are unknown state, never a safe reset.
    private static int? ReadTimeline(GameObject actor)
    {
        try
        {
            int value = Lua.GetReturnVal<int>("local o=_G['" + actor.LuaString + "']; if not o or not o.GetSharedGroupTimelineState then return -1 end; return o:GetSharedGroupTimelineState()");
            return value < 0 ? null : value;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
