using EmbodySense.Core.Application.Loops.ReceiptRetention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

public sealed record CustomLoopInvocationReceiptRetentionResult(
    CustomLoopInvocationReceiptRetentionStatus Status,
    int DeletedReceiptCount,
    long DeletedReceiptUtf8Bytes,
    string Detail)
{
    public bool AllowsReceiptWrite => Status is CustomLoopInvocationReceiptRetentionStatus.Pruned
        or CustomLoopInvocationReceiptRetentionStatus.Replayed
        or CustomLoopInvocationReceiptRetentionStatus.CommittedWithAuditWarning;
}
