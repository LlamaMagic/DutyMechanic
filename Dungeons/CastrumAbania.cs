using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Castrum Abania's current elemental trial, keeping damaging geometry in RB avoidance
/// and using one exclusive positive-position owner for tower soaks and targeted fire stacks.
/// </summary>
public class CastrumAbania : AbstractDungeon
{
    // XXIV helpers share the boss name row and expose zero CastLocation for actor-relative attacks. Select by action.
    private const uint FireStack = 33462, Tower = 33464, IceSpread = 33461;
    private const uint FireCone = 33463, IceGrid = 33460, LineBait = 33749;
    private const uint Cutter = 7975, Comet = 7979, CometTwo = 8328;
    private static readonly Vector3 MagnaCenter = new(-213, -2, 187);
    private static readonly Vector3 SubjectCenter = new(10.5f, 14, 186.5f);
    private static readonly Vector3 InfernoCenter = new(282.5f, 20, -27.5f);
    private readonly HashSet<uint> ownedOverrides = [];
    private uint positiveOwner, positiveAction;
    private Vector3 positivePoint;
    private DateTime positiveExpires;
    private bool moving, leased;
    private string unavailableWave = "";
    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.CastrumAbania;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        // Selective overrides preserve SideStep for trash and the verified Magna/Inferno ordinary
        // telegraphs. Do not let a tower/stack become an avoid, or register the same cone twice.
        foreach (uint action in new uint[]
        {
            FireStack,
            Tower,
            IceSpread,
            FireCone,
            IceGrid,
            Cutter,
            Comet,
            CometTwo,
            33448,
            33449,
            33450,
            33451,
            33452,
            33453
        }

        )
        {
            if (SideStep.Override(action))
            {
                ownedOverrides.Add(action);
            }
        }

        AddBoundary(SubZoneId.TerrestrialWeaponry, MagnaCenter, 19f);
        AddBoundary(SubZoneId.ProjectAegis, SubjectCenter, 18.5f);
        AddBoundary(SubZoneId.AssessmentGrounds, InfernoCenter, 18.5f);
        // A damaging helper owns each grid/cone independently, including overlapping serial waves.
        // Lines expand all edges by 0.5y. Cones add 0.5y radially and 3 degrees per edge so their
        // narrow tips do not under-cover the authored 60/20-degree sectors at ordinary range.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(SubjectActive, actor => Casting(actor, IceGrid), width: 5, length: 41, yOffset: -0.5f);
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(SubjectActive, ActiveFireCone, () => SubjectCenter, 40, 0, 60.5f, 66);
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(() => Active(SubZoneId.AssessmentGrounds), actor => Casting(actor, Cutter), () => InfernoCenter, 40, 0, 20.5f, 26);
        // The four Blizzard helpers follow their targets. Only other targets are unsafe; drawing
        // one's own moving circle makes escape impossible. End on the cast, not a renewed timer.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(SubjectActive, actor => Casting(actor, IceSpread) && actor.SpellCastInfo.TargetId != Core.Player.ObjectId && GameObjectManager.GetObjectByObjectId(actor.SpellCastInfo.TargetId) != null, radiusProducer: _ => 5.5f, locationProducer: actor => GameObjectManager.GetObjectByObjectId(actor.SpellCastInfo.TargetId)?.Location ?? actor.Location, priority: AvoidancePriority.High));
        // Sparking Current is an instant twenty-yalm radial line, announced by Discrete Magick.
        // Serial tower waves can overlap these baits: the same RB solver owns all other-party
        // lines while the positive planner selects an uncovered tower outside those lines.
        AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(LineBaitsActive, IsOtherPartyMember, width: 7, length: 21, yOffset: -0.5f, rotationProducer: ally => -(float)Math.Atan2(ally.X - SubjectCenter.X, ally.Z - SubjectCenter.Z), locationProducer: _ => SubjectCenter);
        // Rahu Comet is proximity damage, not a stack. Twenty yalms is the conservative distance
        // used for the normal encounter; the half-yalm margin keeps the solver away from its edge.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(() => Active(SubZoneId.AssessmentGrounds), actor => Casting(actor, Comet) || Casting(actor, CometTwo), radiusProducer: _ => 20.5f, locationProducer: actor => actor.SpellCastInfo.CastLocation, priority: AvoidancePriority.High));
        // VFX 74 marks the instant ten-yalm Rahu Ray spread. Avoid marked allies, or separate from all allies when marked.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(() => Active(SubZoneId.AssessmentGrounds), actor => IsOtherPartyMember(actor) && (HasRayMarker(actor) || HasRayMarker(Core.Player)), radiusProducer: _ => 10.5f, locationProducer: actor => actor.Location, priority: AvoidancePriority.High));
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleasePositive("Castrum Abania lifecycle ended");
        foreach (uint action in ownedOverrides)
        {
            SideStep.RemoveHandler(action);
        }

