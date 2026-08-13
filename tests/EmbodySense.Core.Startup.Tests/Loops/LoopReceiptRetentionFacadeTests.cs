using System.Collections.Immutable;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

[Collection(SharedDefaultCapabilityTrustCollection.Name)]
public sealed class LoopReceiptRetentionFacadeTests
{
    public static TheoryData<string, LoopReceiptRetentionHealth> BlockReasonCases => new()
    {
        { "None", LoopReceiptRetentionHealth.Healthy },
        { "PendingEvidence", LoopReceiptRetentionHealth.Degraded },
        { "UnauditedEvidence", LoopReceiptRetentionHealth.Degraded },
        { "DegradedEvidence", LoopReceiptRetentionHealth.Degraded },
        { "CorruptEvidence", LoopReceiptRetentionHealth.Corrupt },
        { "OwnershipUnresolved", LoopReceiptRetentionHealth.RecoveryPending },
        { "AmbiguousEvidence", LoopReceiptRetentionHealth.Degraded },
        { "AuditUnavailable", LoopReceiptRetentionHealth.AuditUnavailable },
        { "CleanupConflict", LoopReceiptRetentionHealth.Degraded },
        { "ProofCapacityExhausted", LoopReceiptRetentionHealth.Exhausted },
        { "CleanupHistoryCapacityExhausted", LoopReceiptRetentionHealth.Exhausted }
    };

    public static TheoryData<string, LoopReceiptRetentionHealth> ExhaustionReasonCases => new()
    {
        { "None", LoopReceiptRetentionHealth.Healthy },
        { "ArtifactCountLimit", LoopReceiptRetentionHealth.Exhausted },
        { "ArtifactByteLimit", LoopReceiptRetentionHealth.Exhausted },
        { "ReservedArtifactCountLimit", LoopReceiptRetentionHealth.Exhausted },
        { "ReservedArtifactByteLimit", LoopReceiptRetentionHealth.Exhausted },
        { "ProofCountLimit", LoopReceiptRetentionHealth.Exhausted },
        { "ProofByteLimit", LoopReceiptRetentionHealth.Exhausted },
        { "CleanupHistoryCountLimit", LoopReceiptRetentionHealth.Exhausted },
        { "CleanupHistoryByteLimit", LoopReceiptRetentionHealth.Exhausted },
        { "WorkspaceByteLimit", LoopReceiptRetentionHealth.Exhausted }
    };

    public static TheoryData<string, string, string, LoopReceiptRetentionHealth> HealthCases => new()
    {
        { "Pruned", "None", "None", LoopReceiptRetentionHealth.Healthy },
        { "QuotaExhausted", "ArtifactCountLimit", "None", LoopReceiptRetentionHealth.Exhausted },
        { "Corrupt", "None", "CorruptEvidence", LoopReceiptRetentionHealth.Corrupt },
        { "AuditUnavailable", "None", "AuditUnavailable", LoopReceiptRetentionHealth.AuditUnavailable },
        { "OperationInProgress", "None", "OwnershipUnresolved", LoopReceiptRetentionHealth.OwnershipConflict },
        { "Degraded", "None", "PendingEvidence", LoopReceiptRetentionHealth.Degraded },
        { "Pruned", "None", "OwnershipUnresolved", LoopReceiptRetentionHealth.RecoveryPending }
    };

    [Fact]
    public async Task Posture_projects_all_classes_workspace_accounting_and_server_owned_bounded_cleanup()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        var facade = new LoopReceiptRetentionFacade(workspace.RootPath);

        var posture = await facade.GetPostureAsync();
        var invalidClass = await facade.CleanupAsync(new LoopReceiptCleanupInput("unknown", "retention-invalid-class", 64, 4 * 1024 * 1024));
        var numericClass = await facade.CleanupAsync(new LoopReceiptCleanupInput("1", "retention-numeric-class", 64, 4 * 1024 * 1024));
        var invalidBound = await facade.CleanupAsync(new LoopReceiptCleanupInput("LifecycleControlReceipt", "retention-invalid-bound", 65, 4 * 1024 * 1024));

