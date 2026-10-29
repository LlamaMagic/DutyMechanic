using System;
using System.Linq;
using System.Numerics;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Selects orb and meteor objectives from scalar encounter snapshots. The runtime
/// adapter must establish actor identities, collision radii and a safe approach first;
/// these decisions do not issue movement or retain frame-scoped game objects.
/// </summary>
internal static class ChrysalisPolicy
{
    internal enum OrbType
    {
        Physical,
        Magical
    }

    internal sealed record Member(uint Id, Vector2 Position, bool Tank);
    internal sealed record Orb(uint Id, Vector2 Position, OrbType Type);
    internal sealed record Tower(uint Id, Vector2 Position, float Radius, double Finish);
    // Boss visibility and combat flags are not reliable across the two-room transfer.
    // A full-health idle boss still releases ownership after a wipe.
    internal static bool EncounterActive(bool bossPresent, float bossHealth, bool combat, bool blight, bool extend, bool enteringPortal) => blight || extend || enteringPortal || bossPresent && (combat || bossHealth < 100);
    internal static Vector2 PortalWaitPoint(Vector2 player, float travelRadius)
    {
        if (player.LengthSquared() < 1)
        {
            player = new Vector2(0, 1);
        }

        // Normalizing a boundary ray can round past the travel radius (24.000002
        // was observed). Keep a tenth-yalm inset so the floor check stays strict.
        return Vector2.Normalize(player) * (travelRadius - 0.1f);
    }

    // Position identifies the actual floor, independently of status packets arriving
    // on adjacent frames. Refresh after one second of settling; the runtime adapter
    // retries a failed escape if collision was still unavailable at that point.
    internal sealed class RoomGeometry
    {
        private int room = -1;
        private double refreshAt = double.PositiveInfinity;
        internal bool Refresh(Vector2 position, double now, bool escaping)
        {
            int observed = Vector2.Distance(position, Vector2.Zero) <= 30 ? 0 : Vector2.Distance(position, new Vector2(220, 0)) <= 30 ? 1 : -1;
            if (observed < 0)
            {
                return false;
            }

            if (observed != room)
            {
                room = observed;
                refreshAt = now + 1;
            }

            if (escaping || now < refreshAt)
            {
                return false;
            }

            refreshAt = double.PositiveInfinity;
            return true;
        }

        internal void Reset()
        {
            room = -1;
            refreshAt = double.PositiveInfinity;
        }
    }

    // Alternation is determined by the vulnerability actually present, not by a color
    // guess or a fixed spawn order. If both vulnerabilities remain, collecting neither
    // is safer than treating the last observed one as permission for another hit.
    internal static bool CanCollect(OrbType type, bool physicalVulnerability, bool magicalVulnerability) => type == OrbType.Physical ? !physicalVulnerability : !magicalVulnerability;
    internal static Orb ChooseOrb(uint self, Vector2 player, Member[] party, Orb[] orbs, bool physicalVulnerability, bool magicalVulnerability, uint previous, float collisionRadius, Func<Vector2, bool> safeApproach)
    {
        bool Available(Orb orb)
        {
            if (!CanCollect(orb.Type, physicalVulnerability, magicalVulnerability) || !safeApproach(orb.Position))
            {
                return false;
            }

            // A tempting opposite orb is still unsafe if its approach crosses a
            // same-type orb. The caller supplies the measured collection radius.
            if (orbs.Any(other => other.Id != orb.Id && !CanCollect(other.Type, physicalVulnerability, magicalVulnerability) && SegmentDistanceSquared(player, orb.Position, other.Position) <= collisionRadius * collisionRadius))
            {
                return false;
            }

            // Leave an orb already being collected by another player. Do not reserve
            // every orb near a teammate: that would let a stationary healer block a lane.
            return !party.Any(member => member.Id != self && Vector2.DistanceSquared(member.Position, orb.Position) <= collisionRadius * collisionRadius);
        }

        Orb retained = orbs.FirstOrDefault(orb => orb.Id == previous && Available(orb));
        return retained ?? orbs.Where(Available).OrderBy(orb => Vector2.DistanceSquared(player, orb.Position)).ThenBy(orb => orb.Id).FirstOrDefault();
    }

    internal static Tower ChooseTower(uint self, Vector2 player, bool tank, Member[] party, Tower[] towers, uint previous, double now, float arrivalMargin, Func<Vector2, bool> safeApproach)
    {
        // Tanks take uncovered meteors while other jobs retain their tear/healing work.
        // A covered tower needs no second occupant, regardless of the occupant's job.
        if (!tank)
        {
            return null;
        }

        bool Available(Tower tower)
        {
            float inside = Math.Max(0, tower.Radius - arrivalMargin);
            if (tower.Finish <= now || !safeApproach(tower.Position) || party.Any(member => member.Id != self && Vector2.DistanceSquared(member.Position, tower.Position) <= inside * inside))
            {
                return false;
            }

            return true;
        }

        bool Inside(Vector2 position, Tower tower) => Vector2.DistanceSquared(position, tower.Position) <= MathF.Pow(Math.Max(0, tower.Radius - arrivalMargin), 2);
        // Hold a tower we already cover until its effect lands. A tank standing in
        // another active tower is occupied, even if closer to the next empty one.
        Tower held = towers.FirstOrDefault(tower => tower.Id == previous && Available(tower) && Inside(player, tower));
        held ??= towers.FirstOrDefault(tower => Available(tower) && Inside(player, tower));
        if (held != null)
        {
            return held;
        }

        var freeTanks = party.Where(member => member.Id != self && member.Tank && !towers.Any(tower => tower.Finish > now && Inside(member.Position, tower))).Append(new Member(self, player, true)).ToList();
        // Allocate each tank at most one tower. Comparing every tower independently
        // with the nearest tank can leave the farther tank idle while two need soaking.
        // Effect order wins; equal deadlines and distances use stable identities.
        foreach (Tower tower in towers.Where(Available).OrderBy(tower => tower.Finish).ThenBy(tower => tower.Id))
        {
            Member owner = freeTanks.OrderBy(member => Vector2.DistanceSquared(member.Position, tower.Position)).ThenBy(member => member.Id).FirstOrDefault();
            if (owner == null)
            {
                break;
            }

            if (owner.Id == self)
            {
                return tower;
            }

            freeTanks.Remove(owner);
        }

        return null;
    }

    private static float SegmentDistanceSquared(Vector2 start, Vector2 end, Vector2 point)
    {
        Vector2 delta = end - start;
        float length = delta.LengthSquared();
        float fraction = length == 0 ? 0 : Math.Clamp(Vector2.Dot(point - start, delta) / length, 0, 1);
        return Vector2.DistanceSquared(point, start + fraction * delta);
    }
}
