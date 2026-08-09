namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityArtifactOperationDocument(string OperationId, string Kind, string CapabilityId, string RequestDigest, string ArtifactDigest, long ExpectedRevision, long ResultRevision);
