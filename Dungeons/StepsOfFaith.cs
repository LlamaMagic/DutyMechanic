using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Solo quest 70127 geometry. The October 5 capture separates Vishap's target markers,
/// ground helpers and knockback choreography; SoloDuty remains responsible for retreats.
/// </summary>
public class StepsOfFaith : AbstractDungeon, IDisposable
{
    private bool _restoreSideStep;
    private bool _disposed;
    private bool _breathing;
    private bool _scorching;
    private Vector3 _breathOrigin;
    private float _breathHeading;
    private float _breathLength;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheStepsOfFaith;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];

    private static IEnumerable<BattleCharacter> Actors => GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(a => a.IsValid && a.IsVisible);
    private static IEnumerable<BattleCharacter> Casting(uint id) => Actors.Where(a => a.CastingSpellId == id);
    private static bool Active => WorldManager.ZoneId == 1068 && Core.Me != null && Core.Me.IsAlive;

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Generic Touchdown radius80 blocked the mandatory retreat in the captured wipe.
        // This encounter owns its shapes; Dispose also restores SideStep on stop/reload.
        _restoreSideStep = SidestepPlugin != null && SidestepPlugin.Enabled;
        if (_restoreSideStep) SidestepPlugin.Enabled = false;
        _disposed = false;
        _breathing = false;
        _scorching = false;

        // Fireball30875 targets actors; its ground field retains old Flamisphere positions.
        // Never avoid our own moving marker. Ally targets need radius6 plus0.5 clearance.
        AvoidanceManager.AddAvoidLocation(canRun: () => Active,
            radiusProducer: a => 6.5f,
            locationProducer: a => GameObjectManager.GetObjectByObjectId(a.SpellCastInfo.TargetId).Location,
            collectionProducer: () => Casting(30875).Where(a => a.SpellCastInfo.TargetId != Core.Me.ObjectId && GameObjectManager.GetObjectByObjectId(a.SpellCastInfo.TargetId) is { IsValid: true, IsVisible: true }));

        // Body Slam26401 resolves two seconds before Flamisphere30883 (captured in all
        // three wards). One RB avoidance owner enforces both present and projected landing
        // safety: until knockback resolves, forbid the preimage of each ground circle too.
        // This is a directional20-yalm push, not an80-yalm damage circle. RB chooses paths.
        AvoidanceManager.AddAvoidLocation(canRun: () => Active,
            radiusProducer: a => 10.5f, locationProducer: a => a.SpellCastInfo.CastLocation,
            collectionProducer: () => Casting(30883));
        AvoidanceManager.AddAvoidLocation(canRun: () => Active && Casting(26401).Any(),
            radiusProducer: a => 10.5f, locationProducer: LandingPreimage,
            collectionProducer: () => Casting(30883));

        // Earth Shaker's boss circle resolves before the helper cones. Publishing both
        // together asks the same planner for a shared safe region instead of competing moves.
        AvoidanceManager.AddAvoidLocation(canRun: () => Active,
            radiusProducer: a => 31.5f, locationProducer: a => a.Location,
            collectionProducer: () => Actors.Where(a => a.CastingSpellId is 30880 or 26410));
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(canRun: () => Active,
            objectSelector: a => a.IsValid && a.CastingSpellId == 30887,
            leashPointProducer: () => Core.Me.Location, leashRadius: 100f,
            rotationDegrees: 0f, radius: 80.5f, arcDegrees: 32f, priority: AvoidancePriority.Medium);
        // Game data confirms radius50; the frontal120-degree shape gets one degree per
        // side and0.5 radial clearance. Execution remains a later-phase capture requirement.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(canRun: () => Active,
            objectSelector: a => a.IsValid && a.CastingSpellId == 30879,
            leashPointProducer: () => Core.Me.Location, leashRadius: 100f,
            rotationDegrees: 0f, radius: 50.5f, arcDegrees: 122f, priority: AvoidancePriority.Medium);

        // The opening helper is at z340 while combat occurs near z260: the previous
        // sixty-yalm lane never reached the player. Damage continues after cast completion.
        // Retain a20-wide lane (+0.5 each side) until the next distinct boss choreography;
        // Cauterize and untargetability release it before SoloDuty's mandatory retreat.
        AvoidanceManager.AddAvoidPolygon<BattleCharacter>(condition: UpdateBreath,
            leashPointProducer: () => Core.Me.Location, leashRadius: 50f,
            rotationProducer: a => 0f, scaleProducer: a => 1f,
            heightProducer: a => 15f,
            pointsProducer: a => BreathPoints(),
            locationProducer: a => Core.Me.Location,
            collectionProducer: () => Actors.Where(a => a.BaseId == 14943), priority: AvoidancePriority.Medium);

        // Earthrising30888 is radius8, not Flamisphere or a backwards60-yalm rectangle.
        // Cover observed cast origins without inventing repeat-event timing; repeat26412
        // requires additional action-effect capture before forecasting its moving sequence.
        AvoidanceManager.AddAvoidLocation(canRun: () => Active,
            radiusProducer: a => 8.5f, locationProducer: a => a.Location,
            collectionProducer: () => Casting(30888));
        return Task.FromResult(false);
    }

    private static Vector3 LandingPreimage(BattleCharacter sphere)
    {
        var p = sphere.SpellCastInfo.CastLocation;
        var slam = Casting(26401).FirstOrDefault();
        if (slam == null) return p;
        return new Vector3(p.X - 20f * (float)Math.Sin(slam.Heading), p.Y, p.Z - 20f * (float)Math.Cos(slam.Heading));
    }

    private Vector2[] BreathPoints()
    {
        // RB rejects a polygon whose origin lies outside its local heightfield before
        // checking intersection. The live helper at z340 is85 yalms away and produced
        // zero active avoids. Rebase the same world polygon around the player so the
        // origin is in the heightfield; this does not move or resize the harmful lane.
        var player = Core.Me.Location;
        float sin = (float)Math.Sin(_breathHeading), cos = (float)Math.Cos(_breathHeading);
        Vector2 Point(float x, float z) => new(_breathOrigin.X + x * cos + z * sin - player.X,
            _breathOrigin.Z - x * sin + z * cos - player.Z);
        var back = _scorching ? -100.5f : -0.5f;
        return new[] { Point(10.5f, _breathLength), Point(-10.5f, _breathLength), Point(-10.5f, back), Point(10.5f, back) };
    }

    private bool UpdateBreath()
    {
        if (!Active) { _breathing = false; _scorching = false; return false; }
        var boss = Actors.FirstOrDefault(a => a.BaseId == 14943);
        // October 6: 29785 and helper30187 began together; the central lane then dealt
        // repeated damage AFTER their19.7s cast, killing the player at x=-6.91. The final
        // phase retains a20-wide lane,100 each way (+0.5 clearance), through boss death.
        // Reuse the rebased polygon so distant origins and competing movement owners
        // cannot hide this hazard. Wipe/exit/disposal resets the phase latch.
        if (boss != null && (boss.CastingSpellId == 29785 || Casting(30187).Any()))
        {
            _scorching = true;
            _breathOrigin = boss.Location;
            _breathHeading = boss.Heading;
        }
        if (_scorching)
        {
            _breathLength = 100.5f;
            return _breathing = boss != null && boss.IsAlive;
        }
        var helper = Actors.FirstOrDefault(a => a.CastingSpellId is 30185 or 30186);
        if (helper != null)
        {
            _breathing = true;
            _breathOrigin = helper.Location;
            _breathHeading = helper.Heading;
            _breathLength = helper.CastingSpellId == 30185 ? 500.5f : 60.5f;
        }
        else if (boss == null || (_breathLength < 100 && !boss.IsTargetable) || boss.CastingSpellId == 30878 ||
                 (boss.CastingSpellId != 0 && boss.CastingSpellId is not (30877 or 26411 or 30884)))
            _breathing = false;
        return _breathing;
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync() => Task.FromResult(false);

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync() { Dispose(); return Task.FromResult(false); }

    /// <summary>Releases the encounter's SideStep override when the host stops or reloads.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _breathing = false;
        _scorching = false;
        if (_restoreSideStep && SidestepPlugin != null) SidestepPlugin.Enabled = true;
    }
}
