namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects one bounded custom-loop receipt artifact class without exposing persistence contracts.
/// </summary>
/// <param name="ArtifactClass">The receipt artifact class name.</param>
/// <param name="Health">The actionable class health.</param>
/// <param name="ArtifactCount">The accounted raw receipt count.</param>
/// <param name="ArtifactUtf8Bytes">The accounted raw receipt bytes.</param>
/// <param name="MaximumArtifactCount">The raw receipt count ceiling.</param>
/// <param name="MaximumArtifactUtf8Bytes">The raw receipt byte ceiling.</param>
/// <param name="ReservedArtifactCount">The count reserved for integrity-preserving completion.</param>
/// <param name="ReservedArtifactUtf8Bytes">The bytes reserved for integrity-preserving completion.</param>
/// <param name="ProofCount">The compact proof count.</param>
/// <param name="ProofUtf8Bytes">The compact proof bytes.</param>
/// <param name="MaximumProofCount">The compact proof count ceiling.</param>
/// <param name="MaximumProofUtf8Bytes">The compact proof byte ceiling.</param>
/// <param name="ActiveCleanupJournalUtf8Bytes">The bytes retained by this class's current cleanup journal.</param>
/// <param name="CleanupRecoveryAvailableAtUtc">The earliest safe explicit recovery retry, when an active journal owns the cleanup window.</param>
/// <param name="CompletedCleanupOperationCount">The retained terminal cleanup-operation count.</param>
/// <param name="CompletedCleanupHistoryUtf8Bytes">The retained terminal cleanup-history bytes.</param>
/// <param name="OldestExactReplayExpiresAtUtc">The earliest exact-replay expiry, when raw exact evidence exists.</param>
/// <param name="NewestExactReplayExpiresAtUtc">The latest exact-replay expiry, when raw exact evidence exists.</param>
/// <param name="ExhaustionReason">The capacity reason, or <c>None</c>.</param>
/// <param name="CleanupBlockReason">The cleanup block reason, or <c>None</c>.</param>
/// <param name="Categories">The complete safe category accounting set.</param>
/// <param name="Detail">A bounded actionable posture detail.</param>
public sealed record LoopReceiptRetentionClassSnapshot(
    string ArtifactClass,
    LoopReceiptRetentionHealth Health,
    int ArtifactCount,
    long ArtifactUtf8Bytes,
    int MaximumArtifactCount,
    long MaximumArtifactUtf8Bytes,
    int ReservedArtifactCount,
    long ReservedArtifactUtf8Bytes,
    int ProofCount,
    long ProofUtf8Bytes,
    int MaximumProofCount,
    long MaximumProofUtf8Bytes,
    long ActiveCleanupJournalUtf8Bytes,
    DateTimeOffset? CleanupRecoveryAvailableAtUtc,
    int CompletedCleanupOperationCount,
    long CompletedCleanupHistoryUtf8Bytes,
    DateTimeOffset? OldestExactReplayExpiresAtUtc,
    DateTimeOffset? NewestExactReplayExpiresAtUtc,
    string ExhaustionReason,
    string CleanupBlockReason,
    IReadOnlyList<LoopReceiptCategoryUsageSnapshot> Categories,
    string Detail);
