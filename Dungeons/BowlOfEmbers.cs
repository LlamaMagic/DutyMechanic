using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 20: The Bowl of Embers dungeon logic.
/// </summary>
public class BowlOfEmbers : AbstractDungeon
{
    private const int IfritNPCID = 1185;

    private static readonly Vector3 IfritArenaCenter = new(2.016229f, 0f, 1.375818f);

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheBowlOfEmbers;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        

        // Boss 1
        // In general, if not tank stay out of the front to avoid AOE breath attack
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: ShouldAvoidIfritCone,
            objectSelector: (bc) => bc.NpcId == IfritNPCID && bc.CanAttack,
            leashPointProducer: () => IfritArenaCenter,
            leashRadius: 40.0f,
            rotationDegrees: 0.0f,
            radius: 15.0f,
            arcDegrees: 160.0f);

        return false;
    }

    private static bool ShouldAvoidIfritCone()
    {
        if (!Core.Player.InCombat || WorldManager.ZoneId != 1045 || Core.Me.IsTank())
        {
            return false;
        }

        if (!PartyManager.IsInParty || PartyManager.NumMembers < 4)
        {
            return false;
        }

        var ifrit = GameObjectManager.GetObjectsOfType<BattleCharacter>()
            .FirstOrDefault(bc => bc.NpcId == IfritNPCID && bc.CanAttack);

        return ifrit?.CurrentTargetId != Core.Me.ObjectId;
    }
    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        return false;
    }
}
