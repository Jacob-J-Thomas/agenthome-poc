using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Tests.HumanReview;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using CommonCustomLoopRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using CommonGovernedLoopFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;
using StartupCustomLoopRunStatus = EmbodySense.Core.Startup.HumanReview.Models.CustomLoopRunStatus;
using StartupFrontierStatus = EmbodySense.Core.Startup.HumanReview.Models.GovernedLoopFrontierStatus;
using CommonHumanReviewTiming = EmbodySense.Core.Common.HumanReview.Models.HumanReviewTiming;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Public_human_review_facade_projects_persisted_request_detail_evidence_and_posture()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-public-facade-coverage";
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(RunId, "admission-public-facade-coverage");
        await PersistApprovedHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var authorization = new HumanReviewDelegateAuthorizationProvider(request => new HumanReviewDecisionAuthorizationResult(HumanReviewDecisionAuthorizationStatus.Ready, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "actor", request.EligibleReviewers[0].ReviewerRoleId, request.EligibleReviewers[0].ScopeIds, "correlation"));
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(authorization);

        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
        var page = await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1));
        var detail = await runtime.HumanReview.ReadAsync(RunId);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(RunId);
        var posture = await runtime.HumanReview.ReadRuntimePostureAsync(RunId);
        var replay = await runtime.HumanReview.DecideAsync(RunId, 5, blueprint.HumanReview!.AcceptedTerminalDecision!.DecisionOperationId, HumanReviewDecisionKind.Approve);
        var conflict = await runtime.HumanReview.DecideAsync(RunId, 5, "new-decision-after-approval", HumanReviewDecisionKind.Reject);
        var missingDecision = await runtime.HumanReview.DecideAsync("run-not-found", 1, "missing-decision", HumanReviewDecisionKind.Approve);

        Assert.Equal(HumanReviewPageStatus.Ready, page.Status);
        var summary = Assert.Single(page.Items);
        Assert.Equal(RunId, summary.RunId);
        Assert.Equal(HumanReviewPurpose.Continuation, summary.Purpose);
        Assert.Equal(HumanReviewLifecycleStatus.Approved, summary.LifecycleStatus);
        Assert.Equal(StartupCustomLoopRunStatus.Paused, summary.RunStatus);
        Assert.Equal(StartupFrontierStatus.ReviewBlocked, summary.FrontierStatus);
        Assert.Equal(4, summary.RequestedDecisions.Length);
        Assert.Contains(HumanReviewDecisionKind.Approve, summary.RequestedDecisions);
        Assert.Contains(HumanReviewDecisionKind.Reject, summary.RequestedDecisions);
        Assert.Contains(HumanReviewDecisionKind.Cancel, summary.RequestedDecisions);
        Assert.Contains(HumanReviewDecisionKind.RequestInformation, summary.RequestedDecisions);

        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        var projection = Assert.IsType<HumanReviewDetail>(detail.Detail);
        Assert.Equal(RunId, projection.Summary.RunId);
        Assert.Equal(3, projection.Previews.Count);
        Assert.Contains(projection.Previews, item => item.Kind == HumanReviewPreviewKind.Action);
        Assert.Contains(projection.Previews, item => item.Kind == HumanReviewPreviewKind.Result);
        Assert.Contains(projection.Previews, item => item.Kind == HumanReviewPreviewKind.Evidence);
        Assert.Single(projection.Decisions);
        Assert.Equal(HumanReviewDecisionKind.Approve, projection.Decisions[0].Kind);
        Assert.NotEmpty(projection.Evidence);
        Assert.Equal(HumanReviewLifecycleStatus.Approved, projection.Runtime.LifecycleStatus);
        Assert.Equal(HumanReviewContinuationStatus.Reserved, projection.Runtime.ContinuationStatus);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Missing, projection.EffectEvidence!.Status);

        Assert.Equal(HumanReviewEvidenceReadStatus.Ready, evidence.Status);
        Assert.Equal(projection.Evidence.Count, evidence.Evidence.Count);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Missing, evidence.EffectEvidence!.Status);
        Assert.Equal(HumanReviewReadStatus.Ready, posture.Status);
        Assert.Equal(HumanReviewLifecycleStatus.Approved, posture.Posture!.LifecycleStatus);
        Assert.Equal(HumanReviewContinuationStatus.Reserved, posture.Posture.ContinuationStatus);
        Assert.Equal(projection.Runtime.EvidenceCount, posture.Posture.EvidenceCount);
        Assert.Equal(HumanReviewDecisionStatus.Replayed, replay.Status);
        Assert.Equal(HumanReviewDecisionStatus.Conflict, conflict.Status);
        Assert.Equal(HumanReviewDecisionStatus.NotFound, missingDecision.Status);
    }

    [Fact]
    public async Task Public_human_review_facade_keeps_invalid_and_missing_reads_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, executablePath);

        Assert.Equal(HumanReviewPageStatus.Invalid, (await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(0))).Status);
        Assert.Equal(HumanReviewPageStatus.Invalid, (await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(HumanReviewRuntimeFacade.MaxPageSize + 1))).Status);
        Assert.Equal(HumanReviewReadStatus.Invalid, (await runtime.HumanReview.ReadAsync("invalid id")).Status);
        Assert.Equal(HumanReviewEvidenceReadStatus.Invalid, (await runtime.HumanReview.ReadEvidenceAsync("invalid id")).Status);
        Assert.Equal(HumanReviewReadStatus.Invalid, (await runtime.HumanReview.ReadRuntimePostureAsync("invalid id")).Status);
        Assert.Equal(HumanReviewReadStatus.NotFound, (await runtime.HumanReview.ReadAsync("run-not-found")).Status);
        Assert.Equal(HumanReviewEvidenceReadStatus.NotFound, (await runtime.HumanReview.ReadEvidenceAsync("run-not-found")).Status);
        Assert.Equal(HumanReviewReadStatus.NotFound, (await runtime.HumanReview.ReadRuntimePostureAsync("run-not-found")).Status);
        Assert.Equal(HumanReviewDecisionStatus.Invalid, (await runtime.HumanReview.DecideAsync("invalid id", 1, "invalid-decision", HumanReviewDecisionKind.Approve)).Status);
        Assert.Equal(HumanReviewDecisionStatus.NotFound, (await runtime.HumanReview.DecideAsync("run-not-found", 1, "missing-decision", HumanReviewDecisionKind.Approve)).Status);
    }

    [Fact]
    public async Task Public_human_review_facade_maps_expired_decisions()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-public-facade-information";
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(RunId, "admission-public-facade-information");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var authorization = new HumanReviewDelegateAuthorizationProvider(request => new HumanReviewDecisionAuthorizationResult(HumanReviewDecisionAuthorizationStatus.Ready, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "actor", request.EligibleReviewers[0].ReviewerRoleId, request.EligibleReviewers[0].ScopeIds, "correlation"));
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(authorization);
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
        var detail = await runtime.HumanReview.ReadAsync(RunId);
        var result = await runtime.HumanReview.DecideAsync(RunId, (int)detail.Detail!.Summary.LifecycleVersion, "information-request-operation", HumanReviewDecisionKind.RequestInformation, "bounded clarification");

        Assert.Equal(HumanReviewDecisionStatus.Expired, result.Status);
        Assert.Equal("information-request-operation", result.OperationId);
    }

    [Fact]
    public async Task Public_human_review_facade_maps_server_denial_without_exposing_authority()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        const string RunId = "run-public-facade-denied";
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(RunId, "admission-public-facade-denied");
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(new HumanReviewDecisionAuthorizationProviderTestDouble());
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
        var detail = await runtime.HumanReview.ReadAsync(RunId);
        var result = await runtime.HumanReview.DecideAsync(RunId, (int)detail.Detail!.Summary.LifecycleVersion, "denied-operation", HumanReviewDecisionKind.Approve);

        Assert.Equal(HumanReviewDecisionStatus.Denied, result.Status);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task Public_human_review_facade_maps_each_terminal_and_information_decision()
    {
        await AssertPublicDecisionMappingAsync("run-public-facade-approve", "approve-operation", HumanReviewDecisionKind.Approve, HumanReviewDecisionStatus.Accepted, HumanReviewLifecycleStatus.Approved, HumanReviewDecisionOperationDisposition.Accepted);
        await AssertPublicDecisionMappingAsync("run-public-facade-reject", "reject-operation", HumanReviewDecisionKind.Reject, HumanReviewDecisionStatus.Accepted, HumanReviewLifecycleStatus.Rejected, HumanReviewDecisionOperationDisposition.Accepted);
        await AssertPublicDecisionMappingAsync("run-public-facade-cancel", "cancel-operation", HumanReviewDecisionKind.Cancel, HumanReviewDecisionStatus.Accepted, HumanReviewLifecycleStatus.Cancelled, HumanReviewDecisionOperationDisposition.Accepted);
        await AssertPublicDecisionMappingAsync("run-public-facade-information", "information-operation", HumanReviewDecisionKind.RequestInformation, HumanReviewDecisionStatus.InformationRequested, HumanReviewLifecycleStatus.AwaitingInformation, HumanReviewDecisionOperationDisposition.InformationRequested, "bounded clarification");
    }

    [Fact]
    public async Task Public_human_review_facade_maps_an_admitted_run_without_review_as_not_found()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-public-facade-admitted", "admission-public-facade-admitted");
        await PersistAdmittedRunAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider);
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var page = await runtime.HumanReview.ListAsync(new HumanReviewPageRequest(1));
        var detail = await runtime.HumanReview.ReadAsync(blueprint.Id);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(blueprint.Id);
        var posture = await runtime.HumanReview.ReadRuntimePostureAsync(blueprint.Id);

        Assert.Equal(HumanReviewPageStatus.Ready, page.Status);
        Assert.Empty(page.Items);
        Assert.Equal(HumanReviewReadStatus.NotFound, detail.Status);
        Assert.Equal(HumanReviewEvidenceReadStatus.NotFound, evidence.Status);
        Assert.Equal(HumanReviewReadStatus.NotFound, posture.Status);
    }

    private static async Task<CustomLoopRunRecord> CreateLivePendingBlueprintAsync(string runId)
    {
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(runId, "admission-" + runId);
        var request = blueprint.HumanReview!.Request;
        var now = DateTimeOffset.UtcNow;
        var timing = new CommonHumanReviewTiming(request.Timing.CreatedAtUtc, now, now.AddHours(1));
        return blueprint with { HumanReview = blueprint.HumanReview with { Request = HumanReviewContractHash.ApplyRequest(request with { Timing = timing }) } };
    }

    private static IHumanReviewDecisionAuthorizationProvider CreateAllowingAuthorizationProvider()
        => new HumanReviewDelegateAuthorizationProvider(request => new HumanReviewDecisionAuthorizationResult(HumanReviewDecisionAuthorizationStatus.Ready, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "actor", request.EligibleReviewers[0].ReviewerRoleId, request.EligibleReviewers[0].ScopeIds, "correlation"));

    private static async Task AssertPublicDecisionMappingAsync(string runId, string operationId, HumanReviewDecisionKind kind, HumanReviewDecisionStatus expectedStatus, HumanReviewLifecycleStatus expectedLifecycle, HumanReviewDecisionOperationDisposition expectedDisposition, string? detail = null)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await CreateLivePendingBlueprintAsync(runId);
        await PersistPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runStoreProvider = new CustomLoopRunStoreProvider(workspace.RootPath);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath))
            .WithCustomLoopRunStoreProvider(runStoreProvider)
            .WithHumanReviewDecisionAuthorizationProvider(CreateAllowingAuthorizationProvider());
        await using var runtime = await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);

        var result = await runtime.HumanReview.DecideAsync(runId, 4, operationId, kind, detail);
        var projection = await runtime.HumanReview.ReadAsync(runId);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedDisposition, result.Evidence!.Disposition);
        Assert.Equal(HumanReviewReadStatus.Ready, projection.Status);
        Assert.Equal(expectedLifecycle, projection.Detail!.Summary.LifecycleStatus);
    }

    private static async Task PersistApprovedHumanReviewAsync(TestWorkspace workspace, CustomLoopRunRecord blueprint)
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
        var accepted = blueprint.HumanReview.AcceptedTerminalDecision ?? throw new InvalidOperationException("The canonical recovery test run did not retain its approval decision.");
        var persisted = await seedStore.GetAsync(started.Id) ?? throw new InvalidOperationException("The canonical recovery test run was not persisted.");
        var decision = await new HumanReviewDecisionService(seedStore, new HumanReviewRecoveryServerAuthorizer(), new HumanReviewRecoveryTrustedClock(persisted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new HumanReviewDecisionCommand(started.Id, persisted.LifecycleVersion, accepted.DecisionOperationId, accepted.Kind, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
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

    private static async Task PersistAdmittedRunAsync(TestWorkspace workspace, CustomLoopRunRecord blueprint)
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
    }
}
