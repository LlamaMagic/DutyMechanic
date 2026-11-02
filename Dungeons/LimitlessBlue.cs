using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Boards Bismarck's targetable shell in the normal level-57 trial while
/// preserving native avoidance and the combat routine's ordinary actions.
/// </summary>
public class LimitlessBlue : AbstractDungeon
{
    private const uint Carapace = 3656, Corona = 3657, Whaleback = 719;
    private const uint DragonkillerA = 2005544, DragonkillerB = 2005545;
    // The 2026-11-01 party requested green; So is the wind add and Ul the water add.
    private const uint GreenSanuwa = 3654, BlueSanuwa = 3655;
    private const string CannonRoute = "Limitless Blue cannon";
    // The November 1 synchronized WHM capture acquired status 719 on both
    // shells while approaching this pre-existing point; no jump was required.
    private static readonly Vector3 BoardingPoint = new(4.443342f, 0.1489766f, -6.865881f);
    private ChrysalisMovement movement;
    private bool lifecycle, boarding, leased, warnedMover;
    private uint boardingTarget, boardingObject;
    private readonly CapabilityManagerHandle cannonHandle = CapabilityManager.CreateNewHandle();
    private bool cannonLeased, shellAvailable;
    private uint cannonObject;
    private DateTime nextCannonInteract, nextCannonApproach;
    private readonly CapabilityManagerHandle sanuwaAoeHandle = CapabilityManager.CreateNewHandle();
    private bool sanuwaAoeLeased;
    private uint sanuwaAssignment, pendingSanuwa;
    private DateTime pendingSanuwaSince, nextSanuwaTarget;
    private bool sanuwaAcquiring;
    private SanuwaTargetProvider sanuwaProvider;
    private bool sanuwaProviderLost;
    private readonly LimitlessBluePullSettings pullSettings = new();
    private bool warnedPullSettings;
    private uint sanuwaSeparationTarget;
    private AvoidInfo sanuwaAvoid;

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.LimitlessBlue;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        lifecycle = true;
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        TreeRoot.OnStop += OnStop;
        TreeHooks.Instance.OnHooksCleared += OnHooksCleared;
        GameEvents.OnMapChanged += OnMapChanged;
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        EndLifecycle();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        Update();
        // Native avoidance takes precedence. Only active approach consumes this
        // scheduling path; once on the back the routine may heal and attack.
        if (movement?.IsEscaping == true) return Task.FromResult(true);
        if (boarding && !AvoidanceManager.IsRunningOutOfAvoid) return Task.FromResult(true);
        if (sanuwaAcquiring && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
            return Task.FromResult(true);
        return Task.FromResult(UpdateCannon());
    }

