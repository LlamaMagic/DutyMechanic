namespace DutyMechanic.Dungeons;

// These are Hard-mode floor radii, not normal-mode progression. Bomb activation
// groups the earliest wave without blocking the later rows needed for escape.
internal static class TitanHardPolicy
{
    internal static float FloorRadius(int jumps) => jumps switch { 0 => 25, 1 => 23, 2 => 20, _ => 15 };
    // Rows are staggered by 1.5s. Include the next row, but keep the final row
    // available until the earliest one clears; the circular pattern uses the same window.
    internal static bool BombIsImminent(double remaining, double first) => remaining <= first + 2.25;
}
