using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Directors;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

// Shared hazards and Nixie/Mother Porxie phase responses use the movement owner
// defined in MatoyasRelict.cs. Route and required-kill decisions stay in the profile.
public partial class MatoyasRelict
{
    private int bossKind;
    private bool sideStepLease, cloudPhase, cloudCompleted, cloudSeen;
    private DateTime barbequeUntil, puffUntil, puffInputHoldFrom;
    private float puffHeading;
    private bool barbequeSteering;
    private DateTime nextBarbequeTrace;
    private float barbequeGoalZ;
    private readonly List<AvoidInfo> bossAvoids = new();
    private readonly Dictionary<string, Hazard> hazards = new();
    private readonly Dictionary<uint, uint> pipeStates = new();
    private readonly Dictionary<uint, uint> previousActions = new();
    private readonly Dictionary<uint, DateTime> flameMarkers = new();
    private readonly HashSet<uint> visibleFlameMarkers = new();
    private static readonly Vector3 NixieCenter = new(0, 150, -150);
    private static readonly Vector3 PorxieCenter = new(0, 0, 0);

    // A stable object is retained while a cast refreshes: RB keys its escape path
    // by object identity. Timestamps must not manufacture a new avoid each frame.
    private sealed class Hazard
    {
        internal Vector3 Position;
        internal float Heading, Radius, Length, HalfWidth;
        internal DateTime Until, From;
        internal uint Action;
    }

    // Native MapEffects uses placed-instance IDs, not packet indices 13..1A.
    // These eight pipe instances were resolved from the attached Global bg.lgb.
    private static readonly Dictionary<uint, Vector3> Pipes = new()
    {
        [8529007] = new(-20, 150, -165),
        [8529006] = new(-20, 150, -155),
        [8527362] = new(-20, 150, -145),
        [8529008] = new(-20, 150, -135),
        [8529012] = new(20, 150, -165),
        [8529011] = new(20, 150, -155),
        [8529009] = new(20, 150, -145),
        [8529010] = new(20, 150, -135)
    };

    private bool BossActive() => Live() && bossKind != 0 && Core.Me.InCombat;
    private bool BarbequeActive() => bossKind == 3 && DateTime.UtcNow < barbequeUntil;
    private IEnumerable<Hazard> CurrentHazards() => hazards.Values.Where(h => h.From <= DateTime.UtcNow && h.Until > DateTime.UtcNow);

    private Hazard Put(string key, uint action, Vector3 position, DateTime until)
    {
        if (!hazards.TryGetValue(key, out var h))
        {
            hazards[key] = h = new();
        }

        h.Position = position;
        h.Action = action;
        h.Until = until;
        return h;
    }

