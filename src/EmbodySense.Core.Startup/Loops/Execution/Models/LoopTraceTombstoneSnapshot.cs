namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopTraceTombstoneSnapshot(
    string RunId,
    string LoopId,
    string AdmissionOperationId,
    string TerminalStatus,
    int DefinitionVersion,
    string DefinitionHash,
    string OriginalTraceHash,
    long OriginalTraceUtf8Bytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset DeletedAtUtc,
    string DeletionActor,
    string DeletionSurface,
    string DeletionOperationId,
    string IntentAuditCorrelationId,
    string OutcomeAuditCorrelationId,
    string OutcomeIntegrity);
