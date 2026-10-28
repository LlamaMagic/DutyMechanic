using DutyMechanic.Data;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using Clio.Utilities;
using ff14bot;
using ff14bot.AClasses;
using ff14bot.Helpers;
using ff14bot.Behavior;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Navigation;
using ff14bot.NeoProfiles;
using LlamaLibrary.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

/// <summary>
/// Handles Thunderstorm rescues and Rolling Thunder pickups in Ramuh's Hard trial.
/// OrderBot retains targeting and SideStep retains Thunderspark and Arbiter lines.
/// </summary>
/// <remarks>
/// Positive positioning shares one navigation gate with ordinary combat movement.
/// Native avoidance retains escape priority. See docs/StrikingTreeHard.md for
/// mechanic evidence, runtime constraints and verification coverage.
/// </remarks>
public sealed partial class StrikingTreeHard : AbstractDungeon
{
    private const string SignalScope = "StrikingTreeHard";
    private DateTime nextSample;
    private readonly Dictionary<uint, string> objects = new();
    private readonly Dictionary<uint, DateTime> rejectedOrbs = new();
    private readonly Dictionary<uint, DateTime> thunderTargets = new();
    private MovementGate movement;
    private bool positioning;
    private uint rescueTarget;
    private uint orbTarget;
    private uint bossTarget;
    private Vector3 lastOrbPosition;
    private DateTime lastOrbProgress;
    private DateTime resumePositioningAt;
    private string stage;
    private bool ownsOverride;
    private bool ownsGroundOverride;
    private bool overrideAttempted;
    private bool reportedMover;
    private bool movementLost;

    // Hard-mode identities verified against helper casts and status changes.
    // Event-object targetability stays false even while a pickup is available.
    private const uint Ramuh = 0xBAB;
    private const uint RamuhHelper = 0xBAC;
    private const uint GroundThunderstorm = 2277;
    private const uint Thunderstorm = 2278;
    private const uint LightningOrb = 0x1E948A;
    private const uint Terror = 66;
    private const uint Conductivity = 512;
    private const uint Causality = 514;
    private const uint Protection = 517;
    // The layout's perimeter collision boxes have inner faces at roughly 25y.
    // Keep half a yalm inside them. Supply scalar centers below: RB also derives a
    // combat-range preference from the first BattleCharacter in an avoid collection,
    // independently of the explicit leash. A hazard carrier is not a melee destination.
    private static readonly Vector3 ArenaCenter = new(0, 75, 0);
    private const float ArenaRadius = 24.5f;
    // CapabilityManager accepts one capability per Update call even though the
    // enum is flagged. Combining these throws before the first movement request.
    private static readonly CapabilityFlags[] PositionCapabilities =
        [CapabilityFlags.Movement, CapabilityFlags.GapCloser, CapabilityFlags.MoveBehind];

