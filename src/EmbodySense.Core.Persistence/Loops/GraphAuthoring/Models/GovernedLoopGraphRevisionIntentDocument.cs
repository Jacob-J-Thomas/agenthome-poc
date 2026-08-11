namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GovernedLoopGraphRevisionIntentDocument(
    int SchemaVersion,
    string WorkspaceIdentity,
    long TrustGeneration,
    string GraphId,
    string OperationId,
    string LifecycleRequestHash,
    string AuthoringRequestHash,
    string? GraphPayloadHash,
    string? GraphValidationEvidenceHash,
    string ContentDigest,
    string AuthenticationTag)
{
    internal const int CurrentSchemaVersion = 1;
}
