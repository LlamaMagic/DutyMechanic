using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Owns Sohm Al boss hazards and target-specific Mad Dash participation. The profile
/// retains dragon gates/add priorities, native avoidance chooses dodges, and ordinary
/// combat remains with the routine. Historical October captures inform the candidate;
/// qualification and unresolved marker/object evidence are recorded in SohmAlAudit.md.
/// </summary>
public partial class SohmAl : AbstractDungeon
{
    private const uint Raskovnik = 3791, Myath = 3793, Tioman = 3798;
    private static readonly Vector3 RaskovnikCenter = new(-127, 12, 168);
    private static readonly Vector3 MyathCenter = new(158, 137.608f, -94);
    private static readonly Vector3 TiomanCenter = new(-103, 348.164f, -395);
    private static SohmAl active;
    private bool? savedSideStepEnabled;
    private uint encounter;
    private uint stackTarget;
    private DateTime stackUntil;
    private uint spreadTarget;
    private DateTime spreadUntil;
    private Vector3? stackDestination;

    // Same-name untargetable helpers use base0x1B2. Only the actual boss acquires the
    // lease; Tioman retains it through invincible wings while the living boss remains.
    private static bool IsBoss(BattleCharacter a) => a.BaseId is 0xE8F or 0xE91 or 0xE96;
    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1064 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter a) => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid;
    private Vector3 ArenaCenter => encounter == Raskovnik ? RaskovnikCenter : encounter == Myath ? MyathCenter : TiomanCenter;
    // Reference floor radii23/30/27, inset half a yalm. Live wall clearance remains
    // a diagnostic acceptance item; never infer a mesh defect from this boundary alone.
    private float ArenaRadius => encounter == Raskovnik ? 22.5f : encounter == Myath ? 29.5f : 26.5f;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.SohmAl;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [3803]; // Overbite; no invented instant-cast timer.
    /// <inheritdoc/>
    // Leafstorm3798 has a50y room-wide effect in the current client, corroborated
    // by the historical boss cast and encounter guide. Request existing mitigation;
    // an arena-erasing avoid would leave no safe destination. Live effect capture
    // remains tracked separately from this strong semantic classification.
    protected override HashSet<uint> SpellsToMitigate { get; } = [5011, 3798, 3810, 4995, 3812];

    /// <summary>Releases this dungeon's input and capability leases on stop or replacement.</summary>
    public static void ResetActive()
    {
        active?.SetOwnership(false);
        active = null;
    }

    /// <summary>Keeps cleanup and helper discovery active through yielding profile hooks.</summary>
    public static void CheckLifecycle() => active?.Pulse();

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        return Task.FromResult(false);
    }

    private void Pulse()
    {
        if (!Live()) { SetOwnership(false); return; }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && Math.Abs(a.Y - Core.Me.Y) < 10).ToArray();
        var boss = actors.FirstOrDefault(a => IsBoss(a) && a.Distance2D() < 65 &&
            (savedSideStepEnabled != null || a.IsVisible && a.IsTargetable && a.CanAttack && a.InCombat));
        if (boss == null) { SetOwnership(false); return; }
        if (savedSideStepEnabled != null && encounter != boss.NpcId) SetOwnership(false);
        encounter = boss.NpcId;
        SetOwnership(true);
        RegisterHazards();
        UpdateInputOwner();
        var now = DateTime.UtcNow;
        UpdateHazards(actors, boss, now);
        if (Casting(boss) && boss.CastingSpellId == ActionId.Stack)
        {
            if (stackTarget != boss.SpellCastInfo.TargetId) stackDestination = null;
            stackTarget = boss.SpellCastInfo.TargetId;
            // October20 casts last4.7s and alternate with spreads. There is no
            // multi-hit lease: retain only a short effect-delivery tail after this cast.
            stackUntil = now + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.35);
        }
        else if (now >= stackUntil || Casting(boss) || stackUntil - now > TimeSpan.FromSeconds(.6))
        {
            stackTarget = 0;
            stackDestination = null;
            ReleaseStack();
        }
        CaptureUnresolvedSignals(actors, now);
        inputOwner?.CompleteEscape();
    }

    private void SetOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[SohmAl] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled)
        {
            ReleaseStack();
            inputOwner?.Detach();
            inputOwner = null;
            hazards.Clear();
            hadMeteorMarker = false;
            meteorPlacementUntil = DateTime.MinValue;
            ReleaseHazardRegistrations();
            stackTarget = 0;
            stackUntil = DateTime.MinValue;
            spreadTarget = 0;
            spreadUntil = DateTime.MinValue;
            stackDestination = null;
            signalState.Clear();
            nextSignalSample = DateTime.MinValue;
            if (savedSideStepEnabled != null)
            {
                if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
                savedSideStepEnabled = null;
                Logger.Information("[SohmAl] Boss ownership released; SideStep restored.");
            }
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!ReferenceEquals(active, this)) return false;
        Pulse();
        if (!OwnsHazards()) return false;
        await TankBusterSpells();
        await DamageMitigationSpells();
        return PositionForStack();
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetOwnership(false);
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
