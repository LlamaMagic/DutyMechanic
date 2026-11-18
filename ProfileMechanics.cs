using ff14bot.NeoProfile;
using ff14bot.NeoProfiles;
using DutyMechanic.Managers;
using DutyMechanic.Logging;
using Buddy.Coroutines;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DutyMechanic;

/// <summary>
/// Registration of mechanic callbacks supplied by the current OrderBot profile.
/// This source is compiled into DutyMechanic itself; no contracts assembly is required.
/// </summary>
/// <remarks>
/// Call from the bot thread. Built-in encounters, including protected floor logic,
/// remain ineligible for profile registration.
/// Registration proves integration, not authorization: PandaAuth still owns profile delivery.
/// </remarks>
public static class ProfileMechanics
{
    /// <summary>Version of the profile mechanic registration contract.</summary>
    public static int ApiVersion => 1;
    private static readonly object Gate = new();
    private static readonly BotEvents.NeoProfile.NewProfileLoadedDelegate ProfileChanged = _ => Clear();
    private static Registration _current;
    private static Registration _pendingRelease;
    private static bool _releasing;
    private static bool _runtimeEnabled;

    /// <summary>Registers the active profile's mechanic callback until its lease or lifecycle ends.</summary>
    /// <param name="zoneId">Nonzero territory with no built-in DutyMechanic handler.</param>
    /// <param name="runAsync">Cooperative bot-thread callback. Observe cancellation and never own a second movement channel.</param>
    /// <param name="cleanup">Releases only this registration's state; must not register a successor during cleanup.</param>
    /// <returns>An idempotent lease. Disposing an old lease cannot remove a newer registration.</returns>
    /// <exception cref="InvalidOperationException">No profile is active, a built-in handler exists, or another registration is active/releasing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The territory is zero.</exception>
    /// <exception cref="ArgumentNullException">A callback is missing.</exception>
    public static IDisposable Register(ushort zoneId, Func<CancellationToken, Task<bool>> runAsync, Action cleanup)
        => RegisterCore(zoneId, runAsync, cleanup, null, null);

    /// <summary>Registers a profile's cooperative mechanic and optional non-yielding observer.</summary>
    /// <param name="zoneId">Territory associated with the loaded profile.</param>
    /// <param name="runAsync">Cooperative scheduler callback that observes cancellation.</param>
    /// <param name="cleanup">Idempotently releases only state owned by this registration.</param>
    /// <param name="onFault">Receives the first callback or cleanup failure for recoverable retry.</param>
    /// <param name="observe">Optional non-yielding bot-thread observation callback.</param>
    /// <returns>An idempotent registration lease.</returns>
    public static IDisposable RegisterProfile(ushort zoneId, Func<CancellationToken, Task<bool>> runAsync,
        Action cleanup, Action<string> onFault, Action<CancellationToken> observe = null)
    {
        if (onFault == null) throw new ArgumentNullException(nameof(onFault));
        return RegisterCore(zoneId, runAsync, cleanup, onFault, observe);
    }

    private static IDisposable RegisterCore(ushort zoneId, Func<CancellationToken, Task<bool>> runAsync,
        Action cleanup, Action<string> onFault, Action<CancellationToken> observe)
    {
        if (zoneId == 0) throw new ArgumentOutOfRangeException(nameof(zoneId));
        if (runAsync == null) throw new ArgumentNullException(nameof(runAsync));
        if (cleanup == null) throw new ArgumentNullException(nameof(cleanup));
        var profilePath = NeoProfileManager.CurrentProfile?.Path;
        if (string.IsNullOrWhiteSpace(profilePath)) throw new InvalidOperationException("A loaded profile is required.");
        // This profile-owned trial requires a fault callback so the route can
        // leave and retry safely rather than silently falling back.
        if (zoneId == 922 && onFault == null)
            throw new InvalidOperationException("Seat replacement requires fault recovery.");
        if (DungeonManager.HasBuiltInHandler(zoneId))
            throw new InvalidOperationException("The prototype cannot replace a built-in duty handler.");

        lock (Gate)
        {
            if (!_runtimeEnabled)
                throw new InvalidOperationException("Duty Mechanic is not enabled.");
            if (_current != null || _releasing)
                throw new InvalidOperationException("The previous profile mechanic must finish releasing first.");
            _current = new Registration(zoneId, Path.GetFullPath(profilePath), runAsync, cleanup, onFault, observe);
            BotEvents.NeoProfile.OnNewProfileLoaded += ProfileChanged;
            return _current;
        }
    }

