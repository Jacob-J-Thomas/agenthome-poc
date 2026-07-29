namespace EmbodySense.Core.Startup.Loops.Execution;

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
