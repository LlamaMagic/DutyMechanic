using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;

namespace DutyMechanic.Dungeons;
// Native avoidance owns Kyklops geometry. Open Hearth uses the actual stack target; map states order the delayed tile impacts.
public partial class AnamnesisAnyder
{
    private static AnamnesisAnyder activeKyklops;
    private static readonly Vector3 KyklopsCenter = new(20, 110, -80);
    private readonly List<AvoidInfo> kyklopsAvoids = new();
    private readonly HashSet<uint> kyklopsOverrides = new();
    private readonly Dictionary<uint, CastRegion> kyklopsRegions = new();
    private readonly HashSet<uint> pyreTargets = new();
    private DateTime pyreUntil, hearthUntil;
    private uint hearthTarget;
    private bool hearthMoving, hearthLeased;
    private Vector3? hearthDestination;
    private bool kyklopsEnteredArena;
    private KyklopsTileStage hammerTiles = new(), bladeTiles = new();
    // Layout IDs survive wipes. Retain the observed activation order through each delayed impact.
    private const uint HammerLayout = 0x7DEF99, BladeLayout = 0x7ECD56;
    // Use 1.5 yalms beyond the nominal tile edge to cover damage snapshots and arrival tolerance.
    internal const float TileHalfWidth = 6.5f;
    internal sealed class KyklopsTileStage
    {
        internal int Pattern;
        internal bool Active;
        internal DateTime Until;
        internal DateTime Activated;
        internal void Observe(ushort state, DateTime now)
        {
            int pattern = TilePattern(state);
            if (pattern != 0)
            {
                if (!Active || Pattern != pattern)
                {
                    Activated = now;
                }

                Pattern = pattern;
                Active = true;
                Until = DateTime.MaxValue;
            }
            // Tile damage follows the map clear by about 0.8 seconds; retain the stage for one second.
            else if (state == 4 && Active)
            {
                Active = false;
                Until = now.AddSeconds(1);
            }
        }

        internal bool Pending(DateTime now) => Pattern != 0 && now < Until;
    }

    internal static int TilePattern(ushort state) => state is 0x0400 or 0x0100 ? 1 : state is 0x1000 or 0x0040 ? 2 : 0;
    internal static Vector3[] TileCenters(int pattern) => pattern == 1 ? new[]
    {
        new Vector3(10, 110, -70),
        new Vector3(30, 110, -70),
        new Vector3(20, 110, -80),
        new Vector3(10, 110, -90),
        new Vector3(30, 110, -90)
    }

    : pattern == 2 ? new[]
    {
        new Vector3(20, 110, -70),
        new Vector3(30, 110, -80),
        new Vector3(20, 110, -90),
        new Vector3(10, 110, -80)
    }

    : Array.Empty<Vector3>();
    private IEnumerable<Vector3> PendingTiles()
    {
        var now = DateTime.UtcNow;
        return TileCenters(PendingTilePattern(hammerTiles, bladeTiles, now));
    }

