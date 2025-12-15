using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Occult Crescent: South Horn boss logic
/// </summary>
public class SouthHorn : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.SouthHorn;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    private static uint? CurrentFateId =>
        FateManager.WithinFate && FateManager.ActiveFates.Any()
            ? FateManager.ActiveFates
                .OrderBy(fate => Core.Me.Distance(fate.Location))
                .First().Id
            : null;

    private static readonly Dictionary<ClassJobType, uint> TankInvul = new()
    {
        { ClassJobType.Warrior, 43 }, // Holmgang
        { ClassJobType.Paladin, 30 }, // Hallowed Ground
        { ClassJobType.DarkKnight, 3638 }, // Living Dead
        { ClassJobType.Gunbreaker, 16152 }, // Superbolide
    };

    readonly List<uint> soundDetectingEnemyIds =
    [
        13935, // Crescent Harpuia
        13923, // Crescent Geshunpest
        13924, // Crescent Armor
        // 67890, // Another Enemy
    ];

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        // Rough Waters (Fate ID: 1962)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.RoughWaters,
            () => ArenaCenter.RoughWaters,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // Rough Waters (Fate ID: 1962) > Namu > Encroaching Twin Tides
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.RoughWaters,
            objectSelector: c => c.CastingSpellId == EnemyAction.EncroachingTwinTides,
            outerRadius: 40.0f,
            innerRadius: 5.0F,
            priority: AvoidancePriority.Medium);

        // Rough Waters (Fate ID: 1962) > Nammu > Tideline
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.RoughWaters,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.Tideline or EnemyAction.Tideline2 && bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds > 100,
            width: 12f,
            length: 60f,
            xOffset: 0f,
            yOffset: -25f,
            priority: AvoidancePriority.High);

        // Rough Waters (Fate ID: 1962) > Nammu > Far Tide and Near Tide
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.RoughWaters,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.FarTide or EnemyAction.NearTide,
            width: 9f,
            length: 55f,
            xOffset: 0f,
            yOffset: -25f,
            priority: AvoidancePriority.High);

        // Rough Waters (Fate ID: 1962) > Nammu > Left Twin Tentacle and Right Twin Tentacle
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.RoughWaters,
            objectSelector: c => c.CastingSpellId is EnemyAction.LeftTwinTentacle,
            leashPointProducer: () => ArenaCenter.RoughWaters,
            leashRadius: 40.0f,
            rotationDegrees: 90.0f,
            radius: 40.0f,
            arcDegrees: 180f);

        // The Golden Guardian (Fate ID: 1963)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.TheGoldenGuardian,
            () => ArenaCenter.TheGoldenGuardian,
            outerRadius: 90.0f,
            innerRadius: 30.0f,
            priority: AvoidancePriority.High);

        // The Golden Guardian (Fate ID: 1963) > Gilded Headstone > Erosive Eye
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.TheGoldenGuardian,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.ErosiveEye0 or EnemyAction.ErosiveEye1 or EnemyAction.ErosiveEye2 && bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds <= 500,
            radiusProducer: bc => 29.0f,
            priority: AvoidancePriority.High));

        // The Golden Guardian (Fate ID: 1963) > Gilded Headstone > Flaming Epigraph
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.TheGoldenGuardian,
            objectSelector: c => c.CastingSpellId is EnemyAction.FlamingEpigraph1 or EnemyAction.FlamingEpigraph2,
            leashPointProducer: () => ArenaCenter.TheGoldenGuardian,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 60f);

        // King of the Crescent (Fate ID: 1964)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.KingOfTheCrescent,
            () => ArenaCenter.KingOfTheCrescent,
            outerRadius: 90.0f,
            innerRadius: 50.0f,
            priority: AvoidancePriority.High);

        // King of the Crescent (Fate ID: 1964) > Gale Sphere
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.KingOfTheCrescent,
            objectSelector: bc => bc.NpcId == EnemyNpc.GaleSphere && bc.IsVisible,
            radiusProducer: bc => 11.0f,
            priority: AvoidancePriority.High));

        // The Winged Terror (Fate ID: 1965)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.TheWingedTerror,
            () => ArenaCenter.TheWingedTerror,
            outerRadius: 90.0f,
            innerRadius: 35.0f,
            priority: AvoidancePriority.High);

        // An Unending Duty (Fate ID: 1966)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.AnUnendingDuty,
            () => ArenaCenter.AnUnendingDuty,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // An Unending Duty (Fate ID: 1966) > Sisyphus > Thunderous Memory
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.AnUnendingDuty,
            objectSelector: c => c.CastingSpellId is EnemyAction.ThunderousMemory,
            leashPointProducer: () => ArenaCenter.AnUnendingDuty,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 45f);

        // An Unending Duty (Fate ID: 1966) > Sisyphus > Resounding Memory
        // This move creates four cones and a donut to dodge
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.AnUnendingDuty,
            objectSelector: c => c.CastingSpellId is EnemyAction.ResoundingMemory1 or EnemyAction.ResoundingMemory2 or EnemyAction.ResoundingMemory3,
            leashPointProducer: () => ArenaCenter.AnUnendingDuty,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 45f);

        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.AnUnendingDuty,
            objectSelector: c => c.CastingSpellId is EnemyAction.ResoundingMemory1 or EnemyAction.ResoundingMemory2 or EnemyAction.ResoundingMemory3,
            outerRadius: 10.0f,
            innerRadius: 40.0F,
            priority: AvoidancePriority.Medium);

        // Brain Drain (Fate ID: 1967)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.BrainDrain,
            () => ArenaCenter.BrainDrain,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // Brain Drain (Fate ID: 1967) > Advanced Aevis > Triple Flight
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.BrainDrain,
            objectSelector: c => c.CastingSpellId == EnemyAction.TripleFlight,
            outerRadius: 5.0f,
            innerRadius: 40.0F,
            priority: AvoidancePriority.Medium);

        // Brain Drain (Fate ID: 1967) > Advanced Aevis > Zombie Scales
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.BrainDrain,
            objectSelector: c => c.CastingSpellId is EnemyAction.ZombieScales1 or EnemyAction.ZombieScales2 or EnemyAction.ZombieScales3,
            leashPointProducer: () => ArenaCenter.BrainDrain,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 45f);

        // Brain Drain (Fate ID: 1967) > Advanced Aevis > Zombie BReath
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.BrainDrain,
            objectSelector: c => c.CastingSpellId is EnemyAction.ZombieBreath,
            leashPointProducer: () => ArenaCenter.BrainDrain,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 180f);

        // A Delicate Balance (Fate ID: 1968)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.ADelicateBalance,
            () => ArenaCenter.ADelicateBalance,
            outerRadius: 80.0f,
            innerRadius: 30.0f,
            priority: AvoidancePriority.High);

        // Sworn to Soil (Fate ID: 1969)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.SwornToSoil,
            () => ArenaCenter.SwornToSoil,
            outerRadius: 83.0f,
            innerRadius: 30.0f,
            priority: AvoidancePriority.High);

        // A Prying Eye (Fate ID: 1970)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.APryingEye,
            () => ArenaCenter.APryingEye,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // A Prying Eye (Fate ID: 1970) > Observer's Eye
        // Making the avoid a bit larger than necessary to avoid getting caught by quick turns
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.APryingEye,
            objectSelector: bc => bc.NpcId == EnemyNpc.ObserversEye && bc.IsVisible,
            leashPointProducer: () => ArenaCenter.APryingEye,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 12.0f,
            arcDegrees: 100f);

        // A Prying Eye (Fate ID: 1970) > Oogle
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.APryingEye,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Oogle && bc.SpellCastInfo.RemainingCastTime.TotalMilliseconds <= 500,
            radiusProducer: bc => 40.0f,
            priority: AvoidancePriority.High));

        // A Prying Eye (Fate ID: 1970) > Stare
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.APryingEye,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Stare,
            width: 10f,
            length: 50f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Fatal Allure (Fate ID: 1971)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.FatalAllure,
            () => ArenaCenter.FatalAllure,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // Fatal Allure (Fate ID: 1971) > Execrator > Void Fire III
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.FatalAllure,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.VoidFireIII,
            radiusProducer: bc => 10.0f,
            priority: AvoidancePriority.High));

        // Fatal Allure (Fate ID: 1971) > Ball of Fire
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.FatalAllure,
            objectSelector: bc => bc.NpcId == EnemyNpc.BallOfFire && bc.IsVisible && bc.IsCasting,
            radiusProducer: bc => 20.0f,
            priority: AvoidancePriority.High));

        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.FatalAllure,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Mini,
            leashPointProducer: () => ArenaCenter.APryingEye,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 20.0f,
            arcDegrees: 180f);

        // Serving Darkness (Fate ID: 1972)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.ServingDarkness,
            () => ArenaCenter.ServingDarkness,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // Serving Darkness (Fate ID: 1972) > Lifereaper > Menacing Charge
        // TODO: This one is problematic. The avoid is a line 10f wide and 20f long, but the mob doesn't cast it in front of him.
        // he seems to pick a random direction and cast it there while still facing a different direction.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.ServingDarkness,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.MenacingCharge,
            leashPointProducer: () => ArenaCenter.ServingDarkness,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 50.0f,
            arcDegrees: 180f);

        // Serving Darkness (Fate ID: 1972) > Lifereaper > Sweeping Charge
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.ServingDarkness,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SweepingCharge,
            width: 10f,
            length: 50f,
            yOffset: 0f,
            priority: AvoidancePriority.High);

        // Serving Darkness (Fate ID: 1972) > Lifereaper > Soul Sweep
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.ServingDarkness,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.SoulSweep,
            leashPointProducer: () => ArenaCenter.ServingDarkness,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 50.0f,
            arcDegrees: 160f);

        // Pleading Pots (Fate ID: 1977)
        // Arena Boundary
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.ZoneId == (uint)ZoneId.SouthHorn && CurrentFateId == FateIds.PleadingPots,
            () => ArenaCenter.PleadingPots,
            outerRadius: 90.0f,
            innerRadius: 40.0f,
            priority: AvoidancePriority.High);

        // Setting this general avoid to stay away from mobs that are higher level than us.
        // We'll make a cone and a cirlce avoid to avoid sight detecting mobs.
        // The cirlce is to prevent from traveling too close to the head and still agroing.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => WorldManager.ZoneId == 1252 && !FateManager.WithinFate,
            objectSelector: bc => !bc.InCombat && !bc.IsFate && !soundDetectingEnemyIds.Contains(bc.NpcId) && bc.ElementalLevel >= Core.Me.ElementalLevel && bc.IsVisible && !FateManager.ActiveFates.Any(r => r.Location.Distance2D(bc.Location) <= r.Radius) && bc.CanAttack && Core.Me.Distance(bc.Location) < 50,
            leashPointProducer: () => Core.Player.Location,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 15.0f,
            arcDegrees: 90.0f,
            priority: AvoidancePriority.Low);

        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == 1252 && !FateManager.WithinFate,
            objectSelector: bc => !bc.InCombat && !bc.IsFate && !soundDetectingEnemyIds.Contains(bc.NpcId) && bc.ElementalLevel >= Core.Me.ElementalLevel && bc.IsVisible && !FateManager.ActiveFates.Any(r => r.Location.Distance2D(bc.Location) <= r.Radius) && bc.CanAttack && Core.Me.Distance(bc.Location) < 50,
            radiusProducer: bc => 1.5f,
            priority: AvoidancePriority.Low));

        // Make a large Circular avoid for sound detecting mobs
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => WorldManager.ZoneId == 1252 && !FateManager.WithinFate,
            objectSelector: bc => !bc.InCombat && !bc.IsFate && soundDetectingEnemyIds.Contains(bc.NpcId) && bc.ElementalLevel >= Core.Me.ElementalLevel && bc.IsVisible && !FateManager.ActiveFates.Any(r => r.Location.Distance2D(bc.Location) <= r.Radius) && bc.CanAttack && Core.Me.Distance(bc.Location) < 50,
            radiusProducer: bc => 11.0f,
            priority: AvoidancePriority.Low));

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        switch (CurrentFateId)
        {
            case FateIds.RoughWaters:
                await HandleRoughWaters();
                break;
            case FateIds.TheGoldenGuardian:
                await HandleTheGoldenGuardian();
                break;
            case FateIds.KingOfTheCrescent:
                await HandleKingOfTheCrescent();
                break;
            case FateIds.TheWingedTerror:
                await HandleTheWingedTerror();
                break;
            case FateIds.AnUnendingDuty:
                await HandleAnUnendingDuty();
                break;
            case FateIds.BrainDrain:
                await HandleBrainDrain();
                break;
            case FateIds.ADelicateBalance:
                await HandleADelicateBalance();
                break;
            case FateIds.SwornToSoil:
                await HandleSwornToSoil();
                break;
            case FateIds.APryingEye:
                await HandleAPryingEye();
                break;
            case FateIds.FatalAllure:
                await HandleFatalAllure();
                break;
            case FateIds.ServingDarkness:
                await HandleServingDarkness();
                break;
            case FateIds.PleadingPots:
                await HandlePleadingPots();
                break;
            default:
                // Optional: Log or handle unknown fate
                break;
        }

        return false;
    }

    /// <summary>
    /// 1962 Type: Rough Waters
    /// </summary>
    private static async Task<bool> HandleRoughWaters()
    {
        //Logger.Information($"We are in FATE Rough Waters");
        return false;
    }

    /// <summary>
    /// 1963 Type: The Golden Guardian
    /// </summary>
    private static async Task<bool> HandleTheGoldenGuardian()
    {
        //Logger.Information($"We are in FATE The Golden Guardian");
        return false;
    }

    /// <summary>
    /// 1964 Type: King of the Crescent
    /// </summary>
    private static async Task<bool> HandleKingOfTheCrescent()
    {
        //Logger.Information($"We are in FATE King of the Crescent");
        return false;
    }

    /// <summary>
    /// 1965 Type: The Winged Terror
    /// </summary>
    private static async Task<bool> HandleTheWingedTerror()
    {
        //Logger.Information($"We are in FATE The Winged Terror");
        return false;
    }

    /// <summary>
    /// 1966 Type: An Unending Duty
    /// </summary>
    private static async Task<bool> HandleAnUnendingDuty()
    {
        //Logger.Information($"We are in FATE An Unending Duty");
        return false;
    }

    /// <summary>
    /// 1967 Type: Brain Drain
    /// </summary>
    private static async Task<bool> HandleBrainDrain()
    {
        //Logger.Information($"We are in FATE Brain Drain");
        return false;
    }

    /// <summary>
    /// 1968 Type: A Delicate Balance
    /// </summary>
    private static async Task<bool> HandleADelicateBalance()
    {
        //Logger.Information($"We are in FATE A Delicate Balance");
        return false;
    }

    /// <summary>
    /// 1969 Type: Sworn to Soil
    /// </summary>
    private static async Task<bool> HandleSwornToSoil()
    {
        //Logger.Information($"We are in FATE Sworn to Soil");
        return false;
    }

    /// <summary>
    /// 1970 Type: A Prying Eye
    /// </summary>
    private static async Task<bool> HandleAPryingEye()
    {
        //Logger.Information($"We are in FATE A Prying Eye");
        return false;
    }

    /// <summary>
    /// 1971 Type: Fatal Allure
    /// </summary>
    private static async Task<bool> HandleFatalAllure()
    {
        //Logger.Information($"We are in FATE Fatal Allure");
        return false;
    }

    /// <summary>
    /// 1972 Type: Serving Darkness
    /// </summary>
    private static async Task<bool> HandleServingDarkness()
    {
        //Logger.Information($"We are in FATE Serving Darkness");
        return false;
    }

    /// <summary>
    /// 1977 Type: Pleading Pots
    /// </summary>
    private static async Task<bool> HandlePleadingPots()
    {
        //Logger.Information($"We are in FATE Pleading Pots");
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Fate 1964: King of the Crescent
        /// Gale Sphere
        /// </summary>
        public const uint GaleSphere = 13869;

        /// <summary>
        /// Fate ID: 1970 - Prying Eye
        /// Observer's Eye
        /// </summary>
        public const uint ObserversEye = 13854;

        /// <summary>
        /// Fate 1971: Fatal Allure
        /// Ball of Fire
        /// </summary>
        public const uint BallOfFire = 13946;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// Fate 1962: Rough Waters
        /// </summary>
        public static readonly Vector3 RoughWaters = new(162f, 56f, 676.0001f);

        /// <summary>
        /// Fate 1963: The Golden Guardian
        /// </summary>
        public static readonly Vector3 TheGoldenGuardian = new(373.2f, 70f, 486f);

        /// <summary>
        /// Fate 1964: King of the Crescent
        /// </summary>
        public static readonly Vector3 KingOfTheCrescent = new(-226.1f, 116.3825f, 254f);

        /// <summary>
        /// Fate 1965: The Winged Terror
        /// </summary>
        public static readonly Vector3 TheWingedTerror = new(-548.5f, 3f, -595f);

        /// <summary>
        /// Fate 1966: An Unending Duty
        /// </summary>
        public static readonly Vector3 AnUnendingDuty = new(-223.1f, 107f, 36f);

        /// <summary>
        /// Fate 1967: Brain Drain
        /// </summary>
        public static readonly Vector3 BrainDrain = new(-48.09998f, 111.761f, -320f);

        /// <summary>
        /// Fate 1968: A Delicate Balance
        /// </summary>
        public static readonly Vector3 ADelicateBalance = new(-370f, 75f, 650f);

        /// <summary>
        /// Fate 1969: Sworn to Soil
        /// </summary>
        public static readonly Vector3 SwornToSoil = new(-589.1f, 96.5f, 333f);

        /// <summary>
        /// Fate 1970: A Prying Eye
        /// </summary>
        public static readonly Vector3 APryingEye = new(-71f, 71.31213f, 557f);

        /// <summary>
        /// Fate 1971: Fatal Allure
        /// </summary>
        public static readonly Vector3 FatalAllure = new(79f, 97.85933f, 278f);

        /// <summary>
        /// Fate 1972: Serving Darkness
        /// </summary>
        public static readonly Vector3 ServingDarkness = new(413f, 96f, -13f);

        /// <summary>
        /// Fate 1977: Pleading Pots
        /// </summary>
        public static readonly Vector3 PleadingPots = new(-481f, 75f, 528f);
    }

    private static class FateIds
    {
        /// <summary>
        /// Rough Waters
        /// </summary>
        public const uint RoughWaters = 1962;

        /// <summary>
        /// The Golden Guardian
        /// </summary>
        public const uint TheGoldenGuardian = 1963;

        /// <summary>
        /// King of the Crescent
        /// </summary>
        public const uint KingOfTheCrescent = 1964;

        /// <summary>
        /// The Winged Terror
        /// </summary>
        public const uint TheWingedTerror = 1965;

        /// <summary>
        /// An Unending Duty
        /// </summary>
        public const uint AnUnendingDuty = 1966;

        /// <summary>
        /// Brain Drain
        /// </summary>
        public const uint BrainDrain = 1967;

        /// <summary>
        /// A Delicate Balance
        /// </summary>
        public const uint ADelicateBalance = 1968;

        /// <summary>
        /// Sworn to Soil
        /// </summary>
        public const uint SwornToSoil = 1969;

        /// <summary>
        /// A Prying Eye
        /// </summary>
        public const uint APryingEye = 1970;

        /// <summary>
        /// Fatal Allure
        /// </summary>
        public const uint FatalAllure = 1971;

        /// <summary>
        /// Serving Darkness
        /// </summary>
        public const uint ServingDarkness = 1972;

        /// <summary>
        /// Pleading Pots
        /// </summary>
        public const uint PleadingPots = 1977;
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Fate 1962: Rough Waters
        /// Nammu
        /// Far Tide
        /// Line AoE (outward)
        /// </summary>
        public const uint FarTide = 41777;

        /// <summary>
        /// Fate 1962: Rough Waters
        /// Nammu
        /// Encroaching Twin Tides (41776)
        /// </summary>
        public const uint EncroachingTwinTides = 41776;

        /// <summary>
        /// Fate 1962: Rough Waters
        /// Nammu
        /// Near Tide
        /// Line AoE (inward)
        /// </summary>
        public const uint NearTide = 41775;

        /// <summary>
        /// Fate 1962: Rough Waters
        /// Nammu
        /// Tideline
        /// Arena-wide or pattern AoE
        /// </summary>
        public const uint Tideline = 41771;

        /// <summary>
        /// Fate 1962: The Rough Waters
        /// Nammu
        /// Tideline (41772)
        /// </summary>
        public const uint Tideline2 = 41772;

        /// <summary>
        /// Fate 1962: Rough Waters
        /// Nammu
        /// Left Twin Tentacle (41779)
        /// </summary>
        public const uint LeftTwinTentacle = 41779;

        /// <summary>
        /// Fate 1963: The Golden Guardian
        /// Gilded Headstone
        /// Erosive Eye (41791)
        /// </summary>
        public const uint ErosiveEye0 = 41791;

        /// <summary>
        /// Fate 1963: The Golden Guardian
        /// Gilded Headstone
        /// Erosive Eye (41792)
        /// </summary>
        public const uint ErosiveEye1 = 41792;

        /// <summary>
        /// Fate 1963: The Golden Guardian
        /// Gilded Headstone
        /// Erosive Eye (41794)
        /// </summary>
        public const uint ErosiveEye2 = 41794;

        /// <summary>
        /// Fate 1963: The Golden Guardian
        /// Gilded Headstone
        /// Flaming Epigraph (41802)
        /// </summary>
        public const uint FlamingEpigraph1 = 41802;

        /// <summary>
        /// Fate 1963: The Golden Guardian
        /// Gilded Headstone
        /// Flaming Epigraph (41803)
        /// </summary>
        public const uint FlamingEpigraph2 = 41803;

        /// <summary>
        /// Fate 1966: An Unending Duty
        /// Sisyphus
        /// Thunderous Memory (41978)
        /// </summary>
        public const uint ThunderousMemory = 41978;

        /// <summary>
        /// Fate 1966: Brain Drain
        /// Sisyphus
        /// Resounding Memory (41979)
        /// </summary>
        public const uint ResoundingMemory1 = 41979;

        /// <summary>
        /// Fate 1966: Brain Drain
        /// Sisyphus
        /// Resounding Memory (41981)
        /// </summary>
        public const uint ResoundingMemory2 = 41981;

        /// <summary>
        /// Fate 1966: Brain Drain
        /// Sisyphus
        /// Resounding Memory (41982)
        /// </summary>
        public const uint ResoundingMemory3 = 41982;

        /// <summary>
        /// Fate 1967: Brain Drain
        /// Advanced Aevis
        /// Triple Flight
        /// Donut AoE
        /// </summary>
        public const uint TripleFlight = 42012;

        /// <summary>
        /// Fate 1967: Brain Drain
        /// Advanced Aevis
        /// Zombie Scales (variant 1)
        /// Cone AoE
        /// </summary>
        public const uint ZombieScales1 = 41998;

        /// <summary>
        /// Fate 1967: Brain Drain
        /// Advanced Aevis
        /// Zombie Scales (variant 2)
        /// Cone AoE
        /// </summary>
        public const uint ZombieScales2 = 42000;

        /// <summary>
        /// Fate 1967: Brain Drain
        /// Advanced Aevis
        /// Zombie Scales (variant 3)
        /// Cone AoE
        /// </summary>
        public const uint ZombieScales3 = 42001;

        /// <summary>
        /// Fate 1967: Brain Drain
        /// Advanced Aevis
        /// Zombie Breath (42004)
        /// </summary>
        public const uint ZombieBreath = 42004;

        /// <summary>
        /// Fate 1970: A Prying Eye
        /// Observer
        /// Oogle (likely self-targeted or aura)
        /// </summary>
        public const uint Oogle = 43043;

        /// <summary>
        /// Fate 1970: A Prying Eye
        /// Observer
        /// Stare
        /// </summary>
        public const uint Stare = 43268;

        /// <summary>
        /// Fate 1971: Fatal Allure
        /// Execrator
        /// Void Fire III
        /// Self-targeted AoE
        /// </summary>
        public const uint VoidFireIII = 43049;

        /// <summary>
        /// Fate 1971: Fatal Allure
        /// Execrator
        /// Mini
        /// Self-targeted AoE
        /// </summary>
        public const uint Mini = 43046;

        /// <summary>
        /// Fate 1971: Serving Darkness
        /// Ball of Fire
        /// Arm of Purgatory
        /// Targeted AoE explosions
        /// </summary>
        public const uint ArmOfPurgatory = 43269;

        /// <summary>
        /// Fate 1972: Serving Darkness
        /// Lifereaper
        /// Menacing Charge
        /// Cast at location
        /// </summary>
        public const uint MenacingCharge = 42179;

        /// <summary>
        /// Fate 1972: Lifereaper
        /// Lifereaper
        /// Sweeping Charge (42178)
        /// </summary>
        public const uint SweepingCharge = 42178;

        /// <summary>
        /// Fate 1972: Serving Darkness
        /// Lifereaper
        /// Soul Sweep (42177)
        /// </summary>
        public const uint SoulSweep = 42177;
    }
}
