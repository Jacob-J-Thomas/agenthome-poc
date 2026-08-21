using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Resolves fresh provider transport only from one exact admitted profile, attempt, authority, and hard-budget envelope.</summary>
public interface IExactModelProfileInferenceClientResolver
{
    /// <summary>Returns a fresh exact client lease only after the adapter accepts every required pre-transport hard bound.</summary>
    Task<ExactModelProfileInferenceClientResolution> ResolveAsync(ExactModelProfileInferenceClientRequest request, CancellationToken cancellationToken = default);
}
