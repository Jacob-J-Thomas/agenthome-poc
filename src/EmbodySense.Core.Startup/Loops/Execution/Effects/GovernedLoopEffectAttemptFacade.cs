using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Effects;

/// <summary>Exposes the canonical actuator catalog and effect-attempt protocol without surface-specific semantics.</summary>
public sealed class GovernedLoopEffectAttemptFacade(
    IGovernedActuatorCatalogResolver catalog,
    IGovernedLoopEffectAttemptService service)
{
    private readonly IGovernedActuatorCatalogResolver _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IGovernedLoopEffectAttemptService _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <summary>Reads a bounded current snapshot of server-registered actuator operations.</summary>
    public Task<GovernedActuatorCatalogReadResult> ReadCatalogAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
        => _catalog.ReadAsync(maximumCount, cancellationToken);

    /// <summary>Executes or safely resumes one exact admitted effect generation.</summary>
    public Task<GovernedLoopEffectAttemptExecutionResult> ExecuteAsync(
        GovernedLoopEffectAttemptRequest request,
        CancellationToken cancellationToken = default)
        => _service.ExecuteAsync(request, cancellationToken);
}
