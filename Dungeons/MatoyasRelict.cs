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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Matoya's Relict boss mechanics while OrderBot owns traversal and targets.
/// Native avoidance handles ordinary hazards; one positioning owner handles stacks,
/// baits, knockback staging and sustained forward input during Barbeque.
/// Encounter exit, death, loading and bot stop release only this handler's resources.
/// </summary>
public partial class MatoyasRelict : AbstractDungeon
{
    private static MatoyasRelict active;
    private static readonly Vector3 MudmanCenter = new(-180, -220, -140);
    private uint stackTarget;
    private DateTime stackUntil, nextCapture;
    private bool moving, leased;
    private string mapSignature;
    private readonly List<AvoidInfo> mudAvoids = new();
    private readonly Dictionary<uint, SectorCast> sectors = new();
    private bool mudActive;
    // Placed-instance IDs from Global bg.lgb. Captured MapEffects state 1 means
    // an open drain; state 4 means a ball has consumed it.
    private static readonly Dictionary<uint, Vector3> Drains = new()
    {
        [8536401] = new(-157.3726f, -220, -162.6274f),
        [8536402] = new(-157.3726f, -220, -117.3726f),
        [8536403] = new(-202.6274f, -220, -117.3726f),
        [8536404] = new(-202.6274f, -220, -162.6274f)
    };
    // Mutable identity prevents per-frame deadline changes from resetting RB's escape path.
    private sealed class SectorCast
    {
        internal uint Action;
        internal Vector3 Position;
        internal float Heading;
        internal DateTime Until;
    }

    /// <inheritdoc />
    public override ZoneId ZoneId => Data.ZoneId.MatoyasRelict;
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new();
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { 21631, 22801 };
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToMitigate { get; } = new() { 21649, 22803 };

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance &&
        WorldManager.ZoneId == 933 && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive;

    /// <summary>Releases this handler's movement leases, avoids and SideStep suspension before another owner starts.</summary>
    public static void ResetActive()
    {
        active?.Reset();
        active = null;
    }

    /// <summary>Handles stop, death and loading boundaries that prevent the encounter hook from executing.</summary>
    public static void CheckLifecycle()
    {
        if (!Live())
        {
            ResetActive();
        }
    }

