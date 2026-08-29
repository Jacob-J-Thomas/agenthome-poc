using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Retry;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Owns activation and bounded shutdown for the canonical durable local background coordinator.</summary>
/// <remarks>
/// The factory binds one fully composed Trigger, Wait, Wake, and retained-retry work runner before exposing its
/// <c>AgentRuntime</c>. This host never creates workspace stores, accepts request-scoped dependencies, or permits a
/// second background composition. Wait recovery remains a dependency because unfinished Wait work must be recovered
/// before new work can acquire durable coordinator ownership.
/// </remarks>
internal sealed class GovernedLoopBackgroundRuntimeHost : ICustomLoopExecutionActivation, IAsyncDisposable
{
    private static readonly TimeSpan _cycleInterval = TimeSpan.FromMilliseconds(100);
    // The canonical Wait host may share a constrained Windows runner with the full verification
    // suite. Keep heartbeats frequent, but leave enough fenced lease headroom for scheduler and
    // cross-process persistence stalls without falsely terminating a healthy coordinator.
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _ownershipLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _stopDrainBound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _stopInitiationBound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _stopInitiationPollInterval = TimeSpan.FromMilliseconds(10);
    internal const string CoordinatorId = "local-background";
    private const int CandidateReadLimit = 16;
    private const int MaximumItemsPerFamilyPerCycle = 4;
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private readonly GovernedLoopCoordinatorEvidenceStore _coordinatorEvidenceStore;
    private readonly TimeProvider _timeProvider;
    private readonly GovernedLoopWaitExecutionService _wait;
    private readonly GovernedLoopRetryExecutionService _retry;
    private readonly IGovernedLoopLocalCoordinatorBoundaryObserver? _coordinatorBoundaryObserver;
    private GovernedLoopLocalCoordinator? _coordinator;
    private Task<GovernedLoopLocalCoordinatorStopResult>? _stopTask;
    private AgentRuntimeGovernedLoopBackgroundStopResult? _completedStopResult;
    private AgentRuntimeGovernedLoopBackgroundStartResult? _lastActivation;
    private string? _ownerId;
    private int _activationRequested;
    private int _backgroundWorkBound;
    private int _disposed;
    private long _activationSequence;
    private TaskCompletionSource<bool>? _disposeCompletion;

    internal GovernedLoopBackgroundRuntimeHost(
        GovernedLoopCoordinatorEvidenceStore coordinatorEvidenceStore,
        GovernedLoopWaitExecutionService wait,
        GovernedLoopRetryExecutionService retry,
        TimeProvider? timeProvider = null,
        IGovernedLoopLocalCoordinatorBoundaryObserver? coordinatorBoundaryObserver = null)
    {
        _coordinatorEvidenceStore = coordinatorEvidenceStore ?? throw new ArgumentNullException(nameof(coordinatorEvidenceStore));
        _wait = wait ?? throw new ArgumentNullException(nameof(wait));
        _retry = retry ?? throw new ArgumentNullException(nameof(retry));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _coordinatorBoundaryObserver = coordinatorBoundaryObserver;
    }

