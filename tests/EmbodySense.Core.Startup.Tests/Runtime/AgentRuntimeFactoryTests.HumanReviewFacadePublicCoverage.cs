using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.HumanReview;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Public_human_review_facade_propagates_cancellation_at_each_read_and_decision_boundary()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await CreateLivePendingBlueprintAsync("run-public-facade-cancellation");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);
        var detail = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(blueprint.Id)).Detail);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.ListAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.ReadAsync(blueprint.Id, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.ReadEvidenceAsync(blueprint.Id, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.ReadRuntimePostureAsync(blueprint.Id, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.DecideAsync(blueprint.Id, (int)detail.Summary.LifecycleVersion, "cancelled-facade-operation", HumanReviewDecisionKind.Approve, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Public_human_review_facade_rejects_null_unknown_and_malformed_inputs_before_store_access()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        Assert.Equal(HumanReviewPageStatus.Invalid, (await runtime.HumanReview.ListAsync(null)).Status);
        Assert.Equal(HumanReviewPageStatus.Invalid, (await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1, string.Empty))).Status);
        Assert.Equal(HumanReviewPageStatus.Invalid, (await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1, " "))).Status);
        Assert.Equal(HumanReviewReadStatus.Invalid, (await runtime.HumanReview.ReadAsync(null)).Status);
        Assert.Equal(HumanReviewEvidenceReadStatus.Invalid, (await runtime.HumanReview.ReadEvidenceAsync(null)).Status);
        Assert.Equal(HumanReviewReadStatus.Invalid, (await runtime.HumanReview.ReadRuntimePostureAsync(null)).Status);

        var empty = await runtime.HumanReview.DecideAsync((HumanReviewDecisionOperationInput?)null);
        var unknown = await runtime.HumanReview.DecideAsync("run-valid-format", 1, "operation-valid-format", HumanReviewDecisionKind.Unknown);
        var invalidVersion = await runtime.HumanReview.DecideAsync("run-valid-format", 0, "operation-valid-format-2", HumanReviewDecisionKind.Approve);

        Assert.Equal(HumanReviewDecisionStatus.Invalid, empty.Status);
        Assert.Equal(HumanReviewDecisionStatus.Invalid, unknown.Status);
        Assert.Equal(HumanReviewDecisionStatus.Invalid, invalidVersion.Status);
        Assert.Empty(empty.OperationId);
        Assert.Equal("operation-valid-format", unknown.OperationId);
    }

    [Fact]
    public async Task Public_human_review_facade_projects_expired_decision_into_detail_evidence_and_posture()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-public-facade-expired-projection";
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(RunId, "admission-public-facade-expired-projection");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, CreateAllowingAuthorizationProvider());

        var before = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(RunId)).Detail);
        var operationId = "expired-public-facade-operation";
        var result = await runtime.HumanReview.DecideAsync(RunId, checked((int)before.Summary.LifecycleVersion), operationId, HumanReviewDecisionKind.RequestInformation, "late information");
        var detail = await runtime.HumanReview.ReadAsync(RunId);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(RunId);
        var posture = await runtime.HumanReview.ReadRuntimePostureAsync(RunId);

        Assert.Equal(HumanReviewDecisionStatus.Expired, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Expired, result.Evidence!.Disposition);
        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, detail.Detail!.Summary.LifecycleStatus);
        Assert.Contains(detail.Detail.Evidence, item => item.Kind == HumanReviewEvidenceKind.DecisionExpired && item.DecisionOperationId == operationId);
        Assert.Equal(HumanReviewEvidenceReadStatus.Ready, evidence.Status);
        Assert.Contains(evidence.Evidence, item => item.Kind == HumanReviewEvidenceKind.DecisionExpired && item.DecisionOperationId == operationId);
        Assert.Equal(HumanReviewReadStatus.Ready, posture.Status);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, posture.Posture!.LifecycleStatus);
    }

    [Fact]
    public async Task Public_human_review_facade_retains_conflict_evidence_after_terminal_decision()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-public-facade-conflict-projection";
        var blueprint = await CreateLivePendingBlueprintAsync(RunId);
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, CreateAllowingAuthorizationProvider());

        var pending = Assert.IsType<HumanReviewDetail>((await runtime.HumanReview.ReadAsync(RunId)).Detail);
        var expectedVersion = checked((int)pending.Summary.LifecycleVersion);
        var accepted = await runtime.HumanReview.DecideAsync(RunId, expectedVersion, "conflict-first-operation", HumanReviewDecisionKind.Approve);
        var conflict = await runtime.HumanReview.DecideAsync(RunId, expectedVersion, "conflict-second-operation", HumanReviewDecisionKind.Reject);
        var detail = await runtime.HumanReview.ReadAsync(RunId);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(RunId);

        Assert.Equal(HumanReviewDecisionStatus.Accepted, accepted.Status);
        Assert.Equal(HumanReviewDecisionStatus.Conflict, conflict.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, conflict.Evidence!.Disposition);
        Assert.Equal(HumanReviewLifecycleStatus.Approved, detail.Detail!.Summary.LifecycleStatus);
        Assert.Contains(detail.Detail.Evidence, item => item.Kind == HumanReviewEvidenceKind.DecisionConflict && item.DecisionOperationId == "conflict-second-operation");
        Assert.Contains(evidence.Evidence, item => item.Kind == HumanReviewEvidenceKind.DecisionConflict && item.DecisionOperationId == "conflict-second-operation");
    }

}
