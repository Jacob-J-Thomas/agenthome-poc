using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

public enum CustomLoopTraceArtifactKind
{
    Unknown = 0,
    LiveTrace = 1,
    Tombstone = 2
}

public enum CustomLoopTraceDeletionIntegrity
{
    Unknown = 0,
    PendingOutcomeAudit = 1,
    OutcomeAuditStarted = 2,
    Complete = 3,
    CommittedWithAuditWarning = 4
}

public sealed record CustomLoopTraceInspection(
    CustomLoopTraceArtifactKind Kind,
    string RunId,
    string LoopId,
    CustomLoopRunStatus TerminalStatus,
    int DefinitionVersion,
    string DefinitionHash,
    string PersistedArtifactHash,
    long PersistedArtifactUtf8Bytes,
    string OriginalTraceHash,
    long OriginalTraceUtf8Bytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    CustomLoopTraceTombstone? Tombstone)
{
    public bool IsDeleted => Kind == CustomLoopTraceArtifactKind.Tombstone;
}

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

public sealed record CustomLoopTraceDeletionRequest(
    string RunId,
    string ExpectedTraceHash,
    string OperationId,
    string Actor,
    string Surface);

public static class CustomLoopTraceDeletionRequestHash
{
    public static string Compute(CustomLoopTraceDeletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new CanonicalDeletionRequest(1, request.RunId, request.ExpectedTraceHash, request.OperationId, request.Actor, request.Surface);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }

    private sealed record CanonicalDeletionRequest(int SchemaVersion, string RunId, string ExpectedTraceHash, string OperationId, string Actor, string Surface);
}

public sealed record CustomLoopTraceDeletionMutation(
    CustomLoopTraceDeletionRequest Request,
    string RequestHash,
    DateTimeOffset RequestedAtUtc);

public enum CustomLoopTraceDeletionStoreStatus
{
    Unknown = 0,
    Deleted = 1,
    AlreadyDeleted = 2,
    NotFound = 3,
    Nonterminal = 4,
    HashMismatch = 5,
    OperationConflict = 6,
    TombstoneLimitExceeded = 7
}

public sealed record CustomLoopTraceDeletionStoreResult(
    CustomLoopTraceDeletionStoreStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    CustomLoopTraceDeletionIntegrity Integrity)
{
    public bool IsCommitted => Status is CustomLoopTraceDeletionStoreStatus.Deleted or CustomLoopTraceDeletionStoreStatus.AlreadyDeleted;
}

public enum CustomLoopTraceDeletionOperationState
{
    Unknown = 0,
    PendingMutation = 1,
    OutcomeCommitted = 2
}

public sealed record CustomLoopTraceDeletionOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    CustomLoopTraceDeletionRequest Request,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopTraceDeletionOperationState State,
    CustomLoopTraceDeletionStoreStatus Outcome,
    CustomLoopTraceTombstone? Tombstone,
    CustomLoopTraceDeletionIntegrity Integrity)
{
    public const int CurrentSchemaVersion = 1;

    public CustomLoopTraceDeletionStoreResult ToStoreResult()
    {
        if (State != CustomLoopTraceDeletionOperationState.OutcomeCommitted)
        {
            throw new InvalidOperationException("A pending trace-deletion operation has no replayable store result.");
        }

        return new CustomLoopTraceDeletionStoreResult(Outcome, Tombstone, Integrity);
    }
}

public enum CustomLoopTraceDeletionLookupStatus
{
    Unknown = 0,
    NotFound = 1,
    PendingMutation = 2,
    OutcomeCommitted = 3
}

public sealed record CustomLoopTraceDeletionLookupResult(CustomLoopTraceDeletionLookupStatus Status, CustomLoopTraceDeletionOperation? Operation)
{
    public static CustomLoopTraceDeletionLookupResult NotFound() => new(CustomLoopTraceDeletionLookupStatus.NotFound, null);

    public static CustomLoopTraceDeletionLookupResult Found(CustomLoopTraceDeletionOperation operation)
    {
        var status = operation.State == CustomLoopTraceDeletionOperationState.PendingMutation
            ? CustomLoopTraceDeletionLookupStatus.PendingMutation
            : CustomLoopTraceDeletionLookupStatus.OutcomeCommitted;
        return new CustomLoopTraceDeletionLookupResult(status, operation);
    }
}

public enum CustomLoopTraceDeletionAuditMarkStatus
{
    Unknown = 0,
    Marked = 1,
    AlreadyMarked = 2,
    NotFound = 3
}

public enum CustomLoopTraceDeletionStatus
{
    Unknown = 0,
    Deleted = 1,
    Replayed = 2,
    NotFound = 3,
    Nonterminal = 4,
    HashMismatch = 5,
    Conflict = 6,
    LimitExceeded = 7,
    Invalid = 8,
    AuditUnavailable = 9,
    CommittedWithAuditWarning = 10
}

public sealed record CustomLoopTraceDeletionResult(
    CustomLoopTraceDeletionStatus Status,
    CustomLoopTraceTombstone? Tombstone,
    string Detail)
{
    public bool IsCommitted => Status is CustomLoopTraceDeletionStatus.Deleted or CustomLoopTraceDeletionStatus.Replayed or CustomLoopTraceDeletionStatus.CommittedWithAuditWarning;
}
