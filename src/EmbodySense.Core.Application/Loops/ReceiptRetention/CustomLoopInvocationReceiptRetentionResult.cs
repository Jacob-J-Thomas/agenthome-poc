using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Represents a custom loop invocation receipt retention result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="DeletedReceiptCount">The deleted receipt count.</param>
/// <param name="DeletedReceiptUtf8Bytes">The deleted receipt UTF-8 bytes.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopInvocationReceiptRetentionResult(
    CustomLoopInvocationReceiptRetentionStatus Status,
    int DeletedReceiptCount,
    long DeletedReceiptUtf8Bytes,
    string Detail)
{
    /// <summary>
    /// Gets a value indicating whether the allows receipt write condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the allows receipt write condition holds; otherwise, <see langword="false"/>.</value>
    public bool AllowsReceiptWrite => Status is CustomLoopInvocationReceiptRetentionStatus.Pruned
        or CustomLoopInvocationReceiptRetentionStatus.Replayed
        or CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning;
}
