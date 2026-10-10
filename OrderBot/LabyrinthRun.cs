using System;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using Clio.XmlEngine;
using DutyMechanic.Dungeons;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Helpers;
using ff14bot.Navigation;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using TreeSharp;

namespace ff14bot.NeoProfiles.Tags;
/// <summary>
/// Coordinates one Labyrinth visit using native objectives and RB navigation. Every tick
/// chooses the current destination again, so combat, a platform hold, a shortcut or a drop
/// cannot leave a previous MoveTo active. Entry, loot and verified exit remain in XML.
/// </summary>
[XmlElement("LabyrinthRun")]
public sealed class LabyrinthRun : ProfileBehavior
{
    private static LabyrinthRun running;
    private bool moving;
    private Composite controllerHook;
    private LabyrinthOfTheAncients mechanics;
    private LabyrinthRoutineLease routineLease;
    private bool routineChecked;
    private DateTime nextRoutineCheck;
    private uint pendingShortcut;
    private DateTime nextInteraction;
    /// <summary>Completes on the intended director's completion, or departure; neither leaves a duty.</summary>
    public override bool IsDone => !CommonBehaviors.IsLoading && (!DutyManager.InInstance || WorldManager.ZoneId != 174 || LabyrinthOfTheAncients.Director()?.InstanceEnded == true);

    /// <inheritdoc/>
    protected override void OnStart()
    {
        running = this;
        pendingShortcut = 0;
        routineChecked = false;
        TreeRoot.OnStop += OnBotStop;
        // A normal profile slot is skipped while a Kill POI owns the brain. The tag owns
        // this hook so urgent mechanics and ready checks still pulse during combat.
        // Its ordinary behavior is inert to prevent executing the controller twice.
        controllerHook = new ActionRunCoroutine(_ => TickAsync());
        TreeHooks.Instance.AddHook("TreeStart", controllerHook);
        Log("Tag owns Labyrinth mechanics and travel; combat remains with OrderBot.");
    }

    /// <inheritdoc/>
    protected override Composite CreateBehavior() => new ActionAlwaysFail();
    /// <inheritdoc/>
    protected override void OnDone()
    {
        SuspendTravel();
        mechanics?.ReleaseFromTag(!CommonBehaviors.IsLoading);
        ClearOwner();
    }

    /// <inheritdoc/>
    protected override void OnResetCachedDone()
    {
        pendingShortcut = 0;
    }

    // Stop callbacks discard managed ownership only; RB owns native movement shutdown.
    private void OnBotStop(BotBase bot)
    {
        moving = false;
        mechanics?.Dispose();
        ClearOwner();
    }

    private void ClearOwner()
    {
        try
        {
            routineLease?.Dispose();
        }
        catch (Exception ex)
        {
            Log("Could not restore Scholar multi-dot preference: " + ex.Message);
        }

        routineLease = null;
        if (controllerHook != null)
            TreeHooks.Instance.RemoveHook("TreeStart", controllerHook);
        controllerHook = null;
        mechanics = null;
        pendingShortcut = 0;
        if (running == this)
            running = null;
        TreeRoot.OnStop -= OnBotStop;
    }

    internal static bool OwnsShortcutConfirmation => running?.pendingShortcut > 0;

    // The tag-driven mechanic planner calls this before taking movement ownership. This only
    // cancels travel issued by this tag; healing, avoidance and unrelated movement remain free.
    internal static void YieldTravelToMechanic() => running?.SuspendTravel();
    private void SuspendTravel()
    {
        if (!moving)
            return;
        moving = false;
        if (AvoidanceManager.IsRunningOutOfAvoid)
            return;
        Navigator.Clear();
        Navigator.PlayerMover.MoveStop();
    }

