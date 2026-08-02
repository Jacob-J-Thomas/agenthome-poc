using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Reads artifacts only through a bounded remote transport.</summary>
public interface IRemoteCapabilityArtifactSource
{
    /// <summary>Reads one bounded canonical HTTPS artifact.</summary>
    Task<CapabilityArtifactContent> ReadAsync(CapabilityArtifactSourceReference source, CancellationToken cancellationToken = default);
}