    /// <inheritdoc/>
    public override ZoneId ZoneId => (ZoneId)374;
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToFollowDodge { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToTankBust { get; } = [];
    /// <inheritdoc/>
    protected override HashSet<uint> SpellsToMitigate { get; } = [];

    /// <inheritdoc/>
    protected override Task<bool> EnterDungeonAsync()
    {
        ResetCapture();
        TreeRoot.OnStop += OnStop;
        GameEvents.OnMapChanged += OnMapChanged;
        // SideStep retains Thunderspark (2275) and Arbiter lines (2282). Register
        // Thunderstorm as scalar centers to avoid attracting escape paths toward
        // its helper. Both five-yalm impacts include a half-yalm safety margin.
        EnsureOverride();
        AvoidanceManager.AddAvoidLocation<Vector3>(EncounterActive,
            () => ArenaCenter, ArenaRadius, _ => 5.5f,
            point => point,
            () => GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
                .Where(actor => actor.IsValid && actor.BaseId == RamuhHelper && actor.IsCasting
                    && actor.CastingSpellId == GroundThunderstorm)
                .Select(actor => actor.SpellCastInfo.CastLocation));
        AvoidanceManager.AddAvoidLocation<Vector3>(EncounterActive,
            () => ArenaCenter, ArenaRadius, _ => 5.5f,
            point => point, () => ThunderstormExclusions().Select(actor => actor.Location));
        // Shock Strike is an instant five-yalm target circle. Its victim holds the
        // boss; other party members leave a half-yalm safety margin around that victim.
        AvoidanceManager.AddAvoidLocation<Vector3>(EncounterActive,
            () => ArenaCenter, ArenaRadius, _ => 5.5f,
            point => point,
            () => LivingParty().Where(actor => actor.ObjectId == bossTarget && actor.ObjectId != Core.Me.ObjectId)
                .Select(actor => actor.Location));
        Logger.Information("[StrikingTree] Hard-mode handler registered: Terror rescue and Causality pickups.");
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    protected override Task<bool> ExitDungeonAsync()
    {
        ResetCapture();
        TreeRoot.OnStop -= OnStop;
        GameEvents.OnMapChanged -= OnMapChanged;
        ResetMovement();
        RestoreOverride();
        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public override Task<bool> RunAsync()
    {
        CaptureSignals();
        if (!EncounterActive())
        {
            ResetMovement();
            return Task.FromResult(false);
        }
        if (movement != null && !movement.IsCurrent)
            LoseMovementOwnership();
        if (movementLost)
            return Task.FromResult(false);
        if (movement == null)
        {
            // The captured host uses SlideMover. Do not stack this input owner over
            // an unknown controller with its own lifetime and cleanup rules.
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
            {
                if (!reportedMover)
                    Logger.Information("[StrikingTree] Another mover owns input; semantic positioning deferred.");
                reportedMover = true;
                return Task.FromResult(false);
            }
            movement = new MovementGate(this);
        }
        EnsureOverride();
        movement.CompleteEscape();
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Where(actor => actor.IsValid).ToArray();
        foreach (var caster in actors.Where(actor => actor.IsCasting && actor.CastingSpellId == Thunderstorm))
        {
            // Keep the marker through its hit, not just until the cast bar disappears.
            // One second covers effect delivery plus the capture's half-second sampling.
            thunderTargets[caster.SpellCastInfo.TargetId] = now + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(1);
        }
        foreach (uint id in thunderTargets.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray())
            thunderTargets.Remove(id);

        var party = LivingParty();
        var boss = actors.FirstOrDefault(actor => actor.BaseId == Ramuh && actor.IsAlive);
        // Choreography casts temporarily target Ramuh himself. Retain the last
        // living player victim across those casts, but never across a wipe or exit.
        if (boss != null && party.Any(member => member.ObjectId == boss.CurrentTargetId))
            bossTarget = boss.CurrentTargetId;
        bool linked = Core.Me.HasAura(Causality);
        // A cast started before the status arrived is still punished on completion.
        // Cancel that cast without selecting an alternative action for the routine.
        if (linked && Core.Me.IsCasting)
            ActionManager.StopCasting();
        rescueTarget = StrikingTreeHardPolicy.RescueTarget(Core.Me.ObjectId, party.Select(member =>
            new StrikingTreeHardPolicy.Member(member.ObjectId, member.Location,
                thunderTargets.ContainsKey(member.ObjectId), member.HasAura(Terror))).ToArray());
        var victim = party.FirstOrDefault(member => member.ObjectId == rescueTarget);

        // Releasing a competing combat request cannot interrupt native escape. A
        // Terror victim cannot move; keep ordinary recovery/healing schedulable.
        bool avoiding = AvoidanceManager.IsRunningOutOfAvoid || InHazard();
        if (avoiding || Core.Me.HasAura(Terror) || MovementManager.GenerallyOutOfControl)
        {
            if (avoiding)
                resumePositioningAt = now.AddMilliseconds(300);
            ReleasePosition();
            return Task.FromResult(false);
        }
        if ((victim != null || linked) && now < resumePositioningAt)
        {
            // Moving markers can repeatedly interrupt a rescue. Require 300ms clear
            // before resuming, holding through the same owner with escape priority.
            // Retain scheduling here: an awaited heal could outlast the rescue window.
            Position(Core.Me.Location, 0.75f, "Waiting for avoidance to settle");
            return Task.FromResult(true);
        }
        if (victim != null)
        {
            // Awaited routine casts can leave navigation unpulsed past the marker
            // window. Travel owns scheduling; healing resumes on arrival while the
            // movement hold keeps the carrier within the five-yalm impact radius.
            bool travelling = Core.Me.Location.Distance2D(victim.Location) > 3.5f;
            if (travelling && Core.Me.IsCasting)
                ActionManager.StopCasting();
            Position(victim.Location, 3.5f, "Thunderstorm rescue");
            return Task.FromResult(linked || travelling);
        }
        if (!linked)
        {
            orbTarget = 0;
            rejectedOrbs.Clear();
            ReleasePosition();
            return Task.FromResult(false);
        }

        int needed = 3 - Math.Max(0, Core.Me.Auras.GetAuraStacksById(Conductivity));
        if (Core.Me.HasAura(Protection) || needed <= 0)
        {
            // Status delivery may span frames. Never start a fourth pickup while
            // waiting for protection to remove Causality on both linked players.
            orbTarget = 0;
            Position(Core.Me.Location, 0.75f, "Waiting for Causality removal");
            return Task.FromResult(true);
        }
        var orbs = GameObjectManager.GetObjectsOfType<EventObject>().Where(actor => actor.IsValid
            && actor.BaseId == LightningOrb && actor.IsVisible
            && (!rejectedOrbs.TryGetValue(actor.ObjectId, out var until) || until <= now)).ToArray();
        var selected = orbs.FirstOrDefault(actor => actor.ObjectId == orbTarget);
        if (selected == null)
        {
            // Start with the available pickups rather than waiting for three to
            // coexist. Another player can consume one en route, so aura stacks and
            // current visibility determine the next leg instead of a saved counter.
            uint choice = StrikingTreeHardPolicy.FirstOrb(Core.Me.Location, needed,
                orbs.Select(actor => new StrikingTreeHardPolicy.Orb(actor.ObjectId, actor.Location)).ToArray());
            selected = orbs.FirstOrDefault(actor => actor.ObjectId == choice);
            orbTarget = choice;
            lastOrbPosition = Core.Me.Location;
            lastOrbProgress = now;
        }
        if (selected != null)
        {
            if (Core.Me.Location.Distance2D(lastOrbPosition) > 0.5f)
            {
                lastOrbPosition = Core.Me.Location;
                lastOrbProgress = now;
            }
            if (now - lastOrbProgress > TimeSpan.FromSeconds(3))
            {
                // A blocked or already-consumed pickup must not pin the bot. Try a
                // different available route, then allow this object again after a delay.
                rejectedOrbs[orbTarget] = now.AddSeconds(4);
                orbTarget = 0;
                ReleasePosition();
            }
            else
                Position(selected.Location, 0.75f, "Rolling Thunder orb");
        }
        else
            Position(Core.Me.Location, 0.75f, "Waiting for Rolling Thunder orbs");

        // Causality punishes every action. Yielding ordinary combat here would keep
        // casting through the tether; death/loading/native escape were handled above.
        // No job action is selected by this handler. The routine resumes immediately
        // when either partner removes Causality, including while waiting for more orbs.
        return Task.FromResult(true);
    }

    private static bool EncounterActive() => TreeRoot.IsRunning && Core.Me != null && Core.Me.IsAlive
        && DutyManager.InInstance && WorldManager.ZoneId == 374 && Core.Me.InCombat
        && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene
        && GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false)
            .Any(actor => actor.IsValid && actor.BaseId == Ramuh && actor.IsAlive);

    private static BattleCharacter[] LivingParty() => PartyManager.VisibleMembers.Select(member => member.BattleCharacter)
        .Append(Core.Me).Where(actor => actor != null && actor.IsValid && actor.IsAlive)
        .DistinctBy(actor => actor.ObjectId).ToArray();

    private static bool InHazard() => AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location));

