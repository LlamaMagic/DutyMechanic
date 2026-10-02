using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private void RegisterSpiritflames()
    {
        // October 1 17:49:16–44: Npc 12363/Base 16263 becomes visible and crosses
        // the floor at roughly 3y/s. Five contact hits continued after the placed
        // circles ended. Hidden staging actors share one off-lane location and
        // must not reserve floor. Visibility ends when each flame leaves the arena.
        // Radius 2.4 plus 0.5 padding covers contact; the six-yalm forecast exposes
        // its approaching corridor without blocking its entire remaining path.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => FlameCapsule(), c => c.Location, () => GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(c => c.IsValid && c.BaseId == 0x3F87 && c.NpcId == 12363 && c.IsVisible && c.Distance2D(MokoCenter) < 32), priority: AvoidancePriority.High);
    // Spiritflame 34214's ordinary placed circles remain with generic avoidance.
    // This provider owns only moving contact hazards and issues no movement.
    }

    private static readonly Vector2[] SpiritflameFootprint = BuildFlameCapsule();
    private static Vector2[] FlameCapsule() => SpiritflameFootprint;
    private static Vector2[] BuildFlameCapsule()
    {
        // Rounded ends preserve small gaps between crossing flames that a bounding
        // rectangle would incorrectly remove. Local +Z follows the actor's heading.
        const float radius = 2.9f, length = 6;
        const int halfCircleSegments = 16;
        var points = new Vector2[2 * (halfCircleSegments + 1)];
        for (var i = 0; i <= halfCircleSegments; i++)
        {
            var angle = i * Math.PI / halfCircleSegments;
            var x = radius * (float)Math.Cos(angle);
            var z = radius * (float)Math.Sin(angle);
            points[i] = new Vector2(x, -z);
            points[halfCircleSegments + 1 + i] = new Vector2(-x, length + z);
        }

        return points;
    }
}
