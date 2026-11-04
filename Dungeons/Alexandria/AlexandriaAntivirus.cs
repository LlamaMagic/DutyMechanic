using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    // The 2026-11-02 native-avoidance capture repeatedly delayed travel until
    // the next short cast, missing the next shelter despite advance actor data.
    // An authored sequence stages each wave; the shared input owner executes it
    // on the bot thread.
    private static AlexandriaInterferonGeometry.Hazard[] antivirusHazards = [];
    // A helper can leave the object table before its forecast effect. Retain
    // only detached coordinates for actors still in this wave or visible now.
    private static readonly AlexandriaNodeSnapshotCache antivirusNodes = new();
    private static bool antivirusIncomplete;
    private static bool antivirusPulseHandled, antivirusPulseMoving;
    private static string encounterMovementMode;
    private static bool antivirusUncertified;
    private static Vector2 encounterMovementTarget;
    private bool antivirusFacingLeased;
    private DateTime antivirusNextCastCancel;
    private static double MonotonicNow => (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;

    // Log only changed requests, not every pulse. Combining these monotonic
    // command times with actor-position samples can bound handoff latency;
    // the request itself does not prove that the game accepted the input.
    private static void TraceEncounterMovement(string mode, Vector2 player, Vector2 target, double now)
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled) { encounterMovementMode = null; return; }
        if (encounterMovementMode == mode && (!mode.EndsWith("-move") || encounterMovementTarget == target)) return;
        encounterMovementMode = mode; encounterMovementTarget = target;
        Logger.Information(FormattableString.Invariant($"[AlexandriaInput] mode={mode} monotonic={now:F6} player=({player.X:F3},{player.Y:F3}) target=({target.X:F3},{target.Y:F3}) casting={Core.Me.IsCasting}"));
    }

    private static void CaptureAntivirusHazards(double now, BattleCharacter[] actors)
    {
        var result = new System.Collections.Generic.List<AlexandriaInterferonGeometry.Hazard>();
        antivirusIncomplete = interferonSequence.UnanchoredUnresolved || immuneResponse.Incomplete;
        var forecast = interferonSequence.Forecast();
        var visible = actors.Where(a => (a.BaseId is 0x4174 or 0x4175) && a.IsVisible).ToArray();
        antivirusNodes.Refresh(visible.Select(a => new AlexandriaNodeSnapshotCache.Node(a.ObjectId, a.BaseId,
            new Vector2(a.X, a.Z))).ToArray(), forecast.Select(f => f.Id).ToArray());
        foreach (var entry in forecast)
        {
            if (entry.Latest + .3 < now) continue;
            if (!antivirusNodes.TryGet(entry.Id, out var node)) { antivirusIncomplete = true; continue; }
            // Later captured waves delivered the first effect as early as 9.46s
            // after visibility, earlier than the 9.9s forecast minus its old
            // .25s margin. A .55s margin covers the observed receipt by .11s;
            // the server snapshot and delivery delay remain unmeasured. A full
            // second made a captured wave infeasible in the current planner,
            // so this provisional bound needs live validation and refinement.
            result.Add(new(node.BaseId == 0x4175 ? AlexandriaInterferonGeometry.Shape.Cross : AlexandriaInterferonGeometry.Shape.Donut,
                node.Origin, default, 0, entry.Earliest - .55, entry.Latest + .3));
        }
        // When discovery order is ambiguous the forecast deliberately returns
        // no guessed sequence. Preserve every actually casting helper instead
        // of handing that window to native avoidance. These provisional finish
        // bounds are not a substitute for the missing effect-receipt evidence.
        var forecastIds = forecast.Select(f => f.Id).ToHashSet();
        foreach (var actor in actors.Where(a => (a.BaseId is 0x4174 or 0x4175) &&
                (a.CastingSpellId is 36382 or 36383) && a.SpellCastInfo.IsValid && !forecastIds.Contains(a.ObjectId)))
            {
                double castDue = now + actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
                var origin = new Vector2(actor.X, actor.Z);
                if (!AlexandriaGeometry.Finite(origin)) { antivirusIncomplete = true; continue; }
                result.Add(new(actor.BaseId == 0x4175 ? AlexandriaInterferonGeometry.Shape.Cross : AlexandriaInterferonGeometry.Shape.Donut,
                    origin, default, 0, now, castDue + .3));
            }
        foreach (var cone in immuneResponse.Active)
        {
            if (cone.Due is not { } due || due + .4 < now) continue;
            var actor = actors.FirstOrDefault(a => a.ObjectId == cone.Actor);
            if (actor != null)
            {
                var direction = new Vector2((float)Math.Sin(actor.Heading), (float)Math.Cos(actor.Heading));
                // The wide helper covers the rear/sides, leaving the forward wedge.
                if (cone.Action == 36381) direction = -direction;
                float cosine = (float)Math.Cos((cone.Action == 36381 ? 125 : 62.5) * Math.PI / 180);
                // Across the 2026-11-03 captures, cone receipts arrived as
                // early as 0.171s after the reported cast finish. The prior
                // lower bound of finish + 0.3s missed those events. Begin at
                // reported finish; the server snapshot may still precede the
                // local receipt, so this remains a provisional bound.
                var window = AlexandriaInterferonGeometry.ConeWindow(due);
                result.Add(new(AlexandriaInterferonGeometry.Shape.Cone, new(actor.X, actor.Z), direction, cosine, window.Earliest, window.Latest));
            }
            else antivirusIncomplete = true;
        }
        antivirusHazards = result.ToArray();
    }

    private static void ResetAntivirusRoute()
    {
        if (encounterMovementMode != null && LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Information(FormattableString.Invariant($"[AlexandriaInput] mode=release monotonic={MonotonicNow:F6}"));
        encounterMovementMode = null;
        antivirusUncertified = false;
        activeStackOwner?.ReleaseAntivirusFacing();
        antivirusHazards = [];
        antivirusIncomplete = false;
        antivirusPulseHandled = antivirusPulseMoving = false;
    }

    private void ReleaseAntivirusFacing()
    {
        if (!antivirusFacingLeased) return;
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Facing, "Alexandria route travel ended");
        antivirusFacingLeased = false; antivirusNextCastCancel = default;
    }

    // Use the same input owner as stacks and native-escape handoffs. The
    // pattern selects a destination; the current hazard snapshot still gates
    // each actual movement segment and rejects an unsafe or stale request.
    // Pattern routes use a 4-yalm donut and 3-yalm cross with a
    // half-yalm positional reserve. Cone angles retain the existing angular
    // reserve; their exact values still need live verification.
    private static AlexandriaGeometry.Hazard[] PatternHazards(double now)
    {
        var result = new System.Collections.Generic.List<AlexandriaGeometry.Hazard>();
        foreach (var hazard in antivirusHazards)
        {
            double earliest = hazard.Earliest, latest = hazard.Latest;
            if (hazard.Shape == AlexandriaInterferonGeometry.Shape.Cone)
                latest -= .4; // ConeWindow adds .4 beyond the observed-cast release.
            else if (antivirusPattern.NodeWindow(hazard.Origin) is { } window)
                (earliest, latest) = window;
            if (latest < now || latest < earliest) continue;
            var shape = hazard.Shape switch
            {
                AlexandriaInterferonGeometry.Shape.Cross => new AlexandriaGeometry.Hazard(AlexandriaGeometry.Kind.Cross,
                    hazard.Origin, Vector2.UnitX, 200, 0, 3, .5, earliest, latest),
                AlexandriaInterferonGeometry.Shape.Donut => new AlexandriaGeometry.Hazard(AlexandriaGeometry.Kind.Annulus,
                    hazard.Origin, Vector2.UnitX, 4, 200, 0, .5, earliest, latest),
                AlexandriaInterferonGeometry.Shape.Cone => new AlexandriaGeometry.Hazard(AlexandriaGeometry.Kind.Sector,
                    hazard.Origin, hazard.Direction, 200, Math.Acos(hazard.ConeCosine), 0, .5, earliest, latest),
                _ => default
            };
            result.Add(shape);
        }
        return result.ToArray();
    }

    private bool PositionForAntivirusPattern(AlexandriaAntivirusPattern.Decision decision, double now, out bool handled)
    {
        handled = false;
        if (!OwnsQuarantineMovement() || stackInputOwner?.IsCurrent != true || DateTime.UtcNow < quarantineUntil)
            return false;
        var player = new Vector2(Core.Me.X, Core.Me.Z);
        var target = decision.Target;
        var world = new Clio.Utilities.Vector3(target.X, Core.Me.Y, target.Y);
        double? sprintRemaining = Core.Me.Auras.Where(a => a.Id == 50)
            .Select(a => (double?)a.TimespanLeft.TotalSeconds).FirstOrDefault();
        double distance = Vector2.Distance(player, target);
        // Include the input delay once and reserve 1ms for subtraction at
        // large monotonic clock values; otherwise rounding rejects feasible moves.
        double end = now + .101 + distance / 5;
        double minimum = 0, maximum = 0;
        bool movementKnown = AlexandriaSprintPolicy.TrySnapshot(now, sprintRemaining, out var boost) &&
            AlexandriaTemporalPlanner.TryMovementBounds(now, end, 5,
                8, boost, out minimum, out maximum);
        var hazards = PatternHazards(now);
        bool safe = movementKnown &&
            AlexandriaInterferonGeometry.TravelSafe(player, distance <= .05 ? player : target, now, end,
                distance <= .05 ? 0 : .1, minimum, maximum, hazards);
        if (!safe && movementKnown && distance is > .05 and <= .25)
        {
            // A 0.14-yalm arrival shortfall put a player just outside the
            // half-yalm buffer while still inside the donut. Permit only this
            // short checked correction with a quarter-yalm minimum reserve.
            var correction = hazards.Select(h => h with { Margin = .25 }).ToArray();
            safe = AlexandriaInterferonGeometry.TravelSafe(player, target, now, end,
                .1, minimum, maximum, correction);
        }
        if (!safe && movementKnown && antivirusPattern.TryIntermediate(decision.Stage, player, target,
            now, minimum, maximum, hazards, out var intermediate))
        {
            target = intermediate;
            distance = Vector2.Distance(player, target);
            world = new Clio.Utilities.Vector3(target.X, Core.Me.Y, target.Y);
            safe = true;
        }
        bool avoided = AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(world));
        if (antivirusIncomplete || !movementKnown || avoided)
        {
            // Missing evidence cannot justify a new destination. Keep this
            // encounter's input owner and retry when the observation recovers.
            TraceEncounterMovement($"pattern-{decision.Pattern}-{decision.Stage}-evidence-hold", player, target, now);
            handled = true;
            ReleaseAntivirusFacing();
            stackInputOwner.HoldStack();
            return false;
        }
        // The previous worker repeatedly stopped a viable authored move when
        // its conservative speed envelope could not certify arrival. Continue
        // toward the recognized pattern point after trying a checked short step.
        if (!safe && !antivirusUncertified && LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Information($"[AlexandriaPattern] continuing pattern={decision.Pattern} stage={decision.Stage} player={player} target={target}");
        antivirusUncertified = !safe;
        handled = true;
        stackInputOwner.CompleteEscape();
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.ForceMoved || MovementManager.GenerallyOutOfControl)
        { ReleaseAntivirusFacing(); return false; }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Alexandria pattern route");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Alexandria pattern route");
        stackLeased = true;
        if (distance <= .05)
        {
            ReleaseAntivirusFacing();
            TraceEncounterMovement($"pattern-{decision.Pattern}-{decision.Stage}-hold", player, target, now);
            stackInputOwner.HoldStack();
            return false;
        }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Alexandria pattern travel facing");
        antivirusFacingLeased = true;
        if (Core.Me.IsCasting && DateTime.UtcNow >= antivirusNextCastCancel)
        { ActionManager.StopCasting(); antivirusNextCastCancel = DateTime.UtcNow.AddMilliseconds(200); }
        TraceEncounterMovement($"pattern-{decision.Pattern}-{decision.Stage}-move", player, target, now);
        stackInputOwner.MoveRoute(world);
        return true;
    }

}

