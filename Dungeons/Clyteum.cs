using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using LlamaLibrary.Helpers;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 100: Clyteum dungeon logic.
/// </summary>
public class Clyteum : AbstractDungeon
{
    /// <summary>
    /// Tracks sub-zone since last tick for environmental decision making.
    /// </summary>
    private SubZoneId lastSubZoneId = SubZoneId.NONE;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Clyteum;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [EnemyAction.EyesOnMe];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.PenetratorMissile, EnemyAction.BodyweightExorcismTowers, EnemyAction.ProfanePressure,EnemyAction.StringUp,EnemyAction.GluttonousWire];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.ShadowPlay];

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        SideStep.Override(EnemyAction.BodyweightExorcism);
        SideStep.Override(EnemyAction.BodyweightExorcismTowers);
        SideStep.Override(17995); // Skyshard LB
        // Third boss abilities that aren't handled properly by SideStep
        SideStep.Override(EnemyAction.VoidDark);
        SideStep.Override(EnemyAction.PuppetStrings);

        // Boss 1: Petrifying Beam
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ForumFloricomum,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.PetrifyingBeam or EnemyAction.PetrifyingBeam2 or EnemyAction.PetrifyingBeam3,
            leashPointProducer: () => ArenaCenter.EyeoftheScorpion,
            leashRadius: 80.0f,
            rotationDegrees: 0.0f,
            radius: 80.0f,
            arcDegrees: 130.0f);

        // Boss 2: Bodyweight Exorcism
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Stockyard,
            objectSelector: c => c.CastingSpellId == EnemyAction.BodyweightExorcism,
            outerRadius: 40.0f,
            innerRadius: 3.0F,
            priority: AvoidancePriority.Medium);

        // Boss 3: Void Dark
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.VoidDark,
            leashPointProducer: () => ArenaCenter.EyeoftheScorpion,
            leashRadius: 60.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 185.0f);

        // Boss 3: Puppet Strings
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            objectSelector: (bc) => bc.CastingSpellId is EnemyAction.PuppetStrings,
            leashPointProducer: () => ArenaCenter.EyeoftheScorpion,
            leashRadius: 60.0f,
            rotationDegrees: 0.0f,
            radius: 40.0f,
            arcDegrees: 90.0f);

        // Boss 3: Avoid hitting other party members with AoE tank buster
        AvoidanceManager.AddAvoidObject<GameObject>(
            canRun: () => Core.Player.InCombat && Core.Me.IsTank() && EnemyAction.ShadowPlayHash.IsCasting(),
            radius: 6.5f,
            unitIds:
            [
                .. PartyManager.VisibleMembers.Select(p => p.BattleCharacter.ObjectId),
            ]);

        // Boss 3:
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            objectSelector: bc => bc.CastingSpellId == EnemyAction.Goekinesis,
            width: 5f,
            length: 80f,
            yOffset: -20f,
            priority: AvoidancePriority.High);

        // Boss 1: Antipersonnel Missile
        // Boss 2: Evil Emission
        // Boss 3: Wrathful Wire
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Core.Player.InCombat && WorldManager.SubZoneId is (uint)SubZoneId.ForumFloricomum or (uint)SubZoneId.Stockyard or (uint)SubZoneId.SecureTestSite,
            objectSelector: bc => bc.CastingSpellId is EnemyAction.AntipersonnelMissile or EnemyAction.EvilEmission or EnemyAction.WrathfulWire && bc.SpellCastInfo.TargetId != Core.Player.ObjectId,
            radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f,
            locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidSquareDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.ForumFloricomum,
            innerWidth: 38.0f,
            innerHeight: 38.0f,
            outerWidth: 90.0f,
            outerHeight: 90.0f,
            collectionProducer: () => [ArenaCenter.EyeoftheScorpion],
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.Stockyard,
            () => ArenaCenter.Chort,
            outerRadius: 90.0f,
            innerRadius: 13f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SecureTestSite,
            () => ArenaCenter.Malphas,
            outerRadius: 90.0f,
            innerRadius: 19f,
            priority: AvoidancePriority.High);

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();
        await TankBusterSpells();
        await DamageMitigationSpells();

        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;

        bool result = currentSubZoneId switch
        {
            SubZoneId.ForumFloricomum => await EyeoftheScorpion(),
            SubZoneId.Stockyard => await Chort(),
            SubZoneId.SecureTestSite => await Malphas(),
            _ => false,
        };

        lastSubZoneId = currentSubZoneId;

        return result;
    }

    /// <summary>
    /// Boss 1: EyeoftheScorpion.
    /// </summary>
    private static async Task<bool> EyeoftheScorpion()
    {
        if (Core.Me.HasAura(PlayerAura.MotionTracker))
        {
            Logger.Debug("Standing still");
            ActionManager.StopCasting();
            Core.Me.ClearTarget();
            await Coroutine.Wait(5000, () => !Core.Me.HasAura(PlayerAura.MotionTracker));
        }

        return false;
    }

    /// <summary>
    /// Boss 2: Chort.
    /// </summary>
    private static async Task<bool> Chort()
    {
        return false;
    }

    /// <summary>
    /// Boss 3: Malphas
    /// </summary>
    private async Task<bool> Malphas()
    {
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Eye of the Scorpion
        /// </summary>
        public const uint EyeoftheScorpion = 14716;

        /// <summary>
        /// First Boss: Eye of the Scorpion
        /// </summary>
        public const uint MotionScanner = 108;

        /// <summary>
        /// Second Boss: Chort
        /// </summary>
        public const uint Chort = 14734;

        /// <summary>
        /// Final Boss: Malphas
        /// </summary>
        public const uint Malphas = 14758;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="EnemyNpc.EyeoftheScorpion"/>.
        /// </summary>
        public static readonly Vector3 EyeoftheScorpion = new(-615f, -1.1920929E-07f, 575f);

        /// <summary>
        /// Second Boss: <see cref="EnemyNpc.Chort"/>.
        /// </summary>
        public static readonly Vector3 Chort = new(660f, -15.000002f, -141f);

        /// <summary>
        /// Third Boss: <see cref="EnemyNpc.Malphas"/>.
        /// </summary>
        public static readonly Vector3 Malphas = new(760f, 61f, -803f);
    }

    private static class EnemyAction
    {

        /// <summary>
        /// Eye of the Scorpion
        /// Eyes On Me
        /// Group wide unavoidable damage
        /// </summary>
        public const uint EyesOnMe = 48896;

        /// <summary>
        /// Eye of the Scorpion
        /// Anti-personnel Missile
        /// Spread
        /// </summary>
        public const uint AntipersonnelMissile = 48899;

        /// <summary>
        /// Eye of the Scorpion
        /// Penetrator Missile
        /// Stack
        /// </summary>
        public const uint PenetratorMissile = 48901;

        /// <summary>
        /// Eye of the Scorpion
        /// Petrifying Beam
        /// Follow Dodge
        /// </summary>
        public const uint PetrifyingBeam = 50176;

        public const uint PetrifyingBeam2 = 50177;
        public const uint PetrifyingBeam3 = 50178;

        /// <summary>
        /// Chort
        /// Bodyweight Exorcism
        /// Middle donut-push back
        /// </summary>
        public const uint BodyweightExorcism = 48878;

        /// <summary>
        /// Chort
        /// Bodyweight Exorcism
        /// Middle donut-push back
        /// </summary>
        public const uint BodyweightExorcismTowers = 48882;

        /// <summary>
        /// Chort
        /// Evil Emission
        /// Spread
        /// </summary>
        public const uint EvilEmission = 48885;

        /// <summary>
        /// Chort
        /// Profane Pressure
        /// Towers spawn and you need to stack on them
        /// </summary>
        public const uint ProfanePressure = 48887;



        /// <summary>
        /// Malphas
        /// Puppet Strings
        /// 90 degree cone
        /// </summary>
        public const uint PuppetStrings = 48922;

        /// <summary>
        /// Malphas
        /// Wrathful Wire
        /// Spread
        /// </summary>
        public const uint WrathfulWire = 48928;

        /// <summary>
        /// Malphas
        /// Gluttonous Wire
        /// Stack
        /// </summary>
        public const uint GluttonousWire = 48930;

        /// <summary>
        /// Malphas
        /// String up
        /// Move around to avoid becoming a puppet
        /// </summary>
        public const uint StringUp = 48931;

        /// <summary>
        /// Malphas
        /// Goekinesis
        /// Single line AoEs. Some are automatically dodged by Sidestep, some aren't.
        /// </summary>
        public const uint Goekinesis = 48933;

        /// <summary>
        /// Malphas
        /// Void Dark
        /// 180 degree cone
        /// </summary>
        public const uint VoidDark = 50313;

        /// <summary>
        /// Malphas
        /// Shadow Play
        /// Tank Buster
        /// </summary>
        public const uint ShadowPlay = 50314;
        public static readonly HashSet<uint> ShadowPlayHash = [50314];
    }

    private static class PlayerAura
    {
        /// <summary>
        /// Eye of the Scorpion
        /// Motion Tracker
        /// Don't move while you have the debuff
        /// </summary>
        public const uint MotionTracker = 5191;
    }
}
