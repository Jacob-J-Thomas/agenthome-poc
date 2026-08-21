using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Projects server registration through the authoritative current capability lifecycle catalog.</summary>
public interface IGovernedActuatorCatalogResolver
{
    /// <summary>Reads a finite sorted snapshot of active server-backed operations.</summary>
    Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Resolves one exact admitted capability pin and stable operation id.</summary>
    Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(
        CapabilityAdmissionPin pin,
        string operationId,
        CancellationToken cancellationToken = default);
}
