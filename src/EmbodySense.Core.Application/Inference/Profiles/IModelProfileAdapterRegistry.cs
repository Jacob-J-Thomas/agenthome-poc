using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Proves exact adapter/configuration registration without exposing or creating concrete clients in Application.</summary>
public interface IModelProfileAdapterRegistry
{
    /// <summary>Reads current posture for exact safe profile metadata.</summary>
    Task<ModelProfileAdapterPosture> ReadPostureAsync(GovernedModelProfileMetadata metadata, CancellationToken cancellationToken = default);
}
