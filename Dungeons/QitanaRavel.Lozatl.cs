using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class QitanaRavel
{
    private sealed class Beam
    {
        internal uint Statue;
        internal Cast Owner;
        internal Vector3 Origin;
        internal float Heading, Length;
        internal DateTime Until => Owner.Until;
    }

    private readonly List<Beam> beams = new();
    private DateTime nextObservation;
    private DateTime nextStatueRead;
    private string objectSignature, mapSignature, statueStateSignature;

    private sealed class StatueLight
    {
        internal uint Controller;
        internal bool East;
        internal DateTime Until;
    }

    private readonly List<StatueLight> statueLights = new();
    private readonly HashSet<uint> litStatues = new();

    private void ObserveStatueLights()
    {
        DateTime now = DateTime.UtcNow;
        statueLights.RemoveAll(s => s.Until <= now);
        if (boss != 1)
        {
            statueLights.Clear();
            litStatues.Clear();
            statueStateSignature = null;
            return;
        }
        if (now < nextStatueRead) return;
        nextStatueRead = now.AddMilliseconds(250);

        // Doors share NPC 2007457 with the two statue controllers. Their captured
        // placements near (+8,328.78)/(-7,328.78) distinguish them. Read both
        // timeline states in one public Lua call without changing animations.
        var controllers = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(o => o.IsValid && o.NpcId == 2007457 && Math.Abs(o.X) is > 3 and < 12 &&
                Math.Abs(o.Z - 328.78f) < 1 && !string.IsNullOrEmpty(o.LuaString))
            .OrderBy(o => o.ObjectId).Take(3).ToArray();
        if (controllers.Length != 2) return; // Unknown is not proof of expiry.
        string keys = string.Join(",", controllers.Select(o => "'" + o.LuaString.Replace("\\", "\\\\").Replace("'", "\\'") + "'"));
        string values;
        try
        {
            values = Lua.GetReturnVal<string>("local r={};for i,k in ipairs({" + keys +
                "}) do local o=_G[k];local ok,s=pcall(function() return o:GetSharedGroupTimelineState() end);" +
                "r[i]=ok and tostring(s) or '?' end;return table.concat(r,',')");
        }
        catch (Exception)
        {
            return; // Keep existing predictions until their bounded expiry.
        }
        var states = (values ?? "").Split(',');
        if (states.Length != controllers.Length) return;
        string signature = string.Join(";", controllers.Select((o, i) => $"{o.ObjectId:X8}/x={o.X:F3}:{states[i]}"));
        if (signature != statueStateSignature)
        {
            statueStateSignature = signature;
            Logger.Information("[Qitana] Statue state [{0}]", signature);
        }
        for (int i = 0; i < controllers.Length; i++)
        {
            var o = controllers[i];
            if (states[i] == "4" && litStatues.Add(o.ObjectId))
            {
                // State 1 -> 4 warns about eight seconds before Light. Register
                // that half immediately for Fury overlaps; cap a lost reset at
                // ten seconds without extending it on confirming reads.
                var light = new StatueLight { Controller = o.ObjectId, East = o.X > LozatlCenter.X, Until = now.AddSeconds(10) };
                statueLights.Add(light);
                Logger.Information("[Qitana] Light tell controller={0:X8} east={1} until={2:O}", light.Controller, light.East, light.Until);
            }
            else if (states[i] == "1" && litStatues.Remove(o.ObjectId))
            {
                // Reset follows damage; keep 0.5 seconds for sampling/effect delivery.
                foreach (var light in statueLights.Where(s => s.Controller == o.ObjectId && s.Until > now.AddSeconds(.5)))
                    light.Until = now.AddSeconds(.5);
                Logger.Information("[Qitana] Light reset controller={0:X8}", o.ObjectId);
            }
        }
    }

    private void ObserveBeams(BattleCharacter[] actors)
    {
        beams.RemoveAll(b => b.Until <= DateTime.UtcNow);
        foreach (var statue in actors.Where(a => a.BaseId == Actors.RonkanStatue))
        {
            uint target = TetherTarget(statue, 37) ?? 0;
            var owner = Pending().LastOrDefault(c => c.Action == Actions.WrathOfTheRonka && c.Caster == target);
            if (owner == null || beams.Any(b => b.Statue == statue.ObjectId && ReferenceEquals(b.Owner, owner))) continue;

            // Tether 37 identifies the owning Dreamer. Captured barricades stop
            // rays at 12/22 yalms; open rows cover 35. Room two mirrors the rows.
            // A 1.5-yalm tolerance distinguishes rows 7.5 yalms apart; unknown rows
            // retain the full beam rather than inventing shelter.
            bool firstRoom = owner.Origin.Z > 550;
            bool east = statue.X > 0;
            float shortRow = firstRoom ? (east ? 627.75f : 642.75f) : (east ? 436f : 421f);
            float mediumRow = firstRoom ? (east ? 642.75f : 627.75f) : (east ? 421f : 436f);
            float length = Math.Abs(statue.Z - shortRow) < 1.5f ? 12 :
                Math.Abs(statue.Z - mediumRow) < 1.5f ? 22 : 35;
            beams.Add(new Beam
            {
                Statue = statue.ObjectId,
                Owner = owner,
                // Wall actors float above the floor; use the Dreamer's floor
                // height so avoidance height filtering cannot discard the beam.
                Origin = new Vector3(statue.X, owner.Origin.Y, statue.Z),
                Heading = statue.Heading,
                Length = length
            });
            Logger.Information("[Qitana] Ronkan beam statue={0:X8} length={1} origin={2} until={3:O}",
                statue.ObjectId, length, statue.Location, owner.Until);
        }
    }

    private void ObserveObjects()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || WorldManager.SubZoneId != (uint)Data.SubZoneId.TheDivineThreshold ||
            DateTime.UtcNow < nextObservation) return;
        nextObservation = DateTime.UtcNow.AddMilliseconds(250);

        // BattleCharacter/VFX capture misses the early statue animation.
        // Record decorative objects and map effects as diagnostic evidence.
        var objects = GameObjectManager.GameObjects.Where(o => o.IsValid && o is not BattleCharacter &&
            o.Location.Distance2D(LozatlCenter) < 65).OrderBy(o => o.ObjectId).Take(33).ToArray();
        string signature = string.Join(";", objects.Take(32).Select(o =>
            $"{o.ObjectId:X8}/{o.BaseId:X}/{o.NpcId} type={o.Type} loc={o.Location} heading={o.Heading:F3} visible={o.IsVisible} targetable={o.IsTargetable} lua={o.LuaString}"));
        if (signature != objectSignature)
        {
            objectSignature = signature;
            Logger.Information("[Qitana] Statue objects complete={0} [{1}]", objects.Length <= 32, signature);
        }
        if (DirectorManager.ActiveDirector is not InstanceContentDirector director) return;
        signature = string.Join(";", director.MapEffects.Select(e => $"{e.ID}:{e.State}:{e.Flags}:{e.unk}"));
        if (signature != mapSignature)
        {
            mapSignature = signature;
            Logger.Information("[Qitana] Statue map effects [{0}]", signature);
        }
    }
}
