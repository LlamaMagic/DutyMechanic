using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace DutyMechanic.Dungeons;

// Owned by the encounter pulse thread. Worker inputs are detached values only;
// cancellation and reset retire this worker without touching the input channel.
internal sealed class AlexandriaAmalgamWorker
{
    internal readonly record struct Result(double Started, AlexandriaGridPlanner.Step[] Route);
    private Task<AlexandriaGridPlanner.Step[]> pending;
    private CancellationTokenSource cancellation;
    private double started;

    internal bool Pending => pending != null;

    // The caller must establish complete mechanic evidence for its horizon.
    // Copy nested party centers as well as arrays, so a later pulse cannot
    // mutate a worker's view while it searches. Times are snapshot-relative.
    internal bool Start(Vector2 player, double now, AlexandriaAmalgamPlanner.Effect[] effects,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double spacing,
        double[] times, double delay, double minimumSpeed, double maximumSpeed, bool evidenceComplete,
        double preparationDelay = .35)
    {
        return evidenceComplete && StartKnownSnapshot(player, now, effects, requirements, margin, spacing,
            times, delay, minimumSpeed, maximumSpeed, preparationDelay);
    }

    // A rolling caller may plan the currently observed effects without
    // pretending that future baited casts are already known. It must check
    // the live snapshot and revalidate every edge before issuing any input.
    internal bool StartKnownSnapshot(Vector2 player, double now, AlexandriaAmalgamPlanner.Effect[] effects,
        AlexandriaPositionConstraints.Timed[] requirements, double margin, double spacing,
        double[] times, double delay, double minimumSpeed, double maximumSpeed, double preparationDelay = .35)
    {
        if (pending != null || !double.IsFinite(now) || effects == null ||
            requirements == null || times == null || times.Length < 2 || times[0] != 0 ||
            !double.IsFinite(preparationDelay) || preparationDelay < 0) return false;
        var detachedEffects = effects.ToArray();
        var detachedRequirements = requirements.Select(r => r with { Centers = r.Centers?.ToArray() }).ToArray();
        var detachedTimes = times.Select(time => time + preparationDelay).ToArray();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        started = now;
        pending = Task.Run(() =>
        {
            // The worker may consume its entire publication allowance. Reserve
            // that time at the observed starting point and certify the hold;
            // otherwise a route can already be unsafe before its first command.
            if (!AlexandriaAmalgamPlanner.TryCreateHazards(detachedEffects, margin, detachedTimes[^1], out var shapes, token))
                return Array.Empty<AlexandriaGridPlanner.Step>();
            var hold = new AlexandriaGeometry.Travel(0, preparationDelay, 0, minimumSpeed, maximumSpeed);
            if (!AlexandriaGeometry.TravelSafe(player, player, hold, shapes) ||
                !AlexandriaPositionConstraints.TravelInside(player, player, hold, detachedRequirements))
                return Array.Empty<AlexandriaGridPlanner.Step>();
            var direct = AlexandriaAmalgamDirectPlanner.Plan(player, shapes, detachedRequirements, margin, spacing,
                preparationDelay, detachedTimes[^1], delay, minimumSpeed, maximumSpeed, token);
            if (direct.Length != 0 || token.IsCancellationRequested) return direct;
            return AlexandriaAmalgamPlanner.TryPlanKnownSnapshot(player, detachedEffects, detachedRequirements,
                margin, spacing, detachedTimes, delay, minimumSpeed, maximumSpeed, out var route, token,
                compressRoute: true) ? route : Array.Empty<AlexandriaGridPlanner.Step>();
        }, token);
        return true;
    }

    // Publication does not authorize movement. The caller must compare the
    // result with current mechanic evidence and run the route evaluator from the
    // actual player position before every input, including after new hazards.
    internal bool TryTake(double now, double maximumAge, out Result result)
    {
        result = default;
        if (pending == null || !pending.IsCompleted) return false;
        var completed = pending; pending = null;
        cancellation?.Dispose(); cancellation = null;
        if (!double.IsFinite(now) || !double.IsFinite(maximumAge) || maximumAge < 0 || now < started ||
            now - started > maximumAge || !completed.IsCompletedSuccessfully)
        {
            _ = completed.Exception;
            return false;
        }
        var route = completed.Result;
        if (route.Length == 0) return false;
        result = new Result(started, route);
        return true;
    }

    internal void Reset()
    {
        cancellation?.Cancel();
        // A retired task can finish later, but has no reference to this
        // owner's state or mover and may never publish into a successor.
        if (pending != null) _ = pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        pending = null;
        cancellation?.Dispose(); cancellation = null;
        started = 0;
    }
}
