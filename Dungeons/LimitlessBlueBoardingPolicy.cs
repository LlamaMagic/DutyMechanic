namespace DutyMechanic.Dungeons;

// The shell's availability and Whaleback status own this request's lifetime.
// Death, loading and cutscenes must release the request rather than leaving an
// old boarding loop or capability lease active across a successor state.
internal static class LimitlessBlueBoardingPolicy
{
    internal readonly record struct State(bool InCombat, bool Alive, bool Loading,
        bool Cutscene, bool OnWhaleback, bool TargetAvailable, bool AtDestination = false);

    internal static bool ShouldApproach(State state) => state.InCombat && state.Alive &&
        !state.Loading && !state.Cutscene && !state.OnWhaleback && state.TargetAvailable && !state.AtDestination;

    // The 2026-11-01 WHM capture boarded both shells from the eastern island
    // apron near (-5.7, -2.8) and (-5.6, 0.6). Restrict the direct crossing to
    // that small runway; ordinary navigation may approach the profile hotspot
    // from elsewhere instead of drawing an unverified line across the island.
    internal static bool InObservedRunway(float x, float z) =>
        x >= -7f && x <= -4f && z >= -4f && z <= 1f;
}
