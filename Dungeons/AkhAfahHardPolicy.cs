using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Orders Shiva's Hard-mode hazards without retaining live game objects. Cast deadlines
/// distinguish successive icicles; player identities distinguish spread carriers from helpers.
/// </summary>
internal static class AkhAfahHardPolicy
{
    // Permafrost's client effect range is twenty yalms. Use nineteen as a conservative
    // travel bound; the exact freezing-rim radius has not been measured. Damaging
    // circles separately include half a yalm of margin.
    internal const float Arena = 19;
    internal const uint Hailstorm = 2456;
    internal const uint IcicleImpact = 2462;
    internal const uint GlacierBash = 2465;
    internal const uint Permafrost = 2457;
    internal const uint ThinIce = 604;
    internal const double Grace = 0.25;

    // Both October 28 Bash captures applied Stun about 1.3s after the cast deadline.
    // Keep the rear position through that animation instead of immediately returning.
    internal const double BashRecovery = 1.6;

    internal sealed record Member(uint Id, Vector2 Position);
    internal sealed record Cast(uint Owner, uint Action, Vector2 Position, float Heading, uint Target, double Finish);

    // Native avoidance has no geometry for Hard's omen-less Bash. Two frontal captures
    // remained stationary and were stunned. Use the guide's directly-behind response,
    // without inventing a cone angle. Already distant players need no repositioning.
    internal static Vector2? BashRear(Vector2 player, Cast cast, double now)
    {
        if (cast == null || cast.Action != GlacierBash || now > cast.Finish + BashRecovery
            || Vector2.DistanceSquared(player, cast.Position) > 8.5f * 8.5f)
            return null;

        // Eight-yalm cast range plus the usual half-yalm margin. Three yalms behind
        // the caster clears its two-yalm hitbox while keeping the crossing short.
        Vector2 rear = cast.Position - new Vector2(MathF.Sin(cast.Heading), MathF.Cos(cast.Heading)) * 3;
        return rear.LengthSquared() <= Arena * Arena ? rear : null;
    }

    internal sealed record Hazard(uint Owner, uint Action, Vector2 Position, float Radius)
    {
        internal bool Contains(Vector2 point) => Vector2.DistanceSquared(point, Position) <= Radius * Radius;
    }

    /// <summary>
    /// Publishes the earliest icicle cohort and targeted spreads. A self mark excludes
    /// other players, never the carrier's own position, so escape cannot chase its center.
    /// </summary>
    internal static Hazard[] Build(uint self, Member[] party, Cast[] casts, double now)
    {
        Cast[] live = casts.Where(c => c.Finish + Grace >= now).ToArray();
        double first = live.Where(c => c.Action == IcicleImpact).Select(c => c.Finish)
            .DefaultIfEmpty(double.PositiveInfinity).Min();
        List<Hazard> hazards = [];
        Hazard[] icicles = live.Where(c => c.Action == IcicleImpact)
            .Select(c => new Hazard(c.Owner, c.Action, c.Position, 10.5f)).ToArray();

        // The ninth circle appears before the first perimeter circle lands, briefly
        // covering every point. Wait on the first pair until that overlap opens, then
        // publish the remaining wave together so the late center does not trap us.
        // This only selects native hazards; native avoidance still chooses the path.
        if (HasSafePoint(icicles))
            hazards.AddRange(icicles);
        else
        {
            foreach (Cast cast in live.Where(c => c.Action == IcicleImpact && c.Finish <= first + 0.75))
                hazards.Add(new(cast.Owner, cast.Action, cast.Position, 10.5f));
        }

        HashSet<uint> marked = live.Where(c => c.Action == Hailstorm && c.Target != 0)
            .Select(c => c.Target).ToHashSet();
        bool selfMarked = marked.Contains(self);
        foreach (Member member in party.Where(m => m.Id != self && (selfMarked || marked.Contains(m.Id))))
            hazards.Add(new(member.Id, Hailstorm, member.Position, 6.5f));

        return hazards.ToArray();
    }

    private static bool HasSafePoint(Hazard[] hazards)
    {
        if (hazards.All(h => !h.Contains(Vector2.Zero)))
            return true;

        // Half-yalm rings and 256 spokes resolve the narrow first-open icicle wedge
        // without depending on a player's current position or introducing a destination.
        for (float radius = 0.5f; radius <= Arena; radius += 0.5f)
        {
            for (int i = 0; i < 256; i++)
            {
                float angle = i * MathF.Tau / 256;
                Vector2 point = new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
                if (hazards.All(h => !h.Contains(point)))
                    return true;
            }
        }

        return false;
    }

    // Permafrost changes the floor rather than damaging its entire circle. The Hard
    // capture delivered status 604 about 1.7s after the cast finished; bridge 2.25s,
    // then let the aura own release. The adapter clears early cancelled casts.
    internal static bool HoldForIce(bool thinIce, bool castingPermafrost, double now, double expectedFinish)
        => thinIce || castingPermafrost || expectedFinish > 0 && now <= expectedFinish + 2.25;
}
