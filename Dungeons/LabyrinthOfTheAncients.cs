using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Enums;
using ff14bot.Helpers;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing;
using ff14bot.Pathing.Avoidance;
using ff14bot.RemoteWindows;
using P = DutyMechanic.Dungeons.LabyrinthPlanner;
using V2 = System.Numerics.Vector2;
using V3 = System.Numerics.Vector3;

namespace DutyMechanic.Dungeons;
/// <summary>
/// Synced Labyrinth mechanics driven by the LabyrinthRun tag. The tag
/// owns activation, cleanup and travel; the routine retains job actions.
/// </summary>
public sealed class LabyrinthOfTheAncients : AbstractDungeon, IDisposable
{
    internal sealed class Session
    {
        internal int Alliance = -1;
        // Atomos lockout warp always lands in A, including B/C players. Keep this on the
        // duty session so death/tag recovery cannot turn the forced destination into identity.
        internal bool AtomosTransferSeen;
        internal bool MainTank, TankLead = true, Active = true, Completed;
        internal string RetryKind = "";
        internal bool Retry => RetryKind.Length != 0;

        internal string PadDuty = "Auto";
    }

    internal static Session Current;
    /// <summary>
    /// Creates fresh encounter assignments for the duty lifecycle. Older profiles may still
    /// call this with explicit assignments; normal XML needs no initialization chunk.
    /// This never starts travel, and native work remains gated to the intended director.
    /// </summary>
    /// <param name = "alliance">Auto for observed own-party consensus, or the HUD letter A, B or C.</param>
    /// <param name = "padDuty">Auto for role-based support, Hold for assigned support, or Attack.</param>
    /// <param name = "mainTank">Whether the tank is assigned to initiate shared boss combat.</param>
    /// <exception cref = "ArgumentException">An assignment is outside the supported settings.</exception>
    public static void BeginProfile(string alliance = "Auto", string padDuty = "Auto", bool mainTank = false)
    {
        if (alliance is not ("Auto" or "A" or "B" or "C"))
            throw new ArgumentException("Expected Auto/A/B/C.", nameof(alliance));
        if (padDuty is not ("Auto" or "Hold" or "Attack"))
            throw new ArgumentException("Expected Auto/Hold/Attack.", nameof(padDuty));
        EndProfile();
        Current = new Session
        {
            Alliance = alliance == "Auto" ? -1 : alliance[0] - 'A',
            PadDuty = padDuty,
            MainTank = mainTank
        };
        TreeRoot.OnStop += EndProfileOnStop;
    }

    /// <summary>
    /// Disarms XML-owned mechanics on explicit stop or profile return. This only releases this
    /// session; it never requests a duty exit, stops TreeRoot, or changes another plugin's hooks.
    /// </summary>
    public static void EndProfile()
    {
        if (Current != null)
            Current.Active = false;
        TreeRoot.OnStop -= EndProfileOnStop;
    }

    private static void EndProfileOnStop(BotBase bot) => EndProfile();
    /// <summary>
    /// Assigned alliance index A=0, B=1, C=2; -1 while unarmed or awaiting own-party consensus.
    /// An unknown assignment must not default to the center lane.
    /// </summary>
    public static int ProfileAlliance => Current?.Active == true ? Current.Alliance : -1;
    /// <summary>
    /// True while encounter mechanics own a positive position. XML suspends GrindSafe's
    /// hotspot traversal during this window; routine healing and eligible attacks still run.
    /// </summary>
    public static bool ProfileHolding => Current?.Active == true && Running?.active == true && Running.goal != null;

    // Travel follows actual own-party progress rather than a route cursor. Tanks can lead
    // nearby companions; followers need three allies ahead toward the current entrance.
    internal static bool CanApproachWithParty(Vector3 destination)
    {
        if (Running?.active != true)
            return false;
        return P.CanApproach(Xz(Core.Player.Location), Core.Player.Y, Xz(destination), Role(Core.Player.CurrentJob.ToString()), Core.Player.ObjectId, Running.party);
    }

