using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Snapshots Keeper cast geometry and orders staggered hazards without retaining
/// frame-scoped actors or issuing movement. Dimensions follow the October 27 audit.
/// </summary>
public partial class KeeperOfTheLake
{
    private sealed record Hazard(uint Caster, uint Action, Vector3 Origin, float Heading,
        Vector2[] Points, DateTime Finish, DateTime Expires, bool Forecast = false);
    private readonly Dictionary<(uint, uint), Hazard> hazards = new();
    private bool fireSideRestricted;

    // Named damage actions exclude choreography, raidwides and positive-position mechanics.
    private static class ActionId
    {
        internal const uint QuickCannon = 29271, MiniCannon = 29272, TankExplosion = 29275;
        internal const uint Flamethrower = 3389, CarpetBomb = 3392, Pool = 3411;
        internal const uint VanguardLine = 1433, VanguardCone = 1435;
        internal const uint Admonishment = 29889, InnerTurmoil = 29888;
        internal const uint PhantomLine = 29280, PhantomInner = 29278, PhantomOuter = 29279, MirageLine = 29290;
        internal const uint RingCenter = 29286, RingMiddle = 29287, RingOuter = 29288;
    }

    // The authored out-of-combat profile hook deliberately clears registrations.
    // Retain our exact handles so the next boss can reacquire missing registrations
    // without duplicating survivors or removing another component's definitions.
    private readonly List<ff14bot.Pathing.Avoidance.AvoidInfo> registeredHazards = [];

    private void ReleaseHazardRegistrations()
    {
        foreach (var registration in registeredHazards) AvoidanceManager.RemoveAvoid(registration);
        registeredHazards.Clear();
    }

    private void RegisterHazards()
    {
        if (registeredHazards.Count == 4 && registeredHazards.All(AvoidanceManager.AvoidInfos.Contains)) return;
        ReleaseHazardRegistrations();
        // Scalar collection prevents RB from attracting escape paths toward a moving
        // helper. Empty inactive collections also avoid suppressing SideStep on trash.
        registeredHazards.Add(AvoidanceManager.AddAvoidPolygon<Hazard>(condition: OwnsHazards,
            leashPointProducer: () => ArenaCenter, leashRadius: 60,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1,
            heightProducer: _ => 15, pointsProducer: h => h.Points,
            locationProducer: h => h.Origin, collectionProducer: ActiveHazards,
            priority: AvoidancePriority.High));
        foreach (uint boss in new[] { Einhander, Gunship, Midgardsormr })
        {
            var center = boss == Einhander ? EinhanderCenter : boss == Gunship ? GunshipCenter : MidgardsormrCenter;
            var radius = boss == Einhander ? 19.5f : boss == Gunship ? 18.5f : 19f;
            registeredHazards.Add(AvoidanceHelpers.AddAvoidDonut(() => OwnsHazards() && encounter == boss,
                () => OwnsHazards() && encounter == boss ? new[] { center } : Array.Empty<Vector3>(),
                90, radius, AvoidancePriority.High));
        }
    }

