using System;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using DutyMechanic.Logging;
using ff14bot;
using ff14bot.Enums;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;

public sealed partial class DancingPlague
{
    private const uint WaterPool = 0x1EABE5, FlameSpirit = 0x2878, FlameStackMarker = 62;
    private readonly CapabilityManagerHandle waterHandle = CapabilityManager.CreateNewHandle();
    private DateTime poolsSeenAt, mistWatchUntil, flameUntil, stackUntil;
    private uint stackActor;
    private Vector3? waterDestination;
    private Vector3? waterAlternative;
    private DateTime waterAlternativeSince;
    private bool waterOwned, waterLeased, mistCasting, soakKnown, flameCasting, markerCaptured, waterArrivalLogged, waterFailureLogged;

    private void UpdateWater()
    {
        if (movement == null || !movement.IsCurrent)
        {
            ResetWater(false);
            return;
        }
        var now = DateTime.UtcNow;
        var pools = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(o => o.IsValid && o.IsVisible && o.BaseId == WaterPool && o.Location.Distance2D(Center) < 12)
            .Select(o => o.Location).ToArray();
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        bool mistCast = actors.Any(a => a.BaseId == Titania && a.IsCasting && a.CastingSpellId == 15685);
        if (mistCast && !mistCasting) mistWatchUntil = now.AddSeconds(12);
        mistCasting = mistCast;
        bool flameCast = actors.Any(a => a.BaseId == Titania && a.IsCasting && a.CastingSpellId == 15687);
        if (flameCast && !flameCasting)
        {
            // The November 8 self-stack hit occurred 17s after this cast was
            // observed. This is a bounded watch, not a fixed impact prediction.
            flameUntil = now.AddSeconds(25);
            stackActor = 0;
            stackUntil = default;
            markerCaptured = false;
            waterDestination = null;
        }
        flameCasting = flameCast;
        if (pools.Length == 0)
        {
            bool releaseRequest = frost.Count == 0 && !addOwned;
            if (poolsSeenAt != default) ResetWater(releaseRequest);
            else ReleaseWater(releaseRequest);
            return;
        }
        if (poolsSeenAt == default)
        {
            poolsSeenAt = now;
            // If the bot attached after pools already existed, their activation
            // deadline is unknown. Do not manufacture a new soak window.
            soakKnown = now <= mistWatchUntil;
        }
        bool soaking = soakKnown && (now - poolsSeenAt).TotalSeconds < 11;
        bool flameActive = now < flameUntil && actors.Any(a => a.BaseId == FlameSpirit && a.IsValid);
        var party = actors.Where(a => a.IsAlive && (a.ObjectId == Core.Me.ObjectId ||
            PartyManager.VisibleMembers.Any(p => p.ObjectId == a.ObjectId))).ToList();
        if (party.All(a => a.ObjectId != Core.Me.ObjectId)) party.Add(Core.Me);
        // The synchronized self-stack capture exposed 62 in the actor's Vfx
        // collection; the generic LockOns collection is a different surface.
        var marked = flameActive ? party.Where(a => a.VfxContainer.IsValid &&
            a.VfxContainer.Vfx.Any(v => v != null && v.IsValid && v.Id == FlameStackMarker)).ToArray() : [];
        if (marked.Length == 1 && !markerCaptured)
        {
            stackActor = marked[0].ObjectId;
            markerCaptured = true;
            // The observed marker preceded the hit by about 5.1s. Bound the
            // fallback so missing removal cannot retain movement indefinitely.
            stackUntil = now.AddSeconds(6.0);
            waterDestination = null;
        }
        bool stack = stackActor != 0 && now < stackUntil;
        bool preposition = flameActive && !markerCaptured;
        if (frost.Count != 0 || addOwned)
        {
            // Keep the bounded pool/marker clocks, but do not clear or replace
            // a successor mechanic's request on the shared mover.
            ReleaseWater(false);
            return;
        }
        if (marked.Length > 1)
        {
            HoldWater("ambiguous Flame markers");
            return;
        }
        if (markerCaptured && marked.Length == 1 && marked[0].ObjectId != stackActor && now < stackUntil)
        {
            HoldWater("contradictory Flame carrier");
            return;
        }
        if (!soaking && !preposition && !stack)
        {
            ReleaseWater();
            return;
        }
        if (GameObjectManager.GetObjectsOfType<EventObject>().Any(o => o.IsValid && o.IsVisible && o.BaseId == 0x1EABE4))
        {
            // Root animation and activation are not exposed by this object API;
            // presence alone cannot certify a crossing to a water pool.
            HoldWater("root timing and path are unknown");
            return;
        }
        if (party.Count < 2)
        {
            HoldWater("party positions unavailable");
            return;
        }
        var others = party.Where(a => a.ObjectId != Core.Me.ObjectId).ToArray();
        var carrier = stack ? party.FirstOrDefault(a => a.ObjectId == stackActor) : null;
        if (stack && carrier == null)
        {
            HoldWater("stack carrier unavailable");
            return;
        }
        bool PoolSafe(Vector3 p) => OrdinarySegmentClear(p) &&
            (!soaking || poolsSeenAt.AddSeconds(11) > now.AddSeconds(Core.Me.Location.Distance2D(p) / 5f + 0.25)) &&
            (!stack || stackActor == Core.Me.ObjectId || p.Distance2D(carrier.Location) <= 5.5f);
        var eligible = pools.Where(PoolSafe).ToArray();
        if (eligible.Length == 0)
        {
            HoldWater("no verified pool route");
            return;
        }
        Vector3 selected = ChooseWaterPool(eligible, Core.Me.Location, others.Select(a => a.Location).ToArray(),
            stack && stackActor == Core.Me.ObjectId, stack, soaking, carrier?.Location, waterDestination);
        if (soaking && !stack && waterDestination.HasValue &&
            eligible.Any(p => p.Distance2D(waterDestination.Value) < 0.5f) &&
            selected.Distance2D(waterDestination.Value) > 0.5f)
        {
            // November 8 captures changed soak targets several times within a
            // second as party members crossed pool boundaries. Require a better
            // alternative to persist briefly while the old route remains valid;
            // an invalid old route bypasses this delay and is reassessed at once.
            if (!waterAlternative.HasValue || waterAlternative.Value.Distance2D(selected) > 0.5f)
            {
                waterAlternative = selected;
                waterAlternativeSince = now;
            }
            if (now < waterAlternativeSince.AddSeconds(0.6)) selected = waterDestination.Value;
        }
        else
        {
            waterAlternative = null;
            waterAlternativeSince = default;
        }
        if (soaking && !stack && waterDestination.HasValue &&
            eligible.Any(p => p.Distance2D(waterDestination.Value) < 0.5f) &&
            selected.Distance2D(waterDestination.Value) > 0.5f &&
            poolsSeenAt.AddSeconds(11) <= now.AddSeconds(Core.Me.Location.Distance2D(selected) / 5f + 1.25))
        {
            // The November 8 visit switched pools twice near the soak deadline.
            // Preserve a still-valid pool when the replacement has less than a
            // second of arrival reserve; a speculative late switch can miss both.
            selected = waterDestination.Value;
        }
        if (!waterDestination.HasValue || waterDestination.Value.Distance2D(selected) > 0.5f)
        {
            waterArrivalLogged = false;
            if (LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Water destination={selected} soaking={soaking} stack={stack} carrier=0x{stackActor:X}.");
        }
        waterDestination = selected;
        waterOwned = true;
        waterFailureLogged = false;
        foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
            CapabilityManager.Update(waterHandle, flag, TimeSpan.FromMilliseconds(500), "Dancing Plague: water and Flame");
        waterLeased = true;
        movement.Request(selected, 1.5f); // Pool radius is 3y; reserve 1.5y for movement tolerance.
        if (!waterArrivalLogged && Core.Me.Location.Distance2D(selected) <= 1.5f && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            waterArrivalLogged = true;
            Logger.Information($"[DancingPlague] Water arrived player={Core.Me.Location} target={selected} carrier=0x{stackActor:X}.");
        }
    }

    private static Vector3 ChooseWaterPool(Vector3[] eligible, Vector3 player, Vector3[] others,
        bool selfStack, bool stack, bool soaking, Vector3? carrier, Vector3? previous)
    {
        Vector3 selected;
        if (selfStack || !stack && !soaking)
        {
            // A self marker makes the player the stack center. Prefer the pool
            // covering the most living participants, then the nearest group.
            selected = eligible.OrderByDescending(p => others.Count(a => a.Distance2D(p) <= 5.5f))
                .ThenBy(p => others.Min(a => a.Distance2D(p))).ThenBy(p => p.Distance2D(player)).First();
        }
        else if (stack)
            selected = eligible.OrderBy(p => p.Distance2D(carrier.Value)).First();
        else
            selected = eligible.OrderBy(p => others.Count(a => a.Distance2D(p) < 3f))
                .ThenBy(p => p.Distance2D(player)).First();
        // Keep an equivalent target stable, but switch away from a newly
        // occupied soak or toward better self-stack participation.
        if (previous.HasValue && eligible.Any(p => p.Distance2D(previous.Value) < 0.5f))
        {
            var old = previous.Value;
            int oldCount = others.Count(a => a.Distance2D(old) < (soaking && !stack ? 3f : 5.5f));
            int newCount = others.Count(a => a.Distance2D(selected) < (soaking && !stack ? 3f : 5.5f));
            if (soaking && !stack ? oldCount <= newCount : oldCount >= newCount) selected = old;
        }
        return selected;
    }

    private void HoldWater(string reason)
    {
        if (waterOwned) movement?.Request(null, 1.5f);
        CapabilityManager.Update(waterHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Dancing Plague: water route unavailable");
        waterLeased = true;
        if (!waterFailureLogged && LoggingHelpers.MechanicDiagnosticsEnabled)
            Logger.Warning($"[DancingPlague] Water {reason}; holding ordinary movement for reassessment.");
        waterFailureLogged = true;
    }

    private void ResetWater(bool releaseRequest = true)
    {
        ReleaseWater(releaseRequest);
        poolsSeenAt = mistWatchUntil = flameUntil = stackUntil = default;
        stackActor = 0;
        flameCasting = false;
        markerCaptured = false;
        mistCasting = soakKnown = false;
    }

    private void ReleaseWater(bool releaseRequest = true)
    {
        if (releaseRequest && waterOwned) movement?.Request(null, 1.5f);
        if (waterLeased) CapabilityManager.Clear(waterHandle);
        waterLeased = waterOwned = waterArrivalLogged = waterFailureLogged = false;
        waterDestination = null;
        waterAlternative = null;
        waterAlternativeSince = default;
    }
}
