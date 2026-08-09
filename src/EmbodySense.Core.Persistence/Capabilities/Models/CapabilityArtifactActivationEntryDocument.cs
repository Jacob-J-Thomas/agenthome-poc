namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityArtifactActivationEntryDocument(string CapabilityId, string ArtifactDigest, string? PriorArtifactDigest, long Revision, DateTimeOffset ActivatedAtUtc);
