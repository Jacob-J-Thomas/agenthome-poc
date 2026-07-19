using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

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
    public const int CurrentSchemaVersion = 1;
    public const string CurrentArtifactKind = "terminalTraceTombstone";
}
