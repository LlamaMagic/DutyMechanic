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

public partial class KtisisHyperboreia
{
    // Scalar snapshots outlive cast bars briefly without retaining native wrappers.
    // Native avoidance owns geometric movement; this collector issues no input.
    private sealed record Hazard(uint Caster, uint Action, Vector3 Origin, float Heading, Vector2[] Points, DateTime Finish, DateTime Expires);
    private readonly Dictionary<(uint, uint), Hazard> hazards = new();
    private DateTime nextSignalCapture;
    private readonly HashSet<uint> chargedHeads = new();
    private HashSet<uint> previousHeadStatuses = new();
    private DateTime headClearAfter;

    private void RegisterHazards()
    {
        AvoidanceManager.AddAvoidPolygon<Hazard>(
            condition: () => Live() && savedSideStepEnabled != null,
            leashPointProducer: () => Core.Me.Location, leashRadius: 60,
            rotationProducer: h => -h.Heading, scaleProducer: _ => 1,
            heightProducer: _ => 15, pointsProducer: h => h.Points,
            locationProducer: h => h.Origin, collectionProducer: ActiveHazards,
            priority: AvoidancePriority.High);
    }

    private IEnumerable<Hazard> ActiveHazards()
    {
        var live = hazards.Values.Where(h => h.Expires > DateTime.UtcNow).ToArray();
        // Quadruple announces later pairs while the first pair is still resolving.
        // Reserve only the earliest four lanes, then advance after their effects;
        // simultaneous spread/repeat shapes remain in the same native solver.
        var lanes = live.Where(h => h.Action == ActionId.QuadrupleLane).OrderBy(h => h.Finish).Take(4).ToHashSet();
        // An early breath avoid can send native escape through a live sphere cross.
        // Wait for the sphere's effect tail; the captured overlap still leaves
        // more than four seconds to escape the breath.
        var lastSphere = live.Where(h => h.Action == ActionId.PyricSphere).Select(h => h.Expires).DefaultIfEmpty(DateTime.MinValue).Max();
        return live.Where(h => (h.Action != ActionId.QuadrupleLane || lanes.Contains(h)) &&
            !(lastSphere > DateTime.UtcNow && h.Action is >= ActionId.BreathFront and <= ActionId.BreathRightRepeat &&
              h.Finish > lastSphere)).ToArray();
    }

