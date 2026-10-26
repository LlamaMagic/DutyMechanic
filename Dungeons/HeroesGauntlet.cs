using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 80.4: The Heroes' Gauntlet dungeon logic.
/// </summary>
public partial class HeroesGauntlet : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheHeroesGauntlet;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        // Boss 1: Spectral Gust
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.MountArgaiMines, objectSelector: bc => bc.CastingSpellId == EnemyAction.SpectralGust && bc.SpellCastInfo.TargetId != Core.Player.ObjectId, radiusProducer: bc => bc.SpellCastInfo.SpellData.Radius * 1.05f, locationProducer: bc => GameObjectManager.GetObjectByObjectId(bc.SpellCastInfo.TargetId)?.Location ?? bc.SpellCastInfo.CastLocation);
        // Boss 2: Poison Puddles on the groups
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(condition: () => WorldManager.SubZoneId == (uint)SubZoneId.SummerBallroom, objectSelector: eo => eo.IsVisible && eo.NpcId == EnemyNpc.PoisonPuddle, radiusProducer: eo => 8.0f, priority: AvoidancePriority.High));
        // Boss 2: all eight installed Burst variants have an 8y radius. A 0.5y margin
        // preserves escape space between bombs; the former 15y circles excluded safe gaps.
        // 20323 is Death Throes, not a Burst variant. Pre-grab bait timing remains separate.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SummerBallroom, objectSelector: bc => bc.CastingSpellId is EnemyAction.Burst1 or EnemyAction.Burst2 or EnemyAction.Burst3 or EnemyAction.Burst4 or EnemyAction.Burst5 or EnemyAction.Burst6 or EnemyAction.Burst7 or EnemyAction.Burst8, radiusProducer: bc => 8.5f);
        // Berserker owns its lines, crater lifecycle and positive stack/shelter below.
        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(() => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.MountArgaiMines, () => ArenaCenter.SpectralThief, outerRadius: 90.0f, innerRadius: 19.0f, priority: AvoidancePriority.High);
        AvoidanceHelpers.AddAvoidDonut(() => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SummerBallroom, () => ArenaCenter.SpectralNecromancer, outerRadius: 90.0f, innerRadius: 19.0f, priority: AvoidancePriority.High);
        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        if (BerserkerActive())
        {
            return RunBerserker();
        }

        ResetBerserkerActive();
        SubZoneId currentSubZoneId = (SubZoneId)WorldManager.SubZoneId;
        bool result = false;
        switch (currentSubZoneId)
        {
            case SubZoneId.MountArgaiMines:
                result = await HandleSpectralThiefAsync();
                break;
            case SubZoneId.WhereHeartsLeap:
                result = await HandleSpectralNecromancerAsync();
                break;
            case SubZoneId.WhereAllWitness:
                result = await HandleSpectralBerserkerAsync();
                break;
        }

        return false;
    }

    private static async Task<bool> HandleSpectralThiefAsync()
    {
        return false;
    }

    private static async Task<bool> HandleSpectralNecromancerAsync()
    {
        return false;
    }

    private static async Task<bool> HandleSpectralBerserkerAsync()
    {
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Spectral Thief.
        /// </summary>
        public const uint SpectralThief = 9505;
        /// <summary>
        /// Second Boss: Spectral Necromancer.
        /// </summary>
        public const uint SpectralNecromancer = 9508;
        /// <summary>
        /// Second Boss: Spectral Necromancer's Puddle.
        /// </summary>
        public const uint PoisonPuddle = 2011180;
        /// <summary>
        /// Final Boss: Spectral Berserker.
        /// </summary>
        public const uint SpectralBerserker = 9511;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Spectral Thief.
        /// </summary>
        public static readonly Vector3 SpectralThief = new(-680f, -24f, 450f);
        /// <summary>
        /// Second Boss: Spectral Necromancer.
        /// </summary>
        public static readonly Vector3 SpectralNecromancer = new(-450f, 0f, -531f);
        /// <summary>
        /// Third Boss: Spectral Berserker.
        /// </summary>
        public static readonly Vector3 SpectralBerserker = new(750f, 8f, 482f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralThief"/>'s Coward's Cunning.
        ///
        /// Teleports to marked area and does circle AOE
        /// </summary>
        public const uint Shadowdash = 20436;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralThief"/>'s Dash.
        ///
        /// Teleports to marked area and does circle AOE
        /// </summary>
        public const uint Dash = 20435;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralThief"/>'s Coward's Cunning.
        ///
        /// Line AOE
        /// </summary>
        public const uint CowardsCunning = 20439;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralThief"/>'s Spectral Whirlwind.
        ///
        /// Raid wide AOE
        /// </summary>
        public const uint SpectralWhirlwind = 20428;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralThief"/>'s Spectral Dream.
        ///
        /// Tank Buster
        /// </summary>
        public const uint SpectralDream = 20427;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralThief"/>'s Spectral Gust.
        ///
        /// Spread.
        /// </summary>
        public const uint SpectralGust = 21455;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralNecromancer"/>'s Burst.
        ///
        /// Spread.
        /// </summary>
        public const uint Burst1 = 21430;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralNecromancer"/>'s Burst.
        ///
        /// Spread.
        /// </summary>
        public const uint Burst2 = 21431;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralNecromancer"/>'s Burst.
        ///
        /// Spread.
        /// </summary>
        public const uint Burst3 = 20322;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralNecromancer"/>'s Burst.
        ///
        /// Spread.
        /// </summary>
        public const uint Burst4 = 21429;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralNecromancer"/>'s Burst.
        ///
        /// Spread.
        /// </summary>
        public const uint Burst5 = 20324;
        /// <summary>Crawling Necrobomb variant six; installed 8y self-centered Burst.</summary>
        public const uint Burst6 = 21432;
        /// <summary>Crawling Necrobomb variant seven; installed 8y self-centered Burst.</summary>
        public const uint Burst7 = 21433;
        /// <summary>Crawling Necrobomb variant eight; installed 8y self-centered Burst.</summary>
        public const uint Burst8 = 21434;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Wild Rampage.
        ///
        /// Shelter in an activated crater; leave after the helper impact.
        /// </summary>
        public const uint WildRampage1 = 20998;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Wild Rampage.
        ///
        /// Shelter in an activated crater; leave after the helper impact.
        /// </summary>
        public const uint WildRampage2 = 20999;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Wild Anguish.
        ///
        /// Share with the target initially; use separate rubble during the later multi-target phase.
        /// </summary>
        public const uint WildAnguish = 21000;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Raging Slice.
        ///
        /// Line AOE.
        /// </summary>
        public const uint RagingSlice1 = 21002;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Raging Slice.
        ///
        /// Line AOE.
        /// </summary>
        public const uint RagingSlice2 = 21003;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Wild Rage.
        ///
        ///
        /// </summary>
        public const uint WildRage1 = 20994;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Wild Rage.
        ///
        ///
        /// </summary>
        public const uint WildRage2 = 20995;
        /// <summary>
        /// <see cref = "EnemyNpc.SpectralBerserker"/>'s Wild Rage.
        ///
        ///
        /// </summary>
        public const uint WildRage3 = 20996;
    }
}
