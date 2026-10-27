using Clio.Utilities;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Objects;
using LlamaLibrary.Memory.PatternFinders;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DutyMechanic.Dungeons;

public partial class KtisisHyperboreia
{
    // Native director records use placed-instance IDs, not network event indices.
    // bg.lgb places the two three-pattern footprint assets at Lyssa's room center.
    // Each active low-word state selects one of the six 12.5y emergence craters.
    private readonly Dictionary<uint, ushort> footprintStates = new();
    private readonly Dictionary<uint, Volley> volleys = new();
    private sealed record Volley(uint Caster, Vector3 Source, float Heading, DateTime Finish);
    private Vector3? intactMeteor;
    private uint intactMeteorId;
    private string meteorEvidence;
    private bool animationReaderAttempted;
    private int meteorAnimationOffset;
    private (int Pid, long Start, long Image) animationProcess;
    private void UpdateEncounterSignals(BattleCharacter[] actors, BattleCharacter[] casts, DateTime now)
    {
        if (savedSideStepEnabled == null)
        {
            return;
        }

        if (WorldManager.SubZoneId == 3766)
        {
            UpdateFootprints(casts, now);
        }

        if (WorldManager.SubZoneId != 3768)
        {
            return;
        }

        if (now >= hermeticaUntil)
        {
            volleys.Clear();
            intactMeteor = null;
            intactMeteorId = 0;
        }

        foreach (var caster in casts.Where(a => a.CastingSpellId == ActionId.MeteorVolley))
        {
            // Snapshot once: a completed helper can move to its next gate. Refreshing
            // that old wave from its new location would rotate the shelter early.
            if (!volleys.ContainsKey(caster.ObjectId))
            {
                volleys[caster.ObjectId] = new(caster.ObjectId, caster.Location, caster.Heading, now + caster.SpellCastInfo.RemainingCastTime);
            }
        }

        if (now >= hermeticaUntil)
        {
            return;
        }

        var meteors = actors.Where(a => a.BaseId == 0x348C && a.IsVisible)
            .Select(a => new { a.ObjectId, a.Location, State = MeteorAnimation(a) }).ToArray();
        string evidence = string.Join(";", meteors.Select(m => $"{m.ObjectId:X}:{m.State}"));
        if (evidence != meteorEvidence)
        {
            meteorEvidence = evidence;
            Logger.Information("[Ktisis] Meteor animation states [{0}]", evidence);
        }

        // All four initially spawn alike. Select the unique state 0 rock once
        // the other three report cracked state 1. Unknown is never intact.
        var intact = meteors.Where(m => m.State == 0).ToArray();
        if (meteors.Length == 4 && intact.Length == 1 && meteors.Count(m => m.State == 1) == 3)
        {
            if (intactMeteorId != intact[0].ObjectId)
            {
                Logger.Information("[Ktisis] Intact meteor 0x{0:X} at {1}.", intact[0].ObjectId, intact[0].Location);
            }

            intactMeteorId = intact[0].ObjectId;
            intactMeteor = intact[0].Location;
        }
        // Retain the identified scalar position through the disappearance/effect
        // frames. A late attachment may use the sole surviving, readable state 0 rock.
        else if (meteors.Length == 1 && intact.Length == 1)
        {
            intactMeteorId = intact[0].ObjectId;
            intactMeteor = intact[0].Location;
        }
    }

