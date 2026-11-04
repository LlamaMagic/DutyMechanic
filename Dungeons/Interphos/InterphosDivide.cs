using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>Ordered line baits and the final party volley, without owning input.</summary>
/// <remarks>Two recorded waves identify eight sequential icon 521 targets,
/// then three newly retained icons for an all-party volley. The three-slot VFX
/// container cannot retain all eight simultaneous markers. Only a healthy read
/// after the observed ordered wave can promote that burst to the final volley.</remarks>
internal sealed class InterphosDivide
{
    internal readonly record struct Marker(long Pointer, ulong Target);
    internal readonly record struct Member(ulong Id, float X, float Z);
    internal readonly record struct Bait(ulong Target, double Impact, double Until);
    private HashSet<Marker> previous = [];
    private readonly List<Bait> baits = [];
    private double lastRead;
    private double lastOrdered;
    private double window;
    private int ordered;
    private bool primed;
    private bool final;
    internal IReadOnlyList<Bait> Baits => baits;
    internal bool Active => baits.Count != 0;

    internal void Begin(double now)
    {
        Reset();
        window = now + 30;
    }

    internal void Reset()
    {
        previous.Clear();
        baits.Clear();
        lastRead = lastOrdered = window = 0;
        ordered = 0;
        primed = final = false;
    }

    internal bool InWindow(double now) => now < window;

    internal void Advance(double now) => baits.RemoveAll(b => now >= b.Until);

    internal string Observe(Marker[] markers, Member[] party, double now)
    {
        Advance(now);
        if (!InWindow(now))
        {
            Reset();
            return null;
        }
        var current = markers.ToHashSet();
        // Never assign a fresh three-second lead to stale markers after a gap.
        // Existing pending baits expire normally, and a later fresh icon can
        // still be handled individually without inventing missing order.
        if (!primed || now - lastRead > 0.5)
        {
            previous = current;
            lastRead = now;
            primed = true;
            ordered = 0;
            return null;
        }
        var fresh = current.Where(m => !previous.Contains(m) && party.Any(p => p.Id == m.Target)).ToArray();
        previous = current;
        lastRead = now;
        if (fresh.Length == 0)
        {
            return null;
        }

        bool volley = !final && ordered == 8 && fresh.Length >= 2 && now - lastOrdered > 2;
        // The icon precedes its effect by three seconds. Reserve 200 ms for
        // observation uncertainty and retain the bait for 200 ms after impact.
        var targets = volley ? party.Select(p => p.Id) : fresh.Select(m => m.Target);
        foreach (ulong target in targets.Distinct())
        {
            baits.Add(new(target, now + 2.8, now + 3.2));
        }

        if (volley)
        {
            final = true;
        }
        else if (fresh.Length == 1 && !final)
        {
            ordered++;
            lastOrdered = now;
        }
        else
        {
            ordered = 0;
        }

        return $"ordered={ordered} final={volley} targets={string.Join(",", targets.Select(id => id.ToString("X")))}";
    }

    // The 100-by-5-yalm ray needs 2.5 half-width, 0.5 clearance and 0.25 arrival
    // tolerance. A marked player cannot dodge their own line; keep it off allies.
    internal static bool InRay(float x, float z, float ox, float oz, float tx, float tz)
    {
        float dx = tx - ox, dz = tz - oz, length = MathF.Sqrt(dx * dx + dz * dz);
        if (length < 0.01f)
        {
            return true;
        }

        float along = ((x - ox) * dx + (z - oz) * dz) / length;
        float across = ((x - ox) * dz - (z - oz) * dx) / length;
        return along >= -0.75f && along <= 100.75f && MathF.Abs(across) <= 3.25f;
    }

    internal bool SafePoint(float x, float z, float ox, float oz, ulong self, Member[] party)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        foreach (var bait in baits)
        {
            if (bait.Target == self)
            {
                foreach (var ally in party)
                {
                    if (ally.Id != self && InRay(ally.X, ally.Z, ox, oz, x, z))
                    {
                        return false;
                    }
                }
            }
            else
            {
                foreach (var target in party)
                {
                    if (target.Id == bait.Target && InRay(x, z, ox, oz, target.X, target.Z))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }

    internal bool SafePath(float sx, float sz, float tx, float tz, float ox, float oz,
        ulong self, Member[] party, double now, float speed)
    {
        if (!SafePoint(tx, tz, ox, oz, self, party) || !float.IsFinite(speed) || speed <= 0)
        {
            return false;
        }

        float distance = MathF.Sqrt((tx - sx) * (tx - sx) + (tz - sz) * (tz - sz));
        // Check each predicted snapshot, not a permanent barrier across every
        // pending ray. This allows ordinary walking through a future line before
        // it fires. Re-evaluation each pulse uses the actual changed target/speed.
        foreach (var bait in baits)
        {
            // If already late, permit a direct egress rather than pinning the
            // player in a ray whose predicted snapshot has passed.
            float f = distance < 0.01f || bait.Impact <= now ? 1 : Math.Clamp((float)(bait.Impact - now) * speed / distance, 0, 1);
            float x = sx + (tx - sx) * f, z = sz + (tz - sz) * f;
            if (bait.Target == self)
            {
                foreach (var ally in party)
                {
                    if (ally.Id != self && InRay(ally.X, ally.Z, ox, oz, x, z))
                    {
                        return false;
                    }
                }
            }
            else
            {
                foreach (var target in party)
                {
                    if (target.Id == bait.Target && InRay(x, z, ox, oz, target.X, target.Z))
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }
}