        ownedOverrides.Clear();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        if (!SubjectActive() || Core.Player.IsDead || ff14bot.Behavior.CommonBehaviors.IsLoading)
        {
            ReleasePositive("Elemental encounter inactive or recovering");
            return Task.FromResult(false);
        }

        BattleCharacter[] casters = Actors().Where(actor => actor.IsCasting).ToArray();
        BattleCharacter current = casters.FirstOrDefault(actor => actor.ObjectId == positiveOwner && actor.CastingSpellId == positiveAction);
        // A small effect grace covers the gap between cast disappearance and tower/stack damage.
        // Keep the first wave's destination through that gap before selecting the second wave.
        if (current == null && positiveOwner != 0 && DateTime.UtcNow < positiveExpires)
        {
            return Task.FromResult(ApplyPositive(positivePoint));
        }

        BattleCharacter stack = casters.FirstOrDefault(actor => actor.CastingSpellId == FireStack);
        if (stack != null)
        {
            GameObject target = GameObjectManager.GetObjectByObjectId(stack.SpellCastInfo.TargetId);
            // When marked for a stack, join the Duty Support cluster; otherwise join the actual target.
            BattleCharacter[] allies = Actors().Where(IsOtherPartyMember).ToArray();
            Vector3 stackCenter = target?.Location ?? SubjectCenter;
            if (target?.ObjectId == Core.Player.ObjectId && allies.Length != 0)
            {
                stackCenter = new Vector3(allies.Average(ally => ally.X), Core.Player.Y, allies.Average(ally => ally.Z));
            }

            if (target != null && TryPositivePoint(stackCenter, casters, out Vector3 point))
            {
                return Task.FromResult(OwnPositive(stack, point));
            }

            ReleasePositive("Stack target or safe intersection unavailable; yielding to avoidance");
            return Task.FromResult(false);
        }

        BattleCharacter[] towers = casters.Where(actor => actor.CastingSpellId == Tower).ToArray();
        if (towers.Length != 0)
        {
            double earliest = towers.Min(actor => actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds);
            BattleCharacter[] party = Actors().Where(IsOtherPartyMember).ToArray();
            // Later waves must not distract from the pair resolving first. Do not chase an occupied
            // tower: one player is sufficient and NPC allies have their own fixed assignments.
            BattleCharacter selected = towers.Where(actor => actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds <= earliest + 250 && !party.Any(ally => ally.Distance2D(actor.Location) < 4.5f)).OrderBy(actor => actor.ObjectId == positiveOwner ? 0 : 1).ThenBy(actor => Core.Player.Distance2D(actor.Location)).FirstOrDefault();
            if (selected != null && TryPositivePoint(selected.Location, casters, out Vector3 point))
            {
                unavailableWave = "";
                return Task.FromResult(OwnPositive(selected, point));
            }

            string wave = string.Join(",", towers.Select(actor => actor.ObjectId).OrderBy(id => id));
            if (unavailableWave != wave)
            {
                unavailableWave = wave;
                Logger.Warning("[Castrum Abania] No uncovered safe tower; yielding to avoidance.");
            }
        }

