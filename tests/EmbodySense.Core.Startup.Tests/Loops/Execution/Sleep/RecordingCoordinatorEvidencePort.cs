using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class RecordingCoordinatorEvidencePort : IGovernedLoopCoordinatorEvidencePort
{
    private readonly Lock _gate = new();
    private CoordinatorPostCommitFailureMode _acquisitionPostCommitFailure;
    private CoordinatorPostCommitFailureMode _failurePostCommitFailure;
    private CoordinatorPostCommitFailureMode _heartbeatPostCommitFailure;
    private int _heartbeatAttempts;
    private CoordinatorPostCommitFailureMode _lifecyclePostCommitFailure;
    private GovernedLoopCoordinatorFailure? _latestFailure;
    private GovernedLoopCoordinatorSnapshot? _snapshot;

    internal List<GovernedLoopCoordinatorFailure> Failures { get; } = [];

    internal List<GovernedLoopCoordinatorHeartbeat> Heartbeats { get; } = [];

    internal int HeartbeatAttempts => Volatile.Read(ref _heartbeatAttempts);

    internal List<GovernedLoopCoordinatorLifecycle> Lifecycles { get; } = [];

    internal GovernedLoopCoordinatorAcquisitionStatus? AcquisitionOverride { get; set; }

    internal CoordinatorPostCommitFailureMode AcquisitionPostCommitFailure
    {
        get => _acquisitionPostCommitFailure;
        set => _acquisitionPostCommitFailure = value;
    }

    internal bool AdvanceAcquisitionBeforePostCommitFailure { get; set; }

    internal GovernedLoopCoordinatorFailureMutationStatus? FailureOverride { get; set; }

    internal CoordinatorPostCommitFailureMode FailurePostCommitFailure
    {
        get => _failurePostCommitFailure;
        set => _failurePostCommitFailure = value;
    }

    internal bool AdvanceFailureBeforePostCommitFailure { get; set; }

    internal GovernedLoopCoordinatorHeartbeatMutationStatus? HeartbeatOverride { get; set; }

    internal CoordinatorPostCommitFailureMode HeartbeatPostCommitFailure
    {
        get => _heartbeatPostCommitFailure;
        set => _heartbeatPostCommitFailure = value;
    }

    internal bool AdvanceHeartbeatBeforePostCommitFailure { get; set; }

    internal GovernedLoopCoordinatorLifecycleMutationStatus? LifecycleOverride { get; set; }

    internal CoordinatorPostCommitFailureMode LifecyclePostCommitFailure
    {
        get => _lifecyclePostCommitFailure;
        set => _lifecyclePostCommitFailure = value;
    }

    internal bool AdvanceLifecycleBeforePostCommitFailure { get; set; }

    internal bool ReturnMalformedRead { get; set; }

    internal bool ThrowOnHeartbeat { get; set; }

    internal GovernedLoopCoordinatorSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot is null ? null : new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Found, _snapshot).Snapshot;
            }
        }
    }

    public Task<GovernedLoopCoordinatorReadResult?> ReadAsync(
        string coordinatorId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (ReturnMalformedRead)
            {
                return Task.FromResult<GovernedLoopCoordinatorReadResult?>(null);
            }

            return Task.FromResult<GovernedLoopCoordinatorReadResult?>(_snapshot is null
                || !string.Equals(_snapshot.Ownership.CoordinatorId, coordinatorId, StringComparison.Ordinal)
                    ? new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.NotFound)
                    : new GovernedLoopCoordinatorReadResult(GovernedLoopCoordinatorReadStatus.Found, _snapshot));
        }
    }

    public Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(
        GovernedLoopCoordinatorAcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
            {
                return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                    new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Corrupt));
            }

            if (AcquisitionOverride is { } acquisitionOverride)
            {
                return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                    new GovernedLoopCoordinatorAcquisitionResult(
                        acquisitionOverride,
                        acquisitionOverride is GovernedLoopCoordinatorAcquisitionStatus.Corrupt
                            or GovernedLoopCoordinatorAcquisitionStatus.Unavailable
                                ? null
                                : _snapshot));
            }

            if (_snapshot is null)
            {
                if (request.PriorEvidenceExpectation != GovernedLoopCoordinatorPriorEvidenceExpectation.NotFound)
                {
                    return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                        new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Corrupt));
                }

                return CompleteAcquisition(Acquire(request));
            }

            if (IsExact(request))
            {
                return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                    new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Duplicate, _snapshot));
            }

            if (request.PriorEvidenceExpectation is not (GovernedLoopCoordinatorPriorEvidenceExpectation.Existing
                or GovernedLoopCoordinatorPriorEvidenceExpectation.TerminalSameOwner)
                || !string.Equals(request.ExpectedOwnershipHash, _snapshot.Ownership.ContentHash, StringComparison.Ordinal)
                || !string.Equals(request.ExpectedHeartbeatHash, _snapshot.LatestHeartbeat.ContentHash, StringComparison.Ordinal))
            {
                return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                    new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Conflict, _snapshot));
            }

            var terminalSameOwnerRestart = request.PriorEvidenceExpectation == GovernedLoopCoordinatorPriorEvidenceExpectation.TerminalSameOwner;
            if (!terminalSameOwnerRestart
                && request.ProposedOwnership.AcquiredAtUtc < _snapshot.LatestHeartbeat.LeaseExpiresAtUtc)
            {
                return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                    new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.LeaseNotExpired, _snapshot));
            }

            var transitionIsValid = terminalSameOwnerRestart
                ? GovernedLoopSleepContractValidator.ValidateTerminalSameOwnerRestart(
                    _snapshot.Ownership,
                    _snapshot.LatestLifecycle,
                    _snapshot.LatestHeartbeat,
                    request.ProposedOwnership).IsValid
                : GovernedLoopSleepContractValidator.ValidateHandoff(
                    _snapshot.Ownership,
                    _snapshot.LatestHeartbeat,
                    request.ProposedOwnership).IsValid;
            if (!transitionIsValid)
            {
                return Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                    new GovernedLoopCoordinatorAcquisitionResult(terminalSameOwnerRestart
                        ? GovernedLoopCoordinatorAcquisitionStatus.Conflict
                        : GovernedLoopCoordinatorAcquisitionStatus.Corrupt));
            }

            return CompleteAcquisition(Acquire(request));
        }
    }

    public Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _heartbeatAttempts);
        if (ThrowOnHeartbeat)
        {
            throw new IOException("hostile heartbeat failure");
        }

        lock (_gate)
        {
            if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request) || _snapshot is null)
            {
                return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                    new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt));
            }

            if (HeartbeatOverride is { } heartbeatOverride)
            {
                return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                    new GovernedLoopCoordinatorHeartbeatMutationResult(
                        heartbeatOverride,
                        heartbeatOverride is GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt
                            or GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable
                                ? null
                                : _snapshot));
            }

            if (!SameOwnership(request.ExpectedOwnership, _snapshot.Ownership))
            {
                return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                    new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.OwnershipLost, _snapshot));
            }

            if (request.ExpectedHeartbeatSequence != _snapshot.LatestHeartbeat.HeartbeatSequence
                || !string.Equals(request.ExpectedHeartbeatHash, _snapshot.LatestHeartbeat.ContentHash, StringComparison.Ordinal))
            {
                return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                    new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Conflict, _snapshot));
            }

            if (!GovernedLoopSleepContractValidator.ValidateTransition(_snapshot.LatestHeartbeat, request.ProposedHeartbeat).IsValid)
            {
                return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                    new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Corrupt));
            }

            _snapshot = new GovernedLoopCoordinatorSnapshot(
                _snapshot.Ownership,
                _snapshot.LatestLifecycle,
                request.ProposedHeartbeat,
                _snapshot.LatestFailureSequence,
                _snapshot.LatestFailureHash);
            Heartbeats.Add(request.ProposedHeartbeat);
            var postCommitFailure = Consume(ref _heartbeatPostCommitFailure);
            if (postCommitFailure != CoordinatorPostCommitFailureMode.None && AdvanceHeartbeatBeforePostCommitFailure)
            {
                var advanced = GovernedLoopSleepContractHash.Apply(request.ProposedHeartbeat with
                {
                    HeartbeatSequence = request.ProposedHeartbeat.HeartbeatSequence + 1,
                    RecordedAtUtc = request.ProposedHeartbeat.RecordedAtUtc.AddTicks(1),
                    LeaseExpiresAtUtc = request.ProposedHeartbeat.LeaseExpiresAtUtc.AddTicks(1),
                    ContentHash = string.Empty
                });
                _snapshot = new GovernedLoopCoordinatorSnapshot(
                    _snapshot.Ownership,
                    _snapshot.LatestLifecycle,
                    advanced,
                    _snapshot.LatestFailureSequence,
                    _snapshot.LatestFailureHash);
                Heartbeats.Add(advanced);
            }

            if (postCommitFailure == CoordinatorPostCommitFailureMode.Throw)
            {
                throw new IOException("simulated post-commit heartbeat process loss");
            }

            if (postCommitFailure == CoordinatorPostCommitFailureMode.ReturnUnavailable)
            {
                return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                    new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Unavailable));
            }

            return Task.FromResult<GovernedLoopCoordinatorHeartbeatMutationResult?>(
                new GovernedLoopCoordinatorHeartbeatMutationResult(GovernedLoopCoordinatorHeartbeatMutationStatus.Renewed, _snapshot));
        }
    }

    public Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request) || _snapshot is null)
            {
                return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                    new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt));
            }

            if (LifecycleOverride is { } lifecycleOverride)
            {
                return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                    new GovernedLoopCoordinatorLifecycleMutationResult(
                        lifecycleOverride,
                        lifecycleOverride is GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt
                            or GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable
                                ? null
                                : _snapshot));
            }

            if (!SameOwnership(request.ExpectedOwnership, _snapshot.Ownership))
            {
                return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                    new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.OwnershipLost, _snapshot));
            }

            if (request.ExpectedLifecycleVersion != _snapshot.LatestLifecycle.LifecycleVersion
                || !string.Equals(request.ExpectedLifecycleHash, _snapshot.LatestLifecycle.ContentHash, StringComparison.Ordinal))
            {
                return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                    new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Conflict, _snapshot));
            }

            if (!GovernedLoopSleepContractValidator.ValidateTransition(_snapshot.LatestLifecycle, request.ProposedLifecycle).IsValid)
            {
                return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                    new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Corrupt));
            }

            _snapshot = new GovernedLoopCoordinatorSnapshot(
                _snapshot.Ownership,
                request.ProposedLifecycle,
                _snapshot.LatestHeartbeat,
                _snapshot.LatestFailureSequence,
                _snapshot.LatestFailureHash);
            Lifecycles.Add(request.ProposedLifecycle);
            var postCommitFailure = Consume(ref _lifecyclePostCommitFailure);
            if (postCommitFailure != CoordinatorPostCommitFailureMode.None && AdvanceLifecycleBeforePostCommitFailure)
            {
                var advanced = GovernedLoopSleepContractHash.Apply(request.ProposedLifecycle with
                {
                    LifecycleVersion = request.ProposedLifecycle.LifecycleVersion + 1,
                    Status = GovernedLoopCoordinatorStatus.Stopping,
                    UpdatedAtUtc = request.ProposedLifecycle.UpdatedAtUtc.AddTicks(1),
                    ContentHash = string.Empty
                });
                _snapshot = new GovernedLoopCoordinatorSnapshot(
                    _snapshot.Ownership,
                    advanced,
                    _snapshot.LatestHeartbeat,
                    _snapshot.LatestFailureSequence,
                    _snapshot.LatestFailureHash);
                Lifecycles.Add(advanced);
            }

            if (postCommitFailure == CoordinatorPostCommitFailureMode.Throw)
            {
                throw new IOException("simulated post-commit lifecycle process loss");
            }

            if (postCommitFailure == CoordinatorPostCommitFailureMode.ReturnUnavailable)
            {
                return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                    new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Unavailable));
            }

            return Task.FromResult<GovernedLoopCoordinatorLifecycleMutationResult?>(
                new GovernedLoopCoordinatorLifecycleMutationResult(GovernedLoopCoordinatorLifecycleMutationStatus.Appended, _snapshot));
        }
    }

    public Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request) || _snapshot is null)
            {
                return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                    new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Corrupt));
            }

            if (FailureOverride is { } failureOverride)
            {
                return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                    new GovernedLoopCoordinatorFailureMutationResult(
                        failureOverride,
                        failureOverride is GovernedLoopCoordinatorFailureMutationStatus.Corrupt
                            or GovernedLoopCoordinatorFailureMutationStatus.Unavailable
                                ? null
                                : _snapshot));
            }

            if (!SameOwnership(request.ExpectedOwnership, _snapshot.Ownership))
            {
                return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                    new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.OwnershipLost, _snapshot));
            }

            var expected = request.PriorFailureExpectation == GovernedLoopCoordinatorPriorFailureExpectation.None
                ? _snapshot.LatestFailureSequence == 0 && _snapshot.LatestFailureHash is null
                : request.ExpectedFailureSequence == _snapshot.LatestFailureSequence
                    && string.Equals(request.ExpectedFailureHash, _snapshot.LatestFailureHash, StringComparison.Ordinal);
            if (!expected)
            {
                return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                    new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Conflict, _snapshot));
            }

            if (_latestFailure is not null
                && !GovernedLoopSleepContractValidator.ValidateTransition(_latestFailure, request.ProposedFailure).IsValid)
            {
                return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                    new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Corrupt));
            }

            _latestFailure = request.ProposedFailure;
            _snapshot = new GovernedLoopCoordinatorSnapshot(
                _snapshot.Ownership,
                _snapshot.LatestLifecycle,
                _snapshot.LatestHeartbeat,
                request.ProposedFailure.FailureSequence,
                request.ProposedFailure.ContentHash);
            Failures.Add(request.ProposedFailure);
            var postCommitFailure = Consume(ref _failurePostCommitFailure);
            if (postCommitFailure != CoordinatorPostCommitFailureMode.None && AdvanceFailureBeforePostCommitFailure)
            {
                var advanced = GovernedLoopSleepContractHash.Apply(request.ProposedFailure with
                {
                    FailureSequence = request.ProposedFailure.FailureSequence + 1,
                    OccurredAtUtc = request.ProposedFailure.OccurredAtUtc.AddTicks(1),
                    ContentHash = string.Empty
                });
                _latestFailure = advanced;
                _snapshot = new GovernedLoopCoordinatorSnapshot(
                    _snapshot.Ownership,
                    _snapshot.LatestLifecycle,
                    _snapshot.LatestHeartbeat,
                    advanced.FailureSequence,
                    advanced.ContentHash);
                Failures.Add(advanced);
            }

            if (postCommitFailure == CoordinatorPostCommitFailureMode.Throw)
            {
                throw new IOException("simulated post-commit failure-evidence process loss");
            }

            if (postCommitFailure == CoordinatorPostCommitFailureMode.ReturnUnavailable)
            {
                return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                    new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Unavailable));
            }

            return Task.FromResult<GovernedLoopCoordinatorFailureMutationResult?>(
                new GovernedLoopCoordinatorFailureMutationResult(GovernedLoopCoordinatorFailureMutationStatus.Appended, _snapshot));
        }
    }

    private GovernedLoopCoordinatorAcquisitionResult Acquire(GovernedLoopCoordinatorAcquisitionRequest request)
    {
        _latestFailure = null;
        _snapshot = new GovernedLoopCoordinatorSnapshot(
            request.ProposedOwnership,
            request.StartingLifecycle,
            request.InitialHeartbeat,
            0,
            null);
        Lifecycles.Add(request.StartingLifecycle);
        Heartbeats.Add(request.InitialHeartbeat);
        return new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Acquired, _snapshot);
    }

    private Task<GovernedLoopCoordinatorAcquisitionResult?> CompleteAcquisition(
        GovernedLoopCoordinatorAcquisitionResult acquired)
    {
        var postCommitFailure = Consume(ref _acquisitionPostCommitFailure);
        if (postCommitFailure != CoordinatorPostCommitFailureMode.None && AdvanceAcquisitionBeforePostCommitFailure)
        {
            var current = _snapshot!;
            var advanced = GovernedLoopSleepContractHash.Apply(current.LatestLifecycle with
            {
                LifecycleVersion = current.LatestLifecycle.LifecycleVersion + 1,
                Status = GovernedLoopCoordinatorStatus.Running,
                UpdatedAtUtc = current.LatestLifecycle.UpdatedAtUtc.AddTicks(1),
                ContentHash = string.Empty
            });
            _snapshot = new GovernedLoopCoordinatorSnapshot(
                current.Ownership,
                advanced,
                current.LatestHeartbeat,
                current.LatestFailureSequence,
                current.LatestFailureHash);
            Lifecycles.Add(advanced);
        }

        return postCommitFailure switch
        {
            CoordinatorPostCommitFailureMode.ReturnUnavailable => Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(
                new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Unavailable)),
            CoordinatorPostCommitFailureMode.Throw => throw new IOException("simulated post-commit acquisition process loss"),
            _ => Task.FromResult<GovernedLoopCoordinatorAcquisitionResult?>(acquired)
        };
    }

    private static CoordinatorPostCommitFailureMode Consume(ref CoordinatorPostCommitFailureMode mode)
    {
        var selected = mode;
        mode = CoordinatorPostCommitFailureMode.None;
        return selected;
    }

    internal void SetOwnershipEpoch(long epoch)
    {
        lock (_gate)
        {
            var current = _snapshot ?? throw new InvalidOperationException("No coordinator evidence exists.");
            var ownership = GovernedLoopSleepContractHash.Apply(current.Ownership with
            {
                OwnershipEpoch = epoch,
                ContentHash = string.Empty
            });
            var lifecycle = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorLifecycle(
                current.LatestLifecycle.SchemaVersion,
                current.LatestLifecycle.LifecycleVersion,
                ownership,
                current.LatestLifecycle.Status,
                current.LatestLifecycle.UpdatedAtUtc,
                current.LatestLifecycle.TerminalAtUtc,
                string.Empty));
            var heartbeat = GovernedLoopSleepContractHash.Apply(new GovernedLoopCoordinatorHeartbeat(
                current.LatestHeartbeat.SchemaVersion,
                current.LatestHeartbeat.HeartbeatSequence,
                ownership,
                current.LatestHeartbeat.RecordedAtUtc,
                current.LatestHeartbeat.LeaseExpiresAtUtc,
                string.Empty));
            _snapshot = new GovernedLoopCoordinatorSnapshot(ownership, lifecycle, heartbeat, 0, null);
            _latestFailure = null;
        }
    }

    private bool IsExact(GovernedLoopCoordinatorAcquisitionRequest request)
        => _snapshot!.Ownership == request.ProposedOwnership
            && _snapshot.LatestLifecycle == request.StartingLifecycle
            && _snapshot.LatestHeartbeat == request.InitialHeartbeat;

    private static bool SameOwnership(
        GovernedLoopCoordinatorOwnership first,
        GovernedLoopCoordinatorOwnership second)
        => first == second && string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal);
}
