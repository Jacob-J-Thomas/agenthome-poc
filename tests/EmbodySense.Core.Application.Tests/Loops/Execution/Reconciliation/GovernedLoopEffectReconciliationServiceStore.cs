using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationServiceStore : IGovernedLoopEffectReconciliationCaseStore
{
    private readonly Dictionary<string, (string RequestHash, GovernedLoopEffectReconciliationCaseMutationResult Result)> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GovernedLoopEffectReconciliationCase> _versions = new(StringComparer.Ordinal);

    internal GovernedLoopEffectReconciliationCase? CurrentCase { get; private set; }

    internal GovernedLoopEffectAttempt? CurrentEffect { get; private set; }

    internal GovernedLoopEffectReconciliationCaseReadStatus? ForcedReadStatus { get; set; }

    internal GovernedLoopEffectReconciliationCaseMutationStatus? ForcedMutationStatus { get; set; }

    internal bool ThrowOnRead { get; set; }

    internal bool ThrowOnMutation { get; set; }

    internal bool ReturnNullOnRead { get; set; }

    internal bool ReturnNullOnMutation { get; set; }

    internal int MutationCalls { get; private set; }

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
        _versions[VersionKey(CurrentCase)] = CurrentCase;
        if (request.ReconciledEffectSuccessor is not null)
        {
            CurrentEffect = request.ReconciledEffectSuccessor;
        }

        var result = new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Applied, CurrentCase, CurrentEffect!);
        _operations[request.OperationId] = (request.RequestHash, result);
        return Task.FromResult(result);
    }

    private static bool Matches(GovernedLoopEffectReconciliationCase value, GovernedLoopEffectReconciliationCaseReference reference)
        => string.Equals(value.CaseId, reference.CaseId, StringComparison.Ordinal)
            && value.CaseVersion == reference.CaseVersion
            && string.Equals(value.ContentHash, reference.ContentHash, StringComparison.Ordinal)
            && string.Equals(value.Binding.ContentHash, reference.BindingHash, StringComparison.Ordinal);

    private static string VersionKey(GovernedLoopEffectReconciliationCase value)
        => $"{value.CaseId}:{value.CaseVersion}:{value.ContentHash}";
}
