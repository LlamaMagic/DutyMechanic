using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

// The search operates on copied scalar snapshots only; RB game objects and
// movement stay on the bot thread. Only the newest request may be consumed,
// and at most one search runs at a time for this encounter instance.
internal sealed class EverkeepPlannerWorker : IDisposable
{
    private sealed record Request(long Generation, Vector2 Start,
        EverkeepTemporalPlanner.Hazard[] Hazards, int Ticks);

    private readonly Func<Vector2, EverkeepTemporalPlanner.Hazard[], int, CancellationToken,
        EverkeepTemporalPlanner.Step[]> search;
    private long generation;
    private Request pending, running;
    private CancellationTokenSource cancellation;
    private Task<EverkeepTemporalPlanner.Step[]> task;
    private bool disposed;

    internal EverkeepPlannerWorker() : this((start, hazards, ticks, token) =>
        EverkeepTemporalPlanner.Plan(start, hazards, ticks, token)) { }

    internal EverkeepPlannerWorker(Func<Vector2, EverkeepTemporalPlanner.Hazard[], int, CancellationToken,
        EverkeepTemporalPlanner.Step[]> search) => this.search = search ?? throw new ArgumentNullException(nameof(search));

    // Called on the bot thread. Copy before dispatch so the worker never reads a
    // mutable collection or native wrapper after the originating frame ends.
    internal long Submit(Vector2 start, EverkeepTemporalPlanner.Hazard[] hazards, int ticks)
    {
        if (disposed) throw new ObjectDisposedException(nameof(EverkeepPlannerWorker));
        if (hazards == null) throw new ArgumentNullException(nameof(hazards));
        var request = new Request(++generation, start, (EverkeepTemporalPlanner.Hazard[])hazards.Clone(), ticks);
        pending = request;
        cancellation?.Cancel();
        StartPending();
        return request.Generation;
    }

    // Nonblocking: a stale, cancelled, or faulted result cannot become a move.
    // Empty successful results are preserved as an explicit no-route outcome.
    internal bool TryTake(out long resultGeneration, out EverkeepTemporalPlanner.Step[] route, out bool failed)
    {
        resultGeneration = 0;
        route = null;
        failed = false;
        if (disposed) return false;
        if (task == null) { StartPending(); return false; }
        if (!task.IsCompleted) return false;
        bool current = running.Generation == generation && pending == null;
        if (task.IsFaulted) _ = task.Exception; // Observe failures even when their generation is stale.
        if (current)
        {
            resultGeneration = running.Generation;
            failed = !task.IsCompletedSuccessfully || task.Result == null;
            route = failed ? Array.Empty<EverkeepTemporalPlanner.Step>() : task.Result;
        }
        cancellation.Dispose();
        cancellation = null;
        task = null;
        running = null;
        StartPending();
        return current;
    }

    internal void Reset()
    {
        generation++;
        pending = null;
        cancellation?.Cancel();
    }

    private void StartPending()
    {
        if (disposed || task != null || pending == null) return;
        running = pending;
        pending = null;
        var request = running;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        task = Task.Run(() => search(request.Start, request.Hazards, request.Ticks, token));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pending = null;
        generation++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
    }
}
