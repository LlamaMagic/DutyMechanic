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
    private readonly Dictionary<uint, (uint Target, double Earliest, double Latest)> amalgamSpreadCasts = [];
    private AlexandriaGeometry.Hazard[] amalgamSpreadShapes = [];
    private bool amalgamSpreadIncomplete, amalgamSpreadCorrupt;

    // Static Spark follows its target. Store only detached identities and
    // update the target positions on each bot pulse through the effect margin.
    private void CaptureAmalgamSpreads(double now, BattleCharacter[] actors)
    {
        amalgamSpreadIncomplete = amalgamSpreadCorrupt;
        var active = actors.Where(a => a.CastingSpellId == 36334).ToArray();
        foreach (var source in active)
        {
            if (!source.SpellCastInfo.IsValid) { amalgamSpreadIncomplete = true; continue; }
            uint target = source.SpellCastInfo.TargetId;
            double remaining = source.SpellCastInfo.RemainingCastTime.TotalSeconds;
            if (target is 0 or 0xE0000000 || !double.IsFinite(remaining) || remaining < 0)
            { amalgamSpreadIncomplete = true; continue; }
            double finish = now + remaining;
            if (amalgamSpreadCasts.TryGetValue(source.ObjectId, out var prior))
            {
                if (prior.Target != target) { amalgamSpreadIncomplete = amalgamSpreadCorrupt = true; continue; }
                if (remaining == 0) continue;
                amalgamSpreadCasts[source.ObjectId] = (target, Math.Min(prior.Earliest, finish - .2), Math.Max(prior.Latest, finish + .8));
            }
            else if (remaining > 0) amalgamSpreadCasts[source.ObjectId] = (target, finish - .2, finish + .8);
            else amalgamSpreadIncomplete = amalgamSpreadCorrupt = true;
        }
        var shapes = new List<AlexandriaGeometry.Hazard>();
        foreach (var entry in amalgamSpreadCasts.ToArray())
        {
            if (now > entry.Value.Latest)
            {
                if (active.Any(a => a.ObjectId == entry.Key)) amalgamSpreadIncomplete = true;
                else amalgamSpreadCasts.Remove(entry.Key);
                continue;
            }
            if (entry.Value.Target == Core.Me.ObjectId) continue;
            var target = actors.FirstOrDefault(a => a.ObjectId == entry.Value.Target && a.IsVisible && a.IsAlive);
            if (target == null || !AlexandriaMechanicShapes.TryCreate(36334, new Vector2(target.X, target.Z), Vector2.UnitX,
                .5, entry.Value.Earliest, entry.Value.Latest, out var spread))
            { amalgamSpreadIncomplete = true; continue; }
            shapes.Add(spread);
        }
        var local = amalgamSpreadCasts.Values.Where(s => s.Target == Core.Me.ObjectId && s.Latest >= now).ToArray();
        if (local.Length != 0)
        {
            var party = actors.Where(a => a.IsVisible && a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
                 PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
            if (party.Length == 0) amalgamSpreadIncomplete = true;
            foreach (var member in party)
            foreach (var cast in local)
            {
                if (!AlexandriaMechanicShapes.TryCreate(36334, new Vector2(member.X, member.Z), Vector2.UnitX,
                    .5, cast.Earliest, cast.Latest, out var spread)) { amalgamSpreadIncomplete = true; continue; }
                shapes.Add(spread);
            }
        }
        amalgamSpreadShapes = shapes.ToArray();
    }
}
