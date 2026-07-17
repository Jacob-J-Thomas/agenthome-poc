using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Application.Loops.TraceRetention;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopRunStore
{
    Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default);

    Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default);

    Task<CustomLoopTraceQuota> GetTraceQuotaAsync(CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceQuota.Empty());

    Task<CustomLoopTraceInspection?> InspectTraceAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopTraceInspection?>(null);

    Task<CustomLoopTraceDeletionLookupResult> GetTraceDeletionOperationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceDeletionLookupResult.NotFound());

    Task<CustomLoopTraceDeletionStoreResult> DeleteTerminalTraceAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default) => Task.FromResult(new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.NotFound, null, CustomLoopTraceDeletionIntegrity.Unknown));

    Task<CustomLoopTraceDeletionAuditMarkStatus> MarkTraceDeletionOutcomeAsync(string operationId, CustomLoopTraceDeletionIntegrity integrity, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceDeletionAuditMarkStatus.NotFound);

    Task<CustomLoopRunStoreResult> AppendTerminalIntegrityWarningAsync(string runId, int expectedLifecycleVersion, CustomLoopRunEvent warning, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());

    Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default);
}

public sealed record CustomLoopTraceQuota(
    int RetainedTraceCount,
    long ActualTraceUtf8Bytes,
    long AccountedTraceUtf8Bytes,
    int ActiveReservationCount,
    int MaximumTraceCount,
    long MaximumWorkspaceUtf8Bytes,
    int MaximumPerTraceUtf8Bytes,
    int TombstoneCount = 0,
    long TombstoneUtf8Bytes = 0,
    int MaximumTombstoneCount = CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace)
{
    public long ReservedCapacityUtf8Bytes => Math.Max(0, AccountedTraceUtf8Bytes - ActualTraceUtf8Bytes - TombstoneUtf8Bytes);

    public long ActualStoredUtf8Bytes => checked(ActualTraceUtf8Bytes + TombstoneUtf8Bytes);

    public long AvailableAccountedUtf8Bytes => Math.Max(0, MaximumWorkspaceUtf8Bytes - AccountedTraceUtf8Bytes);

    public bool IsOverLimit => RetainedTraceCount > MaximumTraceCount || TombstoneCount > MaximumTombstoneCount || AccountedTraceUtf8Bytes > MaximumWorkspaceUtf8Bytes;

    public static CustomLoopTraceQuota Empty() => new(
        0,
        0,
        0,
        0,
        CustomLoopLimits.MaxRunTracesPerWorkspace,
        CustomLoopLimits.MaxRunTraceWorkspaceUtf8Bytes,
        CustomLoopLimits.MaxRunTraceUtf8Bytes,
        0,
        0,
        CustomLoopLimits.MaxRunTraceTombstonesPerWorkspace);
}

public enum CustomLoopRunStoreStatus
{
    Unknown = 0,
    Created = 1,
    Updated = 2,
    AlreadyCreated = 3,
    Conflict = 4,
    OperationConflict = 5,
    NonterminalRunExists = 6,
    NotFound = 7,
    LimitExceeded = 8,
    TerminalImmutable = 9,
    DeletedIdentityConflict = 10
}

public sealed record CustomLoopRunConflict(
    string RunId,
    int ExpectedLifecycleVersion,
    int ActualLifecycleVersion,
    CustomLoopRunStatus ActualStatus,
    DateTimeOffset ActualUpdatedAtUtc);

public sealed record CustomLoopRunStoreResult(
    CustomLoopRunStoreStatus Status,
    CustomLoopRunRecord? Run,
    CustomLoopRunConflict? Conflict)
{
    public static CustomLoopRunStoreResult Created(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.Created, run, null);

    public static CustomLoopRunStoreResult Updated(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.Updated, run, null);

    public static CustomLoopRunStoreResult AlreadyCreated(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.AlreadyCreated, run, null);

    public static CustomLoopRunStoreResult VersionConflict(CustomLoopRunRecord run, int expectedLifecycleVersion)
    {
        var conflict = new CustomLoopRunConflict(run.Id, expectedLifecycleVersion, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.Conflict, null, conflict);
    }

    public static CustomLoopRunStoreResult NotFound() => new(CustomLoopRunStoreStatus.NotFound, null, null);

    public static CustomLoopRunStoreResult LimitExceeded() => new(CustomLoopRunStoreStatus.LimitExceeded, null, null);

    public static CustomLoopRunStoreResult OperationConflict(CustomLoopRunRecord run)
    {
        var conflict = new CustomLoopRunConflict(run.Id, 0, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.OperationConflict, null, conflict);
    }

    public static CustomLoopRunStoreResult NonterminalRunExists(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.NonterminalRunExists, run, null);

    public static CustomLoopRunStoreResult TerminalImmutable(CustomLoopRunRecord run, int expectedLifecycleVersion)
    {
        var conflict = new CustomLoopRunConflict(run.Id, expectedLifecycleVersion, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.TerminalImmutable, null, conflict);
    }
}
