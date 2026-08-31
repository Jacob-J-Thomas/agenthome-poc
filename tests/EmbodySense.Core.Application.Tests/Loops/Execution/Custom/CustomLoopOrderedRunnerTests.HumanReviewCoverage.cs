using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Pre_dispatch_approval_without_admission_service_stops_before_exposing_review()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var action = new QueueWorkspaceActionExecutor(new GovernedLoopWorkspaceActionExecutionResult(
            GovernedLoopWorkspaceActionExecutionStatus.ApprovalRequired,
            null,
            "The prepared Action requires a review admission boundary."));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("bounded provider output")), workspaceActionExecutor: action),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("human_review_admission_unavailable", result.Run.FailureCode);
        Assert.Null(result.Run.HumanReview);
        Assert.Single(action.Requests);
        Assert.Single(result.Run.Events, item => item.SequentialNodeEvidence is
        {
            NodeId: "workspace-action",
            Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
        });
    }

    [Fact]
    public async Task Pre_dispatch_approval_without_prepared_effect_stops_before_exposing_review()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var action = new QueueWorkspaceActionExecutor(new GovernedLoopWorkspaceActionExecutionResult(
            GovernedLoopWorkspaceActionExecutionStatus.ApprovalRequired,
            null,
            "The Action did not return its immutable prepared effect."));
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("bounded provider output")), workspaceActionExecutor: action, humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("human_review_pre_dispatch_request_invalid", result.Run.FailureCode);
        Assert.Null(result.Run.HumanReview);
        Assert.Single(action.Requests);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.HumanReviewRequestAdmitted);
    }

    [Fact]
    public async Task Human_review_node_without_admission_service_stops_before_exposing_review()
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var adapter = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor()),
            evidence,
            evidence);

        var result = await adapter.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, result.Run!.Status);
        Assert.Equal("human_review_admission_unavailable", result.Run.FailureCode);
        Assert.Null(result.Run.HumanReview);
        Assert.DoesNotContain(result.Run.Events, item => item.Kind == CustomLoopRunEventKind.HumanReviewRequestAdmitted);
    }
}
