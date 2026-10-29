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
/// Registers scalar geometry from each damaging cast, rather than attracting native
/// movement to its actor. Visual casts do not duplicate their helper's rectangles.
/// Positive transfer/knockback/stack requests use the same input channel separately.
/// </summary>
public partial class Aery
{
    private sealed record Hazard(uint Caster, uint Action, Vector3 Origin, float Heading,
        Vector2[] Points, DateTime Finish, DateTime Expires);
    private readonly Dictionary<(uint, uint), Hazard> hazards = new();
    private readonly List<AvoidInfo> registeredHazards = [];

    // Current client rows and October 14/20 captures distinguish visual 30196 from
    // damaging 30197 and each independent 30195 wing helper.30202 is not Cachexia.
    private static class ActionId
    {
        internal const uint Predation = 3887, Cachexia = 3889, LightningBolt = 3893;
        internal const uint Pyre = 30191, Burst = 30184, Ouroboros = 30190, BodySlam = 31234;
        internal const uint Tail = 30197, Wing = 30195, Cauterize = 30198, GroundRoar = 30200;
        internal const uint Spread = 30202, Stack = 30224, Roast = 30209, Prison = 30204;
    }

    private void ReleaseHazardRegistrations()
    {
        foreach (var handle in registeredHazards)
            AvoidanceManager.RemoveAvoid(handle);
        registeredHazards.Clear();
    }

    private void RegisterHazards()
    {
        if (registeredHazards.Count == 4 && registeredHazards.All(AvoidanceManager.AvoidInfos.Contains))
            return;
        ReleaseHazardRegistrations();
        // Keep exact handles: an external authored clear may require reacquisition,
        // but this handler must never clear another owner's registrations or paths.
        registeredHazards.Add(AvoidanceManager.AddAvoidPolygon<Hazard>(condition: OwnsHazards,
            leashPointProducer: () => ArenaCenter, leashRadius: 90,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1,
            heightProducer: _ => 12, pointsProducer: h => h.Points,
            locationProducer: h => h.Origin, collectionProducer: ActiveHazards,
            priority: AvoidancePriority.High));
        foreach (uint boss in new[] { Rangda, Gyascutus, Nidhogg })
        {
            var center = boss == Rangda ? RangdaCenter : boss == Gyascutus ? GyascutusCenter : NidhoggCenter;
            float radius = boss == Rangda ? 25.5f : boss == Gyascutus ? 19.0f : 29.5f;
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
        // Each helper has its own finish. Earlier visual/main-cast completion cannot
        // erase a later Burst or wing; cancellation removes only that cast's region.
        foreach (var key in hazards.Where(p => p.Value.Expires <= now ||
            (p.Value.Finish > now && !casts.Any(a => a.ObjectId == p.Value.Caster &&
                a.CastingSpellId == p.Value.Action))).Select(p => p.Key).ToArray())
            hazards.Remove(key);
        foreach (var actor in casts)
        {
            uint action = actor.CastingSpellId;
            var origin = actor.Location;
            float heading = actor.Heading;
            Vector2[] points = null;
            // Half-yalm clearance is applied to physical edges, not to a visual
            // owner's oversized approximation. Ground locations never become actor locations.
            switch (action)
            {
                case ActionId.Cachexia:
                    points = Sector(6.5f, 65, 360);
                    break;
                case ActionId.LightningBolt:
                    origin = actor.SpellCastInfo.CastLocation;
                    points = Circle(3.5f);
                    break;
                case ActionId.Pyre:
                    points = Circle(12.5f);
                    break;
                case ActionId.Burst:
                    points = Circle(10.5f);
                    break;
                case ActionId.Ouroboros:
                    points = Sector(10.5f, 20.5f, 360);
                    break;
                // Same-location helpers can have displaced telegraph origins. In
                // particular the two wing helpers must preserve the central gap.
                // Validate the live matrix dimensions rather than guess an offset.
                case ActionId.Tail:
                case ActionId.Wing:
                    if (TryTelegraphRectangle(actor, action == ActionId.Tail ? 16 : 68,
                        action == ActionId.Tail ? 68 : 30, out origin, out points))
                        heading = 0;
                    break;
                case ActionId.Cauterize:
                    points = Rectangle(23, 80.5f);
                    break;
                case ActionId.GroundRoar:
                    origin = actor.SpellCastInfo.CastLocation;
                    points = Circle(6.5f);
                    break;
                case ActionId.Roast:
                    points = Rectangle(9, 30.5f);
                    break;
                case ActionId.Spread:
                    // Self spread cannot avoid its own center. Other marked allies
                    // remain scalar circles; when self is marked every ally matters.
                    if (actor.SpellCastInfo.TargetId == Core.Me.ObjectId)
                        break;
                    var target = actors.FirstOrDefault(a => a.ObjectId == actor.SpellCastInfo.TargetId);
                    if (target != null)
                    {
                        origin = target.Location;
                        points = Circle(6.5f);
                    }
                    break;
            }
            if (points == null || origin == Vector3.Zero)
                continue;
            var finish = now + actor.SpellCastInfo.RemainingCastTime;
            var key = (actor.ObjectId, action);
            bool first = !hazards.ContainsKey(key);
            hazards[key] = new(actor.ObjectId, action, origin, heading, points, finish, finish.AddSeconds(.35));
            if (first && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information("[Aery] HAZARD action={0} caster={1} origin={2} heading={3} finish={4:o}.",
                    action, actor.ObjectId, origin, actor.Heading, finish);
        }
        // Dynamic synthetic self-spread constraints are rebuilt each pulse; the
        // source helper still determines the lifetime, including its delivery tail.
        foreach (var key in hazards.Keys.Where(k => k.Item2 == uint.MaxValue).ToArray())
            hazards.Remove(key);
        var selfSpread = casts.FirstOrDefault(a => a.CastingSpellId == ActionId.Spread &&
            a.SpellCastInfo.TargetId == Core.Me.ObjectId);
        if (selfSpread != null)
        {
            spreadUntil = now + selfSpread.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.35);
            spreadCaster = selfSpread.ObjectId;
        }
        else if (spreadUntil - now > TimeSpan.FromSeconds(.6))
            spreadUntil = DateTime.MinValue;
        if (spreadUntil > now)
            foreach (var ally in actors.Where(a => PartyActor(a) && a.ObjectId != Core.Me.ObjectId && a.IsVisible))
                hazards[(ally.ObjectId, uint.MaxValue)] = new(spreadCaster, ActionId.Spread, ally.Location,
                    0, Circle(6.5f), spreadUntil, spreadUntil);
        UpdateCleave(boss, now);
    }

    private DateTime spreadUntil;
    private uint spreadCaster;

    private static bool TryTelegraphRectangle(BattleCharacter actor, float width, float length,
        out Vector3 origin, out Vector2[] points)
    {
        var m = actor.OmenMatrix;
        origin = m.Center;
        points = null;
        float halfWidth = (float)Math.Sqrt(m.M00 * m.M00 + m.M02 * m.M02);
        float depth = (float)Math.Sqrt(m.M20 * m.M20 + m.M22 * m.M22);
        // Missing/degenerate matrices must remain diagnostic gaps, never an arena
        // erasing fallback. An acquired valid shape can survive a transient matrix
        // dropout until its existing cast expiry; it cannot gain a new lifetime.
        // Installed-client dimensions independently validate scale.
        if (origin == Vector3.Zero || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z) ||
            origin.Distance2D(actor.Location) > 80 ||
            !float.IsFinite(halfWidth) || !float.IsFinite(depth) ||
            Math.Abs(halfWidth * 2 - width) > 1 || Math.Abs(depth - length) > 1 ||
            Math.Abs(m.M00 * m.M20 + m.M02 * m.M22) > halfWidth * depth * .05f)
            return false;
        float across = 1 + .5f / halfWidth, behind = -.5f / depth, ahead = 1 + .5f / depth;
        Vector2 Corner(float x, float z) => new(x * m.M00 + z * m.M20, x * m.M02 + z * m.M22);
        points = [Corner(-across, behind), Corner(across, behind), Corner(across, ahead), Corner(-across, ahead)];
        return true;
    }

