namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityCatalogTrustAnchorDocument(int SchemaVersion, string WorkspaceIdentity, long CurrentGeneration, string CurrentContentDigest, long? PreviousGeneration, string? PreviousContentDigest, string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
