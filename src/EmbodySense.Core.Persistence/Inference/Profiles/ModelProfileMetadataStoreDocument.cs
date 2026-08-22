using EmbodySense.Core.Persistence.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal sealed record ModelProfileMetadataStoreDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    long Generation,
    IReadOnlyList<ModelProfileMetadataRevision> Revisions,
    IReadOnlyList<ModelProfileMetadataCurrentPointer> CurrentProfiles,
    string ContentDigest,
    string AuthenticationTag) : IAuthenticatedModelPersistenceDocument<ModelProfileMetadataStoreDocument>
{
    internal const int CurrentSchemaVersion = 1;

    public ModelProfileMetadataStoreDocument WithAuthentication(string contentDigest, string authenticationTag)
        => this with { ContentDigest = contentDigest, AuthenticationTag = authenticationTag };
}
