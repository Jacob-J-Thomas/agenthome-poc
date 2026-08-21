namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal interface IAuthenticatedModelPersistenceDocument<TDocument> where TDocument : class
{
    long Generation { get; }

    string WorkspaceIdentity { get; }

    string ContentDigest { get; }

    string AuthenticationTag { get; }

    TDocument WithAuthentication(string contentDigest, string authenticationTag);
}
