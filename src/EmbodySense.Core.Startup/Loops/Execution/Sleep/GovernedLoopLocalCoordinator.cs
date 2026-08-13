using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Owns one fenced, restart-safe, single-host background lifetime over canonical one-shot work.</summary>
/// <remarks>
/// This type owns no browser or request lifetime. Starting acquires durable ownership before admitting work. Stopping
/// cancels new one-shot acquisition, waits for the current one-shot call to return its truthful safe-boundary result, keeps
/// the ownership heartbeat alive while draining, and only then records terminal lifecycle evidence. The coordinator lease
/// fences ownership evidence and rejects work whose trusted admission sample predates the latest durable heartbeat or is
/// already expired; it is not a transaction across the independent schedule, queue, and sleep stores. A process paused
/// across lease expiry may overlap its successor, so each canonical one-shot remains the authoritative CAS/idempotency fence
/// that prevents duplicate work or continuation.
/// </remarks>
public sealed class GovernedLoopLocalCoordinator : IAsyncDisposable
{
    private static readonly GovernedLoopLocalWorkFamily[] _workFamilies =
    [
        GovernedLoopLocalWorkFamily.Schedule,
        GovernedLoopLocalWorkFamily.Trigger,
        GovernedLoopLocalWorkFamily.Wake
    ];

    private readonly SemaphoreSlim _evidenceGate = new(1, 1);
    private readonly IGovernedLoopLocalCoordinatorBoundaryObserver? _boundaryObserver;
    private readonly IGovernedLoopCoordinatorEvidencePort _evidence;
    private readonly GovernedLoopLocalCoordinatorOptions _options;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly IGovernedLoopLocalWorkRunner _work;
    private int _disposed;
    private GovernedLoopCoordinatorSnapshot? _lastSnapshot;
    private GovernedLoopLocalCoordinatorSession? _session;

    /// <summary>Creates one inert local coordinator over injected durable evidence and one-shot work boundaries.</summary>
    /// <param name="evidence">The canonical atomic ownership, lifecycle, heartbeat, and failure evidence port.</param>
    /// <param name="work">The canonical one-shot work-family runner.</param>
    /// <param name="options">The bounded coordinator identity, lease, cadence, and fairness policy.</param>
    /// <param name="timeProvider">An optional trusted UTC clock.</param>
    /// <param name="boundaryObserver">An optional non-authoritative safe-boundary timing observer.</param>
    public GovernedLoopLocalCoordinator(
        IGovernedLoopCoordinatorEvidencePort evidence,
        IGovernedLoopLocalWorkRunner work,
        GovernedLoopLocalCoordinatorOptions options,
        TimeProvider? timeProvider = null,
        IGovernedLoopLocalCoordinatorBoundaryObserver? boundaryObserver = null)
    {
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _options = ValidateOptions(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _boundaryObserver = boundaryObserver;
    }

    /// <summary>Acquires fenced ownership and starts the browser-independent background lifetime.</summary>
    /// <remarks>
    /// Cancellation is honored until acquisition is durable. An ambiguous acquisition is reconciled by exact proposed
    /// evidence before cancellation is rethrown. After acquisition, running evidence and the owned lifetime are completed
    /// independently of the caller token.
    /// </remarks>
    public async Task<GovernedLoopLocalCoordinatorStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                return new GovernedLoopLocalCoordinatorStartResult(
                    GovernedLoopLocalCoordinatorStartStatus.AlreadyRunning,
                    _session.Snapshot);
            }

            var read = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (read.Status is not (GovernedLoopCoordinatorReadStatus.Found or GovernedLoopCoordinatorReadStatus.NotFound))
            {
                return new GovernedLoopLocalCoordinatorStartResult(
                    read.Status == GovernedLoopCoordinatorReadStatus.Corrupt
                        ? GovernedLoopLocalCoordinatorStartStatus.Corrupt
                        : GovernedLoopLocalCoordinatorStartStatus.Unavailable);
            }

            GovernedLoopLocalCoordinatorStartResult? blocked = null;
            if (!TryGetUtcNow(out var acquiredAtUtc)
                || !TryCreateAcquisition(read, acquiredAtUtc, out var request, out blocked))
            {
                return blocked ?? new GovernedLoopLocalCoordinatorStartResult(GovernedLoopLocalCoordinatorStartStatus.Corrupt);
            }

            var acquisition = await AcquireAsync(request!, cancellationToken).ConfigureAwait(false);
            if (acquisition.Status is not (GovernedLoopCoordinatorAcquisitionStatus.Acquired or GovernedLoopCoordinatorAcquisitionStatus.Duplicate))
            {
                _lastSnapshot = acquisition.Snapshot;
                return new GovernedLoopLocalCoordinatorStartResult(Map(acquisition.Status), acquisition.Snapshot);
            }

