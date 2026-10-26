using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
// Visible hands announce Wavebreaker without a cast. Keep scalar lanes and resolve the first bank before the opposite bank.
public partial class AnamnesisAnyder
{
    private sealed class RukshsWave
    {
        internal uint Id;
        internal Vector3 Origin;
        internal float Heading;
        internal bool Wide, Narrow;
        internal DateTime LastVisible;
    }

    private readonly Dictionary<uint, RukshsWave> rukshsWaves = new();
    private readonly bool[] knownDrains = new bool[8];
    private readonly ushort[] drainStates = new ushort[8];
    private DateTime drainsUntil;
    private int drainChoice = -1, firstHandBank;
    private bool rukshsEnteredArena;
    private int rukshsFloor; //0=full rectangle,1=narrow sides,2=split center.
    private Vector3? narrowSupportPosition;
    private void UpdateNarrowSupport()
    {
        // NPC 8378 is the installed Y'shtola Duty Support actor. Unlike a nearest
        // ally (which can be the tank), she stages at the wave gap and crosses on
        // the animation cue unavailable in the hand's cast/visibility properties.
        // Limit this exception to narrow-floor waves after repeated native-avoid
        // failures. Missing/dead/off-floor support restores native wave geometry.
        var anchor = rukshsFloor == 1 ? RukshsAllies().FirstOrDefault(a => a.NpcId == 8378 && RukshsFloorSafe(a.Location, 1)) : null;
        narrowSupportPosition = anchor?.Location;
    }

    private static readonly uint[] DrainMapIds =
    {
        0x7DB91E,
        0x7DB91D,
        0x7DB91C,
        0x7DB91B,
        0x7DB917,
        0x7DB918,
        0x7DB919,
        0x7DB91A
    };
    private static Vector3 DrainPosition(int index) => new(index < 4 ? -11 : 11, -200, -465 + index % 4 * 10);
    // Layouts 0x7E20A6/0x7E20A7 select narrow or split floor; state 4 restores the rectangle. Leave half a yalm at the edges.
    internal static bool RukshsFloorSafe(Vector3 p, int floor) => Math.Abs(p.X) < (floor == 1 ? 7.5f : 15) && Math.Abs(p.Z + 450) < 19 && (floor != 2 || Math.Abs(p.Z + 450) > 4.5f);
    private IEnumerable<RukshsWave> PendingRukshsWaves()
    {
        bool firstPending = rukshsWaves.Values.Any(w => w.Narrow && Math.Sign(w.Origin.X) == firstHandBank);
        // Do not let delayed hand disappearance block the support-led crossing.
        // Only these narrow waves transfer ownership; floor and other hazards stay.
        return rukshsWaves.Values.Where(w => !w.Narrow || (!narrowSupportPosition.HasValue && (!firstPending || Math.Sign(w.Origin.X) == firstHandBank)));
    }

    private static Vector2[] WavePolygon(bool wide)
    {
        // Wavebreaker lanes are 36-by-8 or 21-by-10, facing inward from the hand. Pad every edge by half a yalm.
        float half = wide ? 5.5f : 4.5f, length = wide ? 21.5f : 36.5f;
        return new[]
        {
            new Vector2(-half, -.5f),
            new Vector2(half, -.5f),
            new Vector2(half, length),
            new Vector2(-half, length)
        };
    }

    internal static bool InRukshsWave(Vector3 p, Vector3 origin, float heading, bool wide)
    {
        float x = p.X - origin.X, z = p.Z - origin.Z;
        float forward = x * (float)Math.Sin(heading) + z * (float)Math.Cos(heading);
        float side = x * (float)Math.Cos(heading) - z * (float)Math.Sin(heading);
        return forward >= -.5f && forward <= (wide ? 21.5f : 36.5f) && Math.Abs(side) <= (wide ? 5.5f : 4.5f);
    }

