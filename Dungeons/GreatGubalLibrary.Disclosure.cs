using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Data;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Behavior;
using ff14bot.Interfaces;
using ff14bot.Managers;
using ff14bot.Navigation;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public partial class GreatGubalLibrary
{
    private DisclosureMover disclosureMover;
    private DateTime disclosureUntil;
    private uint disclosureCaster;
    private Vector3 disclosureStage, disclosureDestination;
    private bool disclosureStaged, disclosureLeased, disclosureUnsupported;

    private void UpdateBossOwnership()
    {
        uint bossBaseId = WorldManager.SubZoneId switch
        {
            (uint)SubZoneId.HallofMagicks => 0xE82,
            (uint)SubZoneId.AstrologyandAstromancyCamera => 0xE83,
            (uint)SubZoneId.RhapsodiesQuadrangle => 0xE84,
            _ => 0
        };
        // Keep one saved plugin state throughout each boss, including add and
        // invulnerability phases; restore it on death, loading, stop or exit.
        // A living real boss plus player combat scopes the lease away from trash.
        bool bossActive = TreeRoot.IsRunning && !CommonBehaviors.IsLoading && !QuestLogManager.InCutscene &&
            Core.Me != null && Core.Me.IsValid && Core.Me.IsAlive && Core.Me.InCombat &&
            bossBaseId != 0 &&
            GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Any(actor =>
                actor.IsValid && actor.IsAlive && actor.BaseId == bossBaseId);
        if (!bossActive) { ReleaseBossOwnership(); return; }
        if (bossSideStep == null && SidestepPlugin != null)
        {
            bossSideStep = SidestepPlugin.Enabled;
            SidestepPlugin.Enabled = false;
            Logger.Information("[Gubal] Boss 0x{0:X} active; SideStep suspended.", bossBaseId);
        }
    }

    private void ReleaseBossOwnership()
    {
        ReleaseDisclosure();
        if (bossSideStep == null) return;
        if (SidestepPlugin != null) SidestepPlugin.Enabled = bossSideStep.Value;
        bossSideStep = null;
        Logger.Information("[Gubal] Boss ownership released; SideStep restored.");
    }

    // The two native trials failed on the book collision edge even though a
    // previous visit crossed successfully. Stage outside that edge before
    // traversing the book; the unsafe floor is crossed before cast resolution.
    private bool UpdateDisclosure()
    {
        if (!ReferenceEquals(active, this) || !TreeRoot.IsRunning || CommonBehaviors.IsLoading ||
            QuestLogManager.InCutscene || Core.Me == null || !Core.Me.IsValid || !Core.Me.IsAlive ||
            !Core.Me.InCombat || WorldManager.SubZoneId != (uint)SubZoneId.HallofMagicks || bossSideStep == null)
        {
            ReleaseDisclosure();
            return false;
        }

        var caster = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).FirstOrDefault(actor =>
            actor.IsValid && actor.IsCasting && actor.CastingSpellId == EnemyAction.Disclosure && actor.SpellCastInfo.IsValid);
        DateTime now = DateTime.UtcNow;
        if (caster != null && ownsDisclosureOverride)
        {
            if (disclosureCaster != caster.ObjectId || now >= disclosureUntil)
            {
                ReleaseDisclosure();
                // Live helpers face along X. Reject unknown orientations instead
                // of manufacturing a route for a different arena or mechanic.
                float direction = (float)Math.Sin(caster.Heading);
                if (Math.Abs(direction) < .9f) return false;
                float side = Core.Me.Z < 0 ? -1 : 1;
                disclosureStage = new(.5f + Math.Sign(direction) * 10f, 0f, side * 7.5f);
                disclosureDestination = new(.5f - Math.Sign(direction) * 9f, 0f, side * 7.5f);
                disclosureCaster = caster.ObjectId;
                // A character already behind the book must not be routed back
                // across the closing half merely to visit the entry waypoint.
                disclosureStaged = Math.Sign(direction) * (Core.Me.X - .5f) < -5.5f;
                Logger.Information("[Gubal] Disclosure staged route: {0} then {1}.", disclosureStage, disclosureDestination);
            }
            disclosureUntil = now + caster.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(.5);
        }
        if (disclosureMover == null)
        {
            if (Navigator.PlayerMover?.GetType() != typeof(SlideMover))
            {
                if (!disclosureUnsupported) Logger.Warning("[Gubal] Disclosure mover unavailable: another owner is installed.");
                disclosureUnsupported = true;
                return false;
            }
            disclosureMover = new DisclosureMover(Navigator.PlayerMover, () => disclosureLeased);
            Navigator.PlayerMover = disclosureMover;
        }
        if (!disclosureMover.IsCurrent) { ReleaseDisclosure(); return false; }
        disclosureMover.CompleteEscape();
        if (disclosureCaster == 0 || now >= disclosureUntil)
        {
            ReleaseDisclosureMovement();
            disclosureCaster = 0;
            disclosureUntil = DateTime.MinValue;
            disclosureStaged = false;
            return false;
        }
        if (AvoidanceManager.IsRunningOutOfAvoid)
        {
            disclosureMover.YieldToSuccessor();
            return false;
        }
        if (MovementManager.GenerallyOutOfControl || Math.Abs(Core.Me.Y) > .5f) return false;

        // The captured arena is rectangular, 39 by 20 yalms. Do not claim a
        // route if another active hazard covers the next segment or the floor is
        // unknown. Native hazards retain priority over the staged crossing.
        if (!disclosureStaged && Core.Me.Location.Distance2D(disclosureStage) <= 1.25f)
        {
            disclosureStaged = true;
            Logger.Information("[Gubal] Disclosure entry waypoint reached at {0}.", Core.Me.Location);
        }
        Vector3 destination = disclosureStaged ? disclosureDestination : disclosureStage;
        if (!DisclosurePathClear(Core.Me.Location, destination))
        {
            ReleaseDisclosureMovement();
            return false;
        }
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.Movement, TimeSpan.FromSeconds(1), "Gubal Disclosure route");
        CapabilityManager.Update(CapabilityHandle, CapabilityFlags.GapCloser, TimeSpan.FromSeconds(1), "Gubal Disclosure route");
        disclosureLeased = true;
        if (Core.Me.Location.Distance2D(destination) <= 1.25f)
        {
            disclosureMover.Hold();
            return false;
        }
        disclosureMover.RouteTo(destination);
        return true;
    }

    private static bool DisclosurePathClear(Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(from.Distance2D(to) / .5f));
        for (int i = 1; i <= steps; ++i)
        {
            float t = (float)i / steps;
            var point = new Vector3(from.X + (to.X - from.X) * t, 0, from.Z + (to.Z - from.Z) * t);
            if (point.X < -19.5f || point.X > 20f || Math.Abs(point.Z) > 9.5f ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(point))) return false;
        }
        return true;
    }

    private void ReleaseDisclosureMovement()
    {
        disclosureMover?.ReleaseRoute();
        if (!disclosureLeased) return;
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.Movement, "Gubal Disclosure released");
        CapabilityManager.Clear(CapabilityHandle, CapabilityFlags.GapCloser, "Gubal Disclosure released");
        disclosureLeased = false;
    }

    private void ReleaseDisclosure()
    {
        ReleaseDisclosureMovement();
        disclosureMover?.Detach();
        disclosureMover = null;
        disclosureCaster = 0;
        disclosureUntil = DateTime.MinValue;
        disclosureStaged = false;
        disclosureUnsupported = false;
    }

    // Every move and stop passes through the borrowed native channel. An explicit
    // stop or newer native escape revokes the route's cleanup claim immediately.
    private sealed class DisclosureMover : IPlayerMover
    {
        private readonly IPlayerMover inner;
        private readonly Func<bool> routeLeased;
        private bool routeOutstanding, escapeOutstanding, detached;
        private Vector3 lastDestination;
        private DateTime nextCommandUtc;
        internal DisclosureMover(IPlayerMover inner, Func<bool> routeLeased)
        {
            this.inner = inner;
            this.routeLeased = routeLeased;
        }
        internal bool IsCurrent => !detached && ReferenceEquals(Navigator.PlayerMover, this);
        public void MoveTowards(Vector3 location)
        {
            if (!IsCurrent) return;
            // Ordinary combat travel cannot cross back into the closing book;
            // a native escape is always allowed to preempt this route.
            if (routeLeased() && !AvoidanceManager.IsRunningOutOfAvoid) return;
            routeOutstanding = false;
            escapeOutstanding = AvoidanceManager.IsRunningOutOfAvoid;
            inner.MoveTowards(location);
        }
        public void MoveStop()
        {
            if (!IsCurrent) return;
            routeOutstanding = false;
            escapeOutstanding = false;
            inner.MoveStop();
        }
        internal void RouteTo(Vector3 destination)
        {
            if (!IsCurrent || AvoidanceManager.IsRunningOutOfAvoid) return;
            if (routeOutstanding && destination == lastDestination && DateTime.UtcNow < nextCommandUtc) return;
            routeOutstanding = true;
            escapeOutstanding = false;
            lastDestination = destination;
            nextCommandUtc = DateTime.UtcNow.AddMilliseconds(250);
            inner.MoveTowards(destination);
        }
        internal void Hold() => ReleaseRoute();
        internal void YieldToSuccessor() => routeOutstanding = false;
        internal void CompleteEscape()
        {
            if (!IsCurrent || !escapeOutstanding || AvoidanceManager.IsRunningOutOfAvoid ||
                MovementManager.GenerallyOutOfControl ||
                AvoidanceManager.Avoids.Any(avoid => !avoid.ShouldIgnore && avoid.IsPointInAvoid(Core.Me.Location))) return;
            MoveStop();
            Logger.Information("[Gubal] Released completed Demon Tome escape input at {0}.", Core.Me.Location);
        }
        internal void ReleaseRoute()
        {
            if (IsCurrent && routeOutstanding && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
                MoveStop();
            else routeOutstanding = false;
        }
        internal void Detach()
        {
            if (IsCurrent)
            {
                ReleaseRoute();
                if (escapeOutstanding && !AvoidanceManager.IsRunningOutOfAvoid && !MovementManager.GenerallyOutOfControl)
                    MoveStop();
                Navigator.PlayerMover = inner;
            }
            routeOutstanding = false;
            detached = true;
        }
    }
}
