using Clio.Utilities;
using DutyMechanic.Data;
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
/// Converts confirmed Sohm Al cast geometry into scalar native avoids. Choreography,
/// room-wide damage and positive stacks are excluded; helper casts own their own
/// shapes so Tioman's eight-spoke sequence cannot become one boss-facing rectangle.
/// </summary>
public partial class SohmAl
{
    private sealed record Hazard(uint Caster, uint Action, Vector3 Origin, float Heading,
        Vector2[] Points, DateTime Finish, DateTime Expires);
    private readonly Dictionary<(uint, uint), Hazard> hazards = new();
    private readonly List<AvoidInfo> registeredHazards = [];
    private bool hadMeteorMarker;
    private DateTime meteorPlacementUntil;

    // October14/20 historical casts plus installed Global action rows. Distinct
    // IDs matter: the two identically named Mad Dashes have opposite responses.
    private static class ActionId
    {
        internal const uint BloodyCaress = 3793, AcidRain = 3799, Phytobeam = 3797, Devour = 5010;
        internal const uint RazorScales = 3804, Spread = 3808, Stack = 3809;
        internal const uint AbyssicBuster = 3811, ChaosCircle = 3813, ChaosLine = 3819;
        internal const uint CometCircle = 3816, Heavensfall = 3818;
        internal const uint MeteorImpact = 4999;
        // Synthetic identity: marker placement has a lifetime independent of cast bars.
        internal const uint MeteorPlacement = uint.MaxValue - 7;
    }

    private void ReleaseHazardRegistrations()
    {
        foreach (var handle in registeredHazards) AvoidanceManager.RemoveAvoid(handle);
        registeredHazards.Clear();
    }

    private void RegisterHazards()
    {
        if (registeredHazards.Count == 4 && registeredHazards.All(AvoidanceManager.AvoidInfos.Contains)) return;
        ReleaseHazardRegistrations();
        // Scalar origins avoid native attraction toward moving helpers/party actors.
        // Retained handles allow reacquisition after an external clear without duplicates.
        registeredHazards.Add(AvoidanceManager.AddAvoidPolygon<Hazard>(condition: OwnsHazards,
            leashPointProducer: () => ArenaCenter, leashRadius: 90,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1,
            heightProducer: _ => 10, pointsProducer: h => h.Points,
            locationProducer: h => h.Origin, collectionProducer: ActiveHazards,
            priority: AvoidancePriority.High));
        foreach (uint boss in new[] { Raskovnik, Myath, Tioman })
        {
            var center = boss == Raskovnik ? RaskovnikCenter : boss == Myath ? MyathCenter : TiomanCenter;
            float radius = boss == Raskovnik ? 22.5f : boss == Myath ? 29.5f : 26.5f;
            registeredHazards.Add(AvoidanceHelpers.AddAvoidDonut(() => OwnsHazards() && encounter == boss,
                () => OwnsHazards() && encounter == boss ? new[] { center } : Array.Empty<Vector3>(),
                100, radius, AvoidancePriority.High));
        }
    }

