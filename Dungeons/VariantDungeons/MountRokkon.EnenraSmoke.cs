using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private readonly Dictionary<uint, Impact> _enenraSmoke = new();
    private void RegisterEnenraSmoke()
    {
        _enenraSmoke.Clear();
        // October 1: the 1200ms helper report began 22:18:41.287 and hit 43.486.
        // Generic late rectangles cannot escape a 50y cleave from the wrong half.
        // The parent and its visible smoke object identify that half much earlier.
        AvoidanceManager.AddAvoidPolygon<Impact>(InEnenra, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(25.5f, .5f, 50.5f), c => c.Location, EnenraSmokeShapes, priority: AvoidancePriority.High);
    }

    private void ObserveEnenraSmoke()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _enenraSmoke.Where(p => p.Value.End <= now).Select(p => p.Key).ToArray())
            _enenraSmoke.Remove(key);
        var smoke = GameObjectManager.GameObjects.Where(o => o.IsValid && o.IsVisible && o.BaseId is 0x1EB88F or 0x1EB890 or 0x1EB891 && o.Distance2D(EnenraCenter) < 25).ToArray();
        foreach (var parent in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.BaseId is 0x3EAD or 0x3EAE && b.IsCasting && b.CastingSpellId == 32844))
        {
            // These objects can be visible 18s before the parent begins. Do not
            // activate on appearance alone while intervening rings still resolve.
            // Capture one unique smoke at the casting parent; clones own separate
            // objects. Unknown/ambiguous preview retains the actual-cast fallback.
            var matches = smoke.Where(o => o.Distance2D(parent.Location) < 1).ToArray();
            if (matches.Length != 1)
                continue;
            var visual = matches[0];
            if (!_enenraSmoke.TryGetValue(visual.ObjectId, out var shape))
            {
                var geometry = EnenraSmokeGeometry(visual.BaseId, visual.Location, visual.Heading);
                _enenraSmoke[visual.ObjectId] = shape = new Impact
                {
                    Action = 32856,
                    Location = geometry.origin,
                    Heading = geometry.heading
                };
            }

            // Parent end 19:11.096 preceded the impact by about 5.6s. The later
            //22:22:51.976 hit landed 1.136s after its short helper report ended;
            //6.1s beyond the parent includes a 1.35s helper fence. Native reports
            // refine that deadline below without withdrawing the early geometry.
            shape.End = now + parent.SpellCastInfo.RemainingCastTime + TimeSpan.FromSeconds(6.1);
        }

        foreach (var native in PendingImpacts().Where(c => c.Action == 32856))
            foreach (var predicted in _enenraSmoke.Values.Where(c => c.Location.Distance2D(native.Location) < 1 && Math.Abs(Math.Atan2(Math.Sin(c.Heading - native.Heading), Math.Cos(c.Heading - native.Heading))) < .1))
                predicted.End = native.End;
    }

    private static (Vector3 origin, float heading) EnenraSmokeGeometry(uint visual, Vector3 position, float heading)
    {
        // The smoke graphic is offset from the emerging actor. October 1 pair:
        // visual 3 at(911,-900), facingwest -> origin(889,-900), facingeast;
        // visual 1 at(889,-900), facingeast -> origin(900,-889), facingnorth.
        //15.556349 is the 22y diagonal's component length; round the resulting
        // location to the actual integer-yalm helper origin, preserving floorY.
        var side = visual == 0x1EB88F ? -1 : 1;
        var delta = visual == 0x1EB891 ? Forward(heading) * 22 : Forward(heading + side * (float)Math.PI / 4) * 15.556349f;
        var point = position + delta;
        return (new Vector3((float)Math.Round(point.X), position.Y, (float)Math.Round(point.Z)), heading + (visual == 0x1EB891 ? (float)Math.PI : -side * (float)Math.PI / 2));
    }

    private IEnumerable<Impact> EnenraSmokeShapes()
    {
        var predicted = _enenraSmoke.Values.Where(c => c.End > DateTime.UtcNow).ToArray();
        return predicted.Concat(PendingImpacts().Where(c => c.Action == 32856 && !predicted.Any(p => p.Location.Distance2D(c.Location) < 1)));
    }
}