    private void UpdateRukshsWaves()
    {
        var now = DateTime.UtcNow;
        if (RukshsFloorSafe(Core.Me.Location, 0))
        {
            rukshsEnteredArena = true;
        }

        if (DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
        {
            var effects = director.MapEffects;
            int floor = effects.Any(e => e.ID == 0x7E20A6 && e.State == 1) ? 1 : effects.Any(e => e.ID == 0x7E20A7 && e.State == 1) ? 2 : 0;
            if (floor != rukshsFloor)
            {
                firstHandBank = 0;
                rukshsFloor = floor;
            }

            // Seabed starts the next choreography and ends the previous drain
            // assignment. A new state1 arms a drain; covered state4 alone never
            // invents an active drain from the director's persistent idle entries.
            bool ceremony = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.BaseId == 0x2CFF && a.IsCasting && a.CastingSpellId == 19323);
            if (ceremony)
            {
                Array.Clear(knownDrains, 0, 8);
                drainsUntil = DateTime.MinValue;
                drainChoice = -1;
            }

            for (int i = 0; i < 8; i++)
            {
                var entry = effects.FirstOrDefault(e => e.ID == DrainMapIds[i]);
                drainStates[i] = entry.State;
                if (!ceremony && entry.State == 1 && !knownDrains[i])
                {
                    knownDrains[i] = true;
                    // Reference12/16s drain window: bounded16s ceiling avoids a
                    // stale permanent assignment; Seabed normally releases sooner.
                    if (drainsUntil <= now)
                    {
                        drainsUntil = now.AddSeconds(16);
                    }
                }
            }
        }

        foreach (var hand in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsVisible && a.BaseId == 0x2D00 && a.Location.Distance2D(RukshsCenter) < 40))
        {
            if (!rukshsWaves.TryGetValue(hand.ObjectId, out var wave))
            {
                wave = new RukshsWave
                {
                    Id = hand.ObjectId,
                    Origin = hand.Location,
                    Heading = hand.Heading,
                    Wide = rukshsFloor == 0,
                    Narrow = rukshsFloor == 1
                };
                rukshsWaves.Add(hand.ObjectId, wave);
                if (wave.Narrow && firstHandBank == 0)
                {
                    firstHandBank = Math.Sign(wave.Origin.X);
                }
            }

            wave.LastVisible = now;
        }

        // Impacts precede hand disappearance. A 0.2-second visibility grace tolerates a missed tick.
        foreach (var id in rukshsWaves.Where(p => (now - p.Value.LastVisible).TotalSeconds > .2).Select(p => p.Key).ToArray())
        {
            rukshsWaves.Remove(id);
        }
    }

    private Vector3? SelectRukshsDrain()
    {
        if (DateTime.UtcNow >= drainsUntil || rukshsFloor != 0)
        {
            drainChoice = -1;
            return null;
        }

        var allies = RukshsAllies();
        bool Eligible(int i) => knownDrains[i] && drainStates[i] is 1 or 4 && (drainStates[i] == 1 || i == drainChoice) && !allies.Any(a => a.Location.Distance2D(DrainPosition(i)) < 2) && !rukshsWaves.Values.Any(w => InRukshsWave(DrainPosition(i), w.Origin, w.Heading, w.Wide)) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(DrainPosition(i)));
        if (drainChoice >= 0 && Eligible(drainChoice))
        {
            return DrainPosition(drainChoice);
        }

        drainChoice = Enumerable.Range(0, 8).Where(Eligible).OrderBy(i => Core.Me.Location.Distance2D(DrainPosition(i))).DefaultIfEmpty(-1).First();
        return drainChoice < 0 ? null : DrainPosition(drainChoice);
    }

    private void RegisterRukshsWaves()
    {
        rukshsAvoids.Add(AvoidanceManager.AddAvoidPolygon<RukshsWave>(condition: RukshsActive, leashPointProducer: null, leashRadius: 60, rotationProducer: w => -w.Heading, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: w => WavePolygon(w.Wide), locationProducer: w => w.Origin, collectionProducer: PendingRukshsWaves));
        rukshsAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => RukshsActive() && rukshsEnteredArena && rukshsFloor != 1, 30, 38, 100, 100, () => new[] { RukshsCenter }, AvoidancePriority.High));
        rukshsAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => RukshsActive() && rukshsEnteredArena && rukshsFloor == 1, 15, 38, 100, 100, () => new[] { RukshsCenter }, AvoidancePriority.High));
        rukshsAvoids.Add(AvoidanceManager.AddAvoidPolygon<Vector3>(condition: () => RukshsActive() && rukshsEnteredArena && rukshsFloor == 2, leashPointProducer: null, leashRadius: 60, rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: _ => new[] { new Vector2(-20, -4.5f), new Vector2(20, -4.5f), new Vector2(20, 4.5f), new Vector2(-20, 4.5f) }, locationProducer: p => p, collectionProducer: () => new[] { RukshsCenter }));
    }

    private void ResetRukshsWaves()
    {
        narrowSupportPosition = null;
        rukshsWaves.Clear();
        Array.Clear(knownDrains, 0, 8);
        Array.Clear(drainStates, 0, 8);
        drainsUntil = DateTime.MinValue;
        drainChoice = -1;
        firstHandBank = rukshsFloor = 0;
        rukshsEnteredArena = false;
    }
}