        Assert.Equal(LoopReceiptRetentionHealth.Healthy, posture.Health);
        Assert.Equal(3, posture.Classes.Count);
        Assert.Equal(posture.MaximumWorkspaceUtf8Bytes - posture.AccountedWorkspaceUtf8Bytes, posture.AvailableWorkspaceUtf8Bytes);
        Assert.Equal(posture.AccountedWorkspaceUtf8Bytes, posture.Classes.Sum(item => item.ArtifactUtf8Bytes + item.ProofUtf8Bytes + item.CompletedCleanupHistoryUtf8Bytes) + posture.ActiveCleanupJournalUtf8Bytes);
        Assert.Equal(posture.ActiveCleanupJournalUtf8Bytes, posture.Classes.Sum(item => item.ActiveCleanupJournalUtf8Bytes));
        Assert.All(posture.Classes, item =>
        {
            Assert.True(item.MaximumArtifactCount > 0);
            Assert.True(item.MaximumArtifactUtf8Bytes > 0);
            Assert.True(item.ReservedArtifactCount > 0);
            Assert.True(item.ReservedArtifactUtf8Bytes > 0);
            Assert.True(item.MaximumProofCount > 0);
            Assert.True(item.MaximumProofUtf8Bytes > 0);
            Assert.Null(item.CleanupRecoveryAvailableAtUtc);
            Assert.NotEmpty(item.Categories);
        });
        Assert.Equal("Invalid", invalidClass.Status);
        Assert.Equal("Invalid", numericClass.Status);
        Assert.Equal("Invalid", invalidBound.Status);
    }

    [Fact]
    public async Task Cleanup_projects_a_bounded_interface_detail_without_persistence_paths_or_operation_identities()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var requestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        const string OperationId = "retention-safe-detail";
        var request = new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            OperationId,
            WorkspaceActors.Web,
            AgentRuntimeSurface.Web.Id,
            requestedAtUtc,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(requestedAtUtc),
            64,
            4 * 1024 * 1024);
        var unsafeDetail = $"Persistence path C:\\private\\receipt.json and operation {OperationId}: {new string('x', 4_096)}";
        var journal = new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-safe-detail",
            Environment.ProcessId,
            requestedAtUtc,
            CustomLoopReceiptCleanupStage.Completed,
            CustomLoopReceiptCleanupOutcome.NothingEligible,
            requestedAtUtc,
            ImmutableArray<CustomLoopReceiptCleanupCandidate>.Empty,
            null,
            0,
            0,
            unsafeDetail);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath, CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));

        var response = await new LoopReceiptRetentionFacade(workspace.RootPath).CleanupAsync(new LoopReceiptCleanupInput(nameof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt), OperationId, 64, 4 * 1024 * 1024));

        Assert.Equal(nameof(CustomLoopReceiptCleanupStatus.NothingEligible), response.Status);
        Assert.Equal("No eligible expired receipt evidence was available for cleanup.", response.Detail);
        Assert.DoesNotContain("private", response.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OperationId, response.Detail, StringComparison.Ordinal);
        Assert.True(response.Detail.Length < 256);
    }

    [Fact]
    public async Task Posture_exposes_only_the_safe_retry_deadline_for_an_active_cleanup_journal()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var ownershipAcquiredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5);
        var request = new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
            "retention-recovery-posture",
            "embodysense.web",
            "web",
            ownershipAcquiredAtUtc,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(ownershipAcquiredAtUtc),
            64,
            4 * 1024 * 1024);
        var journal = new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            "cleanup-owner-recovery-posture",
            Environment.ProcessId,
            ownershipAcquiredAtUtc,
            CustomLoopReceiptCleanupStage.IntentPersisted,
            CustomLoopReceiptCleanupOutcome.Unknown,
            ownershipAcquiredAtUtc,
            ImmutableArray<CustomLoopReceiptCleanupCandidate>.Empty,
            null,
            0,
            0,
            "A prior cleanup owner stopped after persisting its bounded intent.");
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllBytesAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath, CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));

        var posture = await new LoopReceiptRetentionFacade(workspace.RootPath).GetPostureAsync();
        var item = Assert.Single(posture.Classes, item => item.ArtifactClass == nameof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt));

        Assert.Equal(LoopReceiptRetentionHealth.RecoveryPending, item.Health);
        Assert.Equal(nameof(CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved), item.CleanupBlockReason);
        Assert.Equal(ownershipAcquiredAtUtc + CustomLoopReceiptRetentionPolicy.CleanupOwnershipWindow, item.CleanupRecoveryAvailableAtUtc);
        Assert.True(item.ActiveCleanupJournalUtf8Bytes > 0);
    }

    [Theory]
    [MemberData(nameof(BlockReasonCases))]
    public void Posture_health_maps_every_cleanup_block_reason(string blockReason, LoopReceiptRetentionHealth expected)
    {
        Assert.Equal(expected, LoopReceiptRetentionHealthProjection.FromPosture(nameof(CustomLoopReceiptQuotaExhaustionReason.None), blockReason));
        Assert.Equal(Enum.GetNames<CustomLoopReceiptCleanupBlockReason>().Length, BlockReasonCases.Count);
    }

    [Theory]
    [MemberData(nameof(ExhaustionReasonCases))]
    public void Posture_health_maps_every_exhaustion_reason(string exhaustionReason, LoopReceiptRetentionHealth expected)
    {
        Assert.Equal(expected, LoopReceiptRetentionHealthProjection.FromPosture(exhaustionReason, nameof(CustomLoopReceiptCleanupBlockReason.None)));
        Assert.Equal(Enum.GetNames<CustomLoopReceiptQuotaExhaustionReason>().Length, ExhaustionReasonCases.Count);
    }

    [Theory]
    [MemberData(nameof(HealthCases))]
    public void Cleanup_and_posture_projection_reach_every_safe_health(string status, string exhaustionReason, string blockReason, LoopReceiptRetentionHealth expected)
    {
        Assert.Equal(expected, LoopReceiptRetentionHealthProjection.FromCleanup(status, exhaustionReason, blockReason));
        Assert.Equal(Enum.GetNames<LoopReceiptRetentionHealth>().Length, HealthCases.Count);
        Assert.Contains(status, Enum.GetNames<CustomLoopReceiptCleanupStatus>());
    }

    [Fact]
    public void Workspace_cleanup_block_reason_tracks_most_severe_class_and_breaks_ties_deterministically()
    {
        var pending = CreateClassPosture("DefinitionMutationReceipt", LoopReceiptRetentionHealth.Degraded, "PendingEvidence");
        var ambiguous = CreateClassPosture("DefinitionTombstone", LoopReceiptRetentionHealth.Degraded, "AmbiguousEvidence");
        var conflict = CreateClassPosture("LifecycleControlReceipt", LoopReceiptRetentionHealth.Degraded, "CleanupConflict");
        var corrupt = CreateClassPosture("DefinitionTombstone", LoopReceiptRetentionHealth.Corrupt, "CorruptEvidence");

        Assert.Equal("CorruptEvidence", LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason([pending, corrupt]));
        Assert.Equal("CorruptEvidence", LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason([corrupt, pending]));
        Assert.Equal("CleanupConflict", LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason([ambiguous, conflict]));
        Assert.Equal("CleanupConflict", LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason([conflict, ambiguous]));
        Assert.Equal("None", LoopReceiptRetentionHealthProjection.SelectWorkspaceCleanupBlockReason([]));
    }

    private static LoopReceiptRetentionClassSnapshot CreateClassPosture(string artifactClass, LoopReceiptRetentionHealth health, string blockReason)
    {
        return new LoopReceiptRetentionClassSnapshot(
            artifactClass,
            health,
            0,
            0,
            1,
            1,
            1,
            1,
            0,
            0,
            1,
            1,
            0,
            null,
            0,
            0,
            null,
            null,
            "None",
            blockReason,
            [],
            "Test posture.");
    }
}
