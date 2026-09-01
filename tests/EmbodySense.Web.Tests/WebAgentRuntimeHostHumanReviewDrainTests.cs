using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Services;
using CommonCustomLoopRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using CommonGovernedLoopFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;
using StartupHumanReviewLifecycleStatus = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewLifecycleStatus;
using StartupHumanReviewAuthorizationRequest = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;

namespace EmbodySense.Web.Tests;

[Collection(EphemeralPortApiCollection.Name)]
public sealed class WebAgentRuntimeHostHumanReviewDrainTests
{
    [Fact]
    public async Task HumanReview_decision_remains_in_flight_until_shutdown_safe_boundary_then_cancels_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-human-review-drain", "admission-human-review-drain");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var codexPath = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var host = CreateHost(workspace, codexPath, out var authorization);
        await using (host)
        {
            var detail = await host.ReadHumanReviewAsync(blueprint.Id);
            Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
            Assert.NotNull(detail.Detail);
            var review = detail.Detail!;
            Assert.Equal(StartupHumanReviewLifecycleStatus.Pending, review.Summary.LifecycleStatus);

            var decision = host.DecideHumanReviewAsync(new HumanReviewDecisionOperationInput(blueprint.Id, checked((int)review.Summary.LifecycleVersion), "shutdown-human-review-decision", HumanReviewDecisionKind.Approve, null));
            await authorization.WaitUntilEnteredAsync();

            var disposal = host.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<TimeoutException>(() => disposal.WaitAsync(TimeSpan.FromMilliseconds(250)));

            authorization.Release();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            var decisionException = await Record.ExceptionAsync(async () => await decision);

            Assert.IsAssignableFrom<OperationCanceledException>(decisionException);
        }

