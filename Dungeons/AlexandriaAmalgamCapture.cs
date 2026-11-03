using System;
using System.Linq;
using System.Numerics;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    // A cast timer is not the server's damage snapshot. These provisional
    // windows feed the rolling owner and are rechecked as new casts appear.
    private void CaptureAmalgamCasts()
    {
        if (WorldManager.SubZoneId != (uint)SubZoneId.CorruptedMemoryCache || Core.Me == null || !Core.Me.InCombat)
            return;
        double now = MonotonicNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(actor => actor.IsValid).ToArray();
        CaptureAmalgamSpreads(now, actors);
        amalgamTimeline.Reconcile(now, actors.Where(actor => actor.CastingSpellId is
            36327 or 36329 or 36336 or 39136 or 39138 or 39254 or 39255 or 39256)
            .Select(actor => (actor.ObjectId, actor.CastingSpellId)));
        foreach (var actor in actors)
        {
            uint action = actor.CastingSpellId;
            // Positive stack requirements retain their marker lifecycle;
            // moving-target spreads are sampled separately above.
            if (action is 36333)
            {
                if (!actor.SpellCastInfo.IsValid) { amalgamTimeline.MarkIncomplete(); continue; }
                double unsupportedRemaining = actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
                if (!double.IsFinite(unsupportedRemaining) || unsupportedRemaining < 0) { amalgamTimeline.MarkIncomplete(); continue; }
                // A timer does not prove resolution. Retain the exact source,
                // generation and cast target until the observed marker ends.
                ulong generation = amalgamTimeline.TrackUnsupported(actor.ObjectId, action);
                uint targetId = actor.SpellCastInfo.TargetId;
                // A target can be temporarily unavailable on the first cast
                // pulse. Keep the timeline blocked and retry while casting;
                // never turn an unknown target into a completed marker.
                if (targetId is not (0 or 0xE0000000))
                    amalgamMarkers.ObserveCast(actor.ObjectId, action, targetId, generation);
                continue;
            }
            if (action is not (36327 or 36329 or 36336 or 39136 or 39138 or 39254 or 39255 or 39256)) continue;
            if (!actor.SpellCastInfo.IsValid) { amalgamTimeline.MarkIncomplete(); continue; }
            double remaining = actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
            float heading = actor.Heading;
            if (!double.IsFinite(remaining) || remaining < 0 || !float.IsFinite(heading))
            { amalgamTimeline.MarkIncomplete(); continue; }
            bool ground = action is 36336 or 39254 or 39255 or 39256;
            var point = ground ? actor.SpellCastInfo.CastLocation : actor.Location;
            var origin = new Vector2(point.X, point.Z);
            // Reject zero and off-arena origins instead of constructing a
            // credible-looking hazard from an unpopulated game wrapper.
            if (!AlexandriaGeometry.Finite(origin) || Math.Abs(origin.X + 533) > 30 || Math.Abs(origin.Y + 373) > 30)
            { amalgamTimeline.MarkIncomplete(); continue; }
            var direction = new Vector2((float)Math.Sin(heading), (float)Math.Cos(heading));
            double finish = now + remaining;
            // Voltburst gain times range from 0.346 to 1.589 seconds after
            // estimated finish. Ternary has separate observed, sequential
            // effect windows so the circle and rings do not falsely overlap.
            var window = AlexandriaAmalgamTiming.Window(action, finish);
            amalgamTimeline.Observe(new(actor.ObjectId, action, origin, direction, window.Earliest, window.Latest, false),
                remainingIsZero: remaining == 0);
        }
        foreach (var pending in amalgamMarkers.Snapshot())
        {
            var source = actors.FirstOrDefault(actor => actor.ObjectId == pending.Source);
            var target = pending.Target == Core.Me.ObjectId ? Core.Me :
                actors.FirstOrDefault(actor => actor.ObjectId == pending.Target);
            bool casting = source != null && source.CastingSpellId == pending.Action;
            bool available = target != null && target.IsValid && target.IsVisible && target.IsAlive && target.VfxContainer.IsValid;
            bool present = available && target.VfxContainer.LockOns.Any(marker => marker != null && marker.IsValid && marker.Id == pending.Marker);
            if (amalgamMarkers.ObserveMarker(pending.Source, pending.Action, now, casting, available, present, out ulong generation) &&
                amalgamTimeline.ResolveUnsupported(pending.Source, pending.Action, generation))
                amalgamMarkers.Forget(pending.Source, pending.Action, generation);
        }
        if (amalgamMarkers.Incomplete) amalgamTimeline.MarkIncomplete();
    }
}
