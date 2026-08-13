using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Owns the canonical durable stores, one-shot adapters, and local background coordinator as one lifetime.</summary>
/// <remarks>
/// This runtime is inert until <see cref="StartAsync"/> is called. It is independent of Web and request lifetimes, and
/// disposal drains the coordinator before releasing the retained schedule composition.
/// </remarks>
public sealed class GovernedLoopLocalBackgroundRuntime : IAsyncDisposable
{
    private readonly GovernedLoopLocalCoordinator _coordinator;
    private readonly IDisposable _runs;
    private readonly ScheduleRuntimeFacade _schedules;
    private readonly IGovernedLoopLocalWorkRunner _work;
    private int _disposed;

    internal GovernedLoopLocalBackgroundRuntime(
        GovernedLoopLocalCoordinator coordinator,
        ScheduleRuntimeFacade schedules,
        IDisposable runs,
        IGovernedLoopLocalWorkRunner work)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _work = work ?? throw new ArgumentNullException(nameof(work));
    }

    /// <summary>Runs at most one explicit schedule, trigger, or wake unit through the same canonical one-shot composition.</summary>
    public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _work.RunOnceAsync(family, cancellationToken);
    }

    /// <summary>Acquires durable ownership and starts the browser-independent background lifetime.</summary>
    public Task<GovernedLoopLocalCoordinatorStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _coordinator.StartAsync(cancellationToken);
    }

    /// <summary>Stops new acquisition, drains the current one-shot boundary, and records terminal evidence.</summary>
    public Task<GovernedLoopLocalCoordinatorStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _coordinator.StopAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _schedules.Dispose();
            }
            finally
            {
                _runs.Dispose();
            }
        }
    }
}
