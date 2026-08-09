using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Exposes durable custom-loop run, operation, monitor, trace, and quota evidence through Core.Startup.
/// </summary>
/// <remarks>
/// Supplying neither authenticated identity value creates a read-only facade. Supplying both enables
/// interrupted-run recovery and audited terminal trace deletion. Unsupported run-discovery schemas are
/// translated to <see cref="LoopRunEvidenceUnsupportedSchemaException"/> so interfaces can provide
/// explicit cleanup guidance. Dispose the facade to release its run store and optional execution gate.
/// </remarks>
public sealed class LoopRunInspectionFacade : IAsyncDisposable
{
    private readonly WorkspacePaths _paths;
    private readonly CustomLoopRunStore _runStore;
    private readonly CustomLoopInvocationOperationStore _invocationOperationStore;
    private readonly CustomLoopControlOperationStore _controlOperationStore;
    private readonly CustomLoopRecoveryService? _recovery;
    private readonly CustomLoopTraceRetentionService? _retention;
    private readonly string? _actor;
    private readonly string? _surface;
    private CustomLoopWorkspaceExecutionGate? _executionGate;
    private int _disposed;

    /// <summary>
    /// Creates a read-only or authenticated inspection facade for one workspace.
    /// </summary>
    /// <param name="workingDirectory">The workspace root, normalized to an absolute path.</param>
    /// <param name="authenticatedActor">The optional server-owned actor that authorizes recovery and owns deletion audit events. Recovery lifecycle events retain each run's admission actor.</param>
    /// <param name="authenticatedSurface">The optional server-owned interface surface paired with <paramref name="authenticatedActor"/>.</param>
    /// <remarks>Actor and surface must either both be supplied or both be omitted.</remarks>
    public LoopRunInspectionFacade(string workingDirectory, string? authenticatedActor = null, string? authenticatedSurface = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (string.IsNullOrWhiteSpace(authenticatedActor) != string.IsNullOrWhiteSpace(authenticatedSurface))
        {
            throw new ArgumentException("Authenticated trace management requires both a server-owned actor and surface.");
        }

        _paths = new WorkspacePaths(workingDirectory);
        _runStore = new CustomLoopRunStore(_paths);
        _invocationOperationStore = new CustomLoopInvocationOperationStore(_paths);
        _controlOperationStore = new CustomLoopControlOperationStore(_paths);
        _actor = authenticatedActor;
        _surface = authenticatedSurface;
        var audit = authenticatedActor is null ? null : new AuditLog(_paths);
        _recovery = audit is null ? null : new CustomLoopRecoveryService(_runStore, audit);
        _retention = audit is null ? null : new CustomLoopTraceRetentionService(_runStore, audit);
    }

    /// <summary>
    /// Attempts to acquire workspace execution ownership and safely parks interrupted custom runs.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel recovery and conversation snapshot reads.</param>
    /// <returns>
    /// A task whose result reports whether this process completed recovery and whether the invoking
    /// conversation must be rehydrated. A busy or unavailable workspace host returns
    /// <c>Completed = false</c> instead of waiting or stealing ownership.
    /// </returns>
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
            IReadOnlyList<CustomLoopRecoveryResult> results;
            try
            {
                results = await _recovery.RecoverAsync(_actor, cancellationToken);
            }
            catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
            {
                throw new LoopRunEvidenceUnsupportedSchemaException(exception);
            }

            if (results.Any(result => result.Status is CustomLoopRecoveryStatus.Conflict or CustomLoopRecoveryStatus.Failed))
            {
                throw new InvalidOperationException("custom_loop_recovery_failed: one or more interrupted runs could not be parked safely.");
            }

            if (results.Count == 0)
            {
                return new LoopRunRecoverySnapshot(true, false);
            }

