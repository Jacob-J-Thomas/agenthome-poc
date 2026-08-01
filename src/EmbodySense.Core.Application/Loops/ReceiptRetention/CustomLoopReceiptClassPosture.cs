using System.Collections.Immutable;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Represents accounted usage, replay horizon, capacity, and cleanup posture for one receipt artifact class.
/// </summary>
/// <param name="ArtifactClass">The artifact class.</param>
/// <param name="Budget">The immutable class budget.</param>
/// <param name="Categories">The complete per-category usage.</param>
/// <param name="OldestExactReplayExpiresAtUtc">The oldest retained exact receipt expiry timestamp.</param>
/// <param name="NewestExactReplayExpiresAtUtc">The newest retained exact receipt expiry timestamp.</param>
/// <param name="CompletedCleanupOperationCount">The completed cleanup identities retained after active-journal rotation.</param>
/// <param name="CompletedCleanupHistoryUtf8Bytes">The canonical terminal cleanup-journal bytes retained after active-journal rotation.</param>
/// <param name="ExhaustionReason">The actionable capacity exhaustion reason.</param>
/// <param name="CleanupBlockReason">The actionable cleanup block reason.</param>
/// <param name="Detail">A bounded human-readable posture detail.</param>
public sealed record CustomLoopReceiptClassPosture(
    CustomLoopReceiptArtifactClass ArtifactClass,
    CustomLoopReceiptRetentionBudget Budget,
    ImmutableArray<CustomLoopReceiptCategoryUsage> Categories,
    DateTimeOffset? OldestExactReplayExpiresAtUtc,
    DateTimeOffset? NewestExactReplayExpiresAtUtc,
    int CompletedCleanupOperationCount,
    long CompletedCleanupHistoryUtf8Bytes,
    CustomLoopReceiptQuotaExhaustionReason ExhaustionReason,
    CustomLoopReceiptCleanupBlockReason CleanupBlockReason,
    string Detail)
{
    /// <summary>
    /// Gets the accounted raw artifact count.
    /// </summary>
    /// <value>The raw artifact count excluding compact proof entries.</value>
    public int ArtifactCount => Categories.Where(item => !IsProof(item.Category)).Sum(item => item.ArtifactCount);

    /// <summary>
    /// Gets the accounted raw artifact bytes.
    /// </summary>
    /// <value>The raw artifact bytes excluding compact proof entries.</value>
    public long ArtifactUtf8Bytes => Categories.Where(item => !IsProof(item.Category)).Sum(item => item.Utf8Bytes);

    /// <summary>
    /// Gets the compact proof entry count.
    /// </summary>
    /// <value>The retained-lineage and expired-idempotency entry count.</value>
    public int ProofCount => Categories.Where(item => IsProof(item.Category)).Sum(item => item.ArtifactCount);

    /// <summary>
    /// Gets the compact proof bytes.
    /// </summary>
    /// <value>The retained-lineage and expired-idempotency bytes.</value>
    public long ProofUtf8Bytes => Categories.Where(item => IsProof(item.Category)).Sum(item => item.Utf8Bytes);

    /// <summary>
    /// Gets the total accounted class bytes.
    /// </summary>
    /// <value>The raw artifact, compact proof, and completed cleanup-history bytes.</value>
    public long AccountedUtf8Bytes => checked(ArtifactUtf8Bytes + ProofUtf8Bytes + CompletedCleanupHistoryUtf8Bytes);

    /// <summary>
    /// Gets a value indicating whether an explicit capacity boundary is exhausted.
    /// </summary>
    /// <value><see langword="true"/> when the exhaustion reason is not none.</value>
    public bool IsExhausted => ExhaustionReason != CustomLoopReceiptQuotaExhaustionReason.None;

    /// <summary>
    /// Gets a value indicating whether cleanup is explicitly blocked.
    /// </summary>
    /// <value><see langword="true"/> when the block reason is not none.</value>
    public bool IsCleanupBlocked => CleanupBlockReason != CustomLoopReceiptCleanupBlockReason.None;

    private static bool IsProof(CustomLoopReceiptArtifactCategory category)
    {
        return category is CustomLoopReceiptArtifactCategory.RetainedLineage or CustomLoopReceiptArtifactCategory.ExpiredIdempotency;
    }
}
