using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Persistence.Triggers.Schedules;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Couples executable Wait recovery to the canonical durable local coordinator.</summary>
internal sealed class GovernedLoopWaitRuntimeHost : ICustomLoopExecutionActivation, IAsyncDisposable
{
    private static readonly TimeSpan _cycleInterval = TimeSpan.FromMilliseconds(100);
    // The canonical Wait host may share a constrained Windows runner with the full verification
    // suite. Keep heartbeats frequent, but leave enough fenced lease headroom for scheduler and
    // cross-process persistence stalls without falsely terminating a healthy coordinator.
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _ownershipLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _takeoverMargin = TimeSpan.FromMilliseconds(25);
    internal const string CoordinatorId = "local-background";
    private const int CandidateReadLimit = 16;
    private const int MaximumItemsPerFamilyPerCycle = 4;
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private readonly GovernedLoopLocalCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;
    private readonly GovernedLoopWaitExecutionService _wait;
    private int _activationRequested;
    private int _disposed;

    internal GovernedLoopWaitRuntimeHost(
        ScheduleStore scheduleStore,
        GovernedLoopSleepStore sleepStore,
        GovernedLoopCoordinatorEvidenceStore coordinatorEvidenceStore,
        GovernedLoopSleepService sleep,
        GovernedLoopWaitExecutionService wait,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(scheduleStore);
        ArgumentNullException.ThrowIfNull(sleepStore);
        ArgumentNullException.ThrowIfNull(coordinatorEvidenceStore);
        ArgumentNullException.ThrowIfNull(sleep);
        _wait = wait ?? throw new ArgumentNullException(nameof(wait));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var instanceId = Guid.NewGuid().ToString("N");
        var backgroundWork = new GovernedLoopBackgroundWorkSource(
            scheduleStore,
            sleepStore);
        var canonicalWork = new GovernedLoopLocalWorkRunner(
            backgroundWork,
            new GovernedLoopWaitOnlyOneShotServices(sleep),
            new GovernedLoopLocalWorkRunnerOptions(
                "agent-runtime-wait-" + instanceId,
                _ownershipLeaseDuration,
                1,
                CandidateReadLimit),
            _timeProvider);
        var recoveringWork = new GovernedLoopRecoveringWaitWorkRunner(
            new GovernedLoopWaitOnlyWorkRunner(canonicalWork),
            wait,
            CandidateReadLimit);
        _coordinator = new GovernedLoopLocalCoordinator(
            coordinatorEvidenceStore,
            recoveringWork,
            new GovernedLoopLocalCoordinatorOptions(
                CoordinatorId,
                "agent-runtime-" + instanceId,
                _cycleInterval,
                _heartbeatInterval,
                _ownershipLeaseDuration,
                MaximumItemsPerFamilyPerCycle),
            _timeProvider);
    }

    public async Task<CustomLoopExecutionActivationResult> ActivateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _activationRequested) == 0)
        {
            return Available("Canonical Wait background activation has not been requested by this runtime.");
        }

        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            try
            {
                var recovery = await _wait.RecoverAsync(256, cancellationToken).ConfigureAwait(false);
                if (recovery.NeedsReview > 0)
                {
                    return Unavailable(
                        retryAllowed: false,
                        status: "Failed",
                        detail: $"governed_wait_recovery_requires_review: {recovery.NeedsReview} retained Wait recovery item(s) could not be reconciled safely.");
                }

                var start = await StartCoordinatorAsync(cancellationToken).ConfigureAwait(false);
                return start.Status switch
                {
                    GovernedLoopLocalCoordinatorStartStatus.Started or GovernedLoopLocalCoordinatorStartStatus.AlreadyRunning
                        => Available("Canonical Wait recovery and durable background-work ownership are active."),
                    GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer
                        => Available("Another live canonical coordinator already owns durable background-work delivery."),
                    GovernedLoopLocalCoordinatorStartStatus.Conflict or GovernedLoopLocalCoordinatorStartStatus.Unavailable
                        => Unavailable(
                            retryAllowed: true,
                            status: "Failed",
                            detail: $"governed_wait_coordinator_unavailable: canonical background-work activation returned {start.Status}."),
                    _ => Unavailable(
                        retryAllowed: false,
                        status: "Failed",
                        detail: "governed_wait_coordinator_corrupt: canonical background-work evidence requires explicit repair."),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Unavailable(
                    retryAllowed: true,
                    status: "Failed",
                    detail: $"governed_wait_coordinator_unavailable: activation failed closed ({exception.GetType().Name}).");
            }
        }
        finally
        {
            _activationGate.Release();
        }
    }

    internal void RequestActivation()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _activationRequested, 1);
    }

    private async Task<GovernedLoopLocalCoordinatorStartResult> StartCoordinatorAsync(
        CancellationToken cancellationToken)
    {
        var start = await _coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
        if (start.Status != GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer
            || start.Snapshot is not { } snapshot)
        {
            return start;
        }

        DateTimeOffset now;
        try
        {
            now = _timeProvider.GetUtcNow();
        }
        catch
        {
            return start;
        }

        if (now == default || now.Offset != TimeSpan.Zero)
        {
            return start;
        }

        var remaining = snapshot.LatestHeartbeat.LeaseExpiresAtUtc - now;
        if (remaining > TimeSpan.Zero)
        {
            if (remaining > _ownershipLeaseDuration)
            {
                return start;
            }

            await Task.Delay(remaining + _takeoverMargin, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return await _coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _activationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _activationGate.Release();
            _activationGate.Dispose();
        }
    }

    private static CustomLoopExecutionActivationResult Available(string detail)
        => new(
            true,
            true,
            "Available",
            detail);

    private static CustomLoopExecutionActivationResult Unavailable(
        bool retryAllowed,
        string status,
        string detail)
        => new(false, retryAllowed, status, detail);
}
