using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
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
    // These are the real boss and lyre, excluding same-name choreography helpers.
    private const uint LengthsomeBossBase = 0xF14, LyreBase = 0xF63;
    private const uint BileBombardment = 13550, CorrosiveBile = 13547, FlailingTentacles = 13952;
    private const uint FoolsTightrope = 385, FoolsTumble = 387;

    // Toad Choir spans 150 degrees over 19.5y. Add 0.5y range and five
    // degrees on each side so native avoidance leaves room at the edges.
    private static readonly Vector2[] ToadPoints = ConePoints(20f, 160f);
    private static readonly Vector2[] BilePoints = CirclePoints(8.5f);
    // Include the transformed boss's 6.875y reach. The 130-degree sector
    // conservatively covers the action's 120-degree description plus clearance.
    private static readonly Vector2[] CorrosivePoints = ConePoints(25.5f, 130f);
    // Four 7y-wide tentacle lines form an X relative to the parent facing.
    // Half-width 4 adds 0.5y; length 39.5 covers 32 + 6.875y plus clearance.
    private static readonly Vector2[] TentaclePoints =
    [
        new(4, 39.5f),
        new(4, 4),
        new(39.5f, 4),
        new(39.5f, -4),
        new(4, -4),
        new(4, -39.5f),
        new(-4, -39.5f),
        new(-4, -4),
        new(-39.5f, -4),
        new(-39.5f, 4),
        new(-4, 4),
        new(-4, 39.5f),
    ];

    private readonly Dictionary<(uint, uint), CastHazard> finalHazards = new();
    private readonly CapabilityManagerHandle finalPositionHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle finalFacingHandle = CapabilityManager.CreateNewHandle();
    private AvoidInfo finalRegistration;
    private ChrysalisMovement finalMover;
    private object finalOriginalProvider;
    private bool finalUnsupportedReported;
    private bool finalPositionLeased, finalGapLeased, finalOrdinaryBlocked;
    private DateTime finalSnapshotAt;
    private uint bridgeSeenActor;
    private DateTime bridgeUntil, bridgeEndAfter;
    private Vector3 bridgeAnchor;
    private int bridgeIndex;
    private bool bridgeFloorActive, bridgeArrived;
    private DateTime gazeFinish, gazeUntil, nextGazeCancel;
    private Vector3 gazeOrigin;
    private bool? savedFinalAutoFacing;
    private bool finalGazeHolding, gazeCastObserved;

    private static bool FinalCombat() => BossCombat(LordOfLengthsomeGait, LengthsomeBossBase, SubZoneId.TheThroneRoom);

    private void UpdateFinalBoss()
    {
        if (!FinalCombat())
        {
            ResetFinalBoss();
            return;
        }
        RegisterCastHazards(ref finalRegistration, finalHazards, FinalCombat, LordOfLengthsomeGaitArenaCenter);
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        var boss = actors.First(a => a.BaseId == LengthsomeBossBase && a.NpcId == LordOfLengthsomeGait && a.IsAlive);
        ExpireCastHazards(finalHazards);
        foreach (var caster in actors.Where(a => a.IsCasting))
        {
            switch (caster.CastingSpellId)
            {
                case ToadChoir:
                    ObserveCastHazard(finalHazards, caster, caster.Location, caster.Heading, ToadPoints, 1.6);
                    break;
                case BileBombardment:
                    ObserveCastHazard(finalHazards, caster, caster.SpellCastInfo.CastLocation, 0, BilePoints, 1.6);
                    break;
                case CorrosiveBile:
                    // Six effects include lingering helper ticks after the bar.
                    // Retain the last parent pose for a bounded 5s effect tail.
                    ObserveCastHazard(finalHazards, caster, caster.Location, caster.Heading, CorrosivePoints, 5);
                    break;
                case FlailingTentacles:
                    // The visual parent ends about 1s before the four line impacts.
                    ObserveCastHazard(finalHazards, caster, caster.Location, caster.Heading + (float)Math.PI / 4, TentaclePoints, 1.6);
                    break;
            }
        }
        if (finalMover != null && !finalMover.IsCurrent)
        {
            ReleaseFinalPosition();
            ReleaseFinalGaze();
            finalMover.Detach();
            finalMover = null;
        }
        if (finalMover == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover) &&
            (finalOriginalProvider == null || ReferenceEquals(finalOriginalProvider, Navigator.NavigationProvider)))
        {
            finalOriginalProvider ??= Navigator.NavigationProvider;
            finalMover = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: FinalSegmentSafe);
            Navigator.PlayerMover = finalMover;
            finalUnsupportedReported = false;
        }
        if (finalMover?.IsCurrent != true)
        {
            ReleaseFinalPosition();
            ReleaseFinalGaze();
            if (!finalUnsupportedReported)
                Logger.Information("[Dohn Final] Foreign movement owner; geometry continues, semantic positioning waits for platform handoff.");
            finalUnsupportedReported = true;
            return;
        }
        bool bridgeOwns = UpdateBridge(actors, boss);
        if (!bridgeOwns)
            UpdateFinalGaze(boss);
        else
            ReleaseFinalGaze();
        if (!bridgeOwns && !finalGazeHolding)
            ReleaseFinalPosition();
        bool gapNeeded = bridgeFloorActive || finalGazeHolding || finalHazards.Count > 0;
        if (gapNeeded)
        {
            CapabilityManager.Update(finalPositionHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Dohn final mechanic");
            finalGapLeased = true;
        }
        else if (finalGapLeased)
        {
            CapabilityManager.Clear(finalPositionHandle, CapabilityFlags.GapCloser, "Dohn final floor clear");
            finalGapLeased = false;
        }
        finalMover.Update();
        if (LoggingHelpers.MechanicDiagnosticsEnabled && (finalHazards.Count > 0 || finalGazeHolding || bridgeFloorActive) && DateTime.UtcNow >= finalSnapshotAt)
        {
            finalSnapshotAt = DateTime.UtcNow.AddMilliseconds(100);
            Logger.Information($"[Dohn Final] player={Core.Me.Location} heading={Core.Me.Heading:F3} boss={boss.Location} bossHeading={boss.Heading:F3} cast={boss.CastingSpellId} escaping={AvoidanceManager.IsRunningOutOfAvoid} moving={MovementManager.IsMoving} gaze={finalGazeHolding} bridge={bridgeIndex} inAvoid={AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location))}");
        }
    }

    private bool FinalSegmentSafe(Vector3 waypoint)
    {
        // The far platform is solid only beyond Z=-254 during the chasm. Ordinary
        // combat may approach the lyre there; it cannot shortcut across the pit.
        bool floorSafe = !bridgeFloorActive || bridgeArrived && Core.Me.Z <= -254.2f && waypoint.Z <= -254.2f &&
            Core.Me.Location.Distance2D(bridgeAnchor) <= 7f && waypoint.Distance2D(bridgeAnchor) <= 7f;
        bool safe = floorSafe && OrdinarySegmentSafe(waypoint, LordOfLengthsomeGaitArenaCenter, 19f);
        if (safe == finalOrdinaryBlocked)
            Logger.Information($"[Dohn Final] ordinary waypoint {(safe ? "admitted" : "held")} player={Core.Me.Location} waypoint={waypoint}");
        finalOrdinaryBlocked = !safe;
        return safe;
    }

    private bool UpdateBridge(BattleCharacter[] actors, BattleCharacter boss)
    {
        var lyre = actors.FirstOrDefault(a => a.BaseId == LyreBase && a.IsAlive && a.IsCasting && a.CastingSpellId == Finale);
        var now = DateTime.UtcNow;
        if (lyre != null)
        {
            if (bridgeSeenActor != lyre.ObjectId)
            {
                bridgeSeenActor = lyre.ObjectId;
                bridgeUntil = now + lyre.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1.5);
                bridgeIndex = 0;
                bridgeArrived = false;
                bridgeFloorActive = true;
                Logger.Information($"[Dohn Bridge] lyre=0x{bridgeSeenActor:X} deadline={bridgeUntil:O}");
            }
            bridgeAnchor = lyre.Location;
            bridgeEndAfter = DateTime.MinValue;
        }
        else if (bridgeFloorActive)
        {
            if (bridgeEndAfter == DateTime.MinValue)
                bridgeEndAfter = now.AddSeconds(1.5);
            if (now >= bridgeEndAfter && !boss.HasAura(FoolsTightrope))
            {
                bridgeFloorActive = bridgeArrived = false;
                bridgeSeenActor = 0;
                ReleaseFinalPosition();
                Logger.Information("[Dohn Bridge] arena restored; ordinary combat released.");
                return false;
            }
        }
        if (!bridgeFloorActive)
            return false;
        if (lyre == null || now >= bridgeUntil)
        {
            // Missing cast data does not certify a restored floor. Hold the last
            // safe point until the aura/tail resolves; death/loading releases normally.
            HoldFinalPosition(Core.Me.Location, .1f, "Waiting for bridge floor restoration");
            return true;
        }
        if (Core.Me.HasAura(FoolsTumble) || bridgeIndex > 0 && Core.Me.Z > -234f &&
            Core.Me.Location.Distance2D(tightRopeWalkPoints[0]) > 2f)
        {
            if (bridgeIndex != 0)
                Logger.Information("[Dohn Bridge] returned to entrance; route restarted within original deadline.");
            bridgeIndex = 0;
            bridgeArrived = false;
        }
        if (MovementManager.GenerallyOutOfControl || Core.Me.HasAura(FoolsTumble) || Math.Abs(Core.Me.Y - LordOfLengthsomeGaitArenaCenter.Y) > 3f)
        {
            ReleaseFinalPosition();
            return true;
        }
        while (bridgeIndex < tightRopeWalkPoints.Count && Core.Me.Location.Distance2D(tightRopeWalkPoints[bridgeIndex]) <= .25f)
        {
            ++bridgeIndex;
            Logger.Information($"[Dohn Bridge] waypoint={bridgeIndex}/{tightRopeWalkPoints.Count} player={Core.Me.Location}");
        }
        if (bridgeIndex >= tightRopeWalkPoints.Count)
        {
            if (!bridgeArrived)
                Logger.Information($"[Dohn Bridge] inside lyre barrier distance={Core.Me.Location.Distance2D(bridgeAnchor):F2}; routine owns attacks.");
            bridgeArrived = true;
            ReleaseFinalPosition();
        }
        else
        {
            // Follow the tightrope corners in order. Pulse-driven requests let
            // native escape and forced movement preempt traversal safely.
            HoldFinalPosition(tightRopeWalkPoints[bridgeIndex], .2f, "Walking verified tightrope segment");
        }
        return true;
    }

    private void UpdateFinalGaze(BattleCharacter boss)
    {
        var now = DateTime.UtcNow;
        if (boss.IsCasting && boss.CastingSpellId == ImpChoir)
        {
            if (!gazeCastObserved && now >= gazeUntil)
            {
                gazeFinish = now + boss.SpellCastInfo.RemainingCastTime;
                gazeUntil = gazeFinish.AddSeconds(1.5);
            }
            gazeCastObserved = true;
            gazeOrigin = boss.Location;
        }
        else
            gazeCastObserved = false;
        if (now >= gazeUntil || now < gazeFinish.AddSeconds(-1.5) ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseFinalGaze();
            return;
        }
        if (savedFinalAutoFacing == null)
        {
            savedFinalAutoFacing = GameSettingsManager.FaceTargetOnAction;
            Logger.Information("[Dohn Gaze] facing acquired; client auto-facing suspended.");
        }
        finalGazeHolding = true;
        GameSettingsManager.FaceTargetOnAction = false;
        CapabilityManager.Update(finalFacingHandle, CapabilityFlags.Facing, 250, "Imp Choir look-away");
        HoldFinalPosition(Core.Me.Location, .1f, "Imp Choir stationary facing");
        if (Core.Me.IsCasting && now >= nextGazeCancel)
        {
            ActionManager.StopCasting();
            nextGazeCancel = now.AddMilliseconds(200);
        }
        var away = Core.Me.Location - gazeOrigin;
        if (Math.Abs(away.X) + Math.Abs(away.Z) < .1f)
            away = new Vector3(0, 0, 1);
        Core.Me.SetFacing((float)Math.Atan2(away.X, away.Z));
        // Use the supported facing API. A delayed rotation-bit stop could
        // outlive this lease and cancel the next owner's input.

    }

    private void HoldFinalPosition(Vector3 destination, float tolerance, string reason)
    {
        CapabilityManager.Update(finalPositionHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), reason);
        finalPositionLeased = true;
        if (Core.Me.IsCasting && Core.Me.Location.Distance2D(destination) > tolerance)
            ActionManager.StopCasting();
        finalMover.Request(destination, tolerance);
    }

    private void ReleaseFinalPosition()
    {
        if (finalPositionLeased && finalMover?.IsCurrent == true)
            finalMover.Request(null, 0);
        if (finalPositionLeased)
            CapabilityManager.Clear(finalPositionHandle, CapabilityFlags.Movement, "Final mechanic positioning released");
        finalPositionLeased = false;
    }

    private void ReleaseFinalGaze()
    {
        if (savedFinalAutoFacing != null)
        {
            // A successor platform owns its current setting. Never restore an
            // old lease through a replaced mover/provider, even if the value matches.
            if (finalMover?.IsCurrent == true && !GameSettingsManager.FaceTargetOnAction)
                GameSettingsManager.FaceTargetOnAction = savedFinalAutoFacing.Value;
            savedFinalAutoFacing = null;
            CapabilityManager.Clear(finalFacingHandle, CapabilityFlags.Facing, "Imp Choir facing released");
            Logger.Information($"[Dohn Gaze] facing released; setting restoration owned={finalMover?.IsCurrent == true}.");
        }
        finalGazeHolding = false;
    }

    private void ResetFinalBoss()
    {
        if (finalMover == null && finalRegistration == null && !finalPositionLeased && !finalGapLeased && savedFinalAutoFacing == null)
            return;
        ReleaseFinalGaze();
        ReleaseFinalPosition();
        finalMover?.Detach();
        finalMover = null;
        finalOriginalProvider = null;
        finalUnsupportedReported = false;
        if (finalRegistration != null)
            AvoidanceManager.RemoveAvoid(finalRegistration);
        finalRegistration = null;
        finalHazards.Clear();
        CapabilityManager.Clear(finalPositionHandle, reason: "Final boss lifecycle ended");
        bridgeFloorActive = bridgeArrived = finalGapLeased = finalOrdinaryBlocked = gazeCastObserved = false;
        bridgeSeenActor = 0;
        bridgeIndex = 0;
        gazeFinish = gazeUntil = bridgeUntil = bridgeEndAfter = nextGazeCancel = DateTime.MinValue;
    }
}
