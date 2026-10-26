using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;
// Publish Unknown geometry through native avoidance while the combat routine keeps control of job actions.
public partial class AnamnesisAnyder
{
    private static AnamnesisAnyder activeUnknown;
    private static readonly Vector3 UnknownCenter = new(-40, 95, 290);
    private readonly Dictionary<(uint Actor, uint Action), CastRegion> unknownRegions = new();
    private readonly List<AvoidInfo> unknownAvoids = new();
    private readonly HashSet<uint> unknownOverrides = new();
    private bool unknownEntered;
    private DateTime scrutinyUntil;
    private Vector3? unknownSupportPosition;
    private bool unknownSupportMoving, unknownSupportLeased;
    private readonly CapabilityManagerHandle unknownSupportHandle = CapabilityManager.CreateNewHandle();
    private void UpdateUnknownSupport(DateTime now)
    {
        // Y'shtola moves to the Scrutiny gap before Reflection's omen becomes valid. Use that support cue only during Scrutiny.
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        foreach (var boss in actors.Where(a => a.BaseId is 0x2CD9 or 0x2CDA && a.IsCasting && a.CastingSpellId == 20005 && a.SpellCastInfo.IsValid))
        {
            scrutinyUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(3);
        }

        var support = now < scrutinyUntil ? actors.FirstOrDefault(a => a.NpcId == 8378 && a.IsAlive && a.IsVisible && a.Location.Distance2D(UnknownCenter) < 18.5f) : null;
        unknownSupportPosition = support?.Location;
        if (!unknownSupportPosition.HasValue)
        {
            ReleaseUnknownSupport();
        }
    }

    private bool PositionUnknownSupport()
    {
        CapabilityManager.Update(unknownSupportHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Unknown Scrutiny support crossing");
        unknownSupportLeased = true;
        // Scrutiny gaps require a 0.2-yalm arrival tolerance.
        if (Core.Me.Location.Distance2D(unknownSupportPosition.Value) <= .2f)
        {
            if (unknownSupportMoving)
            {
                Navigator.PlayerMover.MoveStop();
            }

            unknownSupportMoving = false;
            return false; // Healing/rotation remain available while holding the gap.
        }

        // Do not let slidecasting delay the time-sensitive support crossing.
        // Both points lie in the verified open circular arena; this remains the
        // same owner and yields to all non-Scrutiny native hazards before entry.
        if (Core.Me.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(unknownSupportPosition.Value) { DistanceTolerance = .1f, UseMount = false, MoveImmediately = Core.Me.Location.Distance2D(UnknownCenter) < 19 });
        unknownSupportMoving = true;
        return true;
    }

    private void ReleaseUnknownSupport()
    {
        if (unknownSupportMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        unknownSupportMoving = false;
        if (unknownSupportLeased)
        {
            CapabilityManager.Clear(unknownSupportHandle, CapabilityFlags.Movement, "Scrutiny ended or support unavailable");
        }

        unknownSupportLeased = false;
        unknownSupportPosition = null;
    }

    private static bool UnknownActive() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance && WorldManager.ZoneId == 898 && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat && Core.Me.Location.Distance2D(UnknownCenter) < 45 && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsAlive && a.BaseId is 0x2CD9 or 0x2CDA);
    /// <summary>Removes only Unknown's geometry and overrides before a successor hook starts.</summary>
    public static void ResetUnknownActive()
    {
        activeUnknown?.ResetUnknownGeometry();
        activeUnknown = null;
    }

    /// <summary>Releases geometry when death, loading or a stopped bot bypasses the combat hook.</summary>
    public static void CheckUnknownLifecycle()
    {
        if (!UnknownActive())
        {
            ResetUnknownActive();
        }
    }

    private void UpdateUnknownGeometry()
    {
        activeUnknown = this;
        // Scrutiny is geometric, not an ally stack. Keep generic avoidance alive
        // while this encounter owns only the casts whose native behavior failed.
        SidestepPlugin.Enabled = true;
        if (Core.Me.Location.Distance2D(UnknownCenter) < 19)
        {
            unknownEntered = true;
        }

        if (unknownAvoids.Count == 0)
        {
            RegisterUnknownGeometry();
        }

        var now = DateTime.UtcNow;
        UpdateUnknownSupport(now);
        foreach (var key in unknownRegions.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
        {
            unknownRegions.Remove(key);
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsCasting && a.SpellCastInfo.IsValid && a.Location.Distance2D(UnknownCenter) < 35 && a.BaseId is 0x2CD9 or 0x2CDA or 0x2CDB))
        {
            uint action = actor.CastingSpellId;
            if (action is not (19307 or 19308 or 19310 or 19311 or 19316 or 19317))
            {
                continue;
            }

            // Retain Clearout and Setback for 1.5 seconds after the bar to cover delayed impacts.
            float heading = actor.Heading + (action is 19308 or 19317 ? (float)Math.PI : 0);
            if (action == 19311)
            {
                // Reflection's animated direction differs from actor facing. Require its omen transform before publishing a cone.
                var matrix = actor.OmenMatrix;
                matrix.Transform(new Vector3(0, 0, 1), out var forward);
                var direction = forward - matrix.Center;
                float length = forward.Distance2D(matrix.Center);
                if (matrix.Center.Distance2D(actor.Location) > 2 || length < 35 || length > 45)
                {
                    continue;
                }

                heading = (float)Math.Atan2(direction.X, direction.Z);
            }

            unknownRegions[(actor.ObjectId, action)] = new(action, actor.Location, heading, now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5));
        }
    }

