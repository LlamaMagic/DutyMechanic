using System.Linq;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class Vault
{
    private readonly VaultMarchState marchActors = new();

    // Captures show real marching rows first become visible at the square's
    // perimeter, whereas dormant actors with the same NPC IDs sit near the
    // middle. Keep an acquired row through its entire crossing and remove it
    // when that exact actor disappears, including during Holy Flame phases.
    private void UpdateMarchActors()
    {
        if (!bossEncounterActive || WorldManager.SubZoneId != 1572)
        {
            marchActors.Clear();
            return;
        }
        var knights = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.NpcId is DawnKnightNpc or DuskKnightNpc).ToArray();
        marchActors.Update(knights.Select(a => (a.ObjectId, a.Location.Distance2D(SerCharibertArenaCenter))));
    }
}
