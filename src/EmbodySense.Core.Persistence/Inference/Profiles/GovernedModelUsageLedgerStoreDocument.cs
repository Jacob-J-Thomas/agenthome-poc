using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal sealed record GovernedModelUsageLedgerStoreDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    string WorkspaceId,
    long Generation,
    long SegmentIndex,
    long SegmentStartGeneration,
    string? PreviousSegmentContentDigest,
    IReadOnlyList<GovernedModelUsageLedgerEntry> Entries,
    string ContentDigest,
    string AuthenticationTag) : IAuthenticatedModelPersistenceDocument<GovernedModelUsageLedgerStoreDocument>
{
    internal const int CurrentSchemaVersion = 1;

    public GovernedModelUsageLedgerStoreDocument WithAuthentication(string contentDigest, string authenticationTag)
        => this with { ContentDigest = contentDigest, AuthenticationTag = authenticationTag };
}
