using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Interfaces;
using ff14bot.Pathing.Avoidance;
using ff14bot.Pathing;
using ff14bot.Pathing.Service_Navigation;
using ff14bot.Enums;
using Buddy.Coroutines;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Coordinates normal Shinryu's verified positioning through one movement owner.
/// Later semantic mechanics remain under diagnostic capture until their actor lifetimes are verified.
/// OrderBot owns combat and QTE progression; this handler never selects combat actions or targets.
/// </summary>
public sealed class TheRoyalMenagerie : AbstractDungeon
{
    // October22 capture: untargetable helper8075 at(0,-380,-30), heading0, pushes toward+Z.
    // Its rectangle is knockback choreography, not an area to escape. The35y push needs
    // a source-side start at-17.5 on the40y platform, leaving2.5y at either end.
    private const uint TidalWave = 8075;
    private uint waveOwner;
    private DateTime waveFinish;
    private Vector3 wavePoint;
    private bool moving, leased, arrived;
    private DateTime nextCapture;
    private DateTime meteorUntil;
    private string meteorPattern;
    private Vector3 meteorPoint;
    private DateTime windUntil;
    private Vector3 windPoint;
    private DateTime chargeUntil;
    private uint chargeActor;
    private Vector3 chargePoint;
    private DateTime fistUntil;
    private uint fistActor;
    private Vector3 fistPoint;
    private MovementGate movementGate;
    private readonly HashSet<uint> ownedOverrides = [];
    private static TheRoyalMenagerie activeHandler;
    private bool ownsTick;

    /// <summary>
    /// Ticks the one active controller on the plugin's bot-thread pulse. AutoParty can retain
    /// TreeStart inside a yielding wait, so hook re-entry is not a reliable mechanic clock.
    /// </summary>
    public static void PulseActive() { if (activeHandler != null) activeHandler.ownsTick = activeHandler.Tick(); }

