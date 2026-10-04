using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    // October 1 15:15:18–34 UTC: four harmless preview groups resolve later as
    // rectangle/cone/rectangle/cone damage. Four vulnerabilities followed the old
    // preview-only avoidance. Helpers are reused between groups, so object identity
    // alone cannot retain the choreography. RB reports the damaging cast for <0.5s.
    private const uint SeasonsVisual = 33665;
    private const uint SeasonsRectanglePreview = 33667;
    private const uint SeasonsConePreview = 33668;
    private readonly List<SeasonWave> _seasonWaves = new();
    private int _seasonPair;
    private bool _seasonPreparing;
    private DateTime _nextAvoidCastCancel;
    private void RegisterSeasons()
    {
        ResetSeasons();
        // Boss-wide ownership keeps harmless previews out of generic avoidance.
        ff14bot.NeoProfile.BotEvents.OnPulse += ObserveSeasons;
        var padding = .5f / (float)Math.Sin(Math.PI / 8);
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(3, .5f, 46.5f), c => c.Location, () => ActiveSeasonPair().Where(c => c.Action == SeasonsRectanglePreview), priority: AvoidancePriority.High);
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Cone45(padding), c => c.Location - Forward(c.Heading) * padding, () => ActiveSeasonPair().Where(c => c.Action == SeasonsConePreview), priority: AvoidancePriority.High);
    }

    private void ReleaseSeasons()
    {
        ff14bot.NeoProfile.BotEvents.OnPulse -= ObserveSeasons;
        ResetSeasons();
    }

    private void ResetSeasons()
    {
        _seasonWaves.Clear();
        _seasonPair = 0;
        _seasonPreparing = false;
    }

    private void ObserveSeasons(object sender, EventArgs args)
    {
        UpdateBossAvoidanceOwner();
        // OnPulse remains on the bot thread while avoidance holds the behavior tree.
        // Read scalar geometry here without awaiting or retaining native wrappers;
        // the explicit gaze/tower owners below also maintain their leased movement.
        // The same boundary must observe paired seals: RunAsync can be held behind
        // an earlier avoid, losing the short second cast entirely (October 1 wipe).
        if (TreeRoot.IsRunning && !CommonBehaviors.IsLoading && Core.Me != null && !Core.Me.IsDead)
        {
            // Physical arena changes also matter between combat ticks; keep their
            // collision latch independent of short-lived attack geometry.
            ObserveGoraiFloor();
            if (InMoko())
            {
                UpdateGiri();
                UpdateImpacts(MokoCenter);
                ObserveAzure();
            }
            else if (InYozakura())
            {
                UpdateImpacts(YozakuraCenter);
                ObserveLance();
                PrioritizeLivingGaol();
            }
            else if (InGorai())
                UpdateImpacts(GoraiCenter);
            else if (InEnenra())
                UpdateImpacts(EnenraCenter);
            else if (InShishio())
                UpdateImpacts(ShishioCenter);
            else
            {
                _impacts.Clear();
                _azure.Clear();
                _giri = _nextGiri = null;
            }

            if (InEnenra())
                ObserveEnenraSmoke();
            else
                _enenraSmoke.Clear();
            if (InGorai())
            {
                ObserveGorai();
                ObserveWorldlyPursuit();
                ObserveGoraiPrayer();
                // October 1 fresh 6: a routine action held the tree through the
                // second cone's release, leaving the north tower arrival at its
                // disappearance. Continue the existing narrow movement owner on
                // bot pulses; it yields stationary so heals remain schedulable.
                HandleGoraiPrayer();
                // Keep the timed orb transfer alive while avoidance holds the tree.
                HandleGoraiOrbs();
            }
            else
            {
                _goraiShapes.Clear();
                _goraiOrbs.Clear();
                _pursuit = null;
                _goraiBalladActive = false;
                ReleaseGoraiPrayer();
                ReleaseGoraiOrbs();
            }

            // The gaze/circle overlap deliberately owns facing and a short outward
            // move here: both must continue when a lower combat action holds the
            // tree. HandleFluffFacing yields to unrelated avoidance and releases
            // only its own movement when the overlap ends.
            if (InYozakura())
                HandleFluffFacing();
            else
                ReleaseFluff();
            ObserveRootChase();
            ObserveRightPetals();
            ObserveShishioFloor();
            ObserveShishioClouds();
            ObserveShishioQuicksand();
            ObserveShishioGhosts();
            ObserveCloudLines();
            // October 1 16:22:52: avoidance requested escape immediately, but a
            // Verstone cast held the player outside the seal's safe hole for 1.5s.
            // Cancel only when the existing avoidance owner is actively escaping;
            // do not issue competing movement or suppress routine healing/rotation.
            // A failed cancellation may retry after 750ms, without per-pulse spam.
            if ((InYozakura() || InMoko() || InGorai() || InEnenra() || InShishio()) && AvoidanceManager.IsRunningOutOfAvoid && Core.Me.IsCasting && DateTime.UtcNow >= _nextAvoidCastCancel)
            {
                _nextAvoidCastCancel = DateTime.UtcNow.AddMilliseconds(750);
                ActionManager.StopCasting();
            }
        }
        else
        {
            _impacts.Clear();
            _azure.Clear();
            _goraiShapes.Clear();
            _goraiOrbs.Clear();
            _pursuit = null;
            _enenraSmoke.Clear();
            _goraiBalladActive = false;
            ReleaseGoraiPrayer();
            ReleaseGoraiOrbs();
            ReleaseFluff();
            ResetRootChase();
            ReleaseRightPetals();
            ReleaseCloudLines();
            // Release the shelter lease on loading, death and stopped pulses.
            ReleaseShishioQuicksand();
            RegisterShishioClouds();
            _giri = _nextGiri = null;
        }

        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || Core.Me == null || Core.Me.IsDead || !InYozakura())
        {
            ResetSeasons();
            _lance = null;
            return;
        }

        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.Distance2D(YozakuraCenter) < 75).ToArray();
        var preparing = actors.Any(b => b.BaseId == YozakuraBase && b.CastingSpellId == SeasonsVisual);
        if (preparing && !_seasonPreparing)
        {
            _seasonWaves.Clear();
            _seasonPair = 0;
        }

        _seasonPreparing = preparing;
        foreach (var actor in actors)
        {
            var action = actor.CastingSpellId;
            if (action is SeasonsRectanglePreview or SeasonsConePreview)
            {
                var previewEnd = now + actor.SpellCastInfo.RemainingCastTime;
                // Same-wave helpers finish together; 0.5s tolerates pulse/report jitter
                // but cannot merge the measured ~2.2s-separated preview groups.
                var wave = _seasonWaves.FirstOrDefault(w => Math.Abs((w.PreviewEnd - previewEnd).TotalSeconds) < .5);
                if (wave == null)
                {
                    wave = new SeasonWave
                    {
                        PreviewEnd = previewEnd
                    };
                    _seasonWaves.Add(wave);
                    ff14bot.Helpers.Logging.Write("[Rokkon] Seasons preview group {0} captured.", _seasonWaves.Count);
                }

                if (!wave.Shapes.ContainsKey(actor.ObjectId))
                    wave.Shapes.Add(actor.ObjectId, new Impact
                    {
                        Action = action,
                        Location = actor.Location,
                        Heading = actor.Heading,
                        // Account for early cast-end reporting and 0.35s impact
                        // retention. Damage casts refine the matching shape below.
                        End = previewEnd.AddSeconds(8.25)
                    });
            }
            else if (action is >= 33669 and <= 33672)
            {
                var shapeAction = action <= 33670 ? SeasonsRectanglePreview : SeasonsConePreview;
                var shape = _seasonWaves.SelectMany(w => w.Shapes.Values).FirstOrDefault(s => s.End > now && s.Action == shapeAction && s.Location.Distance2D(actor.Location) < 1 && Math.Abs(Math.Atan2(Math.Sin(s.Heading - actor.Heading), Math.Cos(s.Heading - actor.Heading))) < .1);
                if (shape != null)
                    shape.End = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(350);
            }
        }
    }

    private IEnumerable<Impact> ActiveSeasonPair()
    {
        // The first two previews have a shared safe destination. Keep that pair until
        // both resolve, then expose the second pair. Sliding a two-wave window after
        // only one impact would combine opposing lanes and move before the second hit.
        var now = DateTime.UtcNow;
        if (_seasonWaves.Count > _seasonPair + 1 && _seasonWaves.Skip(_seasonPair).Take(2).All(w => w.Shapes.Count > 0 && w.Shapes.Values.All(s => s.End <= now)))
            _seasonPair += 2;
        return _seasonWaves.Skip(_seasonPair).Take(2).SelectMany(w => w.Shapes.Values).Where(s => s.End > now);
    }

    private sealed class SeasonWave
    {
        internal DateTime PreviewEnd;
        internal readonly Dictionary<uint, Impact> Shapes = new();
    }
}
