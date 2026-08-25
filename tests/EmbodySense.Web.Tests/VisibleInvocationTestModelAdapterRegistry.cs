using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Web.Tests;

internal sealed class VisibleInvocationTestModelAdapterRegistry(
    string metadataHash,
    string registryRevisionHash) : IModelProfileAdapterRegistry
{
    public Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ModelProfileAdapterPosture(
            string.Equals(metadata.ContentHash, metadataHash, StringComparison.Ordinal)
                ? ModelProfileAdapterPostureStatus.Ready
                : ModelProfileAdapterPostureStatus.Unregistered,
            metadata.ContentHash,
            registryRevisionHash));
    }
}
