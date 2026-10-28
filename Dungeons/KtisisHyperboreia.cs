using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
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
/// Handles Ktisis boss mechanics through native avoidance and one movement owner
/// for stacks, meteor shelters and safe holds. The standalone duty profile owns
/// routing and recovery; encounter signals determine positioning without NPC following.
/// </summary>
public partial class KtisisHyperboreia : AbstractDungeon
{
    private static KtisisHyperboreia active;
    private bool moving;
    private bool leased;
    private bool gapCloserLeased;
    private string movementReason;
    private DateTime frostUntil;
    private DateTime hermeticaUntil;
    private DateTime meteorUntil;
    private DateTime stackUntil;
    private uint stackTarget;
    private Vector3? stackDestination;
    private uint previousBossCast;
    private uint previousSubZone;
    private bool? savedSideStepEnabled;
    private uint interruptCaster;
    private bool interruptSubmitted;
    private DateTime nextInterruptAttempt;

    private static readonly Vector3 LyssaCenter = new(-144, 496, 49);
    private static readonly Vector3 LadonCenter = new(0, 630, 48);
    private static readonly Vector3 HermesCenter = new(0, 0, -50);

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.KtisisHyperboreia;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 974 && Core.Me != null &&
        Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat;

    /// <summary>Releases Ktisis capability leases and restores SideStep on stop, disable or hook replacement.</summary>
    public static void ResetActive()
    {
        active?.Reset();
        active = null;
    }

    /// <summary>Clears stale positioning when death or loading bypasses the encounter hook.</summary>
    public static void CheckLifecycle()
    {
        if (active != null && !Live())
        {
            ResetActive();
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        Reset();
        // Ladon's room needs a square boundary: its corners provide safe space
        // during cone/cross overlaps. Lyssa and Hermes use circular rooms.
        AvoidanceHelpers.AddAvoidDonut(() => Live() && WorldManager.SubZoneId == 3766, () => LyssaCenter, 90, 19, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidSquareDonut(() => Live() && WorldManager.SubZoneId == 3767,
            38, 38, 180, 180, () => new[] { LadonCenter }, AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(() => Live() && WorldManager.SubZoneId == 3768, () => HermesCenter, 90, 19, AvoidancePriority.High);
        RegisterHazards();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Reset();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync() => Task.FromResult(Tick());

    private bool Tick()
    {
        if (!Live())
        {
            Reset();
            return false;
        }

        active = this;
        uint subZone = WorldManager.SubZoneId;
        if (previousSubZone != subZone)
        {
            Reset();
            previousSubZone = subZone;
        }

        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive).ToArray();
        var casts = actors.Where(a => a.IsCasting && a.SpellCastInfo.IsValid).ToArray();
        // Hidden damage helpers reuse the boss NameID; BaseId identifies the
        // real boss even when it becomes untargetable during choreography.
        var boss = actors.FirstOrDefault(a => a.BaseId == (subZone == 3766 ? 0x3323u : subZone == 3767 ? 0x3425u : 0x348Au));
        uint bossCast = boss != null && boss.IsCasting && boss.SpellCastInfo.IsValid ? boss.CastingSpellId : 0;
        var now = DateTime.UtcNow;
        bool bossRoom = subZone is 3766 or 3767 or 3768;
        SetBossOwnership(bossRoom && boss != null);
        UpdateHazards(actors, casts, boss, now);
        if (subZone == 3766)
        {
            // The footprint record forecasts the attack before its short cast.
            // Edge triggering bounds stale map state after an interrupted phase.
            if (bossCast == ActionId.Frostbite && previousBossCast != bossCast)
            {
                frostUntil = now.AddSeconds(20);
            }

            var slice = casts.FirstOrDefault(a => a.CastingSpellId == ActionId.PunishingSlice);
            if (slice != null)
            {
                frostUntil = now + slice.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.5);
            }
        }

        if (subZone == 3768)
        {
            // Only the meteor-blocked volley requires positive shelter guidance.
            // Ordinary and quadruple lanes belong exclusively to native geometry.
            if (bossCast == ActionId.DoubleHermetica && previousBossCast != bossCast)
            {
                hermeticaUntil = now.AddSeconds(30);
            }
            else if (bossCast != 0 && bossCast != ActionId.DoubleHermetica)
            {
                hermeticaUntil = DateTime.MinValue;
            }

            // The actual meteor helper owns impact time. The half-second tail
            // covers effect delivery; the following Hermetica takes shelter priority.
            foreach (var cast in casts.Where(a => a.CastingSpellId is ActionId.Meteor or ActionId.CosmicKiss))
            {
                var impact = now + cast.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.5);
                if (impact > meteorUntil)
                {
                    meteorUntil = impact;
                }
            }
        }

        previousBossCast = bossCast;
        UpdateEncounterSignals(actors, casts, now);
        var stack = casts.FirstOrDefault(a => a.CastingSpellId is ActionId.HeavySmash or ActionId.PyricBlast);
        if (stack != null)
        {
            if (stackTarget != stack.SpellCastInfo.TargetId)
            {
                stackDestination = null;
            }

            stackTarget = stack.SpellCastInfo.TargetId;
            // Pyric Blast damage arrived about 1.1s after cast-bar finish in the
            // live capture. Hold the stack for 1.5s to cover effect delivery.
            stackUntil = now + stack.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
        }

        UpdateGapCloserGate(bossRoom && (ActiveHazards().Any() || now < stackUntil || now < frostUntil || now < meteorUntil || now < hermeticaUntil));
        // Interrupt independently of routine settings. Only a successful dispatch
        // consumes this tick; ordinary rotation remains available on later ticks.
        if (TryInterruptTrueBravery(boss, now))
        {
            return true;
        }

        // Native egress always wins; releasing our request must not stop its input.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            ReleaseMovement();
            return false;
        }

        if (subZone == 3766 && now < frostUntil && ActiveHazards().Any(h => h.Action == ActionId.PunishingSlice))
        {
            return HoldFrostbiteSafety();
        }

        if (subZone == 3768 && now < hermeticaUntil && now >= meteorUntil)
        {
            return ShelterFromVolley(now);
        }

        if (subZone == 3768 && now < meteorUntil)
        {
            return PositionAt(HermesCenter, "Meteor proximity staging");
        }

        if (now < stackUntil)
        {
            if (stackTarget == Core.Me.ObjectId)
            {
                return StackWithParty(actors);
            }

            var target = actors.FirstOrDefault(a => a.ObjectId == stackTarget);
            // Pillar rays and sphere crosses resolve before the stack. Only
            // approach a target outside active hazards; otherwise native egress
            // retains priority until the earlier mechanic has cleared.
            if (target != null)
            {
                return StackAt(target.Location);
            }

            // An unresolved target is not evidence that an unrelated ally is safe.
            // Yield to ordinary combat and reacquire the actual target next tick.
            ReleaseMovement();
            return false;
        }

        // Aero repeats are harmful ground telegraphs, never a request to return
        // to the original bait point. Native geometry owns both spread and repeat.
        ReleaseMovement();
        return false;
    }

