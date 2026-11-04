using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

/// <summary>Scalar-only diagnostic capture; never selects or issues movement.</summary>
internal sealed class InterphosVisualCapture
{
    private static readonly Matrix44 EmptyMatrix = new();
    private readonly record struct Snapshot(byte State, uint Id,
        float M00, float M01, float M02, float M03,
        float M10, float M11, float M12, float M13,
        float M20, float M21, float M22, float M23,
        float M30, float M31, float M32, float M33);
    private readonly Dictionary<(ulong Actor, int Slot), Snapshot> previous = new();
    private readonly InterphosSceneCapture scene = new();
    private readonly HashSet<(ulong Actor, int Slot)> seen = new();
    private readonly List<(ulong Actor, int Slot)> removed = new();
    private double nextSample, nextPartySample, nextBaitSample, nextSplitSample, ends, baitEnds, splitEnds;
    private bool wasWindup, wasCoronation, wasSplit;
    private uint lastMarkerCast;
    private double markerEnds, nextMarkerSample;
    private double divideEnds;
    private string previousDivide;

    internal void Pulse(double now)
    {
        if (now < nextSample)
        {
            return;
        }

        nextSample = now + 0.2;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false);
        bool windup = false;
        bool coronation = false;
        bool split = false;
        uint markerCast = 0;
        foreach (var actor in actors)
        {
            if (!actor.IsValid || actor.NpcId != 13029 || !actor.IsCasting)
            {
                continue;
            }

            if (actor.CastingSpellId == 36604)
            {
                windup = true;
            }

            if (actor.CastingSpellId == 36629)
            {
                coronation = true;
            }

            if (actor.CastingSpellId == 36607)
            {
                split = true;
            }

            if (actor.BaseId == 0x41C0)
            {
                markerCast = actor.CastingSpellId;
            }
        }
        // Captures confirmed Authority markers 327/55/75/179 and statuses
        // 3815/4130. Reuse the shared detached actor logger after
        // movement, including the boss because Divide's icon may be source-side.
        // Windows are diagnostic only; these times never authorize movement.
        if (markerCast != lastMarkerCast)
        {
            if (markerCast == 39531)
            {
                markerEnds = now + 80;
            }
            else if (markerCast == 36629)
            {
                markerEnds = now + 35;
            }
            else if (markerCast == 36636)
            {
                // The old nine-second window missed the ordered-to-simultaneous
                // targets. Capture the complete sequence; this is no movement timer.
                divideEnds = now + 35;
                markerEnds = Math.Max(markerEnds, divideEnds);
            }
            lastMarkerCast = markerCast;
        }
        if (markerEnds > now && now >= nextMarkerSample)
        {
            nextMarkerSample = now + 0.5;
            var ids = PartyManager.VisibleMembers.Select(member => member.ObjectId).ToHashSet();
            LoggingHelpers.LogActorSignalChanges("Interphos.PartyMarkers", actor =>
                actor.ObjectId == Core.Me.ObjectId || ids.Contains(actor.ObjectId) || actor.BaseId == 0x41C0);
        }
        else if (markerEnds <= now)
        {
            LoggingHelpers.ClearActorSignalWatch("Interphos.PartyMarkers");
        }

        if (divideEnds > now)
        {
            // Party Vfx IDs did not reveal the Divide targets. Enumerate the
            // public LockOns list separately; no new native offsets or hooks.
            var ids = PartyManager.VisibleMembers.Select(member => member.ObjectId).ToHashSet();
            var rows = actors.Where(a => a.IsValid && (a.ObjectId == Core.Me.ObjectId || ids.Contains(a.ObjectId) || a.BaseId == 0x41C0))
                .OrderBy(a => a.ObjectId).Select(a =>
                {
                    var markers = a.VfxContainer.LockOns.Where(v => v != null && v.IsValid)
                        .Select(v => v.Id + "@" + v.Projection.Center.ToString());
                    return "0x" + a.ObjectId.ToString("X") + " pos=" + a.Location + " target=0x" + a.CurrentTargetId.ToString("X") + " icons=[" + string.Join(",", markers) + "]";
                });
            var snapshot = "cast=" + markerCast + " actors=[" + string.Join("; ", rows) + "]";
            if (snapshot != previousDivide)
            {
                previousDivide = snapshot;
                Logger.Information("[MechanicDiag] DIVIDE_LOCKONS " + snapshot);
            }
        }
        else
        {
            previousDivide = null;
        }

