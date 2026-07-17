using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopControlKind
{
    Unknown = 0,
    Pause = 1,
    Cancel = 2,
    Resume = 3
}

public enum CustomLoopControlStatus
{
    Unknown = 0,
    PauseRequested = 1,
    Paused = 2,
    CancelRequested = 3,
    Cancelled = 4,
    Resumed = 5,
    Completed = 6,
    Failed = 7,
    NeedsReview = 8,
    Replayed = 9,
    Conflict = 10,
    InvalidState = 11,
    NotFound = 12,
    AuditWarning = 13,
    WorkspaceExecutionBusy = 14,
    OperationInProgress = 15
}

public enum CustomLoopControlOperationState
{
    Unknown = 0,
    Pending = 1,
    Complete = 2
}

public sealed record CustomLoopControlOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    CustomLoopControlKind Kind,
    string RunId,
    int ExpectedLifecycleVersion,
    string Actor,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopControlOperationState State,
    CustomLoopControlStatus Outcome,
    int? ResultLifecycleVersion,
    CustomLoopRunStatus? ResultRunStatus,
    bool OutcomeAuditRecorded,
    string Detail)
{
    public const int CurrentSchemaVersion = 1;
}

public enum CustomLoopControlOperationStoreStatus
{
    Unknown = 0,
    Created = 1,
    Replayed = 2,
    Conflict = 3,
    Completed = 4,
    NotFound = 5
}

public sealed record CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus Status, CustomLoopControlOperation? Operation);

public interface ICustomLoopControlOperationStore
{
    Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default);

    Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default);
}

public static class CustomLoopControlRequestHash
{
    public static string Compute(CustomLoopControlKind kind, string runId, int expectedLifecycleVersion, string operationId, string actor)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind.ToString().ToLowerInvariant());
            writer.WriteString("runId", runId);
            writer.WriteNumber("expectedLifecycleVersion", expectedLifecycleVersion);
            writer.WriteString("operationId", operationId);
            writer.WriteString("actor", actor);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public static bool Matches(CustomLoopControlOperation operation)
    {
        var expected = Encoding.ASCII.GetBytes(Compute(operation.Kind, operation.RunId, operation.ExpectedLifecycleVersion, operation.OperationId, operation.Actor));
        var actual = Encoding.ASCII.GetBytes(operation.RequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