            if (!IsExactAcquisition(acquisition.Snapshot, request!))
            {
                return new GovernedLoopLocalCoordinatorStartResult(GovernedLoopLocalCoordinatorStartStatus.Corrupt);
            }

            var running = CreateLifecycle(acquisition.Snapshot!, GovernedLoopCoordinatorStatus.Running, terminal: false);
            var runningMutation = await AppendLifecycleAsync(acquisition.Snapshot!, running).ConfigureAwait(false);
            if (runningMutation.Status != GovernedLoopLocalCoordinatorMutationStatus.Succeeded || runningMutation.Snapshot is null)
            {
                _lastSnapshot = runningMutation.Snapshot ?? acquisition.Snapshot;
                return new GovernedLoopLocalCoordinatorStartResult(MapStart(runningMutation.Status), _lastSnapshot);
            }

            var session = new GovernedLoopLocalCoordinatorSession(runningMutation.Snapshot, _workFamilies.Length);
            _session = session;
            _lastSnapshot = session.Snapshot;
            session.Completion = Task.Run(() => RunSessionAsync(session));
            return new GovernedLoopLocalCoordinatorStartResult(GovernedLoopLocalCoordinatorStartStatus.Started, session.Snapshot);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Stops new acquisition, drains the current one-shot call, and records truthful terminal evidence.</summary>
    /// <remarks>
    /// The caller token is honored before shutdown is requested. Once shutdown begins, this method waits independently of
    /// that token so caller cancellation cannot abandon an owned work operation or fabricate stopped posture.
    /// </remarks>
    public async Task<GovernedLoopLocalCoordinatorStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<GovernedLoopLocalCoordinatorStopResult> StopCoreAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session;
            if (session is null)
            {
                return new GovernedLoopLocalCoordinatorStopResult(
                    GovernedLoopLocalCoordinatorStopStatus.AlreadyStopped,
                    _lastSnapshot);
            }

