using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using P = DutyMechanic.Dungeons.AkhAfahHardPolicy;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Shiva's targeted hail, sequential icicles, rear dodge and slippery floor.
/// The standalone profile owns progression and targets; the routine retains job actions.
/// </summary>
public sealed partial class AkhAfahHard : AbstractDungeon
{
    private static AkhAfahHard current;

    // The October 28 capture distinguishes the targetable boss from same-name helpers:
    // C1C is Shiva, C21 the icicles and C22 the targeted Hailstorm/Diamond Dust helpers.
    private const uint ShivaBase = 0xC1C;

    // RB indexes capability leases by a single flag, despite the flags enum. Combining
    // values throws at the first ice hold and can prevent normal routine scheduling.
    private static readonly CapabilityFlags[] PositionCapabilities =
        [CapabilityFlags.Movement, CapabilityFlags.GapCloser, CapabilityFlags.MoveBehind];

    private readonly Dictionary<uint, P.Cast> casts = [];
    private readonly Dictionary<(uint Owner, uint Action), CircleSnapshot> envelopes = [];
    private CircleSnapshot[] circles = [];
    private MovementGate movement;
    private bool active;
    private bool holdingIce;
    private bool leased;
    private bool sideStepOwned;
    private bool previousSideStep;
    private bool unsupportedMoverReported;
    private double expectedIceFinish;
    private double nextSnapshot;
    private string previousStage = "";
    private Point? bashRear;

    // Native avoidance caches object identities. Update these scalar envelopes in place
    // so moving marks do not invalidate the escape route every time they are sampled.
    private sealed class CircleSnapshot
    {
        internal P.Hazard Geometry;
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)377;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    // Diamond Dust is a raidwide; Hard-mode Icebrand remains ordinary tank positioning.
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [2466];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        // Bot-thread pulses continue while a routine coroutine owns TreeStart. A one-second
        // Permafrost cast must not wait for an unrelated long action before stopping input.
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        AvoidanceHelpers.AddAvoidDonut(() => active && !holdingIce, () => Vector3.Zero, 80, P.Arena);
        AvoidanceManager.AddAvoidLocation<CircleSnapshot>(() => active && !holdingIce,
            () => Vector3.Zero, 40, h => h.Geometry.Radius,
            h => new Vector3(h.Geometry.Position.X, 0, h.Geometry.Position.Y), () => circles);
        Logger.Information("[AkhAfah] Hard-mode handler registered.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Unsubscribe();
        Reset();
        if (ReferenceEquals(current, this))
            current = null;

        return Task.FromResult(false);
    }

    /// <summary>
    /// Releases this encounter's pulse subscription, borrowed mover and capabilities when
    /// the plugin is disabled or the bot stops before normal dungeon exit can run.
    /// </summary>
    public static void ResetActive()
    {
        current?.Unsubscribe();
        current?.Reset();
        current = null;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        Update();
        await DamageMitigationSpells();
        // The mover/capability hold suppresses travel only. Returning false keeps healing,
        // mitigation and attacks available throughout Thin Ice and stationary escapes.
        return false;
    }

    private void OnPulse(object sender, EventArgs args) => Update();

