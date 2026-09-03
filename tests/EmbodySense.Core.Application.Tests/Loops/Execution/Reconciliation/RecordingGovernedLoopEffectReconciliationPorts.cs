using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

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

    internal GovernedLoopEffectReconciliationContractMetadata? RegisteredContract { get; set; }

    internal GovernedLoopEffectReconciliationProbeRegistryReadStatus? ForcedRegistryStatus { get; set; }

    internal bool ThrowOnRegistryRead { get; set; }

    internal bool ReturnNullOnRegistryRead { get; set; }

    internal int RegistryReadCalls { get; private set; }

    internal Func<int, GovernedLoopEffectReconciliationProbeRegistryReadResult?>? RegistryReadResultFactory { get; set; }

    internal Func<GovernedLoopEffectReconciliationProbeInvocationRequest, GovernedLoopEffectReconciliationProbeInvocationResult>? ProbeResultFactory { get; set; }

    internal Exception? ProbeException { get; set; }

    internal int ProbeCalls { get; private set; }

    internal GovernedLoopEffectReconciliationProbeInvocationRequest? LastInvocation { get; private set; }

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
        RegistryReadCalls++;
        if (ThrowOnRegistryRead)
        {
            throw new IOException("The test registry is unavailable.");
        }

        if (ReturnNullOnRegistryRead)
        {
            return Task.FromResult<GovernedLoopEffectReconciliationProbeRegistryReadResult>(null!);
        }

        if (RegistryReadResultFactory is not null)
        {
            return Task.FromResult(RegistryReadResultFactory(RegistryReadCalls)!);
        }

        if (ForcedRegistryStatus is { } forcedStatus)
        {
            return Task.FromResult(new GovernedLoopEffectReconciliationProbeRegistryReadResult(forcedStatus, forcedStatus == GovernedLoopEffectReconciliationProbeRegistryReadStatus.Conflict ? RegisteredContract : null, null));
        }

        return Task.FromResult(RegisteredContract is null
            ? new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Unavailable, null, null)
            : new GovernedLoopEffectReconciliationProbeRegistryReadResult(GovernedLoopEffectReconciliationProbeRegistryReadStatus.Found, RegisteredContract, this));
    }

    public Task<GovernedLoopEffectReconciliationProbeInvocationResult> ProbeAsync(GovernedLoopEffectReconciliationProbeInvocationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokens.Add(cancellationToken);
        ProbeCalls++;
        LastInvocation = request;
        if (ProbeException is not null)
        {
            throw ProbeException;
        }

        return Task.FromResult(ProbeResultFactory?.Invoke(request)
            ?? new GovernedLoopEffectReconciliationProbeInvocationResult(GovernedLoopEffectReconciliationProbeInvocationStatus.Unavailable, null));
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
