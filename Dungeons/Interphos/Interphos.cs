using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Directors;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>Coordinates normal Interphos mechanics and encounter movement.</summary>
/// <remarks>
/// The profile owns targeting and duty completion. Mechanic planners share one
/// movement owner, combining overlapping hazards against the observed arena layout.
/// SideStep stays disabled throughout the encounter, including untargetable phases.
/// </remarks>
public sealed class Interphos : AbstractDungeon
{
    private const uint QueenEternal = 13029;
    private const uint ProsecutionOfWar = 36602;
    private const uint RoyalDomain = 36603;
    private const uint VirtualShiftOne = 36606;
    private const uint VirtualShiftTwo = 36607;
    private const uint VirtualShiftThree = 36608;
    private const uint AuthorityCircle = 36622;
    private const uint Waltz = 36632;
    private const uint BrutalCrown = 36633;
    private const uint DynasticDiadem = 40059;
    private const uint AbsoluteAuthority = 39531;
    private const uint RoyalBanishment = 36647;
    private readonly InterphosRoyalSequence royalSequence = new();
    private string royalGroup = string.Empty;
    private string royalGroupPending;
    private string cleavePending;
    private double cleaveCaptureUntil;
    private double nextCleaveCapture;
    private const uint AuthorityHold = 4130;
    private const uint AuthorityGaze = 3815;
    private ChrysalisMovement movement;
    private bool lifecycle;
    private bool moverReplaced;
    private bool holdingStill;
    private bool servicingRoutine;
    private bool openingApproachArmed;
    private bool openingApproachOwned;
    private string openingApproachPending;
    private readonly CapabilityManagerHandle gazeFacingHandle = CapabilityManager.CreateNewHandle();
    private bool gazeFacingOwned;
    private bool authorityOwned;
    private Vector3 authorityTarget;
    private string authorityPending;
    private bool authorityNoRouteLogged;
    private AvoidInfo authorityCircleAvoid;
    private bool sideStepBorrowed;
    private bool savedSideStep;
    private readonly InterphosCleave cleave = new();
    private readonly InterphosVisualCapture visualCapture = new();
    private readonly InterphosAethertitheSignal aethertitheSignal = new();
    private readonly InterphosDivideSignal divideSignal = new();
    private readonly InterphosDivide divide = new();
    private InterphosDivide.Member[] divideParty = [];
    private double divideNow;
    private double divideNextRead;
    private double nextDivideCapture;
    private bool divideReadable;
    private uint divideLastCast;
    private Vector3 divideOrigin;
    private float divideSpeed = 6;
    private readonly InterphosMovementSpeed divideMovementSpeed = new();
    private string dividePending;
    private bool squareFloorKnown;
    private bool crossFloorKnown;
    private bool splitFloorKnown;
    private bool aethertitheOwned;
    private bool aethertitheWindup;
    private bool aethertitheSeenSignal;
    private bool gustOwned;
    private bool crossCrownOwned;
    private uint pendingFloorShift;
    private double aethertitheEnd;
    private double aethertitheNextRead;
    private double aethertitheLastSignal;
    private int aethertitheSector;
    private Vector3 aethertitheTarget;
    private readonly Dictionary<uint, double> waltzActivations = [];
    private InterphosWaltz.Circle[] waltzThreats = [];
    private InterphosWaltz.Ray[] waltzRays = [];
    private double waltzRayUntil;
    private double waltzNextRead;
    private bool waltzOwned;
    private Vector3 waltzTarget;
    private string waltzPending;
    private bool splitOwned;
    private bool splitTargetIsWall;
    private bool splitSequenceKnown;
    private bool splitWallArmed;
    private int splitWallSequence;
    private bool splitEntryFinished;
    private double nextSplitDecisionCapture;
    private ulong splitEncounterBoss;
    private ushort splitLastWallState;
    private readonly List<(InterphosWalls.Lane[] Lanes, double Until)> splitWaves = [];
    private Vector3 splitTarget;
    private uint gustCaster;
    private uint gustAction;
    private Vector3 gustOrigin;
    private double downburstUntil;
    private string gustPending;
    private float gustHeading;
    private Vector3 gustTarget;
    private uint crossCrownCaster;
    private Vector3 crossCrownTarget;
    private InterphosCrossCrown.Point[] crossCrownRoute = [];
    private int crossCrownStep;
    private string crossCrownPending;
    private bool regaliaOwned;
    private uint regaliaActor;
    private int regaliaTimeline;
    private double regaliaTellAt;
    private double regaliaNextRead;
    private Vector3 regaliaTarget;
    private string regaliaPending;
    private AvoidInfo brutalCrownAvoid;
    private bool diagnosticFailed;
    private bool aethertitheGateLogged;
    private bool splitGateLogged;
    private readonly List<AvoidInfo> registeredAvoids = [];
    // Placed-effect IDs identify retained objects, not network packet indices.
    // Keep raw state changes available to diagnose floor transitions.
    private const bool CaptureMapEffects = true;
    private readonly Dictionary<uint, (uint Unknown, ushort State, byte Flags)> observedMapEffects = [];

    /// <inheritdoc />
    public override ZoneId ZoneId => (ZoneId)1202;
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToTankBust { get; } = [ProsecutionOfWar];
    /// <inheritdoc />
    protected override HashSet<uint> SpellsToMitigate
    {
        get;
    } =
        [RoyalDomain, VirtualShiftOne, VirtualShiftTwo, VirtualShiftThree, AbsoluteAuthority];

