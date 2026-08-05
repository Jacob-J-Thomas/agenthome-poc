namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityCatalogDocument(int SchemaVersion, string WorkspaceIdentity, long Generation, long CatalogRevision, IReadOnlyList<CapabilityCatalogEntryDocument> Entries, IReadOnlyList<CapabilityCatalogOperationDocument> Operations, string ContentDigest, string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