    // Shared by travel and pad ownership: neither path may bypass the non-tank entry gate.
    internal static P.Goal AtomosEntryGoal() => P.AtomosEntry(Xz(Core.Player.Location), Core.Player.Y, Current.Alliance, Role(Core.Player.CurrentJob.ToString()), Core.Player.ObjectId, Running.party, Core.Player.InCombat || Running.actors.Any(a => a.Base == P.Atomos && a.Alive && a.InCombat && Current.Alliance >= 0 && P.Nearest(a.P, P.AtomosPads) == Current.Alliance));
    private Session session;
    private readonly P.FlareState flare = new();
    private bool linkedAtomosDead;
    // A follow episode ends on arrival, loss of its anchor, or encounter reset.
    private bool fireFollowing;
    private readonly LabyrinthRoutes.DepartureLane departureLane = new();
    private readonly Dictionary<uint, int> assignments = [];
    private readonly Dictionary<(uint, uint), P.Hazard> casts = [];
    // Bone Dragon 2026-10-09 02:29 UTC repeatedly failed the same raised-floor
    // destination while standing below it. Briefly exclude that local landing
    // area so the next native navigation attempt can choose another safe platform.
    private readonly Dictionary<V2, double> failedBoneLandings = [];
    private P.Hazard[] hazards = [];
    private P.Actor[] actors = [];
    private P.Ally[] party = [];
    private HashSet<uint> eligible = [];
    private P.Goal goal;
    private GrindArea authoredArea;
    private ITargetingProvider previousProvider;
    private RaidTargets provider;
    private bool previousLock, active, leased, moved, suppressed, sideStepWasEnabled, disposed, finalStaging;
    private uint ownedPoi;
    private V2 allianceFormationOrigin;
    private int phase = -1, lastPhase = -1, candidateAlliance = -1;
    private double now, allianceSince, meteorUntil, nextInteraction, lastProgress, nextRecovery, nextNavigation, lastNavigationAttempt, transportStarted, laneMismatchSince;
    private uint transportOwner;
    private V3 lastPosition;
    private string intent = "";
    private string cometDiscovery = "";
    private DateTime nextWarning, nextSealedAreaAction, nextReadyCheckAction;
    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)174;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    private static DateTime nextCompletedReturn;
    // Platypus's death wait does not observe duty completion. With a completed native
    // director and the final objective cleared, a dead player can accept the standard
    // Return confirmation without abandoning any live encounter. This bot-thread pulse
    // is needed even when that wait prevents LabyrinthRun from being started after reload.
    internal static void RecoverCompletedDuty()
    {
        // This plugin pulse also runs in other duties whose profiles have no GrindAreas.
        // Reject those maps before inspecting Labyrinth's marker; missing profile data
        // must never throw during another duty's normal death/raise recovery.
        if (!TreeRoot.IsRunning || CommonBehaviors.IsLoading || Core.Player == null || Core.Player.IsAlive || WorldManager.ZoneId != 174 || Core.Player.HasAura(148) || DateTime.UtcNow < nextCompletedReturn || NeoProfileManager.CurrentProfile?.GrindAreas?.Any(a => a.Name == "Phlegethon") != true)
            return;
        var d = Director();
        if (d == null || ClientGameUiRevive.ReviveState != ReviveState.Dead || !SelectYesno.IsOpen)
            return;
        bool completed = d.InstanceEnded && d.GetTodoArgs(7).Item1 == 1;
        // Captures remained dead after a cleared Behemoth (subzone869/todo6, Oct8)
        // and Fire (subzone868/todo5, Oct9 03:34 UTC). Return stays inside the public
        // duty so normal travel/shortcut recovery can continue after the group departs.
        // An absent director, an active enemy or a pending Raise never authorizes it.
        int clearedWingSlot = WorldManager.SubZoneId switch
        {
            868 => 5,
            869 => 6,
            _ => -1
        };
        bool clearedWing = clearedWingSlot >= 0 && d.GetTodoArgs(clearedWingSlot).Item1 == 1 && !GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(a => a.IsValid && a.IsAlive && a.CanAttack && a.InCombat);
        if (!completed && !clearedWing)
            return;
        nextCompletedReturn = DateTime.UtcNow.AddSeconds(5);
        Logger.Information(completed ? "[Labyrinth] Verified completed duty while dead; accepting Return to release the raise wait." : $"[Labyrinth] Verified cleared {(clearedWingSlot == 5 ? "Walk of Fire" : "Behemoth")} arena while dead; accepting Return to continue within this duty.");
        SelectYesno.Yes();
    }

    internal static InstanceContentDirector Director()
    {
        if (CommonBehaviors.IsLoading || WorldManager.ZoneId != 174 || !DutyManager.InInstance)
            return null;
        var d = DirectorManager.ActiveDirector as InstanceContentDirector;
        // DungeonId is InstanceContent 30001; Duty Finder uses the different key 92.
        return d != null && d.IsValid && d.DungeonId == 30001 ? d : null;
    }

    // Register this instance without clearing avoidance owned by other plugins.
    internal Task<bool> InitializeForTag() => EnterDungeonAsync();
    // Tag release must not call AbstractDungeon's global RemoveAllAvoids lifecycle.
    // Existing registrations become inactive with this controller, preserving other owners.
    internal void ReleaseFromTag(bool nativeCleanup)
    {
        if (nativeCleanup)
            Deactivate();
        Dispose();
    }

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        TreeRoot.OnStop += OnStop;
        AvoidanceManager.AddAvoidPolygon<P.Hazard>(condition: () => active && suppressed, leashPointProducer: () => Core.Player.Location, leashRadius: 120, rotationProducer: _ => 0, scaleProducer: _ => 1, heightProducer: _ => 10, pointsProducer: h => h.Polygon().Select(p => new Vector2(p.X - h.Origin.X, p.Y - h.Origin.Y)).ToArray(), locationProducer: h => new Vector3(h.Origin.X, Core.Player.Y, h.Origin.Y), collectionProducer: () => hazards, priority: AvoidancePriority.High);
        // Walk of Fire retains SideStep, so the suppressed-only helper polygon hook
        // is inactive there. Register the floor separately to constrain routine dodges too.
        AvoidanceHelpers.AddAvoidDonut(() => active && P.FireBoundaryActive(phase, Core.Player.InCombat, Xz(Core.Player.Location)), () => new Vector3(P.FireCenter.X, Core.Player.Y, P.FireCenter.Y), 65, P.FireFloorRadius);
        AvoidanceHelpers.AddAvoidDonut(() => active && phase == 5 && Core.Player.InCombat, () => new Vector3(-110, 68.16f, -368.35f), 100, 29);
        AvoidanceHelpers.AddAvoidDonut(() => active && phase == 1 && Core.Player.InCombat, () => new Vector3(-451.2f, 25.5f, 23.93f), 100, 48.9);
        // The final apron contains the alliance pads. A central 32-yalm boundary would
        // actively block the required Flare escape; retain the outer footprint instead.
        AvoidanceHelpers.AddAvoidDonut(() => active && phase == 6 && Core.Player.InCombat && Core.Player.Y > 600, () => new Vector3(-110, 650.3f, 181.6f), 100, 44);
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        EndProfile();
        ReleaseMovement();
        Dispose();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        if (disposed)
            return Task.FromResult(false);
        // A loaded Labyrinth target catalog identifies the authored raid profile. Keep this
        // automatic lifecycle scoped to it so merely enabling the plugin during manual play
        // cannot seize targeting. Defaults resolve alliance/role afresh on each duty entry.
        if (Current == null || !Current.Active)
        {
            if (Director() == null || NeoProfileManager.CurrentProfile?.GrindAreas.Any(a => a.Name == "Phlegethon") != true)
            {
                Deactivate();
                return Task.FromResult(false);
            }

            BeginProfile();
        }

        if (session != Current)
        {
            Deactivate();
            session = Current;
            lastPhase = -1;
            assignments.Clear();
        }

        if (CommonBehaviors.IsLoading || QuestLogManager.InCutscene || Core.Player == null || !Core.Player.IsAlive)
        {
            ResetTransient();
            return Task.FromResult(false);
        }

        var director = Director();
        if (director == null)
        {
            ResetTransient();
            Warn("Waiting for the Labyrinth director; no checkpoint assumed.");
            return Task.FromResult(false);
        }

        if (AcceptSealedArea())
            return Task.FromResult(true);
        AcceptReadyCheck();
        phase = P.Phase((int)director.GetUI8A);
        if (phase < 0)
        {
            ResetTransient();
            Warn($"Unrecognized Labyrinth checkpoint {director.GetUI8A}; reacquiring state.");
            return Task.FromResult(false);
        }

        if (phase == 7 && director.InstanceEnded)
        {
            session.Completed = true;
            Deactivate();
            return Task.FromResult(false);
        }

        active = true;
        Running = this;
        now = DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        if (phase != lastPhase)
        {
            ResetTransient();
            active = true;
            assignments.Clear();
            lastPhase = phase;
            lastProgress = now;
            Logger.Information($"[Labyrinth] Objective phase {phase}; UI8A={director.GetUI8A}.");
        }

        InstallProvider();
        Snapshot();
        ResolveAlliance();
        SetSideStep(phase is 1 or 2 or 3 or 5 or 6);
        PlanMechanics();
        UpdateTargets();
        if (goal != null)
        {
            ff14bot.NeoProfiles.Tags.LabyrinthRun.YieldTravelToMechanic();
            return Task.FromResult(Hold(goal));
        }

        // Releasing an absent hold stopped profile-owned travel on every TreeStart pulse.
        // Only release a real semantic lease; ordinary travel keeps its movement ownership.
        if (leased)
            ReleaseMovement();
        return Task.FromResult(false);
    }

    // Reuse the verified alliance lockout notification contract. Its presence scopes Yes to
    // the sealed-area transfer instead of accepting arbitrary dialogs. Poll after the callback
    // rather than waiting indefinitely for a load that the server may decline or delay.
    private bool AcceptSealedArea()
    {
        if (!LlamaLibrary.RemoteWindows.NotificationIcLockoutWar.Instance.IsOpen || transportOwner != 0 || ff14bot.NeoProfiles.Tags.LabyrinthRun.OwnsShortcutConfirmation)
            return false;
        if (DateTime.UtcNow < nextSealedAreaAction)
            return true;
        var window = RaptureAtkUnitManager.GetWindowByName("_Notification");
        if (window == null)
            return false;
        ReleaseMovement();
        nextSealedAreaAction = DateTime.UtcNow.AddSeconds(2);
        if (SelectYesno.IsOpen)
        {
            if (P.Phase((int)(Director()?.GetUI8A ?? 255)) == 2)
                session.AtomosTransferSeen = true;
            Logger.Information("[Labyrinth] Accepting sealed-area transfer.");
            SelectYesno.Yes();
        }
        else
        {
            // Existing Syrcus Tower notification callback: open the lockout-warp confirmation.
            window.SendAction(2, 3, 0, 3, 0xA);
        }

        return true;
    }

    // Use the existing alliance-raid notification + SelectYesno contract, only after this
    // profile's live duty/alive guards. Do not consume the tick: pad staging and Flare must
    // remain schedulable even if a ready-check notification lingers after the response.
    private void AcceptReadyCheck()
    {
        if (transportOwner != 0 || ff14bot.NeoProfiles.Tags.LabyrinthRun.OwnsShortcutConfirmation || LlamaLibrary.RemoteWindows.NotificationIcLockoutWar.Instance.IsOpen || !LlamaLibrary.RemoteWindows.NotificationReadyCheck.Instance.IsOpen || !SelectYesno.IsOpen || DateTime.UtcNow < nextReadyCheckAction)
            return;
        nextReadyCheckAction = DateTime.UtcNow.AddSeconds(2);
        Logger.Information("[Labyrinth] Accepting ready check.");
        SelectYesno.Yes();
    }

    private void Snapshot()
    {
        var all = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        var roster = PartyManager.AllMembers.ToDictionary(a => a.ObjectId, a => a.Class.ToString());
        party = all.Where(a => roster.ContainsKey(a.ObjectId)).Select(a => new P.Ally(a.ObjectId, Xz(a.Location), a.Y, Role(roster[a.ObjectId]), a.IsAlive))// GetObjectsOfType omits the local player in the live host. Without this row
        // PadSupport never selected our SCH, allowing routine chase off the four-person
        // Atomos platform. Snapshot self explicitly on this bot thread; deduplicate hosts
        // that do include self so formation votes and role allocation stay consistent.
        .Append(new P.Ally(Core.Player.ObjectId, Xz(Core.Player.Location), Core.Player.Y, Role(Core.Player.CurrentJob.ToString()), Core.Player.IsAlive)).DistinctBy(a => a.Id).ToArray();
        actors = all.Where(a => a.IsNpc && Math.Abs(a.Y - Core.Player.Y) < 20 && a.Location.Distance2D(Core.Player.Location) < 125).Select(a =>
        {
            var s = a.IsCasting ? a.SpellCastInfo : null;
            bool casting = s != null && s.IsValid;
            return new P.Actor(a.ObjectId, a.BaseId, Xz(a.Location), a.Y, a.Heading, a.CombatReach, a.IsAlive, a.IsTargetable && a.CanAttack, a.CurrentTargetId, casting ? s.ActionId : 0, casting ? now + s.RemainingCastTime.TotalSeconds : 0, casting ? Xz(s.CastLocation) : default, a.InCombat);
        }).ToArray();
        // Capture filter inputs on the bot thread during meteor only. A missing planner
        // candidate must be distinguishable from a live comet excluded by NPC/elevation/range.
        // Retain detached text, never BattleCharacter wrappers across native frames.
        cometDiscovery = phase == 5 && actors.Any(a => a.Base == P.Behemoth && a.Action == 1756) ? string.Join("; ", all.Where(a => a.BaseId == P.Comet).Take(12).Select(a => $"{a.ObjectId:X} npc={a.IsNpc} visible={a.IsVisible} alive={a.IsAlive} pos={a.Location} dy={Math.Abs(a.Y - Core.Player.Y):F2} range={a.Location.Distance2D(Core.Player.Location):F2}")) : "";
        foreach (var a in actors)
        {
            // Cache assignment at first sight. A claw chasing its victim across the arena must
            // not change alliance merely because it later becomes closer to another pad.
            if (!assignments.ContainsKey(a.Id) && a.Base is P.Claw or P.FinalGiant or P.Vassago or 2408 or 2409)
            {
                assignments[a.Id] = P.Nearest(a.P, a.Base is P.Claw or P.FinalGiant ? P.FinalPads : P.FireSectors);
                // Log once at attribution so routine off-POI casts can be checked against
                // the original lane even after a claw follows its victim across the arena.
                if (a.Base is P.Claw or P.FinalGiant)
                    Logger.Information($"[Labyrinth] Alliance actor: object={a.Id:X} base={a.Base} lane={assignments[a.Id]} own={session.Alliance} position={a.P} target={a.Target:X}.");
            }

            var h = P.Cast(a);
            if (h != null)
                casts[(a.Id, a.Action)] = h;
        }

        foreach (var entry in casts.ToArray())
            if (now > entry.Value.Finish + .3 || !actors.Any(a => a.Id == entry.Key.Item1 && (a.Action == entry.Key.Item2 || now >= entry.Value.Finish - .2)))
                casts.Remove(entry.Key);
        hazards = casts.Values.OrderBy(h => h.Finish).ToArray();
        // Instant cleaves have no fabricated countdown. A persistent facing footprint protects
        // non-targets; the actual tank is permitted to hold aggro without dodging its own frontal.
        hazards = hazards.Concat(actors.Where(a => a.Alive && a.Target != Core.Player.ObjectId && a.Target != 0 && (a.Base == P.Bone || a.Base is 2357 or P.FinalGiant)).Select(a => new P.Hazard(a.Id, 0, a.P, a.Heading, double.PositiveInfinity, P.Shape.Cone, a.Base == P.Bone ? 13.5f : 15.5f, 0, a.Base == P.Bone ? MathF.PI / 4 : MathF.PI / 3))).ToArray();
        // Keep avoidance and semantic goals on the same floor; release the boundary
        // after the native clear so it cannot block the north exit or pre-pull travel.
        if (P.FireBoundaryActive(phase, Core.Player.InCombat, Xz(Core.Player.Location)))
            hazards = [..hazards, ..P.FireBoundary];
        if (phase == 6 && Core.Player.Y > 600 && Core.Player.InCombat)
            hazards = [..hazards, P.FinalNorthBoundary];
    }

    private void ResolveAlliance()
    {
        if (session.Alliance >= 0 || phase is not (2 or 4 or 6))
            return;
        // Chris confirmed the warp always uses A. Preserve a known assignment; otherwise
        // reacquire from a later encounter's formation, never from the forced Atomos lane.
        if (phase == 2 && session.AtomosTransferSeen)
            return;
        int inferred = P.InferAlliance(party.Where(a => a.Id != Core.Player.ObjectId).ToArray(), phase);
        // The final entrance passes through B's vicinity. A moving group there previously
        // misidentified HUD-confirmed C. Require a settled pad formation before assigning it.
        var formation = party.Where(a => a.Alive && a.Id != Core.Player.ObjectId).ToArray();
        var center = formation.Length == 0 ? V2.Zero : formation.Aggregate(V2.Zero, (sum, a) => sum + a.P) / formation.Length;
        if (inferred != candidateAlliance || phase == 6 && V2.Distance(center, allianceFormationOrigin) > 1)
        {
            candidateAlliance = inferred;
            allianceSince = now;
            allianceFormationOrigin = center;
        }

        if (inferred >= 0 && now - allianceSince >= (phase == 6 ? 3 : 1))
        {
            session.Alliance = inferred;
            Logger.Information($"[Labyrinth] Own-party formation resolved alliance {(char)('A' + inferred)}; three-member stable consensus.");
        }
    }

    private void PlanMechanics()
    {
        goal = null;
        finalStaging = false;
        var player = Xz(Core.Player.Location);
        int alliance = session.Alliance;
        if (P.HoldOpeningPlatform(phase, player, Core.Player.InCombat, Role(Core.Player.CurrentJob.ToString()), actors))
        {
            // Ranged rotation otherwise stops on the low floor as soon as targets enter range.
            // The shared hold preserves emergency avoidance and yields rotation after arrival.
            goal = new(P.OpeningPlatform, 42.4f, .35f, "Opening Pools raised platform");
            return;
        }

        if (P.HoldDiraPlatform(phase, player, Core.Player.InCombat, Role(Core.Player.CurrentJob.ToString()), actors))
        {
            // The last Pools pack has its own raised support position. Retain the hold
            // through surviving eyes, and let existing Hold escape live telegraphs first.
            goal = new(P.DiraPlatform, 42.4f, .35f, "Dira raised platform");
            return;
        }

        if (P.HoldGreaterDemonPlatform(phase, player, Core.Player.InCombat, Role(Core.Player.CurrentJob.ToString()), actors))
        {
            // Chris confirmed the Greater Demon room. Retain the captured raised point
            // while this pack lives; Hold preserves RB escape priority and routine healing.
            // Existing goal-range targeting prevents chasing enemies off the platform.
            goal = new(P.GreaterDemonPlatform, 42.4f, .35f, "Greater Demon raised platform");
            return;
        }

        // The sealed transfer physically strands B/C in A. Handle local support before
        // the general lane-mismatch hold, preserving alliance identity for later fights.
        // The existing phase/position guard releases this fallback at the native clear.
        if (P.SupportForcedLanding(phase, session.AtomosTransferSeen, alliance, player, Core.Player.Y))
        {
            goal = SafeGoal(P.AtomosPads[0], 51.05f, 2.3f, "Atomos: forced-landing local support");
            return;
        }

        if (phase == 2 && alliance >= 0 && Core.Player.X > 191 && Core.Player.X < 268 && Math.Abs(Core.Player.Z - P.AtomosPads[alliance].Y) > 13)
        {
            // Disconnected lanes cannot be repaired by a cross-lane MoveTo. Preserve healing
            // while reacquiring the state, then request the profile's same-route recovery.
            if (laneMismatchSince == 0)
                laneMismatchSince = now;
            goal = new(player, Core.Player.Y, .35f, "Atomos: wrong reachable lane; reacquiring assignment");
            if (now - laneMismatchSince > 120 && !Core.Player.InCombat && !session.Retry)
                session.RetryKind = "Lane";
            return;
        }

        laneMismatchSince = 0;
        if (phase == 6 && Core.Player.Y > 600)
        {
            var boss = actors.FirstOrDefault(a => a.Base == P.Phlegethon);
            var giants = actors.Where(a => a.Base == P.FinalGiant && assignments.GetValueOrDefault(a.Id, -1) == alliance).ToArray();
            flare.Update(now, boss, giants);
            if (flare.Holding)
            {
                if (alliance < 0)
                {
                    Warn("Flare needs an alliance assignment; waiting for own-party pad consensus.");
                    return;
                }

                var pad = P.FinalPads[alliance];
                if (Role(Core.Player.CurrentJob.ToString()) == P.Role.Tank && giants.Any(a => a.Alive && a.Target == Core.Player.ObjectId))
                {
                    // The assigned giant spawns on the pad. Tank its inner edge while remaining
                    // inside the pad, so its frontal points away from the outer support stack.
                    var inner = pad - V2.Normalize(pad - P.FinalCenter) * 2.3f;
                    goal = SafeGoal(inner, 650.3f, .2f, "Own Iron Giant tank position", true);
                }
                else
                    goal = SafeGoal(pad, 650.3f, 2.5f, "Ancient Flare / own Iron Giant", true);
                return;
            }

            finalStaging = P.FinalStaging(boss, Core.Player.InCombat);
            if (finalStaging)
            {
                // All roles assemble before another player pulls. Suppress target eligibility as
                // well as travel: a party member selecting Phlegethon is not permission to engage.
                // Unknown alliance holds locally until own-party consensus supplies the right pad.
                goal = alliance >= 0 ? SafeGoal(P.FinalPads[alliance], 650.3f, 2.5f, "Phlegethon: assemble on own starting pad") : new(player, Core.Player.Y, .35f, "Phlegethon: awaiting own-party starting-pad assignment");
                return;
            }
        }
        else
            flare.Reset();
        if (phase == 5)
        {
            var boss = actors.FirstOrDefault(a => a.Base == P.Behemoth && a.Alive);
            // The Oct 8 death was observed ~2s after cast end, after the old .8s hold
            // had released. Keep shelter for 3s through animation/network resolution;
            // this does not suppress healing or claim the death's exact damage source.
            if (boss != null && boss.Action == 1756)
                meteorUntil = boss.Finish + 3;
            if (boss == null)
                meteorUntil = 0;
            if (boss != null && now < meteorUntil)
            {
                // Apply the same overlap policy to native avoidance and shelter selection.
                // Filtering only destination choice still lets the independent giant cone
                // eject us at impact (observed 01:12:42). Snapshot restores it after Meteor.
                hazards = P.MeteorHazards(hazards, actors);
                var shelter = P.Shelter(player, boss, actors, hazards);
                if (shelter.HasValue)
                    goal = new(shelter.Value, 68.16f, .35f, "Ecliptic Meteor shelter", true);
                else
                {
                    goal = new(player, Core.Player.Y, .35f, "Meteor: no surviving valid comet", true);
                    Warn("Meteor has no valid comet shelter. " + P.ShelterEvidence(boss, actors, hazards) + $" discovery=[{cometDiscovery}]");
                }

                return;
            }
        }

        if (phase == 2 && alliance >= 0 && Core.Player.X > 190 && Core.Player.X < 270)
        {
            bool support = session.PadDuty == "Hold" || session.PadDuty == "Auto" && P.PadSupport(Core.Player.ObjectId, party);
            // Corpse despawn caused a needless return to the pad after an observed linked
            // kill at 01:07:23. Preserve that observation, but reset on live reappearance
            // or the normal death/loading/phase lifecycle before assuming another clear.
            bool linkedAlive = P.LinkedAtomosAlive(actors, alliance, linkedAtomosDead);
            linkedAtomosDead = !linkedAlive;
            // Keep supporting the next lane until its death is observed or the director advances.
            if (support && linkedAlive)
            {
                var entry = AtomosEntryGoal();
                goal = SafeGoal(entry.P, entry.Y, entry.Tolerance, entry.Reason);
                return;
            }

            if (Role(Core.Player.CurrentJob.ToString()) == P.Role.Tank && actors.Any(a => a.Alive && a.Base is >= 2404 and <= 2406 && a.Target == Core.Player.ObjectId))
            {
                goal = SafeGoal(P.AtomosPads[alliance] + new V2(8, 0), 51.05f, 2, "Atomos: gather adds near pad support");
                return;
            }
        }

        if (phase == 1 && Core.Player.InCombat && Vector2Distance(player, P.BoneCenter) < 50)
        {
            // Positive platform staging keeps routine chasing out of poison even when the floor
            // animation cannot be read. Avoidance remains the owner of timed cleave escape.
            var options = new List<V2>();
            for (int x = -7; x <= 7; x++)
                for (int z = -7; z <= 7; z++)
                {
                    var p = P.BoneCenter + new V2(x, z);
                    if (P.OnBonePlatform(p))
                        options.Add(p);
                }

            for (int i = 0; i < 8; i++)
                foreach (float r in new[]
                {
                    18.02f,
                    32.13f,
                    45.745f
                }

                )
                {
                    var center = P.BoneCenter + new V2(MathF.Sin(i * MathF.PI / 4), MathF.Cos(i * MathF.PI / 4)) * r;
                    options.Add(center);
                }

            var target = actors.Where(a => a.Alive && a.Attackable && a.Base is P.Bone or 2349 or 2434).OrderBy(a => V2.DistanceSquared(a.P, player)).FirstOrDefault();
            bool melee = Role(Core.Player.CurrentJob.ToString())is P.Role.Tank or P.Role.Melee;
            bool InRange(V2 p) => target == null || V2.Distance(p, target.P) < target.Radius + (melee ? 2.5f : 24);
            foreach (var p in failedBoneLandings.Where(e => e.Value <= now).Select(e => e.Key).ToArray())
                failedBoneLandings.Remove(p);
            // The three-yalm neighborhood covers adjacent samples on the same small
            // platform, avoiding a retry loop that merely nudges the failed endpoint.
            // Never relax floor or hazard checks when choosing the alternate landing.
            var safe = options.Where(p => !hazards.Any(h => h.Contains(p)) && !failedBoneLandings.Keys.Any(f => V2.DistanceSquared(f, p) < 9)).OrderBy(p => InRange(p) ? 0 : 1).ThenBy(p => V2.DistanceSquared(p, player)).ToArray();
            if (P.OnBonePlatform(player) && !hazards.Any(h => h.Contains(player)) && InRange(player))
                goal = new(player, Core.Player.Y, .5f, "Bone Dragon safe platform");
            else if (safe.Length > 0)
                goal = new(safe[0], 25.5f, .5f, "Bone Dragon safe platform");
        }

        if (phase == 4 && alliance >= 0 && Core.Player.InCombat && Role(Core.Player.CurrentJob.ToString()) != P.Role.Tank)
        {
            var allies = party.Where(a => a.Alive && P.Nearest(a.P, P.FireSectors) == alliance && V2.Distance(a.P, P.FireSectors[alliance]) < 20).ToArray();
            var anchor = allies.OrderBy(a => a.Role == P.Role.Tank ? 0 : 1).FirstOrDefault();
            fireFollowing = anchor != null && P.ContinueFireAssist(V2.Distance(player, anchor.P), fireFollowing);
            if (fireFollowing)
                goal = SafeGoal(anchor.P, anchor.Y, 5, "Walk of Fire own-party assist");
        }
        else
            fireFollowing = false;
        // Recover every role from lava even when already close to its own tank.
        // Once inside, release this goal so tank/melee engagement remains normal.
        if (P.FireBoundaryActive(phase, Core.Player.InCombat, player) && V2.Distance(player, P.FireCenter) >= P.FireFloorRadius)
            goal = SafeGoal(P.FireRecovery(player), Core.Player.Y, .5f, "Walk of Fire: return from lava", true);
        // Oct8 C/SCH waited at X410 after combat began: travel yielded to combat but
        // boss immunity supplied no Kill POI until adds spawned. Enter support range
        // behind an established own-party group independently of attack eligibility.
        // Stop this entry-only assist inside the arena so add pursuit remains OrderBot's.
        if (phase == 3 && player.X < 425 && Role(Core.Player.CurrentJob.ToString()) != P.Role.Tank)
        {
            var entered = party.Where(a => a.Alive && a.Id != Core.Player.ObjectId && a.P.X > 425 && Math.Abs(a.Y - Core.Player.Y) < 8 && V2.Distance(a.P, new V2(440.4f, 280)) < 30).ToArray();
            if (entered.Length >= 3)
            {
                var anchor = entered.OrderBy(a => V2.DistanceSquared(a.P, player)).First();
                goal = SafeGoal(anchor.P, anchor.Y, 2, "Thanatos: entering with own party");
            }
        }
    }

    private P.Goal SafeGoal(V2 center, float y, float radius, string reason, bool urgent = false)
    {
        var player = Xz(Core.Player.Location);
        var points = new List<V2>
        {
            center
        };
        if (V2.Distance(player, center) < radius)
            points.Insert(0, player);
        for (float r = .5f; r <= radius; r += .5f)
            for (int i = 0; i < 24; i++)
                points.Add(center + new V2(MathF.Sin(i * MathF.Tau / 24), MathF.Cos(i * MathF.Tau / 24)) * r);
        var safe = points.Where(p => !hazards.Any(h => h.Contains(p))).OrderBy(p => V2.DistanceSquared(p, player)).ToArray();
        // If no pad point satisfies earlier damage, RB escapes first; the single semantic goal
        // remains pending. Independent add handlers never overwrite or release this hold.
        return safe.Length == 0 ? null : new(safe[0], y, .35f, reason, urgent);
    }

    private void UpdateTargets()
    {
        string areaName = phase switch
        {
            0 => "Pools of Oblivion",
            1 => "Bone Dragon",
            2 => "Atomos",
            3 => "Thanatos",
            4 => "Walk of Fire",
            5 => "King Behemoth",
            6 => "Phlegethon",
            _ => ""
        };
        // Read the normal XML target catalog without publishing its hotspots as a global grind
        // route: unguarded hotspot traversal would defeat the party tether this profile needs.
        authoredArea = NeoProfileManager.CurrentProfile?.GrindAreas.FirstOrDefault(a => a.Name == areaName);
        var me = Core.Player;
        bool tank = Role(me.CurrentJob.ToString()) == P.Role.Tank;
        bool ready = party.Count(a => a.Alive && V2.Distance(a.P, Xz(me.Location)) < 30) >= 4 && party.Any(a => a.Alive && a.Role == P.Role.Healer && V2.Distance(a.P, Xz(me.Location)) < 30);
        var partyIds = party.Select(a => a.Id).ToHashSet();
        var ownParty = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive && partyIds.Contains(a.ObjectId) && a.ObjectId != me.ObjectId).ToArray();
        var assisted = ownParty.Select(a => a.CurrentTargetId).ToHashSet();
        // A party member selecting Valefor caused an observed SCH pre-pull on Oct8.
        // Selection is not engagement: non-tanks require another nearby own-party
        // member in combat AND an already-engaged enemy. Excluding self prevents
        // our own accidental pull from satisfying the group-start condition.
        bool groupInCombat = ownParty.Any(a => a.InCombat && a.Location.Distance2D(me.Location) < 50 && Math.Abs(a.Y - me.Y) < 8);
        bool astral = me.HasAura(398); // Captured player aura, not an alliance timer.
        eligible = actors.Where(a => !finalStaging && P.Eligible(a, phase, session.Alliance, astral, flare.Holding, assignments)).Where(a => tank || groupInCombat && a.InCombat).Where(a => !flare.Holding || goal != null).Where(a => a.Base != P.Bomb || !actors.Any(v => v.Base == P.Vassago && v.Alive))// Another alliance engaging an enemy is not permission for a healer to run ahead.
        // Assist an own-party target/threat, or join an engaged fight with three allies near it.
        .Where(a => partyIds.Contains(a.Target) || assisted.Contains(a.Id) || GameObjectManager.GetObjectByObjectId(a.Id)is BattleCharacter b && b.InCombat && party.Count(p => p.Alive && V2.Distance(p.P, a.P) < 30 && Math.Abs(p.Y - a.Y) < 8) >= 3 || tank && ready && (session.MainTank || a.Base is not (P.Bone or P.Behemoth or P.Phlegethon))).Where(a => goal == null || V2.Distance(a.P, goal.P) <= (tank || Role(me.CurrentJob.ToString()) == P.Role.Melee ? 3 : 24) + a.Radius).Where(a => V2.Distance(a.P, Xz(me.Location)) < 65).Select(a => a.Id).ToHashSet();
        var targets = provider.GetObjectsByWeight();
        if (Poi.Current.Type == PoiType.Kill && Poi.Current.Unit is BattleCharacter current && !eligible.Contains(current.ObjectId))
            Poi.Clear("Labyrinth alliance/mechanic target restriction");
        if (me.CurrentTarget is BattleCharacter target && target.CanAttack && !eligible.Contains(target.ObjectId))
            me.ClearTarget();
        var preferred = targets.FirstOrDefault();
        if (preferred != null && (Poi.Current.Type != PoiType.Kill || Poi.Current.Unit?.ObjectId != preferred.ObjectId))
        {
            preferred.Target();
            Poi.Current = new Poi(preferred, PoiType.Kill);
            ownedPoi = preferred.ObjectId;
        }
    }

    private bool Hold(P.Goal hold)
    {
        SetIntent(hold.Reason);
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moved = false;
            ReleaseMovement();
            return false;
        }

        // Atomos capture 2026-10-09 01:35 UTC: the 500ms lease expired inside
        // ~670ms routine actions, letting combat movement drift off the required pad.
        // Two seconds bridges those actions and the observed ~1.5s casts. This is
        // only a bounded stale-owner fallback: normal hold/phase/death/loading/stop
        // cleanup still releases our handle immediately, as does active avoidance.
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, 2000, hold.Reason);
        leased = true;
        var target = new Vector3(hold.P.X, hold.Y, hold.P.Y);
        // Pools support points are on raised Y42.4 floors above the poisoned Y41.6 floor.
        // Check height for these measured points only: other goals use nominal arena
        // heights, so applying a strict 3D tolerance there could prevent a valid hold.
        bool raisedPools = phase == 0 && (hold.P == P.OpeningPlatform || hold.P == P.GreaterDemonPlatform || hold.P == P.DiraPlatform);
        float distance = raisedPools ? Core.Player.Location.Distance(target) : Core.Player.Location.Distance2D(target);
        if (distance <= hold.Tolerance)
        {
            StopMovement();
            return false;
        }

        if (hold.Urgent && Core.Player.IsCasting)
            ActionManager.StopCasting();
        return Navigate(target, .35f);
    }

    internal bool Travel()
    {
        if (!active || session == null || !session.Active || goal != null || CommonBehaviors.IsLoading || QuestLogManager.InCutscene || !Core.Player.IsAlive || Core.Player.InCombat || AvoidanceManager.IsRunningOutOfAvoid)
            return false;
        var p = Core.Player.Location;
        // Return shortcuts are only activated after their actual clear bit. Arrival is verified
        // by hub coordinates, not a successful Interact return or a fixed sleep.
        uint shortcut = phase >= 2 && p.X < -300 ? 2002804u : phase >= 4 && p.X > 350 ? 2002805u : phase == 6 && p.Z < -300 ? 2002806u : 0;
        if (shortcut != 0)
        {
            var obj = GameObjectManager.GameObjects.FirstOrDefault(a => a.IsValid && a.BaseId == shortcut && a.IsTargetable);
            if (obj == null)
            {
                Warn($"Waiting for cleared-wing shortcut {shortcut}.");
                return false;
            }

            if (obj.Location.Distance(p) > 2.5f)
                return Navigate(obj.Location, 2);
            StopMovement();
            // Accept only the yes/no opened by this exact nearby shortcut. Never consume an
            // unrelated confirmation or select a numeric entry in a localization-sensitive menu.
            if (SelectYesno.IsOpen && transportOwner == obj.ObjectId && now - nextInteraction < 7)
            {
                SelectYesno.Yes();
                nextInteraction = now + 3;
                return false;
            }

            if (transportOwner != obj.ObjectId)
            {
                transportOwner = obj.ObjectId;
                transportStarted = now;
            }

            if (now >= nextInteraction && !SelectYesno.IsOpen && !SelectString.IsOpen && !SelectIconString.IsOpen)
            {
                obj.Interact();
                nextInteraction = now + 3;
            }

            if (now - transportStarted > 120 && !session.Retry)
                session.RetryKind = "Shortcut";
            return false;
        }

        transportOwner = 0;
        transportStarted = 0;
        if (phase is 2 or 4 or 6 && session.Alliance < 0 && (phase == 2 && p.X > 175 || phase == 4 && p.Z < -115 || phase == 6 && p.Y > 600))
        {
            StopMovement();
            Warn("Waiting for three own-party members to establish the assigned lane/pad.");
            return false;
        }

        int lane = session.Alliance;
        // Lockout transfer can place a B/C member in physical A. Exit the occupied lane
        // until the corridors merge; combat ownership must retain the assigned alliance.
        lane = departureLane.Resolve(phase, ToNumerics(p), lane);
        // Reconstruct the physical approach after a wipe as well as the logical checkpoint.
        // Previously cleared wings do not imply the player respawned at their far doorway.
        V3[] route = phase switch
        {
            0 => LabyrinthRoutes.Pools,
            1 => p.Z > 180 ? LabyrinthRoutes.Pools.Concat(LabyrinthRoutes.Bone).ToArray() : LabyrinthRoutes.Bone,
            2 => LabyrinthRoutes.Atomos(lane),
            3 => p.X < 190 ? LabyrinthRoutes.Atomos(lane).Concat(LabyrinthRoutes.Thanatos(lane)).ToArray() : LabyrinthRoutes.Thanatos(lane),
            4 => p.Z > 0 ? LabyrinthRoutes.FireApproach : LabyrinthRoutes.Fire,
            5 => p.Z > 0 ? LabyrinthRoutes.FireApproach : p.Z > -190 ? LabyrinthRoutes.Fire.Concat(LabyrinthRoutes.Behemoth).ToArray() : LabyrinthRoutes.Behemoth,
            6 => p.Y < 600 ? LabyrinthRoutes.Drop : LabyrinthRoutes.Final,
            _ => []
        };
        if (phase >= 2 && p.Z > 330 && p.Y < 100 && p.X > -150 && p.X < -70)
            // Select the entrance by its hub boundary so denser terrain samples do not truncate recovery.
            route = LabyrinthRoutes.Pools.TakeWhile(a => a.Z >= 318).Concat(route).ToArray();
        if (route.Length < 2)
            return false;
        var members = party.Where(a => a.Alive && a.Id != Core.Player.ObjectId && Math.Abs(a.Y - p.Y) < 25).Select(a => new V3(a.P.X, a.Y, a.P.Y)).ToArray();
        bool tank = Role(Core.Player.CurrentJob.ToString()) == P.Role.Tank;
        bool ready = members.Count(a => V3.Distance(a, ToNumerics(p)) < 35) >= 3 && party.Any(a => a.Alive && a.Role == P.Role.Healer && V2.Distance(a.P, Xz(p)) < 35);
        var destination = LabyrinthRoutes.Tether(ToNumerics(p), members, route, tank && session.TankLead && ready);
        // At the two observed one-way transitions a party already on the destination floor is
        // evidence to approach the trigger. Never path directly to the far endpoint or Y=650.
        if (destination == null && phase == 6 && p.Y < 600 && party.Count(a => a.Alive && a.Y > 600) >= 3)
            destination = LabyrinthRoutes.ApproachStep(ToNumerics(p), LabyrinthRoutes.Drop);
        if (destination == null && phase is 4 or 5 && p.Z > 0 && party.Count(a => a.Alive && a.P.Y < 0) >= 3)
            destination = LabyrinthRoutes.ApproachStep(ToNumerics(p), LabyrinthRoutes.FireApproach);
        if (destination == null)
        {
            StopMovement();
            SetIntent("Waiting for own-party progression");
            return false;
        }

        float tolerance = phase is 4 or 5 && p.Z > 0 || phase == 6 && p.Y < 600 ? .15f : 1;
        return Navigate(new Vector3(destination.Value.X, destination.Value.Y, destination.Value.Z), tolerance);
    }

    private bool Navigate(Vector3 target, float tolerance)
    {
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            moved = false;
            return false;
        }

        if (Core.Player.Location.Distance(target) <= tolerance)
        {
            StopMovement();
            return false;
        }

        var pos = ToNumerics(Core.Player.Location);
        if (now - lastNavigationAttempt > 5)
            lastProgress = now;
        lastNavigationAttempt = now;
        if (V3.Distance(pos, lastPosition) > 1)
        {
            lastPosition = pos;
            lastProgress = now;
        }

        if (now < nextNavigation)
            return false;
        var result = Navigator.MoveTo(new MoveToParameters(target) { DistanceTolerance = tolerance, UseMount = false });
        moved = true;
        if (result is MoveResult.Failed or MoveResult.PathGenerationFailed || now - lastProgress > 15)
        {
            // Scope endpoint backoff to the captured Bone Dragon hold failure. Ten
            // seconds permits several normal two-second retries at other platforms;
            // expiry and encounter reset allow the original landing to recover later.
            if (phase == 1 && goal?.Reason == "Bone Dragon safe platform" && result is MoveResult.Failed or MoveResult.PathGenerationFailed)
                failedBoneLandings[Xz(target)] = now + 10;
            StopMovement();
            nextNavigation = now + 2;
            // Bone Dragon, Oct8 11:04: player remained on the poisoned low floor while
            // successful path responses were immediately stopped by the old expired timer.
            // Each backed-off attempt needs a fresh progress window; retain nextRecovery
            // separately so diagnostics stay throttled without starving navigator execution.
            // Reacquire party/route and retry with backoff. If local recovery has made no
            // progress for two minutes out of combat, XML owns the once-only leave/retry toast
            // and verified exit. This is never a clear or a terminal retry-count stop.
            if (now >= nextRecovery)
            {
                nextRecovery = now + 5;
                Warn($"Route recovery: {result}; reacquiring party/waypoint at {Core.Player.Location}. Destination {target}.");
            }

            if (!Core.Player.InCombat && goal == null && now - lastProgress > 120 && !session.Retry)
                session.RetryKind = "Route";
            lastProgress = now;
            return false;
        }

        return true;
    }

    private void InstallProvider()
    {
        if (provider != null)
            return;
        previousProvider = CombatTargeting.Instance.Provider;
        previousLock = CombatTargeting.Instance.Locked;
        provider = new RaidTargets(this);
        CombatTargeting.Instance.Locked = false;
        CombatTargeting.Instance.Provider = provider;
        CombatTargeting.Instance.Locked = true;
    }

    private sealed class RaidTargets(LabyrinthOfTheAncients owner) : ITargetingProvider
    {
        public List<BattleCharacter> GetObjectsByWeight() => !owner.active ? [] : GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid && a.IsAlive && a.CanAttack && a.IsTargetable && owner.eligible.Contains(a.ObjectId)).Where(a => owner.authoredArea?.TargetMobs.Any(m => m.Id == a.NpcId) == true).OrderByDescending(a => P.Priority(a.BaseId, owner.phase, Role(Core.Player.CurrentJob.ToString()), owner.session.MainTank || a.CurrentTargetId == Core.Player.ObjectId, owner.authoredArea.TargetMobs.First(m => m.Id == a.NpcId).Weight)).ThenBy(a => a.Location.Distance2DSqr(Core.Player.Location)).ToList();
    }

    private void SetSideStep(bool disable)
    {
        if (SidestepPlugin == null)
            return;
        if (disable && !suppressed)
        {
            sideStepWasEnabled = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            suppressed = true;
        }
        else if (!disable && suppressed)
        {
            SidestepPlugin.Enabled = sideStepWasEnabled;
            suppressed = false;
        }
    }

    private void StopMovement()
    {
        if (moved && !AvoidanceManager.IsRunningOutOfAvoid)
            Navigator.PlayerMover.MoveStop();
        moved = false;
    }

    private void ReleaseMovement()
    {
        StopMovement();
        if (leased)
            CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Labyrinth hold released");
        leased = false;
    }

    private void ResetTransient()
    {
        active = false;
        goal = null;
        eligible.Clear();
        casts.Clear();
        failedBoneLandings.Clear();
        hazards = [];
        flare.Reset();
        linkedAtomosDead = false;
        fireFollowing = false;
        departureLane.Reset();
        meteorUntil = 0;
        ReleaseMovement();
    }

    private void Deactivate(bool nativeCleanup = true)
    {
        if (nativeCleanup && ownedPoi != 0 && Poi.Current.Type == PoiType.Kill && Poi.Current.Unit?.ObjectId == ownedPoi)
            Poi.Clear("Labyrinth controller released its combat target");
        ownedPoi = 0;
        ResetTransient();
        SetSideStep(false);
        if (provider != null && ReferenceEquals(CombatTargeting.Instance.Provider, provider))
        {
            CombatTargeting.Instance.Locked = false;
            CombatTargeting.Instance.Provider = previousProvider;
            CombatTargeting.Instance.Locked = previousLock;
        }

        provider = null;
    }

    internal static LabyrinthOfTheAncients Running;
    internal static bool TravelCurrent() => Running?.Travel() ?? false;
    private void SetIntent(string value)
    {
        Running = this;
        if (value == intent)
            return;
        intent = value;
        Logger.Information($"[Labyrinth] {value}");
    }

    private void Warn(string text)
    {
        if (DateTime.UtcNow < nextWarning)
            return;
        nextWarning = DateTime.UtcNow.AddSeconds(5);
        Logger.Information($"[Labyrinth] {text}");
    }

    private static P.Role Role(string job) => job is "Gladiator" or "Paladin" or "Marauder" or "Warrior" or "DarkKnight" or "Gunbreaker" ? P.Role.Tank : job is "Conjurer" or "WhiteMage" or "Scholar" or "Astrologian" or "Sage" ? P.Role.Healer : job is "Pugilist" or "Monk" or "Lancer" or "Dragoon" or "Rogue" or "Ninja" or "Samurai" or "Reaper" or "Viper" ? P.Role.Melee : P.Role.Ranged;
    private static V2 Xz(Vector3 p) => new(p.X, p.Z);
    private static V3 ToNumerics(Vector3 p) => new(p.X, p.Y, p.Z);
    private static float Vector2Distance(V2 a, V2 b) => V2.Distance(a, b);
    private void OnStop(BotBase bot) => Dispose();
    /// <summary>Releases managed ownership only; the host owns native movement on stop/disable.</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        moved = false;
        Deactivate(false);
        if (ReferenceEquals(Current, session))
            EndProfile();
        if (Running == this)
            Running = null;
        TreeRoot.OnStop -= OnStop;
    }
}