// The first two ordered helpers identify each of the four patterns observed in
// 134 complete waves. Points derive from no-hit Trust samples, with extra cone
// and cross clearance; mirrored waves use the reflected arena coordinates.
// Cast completion controls departure: a forecast alone never releases a node.
internal sealed class AlexandriaAntivirusPattern
{
    internal readonly record struct Node(uint Id, uint BaseId, Vector2 Position, bool Visible,
        uint CastAction, double Remaining);
    internal readonly record struct Cone(uint Action, double Due);
    internal enum State { Waiting, Ready, Complete, Invalid }
    internal readonly record struct Decision(State State, string Pattern, int Stage, Vector2 Target);

    private static readonly Vector2[] Slots = [new(842, 813), new(842, 823), new(852, 823), new(862, 823), new(862, 813)];
    private readonly uint[] ids = new uint[5];
    private readonly char[] shapes = new char[5];
    private readonly double?[] castEnds = new double?[5];
    private bool started, invalid, complete, mirrored;
    private double firstSeen, lastObserved, lastVisible;
    private string pattern;

    internal bool Active => started && !invalid && !complete;

    internal void Reset()
    {
        Array.Clear(ids); Array.Clear(shapes); Array.Clear(castEnds);
        started = invalid = complete = mirrored = false;
        firstSeen = lastObserved = lastVisible = 0; pattern = null;
    }

