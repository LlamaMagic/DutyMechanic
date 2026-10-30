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
    // Enable only when collecting encounter diagnostics during development.
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
    public override string Description => "Handles advanced duty and boss mechanics. Formerly known as RBTrust/Trust.";
    /// <inheritdoc/>
    public override Version Version => new(7, 5, 04282015, 21);
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
        ResetEncounters();
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
        // ARR boss ownership must release even when death/loading skips its hook.
        Dungeons.Sastasha.CheckLifecycle();
        Dungeons.TamTaraDeepcroft.CheckLifecycle();
        Dungeons.CopperbellMines.CheckLifecycle();
        Dungeons.ThousandMawsOfTotoRak.CheckLifecycle();
        Dungeons.HaukkeManor.CheckLifecycle();
        Dungeons.BrayfloxsLongstop.CheckLifecycle();
        Dungeons.StoneVigil.CheckLifecycle();
        // Keeper sequences and input cleanup must survive yielding boss/add profile hooks.
        Dungeons.KeeperOfTheLake.CheckLifecycle();
        // Sohm's helper/wing phases must retain the same input owner through yielding grinds.
        Dungeons.SohmAl.CheckLifecycle();
        // Aery flight/add phases retain the same owned avoidance/input channel.
        Dungeons.Aery.CheckLifecycle();
        Dungeons.ThokAstThokHard.CheckLifecycle();
        // Trial combat/cutscene coroutines can retain TreeStart. Keep the shared
        // diagnostic collector on the bot-thread pulse for future Ravana captures.
        if (TreeRoot.IsRunning && WorldManager.ZoneId == 432)
            LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
        if (TreeRoot.IsRunning && WorldManager.ZoneId is 1036 or 1037 or 1038 or 1039 or 1040 or 1041 or 1042 or 1063 or 1064 or 1065)
        {
            // Door/combat coroutines can retain TreeStart; observe boss impacts
            // here too so a delayed hook cannot misattribute vulnerability gains.
            LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
        }
        // Matoya leases must end even when death/loading bypasses the encounter hook.
        Dungeons.MatoyasRelict.CheckLifecycle();
        Dungeons.GrandCosmos.CheckLifecycle();
        Dungeons.AnamnesisAnyder.CheckKyklopsLifecycle();
        Dungeons.HeroesGauntlet.CheckBerserkerLifecycle();
        Dungeons.AnamnesisAnyder.CheckRukshsLifecycle();
        Dungeons.AnamnesisAnyder.CheckUnknownLifecycle();
        Dungeons.Paglthan.CheckPositioningLifecycle();
        // Ktisis positioning must release when death/loading bypasses its TreeStart tick.
        Dungeons.KtisisHyperboreia.CheckLifecycle();
        // Observe impacts even while another coroutine retains TreeStart; otherwise
        // the reported vulnerability time can lag behind the actual sphere hit.
        if (TreeRoot.IsRunning && WorldManager.ZoneId == 974)
        {
            LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
        }
        // AutoParty retains TreeStart: publish Hades hazards on this existing bot-thread pulse.
        if (TreeRoot.IsRunning && BotManager.Current is ff14bot.BotBases.OrderBot)
        {
            if (WorldManager.ZoneId == 847)
            {
                LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
            }

            Dungeons.DyingGaspMechanics.Pulse();
            if (WorldManager.ZoneId == 922)
            {
                LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
            }

            Dungeons.SeatOfSacrifice.Pulse();
        }
        else
        {
            Dungeons.DyingGaspMechanics.Reset();
            Dungeons.SeatOfSacrifice.Reset();
        }

        // One Royal Menagerie owner must tick even while AutoParty retains a yielding coroutine.
        // It performs no awaits; normal TreeStart only observes its scheduling result.
        if (TreeRoot.IsRunning && BotManager.Current is ff14bot.BotBases.OrderBot)
        {
            if (WorldManager.ZoneId == 679)
            {
                LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
            }

            Dungeons.TheRoyalMenagerie.PulseActive();
        }

        // Enabled plugins pulse on the bot thread even while OrderBot's cutscene/profile
        // coroutine owns TreeStart. RB's native QTE API supplies one input per frame without
        // keyboard simulation or a blocking coroutine. Keep this outside CanTrust so solo
        // story QTEs do not require a registered dungeon or an instance-director match.
        if (TreeRoot.IsRunning && BotManager.Current is ff14bot.BotBases.OrderBot && ff14bot.RemoteWindows.QTE.IsOpen)
        {
            ff14bot.RemoteWindows.QTE.Pulse();
        }

        // Panda Farmer owns the ARR alliance raid tags and their guarded death recovery.
        // Do not pulse them here: two plugin owners can race the same Return confirmation.
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
        ResetEncounters();
        RemoveHooks();
        LoggingHelpers.UpdateMechanicDiagnostics(false);
    }

    // Stop and hook replacement must release encounter leases before a new mover runs.
    private static void ResetEncounters()
    {
        // Ravana's native escape and rail staging share a scoped input owner.
        Dungeons.ThokAstThokHard.ResetActive();
        // Pillar holds must release their borrowed navigation and targeting on stop.
        Dungeons.GarudaHard.ResetActive();
        // Titan's native escape input and floor callbacks must not survive an exit.
        Dungeons.TitanHard.ResetActive();
        // Native escape input must not outlive the owning Ifrit run.
        Dungeons.IfritHard.ResetActive();
        // The Chrysalis borrows navigation input and must release it on plugin shutdown.
        Dungeons.Chrysalis.ResetActive();
        Dungeons.AkhAfahHard.ResetActive();
        // Boss ownership is encounter-local and must not survive a stopped run.
        Dungeons.Sastasha.ResetActive();
        Dungeons.TamTaraDeepcroft.ResetActive();
        Dungeons.CopperbellMines.ResetActive();
        Dungeons.ThousandMawsOfTotoRak.ResetActive();
        Dungeons.HaukkeManor.ResetActive();
        Dungeons.BrayfloxsLongstop.ResetActive();
        Dungeons.StoneVigil.ResetActive();
        Dungeons.KeeperOfTheLake.ResetActive();
        Dungeons.SohmAl.ResetActive();
        Dungeons.Aery.ResetActive();
        Dungeons.Lahabrea.ResetActive();
        Dungeons.TheRoyalMenagerie.ResetActive();
        Dungeons.DyingGaspMechanics.Reset();
        Dungeons.SeatOfSacrifice.Reset();
        Dungeons.Amaurot.ResetActive();
        Dungeons.MatoyasRelict.ResetActive();
        Dungeons.GrandCosmos.ResetActive();
        Dungeons.AnamnesisAnyder.ResetKyklopsActive();
        Dungeons.HeroesGauntlet.ResetBerserkerActive();
        Dungeons.AnamnesisAnyder.ResetRukshsActive();
        Dungeons.AnamnesisAnyder.ResetUnknownActive();
        Dungeons.Paglthan.ResetPositioning();
        Dungeons.KtisisHyperboreia.ResetActive();
    }

    private void OnBotStart(BotBase bot)
    {
        AddHooks();
    }

    private void OnHooksCleared(object sender, EventArgs e)
    {
        ResetEncounters();
        RemoveHooks();
        LoggingHelpers.UpdateMechanicDiagnostics(false);
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
        // Seat consumes TreeStart only for travel, fire or QTE; routine healing resumes at safety.
        if (Dungeons.SeatOfSacrifice.HoldsTree)
        {
            return true;
        }

        DisableConflictingPlugins();
        await MovementHelpers.TryIncreaseMovementSpeedAsync();
        // Frame-scoped wrappers must be sampled on the bot thread, never from the UI.
        LoggingHelpers.UpdateMechanicDiagnostics(EnableMechanicDiagnostics);
        LoggingHelpers.LogZoneChanges();
        return await DungeonManager.RunAsync();
    }

    /// <summary>
    /// Disables conflicting plugins. See <seealso cref = "ConflictingPluginsToDisable"/>.
    /// </summary>
    protected static void DisableConflictingPlugins()
    {
        foreach (var pluginName in ConflictingPluginsToDisable)
        {
            PluginContainer enabledPlugin = PluginManager.Plugins.FirstOrDefault(p => p.Plugin.Name == pluginName && p.Enabled);
            if (enabledPlugin != null)
            {
                Logger.Warning($"Disabling {pluginName} plugin to prevent conflicts. Consider uninstalling the {pluginName} plugin.");
                enabledPlugin.Enabled = false;
            }
        }
    }
}
