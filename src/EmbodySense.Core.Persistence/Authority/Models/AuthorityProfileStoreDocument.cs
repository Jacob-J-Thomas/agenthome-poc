namespace EmbodySense.Core.Persistence.Authority.Models;

internal sealed record AuthorityProfileStoreDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    long Generation,
    IReadOnlyList<AuthorityProfileDocument> Profiles,
    IReadOnlyList<AuthorityProfileOperationDocument> Operations,
    IReadOnlyList<AuthorityGrantDocument> Grants,
    IReadOnlyList<AuthorityGrantOperationDocument> GrantOperations,
    string ContentDigest,
    string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
