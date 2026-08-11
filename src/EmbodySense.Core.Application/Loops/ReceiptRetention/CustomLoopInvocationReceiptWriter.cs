using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>Writes invocation receipts through the exact operation store with governed capacity recovery.</summary>
public sealed class CustomLoopInvocationReceiptWriter
{
    private readonly ICustomLoopInvocationOperationStore _store;
    private readonly CustomLoopInvocationReceiptRetentionService _retention;

    /// <summary>Creates a receipt writer over one exact store and its governed retention policy.</summary>
    public CustomLoopInvocationReceiptWriter(
        ICustomLoopInvocationOperationStore store,
        CustomLoopInvocationReceiptRetentionService retention)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _retention = retention ?? throw new ArgumentNullException(nameof(retention));
    }

    /// <summary>Begins one invocation receipt with governed capacity recovery and at most one exact retry.</summary>
    public Task<CustomLoopInvocationOperationStoreResult> BeginAsync(
        CustomLoopInvocationOperation operation,
        CancellationToken cancellationToken = default)
        => WriteWithRetentionAsync(operation, _store.BeginAsync, cancellationToken);

    /// <summary>Binds one invocation receipt with governed capacity recovery and at most one exact retry.</summary>
    public Task<CustomLoopInvocationOperationStoreResult> BindAsync(
        CustomLoopInvocationOperation operation,
        CancellationToken cancellationToken = default)
        => WriteWithRetentionAsync(operation, _store.BindAsync, cancellationToken);

    /// <summary>Completes one invocation receipt with governed capacity recovery and at most one exact retry.</summary>
    public Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(
        CustomLoopInvocationOperation operation,
        CancellationToken cancellationToken = default)
        => WriteWithRetentionAsync(operation, _store.CompleteAsync, cancellationToken);

    private async Task<CustomLoopInvocationOperationStoreResult> WriteWithRetentionAsync(
        CustomLoopInvocationOperation operation,
        Func<CustomLoopInvocationOperation, CancellationToken, Task<CustomLoopInvocationOperationStoreResult>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = await write(operation, cancellationToken).ConfigureAwait(false);
        if (result.Status is not (CustomLoopInvocationOperationStoreStatus.LimitExceeded or CustomLoopInvocationOperationStoreStatus.RetentionRequired))
        {
            return result;
        }

        // Capacity is reclaimed only through governed retention. Retry the exact typed operation once
        // after a safe retention outcome so no caller can bypass replay or audit guarantees.
        var retention = await _retention.PruneForCapacityAsync(operation.Actor, operation.Surface, cancellationToken).ConfigureAwait(false);
        if (!retention.AllowsReceiptWrite)
        {
            var status = retention.Status switch
            {
                CustomLoopInvocationReceiptRetentionStatus.OperationInProgress => CustomLoopInvocationOperationStoreStatus.RetentionRequired,
                CustomLoopInvocationReceiptRetentionStatus.AuditUnavailable => CustomLoopInvocationOperationStoreStatus.RetentionAuditUnavailable,
                CustomLoopInvocationReceiptRetentionStatus.Invalid => CustomLoopInvocationOperationStoreStatus.RetentionInvalid,
                _ => CustomLoopInvocationOperationStoreStatus.LimitExceeded,
            };
            return new CustomLoopInvocationOperationStoreResult(status, result.Operation);
        }

        return await write(operation, cancellationToken).ConfigureAwait(false);
    }
}