        if (split && !wasSplit)
        {
            splitEnds = now + 55;
        }

        wasSplit = split;
        if (splitEnds != 0 && now >= splitEnds)
        {
            splitEnds = 0;
        }

        if (splitEnds != 0 && now >= nextSplitSample)
        {
            nextSplitSample = now + 0.5;
            var player = Core.Me;
            var aura = player.GetAuraById(3814);
            var members = PartyManager.VisibleMembers.Select(member => member.ObjectId).ToHashSet();
            var party = actors.Where(actor => actor.IsValid && actor.IsVisible && members.Contains(actor.ObjectId))
                .Select(actor => FormattableString.Invariant($"0x{actor.ObjectId:X}:{actor.Location.X:F2},{actor.Location.Y:F2},{actor.Location.Z:F2}:{actor.IsAlive}:{(actor.GetAuraById(3814) != null)}"));
            Logger.Information(FormattableString.Invariant($"[InterphosCapture] SPLIT time={now:F3} player=({player.Location.X:F3},{player.Location.Y:F3},{player.Location.Z:F3}) gravity={(aura == null ? -1f : aura.TimeLeft):F2} control={MovementManager.GenerallyOutOfControl} party={string.Join(";", party)}"));
        }
        if (windup && !wasWindup)
        {
            ends = now + 35;
            Logger.Information($"[InterphosCapture] VFX_BEGIN time={now:F3} sampleSeconds=0.2");
        }
        wasWindup = windup;
        if (coronation && !wasCoronation)
        {
            baitEnds = now + 35;
            Logger.Information(FormattableString.Invariant($"[InterphosCapture] BAIT_BEGIN time={now:F3}"));
        }
        wasCoronation = coronation;
        if (baitEnds != 0 && now >= baitEnds)
        {
            Logger.Information(FormattableString.Invariant($"[InterphosCapture] BAIT_END time={now:F3}"));
            baitEnds = 0;
        }
        if (baitEnds != 0 && now >= nextBaitSample)
        {
            nextBaitSample = now + 0.2;
            // The X-floor hit was absent from the 0x41C4-only trace. Include the
            // 0x41C2 line helper, heading and timeline without inferring a hazard.
            var bait = GameObjectManager.GetObjectsOfType<GameObject>(true, false)
                .Where(actor => actor.IsValid && actor.BaseId is 0x41C4 or 0x41C2)
                .Select(actor => FormattableString.Invariant($"0x{actor.ObjectId:X}:{actor.GetType().Name}:base=0x{actor.BaseId:X}:heading={actor.Heading:F3}:{actor.Location.X:F3},{actor.Location.Z:F3}:{actor.IsVisible}:{(actor is BattleCharacter b ? b.CurrentTargetId.ToString("X") : "na")}:{BaitTimeline(actor)}"));
            var player = Core.Me.Location;
            Logger.Information(FormattableString.Invariant($"[InterphosCapture] BAIT time={now:F3} player=({player.X:F3},{player.Z:F3}) actors={string.Join(";", bait)}"));
        }
        if (ends == 0)
        {
            return;
        }

