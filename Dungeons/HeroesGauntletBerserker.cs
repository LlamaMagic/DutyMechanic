using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;
// Native avoidance owns Berserker hazards. One movement lease handles stacks, knockback staging and crater shelter.
public partial class HeroesGauntlet
{
    private static HeroesGauntlet activeBerserker;
    private readonly List<AvoidInfo> berserkerAvoids = new();
    private readonly HashSet<uint> berserkerOverrides = new();
    private readonly Dictionary<uint, BerserkerCast> berserkerCasts = new();
    private readonly Dictionary<uint, uint> rubbleOwners = new();
    private readonly List<Vector3> craters = new();
    private readonly Dictionary<uint, DateTime> fallingTargets = new();
    private readonly CapabilityManagerHandle berserkerMovement = CapabilityManager.CreateNewHandle();
    private DateTime rampageUntil, anguishUntil, rubbleUntil, furyUntil;
    private uint anguishTarget;
    private bool floorChanged, enteredBerserkerArena, berserkerMoving, berserkerLeased;
    private Vector3? berserkerDestination;
    private string berserkerGoal = "";
    // Native avoidance keys each shape by object equality. Keep this identity stable
    // while refreshing the cast: changing a record's deadline every frame made RB
    // discard/rebuild its escape path continuously during the 05:04 Slice failure.
    private sealed class BerserkerCast
    {
        internal uint Action;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Until;
        internal void Refresh(uint action, Vector3 origin, float heading, DateTime until)
        {
            Action = action;
            Origin = origin;
            Heading = heading;
            Until = until;
        }
    }

