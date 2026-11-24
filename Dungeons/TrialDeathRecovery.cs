using System;
using System.Threading.Tasks;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.RemoteWindows;
using TreeSharp;

namespace DutyMechanic.Dungeons;

// RebornBuddy's default death task stops after a missing revive notification.
// Registered profile mechanics need retryable in-instance recovery and must not
// replace another plugin's death owner or affect unrelated duties.
internal static class TrialDeathRecovery
{
    private const string ReviveHook = "DeathReviveLogic";
    private const string ReturnHook = "DeathReturnLogic";
    private static readonly Composite Revive = new ActionRunCoroutine(_ => HandleRevive());
    private static readonly Composite Return = new ActionRunCoroutine(_ => HandleReturn());
    private static DateTime nextAttempt;
    private static bool installed, deathRecorded;

    // These profile trials use in-place raises rather than dungeon return travel.
    private static bool IsProfileTrial() =>
        (WorldManager.ZoneId is 922 or 559 or 992) &&
        ProfileMechanics.Owns(WorldManager.ZoneId, ff14bot.NeoProfiles.NeoProfileManager.CurrentProfile?.Path);

    internal static void Update()
    {
        bool inTrial = TreeRoot.IsRunning && DutyManager.InInstance && IsProfileTrial();
        if (!inTrial) { Release(); return; }
        if (Core.Me is { IsAlive: true }) deathRecorded = false;
        var hooks = TreeHooks.Instance.Hooks;
        if (!hooks.TryGetValue(ReviveHook, out var revive) || !hooks.TryGetValue(ReturnHook, out var after)) return;
        // A later competing owner takes precedence; do not keep two death
        // controllers registered merely because ours was installed first.
        if (installed && revive.Count == 1 && after.Count == 1 &&
            revive.Contains(Revive) && after.Contains(Return)) return;
        Release();
        // Any existing handler owns the whole death path; leave it untouched.
        if (revive.Count != 0 || after.Count != 0) return;
        TreeHooks.Instance.AddHook(ReviveHook, Revive);
        TreeHooks.Instance.AddHook(ReturnHook, Return);
        installed = true;
        Logger.Information("[TrialRecovery] Retryable trial revive hooks installed.");
    }

    private static Task<bool> HandleRevive()
    {
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
            !DutyManager.InInstance || !IsProfileTrial() ||
            Core.Me == null || !Core.Me.IsValid || Core.Me.IsAlive) return Task.FromResult(true);
        // RB's default task increments this statistic once per death. Retain
        // that accounting when its built-in retry loop is bypassed.
        if (!deathRecorded)
        {
            GameStatsManager.Died();
            deathRecorded = true;
        }
        // Reuse RB's supported dead-state confirmation flow. A missing window
        // means wait and retry; it is not a terminal error or permission to stop.
        if (DateTime.UtcNow >= nextAttempt && ClientGameUiRevive.ReviveState == ReviveState.Dead)
        {
            nextAttempt = DateTime.UtcNow.AddSeconds(3);
            if (SelectYesno.IsOpen) ClientGameUiRevive.Revive();
            else if (NotificationRevive.IsOpen) NotificationRevive.Click();
        }
        return Task.FromResult(true);
    }

    private static Task<bool> HandleReturn()
    {
        if (TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance &&
            IsProfileTrial() && Core.Me is { IsAlive: true } && Poi.Current.Type == PoiType.Death)
        {
            // The trial profile owns the next target; never teleport to a stale
            // pre-death point after an in-place raise or arena restart.
            Poi.Clear("Live trial revive completed");
        }
        return Task.FromResult(true);
    }

    internal static void Release()
    {
        if (!installed) return;
        TreeHooks.Instance.RemoveHook(ReviveHook, Revive);
        TreeHooks.Instance.RemoveHook(ReturnHook, Return);
        installed = false;
        nextAttempt = DateTime.MinValue;
        deathRecorded = false;
    }
}
