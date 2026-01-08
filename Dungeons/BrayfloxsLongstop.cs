using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 32: Brayflox's Longstop dungeon logic.
/// </summary>
public class BrayfloxsLongstop : AbstractDungeon
{
    private const int GreatYellowPelican = 1280;
    private const int InfernoDrake = 1284;
    private const int Hellbender = 1286;
    private const int BubbleObj = 1383;
    private const int Aiatar = 1279;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.BrayfloxsLongstop;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        // Hellbender
        if (WorldManager.SubZoneId == (uint)SubZoneId.LongstopFrontblock)
        {
            BattleCharacter bubbleNpc = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(NpcId: BubbleObj)
                .FirstOrDefault(bc => bc.Distance() < 50 && bc.IsVisible);
            if (bubbleNpc != null && bubbleNpc.IsValid)
            {
                AvoidanceManager.AddAvoidObject<GameObject>(() => Core.Player.InCombat, 2f, bubbleNpc.ObjectId);
            }
        }

        return false;
    }
}
