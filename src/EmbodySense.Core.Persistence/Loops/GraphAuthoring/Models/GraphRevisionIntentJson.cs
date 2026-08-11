namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GraphRevisionIntentJson(
    int SchemaVersion,
    string? WorkspaceIdentity,
    long TrustGeneration,
    string? GraphId,
    string? OperationId,
    string? LifecycleRequestHash,
    string? AuthoringRequestHash,
    string? GraphPayloadHash,
    string? GraphValidationEvidenceHash,
    string? ContentDigest,
    string? AuthenticationTag);
