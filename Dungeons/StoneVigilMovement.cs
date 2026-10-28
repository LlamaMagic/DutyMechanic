using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Keeps native escape input bounded to the owning Stone Vigil encounter without
/// replacing RB's path selection or the combat routine's movement commands.
/// </summary>
public partial class StoneVigil
{
    private EscapeInputLease escapeInput;
    private bool reportedUnsupportedMover;

    // Both lane egress and flight egress use the affected native mover. Reuse one
    // cleanup channel for these bosses, retaining ownership through untargetable
    // phases. Captured boss base IDs exclude choreography helpers from the lease.
    private bool OwnsEscapeCleanup() => OwnsHazards() &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
            actor.IsValid && actor.IsAlive &&
            ((actor.NpcId == Koshchei && actor.BaseId == KoshcheiBossBase) ||
             (actor.NpcId == Isgebind && actor.BaseId == IsgebindBossBase)));

    // The October 28 Isgebind capture continued 31y after native escape completion.
    // RB clears its run-out path without releasing SlideMover's forward input.
    // Interpose at the existing mover channel only for this encounter: path choice,
    // facing, and every new destination still belong to RB/the combat routine.
    // This is input-lifetime cleanup, not a second movement loop or safe-point planner.
    private void UpdateEscapeInput()
    {
        if (!OwnsEscapeCleanup())
        {
            ReleaseEscapeInput();
            return;
        }

        if (escapeInput == null)
        {
            // Do not wrap another mechanic's controller or take back a replaced mover.
            // This compatibility repair is supported only for the captured SlideMover.
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
            {
                if (!reportedUnsupportedMover)
                {
                    Logger.Information("[StoneVigil] Escape cleanup unavailable: another mover owns input.");
                    reportedUnsupportedMover = true;
                }
                return;
            }

            escapeInput = new EscapeInputLease(Navigator.PlayerMover, OwnsEscapeCleanup);
            Navigator.PlayerMover = escapeInput;
        }

        escapeInput.CompleteEscape();
    }

    private void ReleaseEscapeInput()
    {
        escapeInput?.Detach();
        escapeInput = null;
        reportedUnsupportedMover = false;
    }

    // All calls are synchronous on RB's movement path. A newer non-escape command or
    // explicit stop revokes the cleanup claim before forwarding to the original mover.
    // Checking the installed wrapper identity also rejects stale callbacks and prevents
    // an old encounter from stopping/restoring over a successor. No native patches,
    // provider reset, capability lease, timer, or changes outside Stone Vigil are needed.
    private sealed class EscapeInputLease : IPlayerMover
    {
        private readonly IPlayerMover inner;
        private readonly Func<bool> encounterOwnsHazards;
        private bool escapeCommandOutstanding;
        private bool detached;
        private long nextDiagnostic;
        private Vector3 lastDestination;
        private int moveCommands, stopCommands;

        internal EscapeInputLease(IPlayerMover inner, Func<bool> encounterOwnsHazards)
        {
            this.inner = inner;
            this.encounterOwnsHazards = encounterOwnsHazards;
        }

        private bool Owns => !detached && ReferenceEquals(Navigator.PlayerMover, this);

        /// <summary>Forwards the current destination and records whether native escape owns its input.</summary>
        /// <param name="location">Unmodified destination selected by RB or the combat routine.</param>
        public void MoveTowards(Vector3 location)
        {
            if (!Owns) return;
            // Native avoidance holds RB's movement/facing capability while this flag is
            // true. After release, any successor command immediately supersedes our claim.
            escapeCommandOutstanding = encounterOwnsHazards() && AvoidanceManager.IsRunningOutOfAvoid;
            lastDestination = location;
            ++moveCommands;
            inner.MoveTowards(location);
        }

        /// <summary>Forwards explicit stop and relinquishes any outstanding escape cleanup claim.</summary>
        public void MoveStop()
        {
            if (!Owns) return;
            escapeCommandOutstanding = false;
            ++stopCommands;
            inner.MoveStop();
        }

        internal void CompleteEscape()
        {
            // An October 28 BLM capture jumped in place during Cauterize despite a
            // valid escape route. Observe this same authoritative input channel
            // to distinguish absent commands, repeated stops and blocked movement.
            // Developer-only snapshots never issue input or alter cleanup timing.
            if (Owns && LoggingHelpers.MechanicDiagnosticsEnabled &&
                AvoidanceManager.IsRunningOutOfAvoid && Environment.TickCount64 >= nextDiagnostic)
            {
                nextDiagnostic = Environment.TickCount64 + 250;
                Logger.Information("[StoneVigil] ESCAPE_INPUT player=" + Core.Me.Location +
                    " destination=" + lastDestination + " moves=" + moveCommands +
                    " stops=" + stopCommands + " outstanding=" + escapeCommandOutstanding +
                    " moving=" + MovementManager.IsMoving + " casting=" + Core.Me.IsCasting +
                    " outOfControl=" + MovementManager.GenerallyOutOfControl);
            }
            if (!Owns || !escapeCommandOutstanding || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location)))
                return;

            // Release only the command observed through this channel. Merely seeing an
            // escaping true->false edge is insufficient: a successor may already be moving.
            MoveStop();
            Logger.Information("[StoneVigil] Released completed native escape input at " + Core.Me.Location + ".");
        }

        internal void Detach()
        {
            if (Owns)
            {
                if (escapeCommandOutstanding) MoveStop();
                Navigator.PlayerMover = inner;
            }
            escapeCommandOutstanding = false;
            detached = true;
        }
    }
}
