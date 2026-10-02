using System;
using System.Linq;
using Clio.Utilities;
using ff14bot;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private const uint LanceClockwise = 33687;
    private const uint LanceCounterclockwise = 33688;
    private const uint LanceFirst = 33689;
    private const uint LanceRest = 33690;
    private LanceSequence _lance;
    private void RegisterLance()
    {
        _lance = null;
        // October 1 20:48: generic raw-type 12 moved into the center and missed
        // all instant follow-ups. The damaging helper owns a centered 60x 7 line,
        // not a forward line beginning at the boss's animated facing.
        AvoidanceManager.AddAvoidPolygon<Impact>(InYozakura, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(4, 30.5f, 30.5f), c => c.Location, () => _lance == null ? Array.Empty<Impact>() : _lance.Shapes.Skip(_lance.Index).Take(2), priority: AvoidancePriority.High);
    }

    private void ObserveLance()
    {
        var now = DateTime.UtcNow;
        if (_lance != null && now >= _lance.End)
            _lance = null;
        var actors = GameObjectManager.GetObjectsOfType<BattleCharacter>().Where(b => b.IsValid && b.NpcId == 12325 && b.Distance2D(YozakuraCenter) < 40).ToArray();
        var parent = actors.FirstOrDefault(b => b.BaseId == YozakuraBase && b.IsCasting && b.CastingSpellId is LanceClockwise or LanceCounterclockwise);
        var first = actors.Where(b => b.BaseId == 9020 && b.IsCasting && b.CastingSpellId == LanceFirst).ToArray();
        if (parent != null && first.Length == 1)
        {
            var helper = first[0];
            var firstEnd = now + helper.SpellCastInfo.RemainingCastTime;
            if (_lance == null || _lance.Owner != helper.ObjectId || now > _lance.FirstEnd.AddSeconds(1))
            {
                var step = (parent.CastingSpellId == LanceClockwise ? -1 : 1) * (float)(28 * Math.PI / 180);
                _lance = new LanceSequence
                {
                    Owner = helper.ObjectId,
                    InitialHeading = helper.Heading,
                    Step = step,
                    Shapes = Enumerable.Range(0, 5).Select(i => new Impact { Location = new Vector3(helper.Location.X, YozakuraCenter.Y, helper.Location.Z), Heading = helper.Heading + i * step }).ToArray()
                };
            }

            _lance.FirstEnd = firstEnd;
            // First damage followed the reported end by~0.9s; five headings
            // advanced at~1.07s intervals. This bounded fence covers the final
            // instant hit without keeping the old sequence into the next cast.
            _lance.End = firstEnd.AddSeconds(6);
        }

        if (_lance == null)
            return;
        var actor = actors.FirstOrDefault(b => b.ObjectId == _lance.Owner);
        if (actor == null)
            return;
        // Captured CCW helper headings were 0.528,1.017,1.506,1.994,2.483.
        // Advance from that actual native turn, avoiding clock drift and cast-end
        // polling gaps. Reserve only the current and next line, not all five.
        var delta = (float)Math.Atan2(Math.Sin(actor.Heading - _lance.InitialHeading), Math.Cos(actor.Heading - _lance.InitialHeading));
        var index = (int)Math.Round(delta / _lance.Step);
        if (index >= _lance.Index && index < 5 && Math.Abs(delta - index * _lance.Step) < .08f)
            _lance.Index = index;
    }

    private sealed class LanceSequence
    {
        internal uint Owner;
        internal float InitialHeading, Step;
        internal int Index;
        internal DateTime FirstEnd, End;
        internal Impact[] Shapes;
    }
}
