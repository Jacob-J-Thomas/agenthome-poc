namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Projects one exact, bounded canonical execution frontier without exposing a mutable runtime object.</summary>
public sealed record LoopRunFrontierSnapshot(
    int SchemaVersion,
    string WorkspaceId,
    LoopRunFrontierBindingSnapshot Binding,
    string GraphArtifactHash,
    string GraphLayoutHash,
    string AdmissionReceiptHash,
    long FrontierVersion,
    int ConcurrencyCeiling,
    string Status,
    DateTimeOffset UpdatedAtUtc,
    string ContentHash,
    IReadOnlyList<LoopRunFrontierNodeSnapshot> Nodes);

/// <summary>Projects the immutable run, revision, and execution-generation coordinates of a frontier.</summary>
public sealed record LoopRunFrontierBindingSnapshot(
    int SchemaVersion,
    string RunId,
    string GraphId,
    string RevisionId,
    string ExecutableHash,
    long ExecutionGeneration);

/// <summary>Projects one reached node's immutable plan coordinates and durable posture.</summary>
public sealed record LoopRunFrontierNodeSnapshot(
    int SchemaVersion,
    int PlanOrdinal,
    string NodeId,
    string Kind,
    string TypeId,
    int DescriptorVersion,
    IReadOnlyList<string> IncomingControlEdgeIds,
    IReadOnlyList<string> OutgoingControlEdgeIds,
    string Status,
    int? Attempt,
    string? AttemptOperationId,
    string? OutcomeEvidenceId,
    string? OutcomeEvidenceHash);
