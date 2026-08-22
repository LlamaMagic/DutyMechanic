using Buddy.Coroutines;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Localization;
using DutyMechanic.Logging;
using DutyMechanic.Managers;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.NeoProfiles;
using ff14bot.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TreeSharp;

namespace DutyMechanic;

/// <summary>
/// Main RebornBuddy plugin class for Duty Mechanic.
/// </summary>
public class DutyMechanicPlugin : BotPlugin
{
    private Composite _root;

    // Developer-only high-volume diagnostics must default off in every committed build so customer
    // logs do not collect encounter cast, status, director, movement-anchor, and arena-object
    // traffic. Live investigations may toggle this locally, but the push invariant requires false.
    private const bool EnableMechanicDiagnostics = false;

    // The in-duty TreeStart decorator does not tick while the instance director is absent,
    // so DungeonManager cannot observe the open-world zone between back-to-back runs. Plugin
    // pulses continue during that interval; retaining this edge lets the next instance force
    // a fresh dungeon object even when both duty runs use the same zone ID.
    private bool _wasInInstance;

    /// <inheritdoc/>
    public override string Author => "DW, Manta, Athlon";

    /// <inheritdoc/>
    public override string Name => Translations.PROJECT_NAME;

    /// <summary>
    /// List of plugins we disable to prevent conflicts.
    /// </summary>
    protected static List<string> ConflictingPluginsToDisable => ["RBTrust", "Trust"];

    /// <inheritdoc/>
    public override string Description => "Plugin the causes the bot to execute advanced Duty/Boss Mechanics. Formerly known as RBTrust/Trust.";

    /// <inheritdoc/>
    /// Using Major/Minor as Current Global Game version, Build = date. Revision advances this
    /// net10-compatible release without changing the historical game-version/date identity.
    public override Version Version => new(7, 5, 04282015, 1);

    /// <inheritdoc/>
    public override bool WantButton => false;

    /// <inheritdoc/>
    public override void OnInitialize()
    {
        PluginContainer plugin = PluginHelpers.GetSideStepPlugin();
        if (plugin != null)
        {
            plugin.Enabled = true;
        }

        _root = new Decorator(c => CanTrust(), new ActionRunCoroutine(r => RunTrust()));
    }

    /// <inheritdoc/>
    public override void OnEnabled()
    {
        TreeRoot.OnStart += OnBotStart;
        TreeRoot.OnStop += OnBotStop;
        TreeHooks.Instance.OnHooksCleared += OnHooksCleared;

        if (TreeRoot.IsRunning)
        {
            AddHooks();
        }

        
    }

    /// <inheritdoc/>
    public override void OnDisabled()
    {
        TreeRoot.OnStart -= OnBotStart;
        TreeRoot.OnStop -= OnBotStop;
        TreeHooks.Instance.OnHooksCleared -= OnHooksCleared;
        RemoveHooks();
        LoggingHelpers.UpdateMechanicDiagnostics(false);
    }

    /// <inheritdoc/>
    public override void OnShutdown()
    {
        OnDisabled();
    }

    /// <inheritdoc/>
    public override void OnPulse()
    {
        bool isInInstance = LoadingHelpers.IsInInstance;

        if (isInInstance && !_wasInInstance)
        {
            // Zone IDs identify duty maps, not individual duty sessions. Marking the manager
            // stale on the entry edge ensures per-run avoids and state are rebuilt after a
            // same-duty requeue without repeatedly clearing them on every plugin pulse.
            DungeonManager.ClearCurrent();
        }

        _wasInInstance = isInInstance;
    }

    /// <inheritdoc/>
    public override void OnButtonPress()
    {
        base.OnButtonPress();
    }

    private void AddHooks()
    {
        Logger.Information("Adding DutyMechanic Hook");
        DungeonManager.ClearCurrent();
        TreeHooks.Instance.AddHook("TreeStart", _root);
    }

    private void RemoveHooks()
    {
        Logger.Information("Removing DutyMechanic Hook");
        TreeHooks.Instance.RemoveHook("TreeStart", _root);
        DungeonManager.ClearCurrent();
    }

    private void OnBotStop(BotBase bot)
    {
        RemoveHooks();
        LoggingHelpers.UpdateMechanicDiagnostics(false);
    }

    private void OnBotStart(BotBase bot)
    {
        AddHooks();
    }

    private void OnHooksCleared(object sender, EventArgs e)
    {
        RemoveHooks();
    }

    private static bool CanTrust()
    {
        if (LoadingHelpers.IsInInstance || WorldManager.ZoneId is (ushort)ZoneId.UltimaThule or (ushort)ZoneId.SouthHorn or (ushort)ZoneId.EurekaAnemos or (ushort)ZoneId.EurekaPagos or (ushort)ZoneId.EurekaPyros)
        {
            return true;
        }

        return false;
    }

    private async Task<bool> RunTrust()
    {
        /*
        if (await TryRespawnPlayerAsync())
        {
            return true;
        }
        */

        DisableConflictingPlugins();

        await MovementHelpers.TryIncreaseMovementSpeedAsync();

        // RB object and aura wrappers are frame-scoped, so diagnostics run on TreeStart's bot thread.
        LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
        LoggingHelpers.LogZoneChanges();

        return await DungeonManager.RunAsync();
    }

    /// <summary>
    /// Disables conflicting plugins. See <seealso cref="ConflictingPluginsToDisable"/>.
    /// </summary>
    protected static void DisableConflictingPlugins()
    {
        foreach (var pluginName in ConflictingPluginsToDisable)
        {
            PluginContainer enabledPlugin = PluginManager.Plugins.FirstOrDefault(p => p.Plugin.Name == pluginName && p.Enabled);

            if (enabledPlugin != null)
            {
                Logger.Warning($"Disabling {pluginName} plugin to prevent conflicts. Consider uninstalling the {pluginName} plugin.", "QolFreeCompanyActions");
                enabledPlugin.Enabled = false;
            }
        }
    }

    private static async Task<bool> TryRespawnPlayerAsync()
    {
        if (Core.Player.IsAlive)
        {
            return false;
        }

        if (!PartyManager.AllMembers.Any(pm => pm is TrustPartyMember))
        {
            return false;
        }

        Logger.Information(Translations.PLAYER_DIED_RELOADING_PROFILE);

        const int maxRespawnTime = 60_000;
        bool respawnedInReasonableTime = await Coroutine.Wait(maxRespawnTime, () => Core.Player.IsAlive);

        await LoadingHelpers.WaitForLoadingAsync();

        if (respawnedInReasonableTime)
        {
            NeoProfileManager.Load(CharacterSettings.Instance.LastNeoProfile, true);
            NeoProfileManager.UpdateCurrentProfileBehavior();
        }
        else
        {
            Logger.Error(Translations.PLAYER_FAILED_TO_RESPAWN, maxRespawnTime);
        }

        return true;
    }
}