    private bool TryInterruptTrueBravery(BattleCharacter boss, DateTime now)
    {
        if (WorldManager.SubZoneId != 3768 || boss == null || boss.BaseId != 0x348A ||
            !boss.IsCasting || !boss.SpellCastInfo.IsValid || boss.CastingSpellId != ActionId.TrueBravery)
        {
            ResetInterrupt();
            return false;
        }

        if (interruptCaster != boss.ObjectId)
        {
            ResetInterrupt();
            interruptCaster = boss.ObjectId;
        }

        // Never cancel our own cast, retarget, face, move into range, or compete
        // with native escape. Retry transient action locks on later bot ticks.
        if (interruptSubmitted || now < nextInterruptAttempt || !boss.IsTargetable ||
            !boss.SpellCastInfo.Interruptible || Core.Me.IsCasting || AvoidanceManager.IsRunningOutOfAvoid)
        {
            return false;
        }

        uint actionId = InterruptForJob(Core.Me.CurrentJob);
        // Use the same known-action gate as the routine. Native CanCast then
        // validates availability, cooldown, range, LOS and action-lock state.
        if (actionId == 0 || !ActionManager.HasSpell(actionId))
        {
            return false;
        }

        nextInterruptAttempt = now.AddMilliseconds(250); // Bound rejected native requests without blocking combat.
        if (!ActionManager.CanCast(actionId, boss))
        {
            return false;
        }

        var action = DataManager.GetSpellData(actionId);
        if (action == null || !ActionManager.DoAction(action, boss))
        {
            return false;
        }

        interruptSubmitted = true; // One accepted request per cast, even before the cooldown cache updates.
        Logger.Information("[Ktisis] True Bravery interrupt submitted: job={0} action={1} caster=0x{2:X} remainingMs={3:F0}.",
            Core.Me.CurrentJob, actionId, boss.ObjectId, boss.SpellCastInfo.RemainingCastTime.TotalMilliseconds);
        return true;
    }

    private static uint InterruptForJob(ClassJobType job) => job switch
    {
        // Verified role-action rows: Interject 7538 for tanks; Head Graze 7551
        // for physical ranged. Stuns are not substitutes for boss interrupts.
        ClassJobType.Gladiator or ClassJobType.Paladin or ClassJobType.Marauder or
            ClassJobType.Warrior or ClassJobType.DarkKnight or ClassJobType.Gunbreaker => 7538,
        ClassJobType.Archer or ClassJobType.Bard or ClassJobType.Machinist or ClassJobType.Dancer => 7551,
        // Flying Sardine 11423 is BLU's learned/equipped interrupt. This mapping
        // does not bypass the client's duty eligibility or spell availability.
        ClassJobType.BlueMage => 11423,
        _ => 0 // Healers, melee and the other casters have no corresponding PvE interrupt.
    };