    /// <summary>Releases this encounter's input on explicit stop/disable without stopping a successor avoid.</summary>
    public static void ResetActive()
    {
        activeHandler?.Release("Bot stop/disable");
        activeHandler?.ReleaseOverrides();
        activeHandler = null;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheRoyalMenagerie;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private static bool Active() => !CommonBehaviors.IsLoading && Core.Player != null &&
        Core.Player.IsValid && Core.Player.IsAlive && DutyManager.InInstance && WorldManager.ZoneId == 679;
    private static Vector3 Center => new(0, Core.Player.Y > 100 ? 620 : -380, 0);
    private static BattleCharacter[] Actors() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
        .Where(a => a.IsValid && Math.Abs(a.Y - Core.Player.Y) < 15).ToArray();

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        activeHandler = this;
        // Keep SideStep's ordinary telegraphs, but remove its confirmed inverted full-arena wave.
        // One square replaces the old circle19 plus square5 intersection. Widths are FULL widths.
        // Captured9291/8086 are proximity falloffs, not60y exclusion circles. Their
        // repeated no-path failures at12:19:59 require one explicit max-clearance goal.
        foreach (var action in new uint[] { TidalWave, 9291, 8086, 8077, 8085, 8088, 8080, 8984, 8180, 9456 })
            if (SideStep.Override(action)) ownedOverrides.Add(action);
        AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && Core.Player.Y < 100,
            38.5f, 38.5f, 180, 180, () => [new Vector3(0, -380, 0)], AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && Core.Player.Y > 100,
            58.5f, 58.5f, 180, 180, () => [new Vector3(0, 620, 0)], AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync() => Task.FromResult(ownsTick);

    private bool Tick()
    {
        if (!Active()) { Release("Death/loading/exit"); return false; }
        Capture();
        var wave = Actors().Where(a => a.IsCasting && a.CastingSpellId == TidalWave)
            .OrderBy(a => a.SpellCastInfo.RemainingCastTime).FirstOrDefault();
        if (wave != null)
        {
            // Only scalar snapshots survive this tick. Do not retain the native helper across a wipe.
            var finish = DateTime.UtcNow + wave.SpellCastInfo.RemainingCastTime;
            if (waveOwner != wave.ObjectId || waveFinish < DateTime.UtcNow.AddSeconds(-4))
            {
                waveOwner = wave.ObjectId;
                arrived = false;
                var direction = new Vector3((float)Math.Sin(wave.Heading), 0, (float)Math.Cos(wave.Heading));
                wavePoint = Center - direction * 17.5f;
                Logger.Information("Menagerie: Tidal staging helper={0:X} heading={1} goal={2} remaining={3}",
                    waveOwner, wave.Heading, wavePoint, wave.SpellCastInfo.RemainingCastTime);
            }
            waveFinish = finish;
            return PositionForMechanic("Tidal");
        }
        if (waveOwner != 0 && DateTime.UtcNow < waveFinish.AddSeconds(3.2))
        {
            // Cast completion precedes the push. Keep CR movement leased, but never run back to
            // the old source-side destination after knockback begins. RB still owns emergency egress.
            Lease("Tidal resolution");
            StopOwnedMovement();
            return false;
        }
        var wind = Actors().FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 8080);
        if (wind != null)
        {
            if (windUntil < DateTime.UtcNow)
            {
                // Live12:22:59/12:23:01/12:23:03 pulses each push5y; the first killed
                // both edge-positioned players. The center tile is unbreakable. Stage3y
                // south of its center, returning between pulses; the final15y push ends
                // on the south-center tile. Broken-floor validation remains a separate
                // later-phase capture gate, not a claim inferred from a rectangular bound.
                windPoint = new Vector3(wind.X, wind.Y, wind.Z + 3);
                arrived = false;
                Logger.Information("Menagerie: Aerial Blast staging goal={0}", windPoint);
            }
            windUntil = DateTime.UtcNow + wind.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(2.2);
        }
        if (DateTime.UtcNow < windUntil)
        {
            waveOwner = 0;
            wavePoint = windPoint;
            return PositionForMechanic("Aerial Blast");
        }
        var meteors = Actors().Where(a => a.IsCasting && a.CastingSpellId is 9291 or 8086).ToArray();
        if (meteors.Length > 0 && Core.Player.Y < 100)
        {
            // Helpers and cocoons describe the same simultaneous landing positions.
            // Deduplicate spatially so a later cocoon wrapper cannot change the chosen side.
            var points = meteors.Select(a => a.Location).GroupBy(p => (Math.Round(p.X), Math.Round(p.Z)))
                .Select(g => g.First()).ToArray();
            var pattern = string.Join(";", points.OrderBy(p => p.X).ThenBy(p => p.Z).Select(p => $"{p.X:F1},{p.Z:F1}"));
            if (pattern != meteorPattern)
            {
                meteorPoint = MeteorRetreat(points, Core.Player.Location,
                    meteors.Min(a => a.SpellCastInfo.RemainingCastTime.TotalSeconds));
                meteorPattern = pattern;
                arrived = false;
                Logger.Information("Menagerie: Meteor retreat impacts={0} goal={1} minimumDistance={2:F2}",
                    pattern, meteorPoint, points.Min(p => p.Distance2D(meteorPoint)));
            }
            meteorUntil = DateTime.UtcNow + meteors.Max(a => a.SpellCastInfo.RemainingCastTime) + TimeSpan.FromMilliseconds(600);
        }
        if (DateTime.UtcNow < meteorUntil)
        {
            waveOwner = 0;
            wavePoint = meteorPoint;
            return PositionForMechanic("Meteor");
        }
        var charge = Actors().FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 8180);
        if (charge != null)
        {
            // Live12:40:30: generic escape arrived, then kept walking off the edge before
            // Gyre resolved. The captured diagonal100x60 lane has two safe corners.
            // Select the nearest inset point outside it and own arrival/hold through impact.
            if (chargeActor != charge.ObjectId || chargeUntil < DateTime.UtcNow)
            {
                chargePoint = ChargeRetreat(charge.Location, charge.Heading, Core.Player.Location);
                chargeActor = charge.ObjectId;
                arrived = false;
                Logger.Information("Menagerie: Gyre staging goal={0} origin={1} heading={2}", chargePoint, charge.Location, charge.Heading);
            }
            chargeUntil = DateTime.UtcNow + charge.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(800);
        }
        if (DateTime.UtcNow < chargeUntil)
        {
            waveOwner = 0;
            wavePoint = chargePoint;
            return PositionForMechanic("Gyre");
        }
        var fist = Actors().FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 9456);
        if (fist != null)
        {
            // October22 12:59:20 capture: the16y central hit had3.7s warning;
            // generic avoidance jumped/wall-followed in place. Suppress that spell's
            // RB avoid and route its retreat/hold through this existing movement owner.
            if (fistActor != fist.ObjectId || fistUntil < DateTime.UtcNow)
            {
                fistPoint = FistRetreat(fist.Location, Core.Player.Location);
                fistActor = fist.ObjectId;
                arrived = false;
                Logger.Information("Menagerie: Dragonfist staging goal={0} origin={1}", fistPoint, fist.Location);
            }
            fistUntil = DateTime.UtcNow + fist.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(700);
        }
        if (DateTime.UtcNow < fistUntil)
        {
            waveOwner = 0;
            wavePoint = fistPoint;
            return PositionForMechanic("Dragonfist");
        }
        Release("Mechanic resolution complete");
        return false;
    }

    private bool PositionForMechanic(string mechanic)
    {
        Lease(mechanic);
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // An actual lane/spread can temporarily take ownership. Never stop that successor.
            moving = false;
            return false;
        }
        if (Core.Player.Distance2D(wavePoint) <= 0.5f)
        {
            // Brake even when the previous request belonged to an avoid that just ended.
            // Its last input can otherwise outlive IsRunningOutOfAvoid and cross the edge.
            movementGate?.Brake();
            moving = false;
            if (!arrived)
            {
                arrived = true;
                Logger.Information("Menagerie: {0} ready player={1}; rotation remains schedulable", mechanic, Core.Player.Location);
            }
            return false;
        }
        movementGate?.Command(wavePoint);
        moving = true;
        return true;
    }

    private void Lease(string mechanic)
    {
        // Scope the final mover gate to this mechanic window, not the whole fight.
        // This prevents delayed patrol destinations/stops from replacing a semantic hold.
        // Unhandled RB avoids retain an explicit higher-priority handoff.
        if (!leased) movementGate = new MovementGate(Navigator.PlayerMover);
        leased = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500),
            "Royal Menagerie: " + mechanic);
    }
    private void StopOwnedMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid) movementGate?.Brake();
        moving = false;
    }
    private void Release(string reason)
    {
        StopOwnedMovement();
        if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, reason);
            Logger.Information("Menagerie: release {0}", reason);
        }
        leased = false;
        movementGate?.Detach();
        movementGate = null;
        waveOwner = 0;
        meteorUntil = DateTime.MinValue;
        meteorPattern = null;
        windUntil = DateTime.MinValue;
        chargeUntil = DateTime.MinValue;
        chargeActor = 0;
        fistUntil = DateTime.MinValue;
        fistActor = 0;
        arrived = false;
    }
    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Release("Encounter cleanup");
        ReleaseOverrides();
        if (ReferenceEquals(activeHandler, this)) activeHandler = null;
        return Task.FromResult(false);
    }

    private void ReleaseOverrides()
    {
        foreach (var action in ownedOverrides) SideStep.RemoveHandler(action);
        ownedOverrides.Clear();
    }

    // One synchronous final-input gate, not another mover loop. Stale callbacks into a
    // detached gate are discarded; foreign replacements are never unwrapped or stopped.
    private sealed class MovementGate : IPlayerMover
    {
        private readonly IPlayerMover inner;
        private bool detached;
        private readonly NavigationGate navigation;
        internal MovementGate(IPlayerMover innerMover)
        {
            inner = innerMover ?? throw new InvalidOperationException("Royal Menagerie needs an installed mover");
            navigation = new NavigationGate(Navigator.NavigationProvider, this);
            Navigator.PlayerMover = this;
            Navigator.NavigationProvider = navigation;
            if (!AvoidanceManager.IsRunningOutOfAvoid) inner.MoveStop();
        }
        private bool Owns => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal void Command(Vector3 point) { if (Owns) inner.MoveTowards(point); }
        internal void Brake() { if (Owns) inner.MoveStop(); }
        internal void Detach()
        {
            navigation.Detach();
            if (Owns) Navigator.PlayerMover = inner;
            detached = true;
        }
        // RB936's path Progress performs its own jump/wall-follow input, bypassing
        // IPlayerMover. The13:23 meteor hold exposed that competing input. Gate path
        // progression under this same owner, while forwarding read-only path queries.
        private sealed class NavigationGate : WrappingNavigationProvider
        {
            private readonly MovementGate owner;
            private bool detached;
            internal NavigationGate(NavigationProvider original, MovementGate owner) : base(original)
            {
                this.owner = owner;
            }
            private bool Owns => !detached && ReferenceEquals(Navigator.NavigationProvider, this);
            private bool PassThrough => !owner.Owns || AvoidanceManager.IsRunningOutOfAvoid || !TreeRoot.IsRunning || !Active();
            /// <inheritdoc/>
            public override MoveResult MoveTo(MoveToParameters parameters) =>
                Owns && PassThrough ? Original.MoveTo(parameters) : MoveResult.PathGenerating;
            /// <inheritdoc/>
            public override void OnPulse() { if (Owns && PassThrough) Original.OnPulse(); }
            /// <inheritdoc/>
            public override async Task<MoveResult> MoveToAndStopAsync(MoveToParameters parameters, Func<bool> abortCondition = null, float stopDistance = 3.5f)
            {
                while (Owns && !PassThrough)
                {
                    if (abortCondition?.Invoke() == true) return MoveResult.Done;
                    await Coroutine.Yield();
                }
                // A detached request is canceled, not an arrival, and must never stop
                // or issue a destination into a successor's movement window.
                if (!Owns) return MoveResult.Done;
                return await Original.MoveToAndStopAsync(parameters, abortCondition, stopDistance);
            }
            internal void Detach()
            {
                if (Owns)
                {
                    // RB's supported provider lifecycle cancels its old path/anti-stuck
                    // state before restoration. Do not replay a pre-mechanic escape path.
                    Navigator.NavigationProvider = Original;
                }
                detached = true;
            }
        }
        /// <summary>Allows only an explicit RB avoidance handoff during a semantic movement lease.</summary>
        /// <param name="point">Destination requested by the provider or routine.</param>
        public void MoveTowards(Vector3 point)
        {
            if (Owns && (AvoidanceManager.IsRunningOutOfAvoid || !TreeRoot.IsRunning || !Active())) inner.MoveTowards(point);
        }
        /// <summary>Preserves avoidance/user-stop cleanup while rejecting stale lower-priority path stops.</summary>
        public void MoveStop()
        {
            if (Owns && (AvoidanceManager.IsRunningOutOfAvoid || !TreeRoot.IsRunning || !Active())) inner.MoveStop();
        }
    }

    private static Vector3 FistRetreat(Vector3 origin, Vector3 player)
    {
        // Preserve the player's radial side instead of crossing the central blast.
        // Radius17 leaves1y beyond the captured16y hit (including0.5y arrival tolerance).
        // This is first-Dragonfist geometry only: destroyed-tile safety is still a
        // separate capture gate, so do not infer general late-platform coverage.
        double dx = player.X - origin.X, dz = player.Z - origin.Z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        if (distance >= 17) return player;
        if (distance < 0.01) { dx = 0; dz = -1; distance = 1; }
        return new Vector3(origin.X + (float)(dx / distance * 17), player.Y,
            origin.Z + (float)(dz / distance * 17));
    }
    private static Vector3 ChargeRetreat(Vector3 origin, float heading, Vector3 player)
    {
        // Capture confirms the reference's forward100y, half-width30y rectangle.
        // One extra yard outside the lane absorbs0.5y arrival tolerance; the28y
        // platform inset prevents a successful dodge becoming an edge fall.
        var best = player;
        float bestTravel = float.MaxValue;
        double dx = Math.Sin(heading), dz = Math.Cos(heading);
        for (float x = -28; x <= 28; x += 0.5f)
        for (float z = -28; z <= 28; z += 0.5f)
        {
            double forward = (x - origin.X) * dx + (z - origin.Z) * dz;
            double lateral = Math.Abs((x - origin.X) * dz - (z - origin.Z) * dx);
            if (forward > -1 && forward < 101 && lateral < 31) continue;
            var point = new Vector3(x, 620, z);
            float travel = player.Distance2D(point);
            if (travel < bestTravel) { best = point; bestTravel = travel; }
        }
        return best;
    }

    private static Vector3 MeteorRetreat(Vector3[] impacts, Vector3 player, double secondsRemaining)
    {
        // The captured40y platform is intact during this phase. Search its inset square,
        // A far corner can be safest yet unreachable before the4.7s impact. Live second-wave
        // movement exposed that error. Prefer the nearest reachable14.5y clearance; otherwise
        // maximize reachable clearance. Budget5.5y/s (below observed6y/s), reserving0.75s
        // for input/arrival. Five impacts may leave no14y-safe point; never claim immunity.
        // The1.25y edge inset plus0.5y arrival tolerance retains0.75y physical margin.
        var best = player;
        double bestClearance = -1, bestTravel = double.MaxValue;
        bool bestSafe = false;
        double travelBudget = Math.Max(0, secondsRemaining - 0.75) * 5.5;
        for (float x = -18.75f; x <= 18.75f; x += 0.5f)
        for (float z = -18.75f; z <= 18.75f; z += 0.5f)
        {
            var point = new Vector3(x, -380, z);
            var clearance = impacts.Min(p => p.Distance2D(point));
            var travel = player.Distance2D(point);
            if (travel > travelBudget) continue;
            bool safe = clearance >= 14.5;
            if ((safe && !bestSafe) || (safe == bestSafe &&
                (safe ? travel < bestTravel : clearance > bestClearance + 0.001 ||
                    (Math.Abs(clearance - bestClearance) <= 0.001 && travel < bestTravel))))
            {
                best = point; bestClearance = clearance; bestTravel = travel; bestSafe = safe;
            }
        }
        return best;
    }

    private void Capture()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || DateTime.UtcNow < nextCapture) return;
        nextCapture = DateTime.UtcNow.AddSeconds(1);
        LoggingHelpers.LogActorSignalChanges("Menagerie", a => Math.Abs(a.Y - Core.Player.Y) < 15);
        // Event objects include inactive upper-platform actors before transition. Preserve height,
        // visibility and IDs rather than assuming that presence means a live puddle or safe tile.
        foreach (var o in GameObjectManager.GetObjectsOfType<EventObject>(true, false).Where(o => o.IsValid))
            Logger.Information("Menagerie object id={0:X} npc={1:X} visible={2} pos={3}",
                o.ObjectId, o.NpcId, o.IsVisible, o.Location);
    }
}


