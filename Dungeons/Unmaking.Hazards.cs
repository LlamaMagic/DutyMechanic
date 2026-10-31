using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

public sealed partial class Unmaking
{
    // Six-yalm damage radius plus two observed three-yalm advances. The earlier
    // one-step buffer left the WHM stationary at 11.4y until the orb was already 9.3y
    // away. This planning radius (plus Margin) gives another step of lead time,
    // including the initial cast, without predicting another player's future route.
    private const float ChasePlanningRadius = 12;

    private void BuildHazards(DateTime now)
    {
        var live = new HashSet<string>();
        void Add(string key, Vector3 origin, float heading, Func<Point[]> shape)
        {
            live.Add(key);
            if (!hazards.TryGetValue(key, out var h))
            {
                var polygon = shape();
                h = new Hazard { MathPolygon = polygon };
                hazards[key] = h;
            }
            h.Origin = origin;
            h.Heading = heading;
            PublishPolygon(h);
        }
        void Circle(string key, Vector3 origin, float radius) => Add(key, origin, 0,
            () => UnmakingGeometry.Circle(radius + UnmakingGeometry.Margin));

        Add("boundary", new Vector3(0, Core.Me.Y, 0), 0, () => boundary);
        // All ten cones are visible before the first resolves. Registering all of
        // them would cover the entire floor. The earliest seven preserve the later
        // safe sector; scalar finish times order repeated sectors deterministically.
        var cones = casts.Values.Where(c => c.Action == GazeCone).ToArray();
        // At 15:21:35 the later Gaze escape crossed a resolving Torrent and gained a
        // vulnerability. Finish the curved lines first when their resolution tail
        // still leaves at least 4s (24y of travel) before the first cone. Native
        // avoidance owns both stages; removing Torrent admits the pending cones.
        var torrents = casts.Values.Where(c => c.Action is TorrentSmall or TorrentMedium or TorrentLarge).ToArray();
        bool torrentFirst = cones.Length > 0 && torrents.Length > 0 &&
            torrents.Max(c => c.Until).AddSeconds(4) <= cones.Min(c => c.Finish);
        // Gaze staging crossed the resolving Vacuum at 21:06:49. Its first cone
        // still had over 3s left after the circle damage. Finish the earlier landing
        // circles before introducing the seven-cone union; native escape remains
        // responsible for both stages. Do not defer an earlier/simultaneous cone.
        var vacuums = casts.Values.Where(c => c.Action == Vacuum).ToArray();
        bool vacuumFirst = cones.Length > 0 && vacuums.Length > 0 &&
            vacuums.Max(c => c.Until).AddSeconds(2.5) <= cones.Min(c => c.Finish);
        var meltdowns = casts.Values.Where(c => c.Action is Puddle or Spread).ToArray();
        // The 21:34 capture escaped Vacuum for a later puddle, then crossed that
        // puddle again to stage Gaze. Keep each earlier effect authoritative when
        // its tail still leaves 2.5s (15y of travel) for the next dodge. Re-evaluate
        // every pulse; urgent or simultaneous mechanics remain in the union.
        bool meltdownFirst = cones.Length > 0 && meltdowns.Length > 0 &&
            meltdowns.Max(c => c.Until).AddSeconds(2.5) <= cones.Min(c => c.Finish);
        bool vacuumBeforeMeltdown = vacuums.Length > 0 && meltdowns.Length > 0 &&
            vacuums.Max(c => c.Until).AddSeconds(2.5) <= meltdowns.Min(c => c.Finish);
        var coneOwners = cones.Where(_ => !torrentFirst && !vacuumFirst && !meltdownFirst)
            .OrderBy(c => c.Finish).ThenBy(c => c.Caster).Take(7).Select(c => c.Caster).ToHashSet();
        // At 16:02:20 the short Vacuum bar arrived after movement for Naught Grows
        // had already crossed the approaching orbs. Publish their announced landing
        // circles alongside Torrent and Naught so native escape can choose a shared
        // safe region from the outset. Actual Vacuum reuses this key and object;
        // the handoff must not restart the native escape with a duplicate circle.
        foreach (var preview in approachingVacuum.Values)
            Circle($"cast/{preview.Caster}/{Vacuum}", preview.Origin, 7);
        chased.Clear();
        var movingChasers = new HashSet<uint>();
        foreach (var actor in actors.Where(a => a.VfxContainer.IsValid))
        {
            foreach (var tether in actor.VfxContainer.Tethers.Where(t => t.Id is ChaseTether or PassTether))
            {
                if (party.Any(p => p.ObjectId == tether.TargetId)) chased.Add(tether.TargetId);
                if (tether.Id == PassTether && party.Any(p => p.ObjectId == actor.ObjectId)) chased.Add(actor.ObjectId);
                // Active 404 chaser proximity threatens everyone, including unmarked
                // players crossed by another carrier. Native escape owns the response.
                if (actor.BaseId == Chaser && tether.Id == ChaseTether)
                {
                    observedChasers.Add(actor.ObjectId);
                    movingChasers.Add(actor.ObjectId);
                    // The 21:01 WHM capture ran 2-3y behind another carrier and took
                    // repeated Chase hits despite continuous escape from the orb.
                    // Keep its current target's 6y landing area clear as well, so
                    // native planning can separate us instead of sharing that route.
                    // This predicts no turns or trail lifetime and never repels the
                    // carrier from their own position. Live 404 owns the separation.
                    var carrier = party.FirstOrDefault(p => p.ObjectId == tether.TargetId && !p.IsMe);
                    if (carrier != null) Circle($"chase-carrier/{carrier.ObjectId}", carrier.Location, 6);
                }
            }
        }
        foreach (var actor in actors.Where(a => a.BaseId == Chaser))
        {
            bool firstCast = casts.Values.Any(c => c.Caster == actor.ObjectId && c.Action == ChaseFirst);
            if (firstCast) observedChasers.Add(actor.ObjectId);
            if (!observedChasers.Contains(actor.ObjectId) || firstCast) continue;
            // Losing the tether does not end the damage. In two WHM captures the
            // untethered orb remained while our route crossed its last position.
            // Keep its 6y damage footprint until the actor disappears. Only an
            // actively tethered orb needs the larger lead-time planning radius.
            // Separate keys rebuild the polygon when the radius changes.
            bool moving = movingChasers.Contains(actor.ObjectId);
            Circle($"chase/{actor.ObjectId}/{(moving ? "moving" : "ending")}", actor.Location,
                moving ? ChasePlanningRadius : 6);
        }
        observedChasers.RemoveWhere(id => !actors.Any(a => a.BaseId == Chaser && a.ObjectId == id));

        foreach (var c in casts.Values)
        {
            string key = $"cast/{c.Caster}/{c.Action}";
            switch (c.Action)
            {
                case GrowsLarge when !FinishMeltdownFirst(c): Circle(key, c.Origin, 40); break;
                case GrowsSmall when !FinishMeltdownFirst(c): Circle(key, c.Origin, 12); break;
                case GazeCone when coneOwners.Contains(c.Caster):
                    Add(key, c.Origin, c.Heading, () => UnmakingGeometry.Cone(40, 22.5f)); break;
                case Puddle when !vacuumBeforeMeltdown:
                    // A missing location is evidence failure, not a circle at world zero.
                    if (c.Origin != Vector3.Zero) Circle(key, c.Origin, 5);
                    else Warn("Meltdown has no ground location; capture its helper/omen before adding a fallback.");
                    break;
                case ChaseFirst: Circle(key, c.Origin, ChasePlanningRadius); break;
                case Vacuum: Circle(key, c.Origin, 7); break;
                case Empty: Circle(key, c.Origin, 10); break;
                case Proximity:
                    //100y is damage reach with falloff, not a forbidden circle. The normal
                    // enlarged room permits 42y separation, plus the standard 0.5y margin.
                    Circle(key, c.Origin, 42); break;
                case Nothingness:
                    Add(key, c.Origin, c.Heading, () => UnmakingGeometry.Rectangle(100, 2)); break;
                case TorrentSmall:
                case TorrentMedium:
                case TorrentLarge:
                    float halfAngle = TorrentHalfAngle(c.Action);
                    Add(key, c.Origin, c.Heading, () => UnmakingGeometry.Sector(halfAngle)); break;
                case Flare:
                case Spread when !vacuumBeforeMeltdown:
                    float radius = c.Action == Flare ? 15 : 5;
                    // A marked local player must actively separate. Avoiding only the
                    // other marked target would leave the carrier inside the party.
                    foreach (var p in party.Where(p => !p.IsMe && (c.Target == Core.Me.ObjectId || p.ObjectId == c.Target)))
                        // A self spread and another target's cast describe the same
                        // forbidden participant circle. Publish it once for the union.
                        Circle($"spacing/{c.Action}/{p.ObjectId}", p.Location, radius);
                    break;
            }
        }
        foreach (string key in hazards.Keys.Where(k => !live.Contains(k)).ToArray()) hazards.Remove(key);
    }