    private async Task<bool> TickAsync()
    {
        if (running != this)
            return false;
        if (!routineChecked && DateTime.UtcNow >= nextRoutineCheck && !CommonBehaviors.IsLoading && Core.Player != null)
        {
            nextRoutineCheck = DateTime.UtcNow.AddSeconds(10);
            // Scope this compatibility preference to the attached Scholar rotation.
            // Other jobs/routines retain their own settings and combat implementation.
            var routine = RoutineManager.Current;
            if (Core.Player.CurrentJob.ToString() == "Scholar" && routine?.Name.IndexOf("Magitek", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    // The routine entry can be a compiled loader from another assembly;
                    // resolve the verified settings type among already-loaded assemblies.
                    // Settings may initialize after OnStart, so absence is retried slowly.
                    var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Magitek.Models.Scholar.ScholarSettings", false)).FirstOrDefault(t => t != null);
                    var settings = type?.GetProperty("Instance")?.GetValue(null);
                    var property = type?.GetProperty("BioMultipleTargets");
                    if (settings != null && property?.PropertyType == typeof(bool) && property.CanRead && property.CanWrite)
                    {
                        routineLease = new LabyrinthRoutineLease(settings, property);
                        routineChecked = true;
                        Log("Scholar multi-dot disabled for encounter target restrictions; prior setting restores on release.");
                    }
                    else
                        Log("Scholar multi-dot compatibility unavailable; routine target restrictions are not guaranteed.");
                }
                catch (Exception ex)
                {
                    Log("Scholar multi-dot compatibility failed: " + ex.Message);
                }
            }
        }

        if (mechanics == null && LabyrinthOfTheAncients.Director() != null)
        {
            mechanics = new LabyrinthOfTheAncients();
            await mechanics.InitializeForTag();
        }

        // Run once, before travel and combat gating. A positioned hold returns false so
        // healing remains schedulable; ProfileHolding then prevents travel from taking over.
        if (mechanics != null && await mechanics.RunAsync())
            return true;
        var director = LabyrinthOfTheAncients.Director();
        if (director == null || director.InstanceEnded || !Core.Player.IsAlive || QuestLogManager.InCutscene)
        {
            SuspendTravel();
            return false;
        }

        // A Kill POI also owns pre-pull movement; the top-level hook must yield there.
        if (LabyrinthOfTheAncients.ProfileHolding || Core.Player.InCombat || Poi.Current.Type == PoiType.Kill || AvoidanceManager.IsRunningOutOfAvoid)
        {
            SuspendTravel();
            return false;
        }

        bool Done(int slot) => director.GetTodoArgs(slot).Item1 == 1;
        var p = Core.Player.Location;
        // Only a completed wing authorizes its captured return object. No timeout abandons
        // this public raid, and a missing object causes reacquisition on subsequent ticks.
        uint shortcut = Done(2) && p.X < -300 ? 2002804u : Done(4) && p.X > 350 ? 2002805u : Done(6) && WorldManager.SubZoneId == 869 ? 2002806u : 0;
        if (shortcut != 0)
        {
            var obj = GameObjectManager.GetObjectByNPCId(shortcut);
            if (obj == null || !obj.IsValid || !obj.IsTargetable)
            {
                SuspendTravel();
                return false;
            }

            if (SelectYesno.IsOpen)
            {
                // Accept only the confirmation opened by this nearby shortcut interaction.
                if (pendingShortcut == shortcut && obj.Location.Distance(Core.Player.Location) < 5 && DateTime.UtcNow >= nextInteraction)
                {
                    SelectYesno.Yes();
                    nextInteraction = DateTime.UtcNow.AddSeconds(2);
                }

                return false;
            }

            if (!obj.IsWithinInteractRange)
                return await Move(obj.Location, 2, "Cleared-wing shortcut");
            SuspendTravel();
            if (DateTime.UtcNow >= nextInteraction)
            {
                pendingShortcut = shortcut;
                obj.Interact();
                nextInteraction = DateTime.UtcNow.AddSeconds(2);
            }

            return false;
        }

        pendingShortcut = 0;
        Vector3? destination = null;
        string name = "Labyrinth approach";
        float tolerance = 2;
        // These are encounter entrances and the observed stair obstruction, not a route replay.
        // RB computes paths between them. Native objectives always supersede old destinations.
        if (!Done(1))
        {
            if (p.X > -400)
                destination = new(-410, 42.4f, 280);
            else if (p.X > -440)
                destination = new(-446.57382f, 42.4f, 279.7106f);
            else if (p.Z > 215)
                destination = new(-466.1784f, 42.4f, 193.19446f);
        }
        else if (!Done(2))
        {
            if (p.Z > 46)
                destination = new(-451.549f, 25.534311f, 43.84742f);
        }
        else if (!Done(3))
        {
            if (p.X < 83.3f)
                destination = new(85.54079f, 52.8f, 267.2961f);
            else if (p.X < 119)
                destination = new(121.3516f, 62.4f, 267.27786f);
            else if (p.X < 190)
            {
                // Follow witnessed party descent before alliance consensus, and never
                // turn consensus alone into permission to run ahead onto the platform.
                var entry = LabyrinthOfTheAncients.AtomosEntryGoal();
                if (entry.P.X <= p.X)
                {
                    SuspendTravel();
                    return false;
                }

                destination = new(entry.P.X, entry.Y, entry.P.Y);
                tolerance = entry.Tolerance;
                name = entry.Reason;
            }
        }
        else if (!Done(4))
        {
            if (p.X < 410)
                destination = new(412.4f, 66.2f, 280);
        }
        else if (!Done(5))
        {
            if (p.Z > 93)
                destination = new(-142.28629f, 47.884533f, 90.77296f);
            else if (p.Z > 0)
            {
                destination = new(-114.05978f, 41.97415f, 70.173515f);
                tolerance = .15f;
            }
            else if (p.Z > -115)
                destination = new(-110, 44.17f, -139);
        }
        else if (!Done(6))
        {
            if (p.Z > -335)
                destination = new(-110.32939f, 68.20247f, -338.99496f);
        }
        else if (!Done(7) && p.Y < 600)
        {
            destination = new(-111.652824f, 48.36361f, 299.30038f);
            tolerance = .15f;
            name = "Phlegethon drop approach";
        }

        if (destination == null)
        {
            SuspendTravel();
            return false;
        }

        if (!LabyrinthOfTheAncients.CanApproachWithParty(destination.Value))
        {
            SuspendTravel();
            return false;
        }

        return await Move(destination.Value, tolerance, name);
    }

    private async Task<bool> Move(Vector3 destination, float tolerance, string name)
    {
        moving = await CommonTasks.MoveAndStop(new MoveToParameters(destination) { DistanceTolerance = tolerance, UseMount = false }, tolerance, true, name);
        return moving;
    }
}