    internal Decision Observe(double now, Node[] nodes, Cone[] cones)
    {
        if (!double.IsFinite(now) || now < 0 || nodes == null || cones == null || (started && now < lastObserved))
            return Reject();
        var visible = nodes.Where(n => n.Visible).ToArray();
        if ((complete || invalid) && visible.Length == 0 && now - lastVisible > 2)
        { Reset(); return new(State.Waiting, null, -1, default); }
        if (visible.Length != 0) lastVisible = now;
        if (complete) return new(State.Complete, pattern, 6, default);
        if (invalid) return new(State.Invalid, null, -1, default);
        lastObserved = now;
        if (!started)
        {
            if (visible.Length == 0) return new(State.Waiting, null, -1, default);
            // A late or simultaneous initial observation cannot establish the
            // first appearance time. Hold rather than guess the pattern.
            if (visible.Length != 1 || visible[0].CastAction != 0 ||
                Math.Abs(visible[0].Position.Y - 813) > 1 || Math.Abs(Math.Abs(visible[0].Position.X - 852) - 10) > 1)
                return Reject();
            started = true; firstSeen = now; mirrored = visible[0].Position.X > 852;
        }
        foreach (var node in nodes)
        {
            if (!node.Visible && node.CastAction == 0) continue;
            if (node.Id is 0 or 0xE0000000 || node.BaseId is not (0x4174 or 0x4175) ||
                !float.IsFinite(node.Position.X) || !float.IsFinite(node.Position.Y)) return Reject();
            var point = Canonical(node.Position);
            int slot = Array.FindIndex(Slots, p => Vector2.Distance(p, point) < 1);
            if (slot < 0 || (ids[slot] != 0 && ids[slot] != node.Id)) return Reject();
            ids[slot] = node.Id; shapes[slot] = node.BaseId == 0x4174 ? 'O' : 'X';
            if (node.CastAction != 0)
            {
                if (node.CastAction != (shapes[slot] == 'O' ? 36382u : 36383u) ||
                    !double.IsFinite(node.Remaining) || node.Remaining < 0) return Reject();
                castEnds[slot] ??= now + node.Remaining;
            }
        }
        if (ids[0] == 0 || ids[1] == 0) return new(State.Waiting, null, -1, default);
        pattern = (shapes[0], shapes[1]) switch
        {
            ('O', 'O') => "OOXXO", ('O', 'X') => "OXOOX",
            ('X', 'O') => "XOOXO", ('X', 'X') => "XXOOX", _ => null
        };
        if (pattern == null || Enumerable.Range(0, 5).Any(i => ids[i] != 0 && shapes[i] != pattern[i])) return Reject();

        var route = Points(pattern);
        if (route == null) return Reject();
        int stage = 0;
        while (stage < 5)
        {
            if (castEnds[stage] is not { } finish)
            {
                // The 56 captured first effects appeared 9.56–10.00s after
                // first visibility, then about 2.5s apart. A missed cast is
                // not an effect receipt and cannot authorize departure.
                if (now > firstSeen + 10.8 + stage * 2.5) return Reject();
                break;
            }
            // 295 matched receipts arrived 0.144–0.383s after cast finish.
            // Half a second retains the shelter beyond that observed range.
            double release = finish + .50;
            if (stage == 1)
            {
                foreach (var cone in cones.Where(c => c.Due >= finish && c.Due <= finish + 2))
                {
                    uint expected = pattern[0] == 'X' ? 36379u : 36381u;
                    if (cone.Action != expected) return Reject();
                    // Front-cone patterns need a head start toward the next
                    // donut. This waypoint stays outside the cone while it
                    // resolves; the final donut point remains cast-gated.
                    if (pattern[0] == 'X' && now >= release && now < cone.Due)
                    {
                        var transition = pattern == "XOOXO" ? new Vector2(845.28f, 818.82f) : new Vector2(850.66f, 816.02f);
                        return new(State.Ready, pattern, 6, World(transition));
                    }
                    release = Math.Max(release, cone.Due);
                }
            }
            if (now < release) break;
            stage++;
        }
        if (stage == 5)
        {
            // The late cone is observed after node five; its actual cast owns
            // release. The pattern predicts which side to stage on, not that
            // a missing cast has already resolved.
            var late = cones.Where(c => c.Due > castEnds[4].Value + .5).OrderBy(c => c.Due).FirstOrDefault();
            uint expected = pattern[4] == 'X' ? 36379u : 36381u;
            if (late.Action != 0 && late.Action != expected) return Reject();
            if (late.Action != 0 && now >= late.Due + .1) { complete = true; return new(State.Complete, pattern, 6, default); }
            if (late.Action == 0 && now > firstSeen + 27) return Reject();
        }
        return new(State.Ready, pattern, stage, World(route[stage]));
    }

