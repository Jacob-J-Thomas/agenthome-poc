namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects the durable reconciliation receipt for one pause, cancel, or resume operation.
/// </summary>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="Kind">The kind.</param>
/// <param name="RunId">The run identifier.</param>
/// <param name="ExpectedLifecycleVersion">The expected lifecycle version.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="ResultLifecycleVersion">The result lifecycle version.</param>
/// <param name="ResultRunStatus">The result run status.</param>
/// <param name="OutcomeAuditRecorded">The outcome audit recorded.</param>
/// <param name="CompletionDurablyProved">The completion durably proved.</param>
/// <param name="CreatedAtUtc">The created at utc.</param>
/// <param name="UpdatedAtUtc">The updated at utc.</param>
public sealed record LoopControlOperationSnapshot(
    string OperationId,
    string Kind,
    string RunId,
    int ExpectedLifecycleVersion,
    string State,
    string Outcome,
    int? ResultLifecycleVersion,
    string? ResultRunStatus,
    bool OutcomeAuditRecorded,
    bool CompletionDurablyProved,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