    private void UpdateCleave(BattleCharacter boss, DateTime now)
    {
        foreach (var key in hazards.Keys.Where(k => k.Item2 == ActionId.Predation).ToArray())
            hazards.Remove(key);
        // The actual victim is exempt; a non-tank with temporary aggro must not
        // chase its own rotating cone. Cachexia shares the inner floor behind Rangda.
        if (encounter != Rangda || !boss.IsTargetable || !boss.CanAttack ||
            boss.CurrentTargetId is 0 or 0xE0000000 || boss.CurrentTargetId == Core.Me.ObjectId)
            return;
        hazards[(boss.ObjectId, ActionId.Predation)] = new(boss.ObjectId, ActionId.Predation,
            boss.Location, boss.Heading, Sector(0, 8.5f, 94), now, now.AddSeconds(.25));
    }

    private IEnumerable<Hazard> ActiveHazards() => OwnsHazards()
        ? hazards.Values.Where(h => h.Expires > DateTime.UtcNow).ToArray() : Array.Empty<Hazard>();

    private static bool Contains(Hazard h, Vector3 point)
    {
        // Inverse of the polygon registration's -heading world-XZ transform.
        float dx = point.X - h.Origin.X, dz = point.Z - h.Origin.Z;
        float x = dx * (float)Math.Cos(h.Heading) - dz * (float)Math.Sin(h.Heading);
        float y = dx * (float)Math.Sin(h.Heading) + dz * (float)Math.Cos(h.Heading);
        bool inside = false;
        for (int i = 0, j = h.Points.Length - 1; i < h.Points.Length; j = i++)
        {
            var a = h.Points[i];
            var b = h.Points[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -.5f), new(width / 2, -.5f), new(width / 2, length), new(-width / 2, length)];
    private static Vector2[] Circle(float radius) => Sector(0, radius, 360);
    private static Vector2[] Sector(float inner, float outer, float degrees)
    {
        // Three-degree facets lose less than 0.023y at 65y, inside the 0.5y margin.
        int steps = (int)Math.Ceiling(degrees / 3);
        var points = new List<Vector2>();
        for (int i = 0; i <= steps; ++i)
        {
            double angle = (i * degrees / steps - degrees / 2) * Math.PI / 180;
            points.Add(new(outer * (float)Math.Sin(angle), outer * (float)Math.Cos(angle)));
        }
        if (inner == 0)
            points.Add(Vector2.Zero);
        else
        for (int i = steps; i >= 0; --i)
        {
            double angle = (i * degrees / steps - degrees / 2) * Math.PI / 180;
            points.Add(new(inner * (float)Math.Sin(angle), inner * (float)Math.Cos(angle)));
        }
        return points.ToArray();
    }
}
