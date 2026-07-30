using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Represents a custom loop trace tombstone.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="ArtifactKind">The artifact kind.</param>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="AdmissionOperationId">The idempotency identity of the admission operation.</param>
/// <param name="AdmissionRequestHash">The integrity hash of the immutable admission inputs.</param>
/// <param name="TerminalStatus">The terminal status.</param>
/// <param name="DefinitionVersion">The monotonically increasing definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="OriginalTraceHash">The original trace hash.</param>
/// <param name="OriginalTraceUtf8Bytes">The original trace UTF-8 bytes.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="CompletedAtUtc">The UTC terminal time, or <see langword="null"/> while nonterminal.</param>
/// <param name="DeletedAtUtc">The deleted at UTC.</param>
/// <param name="DeletionActor">The deletion actor.</param>
/// <param name="DeletionSurface">The deletion surface.</param>
/// <param name="DeletionOperationId">The deletion operation ID.</param>
/// <param name="DeletionRequestHash">The deletion request hash.</param>
/// <param name="IntentAuditCorrelationId">The intent audit correlation ID.</param>
/// <param name="OutcomeAuditCorrelationId">The outcome audit correlation ID.</param>
/// <param name="OutcomeIntegrity">The outcome integrity.</param>
public sealed record CustomLoopTraceTombstone(
    int SchemaVersion,
    string ArtifactKind,
    string RunId,
    string LoopId,
    string AdmissionOperationId,
    string AdmissionRequestHash,
    CustomLoopRunStatus TerminalStatus,
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
    string DeletionRequestHash,
    string IntentAuditCorrelationId,
    string OutcomeAuditCorrelationId,
    CustomLoopTraceDeletionIntegrity OutcomeIntegrity)
{
    /// <summary>
    /// Identifies the current schema version custom loop trace tombstone.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
    /// <summary>
    /// Identifies the current artifact kind custom loop trace tombstone.
    /// </summary>
    public const string CurrentArtifactKind = "terminalTraceTombstone";
}
