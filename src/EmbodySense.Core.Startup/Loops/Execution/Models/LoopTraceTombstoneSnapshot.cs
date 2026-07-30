namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Preserves deletion, integrity, and original-trace identity after terminal evidence is removed.
/// </summary>
/// <param name="RunId">The run identifier.</param>
/// <param name="LoopId">The loop identifier.</param>
/// <param name="AdmissionOperationId">The admission operation identifier.</param>
/// <param name="TerminalStatus">The terminal status.</param>
/// <param name="DefinitionVersion">The definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="OriginalTraceHash">The original trace hash.</param>
/// <param name="OriginalTraceUtf8Bytes">The original trace utf8 bytes.</param>
/// <param name="CreatedAtUtc">The created at utc.</param>
/// <param name="CompletedAtUtc">The completed at utc.</param>
/// <param name="DeletedAtUtc">The deleted at utc.</param>
/// <param name="DeletionActor">The deletion actor.</param>
/// <param name="DeletionSurface">The deletion surface.</param>
/// <param name="DeletionOperationId">The deletion operation identifier.</param>
/// <param name="IntentAuditCorrelationId">The intent audit correlation identifier.</param>
/// <param name="OutcomeAuditCorrelationId">The outcome audit correlation identifier.</param>
/// <param name="OutcomeIntegrity">The outcome integrity.</param>
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
