namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityLifecycleDocument(int SchemaVersion, string WorkspaceIdentity, long Generation, long DependentSetRevision, string DependentSetHash, IReadOnlyList<CapabilityDependentDocument> Dependents, IReadOnlyList<CapabilityLifecycleEntryDocument> Entries, IReadOnlyList<CapabilityLifecycleOperationDocument> Operations, string ContentDigest, string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
