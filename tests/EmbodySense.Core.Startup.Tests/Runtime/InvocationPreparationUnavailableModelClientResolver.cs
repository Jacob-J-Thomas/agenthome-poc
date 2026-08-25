using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class InvocationPreparationUnavailableModelClientResolver : IExactModelProfileInferenceClientResolver
{
    public Task<ExactModelProfileInferenceClientResolution> ResolveAsync(ExactModelProfileInferenceClientRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExactModelProfileInferenceClientResolution(ExactModelProfileInferenceClientResolutionStatus.Unavailable, null));
    }
}
