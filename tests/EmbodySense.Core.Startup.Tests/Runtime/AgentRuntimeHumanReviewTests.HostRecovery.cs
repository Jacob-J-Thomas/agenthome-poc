using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using CommonCustomLoopRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using CommonGovernedLoopFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;

using static EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeHumanReviewTests
{
    [Fact]
    public async Task Start_background_publishes_human_review_recovery_before_the_first_coordinator_review_attempt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-startup-human-review-recovery";
        var approved = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(RunId, "admission-startup-human-review-recovery");
        var paths = new WorkspacePaths(workspace.RootPath);
        var finalFrontier = approved.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var review = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var initialReview = GovernedLoopNodeExecutionEvidence.CreateActivation(
            review.ActivationOrdinal,
            review.PlanOrdinal,
            review.VisitOrdinal,
            review.NodeId,
            review.Descriptor,
            review.IncomingControlEdgeIds,
            review.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Ready);
        var initialFrontier = GovernedLoopFrontierPosture.Create(
            finalFrontier.Binding,
            finalFrontier.WorkspaceId,
            finalFrontier.GraphArtifactHash,
            finalFrontier.GraphLayoutHash,
            finalFrontier.AdmissionReceiptHash,
            1,
            finalFrontier.Payload.ConcurrencyCeiling,
            CommonGovernedLoopFrontierStatus.Active,
            [finalFrontier.Payload.Nodes[0], initialReview],
            approved.CreatedAtUtc,
            string.Empty);
        var admitted = approved with
        {
            LifecycleVersion = 1,
            Status = CommonCustomLoopRunStatus.Admitted,
            UpdatedAtUtc = approved.CreatedAtUtc,
            CompletedAtUtc = null,
            ExecutionClock = CustomLoopExecutionClock.NotStarted(),
            Events = approved.Events.Take(2).ToArray(),
            Frontier = initialFrontier,
            Checkpoint = CustomLoopRunCheckpoint.Start(),
            HumanReview = null,
            WaitEvidence = [],
            HumanInputWaitingCheckpoints = [],
            FinalOutput = null,
            FailureCode = null,
            FailureDetail = null
        };
        var admittedValidation = CustomLoopRunValidator.Validate(admitted);
        Assert.True(admittedValidation.IsValid, string.Join(Environment.NewLine, admittedValidation.Errors.Select(error => $"{error.Field}: {error.Message}")));

        using (var seedStore = new CustomLoopRunStore(paths))
        {
            Assert.Equal(CustomLoopRunStoreStatus.Created, (await seedStore.CreateAsync(admitted)).Status);
            var running = CreateRunning(admitted, approved);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await seedStore.UpdateAsync(running, admitted.LifecycleVersion)).Status);
            var started = CreateStarted(running, approved);
            Assert.Equal(CustomLoopRunStoreStatus.Updated, (await seedStore.UpdateAsync(started, running.LifecycleVersion)).Status);
            var admission = await new HumanReviewAdmissionService(seedStore).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, approved.HumanReview!.Request, approved.Frontier!, approved.Events[3]));
            Assert.Equal(CustomLoopRunStoreStatus.Updated, admission.Status);
            var accepted = approved.HumanReview.AcceptedTerminalDecision ?? throw new InvalidOperationException("The canonical recovery test run did not retain its approval decision.");
            var paused = await seedStore.GetAsync(started.Id) ?? throw new InvalidOperationException("The canonical recovery test run was not persisted after admission.");
            var decision = await new HumanReviewDecisionService(seedStore, new HumanReviewRecoveryServerAuthorizer(), new HumanReviewRecoveryTrustedClock(paused.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new HumanReviewDecisionCommand(started.Id, paused.LifecycleVersion, accepted.DecisionOperationId, accepted.Kind, null));
            Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        }

        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        using var observer = new AgentRuntimeFactoryHumanReviewHostObserver(workspace.RootPath, RunId);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                new RejectingApprovalPrompt(),
                workspace.ServerStatePath,
                CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble())
            .WithGovernedLoopLocalCoordinatorBoundaryObserver(observer);

        await using var runtime = await factory.CreateAsync(
            "test-model",
            workspace.RootPath,
            executablePath,
            "read-only",
            AgentRuntimeSurface.Web);

        var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync();
        await observer.HumanReviewWorkAttempted.WaitAsync(TimeSpan.FromSeconds(10));
        var detail = await runtime.HumanReview.ReadAsync(RunId);

        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStartStatus.Started, start.Status);
        Assert.True(observer.FirstHumanReviewWorkSawPublishedContinuation);
        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        Assert.NotNull(detail.Detail);
        Assert.Equal(HumanReviewContinuationStatus.Published, detail.Detail!.Runtime.ContinuationStatus);
        Assert.Equal(AgentRuntimeGovernedLoopBackgroundStopStatus.Stopped, (await runtime.StopGovernedLoopLocalBackgroundAsync()).Status);
    }

    private static CustomLoopRunRecord CreateRunning(CustomLoopRunRecord admitted, CustomLoopRunRecord blueprint)
    {
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical recovery test run did not retain a frontier.");
        var finalReview = finalFrontier.Payload.Nodes.Single(node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var readyReview = GovernedLoopNodeExecutionEvidence.CreateActivation(
            finalReview.ActivationOrdinal,
            finalReview.PlanOrdinal,
            finalReview.VisitOrdinal,
            finalReview.NodeId,
            finalReview.Descriptor,
            finalReview.IncomingControlEdgeIds,
            finalReview.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Ready);
        var updatedAtUtc = blueprint.Events[2].TimestampUtc;
        var frontier = GovernedLoopFrontierPosture.Create(
            finalFrontier.Binding,
            finalFrontier.WorkspaceId,
            finalFrontier.GraphArtifactHash,
            finalFrontier.GraphLayoutHash,
            finalFrontier.AdmissionReceiptHash,
            admitted.Frontier!.Payload.FrontierVersion,
            finalFrontier.Payload.ConcurrencyCeiling,
            CommonGovernedLoopFrontierStatus.Active,
            [finalFrontier.Payload.Nodes[0], readyReview],
            admitted.Frontier.Payload.UpdatedAtUtc,
            string.Empty);
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
        var startedReview = GovernedLoopNodeExecutionEvidence.CreateActivation(
            finalReview.ActivationOrdinal,
            finalReview.PlanOrdinal,
            finalReview.VisitOrdinal,
            finalReview.NodeId,
            finalReview.Descriptor,
            finalReview.IncomingControlEdgeIds,
            finalReview.OutgoingControlEdgeIds,
            GovernedLoopNodeExecutionStatus.Running,
            finalReview.Attempt,
            finalReview.AttemptOperationId);
        var updatedAtUtc = blueprint.Events[3].TimestampUtc.AddMinutes(-1);
        var frontier = GovernedLoopFrontierPosture.Create(
            finalFrontier.Binding,
            finalFrontier.WorkspaceId,
            finalFrontier.GraphArtifactHash,
            finalFrontier.GraphLayoutHash,
            finalFrontier.AdmissionReceiptHash,
            blueprint.HumanReview!.Request.Binding.FrontierVersion - 1,
            finalFrontier.Payload.ConcurrencyCeiling,
            CommonGovernedLoopFrontierStatus.Active,
            [finalFrontier.Payload.Nodes[0], startedReview],
            updatedAtUtc,
            string.Empty);
        return running with
        {
            LifecycleVersion = 3,
            UpdatedAtUtc = updatedAtUtc,
            Frontier = frontier
        };
    }
}
