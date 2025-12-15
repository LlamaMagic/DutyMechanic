using Clio.Utilities;
using DutyMechanic.Data;
using System.Collections.Generic;
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
    protected override HashSet<uint> SpellsToFollowDodge { get; } = new() { EnemyAction.Fireflow, EnemyAction.Fireflow2, EnemyAction.HydroelectricShock };
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = new() { };
    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
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
        /// Stack
        /// </summary>
        public const uint Fireflow = 40588;

        /// <summary>
        /// Firemane
        /// Fireflow
        /// Stack
        /// </summary>
        public const uint Fireflow2 = 40589;

        /// <summary>
        /// Thunderclap Guivre
        /// Hydroelectric Shock
        /// Stack
        /// </summary>
        public const uint HydroelectricShock = 40593;
    }
}