    private void OnPulse(object sender, EventArgs args) => Update();
    private void OnStop(BotBase bot) => EndLifecycle();
    private void OnHooksCleared(object sender, EventArgs args) => EndLifecycle();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != (uint)ZoneId) EndLifecycle();
    }

    private void Update()
    {
        if (!lifecycle) return;
        if (!TreeRoot.IsRunning || WorldManager.ZoneId != (uint)ZoneId || !DutyManager.InInstance ||
            Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            Release();
            return;
        }

        BattleCharacter shell = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.IsVisible && a.IsTargetable &&
                (a.NpcId == Carapace || a.NpcId == Corona));
        shellAvailable = shell != null;
        if (shellAvailable) ReleaseCannon();
        UpdateSanuwa();
        bool onBack = Core.Me.HasAura(Whaleback);
        var state = new LimitlessBlueBoardingPolicy.State(Core.Me.InCombat, Core.Me.IsAlive,
            CommonBehaviors.IsLoading, QuestLogManager.InCutscene, onBack, shell != null,
            Core.Me.Location.Distance2D(BoardingPoint) <= 1f);
        bool requested = LimitlessBlueBoardingPolicy.ShouldApproach(state);
        // A new direct crossing begins only on the captured eastern apron. Once
        // acquired, keep it until status/target loss or an unsafe segment; an
        // avoidance interruption must reacquire from the observed apron.
        bool safe = requested && !AvoidanceManager.IsRunningOutOfAvoid &&
            !MovementManager.GenerallyOutOfControl && SegmentClear(Core.Me.Location, BoardingPoint);
        // A new actor, even one with the same NPC row, must not inherit a
        // predecessor's input claim. It can acquire its own request next pulse.
        bool sameOwner = !boarding || shell?.ObjectId == boardingObject;
        bool next = safe && sameOwner && (boarding || LimitlessBlueBoardingPolicy.InObservedRunway(Core.Me.Location.X, Core.Me.Location.Z));
        if (next && movement == null) EnsureMovement();
        next &= movement?.IsCurrent == true;

        boarding = next;
        if (next) { boardingTarget = shell.NpcId; boardingObject = shell.ObjectId; }
        movement?.Request(next ? BoardingPoint : null, 1f);
        movement?.Update();
        if (next)
        {
            CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 500, "Limitless Blue boarding");
            leased = true;
        }
        else if (leased)
        {
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Limitless Blue boarding released");
            leased = false;
        }
    }

    private void EnsureMovement()
    {
        if (Navigator.PlayerMover?.GetType() != typeof(SlideMover) || Navigator.NavigationProvider == null)
        {
            if (!warnedMover) Logger.Warning("[LimitlessBlue] Boarding unavailable: another mover owns input.");
            warnedMover = true;
            return;
        }
        // This existing owner arbitrates semantic movement, native escape, service
        // navigation and stale cleanup. It remains installed through the encounter
        // so a later escape or successor cannot be stopped by an old request.
        movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: point =>
            Core.Me != null && Core.Me.IsValid && SegmentClear(Core.Me.Location, point));
        Navigator.PlayerMover = movement;
    }

    private static bool SegmentClear(Vector3 start, Vector3 end)
    {
        // Check the actual next segment, not just its endpoints: ordinary combat
        // can otherwise cross back into a still-active native avoid after egress.
        int steps = Math.Max(1, (int)Math.Ceiling(start.Distance2D(end) / .5f));
        for (int i = 0; i <= steps; ++i)
        {
            float t = i / (float)steps;
            var point = new Vector3(start.X + (end.X - start.X) * t,
                start.Y + (end.Y - start.Y) * t, start.Z + (end.Z - start.Z) * t);
            if (AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private static bool IsTank(ClassJobType job) => job is ClassJobType.Gladiator or ClassJobType.Paladin or
        ClassJobType.Marauder or ClassJobType.Warrior or ClassJobType.DarkKnight or ClassJobType.Gunbreaker;

    private void UpdateSanuwa()
    {
        // Each tank must hold one add apart. The 2026-11-01 PLD capture showed
        // both adds tethered on Arelia after a point-blank AoE; disable routine AoE
        // only while both are alive and let OrderBot retain normal kill scheduling.
        var adds = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible && a.IsTargetable &&
                (a.NpcId == GreenSanuwa || a.NpcId == BlueSanuwa)).ToArray();
        var green = adds.FirstOrDefault(a => a.NpcId == GreenSanuwa);
        var blue = adds.FirstOrDefault(a => a.NpcId == BlueSanuwa);
        if (!IsTank(Core.Me.CurrentJob) || green == null || blue == null)
        {
            ReleaseSanuwa();
            return;
        }

        CapabilityManager.Update(sanuwaAoeHandle, CapabilityFlags.Aoe, 500, "Limitless Blue split Sanuwa");
        sanuwaAoeLeased = true;
        UpdatePullSettings();
        uint otherTank = PartyManager.VisibleMembers.Where(m => m != null && m.IsValid && m.ObjectId != Core.Me.ObjectId &&
            m.CurrentHealth > 0 && IsTank(m.Class)).Select(m => m.ObjectId).FirstOrDefault();
        uint desired = LimitlessBlueSanuwaPolicy.Select(
            new(green.ObjectId, true, green.CurrentTargetId), new(blue.ObjectId, true, blue.CurrentTargetId),
            Core.Me.ObjectId, otherTank, sanuwaAssignment);
        if (sanuwaAssignment != 0 && desired != sanuwaAssignment &&
            (sanuwaAssignment == green.ObjectId || sanuwaAssignment == blue.ObjectId))
        {
            // Target IDs can change briefly during casts. Require a stable handoff
            // before abandoning a living assignment for the other tank's add.
            if (pendingSanuwa != desired) { pendingSanuwa = desired; pendingSanuwaSince = DateTime.UtcNow; }
            if (DateTime.UtcNow - pendingSanuwaSince < TimeSpan.FromSeconds(1)) desired = sanuwaAssignment;
        }
        else pendingSanuwa = 0;
        if (desired != sanuwaAssignment)
        {
            sanuwaAssignment = desired;
            pendingSanuwa = 0;
            Logger.Information($"[LimitlessBlue] SANUWA_ASSIGN object={desired:X} otherTank={otherTank:X} greenTarget={green.CurrentTargetId:X} blueTarget={blue.CurrentTargetId:X}");
        }
        if (sanuwaProvider != null && !ReferenceEquals(CombatTargeting.Instance.Provider, sanuwaProvider))
        {
            // A successor owns targeting; do not replace it or attack an
            // unverified snake until this split ends.
            sanuwaProvider = null;
            sanuwaProviderLost = true;
        }
        if (sanuwaProvider == null && !sanuwaProviderLost && !CombatTargeting.Instance.Locked)
        {
            sanuwaProvider = new SanuwaTargetProvider(this, CombatTargeting.Instance.Provider);
            CombatTargeting.Instance.Provider = sanuwaProvider;
        }
        if (sanuwaProvider == null) { sanuwaAcquiring = true; sanuwaSeparationTarget = 0; return; }
        var assigned = desired == green.ObjectId ? green : blue;
        bool combatPoi = Poi.Current == null || Poi.Current.Type is PoiType.None or PoiType.Kill or PoiType.Hotspot;
        // A death, recovery, or interaction POI owns the actor. Disable this
        // positional request before yielding so it cannot redirect that work.
        if (!combatPoi) { sanuwaAcquiring = false; sanuwaSeparationTarget = 0; return; }
        sanuwaSeparationTarget = LimitlessBlueSanuwaPolicy.SeparationTarget(
            new(green.ObjectId, true, green.CurrentTargetId), new(blue.ObjectId, true, blue.CurrentTargetId),
            Core.Me.ObjectId, desired);
        if (sanuwaSeparationTarget != 0)
        {
            // Native escape needs the same ownership-aware input cleanup as
            // boarding. If another mover owns input, do not register this avoid.
            if (movement == null) EnsureMovement();
            if (movement?.IsCurrent == true && (sanuwaAvoid == null || !AvoidanceManager.AvoidInfos.Contains(sanuwaAvoid)))
            {
                // A 15y separation model agrees with the tether dropping at
                // roughly 13–15y in capture; 0.5y adds standard clearance.
                sanuwaAvoid = AvoidanceManager.AddAvoidObject<BattleCharacter>(
                    canRun: () => lifecycle && sanuwaSeparationTarget != 0 && movement?.IsCurrent == true,
                    objectSelector: a => a.IsValid && a.IsAlive && a.ObjectId == sanuwaSeparationTarget,
                    radiusProducer: _ => 15.5f);
            }
        }
        if (pendingSanuwa != 0)
        {
            // Keep the stable POI until the debounce completes, but do not let
            // the routine keep attacking a snake now held by the other tank.
            // Native avoidance and forced movement still preempt this hold.
            sanuwaAcquiring = true;
            return;
        }
        if (Poi.Current?.Unit?.ObjectId != assigned.ObjectId) Poi.Current = new Poi(assigned, PoiType.Kill);
        sanuwaAcquiring = Core.Me.CurrentTarget?.ObjectId != assigned.ObjectId;
        if (sanuwaAcquiring && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl &&
            DateTime.UtcNow >= nextSanuwaTarget)
        {
            // A stale selected target can permit the routine's off-target attack
            // before OrderBot reacts to the POI. Acquire only the chosen add and
            // hold routine scheduling until the client confirms the handoff.
            nextSanuwaTarget = DateTime.UtcNow.AddSeconds(1);
            assigned.Target();
        }
    }

    // The November 1 live capture showed OrderBot repeatedly restoring its
    // old Kill POI after the other tank took that add. Filter only the other
    // snake from the profile's ranked candidates so both schedulers agree;
    // leave the profile's eligibility and ordering for all other actors intact.
    private sealed class SanuwaTargetProvider(LimitlessBlue owner, ITargetingProvider previous) : ITargetingProvider
    {
        internal ITargetingProvider Previous => previous;
        public List<BattleCharacter> GetObjectsByWeight()
        {
            var ranked = previous?.GetObjectsByWeight() ?? [];
            if (!owner.lifecycle || !ReferenceEquals(owner.sanuwaProvider, this) || owner.sanuwaAssignment == 0)
                return ranked;
            return ranked.Where(a => (a.NpcId != GreenSanuwa && a.NpcId != BlueSanuwa) || a.ObjectId == owner.sanuwaAssignment).ToList();
        }
    }

    private void ReleaseSanuwa()
    {
        if (sanuwaProvider != null && ReferenceEquals(CombatTargeting.Instance.Provider, sanuwaProvider))
        {
            bool locked = CombatTargeting.Instance.Locked;
            CombatTargeting.Instance.Locked = false;
            CombatTargeting.Instance.Provider = sanuwaProvider.Previous;
            CombatTargeting.Instance.Locked = locked;
        }
        sanuwaProvider = null;
        sanuwaProviderLost = false;
        pullSettings.Restore();
        warnedPullSettings = false;
        sanuwaSeparationTarget = 0;
        if (sanuwaAvoid != null) AvoidanceManager.RemoveAvoid(sanuwaAvoid);
        sanuwaAvoid = null;
        if (sanuwaAoeLeased) CapabilityManager.Clear(sanuwaAoeHandle, CapabilityFlags.Aoe, "Limitless Blue Sanuwa ended");
        sanuwaAoeLeased = false;
        sanuwaAcquiring = false;
        sanuwaAssignment = pendingSanuwa = 0;
        nextSanuwaTarget = default;
    }

    private void UpdatePullSettings()
    {
        if (RoutineManager.Current?.GetType().FullName?.Contains("Magitek") != true)
        {
            pullSettings.Restore();
            return;
        }
        string job = Core.Me.CurrentJob.ToString();
        string[] names = job switch
        {
            "Paladin" => ["UseShieldLobOnLostAggro", "UseShieldLobToPullExtraEnemies", "UseProvoke"],
            "Warrior" => ["UseTomahawkOnLostAggro", "UseTomahawkToPullExtraEnemies", "UseProvoke"],
            "DarkKnight" => ["UnmendToPullOrAggro", "UseProvoke"],
            "Gunbreaker" => ["LightningShotToPullAggro", "UseProvoke"],
            _ => []
        };
        if (names.Length == 0) return;
        try
        {
            // RebornBuddy may retain unloaded hot-reload assemblies. Refuse an
            // ambiguous settings singleton rather than modifying a stale routine.
            var types = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType($"Magitek.Models.{job}.{job}Settings", false))
                .Where(t => t != null).ToArray();
            var settings = types.Length == 1 ? types[0].GetProperty("Instance")?.GetValue(null) : null;
            bool alreadyActive = pullSettings.Active;
            if (pullSettings.Apply(settings, names))
            {
                warnedPullSettings = false;
                if (!alreadyActive) Logger.Information($"[LimitlessBlue] Scoped {job} off-target aggro settings disabled.");
                return;
            }
        }
        catch (Exception ex)
        {
            if (!warnedPullSettings) Logger.Warning($"[LimitlessBlue] Cannot scope Magitek aggro settings: {ex.Message}");
            warnedPullSettings = true;
            return;
        }
        if (!warnedPullSettings) Logger.Warning("[LimitlessBlue] Magitek aggro settings unavailable or ambiguous.");
        warnedPullSettings = true;
    }

    private bool UpdateCannon()
    {
        if (!lifecycle || !TreeRoot.IsRunning || WorldManager.ZoneId != (uint)ZoneId ||
            !DutyManager.InInstance || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            CommonBehaviors.IsLoading || QuestLogManager.InCutscene || shellAvailable)
        {
            ReleaseCannon();
            return false;
        }

        // Reacquire both event objects each pulse: a party member can fire either
        // one during our approach, invalidating its wrapper and targetability.
        GameObject first = GameObjectManager.GetObjectByNPCId(DragonkillerA);
        GameObject second = GameObjectManager.GetObjectByNPCId(DragonkillerB);
        bool firstValid = first?.IsValid == true, secondValid = second?.IsValid == true;
        uint selected = LimitlessBlueCannonPolicy.Select(firstValid ? first.ObjectId : 0,
            firstValid && first.IsTargetable, secondValid ? second.ObjectId : 0,
            secondValid && second.IsTargetable, cannonObject);
        if (selected == 0)
        {
            ReleaseCannon();
            return false;
        }
        if (cannonObject != selected)
        {
            ReleaseCannon();
            cannonObject = selected;
        }
        var cannon = firstValid && first.ObjectId == selected ? first : second;
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseCannonMovement();
            return false;
        }
        // Allow the interaction acknowledgment to arrive before a small
        // position drift can start another approach and interrupt the action.
        if (DateTime.UtcNow < nextCannonInteract) return false;
        // The legacy route interacted at 1.5 yalms. Use the mesh for the
        // approach and keep SideStep enabled; a straight line is not validated floor.
        if (Core.Me.Location.Distance2D(cannon.Location) > 1.5f)
        {
            if (DateTime.UtcNow < nextCannonApproach) return false;
            if (movement == null) EnsureMovement();
            if (movement?.IsCurrent != true) return false;
            movement.BeginNavigation(CannonRoute);
            CapabilityManager.Update(cannonHandle, CapabilityFlags.Movement, 500, "Limitless Blue cannon approach");
            cannonLeased = true;
            var result = movement.Navigate(cannon.Location, 1.25f);
            if (result is MoveResult.Failed or MoveResult.PathGenerationFailed or MoveResult.Done or MoveResult.ReachedDestination)
            {
                // A navigation failure is retryable, but must not starve healing
                // or the party's own cannon interaction while the mesh recovers.
                ReleaseCannonMovement();
                nextCannonApproach = DateTime.UtcNow.AddSeconds(1);
                return false;
            }
            return true;
        }

        ReleaseCannonMovement();
        if (DateTime.UtcNow >= nextCannonInteract && cannon.IsValid && cannon.IsTargetable)
        {
            cannon.Interact();
            // Server and other players can win the race. Retry with a fresh
            // object snapshot rather than retaining a wrapper or spinning.
            nextCannonInteract = DateTime.UtcNow.AddSeconds(1);
        }
        return false;
    }

    private void ReleaseCannonMovement()
    {
        movement?.EndNavigation();
        if (cannonLeased) CapabilityManager.Clear(cannonHandle, CapabilityFlags.Movement, "Limitless Blue cannon released");
        cannonLeased = false;
    }

    private void ReleaseCannon()
    {
        ReleaseCannonMovement();
        cannonObject = 0;
        nextCannonInteract = default;
        nextCannonApproach = default;
    }

    private void Release()
    {
        ReleaseSanuwa();
        ReleaseCannon();
        boarding = false;
        if (leased) CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Limitless Blue lifecycle ended");
        leased = false;
        movement?.Detach();
        movement = null;
        shellAvailable = false;
    }

    private void EndLifecycle()
    {
        lifecycle = false;
        ff14bot.NeoProfile.BotEvents.OnPulse -= OnPulse;
        TreeRoot.OnStop -= OnStop;
        TreeHooks.Instance.OnHooksCleared -= OnHooksCleared;
        GameEvents.OnMapChanged -= OnMapChanged;
        Release();
    }
}
