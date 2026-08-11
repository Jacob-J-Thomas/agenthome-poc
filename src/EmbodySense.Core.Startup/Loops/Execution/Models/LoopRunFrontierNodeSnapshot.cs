namespace EmbodySense.Core.Startup.Loops.Execution.Models;

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