    private void UpdateHazards(BattleCharacter[] actors, BattleCharacter[] casts, BattleCharacter boss, DateTime now)
    {
        foreach (var key in hazards.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray())
        {
            hazards.Remove(key);
        }

        if (savedSideStepEnabled == null)
        {
            return;
        }

        if (boss?.BaseId == 0x3425)
        {
            // Inhale gains announce the heads before the breath cast. Retain
            // those scalar IDs even if the native status vanishes at cast start.
            // Consumption clears the sequence, not a transient missing aura read.
            if (headClearAfter != DateTime.MinValue && now >= headClearAfter)
            {
                chargedHeads.Clear();
                headClearAfter = DateTime.MinValue;
            }

            if (boss.Auras != null && boss.Auras.IsValid)
            {
                var current = boss.Auras.Where(a => a.Id is >= 2812 and <= 2814).Select(a => a.Id).ToHashSet();
                chargedHeads.UnionWith(current.Except(previousHeadStatuses));
                previousHeadStatuses = current;
            }
        }

        var allies = actors.Where(a => !a.IsMe && a.IsVisible &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
        foreach (var caster in casts)
        {
            uint action = caster.CastingSpellId;
            var finish = now + caster.SpellCastInfo.RemainingCastTime;
            var origin = caster.Location;
            float heading = caster.Heading;
            Vector2[] points = null;
            // Action IDs identify the damage helper, not the similarly named
            // choreography cast. All damaging edges receive a 0.5y spatial margin.
            switch (action)
            {
                case ActionId.PunishingSlice:
                    // The unpadded attack is a 50-by-50 forward rectangle.
                    points = Rectangle(51, 50.5f);
                    break;
                case ActionId.IcePillar:
                    points = Circle(4.5f);
                    break;
                case ActionId.PillarPierce:
                    // The arena boundary clips the ray's full reach.
                    points = Rectangle(5, 80.5f);
                    break;
                case ActionId.PyricSphere:
                    points = Cross(5, 50.5f);
                    break;
                case ActionId.AeroIV:
                case ActionId.QuadrupleLane:
                    points = Rectangle(11, 50.5f);
                    break;
                case ActionId.AeroRepeat:
                    points = Rectangle(7, 40.5f);
                    break;
                case ActionId.AeroIIRepeat:
                    origin = GroundOrigin(caster);
                    points = Circle(6.5f);
                    break;
                case ActionId.TornadoRepeat:
                    origin = GroundOrigin(caster);
                    points = Circle(4.5f);
                    break;
                case ActionId.CosmicKiss:
                    points = Circle(10.5f);
                    break;
                case ActionId.BreathFront:
                case ActionId.BreathLeft:
                case ActionId.BreathRight:
                    points = Cone(40, 120);
                    // Front/left/right statuses 2812/2813/2814 follow the action-row
                    // order. Identify the other charged head by ID, not aura order,
                    // and retain its sector through the second exhalation 2.1s later.
                    uint firstHead = 2812 + action - ActionId.BreathFront;
                    // RB exposes body Heading, not the casting head's direction.
                    // Apply the selected head's relative angle to both sectors.
                    heading += HeadAngle(firstHead);
                    var otherHeads = chargedHeads.Where(id => id != firstHead).ToArray();
                    headClearAfter = finish.AddSeconds(3.6);
                    if (otherHeads.Length == 1)
                    {
                        float secondHeading = heading - HeadAngle(firstHead) + HeadAngle(otherHeads[0]);
                        Put(caster.ObjectId, ActionId.BreathFrontRepeat + otherHeads[0] - 2812, origin, secondHeading,
                            Cone(40, 120), finish.AddSeconds(2.1));
                    }

                    break;
            }

            if (points != null)
            {
                Put(caster.ObjectId, action, origin, heading, points, finish);
            }

            if (action is ActionId.AeroIIVisual or ActionId.AeroIISpread or ActionId.Tornado)
            {
                // Circle spreads and the tank's small buster must separate from
                // allies; a self-centered avoid would chase its own target forever.
                float radius = action == ActionId.Tornado ? 4.5f : 6.5f;
                foreach (var ally in allies)
                {
                    Put(ally.ObjectId, action, ally.Location, 0, Circle(radius), finish);
                }
            }

            if (action == ActionId.AeroBait && boss != null)
            {
                foreach (var ally in allies)
                {
                    float ray = (float)Math.Atan2(ally.Location.X - boss.Location.X, ally.Location.Z - boss.Location.Z);
                    Put(ally.ObjectId, action, boss.Location, ray, Rectangle(7, 40.5f), finish);
                }
            }
        }

        if (now >= nextSignalCapture)
        {
            // Shared cast records cannot show charged heads, surviving meteors,
            // or Hermes' cast ending without its buff. Observe Hermes as well so
            // native interrupt submission is not mistaken for server acceptance.
            nextSignalCapture = now.AddMilliseconds(250);
            LoggingHelpers.LogActorSignalChanges("Ktisis heads/meteors/interrupt", a => a.BaseId is 0x3425 or 0x348C or 0x348A);
        }
    }

    private void Put(uint caster, uint action, Vector3 origin, float heading, Vector2[] points, DateTime finish)
    {
        var key = (caster, action);
        // Retain the last geometry snapshot through damage delivery. Observed
        // delays after bar finish were 0.78s for spheres, 1.13s for breaths and
        // 0.90-0.95s for ordinary Aero IV, requiring a 1.5s tail. Queued Quadruple
        // lanes keep their shorter tail so the next safe lane remains available.
        double tail = action is ActionId.PyricSphere or ActionId.AeroIV ||
            action is >= ActionId.BreathFront and <= ActionId.BreathRightRepeat ? 1.5 : .5;
        hazards[key] = new(caster, action, origin, heading, points, finish, finish.AddSeconds(tail));
    }

    private static Vector3 GroundOrigin(BattleCharacter caster) => caster.SpellCastInfo.CastLocation != Vector3.Zero
        ? caster.SpellCastInfo.CastLocation : caster.Location;

    private static float HeadAngle(uint status) => status == 2813 ? 2 * (float)Math.PI / 3 : status == 2814 ? -2 * (float)Math.PI / 3 : 0;

    private static Vector2[] Rectangle(float width, float length) =>
        [new(-width / 2, -.5f), new(width / 2, -.5f), new(width / 2, length), new(-width / 2, length)];

    private static Vector2[] Cross(float width, float length)
    {
        float w = width / 2;
        return [new(-w, -length), new(w, -length), new(w, -w), new(length, -w),
            new(length, w), new(w, w), new(w, length), new(-w, length),
            new(-w, w), new(-length, w), new(-length, -w), new(-w, -w)];
    }

    private static Vector2[] Circle(float radius) => Enumerable.Range(0, 48)
        .Select(i => new Vector2(radius * (float)Math.Sin(i * Math.PI / 24), radius * (float)Math.Cos(i * Math.PI / 24))).ToArray();

    private static Vector2[] Cone(float radius, float degrees)
    {
        // Offset each edge of the convex sector by 0.5y, including its apex.
        // Angular padding alone becomes arbitrarily small near the boss.
        float half = degrees * (float)Math.PI / 360;
        var points = new[]
        {
            new Vector2(0, 0)
        }.Concat(Enumerable.Range(0, 33).Select(i =>
        {
            float angle = -half + 2 * half * i / 32;
            return new Vector2(radius * (float)Math.Sin(angle), radius * (float)Math.Cos(angle));
        })).ToArray();
        var padded = new Vector2[points.Length];
        for (int i = 0; i < points.Length; ++i)
        {
            var before = points[(i + points.Length - 1) % points.Length];
            var p = points[i];
            var after = points[(i + 1) % points.Length];
            float ax = p.X - before.X, ay = p.Y - before.Y;
            float bx = after.X - p.X, by = after.Y - p.Y;
            float al = (float)Math.Sqrt(ax * ax + ay * ay), bl = (float)Math.Sqrt(bx * bx + by * by);
            // The sector vertices run clockwise; left-hand normals point outward.
            float nx = -ay / al, ny = ax / al, mx = -by / bl, my = bx / bl;
            float divisor = 1 + nx * mx + ny * my;
            padded[i] = new Vector2(p.X + .5f * (nx + mx) / divisor, p.Y + .5f * (ny + my) / divisor);
        }

        return padded;
    }
}