    private IEnumerable<BattleCharacter> ThunderstormExclusions()
    {
        if (Core.Me == null || Core.Me.HasAura(Terror))
            return [];
        bool marked = thunderTargets.TryGetValue(Core.Me.ObjectId, out var until) && until > DateTime.UtcNow;
        // A required rescue takes precedence over ordinary spreading from unmarked
        // allies. Otherwise a generic escape can run away before rescue travel starts.
        // Other marked carriers remain unsafe: two hits must never be stacked deliberately.
        return LivingParty().Where(actor => actor.ObjectId != Core.Me.ObjectId && actor.ObjectId != rescueTarget
            && (marked && rescueTarget == 0 || thunderTargets.TryGetValue(actor.ObjectId, out var end) && end > DateTime.UtcNow));
    }

    private void Position(Vector3 point, float tolerance, string reason)
    {
        // Gap closers and positional attacks can relocate the player without a
        // navigator request, so their routine capabilities share this same lease.
        positioning = true;
        // The routine may await longer than a short timed lease while holding a
        // rescue position. Tie suppression to this owner; stop/map/death/replacement
        // cleanup clears it, and the predicate also rejects inactive encounters.
        foreach (var capability in PositionCapabilities)
            CapabilityManager.Update(CapabilityHandle, capability, () => positioning && EncounterActive(), reason);
        if (stage != reason)
        {
            Logger.Information($"[StrikingTree] {reason}; target={point}; player={Core.Me.Location}.");
            stage = reason;
        }
        movement.Approach(point, tolerance);
    }

