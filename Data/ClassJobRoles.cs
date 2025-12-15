using ff14bot.Enums;
using System.Collections.Generic;

namespace DutyMechanic.Data;

/// <summary>
/// Groupings of <see cref="ClassJobType"/>s into their roles.
/// </summary>
internal static class ClassJobRoles
{
    /// <summary>
    /// Gets all <see cref="ClassJobType"/>s in the Tank role.
    /// </summary>
    public static readonly HashSet<ClassJobType> Tanks =
    [
        ClassJobType.Gladiator,
        ClassJobType.Marauder,
        ClassJobType.Paladin,
        ClassJobType.Gunbreaker,
        ClassJobType.Warrior,
        ClassJobType.DarkKnight,
    ];

    /// <summary>
    /// Gets all <see cref="ClassJobType"/>s in the DPS role.
    /// </summary>
    public static readonly HashSet<ClassJobType> DPS =
    [
        ClassJobType.Lancer,
        ClassJobType.Archer,
        ClassJobType.Thaumaturge,
        ClassJobType.Pugilist,
        ClassJobType.Monk,
        ClassJobType.Dragoon,
        ClassJobType.Bard,
        ClassJobType.BlackMage,
        ClassJobType.Arcanist,
        ClassJobType.Summoner,
        ClassJobType.Rogue,
        ClassJobType.Ninja,
        ClassJobType.Machinist,
        ClassJobType.Samurai,
        ClassJobType.RedMage,
        ClassJobType.Dancer,
        ClassJobType.Reaper,
        ClassJobType.Viper,
        ClassJobType.Pictomancer,
    ];

    /// <summary>
    /// Gets all <see cref="ClassJobType"/>s in the Healer role.
    /// </summary>
    public static readonly HashSet<ClassJobType> Healers =
    [
        ClassJobType.Sage,
        ClassJobType.Astrologian,
        ClassJobType.WhiteMage,
        ClassJobType.Scholar,
        ClassJobType.Conjurer,
    ];

    /// <summary>
    /// Gets all <see cref="ClassJobType"/>s that primarily fight in melee range.
    /// </summary>
    public static readonly List<ClassJobType> Melee =
    [
        ClassJobType.Lancer,
        ClassJobType.Dragoon,
        ClassJobType.Pugilist,
        ClassJobType.Monk,
        ClassJobType.Rogue,
        ClassJobType.Ninja,
        ClassJobType.Samurai,
        ClassJobType.Reaper,
        ClassJobType.DarkKnight,
        ClassJobType.Gladiator,
        ClassJobType.Marauder,
        ClassJobType.Paladin,
        ClassJobType.Warrior,
        ClassJobType.Gunbreaker,
    ];

    internal static readonly Dictionary<ClassJobType, int> LimitBreak3 = new()
    {
        // Tank Limit Breaks
        { ClassJobType.Gladiator, 199 }, // Last Bastion
        { ClassJobType.Marauder, 4240 }, // Land Waker
        { ClassJobType.Paladin, 199 }, // Last Bastion
        { ClassJobType.Gunbreaker, 17105 }, // Gunmetal Soul
        { ClassJobType.Warrior, 4240 }, // Land Waker
        { ClassJobType.DarkKnight, 4241 }, // Dark Force

        // Melee DPS Limit Breaks
        { ClassJobType.Lancer, 4242 }, // Dragonsong Dive
        { ClassJobType.Pugilist, 202 }, // Final Heaven
        { ClassJobType.Monk, 202 }, // Final Heaven
        { ClassJobType.Dragoon, 4242 }, // Dragonsong Dive
        { ClassJobType.Rogue, 4243 }, // Chimatsuri
        { ClassJobType.Ninja, 4243 }, // Chimatsuri
        { ClassJobType.Samurai, 7861 }, // Doom of the Living
        { ClassJobType.Reaper, 24858 }, // The End
        { ClassJobType.Viper, 34866 }, // World-swallower

        // Ranged DPS Limit Breaks
        { ClassJobType.Archer, 4244 }, // Sagittarius Arrow
        { ClassJobType.Bard, 4244 }, // Sagittarius Arrow
        { ClassJobType.Machinist, 4245 }, // Satellite Beam
        { ClassJobType.Dancer, 17106 }, // Crimson Lotus

        // Magic DPS Limit Breaks
        { ClassJobType.Thaumaturge, 205 }, // Meteor
        { ClassJobType.BlackMage, 205 }, // Meteor
        { ClassJobType.Arcanist, 4246 }, // Teraflare
        { ClassJobType.Summoner, 4246 }, // Teraflare
        { ClassJobType.RedMage, 7862 }, // Vermilion Scourge
        { ClassJobType.Pictomancer, 34867 }, // Chromatic Fantasy

        // Healer Limit Breaks
        { ClassJobType.Sage, 24859 }, // Techne Makre
        { ClassJobType.Astrologian, 4248 }, // Astral Stasis
        { ClassJobType.WhiteMage, 208 }, // Pulse of Life
        { ClassJobType.Scholar, 4247 }, // Angel Feathers
        { ClassJobType.Conjurer, 208 } // Pulse of Life
    };
}
