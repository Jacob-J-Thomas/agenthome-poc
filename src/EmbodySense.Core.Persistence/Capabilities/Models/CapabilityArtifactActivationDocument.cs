namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityArtifactActivationDocument(int SchemaVersion, long Revision, IReadOnlyList<CapabilityArtifactActivationEntryDocument> Entries, IReadOnlyList<CapabilityArtifactOperationDocument> Operations, string ContentDigest, string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
