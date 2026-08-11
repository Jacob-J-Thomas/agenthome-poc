using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

/// <summary>Authors immutable governed graph revisions through the canonical lifecycle policy.</summary>
public interface IGovernedLoopGraphAuthoringService
{
    /// <summary>Executes one authenticated, globally idempotent graph authoring operation.</summary>
    Task<GovernedLoopGraphAuthoringResult> MutateAsync(
        GovernedLoopGraphAuthoringRequest? request,
        CancellationToken cancellationToken = default);
}
