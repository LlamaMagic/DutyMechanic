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
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Garuda's pillar shelters and Hard-mode wind hazards. The profile and
/// combat routine retain eligible targets, rotation, healing and duty progression.
/// </summary>
public sealed partial class GarudaHard : AbstractDungeon
{
    // Hard-mode base IDs distinguish pillars and persistent wind helpers from
    // the boss and her sisters. Name IDs alone overlap the normal trial.
    private const uint Garuda = 0xF2, Monolith = 0xF0, Chirada = 0x61E, Suparna = 0x61F;
    private const uint Slipstream = 1382, Shriek = 1384, Song = 1390, Blast = 1385;
    private const uint Whirlwind = 1386, Eye = 1387;
    private static readonly Vector3 Center = new(0, -1.795f, 0);
    private static readonly CapabilityFlags[] PositionCapabilities =
        [CapabilityFlags.Movement, CapabilityFlags.GapCloser, CapabilityFlags.MoveBehind];
    private static GarudaHard current;
    private readonly HashSet<uint> overrides = [];
    private readonly Dictionary<uint, ShelterCast> shelters = [];
    private readonly HashSet<uint> activeWhirlwinds = [];
    private MovementGate movement;
    private bool positioning, movementLost;
    private Vector3? shelterPoint;

    // Retain scalar cast data through impact; frame-scoped actors must not survive
    // an asynchronous navigation request or a loading transition.
    private sealed record ShelterCast(Vector3 Origin, float Range, DateTime Until);

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)294;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [Blast];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        current = this;
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        // Song/Shriek are LOS shelters, Blast is unavoidable, and Eye is a donut.
        // Explicit exclusions prevent generic circles from erasing safe cover.
        foreach (var spell in new[] { Slipstream, Shriek, Song, Blast, Whirlwind, Eye })
        {
            if (SideStep.Override(spell))
                overrides.Add(spell);
        }
        // Reference radius 22; retain a half-yalm inset without clipping the pillars.
        AvoidanceHelpers.AddAvoidDonut(EncounterActive, () => Center, 60, 21.5);
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(EncounterActive,
            actor => Enemy(actor) && actor.IsCasting && actor.CastingSpellId == Slipstream,
            () => Center, 22, 0, 12.2f, 100);
        // Downburst is instant. The 11.7y/120-degree cleave adds 0.5y and five
        // degrees per side, while the actual aggro holder keeps its tank position.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(EncounterActive,
            actor => Enemy(actor) && actor.IsTargetable && !actor.IsCasting
                && actor.CurrentTargetId != 0 && actor.CurrentTargetId != 0xE0000000
                && actor.CurrentTargetId != actor.ObjectId && actor.CurrentTargetId != Core.Me.ObjectId,
            () => Center, 22, 0, 12.2f, 130);
        AvoidanceManager.AddAvoidLocation<Vector3>(EncounterActive, () => Center, 22,
            _ => 8.5f, point => point,
            // Captured helpers remain in place between six-second pulse cycles.
            // Keep their footprint until they hide or return to the off-arena park;
            // otherwise routine travel repeatedly walks back into the same tornado.
            () => Actors().Where(a => activeWhirlwinds.Contains(a.ObjectId) && a.IsVisible
                    && a.Location.Distance2D(Center) < 30)
                .Select(a => a.Location));
        // Eye's 12–25y ring is not a full circle. Inset the safe inner radius by
        // 0.5y, and cover the outer rim through the encounter's floor boundary.
        AvoidanceHelpers.AddAvoidDonut(EncounterActive,
            () => Actors().Where(a => a.CastingSpellId == Eye).Select(a => a.Location).ToArray(), 25.5, 11.5);
        Logger.Information("[GarudaHard] Registered pillar shelters and wind hazards.");
        return Task.FromResult(false);
    }

    private static IEnumerable<BattleCharacter> Actors() =>
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive);
    private static bool Enemy(BattleCharacter actor) => actor.IsValid && actor.IsAlive
        && actor.BaseId is Garuda or Chirada or Suparna;
    private static bool EncounterActive() => current != null && TreeRoot.IsRunning
        && Core.Me != null && Core.Me.IsAlive && WorldManager.ZoneId == 294 && DutyManager.InInstance
        && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene
        && Actors().Any(a => Enemy(a) && (Core.Me.InCombat || a.CurrentHealthPercent < 100 || a.IsCasting));
    private static bool InHazard() => AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location));

    private void OnPulse(object sender, EventArgs args)
    {
        if (!EncounterActive())
        {
            ResetMovement();
            ReleaseTargets();
            shelters.Clear();
            activeWhirlwinds.Clear();
            return;
        }
        if (movement != null && !movement.IsCurrent)
            LoseMovementOwnership();
        if (movement == null && !movementLost && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
            movement = new MovementGate(this);
        movement?.CompleteEscape();
        UpdateTargets();
        foreach (var actor in Actors().Where(a => a.BaseId == 0x623 && a.CastingSpellId == Whirlwind))
            activeWhirlwinds.Add(actor.ObjectId);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        OnPulse(null, EventArgs.Empty);
        if (!EncounterActive() || movement == null || movementLost)
            return false;

        var now = DateTime.UtcNow;
        foreach (var actor in Actors().Where(a => Enemy(a) && a.CastingSpellId is Song or Shriek))
        {
            // A quarter-second impact grace covers effect delivery after cast end.
            shelters[actor.ObjectId] = new(actor.Location, actor.CastingSpellId == Song ? 31.7f : 24.7f,
                now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(250));
        }
        foreach (var id in shelters.Where(pair => pair.Value.Until < now).Select(pair => pair.Key).ToArray())
            shelters.Remove(id);

        if (shelters.Count == 0)
        {
            ReleasePosition();
            await DamageMitigationSpells();
            return false;
        }

        // Earlier lethal telegraphs own native escape. A shelter is accepted only
        // when it also satisfies all active hazards and all concurrent LOS casts.
        if (AvoidanceManager.IsRunningOutOfAvoid || InHazard() || MovementManager.GenerallyOutOfControl)
        {
            ReleasePosition();
            return false;
        }
        var pillars = Actors().Where(a => a.BaseId == Monolith)
            .Select(a => new GarudaHardPolicy.Pillar(a.Location, a.CombatReach)).ToArray();
        var casts = shelters.Values.Select(c => new GarudaHardPolicy.Cast(c.Origin, c.Range)).ToArray();
        bool Safe(Vector3 point) => point.Distance2D(Center) <= 21.5f
            && !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point))
            && GarudaHardPolicy.Covered(point, pillars, casts);
        if (!shelterPoint.HasValue || !Safe(shelterPoint.Value))
            shelterPoint = GarudaHardPolicy.SelectCover(Core.Me.Location, pillars, casts, Safe);
        if (!shelterPoint.HasValue)
        {
            ReleasePosition();
            return false; // No surviving cover: retain healing and normal recovery.
        }
        if (!positioning)
            Logger.Information($"[GarudaHard] Taking pillar cover at {shelterPoint.Value}.");
        positioning = true;
        foreach (var capability in PositionCapabilities)
            CapabilityManager.Update(CapabilityHandle, capability, () => positioning && EncounterActive(), "Garuda pillar cover");
        movement.Approach(shelterPoint.Value, 0.4f);
        // Holding cover must not starve healing or mitigation hooks.
        return false;
    }

    private void ReleasePosition()
    {
        if (positioning)
        {
            positioning = false;
            movement?.ReleasePosition();
            foreach (var capability in PositionCapabilities)
                CapabilityManager.Clear(CapabilityHandle, capability, "Garuda shelter ended");
            Logger.Information("[GarudaHard] Released pillar cover.");
        }
        shelterPoint = null;
    }

    private void ResetMovement()
    {
        ReleasePosition();
        movement?.Detach();
        movement = null;
        movementLost = false;
    }
    private void LoseMovementOwnership()
    {
        ResetMovement();
        movementLost = true;
    }
    private void OnStop(BotBase bot) => ResetActive();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != 294)
            ResetActive();
    }
    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }
    /// <summary>Releases only this encounter's navigation, capability and spell overrides.</summary>
    public static void ResetActive()
    {
        if (current == null)
            return;
        TreeRoot.OnStop -= current.OnStop;
        GameEvents.OnMapChanged -= current.OnMapChanged;
        ff14bot.NeoProfile.BotEvents.OnPulse -= current.OnPulse;
        current.ResetMovement();
        current.ReleaseTargets();
        current.shelters.Clear();
        current.activeWhirlwinds.Clear();
        foreach (var spell in current.overrides)
            SideStep.RemoveHandler(spell);
        current.overrides.Clear();
        current = null;
    }
}
