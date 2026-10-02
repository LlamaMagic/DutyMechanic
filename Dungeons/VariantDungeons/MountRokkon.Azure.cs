using System;
using System.Collections.Generic;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private const uint BoundlessAzure = 34206;
    private const uint AzureAuspice = 34204;
    private const uint BoundlessScarlet = 34202;
    private const uint UpwellFirst = 34207;
    private const uint UpwellRest = 34208;
    private readonly List<AzureSequence> _azure = new();
    private void RegisterAzure()
    {
        _azure.Clear();
        // The October 2 cast decoded as a 16y safe hole, leaving the player 15.8y
        // from Moko when damage landed. Its actual hole is 6y; keep a half-yalm
        // inset and the measured post-cast fence instead of that generic torus.
        DutyMechanic.Helpers.AvoidanceHelpers.AddAvoidDonut(InMoko, () => PendingImpacts().Where(c => c.Action == AzureAuspice).Select(c => c.Location).ToArray(), 60.5, 5.5);
        // October 1 16:01:32–16:02:11: helpers stand at line midpoints. Their
        // omen origins are 30y behind them, explaining the generic forward-only
        // lines missing the player near center. Subsequent strips are offset 2.5y
        // sideways from the helper, as confirmed by the captured omen transform.
        AvoidanceManager.AddAvoidPolygon<Impact>(InMoko, null, 80, c => -c.Heading, _ => 1, _ => 15, c => Rectangle(c.Action == UpwellRest ? 3 : 5.5f, 30.5f, 30.5f), // October 1 17:50:15: Scarlet uses the same midpoint helpers; generic
        // forward-only geometry left the player in its diagonal damage lane.
        c => c.Location, () => PendingImpacts().Where(c => c.Action is BoundlessAzure or BoundlessScarlet).Concat(ActiveAzure()), priority: AvoidancePriority.High);
    }

    private void ObserveAzure()
    {
        var now = DateTime.UtcNow;
        _azure.RemoveAll(s => s.Impacts.All(i => i.End <= now));
        foreach (var actor in GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.IsCasting && b.Distance2D(MokoCenter) < 60 && b.CastingSpellId is UpwellFirst or UpwellRest))
        {
            var reportedEnd = now + actor.SpellCastInfo.RemainingCastTime;
            if (actor.CastingSpellId == UpwellFirst)
            {
                // Reused helpers can start a later perpendicular sequence before
                // the older wave finishes. Match cast identity by actor and end,
                // never replace that actor's still-unresolved earlier strips.
                if (_azure.Any(s => s.Actor == actor.ObjectId && Math.Abs((s.ReportedEnd - reportedEnd).TotalSeconds) < .5))
                    continue;
                var origin = actor.Location;
                var heading = actor.Heading;
                var firstEnd = reportedEnd.AddSeconds(.75);
                var sequence = new AzureSequence
                {
                    Actor = actor.ObjectId,
                    ReportedEnd = reportedEnd
                };
                sequence.Impacts.Add(new Impact { Action = UpwellFirst, Location = origin, Heading = heading, End = firstEnd });
                var right = new Vector3(-(float)Math.Cos(heading), 0, (float)Math.Sin(heading));
                foreach (var sign in new[]
                {
                    -1,
                    1
                }

                )
                {
                    // The horizontal lines at z 530/550 reach one nearby edge
                    // immediately. Their other side, and both diagonal sides,
                    // have five 5y advances. The first strip center is 7.5y away:
                    // five-yalm first half-width plus the next 2.5y half-width.
                    var outward = sign < 0 && Math.Abs(origin.Z - 530) < 1 || sign > 0 && Math.Abs(origin.Z - 550) < 1;
                    var count = outward ? 1 : 5;
                    for (var i = 0; i < count; i++)
                        sequence.Impacts.Add(new Impact { Action = UpwellRest, Location = origin + right * (sign * (7.5f + 5 * i)), Heading = heading, End = firstEnd.AddSeconds(2 * (i + 1)) });
                }

                _azure.Add(sequence);
                ff14bot.Helpers.Logging.Write("[Rokkon] Upwell sequence captured at {0}.", origin);
            }
            else
            {
                var center = actor.OmenMatrix.Center + Forward(actor.Heading) * 30;
                var actualEnd = reportedEnd.AddSeconds(.75);
                var impact = _azure.SelectMany(s => s.Impacts).Where(i => i.Action == UpwellRest && i.Location.Distance2D(center) < 1 && Math.Abs((i.End - actualEnd).TotalSeconds) < 2).OrderBy(i => Math.Abs((i.End - actualEnd).TotalSeconds)).FirstOrDefault();
                if (impact != null)
                {
                    // Real short casts refine the forecast but cannot supply enough
                    // warning alone: the captured report lasts only 0.7seconds.
                    impact.Location = center;
                    impact.Heading = actor.Heading;
                    impact.End = actualEnd;
                }
            }
        }
    }

    private IEnumerable<Impact> ActiveAzure()
    {
        var pending = _azure.SelectMany(s => s.Impacts).Where(i => i.End > DateTime.UtcNow).OrderBy(i => i.End).ToArray();
        if (pending.Length == 0)
            return Array.Empty<Impact>();
        // Expose the earliest resolving wave, including truly concurrent strips
        // from another sequence. Reserving all future strips would remove the
        // cleared lanes the player must enter. Each next wave has~2s warning.
        return pending.Where(i => i.End <= pending[0].End.AddSeconds(.75));
    }

    private sealed class AzureSequence
    {
        internal uint Actor;
        internal DateTime ReportedEnd;
        internal readonly List<Impact> Impacts = new();
    }
}