    private static float TorrentHalfAngle(uint action) => action == TorrentSmall ? 10 : action == TorrentMedium ? 20 : 30;

    private bool FinishMeltdownFirst(Cast portal)
    {
        var earlier = casts.Values.Where(c => c.Action is Puddle or Spread && c.Finish < portal.Finish).ToArray();
        if (earlier.Length == 0) return false;
        // At 21:07:13 the portal forecast drove a safe WHM back through resolving
        // Meltdown circles. Defer that forecast only while a radial departure still
        // fits after Meltdown:6y/s,1y arrival clearance and 1s for input/path latency.
        // Re-evaluate current position each pulse; do not promise a future safe path.
        float radius = portal.Action == GrowsLarge ? 40 : 12;
        float departure = Math.Max(0, radius + UnmakingGeometry.Margin + 1 -
            Point.Distance(Xz(Core.Me.Location), Xz(portal.Origin)));
        return earlier.Max(c => c.Until).AddSeconds(departure / 6 + 1) < portal.Finish;
    }

    private void PublishPolygon(Hazard hazard)
    {
        // RB 942's admission check uses LocationProducer inside a 70y local box, not the
        // polygon's intersecting bounds. Rebase without changing world geometry. Keeping
        // an anchor until 40y travel leaves room for the native 10y recenter threshold and
        // avoids a geometry rewrite on every player step. This changes no shared helper.
        bool reanchor = hazard.NativePolygon == null || hazard.NativeAnchor.Distance2D(Core.Me.Location) > 40;
        if (!reanchor && hazard.PublishedOrigin == hazard.Origin && hazard.PublishedHeading == hazard.Heading) return;
        if (reanchor) hazard.NativeAnchor = Core.Me.Location;
        hazard.NativePolygon = UnmakingGeometry.Rebase(hazard.MathPolygon, Xz(hazard.Origin), hazard.Heading, Xz(hazard.NativeAnchor))
            .Select(p => new Vector2(p.X, p.Y)).ToArray();
        hazard.PublishedOrigin = hazard.Origin;
        hazard.PublishedHeading = hazard.Heading;
    }

    private bool Safe(Point point) => UnmakingGeometry.InArena(point, adds, beacon) &&
        !hazards.Values.Any(h => UnmakingGeometry.InPolygon(
            UnmakingGeometry.Local(point, Xz(h.Origin), h.Heading), h.MathPolygon));

    private bool SafeApproach(Point point)
    {
        if (!Safe(point)) return false;
        var start = Xz(Core.Me.Location);
        int steps = Math.Max(1, (int)Math.Ceiling(Point.Distance(start, point) * 2));
        for (int i = 1; i <= steps; i++)
            if (!Safe(start + (point - start) * (i / (float)steps))) return false;
        return true;
    }
}