    internal static Vector2[] UnknownCone(uint action)
    {
        // Cone ranges are 9/40 yalms with 120/45-degree angles. Pad by half a yalm and two degrees; Setback faces behind the boss.
        float radius = action == 19311 ? 40.5f : 9.5f, half = action == 19311 ? 24.5f : 62;
        return new[]
        {
            new Vector2(0, -.5f)
        }.Concat(Enumerable.Range(0, 33).Select(i =>
        {
            double angle = (-half + i * half * 2 / 32) * Math.PI / 180;
            return new Vector2((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius);
        })).ToArray();
    }

    private void RegisterUnknownGeometry()
    {
        foreach (uint action in new uint[]
        {
            19307,
            19308,
            19310,
            19311,
            19316,
            19317
        }

        )
        {
            if (SideStep.Override(action))
            {
                unknownOverrides.Add(action);
            }
        }

        unknownAvoids.Add(AvoidanceManager.AddAvoidLocation(canRun: () => UnknownActive() && !unknownSupportPosition.HasValue, radiusProducer: (Vector3 _) => 8.5f, locationProducer: (Vector3 p) => p, collectionProducer: () => unknownRegions.Values.Where(r => r.Action == 19310).Select(r => r.Origin)));
        unknownAvoids.Add(AvoidanceManager.AddAvoidPolygon<CastRegion>(condition: UnknownActive, leashPointProducer: null, leashRadius: 60, rotationProducer: r => -r.Heading, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: r => UnknownCone(r.Action), locationProducer: r => r.Origin, collectionProducer: () => unknownRegions.Values.Where(r => r.Action != 19310 && (r.Action != 19311 || !unknownSupportPosition.HasValue))));
        unknownAvoids.Add(AvoidanceHelpers.AddAvoidDonut(() => UnknownActive() && unknownEntered, () => new[] { UnknownCenter }, 60, 19)); //19.5y floor with0.5y wall clearance.
    }

    private void ResetUnknownGeometry()
    {
        ReleaseUnknownSupport();
        scrutinyUntil = DateTime.MinValue;
        foreach (var avoid in unknownAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        foreach (var action in unknownOverrides)
        {
            SideStep.RemoveHandler(action);
        }

        unknownAvoids.Clear();
        unknownOverrides.Clear();
        unknownRegions.Clear();
        unknownEntered = false;
    }
}
