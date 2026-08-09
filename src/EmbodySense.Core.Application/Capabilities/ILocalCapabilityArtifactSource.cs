using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Reads artifacts only from a configured local source boundary.</summary>
public interface ILocalCapabilityArtifactSource
{
    /// <summary>Reads one bounded local artifact without following a source outside the configured root.</summary>
    Task<CapabilityArtifactContent> ReadAsync(CapabilityArtifactSourceReference source, CancellationToken cancellationToken = default);
}
