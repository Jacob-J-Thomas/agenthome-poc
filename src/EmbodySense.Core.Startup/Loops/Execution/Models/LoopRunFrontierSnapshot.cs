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