    private static bool PartyActor(BattleCharacter a) => a.ObjectId == Core.Me.ObjectId ||
        PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId);

    private void UpdateHazards(BattleCharacter[] actors, BattleCharacter boss, DateTime now)
    {
        var casts = actors.Where(Casting).ToArray();
        // A vanished/cancelled owner must not leave a long-lived cast avoid. Retain
        // only the bounded350ms delivery tail after an observed cast has actually ended.
        foreach (var key in hazards.Where(p => p.Value.Expires <= now ||
            (p.Value.Finish > now &&
             !casts.Any(a => a.ObjectId == p.Value.Caster && a.CastingSpellId == p.Value.Action)))
            .Select(p => p.Key).ToArray()) hazards.Remove(key);

        foreach (var actor in casts)
        {
            uint action = actor.CastingSpellId;
            var origin = actor.Location;
            Vector2[] points = null;
            // All damaging shapes add0.5y clearance. Cone edge margins are applied
            // geometrically by expanding the fan two degrees per side as well as range.
            switch (action)
            {
                case ActionId.Phytobeam: points = Rectangle(13, 45.5f); break;
                case ActionId.AcidRain: origin = actor.SpellCastInfo.CastLocation; points = Circle(6.5f); break;
                case ActionId.Devour: points = Circle(8.5f); break;
                case ActionId.RazorScales: points = Sector(0, 60.5f, 64); break;
                case ActionId.ChaosCircle: origin = actor.SpellCastInfo.CastLocation; points = Circle(2.5f); break;
                case ActionId.ChaosLine: points = Rectangle(5, 50.5f); break;
                case ActionId.CometCircle: origin = actor.SpellCastInfo.CastLocation; points = Circle(4.5f); break;
                case ActionId.Heavensfall: origin = actor.SpellCastInfo.CastLocation; points = Circle(5.5f); break;
                // Proximity falloff has a30y effect range, not a binary30y death
                // circle. The corroborating mechanic model uses15y dangerous floor.
                // October28 WHM capture escaped to15.558y before cast end and
                // survived1661 proximity damage without vulnerability or input drift.
                case ActionId.MeteorImpact: points = Circle(15.5f); break;
            }
            if (points == null || origin == Vector3.Zero) continue;
            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            var key = (actor.ObjectId, action);
            bool first = !hazards.ContainsKey(key);
            // Meteor's instant4997 impact follows the4999 cast by roughly1s;
            // preserve that separate delivery stage plus the ordinary350ms tail.
            hazards[key] = new(actor.ObjectId, action, origin, actor.Heading, points, finish,
                finish.AddSeconds(action == ActionId.MeteorImpact ? 1.35 : .35));
            if (first && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information("[SohmAl] HAZARD action={0} caster={1} origin={2} heading={3} finish={4:o}.",
                    action, actor.ObjectId, origin, actor.Heading, finish);
        }
        UpdateSpread(actors, boss, now);
        UpdateCleave(boss, now);
        UpdateMeteorPlacement(casts, now);
        // Object2005287 belonged to trash Fireball3928 in the original handler.
        // Its later unscoped registration was not a boss mechanic; SideStep keeps
        // trash ownership, while passive diagnostics retain visibility evidence.
    }

    private void UpdateSpread(BattleCharacter[] actors, BattleCharacter boss, DateTime now)
    {
        // These synthetic entries are recalculated from the current cast target,
        // not retained for the old seven-second timer across the following stack.
        foreach (var key in hazards.Keys.Where(k => k.Item2 == ActionId.Spread).ToArray()) hazards.Remove(key);
        if (Casting(boss) && boss.CastingSpellId == ActionId.Spread)
        {
            spreadTarget = boss.SpellCastInfo.TargetId;
            spreadUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.35);
        }
        // A different cast or disappearance well before the delivery tail is
        // cancellation.250ms tolerance avoids mistaking cast-bar update skew for it.
        else if (Casting(boss) || now >= spreadUntil || spreadUntil - now > TimeSpan.FromSeconds(.6))
            spreadTarget = 0;
        if (spreadTarget == 0) return;
        uint markedId = spreadTarget;
        var targets = actors.Where(a => a.IsVisible && PartyActor(a) && a.ObjectId != Core.Me.ObjectId &&
            (markedId == Core.Me.ObjectId || a.ObjectId == markedId));
        var finish = spreadUntil;
        foreach (var target in targets)
            hazards[(target.ObjectId, ActionId.Spread)] = new(target.ObjectId, ActionId.Spread,
                target.Location, 0, Circle(6.5f), finish, finish);
    }

    private void UpdateCleave(BattleCharacter boss, DateTime now)
    {
        foreach (var key in hazards.Keys.Where(k => k.Item2 is ActionId.BloodyCaress or ActionId.AbyssicBuster).ToArray()) hazards.Remove(key);
        // Instant frontal attacks have no useful cast bar. Their current victim is
        // exempt, allowing a tank to face away from the party instead of chasing
        // its own rotating cone. Untargetable or invincibility325 wing-phase
        // Tioman cannot cleave, but the rest of her helper geometry remains active.
        if (!boss.IsTargetable || !boss.CanAttack ||
            (boss.Auras != null && boss.Auras.IsValid && boss.Auras.Any(a => a.Id == 325)) || boss.CurrentTargetId == 0 ||
            boss.CurrentTargetId == 0xE0000000 || boss.CurrentTargetId == Core.Me.ObjectId) return;
        uint action = encounter == Raskovnik ? ActionId.BloodyCaress : encounter == Tioman ? ActionId.AbyssicBuster : 0;
        if (action == 0) return;
        float reach = encounter == Raskovnik ? 8.5f : 25.5f;
        hazards[(boss.ObjectId, action)] = new(boss.ObjectId, action, boss.Location, boss.Heading,
            Sector(0, reach, 94), now, now.AddSeconds(.25));
    }

    private void UpdateMeteorPlacement(BattleCharacter[] casts, DateTime now)
    {
        bool marked = encounter == Tioman && Core.Me.VfxContainer.IsValid &&
            Core.Me.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 7);
        // Green marker7 predicts placement about9.1s later. Capture the rising edge
        // once, bound missing-helper recovery at10s, and release as soon as4999 fixes
        // the impact origin. A lingering icon cannot rearm after that handoff.
        if (marked && !hadMeteorMarker) meteorPlacementUntil = now.AddSeconds(10);
        hadMeteorMarker = marked;
        if (encounter != Tioman || casts.Any(a => a.CastingSpellId == ActionId.MeteorImpact))
            meteorPlacementUntil = DateTime.MinValue;
    }

    private IEnumerable<Hazard> ActiveHazards()
    {
        if (!OwnsHazards()) return Array.Empty<Hazard>();
        var now = DateTime.UtcNow;
        var shapes = hazards.Values.Where(h => h.Expires > now);
        // Unsafe-center geometry requests peripheral bait without choosing a point
        // or adding another movement controller. The23y staging radius is inset into
        // the safe outer ring by0.5y. October28 WHM captured self placement at24.26y
        // and immediate handoff to the fixed impact while the marker still lingered.
        if (meteorPlacementUntil > now)
            shapes = shapes.Append(new(0, ActionId.MeteorPlacement, TiomanCenter, 0,
                Circle(23.5f), meteorPlacementUntil, meteorPlacementUntil));
        return shapes.ToArray();
    }

    private static bool Contains(Hazard h, Vector3 point)
    {
        // Inverse of the polygon registration's -heading world-XZ transform.
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
        // Three-degree facets lose at most0.021y at60.5y, inside the0.5y margin.
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
