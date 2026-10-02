using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using DutyMechanic.Helpers;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    // Secret-route arena and actor identities come from the installed level and
    // encounter reference. Helpers alone cannot activate an encounter after a wipe.
    private static readonly Vector3 EnenraCenter = new(900, 90, -900);
    private static bool InEnenra() => InRokkonCombat() && Core.Me.Distance2D(EnenraCenter) < 35 && GameObjectManager.GetObjectsOfType<BattleCharacter>().Any(b => b.IsValid && b.BaseId is 0x3EAD or 0x3EAE && b.IsAlive && b.Distance2D(EnenraCenter) < 35);
    private void RegisterEnenra()
    {
        // The first raidwide reduces 20.5 to 20. Keep a half-yalm wall inset from
        // entry, preserving space for the 16y knockback without relying on wall contact.
        AvoidanceHelpers.AddAvoidDonut(InEnenra, () => new[] { EnenraCenter }, 70, 19.5);
        AvoidanceManager.AddAvoidLocation<Impact>(InEnenra, _ => 6.5f, c => c.Location, () => EnenraRingWave().Where(c => c.Action == 32840));
        AvoidanceHelpers.AddAvoidDonut(InEnenra, () => EnenraRingWave().Where(c => c.Action == 32841).Select(c => c.Location).ToArray(), 12.5, 5.5);
        AvoidanceHelpers.AddAvoidDonut(InEnenra, () => EnenraRingWave().Where(c => c.Action == 32842).Select(c => c.Location).ToArray(), 18.5, 11.5);
        AvoidanceHelpers.AddAvoidDonut(InEnenra, () => EnenraRingWave().Where(c => c.Action == 32843).Select(c => c.Location).ToArray(), 24.5, 17.5);
        AvoidanceManager.AddAvoidLocation<Impact>(InEnenra, _ => 8.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == 32848));
        AvoidanceManager.AddAvoidLocation<Impact>(InEnenra, _ => 6.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == 32855));
        AvoidanceManager.AddAvoidLocation<Impact>(InEnenra, _ => 16.5f, c => c.Location, () => PendingImpacts().Where(c => c.Action == 32851));
        // Clearing Smoke is required displacement, not a damage circle. For the
        // centered source, a 3.5y staging radius plus 16y travel stays within the
        //19.5y boundary and composes with the smoldering circles. Unknown off-center
        // sources are not silently reinterpreted as the reference's centered cast.
        AvoidanceHelpers.AddAvoidDonut(InEnenra, () => PendingImpacts().Where(c => c.Action == 32850 && c.Location.Distance2D(EnenraCenter) < 1).Select(c => c.Location).ToArray(), 70, 3.5);
    }

    private IEnumerable<Impact> EnenraRingWave()
    {
        var pending = PendingImpacts().Where(c => c.Action is >= 32840 and <= 32843).ToArray();
        // Clones can own independent ring origins. Select the next impact at each
        // origin, rather than combining all four concentric shapes into a full disc
        // or hiding another clone's simultaneous sequence behind one global timer.
        return pending.Where(c => !pending.Any(other => other.Location.Distance2D(c.Location) < 1 && other.End < c.End.AddMilliseconds(-350)));
    }
}
