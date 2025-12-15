using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;
using LlamaLibrary.Extensions;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Eureka Pagos boss logic
/// </summary>
public class EurekaPagos : AbstractDungeon
{
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.EurekaPagos;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    private static readonly Dictionary<ClassJobType, uint> TankInvul = new()
    {
        { ClassJobType.Warrior, 43 }, // Holmgang
        { ClassJobType.Paladin, 30 }, // Hallowed Ground
        { ClassJobType.DarkKnight, 3638 }, // Living Dead
        { ClassJobType.Gunbreaker, 16152 }, // Superbolide
    };

    /// <inheritdoc/>
    public override Task<bool> OnEnterDungeonAsync()
    {
        AvoidanceManager.AvoidInfos.Clear();

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        await FollowDodgeSpells();

                Core.Me.SetWalk();


        GameObject sleepingDragon = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.FrozenVoidDragon)
            .FirstOrDefault(bc => bc.Distance() < 40 && bc.IsVisible && bc.HasAura(EnemyAuras.Sleep));

        /*
        if (sleepingDragon != null)
        {
            if (!LlamaLibrary.Extensions.LocalPlayerExtensions.IsWalking)
            {
                Logger.Information($"Setting Walking because dragon is nearby with stop");
                Core.Me.SetWalk();
            }
        }
        else
        {
            if (LlamaLibrary.Extensions.LocalPlayerExtensions.IsWalking)
            {
                Logger.Information($"Setting Run");
                Core.Me.SetRun();
            }
        }
*/
        return false;
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// Frozen Void Dragon
        /// Walk around these guys so you don't wake them up
        /// </summary>
        public const uint FrozenVoidDragon = 7473;
    }

    private static class ArenaCenter
    {
    }

    private static class FateIds
    {
    }

    private static class EnemyAction
    {
    }

    private static class EnemyAuras
    {
        /// <summary>
        /// Sleep
        /// </summary>
        public const uint Sleep = 1596;
    }
}
