using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class LoopRunInspectionFacade : IAsyncDisposable
{
    private readonly WorkspacePaths _paths;
    private readonly CustomLoopRunStore _runStore;
    private readonly CustomLoopRecoveryService? _recovery;
    private readonly CustomLoopTraceRetentionService? _retention;
    private readonly string? _actor;
    private readonly string? _surface;
    private CustomLoopWorkspaceExecutionGate? _executionGate;
    private int _disposed;

    public LoopRunInspectionFacade(string workingDirectory, string? authenticatedActor = null, string? authenticatedSurface = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(authenticatedActor) != string.IsNullOrWhiteSpace(authenticatedSurface))
        {
            throw new ArgumentException("Authenticated trace management requires both a server-owned actor and surface.");
        }

        _paths = new WorkspacePaths(workingDirectory);
        _runStore = new CustomLoopRunStore(_paths);
        _actor = authenticatedActor;
        _surface = authenticatedSurface;
        var audit = authenticatedActor is null ? null : new AuditLog(_paths);
        _recovery = audit is null ? null : new CustomLoopRecoveryService(_runStore, audit);
        _retention = audit is null ? null : new CustomLoopTraceRetentionService(_runStore, audit);
    }

    public async Task<LoopRunRecoverySnapshot> RecoverInterruptedRunsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_recovery is null || _actor is null)
        {
            throw new InvalidOperationException("This read-only facade was not constructed with an authenticated recovery identity.");
        }

        _executionGate ??= new CustomLoopWorkspaceExecutionGate(_paths);
        var ownership = _executionGate.TryAcquire($"inspection-recovery-{Guid.NewGuid():N}", new string('0', CustomLoopLimits.Sha256HexCharacters));
        if (ownership.Status is CustomLoopExecutionLeaseStatus.WorkspaceBusy or CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable)
        {
            return new LoopRunRecoverySnapshot(false, false);
        }

        if (ownership.Status != CustomLoopExecutionLeaseStatus.Acquired || ownership.Lease is null)
        {
            throw new InvalidOperationException($"custom_loop_recovery_unavailable: recovery ownership returned {ownership.Status}.");
        }

        using (ownership.Lease)
        {
            var results = await _recovery.RecoverAsync(_actor, cancellationToken);
            if (results.Any(result => result.Status is CustomLoopRecoveryStatus.Conflict or CustomLoopRecoveryStatus.Failed))
            {
                throw new InvalidOperationException("custom_loop_recovery_failed: one or more interrupted runs could not be parked safely.");
            }

            return new LoopRunRecoverySnapshot(true, results.Any(result => CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(result.Run)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _executionGate is not null)
        {
            await _executionGate.DisposeAsync();
        }
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