    private void Update()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != 377 || !DutyManager.InInstance
            || Core.Me == null || !Core.Me.IsAlive || CommonBehaviors.IsLoading)
        {
            Reset();
            return;
        }

        BattleCharacter boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.BaseId == ShivaBase && a.IsAlive);
        if (boss == null)
        {
            Reset();
            return;
        }

        SuppressSideStep();
        active = Core.Me.InCombat;
        if (!active)
        {
            ClearCombat();
            return;
        }

        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (now >= nextSnapshot)
        {
            nextSnapshot = now + 0.1;
            Snapshot(now);
        }

        bool castingIce = casts.Values.Any(c => c.Action == P.Permafrost && c.Finish >= now);
        bool hold = P.HoldForIce(Core.Me.HasAura(P.ThinIce), castingIce, now, expectedIceFinish);
        EnsureMovement();
        Point? rear = P.BashRear(Xz(Core.Me.Location),
            casts.Values.FirstOrDefault(c => c.Action == P.GlacierBash), now);

        // Thin Ice and verified native hazards take precedence. The rear point is a
        // short traversal of the unobstructed arena floor, admitted by the same input
        // owner as routine travel. Never cross an active circle to reach it.
        if (rear.HasValue && !RearPathClear(Xz(Core.Me.Location), rear.Value))
            rear = null;
        if (rear.HasValue != bashRear.HasValue)
            Logger.Information($"[AkhAfah] Glacier Bash rear {(rear.HasValue ? "acquired" : "released")} at {Core.Me.Location} destination={rear}.");
        bashRear = rear;
        movement?.SetPosition(rear.HasValue ? new Vector3(rear.Value.X, 0, rear.Value.Y) : null);
        movement?.Update(hold);
        if (hold != holdingIce)
            Logger.Information($"[AkhAfah] Thin Ice hold {(hold ? "acquired" : "released")} at {Core.Me.Location}.");
        holdingIce = hold;

        if (hold || rear.HasValue)
        {
            foreach (CapabilityFlags capability in PositionCapabilities)
                CapabilityManager.Update(CapabilityHandle, capability, 500, "Akh Afah mechanic position");
            leased = true;
        }
        else if (leased)
        {
            foreach (CapabilityFlags capability in PositionCapabilities)
                CapabilityManager.Clear(CapabilityHandle, capability, "Akh Afah floor released");
            leased = false;
        }
    }

    private void Snapshot(double now)
    {
        HashSet<uint> seen = [];
        foreach (BattleCharacter actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false))
        {
            if (!actor.IsValid || !actor.IsAlive || !actor.IsCasting || !actor.SpellCastInfo.IsValid)
                continue;

            uint action = actor.CastingSpellId;
            if (action is not (P.Hailstorm or P.IcicleImpact or P.Permafrost or P.GlacierBash))
                continue;

            double finish = now + actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
            bool started = !casts.TryGetValue(actor.ObjectId, out var previous)
                || previous.Action != action || finish > previous.Finish + 1;
            casts[actor.ObjectId] = new(actor.ObjectId, action, Xz(actor.Location), actor.Heading,
                actor.SpellCastInfo.TargetId, finish);
            seen.Add(actor.ObjectId);
            if (action == P.Permafrost)
                expectedIceFinish = finish;
            if (started)
                Logger.Information($"[AkhAfah] Cast {action} helper={actor.ObjectId:X} target={actor.SpellCastInfo.TargetId:X} finish={finish:F3}.");
        }

        // Retain casts through their effect window, but discard early cancellations.
        // Bash needs the longer observed animation delay before ordinary travel resumes.
        foreach (var entry in casts.ToArray())
        {
            if (!seen.Contains(entry.Key) && (now > entry.Value.Finish +
                (entry.Value.Action == P.GlacierBash ? P.BashRecovery : P.Grace) || now < entry.Value.Finish - 0.35))
            {
                if (entry.Value.Action == P.Permafrost && now < entry.Value.Finish - 0.35)
                    expectedIceFinish = 0;
                casts.Remove(entry.Key);
            }
        }

        P.Member[] party = PartyManager.VisibleMembers.Select(m => m.BattleCharacter).Append(Core.Me)
            .Where(m => m != null && m.IsValid && m.IsAlive).DistinctBy(m => m.ObjectId)
            .Select(m => new P.Member(m.ObjectId, Xz(m.Location))).ToArray();
        P.Hazard[] hazards = P.Build(Core.Me.ObjectId, party, casts.Values.ToArray(), now);
        var live = new HashSet<(uint, uint)>();
        var current = new List<CircleSnapshot>();
        foreach (P.Hazard hazard in hazards)
        {
            var key = (hazard.Owner, hazard.Action);
            if (!envelopes.TryGetValue(key, out var envelope))
                envelopes[key] = envelope = new();
            envelope.Geometry = hazard;
            if (live.Add(key))
                current.Add(envelope);
        }
        foreach (var key in envelopes.Keys.Where(k => !live.Contains(k)).ToArray())
            envelopes.Remove(key);
        circles = current.ToArray();

        string stage = string.Join(",", hazards.Select(h => $"{h.Action}/{h.Owner:X}"));
        if (stage != previousStage)
        {
            Logger.Information($"[AkhAfah] Active hazards [{stage}] at {Core.Me.Location}.");
            previousStage = stage;
        }
    }

    private void EnsureMovement()
    {
        if (movement != null)
            return;

        if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
        {
            if (!unsupportedMoverReported)
                Logger.Warning("[AkhAfah] Thin Ice input gate unavailable: an unsupported mover owns travel.");
            unsupportedMoverReported = true;
            return;
        }

        movement = new MovementGate(Navigator.PlayerMover);
        Navigator.PlayerMover = movement;
    }

    private static Point Xz(Vector3 point) => new(point.X, point.Z);

    private bool RearPathClear(Point start, Point end)
    {
        // Half-yalm samples cover the short boss-relative traversal, including both
        // endpoints. The arena disk is convex, so an in-bounds endpoint stays on floor.
        int steps = Math.Max(1, (int)Math.Ceiling(Point.Distance(start, end) / 0.5f));
        for (int i = 0; i <= steps; ++i)
        {
            Point point = Point.Lerp(start, end, i / (float)steps);
            if (circles.Any(c => c.Geometry.Contains(point)))
                return false;
        }

        return true;
    }

    private void ClearCombat()
    {
        active = holdingIce = false;
        movement?.Detach();
        movement = null;
        if (leased)
        {
            foreach (CapabilityFlags capability in PositionCapabilities)
                CapabilityManager.Clear(CapabilityHandle, capability, "Akh Afah combat ended");
        }

        leased = false;
        bashRear = null;
        expectedIceFinish = nextSnapshot = 0;
        casts.Clear();
        envelopes.Clear();
        circles = [];
        previousStage = "";
    }

    private void SuppressSideStep()
    {
        if (SidestepPlugin == null)
            return;

        if (!sideStepOwned)
        {
            previousSideStep = SidestepPlugin.Enabled;
            sideStepOwned = true;
        }

        SidestepPlugin.Enabled = false;
    }

    private void Reset()
    {
        ClearCombat();
        if (sideStepOwned && SidestepPlugin != null)
            SidestepPlugin.Enabled = previousSideStep;
        sideStepOwned = unsupportedMoverReported = false;
    }

    private void Unsubscribe()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= OnPulse;
        TreeRoot.OnStop -= OnStop;
        GameEvents.OnMapChanged -= OnMapChanged;
    }

    private void OnStop(BotBase bot)
    {
        Unsubscribe();
        Reset();
    }

    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != 377)
        {
            Unsubscribe();
            Reset();
        }
    }
}