        ReleasePositive("No unoccupied current tower or active stack");
        return Task.FromResult(false);
    }

    private bool OwnPositive(BattleCharacter caster, Vector3 point)
    {
        positiveOwner = caster.ObjectId;
        positiveAction = caster.CastingSpellId;
        positivePoint = point;
        positiveExpires = DateTime.UtcNow + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromMilliseconds(250);
        return ApplyPositive(point);
    }

    private bool ApplyPositive(Vector3 point)
    {
        leased = true;
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Holding the assigned Castrum Abania tower/stack");
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            // RB has taken the input channel. Never let our old request stop its successor.
            moving = false;
            return false;
        }

        if (Core.Player.Distance2D(point) <= 0.75f)
        {
            StopPositiveMovement();
            // Continue rotation/healing while the Movement capability alone holds the position.
            return false;
        }

        Navigator.PlayerMover.MoveTowards(new Vector3(point.X, Core.Player.Y, point.Z));
        moving = true;
        return true;
    }

    private void StopPositiveMovement()
    {
        if (moving && !AvoidanceManager.IsRunningOutOfAvoid)
        {
            Navigator.PlayerMover.MoveStop();
        }

        moving = false;
    }

    private void ReleasePositive(string reason)
    {
        StopPositiveMovement();
        if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, reason);
        }

        leased = false;
        positiveOwner = positiveAction = 0;
        positiveExpires = DateTime.MinValue;
    }

    private static bool TryPositivePoint(Vector3 center, BattleCharacter[] casters, out Vector3 result)
    {
        // Search only the interior of the required five-yalm positive region. This is not a second
        // dodge controller: RB still owns all hazard escape. Candidate filtering prevents the
        // positive request from repeatedly pulling back into an earlier-resolving cone/grid/line.
        List<Vector3> candidates = [center];
        foreach (float radius in new[]
        {
            2f,
            3.5f
        }

        )
        {
            for (int index = 0; index < 16; index++)
            {
                double angle = index * Math.PI / 8;
                candidates.Add(new Vector3(center.X + radius * (float)Math.Sin(angle), center.Y, center.Z + radius * (float)Math.Cos(angle)));
            }
        }

        Vector3[] safe = candidates.Where(point => point.Distance2D(SubjectCenter) < 18.25f && SafeFromSubjectHazards(point, casters)).OrderBy(point => Core.Player.Distance2D(point)).ToArray();
        result = safe.FirstOrDefault();
        return safe.Length != 0;
    }

    private static bool SafeFromSubjectHazards(Vector3 point, BattleCharacter[] casters)
    {
        foreach (BattleCharacter actor in casters)
        {
            float dx = point.X - actor.X, dz = point.Z - actor.Z;
            float forward = dx * (float)Math.Sin(actor.Heading) + dz * (float)Math.Cos(actor.Heading);
            float side = dx * (float)Math.Cos(actor.Heading) - dz * (float)Math.Sin(actor.Heading);
            if (actor.CastingSpellId == IceGrid && forward >= -0.5f && forward <= 40.5f && Math.Abs(side) <= 2.5f)
            {
                return false;
            }

            if (ActiveFireCone(actor) && forward >= 0 && Math.Abs(Math.Atan2(side, forward)) <= 33 * Math.PI / 180)
            {
                return false;
            }
        }

        if (LineBaitsActive())
        {
            foreach (BattleCharacter ally in Actors().Where(IsOtherPartyMember))
            {
                double angle = Math.Atan2(ally.X - SubjectCenter.X, ally.Z - SubjectCenter.Z);
                float dx = point.X - SubjectCenter.X, dz = point.Z - SubjectCenter.Z;
                double forward = dx * Math.Sin(angle) + dz * Math.Cos(angle);
                double side = dx * Math.Cos(angle) - dz * Math.Sin(angle);
                if (forward >= -0.5 && forward <= 20.5 && Math.Abs(side) <= 3.5)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static IEnumerable<BattleCharacter> Actors() => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(actor => actor.IsValid && actor.IsVisible);
    private static bool Casting(BattleCharacter actor, uint action) => actor.IsValid && actor.IsCasting && actor.CastingSpellId == action;
    private static bool HasRayMarker(BattleCharacter actor) => actor.IsValid && actor.VfxContainer.IsValid && actor.VfxContainer.Vfx.Any(vfx => vfx != null && vfx.IsValid && vfx.Id == 74);
    // Serial Magicks exposes both sets early. Use only the earliest resolving cones for avoidance and stack selection.
    private static bool ActiveFireCone(BattleCharacter actor) => Casting(actor, FireCone) && !Actors().Any(other => Casting(other, FireCone) && other.SpellCastInfo.RemainingCastTime.TotalMilliseconds + 250 < actor.SpellCastInfo.RemainingCastTime.TotalMilliseconds);
    private static bool IsOtherPartyMember(BattleCharacter actor) => actor.IsValid && actor.IsAlive && !actor.IsMe && (PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)actor.NpcId) || PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId));
    private static bool SubjectActive() => Active(SubZoneId.ProjectAegis);
    private static bool LineBaitsActive() => SubjectActive() && Actors().Any(actor => Casting(actor, LineBait) || Casting(actor, Tower));
    private static bool Active(SubZoneId subzone) => DutyManager.InInstance && Core.Player.InCombat && WorldManager.ZoneId == (uint)Data.ZoneId.CastrumAbania && WorldManager.SubZoneId == (uint)subzone;
    private static void AddBoundary(SubZoneId subzone, Vector3 center, float radius) => AvoidanceHelpers.AddAvoidDonut(() => Active(subzone), () => center, 90, radius, AvoidancePriority.High);
}