    private static bool BerserkerActive() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance && WorldManager.ZoneId == 916 && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat && Core.Me.Location.Distance2D(ArenaCenter.SpectralBerserker) < 40 && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsAlive && a.BaseId == 0x2EFD);
    /// <summary>Releases only Berserker geometry and semantic movement on stop or hook replacement.</summary>
    public static void ResetBerserkerActive()
    {
        activeBerserker?.ResetBerserker();
        activeBerserker = null;
    }

    /// <summary>Releases stale ownership when death, loading or departure prevents the encounter hook from running.</summary>
    public static void CheckBerserkerLifecycle()
    {
        if (!BerserkerActive())
        {
            ResetBerserkerActive();
        }
    }

    // The native enumeration excludes the local player; include it explicitly for
    // self-target stacks, rock assignment and local lock-on markers.
    private BattleCharacter[] BerserkerParty() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Append(Core.Me).GroupBy(a => a.ObjectId).Select(g => g.First()).Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.Location.Distance2D(ArenaCenter.SpectralBerserker) < 40 && (a.IsMe || PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) || PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
    private bool RunBerserker()
    {
        activeBerserker = this;
        if (InsideBerserker(Core.Me.Location, false))
        {
            enteredBerserkerArena = true;
        }

        if (berserkerAvoids.Count == 0)
        {
            RegisterBerserker();
        }

        var now = DateTime.UtcNow;
        // EObj2007457 exists visibly at both locations before the holes open.
        // Arm from the completed20995 helper instead, never from visibility alone.
        foreach (var pair in berserkerCasts.Where(p => now >= p.Value.Until).ToArray())
        {
            if (pair.Value.Action == 20995 && !craters.Any(p => p.Distance2D(pair.Value.Origin) < 1))
            {
                craters.Add(pair.Value.Origin);
                // The impact creates a real depression. Rebuild the local collision
                // heightfield once per new hole; do not reset the service route.
                AvoidanceManager.ResetNavigation();
            }

            berserkerCasts.Remove(pair.Key);
        }

        if (furyUntil != DateTime.MinValue && now >= furyUntil)
        {
            floorChanged = true;
        }

        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.Location.Distance2D(ArenaCenter.SpectralBerserker) < 45).ToArray();
        var party = BerserkerParty();
        foreach (var a in actors.Where(a => a.IsCasting && a.SpellCastInfo.IsValid))
        {
            uint id = a.CastingSpellId;
            // The01:58 Anguish effect arrived1.26s after the native bar; Slice
            // vulnerability likewise followed its bar. Retain1.5s through impact;
            // do not treat cast disappearance as damage resolution.
            var until = now + a.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(id is 20995 or 20999 ? 0.5 : 1.5);
            if (id is 21002 or 21003 or 20995)
            {
                if (!berserkerCasts.TryGetValue(a.ObjectId, out var cast))
                {
                    berserkerCasts[a.ObjectId] = cast = new BerserkerCast();
                }

                cast.Refresh(id, id == 20995 ? a.SpellCastInfo.CastLocation : a.Location, a.Heading, until);
            }

            if (id == 20999)
            {
                rampageUntil = until;
            }

            if (id == 21004 && !floorChanged)
            {
                furyUntil = until;
            }

            if (id == 21000)
            {
                anguishTarget = a.SpellCastInfo.TargetId;
                anguishUntil = until;
            }
        }

        foreach (var a in party.Where(a => a.VfxContainer.IsValid))
        {
            if (a.VfxContainer.LockOns.Any(v => v != null && v.IsValid && v.Id == 229))
            {
                fallingTargets[a.ObjectId] = now.AddSeconds(0.5);
            }
        }

        foreach (var id in fallingTargets.Where(p => p.Value < now).Select(p => p.Key).ToArray())
        {
            fallingTargets.Remove(id);
        }

        var rocks = actors.Where(a => a.BaseId == 0x2EFE && a.IsVisible && a.IsAlive).ToArray();
        // Each rock belongs to the nearest party member at appearance, not whoever
        // happens to approach it later. Preserve assignments through sequential hits.
        foreach (var rock in rocks.Where(r => !rubbleOwners.ContainsKey(r.ObjectId)))
        {
            var owner = party.Where(p => !rubbleOwners.Values.Contains(p.ObjectId)).OrderBy(p => p.Location.Distance2D(rock.Location)).FirstOrDefault();
            if (owner != null)
            {
                rubbleOwners[rock.ObjectId] = owner.ObjectId;
            }
        }

        foreach (var id in rubbleOwners.Keys.Where(id => !rocks.Any(r => r.ObjectId == id)).ToArray())
        {
            rubbleOwners.Remove(id);
        }

        if (rocks.Length > 0 && now < anguishUntil)
        {
            rubbleUntil = now.AddSeconds(8);
        }

        // Resolution priority: native immediate hazards, Rage staging, Rampage
        // shelter, rock spread, individual rubble stacks, then ordinary party stack.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            berserkerMoving = false;
            return false;
        }

        bool Safe(Vector3 p) => InsideBerserker(p, floorChanged) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        if (NeedsCraterExit())
        {
            // The02:00:14-17 Rampage exit repeatedly failed native local path generation
            // from the depression. Use the same semantic owner/service navigator for
            // this short return to the central cross, with no jump/off-mesh input.
            // Suppress only our crater exclusion during this handoff; other hazards win.
            var exit = CraterExitDestination(Core.Me.Location);
            if (Safe(exit))
            {
                return HoldBerserker(exit, "Wild Rampage crater exit", 0.65f);
            }
        }

        var rage = berserkerCasts.Values.FirstOrDefault(c => c.Action == 20995);
        if (rage != null)
        {
            var choices = RageStaging(rage.Origin).Where(Safe).OrderBy(p => p.Distance2D(Core.Me.Location)).ToArray();
            if (choices.Length > 0)
            {
                return HoldBerserker(choices[0], "Wild Rage knockback", 0.65f);
            }
        }

        if (now < rampageUntil && craters.Count > 0)
        {
            // Aim six yalms from the center: inside the7y shelter without dwelling
            // deep in the depression. This leaves a short, prompt exit after impact.
            var choices = craters.SelectMany(c => Enumerable.Range(0, 32).Select(i => c + new Vector3((float)Math.Sin(i * Math.PI / 16) * 6, 0, (float)Math.Cos(i * Math.PI / 16) * 6))).Where(Safe).OrderBy(p => p.Distance2D(Core.Me.Location)).ToArray();
            if (berserkerGoal == "Wild Rampage shelter" && berserkerDestination != null && Safe(berserkerDestination.Value))
            {
                return HoldBerserker(berserkerDestination.Value, berserkerGoal, 0.6f);
            }

            if (choices.Length > 0)
            {
                return HoldBerserker(choices[0], "Wild Rampage shelter", 0.6f);
            }
        }

        if (fallingTargets.Count > 0)
        {
            ReleaseBerserkerPosition();
            return false;
        }

        if (rocks.Length > 0 && (now < anguishUntil || now < rubbleUntil))
        {
            var rock = rocks.FirstOrDefault(r => rubbleOwners.TryGetValue(r.ObjectId, out var owner) && owner == Core.Me.ObjectId);
            if (rock != null)
            {
                var choices = new[]
                {
                    Core.Me.Location,
                    rock.Location
                }.Concat(Enumerable.Range(0, 16).Select(i => rock.Location + new Vector3((float)Math.Sin(i * Math.PI / 8) * 2, 0, (float)Math.Cos(i * Math.PI / 8) * 2))).Where(p => p.Distance2D(rock.Location) <= 4.5f && Safe(p) && party.Where(a => !a.IsMe).All(a => a.Location.Distance2D(p) > 6.5f)).OrderBy(p => p.Distance2D(Core.Me.Location)).ToArray();
                if (choices.Length > 0)
                {
                    return HoldBerserker(choices[0], "Wild Anguish own rubble", 0.75f);
                }
            }
        }
        else if (now < anguishUntil && rocks.Length == 0)
        {
            var target = party.FirstOrDefault(a => a.ObjectId == anguishTarget);
            if (target != null)
            {
                var anchors = target.IsMe ? party.Where(a => !a.IsMe).Select(a => a.Location) : new[]
                {
                    target.Location
                };
                var choices = new[]
                {
                    Core.Me.Location
                }.Concat(anchors).Where(p => Safe(p) && p.Distance2D(target.Location) <= 4.5f && party.Any(a => !a.IsMe && a.Location.Distance2D(p) <= 5)).OrderBy(p => p.Distance2D(Core.Me.Location)).ToArray();
                if (choices.Length > 0)
                {
                    return HoldBerserker(choices[0], "Wild Anguish party stack", 0.75f);
                }
            }
        }

        ReleaseBerserkerPosition();
        return false;
    }

    // Stage 8.5 yalms toward center and three sideways to clear the impact while keeping the sixteen-yalm knockback on the cross.
    internal static Vector3[] RageStaging(Vector3 origin) => new[]
    {
        new Vector3(origin.X < 750 ? 746.5f : 753.5f, 8, 479),
        new Vector3(origin.X < 750 ? 746.5f : 753.5f, 8, 485)
    };
    internal static bool InsideBerserker(Vector3 p, bool cross) => Math.Abs(p.X - 750) <= 19.5f && Math.Abs(p.Z - 482) <= 19.5f && (!cross || Math.Abs(p.X - 750) <= 9.5f || Math.Abs(p.Z - 482) <= 9.5f);
    // The central six-yalm corridor is outside both measured8.5y crater regions.
    internal static Vector3 CraterExitDestination(Vector3 player) => new(750, 8, Math.Clamp(player.Z, 476, 488));
    private bool NeedsCraterExit() => DateTime.UtcNow >= rampageUntil && (craters.Any(c => c.Distance2D(Core.Me.Location) < 8.5f) || (berserkerGoal == "Wild Rampage crater exit" && berserkerDestination != null && Core.Me.Location.Distance2D(berserkerDestination.Value) > 0.65f));
    private void RegisterBerserker()
    {
        // Generic Rampage rectangle covers the required shelter; the original
        // Anguish circle pushes out of the stack. Override only these owned casts.
        foreach (uint id in new uint[]
        {
            20994,
            20995,
            20996,
            20998,
            20999,
            21000,
            21001,
            21002,
            21003
        }

        )
        {
            if (SideStep.Override(id))
            {
                berserkerOverrides.Add(id);
            }
        }

        berserkerAvoids.Add(AvoidanceManager.AddAvoidPolygon<BerserkerCast>(condition: BerserkerActive, leashPointProducer: null, leashRadius: 60, rotationProducer: c => -c.Heading, scaleProducer: _ => 1, heightProducer: _ => 12, pointsProducer: _ => new[] { new Vector2(-3.5f, -0.5f), new Vector2(3.5f, -0.5f), new Vector2(3.5f, 50.5f), new Vector2(-3.5f, 50.5f) }, locationProducer: c => c.Origin, collectionProducer: () => berserkerCasts.Values.Where(c => c.Action is 21002 or 21003)));
        berserkerAvoids.Add(AvoidanceManager.AddAvoidLocation(canRun: BerserkerActive, radiusProducer: (Vector3 _) => 8.5f, locationProducer: (Vector3 p) => p, collectionProducer: () => berserkerCasts.Values.Where(c => c.Action == 20995).Select(c => c.Origin)));
        berserkerAvoids.Add(AvoidanceManager.AddAvoidLocation(canRun: () => BerserkerActive() && DateTime.UtcNow >= rampageUntil && !NeedsCraterExit(), radiusProducer: (Vector3 _) => 8.5f, locationProducer: (Vector3 p) => p, collectionProducer: () => craters));
        berserkerAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => BerserkerActive() && enteredBerserkerArena, 39, 39, 100, 100, () => new[] { ArenaCenter.SpectralBerserker }, AvoidancePriority.High));
        // Post-Fury ten-yalm corner puddles plus0.5y inward margin. The outside
        // square handles the other edges; never clip all four arms with a circle.
        berserkerAvoids.Add(AvoidanceManager.AddAvoidPolygon<Vector3>(condition: () => BerserkerActive() && floorChanged && enteredBerserkerArena, leashPointProducer: null, leashRadius: 60, rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 12, pointsProducer: _ => new[] { new Vector2(-5.5f, -5.5f), new Vector2(5.5f, -5.5f), new Vector2(5.5f, 5.5f), new Vector2(-5.5f, 5.5f) }, locationProducer: p => p, collectionProducer: () => new[] { new Vector3(735, 8, 467), new Vector3(765, 8, 467), new Vector3(735, 8, 497), new Vector3(765, 8, 497) }));
        berserkerAvoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => BerserkerActive() && (fallingTargets.Count > 0 || (rubbleOwners.Count > 0 && (DateTime.UtcNow < anguishUntil || DateTime.UtcNow < rubbleUntil))), objectSelector: a => a.IsValid && !a.IsMe && BerserkerParty().Any(p => p.ObjectId == a.ObjectId), radiusProducer: _ => fallingTargets.Count > 0 ? 8.5f : 6.5f));
    }

    private bool HoldBerserker(Vector3 destination, string goal, float tolerance)
    {
        berserkerGoal = goal;
        berserkerDestination = destination;
        CapabilityManager.Update(berserkerMovement, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), goal);
        berserkerLeased = true;
        if (Core.Me.Location.Distance2D(destination) <= tolerance)
        {
            StopBerserkerMovement();
            return false;
        } // Healing and attacks remain schedulable at safety.

        // RB's default five-yalm path tolerance can finish inside the crater while
        // this owner still requires a sub-yalm arrival. Use the same tolerance for
        // service navigation and completion; native avoidance remains the priority.
        Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = tolerance });
        berserkerMoving = true;
        return true;
    }

    private void StopBerserkerMovement()
    {
        if (berserkerMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.Clear();
            Navigator.PlayerMover.MoveStop();
        }

        berserkerMoving = false;
    }

    private void ReleaseBerserkerPosition()
    {
        StopBerserkerMovement();
        if (berserkerLeased)
        {
            CapabilityManager.Clear(berserkerMovement, CapabilityFlags.Movement, "Berserker semantic position released");
        }

        berserkerLeased = false;
        berserkerDestination = null;
        berserkerGoal = "";
    }

    private void ResetBerserker()
    {
        ReleaseBerserkerPosition();
        foreach (var avoid in berserkerAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        foreach (uint id in berserkerOverrides)
        {
            SideStep.RemoveHandler(id);
        }

        berserkerAvoids.Clear();
        berserkerOverrides.Clear();
        berserkerCasts.Clear();
        craters.Clear();
        rubbleOwners.Clear();
        fallingTargets.Clear();
        rampageUntil = anguishUntil = rubbleUntil = furyUntil = DateTime.MinValue;
        floorChanged = enteredBerserkerArena = false;
    }
}
