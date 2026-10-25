using Clio.Utilities;
using DutyMechanic.Data;
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
/// Lv. 20: Halatali dungeon logic.
/// </summary>
public class Halatali7_1 : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.Halatali7_1;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [EnemyAction.Fireflow, EnemyAction.Fireflow2, EnemyAction.HydroelectricShockVisual, EnemyAction.HydroelectricShock];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [EnemyAction.BurningBolt, EnemyAction.Levinfang, EnemyAction.StraightPunch];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Own all three casts from discovery so SideStep does not simultaneously
        // forbid the current safe area with a later wave's generic donut.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => IsPlainPoundStageActive(EnemyAction.PlainPound),
            objectSelector: caster => IsCasting(caster, EnemyAction.PlainPound),
            radiusProducer: _ => 10.5f));
        RegisterPlainPoundRing(EnemyAction.Tremblor, 9.5, 20.5);
        RegisterPlainPoundRing(EnemyAction.Earthquake, 19.5, 30.5);
        return Task.FromResult(false);
    }

    private void RegisterPlainPoundRing(uint actionId, double innerRadius, double outerRadius)
    {
        AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: () => IsPlainPoundStageActive(actionId),
            objectSelector: caster => IsCasting(caster, actionId),
            innerRadius: innerRadius,
            outerRadius: outerRadius);
    }

    private bool IsPlainPoundStageActive(uint actionId)
    {
        if (WorldManager.ZoneId != (ushort)ZoneId || Core.Me == null || !Core.Me.IsValid || !Core.Me.InCombat)
            return false;

        uint earliestStage = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(caster => caster.IsValid && caster.IsCasting
                && caster.CastingSpellId >= EnemyAction.PlainPound
                && caster.CastingSpellId <= EnemyAction.Earthquake)
            .Select(caster => caster.CastingSpellId)
            .DefaultIfEmpty(0u).Min();
        return earliestStage == actionId;
    }

    private static bool IsCasting(BattleCharacter caster, uint actionId)
        => caster.IsValid && caster.IsCasting && caster.CastingSpellId == actionId;

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await TankBusterSpells();
        await FollowDodgeSpells();

        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Firemane.
        /// </summary>
        public const uint Firemane = 1194;

        /// <summary>
        /// Second Boss: Thunderclap Guivre
        /// </summary>
        public const uint ThunderclapGuivre = 1196;

        /// <summary>
        /// Third Boss: Tangata
        /// </summary>
        public const uint Tangata = 1197;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: <see cref="Firemane"/>.
        /// </summary>
        public static readonly Vector3 Firemane = new(33f, 1f, 131f);

        /// <summary>
        /// Second Boss: Thunderclap Guivre.
        /// </summary>
        public static readonly Vector3 ThunderclapGuivre = new(-177.5f, -15f, -133.5f);

        /// <summary>
        /// Third Boss: Tangata.
        /// </summary>
        public static readonly Vector3 Tangata = new(-255.5f, 17f, 18f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Firemane
        /// Fireflow
        /// First wave of cone AOEs; follow the Duty Support party to safety.
        /// </summary>
        public const uint Fireflow = 40588;

        /// <summary>
        /// Firemane
        /// Fireflow
        /// Second wave of cone AOEs; follow the Duty Support party to safety.
        /// </summary>
        public const uint Fireflow2 = 40589;

        /// <summary>
        /// Thunderclap Guivre
        /// Hydroelectric Shock
        /// Water electrifies; follow the Duty Support party onto dry ground.
        /// </summary>
        public const uint HydroelectricShockVisual = 40593;

        /// <summary>
        /// Thunderclap Guivre's helper resolves one second after the visual cast;
        /// keep following the party onto dry ground through the actual hit.
        /// </summary>
        public const uint HydroelectricShock = 41113;

        /// <summary>Firemane's helper-cast tankbuster (not the boss's visual cast).</summary>
        public const uint BurningBolt = 40591;

        /// <summary>Thunderclap Guivre's tankbuster.</summary>
        public const uint Levinfang = 40594;

        /// <summary>Tangata's tankbuster.</summary>
        public const uint StraightPunch = 40602;

        /// <summary>Tangata's first concentric wave: a ten-yalm circle.</summary>
        public const uint PlainPound = 40599;

        /// <summary>Tangata's second concentric wave: a ten-to-twenty-yalm ring.</summary>
        public const uint Tremblor = 40600;

        /// <summary>Tangata's final concentric wave: a twenty-to-thirty-yalm ring.</summary>
        public const uint Earthquake = 40601;
    }
}
