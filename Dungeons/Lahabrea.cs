using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using P = DutyMechanic.Dungeons.LahabreaPlanner;
using Point = System.Numerics.Vector2;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Quest 70058's solo Lahabrea fight. One damage-stage planner owns overlapping geometry;
/// only the actual knockback helper borrows movement, leaving rotation and adds to OrderBot.
/// </summary>
public sealed class Lahabrea : AbstractDungeon, IDisposable
{
    private static Lahabrea activeEncounter;
    /// <summary>Restores encounter state when the plugin is disabled or its hooks are replaced.</summary>
    public static void ResetActive()
    {
        activeEncounter?.Dispose();
        activeEncounter = null;
    }

    // Base IDs distinguish boss and helpers sharing name row 2143. The solo arena is territory 1052; 1048 belongs to the trial.
    private const uint BossBase = 0x3933;
    private const uint SphereBase = 0x3934;
    private const uint PitchEvent = 0x1EB738;
    private static readonly Vector3 Arena = new(-704, -185.66f, 480);
    private readonly Dictionary<(uint Owner, uint Action), P.Hazard> casts = [];
    private P.Plan plan = new([], null, null, "idle");
    private P.Push push;
    private uint pushOwner;
    private double predictedBurst;
    private double nextPlan;
    private double nextWarning;
    private bool active;
    private bool wall;
    private bool moved;
    private bool leased;
    private bool suppressed;
    private bool sideStepWasEnabled;
    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)1052;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        activeEncounter = this;
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        DisableSideStep();
        AvoidanceHelpers.AddAvoidDonut(() => active && wall, () => Arena, 90, P.Radius);
        AvoidanceManager.AddAvoidPolygon<P.Hazard>(condition: () => active, leashPointProducer: () => Arena, leashRadius: 30, rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 15, pointsProducer: h => h.Polygon().Select(p => new Vector2(p.X - Arena.X, p.Y - Arena.Z)).ToArray(), locationProducer: _ => Arena, collectionProducer: () => plan.Hazards, priority: AvoidancePriority.High);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseMovement(); // This exit callback runs on the bot thread.
        Dispose();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (Core.Player == null || WorldManager.ZoneId != (uint)ZoneId)
        {
            Reset();
            RestoreSideStep();
            return false;
        }

        // Cutscene must precede native encounter reads. Dark Meltdown deliberately kills and
        // resurrects the player; do not leave the duty, stop the bot, or change its quest state.
        if (CommonBehaviors.IsLoading || QuestLogManager.InCutscene || !Core.Player.IsAlive)
        {
            Reset();
            return false;
        }

        DisableSideStep();
        BattleCharacter[] actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive && a.Location.Distance2D(Arena) < 65).ToArray();
        if (!actors.Any(a => a.BaseId == BossBase) || !Core.Player.InCombat)
        {
            Reset();
            return false;
        }

        active = true;
        double now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (now >= nextPlan)
        {
            nextPlan = now + 0.1; // Bound scalar candidate work without delaying a cast-sized response.
            Snapshot(actors, now);
            List<P.Hazard> hazards = casts.Values.ToList();
            foreach (var e in GameObjectManager.GetObjectsOfType<EventObject>())
            {
                if (e.IsValid && e.IsVisible && e.NpcId == PitchEvent && e.Location.Distance2D(Arena) < 30)
                {
                    hazards.Add(new(e.ObjectId, 0, Xz(e.Location), 0, double.PositiveInfinity, P.Shape.Circle, 4 + P.Margin, Persistent: true));
                }
            }

            // Firespheres are dangerous before their short cast begins. The summon-to-effect
            // interval is 12s after summon completion; the real helper cast supersedes prediction.
            if (predictedBurst > now)
            {
                foreach (var sphere in actors.Where(a => a.BaseId == SphereBase && a.IsVisible))
                {
                    if (!casts.ContainsKey((sphere.ObjectId, P.Burst)))
                    {
                        hazards.Add(P.Cast(sphere.ObjectId, P.Burst, Xz(sphere.Location), 0, predictedBurst));
                    }
                }
            }

            plan = P.Build(Xz(Core.Player.Location), hazards.ToArray(), push, plan.Destination);
        }

        if (plan.Push == null || !plan.Destination.HasValue)
        {
            ReleaseMovement();
            if (plan.Push != null && now >= nextWarning)
            {
                nextWarning = now + 3;
                Logger.Warning("[Lahabrea] No safe knockback position; yielding to avoidance.");
            }

            return false;
        }

        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moved = false; // RB owns this mover now; cleanup must not stop its escape.
            ReleaseMovement();
            return false;
        }

        Point destination = plan.Destination.Value;
        Vector3 target = new(destination.X, Core.Player.Y, destination.Y);
        // Recheck the published segment against all registered avoids, including external owners.
        // A route blocked by unrelated geometry must never be forced through by this semantic goal.
        int samples = Math.Max(1, (int)Math.Ceiling(Core.Player.Location.Distance2D(target) / 0.2));
        for (int i = 1; i <= samples; ++i)
        {
            Vector3 sample = Core.Player.Location + (target - Core.Player.Location) * ((float)i / samples);
            if (AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(sample)))
            {
                ReleaseMovement();
                return false;
            }
        }

        if (!leased)
        {
            moved = true; // Stop inherited routine movement once when taking the hold.
        }

        leased = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Lahabrea knockback staging");
        if (Core.Player.Location.Distance2D(target) <= 0.2)
        {
            StopOwnedMovement();
            return false; // Healing and rotation continue while holding the safe landing direction.
        }

        if (Core.Player.IsCasting)
        {
            ActionManager.StopCasting();
        }

        moved = true;
        Navigator.PlayerMover.MoveTowards(target);
        await Coroutine.Yield();
        return true;
    }

    private void Snapshot(BattleCharacter[] actors, double now)
    {
        HashSet<(uint Owner, uint Action)> seen = [];
        bool pushSeen = false;
        foreach (var actor in actors)
        {
            if (!actor.IsCasting || !actor.SpellCastInfo.IsValid)
            {
                continue;
            }

            uint action = actor.CastingSpellId;
            double finish = now + actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
            if (action == 29332)
            {
                predictedBurst = finish + 12;
            }

            if (action == 29327 || P.Cast(actor.ObjectId, action, default, 0, finish) != null)
            {
                wall = true; // Also reconstruct when enabled mid-phase after the opening cast.
            }

            if (action == P.Burst)
            {
                predictedBurst = finish;
            }

            if (action == P.Knockback)
            {
                push = new(Xz(actor.Location), finish);
                pushOwner = actor.ObjectId;
                pushSeen = true;
            }

            P.Hazard hazard = P.Cast(actor.ObjectId, action, Xz(actor.Location), actor.Heading, finish);
            if (hazard == null)
            {
                continue;
            }

            var key = (actor.ObjectId, action);
            casts[key] = hazard;
            seen.Add(key);
        }

        foreach (var pair in casts.ToArray())
        {
            if (!seen.Contains(pair.Key) && (now < pair.Value.Finish - 0.2 || now > pair.Value.Finish + P.Grace || !actors.Any(a => a.ObjectId == pair.Key.Owner)))
            {
                casts.Remove(pair.Key);
            }
        }

        if (!pushSeen && push != null && (now < push.Finish - 0.2 || now > push.Finish + P.Grace || !actors.Any(a => a.ObjectId == pushOwner)))
        {
            push = null;
        }
    }

    private void StopOwnedMovement()
    {
        if (moved && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        moved = false;
    }

    private void ReleaseMovement()
    {
        StopOwnedMovement();
        if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Lahabrea knockback released");
        }

        leased = false;
    }

    private void Reset()
    {
        active = false;
        wall = false;
        casts.Clear();
        push = null;
        predictedBurst = 0;
        plan = new([], null, null, "idle");
        ReleaseMovement();
    }

    private void DisableSideStep()
    {
        if (SidestepPlugin == null)
        {
            return;
        }

        if (!suppressed)
        {
            sideStepWasEnabled = SidestepPlugin.Enabled;
        }

        suppressed = true;
        SidestepPlugin.Enabled = false;
    }

    private void RestoreSideStep()
    {
        if (suppressed && SidestepPlugin != null)
        {
            SidestepPlugin.Enabled = sideStepWasEnabled;
        }

        suppressed = false;
    }

    /// <summary>Releases only this encounter's movement lease and restores its SideStep state.</summary>
    public void Dispose()
    {
        // Plugin-disable cleanup can arrive off the bot thread. Clear managed ownership here;
        // RB stop/map lifecycle owns the mover, so never issue native movement from Dispose.
        moved = false;
        Reset();
        RestoreSideStep();
        TreeRoot.OnStop -= OnStop;
        GameEvents.OnMapChanged -= OnMapChanged;
        if (activeEncounter == this)
        {
            activeEncounter = null;
        }
    }

    private void OnStop(BotBase bot) => Dispose();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != (uint)ZoneId)
        {
            Dispose();
        }
        else
        {
            Reset();
        }
    }

    private static Point Xz(Vector3 p) => new(p.X, p.Z);
}
