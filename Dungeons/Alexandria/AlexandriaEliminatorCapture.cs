using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DutyMechanic.Data;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class Alexandria
{
    private readonly AlexandriaEliminatorTimeline eliminatorTimeline = new();
    private bool eliminatorUnsupportedCast;
    private readonly Dictionary<uint, (uint Target, double Earliest, double Latest)> eliminatorSpreadCasts = [];
    private AlexandriaGeometry.Hazard[] eliminatorSpreadShapes = [];
    private bool eliminatorSpreadIncomplete, eliminatorSpreadCorrupt;
    private readonly Dictionary<uint, (uint Target, Vector2 Origin, double Earliest, double Latest)> eliminatorProteanCasts = [];
    private AlexandriaGeometry.Hazard[] eliminatorProteanShapes = [];
    private (Vector2 Origin, double Earliest, double Latest)[] eliminatorLocalProteans = [];
    private bool eliminatorProteanIncomplete, eliminatorProteanCorrupt;

    // Capture detached cast and target lifetimes for the movement owner. The
    // owner revalidates each pulse and releases movement on incomplete data.
    private void CaptureEliminatorCasts()
    {
        if (WorldManager.SubZoneId != (uint)SubZoneId.Reascension || Core.Me == null || !Core.Me.InCombat)
        {
            eliminatorTimeline.Reset(); eliminatorUnsupportedCast = false;
            eliminatorSpreadCasts.Clear(); eliminatorSpreadShapes = []; eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = false;
            eliminatorProteanCasts.Clear(); eliminatorProteanShapes = []; eliminatorLocalProteans = [];
            eliminatorProteanIncomplete = eliminatorProteanCorrupt = false;
            return;
        }
        double now = MonotonicNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        bool Supported(uint action) => action is EnemyAction.PartitionLeft or EnemyAction.PartitionRight2 or
            EnemyAction.PartitionLeft2 or EnemyAction.PartitionRight or EnemyAction.TerminateHelper or
            EnemyAction.HaloHelper or EnemyAction.CompressionDamage or EnemyAction.ImpactConst or EnemyAction.Explosion;
        eliminatorTimeline.Reconcile(now, actors.Where(a => Supported(a.CastingSpellId)).Select(a => (a.ObjectId, a.CastingSpellId)));
        // Spreads, target-dependent lines and the knockback visual carry
        // participant or source state not represented by these scalar casts.
        // Their target and forced-movement lifetimes are retained below.
        eliminatorUnsupportedCast = actors.Any(a => a.CastingSpellId is EnemyAction.Overexposure or EnemyAction.LightofDevotion);
        eliminatorSpreadIncomplete = eliminatorSpreadCorrupt;
        var activeSpreads = actors.Where(a => a.CastingSpellId == EnemyAction.Electray).ToArray();
        foreach (var source in activeSpreads)
        {
            if (!source.SpellCastInfo.IsValid) { eliminatorSpreadIncomplete = true; continue; }
            uint target = source.SpellCastInfo.TargetId;
            double remaining = source.SpellCastInfo.RemainingCastTime.TotalSeconds;
            if (target is 0 or 0xE0000000 || !double.IsFinite(remaining) || remaining < 0)
            { eliminatorSpreadIncomplete = true; continue; }
            double finish = now + remaining;
            if (eliminatorSpreadCasts.TryGetValue(source.ObjectId, out var prior))
            {
                if (prior.Target != target) { eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = true; continue; }
                if (remaining == 0) continue;
                eliminatorSpreadCasts[source.ObjectId] = (target, Math.Min(prior.Earliest, finish - .5), Math.Max(prior.Latest, finish + .7));
            }
            else if (remaining > 0) eliminatorSpreadCasts[source.ObjectId] = (target, finish - .5, finish + .7);
            else eliminatorSpreadIncomplete = eliminatorSpreadCorrupt = true;
        }
        var spreadShapes = new List<AlexandriaGeometry.Hazard>();
        foreach (var entry in eliminatorSpreadCasts.ToArray())
        {
            if (now > entry.Value.Latest)
            {
                if (activeSpreads.Any(a => a.ObjectId == entry.Key)) eliminatorSpreadIncomplete = true;
                else eliminatorSpreadCasts.Remove(entry.Key);
                continue;
            }
            if (entry.Value.Target == Core.Me.ObjectId) continue;
            var target = actors.FirstOrDefault(a => a.ObjectId == entry.Value.Target && a.IsVisible && a.IsAlive);
            if (target == null || !AlexandriaMechanicShapes.TryCreate(EnemyAction.Electray, new Vector2(target.X, target.Z), Vector2.UnitX,
                .5, entry.Value.Earliest, entry.Value.Latest, out var spread))
            { eliminatorSpreadIncomplete = true; continue; }
            spreadShapes.Add(spread);
        }
        // If the local player carries a spread, unmarked party members must
        // also remain outside its six-yalm hit. Their current positions are
        // resampled and the chosen route is revalidated on every pulse.
        var localSpreads = eliminatorSpreadCasts.Values.Where(s => s.Target == Core.Me.ObjectId && s.Latest >= now).ToArray();
        if (localSpreads.Length != 0)
        {
            var party = actors.Where(a => a.IsVisible && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
            if (party.Length == 0) eliminatorSpreadIncomplete = true;
            foreach (var member in party)
            foreach (var local in localSpreads)
            {
                if (!AlexandriaMechanicShapes.TryCreate(EnemyAction.Electray, new Vector2(member.X, member.Z), Vector2.UnitX,
                    .5, local.Earliest, local.Latest, out var spread)) { eliminatorSpreadIncomplete = true; continue; }
                spreadShapes.Add(spread);
            }
        }
        eliminatorSpreadShapes = spreadShapes.ToArray();
        CaptureEliminatorProteans(now, actors);
        foreach (var actor in actors.Where(a => Supported(a.CastingSpellId)))
        {
            if (!actor.SpellCastInfo.IsValid) { eliminatorTimeline.MarkIncomplete(); continue; }
            double remaining = actor.SpellCastInfo.RemainingCastTime.TotalSeconds;
            float heading = actor.Heading;
            var origin = new Vector2(actor.X, actor.Z);
            if (!double.IsFinite(remaining) || remaining < 0 || !float.IsFinite(heading) ||
                !AlexandriaGeometry.Finite(origin) || Math.Abs(origin.X + 759) > 30 || Math.Abs(origin.Y + 648) > 30)
            { eliminatorTimeline.MarkIncomplete(); continue; }
            if (!AlexandriaEliminatorDirections.TryGet(actor.CastingSpellId, heading, out var direction))
            { eliminatorTimeline.MarkIncomplete(); continue; }
            double finish = now + remaining;
            // Sixty-five matched local receipts arrived 0.154–0.340 seconds
            // after cast finish. The wider window is provisional and does not
            // claim to bound unseen snapshots or other network conditions.
            eliminatorTimeline.Observe(new(actor.ObjectId, actor.CastingSpellId, origin, direction,
                finish - .5, finish + .7, false), remainingIsZero: remaining == 0);
        }
    }

    private void CaptureEliminatorProteans(double now, BattleCharacter[] actors)
    {
        eliminatorProteanIncomplete = eliminatorProteanCorrupt;
        var active = actors.Where(a => a.CastingSpellId == EnemyAction.TargetedLine).ToArray();
        foreach (var source in active)
        {
            if (!source.SpellCastInfo.IsValid) { eliminatorProteanIncomplete = true; continue; }
            uint target = source.SpellCastInfo.TargetId;
            double remaining = source.SpellCastInfo.RemainingCastTime.TotalSeconds;
            var origin = new Vector2(source.X, source.Z);
            if (target is 0 or 0xE0000000 || !double.IsFinite(remaining) || remaining < 0 || !AlexandriaGeometry.Finite(origin))
            { eliminatorProteanIncomplete = true; continue; }
            double finish = now + remaining;
            if (eliminatorProteanCasts.TryGetValue(source.ObjectId, out var prior))
            {
                if (prior.Target != target || Vector2.Distance(prior.Origin, origin) > .25f)
                { eliminatorProteanIncomplete = eliminatorProteanCorrupt = true; continue; }
                if (remaining == 0) continue;
                eliminatorProteanCasts[source.ObjectId] = (target, prior.Origin, Math.Min(prior.Earliest, finish - .5), Math.Max(prior.Latest, finish + .7));
            }
            else if (remaining > 0) eliminatorProteanCasts[source.ObjectId] = (target, origin, finish - .5, finish + .7);
            else eliminatorProteanIncomplete = eliminatorProteanCorrupt = true;
        }
        var shapes = new List<AlexandriaGeometry.Hazard>();
        var local = new List<(Vector2 Origin, double Earliest, double Latest)>();
        foreach (var entry in eliminatorProteanCasts.ToArray())
        {
            var cast = entry.Value;
            if (now > cast.Latest)
            {
                if (active.Any(a => a.ObjectId == entry.Key)) eliminatorProteanIncomplete = true;
                else eliminatorProteanCasts.Remove(entry.Key);
                continue;
            }
            if (cast.Target == Core.Me.ObjectId) { local.Add((cast.Origin, cast.Earliest, cast.Latest)); continue; }
            var target = actors.FirstOrDefault(a => a.ObjectId == cast.Target && a.IsVisible && a.IsAlive);
            if (target == null) { eliminatorProteanIncomplete = true; continue; }
            var delta = new Vector2(target.X, target.Z) - cast.Origin;
            if (delta.LengthSquared() < .25f || !AlexandriaMechanicShapes.TryCreate(EnemyAction.TargetedLine, cast.Origin,
                Vector2.Normalize(delta), .5, cast.Earliest, cast.Latest, out var shape))
            { eliminatorProteanIncomplete = true; continue; }
            shapes.Add(shape);
        }
        eliminatorProteanShapes = shapes.ToArray();
        eliminatorLocalProteans = local.ToArray();
    }
}
