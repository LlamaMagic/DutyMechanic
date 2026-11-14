using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class Ihuykatumu
{
    // Follow Trust-derived pockets in resolve order; simultaneous avoids can
    // block the route through debris that has not yet fallen.
    private readonly List<BuryHazard> buryHazards = [];
    private readonly Dictionary<uint, (uint Action, double Due)> buryActors = [];
    private int buryLayout;
    private double buryFirstCast;
    private bool buryDecayApproach;
    private bool buryFarDecay;
    private int buryStage = -1;
    private int buryPathGroup;
    private int buryPathIndex;
    private BuryInputOwner buryInput;
    private readonly CapabilityManagerHandle buryMovement = CapabilityManager.CreateNewHandle();
    private bool buryActive;
    private DateTime nextBuryFailureLog;
    private DateTime nextBuryCastCancel;
    private DateTime nextBuryMoveRetry;
    private DateTime buryStartupUntil;
    private bool buryStartupPending;
    private bool buryStartupReported;
    private bool buryForecast;

    private sealed record BuryHazard(uint Action, float X, float Z, float Heading, double Due, bool Forecast = false);
    private readonly record struct BuryStep(double At, float X, float Z);

    // The north pocket is safe for the debris, but staying there until Decay
    // starts leaves too little travel time to the far flytrap. These staging
    // points retain access to each of the three recorded flytrap positions.
    private static readonly BuryStep[] NorthStaging =
    [
        new(8, 17.4f, -95.95f), new(8.5, 18.9f, -97.45f),
        new(9, 20.4f, -98.95f), new(9.5, 21.9f, -97.45f),
    ];

    // The far flytrap's cast overlaps the last debris. These timed exits were
    // replayed against the recorded layouts and are kept separate from the
    // flytrap's normal direct shelter approach.
    private static readonly BuryStep[] NorthFarDecay =
    [
        new(10, 23.4f, -97.45f), new(10.5, 24.9f, -98.95f),
        new(11, 26.4f, -100.45f), new(11.5, 27.9f, -101.95f),
        new(12, 29.4f, -101.95f), new(12.5, 30.9f, -103.45f),
        new(13, 32.4f, -103.45f), new(13.5, 33.9f, -101.95f),
        new(14, 35.4f, -100.45f), new(14.5, 36.9f, -98.95f),
        new(15, 38.4f, -97.45f), new(15.5, 39.9f, -95.95f),
        new(16, 41.4f, -94.45f),
    ];
    private static readonly BuryStep[] SouthFarDecay =
    [
        new(10, 17.7f, -109.9f), new(10.5, 19.7f, -109.9f),
        new(11, 21.7f, -109.9f), new(11.5, 23.7f, -107.9f),
        new(12, 25.7f, -105.9f), new(12.5, 27.7f, -103.9f),
        new(13, 29.7f, -103.9f), new(13.5, 31.7f, -103.9f),
        new(14, 33.7f, -101.9f), new(14.5, 35.7f, -99.9f),
        new(15, 37.7f, -97.9f), new(15.5, 39.7f, -95.9f),
        new(16, 41.7f, -93.9f),
    ];

    // These layouts share geometry across the recorded casts. Due is an offset
    // from the first circle's cast completion; every
    // later observed helper must confirm its action, position, heading and time.
    private static readonly BuryHazard[] BuryNorth =
    [
        new(36500, 42.432f, -108.094f, 4.967f, 1.815, true),
        new(36498, 31.044f, -107.146f, 6.283f, 2.018, true),
        new(36498, 35.821f, -98.331f, 6.283f, 2.218, true),
        new(36501, 50.111f, -94.938f, 4.181f, 2.486, true),
        new(36498, 24.274f, -98.278f, 6.283f, 2.753, true),
        new(36499, 33.773f, -96.525f, .192f, 3.059, true),
        new(36497, 24.528f, -103.617f, 6.283f, 3.461, true),
        new(36498, 29.505f, -79.858f, 6.283f, 3.461, true),
        new(36502, 19.465f, -88.570f, 2.160f, 3.461, true),
        new(36499, 41.496f, -98.990f, 3.962f, 4.503, true),
        new(36497, 46.029f, -105.153f, 6.283f, 4.975, true),
        new(36500, 29.705f, -106.367f, 5.918f, 5.177, true),
        new(36498, 32.618f, -88.026f, 6.283f, 5.511, true),
        new(36497, 47.244f, -83.924f, 6.283f, 5.780, true),
        new(36498, 21.913f, -109.275f, 6.283f, 6.252, true),
        new(36503, 50.111f, -94.938f, 4.181f, 7.530, true),
        new(36504, 19.465f, -88.570f, 2.160f, 8.542, true),
    ];
    private static readonly BuryHazard[] BurySouth =
    [
        new(36500, 40.188f, -91.638f, 3.153f, 1.849, true),
        new(36498, 32.217f, -97.910f, 6.283f, 1.982, true),
        new(36498, 21.151f, -80.417f, 6.283f, 2.185, true),
        new(36501, 41.579f, -89.054f, 4.184f, 2.489, true),
        new(36498, 32.826f, -108.165f, 6.283f, 2.792, true),
        new(36499, 39.267f, -97.248f, 5.381f, 3.028, true),
        new(36497, 25.630f, -89.327f, 6.283f, 3.473, true),
        new(36498, 47.377f, -93.147f, 6.283f, 3.473, true),
        new(36502, 37.895f, -110.783f, 5.797f, 3.473, true),
        new(36499, 31.231f, -94.806f, 3.778f, 4.516, true),
        new(36497, 46.408f, -106.463f, 6.283f, 5.055, true),
        new(36500, 41.044f, -79.355f, 4.198f, 5.156, true),
        new(36498, 20.219f, -102.555f, 6.283f, 5.455, true),
        new(36497, 46.993f, -83.878f, 6.283f, 5.793, true),
        new(36498, 32.445f, -79.643f, 6.283f, 6.331, true),
        new(36503, 41.579f, -89.054f, 4.184f, 7.608, true),
        new(36504, 37.895f, -110.783f, 5.797f, 8.522, true),
    ];

    private static double BuryTime => DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
    private static bool IsBury(uint action) => action is >= 36497 and <= 36504;

    private void PulseBury()
    {
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            WorldManager.ZoneId != (uint)Data.ZoneId.Ihuykatumu || WorldManager.SubZoneId != (uint)SubZoneId.PunutiyPool ||
            Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat)
        {
            ResetBury();
            return;
        }

        double now = BuryTime;
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                     .Where(a => a.IsValid && (IsBury(a.CastingSpellId) || buryActive && a.CastingSpellId == EnemyAction.Decay) && a.SpellCastInfo.IsValid))
        {
            uint action = actor.CastingSpellId;
            if (buryActors.TryGetValue(actor.ObjectId, out var seen) && seen.Action == action && now <= seen.Due + .6)
                continue;
            double due = now + Math.Max(0, actor.SpellCastInfo.RemainingCastTime.TotalSeconds);
            float x = actor.Location.X, z = actor.Location.Z, heading = actor.Heading;
            bool first = buryHazards.Count == 0 && action == 36497 && due - now >= 3.0;
            if (buryForecast && IsBury(action))
            {
                var predicted = buryHazards.FirstOrDefault(h => h.Forecast && h.Action == action &&
                    Math.Abs(h.X - x) < .25 && Math.Abs(h.Z - z) < .25 &&
                    Math.Abs(Math.Sin((h.Heading - heading) / 2)) < .05 && Math.Abs(h.Due - due) < .6);
                if (predicted == null) DiscardBuryForecast("contradictory helper");
                else buryHazards.Remove(predicted);
            }
            buryActors[actor.ObjectId] = (action, due);
            buryHazards.Add(new(action, x, z, heading, due));
            if (first)
            {
                BuryHazard[] pattern = Math.Abs(x - 23.492f) < .25f && Math.Abs(z + 84.416f) < .25f ? BuryNorth :
                    Math.Abs(x - 23.340f) < .25f && Math.Abs(z + 105.684f) < .25f ? BurySouth : null;
                if (pattern != null)
                {
                    buryForecast = true;
                    buryLayout = ReferenceEquals(pattern, BuryNorth) ? 1 : 2;
                    buryFirstCast = due - 3.7;
                    foreach (var predicted in pattern)
                        buryHazards.Add(predicted with { Due = due + predicted.Due });
                    Logger.Information("[Ihuykatumu] Bury recognized {0} layout; future helpers must confirm it.",
                        ReferenceEquals(pattern, BuryNorth) ? "north" : "south");
                }
                else
                    Logger.Information("[Ihuykatumu] Bury layout unknown; planning only from observed casts.");
            }
        }
        // A missing expected cast is unknown, not proof that the forecast is
        // still valid. The captured helpers each had a 3.7-second cast.
        if (buryForecast && buryHazards.Any(h => h.Forecast && now > h.Due - 3.7 + .8))
            DiscardBuryForecast("expected helper not observed");
        buryHazards.RemoveAll(h => now > h.Due + .65);
        if (!buryHazards.Any(h => IsBury(h.Action) || h.Action == EnemyAction.Decay))
        {
            if (buryActive) ResetBury();
            return;
        }

        if (buryInput == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            buryInput = new BuryInputOwner(Navigator.PlayerMover, () => buryActive);
            Navigator.PlayerMover = buryInput;
        }
        if (buryInput?.IsCurrent != true)
        {
            // A replacement mover is a successor, never something this lease
            // may stop. Release our state so native geometry is available again.
            ResetBury();
            return;
        }

        if (!buryActive)
        {
            buryActive = true;
            if (!MovementManager.GenerallyOutOfControl) buryInput.Hold();
            Logger.Information("[Ihuykatumu] Bury timed movement acquired.");
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.ForceMoved)
        {
            buryInput.Release();
            CapabilityManager.Clear(buryMovement, CapabilityFlags.Movement, "Ihuykatumu Bury yielded");
            buryStartupPending = false;
            return;
        }

        CapabilityManager.Update(buryMovement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Ihuykatumu Bury timed route");
        var player = Core.Me.Location;
        var hazards = buryHazards.ToArray();
        if (buryLayout == 0)
        {
            // An unknown or contradicted layout must not inherit a known path.
            buryInput.Hold();
            return;
        }

        double elapsed = now - buryFirstCast;
        int stage = buryLayout == 1 ? 0 : elapsed < 3.6 ? 0 : elapsed < 10.5 ? 1 : 2;
        if (buryLayout == 2 && stage == 1)
        {
            var firstCircle = hazards.FirstOrDefault(h => h.Action == 36497 &&
                Math.Abs(h.X - 23.340f) < .25 && Math.Abs(h.Z + 105.684f) < .25);
            if (firstCircle != null && now <= firstCircle.Due + .55 &&
                !BurySegmentSafe(player, new Vector3(15.7f, player.Y, -109.9f), now,
                    firstCircle.Due + .55, [firstCircle]))
                stage = 0;
        }
        var target = buryLayout == 1 ? new Vector3(15.9f, player.Y, -94.45f) : stage switch
        {
            0 => new Vector3(19.974f, player.Y, -86.015f),
            1 => new Vector3(15.7f, player.Y, -109.9f),
            _ => new Vector3(16.98f, player.Y, -99.02f),
        };
        if (buryLayout == 1 && elapsed >= 8 && !buryFarDecay)
        {
            int index = BuryMappedStep(NorthStaging, 1, elapsed, player, now, hazards);
            var point = NorthStaging[index];
            target = new Vector3(point.X, player.Y, point.Z);
            stage = 10 + index;
        }

        // Decay is a separate positive-position mechanic. It can begin while
        // debris is still resolving, so only hand off when its actual caster
        // can be reached along a checked timed segment and held through impact.
        var decay = hazards.FirstOrDefault(h => h.Action == EnemyAction.Decay && now <= h.Due + .65);
        if (decay != null)
        {
            buryFarDecay = Math.Abs(decay.X - 44.093f) < .5 && Math.Abs(decay.Z + 93.526f) < .5;
            if (buryFarDecay && elapsed >= 10)
            {
                buryDecayApproach = true;
                var route = buryLayout == 1 ? NorthFarDecay : SouthFarDecay;
                int index = BuryMappedStep(route, buryLayout == 1 ? 2 : 3, elapsed, player, now, hazards);
                var point = route[index];
                target = new Vector3(point.X, player.Y, point.Z);
                stage = 100 + index;
            }
            else if (!buryFarDecay)
            {
                var shelter = new Vector3(decay.X, player.Y, decay.Z);
                if (buryDecayApproach || BurySegmentSafe(player, shelter, now, decay.Due + .55, hazards))
                {
                    buryDecayApproach = true;
                    target = shelter;
                    stage = 3;
                }
            }
        }
        if (stage != buryStage)
        {
            buryStage = stage;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information("[Ihuykatumu] Bury mapped stage={0} elapsed={1:F2} target={2}.", stage, elapsed, target);
        }

        // The north pocket is constrained by the arena edge and two circles;
        // Trusts and the manual run reached it with less than .5y clearance.
        float tolerance = buryLayout == 1 && stage == 0 ? .10f : .15f;
        bool needsMovement = player.Distance2D(target) > tolerance;
        if (!needsMovement || MovementManager.IsMoving) buryStartupPending = false;
        else if (!buryStartupPending)
        {
            // Bound the scheduling handoff: Magitek can otherwise start a new
            // cast before the first movement command is visible to the game.
            buryStartupPending = true;
            buryStartupUntil = DateTime.UtcNow.AddMilliseconds(800);
            buryStartupReported = false;
        }
        if (MovementManager.GenerallyOutOfControl)
        {
            buryInput.SuspendForLock();
            if (needsMovement && Core.Me.IsCasting && DateTime.UtcNow >= nextBuryCastCancel)
            {
                nextBuryCastCancel = DateTime.UtcNow.AddMilliseconds(200);
                ActionManager.StopCasting();
            }
            return;
        }
        if (!needsMovement)
        {
            buryInput.Hold();
            return;
        }
        if (Core.Me.IsCasting)
        {
            if (DateTime.UtcNow >= nextBuryCastCancel)
            {
                nextBuryCastCancel = DateTime.UtcNow.AddMilliseconds(200);
                ActionManager.StopCasting();
            }
            return;
        }

        // The fixed debris route was checked as a timed sequence. Treating its
        // later shapes as simultaneous blockers rejects the recorded Trust path.
        // The independent Decay handoff still checks its actual timed approach.
        bool segmentSafe = stage == 3 && decay != null
            ? BurySegmentSafe(player, target, now, decay.Due + .55, hazards)
            : BuryNativeSegmentSafe(player, target);
        if (!segmentSafe)
        {
            buryInput.Hold();
            if (DateTime.UtcNow >= nextBuryFailureLog)
            {
                nextBuryFailureLog = DateTime.UtcNow.AddSeconds(1);
                Logger.Information("[Ihuykatumu] Bury mapped segment unavailable stage={0} player={1} target={2}.", stage, player, target);
            }
            return;
        }
        if (!buryInput.HasDestination || buryInput.Destination.Distance2D(target) > .1f ||
            (!MovementManager.IsMoving && DateTime.UtcNow >= nextBuryMoveRetry))
        {
            // Instant actions can stop a held slide command without changing
            // its destination. Retry boundedly while the owned actor is still.
            if (buryInput.HasDestination && !MovementManager.IsMoving && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information("[Ihuykatumu] Bury retrying stalled movement stage={0} player={1} target={2}.", stage, player, target);
            nextBuryMoveRetry = DateTime.UtcNow.AddMilliseconds(200);
            buryInput.Move(target);
        }
    }

    private int BuryMappedStep(BuryStep[] route, int group, double elapsed, Vector3 player, double now, BuryHazard[] hazards)
    {
        int due = Array.FindLastIndex(route, s => elapsed >= s.At);
        if (buryPathGroup != group)
        {
            buryPathGroup = group;
            buryPathIndex = due;
        }
        else buryPathIndex = Math.Max(buryPathIndex, due);
        int next = buryPathIndex + 1;
        if (next < route.Length)
        {
            var current = route[buryPathIndex];
            var following = route[next];
            var currentPoint = new Vector3(current.X, player.Y, current.Z);
            var followingPoint = new Vector3(following.X, player.Y, following.Z);
            // Joining a safe next leg avoids a brief stop that lets the combat
            // routine start a cast. Preserve required waits and never retreat
            // to an earlier waypoint after advancing.
            if (player.Distance2D(currentPoint) < .3f &&
                BurySegmentSafe(player, followingPoint, now, buryFirstCast + following.At + .5, hazards))
                buryPathIndex = next;
        }
        return buryPathIndex;
    }

    private static bool BuryNativeSegmentSafe(Vector3 from, Vector3 to)
    {
        double distance = from.Distance2D(to);
        int samples = Math.Max(1, (int)Math.Ceiling(distance));
        for (int i = 1; i <= samples; i++)
        {
            float fraction = (float)i / samples;
            var point = new Vector3(from.X + (to.X - from.X) * fraction, from.Y,
                from.Z + (to.Z - from.Z) * fraction);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private static bool BurySegmentSafe(Vector3 from, Vector3 to, double now, double holdUntil, BuryHazard[] hazards)
    {
        double distance = from.Distance2D(to);
        // The manual recording showed a 5.96 y/s median during sustained
        // movement without Sprint. Account for modest drift while checking the
        // approach and hold; startup locks are handled by the movement owner.
        foreach (double speed in new[] { 5.5, 6.0 })
        {
            double arrival = now + distance / speed;
            double end = Math.Min(Math.Max(holdUntil, arrival), now + 8);
            for (double at = now; at <= end + .05; at += .1)
            {
                double fraction = distance < .01 ? 1 : Math.Min(1, (at - now) * speed / distance);
                var point = new Vector3((float)(from.X + (to.X - from.X) * fraction), from.Y,
                    (float)(from.Z + (to.Z - from.Z) * fraction));
                if (!BuryPointSafe(point.X, point.Z, Math.Max(now, at - .1), hazards) ||
                    !BuryPointSafe(point.X, point.Z, at + .1, hazards) ||
                    AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point)))
                    return false;
            }
        }
        return true;
    }

    private void ResetBury()
    {
        if (!buryActive && buryInput == null && buryHazards.Count == 0) return;
        if (buryActive)
            Logger.Information("[Ihuykatumu] Bury timed movement released.");
        buryActive = false;
        buryLayout = 0;
        buryFirstCast = 0;
        buryDecayApproach = false;
        buryFarDecay = false;
        buryStage = -1;
        buryPathGroup = 0;
        buryPathIndex = 0;
        buryForecast = false;
        buryActors.Clear();
        buryHazards.Clear();
        nextBuryFailureLog = DateTime.MinValue;
        nextBuryCastCancel = DateTime.MinValue;
        nextBuryMoveRetry = DateTime.MinValue;
        buryStartupPending = false;
        buryStartupReported = false;
        buryStartupUntil = DateTime.MinValue;
        CapabilityManager.Clear(buryMovement, CapabilityFlags.Movement, "Ihuykatumu Bury ended");
        buryInput?.Detach();
        buryInput = null;
    }

    private void DiscardBuryForecast(string reason)
    {
        Logger.Information("[Ihuykatumu] Bury forecast discarded: " + reason + ".");
        buryForecast = false;
        buryHazards.RemoveAll(h => h.Forecast);
        buryLayout = 0;
        buryDecayApproach = false;
        buryInput?.Hold();
    }

    private bool BuryMovementStarting()
    {
        bool starting = buryActive && buryStartupPending &&
            DateTime.UtcNow < buryStartupUntil && TreeRoot.IsRunning && Core.Me != null && Core.Me.IsValid &&
            Core.Me.IsAlive && Core.Me.InCombat && !MovementManager.IsMoving && !MovementManager.ForceMoved &&
            !AvoidanceManager.IsRunningOutOfAvoid &&
            (!MovementManager.GenerallyOutOfControl || Core.Me.IsCasting);
        if (starting && !buryStartupReported && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            buryStartupReported = true;
            Logger.Information("[Ihuykatumu] Bury movement-start handoff claimed the tree.");
        }
        return starting;
    }

    private static bool BuryPointSafe(float x, float z, double time, BuryHazard[] hazards)
    {
        if (x < 15.5f || x > 54.5f || z < -114.5f || z > -75.5f) return false;
        foreach (var h in hazards)
        {
            // Captured damage arrived roughly four tenths after cast finish.
            // Leave room before finish and after the observed damage delay;
            // the route also samples a separate timing band for input drift.
            // Until a predicted helper is confirmed, include the observed
            // variation in cast discovery between the two captures.
            if (time < h.Due - (h.Forecast ? .55 : .35) || time > h.Due + (h.Forecast ? .75 : .55)) continue;
            double dx = x - h.X, dz = z - h.Z;
            if (h.Action == EnemyAction.Decay)
            {
                if (dx * dx + dz * dz >= 3.5 * 3.5) return false;
                continue;
            }
            // The observed Trust pocket leaves less than the usual .5y margin;
            // a .25y buffer preserves it while still expanding every circle.
            float radius = h.Action switch { 36497 => 12.25f, 36498 => 8.25f, 36501 => 4.25f, 36502 => 6.25f, _ => 0 };
            if (radius > 0 && dx * dx + dz * dz <= radius * radius) return false;
            if (radius == 0)
            {
                double forward = dx * Math.Sin(h.Heading) + dz * Math.Cos(h.Heading);
                double across = dx * Math.Cos(h.Heading) - dz * Math.Sin(h.Heading);
                float width = h.Action is 36499 or 36503 ? 6.5f : 10.5f;
                float length = h.Action is 36499 or 36503 ? 25.25f : 35.25f;
                if (forward >= 0 && forward <= length && Math.Abs(across) <= width / 2) return false;
            }
        }
        return true;
    }

    private sealed class BuryInputOwner(IPlayerMover inner, Func<bool> ownsSequence) : IPlayerMover
    {
        private bool detached, ownsCommand, holding, suspended;
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal bool HasDestination => ownsCommand && !suspended;
        internal Vector3 Destination { get; private set; }
        private Vector3 origin;
        internal bool Arrived(Vector3 player)
        {
            float dx = Destination.X - origin.X, dz = Destination.Z - origin.Z;
            return player.Distance2D(Destination) < .45f ||
                (player.X - Destination.X) * dx + (player.Z - Destination.Z) * dz >= 0;
        }
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent) return;
            // Native escape preempts this sequence. Ordinary movement cannot
            // replace an active timed waypoint between planning pulses.
            if (ownsSequence() && !AvoidanceManager.IsRunningOutOfAvoid) return;
            ownsCommand = false;
            holding = false;
            suspended = false;
            inner.MoveTowards(location);
        }
        public void MoveStop()
        {
            if (!IsCurrent) return;
            ownsCommand = false;
            holding = false;
            suspended = false;
            inner.MoveStop();
        }
        internal void Move(Vector3 destination)
        {
            if (!IsCurrent || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl) return;
            origin = Core.Me.Location;
            Destination = destination;
            ownsCommand = true;
            holding = false;
            suspended = false;
            // Each waypoint is a checked short straight segment on the flat
            // arena; a service path could detour through a different snapshot.
            inner.MoveTowards(destination);
        }
        internal void Hold()
        {
            if (!IsCurrent || !ownsSequence() || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || holding) return;
            inner.MoveStop();
            ownsCommand = false;
            holding = true;
            suspended = false;
        }
        internal void SuspendForLock() { if (ownsCommand) suspended = true; }
        internal void Release()
        {
            if (!IsCurrent) return;
            if (ownsCommand && !AvoidanceManager.IsRunningOutOfAvoid &&
                !MovementManager.GenerallyOutOfControl && !MovementManager.ForceMoved) MoveStop();
            else { ownsCommand = false; suspended = false; }
        }
        internal void Detach()
        {
            if (IsCurrent) { Release(); Navigator.PlayerMover = inner; }
            detached = true;
        }
    }
}
