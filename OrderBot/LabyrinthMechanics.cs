namespace ff14bot.NeoProfiles;
/// <summary>
/// Exposes only alliance assignment and semantic positioning through OrderBot's Python namespace.
/// Native GetInstanceTodo and IsDutyEnded own progression; no duplicate clear/readiness gates.
/// </summary>
public static class LabyrinthMechanics
{
    /// <summary>True while XML must suspend hotspot traversal for a semantic position.</summary>
    public static bool Holding => DutyMechanic.Dungeons.LabyrinthOfTheAncients.ProfileHolding;
    /// <summary>A=0, B=1, C=2; -1 until own-party assignment is known.</summary>
    public static int Alliance => DutyMechanic.Dungeons.LabyrinthOfTheAncients.ProfileAlliance;
}
