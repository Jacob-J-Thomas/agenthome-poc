using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class LoopRunInspectionFacade
{
    private readonly CustomLoopRunStore _runStore;
    private readonly CustomLoopTraceRetentionService? _retention;
    private readonly string? _actor;
    private readonly string? _surface;

    public LoopRunInspectionFacade(string workingDirectory, string? authenticatedActor = null, string? authenticatedSurface = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(authenticatedActor) != string.IsNullOrWhiteSpace(authenticatedSurface))
        {
            throw new ArgumentException("Authenticated trace management requires both a server-owned actor and surface.");
        }

        var paths = new WorkspacePaths(workingDirectory);
        _runStore = new CustomLoopRunStore(paths);
        _actor = authenticatedActor;
        _surface = authenticatedSurface;
        _retention = authenticatedActor is null ? null : new CustomLoopTraceRetentionService(_runStore, new AuditLog(paths));
    }

    public async Task<LoopRunSnapshot?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await _runStore.GetAsync(runId, cancellationToken);
        return run is null ? null : CustomLoopRuntimeFacade.Map(run);
    }

    public async Task<IReadOnlyList<LoopRunSummarySnapshot>> ListRecentAsync(int maximumCount = CustomLoopLimits.MaxRecentRunsPageSize, CancellationToken cancellationToken = default)
    {
        var summaries = await _runStore.ListRecentAsync(maximumCount, cancellationToken);
        return summaries.Select(CustomLoopRuntimeFacade.Map).ToArray();
    }

    public async Task<LoopTraceInspectionSnapshot?> GetTraceAsync(string runId, CancellationToken cancellationToken = default)
    {
        var trace = await _runStore.InspectTraceAsync(runId, cancellationToken);
        return trace is null ? null : Map(trace);
    }

    public async Task<LoopTraceQuotaSnapshot> GetTraceQuotaAsync(CancellationToken cancellationToken = default)
    {
        var quota = await _runStore.GetTraceQuotaAsync(cancellationToken);
        return new LoopTraceQuotaSnapshot(
            quota.RetainedTraceCount,
            quota.TombstoneCount,
            quota.ActualTraceUtf8Bytes,
            quota.TombstoneUtf8Bytes,
            quota.ActualStoredUtf8Bytes,
            quota.ActiveReservationCount,
            quota.ReservedCapacityUtf8Bytes,
            quota.AccountedTraceUtf8Bytes,
            quota.AvailableAccountedUtf8Bytes,
            quota.MaximumTraceCount,
            quota.MaximumTombstoneCount,
            quota.MaximumWorkspaceUtf8Bytes,
            quota.MaximumPerTraceUtf8Bytes,
            quota.IsOverLimit);
    }

    public async Task<LoopTraceDeletionResponse> DeleteTraceAsync(string runId, string expectedTraceHash, string operationId, CancellationToken cancellationToken = default)
    {
        if (_retention is null || _actor is null || _surface is null)
        {
            throw new InvalidOperationException("This read-only facade was not constructed with an authenticated trace-management identity.");
        }

        var result = await _retention.DeleteAsync(new CustomLoopTraceDeletionRequest(runId, expectedTraceHash, operationId, _actor, _surface), cancellationToken);
        return new LoopTraceDeletionResponse(result.Status.ToString(), result.IsCommitted, result.Detail, result.Tombstone is null ? null : Map(result.Tombstone));
    }

    private static LoopTraceInspectionSnapshot Map(CustomLoopTraceInspection trace)
    {
        return new LoopTraceInspectionSnapshot(
            trace.Kind.ToString(),
            trace.RunId,
            trace.LoopId,
            trace.TerminalStatus.ToString(),
            trace.DefinitionVersion,
            trace.DefinitionHash,
            trace.PersistedArtifactHash,
            trace.PersistedArtifactUtf8Bytes,
            trace.OriginalTraceHash,
            trace.OriginalTraceUtf8Bytes,
            trace.CreatedAtUtc,
            trace.CompletedAtUtc,
            trace.IsDeleted,
            trace.Tombstone is null ? null : Map(trace.Tombstone));
    }

    private static LoopTraceTombstoneSnapshot Map(CustomLoopTraceTombstone tombstone)
    {
        return new LoopTraceTombstoneSnapshot(
            tombstone.RunId,
            tombstone.LoopId,
            tombstone.AdmissionOperationId,
            tombstone.TerminalStatus.ToString(),
            tombstone.DefinitionVersion,
            tombstone.DefinitionHash,
            tombstone.OriginalTraceHash,
            tombstone.OriginalTraceUtf8Bytes,
            tombstone.CreatedAtUtc,
            tombstone.CompletedAtUtc,
            tombstone.DeletedAtUtc,
            tombstone.DeletionActor,
            tombstone.DeletionSurface,
            tombstone.DeletionOperationId,
            tombstone.IntentAuditCorrelationId,
            tombstone.OutcomeAuditCorrelationId,
            tombstone.OutcomeIntegrity.ToString());
    }
}

public sealed record LoopTraceInspectionSnapshot(
    string Kind,
    string RunId,
    string LoopId,
    string Status,
    int DefinitionVersion,
    string DefinitionHash,
    string PersistedArtifactHash,
    long PersistedArtifactUtf8Bytes,
    string OriginalTraceHash,
    long OriginalTraceUtf8Bytes,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    bool IsDeleted,
    LoopTraceTombstoneSnapshot? Tombstone);

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

public sealed record LoopTraceQuotaSnapshot(
    int LiveTraceCount,
    int TombstoneCount,
    long LiveTraceUtf8Bytes,
    long TombstoneUtf8Bytes,
    long ActualStoredUtf8Bytes,
    int ActiveReservationCount,
    long ReservedCapacityUtf8Bytes,
    long AccountedUtf8Bytes,
    long AvailableAccountedUtf8Bytes,
    int MaximumLiveTraceCount,
    int MaximumTombstoneCount,
    long MaximumWorkspaceUtf8Bytes,
    int MaximumPerTraceUtf8Bytes,
    bool IsOverLimit);

public sealed record LoopTraceDeletionResponse(string Status, bool IsCommitted, string Detail, LoopTraceTombstoneSnapshot? Tombstone);
