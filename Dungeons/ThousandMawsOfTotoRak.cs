using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 24: The Thousand Maws of Toto-Rak dungeon logic.
/// </summary>
public class ThousandMawsOfTotoRak : AbstractDungeon
{
    private const int DeadlyThrustPuddle = 2000404;
    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Deadly Thrust leaves a ten-yalm poison pool; allow another half-yalm for the edge.
        // A live selector recognizes new pools without clearing unrelated avoidance.
        AvoidanceManager.AddAvoidObject<GameObject>(
            canRun: () => Core.Me.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.AbacinationChamber,
            objectSelector: obj => obj.NpcId == DeadlyThrustPuddle,
            radiusProducer: obj => 10.5f);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheThousandMawsOfTotoRak;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();
        return false;
    }
}
