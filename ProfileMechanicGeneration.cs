using System;
using System.Threading;

namespace DutyMechanic;

// Each run generation owns its own token. A suspended encounter can be restarted
// after death/loading without allowing a late completion to control its successor.
internal sealed class ProfileMechanicGeneration : IDisposable
{
    private CancellationTokenSource source = new();
    private bool disposed;
    private bool suspended;
    private bool running;
    private int generation;
    private string fault;

    internal bool Faulted => fault != null;
    internal string Fault => fault;
    internal bool Suspended => suspended;
    internal bool Running => running;
    internal CancellationToken Token => source.Token;

    // Called only on the bot thread; a failed callback retains the first reason
    // until the owning profile has initiated its same-route recovery.
    internal bool TryBegin(out int stamp, out CancellationToken token)
    {
        stamp = generation;
        token = default;
        if (disposed || suspended || fault != null || running) return false;
        token = source.Token;
        running = true;
        return true;
    }

    internal bool Finish(int stamp)
    {
        if (disposed || stamp != generation || !running) return false;
        running = false;
        return !suspended && fault == null;
    }

    // A generation change revokes ownership before cleanup, so reentrant or
    // delayed completion cannot release a newly admitted callback.
    internal bool Suspend()
    {
        if (disposed || suspended) return false;
        suspended = true;
        Revoke();
        return true;
    }

    internal bool Resume()
    {
        if (disposed || !suspended || fault != null) return false;
        suspended = false;
        return true;
    }

    internal bool Fail(string reason)
    {
        if (disposed || fault != null) return false;
        fault = string.IsNullOrWhiteSpace(reason) ? "Mechanic callback failed" : reason;
        Revoke();
        return true;
    }

    private void Revoke()
    {
        var previous = source;
        source = new CancellationTokenSource();
        generation++;
        running = false;
        try { previous.Cancel(); }
        finally { previous.Dispose(); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        generation++;
        running = false;
        try { source.Cancel(); }
        finally { source.Dispose(); }
    }
}
