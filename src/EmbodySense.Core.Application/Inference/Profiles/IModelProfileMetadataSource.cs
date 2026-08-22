using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Reads safe model-profile metadata from replaceable server-owned configuration, never workspace context.</summary>
public interface IModelProfileMetadataSource
{
    /// <summary>Reads one exact profile capability ID.</summary>
    Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default);
}
