namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Preserves the exact source and update evidence for one artifact.</summary>
/// <param name="Kind">The bounded source transport.</param>
/// <param name="Uri">The canonical source URI.</param>
/// <param name="Revision">The exact source revision.</param>
/// <param name="UpdatePolicy">The non-authoritative update declaration.</param>
public sealed record CapabilityArtifactSourceReference(CapabilityArtifactSourceKind Kind, string Uri, string Revision, CapabilityArtifactUpdatePolicy UpdatePolicy);
