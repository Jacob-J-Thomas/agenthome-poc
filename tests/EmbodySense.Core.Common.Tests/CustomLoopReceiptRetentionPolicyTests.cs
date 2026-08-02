using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Common.Tests;

public sealed class CustomLoopReceiptRetentionPolicyTests
{
    private static readonly DateTimeOffset _now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt, 10_000, 134_217_728, 64, 41_943_040, 100_000, 33_554_432)]
    [InlineData(CustomLoopReceiptArtifactClass.DefinitionTombstone, 10_000, 67_108_864, 64, 1_048_576, 10_000, 16_777_216)]
    [InlineData(CustomLoopReceiptArtifactClass.LifecycleControlReceipt, 20_000, 134_217_728, 128, 8_388_608, 100_000, 33_554_432)]
    public void GetBudget_exposes_explicit_class_and_reserved_capacity_boundaries(CustomLoopReceiptArtifactClass artifactClass, int count, long bytes, int reservedCount, long reservedBytes, int proofCount, long proofBytes)
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(artifactClass);

        Assert.Equal(count, budget.MaximumArtifactCount);
        Assert.Equal(bytes, budget.MaximumArtifactUtf8Bytes);
        Assert.Equal(reservedCount, budget.ReservedPendingCompletionCount);
        Assert.Equal(reservedBytes, budget.ReservedPendingCompletionUtf8Bytes);
        Assert.Equal(count - reservedCount, budget.NormalAdmissionArtifactCount);
        Assert.Equal(bytes - reservedBytes, budget.NormalAdmissionArtifactUtf8Bytes);
        Assert.Equal(proofCount, budget.MaximumProofCount);
        Assert.Equal(proofBytes, budget.MaximumProofUtf8Bytes);
    }

    [Fact]
    public void CanAccountArtifacts_protects_reserved_capacity_for_pending_completion()
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt);

        Assert.True(budget.CanAccountArtifacts(budget.NormalAdmissionArtifactCount - 1, budget.NormalAdmissionArtifactUtf8Bytes - 1, 1, 1, integrityPreservingCompletion: false));
        Assert.False(budget.CanAccountArtifacts(budget.NormalAdmissionArtifactCount, budget.NormalAdmissionArtifactUtf8Bytes, 1, 1, integrityPreservingCompletion: false));
        Assert.True(budget.CanAccountArtifacts(budget.NormalAdmissionArtifactCount, budget.NormalAdmissionArtifactUtf8Bytes, budget.ReservedPendingCompletionCount, budget.ReservedPendingCompletionUtf8Bytes, integrityPreservingCompletion: true));
        Assert.False(budget.CanAccountArtifacts(budget.MaximumArtifactCount, budget.MaximumArtifactUtf8Bytes, 1, 1, integrityPreservingCompletion: true));
        Assert.False(budget.CanAccountArtifacts(budget.MaximumArtifactCount + 1, budget.MaximumArtifactUtf8Bytes + 1, 0, 0, integrityPreservingCompletion: true));
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void CanAccountArtifacts_rejects_negative_accounting(int currentCount, long currentBytes, int addedCount, long addedBytes)
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.DefinitionTombstone);

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.CanAccountArtifacts(currentCount, currentBytes, addedCount, addedBytes, integrityPreservingCompletion: false));
    }

    [Fact]
    public void CanAccountProof_fails_closed_at_count_and_byte_boundaries()
    {
        var budget = CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.LifecycleControlReceipt);

        Assert.True(budget.CanAccountProof(budget.MaximumProofCount - 1, budget.MaximumProofUtf8Bytes - 1, 1, 1));
        Assert.False(budget.CanAccountProof(budget.MaximumProofCount, budget.MaximumProofUtf8Bytes, 1, 1));
        Assert.False(budget.CanAccountProof(budget.MaximumProofCount + 1, budget.MaximumProofUtf8Bytes + 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.CanAccountProof(0, 0, -1, 0));
    }

    [Fact]
    public void ExactReplayDuration_has_inclusive_expiry_and_deterministic_cutoff()
    {
        var completedAtUtc = _now - CustomLoopReceiptRetentionPolicy.ExactReplayDuration;

        Assert.False(CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(completedAtUtc, _now.AddTicks(-1)));
        Assert.True(CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(completedAtUtc, _now));
        Assert.True(CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(completedAtUtc, _now.AddTicks(1)));
        Assert.False(CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(_now.AddMinutes(1), _now));
        Assert.Equal(completedAtUtc, CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(_now));
        Assert.Equal(TimeSpan.FromDays(30), CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
    }

    [Fact]
    public void Replay_policy_rejects_non_utc_or_default_timestamps()
    {
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(default, _now));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionPolicy.IsExactReplayExpired(_now, _now.ToOffset(TimeSpan.FromHours(1))));
        Assert.Throws<ArgumentException>(() => CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(default));
    }

    [Fact]
    public void Only_explicitly_compactable_evidence_is_safely_prunable()
    {
        foreach (var category in Enum.GetValues<CustomLoopReceiptArtifactCategory>())
        {
            Assert.Equal(category == CustomLoopReceiptArtifactCategory.Compactable, CustomLoopReceiptRetentionPolicy.IsSafelyPrunable(category));
        }
    }

    [Fact]
    public void Policy_bounds_workspace_and_rejects_unknown_artifact_class()
    {
        var expected = CustomLoopReceiptRetentionPolicy.MaxDefinitionMutationReceiptUtf8Bytes
            + CustomLoopReceiptRetentionPolicy.MaxDefinitionTombstoneUtf8Bytes
            + CustomLoopReceiptRetentionPolicy.MaxLifecycleControlReceiptUtf8Bytes
            + CustomLoopReceiptRetentionPolicy.MaxProofLedgerUtf8Bytes
            + (3 * CustomLoopReceiptRetentionPolicy.MaxCleanupJournalUtf8Bytes);

        Assert.Equal(CustomLoopReceiptRetentionPolicy.MaxAccountedWorkspaceUtf8Bytes, expected);
        Assert.Equal(424L * 1024 * 1024, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() => CustomLoopReceiptRetentionPolicy.GetBudget(CustomLoopReceiptArtifactClass.Unknown));
    }
}
