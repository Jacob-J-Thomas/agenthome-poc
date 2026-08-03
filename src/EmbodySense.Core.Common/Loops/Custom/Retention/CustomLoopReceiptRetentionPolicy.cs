using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Common.Loops.Custom.Retention;

/// <summary>
/// Defines shared replay, cleanup, and accounting policy for bounded authoring and lifecycle receipts.
/// </summary>
public static class CustomLoopReceiptRetentionPolicy
{
    /// <summary>
    /// Maximum definition mutation receipts per workspace.
    /// </summary>
    public const int MaxDefinitionMutationReceiptCount = 10_000;

    /// <summary>
    /// Maximum aggregate definition mutation receipt bytes per workspace.
    /// </summary>
    public const long MaxDefinitionMutationReceiptUtf8Bytes = 128L * 1024 * 1024;

    /// <summary>
    /// Definition mutation receipt slots reserved for completing pending mutations.
    /// </summary>
    public const int ReservedDefinitionMutationReceiptCount = 64;

    /// <summary>
    /// Definition mutation receipt bytes reserved for completing pending mutations.
    /// </summary>
    public const long ReservedDefinitionMutationReceiptUtf8Bytes = 40L * 1024 * 1024;

    /// <summary>
    /// Maximum definition tombstones per workspace.
    /// </summary>
    public const int MaxDefinitionTombstoneCount = 10_000;

    /// <summary>
    /// Maximum aggregate definition tombstone bytes per workspace.
    /// </summary>
    public const long MaxDefinitionTombstoneUtf8Bytes = 64L * 1024 * 1024;

    /// <summary>
    /// Definition tombstone slots reserved for completing pending deletes.
    /// </summary>
    public const int ReservedDefinitionTombstoneCount = 64;

    /// <summary>
    /// Definition tombstone bytes reserved for completing pending deletes.
    /// </summary>
    public const long ReservedDefinitionTombstoneUtf8Bytes = 1024L * 1024;

    /// <summary>
    /// Maximum lifecycle-control receipts per workspace.
    /// </summary>
    public const int MaxLifecycleControlReceiptCount = 20_000;

    /// <summary>
    /// Maximum aggregate lifecycle-control receipt bytes per workspace.
    /// </summary>
    public const long MaxLifecycleControlReceiptUtf8Bytes = 128L * 1024 * 1024;

    /// <summary>
    /// Lifecycle-control receipt slots reserved for completing pending controls.
    /// </summary>
    public const int ReservedLifecycleControlReceiptCount = 128;

    /// <summary>
    /// Lifecycle-control receipt bytes reserved for completing pending controls.
    /// </summary>
    public const long ReservedLifecycleControlReceiptUtf8Bytes = 8L * 1024 * 1024;

    /// <summary>
    /// Maximum compact expired-operation proofs for definition mutation receipts.
    /// </summary>
    public const int MaxDefinitionMutationProofCount = 100_000;

    /// <summary>
    /// Maximum compact proof bytes attributed to definition mutation receipts.
    /// </summary>
    public const long MaxDefinitionMutationProofUtf8Bytes = 32L * 1024 * 1024;

    /// <summary>
    /// Maximum compact definition-lineage proofs.
    /// </summary>
    public const int MaxDefinitionLineageProofCount = MaxDefinitionTombstoneCount;

    /// <summary>
    /// Maximum compact definition-lineage proof bytes.
    /// </summary>
    public const long MaxDefinitionLineageProofUtf8Bytes = 16L * 1024 * 1024;

    /// <summary>
    /// Maximum compact expired-operation proofs for lifecycle-control receipts.
    /// </summary>
    public const int MaxLifecycleControlProofCount = 100_000;

    /// <summary>
    /// Maximum compact proof bytes attributed to lifecycle-control receipts.
    /// </summary>
    public const long MaxLifecycleControlProofUtf8Bytes = 32L * 1024 * 1024;

    /// <summary>
    /// Maximum artifacts selected by one bounded cleanup operation.
    /// </summary>
    public const int MaxCleanupBatchArtifactCount = 64;

    /// <summary>
    /// Maximum raw artifact bytes selected by one bounded cleanup operation.
    /// </summary>
    public const long MaxCleanupBatchArtifactUtf8Bytes = 4L * 1024 * 1024;

