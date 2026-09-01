using System.Text.Json;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Loops.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Tests.HumanReview;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Public_human_review_facade_preserves_opaque_cursor_and_skips_tombstones()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var created = Assert.IsType<LoopDefinitionSnapshot>((await runtime.LoopAuthoring.CreateAsync("create-public-human-review-cursor")).Definition);
        var completed = await runtime.InvokeCustomLoopAsync(new LoopRunInvocationInput(
            created.Id,
            created.DefinitionVersion,
            created.ContentHash,
            "invoke-public-human-review-tombstone",
            "create one terminal trace for the public tombstone journey"));
        Assert.Equal("Completed", completed.ExecutionStatus);
        var traceId = Assert.IsType<LoopRunSnapshot>(completed.Run).Id;

        await using (var inspection = new LoopRunInspectionFacade(workspace.RootPath, "actor-user", "web"))
        {
            var trace = await inspection.GetTraceAsync(traceId);
            Assert.NotNull(trace);
            var deleted = await inspection.DeleteTraceAsync(traceId, trace!.PersistedArtifactHash, "delete-public-human-review-tombstone");
            Assert.Equal("Deleted", deleted.Status);
        }

        var blueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-cursor");
        await PersistPendingHumanReviewAsync(workspace, blueprint);

        var first = await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1));
        Assert.Equal(HumanReviewPageStatus.Ready, first.Status);
        Assert.Empty(first.Items);
        Assert.NotNull(first.ContinuationCursor);

        var second = await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1, first.ContinuationCursor));
        Assert.Equal(HumanReviewPageStatus.Ready, second.Status);
        Assert.Equal(blueprint.Id, Assert.Single(second.Items).RunId);
        Assert.Null(second.ContinuationCursor);

        // Missing-live ambiguity from the old recording-store fixture is deliberately not simulated: the concrete durable store repairs absent canonical artifacts before the public facade receives a summary.
        var oversized = await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1, new string('x', CustomLoopLimits.MaxRunPageCursorCharacters + 1)));
        Assert.Equal(HumanReviewPageStatus.Invalid, oversized.Status);
    }

    [Fact]
    public async Task Public_human_review_facade_maps_malformed_canonical_page_to_unavailable()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-malformed-page");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        // The concrete public CustomLoopRunStore rejects malformed index data before returning a page, so the public facade's closed projection is Unavailable rather than an injected malformed item.
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(paths.CustomLoopRunsPath, ".custom-loop-run-index.json"), "{\"schemaVersion\":2,\"revision\":1,\"entries\":[]}");

        var page = await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1));

        Assert.Equal(HumanReviewPageStatus.Unavailable, page.Status);
        Assert.Empty(page.Items);
        Assert.Null(page.ContinuationCursor);
    }

    [Fact]
    public async Task Public_human_review_facade_maps_corrupt_and_unavailable_durable_reads_without_payloads()
    {
        // The concrete durable store strictly validates decoded runs; the direct-facade Corrupt result requires an injected malformed run, which the public factory intentionally does not permit. Persisted malformed or unavailable artifacts therefore map to Unavailable and are asserted below.
        using (var corruptWorkspace = new TestWorkspace())
        {
            await WorkspaceInitializer.ForFileCapabilityTrustRoot(corruptWorkspace.ServerStatePath).InitializeAsync(corruptWorkspace.RootPath);
            var corruptBlueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-corrupt-read");
            await PersistPendingHumanReviewAsync(corruptWorkspace, corruptBlueprint);
            await using var corruptRuntime = await CreateRuntimeAsync(corruptWorkspace, AgentRuntimeSurface.Web);
            var corruptPaths = new WorkspacePaths(corruptWorkspace.RootPath);
            var corruptPath = Path.Combine(corruptPaths.CustomLoopRunsPath, corruptBlueprint.LoopId, corruptBlueprint.Id + ".json");
            await File.WriteAllTextAsync(corruptPath, "{corrupt");

            var read = await corruptRuntime.HumanReview.ReadAsync(corruptBlueprint.Id);

            Assert.Equal(HumanReviewReadStatus.Unavailable, read.Status);
            Assert.Null(read.Detail);
        }

        using (var unavailableWorkspace = new TestWorkspace())
        {
            await WorkspaceInitializer.ForFileCapabilityTrustRoot(unavailableWorkspace.ServerStatePath).InitializeAsync(unavailableWorkspace.RootPath);
            var unavailableBlueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-unavailable-read");
            await PersistPendingHumanReviewAsync(unavailableWorkspace, unavailableBlueprint);
            await using var unavailableRuntime = await CreateRuntimeAsync(unavailableWorkspace, AgentRuntimeSurface.Web);
            var unavailablePaths = new WorkspacePaths(unavailableWorkspace.RootPath);
            var unavailablePath = Path.Combine(unavailablePaths.CustomLoopRunsPath, unavailableBlueprint.LoopId, unavailableBlueprint.Id + ".json");
            File.Delete(unavailablePath);
            Directory.CreateDirectory(unavailablePath);

            var read = await unavailableRuntime.HumanReview.ReadAsync(unavailableBlueprint.Id);

            Assert.Equal(HumanReviewReadStatus.Unavailable, read.Status);
            Assert.Null(read.Detail);
        }
    }

    [Fact]
    public async Task Public_human_review_facade_returns_null_payloads_for_missing_evidence_and_posture_reads()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var detail = await runtime.HumanReview.ReadAsync("run-public-human-review-missing");
        var evidence = await runtime.HumanReview.ReadEvidenceAsync("run-public-human-review-missing");
        var posture = await runtime.HumanReview.ReadRuntimePostureAsync("run-public-human-review-missing");

        Assert.Equal(HumanReviewReadStatus.NotFound, detail.Status);
        Assert.Null(detail.Detail);
        Assert.Equal(HumanReviewEvidenceReadStatus.NotFound, evidence.Status);
        Assert.Empty(evidence.Evidence);
        Assert.Null(evidence.EffectEvidence);
        Assert.Equal(HumanReviewReadStatus.NotFound, posture.Status);
        Assert.Null(posture.Posture);
    }

    [Fact]
    public async Task Public_human_review_facade_rejects_valid_run_malformed_decision_without_mutation_or_provider_call()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-malformed-decision");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var providerCalls = 0;
        var provider = new HumanReviewDelegateAuthorizationProvider(_ =>
        {
            providerCalls++;
            return null;
        });
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runtime = await AgentRuntimeFactory
            .ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(provider)
            .CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var before = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);
        var result = await runtime.HumanReview.DecideAsync(blueprint.Id, 0, "malformed-decision-operation", HumanReviewDecisionKind.Approve);
        var after = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);

        Assert.Equal(HumanReviewDecisionStatus.Invalid, result.Status);
        Assert.Null(result.Evidence);
        Assert.Equal(0, providerCalls);
        Assert.Equal(before.Summary.RunId, after.Summary.RunId);
        Assert.Equal(before.Summary.RequestId, after.Summary.RequestId);
        Assert.Equal(before.Summary.RequestHash, after.Summary.RequestHash);
        Assert.Equal(before.Summary.Purpose, after.Summary.Purpose);
        Assert.True(before.Summary.RequestedDecisions.SequenceEqual(after.Summary.RequestedDecisions));
        Assert.Equal(before.Summary.LifecycleStatus, after.Summary.LifecycleStatus);
        Assert.Equal(before.Summary.RunStatus, after.Summary.RunStatus);
        Assert.Equal(before.Summary.FrontierStatus, after.Summary.FrontierStatus);
        Assert.Equal(before.Summary.LifecycleVersion, after.Summary.LifecycleVersion);
        Assert.Equal(before.Summary.UpdatedAtUtc, after.Summary.UpdatedAtUtc);
        Assert.Equal(before.Summary.ExpiresAtUtc, after.Summary.ExpiresAtUtc);
        Assert.Equal(before.Decisions.Count, after.Decisions.Count);
        Assert.Equal(before.Evidence.Count, after.Evidence.Count);
        Assert.Equal(before.Runtime, after.Runtime);
    }

    [Fact]
    public async Task Public_human_review_facade_maps_reproducible_decision_limit_exceeded_without_extra_mutation()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-limit");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(CreateAllowingAuthorizationProvider());
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        HumanReviewDecisionResult? limit = null;
        for (var index = 0; index < HumanReviewContractLimits.MaxAcceptedDecisions; index++)
        {
            var detail = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);
            var result = await runtime.HumanReview.DecideAsync(
                blueprint.Id,
                checked((int)detail.Summary.LifecycleVersion),
                "information-limit-" + index,
                HumanReviewDecisionKind.RequestInformation,
                "bounded clarification");
            if (index == HumanReviewContractLimits.MaxAcceptedDecisions - 1)
            {
                limit = result;
            }
            else
            {
                Assert.Equal(HumanReviewDecisionStatus.InformationRequested, result.Status);
            }
        }

        Assert.NotNull(limit);
        Assert.Equal(HumanReviewDecisionStatus.LimitExceeded, limit.Status);
        Assert.Null(limit.Evidence);
        var finalDetail = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions - 1, finalDetail.Decisions.Count);
    }

    [Fact]
    public async Task Public_human_review_facade_projects_receipt_fields_and_detached_redacted_collections()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await CreateLivePendingBlueprintAsync("run-public-human-review-receipt");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithHumanReviewDecisionAuthorizationProvider(CreateAllowingAuthorizationProvider());
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var pending = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);
        var result = await runtime.HumanReview.DecideAsync(
            blueprint.Id,
            checked((int)pending.Summary.LifecycleVersion),
            "receipt-public-operation",
            HumanReviewDecisionKind.Approve);
        var evidence = result.Evidence;
        Assert.NotNull(evidence);
        var detail = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);
        var page = await runtime.HumanReview.ListAsync();

        Assert.Equal(HumanReviewDecisionStatus.Accepted, result.Status);
        Assert.Equal("receipt-public-operation", evidence!.OperationId);
        Assert.Equal(blueprint.HumanReview!.Request.RequestId, evidence.RequestId);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Accepted, evidence.Disposition);
        Assert.Equal(HumanReviewDecisionKind.Approve, evidence.DecisionKind);
        Assert.Equal(TimeSpan.Zero, evidence.RecordedAtUtc.Offset);
        Assert.Matches("^[a-f0-9]{64}$", evidence.ProposalHash);
        Assert.Matches("^[a-f0-9]{64}$", evidence.ReceiptHash);

        var pageItems = Assert.IsAssignableFrom<IList<HumanReviewSummary>>(page.Items);
        var previews = Assert.IsAssignableFrom<IList<HumanReviewPreview>>(detail.Previews);
        Assert.Throws<NotSupportedException>(() => pageItems.Clear());
        Assert.Throws<NotSupportedException>(() => previews.Clear());

        var serialized = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain("Binding", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Grant", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Actor", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EligibleReviewers", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CorrelationId", serialized, StringComparison.OrdinalIgnoreCase);
    }
}