    internal static bool HasRegistration { get { lock (Gate) return _current != null; } }

    // Plugin lifecycle sets this explicitly; a profile cannot claim a dispatcher
    // that will not receive bot-thread pulses and coroutine ticks.
    internal static void SetRuntimeEnabled(bool enabled)
    {
        lock (Gate) _runtimeEnabled = enabled;
        if (!enabled) Clear();
    }

    /// <summary>Checks whether the currently loaded profile owns the given territory.</summary>
    /// <param name="zoneId">Territory required by the profile.</param>
    /// <returns>True only for its active registration, including a retained fault.</returns>
    public static bool IsRegistered(ushort zoneId)
    {
        string profilePath = NeoProfileManager.CurrentProfile?.Path;
        lock (Gate)
            return _current != null && _current.ZoneId == zoneId && !string.IsNullOrWhiteSpace(profilePath) &&
                string.Equals(_current.ProfilePath, Path.GetFullPath(profilePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the current profile mechanic's first failure, or null while it has
    /// not failed. The route can use this to leave and retry the same duty.
    /// </summary>
    public static string FaultReason { get { lock (Gate) return _current?.Generation.Fault; } }

    /// <summary>True once the failed mechanic has released its owned state.</summary>
    public static bool FaultCleanupComplete { get { lock (Gate) return _current != null && _current.Generation.Faulted && !_current.CleanupPending; } }

    // Faulted and suspended registrations remain owners: falling back to the
    // built-in pulse during cleanup would create competing movement lifetimes.
    internal static bool Owns(ushort zoneId, string profilePath)
    {
        lock (Gate)
            return (_pendingRelease != null && _pendingRelease.ZoneId == zoneId) ||
                (_current != null && _current.ZoneId == zoneId && !string.IsNullOrWhiteSpace(profilePath) &&
                string.Equals(_current.ProfilePath, Path.GetFullPath(profilePath), StringComparison.OrdinalIgnoreCase));
    }

    // The dispatcher owns invocation. Returning Registered separately from Handled prevents a
    // cooperative false result from also ticking a second mechanic handler in this scheduler pass.
    internal static async Task<(bool Registered, bool Handled)> RunAsync(ushort zoneId, string profilePath)
    {
        Validate(zoneId, profilePath);
        Registration registration;
        lock (Gate) registration = _current;
        if (registration == null) return (false, false);
        if (!registration.Generation.TryBegin(out var stamp, out var token)) return (true, false);
        try
        {
            bool handled = await registration.RunAsync(token);
            lock (Gate) return (true, ReferenceEquals(_current, registration) && registration.Generation.Finish(stamp) && handled);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (true, false);
        }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception ex)
        {
            if (registration.OnFault == null) { Release(registration); throw; }
            Fail(registration, ex);
            return (true, false);
        }
    }

    // Pulse and scheduler share a registration, not an input owner. Observation
    // cannot yield and is suppressed while the current generation is suspended.
    internal static void Observe(ushort zoneId, string profilePath)
    {
        Validate(zoneId, profilePath);
        Registration registration;
        lock (Gate) registration = _current;
        if (registration == null) return;
        if (registration.Generation.Faulted)
        {
            RetryFailedCleanup(registration);
            return;
        }
        if (registration.Observe == null || registration.Generation.Suspended) return;
        try { registration.Observe(registration.Generation.Token); }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception ex) { Fail(registration, ex); }
    }

    internal static void SetSuspended(ushort zoneId, string profilePath, bool suspended)
    {
        Validate(zoneId, profilePath);
        SetSuspendedActive(suspended);
    }

    // Loading can temporarily make the zone unavailable. Suspend the existing
    // owner before reading identity again rather than treating zone zero as exit.
    internal static void SetSuspendedActive(bool suspended)
    {
        Registration registration;
        lock (Gate) registration = _current;
        if (registration == null) return;
        if (!suspended) { registration.Generation.Resume(); return; }
        try
        {
            if (registration.Generation.Suspend()) registration.Cleanup();
        }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception ex) { Fail(registration, ex); }
    }

