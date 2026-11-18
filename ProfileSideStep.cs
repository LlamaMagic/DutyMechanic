namespace DutyMechanic;

/// <summary>Forwards profile mechanic override requests to DutyMechanic's existing SideStep helper.</summary>
public static class ProfileSideStep
{
    /// <summary>Requests an action override; returns whether SideStep accepted it.</summary>
    /// <param name="action">Action identifier to override.</param>
    /// <returns>Whether the override was accepted.</returns>
    public static bool Override(uint action) => LlamaLibrary.Helpers.SideStep.Override(action);

    /// <summary>Releases an action override previously owned by the caller.</summary>
    /// <param name="action">Action identifier to release.</param>
    /// <returns>Whether the override was removed.</returns>
    public static bool RemoveHandler(uint action) => LlamaLibrary.Helpers.SideStep.RemoveHandler(action);
}
