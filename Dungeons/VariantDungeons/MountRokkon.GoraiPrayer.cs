using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    // October 1 18:39: primary 9809416..23 emits a one-second activation pulse;
    // secondary 9809424..31 remains 1, becomes 16 while occupied, then returns 4.
    // Both families use clockwise N/NE/E/SE/S/SW/W/NW positions from the layout.
    // Captured tower failure followed visual disappearance by about 1.25s. Keep
    // the soak through that delay rather than walking away when the ring vanishes.
    private static readonly Vector3[] GoraiTowerPositions =
    {
        new(741, 91, -201),
        new(748.778f, 91, -197.778f),
        new(752, 91, -190),
        new(748.778f, 91, -182.222f),
        new(741, 91, -179),
        new(733.222f, 91, -182.222f),
        new(730, 91, -190),
        new(733.222f, 91, -197.778f)
    };
    private readonly Dictionary<int, PrayerTower> _prayerTowers = new();
    private readonly Dictionary<uint, ushort> _prayerMaps = new();
    private readonly CapabilityManagerHandle _prayerMovement = CapabilityManager.CreateNewHandle();
    private bool _prayerMoving, _prayerLeased;
    private bool _prayerFallback;
    private Vector3? _goraiCage;
    private DateTime _prayerCancelAt;
    private void RegisterGoraiPrayer()
    {
        ReleaseGoraiPrayer();
        var pad = .5f / (float)Math.Sin(Math.PI / 8);
        // 34023 and 34024 are four cones each, separated by two seconds. The
        // 18:39:24.588 second-wave hit landed 0.89s after its reported cast end.
        // Keep 1.1s and publish only the first unresolved wave. During towers the
        // positive-position planner below owns both requirements, avoiding a
        // standalone cone escape that could repeatedly drag us out of a soak.
        AvoidanceManager.AddAvoidPolygon<Impact>(() => InGorai() && (FirstPrayerTower() == null || _prayerFallback), null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Cone45(pad), c => c.Location - Forward(c.Heading) * pad, GoraiConeWave, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<Vector3>(() => InGorai() && _goraiCage.HasValue, null, 80, _ => 0, _ => 1, _ => 15, _ => GoraiCageRing(), p => p, () => _goraiCage.HasValue ? new[] { _goraiCage.Value } : Array.Empty<Vector3>(), priority: AvoidancePriority.High);
    }

    private IEnumerable<Impact> GoraiConeWave()
    {
        var pending = PendingImpacts().Where(c => c.Action is 34023 or 34024).ToArray();
        if (pending.Length == 0)
            return Array.Empty<Impact>();
        var first = pending.Min(c => c.End);
        return pending.Where(c => c.End <= first.AddMilliseconds(350));
    }

    private void ObserveGoraiPrayer()
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector d || !d.IsValid)
            return;
        var now = DateTime.UtcNow;
        var maps = d.MapEffects;
        var soakAuras = Core.Me.CharacterAuras.Where(a => a.Id is 3597 or 3598 or 3599).Select(a => a.Id).OrderBy(id => id).ToArray();
        // Wily Wall's captured northwest layout 9834579 changes 4->1 while
        // preparing,1->16 after the pull, then 16->4 when its baboon dies.
        // The other three placements share the installed asset. Keep avoidance
        // inside the occupied octagon, and rebuild navigation once per wall change.
        var cageIds = new uint[]
        {
            9834579,
            9834581,
            9834582,
            9834583
        };
        var cageCenters = new[]
        {
            new Vector3(731, 91, -200),
            new Vector3(751, 91, -200),
            new Vector3(731, 91, -180),
            new Vector3(751, 91, -180)
        };
        Vector3? cage = null;
        for (var i = 0; i < cageIds.Length; i++)
            if (maps.Any(m => m.ID == cageIds[i] && m.State == 16) && Core.Me.Distance2D(cageCenters[i]) < 8)
                cage = cageCenters[i];
        if (cage != _goraiCage)
        {
            _goraiCage = cage;
            AvoidanceManager.ResetNavigation();
        }

        for (var i = 0; i < GoraiTowerPositions.Length; i++)
        {
            var primary = 9809416u + (uint)i;
            var secondary = 9809424u + (uint)i;
            var p = maps.Where(m => m.ID == primary).ToArray();
            var s = maps.Where(m => m.ID == secondary).ToArray();
            if (p.Length != 1 || s.Length != 1)
                continue;
            var rising = p[0].State == 1 && (!_prayerMaps.TryGetValue(primary, out var old) || old != 1);
            // Re-registering mid-phase can miss the short pulse. A live secondary
            // ring is positive evidence, but conservatively use its current time.
            if (rising || (!_prayerMaps.ContainsKey(secondary) && s[0].State is 1 or 16))
                _prayerTowers[i] = new PrayerTower
                {
                    Index = i,
                    Seen = now,
                    End = now.AddSeconds(12),
                    RequiredAura = soakAuras.FirstOrDefault(id => !_prayerTowers.Values.Any(t => t.RequiredAura == id))
                };
            if (_prayerTowers.TryGetValue(i, out var tower) && s[0].State == 4 && _prayerMaps.TryGetValue(secondary, out var prior) && prior is 1 or 16)
                tower.End = now.AddSeconds(1.5);
            // October 1 21:23: first ring disappeared 23.864, its 3597 was consumed
            // by 24.669, but the old fence held until 25.364. Ordered 3597/98/99
            // disappear on successful soaks; the failed second retained 3598.
            // Release early only with both the closed native ring and consumption
            // of the exact aura observed when this tower was assigned.
            if (tower != null && tower.RequiredAura != 0 && s[0].State == 4 && !soakAuras.Contains(tower.RequiredAura))
                tower.End = now;
            _prayerMaps[primary] = p[0].State;
            _prayerMaps[secondary] = s[0].State;
        }

        foreach (var key in _prayerTowers.Where(t => t.Value.End <= now).Select(t => t.Key).ToArray())
            _prayerTowers.Remove(key);
    }

    private PrayerTower FirstPrayerTower() => _prayerTowers.Values.Where(t => t.End > DateTime.UtcNow).OrderBy(t => t.Seen).FirstOrDefault();
    private bool HandleGoraiPrayer()
    {
        var tower = FirstPrayerTower();
        if (tower == null)
        {
            _prayerFallback = false;
            ReleasePrayerMovement();
            return false;
        }

        var cones = GoraiConeWave().ToArray();
        var center = GoraiTowerPositions[tower.Index];
        var player = Core.Me.Location;
        // Fresh 6 reached 3.44y from the north tower only as its ring vanished;
        // the helper then moved there and resolved a failed Burst, not another
        // cone. Use a 2.5y interior and prefer actual occupancy before holding.
        // Holding a safe existing position avoids chasing the center. Candidate rings
        // supply a shared soak/cone destination instead of independent commands.
        var candidates = new List<Vector3>
        {
            player,
            center
        };
        foreach (var radius in new[]
        {
            1.5f,
            2f
        }

        )
            for (var i = 0; i < 32; i++)
            {
                var angle = i * Math.PI / 16;
                candidates.Add(center + new Vector3((float)Math.Sin(angle) * radius, 0, (float)Math.Cos(angle) * radius));
            }

        var safe = candidates.Where(p => p.Distance2D(center) <= 2.5f && Math.Abs(p.X - GoraiCenter.X) < 19.5f && Math.Abs(p.Z - GoraiCenter.Z) < 19.5f && cones.All(c => !InsideGoraiCone(p, c))).OrderBy(p => p.Distance2D(player)).ToArray();
        // October 1 21:23: waiting south of the apex until both waves expired
        // reached the north ring after its disappearance. A cast is a timed hit,
        // not a permanent wall. Evaluate the whole straight transfer against both
        // pending damage windows, including the destination after arrival. Native
        // avoidance retains priority below; this owns only the tower/cone overlap.
        var pendingCones = PendingImpacts().Where(c => c.Action is 34023 or 34024).ToArray();
        var now = DateTime.UtcNow;
        var timed = candidates.Where(p => p.Distance2D(center) <= 2.5f && Math.Abs(p.X - GoraiCenter.X) < 19.5f && Math.Abs(p.Z - GoraiCenter.Z) < 19.5f && GoraiPrayerTimedSegmentSafe(player, p, pendingCones, now)).OrderBy(p => p.Distance2D(player)).ToArray();
        var timedTransfer = timed.Length != 0;
        if (timedTransfer)
            safe = timed;
        // October 1 18:52 and 18:55: safe tower endpoints still crossed the opposite
        // cone during travel. Keep the entire segment safe. Between towers, stage
        // near the apex in a real safe sector, then cross as its next wave resolves;
        // never treat the apex itself as a safe hole. Short central transfers leave
        // enough travel time for the following tower without fighting cone avoidance.
        if (!timedTransfer && cones.Length != 0 && player.Distance2D(center) > 2.5f)
        {
            var reachable = safe.Where(p => GoraiPrayerSegmentSafe(player, p, cones)).ToArray();
            if (reachable.Length != 0)
                safe = reachable;
            else
            {
                var staging = Enumerable.Range(0, 64).Select(i => GoraiCenter + new Vector3(2.5f * (float)Math.Sin(i * Math.PI / 32), 0, 2.5f * (float)Math.Cos(i * Math.PI / 32))).Where(p => cones.All(c => !InsideGoraiCone(p, c))).OrderBy(p => p.Distance2D(player) + p.Distance2D(center)).ToArray();
                // When already unsafe, an immediate short escape is preferable to
                // freezing in the hit. Otherwise do not cross an active sector.
                safe = staging.Where(p => GoraiPrayerSegmentSafe(player, p, cones)).ToArray();
                // October 1 20:12:03: minimizing the full trip while already inside
                // the next cone chose an opposite central sector and crossed the
                // apex. Escape into the nearest safe sector first; the next tower
                // cannot justify a longer path through the resolving damage.
                if (safe.Length == 0 && cones.Any(c => InsideGoraiCone(player, c)))
                    safe = staging.OrderBy(p => p.Distance2D(player)).ToArray();
                if (safe.Length == 0 && cones.All(c => !InsideGoraiCone(player, c)))
                    safe = new[]
                    {
                        player
                    };
            }
        }

        if (safe.Length == 0)
        {
            // An unknown overlap must not force an unsafe soak. Release only our
            // movement and keep the routine schedulable for healing and recovery.
            ReleasePrayerMovement();
            if (!_prayerFallback)
                ff14bot.Helpers.Logging.Write("[Rokkon] No shared tower/cone destination; preserving cone avoidance and routine recovery.");
            _prayerFallback = true;
            return false;
        }

        _prayerFallback = false;
        CapabilityManager.Update(_prayerMovement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Holding Gorai's earliest tower through its cone overlap");
        _prayerLeased = true;
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            _prayerMoving = false;
            return false;
        }

        var destination = safe[0];
        // Arrival tolerance must not accept the wrong side of a cone boundary.
        if (player.Distance2D(destination) <= .3f && (timedTransfer ? GoraiPrayerTimedSegmentSafe(player, player, pendingCones, now) : cones.All(c => !InsideGoraiCone(player, c))))
        {
            if (_prayerMoving)
                Navigator.PlayerMover.MoveStop();
            _prayerMoving = false;
            return false; // A successful soak must not starve heals or the rotation.
        }

        if (Core.Me.IsCasting && DateTime.UtcNow >= _prayerCancelAt)
        {
            _prayerCancelAt = DateTime.UtcNow.AddMilliseconds(750);
            ActionManager.StopCasting();
        }

        Navigator.PlayerMover.MoveTowards(destination);
        _prayerMoving = true;
        return true;
    }

    private static bool InsideGoraiCone(Vector3 point, Impact cone)
    {
        var delta = point - cone.Location;
        var distance = (float)Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        if (distance < 1.4f)
            return true;
        var dot = (delta.X * (float)Math.Sin(cone.Heading) + delta.Z * (float)Math.Cos(cone.Heading)) / distance;
        // Match the registered cone's 0.5y side padding when testing a positive
        // destination; a nominal boundary is unsafe even inside a tower.
        return dot >= Math.Cos(Math.PI / 8 + Math.Asin(Math.Min(1, .5f / distance)));
    }

    private static bool GoraiPrayerSegmentSafe(Vector3 start, Vector3 end, Impact[] cones)
    {
        // Half-yalm samples match the geometric margin and include both endpoints.
        var steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(end) / .5f));
        for (var i = 0; i <= steps; i++)
            if (cones.Any(c => InsideGoraiCone(start + (end - start) * ((float)i / steps), c)))
                return false;
        return true;
    }

    private static bool GoraiPrayerTimedSegmentSafe(Vector3 start, Vector3 end, Impact[] cones, DateTime now)
    {
        var distance = start.Distance2D(end);
        foreach (var cone in cones)
        {
            // End includes the measured 1.1s impact fence. Cover the last 0.4s
            // rather than treating the earlier cast animation as lethal terrain.
            // Captured controlled travel spans 4.5–6y/s; checking that envelope
            // plus 150ms startup delay rejects routes relying on one ideal speed.
            var last = (cone.End - now).TotalSeconds;
            if (last < 0)
                continue;
            var first = Math.Max(0, last - .4);
            var samples = Math.Max(1, (int)Math.Ceiling((last - first) / .05));
            for (var i = 0; i <= samples; i++)
            {
                var time = first + (last - first) * i / samples;
                var slow = distance < .001f ? 1f : Math.Min(1f, (float)(Math.Max(0, time - .15) * 4.5 / distance));
                var fast = distance < .001f ? 1f : Math.Min(1f, (float)(time * 6 / distance));
                // Check the continuous reachable segment at the hit time; safe
                // slow/fast endpoints alone could straddle a dangerous apex.
                if (!GoraiPrayerSegmentSafe(start + (end - start) * slow, start + (end - start) * fast, new[] { cone }))
                    return false;
            }
        }

        return true;
    }

    private void ReleasePrayerMovement()
    {
        if (_prayerMoving && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        _prayerMoving = false;
        if (_prayerLeased)
            CapabilityManager.Clear(_prayerMovement, CapabilityFlags.Movement, "Gorai tower ownership ended");
        _prayerLeased = false;
    }

    private void ReleaseGoraiPrayer()
    {
        ReleasePrayerMovement();
        _prayerTowers.Clear();
        _prayerMaps.Clear();
        _prayerFallback = false;
        _goraiCage = null;
    }

    private static Vector2[] GoraiCageRing()
    {
        // The inner octagon has circumradius 7.2858 and 22.5-degree rotation.
        // Reduce its apothem by 0.5y; opposite winding leaves a real polygon hole,
        // preserving corners instead of replacing this floor with a circular bound.
        var inner = 7.2858 - .5 / Math.Cos(Math.PI / 8);
        var points = new List<Vector2>();
        for (var i = 0; i <= 8; i++)
        {
            var a = Math.PI / 8 + i * Math.PI / 4;
            points.Add(new Vector2(70 * (float)Math.Cos(a), 70 * (float)Math.Sin(a)));
        }

        for (var i = 8; i >= 0; i--)
        {
            var a = Math.PI / 8 + i * Math.PI / 4;
            points.Add(new Vector2((float)(inner * Math.Cos(a)), (float)(inner * Math.Sin(a))));
        }

        return points.ToArray();
    }

    private sealed class PrayerTower
    {
        internal int Index;
        internal uint RequiredAura;
        internal DateTime Seen, End;
    }
}