    private void ReleasePosition()
    {
        if (!positioning)
            return;
        positioning = false;
        movement?.ReleasePosition();
        foreach (var capability in PositionCapabilities)
            CapabilityManager.Clear(CapabilityHandle, capability, "Striking Tree mechanic released");
        Logger.Information($"[StrikingTree] Released {stage}.");
        stage = null;
    }

    private void ResetMovement()
    {
        ReleasePosition();
        movement?.Detach();
        movement = null;
        thunderTargets.Clear();
        rejectedOrbs.Clear();
        rescueTarget = orbTarget = bossTarget = 0;
        resumePositioningAt = DateTime.MinValue;
        reportedMover = false;
        movementLost = false;
    }

    private void LoseMovementOwnership()
    {
        ReleasePosition();
        movement?.Detach();
        movement = null;
        movementLost = true;
        Logger.Information("[StrikingTree] Movement controller replaced; yielding until encounter reset.");
    }

    private void EnsureOverride()
    {
        if (overrideAttempted)
            return;
        ownsOverride = SideStep.Override(Thunderstorm);
        ownsGroundOverride = SideStep.Override(GroundThunderstorm);
        overrideAttempted = true;
    }

    private void RestoreOverride()
    {
        if (ownsOverride)
            SideStep.RemoveHandler(Thunderstorm);
        if (ownsGroundOverride)
            SideStep.RemoveHandler(GroundThunderstorm);
        ownsOverride = false;
        ownsGroundOverride = false;
        overrideAttempted = false;
    }

    private void OnStop(BotBase bot)
    {
        ResetMovement();
        RestoreOverride();
    }

    private void OnMapChanged(object sender, EventArgs args)
    {
        if (WorldManager.ZoneId == 374)
            return;
        ResetMovement();
        RestoreOverride();
    }

    private void CaptureSignals()
    {
        if (!LoggingHelpers.MechanicDiagnosticsEnabled || Core.Player == null
            || !DutyManager.InInstance || WorldManager.ZoneId != 374
            || CommonBehaviors.IsLoading || QuestLogManager.InCutscene)
        {
            ResetCapture();
            return;
        }
        if (DateTime.UtcNow < nextSample)
            return;
        // Half-second snapshots expose short marker windows without retaining native
        // wrappers or turning the diagnostic recorder into another movement owner.
        nextSample = DateTime.UtcNow.AddMilliseconds(500);
        LoggingHelpers.LogActorSignalChanges(SignalScope, actor =>
            actor.IsNpc || actor.ObjectId == Core.Player.ObjectId
            || PartyManager.AllMembers.Any(member => member.ObjectId == actor.ObjectId));

        var current = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(actor => actor.IsValid).ToArray();
        foreach (var actor in current)
        {
            string state = $"base={actor.BaseId:X} location={actor.Location} visible={actor.IsVisible} targetable={actor.IsTargetable}";
            if (!objects.TryGetValue(actor.ObjectId, out string previous) || previous != state)
            {
                objects[actor.ObjectId] = state;
                Logger.Information($"[StrikingTree] OBJECT id={actor.ObjectId:X} {state}.");
            }
        }
        foreach (uint id in objects.Keys.Where(id => current.All(actor => actor.ObjectId != id)).ToArray())
        {
            Logger.Information($"[StrikingTree] OBJECT_REMOVED id={id:X} {objects[id]}.");
            objects.Remove(id);
        }
    }

    private void ResetCapture()
    {
        nextSample = DateTime.MinValue;
        objects.Clear();
        LoggingHelpers.ClearActorSignalWatch(SignalScope);
    }
}
