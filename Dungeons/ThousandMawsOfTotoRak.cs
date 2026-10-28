using Clio.Utilities;
using DutyMechanic.Data;
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
/// Owns Toto-Rak boss hazards through native avoidance; OrderBot retains authored
/// travel and add/tail priorities, and the combat routine retains healing/rotation.
/// </summary>
public class ThousandMawsOfTotoRak : AbstractDungeon
{
    // Global2026.10.16 name rows and October26/27 live captures distinguish name
    // IDs from model IDs. Both Coeurl encounters use442;422 belongs to another duty.
    private const uint Coeurl = 442, Graffias = 444;
    private const uint Silkscreen = 701, PodBurst = 730;
    private const uint DeadlyThrustPuddle = 2000404;
    private static ThousandMawsOfTotoRak active;
    private bool? savedSideStepEnabled;

    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1039 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter actor, uint action) => actor.IsValid && actor.IsAlive &&
        actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == action;

    /// <summary>Restores the previous SideStep state on stop, disable or hook replacement.</summary>
    public static void ResetActive()
    {
        active?.SetBossOwnership(false);
        active = null;
    }

    /// <summary>Releases boss ownership even when loading or death bypasses the encounter hook.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        if (!InDuty()) ResetActive();
        else active.UpdateOwnership();
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheThousandMawsOfTotoRak;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Release the predecessor before assigning this instance so a stale exit
        // cannot restore SideStep over its successor after a hook replacement.
        ResetActive();
        active = this;
        // Type4 Silkscreen is15y plus caster reach, width4. Extend every edge by
        // 0.5y. Historic live701 captures confirm the actor-facing line/knockback.
        // Read reach from its owner rather than baking a model-specific radius in.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(
            condition: OwnsHazards, leashPointProducer: null, leashRadius: 50f,
            rotationProducer: actor => -actor.Heading,
            scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: actor => new Vector2[]
            {
                new(2.5f, 15.5f + actor.CombatReach), new(-2.5f, 15.5f + actor.CombatReach),
                new(-2.5f, -0.5f), new(2.5f, -0.5f)
            },
            locationProducer: actor => actor.Location,
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(actor => Casting(actor, Silkscreen)));
        // Sticky Web698 is a target announcement, not a circle on the boss.
        // The actual pod730 owns the type5 circle:6y plus its reach and0.5y.
        // Untargetable pods still count; targetability is not a hazard lifecycle.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => Casting(actor, PodBurst),
            radiusProducer: actor => 6.5f + actor.CombatReach,
            locationProducer: actor => actor.Location);
        // Deadly Thrust702's60y action range is an effect/placement signal, not
        // a room-wide damage circle. Preserve the actual10y pool plus0.5y margin.
        // Persistent pools and live casts share one native unsafe-region union:
        // every selected destination must satisfy both, without competing movers.
        AvoidanceManager.AddAvoidObject<GameObject>(canRun: OwnsHazards,
            objectSelector: obj => obj.IsValid && obj.NpcId == DeadlyThrustPuddle,
            radiusProducer: _ => 10.5f);
        return Task.FromResult(false);
    }

    private void UpdateOwnership()
    {
        if (!Live()) { SetBossOwnership(false); return; }
        // Keep the lease during add/tail phases. Only an engaged named boss can
        // acquire it; nearby corridor trash must retain SideStep. The40y/8y bounds
        // isolate the current room and floor without introducing a movement owner.
        if (savedSideStepEnabled != null) return;
        SetBossOwnership(GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.CanAttack &&
                actor.InCombat && actor.NpcId is Coeurl or Graffias &&
                Math.Abs(actor.Y - Core.Me.Y) < 8 && actor.Distance2D() < 40));
    }

    private void SetBossOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[TotoRak] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            Logger.Information("[TotoRak] Boss ownership released; SideStep restored.");
        }
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        if (!InDuty()) { SetBossOwnership(false); return Task.FromResult(false); }
        active = this;
        UpdateOwnership();
        // Instant Acid Mist/Realm Shaker remain routine healing responsibilities;
        // no speculative future avoid or NPC-follow dependency starves combat.
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetBossOwnership(false);
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