            var currentConversation = await new ConversationMemoryStore(_paths).LoadCurrentConversationSnapshotAsync(cancellationToken);
            return new LoopRunRecoverySnapshot(true, results.Any(result => CustomLoopConversationRecoveryPolicy.RequiresCurrentConversation(result.Run, currentConversation.Version)));
        }
    }

    /// <summary>
    /// Idempotently disposes the run store and any workspace execution gate created for recovery.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _runStore.Dispose();
        if (_executionGate is not null) await _executionGate.DisposeAsync();
    }

    /// <summary>
    /// Reads the full durable projection of one run.
    /// </summary>
    /// <param name="runId">The durable run identifier.</param>
    /// <param name="cancellationToken">The token used to cancel evidence reads.</param>
    /// <returns>A task whose result is the run, or null when it is not retained.</returns>
    public async Task<LoopRunSnapshot?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        return await ReadEvidenceAsync(async () =>
        {
            var run = await _runStore.GetAsync(runId, cancellationToken);
            return run is null ? null : CustomLoopRuntimeFacade.Map(run);
        });
    }

    /// <summary>
    /// Reads the lightweight monitor projection and artifact validator for one run.
    /// </summary>
    /// <param name="runId">The durable run identifier.</param>
    /// <param name="cancellationToken">The token used to cancel evidence reads.</param>
    /// <returns>A task whose result is the monitor snapshot, or null when the run is not retained.</returns>
    public async Task<LoopRunMonitorSnapshot?> GetMonitorAsync(string runId, CancellationToken cancellationToken = default)
    {
        return await ReadEvidenceAsync(async () =>
        {
            var monitor = await _runStore.GetMonitorAsync(runId, cancellationToken);
            return monitor is null ? null : new LoopRunMonitorSnapshot(CustomLoopRuntimeFacade.Map(monitor.Summary), monitor.ArtifactHash);
        });
    }

    /// <summary>
    /// Reads the durable reconciliation receipt for an invocation operation identity.
    /// </summary>
    /// <param name="operationId">The invocation idempotency identity.</param>
    /// <param name="cancellationToken">The token used to cancel the receipt read.</param>
    /// <returns>A task whose result is the operation snapshot, or null when no receipt exists.</returns>
    public async Task<LoopInvocationOperationSnapshot?> GetInvocationOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var operation = await _invocationOperationStore.GetAsync(operationId, cancellationToken);
        return operation is null
            ? null
            : new LoopInvocationOperationSnapshot(
                operation.OperationId,
                operation.LoopId,
                operation.State.ToString(),
                operation.Outcome.ToString(),
                operation.AdmissionStatus,
                operation.RunId,
                operation.CreatedAtUtc,
                operation.UpdatedAtUtc,
                operation.Detail);
    }

    /// <summary>
    /// Reads the durable reconciliation receipt for a pause, cancel, or resume operation identity.
    /// </summary>
    /// <param name="operationId">The lifecycle-control idempotency identity.</param>
    /// <param name="cancellationToken">The token used to cancel the receipt read.</param>
    /// <returns>A task whose result is the operation snapshot, or null when no receipt exists.</returns>
    public async Task<LoopControlOperationSnapshot?> GetControlOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var operation = await _controlOperationStore.GetAsync(operationId, cancellationToken);
        return operation is null
            ? null
            : new LoopControlOperationSnapshot(
                operation.OperationId,
                operation.Kind.ToString(),
                operation.RunId,
                operation.ExpectedLifecycleVersion,
                operation.State.ToString(),
                operation.Outcome.ToString(),
                operation.ResultLifecycleVersion,
                operation.ResultRunStatus?.ToString(),
                operation.OutcomeAuditRecorded,
                operation.State == CustomLoopControlOperationState.Complete,
                operation.CreatedAtUtc,
                operation.UpdatedAtUtc);
    }

    /// <summary>
    /// Reads the newest retained run summaries across the workspace.
    /// </summary>
    /// <param name="maximumCount">The bounded maximum number of summaries to return.</param>
    /// <param name="cancellationToken">The token used to cancel evidence reads.</param>
    /// <returns>A task whose result contains the newest retained summaries in store order.</returns>
    public async Task<IReadOnlyList<LoopRunSummarySnapshot>> ListRecentAsync(int maximumCount = CustomLoopLimits.MaxRecentRunsPageSize, CancellationToken cancellationToken = default)
    {
        return await ReadEvidenceAsync(async () => (await _runStore.ListRecentAsync(maximumCount, cancellationToken)).Select(CustomLoopRuntimeFacade.Map).ToArray());
    }

    /// <summary>
    /// Reads one cursor page of retained run summaries, optionally restricted to a loop.
    /// </summary>
    /// <param name="maximumCount">The bounded maximum page size.</param>
    /// <param name="loopId">An optional exact loop identifier filter.</param>
    /// <param name="cursor">An opaque continuation cursor returned by a prior matching request.</param>
    /// <param name="cancellationToken">The token used to cancel evidence reads.</param>
    /// <returns>A task whose result contains the page and an optional continuation cursor.</returns>
    public async Task<LoopRunSummaryPageSnapshot> ListPageAsync(int maximumCount = CustomLoopLimits.MaxRecentRunsPageSize, string? loopId = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        return await ReadEvidenceAsync(async () =>
        {
            var page = await _runStore.ListPageAsync(new CustomLoopRunPageRequest(maximumCount, loopId, cursor), cancellationToken);
            return new LoopRunSummaryPageSnapshot(page.Items.Select(CustomLoopRuntimeFacade.Map).ToArray(), page.ContinuationCursor);
        });
    }

    /// <summary>
    /// Reads retained trace metadata or its deletion tombstone for one run.
    /// </summary>
    /// <param name="runId">The durable run identifier.</param>
    /// <param name="cancellationToken">The token used to cancel evidence reads.</param>
    /// <returns>A task whose result is retained trace evidence, a tombstone projection, or null when neither exists.</returns>
    public async Task<LoopTraceInspectionSnapshot?> GetTraceAsync(string runId, CancellationToken cancellationToken = default)
    {
        return await ReadEvidenceAsync(async () =>
        {
            var trace = await _runStore.InspectTraceAsync(runId, cancellationToken);
            return trace is null ? null : Map(trace);
        });
    }

    /// <summary>
    /// Reads actual, reserved, and bounded trace-retention accounting for the workspace.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel evidence reads.</param>
    /// <returns>A task whose result is the current trace quota snapshot.</returns>
    public async Task<LoopTraceQuotaSnapshot> GetTraceQuotaAsync(CancellationToken cancellationToken = default)
    {
        return await ReadEvidenceAsync(async () =>
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
                quota.DeletionOperationCount,
                quota.MaximumDeletionOperationCount,
                quota.IsOverLimit);
        });
    }

    /// <summary>
    /// Conditionally replaces one terminal retained trace with an auditable tombstone.
    /// </summary>
    /// <param name="runId">The terminal run whose retained trace is targeted.</param>
    /// <param name="expectedTraceHash">The retained trace hash required for optimistic concurrency.</param>
    /// <param name="operationId">The idempotency identity for this exact deletion request.</param>
    /// <param name="cancellationToken">The token used to cancel persistence and auditing.</param>
    /// <returns>
    /// A task whose result distinguishes committed deletion, committed outcome, rejection, replay,
    /// and audit-warning states and includes the retained tombstone when available.
    /// </returns>
    public async Task<LoopTraceDeletionResponse> DeleteTraceAsync(string runId, string expectedTraceHash, string operationId, CancellationToken cancellationToken = default)
    {
        if (_retention is null || _actor is null || _surface is null)
        {
            throw new InvalidOperationException("This read-only facade was not constructed with an authenticated trace-management identity.");
        }

        try
        {
            var result = await _retention.DeleteAsync(new CustomLoopTraceDeletionRequest(runId, expectedTraceHash, operationId, _actor, _surface), cancellationToken);
            return new LoopTraceDeletionResponse(result.Status.ToString(), result.IsCommitted, result.IsOutcomeCommitted, result.Detail, result.Tombstone is null ? null : Map(result.Tombstone));
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
        {
            throw new LoopRunEvidenceUnsupportedSchemaException(exception);
        }
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

    private static async Task<T> ReadEvidenceAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (UnsupportedCustomLoopRunDiscoveryIndexSchemaException exception)
        {
            throw new LoopRunEvidenceUnsupportedSchemaException(exception);
        }
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
