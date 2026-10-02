using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private readonly Dictionary<uint, Impact> _shishioClouds = new();
    private readonly HashSet<Impact> _shishioForecasted = new();
    private void RegisterShishioClouds()
    {
        _shishioClouds.Clear();
        _shishioForecasted.Clear();
    }

    private void ObserveShishioClouds()
    {
        if (!InShishio())
        {
            RegisterShishioClouds();
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var id in _shishioClouds.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            _shishioClouds.Remove(id);
        _shishioForecasted.RemoveWhere(c => c.End <= now);
        foreach (var parent in PendingImpacts().Where(c => c.Action is 33757 or 34725 or 34726).ToArray())
        {
            var castEnd = parent.End.AddMilliseconds(parent.Action == 34726 ? -750 : -1100);
            if (_shishioForecasted.Contains(parent) || castEnd > now.AddSeconds(4))
                continue;
            // Absorbed clouds are still visible briefly: at 23:23:37 twelve
            // remained in RB, but only the two surviving clouds remained by
            // 23:23:40. Wait until the parent's final four seconds before taking
            // this supported actor snapshot. Appearance alone cannot predict damage.
            var clouds = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsVisible && b.BaseId == 0x3F41 && b.Distance2D(ShishioCenter) < 25).Select(b => (id: b.ObjectId, point: b.Location)).ToArray();
            if (clouds.Length == 0)
                continue;
            _shishioForecasted.Add(parent);
            foreach (var prediction in PredictShishioClouds(parent.Action, parent.Location, parent.Heading, clouds))
                _shishioClouds[prediction.id] = new Impact
                {
                    Action = parent.Action == 34726 ? 33760u : parent.Action == 34725 ? 33759u : 33758u,
                    Location = prediction.point,
                    End = castEnd.AddSeconds(prediction.delay + 1.1)
                };
        }

        // Short native reports refine the forecast's impact fence without losing
        // the early destination. Unknown layouts still get actual cast geometry.
        foreach (var pair in _impacts.Where(p => p.Value.Action is 33758 or 33759 or 33760))
            _shishioClouds[pair.Key] = pair.Value;
    }

    private static IEnumerable<(uint id, Vector3 point, double delay)> PredictShishioClouds(uint action, Vector3 origin, float heading, (uint id, Vector3 point)[] clouds)
    {
        if (action == 34726)
            return clouds.Select(c => (c.id, c.point, 7.1));
        var radius = action == 34725 ? 12f : 8f;
        var forward = Forward(heading);
        var remaining = clouds.ToList();
        // The centered lightning line triggers the first pair; subsequent clouds
        // chain through the preceding blast radius. Bound the traversal by removing
        // each discovered actor, so an unfamiliar disconnected layout cannot loop.
        var wave = remaining.Where(c =>
        {
            var d = c.point - origin;
            return Math.Abs(d.X * forward.X + d.Z * forward.Z) <= 30 && Math.Abs(d.X * forward.Z - d.Z * forward.X) <= 7;
        }).ToArray();
        var result = new List<(uint id, Vector3 point, double delay)>();
        var waveIndex = 0;
        while (wave.Length > 0)
        {
            var delay = action == 34725 ? waveIndex == 0 ? 1.6 : 3.1 : 1.0 + waveIndex;
            foreach (var cloud in wave)
            {
                result.Add((cloud.id, cloud.point, delay));
                remaining.Remove(cloud);
            }

            wave = remaining.Where(c => wave.Any(previous => previous.point.Distance2D(c.point) <= radius)).ToArray();
            waveIndex++;
        }

        return result;
    }

    private IEnumerable<Impact> ShishioCloudWave()
    {
        var pending = _shishioClouds.Values.Where(c => c.End > DateTime.UtcNow).ToArray();
        if (pending.Length == 0)
            return Array.Empty<Impact>();
        var first = pending.Min(c => c.End);
        // Once-on-Rokujo advances every second. Reserve three pairs: the two-pair
        // horizon let the October 2 escape stop in the final pair's circle, then
        // leave only during its 700ms cast, after the server's damage snapshot.
        // Twice also includes its immediately following
        // group: the 23:41:30 pair followed the preceding pair by only
        // 1.6 seconds. Waiting for that preceding fence to expire left too little
        // travel time and caused the 23:41:32 hit. Both pairs leave a central lane.
        return pending.Where(c => c.End <= first.AddSeconds(c.Action == 33758 ? 2.35 : c.Action == 33759 ? 1.8 : .35));
    }
}
