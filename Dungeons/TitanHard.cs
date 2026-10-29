using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Hard-mode Titan's collapsing floor, ordered bombs and knockback lane.
/// The duty profile owns gaol/heart targeting; the routine retains healing and combat.
/// </summary>
public sealed class TitanHard : AbstractDungeon
{
    // Hard-mode action/base IDs are distinct from the normal-duty NPC encounter.
    private const uint Titan = 0xF7, Bomb = 0x5A3;
    private const uint Geocrush = 1365, Landslide = 1364, Burst = 1052;
    private static TitanHard current;
    private readonly HashSet<uint> overrides = [];
    private TitanHardMovement movement;
    private bool yieldedMover, jumping;
    private int jumps;
    private DateTime landingUntil;
    private bool refreshFloor;

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)293;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [Geocrush];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        // A mid-fight reload has missed the collapse count. The smallest verified
        // floor is conservative until a fresh pull, avoiding an invented safe rim.
        jumps = Actors().Any(a => a.BaseId == Titan && a.CurrentHealthPercent < 100) ? 3 : 0;
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        // Weight of the Land retains SideStep's ground-targeted omen handling.
        // Its parent visual and the gaol's single-target countdown are not circles.
        foreach (var spell in new[] { Geocrush, Landslide, Burst, 1362u, 1477u })
        {
            if (SideStep.Override(spell))
                overrides.Add(spell);
        }

        // Each Geocrush removes another rim. The half-yalm inset remains after the
        // cast so combat paths cannot use a fallen section of the old navmesh.
        foreach (int stage in new[] { 0, 1, 2, 3 })
        {
            int captured = stage;
            AvoidanceHelpers.AddAvoidDonut(() => EncounterActive() && jumps == captured,
                () => Vector3.Zero, 60, TitanHardPolicy.FloorRadius(stage) - 0.5f);
        }
        // Geocrush is falloff damage, not a 100-yalm forbidden circle. Stage just
        // inside the surviving rim while keeping the outer floor constraint active.
        AvoidanceManager.AddAvoidLocation<Vector3>(
            () => EncounterActive() && DateTime.UtcNow < landingUntil,
            () => Vector3.Zero, 30, _ => TitanHardPolicy.FloorRadius(jumps) - 2,
            p => p, () => new[] { Vector3.Zero });
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(EncounterActive,
            a => a.IsValid && a.IsAlive && a.CastingSpellId == Landslide,
            width: 7, length: 41, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Vector3>(EncounterActive, () => Vector3.Zero, 30,
            _ => 6.8f, p => p, ImminentBombs);
        // Instant frontals have no warning cast. Only the actual aggro holder is
        // exempt; the off-tank and party must not share Mountain Buster's facing.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(EncounterActive,
            a => a.IsValid && a.IsAlive && a.BaseId == Titan && a.IsTargetable && !a.IsCasting
                && a.CurrentTargetId != 0 && a.CurrentTargetId != 0xE0000000
                && a.CurrentTargetId != a.ObjectId && a.CurrentTargetId != Core.Me.ObjectId,
            () => Vector3.Zero, 30, 0, 21.75f, 130);
        Logger.Information("[TitanHard] Registered floor stages, bomb waves and Landslide.");
        return Task.FromResult(false);
    }

    private static IEnumerable<BattleCharacter> Actors() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive);

    private static bool EncounterActive() => current != null && TreeRoot.IsRunning
        && Core.Me != null && Core.Me.IsAlive && WorldManager.ZoneId == 293 && DutyManager.InInstance
        && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene
        && Actors().Any(a => a.BaseId == Titan && (Core.Me.InCombat || a.CurrentHealthPercent < 100 || a.IsCasting));

    private static IEnumerable<Vector3> ImminentBombs()
    {
        // Snapshot remaining times within this frame. Later rows remain available
        // for escape until their turn; marking every bomb at once erases safe floor.
        var bombs = Actors().Where(a => a.BaseId == Bomb && a.CastingSpellId == Burst)
            .Select(a => new { Position = a.Location, Remaining = a.SpellCastInfo.RemainingCastTime.TotalSeconds }).ToArray();
        if (bombs.Length == 0)
            return [];
        double first = bombs.Min(b => b.Remaining);
        return bombs.Where(b => TitanHardPolicy.BombIsImminent(b.Remaining, first)).Select(b => b.Position).ToArray();
    }

    private void OnPulse(object sender, EventArgs args)
    {
        if (!EncounterActive())
        {
            movement?.Detach();
            movement = null;
            yieldedMover = false;
            // Personal death is not a wipe. Preserve the collapsed floor for a
            // resurrection, resetting only when Titan has actually returned to full HP.
            if (Actors().Any(a => a.BaseId == Titan && a.CurrentHealthPercent >= 100 && !a.IsCasting))
            {
                jumps = 0;
                jumping = false;
                landingUntil = DateTime.MinValue;
                refreshFloor = false;
            }
            return;
        }
        if (movement != null && !movement.IsCurrent)
        {
            movement.Detach();
            movement = null;
            yieldedMover = true;
        }
        if (movement == null && !yieldedMover && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new TitanHardMovement(Navigator.PlayerMover);
            Navigator.PlayerMover = movement;
        }
        movement?.CompleteEscape();

        var cast = Actors().FirstOrDefault(a => a.BaseId == Titan && a.CastingSpellId == Geocrush);
        if (cast != null)
        {
            // A transient missing cast sample must not count the same landing twice.
            if (!jumping && DateTime.UtcNow > landingUntil)
            {
                jumps = Math.Min(3, jumps + 1);
                Logger.Information($"[TitanHard] Geocrush stage {jumps}: surviving radius {TitanHardPolicy.FloorRadius(jumps)}.");
            }
            jumping = true;
            // Keep the rim hold through delayed action delivery, then refresh the
            // native floor cache once; never clear active avoids or disable SideStep.
            landingUntil = DateTime.UtcNow + cast.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(750);
            refreshFloor = true;
        }
        else
            jumping = false;
        if (refreshFloor && DateTime.UtcNow > landingUntil && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            AvoidanceManager.ResetNavigation();
            refreshFloor = false;
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        OnPulse(null, EventArgs.Empty);
        if (EncounterActive())
            await DamageMitigationSpells();
        return false;
    }

    private void OnStop(BotBase bot) => ResetActive();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != 293)
            ResetActive();
    }
    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }
    /// <summary>Releases only this encounter's input, subscriptions and spell overrides.</summary>
    public static void ResetActive()
    {
        if (current == null)
            return;
        TreeRoot.OnStop -= current.OnStop;
        GameEvents.OnMapChanged -= current.OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse -= current.OnPulse;
        current.movement?.Detach();
        foreach (var spell in current.overrides)
            SideStep.RemoveHandler(spell);
        current = null;
    }
}
