using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Arbitrates Aery transfer, knockback, stack requests and native escape cleanup.
/// It never chooses a geometric dodge destination or follows an NPC as a safe proxy.
/// </summary>
public partial class Aery
{
    private InputOwner inputOwner;
    private bool stackLeased;
    private bool reportedForeignMover;
    private uint transferStatue;
    private string semanticKind;

    private static bool HasLightningRod(BattleCharacter actor) => actor.Auras != null &&
        actor.Auras.IsValid && actor.Auras.Any(a => a.Id == 2574);

    // The actual 15s Sable Weave target identifies the captive without guessing one
    // of the many client statuses named Fetters. A missing/dead prison releases it.
    private bool IsImprisoned() => encounter == Nidhogg &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a =>
            a.IsValid && a.IsAlive && a.BaseId == 0x39CB && Casting(a) &&
            a.CastingSpellId == ActionId.Prison && a.SpellCastInfo.TargetId == Core.Me.ObjectId);

    private bool SafePosition(Vector3 p) => p.Distance2D(ArenaCenter) < ArenaRadius &&
        !ActiveHazards().Any(h => Contains(h, p)) &&
        !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));

    private bool SafePath(Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = (float)i / steps;
            if (!SafePosition(new(from.X + (to.X - from.X) * t, to.Y, from.Z + (to.Z - from.Z) * t)))
                return false;
        }
        return true;
    }

    private bool PositionForMechanic()
    {
        if (!OwnsHazards() || inputOwner?.IsCurrent != true || IsImprisoned() ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseStack();
            inputOwner?.SuspendOwnedInput();
            return false;
        }
        if (encounter == Rangda && HasLightningRod(Core.Me))
        {
            var statues = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.BaseId == 0xEA8 &&
                    !HasLightningRod(a) && a.Location.Distance2D(ArenaCenter) < 30).ToArray();
            // Keep an eligible target across pulses. Evaluate path safety before
            // acquiring a replacement, never select the last arbitrary visible object.
            var candidates = new List<(uint Id, Vector3 Position)>();
            foreach (var statue in statues)
                for (int i = 0; i < 32; ++i)
                {
                    double angle = i * Math.PI / 16;
                    candidates.Add((statue.ObjectId, new(statue.X + 3.5f * (float)Math.Sin(angle),
                        statue.Y, statue.Z + 3.5f * (float)Math.Cos(angle))));
                }
            var best = candidates.Where(c => SafePosition(c.Position) && SafePath(Core.Me.Location, c.Position))
                .OrderBy(c => c.Id == transferStatue ? 0 : 1).ThenBy(c => c.Position.Distance2D(Core.Me.Location))
                .Select(c => ((uint Id, Vector3 Position)?)c).FirstOrDefault();
            if (best == null)
            {
                ReleaseStack();
                return false;
            }
            transferStatue = best.Value.Id;
            return MoveSemantic(best.Value.Position, "transfer");
        }
        transferStatue = 0;
        if (encounter == Gyascutus && currentBoss != null && Casting(currentBoss) &&
            currentBoss.CastingSpellId == ActionId.BodySlam)
        {
            // Stage for the 10y outward displacement, keeping both path and landing
            // clear. The actual knockback remains game-owned; never countersteer it.
            var origin = currentBoss.Location;
            bool LandingSafe(Vector3 p)
            {
                float distance = p.Distance2D(origin);
                if (distance < .5f)
                    return false; // No defined outward direction at exact center.
                var landing = new Vector3(p.X + 10 * (p.X - origin.X) / distance,
                    p.Y, p.Z + 10 * (p.Z - origin.Z) / distance);
                return SafePosition(landing) && SafePath(p, landing);
            }
            var candidates = new List<Vector3> { Core.Me.Location };
            foreach (float radius in new[] { 1f, 3f, 5f, 7f })
                for (int i = 0; i < 32; ++i)
                {
                    double angle = i * Math.PI / 16;
                    candidates.Add(new(origin.X + radius * (float)Math.Sin(angle), origin.Y,
                        origin.Z + radius * (float)Math.Cos(angle)));
                }
            var best = candidates.Where(p => LandingSafe(p) && SafePosition(p) && SafePath(Core.Me.Location, p))
                .OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
            if (best == null)
            {
                ReleaseStack();
                return false;
            }
            return MoveSemantic(best.Value, "knockback");
        }
        return PositionForStack();
    }

    private bool MoveSemantic(Vector3 destination, string kind)
    {
        if (semanticKind != kind)
            ReleaseStack();
        semanticKind = kind;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Aery " + kind);
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Aery " + kind);
        if (!stackLeased)
            Logger.Information("[Aery] Semantic acquired kind={0} destination={1}.", kind, destination);
        stackLeased = true;
        if (Core.Me.Location.Distance2D(destination) <= .7f)
        {
            inputOwner.HoldStack();
            return false;
        }
        inputOwner.MoveStack(destination);
        return true;
    }

    private void UpdateInputOwner()
    {
        if (inputOwner != null)
            return;
        // Do not layer controllers. This compatibility accommodation is limited to
        // the captured SlideMover; a replacement keeps ownership and receives no stop.
        if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
        {
            if (!reportedForeignMover)
                Logger.Information("[Aery] Another mover owns input; stack/escape adapter unavailable.");
            reportedForeignMover = true;
            return;
        }
        inputOwner = new InputOwner(Navigator.PlayerMover, OwnsHazards, () => !IsImprisoned());
        Navigator.PlayerMover = inputOwner;
        reportedForeignMover = false;
    }

    private bool PositionForStack()
    {
        if (!OwnsHazards() || stackTarget == 0 || DateTime.UtcNow >= stackUntil ||
            inputOwner?.IsCurrent != true || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseStack();
            return false;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible).ToArray();
        var participants = actors.Where(a => a.ObjectId != Core.Me.ObjectId &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).Select(a => a.Location).ToArray();
        var marked = actors.FirstOrDefault(a => a.ObjectId == stackTarget);
        bool self = stackTarget == Core.Me.ObjectId;
        if ((self && participants.Length == 0) || (!self && marked == null))
        {
            ReleaseStack();
            return false;
        }

        // A 6y stack needs real participants even when the player is marked. Allow
        // 1y arrival tolerance and 0.5y clearance; prefer coverage, then shortest travel.
        int Coverage(Vector3 p) => self ? participants.Count(a => a.Distance2D(p) <= 4.5f) : p.Distance2D(marked.Location) <= 4.5f ? 1 : 0;
        bool Safe(Vector3 p) => p.Distance2D(ArenaCenter) < ArenaRadius &&
            !ActiveHazards().Any(h => Contains(h, p)) &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));
        bool Reachable(Vector3 p)
        {
            var from = Core.Me.Location;
            int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(p) / .5f));
            for (int i = 0; i <= steps; ++i)
            {
                float t = (float)i / steps;
                if (!Safe(new(from.X + (p.X - from.X) * t, p.Y, from.Z + (p.Z - from.Z) * t)))
                    return false;
            }
            return true;
        }
        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (var center in self ? participants : new[] { marked.Location })
        {
            candidates.Add(center);
            for (int i = 0; i < 16; ++i)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new(center.X + 4 * (float)Math.Sin(angle), center.Y, center.Z + 4 * (float)Math.Cos(angle)));
            }
        }
        var best = candidates.Where(p => Coverage(p) > 0 && Safe(p) && Reachable(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        if (best == null)
        {
            ReleaseStack();
            return false;
        }
        if (stackDestination == null || Coverage(stackDestination.Value) < Coverage(best.Value) ||
            !Safe(stackDestination.Value) || !Reachable(stackDestination.Value))
            stackDestination = best;

        // RB validates one named capability per call, even though the enum is flags.
        // Separate leases allow rotation while preventing stack-breaking travel.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement,
            TimeSpan.FromSeconds(1), "Aery Horrid Blaze participation");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser,
            TimeSpan.FromSeconds(1), "Aery Horrid Blaze participation");
        if (!stackLeased)
            Logger.Information("[Aery] Horrid Blaze stack acquired target={0} self={1}.", stackTarget, self);
        stackLeased = true;
        semanticKind = "stack";
        if (Core.Me.Location.Distance2D(stackDestination.Value) <= 1)
        {
            inputOwner.HoldStack();
            return false; // Holding never starves healing, mitigation or rotation.
        }
        inputOwner.MoveStack(stackDestination.Value);
        return true;
    }

    private void ReleaseStack()
    {
        inputOwner?.ReleaseStack();
        if (stackLeased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Aery semantic movement released");
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Aery semantic movement released");
            Logger.Information("[Aery] Semantic released kind={0}.", semanticKind);
        }
        stackLeased = false;
        semanticKind = null;
    }

    // One synchronous adapter observes all commands, including successors from combat
    // and routing. A newer command revokes an older cleanup claim before forwarding.
    private sealed class InputOwner : IPlayerMover
    {
        private enum Command
        {
            None, Ordinary, Escape, Stack, StackHold
        }
        private readonly IPlayerMover inner;
        private readonly Func<bool> ownsEncounter;
        private readonly Func<bool> canMove;
        private bool detached, submittingStack;
        private Command command;
        private DateTime nextStopDiagnostic;
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        // The existing native owner may need the tree to yield ordinary movement,
        // but explicit stops still pass through MoveStop unchanged. Prison, forced
        // motion and a replacement mover must never inherit that scheduling claim.
        internal bool HasNativeEscapePriority => IsCurrent && ownsEncounter() && canMove() &&
            AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;
        internal InputOwner(IPlayerMover inner, Func<bool> ownsEncounter, Func<bool> canMove)
        {
            this.inner = inner;
            this.ownsEncounter = ownsEncounter;
            this.canMove = canMove;
        }

        /// <summary>Forwards an unchanged destination and replaces the previous input claim.</summary>
        /// <param name="location">Destination selected by native avoidance, a stack request or ordinary combat.</param>
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent)
                return;
            if (ownsEncounter() && !canMove())
            {
                SuspendOwnedInput();
                return;
            }
            command = ownsEncounter() && AvoidanceManager.IsRunningOutOfAvoid ? Command.Escape :
                submittingStack ? Command.Stack : Command.Ordinary;
            inner.MoveTowards(location);
        }

        /// <summary>Forwards explicit stop, revoking any previous cleanup claim.</summary>
        public void MoveStop()
        {
            if (!IsCurrent)
                return;
            // The Cauterize capture kept the adapter current but repeatedly observed
            // no command while native escape was active. Identify the actual caller
            // before changing arbitration: autorun and ActiveInputControl are separate
            // RB channels, and a stop may also be legitimate prison/recovery cleanup.
            // This bounded developer trace never suppresses or changes the stop.
            if (LoggingHelpers.MechanicDiagnosticsEnabled && ownsEncounter() &&
                AvoidanceManager.IsRunningOutOfAvoid && DateTime.UtcNow >= nextStopDiagnostic)
            {
                nextStopDiagnostic = DateTime.UtcNow.AddSeconds(1);
                string trace = Environment.StackTrace;
                Logger.Information("[Aery] STOP_DURING_ESCAPE previous={0} player={1} blocked={2} caller={3}",
                    command, Core.Me.Location, !canMove(), trace.Length > 2500 ? trace.Substring(0, 2500) : trace);
            }
            command = Command.None;
            inner.MoveStop();
        }

        internal void MoveStack(Vector3 destination)
        {
            if (!IsCurrent || !ownsEncounter() || AvoidanceManager.IsRunningOutOfAvoid)
                return;
            submittingStack = true;
            try
            {
                Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = .7f, UseMount = false });
            }
            finally { submittingStack = false; }
        }

        internal void HoldStack()
        {
            if (!IsCurrent || !ownsEncounter() || AvoidanceManager.IsRunningOutOfAvoid || command == Command.StackHold)
                return;
            // This is acquisition of the current semantic request, not a delayed
            // cleanup callback. Its exclusive capability already suppresses combat travel.
            MoveStop();
            command = Command.StackHold;
        }

        internal void ReleaseStack()
        {
            if (!IsCurrent || command is not (Command.Stack or Command.StackHold))
                return;
            if (!AvoidanceManager.IsRunningOutOfAvoid)
                MoveStop();
            else
                command = Command.None; // Native egress supersedes the semantic request.
        }

        internal void CompleteEscape()
        {
            // RB 1.0.941 clears its escape path without releasing forward input.
            // Stop only a still-current native command after hazards/forced motion end.
            if (!IsCurrent || command != Command.Escape || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl ||
                AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location)))
                return;
            MoveStop();
            Logger.Information("[Aery] Released completed native escape input at " + Core.Me.Location + ".");
        }

        internal void SuspendOwnedInput()
        {
            // Only imprisonment blocks the native channel. Ordinary cast locks or
            // knockback flags must not be treated as permission to cancel a successor.
            if (IsCurrent && ownsEncounter() && !canMove() &&
                command is Command.Escape or Command.Stack or Command.StackHold)
                MoveStop();
        }

        internal void Detach()
        {
            if (IsCurrent)
            {
                if (command is Command.Escape or Command.Stack or Command.StackHold)
                    MoveStop();
                Navigator.PlayerMover = inner;
            }
            command = Command.None;
            detached = true;
        }
    }
}
