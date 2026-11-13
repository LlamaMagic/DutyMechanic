using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class QitanaRavel
{
    private readonly HashSet<Cast> rejectedStages = new();
    private bool fireOwned;
    private string poisonObjectSnapshot;

    private void ObservePoisonObjects()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || boss != 3) return;
        // Compare real puddles with cast forecasts when pull preparation fails.
        // Log changes after arbitration; diagnostics must not gate movement.
        var snapshot = string.Join(";", PoisonObjects().OrderBy(p => p.ObjectId)
            .Select(p => $"{p.ObjectId:X8}@{p.Location}"));
        if (snapshot == poisonObjectSnapshot) return;
        poisonObjectSnapshot = snapshot;
        Logger.Information("[Qitana] Poison objects=[{0}] player={1}", snapshot, Core.Me.Location);
    }

    private void UpdateEros(BattleCharacter[] actors)
    {
        bool hadFire = fireOwned;
        fireOwned = false;
        var breath = Pending().LastOrDefault(c => c.Action == Actions.HeavingBreath);
        if (breath != null)
            foreach (var poison in Pending().Where(c => c.Action == Actions.ViperPoison)) poison.Resolved = true;
        if (breath?.Stage is Vector3 held && DateTime.UtcNow >= breath.Finish &&
            (MovementManager.ForceMoved || Core.Me.Location.Distance2D(held) > 8))
        {
            // Consume the push on forced input or substantial displacement so
            // its tail cannot request a return to the starting point. Ordinary
            // casting locks also set GenerallyOutOfControl, so use ForceMoved.
            breath.Resolved = true;
            ReleasePosition();
            return;
        }
        if (UpdateFireConfession(actors)) return;
        if (hadFire && positionKind == "fire confession") ReleasePosition();
        if (Pending().Any(c => c.Action == Actions.ConfessionCenter))
        {
            // An unvalidated fire sequence yields to native escape. Do not
            // let the generic spread rule choose a conflicting cone-tip goal.
            ReleasePosition();
            return;
        }
        // Native escape preempts positive positioning without restarting its lifetime.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            ReleasePosition();
            return;
        }
        if (MovementManager.GenerallyOutOfControl)
        {
            // The owner yields input while casting. Keep the positioning lease
            // to avoid stop/start churn; revalidate when the lock clears.
            if (positionKind != null && destination.HasValue) SetPosition(positionKind, destination.Value);
            return;
        }
        if (breath != null)
        {
            var stage = KnockbackStage(breath);
            if (stage.HasValue)
            {
                breath.Stage = stage;
                SetPosition("knockback", stage.Value);
                return;
            }
            ReportStageFailure(breath);
            ReleasePosition();
            return;
        }

        var stack = Pending().LastOrDefault(c => c.Action == Actions.ConfessionStack);
        if (stack != null)
        {
            var stage = StackStage(stack, actors);
            if (stage.HasValue)
            {
                SetPosition("lightning stack", stage.Value);
                return;
            }
            ReportStageFailure(stack);
            ReleasePosition();
            return;
        }
        var bait = Pending().LastOrDefault(c => c.Action == Actions.ViperPoisonBait && c.Target == Core.Me.ObjectId);
        var spread = Pending().LastOrDefault(c => c.Action == Actions.ConfessionSpread && c.Target == Core.Me.ObjectId);
        var marker = bait ?? spread;
        if (marker != null)
        {
            string kind = bait != null ? "poison bait" : "fire spread";
            float spacing = bait != null ? 7.5f : 6.5f;
            var allies = PartyActors(actors).Where(a => !a.IsMe).Select(a => a.Location).ToArray();
            bool Valid(Vector3 p) => InsideEros(p) && Safe(p) &&
                allies.All(a => a.Distance2D(p) >= spacing) && SegmentSafe(Core.Me.Location, p);
            // Keep a valid goal rather than chasing a slightly nearer candidate.
            Vector3? stage = positionKind == kind && destination.HasValue && Valid(destination.Value) ? destination :
                EdgeCandidates().Append(Core.Me.Location).Where(Valid)
                    .OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
            if (stage.HasValue)
            {
                SetPosition(kind, stage.Value);
                return;
            }
            ReportStageFailure(marker);
        }
        if (marker == null && PrepareForInhale(actors)) return;
        ReleasePosition();
    }

    private bool UpdateFireConfession(BattleCharacter[] actors)
    {
        var cone = Pending().LastOrDefault(c => c.Action == Actions.ConfessionCenter);
        if (cone == null || movement == null || MovementManager.ForceMoved) return false;
        var spreads = Pending().Where(c => c.Action == Actions.ConfessionSpread).ToArray();
        if (spreads.Length == 0 || spreads.Any(c => c.Target is 0 or 0xE0000000 ||
            c.Target != Core.Me.ObjectId && !OtherTargetKnown(c))) return false;
        // Choose the carrier's flank before native cone escape commits elsewhere.
        if (!spreads.Any(c => c.Target == Core.Me.ObjectId)) return false;
        var ownFinish = spreads.Where(c => c.Target == Core.Me.ObjectId).Min(c => c.Finish);
        // This override owns only fire's cone/spread overlap.
        if (Pending().Any(c => c.Action is Actions.HeavingBreath or Actions.ConfessionStack ||
            c.Action == Actions.HoundOutOfHeaven && c.Target == Core.Me.ObjectId)) return false;
        var poison = PoisonCenters();
        if (!PoisonSafe(Core.Me.Location, poison)) return false;
        cone.Until = spreads.Select(c => c.Until).Append(cone.Until).Max();
        var party = PartyActors(actors).Where(a => !a.IsMe).Select(a => a.Location).ToArray();
        var marked = spreads.Where(c => c.Target != Core.Me.ObjectId)
            .Select(c => new { c.Finish, Point = Position(c) }).ToArray();
        var forward = new Vector3((float)Math.Sin(cone.Heading), 0, (float)Math.Cos(cone.Heading));
        var side = new Vector3(forward.Z, 0, -forward.X);
        bool OutsideCone(Vector3 p)
        {
            var delta = p - cone.Origin;
            float along = delta.X * forward.X + delta.Z * forward.Z;
            float across = Math.Abs(delta.X * side.X + delta.Z * side.Z);
            // The whole platform is within the cone's 60-yalm range. Match the
            // native fence with 1.5 yalms of signed distance from either edge.
            return across * .76604444f - along * .64278761f > 1.5f;
        }
        bool GoalSafe(Vector3 p) => InsideEros(p) && PoisonSafe(p, poison) && OutsideCone(p) &&
            party.All(a => a.Distance2D(p) >= 6.5f);
        bool TimedSegment(Vector3 to)
        {
            var from = Core.Me.Location;
            float length = from.Distance2D(to);
            int count = Math.Max(1, (int)Math.Ceiling(length / .4f));
            for (int i = 0; i <= count; i++)
            {
                float t = (float)i / count;
                var p = new Vector3(from.X + (to.X - from.X) * t, -77, from.Z + (to.Z - from.Z) * t);
                if (!InsideEros(p) || !PoisonSafe(p, poison)) return false;
                // Cross forecasts only before their individual deadlines.
                // Plan at 5 yalms/sec with 0.3 seconds input reserve; verify actual arrival live.
                var arrival = DateTime.UtcNow.AddSeconds(length * t / 5 + .3);
                if (arrival >= cone.Finish && !OutsideCone(p)) return false;
                if (marked.Any(c => arrival >= c.Finish && p.Distance2D(c.Point) < 6.5f)) return false;
                if (arrival >= ownFinish && party.Any(a => p.Distance2D(a) < 6.5f)) return false;
            }
            return true;
        }
        bool Valid(Vector3 p) => GoalSafe(p) && TimedSegment(p);
        Vector3? goal = cone.Stage.HasValue && Valid(cone.Stage.Value) ? cone.Stage : null;
        if (!goal.HasValue)
        {
            // Both captured mirrors put Minfilia in the western deep flank.
            // Prefer east, then validate actual party spacing, poison and time.
            // The 14-yalm slot keeps the western fallback clear of that pocket.
            float east = side.X >= 0 ? 1 : -1;
            var candidates = from flank in new[] { east, -east }
                             from distance in new[] { 10f, 12f, 14f, 8f }
                             from offset in new[] { 13.5f, 14f, 11.5f }
                             select cone.Origin + forward * distance + side * (flank * offset);
            goal = candidates.Where(Valid).Select(p => (Vector3?)p).FirstOrDefault();
        }
        if (!goal.HasValue)
        {
            ReportStageFailure(cone);
            return false;
        }
        cone.Stage = goal;
        fireOwned = true;
        SetPosition("fire confession", goal.Value);
        // Submit the combined escape through the existing input owner.
        movement.RequestHazardExit(goal.Value, .35f);
        moving = !MovementManager.GenerallyOutOfControl && Core.Me.Location.Distance2D(goal.Value) > .35f;
        return true;
    }

    private bool PrepareForInhale(BattleCharacter[] actors)
    {
        var poison = Pending().LastOrDefault(c => c.Action == Actions.ViperPoison);
        if (poison == null) return false;
        var puddles = PoisonObjects().ToArray();
        if (puddles.Length == 0 && !Pending().Any(c => c.Action == Actions.ViperPoisonGround)) return false;
        var poisonCenters = PoisonCenters();
        var inhale = Pending().LastOrDefault(c => c.Action == Actions.Inhale);
        var caster = actors.FirstOrDefault(a => a.BaseId == Actors.Eros && a.IsAlive);
        var origin = inhale?.Origin ?? caster?.Location;
        bool AtWall() => origin.HasValue && (origin.Value.Z < -555.5f || origin.Value.Z > -520.5f);
        Vector3 Mouth() => new(origin.Value.X, -77,
            origin.Value.Z + (origin.Value.Z < ErosCenter.Z ? 1.5f : -1.5f));
        // A short clear approach can exist even when every lane midpoint is
        // blocked. Try the actual mouth before searching for a detour.
        if (AtWall() && Safe(Mouth()) && SegmentSafe(Core.Me.Location, Mouth()))
        {
            SetPosition("pull preparation", Mouth());
            return true;
        }

        // Inhale pulls straight to the boss, so clear that corridor before the
        // pull. Test the observed center, quarter-width and wall-side lanes
        // against both forecast and actual poison.
        bool ClearLane(Vector3 p)
        {
            for (float z = -556.5f; z <= -519.5f; z += .25f)
                if (!PoisonSafe(new Vector3(p.X, -77, z), poisonCenters)) return false;
            return true;
        }
        if (!poison.Stage.HasValue || !ClearLane(poison.Stage.Value))
        {
            // Include lane ends/current row so a blocked midpoint diagonal
            // cannot hide an approach. Quarter columns 9.5/24.5 clear the
            // captured center-and-corners layout, including arrival tolerance.
            poison.Stage = (from x in new[] { 3.5f, 9.5f, 17, 24.5f, 30.5f }
                            from z in new[] { -556.5f, -538, -519.5f, Core.Me.Z }
                            select new Vector3(x, -77, z))
                .Where(p => ClearLane(p) && Safe(p) && SegmentSafe(Core.Me.Location, p))
                .OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        }
        if (!poison.Stage.HasValue)
        {
            ReportStageFailure(poison);
            return false;
        }

        Vector3 stage = poison.Stage.Value;
        // Use the actual jump's wall: take the clear lane, then approach the
        // mouth along its edge. Validate both legs before submitting either.
        if (AtWall())
        {
            var mouth = Mouth();
            var corner = new Vector3(stage.X, -77, mouth.Z);
            if (Safe(mouth) && SegmentSafe(Core.Me.Location, mouth)) stage = mouth;
            else if (Safe(corner) && SegmentSafe(Core.Me.Location, corner) && SegmentSafe(corner, mouth)) stage = corner;
            else
            {
                ReportStageFailure(poison);
                return false;
            }
        }
        if (!Safe(stage) || !SegmentSafe(Core.Me.Location, stage)) return false;
        SetPosition("pull preparation", stage);
        return true;
    }

    private static BattleCharacter[] PartyActors(BattleCharacter[] actors)
    {
        var ids = PartyManager.VisibleMembers.Select(p => p.ObjectId).ToHashSet();
        return actors.Where(a => a.IsAlive && a.IsVisible && ids.Contains(a.ObjectId)).ToArray();
    }

    private void ReportStageFailure(Cast cast)
    {
        if (!rejectedStages.Add(cast)) return;
        Logger.Information("[Qitana] No safe stage action={0} player={1} remainingMs={2:F0} poisonObjects={3} native={4}",
            cast.Action, Core.Me.Location, (cast.Finish - DateTime.UtcNow).TotalMilliseconds,
            PoisonObjects().Count(), AvoidanceManager.IsRunningOutOfAvoid);
        if (LoggingHelpers.MechanicDiagnosticsEnabled && cast.Action == Actions.ViperPoison)
            Logger.Information("[Qitana] Rejected pull forecasts=[{0}] currentSafe={1}",
                string.Join(";", Pending().Where(c => c.Action is Actions.ViperPoisonGround or Actions.ViperPoisonBait)
                    .Select(c => $"{c.Action}/{c.Caster:X8}@{Position(c)}")), Safe(Core.Me.Location));
    }

    private Vector3? StackStage(Cast stack, BattleCharacter[] actors)
    {
        bool self = stack.Target == Core.Me.ObjectId;
        var marked = actors.FirstOrDefault(a => a.ObjectId == stack.Target);
        if (!self && marked == null) return null;
        var participants = PartyActors(actors).Where(a => !a.IsMe).Select(a => a.Location).ToArray();
        if (self && participants.Length == 0) return null;

        // Side-head headings are unreliable. Keep the six-yalm stack in a
        // conservative 15-degree central corridor rather than guessing side cones.
        bool Corridor(Vector3 p)
        {
            float dx = p.X - stack.Origin.X, dz = p.Z - stack.Origin.Z;
            float forward = dx * (float)Math.Sin(stack.Heading) + dz * (float)Math.Cos(stack.Heading);
            float side = dx * (float)Math.Cos(stack.Heading) - dz * (float)Math.Sin(stack.Heading);
            return forward > 1 && Math.Abs(side) <= forward * .2679492f;
        }
        // Reserve one yalm of the stack radius for arrival and party movement.
        // A self marker still needs living participants inside its circle.
        int Coverage(Vector3 p) => self ? participants.Count(a => a.Distance2D(p) <= 5) :
            p.Distance2D(marked.Location) <= 5 ? 1 : 0;
        bool Valid(Vector3 p) => InsideEros(p) && Corridor(p) && Safe(p) &&
            Coverage(p) > 0 && SegmentSafe(Core.Me.Location, p);
        var candidates = new List<Vector3> { Core.Me.Location, ErosCenter };
        var centers = self ? participants : new[] { marked.Location };
        foreach (var center in centers)
        {
            candidates.Add(center);
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new Vector3(center.X + 4.5f * (float)Math.Sin(angle), -77,
                    center.Z + 4.5f * (float)Math.Cos(angle)));
            }
        }
        if (self && participants.Length > 0)
            candidates.Add(new Vector3(participants.Average(p => p.X), -77, participants.Average(p => p.Z)));
        var best = candidates.Where(Valid).OrderByDescending(Coverage)
            .ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        if (positionKind == "lightning stack" && destination.HasValue && Valid(destination.Value) &&
            (!best.HasValue || Coverage(destination.Value) >= Coverage(best.Value))) return destination;
        if (best.HasValue && (positionKind != "lightning stack" || !destination.HasValue ||
            destination.Value.Distance2D(best.Value) > .5f))
            Logger.Information("[Qitana] Stack target={0:X8} self={1} coverage={2}/{3} destination={4}",
                stack.Target, self, Coverage(best.Value), self ? participants.Length : 1, best.Value);
        return best;
    }

    private Vector3? KnockbackStage(Cast breath)
    {
        var poisonCenters = PoisonCenters();
        var forward = new Vector3((float)Math.Sin(breath.Heading), 0, (float)Math.Cos(breath.Heading));
        var side = new Vector3(forward.Z, 0, -forward.X);
        bool Corridor(Vector3 start)
        {
            // Breath pushes 35 yalms along the boss's heading. The helper at the
            // far wall is not the origin; validate the full forced corridor.
            for (float distance = 0; distance <= 35; distance += .25f)
            {
                var p = start + forward * distance;
                if (!InsideEros(p) || !PoisonSafe(p, poisonCenters)) return false;
            }
            return true;
        }
        bool Valid(Vector3 stage)
        {
            if (!InsideEros(stage) || !Safe(stage) || !SegmentSafe(Core.Me.Location, stage)) return false;
            // Arrival can stop short. Validate from the actual hold to avoid
            // repeatedly selecting/releasing a goal on a puddle boundary.
            var hold = Core.Me.Location.Distance2D(stage) <= .35f ? Core.Me.Location : stage;
            return Corridor(hold);
        }
        if (breath.Stage.HasValue && Valid(breath.Stage.Value)) return breath.Stage;
        var candidates = new List<Vector3>();
        for (float offset = -13.5f; offset <= 13.5f; offset += .5f)
        {
            // A 1.5-yalm standoff keeps both ends on the platform in either mirror.
            // Plan at 5 yalms/sec without requiring Sprint or anti-knockback.
            var stage = breath.Origin + forward * 1.5f + side * offset;
            double seconds = (breath.Finish - DateTime.UtcNow).TotalSeconds;
            if (Core.Me.Location.Distance2D(stage) / 5.0 + .3 <= seconds && Valid(stage)) candidates.Add(stage);
        }
        return candidates.OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
    }

    // Read wrappers once per search on the bot thread; sample scalar centers.
    private Vector3[] PoisonCenters() => PoisonObjects().Select(o => o.Location)
        .Concat(Pending().Where(c => c.Action is Actions.ViperPoisonBait or Actions.ViperPoisonGround).Select(Position)).ToArray();
    private static bool PoisonSafe(Vector3 p, Vector3[] centers) => centers.All(o => p.Distance2D(o) > 6.5f);

    private static bool InsideEros(Vector3 p) => p.X >= 2.5f && p.X <= 31.5f && p.Z >= -557.5f && p.Z <= -518.5f;

    private static IEnumerable<Vector3> EdgeCandidates()
    {
        for (float z = -556.5f; z <= -519.5f; z += 2)
        {
            yield return new Vector3(3.5f, -77, z);
            yield return new Vector3(30.5f, -77, z);
        }
        for (float x = 3.5f; x <= 30.5f; x += 2)
        {
            yield return new Vector3(x, -77, -556.5f);
            yield return new Vector3(x, -77, -519.5f);
        }
    }
}