    /// <inheritdoc />
    protected override Task<bool> EnterDungeonAsync()
    {
        lifecycle = true;
        try
        {
            aethertitheSignal.Prepare();
            divideSignal.Prepare();
        }
        catch (Exception) { /* Keep ordinary encounter movement available. */ }
        ff14bot.NeoProfile.BotEvents.OnPulse += OnPulse;
        TreeRoot.OnStop += OnStop;
        TreeHooks.Instance.OnHooksCleared += OnHooksCleared;
        GameEvents.OnMapChanged += OnMapChanged;

        registeredAvoids.Add(AvoidanceHelpers.AddAvoidRectangle<BattleCharacter>(
            // Native escape crossed missing split floor and repeatedly chose a
            // square-floor endpoint within arrival tolerance. Those layouts use
            // their combined mechanic planner; a native shape would compete.
            canRun: () => IsEncounterActive() && cleave.Side != 0 && !splitFloorKnown && !squareFloorKnown,
            objectSelector: actor => actor.IsValid && actor.ObjectId == cleave.Caster,
            width: 31f, length: 61f, yOffset: -0.5f,
            rotationProducer: _ => 0f,
            locationProducer: actor => actor.Location + new Vector3(cleave.Side * 15f, 0f, 0f),
            priority: AvoidancePriority.High));
        registeredAvoids.Add(authorityCircleAvoid = AvoidanceManager.AddAvoidObject<BattleCharacter>(
            // During marked flare/pair positioning the authored handler includes
            // these exact circles. Native escape must not break that combined route.
            canRun: () => IsEncounterActive() && !authorityOwned,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == AuthorityCircle,
            radiusProducer: _ => 8.5f,
            locationProducer: actor => actor.Location));
        // Waltz and Divide share the square planner. Native Waltz escape failed
        // at the arena edge and during their overlap; do not register it here.
        registeredAvoids.Add(brutalCrownAvoid = AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            // Native donut escape crossed missing X-floor after Downburst. The
            // authored cross-floor handler below owns this cast on that layout.
            canRun: () => IsEncounterActive() && !crossFloorKnown,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == BrutalCrown,
            locationProducer: actor => actor.Location,
            outerRadius: 60.5f, innerRadius: 4.5f, priority: AvoidancePriority.High));
        registeredAvoids.Add(AvoidanceHelpers.AddAvoidDonut<BattleCharacter>(
            canRun: IsEncounterActive,
            objectSelector: actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == DynasticDiadem,
            locationProducer: actor => actor.Location,
            outerRadius: 70.5f, innerRadius: 5.5f, priority: AvoidancePriority.High));
        // Final-phase helper cones have a 30-degree arc. A three-degree margin
        // per edge provides at least 0.5y clearance at the nearest floor (10y).
        // The existing mover owns native escape cleanup and ordinary-travel guards.
        registeredAvoids.Add(AvoidanceManager.AddAvoidUnitCone<BattleCharacter>(
            canRun: () => IsEncounterActive() && royalSequence.Active,
            objectSelector: actor => actor.IsValid && royalSequence.Includes(actor.ObjectId),
            leashPointProducer: () => new Vector3(100, 0, 105), leashRadius: 50f,
            rotationDegrees: 0f, radius: 100.5f, arcDegrees: 36f,
            priority: AvoidancePriority.High));
        // Constrain native escape to the physical final rectangle, inset 0.5y.
        // This boundary is scoped to observed cones, never inferred from a timer.
        registeredAvoids.Add(AvoidanceHelpers.AddAvoidSquareDonut(
            canRun: () => IsEncounterActive() && royalSequence.Active,
            innerWidth: 39f, innerHeight: 29f, outerWidth: 120f, outerHeight: 120f,
            collectionProducer: () => new[] { new Vector3(100, 0, 105) },
            priority: AvoidancePriority.High));
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    protected override Task<bool> ExitDungeonAsync()
    {
        EndLifecycle();
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public override async Task<bool> RunAsync()
    {
        if (servicingRoutine)
        {
            return false;
        }

        UpdateMovement();
        if (!IsEncounterActive())
        {
            return false;
        }

        if (movement?.IsEscaping == true)
        {
            return true;
        }

        if (NeedsTargetlessHealing())
        {
            // OrderBot may skip routine execution without an attackable target during
            // sustained raidwide damage. Give the existing routine one opportunity;
            // it still selects its own actions and the encounter mover owns input.
            servicingRoutine = true;
            try
            {
                await CommonTasks.ExecuteCoroutine(BrainBehavior.CombatLogic);
            }
            finally { servicingRoutine = false; }
            UpdateMovement(); // Revalidate stop, death and phase state after the await.
            if (!lifecycle || !TreeRoot.IsRunning || Core.Me == null || !Core.Me.IsValid ||
                !Core.Me.IsAlive || CommonBehaviors.IsLoading || QuestLogManager.InCutscene ||
                !IsEncounterActive())
            {
                return false;
            }
        }
        if (await TankBusterSpells())
        {
            return true;
        }

        return await DamageMitigationSpells();
    }

    private static bool NeedsTargetlessHealing()
    {
        if (Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive || !Core.Me.InCombat ||
            AvoidanceManager.IsRunningOutOfAvoid || Core.Me.CurrentJob is not
                (ff14bot.Enums.ClassJobType.WhiteMage or ff14bot.Enums.ClassJobType.Scholar or
                 ff14bot.Enums.ClassJobType.Astrologian or ff14bot.Enums.ClassJobType.Sage))
        {
            return false;
        }

        var target = Core.Me.CurrentTarget;
        if (target != null && target.IsValid && target.CanAttack)
        {
            return false;
        }

        return Core.Me.CurrentHealthPercent < 90 ||
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
                actor.IsValid && actor.IsAlive && actor.CurrentHealthPercent < 90 &&
                PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId));
    }

    // Reuse the established single mover/provider owner so native escape releases
    // only its own held input and cannot cancel a newer movement request. Stop,
    // loading, death and a foreign replacement release our ownership promptly.
    private void UpdateMovement()
    {
        // The entire trial is one boss lifecycle, including untargetable phases,
        // cutscenes and wipe recovery. Generic SideStep must not regain ownership
        // merely because the boss temporarily disappears.
        if (lifecycle && TreeRoot.IsRunning && DutyManager.InInstance && WorldManager.ZoneId == 1202)
        {
            if (!sideStepBorrowed && SidestepPlugin != null)
            {
                savedSideStep = SidestepPlugin.Enabled;
                sideStepBorrowed = true;
            }
            if (sideStepBorrowed && SidestepPlugin.Enabled)
            {
                SidestepPlugin.Enabled = false;
            }
        }
        if (!lifecycle || !TreeRoot.IsRunning || !DutyManager.InInstance ||
            CommonBehaviors.IsLoading || QuestLogManager.InCutscene || Core.Me == null ||
            !Core.Me.IsValid || !Core.Me.IsAlive || !IsEncounterActive())
        {
            openingApproachArmed = false;
            ReleaseOpeningApproach();
            cleave.Reset();
            divide.Reset();
            divideParty = [];
            divideLastCast = 0;
            divideMovementSpeed.Reset();
            divideSpeed = 6;
            royalSequence.Reset();
            royalGroup = string.Empty;
            ReleaseStillness();
            ReleaseAuthority();
            ReleaseSplit();
            ReleaseWaltz();
            ReleaseAethertithe();
            ReleaseGust();
            ReleaseCrossCrown();
            ResetRegalia();
            squareFloorKnown = crossFloorKnown = splitFloorKnown = false;
            pendingFloorShift = 0;
            aethertitheEnd = 0;
            aethertitheWindup = false;
            aethertitheSeenSignal = false;
            waltzActivations.Clear();
            waltzThreats = [];
            waltzRays = [];
            waltzRayUntil = 0;
            ResetSplitPattern();
            ReleaseGaze();
            movement?.Detach();
            movement = null;
            return;
        }
        if (movement != null && !movement.IsCurrent)
        {
            ReleaseStillness();
            movement.Detach();
            movement = null;
            moverReplaced = true;
        }
        if (!moverReplaced && movement == null && Navigator.PlayerMover?.GetType() == typeof(SlideMover))
        {
            movement = new ChrysalisMovement(Navigator.PlayerMover, canMoveOrdinary: SafeOrdinarySegment);
            Navigator.PlayerMover = movement;
        }
        double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        // A startup velocity fragment once rejected reachable Divide escapes.
        // Only settled movement samples may change the walking-speed estimate.
        divideSpeed = divideMovementSpeed.Observe(now, (float)MovementManager.Speed, MovementManager.IsMoving);
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .FirstOrDefault(actor => actor.IsValid && actor.IsAlive && InterphosCleave.IsBoss(actor.NpcId, actor.BaseId));
        try
        {
            UpdateRoyalSequence(now);
        }
        catch (Exception) { royalSequence.Reset(); }
        int priorSide = cleave.Side;
        cleave.Observe(boss?.ObjectId ?? 0, boss?.IsCasting == true ? boss.CastingSpellId : 0, now);
        if (cleave.Side != priorSide)
        {
            cleavePending = $"[Interphos] CLEAVE side={cleave.Side} caster=0x{cleave.Caster:X} time={now:F3}";
        }

        try
        {
            UpdateFloorState(boss);
        }
        catch (Exception) { squareFloorKnown = crossFloorKnown = splitFloorKnown = false; pendingFloorShift = 0; ResetSplitPattern(); }
        try
        {
            ObserveAethertithe(boss, now);
        }
        catch (Exception) { aethertitheLastSignal = 0; }
        try
        {
            ObserveWaltz(now);
        }
        catch (Exception) { waltzActivations.Clear(); waltzThreats = []; waltzRays = []; waltzRayUntil = 0; }
        try
        {
            ObserveDivide(boss, now);
        }
        catch (Exception) { divide.Reset(); divideParty = []; }
        // Retire the approach before any mechanic can acquire the same mover.
        try
        {
            UpdateOpeningApproach(boss);
        }
        catch (Exception)
        {
            openingApproachArmed = false;
            ReleaseOpeningApproach();
        }
        // Authority markers are a distinct square phase. Retire predecessors
        // before acquiring its request, or release Authority before any successor
        // below can acquire the same owner. Never clear a newer request afterward.
        bool authorityActive;
        try
        {
            authorityActive = UpdateAuthority();
        }
        catch (Exception) { ReleaseAuthority(); authorityActive = false; }
        if (!authorityActive)
        {
            // Retire the old mechanic before its successor acquires the shared mover.
            if (!crossFloorKnown)
            {
                ReleaseGust();
            }

            if (!crossFloorKnown)
            {
                ReleaseCrossCrown();
            }

            if (!crossFloorKnown && !squareFloorKnown)
            {
                ResetRegalia();
            }

            if (!squareFloorKnown)
            {
                ReleaseAethertithe();
            }

            if (!splitFloorKnown)
            {
                ReleaseSplit();
            }

            try
            {
                UpdateSplitPattern(boss, now);
            }
            catch (Exception) { ReleaseSplit(); splitSequenceKnown = false; splitWaves.Clear(); }
            // The line also repeats on square floor during Aethertithe. Choose
            // one destination against both live shapes before either handler can
            // acquire movement; never let a later stale release cancel that request.
            try
            {
                UpdateRegalia(boss, now);
            }
            catch (Exception) { ResetRegalia(); }
            if (!regaliaOwned)
            {
                try
                {
                    UpdateSquareHazards(boss, now);
                }
                catch (Exception) { ReleaseWaltz(); }
                if (!waltzOwned && !splitOwned)
                {
                    try
                    {
                        UpdateAethertithe(boss, now);
                    }
                    catch (Exception) { ReleaseAethertithe(); }
                }
            }
            // The captured line resolves before Downburst/Crown. Preserve its
            // position through impact, then let the later cast own movement.
            if (!regaliaOwned)
            {
                try
                {
                    UpdateCrossCrown();
                }
                catch (Exception) { ReleaseCrossCrown(); }
            }
            if (!crossCrownOwned && !regaliaOwned)
            {
                try
                {
                    UpdateGust(now);
                }
                catch (Exception) { ReleaseGust(); }
            }
        }
        if (!openingApproachOwned && !authorityOwned && !splitOwned && !waltzOwned && !aethertitheOwned && !gustOwned && !crossCrownOwned && !regaliaOwned)
        {
            UpdateStillness();
        }

        UpdateGaze();
        movement?.Update();
        // Detailed captures are developer-only; movement has already executed.
        if (LoggingHelpers.MechanicDiagnosticsEnabled && !diagnosticFailed)
        {
            try
            {
                if (openingApproachPending != null)
                {
                    Logger.Information(openingApproachPending);
                    openingApproachPending = null;
                }
                bool windupNow = boss?.IsCasting == true && boss.CastingSpellId == 36604;
                if (windupNow && !aethertitheGateLogged)
                {
                    aethertitheGateLogged = true;
                    Logger.Information($"[InterphosCapture] AETHERTITHE_GATE square={squareFloorKnown} cross={crossFloorKnown} pending={pendingFloorShift} moverCurrent={movement?.IsCurrent == true} escaping={movement?.IsEscaping == true} outOfControl={MovementManager.GenerallyOutOfControl}");
                }
                else if (!windupNow)
                {
                    aethertitheGateLogged = false;
                }

                bool splitCastNow = boss?.IsCasting == true && boss.CastingSpellId == VirtualShiftTwo;
                if (splitCastNow && !splitGateLogged)
                {
                    splitGateLogged = true;
                    Logger.Information($"[InterphosCapture] SPLIT_GATE sequenceKnown={splitSequenceKnown} priorWalls={splitWallSequence} bossHealth={boss.CurrentHealthPercent:F1} moverCurrent={movement?.IsCurrent == true}");
                }
                else if (!splitCastNow)
                {
                    splitGateLogged = false;
                }
                // Diagnostic runs after the movement owner so logging cannot delay its decision.
                if (dividePending != null)
                {
                    Logger.Information(dividePending);
                    dividePending = null;
                }
                if (divide.InWindow(now) && now >= nextDivideCapture)
                {
                    nextDivideCapture = now + 0.5;
                    var p = Core.Me.Location;
                    Logger.Information(FormattableString.Invariant($"[InterphosCapture] DIVIDE_MOVE readable={divideReadable} time={now:F3} pos=({p.X:F3},{p.Z:F3}) baits={string.Join(";", divide.Baits.Select(b => b.Target.ToString("X") + "@" + (b.Impact - now).ToString("F2")))} owned={waltzOwned} target=({waltzTarget.X:F3},{waltzTarget.Z:F3}) speed={divideSpeed:F2} moving={MovementManager.IsMoving} escaping={movement?.IsEscaping == true}"));
                    LoggingHelpers.LogActorSignalChanges("Interphos.DivideHelpers", actor => actor.BaseId == 0x233C || actor.BaseId == 0x41C0);
                }
                if (cleavePending != null)
                {
                    Logger.Information(cleavePending);
                    cleavePending = null;
                }
                // Domestic observed opening hand-strike stutter. Sample the
                // actual owner/position through each strike and its release;
                // request selection alone cannot verify smooth execution.
                if (cleave.Side != 0)
                {
                    cleaveCaptureUntil = now + 1;
                }

                if (now < cleaveCaptureUntil && now >= nextCleaveCapture)
                {
                    nextCleaveCapture = now + 0.2;
                    var p = Core.Me.Location;
                    Logger.Information(FormattableString.Invariant($"[InterphosCapture] CLEAVE_MOVE time={now:F3} side={cleave.Side} square={squareFloorKnown} pos=({p.X:F3},{p.Z:F3}) owned={waltzOwned} target=({waltzTarget.X:F3},{waltzTarget.Z:F3}) moving={MovementManager.IsMoving} current={movement?.IsCurrent == true} escaping={movement?.IsEscaping == true} casting={Core.Me.IsCasting} control={MovementManager.GenerallyOutOfControl}"));
                }
                if (splitFloorKnown && now >= nextSplitDecisionCapture)
                {
                    nextSplitDecisionCapture = now + 0.5;
                    var player = Core.Me.Location;
                    Logger.Information(FormattableString.Invariant($"[InterphosCapture] SPLIT_DECISION time={now:F3} pos=({player.X:F3},{player.Z:F3}) sequence={splitWallSequence} known={splitSequenceKnown} armed={splitWallArmed} state=0x{splitLastWallState:X} waves={splitWaves.Count} expires={string.Join(",", splitWaves.Select(w => (w.Until - now).ToString("F2")))} owned={splitOwned} wall={splitTargetIsWall} target=({splitTarget.X:F3},{splitTarget.Z:F3}) gravity={SplitGravityActive()} control={MovementManager.GenerallyOutOfControl} current={movement?.IsCurrent == true} escaping={movement?.IsEscaping == true} avoids={AvoidanceManager.Avoids.Count(a => !a.ShouldIgnore)}"));
                }
                if (crossCrownPending != null)
                {
                    Logger.Information(crossCrownPending);
                    crossCrownPending = null;
                }
                if (gustPending != null)
                {
                    Logger.Information(gustPending);
                    gustPending = null;
                }
                if (waltzPending != null)
                {
                    Logger.Information(waltzPending);
                    waltzPending = null;
                }
                if (authorityPending != null)
                {
                    Logger.Information(authorityPending);
                    authorityPending = null;
                }
                if (regaliaPending != null)
                {
                    Logger.Information(regaliaPending);
                    regaliaPending = null;
                }
                if (royalGroupPending != null)
                {
                    Logger.Information(royalGroupPending);
                    royalGroupPending = null;
                }
                CaptureEncounterDiagnostics();
                visualCapture.Pulse(now);
            }
            catch (Exception error)
            {
                diagnosticFailed = true;
                Logger.Information($"[InterphosCapture] GAP diagnostics disabled: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    // WHM and Viper captures both lost the stock arrival move after OrderBot
    // selected its combat POI. Finish the short central approach through the
    // existing owner, only after observing a fresh boss and another player's
    // pull. Never start it on mid-fight attachment or resurrection. The profile
    // still owns targeting and its unchanged MoveToSafe/GrindSafe destinations.
    private void UpdateOpeningApproach(BattleCharacter boss)
    {
        bool mechanicActive = boss?.IsCasting == true || cleave.Side != 0 ||
            aethertitheWindup || divide.Active || waltzThreats.Length != 0 || waltzRays.Length != 0 ||
            authorityOwned || splitOwned || waltzOwned || aethertitheOwned || gustOwned ||
            crossCrownOwned || regaliaOwned || holdingStill || gazeFacingOwned;
        if (boss == null || !squareFloorKnown || pendingFloorShift != 0 ||
            movement?.IsCurrent != true || mechanicActive)
        {
            openingApproachArmed = false;
            ReleaseOpeningApproach();
            return;
        }

        if (!boss.InCombat)
        {
            openingApproachArmed = boss.CurrentHealthPercent >= 99.99f;
            ReleaseOpeningApproach();
            return;
        }

        if (!openingApproachArmed || !PartyManager.VisibleMembers.Any(member =>
                !member.IsMe && member.BattleCharacter.IsAlive && member.BattleCharacter.InCombat))
        {
            ReleaseOpeningApproach();
            return;
        }

        var start = Core.Me.Location;
        var target = new Vector3(100, 0, 100);
        if (start.Distance2D(target) <= 1f ||
            !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Square, start.X, start.Z, target.X, target.Z) ||
            !SafeOrdinarySegment(target))
        {
            openingApproachArmed = false;
            ReleaseOpeningApproach();
            return;
        }

        if (!openingApproachOwned)
        {
            movement.Request(target, 1f);
            openingApproachOwned = true;
            openingApproachPending = FormattableString.Invariant(
                $"[Interphos] OPENING_APPROACH from=({start.X:F3},{start.Z:F3}) to=(100.000,100.000)");
        }
    }

    private void ReleaseOpeningApproach()
    {
        if (!openingApproachOwned)
        {
            return;
        }

        openingApproachOwned = false;
        movement?.Request(null, 0);
        openingApproachPending = "[Interphos] OPENING_APPROACH released";
    }

    // The floor is unknown through transitions and after mid-fight attachment.
    // Only a captured initial state or a completed shift can grant a layout.
    private void UpdateFloorState(BattleCharacter boss)
    {
        if (boss == null || !boss.IsValid || !boss.IsAlive)
        {
            return;
        }

        if (splitEncounterBoss != boss.ObjectId)
        {
            ReleaseSplit();
            ResetSplitPattern();
            splitEncounterBoss = boss.ObjectId;
            // The reference counts formations across the encounter. A handler
            // attached after earlier walls cannot safely invent that count.
            splitSequenceKnown = boss.CurrentHealthPercent >= 99f;
        }
        uint cast = boss.IsCasting ? boss.CastingSpellId : 0;
        if (!squareFloorKnown && !crossFloorKnown && !splitFloorKnown && pendingFloorShift == 0 && !Core.Me.InCombat && boss.CurrentHealthPercent >= 99f)
        {
            squareFloorKnown = true;
        }

        if (cast is VirtualShiftOne or VirtualShiftTwo or VirtualShiftThree)
        {
            if (pendingFloorShift != cast)
            {
                ReleaseSplit();
                ResetSplitPhase();
            }
            squareFloorKnown = crossFloorKnown = splitFloorKnown = false;
            pendingFloorShift = cast;
        }
        else if (pendingFloorShift != 0)
        {
            // The capture showed the X-floor map change about 1.7 seconds after
            // the parent cast ended. Wait for the placed effects instead of
            // declaring the new floor at cast completion.
            if (pendingFloorShift == VirtualShiftOne && ObservedFloorState(1))
            {
                crossFloorKnown = true;
                pendingFloorShift = 0;
            }
            else if (pendingFloorShift == VirtualShiftThree && ObservedFloorState(4))
            {
                squareFloorKnown = true;
                pendingFloorShift = 0;
            }
            else if (pendingFloorShift == VirtualShiftTwo && ObservedSplitFloor())
            {
                splitFloorKnown = true;
                pendingFloorShift = 0;
            }
        }
        // Recover after attachment or resurrection only when a square-phase
        // cast agrees with the observed 4,4,4 map state. This also covers the
        // opening hand strikes after the boss has lost HP. A pending shift
        // invalidates that evidence; elapsed time alone cannot identify floor.
        if (!squareFloorKnown && !crossFloorKnown && !splitFloorKnown && pendingFloorShift == 0 &&
            ((cast is 36604 or 36629 or AbsoluteAuthority) || InterphosFloor.TryForceSequence(cast, out _, out _)) && ObservedFloorState(4))
        {
            squareFloorKnown = true;
            try
            {
                Logger.Information($"[Interphos] FLOOR square recovered cast={cast} markers=4,4,4");
            }
            catch (Exception) { }
        }
        // Authority can leave the square markers at 4,10,4 after resurrection.
        // Require a matching square-phase cast for this residual state too;
        // neither cross (1,1,4) nor split (1,1,1) matches it.
        if (!squareFloorKnown && !crossFloorKnown && !splitFloorKnown && pendingFloorShift == 0 &&
            (cast is 36604 or 36636) && ObservedAuthoritySquare())
        {
            squareFloorKnown = true;
            try
            {
                Logger.Information($"[Interphos] FLOOR square recovered cast={cast} markers=4/100,10,4");
            }
            catch (Exception) { }
        }
        // Recover the X-floor identity after a mid-fight resurrection only when
        // the Gust helper and both captured placed-effect states agree.
        if (!squareFloorKnown && !crossFloorKnown && !splitFloorKnown && pendingFloorShift == 0 &&
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Any(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId is 36612 or BrutalCrown) && ObservedFloorState(1))
        {
            crossFloorKnown = true;
        }
        // A resurrection can recover the floor identity, but not the earlier
        // wall sequence count; do not decode walls as if this were a fresh pull.
        if (!squareFloorKnown && !crossFloorKnown && !splitFloorKnown && pendingFloorShift == 0 &&
            SplitGravityActive() && ObservedSplitFloor())
        {
            splitFloorKnown = true;
        }
    }

    private static bool SplitGravityActive()
    {
        if (Core.Me.GetAuraById(3814) != null)
        {
            return true;
        }

        var members = PartyManager.VisibleMembers.Select(member => member.ObjectId).ToHashSet();
        return GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && members.Contains(actor.ObjectId) && actor.GetAuraById(3814) != null);
    }

    private static bool ObservedSplitFloor()
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector director || !director.IsValid)
        {
            return false;
        }

        var effects = director.MapEffects.ToArray();
        return effects.Any(effect => effect.ID == 0x9DFE5B && effect.State == 1) &&
            effects.Any(effect => effect.ID == 0x9DFE5C && effect.State == 1) &&
            effects.Any(effect => effect.ID == 0x9DFE5D && effect.State == 1);
    }

    private void UpdateSplitPattern(BattleCharacter boss, double now)
    {
        if (!splitFloorKnown || boss == null || !boss.IsValid || !boss.IsAlive)
        {
            ReleaseSplit();
            return;
        }
        // A Castellation cast arms the observed slot-3 tells for this split
        // sequence. A mid-fight attach lacks the earlier wall count and does not
        // guess which of the reference's first/second layouts is active.
        bool firstArm = !splitWallArmed && boss.IsCasting && boss.CastingSpellId == 36613;
        if (firstArm)
        {
            splitWallArmed = true;
        }

        if (splitWallArmed && DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
        {
            var effects = director.MapEffects.ToArray();
            if (effects.Length > 3 && effects[3].ID == 0xA052D3)
            {
                ushort state = effects[3].State;
                // Cold recovery cannot assign a new 6.9-second lifetime to an old
                // retained tell. Require a changed state after actual Castellation.
                if (firstArm && !splitSequenceKnown)
                {
                    splitLastWallState = state;
                }

                if (InterphosSplitPattern.ObserveWall(state, ref splitLastWallState, ref splitWallSequence, out var lanes, splitSequenceKnown))
                {
                    splitEntryFinished = true;
                    // Reference activation is 6.6 seconds; the captured
                    // vulnerability receipt arrived 6.868 seconds after the
                    // tell. Hold the opening through that observed window.
                    splitWaves.Add((lanes, now + 6.9));
                }
            }
        }
        splitWaves.RemoveAll(wave => now >= wave.Until);
        if (movement?.IsCurrent != true || movement.IsEscaping)
        {
            ReleaseSplit();
            return;
        }
        // The owner already pauses input while casting or forced movement has
        // control. Retain its request and revalidate before input resumes.
        if (MovementManager.GenerallyOutOfControl)
        {
            return;
        }

        bool gravity = SplitGravityActive();
        Vector3 start = Core.Me.Location;
        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var hazards = avoids.Select(avoid => (Func<float, float, bool>)((x, z) => avoid.IsPointInAvoid(new Vector3(x, start.Y, z)))).ToArray();
        var lanesNow = splitWaves.SelectMany(wave => wave.Lanes).ToArray();
        var pathLayout = gravity ? InterphosFloor.Layout.Floating : InterphosFloor.Layout.Split;

        if (lanesNow.Length != 0 && InterphosFloor.Contains(pathLayout, start.X, start.Z))
        {
            if (splitOwned && splitTargetIsWall && InterphosSplitPattern.ValidWallPosition(
                start.X, start.Z, splitTarget.X, splitTarget.Z, pathLayout, lanesNow, hazards))
            {
                return;
            }

            if (InterphosSplitPattern.TryWallPosition(start.X, start.Z, pathLayout, lanesNow, hazards, out float x, out float z))
            {
                ReleaseStillness();
                splitTarget = new Vector3(x, start.Y, z);
                movement.Request(splitTarget, 0.25f);
                splitOwned = splitTargetIsWall = true;
                try
                {
                    Logger.Information(FormattableString.Invariant($"[Interphos] SPLIT_WALL from=({start.X:F3},{start.Z:F3}) to=({x:F3},{z:F3}) waves={splitWaves.Count}"));
                }
                catch (Exception) { }
                return;
            }
            ReleaseSplit();
            return;
        }

        // Walls resolve before the overlapping force. Once they finish, stage on
        // solid floor for the current force half and its ordered second strike.
        // Never reuse the south-entry corridor after a wall: that fallback held
        // Livykate over the center gap when gravity expired in the 05:01 capture.
        if (splitEntryFinished)
        {
            if (cleave.Side != 0 || !InterphosFloor.Contains(InterphosFloor.Layout.Split, start.X, start.Z))
            {
                if (InterphosSplitPattern.TryPlatformPosition(start.X, start.Z, cleave.Side, pathLayout, hazards, out float px, out float pz))
                {
                    var target = new Vector3(px, start.Y, pz);
                    if (splitOwned && !splitTargetIsWall && splitTarget.Distance2D(target) <= 0.1f)
                    {
                        return;
                    }

                    ReleaseStillness();
                    splitTarget = target;
                    movement.Request(target, 0.25f);
                    splitOwned = true;
                    splitTargetIsWall = false;
                    try
                    {
                        Logger.Information(FormattableString.Invariant($"[Interphos] SPLIT_FORCE side={cleave.Side} from=({start.X:F3},{start.Z:F3}) to=({px:F3},{pz:F3})"));
                    }
                    catch (Exception) { }
                    return;
                }
            }
            ReleaseSplit();
            return;
        }
        if (!gravity)
        {
            ReleaseSplit();
            return;
        }
        var entry = new Vector3(InterphosSplitPattern.EntryX, start.Y, InterphosSplitPattern.EntryZ);
        bool entrySafe = InterphosSplitPattern.EntrySegment(start.X, start.Z, entry.X, entry.Z) &&
            hazards.All(hazard => InterphosSplitPattern.AvoidsReentry(start.X, start.Z, entry.X, entry.Z, hazard));
        if (splitOwned && !splitTargetIsWall &&
            (entrySafe || (start.Distance2D(entry) <= 0.5f && InterphosFloor.Contains(InterphosFloor.Layout.Floating, start.X, start.Z))))
        {
            return;
        }

        if (!entrySafe)
        {
            ReleaseSplit();
            return;
        }
        ReleaseStillness();
        splitTarget = entry;
        movement.Request(entry, 0.25f);
        splitOwned = true;
        splitTargetIsWall = false;
        try
        {
            Logger.Information(FormattableString.Invariant($"[Interphos] SPLIT_ENTRY from=({start.X:F3},{start.Z:F3}) to=({entry.X:F3},{entry.Z:F3})"));
        }
        catch (Exception) { }
    }

    private void ReleaseSplit()
    {
        if (!splitOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        splitOwned = splitTargetIsWall = false;
    }

    private void ResetSplitPattern()
    {
        ResetSplitPhase();
        splitSequenceKnown = false;
        splitWallSequence = 0;
        splitEncounterBoss = 0;
    }

    private void ResetSplitPhase()
    {
        splitEntryFinished = false;
        splitWallArmed = false;
        splitLastWallState = 0;
        splitWaves.Clear();
    }

    private static bool ObservedFloorState(ushort state)
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector director || !director.IsValid)
        {
            return false;
        }

        var effects = director.MapEffects.ToArray();
        // These are observed placed-object IDs, not the reference packet indices.
        return effects.Any(effect => effect.ID == 0x9DFE5B && effect.State == state) &&
            effects.Any(effect => effect.ID == 0x9DFE5C && effect.State == state) &&
            // Split floor also sets the first two to 1; its third marker is 1,
            // while the observed X floor and square return both use 4.
            effects.Any(effect => effect.ID == 0x9DFE5D && effect.State == 4);
    }

    private static bool ObservedAuthoritySquare()
    {
        if (DirectorManager.ActiveDirector is not InstanceContentDirector director || !director.IsValid)
        {
            return false;
        }

        var effects = director.MapEffects.ToArray();
        return effects.Any(e => e.ID == 0x9DFE5B && e.State is 4 or 0x100) &&
            effects.Any(e => e.ID == 0x9DFE5C && e.State == 0x10) &&
            effects.Any(e => e.ID == 0x9DFE5D && e.State == 4);
    }

    // Observe independently of the current owner so a bait escape can be
    // revalidated against a newly published cone without dropping its command.
    private void ObserveAethertithe(BattleCharacter boss, double now)
    {
        if (boss == null || !boss.IsValid || !boss.IsAlive)
        {
            return;
        }

        uint cast = boss.IsCasting ? boss.CastingSpellId : 0;
        bool windup = cast == 36604;
        if (windup && !aethertitheWindup)
        {
            aethertitheEnd = now + 35;
            aethertitheLastSignal = 0;
            aethertitheSeenSignal = false;
        }
        aethertitheWindup = windup;
        if (squareFloorKnown && aethertitheEnd != 0 && now < aethertitheEnd && now >= aethertitheNextRead)
        {
            aethertitheNextRead = now + 0.2;
            try
            {
                if (aethertitheSignal.TryRead(out int sector))
                {
                    aethertitheSector = sector;
                    aethertitheLastSignal = now;
                    aethertitheSeenSignal = true;
                }
            }
            catch (Exception)
            {
                // A failed observation must never block the existing movement
                // pulse or be interpreted as a safe/known cone.
                aethertitheLastSignal = 0;
            }
        }
    }

    // Aethertithe's scene timeline provides a candidate cone direction before
    // impact. No square-floor route is issued on an unknown or changed floor.
    private void UpdateAethertithe(BattleCharacter boss, double now)
    {
        if (boss == null || !boss.IsValid || !boss.IsAlive || movement?.IsCurrent != true ||
            !squareFloorKnown || aethertitheEnd == 0 || now >= aethertitheEnd || movement.IsEscaping || MovementManager.GenerallyOutOfControl)
        {
            ReleaseAethertithe();
            return;
        }
        // A short publication gap is normal when one timeline ends as the next
        // starts. Longer gaps retire our request instead of guessing a sector.
        if (aethertitheLastSignal == 0 || now - aethertitheLastSignal > 0.4 ||
            !InterphosAethertithe.TryAngle(aethertitheSector, out float angle))
        {
            if (!aethertitheSeenSignal)
            {
                StageAethertithe(boss);
            }
            else
            {
                ReleaseAethertithe();
            }

            return;
        }
        Vector3 start = Core.Me.Location, origin = boss.Location;
        bool startUnsafe = InterphosAethertithe.IsUnsafe(start.X, start.Z, origin.X, origin.Z, angle);
        bool targetValid = aethertitheOwned &&
            !InterphosAethertithe.IsUnsafe(aethertitheTarget.X, aethertitheTarget.Z, origin.X, origin.Z, angle) &&
            SafeAethertitheSegment(start, aethertitheTarget) &&
            (startUnsafe || InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z,
                aethertitheTarget.X, aethertitheTarget.Y, aethertitheTarget.Z,
                (x, _, z) => InterphosAethertithe.IsUnsafe(x, z, origin.X, origin.Z, angle)));
        if (targetValid)
        {
            if (!startUnsafe && start.Distance2D(aethertitheTarget) <= 0.25f)
            {
                ReleaseAethertithe();
            }

            return; // Keep a still-valid command rather than reissuing it each pulse.
        }
        if (!startUnsafe)
        {
            ReleaseAethertithe();
            return;
        }
        if (!InterphosAethertithe.TryDestination(start.X, start.Z, origin.X, origin.Z, angle,
            (x, z) => SafeAethertitheSegment(start, new Vector3(x, start.Y, z)), out float tx, out float tz))
        {
            ReleaseAethertithe();
            return;
        }
        // Release the stillness lease before the new owner request so a stale
        // hold cleanup cannot cancel this emergency movement.
        ReleaseStillness();
        aethertitheTarget = new Vector3(tx, start.Y, tz);
        movement.Request(aethertitheTarget, 0.25f);
        aethertitheOwned = true;
        // A logger failure must not undo the already-submitted movement request.
        try
        {
            Logger.Information(FormattableString.Invariant($"[Interphos] AETHERTITHE sector={aethertitheSector} from=({start.X:F3},{start.Z:F3}) to=({tx:F3},{tz:F3})"));
        }
        catch (Exception) { }
    }

    // The guide and captured movement support approaching the boss before
    // the first cone: from the far south, the observed edge escape began after
    // the scene signal and reached the boundary close to the lethal snapshot.
    // This direct staging request is retired or revalidated as soon as a real
    // sector appears; it is not inserted between the successive cone escapes.
    private void StageAethertithe(BattleCharacter boss)
    {
        Vector3 start = Core.Me.Location;
        Vector3 target = new(boss.Location.X, start.Y, boss.Location.Z + 12f);
        if (start.Distance2D(boss.Location) <= 14f)
        {
            ReleaseAethertithe();
            return;
        }
        if (aethertitheOwned && aethertitheTarget.Distance2D(target) <= 0.25f &&
            SafeAethertitheSegment(start, aethertitheTarget))
        {
            return;
        }

        if (!SafeAethertitheSegment(start, target))
        {
            ReleaseAethertithe();
            return;
        }
        ReleaseStillness();
        aethertitheTarget = target;
        movement.Request(target, 0.25f);
        aethertitheOwned = true;
        try
        {
            Logger.Information(FormattableString.Invariant($"[Interphos] AETHERTITHE_STAGE from=({start.X:F3},{start.Z:F3}) to=({target.X:F3},{target.Z:F3})"));
        }
        catch (Exception) { }
    }

    private bool SafeAethertitheSegment(Vector3 start, Vector3 target, AvoidInfo explicitShape = null)
    {
        if (squareFloorKnown && divide.Active && !DividePathSafe(start, target))
        {
            return false;
        }
        // A straight escape from an occupied half is permitted. Once outside,
        // the safe half is convex, so a safe endpoint cannot cross it again.
        if (squareFloorKnown && InterphosCleave.SquareUnsafe(target.X, cleave.Side))
        {
            return false;
        }

        if (target.X < 80.5f || target.X > 119.5f || target.Z < 80.5f || target.Z > 119.5f ||
            !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Square, start.X, start.Z, target.X, target.Z) ||
            InterphosWaltz.Unsafe(target.X, target.Z, waltzThreats))
        {
            return false;
        }

        if (InterphosWaltz.Unsafe(target.X, target.Z, waltzRays))
        {
            return false;
        }

        foreach (var ray in waltzRays)
        {
            if (!InterphosWaltz.Unsafe(start.X, start.Z, ray) &&
                !InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, target.X, target.Y, target.Z,
                    (x, _, z) => InterphosWaltz.Unsafe(x, z, ray)))
            {
                return false;
            }
        }
        // An escape must be allowed to leave a circle it already occupies, but
        // a safe starting point must not route through a different active bait.
        foreach (var circle in waltzThreats)
        {
            if (!InterphosWaltz.Unsafe(start.X, start.Z, circle) &&
                !InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, target.X, target.Y, target.Z,
                    (x, _, z) => InterphosWaltz.Unsafe(x, z, circle)))
            {
                return false;
            }
        }
        // A combined handler may validate one owned shape analytically so an
        // escape can leave its occupied origin; all other avoids remain in force.
        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore && !ReferenceEquals(avoid, explicitShape)).ToArray();
        return InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, target.X, target.Y, target.Z,
            (x, y, z) => avoids.Any(avoid => avoid.IsPointInAvoid(new Vector3(x, y, z))));
    }

    private void ObserveWaltz(double now)
    {
        // Read actual cast changes each pulse; only expensive bait timelines are
        // throttled. Never retain the previous fan alongside the next one.
        var casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsCasting && a.CastingSpellId == Waltz).ToArray();
        if (casters.Length > 0)
        {
            waltzRays = casters.Select(a => InterphosWaltz.FromHelper(a.Location.X, a.Location.Z, a.Heading)).ToArray();
            // Retire the disk in this same pulse, before the throttled timeline
            // read, so the cast transition cannot issue a stale disk escape.
            waltzThreats = waltzThreats.Where(c => !waltzRays.Any(ray =>
                MathF.Abs(ray.X - c.X) < 0.5f && MathF.Abs(ray.Z - c.Z) < 0.5f)).ToArray();
            // Damage followed the last active fan sample by about 0.94 seconds.
            // Retain its geometry for 0.95 seconds pending exact effect timing;
            // this stays below the three-second spacing between waves.
            waltzRayUntil = now + 0.95;
        }
        else if (now >= waltzRayUntil)
        {
            waltzRays = [];
        }

        if (now < waltzNextRead)
        {
            return;
        }

        waltzNextRead = now + 0.2;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsVisible && actor.BaseId == 0x41C4).ToArray();
        foreach (var actor in actors)
        {
            if (waltzActivations.ContainsKey(actor.ObjectId) || string.IsNullOrEmpty(actor.LuaString))
            {
                continue;
            }

            try
            {
                var value = Lua.GetReturnVal<string>("local o=_G['" + actor.LuaString + "']; if not o or not o.GetActionTimelineId then return '-1' end; return tostring(o:GetActionTimelineId())");
                // Captures show 0x11D7 only briefly before the actor
                // switched to its movement animation; retain the reference timing.
                if (value == "4567")
                {
                    waltzActivations.Add(actor.ObjectId, now + 7);
                }
            }
            catch (Exception) { /* Preserve other already observed actors. */ }
        }
        foreach (var id in waltzActivations.Keys.ToArray())
        {
            if (now >= waltzActivations[id] || !actors.Any(actor => actor.ObjectId == id))
            {
                waltzActivations.Remove(id);
            }
        }
        // The captured actor reached the player's bait point about
        // 2.6 seconds before the line cast began. The 2.5-second reference-risk
        // window issued an eight-yalm escape only 0.3 seconds before that cast;
        // begin 1.5 seconds earlier so ordinary movement can reach the boundary.
        waltzThreats = actors.Where(actor => waltzActivations.TryGetValue(actor.ObjectId, out double activation) && activation - now <= 4.0 &&
                !waltzRays.Any(ray => MathF.Abs(ray.X - actor.Location.X) < 0.5f && MathF.Abs(ray.Z - actor.Location.Z) < 0.5f))
            .Select(actor => new InterphosWaltz.Circle(actor.Location.X, actor.Location.Z)).ToArray();
    }

    // Captured icon 521 targets produce helper 36637 rays about three seconds
    // later. Combine those targeted lines with Waltz in the square request.
    private void ObserveDivide(BattleCharacter boss, double now)
    {
        divideNow = now;
        uint cast = boss?.IsCasting == true ? boss.CastingSpellId : 0;
        if (cast == 36636 && divideLastCast != cast)
        {
            divide.Begin(now);
        }

        divideLastCast = cast;
        divide.Advance(now);
        if (!squareFloorKnown || boss == null || !divide.InWindow(now))
        {
            divide.Reset();
            divideParty = [];
            return;
        }
        divideOrigin = boss.Location;
        var ids = PartyManager.VisibleMembers.Select(m => m.ObjectId).ToHashSet();
        ids.Add(Core.Me.ObjectId);
        // Include the local player: excluding self loses one of the eight baits
        // and prevents detection of the final all-party volley.
        divideParty = GameObjectManager.GetObjectsOfType<BattleCharacter>(allowInheritance: true, includeMeIfFound: true)
            .Where(a => a.IsValid && a.IsAlive && ids.Contains(a.ObjectId))
            .Select(a => new InterphosDivide.Member(a.ObjectId, a.Location.X, a.Location.Z)).ToArray();
        if (now < divideNextRead)
        {
            return;
        }

        divideNextRead = now + 0.2;
        divideReadable = divideSignal.TryRead(boss, out var markers);
        if (!divideReadable)
        {
            return;
        }

        string observed = divide.Observe(markers, divideParty, now);
        if (observed != null)
        {
            dividePending = "[Interphos] DIVIDE " + observed;
        }
    }

    private bool DividePathSafe(Vector3 start, Vector3 target) =>
        divide.SafePath(start.X, start.Z, target.X, target.Z, divideOrigin.X, divideOrigin.Z,
            Core.Me.ObjectId, divideParty, divideNow, divideSpeed);

    private bool DividePointSafe(Vector3 point) =>
        divide.SafePoint(point.X, point.Z, divideOrigin.X, divideOrigin.Z, Core.Me.ObjectId, divideParty);

    private void UpdateSquareHazards(BattleCharacter boss, double now)
    {
        // Reuse this square-hazard request for the two hand strikes. It already
        // combines active spokes/baits/cones and retains a valid destination;
        // adding a second mover or a competing native half-room would stutter.
        bool squareCleave = squareFloorKnown && cleave.Side != 0;
        if (!squareFloorKnown || (waltzThreats.Length == 0 && waltzRays.Length == 0 && !squareCleave && !divide.Active) || movement?.IsCurrent != true ||
            movement.IsEscaping || MovementManager.GenerallyOutOfControl)
        {
            ReleaseWaltz();
            return;
        }
        Vector3 start = Core.Me.Location;
        bool coneKnown = boss != null && boss.IsValid && aethertitheEnd > now && aethertitheLastSignal != 0 &&
            now - aethertitheLastSignal <= 0.4 && InterphosAethertithe.TryAngle(aethertitheSector, out _);
        float angle = 0;
        if (coneKnown)
        {
            InterphosAethertithe.TryAngle(aethertitheSector, out angle);
        }

        bool Accept(float x, float z)
        {
            var target = new Vector3(x, start.Y, z);
            if (!SafeAethertitheSegment(start, target))
            {
                return false;
            }

            if (!coneKnown)
            {
                return true;
            }

            Vector3 origin = boss.Location;
            if (InterphosAethertithe.IsUnsafe(x, z, origin.X, origin.Z, angle))
            {
                return false;
            }

            return InterphosAethertithe.IsUnsafe(start.X, start.Z, origin.X, origin.Z, angle) ||
                InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, x, start.Y, z,
                    (px, _, pz) => InterphosAethertithe.IsUnsafe(px, pz, origin.X, origin.Z, angle));
        }
        bool unsafeStart = InterphosWaltz.Unsafe(start.X, start.Z, waltzThreats) || InterphosWaltz.Unsafe(start.X, start.Z, waltzRays) ||
            squareCleave && InterphosCleave.SquareUnsafe(start.X, cleave.Side) || divide.Active && !DividePointSafe(start);
        float tx = squareCleave ? InterphosCleave.SquareDestinationX(cleave.Side) : 0, tz = start.Z;
        bool directCleave = squareCleave && Accept(tx, tz);
        // A safe first half at X=116.637 left a 17.637-yalm crossing in only
        // 3.1 seconds for the second strike. Stage near the dividing line as
        // soon as Waltz permits. Replace a distant predecessor once, then
        // retain the staged request while it remains valid.
        bool retainCleaveStage = !directCleave || Math.Abs(waltzTarget.X - tx) <= 0.25f;
        if (waltzOwned && retainCleaveStage && Accept(waltzTarget.X, waltzTarget.Z))
        {
            if (!squareCleave && !divide.Active && waltzRays.Length == 0 && !unsafeStart && start.Distance2D(waltzTarget) <= 0.25f)
            {
                ReleaseWaltz();
            }

            return;
        }
        if (!directCleave && (!unsafeStart || !InterphosWaltz.TryDestination(start.X, start.Z, waltzThreats, Accept, out tx, out tz, spokes: waltzRays.Length > 0)))
        {
            ReleaseWaltz();
            return;
        }
        ReleaseStillness();
        ReleaseAethertithe();
        waltzTarget = new Vector3(tx, start.Y, tz);
        movement.Request(waltzTarget, 0.25f);
        waltzOwned = true;
        // Flush after movement.Update so a logging failure cannot prevent input.
        waltzPending = FormattableString.Invariant($"[Interphos] WALTZ from=({start.X:F3},{start.Z:F3}) to=({tx:F3},{tz:F3}) threats={waltzThreats.Length} rays={waltzRays.Length} cleave={cleave.Side} divide={divide.Baits.Count}");
    }

    private void ReleaseWaltz()
    {
        if (!waltzOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        waltzOwned = false;
    }

    private void ReleaseAethertithe()
    {
        if (!aethertitheOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        aethertitheOwned = false;
    }

    // The helper's cast supplies the actual Gust direction. This response owns
    // one staging request until the cast ends; native emergency escape still
    // preempts it and each tick revalidates the floor and other active hazards.
    private void UpdateGust(double now)
    {
        if (!crossFloorKnown || movement?.IsCurrent != true || movement.IsEscaping || MovementManager.GenerallyOutOfControl)
        {
            ReleaseGust();
            return;
        }
        var casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId is 36610 or 36612).ToArray();
        // The observed Downburst push starts after its cast disappears. Retain
        // the safe staging request through that short effect delay, ending on
        // forced movement or 1.2 seconds after the last cast sample. Crown is later.
        if (casters.Length == 0 && gustOwned && gustAction == 36610 && now < downburstUntil &&
            SafeDownburstSegment(Core.Me.Location, gustTarget, gustOrigin))
        {
            return;
        }

        if (casters.Length != 1)
        {
            ReleaseGust();
            return;
        }
        var caster = casters[0];
        Vector3 start = Core.Me.Location;
        bool radial = caster.CastingSpellId == 36610;
        if (radial)
        {
            downburstUntil = now + 1.2;
        }

        bool SameCast() => gustOwned && gustCaster == caster.ObjectId && gustAction == caster.CastingSpellId &&
            (radial ? gustOrigin.Distance2D(caster.Location) < 0.01f : MathF.Abs(gustHeading - caster.Heading) < 0.01f);
        bool Accept(float x, float z) => radial
            ? SafeDownburstSegment(start, new Vector3(x, start.Y, z), caster.Location)
            : SafeGustSegment(start, new Vector3(x, start.Y, z), caster.Heading);
        if (SameCast() && Accept(gustTarget.X, gustTarget.Z))
        {
            return;
        }

        float tx, tz;
        bool found = radial
            ? InterphosGust.TryRadialStage(start.X, start.Z, caster.Location.X, caster.Location.Z, Accept, out tx, out tz)
            : InterphosGust.TryStage(start.X, start.Z, caster.Heading, Accept, out tx, out tz);
        if (!found)
        {
            ReleaseGust();
            return;
        }
        ReleaseStillness();
        gustCaster = caster.ObjectId;
        gustAction = caster.CastingSpellId;
        gustOrigin = caster.Location;
        gustHeading = caster.Heading;
        gustTarget = new Vector3(tx, start.Y, tz);
        movement.Request(gustTarget, 0.25f);
        gustOwned = true;
        gustPending = FormattableString.Invariant($"[Interphos] KNOCKBACK action={gustAction} caster=0x{gustCaster:X} origin=({gustOrigin.X:F3},{gustOrigin.Z:F3}) heading={gustHeading:F3} remaining={caster.SpellCastInfo.RemainingCastTime.TotalSeconds:F3} from=({start.X:F3},{start.Z:F3}) to=({tx:F3},{tz:F3})");
    }

    private static bool SafeDownburstSegment(Vector3 start, Vector3 target, Vector3 origin)
    {
        if (!InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, start.X, start.Z, target.X, target.Z) ||
            !InterphosGust.ValidRadialStage(target.X, target.Z, origin.X, origin.Z))
        {
            return false;
        }

        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        var delta = target - origin;
        float length = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        var landing = new Vector3(target.X + delta.X / length * 10, target.Y, target.Z + delta.Z / length * 10);
        bool Unsafe(float x, float y, float z) => avoids.Any(avoid => avoid.IsPointInAvoid(new Vector3(x, y, z)));
        return InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, target.X, target.Y, target.Z, Unsafe) &&
            InterphosFloor.SegmentAvoids(target.X, target.Y, target.Z, landing.X, landing.Y, landing.Z, Unsafe);
    }

    private static bool SafeGustSegment(Vector3 start, Vector3 target, float heading)
    {
        float dx = MathF.Sin(heading) * 20f, dz = MathF.Cos(heading) * 20f;
        if (!InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, start.X, start.Z, target.X, target.Z) ||
            !InterphosFloor.DirectionalKnockbackOnFloor(InterphosFloor.Layout.Cross, target.X, target.Z, dx, dz))
        {
            return false;
        }

        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        return InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, target.X, target.Y, target.Z,
            (x, y, z) => avoids.Any(avoid => avoid.IsPointInAvoid(new Vector3(x, y, z)))) &&
            !avoids.Any(avoid => avoid.IsPointInAvoid(new Vector3(target.X + dx, target.Y, target.Z + dz)));
    }

    private void ReleaseGust()
    {
        if (!gustOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        gustOwned = false;
        gustCaster = 0;
        gustAction = 0;
        downburstUntil = 0;
    }

    // Crown's positive position may require a turn at an X-floor crossbar.
    // Keep the accepted route while valid; the existing mover owns each leg.
    private void UpdateCrossCrown()
    {
        if (!crossFloorKnown || movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl)
        {
            ReleaseCrossCrown();
            return;
        }
        var casters = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsCasting && actor.CastingSpellId == BrutalCrown).ToArray();
        if (casters.Length != 1)
        {
            ReleaseCrossCrown();
            return;
        }
        var caster = casters[0];
        Vector3 start = Core.Me.Location, center = caster.Location;
        bool Accept(float ax, float az, float bx, float bz) =>
            SafeCrossCrownSegment(new Vector3(ax, start.Y, az), new Vector3(bx, start.Y, bz));
        bool reached(InterphosCrossCrown.Point p) =>
            (start.X - p.X) * (start.X - p.X) + (start.Z - p.Z) * (start.Z - p.Z) <= 0.25f * 0.25f;
        while (crossCrownStep + 1 < crossCrownRoute.Length && reached(crossCrownRoute[crossCrownStep]))
        {
            ++crossCrownStep;
        }

        bool valid = crossCrownOwned && crossCrownCaster == caster.ObjectId && crossCrownRoute.Length > 0;
        if (valid)
        {
            var goal = crossCrownRoute[^1];
            float dx = goal.X - center.X, dz = goal.Z - center.Z;
            valid = dx * dx + dz * dz <= InterphosCrossCrown.TargetRadius * InterphosCrossCrown.TargetRadius;
            float ax = start.X, az = start.Z;
            for (int i = crossCrownStep; valid && i < crossCrownRoute.Length; ++i)
            {
                var point = crossCrownRoute[i];
                valid = Accept(ax, az, point.X, point.Z);
                ax = point.X;
                az = point.Z;
            }
        }
        if (!valid)
        {
            if (!InterphosCrossCrown.TryRoute(start.X, start.Z, center.X, center.Z, Accept, out var route))
            {
                ReleaseCrossCrown();
                return;
            }
            crossCrownRoute = route;
            crossCrownStep = 0;
        }
        var next = crossCrownRoute[crossCrownStep];
        if (valid && crossCrownTarget.X == next.X && crossCrownTarget.Z == next.Z)
        {
            return;
        }
        // Retire earlier owners before submitting a newer semantic request.
        ReleaseGust();
        ReleaseStillness();
        crossCrownCaster = caster.ObjectId;
        crossCrownTarget = new Vector3(next.X, start.Y, next.Z);
        movement.Request(crossCrownTarget, 0.25f);
        crossCrownOwned = true;
        // Emit only after the movement pulse; diagnostics cannot delay this leg.
        crossCrownPending = FormattableString.Invariant($"[Interphos] CROSS_CROWN caster=0x{crossCrownCaster:X} leg={crossCrownStep + 1}/{crossCrownRoute.Length} from=({start.X:F3},{start.Z:F3}) to=({next.X:F3},{next.Z:F3})");
    }
    private bool SafeCrossCrownSegment(Vector3 start, Vector3 target)
    {
        if (!InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, start.X, start.Z, target.X, target.Z))
        {
            return false;
        }

        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore && !ReferenceEquals(avoid, brutalCrownAvoid)).ToArray();
        return InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, target.X, target.Y, target.Z,
            (x, y, z) => avoids.Any(avoid => avoid.IsPointInAvoid(new Vector3(x, y, z))));
    }

    private void ReleaseCrossCrown()
    {
        if (!crossCrownOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        crossCrownOwned = false;
        crossCrownCaster = 0;
        crossCrownRoute = [];
        crossCrownStep = 0;
    }

    private void CaptureEncounterDiagnostics()
    {
        if (CaptureMapEffects && lifecycle && WorldManager.ZoneId == 1202 &&
            DirectorManager.ActiveDirector is InstanceContentDirector director && director.IsValid)
        {
            // Log scalar changes once, on the bot thread, so capture can distinguish
            // RB's placed-effect IDs from the network indices in reference data.
            // Removals matter too: an effect can disappear rather than change state.
            var effects = director.MapEffects.ToArray();
            var present = effects.Select(effect => effect.ID).ToHashSet();
            foreach (var removed in observedMapEffects.Keys.Where(id => !present.Contains(id)).ToArray())
            {
                Logger.Information($"[InterphosCapture] MAP_REMOVED id=0x{removed:X}");
                observedMapEffects.Remove(removed);
            }
            for (int index = 0; index < effects.Length; index++)
            {
                var effect = effects[index];
                var snapshot = (effect.unk, effect.State, effect.Flags);
                if (observedMapEffects.TryGetValue(effect.ID, out var previous) && previous == snapshot)
                {
                    continue;
                }

                observedMapEffects[effect.ID] = snapshot;
                // Retained array order is recorded explicitly; a placed ID
                // alone is not a packet index.
                Logger.Information($"[InterphosCapture] MAP index={index} id=0x{effect.ID:X} state=0x{effect.State:X} flags=0x{effect.Flags:X} raw=0x{effect.unk:X}");
            }
        }
    }

    // After native escape ends, the combat routine can immediately request a path
    // back through a still-live avoid. Check the next requested segment against
    // the currently registered shapes; native escape itself remains exempt.
    // Floor boundaries are deliberately not inferred here until their live state
    // is established from the transition capture.
    private bool SafeOrdinarySegment(Vector3 destination)
    {
        var start = Core.Me.Location;
        if (squareFloorKnown && InterphosFloor.Contains(InterphosFloor.Layout.Square, start.X, start.Z) &&
            !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Square, start.X, start.Z, destination.X, destination.Z))
        {
            return false;
        }

        if (squareFloorKnown && divide.Active && !DividePathSafe(start, destination))
        {
            return false;
        }
        // Ordinary combat must not cross back into the still-active hand strike.
        // Native emergency escape and the authored square request retain their
        // existing ownership paths; this checks only ordinary next-waypoint input.
        if (squareFloorKnown && InterphosCleave.SquareUnsafe(destination.X, cleave.Side))
        {
            return false;
        }
        // With native Waltz removed, ordinary combat must also respect its
        // currently authored spokes. The authored escape uses its own route
        // validation and can leave a spoke occupied when the cast appeared.
        if (squareFloorKnown && waltzRays.Length > 0 &&
            (!InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Square, start.X, start.Z, destination.X, destination.Z) ||
             !InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, destination.X, destination.Y, destination.Z,
                 (x, _, z) => InterphosWaltz.Unsafe(x, z, waltzRays))))
        {
            return false;
        }
        // Combat/navigation can otherwise cut across a missing section of the
        // X floor. Only enforce a known layout from a modeled starting point;
        // an unmodeled starting point must retain ordinary recovery options.
        // Native avoidance is separately owned and is not covered by this gate.
        if (crossFloorKnown && InterphosFloor.Contains(InterphosFloor.Layout.Cross, start.X, start.Z) &&
            !InterphosFloor.SegmentOnFloor(InterphosFloor.Layout.Cross, start.X, start.Z, destination.X, destination.Z))
        {
            return false;
        }

        if (splitFloorKnown)
        {
            // After wall entry, ordinary combat must stay on solid platforms.
            // Authored wall/force crossings still use the gravity-aware planner.
            var layout = !splitEntryFinished && SplitGravityActive() ? InterphosFloor.Layout.Floating : InterphosFloor.Layout.Split;
            if (InterphosFloor.Contains(layout, start.X, start.Z) &&
                !InterphosFloor.SegmentOnFloor(layout, start.X, start.Z, destination.X, destination.Z))
            {
                return false;
            }
        }
        var avoids = AvoidanceManager.Avoids.Where(avoid => !avoid.ShouldIgnore).ToArray();
        return InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z,
            destination.X, destination.Y, destination.Z,
            (x, y, z) => avoids.Any(avoid => avoid.IsPointInAvoid(new Vector3(x, y, z))));
    }

    // Captures identify four VFX 327 flare targets, then VFX 55 on all
    // eight party members. These markers briefly overlap; do not move toward a
    // partner while a flare remains. Resolve both markers and actual ground
    // casts together, using party positions only as mechanic targets.
    private bool UpdateAuthority()
    {
        if (!squareFloorKnown || movement?.IsCurrent != true || MovementManager.GenerallyOutOfControl || royalSequence.Active)
        {
            ReleaseAuthority();
            return false;
        }
        var ids = PartyManager.VisibleMembers.Select(m => m.ObjectId).ToHashSet();
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsAlive && a.IsVisible && ids.Contains(a.ObjectId)).ToList();
        if (!actors.Any(a => a.ObjectId == Core.Me.ObjectId))
        {
            actors.Add(Core.Me);
        }

        bool Marker(BattleCharacter a, uint id) => a.VfxContainer.IsValid &&
            a.VfxContainer.Vfx.Any(v => v != null && v.IsValid && v.Id == id);
        // Circle waves outlast the pair marker. Keep their authored escape alive
        // until the actual casts end, including the Hold/Gaze expiry handoff.
        // Helpers are the circle origins; 8.5 includes the 8-yalm damage radius,
        // 0.25-yalm arrival tolerance and 0.25-yalm clearance.
        var ground = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsCasting && a.CastingSpellId == AuthorityCircle)
            .Select(a => new InterphosAuthority.Circle(a.Location.X, a.Location.Z, 8.5f,
                (float)a.SpellCastInfo.RemainingCastTime.TotalSeconds)).ToArray();
        if (ground.Length == 0 && !actors.Any(a => a.HasAura(AuthorityHold) || a.HasAura(AuthorityGaze)))
        {
            ReleaseAuthority();
            return false;
        }
        var members = actors.Select(a => new InterphosAuthority.Member(a.ObjectId, a.Location.X, a.Location.Z, Marker(a, 327))).ToArray();
        bool flares = members.Any(m => m.Flare), selfFlare = Marker(Core.Me, 327), pair = !flares && Marker(Core.Me, 55);
        if (!flares && !pair && ground.Length == 0)
        {
            ReleaseAuthority();
            return false;
        }
        // Flare is a proximity bait assigned to the marked players. In the
        // captured moving party, forcing an unmarked healer outside every 12-yalm
        // disk stranded her at the rim for the ensuing ground-circle/pair
        // overlap. Let marked players place their baits while unmarked players
        // continue healing; do not follow or chase their moving positions.
        if (flares && !selfFlare && ground.Length == 0)
        {
            ReleaseAuthority();
            return false;
        }
        var threats = InterphosAuthority.Threats(members, Core.Me.ObjectId, selfFlare, ground);
        Vector3 start = Core.Me.Location;
        var from = new InterphosAuthority.Point(start.X, start.Z);
        var hold = Core.Me.GetAuraById(AuthorityHold);
        if (InterphosFloor.ShouldHoldStill(hold?.TimeLeft, true, AvoidanceManager.IsRunningOutOfAvoid) &&
            ground.All(c => InterphosAuthority.DistanceSquared(from, new(c.X, c.Z)) > c.Radius * c.Radius))
        {
            ReleaseAuthority();
            ReleaseWaltz();
            ReleaseAethertithe();
            ReleaseGust();
            ReleaseCrossCrown();
            ReleaseRegalia();
            ReleaseSplit();
            UpdateStillness();
            return true;
        }
        // Own circles are explicit above even when their native registration is
        // suppressed. Other registered shapes still constrain the whole segment.
        bool OtherPathSafe(InterphosAuthority.Point p) =>
            SafeAethertitheSegment(start, new Vector3(p.X, start.Y, p.Z), authorityCircleAvoid);
        var retained = authorityOwned ? new InterphosAuthority.Point?(new(authorityTarget.X, authorityTarget.Z)) : null;
        // Speed is zero while stationary; use ordinary run speed for startup,
        // then revalidate against any observed slowdown. Boosts are never required.
        float speed = (float)MovementManager.Speed;
        if (speed <= 0)
        {
            speed = 6;
        }

        if (!InterphosAuthority.TryResolve(from, retained, members, Core.Me.ObjectId, pair, threats,
            ground.Length != 0, OtherPathSafe, out var target, out bool groundFirst, speed))
        {
            ReleaseAuthority();
            // Both the combined pair search and ground-only escape failed.
            // Preserve emergency avoidance and combat, and expose the gap after
            // input rather than falsely reporting a safe authored destination.
            if (!authorityNoRouteLogged)
            {
                authorityPending = FormattableString.Invariant($"[Interphos] AUTHORITY no-direct-route pair={pair} from=({start.X:F3},{start.Z:F3}) circles={ground.Length} members={members.Length} speed={speed:F2}");
            }

            authorityNoRouteLogged = true;
            return false;
        }
        authorityNoRouteLogged = false;
        if (authorityOwned && authorityTarget.X == target.X && authorityTarget.Z == target.Z)
        {
            return true;
        }

        ReleaseStillness();
        ReleaseWaltz();
        ReleaseAethertithe();
        ReleaseGust();
        ReleaseCrossCrown();
        ReleaseRegalia();
        ReleaseSplit();
        authorityTarget = new Vector3(target.X, start.Y, target.Z);
        authorityOwned = true;
        movement.RequestHazardExit(authorityTarget, 0.25f);
        authorityPending = FormattableString.Invariant($"[Interphos] AUTHORITY mode={(groundFirst || (!pair && !selfFlare) ? "circles" : pair ? "pair" : "flare")} from=({start.X:F3},{start.Z:F3}) to=({target.X:F3},{target.Z:F3}) circles={ground.Length} flareTargets={members.Count(m => m.Flare)}");
        return true;
    }

    private void ReleaseAuthority()
    {
        if (!authorityOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        authorityOwned = false;
    }

    // Authority's Gaze is emitted by marked players, not by the boss. Face away
    // from the other living marked party members near expiry; never treat our own
    // marker as a gaze source. Avoidance retains priority over this facing lease.
    private void UpdateGaze()
    {
        if (AvoidanceManager.IsRunningOutOfAvoid || MovementManager.GenerallyOutOfControl)
        {
            ReleaseGaze();
            return;
        }
        var sources = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.IsAlive && actor.IsVisible &&
                actor.ObjectId != Core.Me.ObjectId &&
                PartyManager.VisibleMembers.Any(member => member.ObjectId == actor.ObjectId))
            .Select(actor => new { Actor = actor, Aura = actor.GetAuraById(AuthorityGaze) })
            .Where(source => source.Aura != null && source.Aura.TimeLeft >= 0f && source.Aura.TimeLeft <= 1.5f)
            .ToArray();
        if (sources.Length == 0)
        {
            ReleaseGaze();
            return;
        }
        var relativeSources = sources.Select(source =>
            (source.Actor.Location.X - Core.Me.Location.X, source.Actor.Location.Z - Core.Me.Location.Z));
        if (!InterphosFloor.TryGazeHeading(relativeSources, out float heading))
        {
            ReleaseGaze(); // Coincident or opposing sources need captured positioning evidence.
            return;
        }
        CapabilityManager.Update(gazeFacingHandle, CapabilityFlags.Facing, 500,
            "Facing away from Authority's Gaze");
        gazeFacingOwned = true;
        Core.Me.SetFacing(heading);
    }

    private void ReleaseGaze()
    {
        if (!gazeFacingOwned)
        {
            return;
        }

        CapabilityManager.Clear(gazeFacingHandle, CapabilityFlags.Facing, "Authority's Gaze released");
        gazeFacingOwned = false;
    }

    // Authority's Hold checks movement when status 4130 expires. Keep routine
    // healing available; use the existing single input owner to hold the present
    // position only in the final second. An active emergency escape preempts it.
    private void UpdateStillness()
    {
        var aura = Core.Me.GetAuraById(AuthorityHold);
        if (!InterphosFloor.ShouldHoldStill(aura?.TimeLeft,
            movement?.IsCurrent == true, AvoidanceManager.IsRunningOutOfAvoid))
        {
            ReleaseStillness();
            return;
        }
        if (!holdingStill)
        {
            movement.Request(Core.Me.Location, 0.25f);
            holdingStill = true;
        }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement,
            aura.TimespanLeft.Add(TimeSpan.FromMilliseconds(500)), "Holding still for Authority's Hold");
    }

    private void ReleaseStillness()
    {
        if (!holdingStill)
        {
            return;
        }

        movement?.Request(null, 0);
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Authority's Hold released");
        holdingStill = false;
    }

    private void UpdateRegalia(BattleCharacter boss, double now)
    {
        if ((!crossFloorKnown && !squareFloorKnown) || movement?.IsCurrent != true)
        {
            ResetRegalia();
            return;
        }
        var helpers = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(a => a.IsValid && a.IsVisible && a.BaseId == 0x41C2).ToArray();
        if (helpers.Length != 1)
        {
            ReleaseRegalia();
            return;
        }
        var helper = helpers[0];
        if (regaliaActor != helper.ObjectId)
        {
            ResetRegalia();
            regaliaActor = helper.ObjectId;
        }
        if (now >= regaliaNextRead)
        {
            regaliaNextRead = now + 0.2;
            if (string.IsNullOrEmpty(helper.LuaString))
            {
                ResetRegalia();
                return;
            }
            int timeline = Lua.GetReturnVal<int>("local o=_G['" + helper.LuaString + "']; if not o or not o.GetActionTimelineId then return -1 end; return o:GetActionTimelineId()");
            if (timeline == InterphosRegalia.TellTimeline && regaliaTimeline != timeline)
            {
                regaliaTellAt = now;
            }

            regaliaTimeline = timeline;
        }
        if (regaliaTellAt == 0 || !InterphosRegalia.ShouldPosition(now - regaliaTellAt, squareFloorKnown) || MovementManager.GenerallyOutOfControl)
        {
            ReleaseRegalia();
            return;
        }
        var start = Core.Me.Location;
        var origin = helper.Location;
        float heading = helper.Heading;
        var layout = crossFloorKnown ? InterphosFloor.Layout.Cross : InterphosFloor.Layout.Square;
        var avoids = AvoidanceManager.Avoids.Where(a => !a.ShouldIgnore).ToArray();
        float coneAngle = 0;
        bool coneKnown = squareFloorKnown && boss != null && boss.IsValid &&
            aethertitheEnd > now && aethertitheLastSignal != 0 && now - aethertitheLastSignal <= 0.4 &&
            InterphosAethertithe.TryAngle(aethertitheSector, out coneAngle);
        Vector3 coneOrigin = coneKnown ? boss.Location : default;
        bool Accept(float x, float z)
        {
            var target = new Vector3(x, start.Y, z);
            if (!InterphosFloor.SegmentOnFloor(layout, start.X, start.Z, x, z))
            {
                return false;
            }

            if (squareFloorKnown)
            {
                if (!SafeAethertitheSegment(start, target))
                {
                    return false;
                }
            }
            else if (!InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, x, start.Y, z,
                (px, py, pz) => avoids.Any(a => a.IsPointInAvoid(new Vector3(px, py, pz)))))
            {
                return false;
            }

            if (!coneKnown)
            {
                return true;
            }

            if (InterphosAethertithe.IsUnsafe(x, z, coneOrigin.X, coneOrigin.Z, coneAngle))
            {
                return false;
            }

            return InterphosAethertithe.IsUnsafe(start.X, start.Z, coneOrigin.X, coneOrigin.Z, coneAngle) ||
                InterphosFloor.SegmentAvoids(start.X, start.Y, start.Z, x, start.Y, z,
                    (px, _, pz) => InterphosAethertithe.IsUnsafe(px, pz, coneOrigin.X, coneOrigin.Z, coneAngle));
        }
        if (regaliaOwned && !InterphosRegalia.Unsafe(regaliaTarget.X, regaliaTarget.Z, origin.X, origin.Z, heading, 0.75f) &&
            Accept(regaliaTarget.X, regaliaTarget.Z))
        {
            return;
        }

        if (!InterphosRegalia.TryPosition(start.X, start.Z, origin.X, origin.Z, heading, Accept, out float tx, out float tz, layout))
        {
            ReleaseRegalia();
            return;
        }
        // Retire earlier square/X-floor requests before acquiring the owner.
        // Native independent hazards still preempt inside that same owner.
        ReleaseWaltz();
        ReleaseAethertithe();
        ReleaseCrossCrown();
        ReleaseGust();
        ReleaseStillness();
        regaliaTarget = new Vector3(tx, start.Y, tz);
        movement.Request(regaliaTarget, 0.25f);
        regaliaOwned = true;
        regaliaPending = FormattableString.Invariant($"[Interphos] REGALIA actor=0x{regaliaActor:X} floor={layout} cone={(coneKnown ? aethertitheSector : 0)} elapsed={now - regaliaTellAt:F3} origin=({origin.X:F3},{origin.Z:F3}) heading={heading:F3} from=({start.X:F3},{start.Z:F3}) to=({tx:F3},{tz:F3})");
    }

    private void ReleaseRegalia()
    {
        if (!regaliaOwned)
        {
            return;
        }

        movement?.Request(null, 0);
        regaliaOwned = false;
    }

    private void ResetRegalia()
    {
        ReleaseRegalia();
        regaliaActor = 0;
        regaliaTimeline = 0;
        regaliaTellAt = regaliaNextRead = 0;
        regaliaPending = null;
    }

    private void UpdateRoyalSequence(double now)
    {
        var helpers = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid && actor.BaseId == 0x233C).ToArray();
        foreach (var actor in helpers)
        {
            if (actor.IsCasting && actor.CastingSpellId == RoyalBanishment)
            {
                royalSequence.Observe(actor.ObjectId, actor.SpellCastInfo.RemainingCastTime.TotalSeconds, now);
            }
        }

        royalSequence.Advance(now);
        // Queue a scalar diagnostic; the output sink runs after movement.Update.
        string group = string.Join(",", helpers.Where(actor => royalSequence.Includes(actor.ObjectId))
            .Select(actor => actor.ObjectId.ToString("X")).OrderBy(id => id));
        if (group == royalGroup)
        {
            return;
        }

        royalGroup = group;
        royalGroupPending = $"[Interphos] ROYAL_GROUP actors={group} pending={royalSequence.Count}";
    }
    private void OnPulse(object sender, EventArgs args) => UpdateMovement();
    private void OnStop(ff14bot.AClasses.BotBase bot) => EndLifecycle();
    private void OnHooksCleared(object sender, EventArgs args) => EndLifecycle();
    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId != 1202)
        {
            EndLifecycle();
        }
    }

    private void EndLifecycle()
    {
        lifecycle = false;
        openingApproachArmed = false;
        ReleaseOpeningApproach();
        openingApproachPending = null;
        ff14bot.NeoProfile.BotEvents.OnPulse -= OnPulse;
        TreeRoot.OnStop -= OnStop;
        TreeHooks.Instance.OnHooksCleared -= OnHooksCleared;
        GameEvents.OnMapChanged -= OnMapChanged;
        ReleaseStillness();
        ReleaseSplit();
        ReleaseWaltz();
        ReleaseAethertithe();
        ReleaseGust();
        ReleaseCrossCrown();
        ReleaseGaze();
        ReleaseAuthority();
        ResetRegalia();
        movement?.Detach();
        movement = null;
        moverReplaced = false;
        observedMapEffects.Clear();
        ResetSplitPattern();
        waltzActivations.Clear();
        waltzThreats = [];
        waltzRays = [];
        waltzRayUntil = 0;
        waltzNextRead = 0;
        waltzPending = null;
        cleave.Reset();
        royalSequence.Reset();
        royalGroup = string.Empty;
        aethertitheSignal.Reset();
        divideSignal.Reset();
        divide.Reset();
        divideParty = [];
        divideLastCast = 0;
        divideNextRead = divideNow = nextDivideCapture = 0;
        divideSpeed = 6;
        dividePending = null;
        divideReadable = false;
        divideMovementSpeed.Reset();
        LoggingHelpers.ClearActorSignalWatch("Interphos.DivideHelpers");
        cleavePending = null;
        cleaveCaptureUntil = nextCleaveCapture = 0;
        squareFloorKnown = crossFloorKnown = splitFloorKnown = aethertitheWindup = aethertitheSeenSignal = false;
        pendingFloorShift = 0;
        aethertitheEnd = aethertitheNextRead = aethertitheLastSignal = 0;
        aethertitheSector = 0;
        visualCapture.Reset();
        diagnosticFailed = false;
        aethertitheGateLogged = false;
        splitGateLogged = false;
        // Remove exactly the shapes owned by this instance, including a bot stop
        // that does not pass through the ordinary dungeon-exit callback.
        foreach (var avoid in registeredAvoids)
        {
            AvoidanceManager.RemoveAvoid(avoid);
        }

        registeredAvoids.Clear();
        brutalCrownAvoid = null;
        authorityCircleAvoid = null;
        authorityPending = null;
        authorityNoRouteLogged = false;
        if (sideStepBorrowed && SidestepPlugin != null && !SidestepPlugin.Enabled)
        {
            SidestepPlugin.Enabled = savedSideStep;
        }

        sideStepBorrowed = false;
    }

    // Keep normal mechanics confined to this territory and actual encounter actor.
    private static bool IsEncounterActive() => WorldManager.ZoneId == 1202 &&
        GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.IsVisible && actor.IsAlive && actor.NpcId == QueenEternal);
}
