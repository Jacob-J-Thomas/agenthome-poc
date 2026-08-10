namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record GraphRevisionArtifactJson(
    int SchemaVersion,
    string WorkspaceIdentity,
    long TrustGeneration,
    ExecutableGraphJson? ExecutableGraph,
    GraphLayoutJson? Layout,
    string? ExecutableHash,
    string? LayoutHash,
    string? PayloadHash,
    string? ContentDigest,
    string? AuthenticationTag);
