using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal sealed class RecordingGovernedLoopEffectReconciliationPorts :
    IGovernedLoopEffectReconciliationCaseStore,
    IGovernedLoopEffectReconciliationAuthorizationSource,
    IGovernedLoopEffectReconciliationProbeRegistry,
    IGovernedLoopEffectReconciliationProbe,
    IGovernedLoopEffectReconciliationInputSource,
    IGovernedLoopEffectReconciliationResolutionReader
{
    internal List<CancellationToken> CancellationTokens { get; } = [];

    public Task<GovernedLoopEffectReconciliationCaseListPage> ListAsync(GovernedLoopEffectReconciliationCaseListRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationCaseListPage(GovernedLoopEffectReconciliationCaseListStatus.Unavailable, [], null));
    }

    public Task<GovernedLoopEffectReconciliationCaseReadResult> ReadAsync(GovernedLoopEffectReconciliationCaseReadRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationCaseReadResult(GovernedLoopEffectReconciliationCaseReadStatus.Unavailable, null));
    }

    public Task<GovernedLoopEffectReconciliationCaseMutationResult> CompareExchangeAsync(GovernedLoopEffectReconciliationCaseMutationRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationCaseMutationResult(GovernedLoopEffectReconciliationCaseMutationStatus.Unavailable, null, null));
    }

    public Task<GovernedLoopEffectReconciliationAuthorizationResult> AuthorizeAsync(GovernedLoopEffectReconciliationAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult<GovernedLoopEffectReconciliationAuthorizationResult>(null!);
    }

    public Task<GovernedLoopEffectReconciliationProbeRegistryPage> ListAsync(GovernedLoopEffectReconciliationProbeRegistryListRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryPage(GovernedLoopEffectReconciliationProbeRegistryListStatus.Unavailable, [], null));
    }

    public Task<GovernedLoopEffectReconciliationProbeRegistryReadResult> ReadAsync(GovernedLoopEffectReconciliationProbeRegistryReadRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Unavailable, null, null));
    }

    public Task<GovernedLoopEffectReconciliationProbeInvocationResult> ProbeAsync(GovernedLoopEffectReconciliationProbeInvocationRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable, null));
    }

    public Task<GovernedLoopEffectReconciliationInputReadResult> ReadAsync(GovernedLoopEffectReconciliationInputReadRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationInputReadResult(GovernedLoopEffectReconciliationInputReadStatus.Unavailable, null, null, null, null, null));
    }

    public Task<GovernedLoopEffectReconciliationResolutionReadResult> ReadAsync(GovernedLoopEffectReconciliationResolutionReadRequest request, CancellationToken cancellationToken = default)
    {
        CancellationTokens.Add(cancellationToken);
        return Task.FromResult(new GovernedLoopEffectReconciliationResolutionReadResult(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, null));
    }
}