    /// <inheritdoc />
    protected override Task<bool> ExitDungeonAsync()
    {
        Reset();
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public override async Task<bool> RunAsync()
    {
        if (!Live())
        {
            Reset();
            return false;
        }
        active = this;
        CaptureSignals();
        if (!Core.Me.InCombat)
        {
            TracePorxieRelease("combat ended");
            Reset();
            return false;
        }
        // Native boss rooms map to Mudman (1), Nixie (2) and Mother Porxie (3).
        // Unknown rooms cannot acquire encounter movement ownership.
        int kind = WorldManager.SubZoneId == 3586 ? 1 : WorldManager.SubZoneId == 3587 ? 2 : WorldManager.SubZoneId == 3589 ? 3 : 0;
        // The sprite's launch reports subzone 0 at Y 40.8 inside this same arena.
        // Preserve its pending instant-puff timeline and SideStep lease during
        // that flight; an ordinary room exit, death or loading still resets them.
        if (kind == 0 && RetainPorxieFlight(WorldManager.SubZoneId, bossKind, Core.Me.Location) &&
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Any(a => a.IsValid && a.IsAlive && a.BaseId == 0x300B))
        {
            kind = 3;
        }

        if (kind == 0)
        {
            TracePorxieRelease("room/flight guard rejected");
            Reset();
            return false;
        }
        if (bossKind != kind)
        {
            Reset();
            bossKind = kind;
        }
        // This handler owns all boss avoidance. Restore the prior SideStep
        // state only when leaving this encounter; trash retains its normal handling.
        if (SidestepPlugin != null && SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = false;
            sideStepLease = true;
        }
        await TankBusterSpells();
        await DamageMitigationSpells();
        if (!Live())
        {
            Reset();
            return false;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid).ToArray();
        UpdateBossHazards(actors);
        if (kind == 2)
        {
            return RunNixie(actors);
        }

        if (kind == 3)
        {
            return await RunPorxie(actors);
        }

        bool mudman = actors.Any(a => a.NpcId == 9735 && a.IsAlive && a.IsVisible &&
            a.Location.Distance2D(Core.Me.Location) < 35) && Core.Me.Location.Distance2D(MudmanCenter) < 20;
        if (!mudman)
        {
            Reset();
            return false;
        }
        mudActive = true;
        RegisterMudAvoids();
        var now = DateTime.UtcNow;
        foreach (var a in actors.Where(a => a.IsCasting && a.CastingSpellId is >= 21646 and <= 21648 && a.SpellCastInfo.IsValid))
        {
            if (!sectors.TryGetValue(a.ObjectId, out var sector))
            {
                sectors[a.ObjectId] = sector = new();
            }

            sector.Action = a.CastingSpellId;
            sector.Position = a.Location;
            sector.Heading = a.Heading;
            // Captured vulnerability arrived about 0.97s after the outer helper's cast ended.
            // Keep the native sector through impact rather than dropping it at cast finish.
            sector.Until = now + a.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.1);
        }
        foreach (var key in sectors.Where(p => now >= p.Value.Until).Select(p => p.Key).ToArray())
        {
            sectors.Remove(key);
        }
        // Installed Global action 21652 is the six-yalm shared hit, not a hostile circle.
        var cast = actors.FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 21652 &&
            a.SpellCastInfo.IsValid && a.Location.Distance2D(MudmanCenter) < 40);
        if (cast != null)
        {
            var target = cast.SpellCastInfo.TargetId;
            if (target != stackTarget || now >= stackUntil)
            {
                Logger.Information("[Matoya] Falling Rock target=0x{0:X8}", target);
            }

            stackTarget = target;
            // Half a second is an initial impact allowance; live damage evidence must
            // verify it. Do not retain a target into a later mechanic or after a wipe.
            stackUntil = now + cast.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.5);
        }
        // The tether's repeated charge precedes a later stack if it was not drained.
        // Bait onto an open drain's ray inside the arena; never navigate to its
        // decorative placement outside the wall. Native sectors/puddles keep priority.
        var bubble = actors.FirstOrDefault(a => a.IsAlive && a.IsVisible && a.BaseId is 0x300E or 0x3009 &&
            ((a.VfxContainer.IsValid && a.VfxContainer.Tethers.Any(t => t.Id == 7 && t.TargetId == Core.Me.ObjectId)) ||
             (a.IsCasting && a.CastingSpellId == 21635 && a.SpellCastInfo.IsValid && a.SpellCastInfo.TargetId == Core.Me.ObjectId)));
        if (bubble != null && DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
        {
            var open = director.MapEffects.Where(e => e.State == 1 && Drains.ContainsKey(e.ID)).Select(e => Drains[e.ID]);
            var bait = open.Select(d => BaitPoint(bubble.Location, d)).Where(p => p.HasValue &&
                !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p.Value)))
                .OrderBy(p => p.Value.Distance2D(Core.Me.Location)).FirstOrDefault();
            if (bait.HasValue)
            {
                return PositionAt(bait.Value, "Matoya bubble drain bait");
            }

            ReleaseMovement();
            return false;
        }
        if (now >= stackUntil)
        {
            ReleaseMovement();
            return false;
        }
        var allies = actors.Where(a => a.IsAlive && a.IsVisible &&
            (a.IsMe || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)) &&
            a.Location.Distance2D(MudmanCenter) < 20).ToArray();
        var targetActor = allies.FirstOrDefault(a => a.ObjectId == stackTarget);
        if (targetActor == null)
        {
            ReleaseMovement();
            return false;
        }
        bool Safe(Vector3 p) => p.Distance2D(MudmanCenter) <= 19 &&
            !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        // Stay in the target's six-yalm circle with a one-yalm inset. In a puddle/sector
        // overlap, only candidate positions that native avoidance considers safe qualify.
        var candidates = new List<Vector3> { Core.Me.Location, targetActor.Location };
        candidates.AddRange(allies.Select(a => a.Location));
        for (int i = 0; i < 16; i++)
        {
            candidates.Add(targetActor.Location +
            new Vector3((float)Math.Sin(i * Math.PI / 8) * 3, 0, (float)Math.Cos(i * Math.PI / 8) * 3));
        }

        var goal = candidates.Where(p => p.Distance2D(targetActor.Location) <= 5 && Safe(p))
            .OrderByDescending(p => allies.Count(a => a.Location.Distance2D(p) <= 5))
            .ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        if (!goal.HasValue)
        {
            ReleaseMovement();
            return false;
        }
        return PositionAt(goal.Value, "Matoya Falling Rock stack");
    }

    // Limited to the observed airborne hole in the arena's subzone volume.
    // Prior encounter ownership and a live boss are required by the caller.
    // The launch reaches exactly Y=60; excluding that apex erases the pending puff.
    internal static bool RetainPorxieFlight(uint subzone, int previousKind, Vector3 position) =>
        subzone == 0 && previousKind == 3 && position.Y > 5 && position.Y <= 60 &&
        Math.Abs(position.X) < 20 && Math.Abs(position.Z) < 20;

    // A captured airborne release erased the pending instant-puff state, but
    // two-second samples did not identify which guard failed. Log the exact
    // rejecting frame once before reset rather than guessing a new height limit.
    private void TracePorxieRelease(string reason)
    {
        if (bossKind == 3)
        {
            Logger.Information("[Matoya] Porxie release reason={0} subzone={1} position={2} combat={3} puffUntil={4:O}",
                reason, WorldManager.SubZoneId, Core.Me.Location, Core.Me.InCombat, puffUntil);
        }
    }

    private bool PositionAt(Vector3 goal, string reason)
    {
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // Native escape has taken movement; cleanup must not stop its successor input.
            moving = false;
            ReleaseMovement();
            return false;
        }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        leased = true;
        if (Core.Me.Location.Distance2D(goal) <= .5f)
        {
            StopOwnedMovement();
            return false; // Rotation/healing continue while the held position is leased.
        }
        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(goal) { DistanceTolerance = .4f, UseMount = false });
        moving = true;
        return true;
    }

    // Reuse the semantic owner during game-controlled knockback: stale navigation
    // must not compete with displacement, but routine healing remains runnable.
    private void HoldScriptedDisplacement(string reason)
    {
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            ReleaseMovement();
            return;
        }
        StopOwnedMovement();
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        leased = true;
    }

    private void RegisterMudAvoids()
    {
        if (mudAvoids.Count != 0)
        {
            return;
        }
        // SideStep reports no support for 21646-48. All three native sectors may
        // coexist, leaving the shared rear wedge; ordinary movement stays with RB.
        mudAvoids.Add(AvoidanceManager.AddAvoidPolygon<SectorCast>(
            condition: () => Live() && mudActive, leashPointProducer: null, leashRadius: 60,
            rotationProducer: s => -s.Heading, scaleProducer: _ => 1, heightProducer: _ => 10,
            pointsProducer: s => SectorPoints(s.Action), locationProducer: s => s.Position,
            collectionProducer: () => sectors.Values));
        mudAvoids.Add(AvoidanceManager.AddAvoidObject<GameObject>(
            canRun: () => Live() && mudActive,
            objectSelector: a => a.IsValid && a.IsVisible && a.BaseId == 0x1EB145,
            radiusProducer: _ => 5.5f)); // Persistent mud radius 5 plus 0.5y margin.
    }

    internal static Vector2[] SectorPoints(uint action)
    {
        float inner = action == 21646 ? 0 : action == 21647 ? 6 : 12;
        float outer = action == 21646 ? 7 : action == 21647 ? 13 : 19;
        // Installed helpers are 270-degree concentric sectors with radii 6.5/12.5/18.5.
        // Expand radial boundaries 0.5y and each angular edge4 degrees; retain rear safety.
        var points = new List<Vector2>();
        for (int i = 0; i <= 40; i++)
        {
            double a = (-139 + 278.0 * i / 40) * Math.PI / 180;
            points.Add(new((float)Math.Sin(a) * outer, (float)Math.Cos(a) * outer));
        }
        if (inner == 0)
        {
            points.Add(new(0, 0));
        }
        else
        {
            for (int i = 40; i >= 0; i--)
            {
                double a = (-139 + 278.0 * i / 40) * Math.PI / 180;
                points.Add(new((float)Math.Sin(a) * inner, (float)Math.Cos(a) * inner));
            }
        }

        return points.ToArray();
    }

    internal static Vector3? BaitPoint(Vector3 bubble, Vector3 drain)
    {
        double dx = drain.X - bubble.X, dz = drain.Z - bubble.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < .01)
        {
            return null;
        }

        dx /= len;
        dz /= len;
        double x = bubble.X - MudmanCenter.X, z = bubble.Z - MudmanCenter.Z;
        double b = x * dx + z * dz, disc = b * b - (x * x + z * z - 18 * 18);
        if (disc < 0)
        {
            return null;
        }

        double t = -b + Math.Sqrt(disc);
        if (t <= 0 || t >= len)
        {
            return null;
        }

        return new Vector3((float)(bubble.X + t * dx), -220, (float)(bubble.Z + t * dz));
    }

    private void CaptureSignals()
    {
        // Drain, pipe and geyser map signals must be correlated with actual transitions;
        // reference event indices are not interchangeable with native MapEffects IDs.
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || DateTime.UtcNow < nextCapture)
        {
            return;
        }

        nextCapture = DateTime.UtcNow.AddSeconds(1);
        LoggingHelpers.LogActorSignalChanges("Matoya", a => a.IsVisible &&
            a.Location.Distance2D(Core.Me.Location) < 80 && (a.NpcId >= 9735 && a.NpcId <= 9743 || a.BaseId == 0x233C));
        if (DirectorManager.ActiveDirector is InstanceContentDirector d && d.IsValid)
        {
            string value = string.Join(";", d.MapEffects.Select(e => $"{e.ID:X}:{e.State:X}"));
            if (value != mapSignature)
            {
                mapSignature = value;
                Logger.Information("[Matoya] MapEffects {0}", value);
            }
        }
        foreach (var o in GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible &&
            o.Location.Distance2D(Core.Me.Location) < 60 && o.BaseId is 0x1EB145 or 0x1EB0C7 or 0x1EA1A1))
        {
            Logger.Information("[Matoya] Object id=0x{0:X8} base=0x{1:X} position={2}", o.ObjectId, o.BaseId, o.Location);
        }
    }

    private void StopOwnedMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover?.MoveStop();
        }

        moving = false;
    }
    private void ReleaseMovement()
    {
        StopOwnedMovement();
        // Barbeque also leases facing so routine target turns cannot rotate its
        // sustained forward input. Release both together on every existing exit.
        if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement | CapabilityFlags.Facing, "Matoya positioning ended");
        }

        leased = false;
    }
    private void Reset()
    {
        ReleaseMovement();
        mudActive = false;
        foreach (var avoid in mudAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        mudAvoids.Clear();
        sectors.Clear();
        ResetBossHazards();
        stackTarget = 0;
        stackUntil = DateTime.MinValue;
    }
}
