using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Observes exact governed-provider execution boundaries without receiving provider content or credentials.</summary>
public interface IGovernedModelPrimaryExecutionBoundaryObserver
{
    /// <summary>Observes one exact boundary. Completion is part of the boundary and cancellation is propagated.</summary>
    ValueTask ObserveAsync(
        GovernedModelPrimaryExecutionBoundary boundary,
        CancellationToken cancellationToken = default);
}