    private static void Fail(Registration registration, Exception exception)
    {
        bool isNew;
        try { isNew = registration.Generation.Fail(exception.Message); }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception cancelError)
        {
            // The generation records its fault before cancellation callbacks run.
            // Keep that first reason and continue recovery even if one callback fails.
            Logger.Error("Profile mechanic cancellation failed: {0}", cancelError);
            isNew = registration.Generation.Faulted && !registration.FaultNotified;
        }
        if (!isNew) return;
        // Failure remains registered and suppresses public fallback until the
        // profile's recovery path explicitly disposes the lease and exits.
        registration.CleanupPending = true;
        RetryFailedCleanup(registration);
        registration.FaultNotified = true;
        try { registration.OnFault?.Invoke(registration.Generation.Fault); }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception notifyError) { Logger.Error("Profile mechanic fault notification failed: {0}", notifyError); }
    }

    private static void RetryFailedCleanup(Registration registration)
    {
        if (!registration.CleanupPending || DateTime.UtcNow < registration.NextCleanup) return;
        registration.NextCleanup = DateTime.UtcNow.AddSeconds(1);
        try { registration.Cleanup(); registration.CleanupPending = false; }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception cleanupError) { Logger.Error("Profile mechanic cleanup failed; will retry: {0}", cleanupError); }
    }

    internal static void Validate(ushort zoneId, string profilePath)
    {
        Registration registration;
        lock (Gate) registration = _current;
        if (registration != null && (registration.ZoneId != zoneId || string.IsNullOrWhiteSpace(profilePath) ||
            !string.Equals(registration.ProfilePath, Path.GetFullPath(profilePath), StringComparison.OrdinalIgnoreCase)))
            Release(registration);
    }

    internal static void Clear()
    {
        Registration registration;
        lock (Gate) registration = _current;
        if (registration != null) Release(registration);
    }

    private static void Release(Registration registration)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_current, registration)) return;
            _current = null;
            _releasing = true;
            _pendingRelease = registration;
            BotEvents.NeoProfile.OnNewProfileLoaded -= ProfileChanged;
        }
        // No successor may register while old cleanup runs. Clear ownership first so reentrant
        // disposal is harmless, then cancel before cleanup so pending work sees its revoked token.
        try { registration.Generation.Dispose(); }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception error) { Logger.Error("Profile mechanic cancellation during release failed: {0}", error); }
        RetryRelease();
    }

    // A failed cleanup must fence successors and the old built-in owner. Keep
    // retrying on the plugin pulse; a missing successor is safer than two movers.
    internal static void RetryRelease()
    {
        Registration registration;
        lock (Gate) registration = _pendingRelease;
        if (registration == null || DateTime.UtcNow < registration.NextReleaseCleanup) return;
        registration.NextReleaseCleanup = DateTime.UtcNow.AddSeconds(1);
        try { registration.Cleanup(); }
        catch (CoroutineStoppedException) { throw; }
        catch (Exception error) { Logger.Error("Profile mechanic release cleanup failed; will retry: {0}", error); return; }
        lock (Gate)
        {
            if (!ReferenceEquals(_pendingRelease, registration)) return;
            _pendingRelease = null;
            _releasing = false;
        }
    }

    private sealed class Registration : IDisposable
    {
        internal readonly ushort ZoneId;
        internal readonly string ProfilePath;
        internal readonly Func<CancellationToken, Task<bool>> RunAsync;
        internal readonly Action Cleanup;
        internal readonly Action<string> OnFault;
        internal readonly Action<CancellationToken> Observe;
        internal readonly ProfileMechanicGeneration Generation = new();
        internal bool CleanupPending, FaultNotified;
        internal DateTime NextCleanup, NextReleaseCleanup;

        internal Registration(ushort zoneId, string profilePath, Func<CancellationToken, Task<bool>> runAsync, Action cleanup,
            Action<string> onFault, Action<CancellationToken> observe)
        {
            ZoneId = zoneId;
            ProfilePath = profilePath;
            RunAsync = runAsync;
            Cleanup = cleanup;
            OnFault = onFault;
            Observe = observe;
        }

        public void Dispose() => Release(this);
    }
}