            Interlocked.Exchange(ref session.StopRequested, 1);
            session.AdmissionStop.Cancel();
            var outcome = await session.Completion.ConfigureAwait(false);
            _lastSnapshot = outcome.Snapshot;
            _session = null;
            session.Dispose();
            return new GovernedLoopLocalCoordinatorStopResult(outcome.Status, outcome.Snapshot);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<GovernedLoopLocalCoordinatorSessionOutcome> RunSessionAsync(GovernedLoopLocalCoordinatorSession session)
    {
        try
        {
            var workTask = RunWorkLoopAsync(session);
            var heartbeatTask = RunHeartbeatLoopAsync(session);
            var first = await Task.WhenAny(workTask, heartbeatTask).ConfigureAwait(false);
            GovernedLoopLocalCoordinatorRunExit workExit;
            GovernedLoopLocalCoordinatorRunExit heartbeatExit;
            if (ReferenceEquals(first, workTask))
            {
                workExit = await workTask.ConfigureAwait(false);
                session.AdmissionStop.Cancel();
                session.HeartbeatStop.Cancel();
                heartbeatExit = await heartbeatTask.ConfigureAwait(false);
            }
            else
            {
                heartbeatExit = await heartbeatTask.ConfigureAwait(false);
                session.AdmissionStop.Cancel();
                workExit = await workTask.ConfigureAwait(false);
                session.HeartbeatStop.Cancel();
            }

            var fatal = heartbeatExit.IsFatal ? heartbeatExit : workExit.IsFatal ? workExit : null;
            if (fatal is not null)
            {
                return await PersistFailureAsync(session, fatal).ConfigureAwait(false);
            }

            if (Volatile.Read(ref session.StopRequested) == 0)
            {
                return await PersistFailureAsync(
                    session,
                    GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.Unexpected, "coordinator-loop-ended")).ConfigureAwait(false);
            }

            return await PersistStoppedAsync(session).ConfigureAwait(false);
        }
        catch (Exception)
        {
            session.AdmissionStop.Cancel();
            session.HeartbeatStop.Cancel();
            return await PersistFailureAsync(
                session,
                GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.Unexpected, "coordinator-loop-faulted")).ConfigureAwait(false);
        }
    }

    private async Task<GovernedLoopLocalCoordinatorRunExit> RunWorkLoopAsync(GovernedLoopLocalCoordinatorSession session)
    {
        while (true)
        {
            try
            {
                session.AdmissionStop.Token.ThrowIfCancellationRequested();
                var start = (int)(session.CycleNumber % _workFamilies.Length);
                session.CycleNumber++;
                for (var offset = 0; offset < _workFamilies.Length; offset++)
                {
                    var family = _workFamilies[(start + offset) % _workFamilies.Length];
                    for (var attempt = 0; attempt < _options.MaximumItemsPerFamilyPerCycle; attempt++)
                    {
                        await _evidenceGate.WaitAsync(session.AdmissionStop.Token).ConfigureAwait(false);
                        try
                        {
                            session.AdmissionStop.Token.ThrowIfCancellationRequested();
                            if (!TryGetUtcNow(out var admittedAtUtc))
                            {
                                return GovernedLoopLocalCoordinatorRunExit.Fatal(
                                    GovernedLoopCoordinatorFailureKind.Unexpected,
                                    "work-admission-clock-unavailable");
                            }

                            var heartbeat = session.Snapshot.LatestHeartbeat;
                            if (admittedAtUtc < heartbeat.RecordedAtUtc)
                            {
                                return GovernedLoopLocalCoordinatorRunExit.Fatal(
                                    GovernedLoopCoordinatorFailureKind.Unexpected,
                                    "work-admission-clock-rollback");
                            }

                            if (admittedAtUtc >= heartbeat.LeaseExpiresAtUtc)
                            {
                                return GovernedLoopLocalCoordinatorRunExit.Fatal(
                                    GovernedLoopCoordinatorFailureKind.HeartbeatExpired,
                                    "work-admission-lease-expired");
                            }
                        }
                        finally
                        {
                            _evidenceGate.Release();
                        }

                        GovernedLoopLocalWorkResult? result;
                        try
                        {
                            result = await _work.RunOnceAsync(family, session.AdmissionStop.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (session.AdmissionStop.IsCancellationRequested)
                        {
                            return GovernedLoopLocalCoordinatorRunExit.Stopped;
                        }
                        catch (Exception)
                        {
                            return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.Unexpected, $"{Family(family)}-runner-faulted");
                        }

                        if (!IsValid(result))
                        {
                            return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, $"{Family(family)}-result-corrupt");
                        }

                        var resultExit = await ClassifyWorkResultAsync(session, family, result!).ConfigureAwait(false);
                        if (resultExit is not null)
                        {
                            return resultExit;
                        }

                        if (result!.Status is GovernedLoopLocalWorkResultStatus.Empty
                            or GovernedLoopLocalWorkResultStatus.Backpressured
                            or GovernedLoopLocalWorkResultStatus.AttentionRequired
                            or GovernedLoopLocalWorkResultStatus.Conflict)
                        {
                            break;
                        }
                    }
                }

                await Task.Delay(_options.CycleInterval, session.AdmissionStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.AdmissionStop.IsCancellationRequested)
            {
                return GovernedLoopLocalCoordinatorRunExit.Stopped;
            }
        }
    }

    private async Task<GovernedLoopLocalCoordinatorRunExit?> ClassifyWorkResultAsync(
        GovernedLoopLocalCoordinatorSession session,
        GovernedLoopLocalWorkFamily family,
        GovernedLoopLocalWorkResult result)
    {
        switch (result.Status)
        {
            case GovernedLoopLocalWorkResultStatus.Completed:
            case GovernedLoopLocalWorkResultStatus.Empty:
            case GovernedLoopLocalWorkResultStatus.AttentionRequired:
            case GovernedLoopLocalWorkResultStatus.Conflict:
                session.BackpressureRecorded[(int)family] = false;
                return null;
            case GovernedLoopLocalWorkResultStatus.Backpressured:
                if (session.BackpressureRecorded[(int)family])
                {
                    return null;
                }

                var mutation = await AppendFailureAsync(
                    session,
                    GovernedLoopCoordinatorFailureKind.Backpressured,
                    $"{Family(family)}-backpressured").ConfigureAwait(false);
                if (mutation.Status != GovernedLoopLocalCoordinatorMutationStatus.Succeeded)
                {
                    return mutation.Status switch
                    {
                        GovernedLoopLocalCoordinatorMutationStatus.OwnershipLost or GovernedLoopLocalCoordinatorMutationStatus.Conflict => GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.OwnershipLost, "failure-evidence-ownership-lost"),
                        GovernedLoopLocalCoordinatorMutationStatus.Corrupt => GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, "failure-evidence-corrupt"),
                        _ => GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.StoreUnavailable, "failure-evidence-unavailable")
                    };
                }

                session.BackpressureRecorded[(int)family] = true;
                return null;
            case GovernedLoopLocalWorkResultStatus.Unavailable:
                return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.StoreUnavailable, $"{Family(family)}-unavailable");
            case GovernedLoopLocalWorkResultStatus.Corrupt:
                return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, $"{Family(family)}-corrupt");
            default:
                return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, $"{Family(family)}-status-corrupt");
        }
    }

    private async Task<GovernedLoopLocalCoordinatorRunExit> RunHeartbeatLoopAsync(GovernedLoopLocalCoordinatorSession session)
    {
        while (true)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, session.HeartbeatStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (session.HeartbeatStop.IsCancellationRequested)
            {
                return GovernedLoopLocalCoordinatorRunExit.Stopped;
            }

            ObserveHeartbeatDue();
            await _evidenceGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!TryGetUtcNow(out var recordedAtUtc))
                {
                    return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.Unexpected, "heartbeat-clock-unavailable");
                }

                var current = session.Snapshot;
                if (recordedAtUtc >= current.LatestHeartbeat.LeaseExpiresAtUtc)
                {
                    return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.HeartbeatExpired, "heartbeat-lease-expired");
                }

                if (!TryAdd(recordedAtUtc, _options.OwnershipLeaseDuration, out var leaseExpiresAtUtc)
                    || leaseExpiresAtUtc <= current.LatestHeartbeat.LeaseExpiresAtUtc
                    || current.LatestHeartbeat.HeartbeatSequence >= GovernedLoopSleepContractLimits.MaxVersion)
                {
                    return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, "heartbeat-successor-invalid");
                }

                var next = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
                    GovernedLoopCoordinatorHeartbeat.CurrentSchemaVersion,
                    current.LatestHeartbeat.HeartbeatSequence + 1,
                    current.Ownership,
                    recordedAtUtc,
                    leaseExpiresAtUtc,
                    string.Empty));
                var request = new GovernedLoopCoordinatorHeartbeatMutationRequest(
                    current.Ownership,
                    current.Ownership.ContentHash,
                    current.LatestHeartbeat.HeartbeatSequence,
                    current.LatestHeartbeat.ContentHash,
                    next);
                if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
                {
                    return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, "heartbeat-request-invalid");
                }

                GovernedLoopCoordinatorHeartbeatMutationResult? result;
                try
                {
                    result = await _evidence.RenewHeartbeatAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    result = new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable);
                }

                if (!GovernedLoopCoordinatorEvidenceContract.IsValid(result))
                {
                    return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, "heartbeat-result-corrupt");
                }

                if (result!.Status == GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable)
                {
                    var reconciled = await ReconcileHeartbeatAsync(request).ConfigureAwait(false);
                    if (reconciled is not null)
                    {
                        result = reconciled;
                    }
                }

                if (result.Status is GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed
                    or GovernedLoopCoordinatorHeartbeatMutationStatus.Duplicate)
                {
                    if (!IsExactHeartbeat(result.Snapshot, next))
                    {
                        return GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, "heartbeat-result-mismatch");
                    }

                    session.Snapshot = result.Snapshot!;
                    _lastSnapshot = result.Snapshot;
                    continue;
                }

                if (result.Snapshot is not null)
                {
                    session.Snapshot = result.Snapshot;
                    _lastSnapshot = result.Snapshot;
                }

                return result.Status switch
                {
                    GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost
                        or GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict
                        => GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.OwnershipLost, "heartbeat-ownership-lost"),
                    GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt
                        => GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.CorruptState, "heartbeat-store-corrupt"),
                    _ => GovernedLoopLocalCoordinatorRunExit.Fatal(GovernedLoopCoordinatorFailureKind.StoreUnavailable, "heartbeat-store-unavailable")
                };
            }
            finally
            {
                _evidenceGate.Release();
            }
        }
    }

    private async Task<GovernedLoopLocalCoordinatorSessionOutcome> PersistStoppedAsync(GovernedLoopLocalCoordinatorSession session)
    {
        var stopping = await EnsureLifecycleAsync(session, GovernedLoopCoordinatorStatus.Stopping, terminal: false).ConfigureAwait(false);
        if (stopping.Status != GovernedLoopLocalCoordinatorMutationStatus.Succeeded)
        {
            return TerminalFromMutation(stopping.Status, session.Snapshot);
        }

        var stopped = await EnsureLifecycleAsync(session, GovernedLoopCoordinatorStatus.Stopped, terminal: true).ConfigureAwait(false);
        return stopped.Status == GovernedLoopLocalCoordinatorMutationStatus.Succeeded
            ? new GovernedLoopLocalCoordinatorSessionOutcome(GovernedLoopLocalCoordinatorStopStatus.Stopped, session.Snapshot)
            : TerminalFromMutation(stopped.Status, session.Snapshot);
    }

    private async Task<GovernedLoopLocalCoordinatorSessionOutcome> PersistFailureAsync(GovernedLoopLocalCoordinatorSession session, GovernedLoopLocalCoordinatorRunExit exit)
    {
        if (exit.FailureKind == GovernedLoopCoordinatorFailureKind.OwnershipLost)
        {
            return new GovernedLoopLocalCoordinatorSessionOutcome(GovernedLoopLocalCoordinatorStopStatus.OwnershipLost, session.Snapshot);
        }

        var failure = await AppendFailureAsync(session, exit.FailureKind, exit.EvidenceReference).ConfigureAwait(false);
        if (failure.Status is GovernedLoopLocalCoordinatorMutationStatus.OwnershipLost
            or GovernedLoopLocalCoordinatorMutationStatus.Conflict)
        {
            return TerminalFromMutation(failure.Status, session.Snapshot);
        }

        var failed = await EnsureLifecycleAsync(session, GovernedLoopCoordinatorStatus.Failed, terminal: true).ConfigureAwait(false);
        if (failed.Status == GovernedLoopLocalCoordinatorMutationStatus.Succeeded)
        {
            return new GovernedLoopLocalCoordinatorSessionOutcome(GovernedLoopLocalCoordinatorStopStatus.Failed, session.Snapshot);
        }

        return TerminalFromMutation(failed.Status, session.Snapshot);
    }

    private async Task<GovernedLoopLocalCoordinatorMutationOutcome> EnsureLifecycleAsync(
        GovernedLoopLocalCoordinatorSession session,
        GovernedLoopCoordinatorStatus status,
        bool terminal)
    {
        await _evidenceGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = session.Snapshot;
            if (current.LatestLifecycle.Status == status)
            {
                return GovernedLoopLocalCoordinatorMutationOutcome.Success(current);
            }

            if (current.LatestLifecycle.Status is GovernedLoopCoordinatorStatus.Stopped or GovernedLoopCoordinatorStatus.Failed)
            {
                return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
            }

            GovernedLoopCoordinatorLifecycle next;
            try
            {
                next = CreateLifecycle(current, status, terminal);
            }
            catch (Exception)
            {
                return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
            }

            var result = await AppendLifecycleCoreAsync(current, next).ConfigureAwait(false);
            if (result.Snapshot is not null)
            {
                session.Snapshot = result.Snapshot;
                _lastSnapshot = result.Snapshot;
            }

            return result;
        }
        finally
        {
            _evidenceGate.Release();
        }
    }

    private async Task<GovernedLoopLocalCoordinatorMutationOutcome> AppendLifecycleAsync(
        GovernedLoopCoordinatorSnapshot current,
        GovernedLoopCoordinatorLifecycle next)
    {
        await _evidenceGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await AppendLifecycleCoreAsync(current, next).ConfigureAwait(false);
        }
        finally
        {
            _evidenceGate.Release();
        }
    }

    private async Task<GovernedLoopLocalCoordinatorMutationOutcome> AppendLifecycleCoreAsync(
        GovernedLoopCoordinatorSnapshot current,
        GovernedLoopCoordinatorLifecycle next)
    {
        var request = new GovernedLoopCoordinatorLifecycleMutationRequest(
            current.Ownership,
            current.Ownership.ContentHash,
            current.LatestLifecycle.LifecycleVersion,
            current.LatestLifecycle.ContentHash,
            next);
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request)
            || !GovernedLoopSleepContractValidator.ValidateTransition(current.LatestLifecycle, next).IsValid)
        {
            return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
        }

        GovernedLoopCoordinatorLifecycleMutationResult? result;
        try
        {
            result = await _evidence.AppendLifecycleAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable);
        }

        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(result))
        {
            return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
        }

        if (result!.Status == GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable)
        {
            var reconciled = await ReconcileLifecycleAsync(request).ConfigureAwait(false);
            if (reconciled is not null)
            {
                result = reconciled;
            }
        }

        if (result.Status is GovernedLoopCoordinatorLifecycleMutationStatus.Appended
            or GovernedLoopCoordinatorLifecycleMutationStatus.Duplicate)
        {
            return IsExactLifecycle(result.Snapshot, next)
                ? GovernedLoopLocalCoordinatorMutationOutcome.Success(result.Snapshot!)
                : GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(result.Snapshot ?? current);
        }

        return result.Status switch
        {
            GovernedLoopCoordinatorLifecycleMutationStatus.OwnershipLost => GovernedLoopLocalCoordinatorMutationOutcome.OwnershipLost(result.Snapshot!),
            GovernedLoopCoordinatorLifecycleMutationStatus.Conflict => GovernedLoopLocalCoordinatorMutationOutcome.Conflict(result.Snapshot!),
            GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt => GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current),
            _ => GovernedLoopLocalCoordinatorMutationOutcome.Unavailable(current)
        };
    }

    private async Task<GovernedLoopLocalCoordinatorMutationOutcome> AppendFailureAsync(
        GovernedLoopLocalCoordinatorSession session,
        GovernedLoopCoordinatorFailureKind kind,
        string evidenceReference)
    {
        await _evidenceGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = session.Snapshot;
            if (!TryGetUtcNow(out var occurredAtUtc)
                || current.LatestFailureSequence >= GovernedLoopSleepContractLimits.MaxVersion)
            {
                return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
            }

            var sequence = current.LatestFailureSequence + 1;
            var failure = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorFailure(
                GovernedLoopCoordinatorFailure.CurrentSchemaVersion,
                sequence,
                current.Ownership,
                kind,
                evidenceReference,
                occurredAtUtc < current.Ownership.AcquiredAtUtc ? current.Ownership.AcquiredAtUtc : occurredAtUtc,
                string.Empty));
            var request = new GovernedLoopCoordinatorFailureMutationRequest(
                current.Ownership,
                current.Ownership.ContentHash,
                current.LatestFailureSequence == 0
                    ? GovernedLoopCoordinatorPriorFailureExpectation.None
                    : GovernedLoopCoordinatorPriorFailureExpectation.Existing,
                current.LatestFailureSequence,
                current.LatestFailureHash,
                failure);
            if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
            {
                return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
            }

            GovernedLoopCoordinatorFailureMutationResult? result;
            try
            {
                result = await _evidence.AppendFailureAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                result = new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Unavailable);
            }

            if (!GovernedLoopCoordinatorEvidenceContract.IsValid(result))
            {
                return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current);
            }

            if (result!.Status == GovernedLoopCoordinatorFailureMutationStatus.Unavailable)
            {
                var reconciled = await ReconcileFailureAsync(request).ConfigureAwait(false);
                if (reconciled is not null)
                {
                    result = reconciled;
                }
            }

            if (result.Status is GovernedLoopCoordinatorFailureMutationStatus.Appended
                or GovernedLoopCoordinatorFailureMutationStatus.Duplicate)
            {
                if (result.Snapshot?.LatestFailureSequence != sequence
                    || !string.Equals(result.Snapshot.LatestFailureHash, failure.ContentHash, StringComparison.Ordinal))
                {
                    return GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(result.Snapshot ?? current);
                }

                session.Snapshot = result.Snapshot;
                _lastSnapshot = result.Snapshot;
                return GovernedLoopLocalCoordinatorMutationOutcome.Success(result.Snapshot);
            }

            if (result.Snapshot is not null)
            {
                session.Snapshot = result.Snapshot;
                _lastSnapshot = result.Snapshot;
            }

            return result.Status switch
            {
                GovernedLoopCoordinatorFailureMutationStatus.OwnershipLost => GovernedLoopLocalCoordinatorMutationOutcome.OwnershipLost(result.Snapshot!),
                GovernedLoopCoordinatorFailureMutationStatus.Conflict => GovernedLoopLocalCoordinatorMutationOutcome.Conflict(result.Snapshot!),
                GovernedLoopCoordinatorFailureMutationStatus.Corrupt => GovernedLoopLocalCoordinatorMutationOutcome.Corrupt(current),
                _ => GovernedLoopLocalCoordinatorMutationOutcome.Unavailable(current)
            };
        }
        finally
        {
            _evidenceGate.Release();
        }
    }

    private async Task<GovernedLoopCoordinatorReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        GovernedLoopCoordinatorReadResult? result;
        try
        {
            result = await _evidence.ReadAsync(_options.CoordinatorId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Unavailable);
        }

        return GovernedLoopCoordinatorEvidenceContract.IsValid(result)
            ? result!
            : new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Corrupt);
    }

    private async Task<GovernedLoopCoordinatorAcquisitionResult> AcquireAsync(
        GovernedLoopCoordinatorAcquisitionRequest request,
        CancellationToken cancellationToken)
    {
        GovernedLoopCoordinatorAcquisitionResult? result;
        try
        {
            result = await _evidence.TryAcquireAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var reconciled = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (reconciled.Status == GovernedLoopCoordinatorReadStatus.Found
                && IsExactAcquisition(reconciled.Snapshot, request))
            {
                return new GovernedLoopCoordinatorAcquisitionResult(
                    GovernedLoopCoordinatorAcquisitionStatus.Duplicate,
                    reconciled.Snapshot);
            }

            throw;
        }
        catch (Exception)
        {
            var reconciled = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (reconciled.Status == GovernedLoopCoordinatorReadStatus.Found
                && IsExactAcquisition(reconciled.Snapshot, request))
            {
                return new GovernedLoopCoordinatorAcquisitionResult(
                    GovernedLoopCoordinatorAcquisitionStatus.Duplicate,
                    reconciled.Snapshot);
            }

            return new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Unavailable);
        }

        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(result))
        {
            return new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Corrupt);
        }

        if (result!.Status == GovernedLoopCoordinatorAcquisitionStatus.Unavailable)
        {
            var reconciled = await ReconcileAcquisitionAsync(request).ConfigureAwait(false);
            if (reconciled is not null)
            {
                return reconciled;
            }
        }

        return result;
    }

    private async Task<GovernedLoopCoordinatorAcquisitionResult?> ReconcileAcquisitionAsync(
        GovernedLoopCoordinatorAcquisitionRequest request)
    {
        var read = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        return read.Status == GovernedLoopCoordinatorReadStatus.Found && IsExactAcquisition(read.Snapshot, request)
            ? new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, read.Snapshot)
            : null;
    }

    private async Task<GovernedLoopCoordinatorHeartbeatMutationResult?> ReconcileHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request)
    {
        var read = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        return read.Status == GovernedLoopCoordinatorReadStatus.Found
            && read.Snapshot!.Ownership == request.ExpectedOwnership
            && read.Snapshot.LatestHeartbeat == request.ProposedHeartbeat
            ? new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Duplicate, read.Snapshot)
            : null;
    }

    private async Task<GovernedLoopCoordinatorLifecycleMutationResult?> ReconcileLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request)
    {
        var read = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        return read.Status == GovernedLoopCoordinatorReadStatus.Found
            && read.Snapshot!.Ownership == request.ExpectedOwnership
            && read.Snapshot.LatestLifecycle == request.ProposedLifecycle
            ? new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Duplicate, read.Snapshot)
            : null;
    }

    private async Task<GovernedLoopCoordinatorFailureMutationResult?> ReconcileFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request)
    {
        var read = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
        return read.Status == GovernedLoopCoordinatorReadStatus.Found
            && read.Snapshot!.Ownership == request.ExpectedOwnership
            && read.Snapshot.LatestFailureSequence == request.ProposedFailure.FailureSequence
            && string.Equals(read.Snapshot.LatestFailureHash, request.ProposedFailure.ContentHash, StringComparison.Ordinal)
            ? new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Duplicate, read.Snapshot)
            : null;
    }

    private bool TryCreateAcquisition(
        GovernedLoopCoordinatorReadResult read,
        DateTimeOffset acquiredAtUtc,
        out GovernedLoopCoordinatorAcquisitionRequest? request,
        out GovernedLoopLocalCoordinatorStartResult? blocked)
    {
        request = null;
        blocked = null;
        var current = read.Snapshot;
        if (current is not null && acquiredAtUtc < current.LatestHeartbeat.LeaseExpiresAtUtc)
        {
            blocked = new GovernedLoopLocalCoordinatorStartResult(
                GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer,
                current);
            return false;
        }

        if (current?.Ownership.OwnershipEpoch >= GovernedLoopSleepContractLimits.MaxVersion
            || !TryAdd(acquiredAtUtc, _options.OwnershipLeaseDuration, out var leaseExpiresAtUtc))
        {
            return false;
        }

        var ownership = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorOwnership(
            GovernedLoopCoordinatorOwnership.CurrentSchemaVersion,
            _options.CoordinatorId,
            _options.OwnerId,
            current is null ? 1 : current.Ownership.OwnershipEpoch + 1,
            acquiredAtUtc,
            string.Empty));
        var lifecycle = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
            GovernedLoopCoordinatorLifecycle.CurrentSchemaVersion,
            1,
            ownership,
            GovernedLoopCoordinatorStatus.Starting,
            acquiredAtUtc,
            null,
            string.Empty));
        var heartbeat = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
            GovernedLoopCoordinatorHeartbeat.CurrentSchemaVersion,
            1,
            ownership,
            acquiredAtUtc,
            leaseExpiresAtUtc,
            string.Empty));
        request = new GovernedLoopCoordinatorAcquisitionRequest(
            current is null
                ? GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound
                : GovernedLoopCoordinatorPriorEvidenceExpectation.Existing,
            current?.Ownership.ContentHash,
            current?.LatestHeartbeat.ContentHash,
            ownership,
            lifecycle,
            heartbeat);

        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request)
            || current is not null
                && !GovernedLoopSleepContractValidator.ValidateHandoff(current.Ownership, current.LatestHeartbeat, ownership).IsValid)
        {
            request = null;
            return false;
        }

        return true;
    }

    private GovernedLoopCoordinatorLifecycle CreateLifecycle(
        GovernedLoopCoordinatorSnapshot current,
        GovernedLoopCoordinatorStatus status,
        bool terminal)
    {
        if (!TryGetUtcNow(out var updatedAtUtc)
            || current.LatestLifecycle.LifecycleVersion >= GovernedLoopSleepContractLimits.MaxVersion)
        {
            throw new InvalidOperationException("A bounded lifecycle successor could not be created.");
        }

        if (updatedAtUtc < current.LatestLifecycle.UpdatedAtUtc)
        {
            updatedAtUtc = current.LatestLifecycle.UpdatedAtUtc;
        }

        return GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
            GovernedLoopCoordinatorLifecycle.CurrentSchemaVersion,
            current.LatestLifecycle.LifecycleVersion + 1,
            current.Ownership,
            status,
            updatedAtUtc,
            terminal ? updatedAtUtc : null,
            string.Empty));
    }

    private bool TryGetUtcNow(out DateTimeOffset value)
    {
        try
        {
            value = _timeProvider.GetUtcNow();
            return value != default && value.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            value = default;
            return false;
        }
    }

    private void ObserveHeartbeatDue()
    {
        try
        {
            _boundaryObserver?.OnHeartbeatDue();
        }
        catch (Exception)
        {
            // Observation cannot grant authority or change durable coordinator behavior.
        }
    }

    private static bool TryAdd(DateTimeOffset value, TimeSpan duration, out DateTimeOffset result)
    {
        try
        {
            result = value.Add(duration);
            return result.Offset == TimeSpan.Zero && result > value;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }

    private static GovernedLoopLocalCoordinatorOptions ValidateOptions(GovernedLoopLocalCoordinatorOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!GovernedLoopCoordinatorEvidenceContract.IsValidCoordinatorId(options.CoordinatorId)
            || !CustomLoopArtifactIdentifier.IsValid(options.OwnerId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters))
        {
            throw new ArgumentException("Coordinator and owner identities must be bounded canonical tokens.", nameof(options));
        }

        if (options.CycleInterval <= TimeSpan.Zero
            || options.CycleInterval > TimeSpan.FromDays(1)
            || options.HeartbeatInterval <= TimeSpan.Zero
            || options.HeartbeatInterval > TimeSpan.FromDays(1)
            || options.OwnershipLeaseDuration <= options.HeartbeatInterval
            || options.OwnershipLeaseDuration > TimeSpan.FromDays(1)
            || options.MaximumItemsPerFamilyPerCycle is < 1 or > GovernedLoopLocalCoordinatorOptions.MaximumPerFamilyCycleQuota)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Coordinator cadence, lease, and fairness bounds are invalid.");
        }

        return options with { };
    }

    private static bool IsValid(GovernedLoopLocalWorkResult? result)
        => result is not null
            && Enum.IsDefined(result.Status)
            && CustomLoopArtifactIdentifier.IsValid(result.ReasonCode, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters);

    private static bool IsExactAcquisition(
        GovernedLoopCoordinatorSnapshot? snapshot,
        GovernedLoopCoordinatorAcquisitionRequest request)
        => snapshot is not null
            && snapshot.Ownership == request.ProposedOwnership
            && snapshot.LatestLifecycle == request.StartingLifecycle
            && snapshot.LatestHeartbeat == request.InitialHeartbeat
            && snapshot.LatestFailureSequence == 0
            && snapshot.LatestFailureHash is null;

    private static bool IsExactLifecycle(
        GovernedLoopCoordinatorSnapshot? snapshot,
        GovernedLoopCoordinatorLifecycle lifecycle)
        => snapshot?.LatestLifecycle == lifecycle
            && snapshot.Ownership == lifecycle.Ownership;

    private static bool IsExactHeartbeat(
        GovernedLoopCoordinatorSnapshot? snapshot,
        GovernedLoopCoordinatorHeartbeat heartbeat)
        => snapshot?.LatestHeartbeat == heartbeat
            && snapshot.Ownership == heartbeat.Ownership;

    private static GovernedLoopLocalCoordinatorStartStatus Map(GovernedLoopCoordinatorAcquisitionStatus status)
        => status switch
        {
            GovernedLoopCoordinatorAcquisitionStatus.OwnedByLivePeer
                or GovernedLoopCoordinatorAcquisitionStatus.LeaseNotExpired
                => GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer,
            GovernedLoopCoordinatorAcquisitionStatus.Conflict => GovernedLoopLocalCoordinatorStartStatus.Conflict,
            GovernedLoopCoordinatorAcquisitionStatus.Corrupt => GovernedLoopLocalCoordinatorStartStatus.Corrupt,
            _ => GovernedLoopLocalCoordinatorStartStatus.Unavailable
        };

    private static GovernedLoopLocalCoordinatorStartStatus MapStart(GovernedLoopLocalCoordinatorMutationStatus status)
        => status switch
        {
            GovernedLoopLocalCoordinatorMutationStatus.OwnershipLost => GovernedLoopLocalCoordinatorStartStatus.OwnedByLivePeer,
            GovernedLoopLocalCoordinatorMutationStatus.Conflict => GovernedLoopLocalCoordinatorStartStatus.Conflict,
            GovernedLoopLocalCoordinatorMutationStatus.Corrupt => GovernedLoopLocalCoordinatorStartStatus.Corrupt,
            _ => GovernedLoopLocalCoordinatorStartStatus.Unavailable
        };

    private static GovernedLoopLocalCoordinatorSessionOutcome TerminalFromMutation(
        GovernedLoopLocalCoordinatorMutationStatus status,
        GovernedLoopCoordinatorSnapshot snapshot)
        => new(
            status is GovernedLoopLocalCoordinatorMutationStatus.OwnershipLost or GovernedLoopLocalCoordinatorMutationStatus.Conflict
                ? GovernedLoopLocalCoordinatorStopStatus.OwnershipLost
                : status == GovernedLoopLocalCoordinatorMutationStatus.Unavailable
                    ? GovernedLoopLocalCoordinatorStopStatus.Unavailable
                    : GovernedLoopLocalCoordinatorStopStatus.Failed,
            snapshot);

    private static string Family(GovernedLoopLocalWorkFamily family)
        => family switch
        {
            GovernedLoopLocalWorkFamily.Schedule => "schedule",
            GovernedLoopLocalWorkFamily.Trigger => "trigger",
            GovernedLoopLocalWorkFamily.Wake => "wake",
            _ => "work"
        };

}
