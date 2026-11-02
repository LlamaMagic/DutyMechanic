namespace DutyMechanic.Dungeons;

// A late Vault cleanup may unwrap its own stale adapter, but must not change
// shared input or SideStep after a different live duty has acquired them.
internal static class VaultLeaseBoundary
{
    internal static bool MayRestore(bool loading, uint zone) => !loading && zone == 1066;
}