        if (now >= ends)
        {
            Logger.Information($"[InterphosCapture] VFX_END time={now:F3}");
            previous.Clear();
            ends = 0;
            return;
        }
        // Scene samples are raw diagnostics only and run after the movement pulse;
        // no inferred timeline state is allowed to choose a destination.
        scene.Pulse(now);
        // Public-party positions are diagnostic evidence only: sample detached scalars
        // once a second during this bounded window, after the movement update. They
        // cannot identify the cone alone and must never become a following strategy.
        if (now >= nextPartySample)
        {
            nextPartySample = now + 1;
            var members = PartyManager.VisibleMembers.Select(member => member.ObjectId).ToHashSet();
            var party = actors.Where(actor => actor.IsValid && actor.IsVisible && members.Contains(actor.ObjectId))
                .Select(actor => FormattableString.Invariant($"0x{actor.ObjectId:X}:{actor.Location.X:F3},{actor.Location.Z:F3}:{actor.IsAlive}"));
            // Position/time plus the public speed value permit independent unit
            // verification before relying on it for Authority's cast deadline.
            Logger.Information(FormattableString.Invariant($"[InterphosCapture] PARTY time={now:F3} speed={MovementManager.Speed:F3} player=({Core.Me.Location.X:F3},{Core.Me.Location.Z:F3}) actors={string.Join(";", party)}"));
        }
        seen.Clear();
        foreach (var actor in actors)
        {
            if (!actor.IsValid || actor.BaseId is not (0x41C0 or 0x233C or 0x41C1 or 0x41C2 or 0x41C4))
            {
                continue;
            }

            var slots = actor.VfxContainer.Vfx;
            for (int slot = 0; slot < slots.Length; ++slot)
            {
                var effect = slots[slot];
                var key = ((ulong)actor.ObjectId, slot);
                seen.Add(key);
                Matrix44 m = effect?.IsValid == true ? effect.Projection : EmptyMatrix;
                var value = new Snapshot(effect == null ? (byte)0 : effect.IsValid ? (byte)2 : (byte)1,
                    effect?.IsValid == true ? effect.Id : 0,
                    m.M00, m.M01, m.M02, m.M03, m.M10, m.M11, m.M12, m.M13,
                    m.M20, m.M21, m.M22, m.M23, m.M30, m.M31, m.M32, m.M33);
                bool existed = previous.TryGetValue(key, out var prior);
                if ((!existed && value.State == 0) || (existed && value == prior))
                {
                    continue;
                }

                previous[key] = value;
                Logger.Information(FormattableString.Invariant($"[InterphosCapture] VFX time={now:F3} actor=0x{actor.ObjectId:X} base=0x{actor.BaseId:X} cast={actor.CastingSpellId} slot={slot} state={value.State} id={value.Id} matrix={m.M00:R},{m.M01:R},{m.M02:R},{m.M03:R};{m.M10:R},{m.M11:R},{m.M12:R},{m.M13:R};{m.M20:R},{m.M21:R},{m.M22:R},{m.M23:R};{m.M30:R},{m.M31:R},{m.M32:R},{m.M33:R}"));
            }
        }
        removed.Clear();
        foreach (var key in previous.Keys)
        {
            if (!seen.Contains(key))
            {
                removed.Add(key);
            }
        }

        foreach (var key in removed)
        {
            Logger.Information($"[InterphosCapture] VFX_ACTOR_ABSENT time={now:F3} actor=0x{key.Actor:X} slot={key.Slot}");
            previous.Remove(key);
        }
    }

    // The public getter has been used for other encounters; capture only, since
    // its value has not yet been correlated with Waltz's action timeline event.
    private static string BaitTimeline(GameObject actor)
    {
        if (string.IsNullOrEmpty(actor.LuaString))
        {
            return "na";
        }

        return Lua.GetReturnVal<string>("local o=_G['" + actor.LuaString + "']; if not o or not o.GetActionTimelineId then return '-1' end; return tostring(o:GetActionTimelineId())");
    }

    internal void Reset()
    {
        previous.Clear();
        LoggingHelpers.ClearActorSignalWatch("Interphos.PartyMarkers");
        markerEnds = nextMarkerSample = 0;
        divideEnds = 0;
        previousDivide = null;
        lastMarkerCast = 0;
        scene.Reset();
        seen.Clear();
        removed.Clear();
        nextSample = nextPartySample = nextBaitSample = nextSplitSample = ends = baitEnds = splitEnds = 0;
        wasWindup = wasCoronation = wasSplit = false;
    }
}
