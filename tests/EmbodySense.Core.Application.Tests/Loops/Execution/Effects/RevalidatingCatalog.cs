using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

internal sealed class RevalidatingCatalog(
    GovernedLoopEffectAttemptTestFixture fixture,
    IGovernedActuatorOperation initialOperation,
    IGovernedActuatorOperation changedOperation) : IGovernedActuatorCatalogResolver
{
    private int _resolveCalls;

    public Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(CapabilityAdmissionPin pin, string operationId, CancellationToken cancellationToken = default)
    {
        var operation = Interlocked.Increment(ref _resolveCalls) == 1 ? initialOperation : changedOperation;
        return Task.FromResult(new GovernedActuatorCatalogResolutionResult(
            GovernedActuatorCatalogResolutionStatus.Active,
            fixture.Capability,
            operation.Descriptor,
            operation,
            "active"));
    }
}
