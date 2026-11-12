using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;

public partial class DohnMheg
{
    // The real Griaule and saplings have distinct bases even though helpers may
    // reuse encounter names. Tether 84 is the feeding beam; aura 1153 prevents travel.
    private const uint GriauleBossBase = 0x98E, SaplingBase = 0xEFB, FeedingTether = 84;
    private const uint Fodder = 8897, Fetters = 1153;
    private static readonly Vector2[] SwingePoints = ConePoints(50.5f, 65f);
    private readonly Dictionary<(uint, uint), CastHazard> griauleHazards = new();
    private readonly CapabilityManagerHandle griauleHandle = CapabilityManager.CreateNewHandle();
    private AvoidInfo griauleRegistration;
    private ChrysalisMovement griauleMover;
    private object griauleOriginalProvider;
    private bool griauleUnsupportedReported;
    private DateTime fodderUntil, feedingGoneAt;
    private uint selectedSapling;
    private bool feedingSeen, fodderLeased, griauleGapLeased, griauleOrdinaryBlocked;
    private string feedingSignature;

    private static bool GriauleCombat() => BossCombat(Griaule, GriauleBossBase, SubZoneId.TheAtelier);

    private void UpdateGriaule()
    {
        if (!GriauleCombat())
        {
            ResetGriaule();
            return;
        }
        RegisterCastHazards(ref griauleRegistration, griauleHazards,
            () => GriauleCombat() && !Core.Me.HasAura(Fetters), GriauleArenaCenter);
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        var boss = actors.First(a => a.BaseId == GriauleBossBase && a.NpcId == Griaule && a.IsAlive);
        ExpireCastHazards(griauleHazards);
        if (boss.IsCasting && boss.CastingSpellId == Swinge)
            ObserveCastHazard(griauleHazards, boss, boss.Location, boss.Heading, SwingePoints, 1.6);
        // While fettered, killing the weighted Painted Root is the prerequisite
        // to movement. The cone remains forecast and activates immediately on release.
        if (boss.IsCasting && boss.CastingSpellId == Fodder && DateTime.UtcNow >= fodderUntil)
        {
            fodderUntil = DateTime.UtcNow + boss.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(11);
            feedingSeen = false;
            feedingGoneAt = DateTime.MinValue;
        }
        if (griauleMover != null && !griauleMover.IsCurrent)
        {
            ReleaseFodderPosition();
            griauleMover.Detach();
            griauleMover = null;
        }
        if (griauleMover == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover) &&
            (griauleOriginalProvider == null || ReferenceEquals(griauleOriginalProvider, Navigator.NavigationProvider)))
        {
            // Reacquire only after the original platform is explicitly restored.
            // A replaced navigation provider remains foreign even if its mover is native.
            griauleOriginalProvider ??= Navigator.NavigationProvider;
            griauleMover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: GriauleSegmentSafe);
            Navigator.PlayerMover = griauleMover;
            griauleUnsupportedReported = false;
        }
        if (griauleMover?.IsCurrent != true)
        {
            ReleaseFodderPosition();
            if (!griauleUnsupportedReported)
                Logger.Information("[Dohn Griaule] Foreign movement owner; geometry continues, feeding positioning waits for platform handoff.");
            griauleUnsupportedReported = true;
            return;
        }
        UpdateFeeding(actors, boss);
        bool gapNeeded = fodderLeased || griauleHazards.Count > 0;
        if (gapNeeded)
        {
            CapabilityManager.Update(griauleHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Griaule feeding/cone");
            griauleGapLeased = true;
        }
        else if (griauleGapLeased)
        {
            CapabilityManager.Clear(griauleHandle, CapabilityFlags.GapCloser, "Griaule floor clear");
            griauleGapLeased = false;
        }
        griauleMover.Update();
    }

    private void UpdateFeeding(BattleCharacter[] actors, BattleCharacter boss)
    {
        var now = DateTime.UtcNow;
        var saplings = actors.Where(a => a.BaseId == SaplingBase && a.NpcId == PaintedSapling && a.IsVisible).ToArray();
        bool HasBeam(BattleCharacter a) => a.VfxContainer.IsValid && a.VfxContainer.Tethers.Any(t => t.Id == FeedingTether);
        uint Target(BattleCharacter a) => HasBeam(a)
            ? a.VfxContainer.Tethers.First(t => t.Id == FeedingTether).TargetId : a.CurrentTargetId;
        bool beams = saplings.Any(HasBeam);
        if (beams && now >= fodderUntil)
        {
            // Mid-phase attachment can miss the parent cast. An observed beam
            // admits a bounded recovery window without inventing a new cast delay.
            fodderUntil = now.AddSeconds(10);
            feedingGoneAt = DateTime.MinValue;
        }
        feedingSeen |= beams;
        if (beams)
            feedingGoneAt = DateTime.MinValue;
        else if (feedingSeen && saplings.All(a => a.VfxContainer.IsValid) && feedingGoneAt == DateTime.MinValue)
            feedingGoneAt = now;
        if (now >= fodderUntil || feedingGoneAt != DateTime.MinValue && now >= feedingGoneAt.AddSeconds(.6))
        {
            ReleaseFodderPosition();
            selectedSapling = 0;
            return;
        }
        string signature = string.Join(";", saplings.Select(a => $"{a.ObjectId:X}:{Target(a):X}:{HasBeam(a)}")) +
            $" growing={Core.Me.HasAura(383)} fullGrown={Core.Me.HasAura(391)}";
        if (signature != feedingSignature)
        {
            feedingSignature = signature;
            Logger.Information("[Dohn Feeding] " + signature);
        }
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseFodderPosition();
            return;
        }
        var own = saplings.FirstOrDefault(a => Target(a) == Core.Me.ObjectId);
        var selected = own ?? saplings.FirstOrDefault(a => a.ObjectId == selectedSapling && Target(a) == boss.ObjectId) ??
            saplings.Where(a => Target(a) == boss.ObjectId).OrderBy(a => a.Distance()).FirstOrDefault();
        if (selected == null)
        {
            ReleaseFodderPosition();
            return;
        }
        // A target change to an ally retires the old commitment. Never wait for
        // exactly two unclaimed sources or take a beam already assigned to an ally.
        selectedSapling = selected.ObjectId;
        float distance = boss.Location.Distance2D(selected.Location);
        float fromBoss = Math.Min(Math.Max(8f, boss.CombatReach + 1f), distance - 2f);
        if (fromBoss <= boss.CombatReach + .5f)
        {
            ReleaseFodderPosition();
            return;
        }
        var destination = own != null && Core.Me.Location.Distance2D(boss.Location) > boss.CombatReach + .5f
            ? Core.Me.Location : boss.Location.GetPointBetween(selected.Location, fromBoss);
        if (!GriauleSegmentSafe(destination))
        {
            ReleaseFodderPosition();
            return;
        }
        if (!fodderLeased)
            Logger.Information($"[Dohn Feeding] selected=0x{selectedSapling:X} destination={destination} confirmed={own != null} bossReach={boss.CombatReach:F2}");
        CapabilityManager.Update(griauleHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Intercept feeding beam");
        fodderLeased = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(destination) > .5f)
            ActionManager.StopCasting();
        griauleMover.Request(destination, .5f);
    }

    private bool GriauleSegmentSafe(Vector3 waypoint)
    {
        bool safe = OrdinarySegmentSafe(waypoint, GriauleArenaCenter, 22f);
        if (safe == griauleOrdinaryBlocked)
            Logger.Information($"[Dohn Griaule] ordinary waypoint {(safe ? "admitted" : "held")} player={Core.Me.Location} waypoint={waypoint}");
        griauleOrdinaryBlocked = !safe;
        return safe;
    }

    private void ReleaseFodderPosition()
    {
        if (fodderLeased && griauleMover?.IsCurrent == true)
            griauleMover.Request(null, 0);
        if (fodderLeased)
            CapabilityManager.Clear(griauleHandle, CapabilityFlags.Movement, "Feeding positioning released");
        fodderLeased = false;
    }

    private void ResetGriaule()
    {
        if (griauleMover == null && griauleRegistration == null && !fodderLeased && !griauleGapLeased)
            return;
        ReleaseFodderPosition();
        griauleMover?.Detach();
        griauleMover = null;
        griauleOriginalProvider = null;
        griauleUnsupportedReported = false;
        if (griauleRegistration != null)
            AvoidanceManager.RemoveAvoid(griauleRegistration);
        griauleRegistration = null;
        griauleHazards.Clear();
        CapabilityManager.Clear(griauleHandle, reason: "Griaule lifecycle ended");
        fodderUntil = feedingGoneAt = DateTime.MinValue;
        selectedSapling = 0;
        feedingSeen = griauleGapLeased = griauleOrdinaryBlocked = false;
        feedingSignature = null;
    }
}
