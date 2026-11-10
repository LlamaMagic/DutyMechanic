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
    private readonly CapabilityManagerHandle rootHandle = CapabilityManager.CreateNewHandle();
    private Vector3? rootDestination;
    private bool rootOwned, rootLeased, rootArrivalLogged, rootFailureLogged;

    private void UpdateRoots()
    {
        if (movement == null || !movement.IsCurrent || frost.Count != 0 || addOwned)
        {
            // These handlers ran first and may already own the shared request.
            ResetRoots(!frostLeased && !addOwned);
            return;
        }
        var roots = GameObjectManager.GetObjectsOfType<EventObject>()
            .Where(o => o.IsValid && o.IsVisible && o.BaseId == 0x1EABE4 && o.Location.Distance2D(Center) < 35)
            .Select(o => o.Location).ToArray();
        if (roots.Length == 0)
        {
            ResetRoots(!waterOwned);
            return;
        }
        // The November 8 capture showed Bind and Bleeding while stationary on
        // the central Growth cross. The referenced pattern has two 6y-wide
        // crosses, one rotated 45 degrees; the object API does not expose its
        // activation animation. Treat each visible root as hazardous until it
        // disappears, including both observed corner origins, rather than
        // guessing when an unobserved animation activated or expired.
        var player = Core.Me.Location;
        bool valid = rootDestination.HasValue && RootRouteSafe(player, rootDestination.Value, roots) &&
            OrdinarySegmentClear(rootDestination.Value);
        if (!valid)
        {
            var candidates = new List<Vector3> { player };
            for (int x = -18; x <= 18; x += 2)
                for (int z = -18; z <= 18; z += 2)
                    candidates.Add(new Vector3(Center.X + x, Center.Y, Center.Z + z));
            rootDestination = candidates.Where(p => RootRouteSafe(player, p, roots) && OrdinarySegmentClear(p))
                .OrderBy(p => p.Distance2D(player)).Cast<Vector3?>().FirstOrDefault();
            rootArrivalLogged = false;
            if (rootDestination.HasValue && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Information($"[DancingPlague] Root destination={rootDestination} origins={roots.Length}.");
        }
        if (!rootDestination.HasValue)
        {
            if (rootOwned) movement.Request(null, 0.5f);
            CapabilityManager.Update(rootHandle, CapabilityFlags.Movement, TimeSpan.FromMilliseconds(500), "Dancing Plague: root route unavailable");
            rootLeased = true;
            if (!rootFailureLogged && LoggingHelpers.MechanicDiagnosticsEnabled)
                Logger.Warning("[DancingPlague] No verified root route; holding ordinary movement for reassessment.");
            rootFailureLogged = true;
            return;
        }
        rootFailureLogged = false;
        foreach (var flag in new[] { CapabilityFlags.Movement, CapabilityFlags.MoveBehind, CapabilityFlags.GapCloser })
            CapabilityManager.Update(rootHandle, flag, TimeSpan.FromMilliseconds(500), "Dancing Plague: Growth roots");
        rootLeased = rootOwned = true;
        movement.Request(rootDestination, 0.5f);
        if (!rootArrivalLogged && player.Distance2D(rootDestination.Value) <= 0.5f && LoggingHelpers.MechanicDiagnosticsEnabled)
        {
            Logger.Information($"[DancingPlague] Root arrived player={player} target={rootDestination} origins={roots.Length}.");
            rootArrivalLogged = true;
        }
    }

    private static bool RootRouteSafe(Vector3 start, Vector3 destination, Vector3[] roots)
    {
        if (MathF.Abs(destination.X - Center.X) > 19 || MathF.Abs(destination.Z - Center.Z) > 19) return false;
        foreach (var root in roots)
        {
            if (!RootPointSafe(destination, root)) return false;
            bool escaped = RootPointSafe(start, root);
            int steps = Math.Max(1, (int)MathF.Ceiling(start.Distance2D(destination) / 0.5f));
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                var point = new Vector3(start.X + (destination.X - start.X) * t, start.Y,
                    start.Z + (destination.Z - start.Z) * t);
                bool safe = RootPointSafe(point, root);
                // Escape from an already occupied ray is permitted, but never
                // cross back into that root's pattern after reaching safety.
                if (escaped && !safe) return false;
                escaped |= safe;
            }
        }
        return true;
    }

    private static bool RootPointSafe(Vector3 point, Vector3 root)
    {
        float x = point.X - root.X, z = point.Z - root.Z;
        const float margin = 3.6f, inverseSqrtTwo = 0.70710678f;
        return MathF.Abs(x) > margin && MathF.Abs(z) > margin &&
            MathF.Abs((x + z) * inverseSqrtTwo) > margin && MathF.Abs((x - z) * inverseSqrtTwo) > margin;
    }

    private void ResetRoots(bool releaseRequest = true)
    {
        if (releaseRequest && rootOwned) movement?.Request(null, 0.5f);
        if (rootLeased) CapabilityManager.Clear(rootHandle);
        rootDestination = null;
        rootOwned = rootLeased = rootArrivalLogged = rootFailureLogged = false;
    }
}
