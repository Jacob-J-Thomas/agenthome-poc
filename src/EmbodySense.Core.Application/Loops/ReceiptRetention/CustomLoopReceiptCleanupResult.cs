using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Represents the visible result of one governed bounded receipt cleanup request.
/// </summary>
/// <param name="Status">The cleanup status.</param>
/// <param name="Journal">The durable cleanup journal when one was reserved.</param>
/// <param name="ExhaustionReason">The capacity exhaustion reason when applicable.</param>
/// <param name="BlockReason">The fail-closed block reason when applicable.</param>
/// <param name="CompactedArtifactCount">The raw artifact count replaced by compact proof.</param>
/// <param name="CompactedArtifactUtf8Bytes">The raw artifact bytes replaced by compact proof.</param>
/// <param name="Detail">A bounded actionable result detail.</param>
public sealed record CustomLoopReceiptCleanupResult(
    CustomLoopReceiptCleanupStatus Status,
    CustomLoopReceiptCleanupJournal? Journal,
    CustomLoopReceiptQuotaExhaustionReason ExhaustionReason,
    CustomLoopReceiptCleanupBlockReason BlockReason,
    int CompactedArtifactCount,
    long CompactedArtifactUtf8Bytes,
    string Detail)
{
    /// <summary>
    /// Gets a value indicating whether cleanup replaced raw evidence or replayed that committed replacement.
    /// </summary>
    /// <value><see langword="true"/> for a committed or replayed cleanup result.</value>
    public bool IsCommitted => Status is CustomLoopReceiptCleanupStatus.Pruned
        or CustomLoopReceiptCleanupStatus.Replayed
        or CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning;
}
