using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 57: The Vault dungeon logic.
/// </summary>
public partial class Vault : AbstractDungeon
{
    private const uint BrightsphereNpc = 4385;
    private const uint SerAdelphelNpc = 3849;
    private const uint SerGrinnauxNpc = 3850;
    private const uint SerCharibertNpc = 3642;

    private const uint AetherialTearNpc = 3293;
    private const uint DimensionalRipNpc = 2003393;
    private const uint FaithUnmovingSpell = 4135;
    private const uint DimensionalCollapseSmallSpell = 4137;
    private const uint DimensionalCollapseMediumSpell = 4138;
    private const uint DimensionalCollapseLargeSpell = 4139;
    private const uint BlackKnightsTourSpell = 4153;
    private const uint WhiteKnightsTourSpell = 4152;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    private const uint DawnKnightNpc = 3851;
    private const uint DuskKnightNpc = 3852;
    private const uint HolyFlame = 4400;
    private const uint BurningChainsAura = 769;
    private readonly VaultChainState chainState = new();

    private static readonly Vector3 SerAdelphelArenaCenter = new(0f, -292f, -100f);
    private static readonly Vector3 SerGrinnauxArenaCenter = new(0f, 0f, 72f);
    private static readonly Vector3 SerCharibertArenaCenter = new(0f, 300f, 4f);

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheVault;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        RegisterTimedCasts();

