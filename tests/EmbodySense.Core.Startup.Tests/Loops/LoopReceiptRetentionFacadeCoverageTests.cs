using System.Collections.Immutable;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Governance.Audit;
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
public sealed class LoopReceiptRetentionFacadeCoverageTests
{
    [Fact]
    public async Task Cleanup_projects_each_durable_terminal_status_with_a_bounded_safe_detail()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var facade = new LoopReceiptRetentionFacade(workspace.RootPath);
        var cases = new[]
        {
            (CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptCleanupOutcome.Succeeded, CustomLoopReceiptCleanupStatus.Replayed, "prior terminal cleanup outcome was replayed"),
            (CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptCleanupOutcome.Unknown, CustomLoopReceiptCleanupStatus.OperationInProgress, "cleanup owner is inside"),
            (CustomLoopReceiptCleanupStage.CommittedWithAuditWarning, CustomLoopReceiptCleanupOutcome.AuditUnavailable, CustomLoopReceiptCleanupStatus.CommittedWithAuditWarning, "terminal audit outcome requires review"),
            (CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptCleanupOutcome.Conflict, CustomLoopReceiptCleanupStatus.CleanupConflict, "ambiguous evidence was preserved"),
            (CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.AuditUnavailable, CustomLoopReceiptCleanupStatus.AuditUnavailable, "required audit durability"),
            (CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, CustomLoopReceiptCleanupStatus.Corrupt, "could not be validated safely"),
            (CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, CustomLoopReceiptCleanupStatus.Degraded, "requires operator review")
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var (stage, outcome, expectedStatus, expectedDetail) = cases[index];
            var operationId = $"retention-status-{index}";
            var request = CreateRequest(operationId, AuditSchema.Actors.Web, AgentRuntimeSurface.Web.Id);
            var candidate = stage is CustomLoopReceiptCleanupStage.Completed or CustomLoopReceiptCleanupStage.CommittedWithAuditWarning ? CreateCandidate(request) : null;
            var journal = CreateJournal(request, stage, outcome, candidate is null ? [] : [candidate], candidate is null ? null : new string('b', 64), candidate is null ? 0 : 1, candidate is null ? 0 : candidate.ArtifactUtf8Bytes);
            await WriteJournalAsync(paths, journal);

            var response = await facade.CleanupAsync(new LoopReceiptCleanupInput(nameof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt), operationId, 64, 4 * 1024 * 1024));

            Assert.Equal(expectedStatus.ToString(), response.Status);
            Assert.Contains(expectedDetail, response.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.True(response.Detail.Length < 256);
            Assert.DoesNotContain(operationId, response.Detail, StringComparison.Ordinal);
        }

