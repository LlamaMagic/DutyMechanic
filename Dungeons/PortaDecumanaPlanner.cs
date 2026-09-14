using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Scalar-only Porta Decumana geometry and resolution ordering. The adapter reads RB objects on
/// the bot thread; this planner never retains wrappers or changes combat/navigation globals.
/// </summary>
internal static class PortaDecumanaPlanner
{
    // Both platforms have a 20-yalm radius; leave one yalm between planned routes and the wall.
    internal const float ArenaRadius = 19f;
    internal const float Margin = 0.5f;
    // Polling can lose a cast bar before its effect. Retain near-finished casts for 250ms,
    // but clear early cancellations. This is a fallback, not a measured action-effect delay.
    internal const double EffectGrace = 0.25;
    internal const double SameStage = 0.35;

    /// <summary>
    /// Issues one local-collision refresh after the new platform is loaded and stable. Half a
    /// second allows the post-cutscene world collision to settle; failed readiness rearms retries.
    /// This state is separate from combat Reset so a pre-pull idle tick cannot cause rebuild loops.
    /// </summary>
    internal sealed class NavigationResetLatch
    {
        private double readySince = -1;
        private bool issued;

        internal bool Observe(bool ready, double now)
        {
            if (!ready)
            {
                readySince = -1;
                issued = false;
                return false;
            }
            if (issued)
                return false;
            if (readySince < 0)
                readySince = now;
            if (now - readySince < 0.5)
                return false;
            issued = true;
            return true;
        }
    }

    // IDs are damage/knockback-owning actions, not boss choreography visuals. The June 25 capture
    // corroborates Landslide, Plume, Shriek, Ray, Cannon and the wrongly avoided stack helper.
    internal const uint Geocrush = 28999;
    internal const uint TitanLandslide = 29000;
    internal const uint UltimaLandslide = 28981;
    internal const uint Weight = 29001;
    internal const uint Eye = 28980;
    internal const uint Shriek = 28997;
    internal const uint Plume = 28983;
    internal const uint Vulcan = 29003;

    internal const uint RayForward = 29008;
    internal const uint RayRight = 29009;
    internal const uint RayLeft = 29010;
    internal const uint Spread = 29012;
    internal const uint Stack = 29014;
    internal const uint Boom = 29015;
    internal const uint Cannon = 29019;
    internal const uint Buster = 29020;
    internal const uint Explosion = 29021;

    /// <summary>World-space hazard shape; rectangle length starts at its caster, not its center.</summary>
    internal enum Shape { Circle, Donut, Rectangle }

    /// <summary>Required positive positioning; unlike an AOE, these regions must be entered.</summary>
    internal enum GoalKind { Stack, Knockback, Orb, TankPosition }

    /// <summary>Stable cast identity and geometry copied from one bot-thread frame.</summary>
    /// <param name="Owner">Caster object ID, also used for deterministic ordering.</param>
    /// <param name="Action">Damage-owning action ID.</param>
    /// <param name="Origin">Caster position, or actual target position for a targeted mechanic.</param>
    /// <param name="Forward">Normalized X/Z facing, converted by the RB adapter.</param>
    /// <param name="Finish">Expected UTC resolution time in seconds.</param>
    /// <param name="Target">Target object ID; zero means unresolved, never an arbitrary party member.</param>
    internal sealed record Cast(uint Owner, uint Action, Vector2 Origin, Vector2 Forward, double Finish, uint Target = 0);

    /// <summary>Inflated damaging geometry shared by RB avoidance and positive-position routing.</summary>
    internal sealed record Hazard(Cast Cast, Shape Shape, float Radius, float Inner = 0, float HalfWidth = 0)
    {
        internal bool Contains(Vector2 point)
        {
            Vector2 delta = point - Cast.Origin;
            float distance = delta.Length();
            return Shape switch
            {
                Shape.Circle => distance <= Radius,
                Shape.Donut => distance >= Inner && distance <= Radius,
                // Facing vectors avoid applying a second heading convention in polygon generation.
                _ => Vector2.Dot(delta, Cast.Forward) >= -Margin
                    && Vector2.Dot(delta, Cast.Forward) <= Radius
                    && Math.Abs(delta.X * Cast.Forward.Y - delta.Y * Cast.Forward.X) <= HalfWidth,
            };
        }