    /// <summary>
    /// Maximum serialized compact proof-ledger bytes.
    /// </summary>
    public const long MaxProofLedgerUtf8Bytes = MaxDefinitionMutationProofUtf8Bytes + MaxDefinitionLineageProofUtf8Bytes + MaxLifecycleControlProofUtf8Bytes;

    /// <summary>
    /// Maximum serialized active cleanup-journal bytes per artifact class.
    /// </summary>
    public const long MaxCleanupJournalUtf8Bytes = 8L * 1024 * 1024;

    /// <summary>
    /// Maximum workspace-wide bytes accounted across raw artifacts, compact proof, and one active journal per class.
    /// </summary>
    public const long MaxAccountedWorkspaceUtf8Bytes = MaxDefinitionMutationReceiptUtf8Bytes + MaxDefinitionTombstoneUtf8Bytes + MaxLifecycleControlReceiptUtf8Bytes + MaxProofLedgerUtf8Bytes + (3 * MaxCleanupJournalUtf8Bytes);

    /// <summary>
    /// Gets the exact replay duration promised for completed receipts.
    /// </summary>
    /// <value>Thirty days from the terminal receipt timestamp.</value>
    public static TimeSpan ExactReplayDuration { get; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets the maximum cross-process cleanup ownership window.
    /// </summary>
    /// <value>Thirty seconds.</value>
    public static TimeSpan CleanupOwnershipWindow { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the budget for an artifact class.
    /// </summary>
    /// <param name="artifactClass">The artifact class.</param>
    /// <returns>The immutable class budget.</returns>
    public static CustomLoopReceiptRetentionBudget GetBudget(CustomLoopReceiptArtifactClass artifactClass)
    {
        return artifactClass switch
        {
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt => new(artifactClass, MaxDefinitionMutationReceiptCount, MaxDefinitionMutationReceiptUtf8Bytes, ReservedDefinitionMutationReceiptCount, ReservedDefinitionMutationReceiptUtf8Bytes, MaxDefinitionMutationProofCount, MaxDefinitionMutationProofUtf8Bytes),
            CustomLoopReceiptArtifactClass.DefinitionTombstone => new(artifactClass, MaxDefinitionTombstoneCount, MaxDefinitionTombstoneUtf8Bytes, ReservedDefinitionTombstoneCount, ReservedDefinitionTombstoneUtf8Bytes, MaxDefinitionLineageProofCount, MaxDefinitionLineageProofUtf8Bytes),
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt => new(artifactClass, MaxLifecycleControlReceiptCount, MaxLifecycleControlReceiptUtf8Bytes, ReservedLifecycleControlReceiptCount, ReservedLifecycleControlReceiptUtf8Bytes, MaxLifecycleControlProofCount, MaxLifecycleControlProofUtf8Bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(artifactClass), artifactClass, "A supported receipt artifact class is required.")
        };
    }

    /// <summary>
    /// Determines whether a terminal receipt is outside the exact replay horizon.
    /// </summary>
    /// <param name="completedAtUtc">The terminal receipt timestamp.</param>
    /// <param name="observedAtUtc">The current observation timestamp.</param>
    /// <returns><see langword="true"/> at or after the exact replay cutoff; otherwise, <see langword="false"/>.</returns>
    public static bool IsExactReplayExpired(DateTimeOffset completedAtUtc, DateTimeOffset observedAtUtc)
    {
        RequireUtc(completedAtUtc, nameof(completedAtUtc));
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return observedAtUtc >= completedAtUtc && observedAtUtc - completedAtUtc >= ExactReplayDuration;
    }

    /// <summary>
    /// Gets the inclusive terminal timestamp cutoff for exact replay expiry.
    /// </summary>
    /// <param name="observedAtUtc">The current observation timestamp.</param>
    /// <returns>Receipts completed at or before this timestamp are expired.</returns>
    public static DateTimeOffset GetReplayCutoffUtc(DateTimeOffset observedAtUtc)
    {
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return observedAtUtc - ExactReplayDuration;
    }

    /// <summary>
    /// Determines whether a posture category is safe to include in a cleanup batch.
    /// </summary>
    /// <param name="category">The classified category.</param>
    /// <returns><see langword="true"/> only for explicitly compactable evidence.</returns>
    public static bool IsSafelyPrunable(CustomLoopReceiptArtifactCategory category) => category == CustomLoopReceiptArtifactCategory.Compactable;

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Receipt retention timestamps must be non-default UTC values.", parameterName);
        }
    }
}
