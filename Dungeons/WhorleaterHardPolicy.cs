using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// Keep assignment and movement decisions independent of frame-scoped RB objects.
// Callers capture distances and timestamps on the bot thread.
internal static class WhorleaterHardPolicy
{
    internal readonly record struct Member(uint Id, float Distance);

    // Logical pieces survive submerges and actor replacement; zero means ordinary targeting.
    internal const uint TankHead = 2550, TankTail = 2551;
    internal sealed class TankAssignment
    {
        private uint partner, piece, pending;
        private DateTime pendingSince;

        internal uint Select(uint self, uint other, uint head, uint tail, uint headVictim,
            uint tailVictim, uint otherTarget, bool bothReady, DateTime now)
        {
            if (other == 0)
            {
                Reset();
                return 0;
            }
            if (partner != other)
            {
                Reset();
                partner = other;
                // Deterministic fallback splits two automated tanks before either has aggro.
                piece = self < other ? TankHead : TankTail;
            }
            if (!bothReady)
            {
                pending = 0;
                return piece;
            }
            bool holdsHead = headVictim == other, holdsTail = tailVictim == other;
            uint desired = holdsHead != holdsTail ? (holdsHead ? TankTail : TankHead)
                : otherTarget == head ? TankTail : otherTarget == tail ? TankHead : piece;
            // A boss can briefly target a healer for a mechanic, or the other tank can
            // select an add. Require two seconds of consistent contrary evidence before
            // changing sides; absent evidence retains the existing assignment.
            if (desired == piece)
            {
                pending = 0;
                return piece;
            }
            if (pending != desired)
            {
                pending = desired;
                pendingSince = now;
            }
            else if ((now - pendingSince).TotalSeconds >= 2)
            {
                piece = desired;
                pending = 0;
            }
            return piece;
        }

        internal void Reset()
        {
            partner = piece = pending = 0;
            pendingSince = default;
        }
    }

    internal static bool KeepTankCandidate(uint npcId, uint assignedPiece, bool tailSeen)
        => assignedPiece == 0 || !tailSeen || npcId is not (TankHead or TankTail) || npcId == assignedPiece;

    // Adds remain a required combat objective even during converter readiness. Otherwise
    // stage during the shield window or when no boss is currently attackable.
    internal static bool ShouldStage(bool converterAvailable, bool addsReady, bool bossReady)
        => !addsReady && (converterAvailable || !bossReady);

    internal static bool TravelToCenter(float distance, bool settled) => distance > (settled ? 5 : 3);

    internal static bool AllowPath(bool escaping, bool converterOwned, bool converterRequest,
        bool nativeEscapeRequest, bool targetUnavailable) => escaping
        ? nativeEscapeRequest
        : converterOwned ? converterRequest : !converterRequest && !targetUnavailable;

    internal sealed class ConverterAssignment
    {
        private uint owner;
        private float bestDistance;
        private DateTime progressAt;
        private readonly Dictionary<uint, DateTime> retryAfter = new();

        internal uint Select(Member[] members, DateTime now)
        {
            if (members.Length == 0)
            {
                Reset();
                return 0;
            }
            var current = members.FirstOrDefault(m => m.Id == owner);
            if (owner != 0 && current.Id != 0)
            {
                // Require 0.75y of progress to ignore positional jitter. Allow 5s for
                // hazard dodges or interaction delays before choosing another member.
                if (current.Distance < bestDistance - 0.75f)
                {
                    bestDistance = current.Distance;
                    progressAt = now;
                }
                if ((now - progressAt).TotalSeconds < 5)
                    return owner;
                retryAfter[owner] = now.AddSeconds(5);
            }

            var available = members.Where(m => !retryAfter.TryGetValue(m.Id, out var until) || now >= until).ToArray();
            // Exhaustion is recoverable: retry the closest living member, never stop the duty.
            if (available.Length == 0)
            {
                retryAfter.Clear();
                available = members;
            }
            var chosen = available.OrderBy(m => m.Distance).ThenBy(m => m.Id).First();
            owner = chosen.Id;
            bestDistance = chosen.Distance;
            progressAt = now;
            return owner;
        }

        internal void Reset()
        {
            owner = 0;
            progressAt = default;
            bestDistance = 0;
            retryAfter.Clear();
        }
    }
}