    internal void BindBackgroundWork(IGovernedLoopLocalWorkRunner work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _backgroundWorkBound, 1, 0) != 0)
        {
            throw new InvalidOperationException("The canonical local background work composition is already bound.");
        }

        var instanceId = Guid.NewGuid().ToString("N");
        _ownerId = "agent-runtime-" + instanceId;
        _coordinator = new GovernedLoopLocalCoordinator(
            _coordinatorEvidenceStore,
            new GovernedLoopRecoveringWaitWorkRunner(work, _wait, CandidateReadLimit),
            new GovernedLoopLocalCoordinatorOptions(
                CoordinatorId,
                _ownerId,
                _cycleInterval,
                _heartbeatInterval,
                _ownershipLeaseDuration,
                MaximumItemsPerFamilyPerCycle),
            _timeProvider,
            _coordinatorBoundaryObserver);
    }

    public async Task<CustomLoopExecutionActivationResult> ActivateAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _activationRequested) == 0)
        {
            return Available("Canonical local background activation has not been requested by this runtime.");
        }

        return ToActivation(await ActivateCoreAsync(cancellationToken).ConfigureAwait(false));
    }

    internal Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartAsync(
        CancellationToken cancellationToken = default)
        => ActivateCoreAsync(cancellationToken);

    internal long ActivationSequence => Volatile.Read(ref _activationSequence);

    internal bool TryGetActivationResultAfter(
        long sequence,
        out AgentRuntimeGovernedLoopBackgroundStartResult? result)
    {
        result = Volatile.Read(ref _activationSequence) > sequence
            ? Volatile.Read(ref _lastActivation)
            : null;
        return result is not null;
    }

    internal async Task<AgentRuntimeGovernedLoopBackgroundStatus> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var status = await ReadStatusCoreAsync(cancellationToken).ConfigureAwait(false);
        if (!IsDrainInProgress() || status.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Draining)
        {
            return status;
        }

        return UnavailableStatus("governed_local_background_stop_unconfirmed: a retained stop request has not durably stopped admission.");
    }

    internal async Task<AgentRuntimeGovernedLoopBackgroundStopResult> StopAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task<AgentRuntimeGovernedLoopBackgroundStopResult> WaitForStopCompletionAsync()
    {
        var stopTask = Volatile.Read(ref _stopTask);
        return stopTask is null
            ? Task.FromResult(AlreadyStopped())
            : CompleteStopAsync(stopTask, CancellationToken.None);
    }

    internal Task WaitForDisposeCompletionAsync()
        => Volatile.Read(ref _disposeCompletion)?.Task ?? Task.CompletedTask;

    private async Task<AgentRuntimeGovernedLoopBackgroundStopResult> StopCoreAsync(
        CancellationToken cancellationToken)
    {
        Task<GovernedLoopLocalCoordinatorStopResult>? stopTask;
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var coordinator = _coordinator;
            if (coordinator is null)
            {
                return new AgentRuntimeGovernedLoopBackgroundStopResult(
                    AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                    "governed_local_background_unavailable: canonical work composition was not bound before stop.");
            }

            if (_stopTask is { IsCompleted: true })
            {
                return await CompleteStopAsync(_stopTask, CancellationToken.None).ConfigureAwait(false);
            }

            var current = await ReadStatusCoreAsync(cancellationToken).ConfigureAwait(false);
            if (current.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer)
            {
                Volatile.Write(ref _activationRequested, 0);
                _stopTask ??= coordinator.ParkAfterOwnershipLossAsync();
                return new AgentRuntimeGovernedLoopBackgroundStopResult(
                    AgentRuntimeGovernedLoopBackgroundStopStatus.OwnedByLivePeer,
                    current.Readiness,
                    current.Ownership,
                    "governed_local_background_owned_by_live_peer: this runtime did not stop another process's coordinator.");
            }

            Volatile.Write(ref _activationRequested, 0);
            stopTask = _stopTask ??= coordinator.StopAsync(CancellationToken.None);
        }
        finally
        {
            _activationGate.Release();
        }

        if (!stopTask.IsCompleted
            && !await WaitForStopAdmissionAsync(stopTask).ConfigureAwait(false))
        {
            return new AgentRuntimeGovernedLoopBackgroundStopResult(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                "governed_local_background_stop_unconfirmed: durable admission did not enter stopping posture within the fixed initiation bound.");
        }

        if (stopTask.IsCompleted)
        {
            return await CompleteStopAsync(stopTask, CancellationToken.None).ConfigureAwait(false);
        }

        var completed = await Task.WhenAny(
            stopTask,
            Task.Delay(_stopDrainBound, _timeProvider, CancellationToken.None)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, stopTask))
        {
            return new AgentRuntimeGovernedLoopBackgroundStopResult(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Draining,
                AgentRuntimeGovernedLoopBackgroundReadiness.Draining,
                AgentRuntimeGovernedLoopBackgroundOwnership.Local,
                "governed_local_background_draining: the fixed drain bound elapsed; durable admission remains stopped while the retained work item reaches a safe boundary.");
        }

        return await CompleteStopAsync(stopTask, CancellationToken.None).ConfigureAwait(false);
    }

    internal void RequestActivation()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _activationRequested, 1);
    }

    private async Task<AgentRuntimeGovernedLoopBackgroundStartResult> ActivateCoreAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _activationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsDrainInProgress())
            {
                return PublishActivation(new AgentRuntimeGovernedLoopBackgroundStartResult(
                    AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundReadiness.Draining,
                    AgentRuntimeGovernedLoopBackgroundOwnership.Local,
                    true,
                    "governed_local_background_draining: the prior stop request has not reached a durable safe boundary."));
            }

            if (_stopTask is { IsCompleted: true })
            {
                _stopTask = null;
                _completedStopResult = null;
            }

            var coordinator = _coordinator;
            if (coordinator is null)
            {
                return PublishActivation(new AgentRuntimeGovernedLoopBackgroundStartResult(
                    AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                    false,
                    "governed_local_background_unavailable: canonical work composition was not bound before activation."));
            }

            try
            {
                var recoveryFailure = await RecoverAsync(cancellationToken).ConfigureAwait(false);
                if (recoveryFailure is not null)
                {
                    return PublishActivation(recoveryFailure);
                }

                var start = await coordinator.StartAsync(cancellationToken).ConfigureAwait(false);
                return PublishActivation(MapStartResult(start));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                return PublishActivation(new AgentRuntimeGovernedLoopBackgroundStartResult(
                    AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                    true,
                    $"governed_wait_coordinator_unavailable: activation failed closed ({exception.GetType().Name})."));
            }
        }
        finally
        {
            _activationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _disposeCompletion, completion, null) is { } existingCompletion)
        {
            await existingCompletion.Task.ConfigureAwait(false);
            return;
        }

        Volatile.Write(ref _disposed, 1);
        try
        {
            _ = await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
            var coordinator = _coordinator;
            var stopTask = _stopTask;
            if (coordinator is null)
            {
                completion.TrySetResult(true);
                return;
            }

            if (stopTask is { IsCompleted: false })
            {
                // Keep the complete coordinator composition alive until the exact deferred stop reaches its
                // terminal safe boundary. The AgentRuntime cannot dispose its runner, stores, or inference
                // dependencies while this task is still able to call them.
                _ = DisposeAfterDrainAsync(stopTask, coordinator, completion);
                return;
            }
            else
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
            }

            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
    }

    private AgentRuntimeGovernedLoopBackgroundStartResult PublishActivation(
        AgentRuntimeGovernedLoopBackgroundStartResult result)
    {
        Volatile.Write(ref _lastActivation, result);
        Interlocked.Increment(ref _activationSequence);
        return result;
    }

    private async Task<AgentRuntimeGovernedLoopBackgroundStartResult?> RecoverAsync(
        CancellationToken cancellationToken)
    {
        var recovery = await _wait.RecoverAsync(256, cancellationToken).ConfigureAwait(false);
        if (recovery.NeedsReview > 0)
        {
            return new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                false,
                $"governed_wait_recovery_requires_review: {recovery.NeedsReview} retained Wait recovery item(s) could not be reconciled safely.");
        }

        var retryRecovery = await _retry.RecoverAsync(256, cancellationToken).ConfigureAwait(false);
        return retryRecovery.NeedsReview > 0
            ? new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                false,
                $"governed_retry_recovery_requires_review: {retryRecovery.NeedsReview} retained retry schedule(s) could not be reconciled safely.")
            : null;
    }

    private async Task<AgentRuntimeGovernedLoopBackgroundStatus> ReadStatusCoreAsync(
        CancellationToken cancellationToken)
    {
        var read = await _coordinatorEvidenceStore.ReadAsync(CoordinatorId, cancellationToken).ConfigureAwait(false);
        if (read is null)
        {
            return UnavailableStatus("governed_local_background_unavailable: coordinator evidence could not be read safely.");
        }

        if (read.Status == GovernedLoopCoordinatorReadStatus.NotFound)
        {
            return new AgentRuntimeGovernedLoopBackgroundStatus(
                AgentRuntimeGovernedLoopBackgroundReadiness.Stopped,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                "governed_local_background_stopped: no coordinator ownership evidence exists.");
        }

        if (read.Status != GovernedLoopCoordinatorReadStatus.Found || read.Snapshot is null)
        {
            return UnavailableStatus("governed_local_background_unavailable: coordinator evidence requires repair or is unavailable.");
        }

        return MapStatus(read.Snapshot);
    }

    private AgentRuntimeGovernedLoopBackgroundStatus MapStatus(GovernedLoopCoordinatorSnapshot snapshot)
    {
        if (snapshot.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Stopped)
        {
            return new AgentRuntimeGovernedLoopBackgroundStatus(
                AgentRuntimeGovernedLoopBackgroundReadiness.Stopped,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                "governed_local_background_stopped: durable coordinator evidence is terminal.");
        }

        if (snapshot.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed)
        {
            return new AgentRuntimeGovernedLoopBackgroundStatus(
                AgentRuntimeGovernedLoopBackgroundReadiness.Degraded,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                "governed_local_background_degraded: durable coordinator evidence terminated fail closed.");
        }

        if (!HasLiveLease(snapshot))
        {
            return new AgentRuntimeGovernedLoopBackgroundStatus(
                AgentRuntimeGovernedLoopBackgroundReadiness.Degraded,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                "governed_local_background_degraded: durable ownership lease is no longer live and awaits fenced acquisition.");
        }

        var local = string.Equals(snapshot.Ownership.OwnerId, _ownerId, StringComparison.Ordinal);
        var ownership = local
            ? AgentRuntimeGovernedLoopBackgroundOwnership.Local
            : AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer;
        if (snapshot.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Stopping)
        {
            return new AgentRuntimeGovernedLoopBackgroundStatus(
                AgentRuntimeGovernedLoopBackgroundReadiness.Draining,
                ownership,
                "governed_local_background_draining: durable coordinator admission has stopped while retained work reaches a safe boundary.");
        }

        if (snapshot.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Running && local)
        {
            return new AgentRuntimeGovernedLoopBackgroundStatus(
                AgentRuntimeGovernedLoopBackgroundReadiness.Ready,
                ownership,
                "governed_local_background_ready: this runtime owns canonical background-work delivery.");
        }

        return new AgentRuntimeGovernedLoopBackgroundStatus(
            AgentRuntimeGovernedLoopBackgroundReadiness.Degraded,
            ownership,
            local
                ? "governed_local_background_degraded: local coordinator has not reached running posture."
                : "governed_local_background_owned_by_live_peer: another process owns canonical background-work delivery.");
    }

    private bool HasLiveLease(GovernedLoopCoordinatorSnapshot snapshot)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            return now != default
                && now.Offset == TimeSpan.Zero
                && snapshot.LatestHeartbeat.LeaseExpiresAtUtc > now;
        }
        catch
        {
            return false;
        }
    }

    private AgentRuntimeGovernedLoopBackgroundStartResult MapStartResult(
        GovernedLoopLocalCoordinatorStartResult start)
        => start.Status switch
        {
            GovernedLoopLocalCoordinatorStartStatus.Started => new(
                AgentRuntimeGovernedLoopBackgroundStartStatus.Started,
                AgentRuntimeGovernedLoopBackgroundReadiness.Ready,
                AgentRuntimeGovernedLoopBackgroundOwnership.Local,
                true,
                "governed_local_background_ready: this runtime acquired canonical background-work delivery."),
            GovernedLoopLocalCoordinatorStartStatus.AlreadyRunning => new(
                AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning,
                AgentRuntimeGovernedLoopBackgroundReadiness.Ready,
                AgentRuntimeGovernedLoopBackgroundOwnership.Local,
                true,
                "governed_local_background_ready: this runtime already owns canonical background-work delivery."),
            GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer => new(
                AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer,
                AgentRuntimeGovernedLoopBackgroundReadiness.Degraded,
                AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer,
                true,
                "governed_local_background_owned_by_live_peer: another process retains the active fenced coordinator lease."),
            GovernedLoopLocalCoordinatorStartStatus.Failed => new(
                AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired,
                AgentRuntimeGovernedLoopBackgroundReadiness.Degraded,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                false,
                "governed_local_background_failed: the prior coordinator session durably terminated fail closed and requires explicit repair before restart."),
            GovernedLoopLocalCoordinatorStartStatus.Conflict or GovernedLoopLocalCoordinatorStartStatus.Unavailable => new(
                AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                true,
                $"governed_wait_coordinator_unavailable: canonical background-work activation returned {start.Status}."),
            _ => new(
                AgentRuntimeGovernedLoopBackgroundStartStatus.RepairRequired,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                false,
                "governed_wait_coordinator_corrupt: canonical background-work evidence requires explicit repair."),
        };

    private async Task<AgentRuntimeGovernedLoopBackgroundStopResult> MapStopResultAsync(
        GovernedLoopLocalCoordinatorStopResult stop,
        CancellationToken cancellationToken)
    {
        var current = await ReadStatusCoreAsync(cancellationToken).ConfigureAwait(false);
        return stop.Status switch
        {
            GovernedLoopLocalCoordinatorStopStatus.Stopped => new(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped,
                AgentRuntimeGovernedLoopBackgroundReadiness.Stopped,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                "governed_local_background_stopped: local admission drained to a durable safe boundary."),
            GovernedLoopLocalCoordinatorStopStatus.AlreadyStopped when IsConfirmedStopped(current) => AlreadyStopped(),
            GovernedLoopLocalCoordinatorStopStatus.AlreadyStopped when IsConfirmedFailed(stop.Snapshot, current) => new(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Failed,
                AgentRuntimeGovernedLoopBackgroundReadiness.Degraded,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                "governed_local_background_failed: durable coordinator evidence terminated fail closed before this runtime requested stop."),
            GovernedLoopLocalCoordinatorStopStatus.AlreadyStopped => new(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable,
                current.Readiness,
                current.Ownership,
                "governed_local_background_stop_unconfirmed: no local session remained, but durable coordinator evidence is not confirmed terminal."),
            GovernedLoopLocalCoordinatorStopStatus.OwnershipLost => new(
                AgentRuntimeGovernedLoopBackgroundStopStatus.OwnershipLost,
                current.Readiness,
                current.Ownership,
                "governed_local_background_ownership_lost: local stop did not overwrite a different durable owner."),
            GovernedLoopLocalCoordinatorStopStatus.Unavailable => new(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Unavailable,
                current.Readiness,
                current.Ownership,
                "governed_local_background_unavailable: durable terminal evidence could not be confirmed."),
            _ => new(
                AgentRuntimeGovernedLoopBackgroundStopStatus.Failed,
                current.Readiness,
                current.Ownership,
                "governed_local_background_failed: the coordinator terminated fail closed before a normal stop."),
        };
    }

    private async Task<AgentRuntimeGovernedLoopBackgroundStopResult> CompleteStopAsync(
        Task<GovernedLoopLocalCoordinatorStopResult> stopTask,
        CancellationToken cancellationToken)
    {
        var completed = Volatile.Read(ref _completedStopResult);
        if (completed is not null)
        {
            return completed;
        }

        var stop = await stopTask.ConfigureAwait(false);
        var mapped = await MapStopResultAsync(stop, cancellationToken).ConfigureAwait(false);
        return Interlocked.CompareExchange(ref _completedStopResult, mapped, null) ?? mapped;
    }

    private static CustomLoopExecutionActivationResult ToActivation(
        AgentRuntimeGovernedLoopBackgroundStartResult result)
        => result.Status is AgentRuntimeGovernedLoopBackgroundStartStatus.Started
            or AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning
            or AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer
            ? Available(result.Detail)
            : Unavailable(result.RetryAllowed, "Failed", result.Detail);

    private static AgentRuntimeGovernedLoopBackgroundStatus UnavailableStatus(string detail)
        => new(
            AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
            AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
            detail);

    private static AgentRuntimeGovernedLoopBackgroundStopResult AlreadyStopped()
        => new(
            AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped,
            AgentRuntimeGovernedLoopBackgroundReadiness.Stopped,
            AgentRuntimeGovernedLoopBackgroundOwnership.None,
            "governed_local_background_stopped: this runtime has no active local coordinator to stop.");

    private static bool IsConfirmedStopped(AgentRuntimeGovernedLoopBackgroundStatus status)
        => status.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Stopped
            && status.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.None;

    private static bool IsConfirmedFailed(
        GovernedLoopCoordinatorSnapshot? snapshot,
        AgentRuntimeGovernedLoopBackgroundStatus status)
        => snapshot?.LatestLifecycle.Status == GovernedLoopCoordinatorStatus.Failed
            && status.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Degraded
            && status.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.None;

    private static async Task DisposeAfterDrainAsync(
        Task<GovernedLoopLocalCoordinatorStopResult> stopTask,
        GovernedLoopLocalCoordinator coordinator,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            _ = await stopTask.ConfigureAwait(false);
            await coordinator.DisposeAsync().ConfigureAwait(false);
            completion.TrySetResult(true);
        }
        catch (Exception exception)
        {
            // A retained coordinator failure is already represented in durable evidence; disposal must not fabricate a terminal posture.
            completion.TrySetException(exception);
        }
    }

    private bool IsDrainInProgress()
        => _stopTask is { IsCompleted: false };

    private async Task<bool> WaitForStopAdmissionAsync(
        Task<GovernedLoopLocalCoordinatorStopResult> stopTask)
    {
        var deadline = Task.Delay(_stopInitiationBound, _timeProvider, CancellationToken.None);
        while (true)
        {
            if (stopTask.IsCompleted)
            {
                return true;
            }

            var status = await ReadStatusCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (status.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Draining)
            {
                return true;
            }

            if (ReferenceEquals(
                    await Task.WhenAny(
                        Task.Delay(_stopInitiationPollInterval, _timeProvider, CancellationToken.None),
                        deadline).ConfigureAwait(false),
                    deadline))
            {
                return stopTask.IsCompleted;
            }
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