    private void UpdateHazards(BattleCharacter[] actors, DateTime now)
    {
        UpdateFireTrail(encounter == Gunship ? actors.FirstOrDefault(a => IsBoss(a) && a.NpcId == Gunship) : null, now);
        foreach (var key in hazards.Where(p => p.Value.Expires <= now).Select(p => p.Key).ToArray()) hazards.Remove(key);
        var casts = actors.Where(Casting).ToArray();
        // The flame is a fixed channel through five instant follow-up ticks. A later
        // distinct gunship cast ends the channel. Preserve the bounded 12s fallback
        // used by the qualifying captures; never rotate a persisted cone with its owner.
        if (casts.Any(a => IsBoss(a) && a.NpcId == Gunship && a.CastingSpellId != ActionId.Flamethrower))
            foreach (var key in hazards.Keys.Where(k => k.Item2 == ActionId.Flamethrower).ToArray()) hazards.Remove(key);

        foreach (var actor in casts)
        {
            uint action = actor.CastingSpellId;
            var origin = actor.Location;
            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            Vector2[] points = null;
            switch (action)
            {
                case ActionId.QuickCannon: points = Rectangle(5, 40.5f); break;
                case ActionId.MiniCannon:
                    // This is proximity falloff, not a 31y binary damage circle. The
                    // corroborated 17y danger zone leaves an outer corridor in the20y room.
                    origin = actor.SpellCastInfo.CastLocation;
                    points = Circle(17.5f);
                    break;
                case ActionId.TankExplosion: points = Circle(12.5f); break;
                case ActionId.Flamethrower: points = Sector(0, 18.5f, 130); break;
                case ActionId.CarpetBomb:
                    origin = actor.SpellCastInfo.CastLocation;
                    points = Circle(5.5f);
                    break;
                case ActionId.VanguardLine: points = Rectangle(6, 33.3f); break;
                case ActionId.VanguardCone: points = Sector(0, 11.3f, 130); break;
                case ActionId.Admonishment:
                case ActionId.PhantomLine:
                case ActionId.MirageLine: points = Rectangle(13, 40.5f); break;
                case ActionId.InnerTurmoil:
                case ActionId.PhantomInner: points = Circle(22.5f); break;
                case ActionId.PhantomOuter:
                    // The damage is a forward half-donut from the southern boss origin,
                    // not a full room-centered ring. Inset the safe hole by0.5y.
                    points = Sector(21.5f, 40.5f, 184);
                    break;
                case ActionId.RingCenter: points = Circle(6.5f); break;
                case ActionId.RingMiddle: points = Sector(5.5f, 12.5f, 360); break;
                case ActionId.RingOuter: points = Sector(11.5f, 20.5f, 360); break;
            }
            if (points == null) continue;
            // Reject absent destinations rather than placing ground hazards at world zero.
            if (origin == Vector3.Zero) continue;
            var expires = finish.AddSeconds(action == ActionId.Flamethrower ? 12 : .5);
            var key = (actor.ObjectId, action);
            bool first = !hazards.TryGetValue(key, out var old) || old.Forecast;
            hazards[key] = new(actor.ObjectId, action, origin, actor.Heading, points, finish, expires);
            if (first && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information("[Keeper] HAZARD action={0} caster={1} origin={2} finish={3:o}.", action, actor.ObjectId, origin, finish);
        }

        // Event-object visibility owns persistent pools; no guessed timer survives despawn.
        var pools = GameObjectManager.GameObjects.Where(a => a.IsValid && a.NpcId == 2005194 &&
            a.IsVisible && Math.Abs(a.Y - GunshipCenter.Y) < 8).ToArray();
        foreach (var key in hazards.Keys.Where(k => k.Item2 == ActionId.Pool && !pools.Any(p => p.ObjectId == k.Item1)).ToArray()) hazards.Remove(key);
        foreach (var pool in pools)
            hazards[(pool.ObjectId, ActionId.Pool)] = new(pool.ObjectId, ActionId.Pool, pool.Location, 0,
                Circle(8.5f), now, now.AddSeconds(1));

        var main = casts.FirstOrDefault(a => IsBoss(a) && a.CastingSpellId is ActionId.PhantomLine or ActionId.PhantomInner or ActionId.PhantomOuter);
        if (main == null) return;
        // Visible phantom-only base IDs identify the next two lanes before their1.7s
        // cast bars. The combat dragons use different bases. Actual helper casts replace
        // these forecasts. October 28 captures verified the earlier staging cue.
        var mainFinish = now + main.SpellCastInfo.RemainingCastTime;
        foreach (var mirage in actors.Where(a => a.BaseId is 0x392B or 0x392C && a.IsVisible &&
            !a.IsTargetable && a.Location.Distance2D(MidgardsormrCenter) < 45 && !Casting(a)))
        {
            var key = (mirage.ObjectId, ActionId.MirageLine);
            if (hazards.TryGetValue(key, out var old) && !old.Forecast) continue;
            hazards[key] = new(mirage.ObjectId, ActionId.MirageLine, mirage.Location, mirage.Heading,
                Rectangle(13, 40.5f), mainFinish.AddSeconds(2), mainFinish.AddSeconds(2.5), true);
        }
    }

    private IEnumerable<Hazard> ActiveHazards()
    {
        if (!OwnsHazards()) return Array.Empty<Hazard>();
        var live = hazards.Values.Concat(fireTrail == null ? Array.Empty<Hazard>() : new[] { fireTrail })
            .Where(h => h.Expires > DateTime.UtcNow).ToArray();
        // Ring bars overlap by about2s: unioning all three would erase the room.
        // Keep the first resolving group through its effect tail, then advance inward.
        var firstRing = live.Where(h => IsRing(h.Action)).Select(h => h.Finish).DefaultIfEmpty(DateTime.MaxValue).Min();
        var firstTank = live.Where(h => h.Action == ActionId.TankExplosion).Select(h => h.Finish).DefaultIfEmpty(DateTime.MaxValue).Min();
        var selected = live.Where(h => (!IsRing(h.Action) || h.Finish <= firstRing.AddMilliseconds(600)) &&
            (h.Action != ActionId.TankExplosion || h.Finish <= firstTank.AddMilliseconds(600))).ToArray();
        // Prefer common-safe floor for main+phantom stages. If their union has no
        // floor, honor main impact first; forecasts cannot force an earlier lethal hit.
        if (selected.Any(h => h.Forecast) && selected.Any(h => h.Action is ActionId.PhantomLine or ActionId.PhantomInner or ActionId.PhantomOuter) &&
            !HasCommonFloor(selected)) return selected.Where(h => !h.Forecast).ToArray();
        return SelectFireEscapeRegion(selected);
    }

    private Hazard[] SelectFireEscapeRegion(Hazard[] shapes)
    {
        bool restricted = false;
        // The October 28 failure escaped a future fire lane by crossing Drill Cannons before
        // its cast finished. Equal-cost native avoids describe safe endpoints but
        // do not preserve that earlier impact's side during an escape. While the
        // fire is still a forecast, keep the current safe component of the cannon
        // line if it contains common-safe floor. Native avoidance still chooses
        // every destination and input; no extra movement controller is introduced.
        if (fireTrail != null && DateTime.UtcNow < fireTrail.Finish && Contains(fireTrail, Core.Me.Location))
        {
            var candidate = shapes.ToArray();
            for (int i = 0; i < candidate.Length; i++)
            {
                var line = candidate[i];
                if (line.Action != ActionId.VanguardLine || line.Expires > fireTrail.Finish) continue;
                float dx = Core.Me.X - line.Origin.X, dz = Core.Me.Z - line.Origin.Z;
                float side = dx * (float)Math.Cos(line.Heading) - dz * (float)Math.Sin(line.Heading);
                float halfWidth = line.Points.Max(p => p.X);
                // Already inside the cannon line: preserve ordinary emergency
                // egress rather than guessing which side the player can reach.
                if (Math.Abs(side) <= halfWidth) continue;
                float extent = line.Origin.Distance2D(ArenaCenter) + ArenaRadius + 1;
                float left = side < 0 ? -halfWidth : -extent;
                float right = side < 0 ? extent : halfWidth;
                candidate[i] = line with { Points = [new(left, -extent), new(right, -extent),
                    new(right, extent), new(left, extent)] };
                restricted = true;
            }
            // A moving/second add or old pools can consume that component. Never
            // impose a side constraint without a safe region inside arena bounds.
            if (restricted && HasCommonFloor(candidate)) shapes = candidate;
            else restricted = false;
        }
        if (restricted != fireSideRestricted)
        {
            fireSideRestricted = restricted;
            Logger.Information(restricted ? "[Keeper] Fire escape constrained to current side of earlier Drill Cannons."
                : "[Keeper] Fire escape side constraint released.");
        }
        return shapes;
    }

    private static bool IsRing(uint action) => action is ActionId.RingCenter or ActionId.RingMiddle or ActionId.RingOuter;

    private bool HasCommonFloor(Hazard[] shapes)
    {
        // Feasibility only: native avoidance still chooses the destination/path. A1y
        // grid is smaller than the corroborated lane gaps and includes the room center.
        for (float x = -ArenaRadius; x <= ArenaRadius; x += 1)
            for (float z = -ArenaRadius; z <= ArenaRadius; z += 1)
            {
                if (x * x + z * z > (ArenaRadius - .5f) * (ArenaRadius - .5f)) continue;
                var point = new Vector3(ArenaCenter.X + x, ArenaCenter.Y, ArenaCenter.Z + z);
                if (!shapes.Any(h => Contains(h, point))) return true;
            }
        return false;
    }

    private static bool Contains(Hazard h, Vector3 point)
    {
        // Convert world XZ into the same forward-local frame used by rotation=-heading.
        float dx = point.X - h.Origin.X, dz = point.Z - h.Origin.Z;
        float x = dx * (float)Math.Cos(h.Heading) - dz * (float)Math.Sin(h.Heading);
        float y = dx * (float)Math.Sin(h.Heading) + dz * (float)Math.Cos(h.Heading);
        bool inside = false;
        for (int i = 0, j = h.Points.Length - 1; i < h.Points.Length; j = i++)
        {
            var a = h.Points[i]; var b = h.Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -.5f), new(width / 2, -.5f), new(width / 2, length), new(-width / 2, length)];
    private static Vector2[] Circle(float radius) => Sector(0, radius, 360);
    private static Vector2[] Sector(float inner, float outer, float degrees)
    {
        // 3deg facets keep chord error below0.02y at40.5y, inside the0.5y margin.
        int steps = (int)Math.Ceiling(degrees / 3);
        var points = new List<Vector2>();
        for (int i = 0; i <= steps; ++i)
        {
            double angle = (i * degrees / steps - degrees / 2) * Math.PI / 180;
            points.Add(new(outer * (float)Math.Sin(angle), outer * (float)Math.Cos(angle)));
        }
        if (inner == 0) points.Add(Vector2.Zero);
        else for (int i = steps; i >= 0; --i)
        {
            double angle = (i * degrees / steps - degrees / 2) * Math.PI / 180;
            points.Add(new(inner * (float)Math.Sin(angle), inner * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }
}
