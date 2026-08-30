using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessCatalog(HumanReviewOrderedReleaseProcessMarkerOperation operation) : IGovernedActuatorCatalogResolver
{
    private readonly CapabilityDescriptor _capability = HumanReviewOrderedReleaseGraphFixture.WorkspaceCapability();

    public Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedActuatorCatalogReadResult(GovernedActuatorCatalogReadStatus.Available, [operation.Descriptor], "The process marker operation is active."));
    }

    public Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(CapabilityAdmissionPin pin, string operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = CapabilityDescriptorIdentity.TryCreate(_capability, out var identity, out _);
        var active = Equals(identity, pin.DescriptorIdentity)
            && Equals(_capability.Implementation, pin.Implementation)
            && string.Equals(operation.Descriptor.OperationId, operationId, StringComparison.Ordinal);
        return Task.FromResult(active
            ? new GovernedActuatorCatalogResolutionResult(GovernedActuatorCatalogResolutionStatus.Active, _capability, operation.Descriptor, operation, "The exact process marker operation is active.")
            : new GovernedActuatorCatalogResolutionResult(GovernedActuatorCatalogResolutionStatus.OperationUnregistered, null, null, null, "The requested process marker operation is not registered."));
    }
}
