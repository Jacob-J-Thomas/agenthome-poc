using EmbodySense.Core.Application.Loops.ReceiptRetention;

namespace EmbodySense.Core.Application.Loops;

public interface ICustomLoopInvocationOperationStore
{
    Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperationStoreResult> BindAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationReceiptRetentionReservationResult> ReserveCompletedReceiptRetentionAsync(CustomLoopInvocationReceiptRetentionRequest request, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionIntentAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationReceiptRetentionOperation> CommitCompletedReceiptRetentionAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditStartedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditedAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationReceiptRetentionOperation> MarkReceiptRetentionOutcomeAuditWarningAsync(string operationId, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken = default);
}
