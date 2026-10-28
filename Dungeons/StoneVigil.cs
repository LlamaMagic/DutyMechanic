using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
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
/// Supplies Stone Vigil boss hazards to RB's native avoidance owner; OrderBot
/// retains the authored route and the combat routine retains normal combat.
/// </summary>
public partial class StoneVigil : AbstractDungeon
{
    private const uint ChudoYudo = 1677, Koshchei = 1678, Isgebind = 1680, Maelstrom = 9910;
    private const uint LionsBreath = 902, Swinge = 903, SpikedTail = 28732, SonicStorm = 29053;
    private const uint FrostBreath = 1022, SheetOfIce = 1023, SheetOfIceHelper = 1024;
    private const uint RimeWreath = 1025, Cauterize = 1026;
    // October 28 captures distinguish the moving damage actors from base 14544
    // choreography helpers, which can remain parked at the room center for a wave.
    private const uint MaelstromDamageBase = 14536, KoshcheiBossBase = 14535, IsgebindBossBase = 1455;
    private static readonly Vector3 IsgebindArenaCenter = new(0f, 0f, -248f);
    private static readonly Vector3 KoshcheiArenaCenter = new(40f, 4f, -80f);
    private static StoneVigil active;
    private bool? savedSideStepEnabled;

    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1042 && Core.Me != null && Core.Me.IsValid;
    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;
    private bool OwnsHazards() => ReferenceEquals(active, this) && Live() && savedSideStepEnabled != null;
    private static bool Casting(BattleCharacter actor, uint action) => actor.IsValid && actor.IsAlive &&
        actor.IsCasting && actor.SpellCastInfo.IsValid && actor.CastingSpellId == action;

    /// <summary>Restores the captured SideStep state on stop, disable or handler replacement.</summary>
    public static void ResetActive()
    {
        active?.SetBossOwnership(false);
        active = null;
    }

    /// <summary>Maintains ownership when a yielding profile tag bypasses the encounter hook.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        if (!InDuty()) active.SetBossOwnership(false);
        else active.UpdateOwnership();
        active.UpdateEscapeInput();
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheStoneVigil;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [SpikedTail];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [RimeWreath];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        // Global 2026.10.16 action rows and encounter descriptions agree on these
        // cones. Type 3 range includes caster reach: 6+4.25 and 20+7 respectively.
        // Add 0.5y clearance and 5deg per edge to the provisional 120deg breath fans.
        // Swinge's angle remains uncertain: retain the existing conservative 125deg
        // until live impact evidence supports narrowing it. Never leash Chudo to
        // Isgebind's distant room as the old non-tank registration did.
        AddCone(LionsBreath, 10.75f, 130f);
        AddCone(Swinge, 40.5f, 125f);
        AddCone(FrostBreath, 27.5f, 130f);
        AddRearPreference(ChudoYudo, 10.75f);
        AddRearPreference(Isgebind, 27.5f);

