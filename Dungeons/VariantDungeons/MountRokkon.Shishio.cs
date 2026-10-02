using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private readonly System.Collections.Generic.List<Impact> _retainedCloudLines = new();
    private bool _shishioGhostsActive;
    private System.DateTime _shishioGhostResetAt;
    private System.DateTime _nextGhostSprint;
    private ushort _shishioFloorState;
    // All four right routes share this arena. Restrict activation to the actual
    // living boss so helpers left over during a wipe cannot retain the boundary.
    private static readonly Vector3 ShishioCenter = new(-40, 360, -300);
    private static bool InShishio() => InRokkonCombat() && Core.Me.Distance2D(ShishioCenter) < 35 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.BaseId == 0x3F40 && b.IsAlive && b.Distance2D(ShishioCenter) < 35);
    private void RegisterShishio()
    {
        ReleaseCloudLines();
        _retainedCloudLines.Clear();
        _shishioGhostsActive = false;
        _shishioGhostResetAt = System.DateTime.MaxValue;
        _nextGhostSprint = default;
        _shishioFloorState = 0;
        RegisterShishioClouds();
        RegisterShishioQuicksand();
        // Reisho's first circles are placed; the later instant pulses follow
        // Haunting Thralls. Keep those separate from harmless tether previews.
        AvoidanceManager.AddAvoidLocation<Impact>(InShishio, _ => 6.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == 34604));
        // October 2: the west ghost advanced about 3y/s while circle-only escape
        // repeatedly stopped at its moving edge, causing hits and a wipe. Reserve
        // two seconds ahead so ordinary avoidance chooses a lateral departure.
        // Rounded ends preserve gaps between the four converging ghosts.
        AvoidanceManager.AddAvoidPolygon<GameObject>(() => InShishio() && _shishioGhostsActive, null, 80, o => -o.Heading, _ => 1, _ => 15, _ => GhostFootprint, o => o.Location, () => GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x3F47 && o.Distance2D(ShishioCenter) < 35), priority: AvoidancePriority.High);
        // Native cloud lines have three widths. Their omen axis remains fixed
        // even when the helper turns toward its next target before damage lands.
        AvoidanceManager.AddAvoidPolygon<Impact>(() => InShishio() && !_cloudOwned, null, 110, c => -c.Heading, _ => 1, _ => 15, c => CloudLineFootprint(c.Action), c => c.Location, CloudLines, priority: AvoidancePriority.High);
        // October 2 Iwakura thralls exposed four 40y half-discs but generic
        // avoidance published none. Native omen axes include the directional
        // swipe; keep them through the observed 1.22s post-cast damage delay.
        AvoidanceManager.AddAvoidPolygon<Impact>(InShishio, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Sector(180, 40.5f), c => c.Location - Forward(c.Heading) * .5f, () => PendingImpacts().Where(c => (c.Action is 33776 or 33777) && c.Length > 0), priority: AvoidancePriority.High);
        // Keep the square's usable corners during thralls, then move inward for
        // Stormcloud Summons before the circular floor appears. The half-yalm
        // inset also clears that circle's narrow cardinal cutouts.
        AvoidanceHelpers.AddAvoidDonut(() => InShishio() && ShishioCircularFloor(), () => new[] { ShishioCenter }, 70, 19.5);
        AvoidanceHelpers.AddAvoidSquareDonut(() => InShishio() && !ShishioCircularFloor(), 39, 39, 140, 140, () => new[] { ShishioCenter });
        // Leaping Levin grows with the consumed smoke. Keep each action's real
        // radius; treating all clouds as the largest blast would remove safe lanes.
        AvoidanceManager.AddAvoidLocation<Impact>(InShishio, c => c.Action == 33758 ? 8.5f : c.Action == 33759 ? 12.5f : 23.5f, c => c.Location, ShishioCloudWave);
        // October 1 23:22:55: helper (-40,-300), omen origin (-40,-330),
        // tip (-40,-270) prove a centered 60x 14 line. Generic avoidance left
        // the player at its center. Retain the actual wave through server impact.
        AvoidanceManager.AddAvoidPolygon<Impact>(InShishio, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(7.5f, 30.5f, 30.5f), c => c.Location, () => PendingImpacts().Where(c => c.Action is 33757 or 34725 or 34732), priority: AvoidancePriority.High);
        // October 1 23:35:57: Rush spans the omen's full 39-yalm axis from
        // the north edge. It hit 1.04s after RB ended the cast report; retain the
        // fixed charge corridor rather than following the tiger after it moves.
        AvoidanceManager.AddAvoidPolygon<Impact>(InShishio, null, 80, c => -c.Heading, _ => 1, _ => 15, c => Rectangle(c.Action == 33774 ? 4.5f : 6.5f, .5f, c.Length + .5f), c => c.Location, () => PendingImpacts().Where(c => (c.Action is 33774 or 33766) && c.Length > 0), priority: AvoidancePriority.High);
        // Noble Pursuit's Rairin actors preview the subsequent 10x 40 strips.
        // At 23:37:34 the visible strip at(-35,-300), heading east, supplied
        // the hit after the charge ended. All three vanished after 35.344: their
        // supported visible lifecycle retains the follow-up through its impact.
        AvoidanceManager.AddAvoidPolygon<GameObject>(InShishio, null, 80, o => -o.Heading, _ => 1, _ => 15, _ => Rectangle(20.5f, 5.5f, 5.5f), o => o.Location, () => GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x3F42 && o.Distance2D(ShishioCenter) < 30), priority: AvoidancePriority.High);
        // Thunder's three strengths and Vasoconstrictor are placed attacks;
        // UpdateImpacts uses their omen centers instead of the helper's position.
        AvoidanceManager.AddAvoidLocation<Impact>(InShishio, c => c.Action == 33775 ? 5.5f : 6.5f, c => c.Location, ShishioPlacedImpacts);
        // References disagree about a six- versus eight-yalm inner radius. The
        // smaller hole is safe under either interpretation until live validation.
        // The last moving Reisho pulses overlap the beginning of this cast.
        // October 2's immediate inward return crossed a live ghost. Its visible
        // disappearance leaves the captured final two seconds for the return.
        AvoidanceHelpers.AddAvoidDonut(() => InShishio() && !HasVisibleShishioGhosts(), () => PendingImpacts().Where(c => c.Action == 33780).Select(c => c.Location).ToArray(), 30.5, 5.5);
    }

    private bool HasVisibleShishioGhosts() => _shishioGhostsActive && GameObjectManager.GameObjects.Any(o => o.IsValid && o.IsVisible && o.BaseId == 0x3F47 && o.Distance2D(ShishioCenter) < 35);
    private bool ShishioCircularFloor() => _shishioFloorState != 4 || GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.BaseId == 0x3F40 && b.IsCasting && b.CastingSpellId == 33751);
    private static Vector2[] CloudLineFootprint(uint action) => Rectangle(action == 33763 ? 1.5f : action == 33764 ? 3.5f : 6.5f, .5f, 100.5f);
    private static readonly Vector2[] GhostFootprint = BuildGhostFootprint();
    private static Vector2[] BuildGhostFootprint()
    {
        // Six-yalm pulses plus 0.5y padding;6y forward matches two seconds of
        // captured travel. This predicts only the current heading, not a full
        // future chase path that would eliminate safe floor when targets turn.
        const float radius = 6.5f, length = 6;
        const int halfCircleSegments = 16;
        var points = new Vector2[2 * (halfCircleSegments + 1)];
        for (var i = 0; i <= halfCircleSegments; i++)
        {
            var angle = i * System.Math.PI / halfCircleSegments;
            var x = radius * (float)System.Math.Cos(angle);
            var z = radius * (float)System.Math.Sin(angle);
            points[i] = new Vector2(x, -z);
            points[halfCircleSegments + 1 + i] = new Vector2(-x, length + z);
        }

        return points;
    }

    private void ObserveShishioGhosts()
    {
        if (!InShishio())
        {
            _shishioGhostsActive = false;
            _shishioGhostResetAt = System.DateTime.MaxValue;
            return;
        }

        var pending = PendingImpacts().ToArray();
        if (!_shishioGhostsActive && pending.Any(c => c.Action == 34604))
        {
            _shishioGhostsActive = true;
            _shishioGhostResetAt = System.DateTime.MaxValue;
        }

        // The sequence ends at the following Thunder Vortex. RB does not expose
        // the reference's twenty instant-damage events here; use the observed
        // next cast's damage fence, plus each ghost's supported visible lifecycle.
        var vortex = pending.FirstOrDefault(c => c.Action == 33780);
        if (_shishioGhostsActive && vortex != null)
            _shishioGhostResetAt = vortex.End;
        // Sprint preserves the short return window without overriding ordinary
        // escape. If unavailable, retain that escape and normal healing/recovery.
        if (_shishioGhostsActive && vortex != null && !HasVisibleShishioGhosts() && Core.Me.Distance2D(vortex.Location) > 5 && !Core.Me.IsCasting && System.DateTime.UtcNow >= _nextGhostSprint && ActionManager.IsSprintReady)
        {
            _nextGhostSprint = System.DateTime.UtcNow.AddSeconds(1);
            ActionManager.Sprint();
        }

        if (System.DateTime.UtcNow >= _shishioGhostResetAt)
            _shishioGhostsActive = false;
    }

    private void ObserveShishioFloor()
    {
        if (!InShishio() || DirectorManager.ActiveDirector is not ff14bot.Directors.InstanceContentDirector d || !d.IsValid)
            return;
        // Captured map entry 100 (event index 0x 64), ID 9835900, switches 1=circle
        // and 4=square with Stormcloud Summons. Rebuild local collision once per
        // physical change; the 02:24 retry stalled 8.5s in an escape request.
        // Unknown/duplicate state never triggers a speculative navigation reset.
        var floor = d.MapEffects.Where(m => m.ID == 9835900).ToArray();
        if (floor.Length != 1 || floor[0].State is not (1 or 4))
            return;
        var state = floor[0].State;
        if (state == _shishioFloorState)
            return;
        if (_shishioFloorState != 0 || state == 1)
        {
            AvoidanceManager.ResetNavigation();
            ff14bot.Helpers.Logging.Write("[Rokkon] Shishio floor changed; refreshing local collision once.");
        }

        _shishioFloorState = state;
    }

    private System.Collections.Generic.IEnumerable<Impact> ShishioPlacedImpacts()
    {
        var pending = PendingImpacts().ToArray();
        var yoki = pending.Where(c => c.Action == 33771).ToArray();
        // The second Yoki grid starts while the first impact fence is still
        // active. Combining both grids erases the dry quarter's intended gaps.
        var first = yoki.Length == 0 ? System.DateTime.MinValue : yoki.Min(c => c.End);
        return pending.Where(c => c.Action is 33762 or 34809 or 34811 or 33773 or 33775 || (c.Action == 33771 && c.End <= first.AddMilliseconds(350)));
    }
}
