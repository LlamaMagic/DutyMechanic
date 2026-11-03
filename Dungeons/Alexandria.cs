using Clio.Common;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Alexandria dungeon logic.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows7.0")]
public partial class Alexandria : AbstractDungeon
{
    private const int QuarantineDuration = 10_000;

    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Alexandria;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        // Interferon and Immune Response are handled by the encounter input
        // owner. Duplicate native avoids previously preempted its staged route.

        // Keep the marked tankbuster target avoided throughout its marker
        // lifetime. A proximity-triggered 20y avoid repeatedly appeared and
        // vanished as the stack movement crossed its nine-yalm trigger.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && !Core.Player.IsTank() && WorldManager.SubZoneId is (uint)SubZoneId.VolatileMemory &&
                System.DateTime.UtcNow < quarantineUntil,
            objectSelector: bc => bc.ObjectId != Core.Player.ObjectId && HasQuarantineMarker(bc, 344),
            radiusProducer: bc => 6.5f,
            locationProducer: bc => bc.Location);

        // Boss 2: Static Spark
        // Boss 2: Voltburst
        // Boss 3: Electray
        // In the observed Halo/Partition/Electray overlap, spreading before the
        // donut resolves left no path and contributed to a death. Keep the
        // earlier donut active first; Electray resolves later and remains cast.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && (WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive ||
                WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && !amalgamAuthoredActive),
            objectSelector: bc => (bc.CastingSpellId is EnemyAction.StaticSpark
                || (bc.CastingSpellId == EnemyAction.Electray && !EnemyAction.HaloCasts.IsCasting()))
                && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // The three baited ground-circle waves overlap in cast time, but resolve
        // sequentially. Retain only the earliest wave through its observed effect
        // margin, independently of helper visibility and cast disappearance.
        AvoidanceManager.AddAvoidLocation<VoltburstCircle>(
            canRun: () => Core.Me.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && !amalgamAuthoredActive,
            radiusProducer: _ => 6.5f,
            locationProducer: circle => circle.Origin,
            collectionProducer: ActiveVoltburstCircles,
            objectValidator: circle => circle.Expires > System.DateTime.UtcNow);

        // Boss 2: Supercell Matrix
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && AmalgamMatrixReady && !amalgamAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SupercellMatrix,
            width: 60f,
            length: 28.5f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Boss 2: Supercell Matrix Lines
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && AmalgamMatrixReady && !amalgamAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SupercellMatrixLine,
            width: 8.5f,
            length: 120f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Boss 2: Split Current
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && !amalgamAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SplitCurrent,
            width: 20.5f,
            length: 120f,
            yOffset: -20f,
            xOffset: 15f,
            priority: AvoidancePriority.High);
        // Boss 2: Split Current
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && !amalgamAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SplitCurrent,
            width: 20.5f,
            length: 120f,
            yOffset: -20f,
            xOffset: -15f,
            priority: AvoidancePriority.High);

        // Boss 2: Centralized Current
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && !amalgamAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.CentralizedCurrent,
            width: 16f,
            length: 120f,
            yOffset: -40f,
            priority: AvoidancePriority.High);

        // Boss 2: Ternary Charge Inner
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache && !amalgamAuthoredActive,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.TernaryChargeInner,
            radiusProducer: eo => 11.0f,
            priority: AvoidancePriority.High));

        // Boss 2: Ternary Charge Outer
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            // The later rings must not pull the player back into the first
            // circle while its captured effect window is still resolving.
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache &&
                !EnemyAction.TernaryChargeHash.IsCasting() && !TernaryFirstCircleActive && !amalgamAuthoredActive,
            objectSelector: c => c.CastingSpellId == EnemyAction.TernaryChargeOuter,
            outerRadius: 40.0f,
            innerRadius: 9.0F,
            priority: AvoidancePriority.Medium);

        // Boss 3: Unknown Lazer
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !EnemyAction.Impact.IsCasting() && !eliminatorAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.TargetedLine && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            width: 7f,
            length: 100f,
            rotationProducer: bc => -MathEx.CalculateNeededFacing(bc.Location, GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId).Location));

        // Boss 3: Explosion
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Explosion,
            width: 8f,
            length: 100f,
            yOffset: -40f,
            priority: AvoidancePriority.High);

        // Halo resolves before the overlapping Terminate line; don't constrain
        // the initial donut shelter with the later line before it is needed.
        // Boss 3: Terminate
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !EnemyAction.HaloCasts.IsCasting() && !eliminatorAuthoredActive,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Terminate,
            width: 12f,
            length: 40f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Boss 3: Impact
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: c => c.CastingSpellId == EnemyAction.ImpactConst,
            outerRadius: 40.0f,
            innerRadius: 8F,
            priority: AvoidancePriority.Medium);
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.ImpactConst,
            radiusProducer: eo => 6.5f,
            priority: AvoidancePriority.High));
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: (bc) => bc.CastingSpellId == EnemyAction.ImpactConst,
            leashPointProducer: () => ArenaCenter.Eliminator,
            leashRadius: 40.0f,
            rotationDegrees: -220.0f,
            radius: 40.0f,
            arcDegrees: 320f);

        // Boss 3: Halo of Destruction
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: c => c.CastingSpellId == EnemyAction.HaloofDestruction,
            outerRadius: 40.0f,
            innerRadius: 4.0F,
            priority: AvoidancePriority.Medium);

        // Both Partition casters are in Eliminator's arena; using Antivirus X's distant
        // center prevents the leash from finding them at all.
        // Boss 3: Partition Left
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.PartitionLeft or EnemyAction.PartitionLeft2,
            leashPointProducer: () => ArenaCenter.Eliminator,
            leashRadius: 40.0f,
            rotationDegrees: -90f,
            radius: 40.0f,
            arcDegrees: 180f);

        // Boss 3: Partition Right
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension && !eliminatorAuthoredActive,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.PartitionRight,
            leashPointProducer: () => ArenaCenter.Eliminator,
            leashRadius: 40.0f,
            rotationDegrees: 90f,
            radius: 40.0f,
            arcDegrees: 180f);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.VolatileMemory,
            innerWidth: 39.0f,
            innerHeight: 29.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.AntivirusX],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.CorruptedMemoryCache,
            innerWidth: 39.0f,
            innerHeight: 39.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Amalgam],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Reascension,
            innerWidth: 28.0f,
            innerHeight: 28.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.Eliminator],
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        SetBossSideStep(currentSubZoneId is SubZoneId.VolatileMemory or SubZoneId.CorruptedMemoryCache or SubZoneId.Reascension);

        bool result = currentSubZoneId switch
        {
            SubZoneId.VolatileMemory => await AntivirusX(),
            SubZoneId.CorruptedMemoryCache => await Amalgam(),
            SubZoneId.Reascension => await Eliminator(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        if (currentSubZoneId is not (SubZoneId.VolatileMemory or SubZoneId.CorruptedMemoryCache or SubZoneId.Reascension)) DetachQuarantineMovement();

        return result;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        DetachQuarantineMovement();
        SetBossSideStep(false);
        return Task.FromResult(false);
    }

    // SideStep is needed on trash but duplicates the authored boss geometry.
    // Restore the state held before the boss if a stop bypasses the next pulse.
    private static bool? sideStepBeforeBoss;
    private static void SetBossSideStep(bool inBoss)
    {
        if (SidestepPlugin == null) return;
        if (inBoss)
        {
            sideStepBeforeBoss ??= SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
        }
        else if (sideStepBeforeBoss is bool previous)
        {
            SidestepPlugin.Enabled = previous;
            sideStepBeforeBoss = null;
        }
    }

    /// <summary>
    /// Boss 1: Antivirus X.
    /// </summary>
    private async Task<bool> AntivirusX()
    {
        if (antivirusPulseHandled) return antivirusPulseMoving;
        if (PositionForQuarantineStack())
            return true;

        if (EnemyAction.Quarantine.IsCasting())
        {
            // If you're on tank you want to spread during Quarantine as it does an AOE tank buster on the tank
            // Otherwise you want to stack.
            if (Core.Player.IsTank())
            {
                await MovementHelpers.Spread(QuarantineDuration, 10f);
            }


        }

        return false;
    }

    /// <summary>
    /// Boss 2: Amalgam.
    /// </summary>
    private Task<bool> Amalgam()
    {
        if (amalgamPulseHandled) return Task.FromResult(amalgamPulseMoving);
        return Task.FromResult(PositionForSuperboltStack());
    }

    /// <summary>
    /// Boss 3: Eliminator.
    /// </summary>
    private Task<bool> Eliminator()
    {
        if (eliminatorPulseHandled) return Task.FromResult(eliminatorPulseMoving);
        return Task.FromResult(PositionForLineStack());
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Antivirus X.
        /// </summary>
        public const uint AntivirusX = 12844;

        /// <summary>
        /// First Boss: Interferon R
        /// </summary>
        public const uint InterferonR = 12843;

        /// <summary>
        /// First Boss: Interferon C
        /// </summary>
        public const uint InterferonC = 12842;

        /// <summary>
        /// Second Boss: Amalgam.
        /// </summary>
        public const uint Amalgam = 12864;

        /// <summary>
        /// Final Boss: Eliminator.
        /// </summary>
        public const uint Eliminator = 12729;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.AntivirusX"/>.
        /// </summary>
        public static readonly Vector3 AntivirusX = new(852f, 46f, 823f);

        /// <summary>
        /// Second Boss: Amalgam.
        /// </summary>
        public static readonly Vector3 Amalgam = new(-533f, -466f, -373f);

        /// <summary>
        /// Third Boss: Eliminator.
        /// </summary>
        public static readonly Vector3 Eliminator = new(-759f, -474f, -648f);

        /// <summary>
        /// Third Boss: Impact Safe Spot.
        /// </summary>
        public static readonly Vector3 ImpactSafeSpot = new(-761.9357f, -473.9999f, -645.7164f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Antivirus X
        /// Quarantine
        /// Stack
        /// </summary>
        public static readonly HashSet<uint> Quarantine = [36384];

        public const uint QuarantineConst = 36384;

        /// <summary>
        /// Antivirus X
        /// Immune Response
        /// Frontal cone
        /// </summary>
        public const uint ImmuneResponseFront = 36378;

        /// <summary>
        /// Antivirus X
        /// Immune Response
        ///  Cone damage on both left and right side
        /// </summary>
        public const uint ImmuneResponseSides = 36380;

        /// <summary>
        /// Amalgam
        /// Static Spark
        /// Spread
        /// </summary>
        public const uint StaticSpark = 36334;

        /// <summary>
        /// Amalgam
        /// Supercell Matrix
        /// Cone type AoE starting from bottom right of arena
        /// </summary>
        public const uint SupercellMatrix = 39136;

        /// <summary>
        /// Amalgam
        /// Supercell Matrix
        /// Straight line AoEs
        /// </summary>
        public const uint SupercellMatrixLine = 39138;

        /// <summary>
        /// Amalgam
        /// Split Current
        /// Line AoE, could also be 36329, or 36330, or 36331
        /// </summary>
        public const uint SplitCurrent = 36331;

        /// <summary>
        /// Amalgam
        /// Centralized Current
        /// Line AoE right through the middle of the arena
        /// </summary>
        public const uint CentralizedCurrent = 36327;

        /// <summary>
        /// Amalgam
        /// Superbolt
        /// Stack
        /// </summary>
        public const uint Superbolt = 36333;

        /// <summary>
        /// Amalgam
        /// Voltburst
        /// Small AoEs to dodge
        /// </summary>
        public const uint Voltburst = 36336;

        /// <summary>
        /// Amalgam
        /// Ternary Charge
        /// Three wave attack
        /// </summary>
        public const uint TernaryChargeInner = 39253;

        public static readonly HashSet<uint> TernaryChargeHash = [39253];

        /// <summary>
        /// Amalgam
        /// Ternary Charge
        /// Three wave attack
        /// </summary>
        public const uint TernaryChargeOuter = 39256;

        /// <summary>
        /// Eliminator
        /// Targeted line attack.
        /// </summary>
        public const uint TargetedLine = 36783;

        /// <summary>
        /// Eliminator
        /// Compression
        /// We want to move to the edge of the blue circle with the other NPCs
        /// </summary>
        public const uint Compression = 36792;

        /// <summary>
        /// Eliminator
        /// Electray
        /// Spread
        /// </summary>
        public const uint Electray = 39243;

        /// <summary>
        /// Eliminator
        /// Overexposure
        /// Stack
        /// </summary>
        public const uint Overexposure = 36779;

        /// <summary>
        /// Eliminator
        /// Light of Devotion
        /// Stack
        /// </summary>
        public const uint LightofDevotion = 36785;

        /// <summary>
        /// Eliminator
        /// Halo of Destruction
        /// Donut AoE
        /// </summary>
        public const uint HaloofDestruction = 36776;

        // The Elimbit choreography and its damage helper overlap in the capture;
        // wait until both end before admitting the later spread or line avoid.
        public static readonly HashSet<uint> HaloCasts = [36776, 39616];

        /// <summary>
        /// Eliminator
        /// Partition
        /// Left Half of room
        /// </summary>
        public const uint PartitionLeft = 39007;

        /// <summary>
        /// Eliminator
        /// Partition
        /// Right Half Room Wide Cone AoE
        /// </summary>
        public const uint PartitionRight = 39249;

        /// <summary>
        /// Eliminator
        /// Partition
        /// Right Half Room Wide Cone AoE
        /// </summary>
        public const uint PartitionLeft2 = 39238;

        /// <summary>
        /// Eliminator
        /// Terminate
        /// Line AoE to dodge
        /// </summary>
        public const uint Terminate = 36773;

        /// <summary>
        /// Eliminator
        /// Explosion
        /// Multiple line AoEs around the room
        /// </summary>
        public const uint Explosion = 39239;

        /// <summary>
        /// Eliminator
        /// Impact
        /// Stand on the edge of impact to get pushed back
        /// </summary>
        public static readonly HashSet<uint> Impact = [36794];

        public const uint ImpactConst = 36794;

        // Captured helper actions for the final boss's partition and knockback waves.
        public const uint PartitionRight2 = 39008;
        public const uint TerminateHelper = 39615;
        public const uint HaloHelper = 39616;
        public const uint CompressionDamage = 36793;
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Prey. Thermal Charge bomb
        /// </summary>
        public const uint Prey = 1253;
    }
}
