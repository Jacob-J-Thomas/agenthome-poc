namespace EmbodySense.Core.Persistence.Credentials.Models;

internal sealed record CredentialRegistryPrivateDocument(int SchemaVersion, string WorkspaceIdentity, long Revision, IReadOnlyList<CredentialRegistryLocatorDocument> Locators, string StateDigest)
{
    internal const int CurrentSchemaVersion = 1;
}
