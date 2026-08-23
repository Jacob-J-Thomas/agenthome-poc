using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Inference;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Executes one exact admitted primary behind durable reservation and usage boundaries.</summary>
public interface IGovernedModelPrimaryExecutionService
{
    /// <summary>Executes or returns one structured stopped posture without selecting a fallback.</summary>
    Task<GovernedModelPrimaryExecutionResult> ExecuteAsync(
        GovernedModelPrimaryExecutionRequest? request,
        InferenceProviderTransportCommitBoundary providerAuthorityBoundary,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Action? providerRequestStarted = null,
        CancellationToken cancellationToken = default);
}
