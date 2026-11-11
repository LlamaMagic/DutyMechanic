using Clio.Utilities;
using DutyMechanic.Data;
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
/// Lv. 59: The Great Gubal Library dungeon logic.
/// </summary>
public partial class GreatGubalLibrary : AbstractDungeon
{
    private static GreatGubalLibrary active;
    private DateTime nextOilReadErrorUtc;
    private DateTime repelBoundaryUntilUtc;
    private bool ownsWinterOverride;
    private bool ownsInnerLiquefyOverride;
    private bool ownsOuterLiquefyOverride;
    private bool ownsDisclosureOverride;
    private bool ownsDeathRayOverride;
    private bool ownsTailSmashOverride;
    private bool? bossSideStep;
    private readonly HashSet<uint> ownedBibliotaphOverrides = [];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheGreatGubalLibrary;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        ResetActive();
        active = this;
        // Two November 10 captures show SideStep treating the arena-wide ice
        // application as an escapable rectangle and repeatedly finding no path.
        // The freeze itself must not suppress movement while Disclosure develops.
        ownsWinterOverride = LlamaLibrary.Helpers.SideStep.Override(EnemyAction.WordsOfWinter);

        // The live helpers originate at the long ends of the floor and face
        // inward in both mirrors. Own each lane once; do not register the same
        // tell through SideStep and DutyMechanic at the same time.
        ownsInnerLiquefyOverride = LlamaLibrary.Helpers.SideStep.Override(EnemyAction.InnerLiquefy);
        ownsOuterLiquefyOverride = LlamaLibrary.Helpers.SideStep.Override(EnemyAction.OuterLiquefy);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HallofMagicks && ownsInnerLiquefyOverride,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.InnerLiquefy,
            width: 8f, length: 50f, priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HallofMagicks && ownsOuterLiquefyOverride,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.OuterLiquefy,
            width: 7f, length: 50f, priority: AvoidancePriority.High);

        // Two captured native escapes failed at the book's collision boundary.
        // The encounter-local staged route owns this mechanic instead.
        ownsDisclosureOverride = LlamaLibrary.Helpers.SideStep.Override(EnemyAction.Disclosure);

        // Byblos
        // The November 10 capture showed oil tether 3 to Rowan in one wave and
        // to an NPC in another. Only the carrier should be drawn toward Byblos;
        // an unrelated visible orb must not override the player's add movement.
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera && HasPlayerOilTether(),
            objectSelector: c => c.NpcId == EnemyNpc.Byblos,
            outerRadius: 40.0f,
            innerRadius: 3.0F,
            priority: AvoidancePriority.Medium);

        // Two simultaneous oils can target Rowan and an NPC. Classify each
        // tether separately: avoiding every oil while carrying one would fight
        // delivery, while ignoring the NPC's oil risks its source-modeled 3y
        // explosion. Verify the radius and handoff in a recorded visit.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera,
            objectSelector: IsOtherPartyOil,
            radiusProducer: _ => 3f);

        // Page 64's live casts expose actor origin and heading; route their
        // narrow line through the same native avoidance owner as oil delivery.
        ownsDeathRayOverride = LlamaLibrary.Helpers.SideStep.Override(EnemyAction.DeathRay);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera && ownsDeathRayOverride,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.BaseId == 0x10B7 && actor.CastingSpellId == EnemyAction.DeathRay,
            width: 3f, length: 26f, priority: AvoidancePriority.High);

        // The November 11 capture showed stationary Tome Wind actors becoming
        // visible in waves and overlapping oil. The source models a 3y hazard;
        // use native avoidance while visible, pending a live radius and handoff test.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera,
            objectSelector: actor => actor.IsValid && actor.IsVisible && actor.BaseId == EnemyNpc.TomeWindBaseId,
            radiusProducer: _ => 3f);

        // Tail Smash is a rear cone: the November 11 capture hit Rowan behind
        // Byblos during her cast. Rotate from the boss heading and use the
        // source's 12y/90-degree shape with a small clearance margin; verify
        // cast interruption, effect timing and escape cleanup in the next visit.
        ownsTailSmashOverride = LlamaLibrary.Helpers.SideStep.Override(EnemyAction.TailSmash);
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera && ownsTailSmashOverride,
            objectSelector: actor => actor.IsValid && actor.BaseId == 0xE83 && actor.IsCasting && actor.CastingSpellId == EnemyAction.TailSmash,
            leashPointProducer: () => ArenaCenter.Byblos, leashRadius: 40f,
            rotationDegrees: 180f, radius: 12.5f, arcDegrees: 100f, priority: AvoidancePriority.High);

        // Page Tear is an uncast frontal cleave, so cast-only avoidance arrives
        // too late. Keep non-targeted party members out of the modeled 8y/90-degree
        // front sector while Byblos is not casting; never chase the boss's facing
        // when Rowan owns its target. The 0.5y/five-degree margins need live testing.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera,
            objectSelector: actor => actor.IsValid && actor.IsAlive && actor.IsTargetable && actor.BaseId == 0xE83 &&
                !actor.IsCasting && actor.CurrentTargetId != 0 && actor.CurrentTargetId != 0xE0000000 &&
                actor.CurrentTargetId != Core.Player.ObjectId,
            leashPointProducer: () => ArenaCenter.Byblos, leashRadius: 40f,
            rotationDegrees: 0f, radius: 8.5f, arcDegrees: 100f, priority: AvoidancePriority.High);

        // Keep the two alternating floor shapes with the same native avoidance
        // owner as overlapping orbs and adds. Cast-relative geometry is a first
        // trial; delayed resolution and the reverse order still need live evidence.
        foreach (uint action in new[] { EnemyAction.DeepDarkness, EnemyAction.MagicBurst, EnemyAction.VoidSpark, EnemyAction.AbyssalCharge, EnemyAction.VoidBlizzard })
            if (LlamaLibrary.Helpers.SideStep.Override(action)) ownedBibliotaphOverrides.Add(action);
        bool BibliotaphActive() => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.RhapsodiesQuadrangle;
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => BibliotaphActive() && ownedBibliotaphOverrides.Contains(EnemyAction.DeepDarkness),
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.DeepDarkness,
            outerRadius: 25, innerRadius: 11.5, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => BibliotaphActive() && ownedBibliotaphOverrides.Contains(EnemyAction.MagicBurst),
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.MagicBurst,
            radiusProducer: _ => 15f);
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => BibliotaphActive() && ownedBibliotaphOverrides.Contains(EnemyAction.VoidSpark),
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.VoidSpark,
            radiusProducer: _ => 8f);
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => BibliotaphActive() && ownedBibliotaphOverrides.Contains(EnemyAction.AbyssalCharge),
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.AbyssalCharge,
            width: 4f, length: 41f, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => BibliotaphActive() && ownedBibliotaphOverrides.Contains(EnemyAction.VoidBlizzard),
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.VoidBlizzard,
            radiusProducer: _ => 5f, locationProducer: actor => actor.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HallofMagicks && DateTime.UtcNow >= repelBoundaryUntilUtc,
            () => ArenaCenter.DemonTome,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        // Repel legitimately lands against the rectangular arena's long wall.
        // The 19y circular approximation then triggered an extra escape and a
        // jump before Disclosure; allow the actual wall/corners during that handoff.
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.HallofMagicks && DateTime.UtcNow < repelBoundaryUntilUtc,
            () => ArenaCenter.DemonTome,
            outerRadius: 90.0f,
            innerRadius: 22.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AstrologyandAstromancyCamera,
            () => ArenaCenter.Byblos,
            outerRadius: 90.0f,
            innerRadius: 22f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.RhapsodiesQuadrangle,
            () => ArenaCenter.TheEverlivingBibliotaph,
            outerRadius: 90.0f,
            innerRadius: 22.0f,
            priority: AvoidancePriority.High);

        return false;
    }

    private bool HasPlayerOilTether()
    {
        try
        {
            return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
                actor != null && actor.IsValid && actor.IsVisible && actor.BaseId == EnemyNpc.WhaleOilBaseId &&
                actor.VfxContainer.IsValid && (actor.VfxContainer.Tethers?.Any(tether =>
                    tether.Id == EnemyTether.WhaleOil && tether.TargetId == Core.Player.ObjectId) ?? false));
        }
        catch (Exception error)
        {
            // An invalidated VFX wrapper is not proof of a player tether. Release
            // the attraction and retry on the next pulse instead of interrupting combat.
            if (DateTime.UtcNow >= nextOilReadErrorUtc)
            {
                nextOilReadErrorUtc = DateTime.UtcNow.AddSeconds(5);
                Logger.Warning($"[Gubal] Oil tether read failed: {error.GetType().Name}.");
            }
            return false;
        }
    }

    private bool IsOtherPartyOil(BattleCharacter actor)
    {
        try
        {
            return actor != null && actor.IsValid && actor.IsVisible && actor.BaseId == EnemyNpc.WhaleOilBaseId &&
                actor.VfxContainer.IsValid && (actor.VfxContainer.Tethers?.Any(tether =>
                    tether.Id == EnemyTether.WhaleOil && tether.TargetId != Core.Player.ObjectId &&
                    PartyManager.AllMembers?.Any(member => member.ObjectId == tether.TargetId) == true) ?? false);
        }
        catch (Exception error)
        {
            // A despawned VFX wrapper is not a confirmed tether; retry on the
            // next pulse rather than interrupting combat or guessing its target.
            if (DateTime.UtcNow >= nextOilReadErrorUtc)
            {
                nextOilReadErrorUtc = DateTime.UtcNow.AddSeconds(5);
                Logger.Warning($"[Gubal] Oil tether read failed: {error.GetType().Name}.");
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        UpdateBossOwnership();
        UpdateRepelBoundary();
        return Task.FromResult(UpdateDisclosure() || UpdateHeadDown() || UpdateTowers());
    }

    private void UpdateRepelBoundary()
    {
        if (!Core.Player.InCombat || WorldManager.SubZoneId != (uint)SubZoneId.HallofMagicks)
        {
            repelBoundaryUntilUtc = DateTime.MinValue;
            return;
        }
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false))
        {
            if (!actor.IsValid || !actor.IsCasting) continue;
            if (actor.CastingSpellId == EnemyAction.Repel)
                repelBoundaryUntilUtc = DateTime.UtcNow.AddSeconds(6);
            else if (actor.CastingSpellId == EnemyAction.Disclosure && actor.SpellCastInfo.IsValid && DateTime.UtcNow < repelBoundaryUntilUtc)
                repelBoundaryUntilUtc = DateTime.UtcNow + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.5);
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        if (ReferenceEquals(active, this)) ResetActive();
        return Task.FromResult(false);
    }

    /// <summary>Releases encounter movement and owned SideStep state on exit or stop.</summary>
    public static void ResetActive()
    {
        if (active == null) return;
        active.ReleaseBossOwnership();
        active.ReleaseDisclosure();
        active.ReleaseHeadDown();
        active.ReleaseTowers();
        if (active.ownsWinterOverride)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(EnemyAction.WordsOfWinter);
        if (active.ownsInnerLiquefyOverride)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(EnemyAction.InnerLiquefy);
        if (active.ownsOuterLiquefyOverride)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(EnemyAction.OuterLiquefy);
        if (active.ownsDisclosureOverride)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(EnemyAction.Disclosure);
        if (active.ownsDeathRayOverride)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(EnemyAction.DeathRay);
        if (active.ownsTailSmashOverride)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(EnemyAction.TailSmash);
        foreach (uint action in active.ownedBibliotaphOverrides)
            LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
        active.ownedBibliotaphOverrides.Clear();
        active.ownsWinterOverride = false;
        active.ownsInnerLiquefyOverride = false;
        active.ownsOuterLiquefyOverride = false;
        active.ownsDisclosureOverride = false;
        active.ownsDeathRayOverride = false;
        active.ownsTailSmashOverride = false;
        active = null;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Demon Tome.
        /// </summary>
        public const uint DemonTome = 3923;

        /// <summary>
        /// Second Boss: Byblos.
        /// </summary>
        public const uint Byblos = 3925;

        /// <summary>
        /// Second Boss: Whale Oil.
        /// </summary>
        public const uint WhaleOil = 3929;

        /// <summary>Whale Oil's distinct object base ID; NPC and base IDs are separate RB fields.</summary>
        public const uint WhaleOilBaseId = 0x10C4;

        /// <summary>Stationary Byblos wind hazard base ID.</summary>
        public const uint TomeWindBaseId = 0x10C5;

        /// <summary>
        /// Third Boss: The Everliving Bibliotaph.
        /// </summary>
        public const uint TheEverlivingBibliotaph = 3930;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Demon Tome.
        /// </summary>
        public static readonly Vector3 DemonTome = new(0f, 0f, 0f);

        /// <summary>
        /// Second Boss: Byblos.
        /// </summary>
        public static readonly Vector3 Byblos = new(177.7828f, -8f, 27.11523f);

        /// <summary>
        /// Third Boss: The Everliving Bibliotaph.
        /// </summary>
        public static readonly Vector3 TheEverlivingBibliotaph = new(377.7593f, -39f, -59.76191f);
    }

    private static class EnemyAura
    {
        /// <summary>
        /// Stunned by pseudo-cutscene.
        /// </summary>
        public const uint InEvent = 1268;

        /// <summary>
        /// AOE stun.
        /// </summary>
        public const uint DownForTheCount = 774;
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Demon Tome
        /// Disclosure
        /// Book closes, need to make sure we move to other side.
        /// </summary>
        public const uint Disclosure = 4818;

        /// <summary>Arena-wide ice application; it is not an escapable damage rectangle.</summary>
        public const uint WordsOfWinter = 3961;

        /// <summary>First center lane, followed by the two outer lanes.</summary>
        public const uint InnerLiquefy = 3520;

        /// <summary>Paired outer lanes after the center lane.</summary>
        public const uint OuterLiquefy = 3521;

        public const uint Repel = 3519;

        public const uint DeathRay = 5058;

        public const uint TailSmash = 4165;

        public const uint DeepDarkness = 3528, MagicBurst = 3529, VoidSpark = 3780, AbyssalCharge = 3531, VoidBlizzard = 3535;
    }

    private static class EnemyTether
    {
        /// <summary>Oil-to-carrier tether observed on both Rowan and a Duty Support NPC.</summary>
        public const ushort WhaleOil = 3;
    }
}