    private void ResetInterrupt()
    {
        interruptCaster = 0;
        interruptSubmitted = false;
        nextInterruptAttempt = DateTime.MinValue;
    }

    private void UpdateGapCloserGate(bool required)
    {
        // Native egress releases Movement on arrival, but a gap closer can still
        // cross live hazards from that safe position. Magitek honors GapCloser
        // separately, so retain this gate without blocking navigation or rotation.
        if (required)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Ktisis active mechanic gap-closer gate");
            gapCloserLeased = true;
        }
        else if (gapCloserLeased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Ktisis mechanic resolved");
            gapCloserLeased = false;
        }
    }

    private bool StackAt(Vector3 target)
    {
        // A shared safe point can satisfy the stack before the rays/cross clear.
        // Keep candidates within 4.5y of the 6y target: the 1y arrival tolerance
        // leaves 0.5y inside the stack. If no candidate is safe, wait for egress.
        bool Safe(Vector3 p) => p.Distance2D(target) <= 4.5f && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        if (stackDestination == null || !Safe(stackDestination.Value))
        {
            var candidates = new List<Vector3> { Core.Me.Location, target };
            foreach (float radius in new[] { 2f, 4f })
            {
                for (int i = 0; i < 16; ++i)
                {
                    float angle = i * (float)Math.PI / 8;
                    candidates.Add(new(target.X + radius * (float)Math.Sin(angle), target.Y, target.Z + radius * (float)Math.Cos(angle)));
                }
            }

            stackDestination = candidates.Where(Safe).OrderBy(p => p.Distance2D(Core.Me.Location))
                .Select(p => (Vector3?)p).FirstOrDefault();
        }

        if (stackDestination == null)
        {
            ReleaseMovement();
            return false;
        }

        return PositionAt(stackDestination.Value, "Heavy Smash/Pyric Blast stack");
    }

    private bool StackWithParty(BattleCharacter[] actors)
    {
        // A self-targeted stack needs nearby participants. Move into the group
        // rather than treating the carrier's current position as the destination.
        var participants = actors.Where(a => a.ObjectId != Core.Me.ObjectId && a.IsVisible &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)))
            .Select(a => a.Location).ToArray();
        if (participants.Length == 0)
        {
            ReleaseMovement();
            return false;
        }

        // The 6y stack radius retains 1y arrival tolerance plus 0.5y margin.
        int Coverage(Vector3 point) => participants.Count(p => p.Distance2D(point) <= 4.5f);
        bool Safe(Vector3 point) => !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(point));
        bool Reachable(Vector3 point)
        {
            // Earlier sphere crosses can separate two individually safe cells.
            // Do not ask navigation to cross them to satisfy the later stack.
            // Half-yalm sampling is smaller than every Ktisis stack-overlap lane.
            var start = Core.Me.Location;
            int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(point) / .5f));
            for (int i = 0; i <= steps; ++i)
            {
                float t = (float)i / steps;
                if (!Safe(new(start.X + (point.X - start.X) * t, point.Y, start.Z + (point.Z - start.Z) * t)))
                {
                    return false;
                }
            }

            return true;
        }

        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (var participant in participants)
        {
            candidates.Add(participant);
            foreach (float radius in new[] { 2f, 4f })
            {
                for (int i = 0; i < 16; ++i)
                {
                    float angle = i * (float)Math.PI / 8;
                    candidates.Add(new(participant.X + radius * (float)Math.Sin(angle), participant.Y,
                        participant.Z + radius * (float)Math.Cos(angle)));
                }
            }
        }

        var best = candidates.Where(p => Coverage(p) > 0 && Safe(p) && Reachable(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (best == null)
        {
            ReleaseMovement();
            return false;
        }

        // Retain an equally good destination as allies settle; reacquire after
        // hazard expiry or when a better-covered position becomes reachable.
        if (stackDestination == null || Coverage(stackDestination.Value) < Coverage(best.Value) ||
            !Safe(stackDestination.Value) || !Reachable(stackDestination.Value))
        {
            stackDestination = best;
            Logger.Information("[Ktisis] Self stack: participants={0}/{1} destination={2}.", Coverage(best.Value), participants.Length, best.Value);
        }

        return PositionAt(stackDestination.Value, "Self-targeted Heavy Smash/Pyric Blast stack");
    }

    private bool HoldFrostbiteSafety()
    {
        // After native egress, combat movement would approach the underground
        // boss and re-enter the slice. Hold the safe exit while allowing combat;
        // release immediately if another hazard requires native egress.
        // Native avoids can lag a new forecast by one pulse, so check its current
        // rectangle too before treating the player position as safe.
        if (ActiveHazards().Any(h => h.Action == ActionId.PunishingSlice && InsideSlice(h, Core.Me.Location)) ||
            AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(Core.Me.Location)))
        {
            ReleaseMovement();
            return false;
        }

        const string reason = "Frostbite safe hold";
        bool handoff = movementReason != reason;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        leased = true;
        movementReason = reason;
        if (handoff)
        {
            // Acquiring the exclusive hold also neutralizes a lingering combat
            // navigation input. This runs only after native egress has released.
            Navigator.PlayerMover.MoveStop();
            moving = false;
            Logger.Information("[Ktisis] Holding native Frostbite exit at {0} until the slice resolves.", Core.Me.Location);
        }

        return false; // Healing, mitigation and legal ranged actions remain schedulable.
    }

    private static bool InsideSlice(Hazard slice, Vector3 point)
    {
        float dx = point.X - slice.Origin.X, dz = point.Z - slice.Origin.Z;
        float sin = (float)Math.Sin(slice.Heading), cos = (float)Math.Cos(slice.Heading);
        float side = dx * cos - dz * sin, forward = dx * sin + dz * cos;
        // Rectangle() supplies the padded bounds in local XZ coordinates; reuse
        // those vertices so hold gating and registered danger cannot diverge.
        return side >= slice.Points[0].X && side <= slice.Points[1].X && forward >= slice.Points[0].Y && forward <= slice.Points[2].Y;
    }

    private bool PositionAt(Vector3 destination, string reason)
    {
        if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(destination)))
        {
            ReleaseMovement();
            return false;
        }

        if (movementReason != reason)
        {
            Logger.Information("[Ktisis] Positioning: {0}", reason);
        }

        movementReason = reason;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        leased = true;
        if (Core.Me.Location.Distance2D(destination) <= 1)
        {
            StopOwnedMovement();
            return false; // Holding still schedules rotation, healing and mitigation.
        }

        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = .7f, UseMount = false });
        moving = true;
        return true;
    }

    private void StopOwnedMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        moving = false;
    }

    private void ReleaseMovement()
    {
        StopOwnedMovement();
        if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Ktisis positioning ended");
        }

        leased = false;
        movementReason = null;
    }

    private void Reset()
    {
        ResetInterrupt();
        UpdateGapCloserGate(false);
        ReleaseMovement();
        hazards.Clear();
        ResetEncounterSignals();
        chargedHeads.Clear();
        previousHeadStatuses.Clear();
        headClearAfter = DateTime.MinValue;
        SetBossOwnership(false);
        frostUntil = hermeticaUntil = meteorUntil = stackUntil = DateTime.MinValue;
        stackTarget = 0;
        stackDestination = null;
        LoggingHelpers.ClearActorSignalWatch("Ktisis heads/meteors/interrupt");
        previousBossCast = previousSubZone = 0;
    }

    private void SetBossOwnership(bool enabled)
    {
        // Suppress duplicate boss avoids while this handler owns the encounter.
        // Restore the prior setting on every exit, including wipe and stop.
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Ktisis] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            if (SidestepPlugin != null)
            {
                SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            }

            savedSideStepEnabled = null;
            hazards.Clear();
        }
    }

    // Choreography and damage helpers can share names but require different responses.
    private static class ActionId
    {
        internal const uint Frostbite = 25175;
        internal const uint PunishingSlice = 25177;
        internal const uint Icicall = 25178;
        internal const uint IcePillar = 25179;
        internal const uint HeavySmash = 25180;
        internal const uint PillarPierce = 25375;
        internal const uint BreathFront = 25734;
        internal const uint BreathLeft = 25735;
        internal const uint BreathRight = 25736;
        internal const uint BreathFrontRepeat = 25737;
        internal const uint BreathRightRepeat = 25739;
        internal const uint PyricBlast = 25742;
        internal const uint PyricSphere = 25745;
        internal const uint Hermetica = 25888;
        internal const uint AeroIV = 25889;
        internal const uint Meteor = 25890;
        internal const uint CosmicKiss = 25891;
        internal const uint DoubleHermetica = 25893;
        internal const uint QuadrupleHermetica = 25895;
        internal const uint AeroIIVisual = 25896;
        internal const uint AeroIISpread = 25897;
        internal const uint AeroIIRepeat = 25898;
        internal const uint AeroBait = 25899;
        internal const uint AeroRepeat = 25901;
        internal const uint Tornado = 25902;
        internal const uint TornadoRepeat = 25906;
        internal const uint TrueBravery = 25907;
        internal const uint MeteorVolley = 27836;
        internal const uint QuadrupleLane = 27837;
    }
}
