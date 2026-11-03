using System;

namespace DutyMechanic.Dungeons;

internal static class AlexandriaAmalgamTiming
{
    // Five Arelia captures for each Ternary effect placed receipts 0.203–0.367s
    // after reported cast finish. The wider provisional window preserves a
    // full second between the two-second-spaced circle/rings; the previous
    // generic 2.2-second window made those disjoint shapes overlap and left no
    // possible route. Other actions retain their older, wider bounds.
    internal static (double Earliest, double Latest) Window(uint action, double finish) =>
        action is 39254 or 39255 or 39256 ? (finish - .2, finish + .8) : (finish - .5, finish + 1.7);
}