        internal Vector2[] Polygon()
        {
            if (Shape == Shape.Rectangle)
            {
                Vector2 side = new(Cast.Forward.Y, -Cast.Forward.X);
                return [Cast.Origin - Cast.Forward * Margin + side * HalfWidth,
                    Cast.Origin + Cast.Forward * Radius + side * HalfWidth,
                    Cast.Origin + Cast.Forward * Radius - side * HalfWidth,
                    Cast.Origin - Cast.Forward * Margin - side * HalfWidth];
            }

            // Circumscribe circles so polygon chords cannot shave away the declared 0.5-yalm
            // safety margin. Donut inner chords deliberately shrink the allowed region slightly.
            const int segments = 96;
            List<Vector2> points = [];
            for (int i = 0; i <= segments; ++i)
                points.Add(Cast.Origin + Direction(i * MathF.Tau / segments) * (Radius / MathF.Cos(MathF.PI / segments)));
            if (Shape == Shape.Donut)
                for (int i = segments; i >= 0; --i)
                    points.Add(Cast.Origin + Direction(i * MathF.Tau / segments) * Inner);
            return points.ToArray();
        }
    }

    /// <summary>One semantic owner with a resolution deadline and a permitted destination region.</summary>
    internal sealed record Goal(GoalKind Kind, uint Owner, Vector2 Center, float Radius, double Finish)
    {
        internal bool Contains(Vector2 point, Vector2 arena)
        {
            Vector2 delta = point - Center;
            if (delta.Length() > Radius)
                return false;
            // Vulcan pushes 15 yalms away from Ifrit. Check the landing as well as the staging
            // point; standing exactly on the source has an undefined knockback direction.
            return Kind != GoalKind.Knockback || (delta.Length() >= 0.5f
                && Vector2.Distance(point + Vector2.Normalize(delta) * 15f, arena) < ArenaRadius);
        }
    }

    /// <summary>One resolution stage, published atomically to avoidance and semantic movement.</summary>
    internal sealed record Plan(Hazard[] Hazards, Goal[] Goals, Vector2? Destination, string Stage, bool Feasible);

