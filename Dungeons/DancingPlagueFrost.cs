using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public sealed partial class DancingPlague
{
    private sealed class FrostCast
    {
        internal uint Actor;
        internal int RingIndex;
        internal Vector3 Origin;
        internal DateTime Impact;
    }

    // November 8 synchronized captures: center first, then eight locations on
    // a 13y grid at roughly 0.8s intervals. Two starting rotations were observed;
    // the second outer tell determines direction rather than assuming a mirror.
    private static readonly (float X, float Z)[] FrostRing =
        [(-13, -13), (0, -13), (13, -13), (13, 0), (13, 13), (0, 13), (-13, 13), (-13, 0)];
    private readonly Dictionary<uint, FrostCast> frost = new();
    private DateTime frostDeadline, nextFrostSearch, nextFrostCastCancel;
    private Vector3? frostStage, frostCenterTarget;
    private bool frostLeased, frostConflictLogged, frostWasInward, frostArrivalLogged;

    private void UpdateFrost()
    {
        if (movement == null || !movement.IsCurrent)
        {
            ResetFrost();
            return;
        }
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        // A cast that disappears well before its expected completion invalidates
        // the sequence; a completed cast naturally disappears near its impact.
        if (frost.Values.Any(f => now < f.Impact.AddSeconds(-1.05) &&
            !actors.Any(a => a.ObjectId == f.Actor && a.IsCasting && a.CastingSpellId == 15694)))
        {
            ResetFrost();
            return;
        }
        foreach (var actor in actors
            .Where(a => a.IsValid && a.BaseId == Helper && a.IsCasting && a.CastingSpellId == 15694 && a.SpellCastInfo != null)
            .OrderBy(a => a.SpellCastInfo.RemainingCastTime))
        {
            if (frost.ContainsKey(actor.ObjectId)) continue;
            int ring = FrostRingIndex(actor.Location);
            if (ring == -2) continue;
            // Damage notifications can arrive after cast completion. Reserve 0.8s
            // and retain another 0.4s before entering the first resolved region.
            var impact = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.8);
            frost[actor.ObjectId] = new FrostCast { Actor = actor.ObjectId, RingIndex = ring, Origin = actor.Location, Impact = impact };
            frostDeadline = now.AddSeconds(16);
        }
        if (frost.Count == 0) return;
        if (now >= frostDeadline)
        {
            ResetFrost();
            return;
        }
        var center = frost.Values.FirstOrDefault(f => f.RingIndex == -1);
        var outer = frost.Values.Where(f => f.RingIndex >= 0).OrderBy(f => f.Impact).ToArray();
        if (center == null || outer.Length < 2) return;
        int delta = (outer[1].RingIndex - outer[0].RingIndex + 8) % 8;
        int direction = delta == 1 ? 1 : delta == 7 ? -1 : 0;
        if (direction == 0 || outer.Any(f => f.Impact < center.Impact))
        {
            RejectFrost("unrecognized order");
            return;
        }
        for (int i = 0; i < outer.Length; i++)
        {
            int expected = (outer[0].RingIndex + direction * i + 8 * 8) % 8;
            double interval = (outer[i].Impact - outer[0].Impact).TotalSeconds;
            if (outer[i].RingIndex != expected || Math.Abs(interval - 0.8 * i) > 0.8)
            {
                RejectFrost("contradictory outer tell");
                return;
            }
        }
        if (!frostStage.HasValue)
        {
            DateTime centerEntry = center.Impact.AddSeconds(0.4);
            if (now >= centerEntry)
                frostStage = Core.Me.Location;
            else
            {
                // November 8: the opposite-first staging point was more than
                // 20y away and the conservative deadline rejected it. Search
                // the same narrow 11.5y staging ring for a nearer point whose
                // complete center-to-ring timing passes the forecast certificate.
                var candidates = Enumerable.Range(0, 32).Select(i =>
                {
                    float angle = i * MathF.PI * 2 / 32;
                    return new Vector3(Center.X + MathF.Sin(angle) * 11.5f, Center.Y,
                        Center.Z + MathF.Cos(angle) * 11.5f);
                });
                frostStage = candidates.Where(p => OrdinarySegmentClear(p) &&
                    FrostRouteSafe(Core.Me.Location, p, now, center, outer, direction, centerEntry, false))
                    .OrderBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
            }
            if (!frostStage.HasValue)
            {
                RejectFrost("no reachable staging point");
                return;
            }
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Frost first={outer[0].RingIndex} direction={direction} stage={frostStage} centerImpact={center.Impact:O}.");
        }

        DateTime enterCenter = center.Impact.AddSeconds(0.4);
        // Keep the owner alive through the same post-impact window used by
        // FrostRouteSafe, so a successor cannot cross the final circle early.
        DateTime lastImpact = outer[0].Impact.AddSeconds(0.8 * 7 + 1.1);
        if (now > lastImpact)
        {
            ResetFrost();
            return;
        }
        bool inward = now >= enterCenter;
        if (inward && !frostWasInward)
        {
            frostArrivalLogged = false;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Frost center handoff player={Core.Me.Location} stage={frostStage} now={now:O}.");
        }
        frostWasInward = inward;
        Vector3 target = inward ? Center : frostStage.Value;
        if (inward && frostCenterTarget.HasValue) target = frostCenterTarget.Value;
        var spreadPositions = inward ? SpreadPositions(actors) : Array.Empty<Vector3>();
        if (inward && spreadPositions == null)
        {
            RejectFrost("spread target unavailable");
            return;
        }
        bool SpreadSafe(Vector3 p) => spreadPositions.All(position => position.Distance2D(p) >= 6.5f);
        bool RouteSafe(Vector3 p) => FrostRouteSafe(Core.Me.Location, p, now, center, outer, direction, enterCenter, true) &&
            OrdinarySegmentClear(p);
        if (inward && (!SpreadSafe(target) || !RouteSafe(target)))
        {
            if (now < nextFrostSearch)
            {
                RejectFrost("waiting to reassess spread-safe route");
                return;
            }
            nextFrostSearch = now.AddMilliseconds(150);
            // The first test found no point within 4y of center despite open
            // resolved sectors. Inspect the actual square arena, but admit only
            // points whose timed Frost route and current spreads are both safe.
            var candidates = new List<Vector3>();
            for (int x = -18; x <= 18; x += 2)
                for (int z = -18; z <= 18; z += 2)
                    candidates.Add(new Vector3(Center.X + x, Center.Y, Center.Z + z));
            var found = candidates.Where(SpreadSafe)
                .Where(RouteSafe)
                .OrderBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
            if (!found.HasValue)
            {
                RejectFrost("no spread-safe center pocket");
                return;
            }
            target = found.Value;
        }
        if (inward)
        {
            if (frostCenterTarget.HasValue && frostCenterTarget.Value.Distance2D(target) > 0.7f) frostArrivalLogged = false;
            frostCenterTarget = target;
        }
        if (!FrostRouteSafe(Core.Me.Location, target, now, center, outer, direction, enterCenter, inward) ||
            !OrdinarySegmentClear(target))
        {
            if (!inward && Core.Me.Location.Distance2D(target) > 0.7f) frostStage = null;
            RejectFrost("route no longer certified");
            return;
        }
        frostConflictLogged = false;
        if (!frostArrivalLogged && Core.Me.Location.Distance2D(target) <= 0.7f && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            Logger.Information($"[DancingPlague] Frost arrived phase={(inward ? "center" : "stage")} player={Core.Me.Location} target={target}.");
            frostArrivalLogged = true;
        }
        foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
            CapabilityManager.Update(CapabilityHandle, flag, TimeSpan.FromMilliseconds(500), "Dancing Plague: ordered Frost");
        frostLeased = true;
        // November 10: Ruin held the player at the stage after center handoff;
        // new Uplift targets then removed the certified route and Frost hit twice.
        // Interrupt only for an already-certified inward move, not for a hold
        // or a rejected forecast, so ordinary healing and casting retain priority.
        if (inward && Core.Me.Location.Distance2D(target) > 0.7f && Core.Me.IsCasting && now >= nextFrostCastCancel)
        {
            ActionManager.StopCasting();
            nextFrostCastCancel = now.AddMilliseconds(250);
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Frost interrupted cast for certified inward move to {target}.");
        }
        movement.Request(target, 0.7f);
    }

    private static int FrostRingIndex(Vector3 location)
    {
        if (location.Distance2D(Center) < 1) return -1;
        for (int i = 0; i < FrostRing.Length; i++)
            if (location.Distance2D(new Vector3(Center.X + FrostRing[i].X, Center.Y, Center.Z + FrostRing[i].Z)) < 1)
                return i;
        return -2;
    }

    private static bool FrostRouteSafe(Vector3 start, Vector3 target, DateTime now, FrostCast center,
        FrostCast[] outer, int direction, DateTime enterCenter, bool inward)
    {
        // A conservative 5y/s plus 0.25s input allowance is a planning estimate,
        // not a claim about measured execution. Check each impact window along
        // both legs, including the hold between them and the projected later tells.
        const float speed = 5f, radius = 10.5f;
        double travel = start.Distance2D(target) / speed + 0.25;
        if (!inward && start.Distance2D(target) > 0.7f && now.AddSeconds(travel) >= center.Impact.AddSeconds(-0.25)) return false;
        for (int i = -1; i < 8; i++)
        {
            Vector3 origin;
            DateTime impact;
            if (i == -1) { origin = center.Origin; impact = center.Impact; }
            else
            {
                int index = (outer[0].RingIndex + direction * i + 64) % 8;
                origin = new Vector3(Center.X + FrostRing[index].X, Center.Y, Center.Z + FrostRing[index].Z);
                impact = i < outer.Length ? outer[i].Impact : outer[0].Impact.AddSeconds(i * 0.8);
            }
            // November 9: the fourth outer helper applied a gain 0.61s after
            // its forecast impact, after the former +0.35s final sample. Keep
            // outer circles through +1.05s; the center handoff keeps its
            // separately observed shorter window instead of delaying every run.
            int finalOffset = i == -1 ? 1 : 3;
            for (int offset = -1; offset <= finalOffset; offset++)
            {
                DateTime sample = impact.AddSeconds(offset * 0.35);
                if (sample < now) continue;
                double elapsed = (sample - now).TotalSeconds;
                Vector3 point;
                if (elapsed <= travel)
                    point = Interpolate(start, target, Math.Max(0, (elapsed - 0.25) * speed / Math.Max(0.01, start.Distance2D(target))));
                else if (!inward && sample > enterCenter)
                    point = Interpolate(target, Center, Math.Max(0, (sample - enterCenter).TotalSeconds - 0.25) * speed / Math.Max(0.01, target.Distance2D(Center)));
                else point = target;
                if (point.Distance2D(origin) < radius) return false;
            }
        }
        return true;
    }

    private static Vector3 Interpolate(Vector3 a, Vector3 b, double fraction)
    {
        float t = (float)Math.Clamp(fraction, 0, 1);
        return new Vector3(a.X + (b.X - a.X) * t, a.Y, a.Z + (b.Z - a.Z) * t);
    }

    private static Vector3[] SpreadPositions(BattleCharacter[] actors)
    {
        var targets = actors.Where(a => a.BaseId == Helper && a.IsCasting && a.CastingSpellId == 16927 && a.SpellCastInfo != null)
            .Select(a => a.SpellCastInfo.TargetId).Distinct().ToArray();
        if (targets.Length == 0) return [];
        bool self = targets.Contains(Core.Me.ObjectId);
        // Missing target wrappers do not establish separation; wait for a fresh
        // bot-thread snapshot instead of accepting an unknowable spread pocket.
        if (targets.Any(id => id != Core.Me.ObjectId && !actors.Any(a => a.ObjectId == id)) ||
            self && !PartyManager.VisibleMembers.Any(p => p.ObjectId != Core.Me.ObjectId)) return null;
        var relevant = actors.Where(a => a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
            (self ? PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) : targets.Contains(a.ObjectId)));
        return relevant.Select(a => a.Location).ToArray();
    }

    private void RejectFrost(string reason)
    {
        movement?.Request(null, 0.7f);
        // A rejected forecast must not hand an unmodeled route back to combat.
        // Hold only ordinary movement while the next bot-thread sample reassesses;
        // native emergency avoidance and the rest of the routine remain available.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Dancing Plague: Frost route unavailable");
        frostLeased = true;
        if (!frostConflictLogged && LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Warning($"[DancingPlague] Frost {reason}; released owned movement for reassessment.");
        frostConflictLogged = true;
    }

    private void ResetFrost()
    {
        movement?.Request(null, 0.7f);
        if (frostLeased) CapabilityManager.Clear(CapabilityHandle);
        frostLeased = false;
        frost.Clear();
        frostStage = frostCenterTarget = null;
        frostDeadline = default;
        nextFrostSearch = default;
        nextFrostCastCancel = default;
        frostConflictLogged = false;
        frostWasInward = false;
        frostArrivalLogged = false;
    }
}
