namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryDocument(int SchemaVersion, string WorkspaceIdentity, long Generation, long Revision, IReadOnlyList<CredentialRegistryEntryDocument> Entries, IReadOnlyList<CredentialRegistryTombstoneDocument> Tombstones, IReadOnlyList<CredentialRegistryOperationDocument> Operations, IReadOnlyList<CredentialRegistryEvidenceDocument> Evidence, string StateDigest, string ContentDigest, string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