        // Heavenly Slash has no cast bar; the transformed, targetable boss is
        // base 0x1051, distinct from the original 0x104E and helper actors.
        // Its 10.2y reach includes the boss hitbox; add 0.5y and 5 degrees per side.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheQuire && !Core.Me.IsTank(),
            objectSelector: a => a.BaseId == 0x1051 && a.IsAlive && a.CanAttack,
            leashPointProducer: () => SerAdelphelArenaCenter,
            leashRadius: 40f, rotationDegrees: 0f, radius: 10.7f, arcDegrees: 100f);

        // Bright Flare reaches six yalms from the sphere center (five plus its
        // hitbox), with 0.5y clearance. November 2 captured exploded spheres
        // lingering as valid, visible but dead actors; retaining their circles
        // removed safe spread routes and caused an Execution overlap hit.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheQuire,
            objectSelector: obj => obj.NpcId == BrightsphereNpc && obj.IsAlive,
            radiusProducer: obj => 6.5f,
            priority: AvoidancePriority.Medium));

        // Execution targets one marked participant; when Rowan is marked the
        // other party members are the hazards, otherwise only the marked ally is.
        // The marker's timed delivery tail is owned by UpdateExecutionSpread.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => executionFallback && execution.Active(DateTime.UtcNow),
            objectSelector: a => a.IsValid && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                PartyManager.AllMembers.Any(p => p.ObjectId == a.ObjectId) &&
                (execution.Target == Core.Me.ObjectId || a.ObjectId == execution.Target),
            radiusProducer: _ => 5.5f,
            priority: AvoidancePriority.High));

        // The original Grinnaux can use instant Overpower before transforming.
        // Keep its 6.5y, 120-degree cleave covered while boss-local SideStep is
        // suspended; the transformed boss below has a different reach and base ID.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChapterHouse && !Core.Me.IsTank(),
            objectSelector: a => a.BaseId == 0x1053 && a.IsAlive && a.CanAttack,
            leashPointProducer: () => SerGrinnauxArenaCenter,
            leashRadius: 40f, rotationDegrees: 0f, radius: 7f, arcDegrees: 130f);

        // Boss 2
        // In general, if not tank stay out of the front to avoid AOE attacks
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChapterHouse &&
                          !Core.Me.IsTank(),
            // The transformed boss reports NPC 3639 and base 0x1054; 3850 is
            // the original phase and cannot own the instant Heavy Swing cleave.
            objectSelector: (bc) => bc.BaseId == 0x1054 && bc.IsAlive && bc.CanAttack,
            leashPointProducer: () => SerGrinnauxArenaCenter,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 10.7f,
            arcDegrees: 130.0f);

        // Boss 2 Faith Unmoving
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            // Only the no-plan fallback owns this donut. A selected staging
            // destination must not compete with an independent center pull.
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChapterHouse && (active?.faithFallback ?? false) && DateTime.UtcNow < active.faithUntil,
            objectSelector: c => c.IsValid && c.ObjectId == active.faithCaster,
            outerRadius: 40.0f,
            innerRadius: 3.0F,
            priority: AvoidancePriority.Medium);

        // Boss 2: Dimensional Rip / Dark Lightning Puddle
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<GameObject>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChapterHouse,
            objectSelector: obj => obj.NpcId == DimensionalRipNpc,
            radiusProducer: obj => 5.5f,
            priority: AvoidancePriority.Medium));

        // The November 2 retest escaped a 7.5y avoid and then suffered Torsion
        // while stationary about 7.61y from the existing tear. An 8.25y radius
        // covers the observed trigger plus more than half a yalm of clearance;
        // a seven-yalm reference alone does not account for this client result.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<GameObject>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChapterHouse,
            objectSelector: obj => obj.NpcId == AetherialTearNpc,
            radiusProducer: obj => 8.25f,
            priority: AvoidancePriority.Medium));

        // The November 1 capture kept a self-chain active for its full 15-second
        // duration at 20.1y separation. Its threshold is relative to the paired
        // actors' initial distance, so freeze that distance as the aura appears.
        // When the partner is not uniquely readable, retain the prior 20y fallback.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => chainFallback && UpdateChainSeparation(),
            objectSelector: bc => bc.ObjectId != Core.Player.ObjectId && bc.HasAura(BurningChainsAura),
            radiusProducer: bc => chainState.RadiusFor(bc.ObjectId),
            priority: AvoidancePriority.Medium));

        // Boss 3: Dawn Knight + Dusk Knight
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            // Only actual edge-spawned marchers own this moving lane. Dormant
            // knight helpers remain in the object table near the arena center.
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheChancel,
            objectSelector: bc => bc.IsValid && bc.IsVisible && marchActors.Contains(bc.ObjectId),
            // Captured march speed is about 6y/s. Cover the 2.5y actor hazard
            // plus 0.5y clearance behind it and roughly 1.5s of forward travel.
            width: 6f,
            length: 12f,
            yOffset: -3f,
            priority: AvoidancePriority.Medium);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheQuire,
            () => SerAdelphelArenaCenter,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ChapterHouse,
            () => SerGrinnauxArenaCenter,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        // The Chancel is a forty-yalm square. A circular boundary removes valid corner
        // space needed by chains and knight lanes; retain a 0.5-yalm inset at each wall.
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.TheChancel,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [SerCharibertArenaCenter],
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        // Native avoidance must finish its escape before ordinary combat movement
        // can issue another destination through the same mover.
        return faithMoving || chainMoving || executionMoving || escapeInput?.IsCurrent == true && AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetActive();
        return Task.FromResult(false);
    }

    // The avoidance pulse owns this observation, so it keeps the initial tether
    // distance even when a profile coroutine retains TreeStart. Ambiguous or
    // temporarily absent partners cannot replace a still-active captured owner.
    private bool UpdateChainSeparation()
    {
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)ZoneId ||
            WorldManager.SubZoneId != (uint)SubZoneId.TheChancel ||
            Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            !Core.Me.InCombat || !Core.Me.HasAura(BurningChainsAura))
        {
            if (chainState.PartnerId != 0)
                Logger.Information("[Vault] Holy Chain partner released.");
            chainState.Reset();
            return false;
        }

        var partyIds = PartyManager.AllMembers.Select(member => member.ObjectId).ToHashSet();
        var partners = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.ObjectId != Core.Me.ObjectId &&
                            partyIds.Contains(actor.ObjectId) && actor.HasAura(BurningChainsAura))
            .Take(2).ToArray();
        if (partners.Length == 1 && chainState.PartnerId == 0)
        {
            chainState.Observe(partners[0].ObjectId, partners[0].Distance2D(Core.Me.Location));
            if (chainState.PartnerId != 0)
                Logger.Information("[Vault] Holy Chain partner {0}; required separation {1:F2}y.",
                    chainState.PartnerId, chainState.RequiredRadius);
        }
        return true;
    }
}
