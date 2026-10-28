using DutyMechanic.Data;
using DutyMechanic.Logging;
using Clio.Utilities;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Supplies Tam-Tara boss geometry to native avoidance while the profile and
/// combat routine retain routing, target selection and combat execution.
/// </summary>
public class TamTaraDeepcroft : AbstractDungeon
{
    // Installed Global name/action rows identify the three altar encounters and
    // Galvanth. Priest/Octavel ownership begins before their Soulcounter spawns.
    private const uint Priest = 79, Octavel = 119, Soulcounter = 455, Galvanth = 73;
    private const uint Condemnation = 912, MindBlast = 987;
    private static TamTaraDeepcroft active;
    private bool? savedSideStepEnabled;
    private readonly List<AvoidInfo> approachAvoids = new();

    // Domestic requested altar-back exclusions after the 23:32 capture showed native
    // wall-following leaving the stairs for the outer corner. This frame follows the
    // successfully traversed bridge: (-80.81596,5.217739) -> (-67.99475,-2.452904).
    // Its side bands cover the two altar backs while retaining a 8.5y approach lane.
    // The lower band starts before the observed drop (side offset3.85y), not only
    // at the final trapped point (7.03y). Bounds end at the bridge entry, so they
    // cannot restrict Galvanth's arena. These are candidate exclusions to live-test,
    // not a claim that the remote mesh or native anti-stuck implementation is fixed.
    private static readonly Vector3 BridgeEntry = new(-80.81596f, 13.859f, 5.217739f);
    private static readonly Vector2[] UpperAltarBack = ApproachBand(-14f, -6f);
    private static readonly Vector2[] LowerAltarBack = ApproachBand(2.5f, 14f);

    private static Vector2[] ApproachBand(float sideMin, float sideMax)
    {
        Vector2 Point(float forward, float side) => new(
            0.8581443f * forward + 0.5134085f * side,
            -0.5134085f * forward + 0.8581443f * side);
        // Sixteen yalms reaches behind both captured orb plinths (forward offsets
        // -11.27/-11.94); zero ends at the verified bridge-entry cross-section.
        return [Point(-16, sideMin), Point(0, sideMin), Point(0, sideMax), Point(-16, sideMax)];
    }

    private static bool InDuty() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == 1037 && Core.Me != null &&
        Core.Me.IsValid;

    private static bool Live() => InDuty() && Core.Me.IsAlive && Core.Me.InCombat;

    /// <summary>Releases Tam-Tara's saved SideStep state on stop, disable or hook replacement.</summary>
    public static void ResetActive()
    {
        active?.SetBossOwnership(false);
        active?.ReleaseApproachAvoids();
        active = null;
    }

    /// <summary>
    /// Updates ownership on the bot pulse so yielding profile waits cannot delay
    /// boss acquisition, and restores SideStep across interrupted combat.
    /// </summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        if (!InDuty()) ResetActive();
        else active.UpdateOwnership();
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheTamTaraDeepcroft;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = null;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        SetBossOwnership(false);
        active = this;
        RegisterApproachAvoids();
        // Condemnation's 6y action range plus the Soulcounter's 1y hitbox forms a
        // 7y frontal 90-degree cone. Add 0.5y radial and 5 degrees per edge for
        // clearance; native avoidance is the sole evasive mover, with no NPC proxy.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Live() && savedSideStepEnabled != null,
            objectSelector: a => a.IsValid && a.IsAlive && a.NpcId == Soulcounter &&
                a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId == Condemnation,
            leashPointProducer: () => Core.Me.Location, leashRadius: 40f,
            rotationDegrees: 0f, radius: 7.5f, arcDegrees: 100f);

        // Mind Blast is a harmful boss-centered circle, not a stack. Its 8y action
        // range plus 1.95y hitbox and 0.5y margin yields 10.45y. Native avoidance
        // retains precedence over the profile's ordinary imp-approach movement.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Live() && savedSideStepEnabled != null,
            radius: 10.45f,
            objectSelector: a => a.IsValid && a.IsAlive && a.NpcId == Galvanth &&
                a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId == MindBlast);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        if (!InDuty()) { SetBossOwnership(false); return Task.FromResult(false); }
        active = this;
        RegisterApproachAvoids();
        UpdateOwnership();
        // Yield so routine healing, interrupts and authored imp priority continue
        // normally, including while native avoidance handles a registered hazard.
        return Task.FromResult(false);
    }

    private bool ApproachActive()
    {
        // No exclusion may obstruct an orb interaction or survive a loading, stop,
        // death or completed-duty boundary. Use the verified director rows, not a
        // coordinate-progress latch, so recovery/re-entry retains the actual state.
        if (!ReferenceEquals(active, this) || !InDuty() || !Core.Me.IsAlive ||
            Math.Abs(Core.Me.Y - BridgeEntry.Y) > 5) return false;
        return DirectorManager.ActiveDirector is InstanceContentDirector director &&
            !director.InstanceEnded && director.IsTodoChecked(0) && !director.IsTodoChecked(3);
    }

    private void RegisterApproachAvoids()
    {
        if (approachAvoids.Count != 0) return;
        foreach (var band in new[] { UpperAltarBack, LowerAltarBack })
            approachAvoids.Add(AvoidanceManager.AddAvoidPolygon<Vector3>(
                condition: ApproachActive, leashPointProducer: () => Core.Me.Location,
                leashRadius: 40f, rotationProducer: _ => 0, scaleProducer: _ => 1,
                heightProducer: _ => 10f, pointsProducer: _ => band,
                locationProducer: p => p, collectionProducer: () => new[] { BridgeEntry },
                ignoreIfBlocking: false, priority: AvoidancePriority.High));
        // Native avoidance remains the only geometric mover. Do not issue input,
        // install another navigator, or disable SideStep for this travel constraint.
    }

    private void ReleaseApproachAvoids()
    {
        foreach (var avoid in approachAvoids) AvoidanceManager.RemoveAvoid(avoid);
        approachAvoids.Clear();
    }

    private void UpdateOwnership()
    {
        if (!Live()) { SetBossOwnership(false); return; }
        // The third altar capture exposed a 2.3s delay while the profile's existing
        // barrier WaitTimer retained TreeStart. Both entry and the existing bot pulse
        // use this single owner; retaining it between fights permits immediate acquire.
        // Once acquired, combat continuity covers the altar's brief spawn gap without
        // repeatedly enumerating actors or releasing on its temporary untargetability.
        if (savedSideStepEnabled != null) return;
        var bossPresent = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(a => a.IsValid && a.IsAlive && a.IsTargetable && a.CanAttack && a.InCombat &&
                a.NpcId is Priest or Octavel or Soulcounter or Galvanth &&
                Math.Abs(a.Y - Core.Me.Y) < 8 && a.Distance2D() < 40);
        // Corridor packs cannot acquire ownership without a named encounter actor.
        SetBossOwnership(bossPresent);
    }

    private void SetBossOwnership(bool enabled)
    {
        if (enabled && savedSideStepEnabled == null && SidestepPlugin != null)
        {
            savedSideStepEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[TamTara] DutyMechanic owns boss hazards; SideStep suspended.");
        }
        else if (!enabled && savedSideStepEnabled != null)
        {
            // Restore the actual predecessor setting, including an explicitly
            // disabled plugin, instead of unconditionally enabling another owner.
            if (SidestepPlugin != null) SidestepPlugin.Enabled = savedSideStepEnabled.Value;
            savedSideStepEnabled = null;
            Logger.Information("[TamTara] Boss ownership released; SideStep restored.");
        }
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        SetBossOwnership(false);
        ReleaseApproachAvoids();
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }
}
