using Buddy.Coroutines;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 71: Holminster Switch dungeon logic.
/// </summary>
public partial class HolminsterSwitch : AbstractDungeon
{
    private const int ForgivenDissonance = 8299;
    private const int TesleenTheForgiven = 8300;
    private const int Philia = 8301;
    private const int IronChainObj = 8570;

    /// <summary>
    /// Set of boss-related monster IDs.
    /// </summary>
    private static readonly HashSet<uint> BossIds =
    [
        ForgivenDissonance,
        TesleenTheForgiven,
        Philia,
        IronChainObj,
    ];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    private static readonly HashSet<uint> FeveredFlagellation = [15829, 15830, 17440];
    private static readonly HashSet<uint> Taphephobia = [15842, 16769];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.HolminsterSwitch;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } =
    [
        15602, 15609, 15843,
        15886, 16765, 16779, 16780, 16781, 16782, 17552,
    ];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        BattleCharacter tesleenNpc = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(NpcId: TesleenTheForgiven)
            .FirstOrDefault(bc => bc.IsTargetable);

        if (tesleenNpc != null && tesleenNpc.IsValid)
        {
            if (FeveredFlagellation.IsCasting())
            {
                // The numbered charges overlap puddles; clearing every avoid
                // erased those hazards during the captured death sequence.
                await MovementHelpers.Spread(10_000, 10);
            }
        }

        BattleCharacter philiaNpc = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(NpcId: Philia)
            .FirstOrDefault(bc => bc.IsTargetable);

        if (philiaNpc != null && philiaNpc.IsValid)
        {
            if (Taphephobia.IsCasting())
            {
                // The captured spread hit resolved about 4.8s after its tell;
                // the old 10s avoid remained active into the line stack.
                await MovementHelpers.Spread(6_000, 9.0f);
            }

        }

        // Dissonance's scoped lease owns SideStep independently of the current
        // target; the legacy toggle remains in place for the other bosses here.
        if (WorldManager.SubZoneId != (uint)SubZoneId.TheWound && WorldManager.SubZoneId != (uint)SubZoneId.TheAuction &&
            WorldManager.SubZoneId != (uint)SubZoneId.TheManorHouseCourtyard)
            BossIds.ToggleSideStep();

        return false;
    }
}
