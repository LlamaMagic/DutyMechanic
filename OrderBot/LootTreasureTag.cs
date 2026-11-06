using Buddy.Coroutines;
using Clio.Utilities;
using Clio.XmlEngine;
using DutyMechanic.Logging;
using DutyMechanic.Windows;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ff14bot.NeoProfiles.Tags;

/// <summary>
/// Loots nearby Treasure Coffers.
/// </summary>
[XmlElement("LootTreasure")]
public class LootTreasureTag : AbstractTaskTag
{
    private const float InteractRange = 1.0f;
    private const int ScanDuration = 3_000;
    private const int LootingCooldown = 1_500;
    private const float KnownChestMatchRadius = 8.0f;
    private const float KnownChestArriveDistance = 3.0f;
    private const int KnownChestWalkTimeout = 90_000;

    /// <summary>
    /// Gets or sets max search radius for Treasure Coffers.
    /// </summary>
    [XmlAttribute("MaxDistance")]
    public float MaxDistance { get; set; } = 40.0f;

    /// <summary>
    /// Gets or sets a value indicating whether to equip recommended after looting.
    /// </summary>
    [XmlAttribute("EquipRecommended")]
    public bool ShouldEquipRecommended { get; set; } = true;

    /// <summary>
    /// Gets or sets (optional) where a known Treasure Coffer sits. When set, the tag walks there first, unless that
    /// coffer is already loaded and open (e.g. re-running a room after a wipe), in which case it skips the walk.
    /// </summary>
    [XmlAttribute("XYZ")]
    public Vector3 ChestLocation { get; set; } = Vector3.Zero;

    /// <inheritdoc/>
    protected override async Task<bool> RunAsync()
    {
        if (ChestLocation != Vector3.Zero && !await WalkToKnownChestAsync())
        {
            return false;
        }

        // Give boss chests time to spawn so they don't get skipped if LootTreasure ticks too early
        Logger.Information($"Waiting up to {ScanDuration:N0}ms for treasure chests to spawn.");
        await Coroutine.Wait(ScanDuration, () => GameObjectManager.GetObjectsOfType<Treasure>()
            .Any(chest => chest.IsValid && chest.IsTargetable));

        IOrderedEnumerable<Treasure> nearbyChests = GameObjectManager.GetObjectsOfType<Treasure>()
          .Where(chest => chest.IsValid && chest.IsTargetable && !chest.IsOpen)
          .Where(chest => chest.Distance() < MaxDistance)
          .OrderBy(chest => chest.Distance());

        // Equip Recommended only works with gear in armory chest, so don't try if item didn't go into armory
        // (loot wasn't gear, already had unique item, armory full, "loot to armory" disabled, etc)
        int oldArmoryChestCount = InventoryManager.FilledArmorySlots.Count();

        foreach (Treasure chest in nearbyChests)
        {
            Logger.Information($"Found treasure chest at {chest.Location}");

            while (Core.Me.Distance(chest.Location) > InteractRange)
            {
                await CommonTasks.MoveTo(chest.Location);
                await Coroutine.Yield();
            }

            Navigator.PlayerMover.MoveStop();
            await Coroutine.Sleep(250);

            chest.Interact();

            await Coroutine.Sleep(LootingCooldown);
            Core.Player.ClearTarget();
        }

        if (ShouldEquipRecommended)
        {
            bool hasNewArmoryItem = oldArmoryChestCount < InventoryManager.FilledArmorySlots.Count();

            if (hasNewArmoryItem)
            {
                Logger.Information($"Looted new item to Armory Chest; trying to Equip Recommended.");
                await RecommendEquip.EquipAsync();
            }
        }

        return false;
    }

    /// <summary>
    /// Walks to <see cref="ChestLocation"/> unless the coffer there is visibly already open.
    /// </summary>
    /// <returns><see langword="true"/> to go on and loot; <see langword="false"/> when the coffer is already open.</returns>
    private async Task<bool> WalkToKnownChestAsync()
    {
        Treasure knownChest = GameObjectManager.GetObjectsOfType<Treasure>()
            .Where(chest => chest.IsValid && chest.Location.Distance2D(ChestLocation) < KnownChestMatchRadius)
            .OrderBy(chest => chest.Location.Distance2D(ChestLocation))
            .FirstOrDefault();

        if (knownChest != null && (knownChest.IsOpen || !knownChest.IsTargetable))
        {
            Logger.Information($"Treasure chest at {ChestLocation} is already open; skipping it.");
            return false;
        }

        // Not loaded yet (too far away) or still closed: walk over as a plain MoveTo would.
        Stopwatch walk = Stopwatch.StartNew();
        while (Core.Me.Distance2D(ChestLocation) > KnownChestArriveDistance && Core.Me.IsAlive && walk.ElapsedMilliseconds < KnownChestWalkTimeout)
        {
            await CommonTasks.MoveTo(ChestLocation);
            await Coroutine.Yield();
        }

        Navigator.PlayerMover.MoveStop();
        return true;
    }
}