        // Ground-target actions use their cast destination, including untargetable
        // flight helpers. Actor-centered circles would erase unrelated safe floor.
        AddGroundCircle(SonicStorm, 6.5f);
        AddGroundCircle(SheetOfIce, 5.5f);
        AddGroundCircle(SheetOfIceHelper, 5.5f);
        // Type 4 Cauterize is 48x20 from the caster, without added model reach.
        // Preserve all simultaneous helper lanes and ground circles in one native
        // union; a half-yalm edge margin does not turn staggered waves into walls.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && Casting(actor, Cauterize), width: 21f, length: 48.5f);
        // October 28 captures show westward tornadoes moving from X57 to X30.4.
        // Block their remaining path with radius 3 + 0.5y clearance, retaining it
        // through the 0.6s pauses between steps. Release already-passed eastern floor:
        // keeping whole lanes during wave overlap erased every cross-lane exit.
        // Scalar position snapshots also prevent RB's BattleCharacter collection
        // overload from attracting escape movement toward an incoming tornado.
        AvoidanceManager.AddAvoidPolygon<Vector3>(condition: OwnsHazards,
            leashPointProducer: () => Core.Me.Location, leashRadius: 60f,
            rotationProducer: _ => 0f, scaleProducer: _ => 1f, heightProducer: _ => 15f,
            pointsProducer: MaelstromRemainingLane,
            locationProducer: center => new Vector3(0f, center.Y, center.Z),
            collectionProducer: () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(IsMovingMaelstrom)
                .Select(actor => actor.Location).ToArray());
        // The 20y square combat floor is centered at (40,-80); the October 28 capture
        // hit the west wall at X29.9 while native avoidance sought the lane's X26.9
        // end cap. Keep a 0.5y inset and a 120y outer extent so an off-floor shortcut
        // cannot beat crossing onto the now-cleared floor behind the outgoing pair.
        // Only the actual live boss owns this boundary; corridor navigation is free.
        AvoidanceHelpers.AddAvoidSquareDonut(canRun: OwnsKoshcheiArena,
            innerWidth: 19f, innerHeight: 19f, outerWidth: 120f, outerHeight: 120f,
            collectionProducer: () => OwnsKoshcheiArena() ? [KoshcheiArenaCenter] : []);
        // The final room's documented 40y square is centered at (0,-248), consistent
        // with the installed arena marker and observed opposite flight approaches.
        // Three captures escaped toward blocked outer edges instead of crossing
        // Cauterize. Keep a 0.5y wall inset so native avoidance must find safe floor
        // inside the rectangle; NPC locations are never movement destinations.
        // The 120y outer extent covers the 50y line-avoid search around this small room.
        AvoidanceHelpers.AddAvoidSquareDonut(canRun: OwnsIsgebindArena,
            innerWidth: 39f, innerHeight: 39f, outerWidth: 120f, outerHeight: 120f,
            collectionProducer: () => OwnsIsgebindArena() ? [IsgebindArenaCenter] : []);
        // Landing (1027) has no confirmed avoid geometry; do not invent a hazard.
        return Task.FromResult(false);
    }

    private bool OwnsIsgebindArena() => OwnsHazards() &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
            actor.IsValid && actor.IsAlive && actor.NpcId == Isgebind && actor.BaseId == IsgebindBossBase);

    private bool OwnsKoshcheiArena() => OwnsHazards() &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
            actor.IsValid && actor.IsAlive && actor.NpcId == Koshchei && actor.BaseId == KoshcheiBossBase);

    // These are observed motion endpoints, not assumed arena walls. The 0.15y
    // tolerance ignores staging jitter and releases both visual/helper duplicates
    // at their settled endpoint without leaving a stale lane between wave sets.
    private const float MaelstromStartX = 57f, MaelstromEndX = 30.4f;
    private const float MaelstromClearance = 3.5f, MaelstromSettledTolerance = 0.15f;
    // Scalar snapshots preserve one pulse's position through polygon construction;
    // no mutable actor wrapper or melee-attraction target reaches native avoidance.
    private static Vector2[] MaelstromRemainingLane(Vector3 actorPosition) =>
    [
        new(MaelstromEndX - MaelstromClearance, -MaelstromClearance),
        new(actorPosition.X + MaelstromClearance, -MaelstromClearance),
        new(actorPosition.X + MaelstromClearance, MaelstromClearance),
        new(MaelstromEndX - MaelstromClearance, MaelstromClearance),
    ];
    private bool IsMovingMaelstrom(BattleCharacter actor) => OwnsHazards() && actor.IsValid &&
        actor.IsAlive && actor.NpcId == Maelstrom && actor.BaseId == MaelstromDamageBase &&
        actor.Location.X < MaelstromStartX - MaelstromSettledTolerance &&
        actor.Location.X > MaelstromEndX + MaelstromSettledTolerance;

    private void AddCone(uint action, float radius, float arc) =>
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && Casting(actor, action),
            leashPointProducer: () => Core.Me.Location, leashRadius: 60f,
            rotationDegrees: 0f, radius: radius, arcDegrees: arc);

    private void AddGroundCircle(uint action, float radius) =>
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: OwnsHazards,
            objectSelector: actor => OwnsHazards() && Casting(actor, action),
            radiusProducer: _ => radius, locationProducer: actor => actor.SpellCastInfo.CastLocation);

    private void AddRearPreference(uint npcId, float radius) =>
        // Fast breath casts warrant pre-positioning for non-tanks. Only exclude
        // the forward quadrant between casts; the cast's actual cone takes over
        // during attacks, and flight cannot leave a stale full-room body avoid.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => OwnsHazards() && !Core.Me.IsTank(),
            objectSelector: actor => OwnsHazards() && !Core.Me.IsTank() && actor.IsValid &&
                actor.IsAlive && actor.IsTargetable && actor.CanAttack && actor.InCombat &&
                !actor.IsCasting && actor.NpcId == npcId,
            leashPointProducer: () => Core.Me.Location, leashRadius: 60f,
            rotationDegrees: 0f, radius: radius, arcDegrees: 90f);

    private void UpdateOwnership()
    {
        if (!Live()) { SetBossOwnership(false); return; }
        // Retain through Isgebind's untargetable flight, but do not acquire from
        // inactive room actors while ordinary corridor trash is being fought.
        if (savedSideStepEnabled != null) return;
        SetBossOwnership(GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.CanAttack &&
                actor.InCombat && actor.NpcId is ChudoYudo or Koshchei or Isgebind &&
                Math.Abs(actor.Y - Core.Me.Y) < 8 && actor.Distance2D() < 40));
    }

    private void SetBossOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[StoneVigil] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            ReleaseEscapeInput();
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            Logger.Information("[StoneVigil] Boss ownership released; SideStep restored.");
        }
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (!InDuty()) { SetBossOwnership(false); return false; }
        if (!ReferenceEquals(active, this)) return false;
        UpdateOwnership();
        UpdateEscapeInput();
        await TankBusterSpells();
        await DamageMitigationSpells();
        return false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetBossOwnership(false);
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
