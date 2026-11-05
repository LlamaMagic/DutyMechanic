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
/// Coordinates Shinryu's positioning while OrderBot retains combat and duty progression.
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
    private DateTime meteorUntil;
    private string meteorPattern;
    private Vector3 meteorPoint;
    private DateTime windUntil;
    private Vector3 windPoint;
    private DateTime hellfireUntil;
    private Vector3 hellfirePoint;
    private DateTime hypernovaUntil;
    private uint hypernovaTarget;
    private DateTime icicleFirstImpact, icicleUntil;
    private DateTime nextIciclePlan;
    private Vector3 icicleFirstPoint, icicleSecondPoint;
    private bool icicleOverride, icicleSecondChecked;
    private DateTime chargeUntil;
    private uint chargeActor;
    private Vector3 chargePoint;
    private DateTime fistUntil;
    private uint fistActor;
    private Vector3 fistPoint;
    private MovementGate movementGate;
    private bool sideStepSuppressed, sideStepWasEnabled;
    private bool upperArenaEntered;
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
        activeHandler?.RestoreSideStep();
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
        // Boss positioning and telegraphs must share this encounter's movement ownership.
        // Restore the prior plugin state when this handler relinquishes the duty.
        if (SidestepPlugin != null)
        {
            sideStepWasEnabled = SidestepPlugin.Enabled;
            sideStepSuppressed = true;
            SidestepPlugin.Enabled = false;
        }
        // One square replaces the old circle19 plus square5 intersection. Widths are FULL widths.
        // Captured9291/8086 are proximity falloffs, not60y exclusion circles. Their
        // repeated no-path failures at12:19:59 require one explicit max-clearance goal.
        foreach (var action in new uint[] { TidalWave, 9291, 8086, 8077, 8085, 8088, 8080, 8984, 8180, 9456, 8097 })
            if (SideStep.Override(action)) ownedOverrides.Add(action);
        // If the ordered plan cannot establish a supported route, retain a scoped native
        // escape instead of treating an unknown floor or missing actor group as safe.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            () => Active() && !icicleOverride,
            a => a.IsValid && a.IsCasting && a.CastingSpellId == 8097,
            10f, 65f, yOffset: -2.5f, priority: AvoidancePriority.High);
        // The captured tail marks a 40-by-20 forward strip; add lines use their
        // own caster and retain RB's urgent escape priority over semantic movement.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            () => Active(), a => a.IsValid && a.IsCasting && a.CastingSpellId is 8083 or 9130,
            20f, 40f, priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            () => Active(), a => a.IsValid && a.IsCasting && a.CastingSpellId == 8730,
            11f, 16.8f, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<BattleCharacter>(
            () => Active(), () => Center, 60f, _ => 4f, a => a.SpellCastInfo.CastLocation,
            () => Actors().Where(a => a.IsCasting && a.CastingSpellId == 8732 && a.SpellCastInfo != null));
        AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && Core.Player.Y < 100,
            38.5f, 38.5f, 180, 180, () => [new Vector3(0, -380, 0)], AvoidancePriority.High);
        // The QTE carries players through the air outside the final platform. The
        // boundary must not ask RB to escape until a grounded arrival is observed.
        AvoidanceHelpers.AddAvoidSquareDonut(() => Active() && upperArenaEntered && Core.Player.Y > 100,
            58.5f, 58.5f, 180, 180, () => [new Vector3(0, 620, 0)], AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync() => Task.FromResult(ownsTick);

    private bool Tick()
    {
        if (!Active()) { upperArenaEntered = false; Release("Death/loading/exit"); return false; }
        if (Core.Player.Y < 100) upperArenaEntered = false;
        else if (!upperArenaEntered && Math.Abs(Core.Player.Y - 620) < 0.6f &&
            Math.Abs(Core.Player.X) < 29 && Math.Abs(Core.Player.Z) < 29 && !MovementManager.GenerallyOutOfControl)
        {
            upperArenaEntered = true;
            Logger.Information("Menagerie: final arena boundary enabled after grounded arrival at {0}", Core.Player.Location);
        }
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
                // The small pulses and final 15y knockback require staging near the eye,
                // but Domestic observed repeated inward travel into the cyclone itself.
                // Keep an 8y radius and select a direction whose entire projected push
                // has same-height collision; do not cross the eye just to return after a pulse.
                windPoint = CycloneStaging(wind.Location, Core.Player.Location);
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
        if (icicleUntil == DateTime.MinValue && Core.Player.Y > 100)
            TryStartIcicleSequence();
        if (DateTime.UtcNow < icicleUntil)
        {
            if (!icicleSecondChecked && DateTime.UtcNow >= icicleFirstImpact)
            {
                // Tail Slap can remove floor after the initial plan was sampled.
                // Do not issue its second segment against stale collision evidence.
                icicleSecondChecked = true;
                arrived = false;
                if (!SupportedSegment(Core.Player.Location, icicleSecondPoint))
                {
                    Release("Spikesicle floor changed; yielding to native escape");
                    return false;
                }
            }
            waveOwner = 0;
            wavePoint = DateTime.UtcNow < icicleFirstImpact ? icicleFirstPoint : icicleSecondPoint;
            return PositionForMechanic("Spikesicle");
        }
        if (icicleOverride)
        {
            icicleOverride = false;
            Release("Spikesicle resolution complete");
        }
        var hellfire = Actors().FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 8076);
        if (hellfire != null && hellfireUntil < DateTime.UtcNow)
        {
            // Water fields shelter the party from Hellfire; hold the nearest one through impact.
            var puddle = GameObjectManager.GetObjectsOfType<EventObject>(true, false)
                .Where(o => o.IsValid && o.IsVisible && o.NpcId == 0x1E950D &&
                    Math.Abs(o.Y - Core.Player.Y) < 5)
                .OrderBy(o => o.Distance2D(Core.Player.Location)).FirstOrDefault();
            if (puddle != null)
            {
                hellfirePoint = puddle.Location;
                hellfireUntil = DateTime.UtcNow + hellfire.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(700);
                arrived = false;
                Logger.Information("Menagerie: Hellfire shelter goal={0} remaining={1}", hellfirePoint, hellfire.SpellCastInfo.RemainingCastTime);
            }
        }
        if (DateTime.UtcNow < hellfireUntil)
        {
            waveOwner = 0;
            wavePoint = hellfirePoint;
            return PositionForMechanic("Hellfire");
        }
        var hypernova = Actors().FirstOrDefault(a => a.IsCasting && a.CastingSpellId == 8089);
        if (hypernova != null && Core.Player.Y < 100)
        {
            // Marker 62 identifies the stack target. A marked player joins the largest nearby group.
            var party = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Concat(new BattleCharacter[] { Core.Me })
                .Where(a => a != null && a.IsValid && a.IsAlive && a.IsVisible &&
                    (a.ObjectId == Core.Me.ObjectId || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId)))
                .GroupBy(a => a.ObjectId).Select(g => g.First()).ToArray();
            var marked = party.FirstOrDefault(a => a.VfxContainer.IsValid &&
                a.VfxContainer.Vfx.Any(v => v != null && v.IsValid && v.Id == 62));
            if (marked != null)
            {
                var destination = marked.ObjectId == Core.Me.ObjectId
                    ? party.Where(a => a.ObjectId != Core.Me.ObjectId)
                        .OrderByDescending(a => party.Count(b => b.ObjectId != Core.Me.ObjectId && b.Distance2D(a.Location) < 5f))
                        .ThenBy(a => a.Distance2D(Core.Me.Location)).FirstOrDefault()
                    : marked;
                if (destination != null)
                {
                    if (hypernovaTarget != marked.ObjectId)
                    {
                        arrived = false;
                        hypernovaTarget = marked.ObjectId;
                        Logger.Information("Menagerie: Hypernova target={0:X} destination={1}", hypernovaTarget, destination.Location);
                    }
                    hypernovaUntil = DateTime.UtcNow + hypernova.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(700);
                    wavePoint = destination.Location;
                    waveOwner = 0;
                    return PositionForMechanic("Hypernova");
                }
            }
        }
        if (DateTime.UtcNow < hypernovaUntil)
            return PositionForMechanic("Hypernova");
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
        hellfireUntil = DateTime.MinValue;
        hypernovaUntil = DateTime.MinValue;
        hypernovaTarget = 0;
        icicleOverride = false;
        icicleFirstImpact = DateTime.MinValue;
        icicleUntil = DateTime.MinValue;
        icicleSecondChecked = false;
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
        RestoreSideStep();
        if (ReferenceEquals(activeHandler, this)) activeHandler = null;
        return Task.FromResult(false);
    }

    private void ReleaseOverrides()
    {
        foreach (var action in ownedOverrides) SideStep.RemoveHandler(action);
        ownedOverrides.Clear();
    }

    private void RestoreSideStep()
    {
        if (sideStepSuppressed && SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = sideStepWasEnabled;
        sideStepSuppressed = false;
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

    private void TryStartIcicleSequence()
    {
        if (DateTime.UtcNow < nextIciclePlan) return;
        var icicles = Actors().Where(a => a.BaseId == 0x1B16).ToArray();
        var first = icicles.Where(a => a.IsCasting && a.CastingSpellId == 8097).ToArray();
        if (first.Length != 3 || first.Any(a => a.SpellCastInfo == null)) return;
        var firstIds = first.Select(a => a.ObjectId).ToHashSet();
        var second = icicles.Where(a => !firstIds.Contains(a.ObjectId) &&
            a.Location.Distance2D(first[0].Location) < 61f).ToArray();
        if (second.Length != 3) return;

        // The alternating waves require an ordered route; native avoidance cannot reliably
        // select both lanes before the second hit, especially around a broken tile.
        bool lanesAlongZ = Math.Abs(Math.Cos(first[0].Heading)) > 0.7;
        float Lateral(Vector3 p) => lanesAlongZ ? p.X : p.Z;
        Vector3 At(float lateral, float longitudinal) => lanesAlongZ
            ? new Vector3(lateral, Core.Player.Y, longitudinal)
            : new Vector3(longitudinal, Core.Player.Y, lateral);
        float[] firstLanes = first.Select(a => Lateral(a.Location)).ToArray();
        float[] secondLanes = second.Select(a => Lateral(a.Location)).ToArray();
        float current = Lateral(Core.Player.Location);
        var firstCandidates = firstLanes.SelectMany(x => new[] { x - 6.5f, x + 6.5f })
            .Concat(new[] { current }).Where(x => x >= -28 && x <= 28 && firstLanes.All(l => Math.Abs(x - l) >= 6f))
            .OrderBy(x => Math.Abs(x - current)).ToArray();
        // Stage outside the tail's 40-by-20 strip before its cast: a late emergency
        // dodge can otherwise consume the second-wave window. Native escape remains
        // available when no supported route can be established.
        var tails = Actors().Where(a => a.BaseId == 0x1B12 && a.IsVisible).ToArray();
        bool OutsideTail(Vector3 point) => tails.All(t =>
        {
            double dx = Math.Sin(t.Heading), dz = Math.Cos(t.Heading);
            double x = point.X - t.X, z = point.Z - t.Z;
            double forward = x * dx + z * dz;
            double lateral = Math.Abs(x * dz - z * dx);
            return forward < -1 || forward > 41 || lateral >= 11.5;
        });
        float longitudinal = lanesAlongZ ? Core.Player.Z : Core.Player.X;
        var alongCandidates = new List<float> { longitudinal };
        // Tail strips observed perpendicular to the icicle lanes are centered on the
        // tail's longitudinal coordinate. The 1.5y inset includes arrival tolerance.
        foreach (var tail in tails)
        {
            float center = lanesAlongZ ? tail.Z : tail.X;
            alongCandidates.Add(center - 11.5f);
            alongCandidates.Add(center + 11.5f);
        }
        var candidatePoints = firstCandidates.SelectMany(start => alongCandidates.Select(along => At(start, along)))
            .Where(p => Math.Abs(p.X) <= 28 && Math.Abs(p.Z) <= 28 && OutsideTail(p))
            .OrderBy(p => Core.Player.Location.Distance2D(p)).ToArray();
        double firstBudget = Math.Max(0, first.Min(a => a.SpellCastInfo.RemainingCastTime.TotalSeconds) - 0.1) * 5.5;
        foreach (var firstPoint in candidatePoints)
        {
            float start = Lateral(firstPoint);
            float along = lanesAlongZ ? firstPoint.Z : firstPoint.X;
            if (Core.Player.Location.Distance2D(firstPoint) > firstBudget || !SupportedSegment(Core.Player.Location, firstPoint)) continue;
            foreach (float end in firstLanes.OrderBy(x => Math.Abs(x - start)))
            {
                var secondPoint = At(end, along);
                if (end < -28 || end > 28 || secondLanes.Any(l => Math.Abs(end - l) < 6f) ||
                    !OutsideTail(secondPoint) || Math.Abs(end - start) > 8.5f ||
                    !SupportedSegment(firstPoint, secondPoint)) continue;
                icicleFirstPoint = firstPoint;
                icicleSecondPoint = secondPoint;
                // The damage effect is delayed; move after the first cast snapshot
                // so the second lane can be reached before its own snapshot.
                icicleFirstImpact = DateTime.UtcNow + first.Min(a => a.SpellCastInfo.RemainingCastTime) + TimeSpan.FromMilliseconds(100);
                icicleUntil = icicleFirstImpact + TimeSpan.FromSeconds(3.75);
                icicleOverride = true;
                icicleSecondChecked = false;
                arrived = false;
                Logger.Information("Menagerie: Spikesicle stages first={0} second={1} firstImpact={2:O}", firstPoint, secondPoint, icicleFirstImpact);
                return;
            }
        }
        nextIciclePlan = DateTime.UtcNow.AddMilliseconds(500);
        Logger.Information("Menagerie: Spikesicle has no floor-verified ordered route; native escape remains available");
    }

    private static bool SupportedSegment(Vector3 from, Vector3 to)
    {
        // A broken tile can retain a valid arena coordinate. Probe the actual collision
        // beneath each yalm of the direct segment; reject a lower floor or missing hit.
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to)));
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            var point = new Vector3(from.X + (to.X - from.X) * t, from.Y, from.Z + (to.Z - from.Z) * t);
            if (!WorldManager.AdvancedRaycast(point + new Vector3(0, 2, 0), point - new Vector3(0, 2, 0),
                    RaycastFlags.PassthruBoundaries, RaycastFlags.PassthruBoundaries, out var ground) ||
                Math.Abs(ground.Y - point.Y) > 0.75f) return false;
        }
        return true;
    }

    private static Vector3 CycloneStaging(Vector3 origin, Vector3 player)
    {
        // Radius8 stays outside the visible center and leaves a 15y final push within
        // the 30y platform. Prefer the player's current radial side to avoid crossing it.
        double baseAngle = Math.Atan2(player.X - origin.X, player.Z - origin.Z);
        Vector3 fallback = new(origin.X + (float)Math.Sin(baseAngle) * 8, origin.Y,
            origin.Z + (float)Math.Cos(baseAngle) * 8);
        for (int i = 0; i < 16; i++)
        {
            int signed = i == 0 ? 0 : (i + 1) / 2 * (i % 2 == 1 ? 1 : -1);
            double angle = baseAngle + signed * Math.PI / 8;
            var direction = new Vector3((float)Math.Sin(angle), 0, (float)Math.Cos(angle));
            var staging = origin + direction * 8;
            var landing = origin + direction * 23;
            double dx = staging.X - player.X, dz = staging.Z - player.Z;
            double lengthSquared = dx * dx + dz * dz;
            double t = lengthSquared < 0.001 ? 0 : Math.Clamp(((origin.X - player.X) * dx + (origin.Z - player.Z) * dz) / lengthSquared, 0, 1);
            double closestX = player.X + t * dx - origin.X, closestZ = player.Z + t * dz - origin.Z;
            bool crossesEye = player.Distance2D(origin) >= 5 && closestX * closestX + closestZ * closestZ < 25;
            if (!crossesEye && SupportedSegment(player, staging) && SupportedSegment(staging, landing))
            {
                Logger.Information("Menagerie: Cyclone floor-checked stage={0} landing={1}", staging, landing);
                return staging;
            }
        }
        // Collision may not yet be published when the cast starts. Preserve the outward
        // side as a bounded fallback; its broken-floor safety remains unverified.
        Logger.Warning("Menagerie: Cyclone has no floor-verified radial route; using unverified outward fallback {0}", fallback);
        return fallback;
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

}
