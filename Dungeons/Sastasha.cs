using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Supplies Sastasha boss hazards and add priorities while native avoidance and
/// OrderBot retain movement and combat execution. The profile owns doors and routing.
/// </summary>
public class Sastasha : AbstractDungeon
{
    // Global action rows and the October 23 captures identify these NameIDs and
    // casts. Keep them duty-local: Madison's reavers also appear as ordinary trash.
    private const uint Chopper = 1204, Madison = 1382, Denn = 1206;
    private const uint ChargedWhisker = 351, Hydroball = 556;
    private static Sastasha active;
    private bool? savedSideStepEnabled;
    private uint priorityTargetId;
    private AddTargetingProvider addTargeting;
    private bool targetingSuperseded;

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1036 && Core.Me != null &&
        Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat;

    /// <summary>Restores Sastasha's SideStep state and releases its targeting adapter on stop or disable.</summary>
    public static void ResetActive()
    {
        active?.Reset();
        active = null;
    }

    /// <summary>Releases boss ownership when death, loading or duty exit bypasses the normal encounter hook.</summary>
    public static void CheckLifecycle()
    {
        if (active != null && !Live()) ResetActive();
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Sastaha;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        Reset();
        // Charged Whisker is a 3y circle plus Chopper's 3.15y hitbox. The
        // additional 0.5y clearance prevents a nominal edge from counting as safe.
        // Native avoidance remains the only evasive mover; no NPC proxy is used.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Live() && savedSideStepEnabled != null,
            radius: 6.65f,
            objectSelector: a => a.IsValid && a.IsAlive && a.NpcId == Chopper &&
                a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId == ChargedWhisker);

        // Hydroball is a 90-degree cone reaching 8y including Denn's hitbox.
        // Add 0.5y radial clearance and a modest 5-degree allowance on either edge;
        // the October 23 capture corroborates frontal geometry and rear safety.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Live() && savedSideStepEnabled != null,
            objectSelector: a => a.IsValid && a.IsAlive && a.NpcId == Denn &&
                a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId == Hydroball,
            leashPointProducer: () => Core.Me.Location, leashRadius: 40f,
            rotationDegrees: 0f, radius: 8.5f, arcDegrees: 100f);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        if (!Live()) { Reset(); return Task.FromResult(false); }
        active = this;

        // The October 27 dog phase exposed a same-tick POI fight: OrderBot replaced
        // our add with Madison. Supply priority through its targeting provider instead,
        // leaving POI selection, combat scheduling and movement entirely with OrderBot.
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsTargetable && a.CanAttack
                && Math.Abs(a.Y - Core.Me.Y) < 8 && a.Distance2D() < 40).ToArray();
        var boss = actors.FirstOrDefault(a => a.InCombat && a.NpcId is Chopper or Madison or Denn);
        // Both Madison encounters share a NameID. Releasing when he becomes
        // ineligible hands trash back to SideStep instead of owning the whole zone.
        SetBossOwnership(boss != null);
        var adds = actors.Where(a => boss != null && a.Distance2D(boss.Location) < 30
            && (boss.NpcId == Madison ? a.NpcId is 342 or 1205 : boss.NpcId == Denn && a.NpcId == 1207)
            && a.InLineOfSight()).ToArray();
        var add = adds.FirstOrDefault(a => a.ObjectId == priorityTargetId)
            ?? adds.OrderBy(a => a.Distance2D()).FirstOrDefault();
        UpdatePriorityTarget(add);
        if (boss == null) targetingSuperseded = false;

        return Task.FromResult(false);
    }

    private void SetBossOwnership(bool enabled)
    {
        // Save the real prior setting so lifecycle cleanup never overrides an
        // explicit disabled state. Entry normally enables SideStep for trash.
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Sastasha] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            Logger.Information("[Sastasha] Boss ownership released; SideStep restored.");
        }
    }

    private void Reset()
    {
        SetBossOwnership(false);
        ReleasePriorityTarget();
        targetingSuperseded = false;
    }

    private void UpdatePriorityTarget(BattleCharacter add)
    {
        if (addTargeting != null && !ReferenceEquals(CombatTargeting.Instance.Provider, addTargeting))
        {
            // A successor owns the provider now. Do not re-wrap it every tick or
            // overwrite it during cleanup; defer until the next encounter boundary.
            targetingSuperseded = true;
            ReleasePriorityTarget();
        }
        if (add == null) { ReleasePriorityTarget(); return; }
        if (targetingSuperseded) return;
        if (addTargeting == null)
        {
            // A locked provider is an explicit existing owner, not permission to
            // steal its slot. Native avoidance and ordinary combat remain available.
            if (CombatTargeting.Instance.Locked) return;
            addTargeting = new AddTargetingProvider(this, CombatTargeting.Instance.Provider);
            CombatTargeting.Instance.Provider = addTargeting;
        }
        priorityTargetId = add.ObjectId;
    }

    private void ReleasePriorityTarget()
    {
        priorityTargetId = 0;
        if (addTargeting != null && ReferenceEquals(CombatTargeting.Instance.Provider, addTargeting))
        {
            // We acquired an unlocked slot. Preserve a later lock while restoring
            // our predecessor, and never touch a different successor provider.
            bool locked = CombatTargeting.Instance.Locked;
            CombatTargeting.Instance.Locked = false;
            CombatTargeting.Instance.Provider = addTargeting.Previous;
            CombatTargeting.Instance.Locked = locked;
        }
        addTargeting = null;
    }

    // This adapter only changes the first eligible combat candidate. It delegates
    // all other ranking to the previous provider and resolves actors afresh, avoiding
    // stale native wrappers and any direct target, POI or movement commands.
    private sealed class AddTargetingProvider(Sastasha owner, ITargetingProvider previous) : ITargetingProvider
    {
        internal ITargetingProvider Previous => previous;

        /// <inheritdoc/>
        public List<BattleCharacter> GetObjectsByWeight()
        {
            var ranked = previous?.GetObjectsByWeight() ?? [];
            if (!Live() || owner.priorityTargetId == 0) return ranked;
            var add = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .FirstOrDefault(a => a.ObjectId == owner.priorityTargetId && a.IsValid &&
                    a.IsAlive && a.IsTargetable && a.CanAttack && a.InLineOfSight() &&
                    Math.Abs(a.Y - Core.Me.Y) < 8 && a.Distance2D() < 40);
            if (add == null) return ranked;
            return new[] { add }.Concat(ranked.Where(a => a.ObjectId != add.ObjectId)).ToList();
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        Reset();
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