    private void UpdateFootprints(BattleCharacter[] casts, DateTime now)
    {
        if (DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
        {
            foreach (var effect in director.MapEffects.Where(e => e.ID is 8752181 or 8752182))
            {
                footprintStates.TryGetValue(effect.ID, out ushort old);
                footprintStates[effect.ID] = effect.State;
                if (old == effect.State)
                {
                    continue;
                }

                Logger.Information("[Ktisis] Footprint map id={0} state=0x{1:X} flags={2} raw=0x{3:X}.",
                    effect.ID, effect.State, effect.Flags, effect.unk);
                int slot = effect.State == 1 ? 0 : effect.State == 0x10 ? 1 : effect.State == 0x80 ? 2 : -1;
                if (slot < 0 || now >= frostUntil)
                {
                    continue;
                }

                // The second asset's state order is 300/180/240 degrees.
                int direction = effect.ID == 8752181 ? slot : slot == 0 ? 5 : slot == 1 ? 3 : 4;
                float heading = direction * (float)Math.PI / 3;
                var origin = new Vector3(LyssaCenter.X - 12.5f * (float)Math.Sin(heading), LyssaCenter.Y,
                    LyssaCenter.Z - 12.5f * (float)Math.Cos(heading));
                hazards[(0, ActionId.PunishingSlice)] = new(0, ActionId.PunishingSlice, origin, heading,
                    Rectangle(51, 50.5f), frostUntil, frostUntil);
                Logger.Information("[Ktisis] Frostbite forecast origin={0} heading={1:F3}; native avoidance owns movement.", origin, heading);
            }
        }

        var slice = casts.FirstOrDefault(a => a.CastingSpellId == ActionId.PunishingSlice);
        if (slice != null)
        {
            // The actual helper always supersedes the forecast. Log disagreement
            // so a regional asset change cannot be mistaken for verified mapping.
            if (hazards.Remove((0, ActionId.PunishingSlice), out var forecast))
            {
                Logger.Information("[Ktisis] Frostbite forecast check: originError={0:F2} headingError={1:F3}.",
                    forecast.Origin.Distance2D(slice.Location),
                    Math.Abs(Math.Atan2(Math.Sin(forecast.Heading - slice.Heading), Math.Cos(forecast.Heading - slice.Heading))));
            }
        }

        if (now >= frostUntil)
        {
            hazards.Remove((0, ActionId.PunishingSlice));
        }
    }

    private bool ShelterFromVolley(DateTime now)
    {
        if (intactMeteor == null)
        {
            ReleaseMovement();
            return false;
        }

        var rock = intactMeteor.Value;
        var ordered = volleys.Values.OrderBy(v => v.Finish).ToArray();
        // Four parallel gates form each wave; the next four begin three seconds
        // later. Keep the first shelter through its half-second effect tail, then
        // cross around the same rock. Preserve the last shelter until choreography
        // ends rather than releasing combat movement at a disappearing cast bar.
        var wave = ordered.FirstOrDefault(v => now <= v.Finish.AddSeconds(.5)) ?? ordered.LastOrDefault();
        if (wave == null)
        {
            // Stage on the room-facing side only after proximity damage resolves.
            // This uses the identified blocker, independent of party composition.
            float dx = HermesCenter.X - rock.X, dz = HermesCenter.Z - rock.Z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (length < 1)
            {
                ReleaseMovement();
                return false;
            }

            return PositionAt(new(rock.X + dx / length * 3.8f, HermesCenter.Y, rock.Z + dz / length * 3.8f), "Intact meteor staging");
        }

        float x = (float)Math.Sin(wave.Heading), z = (float)Math.Cos(wave.Heading);
        // 2.4y blocker radius, 3.8y downwind offset: stay clear of collision while
        // retaining 1.4y lateral margin after the one-yalm arrival tolerance.
        var destination = new Vector3(rock.X + 3.8f * x, HermesCenter.Y, rock.Z + 3.8f * z);
        if (destination.Distance2D(HermesCenter) > 18 || (rock.X - wave.Source.X) * x + (rock.Z - wave.Source.Z) * z <= 0)
        {
            ReleaseMovement();
            return false;
        }

        return PositionAt(destination, $"Meteor shelter heading {wave.Heading:F2}");
    }

    private int MeteorAnimation(BattleCharacter meteor)
    {
        try
        {
            var process = Core.Memory.Process;
            var identity = (process.Id, process.StartTime.ToUniversalTime().Ticks, Core.Memory.ImageBase.ToInt64());
            if (animationProcess != identity)
            {
                animationProcess = identity;
                animationReaderAttempted = false;
                meteorAnimationOffset = 0;
            }

            if (!animationReaderAttempted)
            {
                animationReaderAttempted = true;
                // Semantic character-state copy body, reused from the boulder reader.
                // Ghidra operand masks wildcard every displacement. One .text match
                // on Global 46A52AB1, CN 7BA28760, TC 9FB8DD46 and prior Global
                // 9483706D; full validation details are in KtisisHyperboreiaAudit.md.
                // Read32 extracts adjacent source fields and matching destinations;
                // Add operands are hexadecimal. No regional field offset is assumed.
                const string pattern = "Search 0F B6 86 ? ? ? ? 48 8B 4D ? 88 81 ? ? ? ? 0F B6 86 ? ? ? ? 88 81 ? ? ? ?";
                using var finder = new GreyMagicPf();
                if (finder.SearchMany(pattern).Length != 1)
                {
                    throw new InvalidOperationException("nonunique animation anchor");
                }

                int first = finder.FindSingle(pattern + " Add 3 Read32").ToInt32();
                int second = finder.FindSingle(pattern + " Add 14 Read32").ToInt32();
                if (first < 0x900 || first > 0x1200 || second != first + 1 ||
                    finder.FindSingle(pattern + " Add D Read32").ToInt32() != first ||
                    finder.FindSingle(pattern + " Add 1A Read32").ToInt32() != second)
                {
                    throw new InvalidOperationException("animation copy fields disagree");
                }

                meteorAnimationOffset = second;
            }

            return meteorAnimationOffset != 0 && meteor.IsValid && meteor.Pointer != IntPtr.Zero
                ? Core.Memory.Read<byte>(meteor.Pointer + meteorAnimationOffset) : -1;
        }
        catch (Exception ex)
        {
            Logger.Information("[Ktisis] Meteor animation unavailable: {0}", ex.Message);
            return -1; // An unreadable meteor cannot be selected as shelter.
        }
    }

    private void ResetEncounterSignals()
    {
        footprintStates.Clear();
        volleys.Clear();
        intactMeteor = null;
        intactMeteorId = 0;
        meteorEvidence = null;
    }
}