    // If the final point is too far away, an axis-aligned step can enter the
    // same node's safe region first. Check that intermediate point and the
    // complete travel envelope against the observed hazards before using it.
    internal bool TryIntermediate(int stage, Vector2 player, Vector2 target, double now,
        double minimum, double maximum, AlexandriaGeometry.Hazard[] hazards, out Vector2 point)
    {
        point = default;
        if (stage is < 0 or >= 5 || pattern == null) return false;
        var origin = World(Slots[stage]);
        var kind = pattern[stage] == 'O' ? AlexandriaGeometry.Kind.Annulus : AlexandriaGeometry.Kind.Cross;
        var node = hazards.FirstOrDefault(h => h.Kind == kind && Vector2.Distance(h.Origin, origin) < 1);
        if (!node.Valid) return false;
        foreach (var candidate in new[] { new Vector2(player.X, target.Y), new Vector2(target.X, player.Y) }
            .OrderBy(candidate => Vector2.Distance(player, candidate)))
        {
            double distance = Vector2.Distance(player, candidate);
            if (distance <= .05 || AlexandriaGeometry.Distance(candidate, node) <= node.Margin + .05) continue;
            double end = now + .101 + distance / minimum;
            if (!AlexandriaInterferonGeometry.TravelSafe(player, candidate, now, end, .1, minimum, maximum, hazards)) continue;
            point = candidate;
            return true;
        }
        return false;
    }

    // Ordered patterns remove the generic forecast's simultaneous-discovery
    // ambiguity. The 56 captured waves put later cast finishes within 75ms
    // early and 226ms late of the first cast plus 2.5s per node.
    internal (double Earliest, double Latest)? NodeWindow(Vector2 origin)
    {
        var canonical = Canonical(origin);
        int slot = Array.FindIndex(Slots, p => Vector2.Distance(p, canonical) < 1);
        if (slot < 0) return null;
        if (castEnds[slot] is { } finish) return (finish, finish + .5);
        if (castEnds[0] is { } first)
            return (first + slot * 2.5 - .15, first + slot * 2.5 + .85);
        return null;
    }

    internal void Invalidate() => invalid = true;
    private Decision Reject() { Invalidate(); return new(State.Invalid, pattern, -1, default); }
    private Vector2 Canonical(Vector2 p) => mirrored ? new(1704 - p.X, p.Y) : p;
    private Vector2 World(Vector2 p) => mirrored ? new(1704 - p.X, p.Y) : p;

    internal static Vector2[] Points(string value) => value switch
    {
        // Canonical direction is west to east. These adjustments preserve
        // at least half a yalm from the observed node shapes and expanded cone.
        "OOXXO" => [new(842.22f, 815.45f), new(845.09f, 821.57f), new(848.4f, 819.37f), new(853.57f, 817.13f), new(858.96f, 814.53f), new(854, 823)],
        "OXOOX" => [new(844.88f, 814.8f), new(850.39f, 818.13f), new(853.3f, 819.86f), new(858.81f, 821.82f), new(857.481f, 817.472f), new(859.983f, 816.983f)],
        "XOOXO" => [new(847.471f, 817.990f), new(843.29f, 819.86f), new(849.08f, 821.27f), new(855.09f, 818.41f), new(861.4f, 815.29f), new(855.019f, 823.919f)],
        "XXOOX" => [new(846.5f, 817.5f), new(849.03f, 816.62f), new(852.8f, 819.7f), new(858.81f, 821.83f), new(854.49f, 819.486f), new(859.983f, 816.983f)],
        _ => null
    };
}

// Tracks one observed cone cast without letting a zero-time wrapper extend its
// deadline on every pulse. Ambiguous replacement or an overdue active cast is
// incomplete evidence, not a new guessed safe interval.
internal sealed class AlexandriaConeDeadline
{
    internal double? Due { get; private set; }
    internal double Until { get; private set; }
    internal uint Actor { get; private set; }
    internal uint Action { get; private set; }
    internal bool Incomplete => invalid || overdue;
    private double lastObservation;
    private bool observed, wasCasting, invalid, overdue;

    internal void Observe(double now, bool casting, uint actor = 0, uint action = 0, double remaining = 0)
    {
        if (!double.IsFinite(now) || now < 0 || (observed && now < lastObservation)) { invalid = true; return; }
        observed = true; lastObservation = now;
        if (!casting)
        {
            wasCasting = false;
            if (Due.HasValue && now >= Until) { Due = null; Until = 0; Actor = Action = 0; overdue = false; }
            return;
        }
        if (actor is 0 or 0xE0000000 || action is not (36379 or 36381) ||
            !double.IsFinite(remaining) || remaining < 0) { invalid = true; return; }
        bool same = Due.HasValue && Actor == actor && Action == action;
        if (Due.HasValue && !same && now < Until) { invalid = true; return; }
        if (!same || (!wasCasting && now >= Until))
        {
            double due = now + remaining + .5;
            // The route snapshot retains this cone through due + .4. Use
            // exactly the same boundary for the active-wrapper watchdog: a
            // cast still present after the snapshot ends must fail closed,
            // not disappear for another pulse before becoming incomplete.
            if (!double.IsFinite(due) || !double.IsFinite(due + .4)) { invalid = true; return; }
            Due = due;
            Until = due + .4;
            Actor = actor; Action = action;
            overdue = false;
        }
        else if (wasCasting && now >= Until)
        {
            // A continuously casting wrapper beyond the retained bound may be
            // stale or another cast. Neither possibility proves safety.
            overdue = true;
        }
        wasCasting = true;
    }

