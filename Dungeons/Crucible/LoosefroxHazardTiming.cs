using System;

namespace DutyMechanic.Dungeons
{
    // September24's captured donut resolved at14:47:43.7; the big bombs did
    // not start casting until14:47:46.6. Publishing their future crosses inside
    // the donut window erased its entire refuge. Stage the earlier effect first,
    // then restore the same cross objects for native escape. A real bomb cast
    // always remains dangerous, even if an unexpected sequence overlaps it.
    internal static class LoosefroxHazardTiming
    {
        internal static bool PublishBomb(bool forecastOnly, DateTime now, DateTime donutUntil)
            => !forecastOnly || now >= donutUntil;
    }
}
