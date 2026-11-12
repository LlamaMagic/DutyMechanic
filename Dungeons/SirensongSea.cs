using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Extensions;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Lv. 61: The Sirensong Sea dungeon logic.
/// </summary>
public class SirensongSea : AbstractDungeon
{
    private static SirensongSea active;
    private bool? priorSideStep;
    private InputOwner inputOwner;
    private uint stackTarget;
    private DateTime stackUntil;
    private bool stackLeased;
    private readonly CapabilityManagerHandle stackHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle tetherHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle firstShadowHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle shadowHandle = CapabilityManager.CreateNewHandle();
    private readonly CapabilityManagerHandle marchHandle = CapabilityManager.CreateNewHandle();
    private DateTime marchCastFinish;
    private uint marchAction;
    private bool marchLeased;
    private Vector3? marchStage;
    private float marchHeading;
    private DateTime nextMarchSearch;
    private string semanticKind;
    private const int ShadowflowDuration = 12_500;
    private DateTime ShadowflowTimestamp = DateTime.MinValue;
    private Stopwatch shadowflowTimer;
    private int observedPhase = -1, phaseCandidate = -1;
    private DateTime phaseCandidateSince;
    private bool phaseConesRegistered, shadowflowHadClones;
    private int firstPartyPhase = -1;
    private DateTime firstPartySince;
    private bool firstShadowOwned;
    private Vector3? firstShadowDestination;

    private static bool Live() => TreeRoot.IsRunning && !CommonBehaviors.IsLoading &&
        !QuestLogManager.InCutscene && WorldManager.ZoneId == (uint)ZoneId.TheSirensongSea &&
        Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat;

    /// <summary>Restores the prior plugin state when the bot stops or leaves combat.</summary>
    public static void ResetActive()
    {
        active?.ReleaseBossOwnership();
        active = null;
    }

    /// <summary>Releases encounter ownership even when the usual dungeon hook is bypassed.</summary>
    public static void CheckLifecycle()
    {
        if (active == null) return;
        if (!Live()) { ResetActive(); return; }
    }

    private void ReleaseBossOwnership()
    {
        ReleaseStack();
        ReleaseMarch();
        ReleaseFirstShadow();
        inputOwner?.Detach();
        inputOwner = null;
        stackTarget = 0;
        shadowflowTimer?.Stop();
        shadowflowTimer = null;
        ShadowflowTimestamp = DateTime.MinValue;
        observedPhase = phaseCandidate = -1;
        phaseConesRegistered = shadowflowHadClones = false;
        if (priorSideStep == null) return;
        if (SidestepPlugin != null && !SidestepPlugin.Enabled)
            SidestepPlugin.Enabled = priorSideStep.Value;
        priorSideStep = null;
    }

    private void UpdateBossOwnership()
    {
        if (!Live()) { ReleaseBossOwnership(); return; }
        active = this;
        bool bossPresent = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(a => a.IsValid && a.IsAlive && a.InCombat && a.Distance2D() < 60 &&
                a.NpcId is EnemyNpc.Lugat or EnemyNpc.TheGovernor or EnemyNpc.Lorelei);
        if (!bossPresent) { ReleaseBossOwnership(); return; }
        if (priorSideStep == null && SidestepPlugin != null)
        {
            // The boss-specific handler owns mechanics; restore the prior state for trash.
            priorSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
        }
    }

