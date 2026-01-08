using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Abstract starting point for implementing specialized dungeon logic.
/// </summary>
public class SohmAl : AbstractDungeon
{
    /*
     * 1. Raskovnik NPCID: 3791 SubzoneID: 1609
     * 2. Myath NPCID: 3793 SubzoneID: 1612
     * 3. Tioman NPCID: 3798 SubzoneID: 1613
     */

    /* Raskovnik
     * SpellName: Acid Rain SpellId: 3794 SideStep
     * SpellName: Sweet Scent SpellId: 5013 SideStep
     * SpellName: Flower Devour SpellId: 5010 SideStep
     */

    /* Myath
     * 2005280
     * SpellName: Mad Dash SpellId: 3808 spread
     * SpellName: Mad Dash SpellId: 3809 stack
     */

    /* Tioman
     * SpellName: Chaos Blast SpellId: 3813 SideStep
     * SpellName: Comet SpellId: 3814 SideStep
     *
     */
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    private readonly HashSet<uint> MadDash = [3808,];

    private static readonly int MadDashDuration = 7_000;

    private readonly HashSet<uint> fireball = [3809];

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.SohmAl;

    /// <summary>
    /// Gets spell IDs to follow-dodge while any contained spell is casting.
    /// </summary>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [29272, 3809];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <summary>
    /// Executes dungeon logic.
    /// </summary>
    /// <returns><see langword="true"/> if this behavior expected/handled execution.</returns>
    public override async Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        AvoidanceManager.AddAvoidObject<GameObject>(() => Core.Player.InCombat, 6f, 2005287);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

        if (MadDash.IsCasting())
        {
            await MovementHelpers.Spread(MadDashDuration);
        }

        return false;
    }
}