        var invalidOperationId = "retention-status-invalid";
        var mismatchedRequest = CreateRequest(invalidOperationId, "different.server.actor", AgentRuntimeSurface.Web.Id);
        await WriteJournalAsync(paths, CreateJournal(mismatchedRequest, CustomLoopReceiptCleanupStage.Completed, CustomLoopReceiptCleanupOutcome.NothingEligible, [], null, 0, 0));
        var invalid = await facade.CleanupAsync(new LoopReceiptCleanupInput(nameof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt), invalidOperationId, 64, 4 * 1024 * 1024));

        Assert.Equal(nameof(CustomLoopReceiptCleanupStatus.Invalid), invalid.Status);
        Assert.Contains("cleanup request or durable cleanup journal is invalid", invalid.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Posture_maps_corrupt_cleanup_journal_as_workspace_corruption_and_keeps_detail_bounded()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
        await File.WriteAllTextAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath, "{\"schemaVersion\":1,\"unexpected\":true}");

        var posture = await new LoopReceiptRetentionFacade(workspace.RootPath).GetPostureAsync();
        var mutation = Assert.Single(posture.Classes, item => item.ArtifactClass == nameof(CustomLoopReceiptArtifactClass.DefinitionMutationReceipt));

        Assert.Equal(LoopReceiptRetentionHealth.Corrupt, posture.Health);
        Assert.Equal(nameof(CustomLoopReceiptCleanupBlockReason.CorruptEvidence), mutation.CleanupBlockReason);
        Assert.Equal(LoopReceiptRetentionHealth.Corrupt, mutation.Health);
        Assert.Contains("could not be validated safely", mutation.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be validated safely", posture.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(posture.Detail.Length < 256);
    }

    [Fact]
    public async Task Posture_preserves_each_actionable_active_journal_health_and_block_reason()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var facade = new LoopReceiptRetentionFacade(workspace.RootPath);
        var cases = new[]
        {
            (CustomLoopReceiptCleanupStage.IntentPersisted, CustomLoopReceiptCleanupOutcome.Unknown, LoopReceiptRetentionHealth.RecoveryPending, CustomLoopReceiptCleanupBlockReason.OwnershipUnresolved),
            (CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.AuditUnavailable, LoopReceiptRetentionHealth.AuditUnavailable, CustomLoopReceiptCleanupBlockReason.AuditUnavailable),
            (CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Corrupt, LoopReceiptRetentionHealth.Corrupt, CustomLoopReceiptCleanupBlockReason.CorruptEvidence),
            (CustomLoopReceiptCleanupStage.Degraded, CustomLoopReceiptCleanupOutcome.Degraded, LoopReceiptRetentionHealth.Degraded, CustomLoopReceiptCleanupBlockReason.DegradedEvidence),
            (CustomLoopReceiptCleanupStage.AbandonedConflict, CustomLoopReceiptCleanupOutcome.Conflict, LoopReceiptRetentionHealth.Degraded, CustomLoopReceiptCleanupBlockReason.CleanupConflict)
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var (stage, outcome, expectedHealth, expectedBlockReason) = cases[index];
            var request = CreateRequest($"retention-posture-{index}", AuditSchema.Actors.Web, AgentRuntimeSurface.Web.Id);
            await WriteJournalAsync(paths, CreateJournal(request, stage, outcome, [], null, 0, 0));

            var posture = await facade.GetPostureAsync();
            var lifecycle = Assert.Single(posture.Classes, item => item.ArtifactClass == nameof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt));

            Assert.Equal(expectedHealth, lifecycle.Health);
            Assert.Equal(expectedBlockReason.ToString(), lifecycle.CleanupBlockReason);
            Assert.Contains(stage.ToString(), lifecycle.Detail, StringComparison.Ordinal);
            Assert.Contains(outcome.ToString(), lifecycle.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Posture_fails_closed_when_the_authoring_root_cannot_be_opened_as_a_directory()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.Delete(paths.LoopDefinitionsPath, recursive: true);
        await File.WriteAllTextAsync(paths.LoopDefinitionsPath, "authoring root is unavailable");

        var posture = await new LoopReceiptRetentionFacade(workspace.RootPath).GetPostureAsync();

        Assert.Equal(LoopReceiptRetentionHealth.Corrupt, posture.Health);
        Assert.All(posture.Classes.Where(item => item.ArtifactClass is not nameof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt)), item =>
        {
            Assert.Equal(LoopReceiptRetentionHealth.Corrupt, item.Health);
            Assert.Equal(nameof(CustomLoopReceiptCleanupBlockReason.CorruptEvidence), item.CleanupBlockReason);
            Assert.Contains("journal accounting", item.Detail, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(LoopReceiptRetentionHealth.Healthy, Assert.Single(posture.Classes, item => item.ArtifactClass == nameof(CustomLoopReceiptArtifactClass.LifecycleControlReceipt)).Health);
        Assert.Contains("journal accounting", posture.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static CustomLoopReceiptCleanupRequest CreateRequest(string operationId, string actor, string surface)
    {
        var requestedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        return new CustomLoopReceiptCleanupRequest(
            CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt,
            operationId,
            actor,
            surface,
            requestedAtUtc,
            CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(requestedAtUtc),
            64,
            4 * 1024 * 1024);
    }

    private static CustomLoopReceiptCleanupCandidate CreateCandidate(CustomLoopReceiptCleanupRequest request)
    {
        var proof = new CustomLoopExpiredOperationProof(
            CustomLoopExpiredOperationProof.CurrentSchemaVersion,
            CustomLoopReceiptArtifactClass.LifecycleControlReceipt,
            null,
            null,
            null,
            "retention-replayed-candidate",
            new string('c', 64),
            new string('d', 64),
            request.ReplayCutoffUtc,
            request.ReplayCutoffUtc + CustomLoopReceiptRetentionPolicy.ExactReplayDuration);
        return new CustomLoopReceiptCleanupCandidate(
            proof.OperationId,
            new string('a', 64),
            1,
            CustomLoopReceiptArtifactCategory.Compactable,
            true,
            true,
            proof,
            null);
    }

    private static CustomLoopReceiptCleanupJournal CreateJournal(CustomLoopReceiptCleanupRequest request, CustomLoopReceiptCleanupStage stage, CustomLoopReceiptCleanupOutcome outcome, ImmutableArray<CustomLoopReceiptCleanupCandidate> candidates, string? proofLedgerHash, int removedArtifactCount, long removedArtifactUtf8Bytes)
    {
        var acquiredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        return new CustomLoopReceiptCleanupJournal(
            CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
            request,
            CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(request),
            $"cleanup-owner-{request.OperationId}",
            Environment.ProcessId,
            acquiredAtUtc,
            stage,
            outcome,
            acquiredAtUtc,
            candidates,
            proofLedgerHash,
            removedArtifactCount,
            removedArtifactUtf8Bytes,
            "Test cleanup status evidence remains bounded and safe.");
    }

    private static Task WriteJournalAsync(WorkspacePaths paths, CustomLoopReceiptCleanupJournal journal)
    {
        Directory.CreateDirectory(paths.CustomLoopControlReceiptCleanupPath);
        return File.WriteAllBytesAsync(
            Path.Combine(paths.CustomLoopControlReceiptCleanupPath, "active.json"),
            CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(journal));
    }
}
