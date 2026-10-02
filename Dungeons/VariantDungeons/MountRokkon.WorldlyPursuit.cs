using System;
using System.Linq;
using Clio.Utilities;
using ff14bot.Managers;
using ff14bot.Objects;
using ff14bot.Pathing.Avoidance;

namespace DutyMechanic.Dungeons;
public sealed partial class MountRokkon
{
    private PursuitSequence _pursuit;
    private void RegisterWorldlyPursuit()
    {
        _pursuit = null;
        // October 1 21:22: the boss turns during each short follow-up cast. The
        // omen already points at the final axis; generic live-facing rectangles
        // moved underneath the escape path and missed two damage snapshots.
        AvoidanceManager.AddAvoidPolygon<Impact>(InGorai, null, 80, c => -c.Heading, _ => 1, _ => 15, _ => Rectangle(10.5f, 60.5f, 60.5f), c => c.Location, () => _pursuit == null ? Array.Empty<Impact>() : _pursuit.Crosses.Skip(_pursuit.Index * 2).Take(2), priority: AvoidancePriority.High);
    }

    private void ObserveWorldlyPursuit()
    {
        var now = DateTime.UtcNow;
        var boss = GameObjectManager.GetObjectsOfType<BattleCharacter>().FirstOrDefault(b => b.IsValid && b.BaseId == 16220 && b.IsCasting && b.CastingSpellId is 34042 or 34043 or 34044);
        if (boss != null)
        {
            var omen = boss.OmenMatrix;
            omen.Transform(new Vector3(0, 0, 1), out var tip);
            var delta = tip - omen.Center;
            if (delta.X * delta.X + delta.Z * delta.Z > 900)
            {
                var heading = (float)Math.Atan2(delta.X, delta.Z);
                var end = now + boss.SpellCastInfo.RemainingCastTime;
                var first = boss.CastingSpellId != 34044;
                if (first && (_pursuit == null || now > _pursuit.FirstEnd.AddSeconds(1)))
                {
                    // CW 34043 turns -22.5 degrees; CCW 34042 turns +22.5.
                    // Two perpendicular centered rectangles represent one cross.
                    var step = (boss.CastingSpellId == 34043 ? -1 : 1) * (float)(Math.PI / 8);
                    _pursuit = new PursuitSequence
                    {
                        InitialHeading = heading,
                        Step = step,
                        FirstEnd = end,
                        Crosses = Enumerable.Range(0, 10).Select(i => new Impact { Location = new Vector3(omen.Center.X, GoraiCenter.Y, omen.Center.Z), Heading = heading + (i / 2) * step + (i % 2) * (float)(Math.PI / 2) }).ToArray()
                    };
                }
                else if (!first && _pursuit == null)
                {
                    // A late encounter registration may miss the first cast.
                    // Avoid this observed cross without inventing a turn/count.
                    _pursuit = new PursuitSequence
                    {
                        Count = 1,
                        InitialHeading = heading,
                        Step = 1,
                        FirstEnd = end,
                        Crosses = Enumerable.Range(0, 2).Select(i => new Impact { Location = new Vector3(omen.Center.X, GoraiCenter.Y, omen.Center.Z), Heading = heading + i * (float)(Math.PI / 2) }).ToArray()
                    };
                }

                if (_pursuit != null)
                {
                    var turn = Math.Atan2(Math.Sin(heading - _pursuit.InitialHeading), Math.Cos(heading - _pursuit.InitialHeading));
                    var index = (int)Math.Round(turn / _pursuit.Step);
                    if (index >= 0 && index < _pursuit.Count && Math.Abs(turn - index * _pursuit.Step) < .04)
                    {
                        // Native casts refine the schedule without rotating a live
                        // polygon through the boss's intermediate animation frames.
                        _pursuit.Ends[index] = end.AddSeconds(.75);
                        for (var i = index + 1; i < _pursuit.Count; i++)
                            _pursuit.Ends[i] = _pursuit.Ends[index].AddSeconds(3.7 * (i - index));
                    }
                }
            }
        }

        if (_pursuit == null)
            return;
        // Captured follow-ups ended at 27.565,31.281,35.062,38.720; damage
        // followed by~0.5s. After the retained hit, expose just the next cross
        // before its 1.2s cast starts. Reserving every future cross erases safety.
        while (_pursuit.Index < _pursuit.Count && now >= _pursuit.Ends[_pursuit.Index])
            _pursuit.Index++;
        if (_pursuit.Index == _pursuit.Count)
            _pursuit = null;
    }

    private sealed class PursuitSequence
    {
        internal float InitialHeading, Step;
        internal DateTime FirstEnd;
        internal int Index;
        internal int Count = 5;
        internal readonly DateTime[] Ends = new DateTime[5];
        internal Impact[] Crosses;
    }
}