    private void RegisterBossAvoids()
    {
        if (bossAvoids.Count != 0)
        {
            return;
        }
        // Barbeque's combined planner owns these same shapes during suction.
        // Keep the registrations, but let their native escape finish before the
        // positioning owner takes input; never run two movers for these hazards.
        bossAvoids.Add(AvoidanceManager.AddAvoidLocation(canRun: () => BossActive() && !BarbequeActive(),
            radiusProducer: (Hazard h) => h.Radius, locationProducer: (Hazard h) => h.Position,
            collectionProducer: () => CurrentHazards().Where(h => h.Radius > 0)));
        bossAvoids.Add(AvoidanceManager.AddAvoidPolygon<Hazard>(() => BossActive() && !BarbequeActive(), null, 90,
            h => -h.Heading, _ => 1, _ => 6, h => ForwardRectangle(h.Length, h.HalfWidth),
            h => h.Position, () => CurrentHazards().Where(h => h.Length > 0)));
        bossAvoids.Add(AvoidanceHelpers.AddAvoidDonut(() => BossActive() && bossKind == 1,
            () => new[] { MudmanCenter }, 60, 19)); // 19.5y room with 0.5y wall inset.
        bossAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(
            () => BossActive() && bossKind == 2 && !cloudPhase && !Core.Me.HasAura(2472),
            38, 38, 120, 120, () => new[] { NixieCenter }, AvoidancePriority.High));
        bossAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(
            () => BossActive() && bossKind == 2 && Core.Me.HasAura(2472),
            18, 10, 120, 120, () => new[] { new Vector3(0, Core.Me.Y, -175) }, AvoidancePriority.High));
        bossAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => BossActive() && bossKind == 3 && !BarbequeActive(),
            38, 38, 120, 120, () => new[] { PorxieCenter }, AvoidancePriority.High));
        // Delay the donut exclusion until after its earlier knockback. Applying
        // both at once would forbid the required pre-knockback staging position.
        bossAvoids.Add(AvoidanceHelpers.AddAvoidDonut(() => BossActive() && bossKind == 3 && !BarbequeActive(),
            () => CurrentHazards().Where(h => h.Action is 22814 or 22815 or 20045)
                .Where(_ => DateTime.UtcNow > puffUntil).Select(h => h.Position).ToArray(), 40.5, 4.5));
        Logger.Information("[Matoya] Boss{0} owns native hazards and semantic positioning; SideStep suspended for this encounter.", bossKind);
    }

    private void UpdateBossHazards(BattleCharacter[] actors)
    {
        RegisterBossAvoids();
        var now = DateTime.UtcNow;
        foreach (var key in hazards.Where(p => p.Value.Until <= now).Select(p => p.Key).ToArray())
        {
            hazards.Remove(key);
        }

        foreach (var a in actors.Where(a => a.IsVisible && a.Location.Distance2D(Core.Me.Location) < 90))
        {
            uint id = a.IsCasting && a.SpellCastInfo.IsValid ? a.CastingSpellId : 0;
            previousActions.TryGetValue(a.ObjectId, out uint prior);
            previousActions[a.ObjectId] = id;
            if (id == 0)
            {
                continue;
            }

            DateTime finish = now + a.SpellCastInfo.RemainingCastTime;
            string key = $"cast:{a.ObjectId}:{id}";
            Vector3 ground = a.SpellCastInfo.CastLocation;
            // Ground-authored spells supply a native location. An absent location
            // is logged by diagnostics; it is not replaced with the caster's feet.
            if (id is 21633 or 22808 or 22806 && ground != Vector3.Zero)
            {
                var h = Put(key, id, ground, finish.AddSeconds(.6));
                h.Radius = id == 21633 ? 6.5f : id == 22808 ? 5.5f : 30.5f;
            }
            if (id is 22807 or 22822)
            {
                var h = Put(key, id, a.Location, finish.AddSeconds(.6));
                h.Heading = a.Heading;
                h.Length = id == 22807 ? 50.5f : 40.5f;
                h.HalfWidth = id == 22807 ? 3 : 3.5f;
            }
            if (id == 22931 && a.SpellCastInfo.TargetId != Core.Me.ObjectId)
            {
                var target = actors.FirstOrDefault(p => p.ObjectId == a.SpellCastInfo.TargetId && p.IsAlive);
                if (target != null)
                {
                    Put(key, id, target.Location, finish.AddSeconds(.6)).Radius = 6.5f;
                }
            }
            if (id is 22814 or 22815 or 20045)
            {
                // The captured Medium Rear hit arrived 1.2s after cast completion.
                Put(key, id, a.Location, finish.AddSeconds(1.6));
            }

            if (id == 22809)
            {
                // The captured knockback still displaced the player 1.6s after
                // cast finish. Early donut escape moved before that displacement
                // and carried the landing outside the refuge.
                puffInputHoldFrom = finish.AddSeconds(.8);
                puffUntil = finish.AddSeconds(2.2);
                puffHeading = a.Heading;
            }
            // The sprite explosion is the precursor to the instant second puff.
            // Keep its 10.9s delay tied to cast completion, never to handler startup.
            if (id == 20020)
            {
                puffInputHoldFrom = finish.AddSeconds(10.9);
                puffUntil = finish.AddSeconds(12.3);
            }
            if (id == 22810)
            {
                puffHeading = a.Heading;
            }
            // The suction persists past this choreography cast. A later ordinary
            // boss cast ends the lease; the 30s ceiling only protects missed state
            // after an interrupted cast and is not treated as an observed impact.
            if (id == 23331)
            {
                barbequeUntil = finish.AddSeconds(30);
            }
            if (bossKind == 3 && a.NpcId == 9741 && id != prior && id != 23331 &&
                id is 22801 or 22803 or 22806 or 22809 or 22810 or 22818)
            {
                barbequeUntil = DateTime.MinValue;
            }

            if (id == 22920)
            {
                cloudPhase = true;
            }
        }
        // Instant actions are not reliably exposed by IsCasting. Tethers describe
        // the actual intended charge victim for both bubbles and icicles.
        foreach (var a in actors.Where(a => a.IsAlive && a.IsVisible && a.VfxContainer.IsValid &&
                     a.BaseId is 0x300E or 0x3009 or 0x3081))
        {
            uint tetherId = a.BaseId == 0x3081 ? 8u : 7u;
            foreach (var t in a.VfxContainer.Tethers.Where(t => t.Id == tetherId && t.TargetId != Core.Me.ObjectId))
            {
                var target = actors.FirstOrDefault(p => p.ObjectId == t.TargetId && p.IsAlive);
                if (target == null)
                {
                    continue;
                }

                var h = Put($"tether:{a.ObjectId}", tetherId, a.Location, now.AddSeconds(.25));
                h.Heading = HeadingTo(a.Location, target.Location);
                h.Length = tetherId == 8 ? 80.5f : 60.5f;
                h.HalfWidth = tetherId == 8 ? 2 : Math.Max(2, a.CombatReach) + .5f;
            }
        }
        if (bossKind == 2)
        {
            UpdateNixie(actors, now);
        }

        if (bossKind == 3)
        {
            UpdateFlames(actors, now);
        }
    }

    private void UpdateNixie(BattleCharacter[] actors, DateTime now)
    {
        if (DirectorManager.ActiveDirector is InstanceContentDirector d && d.IsValid)
        {
            foreach (var e in d.MapEffects.Where(e => Pipes.ContainsKey(e.ID)))
            {
                pipeStates.TryGetValue(e.ID, out uint previous);
                pipeStates[e.ID] = e.State;
                // Native map state 1 is the active low word. Exact Nixie transitions
                // remain capture targets; do not treat unknown states as active.
                if (e.State != 1 || previous == 1)
                {
                    continue;
                }

                var p = Pipes[e.ID];
                var h = Put($"pipe:{e.ID}", 22930, p, now.AddSeconds(10));
                h.Length = 60.5f;
                h.HalfWidth = 5.5f;
                h.Heading = p.X < 0 ? (float)Math.PI / 2 : -(float)Math.PI / 2;
                Logger.Information("[Matoya] Pipe {0:X} state1 laneZ={1}; nine-second activation candidate.", e.ID, p.Z);
            }
        }
        // Native captures contain many untargetable helpers sharing NpcId 9738.
        // Only base 0x307F is the boss whose return ends the cloud phase.
        var boss = actors.FirstOrDefault(a => a.BaseId == 0x307F && a.IsAlive);
        if (actors.Any(a => a.NpcId == 9739 && a.IsAlive && a.IsTargetable))
        {
            cloudPhase = true;
            cloudSeen = true;
        }
        if (Core.Me.HasAura(2472))
        {
            cloudPhase = true;
            cloudSeen = true;
        }
        if (cloudSeen && boss != null && boss.IsTargetable && !Core.Me.HasAura(2472))
        {
            cloudCompleted = true;
            cloudPhase = false;
        }
        // During the first cloud transition a geyser is required shelter. Only the
        // later, completed-cloud phase treats new geysers as damaging circles.
        if (cloudCompleted)
        {
            foreach (var g in GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible &&
                         o.BaseId == 0x1EB0C7 && o.Location.Distance2D(NixieCenter) < 30))
            {
                Put($"geyser:{g.ObjectId}", 22926, g.Location, now.AddSeconds(.25)).Radius = 6.5f;
            }
        }
    }

    private bool RunNixie(BattleCharacter[] actors)
    {
        if (cloudPhase && !cloudCompleted && !Core.Me.HasAura(2472))
        {
            var geyser = GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible &&
                o.BaseId == 0x1EB0C7 && o.Location.Distance2D(NixieCenter) < 30)
                .OrderBy(o => o.Z).FirstOrDefault();
            if (geyser != null)
            {
                return PositionAt(geyser.Location, "Matoya cloud-launch geyser");
            }
        }
        // Tether 8's victim should point the icicle line away from allies. Keep the
        // same owner as geyser positioning; required cloud entry takes precedence.
        var icicle = actors.FirstOrDefault(a => a.BaseId == 0x3081 && a.IsAlive && a.IsVisible && a.VfxContainer.IsValid &&
            a.VfxContainer.Tethers.Any(t => t.Id == 8 && t.TargetId == Core.Me.ObjectId));
        if (icicle != null && !Core.Me.HasAura(2472))
        {
            var peers = actors.Where(a => a.IsAlive && !a.IsMe && a.IsVisible && IsAlly(a)).ToArray();
            var goal = ArenaGrid(NixieCenter).Where(SafePoint)
                .Where(p => peers.All(a => !InForwardLane(a.Location, icicle.Location, p, 2)))
                .OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
            if (goal.HasValue)
            {
                return PositionAt(goal.Value, "Matoya icicle bait away from party");
            }
        }
        ReleaseMovement();
        return false;
    }

    private void UpdateFlames(BattleCharacter[] actors, DateTime now)
    {
        var seen = new HashSet<uint>();
        foreach (var a in actors.Where(a => a.IsAlive && a.IsVisible && IsAlly(a) && a.VfxContainer.IsValid &&
                     a.VfxContainer.LockOns.Any(i => i != null && i.IsValid && i.Id == 169)))
        {
            seen.Add(a.ObjectId);
            if (!visibleFlameMarkers.Contains(a.ObjectId))
            {
                flameMarkers[a.ObjectId] = now.AddSeconds(7.2);
            }
        }
        visibleFlameMarkers.Clear();
        visibleFlameMarkers.UnionWith(seen);
        foreach (var key in flameMarkers.Where(p => now >= p.Value).Select(p => p.Key).ToArray())
        {
            flameMarkers.Remove(key);
        }

        foreach (var a in actors.Where(a => !a.IsMe && a.IsAlive && flameMarkers.ContainsKey(a.ObjectId)))
        {
            Put($"flame:{a.ObjectId}", 22819, a.Location, flameMarkers[a.ObjectId]).Radius = 5.5f;
        }
    }

    private async System.Threading.Tasks.Task<bool> RunPorxie(BattleCharacter[] actors)
    {
        var now = DateTime.UtcNow;
        if (barbequeSteering && !BarbequeActive())
        {
            ReleaseMovement();
            barbequeSteering = false;
            Logger.Information("[Matoya] Barbeque continuous movement released; native encounter movement restored.");
        }
        var donut = hazards.Values.Where(h => h.Action is 22814 or 22815 or 20045 && h.Until > now)
            .OrderBy(h => h.Until).FirstOrDefault();
        // Suction can retain an untargetable boss Kill POI.
        // Clear only that ineligible target so normal healing remains runnable
        // without OrderBot chasing across the arena. Normal targeting reacquires it.
        if (now < barbequeUntil && actors.Any(a => a.BaseId == 0x300B && a.IsAlive && !a.IsTargetable))
        {
            if (Poi.Current?.Unit?.BaseId == 0x300B)
            {
                Poi.Clear("Matoya Barbeque boss is untargetable");
            }

            if (Core.Me.CurrentTarget?.BaseId == 0x300B)
            {
                Core.Me.ClearTarget();
            }
        }
        if (donut != null && now < puffUntil)
        {
            // The 15y forward puff resolves before the donut. Score the landing,
            // respecting stop-at-wall behavior, instead of running into the donut
            // center before knockback and being ejected from its safe interior.
            if (now >= puffInputHoldFrom)
            {
                HoldScriptedDisplacement("Matoya Huff and Puff displacement");
                return false;
            }
            var goal = ArenaGrid(PorxieCenter).Append(Core.Me.Location).Where(SafePoint)
                .Where(p => KnockbackLanding(p, puffHeading).Distance2D(donut.Position) <= 4)
                // Center the landing rather than minimizing travel to the edge:
                // navigation tolerance consumed the previous 4y refuge margin.
                .OrderBy(p => KnockbackLanding(p, puffHeading).Distance2D(donut.Position))
                .ThenBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
            if (goal.HasValue)
            {
                return PositionAt(goal.Value, "Matoya puff into Medium Rear refuge");
            }

            ReleaseMovement();
            return false;
        }
        if (now < barbequeUntil)
        {
            if (!SteerBarbeque())
            {
                return true;
            }
            // Preserve the established routine bridge while consuming the route
            // tick. Recheck after awaiting: stop/death/phase changes revoke input.
            await ff14bot.Behavior.CommonTasks.ExecuteCoroutine(ff14bot.Behavior.BrainBehavior.CombatLogic);
            if (!Live())
            {
                Reset();
                return false;
            }
            if (BarbequeActive())
            {
                SteerBarbeque();
                return true;
            }
            ReleaseMovement();
            barbequeSteering = false;
            return false;
        }
        ReleaseMovement();
        return false;
    }

    private static bool IsAlly(BattleCharacter a) => a.IsMe || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
        PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId);
    private static bool SafePoint(Vector3 p) => !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));

    // Native point escapes can stop forward travel during suction, allowing a
    // debris knockback to reach the lethal west edge. Sustain forward input with
    // this handler's existing mover only in the verified arena and suction phase.
    private bool SteerBarbeque()
    {
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moving = false;
            ReleaseMovement();
            return false; // Native escape relinquishes input before this owner acquires it.
        }
        if (!barbequeSteering)
        {
            ff14bot.Navigation.Navigator.Clear();
            barbequeGoalZ = Core.Me.Z;
            barbequeSteering = true;
        }
        // RB940 Update accepts exactly one capability; Clear accepts a mask.
        // Renew both on this owner so phase cleanup releases movement and facing.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement,
            TimeSpan.FromSeconds(1), "Matoya Barbeque continuous forward and lateral movement");
        leased = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Facing,
            TimeSpan.FromSeconds(1), "Matoya Barbeque preserve movement direction");
        var now = DateTime.UtcNow;
        barbequeGoalZ = ChooseBarbequeLane(Core.Me.Location, barbequeGoalZ, hazards.Values.ToArray(), now);
        var input = BarbequeInput(Core.Me.Location, barbequeGoalZ);
        ff14bot.Navigation.Navigator.PlayerMover.MoveTowards(input);
        moving = true;
        if (now >= nextBarbequeTrace)
        {
            Logger.Information("[Matoya] Barbeque steer position={0} laneZ={1:F1} input={2}", Core.Me.Location, barbequeGoalZ, input);
            nextBarbequeTrace = now.AddSeconds(1);
        }
        return true;
    }

    // A six-yalm forward vector with at most three lateral keeps at least 89% of
    // run speed opposing suction. The endpoint is a direction, not a navmesh
    // destination: keep pressing against the observed east wall without arriving
    // and stopping. Lateral goals remain one yalm inside the existing 19y boundary.
    internal static Vector3 BarbequeInput(Vector3 player, float laneZ) =>
        new(player.X + 6, player.Y, player.Z + Math.Clamp(Math.Clamp(laneZ, -18, 18) - player.Z, -3, 3));

    private static float ChooseBarbequeLane(Vector3 player, float previous, Hazard[] source, DateTime now)
    {
        var live = source.Where(h => h.From <= now && h.Until > now && h.Action is 22807 or 22808).ToArray();
        if (live.Length == 0)
        {
            return Math.Clamp(player.Z, -18, 18);
        }
        // Until includes the existing 0.6s impact allowance. Resolve the earliest
        // wave first; overlapping rectangles knock back 15y and take precedence
        // over a circle if no shared safe lane exists. Keep an equally safe prior
        // lane to avoid alternating directions as the next wave is discovered.
        var first = live.Min(h => h.Until).AddSeconds(.3);
        return Enumerable.Range(0, 73).Select(i => -18 + i * .5f)
            .OrderBy(z => live.Count(h => h.Until <= first && h.Action == 22807 && BarbequeLaneHit(z, h)))
            .ThenBy(z => live.Count(h => h.Until <= first && h.Action == 22808 && BarbequeLaneHit(z, h)))
            .ThenBy(z => live.Count(h => h.Until > first && h.Action == 22807 && BarbequeLaneHit(z, h)))
            .ThenBy(z => live.Count(h => h.Until > first && h.Action == 22808 && BarbequeLaneHit(z, h)))
            .ThenBy(z => Math.Abs(z - previous) < .26f ? 0 : 1)
            .ThenBy(z => Math.Abs(z - player.Z)).First();
    }

    private static bool BarbequeLaneHit(float z, Hazard h)
    {
        float dx = 18 - h.Position.X, dz = z - h.Position.Z;
        if (h.Action == 22808)
        {
            return dx * dx + dz * dz <= h.Radius * h.Radius;
        }

        float along = dx * (float)Math.Sin(h.Heading) + dz * (float)Math.Cos(h.Heading);
        float across = dx * (float)Math.Cos(h.Heading) - dz * (float)Math.Sin(h.Heading);
        return along >= -.5f && along <= h.Length && Math.Abs(across) <= h.HalfWidth;
    }
    private static float HeadingTo(Vector3 start, Vector3 end) => (float)Math.Atan2(end.X - start.X, end.Z - start.Z);
    private static IEnumerable<Vector3> ArenaGrid(Vector3 center) =>
        Enumerable.Range(-18, 37).SelectMany(x => Enumerable.Range(-18, 37).Select(z => center + new Vector3(x, 0, z)));

    // Forward geometry uses FFXIV's X/Z plane: heading 0 points +Z. Native polygon
    // rotation receives the negative heading, matching the shared avoidance API.
    internal static Vector2[] ForwardRectangle(float length, float halfWidth) => new[]
        { new Vector2(-halfWidth,-.5f),new Vector2(halfWidth,-.5f),new Vector2(halfWidth,length),new Vector2(-halfWidth,length) };
    internal static Vector3 KnockbackLanding(Vector3 start, float heading)
    {
        float dx = 15 * (float)Math.Sin(heading), dz = 15 * (float)Math.Cos(heading), fraction = 1;
        // Clip along the ray at the first wall; independently clamping X/Z would
        // slide a diagonal knockback around a corner and predict a false refuge.
        if (Math.Abs(dx) > .001f)
        {
            fraction = Math.Min(fraction, ((dx > 0 ? 19 : -19) - start.X) / dx);
        }

        if (Math.Abs(dz) > .001f)
        {
            fraction = Math.Min(fraction, ((dz > 0 ? 19 : -19) - start.Z) / dz);
        }

        fraction = Math.Max(0, fraction);
        return new Vector3(start.X + dx * fraction, start.Y, start.Z + dz * fraction);
    }
    internal static bool InForwardLane(Vector3 point, Vector3 start, Vector3 toward, float halfWidth)
    {
        float heading = HeadingTo(start, toward), dx = point.X - start.X, dz = point.Z - start.Z;
        float along = dx * (float)Math.Sin(heading) + dz * (float)Math.Cos(heading);
        float across = dx * (float)Math.Cos(heading) - dz * (float)Math.Sin(heading);
        return along >= 0 && along <= 80 && Math.Abs(across) <= halfWidth;
    }

    private void ResetBossHazards()
    {
        foreach (var avoid in bossAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        bossAvoids.Clear();
        hazards.Clear();
        pipeStates.Clear();
        previousActions.Clear();
        flameMarkers.Clear();
        visibleFlameMarkers.Clear();
        cloudPhase = cloudCompleted = cloudSeen = false;
        puffUntil = puffInputHoldFrom = barbequeUntil = DateTime.MinValue;
        barbequeSteering = false;
        nextBarbequeTrace = DateTime.MinValue;
        barbequeGoalZ = 0;
        bossKind = 0;
        if (sideStepLease && SidestepPlugin != null)
        {
            SidestepPlugin.Enabled = true;
        }

        sideStepLease = false;
    }
}
