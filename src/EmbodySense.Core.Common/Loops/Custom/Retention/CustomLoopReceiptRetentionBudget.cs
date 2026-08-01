using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Common.Loops.Custom.Retention;

/// <summary>
/// Defines the raw-artifact, reserved-completion, and compact-proof budget for one receipt class.
/// </summary>
/// <param name="ArtifactClass">The artifact class.</param>
/// <param name="MaximumArtifactCount">The maximum retained raw artifact count.</param>
/// <param name="MaximumArtifactUtf8Bytes">The maximum aggregate retained raw artifact bytes.</param>
/// <param name="ReservedPendingCompletionCount">The count reserved exclusively for completing already-pending work.</param>
/// <param name="ReservedPendingCompletionUtf8Bytes">The bytes reserved exclusively for completing already-pending work.</param>
/// <param name="MaximumProofCount">The maximum compact proof entries attributed to the class.</param>
/// <param name="MaximumProofUtf8Bytes">The maximum compact proof bytes attributed to the class.</param>
public sealed record CustomLoopReceiptRetentionBudget(
    CustomLoopReceiptArtifactClass ArtifactClass,
    int MaximumArtifactCount,
    long MaximumArtifactUtf8Bytes,
    int ReservedPendingCompletionCount,
    long ReservedPendingCompletionUtf8Bytes,
    int MaximumProofCount,
    long MaximumProofUtf8Bytes)
{
    /// <summary>
    /// Gets the artifact count ceiling available to new operations.
    /// </summary>
    /// <value>The normal-admission artifact count ceiling.</value>
    public int NormalAdmissionArtifactCount => MaximumArtifactCount - ReservedPendingCompletionCount;

    /// <summary>
    /// Gets the artifact byte ceiling available to new operations.
    /// </summary>
    /// <value>The normal-admission artifact byte ceiling.</value>
    public long NormalAdmissionArtifactUtf8Bytes => MaximumArtifactUtf8Bytes - ReservedPendingCompletionUtf8Bytes;

    /// <summary>
    /// Determines whether adding raw artifacts remains inside the normal or integrity-preserving completion boundary.
    /// </summary>
    /// <param name="currentCount">The currently accounted artifact count.</param>
    /// <param name="currentUtf8Bytes">The currently accounted artifact bytes.</param>
    /// <param name="addedCount">The artifact count to reserve.</param>
    /// <param name="addedUtf8Bytes">The artifact bytes to reserve.</param>
    /// <param name="integrityPreservingCompletion">Whether the write completes already-pending work and may use reserved capacity.</param>
    /// <returns><see langword="true"/> when the requested accounting remains inside the applicable ceilings.</returns>
    public bool CanAccountArtifacts(int currentCount, long currentUtf8Bytes, int addedCount, long addedUtf8Bytes, bool integrityPreservingCompletion)
    {
        return GetArtifactExhaustionReason(currentCount, currentUtf8Bytes, addedCount, addedUtf8Bytes, integrityPreservingCompletion) == CustomLoopReceiptQuotaExhaustionReason.None;
    }

    /// <summary>
    /// Identifies the exact raw-artifact boundary that would prevent one accounting change.
    /// </summary>
    /// <param name="currentCount">The currently accounted artifact count.</param>
    /// <param name="currentUtf8Bytes">The currently accounted artifact bytes.</param>
    /// <param name="addedCount">The artifact count to reserve.</param>
    /// <param name="addedUtf8Bytes">The artifact bytes to reserve.</param>
    /// <param name="integrityPreservingCompletion">Whether the write completes already-pending work and may use reserved capacity.</param>
    /// <returns>The absolute or reserved-capacity boundary preventing the change, or <see cref="CustomLoopReceiptQuotaExhaustionReason.None"/>.</returns>
    public CustomLoopReceiptQuotaExhaustionReason GetArtifactExhaustionReason(int currentCount, long currentUtf8Bytes, int addedCount, long addedUtf8Bytes, bool integrityPreservingCompletion)
    {
        if (currentCount < 0 || currentUtf8Bytes < 0 || addedCount < 0 || addedUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentCount), "Receipt accounting values cannot be negative.");
        }

        if (currentCount > MaximumArtifactCount || addedCount > MaximumArtifactCount - currentCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ArtifactCountLimit;
        }

        if (currentUtf8Bytes > MaximumArtifactUtf8Bytes || addedUtf8Bytes > MaximumArtifactUtf8Bytes - currentUtf8Bytes)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ArtifactByteLimit;
        }

        if (integrityPreservingCompletion)
        {
            return CustomLoopReceiptQuotaExhaustionReason.None;
        }

        if (currentCount > NormalAdmissionArtifactCount || addedCount > NormalAdmissionArtifactCount - currentCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ReservedArtifactCountLimit;
        }

        return currentUtf8Bytes > NormalAdmissionArtifactUtf8Bytes || addedUtf8Bytes > NormalAdmissionArtifactUtf8Bytes - currentUtf8Bytes
            ? CustomLoopReceiptQuotaExhaustionReason.ReservedArtifactByteLimit
            : CustomLoopReceiptQuotaExhaustionReason.None;
    }

    /// <summary>
    /// Determines whether adding compact proof remains inside the class proof boundaries.
    /// </summary>
    /// <param name="currentCount">The currently accounted proof entry count.</param>
    /// <param name="currentUtf8Bytes">The currently accounted proof bytes.</param>
    /// <param name="addedCount">The proof entry count to add.</param>
    /// <param name="addedUtf8Bytes">The proof bytes to add.</param>
    /// <returns><see langword="true"/> when every required proof entry fits without forgetting older evidence.</returns>
    public bool CanAccountProof(int currentCount, long currentUtf8Bytes, int addedCount, long addedUtf8Bytes)
    {
        return GetProofExhaustionReason(currentCount, currentUtf8Bytes, addedCount, addedUtf8Bytes) == CustomLoopReceiptQuotaExhaustionReason.None;
    }

    /// <summary>
    /// Identifies the exact compact-proof boundary that would prevent one accounting change.
    /// </summary>
    /// <param name="currentCount">The currently accounted proof entry count.</param>
    /// <param name="currentUtf8Bytes">The currently accounted proof bytes.</param>
    /// <param name="addedCount">The proof entry count to reserve.</param>
    /// <param name="addedUtf8Bytes">The proof bytes to reserve.</param>
    /// <returns>The compact-proof boundary preventing the change, or <see cref="CustomLoopReceiptQuotaExhaustionReason.None"/>.</returns>
    public CustomLoopReceiptQuotaExhaustionReason GetProofExhaustionReason(int currentCount, long currentUtf8Bytes, int addedCount, long addedUtf8Bytes)
    {
        if (currentCount < 0 || currentUtf8Bytes < 0 || addedCount < 0 || addedUtf8Bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(currentCount), "Compact proof accounting values cannot be negative.");
        }

        if (currentCount > MaximumProofCount || addedCount > MaximumProofCount - currentCount)
        {
            return CustomLoopReceiptQuotaExhaustionReason.ProofCountLimit;
        }

        return currentUtf8Bytes > MaximumProofUtf8Bytes || addedUtf8Bytes > MaximumProofUtf8Bytes - currentUtf8Bytes
            ? CustomLoopReceiptQuotaExhaustionReason.ProofByteLimit
            : CustomLoopReceiptQuotaExhaustionReason.None;
    }
}
