using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class InvocationPreparationReadyModelMetadataSource(
    CapabilityId profileId,
    GovernedModelProfileMetadata metadata,
    string sourceRevisionHash) : IModelProfileMetadataSource
{
    public Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId requestedProfileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(requestedProfileId.Equals(profileId)
            ? new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.Found, metadata, sourceRevisionHash)
            : new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.NotFound, null, null));
    }
}
