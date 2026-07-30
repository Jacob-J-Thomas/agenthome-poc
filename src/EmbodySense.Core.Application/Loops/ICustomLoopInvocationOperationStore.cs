using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists idempotent invocation receipts and their post-completion retention/audit state machine.
/// </summary>
public interface ICustomLoopInvocationOperationStore
{
    /// <summary>
    /// Atomically reserves an invocation operation or replays its request-bound receipt.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The reservation, replay, or request-conflict result.</returns>
    Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds a reserved invocation operation to the admitted run identifier.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The binding result, including idempotent replay or state conflict.</returns>
    Task<CustomLoopInvocationOperationStoreResult> BindAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads an invocation operation by its idempotency identifier.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The operation, or <see langword="null"/> when it is unknown.</returns>
    Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically records the terminal invocation receipt.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The completion result, including idempotent replay or state conflict.</returns>
    Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reserves the bounded retention operation for a completed invocation receipt.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The reservation, replay, conflict, or capacity result.</returns>
    Task<CustomLoopInvocationReceiptRetentionReservationResult> ReserveCompletedReceiptRetentionAsync(CustomLoopInvocationReceiptRetentionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the receipt-retention intent audit as durable.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionIntentAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits removal of the completed receipt selected by the retention reservation.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> CommitCompletedReceiptRetentionAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks that terminal retention-outcome audit emission has started.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the terminal retention outcome as durably audited.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the terminal retention outcome could not be fully audited.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks that conflict-audit emission has started for a retention reservation.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the retention conflict as durably audited.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a retention conflict could not be fully audited.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="updatedAtUtc">The updated at UTC.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop invocation receipt retention operation.</returns>
    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionConflictAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