    internal void Reset()
    {
        Due = null; Until = 0; Actor = Action = 0;
        lastObservation = 0; observed = wasCasting = invalid = overdue = false;
    }
}

// Multiple helpers can overlap. Keep each cast's stable deadline independently
// so observing a new cone never silently erases an unresolved earlier one.
internal sealed class AlexandriaConeTimeline
{
    internal readonly record struct Observation(uint Actor, uint Action, double Remaining);
    private readonly Dictionary<(uint Actor, uint Action), AlexandriaConeDeadline> casts = [];
    private bool invalid, transientInvalid, observed;
    private double lastObservation;

    internal bool Incomplete => invalid || transientInvalid || casts.Values.Any(cast => cast.Incomplete);
    internal IEnumerable<AlexandriaConeDeadline> Active => casts.Values.Where(cast => cast.Due.HasValue);
    internal void MarkIncomplete() => invalid = true;

    internal void Observe(double now, IReadOnlyList<Observation> observations)
    {
        if (!double.IsFinite(now) || now < 0 || (observed && now < lastObservation) || observations == null ||
            observations.Any(item => item.Actor is 0 or 0xE0000000 || item.Action is not (36379 or 36381)) ||
            observations.Select(item => item.Actor).Distinct().Count() != observations.Count)
        { invalid = true; return; }
        observed = true; lastObservation = now; transientInvalid = false;
        var seen = new HashSet<(uint Actor, uint Action)>();
        foreach (var item in observations)
        {
            var key = (item.Actor, item.Action); seen.Add(key);
            bool known = casts.TryGetValue(key, out var deadline);
            if (!double.IsFinite(item.Remaining) || item.Remaining < 0)
            {
                // A known cast retains its earlier deadline while its wrapper
                // is temporarily unreadable. An unseen cast has no trustworthy
                // deadline and cannot be made complete by later disappearance.
                if (known) transientInvalid = true; else invalid = true;
                continue;
            }
            if (!known) casts.Add(key, deadline = new AlexandriaConeDeadline());
            deadline.Observe(now, true, item.Actor, item.Action, item.Remaining);
        }
        foreach (var key in casts.Keys.ToArray())
        {
            if (seen.Contains(key)) continue;
            var deadline = casts[key]; deadline.Observe(now, false);
            // Preserve invalid evidence until the encounter reset, even if its
            // actor vanishes; an absent wrapper cannot repair an ambiguity.
            if (!deadline.Due.HasValue && !deadline.Incomplete) casts.Remove(key);
        }
    }

    internal void Reset()
    {
        casts.Clear(); invalid = transientInvalid = observed = false; lastObservation = 0;
    }
}

// Detached encounter geometry. Activation windows, actor identity, path timing,
// and movement ownership are supplied separately; this does not move the bot.
internal static class AlexandriaInterferonGeometry
{
    internal enum Shape { Cross, Donut, Cone }
    internal readonly record struct Hazard(Shape Shape, Vector2 Origin, Vector2 Direction, float ConeCosine,
        double Earliest, double Latest);

    // The caller's due time is reported cast finish plus 0.5 seconds. Keep
    // the earliest modeled impact at cast finish because observed receipts
    // can arrive before the old finish-plus-0.3-second lower bound.
    internal static (double Earliest, double Latest) ConeWindow(double due) => (due - .5, due + .4);

