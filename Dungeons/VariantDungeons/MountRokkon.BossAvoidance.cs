using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    // Positive back distance is negated by Rectangle; -6.5 starts beyond the safe launch segment.
    private static Vector2[] TenguUnsafeLandingLane() => Rectangle(3f, -6.5f, 40.5f);
    private PluginContainer _mechanicPlugin;
    private bool _sideStepSuspended;
    private void RegisterBossAvoidance()
    {
        // These ordinary telegraphs previously belonged to SideStep. Captured
        // actor/omen origins distinguish self-centered attacks from placed baits;
        // all radii and rectangle edges include the normal half-yalm allowance.
        AvoidanceManager.AddAvoidLocation<Impact>(InYozakura, c => c.Action == 33640 ? 9.5f : 5.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action is 33640 or 33674));
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(3.5f, .5f, 10.5f), c => c.Location, () => PendingImpacts().Where(c => c.Action == 33664), priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Impact>(InMoko, c => MokoCircleRadius(c.Action), c => c.Location, () => PendingImpacts().Where(c => MokoCircleRadius(c.Action) > 0));
        AvoidanceManager.AddAvoidPolygon<Impact>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(3f, .5f, 40.5f), c => c.Location, () => PendingImpacts().Where(c => c.Action == 34218), priority: AvoidancePriority.High);
        // Eight alternating Tengu lanes cover the arena; avoiding their entire
        // length removes all floor. The 33y forward knockback is safe only within
        // the first 7y of its 40y lane. Reserve the unsafe landing portion, keeping
        // a half-yalm inset at both walls. Ghastly Grasp resolves two seconds later
        // and retains its independent placed circles during the landing.
        AvoidanceManager.AddAvoidPolygon<Impact>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => TenguUnsafeLandingLane(), c => c.Location, () => PendingImpacts().Where(c => c.Action == 34210), priority: AvoidancePriority.High);
        // The October 2 Tengu rows face east/west. Reverse-project the later
        // grasp circles by the 33y displacement, so RB chooses a launch whose
        // landing also clears the next hit. The opposite projection falls outside
        // that launch side; both use the same retained circle identity. Drop these
        // projections with the knockback, leaving the actual circles for landing.
        AvoidanceManager.AddAvoidLocation<Impact>(() => InMoko() && PendingImpacts().Any(c => c.Action == 34210), _ => 5.5f, c => c.Location + new Vector3(33, 0, 0), () => PendingImpacts().Where(c => c.Action == 34212));
        AvoidanceManager.AddAvoidLocation<Impact>(() => InMoko() && PendingImpacts().Any(c => c.Action == 34210), _ => 5.5f, c => c.Location - new Vector3(33, 0, 0), () => PendingImpacts().Where(c => c.Action == 34212));
        AvoidanceManager.AddAvoidLocation<Impact>(InGorai, c => c.Action == 34012 ? 18.5f : c.Action == 34021 ? 5.5f : 3.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action is 34012 or 34021 or 34039));
        _mechanicPlugin = PluginManager.Plugins.FirstOrDefault(p => p.Plugin is DutyMechanicPlugin);
        TreeRoot.OnStop += RestoreBossSideStep;
    }

    // Iron Rain is a placed 10y circle, katana emergence 3y, Ghastly Grasp
    // 5y, and Scarlet/Spiritflame 6y. Zero means this provider does not own it.
    private static float MokoCircleRadius(uint action) => action switch
    {
        34196 => 10.5f,
        34217 => 3.5f,
        34212 => 5.5f,
        34200 or 34214 => 6.5f,
        _ => 0
    };
    private void UpdateBossAvoidanceOwner()
    {
        // Called on the existing bot pulse before encounter capture. Actual living
        // boss gates survive target switches and untargetability without disabling
        // trash avoidance. No global manager is pulsed by an HTTP/UI callback.
        var ownsBoss = TreeRoot.IsRunning && _mechanicPlugin?.Enabled == true && (InYozakura() || InMoko() || InGorai() || InShishio() || InEnenra());
        if (!ownsBoss)
        {
            RestoreBossSideStep(null);
            return;
        }

        if (SidestepPlugin?.Enabled == true)
        {
            SidestepPlugin.Enabled = false;
            _sideStepSuspended = true;
            DutyMechanic.Logging.Logger.Information("[Rokkon] DutyMechanic owns boss avoidance; SideStep suspended.");
        }
    }

    private void RestoreBossSideStep(ff14bot.AClasses.BotBase bot)
    {
        // Restore only our own suspension, including a stop before DungeonManager
        // gets another coroutine tick. Never turn on a plugin we found disabled.
        if (!_sideStepSuspended)
            return;
        _sideStepSuspended = false;
        if (SidestepPlugin != null)
            SidestepPlugin.Enabled = true;
        DutyMechanic.Logging.Logger.Information("[Rokkon] Boss avoidance released; SideStep restored.");
    }

    private void ReleaseBossAvoidance()
    {
        TreeRoot.OnStop -= RestoreBossSideStep;
        RestoreBossSideStep(null);
    }
}
