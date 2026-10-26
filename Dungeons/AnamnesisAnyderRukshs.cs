using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;
// Rukshs spread, stack and cross helpers have distinct target semantics. Stack and drain positioning share one movement lease.
public partial class AnamnesisAnyder
{
    private static readonly Vector3 RukshsCenter = new(0, -200, -450);
    private static AnamnesisAnyder activeRukshs;
    private readonly List<AvoidInfo> rukshsAvoids = new();
    private readonly HashSet<uint> rukshsOverrides = new();
    private readonly HashSet<uint> waterTargets = new();
    private readonly CapabilityManagerHandle fountHandle = CapabilityManager.CreateNewHandle();
    private DateTime waterUntil, fountUntil, tideUntil;
    private uint fountTarget;
    private Vector3 tideOrigin;
    private float tideHeading;
    private Vector3? fountDestination;
    private bool fountMoving, fountLeased, rukshsEnabledSideStep;
    private static bool RukshsActive() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance && WorldManager.ZoneId == 898 && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat && Core.Me.Location.Distance2D(RukshsCenter) < 50 && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsAlive && a.BaseId == 0x2CFF);
    /// <summary>Releases Rukshs-owned geometry and stack movement on stop, disable or hook replacement.</summary>
    public static void ResetRukshsActive()
    {
        activeRukshs?.ResetRukshs();
        activeRukshs = null;
    }

    /// <summary>Clears stale encounter state when death or loading bypasses the normal combat hook.</summary>
    public static void CheckRukshsLifecycle()
    {
        if (!RukshsActive())
        {
            ResetRukshsActive();
        }
    }

    private BattleCharacter[] RukshsAllies() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive && a.IsVisible && !a.IsMe && a.Location.Distance2D(RukshsCenter) < 45 && (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) || PartyMembers.AllPartyMemberIds.Contains((DutyMechanic.Data.PartyMemberId)a.NpcId))).ToArray();
    private bool RunRukshs()
    {
        activeRukshs = this;
        if (rukshsAvoids.Count == 0)
        {
            RegisterRukshs();
        }

        var now = DateTime.UtcNow;
        if (now >= waterUntil)
        {
            waterTargets.Clear();
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsCasting && a.SpellCastInfo.IsValid && a.NpcId == 9264 && a.Location.Distance2D(RukshsCenter) < 55))
        {
            // Retain scalar geometry for 1.5 seconds after the bar. Rukshs-specific effect timing remains unverified.
            var until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
            if (actor.CastingSpellId == 19326)
            {
                waterTargets.Add(actor.SpellCastInfo.TargetId);
                waterUntil = until;
            }
            else if (actor.CastingSpellId == 19328)
            {
                if (fountTarget != actor.SpellCastInfo.TargetId)
                {
                    fountDestination = null;
                }

                fountTarget = actor.SpellCastInfo.TargetId;
                fountUntil = until;
            }
            else if (actor.BaseId == 0x2CFF && actor.CastingSpellId == 19339)
            {
                tideOrigin = actor.Location;
                tideHeading = actor.Heading;
                tideUntil = until;
            }
        }

        UpdateRukshsWaves();
        UpdateNarrowSupport();
        // Spread/cross escape has precedence over a stack approach. Clearing this
        // owner cannot stop native escape or delete another mechanic's geometry.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            fountMoving = false;
            return false;
        }

        // Support begins crossing before the hand disappears. Share the stack/drain mover and retain floor avoidance priority.
        if (narrowSupportPosition.HasValue)
        {
            return PositionRukshs(narrowSupportPosition.Value, "Rukshs narrow waves: Duty Support crossing");
        }

        if (now >= fountUntil)
        {
            fountDestination = null;
            fountTarget = 0;
            var drain = SelectRukshsDrain();
            if (drain == null)
            {
                ReleaseFount();
                return false;
            }

            return PositionRukshs(drain.Value, "Rukshs active drain");
        }

        var allies = RukshsAllies();
        var target = allies.FirstOrDefault(a => a.ObjectId == fountTarget);
        if (allies.Length == 0 || (fountTarget != Core.Me.ObjectId && target == null))
        {
            ReleaseFount();
            return false;
        }

        bool Safe(Vector3 p) => RukshsFloorSafe(p, rukshsFloor) && !PendingRukshsWaves().Any(w => InRukshsWave(p, w.Origin, w.Heading, w.Wide)) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        fountDestination = SelectFount(Core.Me.Location, allies.Select(a => a.Location).ToArray(), target?.Location, fountDestination, Safe);
        if (fountDestination == null)
        {
            ReleaseFount();
            return false;
        }

        return PositionRukshs(fountDestination.Value, "Flying Fount target stack");
    }

    // Both positive requirements use this owner: switching from a grate to the
    // actual stack target must replace its request, never leave two active movers.
    private bool PositionRukshs(Vector3 destination, string reason)
    {
        CapabilityManager.Update(fountHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        fountLeased = true;
        bool narrowCrossing = narrowSupportPosition.HasValue && rukshsFloor == 1;
        if (Core.Me.Location.Distance2D(destination) <= (narrowCrossing ? .2f : .75f))
        {
            StopFountMovement();
            return false;
        } // Holding must leave rotation/healing schedulable.

        // At 05:08:48 Stone delayed the support crossing by a full second and
        // left a lethal six-yalm trail. Required movement must interrupt that cast.
        // The narrow rectangle is convex, flat and unobstructed; both endpoints
        // are floor-validated, so its moving support target needs no mesh request.
        if (narrowCrossing && Core.Me.IsCasting)
        {
            ActionManager.StopCasting();
        }

        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = narrowCrossing ? .1f : .5f, UseMount = false, MoveImmediately = narrowCrossing && RukshsFloorSafe(Core.Me.Location, 1) });
        fountMoving = true;
        return true;
    }

    private void RegisterRukshs()
    {
        foreach (uint action in new uint[]
        {
            19325,
            19326,
            19327,
            19328,
            19332,
            19339
        }

        )
        {
            if (SideStep.Override(action))
            {
                rukshsOverrides.Add(action);
            }
        }

        // Keep SideStep for verified ordinary cones; exclude the setup cue and
        // target semantics above. Never erase global avoids to begin a spread.
        if (!SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = true;
            rukshsEnabledSideStep = true;
        }

        rukshsAvoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => RukshsActive() && DateTime.UtcNow < waterUntil, objectSelector: a => a.IsValid && a.IsAlive && !a.IsMe && RukshsAllies().Any(p => p.ObjectId == a.ObjectId) && (waterTargets.Contains(Core.Me.ObjectId) || waterTargets.Contains(a.ObjectId)), radiusProducer: _ => 8.5f)); // Installed/reference8y spread plus0.5y clearance.
        rukshsAvoids.Add(AvoidanceManager.AddAvoidPolygon<Vector3>(condition: () => RukshsActive() && DateTime.UtcNow < tideUntil, leashPointProducer: null, leashRadius: 60, rotationProducer: _ => -tideHeading, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: _ => RisingTidePolygon, locationProducer: p => p, collectionProducer: () => new[] { tideOrigin }));
        RegisterRukshsWaves();
    }

    // Installed raw type11 and the00:25:37 boss cast confirm a50y/6y cross,
    // rather than the old stack interpretation. One polygon includes0.5y on
    // every edge, avoiding two competing geometric registrations at its center.
    internal static readonly Vector2[] RisingTidePolygon =
    {
        new(-3.5f, -50.5f),
        new(3.5f, -50.5f),
        new(3.5f, -3.5f),
        new(50.5f, -3.5f),
        new(50.5f, 3.5f),
        new(3.5f, 3.5f),
        new(3.5f, 50.5f),
        new(-3.5f, 50.5f),
        new(-3.5f, 3.5f),
        new(-50.5f, 3.5f),
        new(-50.5f, -3.5f),
        new(-3.5f, -3.5f)
    };
    // Prefer an existing safe stack point; an unrelated nearer party member must
    // not pull the player outside the actual6y target effect. Native hazards reject
    // candidates before distance scoring. The party target supplies the position.
    internal static Vector3? SelectFount(Vector3 player, Vector3[] allies, Vector3? target, Vector3? previous, Func<Vector3, bool> safe)
    {
        bool Valid(Vector3 p) => safe(p) && (target == null || p.Distance2D(target.Value) <= 4.5f) && allies.Any(a => a.Distance2D(p) <= 5);
        if (previous != null && Valid(previous.Value))
        {
            return previous;
        }

        var candidates = new List<Vector3>
        {
            player
        };
        foreach (var anchor in target == null ? allies : new[]
        {
            target.Value
        }

        )
        {
            candidates.Add(anchor);
            for (int i = 0; i < 16; i++)
            {
                candidates.Add(anchor + new Vector3((float)Math.Sin(i * Math.PI / 8) * 2, 0, (float)Math.Cos(i * Math.PI / 8) * 2));
            }
        }

        return candidates.Where(Valid).OrderByDescending(p => allies.Count(a => a.Distance2D(p) <= 5)).ThenBy(p => p.Distance2D(player)).Select(p => (Vector3? )p).FirstOrDefault();
    }

    private void StopFountMovement()
    {
        if (fountMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        fountMoving = false;
    }

    private void ReleaseFount()
    {
        StopFountMovement();
        if (fountLeased)
        {
            CapabilityManager.Clear(fountHandle, CapabilityFlags.Movement, "Flying Fount ended or interrupted");
        }

        fountLeased = false;
        fountTarget = 0;
        fountDestination = null;
        fountUntil = DateTime.MinValue;
    }

    private void ResetRukshs()
    {
        ReleaseFount();
        ResetRukshsWaves();
        foreach (var avoid in rukshsAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        foreach (var action in rukshsOverrides)
        {
            SideStep.RemoveHandler(action);
        }

        rukshsAvoids.Clear();
        rukshsOverrides.Clear();
        waterTargets.Clear();
        // Restore only the state this encounter changed; leave an already-enabled
        // SideStep alone when normal route/combat ownership resumes.
        if (rukshsEnabledSideStep)
        {
            SidestepPlugin.Enabled = false;
        }

        rukshsEnabledSideStep = false;
        waterUntil = tideUntil = DateTime.MinValue;
    }
}