        using var store = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var persisted = await store.GetAsync(blueprint.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.HumanReview!.AcceptedTerminalDecision);
    }

    private static WebAgentRuntimeHost CreateHost(TestWorkspace workspace, string codexPath, out BlockingHumanReviewAuthorization authorization)
    {
        var approvals = new WebApprovalCoordinator();
        var configuredAuthorization = new BlockingHumanReviewAuthorization();
        authorization = configuredAuthorization;
        var options = WebRunOptions.FromArguments(["--workdir", workspace.RootPath, "--model", "gpt-test", "--codex-path", codexPath]);
        return new WebAgentRuntimeHost(
            options,
            approvals,
            WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath),
            null,
            runtimeStatus => AgentRuntimeFactory.ForFileCapabilityTrustRoot(approvals, workspace.ServerStatePath, runtimeStatus)
                .WithHumanReviewDecisionAuthorizationProvider(configuredAuthorization));
    }

    private static async Task PersistPendingHumanReviewAsync(TestWorkspace workspace, CustomLoopRunRecord blueprint)
    {
        var paths = new WorkspacePaths(workspace.RootPath);
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var review = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var initialReview = GovernedLoopNodeExecutionEvidence.CreateActivation(review.ActivationOrdinal, review.PlanOrdinal, review.VisitOrdinal, review.NodeId, review.Descriptor, review.IncomingControlEdgeIds, review.OutgoingControlEdgeIds, GovernedLoopNodeExecutionStatus.Ready);
        var initialFrontier = GovernedLoopFrontierPosture.Create(finalFrontier.Binding, finalFrontier.WorkspaceId, finalFrontier.GraphArtifactHash, finalFrontier.GraphLayoutHash, finalFrontier.AdmissionReceiptHash, 1, finalFrontier.Payload.ConcurrencyCeiling, CommonGovernedLoopFrontierStatus.Active, [finalFrontier.Payload.Nodes[0], initialReview], blueprint.CreatedAtUtc, string.Empty);
        var admitted = blueprint with
        {
            LifecycleVersion = 1,
            Status = CommonCustomLoopRunStatus.Admitted,
            UpdatedAtUtc = blueprint.CreatedAtUtc,
            CompletedAtUtc = null,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = blueprint.Events.Take(2).ToArray(),
            Frontier = initialFrontier,
            Checkpoint = CustomLoopRunCheckpoint.Start(),
            HumanReview = null,
            WaitEvidence = [],
            HumanInputWaitingCheckpoints = [],
            FinalOutput = null,
            FailureCode = null,
            FailureDetail = null
        };
        Assert.True(CustomLoopRunValidator.Validate(admitted).IsValid);
        using var seedStore = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await seedStore.CreateAsync(admitted)).Status);
        var running = CreateRunning(admitted, blueprint);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await seedStore.UpdateAsync(running, admitted.LifecycleVersion)).Status);
        var started = CreateStarted(running, blueprint);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await seedStore.UpdateAsync(started, running.LifecycleVersion)).Status);
        var admission = await new HumanReviewAdmissionService(seedStore).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, blueprint.HumanReview!.Request, blueprint.Frontier!, blueprint.Events[3]));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, admission.Status);
    }

    private static CustomLoopRunRecord CreateRunning(CustomLoopRunRecord admitted, CustomLoopRunRecord blueprint)
    {
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var finalReview = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var readyReview = GovernedLoopNodeExecutionEvidence.CreateActivation(finalReview.ActivationOrdinal, finalReview.PlanOrdinal, finalReview.VisitOrdinal, finalReview.NodeId, finalReview.Descriptor, finalReview.IncomingControlEdgeIds, finalReview.OutgoingControlEdgeIds, GovernedLoopNodeExecutionStatus.Ready);
        var updatedAtUtc = blueprint.Events[2].TimestampUtc;
        var frontier = GovernedLoopFrontierPosture.Create(finalFrontier.Binding, finalFrontier.WorkspaceId, finalFrontier.GraphArtifactHash, finalFrontier.GraphLayoutHash, finalFrontier.AdmissionReceiptHash, admitted.Frontier!.Payload.FrontierVersion, finalFrontier.Payload.ConcurrencyCeiling, CommonGovernedLoopFrontierStatus.Active, [finalFrontier.Payload.Nodes[0], readyReview], admitted.Frontier.Payload.UpdatedAtUtc, string.Empty);
        return admitted with
        {
            LifecycleVersion = 2,
            Status = CommonCustomLoopRunStatus.Running,
            UpdatedAtUtc = updatedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, updatedAtUtc),
            Frontier = frontier,
            Events = [.. admitted.Events, blueprint.Events[2] with { ControlExpectedLifecycleVersion = admitted.LifecycleVersion }]
        };
    }

    private static CustomLoopRunRecord CreateStarted(CustomLoopRunRecord running, CustomLoopRunRecord blueprint)
    {
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var finalReview = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var startedReview = GovernedLoopNodeExecutionEvidence.CreateActivation(finalReview.ActivationOrdinal, finalReview.PlanOrdinal, finalReview.VisitOrdinal, finalReview.NodeId, finalReview.Descriptor, finalReview.IncomingControlEdgeIds, finalReview.OutgoingControlEdgeIds, GovernedLoopNodeExecutionStatus.Running, finalReview.Attempt, finalReview.AttemptOperationId);
        var updatedAtUtc = blueprint.Events[3].TimestampUtc.AddMinutes(-1);
        var frontier = GovernedLoopFrontierPosture.Create(finalFrontier.Binding, finalFrontier.WorkspaceId, finalFrontier.GraphArtifactHash, finalFrontier.GraphLayoutHash, finalFrontier.AdmissionReceiptHash, blueprint.HumanReview!.Request.Binding.FrontierVersion - 1, finalFrontier.Payload.ConcurrencyCeiling, CommonGovernedLoopFrontierStatus.Active, [finalFrontier.Payload.Nodes[0], startedReview], updatedAtUtc, string.Empty);
        return running with { LifecycleVersion = 3, UpdatedAtUtc = updatedAtUtc, Frontier = frontier };
    }

    private sealed class BlockingHumanReviewAuthorization : IHumanReviewDecisionAuthorizationProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HumanReviewDecisionAuthorizationResult?> AuthorizeAsync(StartupHumanReviewAuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new HumanReviewDecisionAuthorizationResult(HumanReviewDecisionAuthorizationStatus.Ready, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "server-reviewer", request.EligibleReviewers[0].ReviewerRoleId, request.EligibleReviewers[0].ScopeIds, "server-correlation");
        }

        public Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _release.TrySetResult();
    }
}
