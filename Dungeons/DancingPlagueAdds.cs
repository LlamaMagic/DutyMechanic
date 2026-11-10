using System;
using System.Collections.Generic;
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
    private sealed class AddHazard
    {
        internal uint Actor, Action, Target;
        internal Vector3 Origin;
        internal float Heading;
        internal DateTime Impact;
    }

    private readonly Dictionary<(uint, uint), AddHazard> addHazards = new();
    private readonly CapabilityManagerHandle addHandle = CapabilityManager.CreateNewHandle();
    private Vector3? addDestination;
    private bool addOwned, addLeased, addConflictLogged, addArrivalLogged;
    private DateTime nextAddSearch;

    private void UpdateAdds()
    {
        if (movement == null || !movement.IsCurrent || frost.Count != 0)
        {
            // Frost runs first through the same owner. Retire only the add lease;
            // never clear or replace a newly acquired Frost movement request.
            ResetAdds(frost.Count == 0);
            return;
        }
        var now = DateTime.UtcNow;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>(true, false).Where(a => a.IsValid).ToArray();
        foreach (var actor in actors.Where(a => a.IsCasting && a.SpellCastInfo != null &&
            (a.BaseId == Helper && a.CastingSpellId is 15701 or 15875 or 15698 or 15789 or 15704 or 15705 ||
             a.BaseId is 0x27F0 or 0x27F4 && a.CastingSpellId is 16259 or 15703)))
        {
            var key = (actor.ObjectId, actor.CastingSpellId);
            if (addHazards.ContainsKey(key)) continue;
            // The live 15701 helper caused vulnerability around cast completion;
            // retain the cast snapshot briefly for delayed effects and handoffs.
            addHazards[key] = new AddHazard { Actor = actor.ObjectId, Action = actor.CastingSpellId,
                Target = actor.SpellCastInfo.TargetId, Origin = actor.Location, Heading = actor.Heading,
                Impact = now + actor.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(0.5) };
        }
        foreach (var key in addHazards.Where(pair => now > pair.Value.Impact.AddSeconds(0.5) ||
            now < pair.Value.Impact.AddSeconds(-0.75) && !actors.Any(a => a.ObjectId == pair.Value.Actor &&
                a.IsCasting && a.CastingSpellId == pair.Value.Action)).Select(pair => pair.Key).ToArray())
            addHazards.Remove(key);
        if (addHazards.Count == 0)
        {
            if (addOwned) ResetAdds();
            return;
        }
        addOwned = true;
        if (MovementManager.GenerallyOutOfControl)
        {
            // Forced movement is the current input owner. Reassess after landing.
            movement.Request(null, 0.7f);
            return;
        }
        var hazards = addHazards.Values.ToArray();
        bool valid = addDestination.HasValue && AddRouteSafe(Core.Me.Location, addDestination.Value, now, hazards, actors) &&
            OrdinarySegmentClear(addDestination.Value);
        if (!valid && now >= nextAddSearch)
        {
            nextAddSearch = now.AddMilliseconds(150);
            // Candidate points remain on the verified square reference arena. A
            // stable previous destination wins while it is still safe, preventing
            // the rapid destination churn observed in other ordered encounters.
            var points = new List<Vector3> { Core.Me.Location };
            for (int x = -18; x <= 18; x += 2)
                for (int z = -18; z <= 18; z += 2)
                    points.Add(new Vector3(Center.X + x, Center.Y, Center.Z + z));
            // The November 8 trace changed destination four times in 0.84s as
            // moving spread targets crossed the exact boundary. Prefer a wider
            // 1.5y selection buffer; keep the existing smaller safety margin for
            // validating an already-owned route and use it as a fallback only.
            addDestination = points.Where(p => AddRouteSafe(Core.Me.Location, p, now, hazards, actors, 1.5f) && OrdinarySegmentClear(p))
                .OrderBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
            if (!addDestination.HasValue)
                addDestination = points.Where(p => AddRouteSafe(Core.Me.Location, p, now, hazards, actors) && OrdinarySegmentClear(p))
                    .OrderBy(p => p.Distance2D(Core.Me.Location)).Cast<Vector3?>().FirstOrDefault();
            addArrivalLogged = false;
            valid = addDestination.HasValue;
            if (valid && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Add destination={addDestination} hazards={hazards.Length}.");
        }
        if (!valid)
        {
            movement.Request(null, 0.7f);
            CapabilityManager.Update(addHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Dancing Plague: add route unavailable");
            addLeased = true;
            if (!addConflictLogged && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Warning("[DancingPlague] No certified add route; holding ordinary movement for reassessment.");
            addConflictLogged = true;
            return;
        }
        addConflictLogged = false;
        if (!addArrivalLogged && Core.Me.Location.Distance2D(addDestination.Value) <= 0.7f && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            Logger.Information($"[DancingPlague] Add arrived player={Core.Me.Location} target={addDestination} hazards={hazards.Length}.");
            addArrivalLogged = true;
        }
        foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
            CapabilityManager.Update(addHandle, flag, TimeSpan.FromMilliseconds(500), "Dancing Plague: add overlap");
        addLeased = true;
        movement.Request(addDestination, 0.7f);
    }

    private static bool AddRouteSafe(Vector3 start, Vector3 destination, DateTime now, AddHazard[] hazards, BattleCharacter[] actors, float selectionMargin = 0)
    {
        if (MathF.Abs(destination.X - Center.X) > 19 - selectionMargin || MathF.Abs(destination.Z - Center.Z) > 19 - selectionMargin) return false;
        const float speed = 5f;
        double travel = start.Distance2D(destination) / speed + 0.25;
        var knockback = hazards.FirstOrDefault(h => h.Action == 15705);
        foreach (var hazard in hazards)
        {
            for (int offset = -1; offset <= 1; offset++)
            {
                DateTime sample = hazard.Impact.AddSeconds(offset * 0.3);
                if (sample < now) continue;
                double elapsed = (sample - now).TotalSeconds;
                var p = Interpolate(start, destination, Math.Max(0, (elapsed - 0.25) / Math.Max(0.01, travel - 0.25)));
                // Puck pushes 10y from center. Validate both the staging circle
                // and resulting square-arena landing, not just the starting point.
                if (hazard.Action == 15705)
                {
                    float dx = p.X - hazard.Origin.X, dz = p.Z - hazard.Origin.Z;
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d < 5.5f + selectionMargin || d < 0.01f) return false;
                    var landing = new Vector3(p.X + dx / d * 10, p.Y, p.Z + dz / d * 10);
                    if (MathF.Abs(landing.X - Center.X) > 19 - selectionMargin || MathF.Abs(landing.Z - Center.Z) > 19 - selectionMargin) return false;
                    continue;
                }
                if (knockback != null && sample > knockback.Impact.AddSeconds(0.3))
                {
                    float dx = p.X - knockback.Origin.X, dz = p.Z - knockback.Origin.Z;
                    float d = MathF.Sqrt(dx * dx + dz * dz);
                    if (d < 0.01f) return false;
                    p = new Vector3(p.X + dx / d * 10, p.Y, p.Z + dz / d * 10);
                }
                float x = p.X - hazard.Origin.X, z = p.Z - hazard.Origin.Z;
                float forward = x * MathF.Sin(hazard.Heading) + z * MathF.Cos(hazard.Heading);
                float side = x * MathF.Cos(hazard.Heading) - z * MathF.Sin(hazard.Heading);
                if (hazard.Action is 15701 or 15875)
                {
                    // The 20-degree cone receives a 0.5y lateral margin.
                    if (forward >= -selectionMargin && forward <= 50.5f + selectionMargin && MathF.Abs(side) <= MathF.Max(0, forward) * MathF.Tan(10 * MathF.PI / 180) + 0.5f + selectionMargin) return false;
                }
                else if (hazard.Action == 16259 && forward >= -0.5f - selectionMargin && forward <= 60.5f + selectionMargin && MathF.Abs(side) <= 2.5f + selectionMargin) return false;
                else if (hazard.Action == 15704 && p.Distance2D(hazard.Origin) < 5.5f + selectionMargin) return false;
                else if (hazard.Action is 15698 or 15789)
                {
                    float radius = (hazard.Action == 15698 ? 6.5f : 10.5f) + selectionMargin;
                    if (hazard.Target == Core.Me.ObjectId)
                    {
                        var party = actors.Where(a => a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                            PartyManager.VisibleMembers.Any(member => member.ObjectId == a.ObjectId)).ToArray();
                        if (party.Length == 0 || party.Any(a => a.Location.Distance2D(p) < radius)) return false;
                    }
                    else
                    {
                        var marked = actors.FirstOrDefault(a => a.ObjectId == hazard.Target && a.IsAlive);
                        if (marked == null || marked.Location.Distance2D(p) < radius) return false;
                    }
                }
                else if (hazard.Action == 15703)
                {
                    // Puck's Breath is a stack, not an avoid. Check live
                    // participants in both directions; the exact minimum and
                    // self-targeted behavior still need synchronized capture.
                    if (hazard.Target == Core.Me.ObjectId)
                    {
                        if (!actors.Any(a => a.IsAlive && a.ObjectId != Core.Me.ObjectId &&
                            PartyManager.VisibleMembers.Any(member => member.ObjectId == a.ObjectId) &&
                            a.Location.Distance2D(p) <= 5.5f - selectionMargin)) return false;
                    }
                    else
                    {
                        var marked = actors.FirstOrDefault(a => a.ObjectId == hazard.Target && a.IsAlive);
                        if (marked == null || marked.Location.Distance2D(p) > 5.5f - selectionMargin) return false;
                    }
                }
            }
        }
        return true;
    }

    private void ResetAdds(bool releaseRequest = true)
    {
        if (releaseRequest && addOwned) movement?.Request(null, 0.7f);
        if (addLeased) CapabilityManager.Clear(addHandle);
        addLeased = addOwned = addConflictLogged = addArrivalLogged = false;
        addDestination = null;
        addHazards.Clear();
        nextAddSearch = default;
    }
}