    /// <inheritdoc/>
    public override ZoneId ZoneId => Data.ZoneId.TheSirensongSea;

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];

    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate{ get; } = [];
    /// <inheritdoc/>
    protected override async Task<bool> EnterDungeonAsync()
    {
        ResetActive();

        // Lugat's frontal 180-degree cleave reaches 30y; half a yalm and five
        // degrees on each side provide clearance until live geometry is captured.
        AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => Live() && priorSideStep != null,
            objectSelector: a => a.IsValid && a.IsAlive && a.NpcId == EnemyNpc.Lugat &&
                a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId == 8022,
            leashPointProducer: () => Core.Me.Location, leashRadius: 60f,
            rotationDegrees: 0f, radius: 30.5f, arcDegrees: 190f);

        // November 11 capture: Sea Swallows All pulls the player to Lugat, then
        // boss 8026 and four untargetable 0x18D6 helpers casting 8027 resolve
        // together. Their recorded cast locations match their actors; the referenced
        // 7y/8y circles include a 0.5y margin here. Native avoidance owns escape.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Live() && priorSideStep != null,
            objectSelector: a => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid &&
                ((a.BaseId == 0x1AFB && a.CastingSpellId == 8026) ||
                 (a.BaseId == 0x18D6 && a.CastingSpellId == 8027)),
            radiusProducer: a => a.CastingSpellId == 8026 ? 7.5f : 8.5f,
            priority: AvoidancePriority.High));

        // Blue puddles of fire that fall on the player between the first boss and the second
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition: () => WorldManager.ZoneId == (uint)ZoneId.TheSirensongSea,
            objectSelector: eo => eo.IsVisible && eo.NpcId == EnemyNpc.BlueFirePuddle,
            radiusProducer: eo => 4.0f,
            priority: AvoidancePriority.High));

        // Boss 2
        // Shadow clones of the boss spawn and do a black puddle AoE. Stay away from them
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<BattleCharacter>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GloweringKrautz,
            objectSelector: bc => bc.NpcId == EnemyNpc.TheGroveller,
            radiusProducer: bc => 7.0f,
            priority: AvoidancePriority.High));

        // The final boss's observed-object radius is referenced as 7y. Add the
        // standard 0.5y margin; verify its lifetime and physical footprint live.
        AvoidanceManager.AddAvoid(new AvoidObjectInfo<EventObject>(
            condition: () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WardensDelight,
            objectSelector: eo => eo.IsVisible && eo.NpcId == EnemyNpc.WaterPuddle,
            radiusProducer: eo => 7.5f,
            priority: AvoidancePriority.High));

        // Void Water III is a player-targeted 8y circle, distinct from the
        // persistent puddles and forced march. Native avoidance owns the escape.
        AvoidanceManager.AddAvoidObject<BattleCharacter>(
            canRun: () => Live() && priorSideStep != null && WorldManager.SubZoneId == (uint)SubZoneId.WardensDelight,
            objectSelector: a => a.IsValid && a.IsAlive && a.NpcId == EnemyNpc.Lorelei &&
                a.IsCasting && a.SpellCastInfo.IsValid && a.CastingSpellId == 8040 &&
                (GameObjectManager.GetObjectByObjectId(a.SpellCastInfo.TargetId)?.IsValid == true ||
                 a.SpellCastInfo.CastLocation != Vector3.Zero),
            radius: 8.5f,
            locationProducer: a => GameObjectManager.GetObjectByObjectId(a.SpellCastInfo.TargetId)?.Location ?? a.SpellCastInfo.CastLocation);

        // Boss Arenas
        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.SpaeRock,
            () => ArenaCenter.Lugat,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.GloweringKrautz,
            () => ArenaCenter.TheGovernor,
            outerRadius: 90.0f,
            innerRadius: 19.0f,
            priority: AvoidancePriority.High);

        AvoidanceHelpers.AddAvoidDonut(
            () => Core.Player.InCombat && WorldManager.SubZoneId == (uint)SubZoneId.WardensDelight,
            () => ArenaCenter.Lorelei,
            outerRadius: 90.0f,
            innerRadius: 15.0f,
            priority: AvoidancePriority.High);

        return false;
    }

    /// <inheritdoc/>
    public override async Task<bool> RunAsync()
    {
        UpdateBossOwnership();
        if (priorSideStep != null && inputOwner == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            // Wrap only the expected native mover, leaving another owner's input untouched.
            inputOwner = new InputOwner(this, Navigator.PlayerMover);
            Navigator.PlayerMover = inputOwner;
        }

        var lugat = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.Lugat)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid &&
                a.CastingSpellId == EnemyAction.Hydroball);
        if (priorSideStep != null && lugat != null)
        {
            stackTarget = lugat.SpellCastInfo.TargetId;
            stackUntil = DateTime.UtcNow + lugat.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.35);
        }
        // Retain only the short effect-delivery tail after a completed cast. An
        // interruption earlier in the cast must not keep an obsolete movement lease.
        else if (DateTime.UtcNow >= stackUntil || stackUntil - DateTime.UtcNow > TimeSpan.FromSeconds(.6))
            stackTarget = 0;

        if (WorldManager.SubZoneId == (uint)SubZoneId.WardensDelight)
        {
            bool marchMoving = PositionForMarch();
            if (marchLeased) return marchMoving;
        }
        else if (marchLeased) ReleaseMarch();
        UpdateNativeShadowflow();
        bool firstShadowMoving = PositionForFirstShadowflow();
        if (firstShadowOwned) return firstShadowMoving;
        bool tetherActive = HasPlayerTether();
        if (!tetherActive && semanticKind == "tether") ReleaseStack();
        bool stackMoving = tetherActive ? PositionForTether() : PositionForStack();
        inputOwner?.CompleteEscape();
        if (stackMoving || inputOwner?.HasNativeEscapePriority == true) return true;

        return false;
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ReleaseBossOwnership();
        if (ReferenceEquals(active, this)) active = null;
        return Task.FromResult(false);
    }

    private void UpdateNativeShadowflow()
    {
        if (!Live() || WorldManager.SubZoneId != (uint)SubZoneId.GloweringKrautz) return;
        var TheGovernorNPC = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.TheGovernor)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.IsTargetable);
        if (TheGovernorNPC == null) return;
        if (EnemyAction.Shadowflow.IsCasting() && ShadowflowTimestamp.AddMilliseconds(ShadowflowDuration) < DateTime.Now)
        {
            ShadowflowTimestamp = DateTime.Now;
            shadowflowTimer = Stopwatch.StartNew();
            phaseConesRegistered = false;
            shadowflowHadClones = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.TheGroveller)
                .Any(a => a.IsValid && a.IsVisible);
            observedPhase = phaseCandidate = firstPartyPhase = -1;
            firstShadowDestination = null;
            uint objectId = TheGovernorNPC.ObjectId;
            var timer = shadowflowTimer;
            if (shadowflowHadClones)
                AvoidanceManager.AddAvoidObject<GameObject>(
                    canRun: () => Live() && timer.IsRunning && timer.ElapsedMilliseconds < ShadowflowDuration,
                    radius: 6f, unitIds: objectId);
            else
                // First Shadowflow leaves a fixed center hazard after the boss
                // moves. Three captures stopped around 5.8-5.9y and took four
                // hits; use a 7y exclusion as a test margin, not a measured radius.
                AvoidanceManager.AddAvoidLocation(
                    canRun: () => Live() && timer.IsRunning && timer.ElapsedMilliseconds < ShadowflowDuration,
                    radiusProducer: (Vector3 _) => 7f, locationProducer: (Vector3 p) => p,
                    collectionProducer: () => new[] { ArenaCenter.TheGovernor });
            if (!shadowflowHadClones)
            {
                // Preserve Domestic's original first-cast native cones. The
                // clone-based variant applies only when Shadow Split actors
                // are already present; absent clone data is not a phase vote.
                Vector3 location = TheGovernorNPC.Location;
                for (int spoke = 0; spoke < 6; spoke++)
                {
                    float rotation = 30f + spoke * 60f;
                    AvoidanceManager.AddAvoidUnitCone<GameObject>(
                        canRun: () => Live() && !firstShadowOwned && timer.IsRunning && timer.ElapsedMilliseconds < ShadowflowDuration,
                        objectSelector: obj => obj.ObjectId == objectId,
                        leashPointProducer: () => location, leashRadius: 60f,
                        rotationDegrees: rotation, radius: 30f, arcDegrees: 45f);
                }
                Logger.Information("[Sirensong] Original Shadowflow cones retained without clones");
            }
        }
        if (shadowflowHadClones && shadowflowTimer != null && shadowflowTimer.IsRunning &&
            shadowflowTimer.ElapsedMilliseconds < ShadowflowDuration && shadowflowTimer.ElapsedMilliseconds >= 1000)
        {
            int phase = InferShadowflowPhase();
            if (phase != phaseCandidate) { phaseCandidate = phase; phaseCandidateSince = DateTime.UtcNow; }
            if (phase < 0) observedPhase = -1;
            else if ((DateTime.UtcNow-phaseCandidateSince).TotalMilliseconds >= 300) observedPhase = phase;
            if (observedPhase >= 0 && !phaseConesRegistered && shadowflowTimer.ElapsedMilliseconds <= 4000)
            {
                // The original native cones and clone avoids remain the
                // movement owner. Fifteen captured overlaps showed four
                // clone lanes matching the later living-party orientation;
                // party positions corroborate rather than become destinations.
                // The cast's boss heading was pi in the observed resolution
                // windows; offsets cancel its current rotation on discovery.
                var timer = shadowflowTimer;
                Vector3 location = TheGovernorNPC.Location;
                uint objectId = TheGovernorNPC.ObjectId;
                float bossDegrees = TheGovernorNPC.Heading * 180f / (float)Math.PI;
                for (int pIndex = 0; pIndex < 2; pIndex++)
                    for (int spoke = 0; spoke < 6; spoke++)
                    {
                        int selectedPhase = pIndex;
                        float rotation = pIndex * 30f + spoke * 60f - bossDegrees;
                        AvoidanceManager.AddAvoidUnitCone<GameObject>(
                            canRun: () => Live() && timer.IsRunning && timer.ElapsedMilliseconds < ShadowflowDuration && observedPhase == selectedPhase,
                            objectSelector: obj => obj.ObjectId == objectId,
                            leashPointProducer: () => location, leashRadius: 60f,
                            rotationDegrees: rotation, radius: 30.5f, arcDegrees: 50f);
                    }
                phaseConesRegistered = true;
                Logger.Information("[Sirensong] Shadowflow phase={0} elapsedMs={1}", observedPhase, timer.ElapsedMilliseconds);
            }
        }
    }

    // Fifteen recorded overlaps showed four clone lanes agreeing with the
    // later party-safe wedge in both orientations. Party positions corroborate
    // the phase; without clone consensus or with conflicting clues, stay unknown.
    private int InferShadowflowPhase()
    {
        int[] cloneVotes = new int[2], partyVotes = new int[2];
        void Vote(Vector3 location, int[] votes, float minimumRadius)
        {
            float radius = location.Distance2D(ArenaCenter.TheGovernor);
            if (radius < minimumRadius || radius > 18.5f) return;
            double degrees = Math.Atan2(location.X - ArenaCenter.TheGovernor.X, location.Z - ArenaCenter.TheGovernor.Z) * 180 / Math.PI;
            double mod = (degrees % 60 + 60) % 60;
            if (Math.Abs(mod - 30) <= 7) votes[0]++;
            else if (Math.Min(mod, 60 - mod) <= 7) votes[1]++;
        }
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible).ToArray();
        foreach (var a in actors.Where(a => a.NpcId == EnemyNpc.TheGroveller)) Vote(a.Location, cloneVotes, 5f);
        foreach (var a in actors.Where(a => a.ObjectId != Core.Me.ObjectId &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId)))) Vote(a.Location, partyVotes, 7.3f);
        int clone = cloneVotes[0] >= 4 && cloneVotes[1] == 0 ? 0 : cloneVotes[1] >= 4 && cloneVotes[0] == 0 ? 1 : -1;
        int party = partyVotes[0] >= 2 && partyVotes[1] == 0 ? 0 : partyVotes[1] >= 2 && partyVotes[0] == 0 ? 1 : -1;
        if (clone >= 0 && party >= 0 && clone != party) return -1;
        return clone;
    }

    // Two living allies can identify the alternate phase. Keep the native
    // cones until that phase and a reachable, checked path are established.
    private bool PositionForFirstShadowflow()
    {
        if (!Live() || shadowflowHadClones || shadowflowTimer == null ||
            shadowflowTimer.ElapsedMilliseconds >= ShadowflowDuration || inputOwner?.IsCurrent != true ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl || HasPlayerTether())
        {
            ReleaseFirstShadow();
            return false;
        }
        if (shadowflowTimer.ElapsedMilliseconds < 3000) return false;
        int[] votes = new int[2];
        foreach (var a in GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a =>
            a.IsValid && a.IsAlive && a.IsVisible && a.ObjectId != Core.Me.ObjectId &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))))
        {
            float r = a.Location.Distance2D(ArenaCenter.TheGovernor);
            if (r < 7.3f || r > 18.5f) continue;
            double deg = Math.Atan2(a.X-ArenaCenter.TheGovernor.X, a.Z-ArenaCenter.TheGovernor.Z) * 180 / Math.PI;
            double mod = (deg % 60 + 60) % 60;
            if (Math.Abs(mod-30) <= 7) votes[0]++;
            else if (Math.Min(mod, 60-mod) <= 7) votes[1]++;
        }
        int phase = votes[0] >= 2 && votes[1] == 0 ? 0 : votes[1] >= 2 && votes[0] == 0 ? 1 : -1;
        if (phase != firstPartyPhase) { firstPartyPhase = phase; firstPartySince = DateTime.UtcNow; }
        if (phase != 0 || (DateTime.UtcNow-firstPartySince).TotalMilliseconds < 300)
        {
            if (firstShadowOwned) ReleaseFirstShadow();
            return false;
        }
        var boss = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.TheGovernor)
            .FirstOrDefault(a => a.IsValid && a.IsAlive);
        if (boss == null) { ReleaseFirstShadow(); return false; }
        // The original 30-degree offsets correspond to phase 1 only when the
        // observed boss heading is within five degrees of the captured axis.
        double heading = (boss.Heading * 180 / Math.PI % 60 + 60) % 60;
        if (Math.Min(heading, 60-heading) > 5) { ReleaseFirstShadow(); return false; }
        Vector3 player = Core.Me.Location;
        bool afterActivation = shadowflowTimer.ElapsedMilliseconds >= 5500;
        if (firstShadowOwned && firstShadowDestination != null &&
            !FirstShadowSegmentSafe(player, firstShadowDestination.Value, afterActivation))
            ReleaseFirstShadow();
        if (!firstShadowOwned)
        {
            // First damage was observed about 6.1s after cast discovery. Use
            // an earlier 5.5s deadline and measured conservative walk speed.
            double remaining = 5.5 - shadowflowTimer.Elapsed.TotalSeconds;
            if (remaining <= .3) return false;
            var candidates = new List<Vector3> { player };
            for (int spoke = 0; spoke < 6; spoke++)
                foreach (float r in new[] { 7.5f, 9f, 12f, 15f, 18f })
                {
                    double angle = (30 + spoke*60) * Math.PI / 180;
                    candidates.Add(new Vector3(ArenaCenter.TheGovernor.X + r*(float)Math.Sin(angle), player.Y,
                        ArenaCenter.TheGovernor.Z + r*(float)Math.Cos(angle)));
                }
            var best = candidates.OrderBy(p => p.Distance2D(player)).Where(p =>
                p.Distance2D(ArenaCenter.TheGovernor) >= 7f && FirstShadowConeClearance(p) > 0 && p.Distance2D(player)/5.0 + .3 <= remaining &&
                FirstShadowSegmentSafe(player, p, false)).Select(p => (Vector3?)p).FirstOrDefault();
            if (best == null) return false;
            firstShadowDestination = best;
            firstShadowOwned = true;
            Logger.Information("[Sirensong] First Shadowflow phase correction phase=0 destination={0} elapsedMs={1}",
                best.Value, shadowflowTimer.ElapsedMilliseconds);
        }
        CapabilityManager.Update(firstShadowHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Shadowflow phase correction");
        CapabilityManager.Update(firstShadowHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Shadowflow phase correction");
        if (player.Distance2D(firstShadowDestination.Value) <= .5f && FirstShadowConeClearance(player) > 0)
        {
            inputOwner.Hold();
            return false;
        }
        inputOwner.Move(firstShadowDestination.Value);
        return true;
    }

    private double FirstShadowConeClearance(Vector3 point)
    {
        double r = point.Distance2D(ArenaCenter.TheGovernor);
        if (r < .01) return -180;
        double deg = Math.Atan2(point.X-ArenaCenter.TheGovernor.X, point.Z-ArenaCenter.TheGovernor.Z) * 180 / Math.PI;
        double mod = (deg % 60 + 60) % 60;
        return Math.Min(mod, 60-mod) - 25 - Math.Asin(Math.Min(1, .5/r))*180/Math.PI;
    }

    private bool FirstShadowSegmentSafe(Vector3 from, Vector3 to, bool checkCones)
    {
        var clones = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.TheGroveller)
            .Where(a => a.IsValid && a.IsVisible).Select(a => a.Location).ToArray();
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to)/.25f));
        double previousRadius = from.Distance2D(ArenaCenter.TheGovernor);
        double previousClearance = FirstShadowConeClearance(from);
        bool escapedCone = previousClearance > 0;
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i/steps;
            Vector3 p = new(from.X+(to.X-from.X)*t, from.Y, from.Z+(to.Z-from.Z)*t);
            double r = p.Distance2D(ArenaCenter.TheGovernor);
            // Allow only an outward prefix if already inside the extra margin.
            if (r < 6 || r >= 18.5 || previousRadius < 7 && r+.01 < previousRadius ||
                clones.Any(c => c.Distance2D(p) < 7f)) return false;
            if (checkCones)
            {
                double clearance = FirstShadowConeClearance(p);
                if (escapedCone && clearance <= 0 || !escapedCone && clearance+.05 < previousClearance) return false;
                if (clearance > 0) escapedCone = true;
                previousClearance = clearance;
            }
            previousRadius = r;
        }
        return !checkCones || escapedCone;
    }

    private void ReleaseFirstShadow()
    {
        if (firstShadowOwned)
        {
            inputOwner?.Release();
            CapabilityManager.Clear(firstShadowHandle, CapabilityFlags.Movement, "Shadowflow correction ended");
            CapabilityManager.Clear(firstShadowHandle, CapabilityFlags.GapCloser, "Shadowflow correction ended");
        }
        firstShadowOwned = false;
        firstShadowDestination = null;
    }

    // Morbid Advance/Retreat force approximately 18y of travel at 6y/s
    // (November 11, two Advance samples). Choose a staging point and facing
    // only when the entire 18.5y corridor stays within the referenced arena
    // inset and outside current puddles/avoids; never steer during forced input.
    private bool PositionForMarch()
    {
        DateTime now = DateTime.UtcNow;
        var boss = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.Lorelei)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.IsCasting && a.SpellCastInfo.IsValid &&
                a.CastingSpellId is EnemyAction.MorbidAdvance or EnemyAction.MorbidRetreat);
        if (boss != null)
        {
            if (marchAction != boss.CastingSpellId)
            {
                marchAction = boss.CastingSpellId;
                marchStage = null;
            }
            marchCastFinish = now + boss.SpellCastInfo.RemainingCastTime;
        }
        else if (MovementManager.GenerallyOutOfControl || now >= marchCastFinish.AddSeconds(1.5) ||
                 now < marchCastFinish.AddMilliseconds(-300))
        {
            ReleaseMarch("cast ended, interrupted, or forced input began");
            return false;
        }
        if (marchAction == 0 || inputOwner?.IsCurrent != true || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseMarch("native escape, forced input, or mover unavailable");
            return false;
        }
        bool Safe(Vector3 p) => p.Distance2D(ArenaCenter.Lorelei) < 15f &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));
        bool Segment(Vector3 a, Vector3 b)
        {
            int n = Math.Max(1, (int)Math.Ceiling(a.Distance2D(b) / .5f));
            for (int i = 0; i <= n; i++)
            {
                float t = (float)i / n;
                if (!Safe(new Vector3(a.X + (b.X-a.X)*t, a.Y, a.Z + (b.Z-a.Z)*t))) return false;
            }
            return true;
        }
        Vector3 player = Core.Me.Location;
        var stages = new List<Vector3> { player };
        for (float r = 2; r <= 12; r += 2)
            for (int i = 0; i < 24; i++)
            {
                double a = i * Math.PI / 12;
                stages.Add(new Vector3(ArenaCenter.Lorelei.X + r*(float)Math.Sin(a), player.Y,
                    ArenaCenter.Lorelei.Z + r*(float)Math.Cos(a)));
            }
        Vector3? chosen = marchStage;
        float chosenHeading = marchHeading;
        if (chosen != null)
        {
            double direction = chosenHeading - (marchAction == EnemyAction.MorbidRetreat ? Math.PI : 0);
            // Arrival tolerance is not the forced-march origin. Once stopped,
            // validate the corridor from the actual current position.
            Vector3 origin = player.Distance2D(chosen.Value) <= .5f ? player : chosen.Value;
            var end = new Vector3(origin.X + 18.5f*(float)Math.Sin(direction), origin.Y,
                origin.Z + 18.5f*(float)Math.Cos(direction));
            if (!Segment(player, chosen.Value) || !Segment(origin, end)) chosen = null;
        }
        if (chosen == null && now < nextMarchSearch) { ReleaseMarch("search deferred", preserveCast: true); return false; }
        if (chosen == null) nextMarchSearch = now.AddMilliseconds(250);
        foreach (Vector3 stage in chosen != null ? Enumerable.Empty<Vector3>() : (IEnumerable<Vector3>)stages.OrderBy(p => p.Distance2D(player)))
        {
            float distance = stage.Distance2D(player);
            if (distance > .5f && distance / 5.0 + .3 > (marchCastFinish-now).TotalSeconds || !Segment(player, stage)) continue;
            // Inside the arrival tolerance we will hold at the current position,
            // so the planned forced walk must start there as well.
            Vector3 origin = distance <= .5f ? player : stage;
            for (int i = 0; i < 48; i++)
            {
                double direction = i * Math.PI / 24;
                var end = new Vector3(origin.X + 18.5f*(float)Math.Sin(direction), origin.Y,
                    origin.Z + 18.5f*(float)Math.Cos(direction));
                if (!Segment(origin, end)) continue;
                chosen = stage;
                chosenHeading = (float)(direction + (marchAction == EnemyAction.MorbidRetreat ? Math.PI : 0));
                break;
            }
            if (chosen != null) break;
        }
        if (chosen == null)
        {
            // The player may be unable to reach a verified stage in time. Do
            // not turn them toward an unverified corridor or steal native escape.
            ReleaseMarch("no validated march corridor", preserveCast: true);
            return false;
        }
        CapabilityManager.Update(marchHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Morbid march staging");
        CapabilityManager.Update(marchHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Morbid march staging");
        if (!marchLeased) Logger.Information("[Sirensong] Morbid march stage={0} heading={1:F3} action={2}", chosen.Value, chosenHeading, marchAction);
        marchLeased = true;
        marchStage = chosen;
        marchHeading = chosenHeading;
        if (player.Distance2D(chosen.Value) > .5f)
        {
            CapabilityManager.Clear(marchHandle, CapabilityFlags.Facing, "Morbid march staging movement");
            inputOwner.Move(chosen.Value);
            return true;
        }
        inputOwner.Hold();
        CapabilityManager.Update(marchHandle, CapabilityFlags.Facing, TimeSpan.FromSeconds(1), "Morbid march facing");
        Core.Player.SetFacing(marchHeading);
        return false;
    }

    private void ReleaseMarch(string reason = "boss lifecycle", bool preserveCast = false)
    {
        if (marchLeased)
        {
            Logger.Information("[Sirensong] Morbid march released reason={0} castDeltaMs={1:F0} forced={2} nativeEscape={3}",
                reason, (DateTime.UtcNow-marchCastFinish).TotalMilliseconds,
                MovementManager.GenerallyOutOfControl, AvoidanceManager.IsRunningOutOfAvoid);
            if (!MovementManager.GenerallyOutOfControl) inputOwner?.Release();
            CapabilityManager.Clear(marchHandle, CapabilityFlags.Movement, "Morbid march ended");
            CapabilityManager.Clear(marchHandle, CapabilityFlags.GapCloser, "Morbid march ended");
            CapabilityManager.Clear(marchHandle, CapabilityFlags.Facing, "Morbid march ended");
        }
        marchLeased = false;
        if (!preserveCast)
        {
            marchAction = 0;
            marchCastFinish = DateTime.MinValue;
        }
        marchStage = null;
    }

    private bool HasPlayerTether() => Live() && WorldManager.SubZoneId == (uint)SubZoneId.GloweringKrautz &&
        GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.TheGovernor).Any(a =>
            a.IsValid && a.IsAlive && a.VfxContainer.IsValid &&
            (a.VfxContainer.Tethers?.Any(t => t.Id == 61 && t.TargetId == Core.Me.ObjectId) ?? false));

    private bool PositionForTether()
    {
        if (inputOwner?.IsCurrent != true || AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseStack();
            return false;
        }
        var boss = GameObjectManager.GetObjectsByNPCId<BattleCharacter>(EnemyNpc.TheGovernor)
            .FirstOrDefault(a => a.IsValid && a.IsAlive && a.VfxContainer.IsValid &&
                (a.VfxContainer.Tethers?.Any(t => t.Id == 61 && t.TargetId == Core.Me.ObjectId) ?? false));
        if (boss == null) { ReleaseStack(); return false; }

        // The November 11 trace shows tether 61 appearing after the forced pull,
        // then disappearing after reaching about 16.3y. Stage at 18y with a wall
        // inset and retain the request only until that actual tether disappears.
        bool Safe(Vector3 p) => p.Distance2D(ArenaCenter.TheGovernor) < 19f &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));
        bool Reachable(Vector3 p)
        {
            var from = Core.Me.Location;
            int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(p) / .5f));
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                if (!Safe(new Vector3(from.X + (p.X - from.X) * t, p.Y, from.Z + (p.Z - from.Z) * t))) return false;
            }
            return true;
        }
        var candidates = new List<Vector3>();
        for (int i = 0; i < 48; i++)
        {
            double angle = i * Math.PI / 24;
            candidates.Add(new Vector3(boss.X + 18f * (float)Math.Sin(angle), Core.Me.Y,
                boss.Z + 18f * (float)Math.Cos(angle)));
        }
        var best = candidates.Where(p => Safe(p) && Reachable(p))
            .OrderBy(p => p.Distance2D(Core.Me.Location)).Select(p => (Vector3?)p).FirstOrDefault();
        if (best == null) { ReleaseStack(); return false; }
        CapabilityManager.Update(tetherHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Enter Night tether");
        CapabilityManager.Update(tetherHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Enter Night tether");
        if (!stackLeased) Logger.Information("[Sirensong] Enter Night tether destination={0} player={1}", best.Value, Core.Me.Location);
        stackLeased = true;
        semanticKind = "tether";
        if (Core.Me.Location.Distance2D(best.Value) <= 1f) { inputOwner.Hold(); return false; }
        inputOwner.Move(best.Value);
        return true;
    }

    private bool PositionForStack()
    {
        if (stackTarget == 0 || DateTime.UtcNow >= stackUntil || inputOwner?.IsCurrent != true ||
            AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseStack();
            return false;
        }

        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible).ToArray();
        var marked = actors.FirstOrDefault(a => a.ObjectId == stackTarget);
        bool self = stackTarget == Core.Me.ObjectId;
        var participants = actors.Where(a => a.ObjectId != Core.Me.ObjectId &&
            (PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId) ||
             PartyMembers.AllPartyMemberIds.Contains((PartyMemberId)a.NpcId))).Select(a => a.Location).ToArray();
        if (self && participants.Length == 0 || !self && marked == null)
        {
            ReleaseStack();
            return false;
        }

        // Hydroball's reference radius is 5y. Allow half a yalm for clearance and
        // another half for arrival tolerance; a self-marked player must bring allies in.
        int Coverage(Vector3 p) => self ? participants.Count(a => a.Distance2D(p) <= 4f) :
            p.Distance2D(marked.Location) <= 4f ? 1 : 0;
        bool Safe(Vector3 p) => p.Distance2D(ArenaCenter.Lugat) < 19f &&
            !AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(p));
        bool Reachable(Vector3 p)
        {
            Vector3 from = Core.Me.Location;
            int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(p) / .5f));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                if (!Safe(new Vector3(from.X + (p.X - from.X) * t, p.Y, from.Z + (p.Z - from.Z) * t))) return false;
            }
            return true;
        }
        var candidates = new List<Vector3> { Core.Me.Location };
        foreach (var center in self ? participants : new[] { marked.Location })
        {
            candidates.Add(center);
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8;
                candidates.Add(new Vector3(center.X + 3.5f * (float)Math.Sin(angle), center.Y,
                    center.Z + 3.5f * (float)Math.Cos(angle)));
            }
        }
        var best = candidates.Where(p => Coverage(p) > 0 && Safe(p) && Reachable(p))
            .OrderByDescending(Coverage).ThenBy(p => p.Distance2D(Core.Me.Location))
            .Select(p => (Vector3?)p).FirstOrDefault();
        if (best == null) { ReleaseStack(); return false; }

        CapabilityManager.Update(stackHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Hydroball stack");
        CapabilityManager.Update(stackHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Hydroball stack");
        if (!stackLeased) Logger.Information("[Sirensong] Hydroball stack target={0:X} self={1} player={2} destination={3} coverage={4} allies={5}",
            stackTarget, self, Core.Me.Location, best.Value, Coverage(best.Value), string.Join(";", participants.Select(p => p.ToString())));
        stackLeased = true;
        semanticKind = "stack";
        if (Core.Me.Location.Distance2D(best.Value) <= 1f) { inputOwner.Hold(); return false; }
        inputOwner.Move(best.Value);
        return true;
    }

    private void ReleaseStack()
    {
        inputOwner?.Release();
        if (!stackLeased) return;
        Logger.Information("[Sirensong] {0} released at {1}", semanticKind, Core.Me?.Location);
        var handle = semanticKind == "tether" ? tetherHandle : stackHandle;
        CapabilityManager.Clear(handle, CapabilityFlags.Movement, "Sirensong mechanic released");
        CapabilityManager.Clear(handle, CapabilityFlags.GapCloser, "Sirensong mechanic released");
        stackLeased = false;
        semanticKind = null;
    }

    // One adapter observes native escape, semantic movement and later ordinary commands.
    // A newer command replaces the cleanup claim so an old stack cannot stop its successor.
    private sealed class InputOwner : IPlayerMover
    {
        private enum Command { None, Ordinary, Escape, Stack, Hold }
        private readonly IPlayerMover inner;
        private readonly SirensongSea dungeon;
        private bool detached, submitting;
        private Command command;
        internal InputOwner(SirensongSea dungeon, IPlayerMover inner) { this.dungeon = dungeon; this.inner = inner; }
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        internal bool HasNativeEscapePriority => IsCurrent && AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl;
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent) return;
            if (dungeon.firstShadowOwned && !submitting && !AvoidanceManager.IsRunningOutOfAvoid &&
                !dungeon.FirstShadowSegmentSafe(Core.Me.Location, location, true))
            {
                if (command == Command.Ordinary) { inner.MoveStop(); command = Command.None; }
                return;
            }
            command = AvoidanceManager.IsRunningOutOfAvoid ? Command.Escape : submitting ? Command.Stack : Command.Ordinary;
            inner.MoveTowards(location);
        }
        public void MoveStop() { if (IsCurrent) { command = Command.None; inner.MoveStop(); } }
        internal void Move(Vector3 destination)
        {
            if (!IsCurrent || AvoidanceManager.IsRunningOutOfAvoid) return;
            if (command != Command.Stack) Logger.Information("[Sirensong] Semantic movement requested from {0} to {1}", Core.Me.Location, destination);
            submitting = true;
            try { Navigator.MoveTo(new ff14bot.Pathing.MoveToParameters(destination) { DistanceTolerance = .15f, UseMount = false }); }
            finally { submitting = false; }
        }
        internal void Hold()
        {
            if (!IsCurrent || AvoidanceManager.IsRunningOutOfAvoid || command == Command.Hold) return;
            Logger.Information("[Sirensong] Semantic hold acquired at {0}", Core.Me.Location);
            MoveStop(); command = Command.Hold;
        }
        internal void Release()
        {
            if (!IsCurrent || command is not (Command.Stack or Command.Hold)) return;
            // A forced march owns input; retire our stale claim without issuing
            // a stop that could interrupt or compete with that movement.
            if (MovementManager.GenerallyOutOfControl || AvoidanceManager.IsRunningOutOfAvoid)
                command = Command.None;
            else MoveStop();
        }
        internal void CompleteEscape()
        {
            if (!IsCurrent || command != Command.Escape || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl || AvoidanceManager.Avoids.Any(a => !a.ShouldIgnore && a.IsPointInAvoid(Core.Me.Location))) return;
            MoveStop();
        }
        internal void Detach()
        {
            if (IsCurrent)
            {
                if ((command is Command.Escape or Command.Stack or Command.Hold) && !MovementManager.GenerallyOutOfControl) MoveStop();
                Navigator.PlayerMover = inner;
            }
            detached = true; command = Command.None;
        }
    }

    private static class EnemyNpc
    {
        /// <summary>
        /// First Boss: Lugat.
        /// </summary>
        public const uint Lugat = 6071;

        /// <summary>
        /// The Jane Guy casts the Bluefire puddle, the fire puddle is the remaining affect on the ground
        /// </summary>
        public const uint TheJaneGuy = 6070;

        /// <summary>
        /// Before second boss.
        /// </summary>
        public const uint BlueFirePuddle = 2007809;

        /// <summary>
        /// Second Boss: The Governor.
        /// </summary>
        public const uint TheGovernor = 6072;

        /// <summary>
        /// Second Boss: The Groveller.
        /// </summary>
        public const uint TheGroveller = 6073;

        /// <summary>
        /// Final Boss: Lorelei.
        /// </summary>
        public const uint Lorelei = 6074;

        /// <summary>
        /// Before Final boss.
        /// </summary>
        public const uint WaterPuddle = 2007808;
    }

    private static class ArenaCenter
    {
        /// <summary>
        /// First Boss: Lugat.
        /// </summary>
        public static readonly Vector3 Lugat = new(-1.791643f, -2.900793f, -215.6073f);

        /// <summary>
        /// Second Boss: The Governor.
        /// </summary>
        public static readonly Vector3 TheGovernor = new(-7.938193f, 4.440489f, 79.09968f);

        /// <summary>
        /// Third Boss: Lorelei.
        /// </summary>
        public static readonly Vector3 Lorelei = new(-44.54654f, 7.751197f, 465.0925f);
    }

    private static class EnemyAction
    {
        /// <summary>
        /// Lugat
        /// Hydroball
        /// Stack
        /// </summary>
        public const uint Hydroball = 8023;

        /// <summary>
        /// The Governor
        /// Shadowflow
        /// relative to boss facing, that should be cones aiming at 30, 90, 120, 210, 300 going clockwise
        /// </summary>
        public static readonly HashSet<uint> Shadowflow = [8030];

        /// <summary>
        /// Lorelei
        /// Morbid Retreat
        /// Move to NPCs so you hopefully don't walk through as much bad
        /// </summary>
        public const uint MorbidRetreat = 8038;

        /// <summary>
        /// Lorelei
        /// Morbid Advance
        /// Move to NPCs so you hopefully don't walk through as much bad
        /// </summary>
        public const uint MorbidAdvance = 8037;
    }
}
