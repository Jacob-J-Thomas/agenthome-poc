using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists custom-loop lifecycle state and event traces and exposes optional retention, capacity, and deletion-receipt capabilities.
/// </summary>
/// <remarks>
/// Core run methods are required. Compatibility implementations for advanced monitoring, paging, dispatch revalidation, and
/// trace-retention members provide conservative or unsupported results; adapters that advertise those facilities must override them.
/// </remarks>
public interface ICustomLoopRunStore
{
    /// <summary>
    /// Creates a run and its initial lifecycle trace atomically.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The created run or an identifier/admission conflict.</returns>
    Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates one authenticated schedule-derived run under the same mutation ownership that enforces one nonterminal run per loop.
    /// </summary>
    /// <remarks>
    /// The conservative compatibility implementation reports unavailable. Schedule execution must never fall back to ordinary
    /// creation because doing so would discard the directive's overlap policy at the atomic boundary.
    /// </remarks>
    /// <param name="run">The complete admitted run with lifecycle version one.</param>
    /// <param name="envelope">The exact authenticated canonical schedule delivery retained by the invocation snapshot.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The created/replayed run or one durable policy-specific overlap disposition.</returns>
    Task<ScheduleRunAdmissionStoreResult> CreateScheduledAsync(CustomLoopRunRecord run, TriggerDeliveryEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ScheduleRunAdmissionStoreResult(ScheduleRunAdmissionStoreStatus.Unavailable, null, null));
    }

    /// <summary>Loads the durable atomic admission evidence for one exact schedule delivery.</summary>
    /// <param name="deliveryId">The deterministic schedule delivery identity.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The exact evidence, or null when the occurrence has not reached atomic run admission.</returns>
    Task<ScheduleRunAdmissionEvidence?> GetScheduleAdmissionAsync(TriggerDeliveryId deliveryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ScheduleRunAdmissionEvidence?>(null);
    }

    /// <summary>
    /// Loads the full durable run record.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The run, or <see langword="null"/> when it does not exist.</returns>
    Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a read-oriented run summary.
    /// </summary>
    /// <remarks>
    /// The compatibility implementation projects <see cref="GetAsync(string, CancellationToken)"/>, always reports the run as
    /// non-deleted, and supplies an empty artifact hash. Adapters that expose deletion state or artifact identity must override this member.
    /// </remarks>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The monitor view, or <see langword="null"/> when the run is unknown.</returns>
    async Task<CustomLoopRunMonitor?> GetMonitorAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = await GetAsync(runId, cancellationToken);
        return run is null
            ? null
            : new CustomLoopRunMonitor(new CustomLoopRunSummary(run.Id, run.LoopId, run.AdmissionOperationId, run.AdmittedDefinition.DefinitionVersion, run.LifecycleVersion, run.Status, run.CreatedAtUtc, run.UpdatedAtUtc, run.CompletedAtUtc, run.Checkpoint.Iteration, run.Checkpoint.NextStepIndex, run.FailureCode, IsDeleted: false), string.Empty);
    }

    /// <summary>
    /// Finds the run bound to an idempotent admission operation.
    /// </summary>
    /// <param name="admissionOperationId">The admission operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The bound run, or <see langword="null"/> when no binding exists.</returns>
    Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the active nonterminal run for a loop definition.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The nonterminal run, or <see langword="null"/> when the loop is idle.</returns>
    Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the most recently updated run summaries.
    /// </summary>
    /// <param name="maximumCount">The maximum count.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop run summaries.</returns>
    Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a page of run summaries.
    /// </summary>
    /// <remarks>
    /// The compatibility implementation supports only an unfiltered first page backed by
    /// <see cref="ListRecentAsync(int, CancellationToken)"/>. Adapters that support loop filters or continuation cursors must override this member.
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop run page.</returns>
    /// <exception cref="NotSupportedException">
    /// The compatibility implementation received a loop filter or continuation cursor.
    /// </exception>
    async Task<CustomLoopRunPage> ListPageAsync(CustomLoopRunPageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LoopId is not null || request.Cursor is not null)
        {
            throw new NotSupportedException("This custom-loop run store does not support filtered cursor pagination.");
        }

        return new CustomLoopRunPage(await ListRecentAsync(request.MaximumCount, cancellationToken), null);
    }

    /// <summary>
    /// Lists all runs that may require execution or recovery.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop run records.</returns>
    Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Allows a store to revalidate trace capacity and lifecycle version immediately before provider dispatch.
    /// </summary>
    /// <remarks>
    /// The compatibility implementation only observes cancellation and returns <see langword="true"/>.
    /// Stores that enforce trace capacity or lifecycle concurrency at dispatch time must override this member.
    /// </remarks>
    /// <param name="candidate">The candidate.</param>
    /// <param name="expectedLifecycleVersion">The expected lifecycle version.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns><see langword="true"/> when the candidate can still admit the next trace event; otherwise, <see langword="false"/>.</returns>
    Task<bool> HasSufficientTraceCapacityForDispatchAsync(CustomLoopRunRecord candidate, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Allows a store to read current retained-trace usage and configured capacity.
    /// </summary>
    /// <remarks>The compatibility implementation returns an empty quota.</remarks>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace quota.</returns>
    Task<CustomLoopTraceQuota> GetTraceQuotaAsync(CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceQuota.Empty());

    /// <summary>
    /// Allows a store to inspect trace integrity and artifact sizes for one run.
    /// </summary>
    /// <remarks>The compatibility implementation returns <see langword="null"/>.</remarks>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The inspection, or <see langword="null"/> when the run is unknown.</returns>
    Task<CustomLoopTraceInspection?> InspectTraceAsync(string runId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopTraceInspection?>(null);

    /// <summary>
    /// Allows a store to load a durable trace-deletion receipt by its idempotency identifier.
    /// </summary>
    /// <remarks>The compatibility implementation reports the receipt as not found.</remarks>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace deletion lookup result.</returns>
    Task<CustomLoopTraceDeletionLookupResult> GetTraceDeletionOperationAsync(string operationId, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceDeletionLookupResult.NotFound());

    /// <summary>
    /// Allows a store to atomically reserve an idempotent terminal-trace deletion operation.
    /// </summary>
    /// <remarks>The compatibility implementation reports that the deletion-operation limit is exceeded and persists no receipt.</remarks>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace deletion reservation result.</returns>
    Task<CustomLoopTraceDeletionReservationResult> ReserveTraceDeletionOperationAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default) => Task.FromResult(new CustomLoopTraceDeletionReservationResult(CustomLoopTraceDeletionReservationStatus.DeletionOperationLimitExceeded, null));

    /// <summary>
    /// Allows a store to commit a terminal deletion result that failed before destructive actuation because intent audit failed.
    /// </summary>
    /// <remarks>The compatibility implementation returns an unknown result and persists no mutation.</remarks>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace deletion store result.</returns>
    Task<CustomLoopTraceDeletionStoreResult> CommitTraceDeletionAuditFailureAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default) => Task.FromResult(new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.Unknown, null, CustomLoopTraceDeletionIntegrity.Unknown));

    /// <summary>
    /// Allows a store to delete artifacts for an eligible terminal run and commit its tombstone atomically.
    /// </summary>
    /// <remarks>The compatibility implementation reports the run as not found and deletes nothing.</remarks>
    /// <param name="mutation">The mutation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace deletion store result.</returns>
    Task<CustomLoopTraceDeletionStoreResult> DeleteTerminalTraceAsync(CustomLoopTraceDeletionMutation mutation, CancellationToken cancellationToken = default) => Task.FromResult(new CustomLoopTraceDeletionStoreResult(CustomLoopTraceDeletionStoreStatus.NotFound, null, CustomLoopTraceDeletionIntegrity.Unknown));

    /// <summary>
    /// Allows a store to mark the trace-deletion terminal outcome as audited with its integrity disposition.
    /// </summary>
    /// <remarks>The compatibility implementation reports the operation as not found and persists no marker.</remarks>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="integrity">The integrity.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop trace deletion audit mark status.</returns>
    Task<CustomLoopTraceDeletionAuditMarkStatus> MarkTraceDeletionOutcomeAsync(string operationId, CustomLoopTraceDeletionIntegrity integrity, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopTraceDeletionAuditMarkStatus.NotFound);

    /// <summary>
    /// Allows a store to append a warning to a terminal run under optimistic lifecycle concurrency.
    /// </summary>
    /// <remarks>The compatibility implementation reports the run as not found and persists no warning.</remarks>
    /// <param name="runId">The run ID.</param>
    /// <param name="expectedLifecycleVersion">The expected lifecycle version.</param>
    /// <param name="warning">The warning.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop run store result.</returns>
    Task<CustomLoopRunStoreResult> AppendTerminalIntegrityWarningAsync(string runId, int expectedLifecycleVersion, CustomLoopRunEvent warning, CancellationToken cancellationToken = default) => Task.FromResult(CustomLoopRunStoreResult.NotFound());

    /// <summary>
    /// Replaces a run and appends its new events only when the lifecycle version matches.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="expectedLifecycleVersion">The expected lifecycle version.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The persisted run or an optimistic-concurrency/integrity conflict.</returns>
    Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default);
}
