using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Effects;

internal sealed class NullCatalog : IGovernedActuatorCatalogResolver
{
    public Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(CapabilityAdmissionPin pin, string operationId, CancellationToken cancellationToken = default)
        => Task.FromResult<GovernedActuatorCatalogResolutionResult>(null!);
}
