using EmbodySense.Core.Application.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Orchestrates authenticated immutable revision lifecycle operations without granting runtime authority or admission.</summary>
public interface IGovernedLoopRevisionLifecycleService
{
    /// <summary>Executes one canonical request under the shared reentrant workspace authority fence.</summary>
    /// <param name="request">The lifecycle request, or <see langword="null"/> to obtain structured validation evidence.</param>
    /// <param name="cancellationToken">The cancellation token honored before durable intent publication.</param>
    /// <returns>A bounded exact lifecycle outcome.</returns>
    Task<GovernedLoopRevisionLifecycleMutationResult> MutateAsync(
        GovernedLoopRevisionLifecycleRequest? request,
        CancellationToken cancellationToken = default);
}