    internal static bool Safe(Vector2 point, double time, Hazard[] hazards)
    {
        if (point.X <= 832.5f || point.X >= 871.5f || point.Y <= 808.5f || point.Y >= 837.5f)
            return false;
        foreach (var hazard in hazards)
        {
            if (time < hazard.Earliest || time > hazard.Latest) continue;
            var delta = point - hazard.Origin;
            switch (hazard.Shape)
            {
                // Preserve a full yalm against the captured arrival shortfall
                // and server position sampling around the damage effect.
                case Shape.Cross when Math.Abs(delta.X) <= 4f || Math.Abs(delta.Y) <= 4f:
                case Shape.Donut when delta.LengthSquared() >= 3f * 3f:
                    return false;
                case Shape.Cone when !ConeSafe(delta, hazard):
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Certifies the complete travel envelope against the same conservative
    /// shapes, rather than sampling a few positions between waypoints. The caller
    /// must provide independently measured speed, delay and effect windows; this
    /// check cannot validate those estimates or the input controller's trajectory.
    /// </summary>
    internal static bool TravelSafe(Vector2 from, Vector2 to, double start, double end,
        double delay, double minimumSpeed, double maximumSpeed, Hazard[] hazards)
    {
        if (!TryConvert(hazards, out var shapes)) return false;
        return TravelSafe(from, to, start, end, delay, minimumSpeed, maximumSpeed, shapes);
    }

    // Conversion occurs once per immutable worker snapshot. The runtime can
    // also use the convenience overload when checking a newly changed snapshot.
    internal static bool TryConvert(Hazard[] hazards, out AlexandriaGeometry.Hazard[] shapes)
    {
        shapes = Array.Empty<AlexandriaGeometry.Hazard>();
        if (hazards == null) return false;
        var result = new AlexandriaGeometry.Hazard[hazards.Length];
        for (int i = 0; i < hazards.Length; ++i)
        {
            var h = hazards[i];
            if (!AlexandriaGeometry.Finite(h.Origin) || h.Origin.X < 832.5f || h.Origin.X > 871.5f ||
                h.Origin.Y < 808.5f || h.Origin.Y > 837.5f) return false;
            // A 200-yalm outer extent covers the entire bounded arena even
            // from a corner. It preserves the legacy infinite-arm/ring model.
            result[i] = h.Shape switch
            {
                Shape.Cross => new(AlexandriaGeometry.Kind.Cross, h.Origin, Vector2.UnitX, 200, 0, 4, 0, h.Earliest, h.Latest),
                Shape.Donut => new(AlexandriaGeometry.Kind.Annulus, h.Origin, Vector2.UnitX, 3, 200, 0, 0, h.Earliest, h.Latest),
                Shape.Cone when float.IsFinite(h.ConeCosine) && Math.Abs(h.ConeCosine) <= 1 =>
                    new(AlexandriaGeometry.Kind.Sector, h.Origin, h.Direction, 200, Math.Acos(h.ConeCosine), 0, 1, h.Earliest, h.Latest),
                _ => default
            };
            if (!result[i].Valid) return false;
        }
        shapes = result;
        return true;
    }

    internal static bool TravelSafe(Vector2 from, Vector2 to, double start, double end,
        double delay, double minimumSpeed, double maximumSpeed, AlexandriaGeometry.Hazard[] shapes)
    {
        if (shapes == null || !AlexandriaGeometry.Finite(from) || !AlexandriaGeometry.Finite(to) ||
            from.X <= 832.5f || from.X >= 871.5f || from.Y <= 808.5f || from.Y >= 837.5f ||
            to.X <= 832.5f || to.X >= 871.5f || to.Y <= 808.5f || to.Y >= 837.5f) return false;
        return AlexandriaGeometry.TravelSafe(from, to, new(start, end, delay, minimumSpeed, maximumSpeed), shapes);
    }

    /// <summary>
    /// Certifies the current waypoint from the observed position. Once inside
    /// the owner's arrival tolerance it holds the actual position, so a nominal
    /// restart delay cannot invalidate a nearly completed zero-distance move.
    /// The next segment must start at the returned actual or planned position.
    /// </summary>
    internal static bool CurrentWaypointSafe(Vector2 player, Vector2 waypoint, double start, double end,
        double delay, double minimumSpeed, double maximumSpeed, double arrivalTolerance,
        AlexandriaGeometry.Hazard[] shapes, out Vector2 nextStart)
    {
        nextStart = default;
        if (!double.IsFinite(arrivalTolerance) || arrivalTolerance < 0 ||
            !AlexandriaGeometry.Finite(player) || !AlexandriaGeometry.Finite(waypoint)) return false;
        bool arrived = Vector2.Distance(player, waypoint) <= arrivalTolerance;
        if (arrived && TravelSafe(player, player, start, end, 0, minimumSpeed, maximumSpeed, shapes))
        { nextStart = player; return true; }
        // Within the nominal arrival tolerance, holding the observed position
        // can still become unsafe even though the planned waypoint remains
        // viable. Attempt the remaining short move only if its entire envelope
        // is certified; callers must actually issue it rather than hold.
        nextStart = waypoint;
        return TravelSafe(player, waypoint, start, end, delay, minimumSpeed, maximumSpeed, shapes);
    }

    private static bool ConeSafe(Vector2 delta, Hazard hazard)
    {
        if (delta.LengthSquared() < 1) return false;
        double angle = Math.Acos(Math.Clamp(Vector2.Dot(Vector2.Normalize(delta), hazard.Direction), -1, 1));
        double edge = Math.Acos(hazard.ConeCosine);
        // Angular clearance alone collapses near the caster. Require one yalm
        // beyond the cone edge, including at the short first-boss staging points.
        return angle > edge && Math.Sqrt(delta.LengthSquared()) * Math.Sin(Math.Min(Math.PI / 2, angle - edge)) >= 1;
    }

    // A 120-degree unsafe cone is a convex wedge. Clip the segment against
    // its two half-planes; no sampling gap may skip a short intersection.
    // The safe region outside a 240-degree cone is itself a convex wedge,
    // so safe endpoints already establish segment safety in this arena.
    internal static bool CrossesCone(Vector2 from, Vector2 to, Hazard hazard)
    {
        if (hazard.ConeCosine <= 0) return false;
        if (hazard.ConeCosine > 1 || Math.Abs(hazard.Direction.LengthSquared() - 1) > .01f) return true;
        double tangent = Math.Sqrt(1 - hazard.ConeCosine * hazard.ConeCosine) / hazard.ConeCosine;
        var p = from - hazard.Origin; var q = to - hazard.Origin;
        double Along(Vector2 v) => Vector2.Dot(v, hazard.Direction);
        double Side(Vector2 v) => hazard.Direction.X * v.Y - hazard.Direction.Y * v.X;
        double lo = 0, hi = 1;
        bool Clip(double start, double finish)
        {
            if (start < 0 && finish < 0) return false;
            if (start >= 0 && finish >= 0) return true;
            double crossing = -start / (finish - start);
            if (start < 0) lo = Math.Max(lo, crossing); else hi = Math.Min(hi, crossing);
            return lo <= hi;
        }
        double clearance = Math.Sqrt(1 + tangent * tangent);
        return Clip(Along(p) * tangent + Side(p) + clearance, Along(q) * tangent + Side(q) + clearance) &&
            Clip(Along(p) * tangent - Side(p) + clearance, Along(q) * tangent - Side(q) + clearance);
    }
}

// A scalar snapshot of the ordered first-boss actors. Pulse discovery provides
// advance warning for their 0.7s casts; equal-pulse discoveries remain unordered.
internal sealed class AlexandriaInterferonSequence
{
    internal static (bool Interferon, bool Cone) SelectEffects(double? interferon, double? cone) =>
        (interferon.HasValue && (!cone.HasValue || interferon.Value <= cone.Value + .5),
         cone.HasValue && (!interferon.HasValue || cone.Value <= interferon.Value + .5));

    internal readonly record struct Observation(uint Id, bool Visible, bool Casting, double RemainingSeconds, uint Action = 0, bool ExplicitlyIdle = false);
    private sealed class Entry(uint id) { internal uint Id = id, Action; internal bool SeenCast, Resolved, MissingCast, InvalidTiming; internal double Finish, CastObserved, IdleSince = double.NaN; }
    private sealed class Group(double due) { internal double Due = due; internal readonly List<Entry> Entries = []; }
    private readonly List<Group> groups = [];
    private readonly Dictionary<uint, Entry> entries = [];
    private double firstSeen, lastVisible, lastObservation;
    private bool lateDiscovery, overflow, invalidData, hasObservation;

    // An unanchored, overflowing or missed-cast wave cannot safely be treated
    // as empty. Keep the owner in an explicit incomplete state until evidence
    // resolves the missing cast or the wave is reset after disappearance.
    internal bool UnanchoredUnresolved => invalidData || entries.Values.Any(entry => !entry.Resolved &&
        (lateDiscovery || overflow || entry.MissingCast));

    internal void Reset()
    {
        groups.Clear(); entries.Clear(); firstSeen = lastVisible = lastObservation = 0;
        lateDiscovery = overflow = invalidData = hasObservation = false;
    }

    // A coherent action-effect receipt proves that this exact cast has
    // resolved before subsequent input. The caller must own a gap-free stream
    // for the encounter and supply the lower bound of the batch observation;
    // a receipt that might have been buffered before cast discovery cannot be
    // associated safely, even if its source ID and action happen to match.
    // This remains detached until the native observer is validated for use by
    // gameplay; no timer or missing actor is treated as an equivalent receipt.
    internal bool ConfirmEffect(uint id, uint action, double observedAfter, double observedBy, bool streamComplete)
    {
        if (!streamComplete || invalidData || !double.IsFinite(observedAfter) || !double.IsFinite(observedBy) ||
            observedAfter < 0 || observedBy < observedAfter || observedBy > lastObservation ||
            !entries.TryGetValue(id, out var entry) || entry.Resolved || !entry.SeenCast || entry.InvalidTiming ||
            entry.Action is not (36382 or 36383) || action != entry.Action || observedAfter <= entry.CastObserved)
            return false;
        entry.Resolved = true;
        entry.MissingCast = false;
        return true;
    }

    internal void Observe(double now, IReadOnlyList<Observation> observations)
    {
        // Invalid or reordered samples cannot prove an empty wave. Retain the
        // incomplete state until an explicit lifecycle reset rather than turn
        // corrupted time or actor identity into a seemingly safe forecast.
        if (!double.IsFinite(now) || now < 0 || observations == null || (hasObservation && now < lastObservation) ||
            observations.Any(actor => actor.Id is 0 or 0xE0000000) || observations.Select(actor => actor.Id).Distinct().Count() != observations.Count)
        { invalidData = true; return; }
        hasObservation = true; lastObservation = now;
        if (observations.Any(actor => actor.Visible)) lastVisible = now;
        else if (entries.Count != 0 && now - lastVisible > 2)
        {
            // A vanished wrapper is not proof that an unseen attack fired.
            // Retire only previously observed casts after their conservative
            // window; an unobserved attack remains explicitly incomplete.
            foreach (var entry in entries.Values)
            {
                if (!entry.Resolved && entry.SeenCast && !entry.MissingCast && now >= entry.Finish + .30)
                    entry.Resolved = true;
                else if (!entry.Resolved && !entry.SeenCast)
                    entry.MissingCast = true;
            }
            if (entries.Values.All(entry => entry.Resolved)) { Reset(); return; }
        }
        var newActors = observations.Where(actor => actor.Visible && !entries.ContainsKey(actor.Id)).ToArray();
        if (newActors.Length != 0)
        {
            if (entries.Count == 0)
            {
                firstSeen = now;
                // An already-casting first observation cannot anchor a wave.
                lateDiscovery = newActors.Length > 1 || newActors.Any(actor => actor.Casting);
            }
            var group = new Group(firstSeen + 9.9 + entries.Count * 2.5);
            foreach (var actor in newActors)
            {
                var entry = new Entry(actor.Id);
                entries.Add(actor.Id, entry);
                group.Entries.Add(entry);
            }
            groups.Add(group);
            if (entries.Count > 5) overflow = true;
        }
        // An absent or invisible wrapper breaks the continuous-idle evidence;
        // a later reappearance must begin a fresh confirmation interval.
        var visibleIds = observations.Where(actor => actor.Visible).Select(actor => actor.Id).ToHashSet();
        foreach (var entry in entries.Values)
            if (!visibleIds.Contains(entry.Id)) entry.IdleSince = double.NaN;
        foreach (var actor in observations)
        {
            if (!entries.TryGetValue(actor.Id, out var entry) || entry.Resolved) continue;
            if (actor.Casting)
            {
                entry.IdleSince = double.NaN;
                if (actor.RemainingSeconds < 0 || !double.IsFinite(actor.RemainingSeconds))
                { entry.MissingCast = entry.InvalidTiming = true; continue; }
                // A helper identity can be reused or its wrapper can be read
                // during a transition. A different action must not inherit
                // the earlier cast's deadline or release its hazard. Restore
                // this entry only if its originally observed action returns.
                if (entry.SeenCast && entry.Action != 0 && actor.Action != entry.Action)
                { entry.MissingCast = entry.InvalidTiming = true; continue; }
                // Some wrappers retain the cast with a zero remaining time
                // after its predicted finish. Recomputing now + remaining on
                // every pulse drifts the effect later and can strand the next
                // shelter. Each discovered helper owns one cast in this wave;
                // retain its first valid finish estimate instead.
                bool firstCast = !entry.SeenCast;
                entry.SeenCast = true;
                entry.MissingCast = entry.InvalidTiming = false;
                // Captured vulnerability effects arrived 0.44-0.80s after the reported
                // cast finish. Model the effect, then retain a 0.30s observation margin.
                if (firstCast)
                {
                    entry.Finish = now + actor.RemainingSeconds + .55;
                    entry.CastObserved = now;
                    entry.Action = actor.Action;
                }
            }
            else if (entry.SeenCast && !entry.InvalidTiming && actor.Visible && actor.ExplicitlyIdle &&
                entry.Action is 36382 or 36383 && now >= entry.Finish - .55)
            {
                // In five Global-client captures, 163 matched node effects and
                // explicit cast-end observations appeared in the same pulse.
                // Wait 100ms of continuously visible idle state to reject a
                // transient blank wrapper before releasing the shelter. The
                // observed cast and its first finish estimate anchor this to
                // the current helper; missing/invalid wrappers cannot resolve.
                // This empirical transition still needs live and regional
                // validation; it is not proof of the server snapshot time.
                if (double.IsNaN(entry.IdleSince)) entry.IdleSince = now;
                if (now - entry.IdleSince >= .1)
                { entry.Resolved = true; entry.MissingCast = false; }
            }
            else if (entry.SeenCast && !entry.InvalidTiming && actor.Visible && actor.ExplicitlyIdle && now >= entry.Finish + .30)
            {
                // Only an explicitly idle, visible wrapper confirms a cast
                // ended. An invalid cast-info wrapper or another action is
                // not evidence that the prior node effect has resolved.
                entry.Resolved = true;
                entry.MissingCast = false;
            }
            else entry.IdleSince = double.NaN;
        }
        // A previously seen cast can also disappear from the object table.
        // Once its modeled window ends without a valid non-casting sample,
        // do not let forecast filtering turn it into an empty safe region.
        foreach (var entry in entries.Values)
            if (!entry.Resolved && entry.SeenCast && !entry.MissingCast && now > entry.Finish + .30)
                entry.MissingCast = true;
        if (lateDiscovery || overflow) return;
        foreach (var group in groups)
        {
            // A predicted time passing without a cast is not proof of an
            // effect. Match the forecast consumer's 0.30s retirement bound so
            // there is no interval where a missed cast becomes invisible.
            double groupEnd = group.Due + (group.Entries.Count - 1) * 2.5 + .30;
            if (now <= groupEnd) continue;
            foreach (var entry in group.Entries)
                if (!entry.SeenCast) entry.MissingCast = true;
        }
    }

    internal bool IsActive(uint id, bool casting)
    {
        if (UnanchoredUnresolved) return casting;
        var group = groups.FirstOrDefault(candidate => candidate.Entries.Any(entry => !entry.Resolved));
        if (group == null) return casting;
        var castingEntries = group.Entries.Where(entry => !entry.Resolved && entry.SeenCast).ToArray();
        // Once a member of an unordered group starts casting, it is the
        // current hazard; otherwise all still-possible members remain active.
        return castingEntries.Length != 0
            ? castingEntries.Any(entry => entry.Id == id)
            : group.Entries.Any(entry => !entry.Resolved && entry.Id == id);
    }

    internal double? NextDue(double now)
    {
        if (UnanchoredUnresolved) return null;
        var group = groups.FirstOrDefault(candidate => candidate.Entries.Any(entry => !entry.Resolved));
        if (group == null) return null;
        var casting = group.Entries.Where(entry => !entry.Resolved && entry.SeenCast).ToArray();
        // A known earlier member of an unordered group has consumed its slot;
        // do not let its stale deadline suppress an intervening boss cone.
        return casting.Length == 0
            ? group.Due + group.Entries.Count(entry => entry.Resolved) * 2.5
            : casting.Min(entry => entry.Finish);
    }

    // Simultaneous discovery does not reveal internal order. Give every
    // unresolved actor the full remaining slot range, so a planner considers
    // every possible order rather than treating an ambiguous wave as absent.
    // These slot estimates remain provisional until action receipts calibrate
    // them; an unanchored first observation cannot provide any forecast.
    internal (uint Id, double Earliest, double Latest)[] Forecast()
    {
        if (UnanchoredUnresolved) return [];
        return groups.SelectMany(g =>
        {
            double first = g.Due + g.Entries.Count(e => e.Resolved) * 2.5;
            double last = g.Due + (g.Entries.Count - 1) * 2.5;
            return g.Entries.Where(e => !e.Resolved)
                .Select(e => (e.Id, e.SeenCast ? e.Finish : first, e.SeenCast ? e.Finish : last));
        }).ToArray();
    }
}

/// <summary>
/// Keeps detached first-boss helper positions only while a node is visible or
/// still belongs to the active forecast. A vanished wrapper must not silently
/// remove an unresolved hazard; invalid fresh data invalidates the old value.
/// </summary>
internal sealed class AlexandriaNodeSnapshotCache
{
    internal readonly record struct Node(uint Id, uint BaseId, Vector2 Origin);
    private readonly Dictionary<uint, Node> nodes = new();

    internal void Refresh(Node[] visible, uint[] forecastIds)
    {
        foreach (var node in visible)
        {
            if (node.Id != 0 && (node.BaseId is 0x4174 or 0x4175) && AlexandriaGeometry.Finite(node.Origin) &&
                node.Origin.X >= 832.5f && node.Origin.X <= 871.5f && node.Origin.Y >= 808.5f && node.Origin.Y <= 837.5f)
                nodes[node.Id] = node;
            else nodes.Remove(node.Id);
        }
        var retained = forecastIds.Concat(visible.Select(node => node.Id)).ToHashSet();
        foreach (var id in nodes.Keys.Where(id => !retained.Contains(id)).ToArray()) nodes.Remove(id);
    }

    internal bool TryGet(uint id, out Node node) => nodes.TryGetValue(id, out node);
    internal void Clear() => nodes.Clear();
}