    internal static Vector2 Direction(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));

    // RB's world heading differs from the mathematical angle used to draw a circle above.
    internal static Vector2 Forward(float heading) => new(MathF.Sin(heading), MathF.Cos(heading));

    /// <summary>
    /// Picks a melee position opposite the party, facing toward the wall. This is an idle
    /// preference only; the adapter must suspend it for casts, orbs, recovery, and avoidance.
    /// </summary>
    internal static Vector2? TankDestination(Vector2 arena, Vector2 boss, Vector2 player,
        float meleeRange, Vector2[] party, Vector2? previous)
    {
        if (party.Length == 0 || !Safe(player, arena, []) || !float.IsFinite(meleeRange) || meleeRange < 1f)
            return null;

        // Average directions, not positions, so one distant ranged player cannot outweigh
        // the rest of the group. Ignore allies inside the boss center, whose bearing is unstable.
        Vector2[] bearings = party.Select(p => p - boss).Where(p => p.Length() >= 2f)
            .Select(Vector2.Normalize).ToArray();
        if (bearings.Length == 0)
            return null;
        Vector2 mean = bearings.Aggregate(Vector2.Zero, (sum, direction) => sum + direction) / bearings.Length;
        // Scattered allies have no useful shared rear. Leave positioning to the routine.
        if (mean.Length() < 0.5f)
            return null;
        Vector2 away = -Vector2.Normalize(mean);

        // Preserve a valid anchor within 45 degrees of the desired facing. Small party steps
        // must not rotate the boss continuously. Recheck reach when the boss itself moves.
        if (previous.HasValue)
        {
            Vector2 offset = previous.Value - boss;
            if (offset.Length() >= 1f && offset.Length() <= meleeRange
                && Vector2.Distance(previous.Value, arena) <= 17f
                && Vector2.Dot(Vector2.Normalize(offset), away) >= 0.7071068f)
                return previous;
        }

        // Seventeen yalms leaves three to the physical wall for later dodges. Stop sooner
        // when necessary to retain melee range instead of dragging the boss to the boundary.
        Vector2 relativeBoss = boss - arena;
        float projection = Vector2.Dot(relativeBoss, away);
        float discriminant = projection * projection + 17f * 17f - relativeBoss.LengthSquared();
        if (discriminant < 0)
            return null;
        float distance = Math.Min(meleeRange, -projection + MathF.Sqrt(discriminant));
        if (distance < 1f)
            return null;
        Vector2 destination = boss + away * distance;
        return SafeSegment(player, destination, arena, []) ? destination : null;
    }

    // Only these actions have repeatable RB path failures in live captures. Other mechanics
    // retain ordinary avoidance until their own evidence justifies manual recovery.
    internal static bool SupportsRecovery(bool firstPhase, uint action) => firstPhase
        ? action is Geocrush or UltimaLandslide or Weight or Shriek or Plume
        : action is RayForward or RayRight or RayLeft or Spread;

    // The September 10 capture repeatedly failed SpanRef lookup on the phase-two east floor.
    // Recovery may leave hazards already containing the player, but may never enter a new one,
    // increase penetration, re-enter an escaped hazard, or leave the physical 20-yalm platform.
    internal static bool EscapeSegment(Vector2 from, Vector2 to, Vector2 arena, Hazard[] hazards)
    {
        if (!Safe(to, arena, hazards) || Vector2.Distance(from, arena) > 20f)
            return false;
        float[] previous = hazards.Select(h => Penetration(h, from)).ToArray();
        float boundary = Math.Max(0, Vector2.Distance(from, arena) - (ArenaRadius - 0.05f));
        int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) / 0.1f));
        for (int i = 1; i <= steps; ++i)
        {
            Vector2 p = Vector2.Lerp(from, to, (float)i / steps);
            float edge = Math.Max(0, Vector2.Distance(p, arena) - (ArenaRadius - 0.05f));
            if (edge > boundary + 0.001f)
                return false;
            boundary = edge;
            for (int j = 0; j < hazards.Length; ++j)
            {
                float depth = Penetration(hazards[j], p);
                if (depth > previous[j] + 0.001f)
                    return false;
                previous[j] = depth;
            }
        }
        return true;
    }

    private static float Penetration(Hazard hazard, Vector2 point)
    {
        Vector2 delta = point - hazard.Cast.Origin;
        if (hazard.Shape == Shape.Circle)
            return Math.Max(0, hazard.Radius - delta.Length());
        if (hazard.Shape == Shape.Donut)
            return Math.Max(0, Math.Min(delta.Length() - hazard.Inner, hazard.Radius - delta.Length()));
        float forward = Vector2.Dot(delta, hazard.Cast.Forward);
        float side = Math.Abs(delta.X * hazard.Cast.Forward.Y - delta.Y * hazard.Cast.Forward.X);
        return Math.Max(0, Math.Min(Math.Min(forward + Margin, hazard.Radius - forward), hazard.HalfWidth - side));
    }

    internal static Vector2? RecoveryDestination(Vector2 arena, Vector2 player, Hazard[] hazards, Vector2? previous)
    {
        if (previous.HasValue && EscapeSegment(player, previous.Value, arena, hazards))
            return previous;
        return Grid(arena).Where(p => Safe(p, arena, hazards))
            .OrderBy(p => Vector2.DistanceSquared(p, player))
            .Where(p => EscapeSegment(player, p, arena, hazards)).Select(p => (Vector2?)p).FirstOrDefault();
    }

    internal static Goal OrbGoal(uint owner, Vector2 position, Vector2 arena)
    {
        // A half-yalm grid can miss the narrow contact region near the wall. Use an exact
        // inward point and wait for the orb to enter; its center may still be off the platform.
        float distance = Vector2.Distance(position, arena);
        Vector2 inward = distance > 18.8f ? arena + Vector2.Normalize(position - arena) * 18.8f : position;
        return new(GoalKind.Orb, owner, inward, 0.1f, double.PositiveInfinity);
    }

    /// <summary>
    /// Predicts contact from measured travel, not actor facing. A five-yalm/second approach
    /// budget and 350ms head start leave room to stop before collision. Keep a reachable point
    /// on the same trajectory until the orb passes it; refreshing it every tick causes chasing.
    /// No forward interception on verified floor means wait rather than pursue from behind.
    /// </summary>
    internal static Vector2? OrbIntercept(Vector2 position, Vector2 velocity, Vector2 player,
        Vector2 arena, Vector2? previous, Hazard[] hazards = null)
    {
        hazards ??= [];
        // Knockback egress owns the mover until the start itself is safe. Never turn a wall
        // landing into an impossible orb route or estimate arrival through an active hazard.
        if (!Safe(player, arena, hazards))
            return null;
        float speed = velocity.Length();
        if (speed < 0.1f)
        {
            Vector2 stationary = previous ?? OrbGoal(0, position, arena).Center;
            return float.IsFinite(RouteLength(player, stationary, arena, hazards)) ? stationary : null;
        }
        Vector2 direction = velocity / speed;
        if (previous.HasValue)
        {
            Vector2 delta = previous.Value - position;
            float ahead = Vector2.Dot(delta, direction);
            float lateral = (delta - ahead * direction).Length();
            float travel = RouteLength(player, previous.Value, arena, hazards);
            if (Safe(previous.Value, arena, hazards) && lateral <= 0.5f && ahead >= -0.8f
                && (travel <= 0.2f || travel / 5f + 0.35f <= ahead / speed))
                return previous;
        }
        // Eight seconds bounds prediction on this small platform; observed direction changes
        // invalidate the latch above instead of extrapolating a turn or leaving the arena.
        for (float time = 0.5f; time <= 8f; time += 0.25f)
        {
            Vector2 point = position + velocity * time;
            if (Safe(point, arena, hazards) && RouteLength(player, point, arena, hazards) / 5f + 0.35f <= time)
                return point;
        }
        return null;
    }

    /// <summary>
    /// Measures the actual safe waypoint route used by movement, including detours. Infinity
    /// rejects unreachable or cyclic routes; a straight-line estimate can promise an orb
    /// interception that the character cannot reach before it passes.
    /// </summary>
    internal static float RouteLength(Vector2 from, Vector2 to, Vector2 arena, Hazard[] hazards)
    {
        float length = 0;
        HashSet<Vector2> visited = [];
        // The arena grid has fewer than 1600 cells; cap work and fail closed on a broken route.
        for (int i = 0; i < 1600; ++i)
        {
            if (!visited.Add(from))
                return float.PositiveInfinity;
            Vector2? next = NextWaypoint(from, to, arena, hazards);
            if (!next.HasValue)
                return float.PositiveInfinity;
            length += Vector2.Distance(from, next.Value);
            if (next.Value == to)
                return length;
            from = next.Value;
        }
        return float.PositiveInfinity;
    }

    internal static bool KeepAfterDisappearance(Cast cast, double now, bool ownerAlive) =>
        ownerAlive && cast.Finish - now <= SameStage && now <= cast.Finish + EffectGrace;

    internal static Hazard ToHazard(Cast cast, uint playerId) => cast.Action switch
    {
        // Proximity falloff is not a binary radius. Preserve the old conservative distances
        // pending fresh damage-vs-distance capture, including Explosion's Cannon-first handoff.
        Geocrush => new(cast, Shape.Circle, 25f),
        Explosion => new(cast, Shape.Circle, 17.4f),
        Weight => new(cast, Shape.Circle, 6f + Margin),
        Shriek => new(cast, Shape.Circle, 23f + Margin),
        Plume => new(cast, Shape.Circle, 8f + Margin),
        Eye => new(cast, Shape.Donut, 25f + Margin, 12.5f - Margin),
        TitanLandslide or UltimaLandslide or RayForward or RayRight or RayLeft => new(cast, Shape.Rectangle, 40f + Margin, HalfWidth: 3f + Margin),
        Cannon => new(cast, Shape.Rectangle, 40f + Margin, HalfWidth: 2f + Margin),
        Buster => new(cast, Shape.Rectangle, 40f + Margin, HalfWidth: 6f + Margin),
        Spread when cast.Target != 0 && cast.Target != playerId => new(cast, Shape.Circle, 6f + Margin),
        _ => null,
    };

    /// <summary>
    /// Keeps shared safety when possible, otherwise selects the first resolving stage. Cannon's
    /// beams always precede airship proximity, even if their cast bars are discovered in reverse.
    /// Simultaneous incompatible requirements fail closed rather than choosing by registration order.
    /// </summary>
    internal static Plan Build(Vector2 arena, Vector2 player, uint playerId, IReadOnlyList<Cast> casts,
        Goal orb, Vector2? previousDestination, Vector2[] party = null)
    {
        bool cannon = casts.Any(c => c.Action == Cannon);
        Hazard[] hazards = casts.Select(c => ToHazard(c, playerId)).Where(h => h != null
            && !(cannon && h.Cast.Action == Explosion)).OrderBy(h => h.Cast.Finish).ThenBy(h => h.Cast.Owner).ToArray();
        List<Goal> goals = [];
        foreach (Cast cast in casts)
        {
            if (cast.Action == Stack && cast.Target != 0)
            {
                // A marked player must join the party, not hold an isolated position. Prefer
                // the densest ally group; without visible allies, retain the known target position.
                Vector2 center = cast.Origin;
                if (cast.Target == playerId && party is { Length: > 0 })
                    center = party.OrderByDescending(p => party.Count(other => Vector2.Distance(p, other) <= 5f))
                        .ThenBy(p => Vector2.DistanceSquared(p, player)).First();
                goals.Add(new(GoalKind.Stack, cast.Owner, center, 1f, cast.Finish));
            }
            if (cast.Action == Vulcan)
                goals.Add(new(GoalKind.Knockback, cast.Owner, cast.Origin, 4f, cast.Finish));
        }
        // Orbs have no cast deadline. Do not invent one or charge through a resolving stack,
        // spread, beam, or knockback to reach them; try them alongside safe geometry only.
        if (orb != null && goals.Count == 0 && !casts.Any(c => c.Action is Boom or Spread or Stack or Vulcan))
            goals.Add(orb);
        Goal[] selectedGoals = goals.ToArray();
        Vector2? destination = FindDestination(arena, player, hazards, selectedGoals, previousDestination);
        // Tight grouping is a preference, not permission to discard a concurrent beam. If the
        // one-yalm approach is obstructed, use the real stack's six-yalm radius minus one yalm
        // of slack before falling back to time ordering. The target/group anchor stays the same.
        if (destination == null && goals.Any(g => g.Kind == GoalKind.Stack))
        {
            goals = goals.Select(g => g.Kind == GoalKind.Stack ? g with { Radius = 5f } : g).ToList();
            selectedGoals = goals.ToArray();
            destination = FindDestination(arena, player, hazards, selectedGoals, previousDestination);
        }
        string stage = cannon ? "Assault Cannon before Explosion" : "shared safety";
        if (destination == null && (hazards.Length > 0 || goals.Count > 0))
        {
            double first = hazards.Select(h => h.Cast.Finish).Concat(goals.Select(g => g.Finish)).Min();
            hazards = hazards.Where(h => h.Cast.Finish <= first + SameStage).ToArray();
            selectedGoals = goals.Where(g => g.Finish <= first + SameStage).ToArray();
            destination = FindDestination(arena, player, hazards, selectedGoals, previousDestination);
            stage = "first resolving stage";
        }
        // If Cannon divides the arena, approach the stack from our reachable side until the
        // beams clear. This staging applies only when the stack resolves after the beams;
        // a simultaneous stack must still satisfy both requirements.
        if (cannon && selectedGoals.Length == 0 && Safe(player, arena, hazards)
            && goals.Any(g => g.Kind == GoalKind.Stack) && hazards.Any(h => h.Cast.Action == Cannon)
            && goals.Where(g => g.Kind == GoalKind.Stack).Min(g => g.Finish)
                > hazards.Max(h => h.Cast.Finish) + SameStage)
        {
            Goal stack = goals.First(g => g.Kind == GoalKind.Stack);
            Vector2 staging = ReachablePoints(player, arena, hazards).Append(player)
                .OrderBy(p => Vector2.DistanceSquared(p, stack.Center))
                .ThenBy(p => Vector2.DistanceSquared(p, player)).First();
            selectedGoals = [new(GoalKind.Stack, stack.Owner, staging, 0.1f, hazards.Max(h => h.Cast.Finish))];
            destination = staging;
            stage = "Cannon-safe stack staging";
        }
        return new(hazards, selectedGoals, selectedGoals.Length > 0 ? destination : null, stage, destination != null);
    }

    internal static bool Safe(Vector2 point, Vector2 arena, IReadOnlyList<Hazard> hazards) =>
        Vector2.Distance(point, arena) < ArenaRadius - 0.05f && !hazards.Any(h => h.Contains(point));

    private static Vector2? FindDestination(Vector2 arena, Vector2 player, Hazard[] hazards, Goal[] goals, Vector2? previous)
    {
        // Only flood the safe component when a direct segment is blocked. Compute it once per
        // candidate search rather than running a full route search for every half-yalm sample.
        Vector2[] reachable = null;
        bool Reachable(Vector2 p)
        {
            if (goals.Length == 0 || !Safe(player, arena, hazards)) return true; // RB escapes first.
            if (SafeSegment(player, p, arena, hazards))
                return true;
            reachable ??= ReachablePoints(player, arena, hazards).ToArray();
            return reachable.Any(cell => Vector2.DistanceSquared(cell, p) <= 2.25f
                && SafeSegment(cell, p, arena, hazards));
        }
        bool Valid(Vector2 p) => Safe(p, arena, hazards) && goals.All(g => g.Contains(p, arena)) && Reachable(p);
        if (previous.HasValue && Valid(previous.Value))
            return previous;
        if (Valid(player))
            return player;
        // Half-yalm samples resolve narrow stack/beam intersections. Exact goal centers are
        // included so a moving orb cannot disappear between samples. No floor outside this
        // flat, circular trial arena is inferred to be traversable.
        IEnumerable<Vector2> candidates = goals.Select(g => g.Center).Concat(Grid(arena));
        return candidates.Where(Valid).OrderBy(p => Vector2.DistanceSquared(p, player)).Select(p => (Vector2?)p).FirstOrDefault();
    }

    private static IEnumerable<Vector2> Grid(Vector2 arena)
    {
        for (float x = -19; x <= 19; x += 0.5f)
            for (float y = -19; y <= 19; y += 0.5f)
                yield return arena + new Vector2(x, y);
    }

    /// <summary>
    /// Enumerates the same one-yalm safe component used by semantic routing. Every edge and
    /// start attachment is checked, so reachable stack selection cannot bridge a Cannon lane.
    /// </summary>
    private static IEnumerable<Vector2> ReachablePoints(Vector2 player, Vector2 arena, Hazard[] hazards)
    {
        if (!Safe(player, arena, hazards)) yield break;
        var start = ((int)MathF.Round(player.X - arena.X), (int)MathF.Round(player.Y - arena.Y));
        Queue<(int X, int Y)> queue = new();
        HashSet<(int X, int Y)> seen = [];
        Vector2 Point((int X, int Y) cell) => arena + new Vector2(cell.X, cell.Y);
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
            {
                var cell = (start.Item1 + x, start.Item2 + y);
                if (SafeSegment(player, Point(cell), arena, hazards) && seen.Add(cell)) queue.Enqueue(cell);
            }
        while (queue.TryDequeue(out var cell))
        {
            yield return Point(cell);
            for (int x = -1; x <= 1; ++x)
                for (int y = -1; y <= 1; ++y)
                {
                    var next = (cell.X + x, cell.Y + y);
                    if (Math.Abs(next.Item1) > 19 || Math.Abs(next.Item2) > 19 || seen.Contains(next)
                        || !SafeSegment(Point(cell), Point(next), arena, hazards)) continue;
                    seen.Add(next);
                    queue.Enqueue(next);
                }
        }
    }

    internal static bool SafeSegment(Vector2 from, Vector2 to, Vector2 arena, IReadOnlyList<Hazard> hazards)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(from, to) / 0.25f));
        for (int i = 0; i <= steps; ++i)
            if (!Safe(Vector2.Lerp(from, to, (float)i / steps), arena, hazards))
                return false;
        return true;
    }

    /// <summary>
    /// Routes semantic movement around the same hazards registered with RB. Pure geometric
    /// escapes remain RB-owned. A bounded one-yalm grid is sufficient on these flat platforms;
    /// every edge and shortcut is checked at quarter-yalm spacing, so corners cannot cut a beam.
    /// </summary>
    internal static Vector2? NextWaypoint(Vector2 from, Vector2 destination, Vector2 arena, Hazard[] hazards)
    {
        if (SafeSegment(from, destination, arena, hazards))
            return destination;
        if (!Safe(from, arena, hazards))
            return null; // RB first escapes the active avoid; never override that movement.
        var start = ((int)MathF.Round(from.X - arena.X), (int)MathF.Round(from.Y - arena.Y));
        Vector2 Point((int X, int Y) cell) => arena + new Vector2(cell.X, cell.Y);
        Queue<(int X, int Y)> queue = new();
        Dictionary<(int X, int Y), (int X, int Y)> parents = [];
        for (int x = -1; x <= 1; ++x)
            for (int y = -1; y <= 1; ++y)
            {
                var cell = (start.Item1 + x, start.Item2 + y);
                if (SafeSegment(from, Point(cell), arena, hazards))
                {
                    parents[cell] = cell;
                    queue.Enqueue(cell);
                }
            }
        while (queue.TryDequeue(out var cell))
        {
            if (SafeSegment(Point(cell), destination, arena, hazards))
            {
                Vector2 waypoint = Point(cell);
                while (parents[cell] != cell)
                {
                    cell = parents[cell];
                    if (SafeSegment(from, waypoint, arena, hazards))
                        return waypoint;
                    waypoint = Point(cell);
                }
                return waypoint;
            }
            for (int x = -1; x <= 1; ++x)
                for (int y = -1; y <= 1; ++y)
                {
                    var next = (cell.X + x, cell.Y + y);
                    if (Math.Abs(next.Item1) > 19 || Math.Abs(next.Item2) > 19 || parents.ContainsKey(next)
                        || !SafeSegment(Point(cell), Point(next), arena, hazards))
                        continue;
                    parents[next] = cell;
                    queue.Enqueue(next);
                }
        }
        return null;
    }
}
