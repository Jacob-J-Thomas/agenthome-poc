using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Materializes one successful canonical admission into the authoritative durable ordered-run store.</summary>
public interface IGovernedLoopSequentialRunMaterializer
{
    /// <summary>Creates or reconciles the exact receipt-owned run without dispatching execution.</summary>
    Task<GovernedLoopSequentialMaterializationResult> MaterializeAsync(
        GovernedLoopSequentialMaterializationRequest? request,
        CancellationToken cancellationToken = default);
}
