using Clio.Utilities;
using DutyMechanic.Data;
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
/// Arbitrates SohmAl stack requests and escape cleanup on the existing mover channel.
/// It never chooses a geometric dodge destination or follows an NPC as a safe proxy.
/// </summary>
public partial class SohmAl
{
    private InputOwner inputOwner;
    private bool stackLeased;
    private bool reportedForeignMover;

    private void UpdateInputOwner()
    {
        if (inputOwner != null) return;
        // Do not layer controllers. This compatibility accommodation is limited to
        // the captured SlideMover; a replacement keeps ownership and receives no stop.
        if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
        {
            if (!reportedForeignMover)
                Logger.Information("[SohmAl] Another mover owns input; stack/escape adapter unavailable.");
            reportedForeignMover = true;
            return;
        }
        inputOwner = new InputOwner(Navigator.PlayerMover, OwnsHazards);
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
        if ((self && participants.Length == 0) || (!self && marked == null)) { ReleaseStack(); return false; }

        // A6y stack needs real participants even when the player is marked. Allow
        // 1y arrival tolerance and0.5y clearance; prefer coverage, then shortest travel.
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
                if (!Safe(new(from.X + (p.X - from.X) * t, p.Y, from.Z + (p.Z - from.Z) * t))) return false;
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
        if (best == null) { ReleaseStack(); return false; }
        if (stackDestination == null || Coverage(stackDestination.Value) < Coverage(best.Value) ||
            !Safe(stackDestination.Value) || !Reachable(stackDestination.Value)) stackDestination = best;

        // RB validates one named capability per call, even though the enum is flags.
        // Separate leases allow rotation while preventing stack-breaking travel.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement,
            TimeSpan.FromSeconds(1), "SohmAl Mad Dash participation");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser,
            TimeSpan.FromSeconds(1), "SohmAl Mad Dash participation");
        if (!stackLeased) Logger.Information("[SohmAl] Mad Dash stack acquired target={0} self={1}.", stackTarget, self);
        stackLeased = true;
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
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "SohmAl stack released");
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "SohmAl stack released");
            Logger.Information("[SohmAl] Mad Dash stack released.");
        }
        stackLeased = false;
    }

    // One synchronous adapter observes all commands, including successors from combat
    // and routing. A newer command revokes an older cleanup claim before forwarding.
    private sealed class InputOwner : IPlayerMover
    {
        private enum Command { None, Ordinary, Escape, Stack, StackHold }
        private readonly IPlayerMover inner;
        private readonly Func<bool> ownsEncounter;
        private bool detached, submittingStack;
        private Command command;
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal InputOwner(IPlayerMover inner, Func<bool> ownsEncounter)
        {
            this.inner = inner;
            this.ownsEncounter = ownsEncounter;
        }

        /// <summary>Forwards an unchanged destination and replaces the previous input claim.</summary>
        /// <param name="location">Destination selected by native avoidance, a stack request or ordinary combat.</param>
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent) return;
            command = ownsEncounter() && AvoidanceManager.IsRunningOutOfAvoid ? Command.Escape :
                submittingStack ? Command.Stack : Command.Ordinary;
            inner.MoveTowards(location);
        }

        /// <summary>Forwards explicit stop, revoking any previous cleanup claim.</summary>
        public void MoveStop()
        {
            if (!IsCurrent) return;
            command = Command.None;
            inner.MoveStop();
        }

        internal void MoveStack(Vector3 destination)
        {
            if (!IsCurrent || !ownsEncounter() || AvoidanceManager.IsRunningOutOfAvoid) return;
            submittingStack = true;
            try { Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = .7f, UseMount = false }); }
            finally { submittingStack = false; }
        }

        internal void HoldStack()
        {
            if (!IsCurrent || !ownsEncounter() || AvoidanceManager.IsRunningOutOfAvoid || command == Command.StackHold) return;
            // This is acquisition of the current semantic request, not a delayed
            // cleanup callback. Its exclusive capability already suppresses combat travel.
            MoveStop();
            command = Command.StackHold;
        }

        internal void ReleaseStack()
        {
            if (!IsCurrent || command is not (Command.Stack or Command.StackHold)) return;
            if (!AvoidanceManager.IsRunningOutOfAvoid) MoveStop();
            else command = Command.None; // Native egress supersedes the semantic request.
        }

        internal void CompleteEscape()
        {
            // RB1.0.941 clears its escape path without releasing forward input.
            // Stop only a still-current native command after hazards/forced motion end.
            if (!IsCurrent || command != Command.Escape || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl ||
                AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location))) return;
            MoveStop();
            Logger.Information("[SohmAl] Released completed native escape input at " + Core.Me.Location + ".");
        }

        internal void Detach()
        {
            if (IsCurrent)
            {
                if (command is Command.Escape or Command.Stack or Command.StackHold) MoveStop();
                Navigator.PlayerMover = inner;
            }
            command = Command.None;
            detached = true;
        }
    }
}