    internal static int PendingTilePattern(KyklopsTileStage first, KyklopsTileStage second, DateTime now) => first.Pending(now) && (!second.Pending(now) || first.Activated <= second.Activated) ? first.Pattern : second.Pending(now) ? second.Pattern : 0;
    private sealed record CastRegion(uint Action, Vector3 Origin, float Heading, DateTime Until);
    private static bool KyklopsActive() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading && DutyManager.InInstance && WorldManager.ZoneId == 898 && Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat && Core.Me.Location.Distance2D(KyklopsCenter) < 35 && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsAlive && a.IsVisible && a.BaseId == 0x2CFE);
    /// <summary>Releases only Kyklops-owned geometry and stack movement on stop or hook replacement.</summary>
    public static void ResetKyklopsActive()
    {
        activeKyklops?.ResetKyklops();
        activeKyklops = null;
    }

    /// <summary>Clears stale ownership when loading or death prevents the encounter hook from running.</summary>
    public static void CheckKyklopsLifecycle()
    {
        if (!KyklopsActive())
        {
            ResetKyklopsActive();
        }
    }

    private IEnumerable<CastRegion> Regions(uint action) => kyklopsRegions.Values.Where(r => r.Action == action && DateTime.UtcNow < r.Until);
    private BattleCharacter[] KyklopsAllies() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive && a.IsVisible && !a.IsMe && a.Location.Distance2D(KyklopsCenter) < 40 && (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) || PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).ToArray();
    private bool RunKyklops()
    {
        activeKyklops = this;
        // After a wipe the route approaches from outside the floor. Do not let the
        // floor's exclusion region compete with that entry before it has crossed in.
        if (InsideKyklops(Core.Me.Location))
        {
            kyklopsEnteredArena = true;
        }

        if (kyklopsAvoids.Count == 0)
        {
            RegisterKyklops();
        }

        var now = DateTime.UtcNow;
        foreach (var key in kyklopsRegions.Where(p => now >= p.Value.Until).Select(p => p.Key).ToArray())
        {
            kyklopsRegions.Remove(key);
        }

        if (now >= pyreUntil)
        {
            pyreTargets.Clear();
        }

        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsCasting && a.SpellCastInfo.IsValid && a.Location.Distance2D(KyklopsCenter) < 40))
        {
            uint action = actor.CastingSpellId;
            // Retain cast regions for half a second after the bar to cover impact delay.
            var until = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.5);
            if (actor.BaseId == 0x2CFE && action is 19284 or 19285 or 19286 or 19287)
            {
                kyklopsRegions[actor.ObjectId] = new(action, actor.Location, actor.Heading, until);
            }

            if (actor.NpcId != Kyklops)
            {
                continue;
            }

            if (action == 19295)
            {
                // Helper actors can report no health while casting. Their valid
                // cast/target identity, not IsAlive, establishes an active effect.
                pyreTargets.Add(actor.SpellCastInfo.TargetId);
                // Pyre damage can follow the cast bar by 1.32 seconds.
                pyreUntil = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
            }

            if (action == 19296)
            {
                if (hearthTarget != actor.SpellCastInfo.TargetId)
                {
                    hearthDestination = null;
                }

                hearthTarget = actor.SpellCastInfo.TargetId;
                hearthUntil = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
            }
        }

        UpdateKyklopsTiles();
        if (now >= hearthUntil)
        {
            ReleaseHearth();
            return false;
        }

        // Native regions (including spread when it resolves first) always win.
        // No global avoid deletion or nearest-NPC follow can erase an overlap.
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            hearthMoving = false;
            return false;
        }

        var allies = KyklopsAllies();
        var target = allies.FirstOrDefault(a => a.ObjectId == hearthTarget);
        if (allies.Length == 0 || (hearthTarget != Core.Me.ObjectId && target == null))
        {
            ReleaseHearth();
            return false;
        }

        bool Safe(Vector3 p) => InsideKyklops(p) && !AvoidanceManager.Avoids.Any(a => a.IsPointInAvoid(p));
        hearthDestination = SelectHearth(Core.Me.Location, allies.Select(a => a.Location).ToArray(), target?.Location, hearthDestination, Safe);
        if (hearthDestination == null)
        {
            ReleaseHearth();
            return false;
        }

        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Kyklops Open Hearth stack");
        hearthLeased = true;
        if (Core.Me.Location.Distance2D(hearthDestination.Value) <= 1)
        {
            StopHearthMovement();
            return false;
        } // Keep healing and rotation schedulable at the stack.

        Navigator.MoveTo(hearthDestination.Value);
        hearthMoving = true;
        return true;
    }

    private void RegisterKyklops()
    {
        // Keep SideStep enabled for unrelated attacks, but one owner publishes
        // these shapes. The old follow/spread implementation disabled it globally.
        SidestepPlugin.Enabled = true;
        foreach (uint id in new uint[]
        {
            19284,
            19285,
            19286,
            19287,
            19291,
            19292,
            19295,
            19296
        }

        )
        {
            if (SideStep.Override(id))
            {
                kyklopsOverrides.Add(id);
            }
        }

        kyklopsAvoids.Add(AvoidanceManager.AddAvoidLocation(canRun: KyklopsActive, radiusProducer: (Vector3 _) => 12.5f, locationProducer: (Vector3 p) => p, collectionProducer: () => Regions(19285).Select(r => r.Origin)));
        // Installed actions: Swing12y, Cyclone8-25y, Glower45x6, Swipe12y/120deg.
        // Each boundary includes0.5y; cone angle gets an additional2degree margin.
        kyklopsAvoids.Add(AvoidanceHelpers.AddAvoidDonut(KyklopsActive, () => Regions(19287).Select(r => r.Origin).ToArray(), 25.5, 7.5));
        kyklopsAvoids.Add(AvoidanceManager.AddAvoidPolygon<CastRegion>(condition: KyklopsActive, leashPointProducer: null, leashRadius: 50, rotationProducer: r => -r.Heading, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: _ => new[] { new Vector2(-3.5f, -0.5f), new Vector2(3.5f, -0.5f), new Vector2(3.5f, 45.5f), new Vector2(-3.5f, 45.5f) }, locationProducer: r => r.Origin, collectionProducer: () => Regions(19286)));
        kyklopsAvoids.Add(AvoidanceManager.AddAvoidPolygon<CastRegion>(condition: KyklopsActive, leashPointProducer: null, leashRadius: 50, rotationProducer: r => -r.Heading, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: _ => KyklopsCone(), locationProducer: r => r.Origin, collectionProducer: () => Regions(19284)));
        kyklopsAvoids.Add(AvoidanceManager.AddAvoidObject<BattleCharacter>(canRun: () => KyklopsActive() && DateTime.UtcNow < pyreUntil, objectSelector: a => a.IsValid && a.IsAlive && !a.IsMe && KyklopsAllies().Any(p => p.ObjectId == a.ObjectId) && (pyreTargets.Contains(Core.Me.ObjectId) || pyreTargets.Contains(a.ObjectId)), radiusProducer: _ => 5.5f));
        // Only the pending tile stage constrains movement; resolved tiles become refuge.
        kyklopsAvoids.Add(AvoidanceManager.AddAvoidPolygon<Vector3>(condition: KyklopsActive, leashPointProducer: null, leashRadius: 50, rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: _ => new[] { new Vector2(-TileHalfWidth, -TileHalfWidth), new Vector2(TileHalfWidth, -TileHalfWidth), new Vector2(TileHalfWidth, TileHalfWidth), new Vector2(-TileHalfWidth, TileHalfWidth) }, locationProducer: p => p, collectionProducer: PendingTiles));
        kyklopsAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(() => KyklopsActive() && kyklopsEnteredArena, 29, 29, 100, 100, () => new[] { KyklopsCenter }, AvoidancePriority.High)); //30y floor,0.5y inset.
    }

    internal static bool InsideKyklops(Vector3 p) => Math.Abs(p.X - 20) <= 14.5f && Math.Abs(p.Z + 80) <= 14.5f;
    internal static Vector2[] KyklopsCone() => new[]
    {
        new Vector2(0, 0)
    }.Concat(Enumerable.Range(0, 25).Select(i =>
    {
        double a = (-62 + i * 124.0 / 24) * Math.PI / 180;
        return new Vector2((float)Math.Sin(a) * 12.5f, (float)Math.Cos(a) * 12.5f);
    })).ToArray();
    // Only candidates satisfying the live target and every active avoid can hold a
    // stack. Prefer the previous destination to prevent target jitter restarting movement.
    internal static Vector3? SelectHearth(Vector3 player, Vector3[] allies, Vector3? target, Vector3? previous, Func<Vector3, bool> safe)
    {
        bool Valid(Vector3 p) => safe(p) && (target == null || p.Distance2D(target.Value) <= 4.5f) && allies.Any(a => a.Distance2D(p) <= 5);
        if (previous != null && Valid(previous.Value))
        {
            return previous;
        }

        var candidates = new List<Vector3>
        {
            player
        };
        foreach (var anchor in target == null ? allies : new[]
        {
            target.Value
        }

        )
        {
            candidates.Add(anchor);
            for (int i = 0; i < 16; i++)
            {
                candidates.Add(anchor + new Vector3((float)Math.Sin(i * Math.PI / 8) * 2, 0, (float)Math.Cos(i * Math.PI / 8) * 2));
            }
        }

        return candidates.Where(Valid).OrderByDescending(p => allies.Count(a => a.Distance2D(p) <= 5)).ThenBy(p => p.Distance2D(player)).Select(p => (Vector3? )p).FirstOrDefault();
    }

    private void UpdateKyklopsTiles()
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector d || !d.IsValid)
        {
            return;
        }

        var effects = d.MapEffects;
        var now = DateTime.UtcNow;
        foreach (var effect in effects)
        {
            if (effect.ID == HammerLayout)
            {
                hammerTiles.Observe(effect.State, now);
            }

            if (effect.ID == BladeLayout)
            {
                bladeTiles.Observe(effect.State, now);
            }
        }
    }

    private void StopHearthMovement()
    {
        if (hearthMoving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.Clear();
            Navigator.PlayerMover.MoveStop();
        }

        hearthMoving = false;
    }

    private void ReleaseHearth()
    {
        StopHearthMovement();
        if (hearthLeased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Kyklops stack released");
        }

        hearthLeased = false;
        hearthTarget = 0;
        hearthDestination = null;
        hearthUntil = DateTime.MinValue;
    }

    private void ResetKyklops()
    {
        ReleaseHearth();
        foreach (var avoid in kyklopsAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        foreach (var id in kyklopsOverrides)
        {
            SideStep.RemoveHandler(id);
        }

        kyklopsAvoids.Clear();
        kyklopsOverrides.Clear();
        kyklopsRegions.Clear();
        pyreTargets.Clear();
        pyreUntil = DateTime.MinValue;
        kyklopsEnteredArena = false;
        hammerTiles = new();
        bladeTiles = new();
    }
}
