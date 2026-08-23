using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

internal sealed class UnusedCommandActionCatalog : IGovernedActuatorCatalogResolver
{
    internal static UnusedCommandActionCatalog Instance { get; } = new();

    private UnusedCommandActionCatalog()
    {
    }

    public Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The command Action projection must delegate catalog access to the effect service.");

    public Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(
        CapabilityAdmissionPin pin,
        string operationId,
        CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("The command Action projection must delegate catalog resolution to the effect service.");
}
