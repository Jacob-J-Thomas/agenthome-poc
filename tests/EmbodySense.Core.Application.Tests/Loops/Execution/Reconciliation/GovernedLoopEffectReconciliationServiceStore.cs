using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationServiceStore :
    IGovernedLoopEffectReconciliationCaseStore,
    IGovernedLoopEffectReconciliationProbeReservationStore
{
    private readonly Dictionary<string, (string RequestHash, GovernedLoopEffectReconciliationCaseMutationResult Result)> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GovernedLoopEffectReconciliationCase> _versions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string RequestHash, GovernedLoopEffectReconciliationProbeReservation Reservation)> _probeReservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GovernedLoopEffectReconciliationProbeObservationCommitResult> _probeCommits = new(StringComparer.Ordinal);
    private readonly object _probeGate = new();

    internal GovernedLoopEffectReconciliationCase? CurrentCase { get; private set; }

    internal GovernedLoopEffectAttempt? CurrentEffect { get; private set; }

    internal GovernedLoopEffectReconciliationCaseReadStatus? ForcedReadStatus { get; set; }

    internal GovernedLoopEffectReconciliationCaseMutationStatus? ForcedMutationStatus { get; set; }

    internal bool ThrowOnRead { get; set; }

    internal bool ThrowOnMutation { get; set; }

    internal bool ReturnNullOnRead { get; set; }

    internal bool ReturnNullOnMutation { get; set; }

    internal int MutationCalls { get; private set; }

    internal int AppliedMutationCalls { get; private set; }

    internal int ProbeReservationCalls { get; private set; }

    internal int ProbeCommitCalls { get; private set; }

    internal GovernedLoopEffectReconciliationProbeReservationStatus? ForcedProbeReservationStatus { get; set; }

    internal GovernedLoopEffectReconciliationProbeReservationStatus? ForcedProbeCommitStatus { get; set; }

    internal bool ThrowOnProbeReservation { get; set; }

    internal bool ReturnNullOnProbeReservation { get; set; }

    internal bool ThrowOnProbeCommit { get; set; }

    internal bool ThrowAfterProbeCommit { get; set; }

    internal bool ReturnNullOnProbeCommit { get; set; }

    internal Action? BeforeCallbackValidationAction { get; set; }

    internal bool ThrowOnCallbackValidation { get; set; }

    internal GovernedLoopEffectReconciliationProbeReservationStatus? ForcedCallbackValidationStatus { get; set; }

    internal void SeedEffect(GovernedLoopEffectAttempt effect) => CurrentEffect = effect;

    internal void SeedCase(GovernedLoopEffectReconciliationCase value)
    {
        CurrentCase = value;
        _versions[VersionKey(value)] = value;
    }

    public Task<GovernedLoopEffectReconciliationCaseListPage> ListAsync(GovernedLoopEffectReconciliationCaseListRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Ready, [], null));

    public Task<GovernedLoopEffectReconciliationCaseReadResult> ReadAsync(GovernedLoopEffectReconciliationCaseReadRequest request, CancellationToken cancellationToken = default)
    {
        if (ReturnNullOnRead)
        {
            return Task.FromResult<GovernedLoopEffectReconciliationCaseReadResult>(null!);
        }

        if (ThrowOnRead)
        {
            throw new IOException("The test store is unavailable.");
        }

        if (ForcedReadStatus is { } forcedStatus && forcedStatus != GovernedLoopEffectReconciliationCaseReadStatus.Found)
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationCaseReadResult(forcedStatus, null));
        }

        var current = _versions.Values.FirstOrDefault(value => Matches(value, request.Reference));
        if (current is null || !Matches(current, request.Reference))
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.NotFound, null));
        }

        return Task.FromResult(new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Found, current));
    }

    public Task<GovernedLoopEffectReconciliationCaseMutationResult> CompareExchangeAsync(GovernedLoopEffectReconciliationCaseMutationRequest request, CancellationToken cancellationToken = default)
    {
        MutationCalls++;
        if (ReturnNullOnMutation)
        {
            return Task.FromResult<GovernedLoopEffectReconciliationCaseMutationResult>(null!);
        }

        if (ThrowOnMutation)
        {
            throw new IOException("The test store is unavailable.");
        }

        if (ForcedMutationStatus is { } forcedStatus && forcedStatus is not GovernedLoopEffectReconciliationCaseMutationStatus.Applied)
        {
            if (forcedStatus is GovernedLoopEffectReconciliationCaseMutationStatus.Replayed or GovernedLoopEffectReconciliationCaseMutationStatus.Conflict)
            {
                return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(forcedStatus, CurrentCase, CurrentEffect));
            }

            return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(forcedStatus, null, null));
        }

        if (_operations.TryGetValue(request.OperationId, out var previous))
        {
            if (string.Equals(previous.RequestHash, request.RequestHash, StringComparison.Ordinal))
            {
                return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Replayed, CurrentCase, CurrentEffect));
            }

            return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, CurrentCase, CurrentEffect));
        }

        if (request.ExpectedCaseVersion is null)
        {
            if (CurrentCase is not null)
            {
                return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, CurrentCase, CurrentEffect));
            }
        }
        else if (CurrentCase is null || CurrentCase.CaseVersion != request.ExpectedCaseVersion || !string.Equals(CurrentCase.ContentHash, request.ExpectedCaseContentHash, StringComparison.Ordinal))
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Conflict, CurrentCase, CurrentEffect));
        }

        CurrentCase = request.Replacement;
        AppliedMutationCalls++;
        _versions[VersionKey(CurrentCase)] = CurrentCase;
        if (request.ReconciledEffectSuccessor is not null)
        {
            CurrentEffect = request.ReconciledEffectSuccessor;
        }

        var result = new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, CurrentCase, CurrentEffect!);
        _operations[request.OperationId] = (request.RequestHash, result);
        return Task.FromResult(result);
    }

    public Task<GovernedLoopEffectReconciliationProbeReservationResult> ReserveAsync(GovernedLoopEffectReconciliationProbeReservationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_probeGate)
        {
            ProbeReservationCalls++;
            if (ThrowOnProbeReservation)
            {
                throw new IOException("The test reservation store is unavailable.");
            }

            if (ReturnNullOnProbeReservation)
            {
                return Task.FromResult<GovernedLoopEffectReconciliationProbeReservationResult>(null!);
            }

            if (ForcedProbeReservationStatus is { } forcedStatus)
            {
                return Task.FromResult(new GovernedLoopEffectReconciliationProbeReservationResult(forcedStatus, null));
            }

            if (_probeReservations.TryGetValue(request.OperationId, out var existing))
            {
                return Task.FromResult(string.Equals(existing.RequestHash, request.RequestHash, StringComparison.Ordinal)
                    ? new GovernedLoopEffectReconciliationProbeReservationResult(
                        GovernedLoopEffectReconciliationProbeReservationStatus.Replayed,
                        existing.Reservation,
                        _probeCommits.TryGetValue(request.OperationId, out var commit) ? commit.Case : null,
                        _probeCommits.TryGetValue(request.OperationId, out commit) ? commit.EffectHead : null)
                    : new GovernedLoopEffectReconciliationProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Conflict, null));
            }

            var reservation = new GovernedLoopEffectReconciliationProbeReservation(
                request.OperationId,
                request.RequestHash,
                $"probe-{Guid.NewGuid():N}",
                request.Context,
                DateTimeOffset.UtcNow);
            _probeReservations[request.OperationId] = (request.RequestHash, reservation);
            return Task.FromResult(new GovernedLoopEffectReconciliationProbeReservationResult(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, reservation));
        }
    }

    public Task<GovernedLoopEffectReconciliationProbeReservationStatus> ValidateBeforeCallbackAsync(GovernedLoopEffectReconciliationProbeReservation reservation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BeforeCallbackValidationAction?.Invoke();
        if (ThrowOnCallbackValidation)
        {
            throw new IOException("The test callback validation is unavailable.");
        }

        if (ForcedCallbackValidationStatus is { } forcedStatus)
        {
            return Task.FromResult(forcedStatus);
        }

        lock (_probeGate)
        {
            return Task.FromResult(CurrentCase is not null
                && CurrentCase.CaseVersion == reservation.Context.Case.CaseVersion
                && string.Equals(CurrentCase.ContentHash, reservation.Context.Case.ContentHash, StringComparison.Ordinal)
                && CurrentCase.Disposition is null
                && CurrentCase.Resolution is null
                && CurrentEffect is not null
                && string.Equals(CurrentEffect.ContentHash, reservation.Context.EffectHead.ContentHash, StringComparison.Ordinal)
                ? GovernedLoopEffectReconciliationProbeReservationStatus.Reserved
                : GovernedLoopEffectReconciliationProbeReservationStatus.Conflict);
        }
    }

    public Task<GovernedLoopEffectReconciliationProbeObservationCommitResult> CommitObservationAsync(GovernedLoopEffectReconciliationProbeObservationCommitRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_probeGate)
        {
            ProbeCommitCalls++;
            if (ThrowOnProbeCommit)
            {
                throw new IOException("The test observation store is unavailable.");
            }

            if (ReturnNullOnProbeCommit)
            {
                return Task.FromResult<GovernedLoopEffectReconciliationProbeObservationCommitResult>(null!);
            }

            if (ForcedProbeCommitStatus is { } forcedStatus)
            {
                return Task.FromResult(new GovernedLoopEffectReconciliationProbeObservationCommitResult(forcedStatus, forcedStatus is GovernedLoopEffectReconciliationProbeReservationStatus.Reserved or GovernedLoopEffectReconciliationProbeReservationStatus.Replayed ? CurrentCase : null, forcedStatus is GovernedLoopEffectReconciliationProbeReservationStatus.Reserved or GovernedLoopEffectReconciliationProbeReservationStatus.Replayed ? CurrentEffect : null));
            }

            if (_probeCommits.TryGetValue(request.Reservation.OperationId, out var committed))
            {
                return Task.FromResult(new GovernedLoopEffectReconciliationProbeObservationCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Replayed, committed.Case, committed.EffectHead));
            }

            var observation = request.Result.Observation!;
            var current = CurrentCase!;
            var next = GovernedLoopEffectReconciliationContract.Create(
                current.CaseId,
                current.CaseVersion + 1,
                current.Binding,
                current.ContractMetadata,
                current.EvidenceSources,
                [.. current.ObservationHistory, observation],
                current.AssessmentHistory,
                current.CurrentAssessmentHash,
                current.Disposition,
                current.Resolution,
                current.CaseReceiptHashes,
                current.ContentHash,
                current.OpenedAtUtc,
                observation.RecordedAtUtc < current.UpdatedAtUtc ? current.UpdatedAtUtc : observation.RecordedAtUtc);
            CurrentCase = next;
            _versions[VersionKey(next)] = next;
            var result = new GovernedLoopEffectReconciliationProbeObservationCommitResult(GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, next, CurrentEffect);
            _probeCommits[request.Reservation.OperationId] = result;
            if (ThrowAfterProbeCommit)
            {
                throw new IOException("The test observation response was lost after durable commit.");
            }
            return Task.FromResult(result);
        }
    }

    private static bool Matches(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectReconciliationCaseReference reference)
        => string.Equals(value.CaseId, reference.CaseId, StringComparison.Ordinal)
            && value.CaseVersion == reference.CaseVersion
            && string.Equals(value.ContentHash, reference.ContentHash, StringComparison.Ordinal)
            && string.Equals(value.Binding.ContentHash, reference.BindingHash, StringComparison.Ordinal);

    private static string VersionKey(GovernedLoopEffectReconciliationCase value)
        => $"{value.CaseId}:{value.CaseVersion}:{value.ContentHash}";
}
