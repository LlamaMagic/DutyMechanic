using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Keeps players outside Hard-mode Ifrit's instant cleave unless they hold his aggro.
/// Supplies charge geometry when the client omits its omen. The profile owns nails
/// and completion; SideStep retains the observed Eruption and Plume circles.
/// </summary>
public sealed class IfritHard : AbstractDungeon
{
    // The synchronized capture separates the boss/clones (0xD1) from helpers (0x1B2)
    // sharing name row 1185. Only the targetable boss can own the ordinary cleave.
    private const uint IfritBase = 0xD1;
    private const uint Cyclone = 457;
    private static IfritHard current;
    private IfritHardMovement movement;
    private bool yieldedMover;
    private bool ownsCycloneOverride;

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)292;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [1357];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;

        // Global 2026.10.16 exposes an entirely zero omen matrix for Cyclone,
        // producing an empty generic polygon. Override only this spell, keeping
        // SideStep's verified circles. Casts identify the untargetable clones too.
        ownsCycloneOverride = SideStep.Override(Cyclone);
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(condition: EncounterActive,
            leashPointProducer: () => Vector3.Zero, leashRadius: 40,
            rotationProducer: actor => -actor.Heading, scaleProducer: _ => 1, heightProducer: _ => 15,
            // The 38y action range plus 5y reach gives 43y; width is 12y.
            // Half a yalm around the rectangle covers the player's hitbox margin.
            pointsProducer: _ => [new(6.5f, 43.5f), new(-6.5f, 43.5f),
                new(-6.5f, -0.5f), new(6.5f, -0.5f)],
            locationProducer: actor => actor.Location,
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(actor => actor.IsValid && actor.IsAlive && actor.BaseId == IfritBase
                    && actor.CastingSpellId == Cyclone),
            priority: AvoidancePriority.High);

        // Incinerate (1353) is instant and has no cast telegraph. The sheet's 10y range
        // plus 5y boss reach agrees with the 15y/120-degree encounter reference.
        // Add 0.5y and five degrees on either side. Aggro, not job, exempts the tank:
        // an off-tank must not share the main tank's frontal position.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: EncounterActive,
            objectSelector: actor => actor.IsValid && actor.IsAlive && actor.BaseId == IfritBase
                && actor.IsTargetable && actor.CurrentTargetId != 0 && actor.CurrentTargetId != 0xE0000000
                && actor.CurrentTargetId != actor.ObjectId
                && actor.CurrentTargetId != Core.Me.ObjectId,
            leashPointProducer: () => Vector3.Zero,
            leashRadius: 40,
            rotationDegrees: 0,
            radius: 15.5f,
            arcDegrees: 130);
        Logger.Information("[IfritHard] Registered cleave, Cyclone rectangles and native escape cleanup.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    /// <summary>Releases borrowed input and event subscriptions on stop, disable or replacement.</summary>
    public static void ResetActive()
    {
        if (current == null)
        {
            return;
        }

        TreeRoot.OnStop -= current.OnStop;
        GameEvents.OnMapChanged -= current.OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse -= current.OnPulse;
        current.movement?.Detach();
        current.movement = null;
        // Do not remove an override another handler already owned before entry.
        if (current.ownsCycloneOverride)
        {
            SideStep.RemoveHandler(Cyclone);
        }
        current.ownsCycloneOverride = false;
        current = null;
    }

    private static bool EncounterActive() => current != null && TreeRoot.IsRunning
        && Core.Me != null && Core.Me.IsAlive && DutyManager.InInstance && WorldManager.ZoneId == 292
        && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.BaseId == IfritBase
                && (Core.Me.InCombat || actor.CurrentHealthPercent < 100 || actor.CastingSpellId == Cyclone));

    private void OnStop(BotBase bot) => ResetActive();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != 292)
        {
            ResetActive();
        }
    }

    private void OnPulse(object sender, EventArgs args)
    {
        if (!EncounterActive())
        {
            movement?.Detach();
            movement = null;
            yieldedMover = false;
            return;
        }

        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
            yieldedMover = true;
        }

        // Never wrap or reclaim another controller. This compatibility path is for
        // OrderBot's verified SlideMover and leaves all destination selection intact.
        if (movement == null && !yieldedMover && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new IfritHardMovement(Navigator.PlayerMover);
            Navigator.PlayerMover = movement;
        }

        movement?.CompleteEscape();
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (EncounterActive())
        {
            await DamageMitigationSpells();
        }
        return false;
    }
}
