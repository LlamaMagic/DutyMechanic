using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private Impact _shishioSand;
    private uint _shishioSandId;
    private DateTime _nextSandSprint;
    private void RegisterShishioQuicksand()
    {
        _shishioSand = null;
        _shishioSandId = 0;
        _nextSandSprint = default;
        // Sand kills after twelve seconds, but is required shelter for Yoki Uzu.
        // October 2 00:07:23: ordinary circle escape entered sand before Uzu and
        // died at 35.090. A later five-second crossing arrived after the snapshot:
        // sand reduced movement to 3.6y/s. Use the last six cast seconds and Sprint
        // so the inward/outward crossings fit the twelve-second sink limit.
        AvoidanceManager.AddAvoidPolygon<Impact>(() => InShishio() && !NeedsQuicksandShelter(), null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(20.5f, .5f, 30.5f), c => c.Location, () => _shishioSand == null ? Array.Empty<Impact>() : new[] { _shishioSand }, priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidLocation<Impact>(() => InShishio() && NeedsQuicksandShelter(), _ => 23.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == 33772));
    }

    private bool NeedsQuicksandShelter() => PendingImpacts().Any(c => c.Action == 33772 && c.End <= DateTime.UtcNow.AddSeconds(7.4)); // Six seconds plus the measured 1.4s impact fence.
    private void ObserveShishioQuicksand()
    {
        if (!InShishio() || DirectorManager.ActiveDirector is not ff14bot.Directors.InstanceContentDirector d || !d.IsValid)
        {
            _shishioSand = null;
            _shishioSandId = 0;
            return;
        }

        // Sprint is a normal general action, independent of job/loadout. Pace a
        // rejected request and preserve active casts until ordinary avoidance
        // cancels them; do not introduce a competing movement owner.
        if (NeedsQuicksandShelter() && !Core.Me.IsCasting && DateTime.UtcNow >= _nextSandSprint && ActionManager.IsSprintReady)
        {
            _nextSandSprint = DateTime.UtcNow.AddSeconds(1);
            ActionManager.Sprint();
        }

        var active = d.MapEffects.Where(m => m.ID is >= 9835902 and <= 9835905 && m.State == 1).ToArray();
        if (active.Length != 1)
        {
            _shishioSand = null;
            _shishioSandId = 0;
            return;
        }

        if (_shishioSandId == active[0].ID)
            return;
        _shishioSandId = active[0].ID;
        var geometry = ShishioSandGeometry(_shishioSandId);
        _shishioSand = new Impact
        {
            Location = geometry.origin,
            Heading = geometry.heading
        };
    }

    private static (Vector3 origin, float heading) ShishioSandGeometry(uint id)
    {
        // Installed layout rows rotate the same sand visual in quarter turns.
        // Live 9835903=1: (-47.887,-286.024) was dry; crossing to(-52.764,-290.803)
        // acquired 567. That anchors the north 30x 40 rectangle. The other rotations
        // correspond to east/north/west/south. The later 9835902 capture confirmed
        // east sand and dry ground atX-53; west/south still need live capture.
        return id switch
        {
            9835902 => (ShishioCenter + new Vector3(20, 0, 0), -(float)Math.PI / 2),
            9835903 => (ShishioCenter + new Vector3(0, 0, -20), 0),
            9835904 => (ShishioCenter + new Vector3(-20, 0, 0), (float)Math.PI / 2),
            9835905 => (ShishioCenter + new Vector3(0, 0, 20), (float)Math.PI),
            _ => throw new ArgumentOutOfRangeException(nameof(id))};
    }
}
