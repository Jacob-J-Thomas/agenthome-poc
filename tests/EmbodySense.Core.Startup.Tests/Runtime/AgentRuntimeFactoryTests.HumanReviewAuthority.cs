using System.Runtime.InteropServices;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Tests.HumanReview;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using CommonCustomLoopRunStatus = EmbodySense.Core.Common.Loops.Models.Custom.Execution.CustomLoopRunStatus;
using CommonGovernedLoopFrontierStatus = EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopFrontierStatus;
using StartupAuthorizationRequest = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewDecisionAuthorizationRequest;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Public_human_review_decision_without_a_provider_fails_closed_as_unavailable()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-missing", "admission-authority-missing");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, null);

        var result = await runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), "decision-authority-missing", HumanReviewDecisionKind.Approve);

        Assert.Equal(HumanReviewDecisionStatus.Unavailable, result.Status);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task Public_human_review_decision_projects_exact_dynamic_eligibility_and_detaches_it_from_canonical_state()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-dynamic", "admission-authority-dynamic");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var requests = new List<StartupAuthorizationRequest>();
        var provider = new HumanReviewDelegateAuthorizationProvider(request =>
        {
            requests.Add(request);
            return ReadyAuthorization(request);
        });
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, provider);

        var version = await LifecycleVersionAsync(runtime, blueprint.Id);
        var first = await runtime.HumanReview.DecideAsync(blueprint.Id, version, "decision-authority-dynamic", HumanReviewDecisionKind.Approve);
        Assert.Equal(HumanReviewDecisionStatus.Expired, first.Status);
        var firstRequest = Assert.Single(requests);
        Assert.Equal("review-request-" + blueprint.Id, firstRequest.RequestId);
        Assert.Equal(HumanReviewDecisionKind.Approve, firstRequest.DecisionKind);
        var eligible = Assert.Single(firstRequest.EligibleReviewers);
        Assert.Equal("governed-reviewer", eligible.ReviewerRoleId);
        Assert.Equal(["review-scope-one"], eligible.ScopeIds.ToArray());

        var projectedReviewers = ImmutableCollectionsMarshal.AsArray(firstRequest.EligibleReviewers)!;
        projectedReviewers[0] = new HumanReviewDecisionAuthorizationEligibility("tampered-role", ["tampered-scope"]);
        var projectedScopes = ImmutableCollectionsMarshal.AsArray(eligible.ScopeIds)!;
        projectedScopes[0] = "tampered-scope";

        var replay = await runtime.HumanReview.DecideAsync(blueprint.Id, version, "decision-authority-dynamic", HumanReviewDecisionKind.Approve);

        Assert.Equal(HumanReviewDecisionStatus.Replayed, replay.Status);
        Assert.Equal(2, requests.Count);
        Assert.Equal("governed-reviewer", requests[1].EligibleReviewers[0].ReviewerRoleId);
        Assert.Equal(["review-scope-one"], requests[1].EligibleReviewers[0].ScopeIds.ToArray());
    }

    [Fact]
    public async Task Public_human_review_decision_preserves_denial_and_fails_closed_for_unavailable_unknown_mismatched_and_malformed_authority()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-statuses", "admission-authority-statuses");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var cases = new[]
        {
            (OperationId: "decision-authority-denied", Status: HumanReviewDecisionAuthorizationStatus.Denied, Expected: HumanReviewDecisionStatus.Denied),
            (OperationId: "decision-authority-unavailable", Status: HumanReviewDecisionAuthorizationStatus.Unavailable, Expected: HumanReviewDecisionStatus.Unavailable),
            (OperationId: "decision-authority-unknown", Status: HumanReviewDecisionAuthorizationStatus.Unknown, Expected: HumanReviewDecisionStatus.Unavailable),
            (OperationId: "decision-authority-mismatched", Status: HumanReviewDecisionAuthorizationStatus.Ready, Expected: HumanReviewDecisionStatus.Unavailable),
            (OperationId: "decision-authority-malformed", Status: HumanReviewDecisionAuthorizationStatus.Ready, Expected: HumanReviewDecisionStatus.Unavailable),
            (OperationId: "decision-authority-unknown-eligibility", Status: HumanReviewDecisionAuthorizationStatus.Ready, Expected: HumanReviewDecisionStatus.Unavailable),
        };

        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var provider = new HumanReviewDelegateAuthorizationProvider(request => request.DecisionOperationId switch
        {
            "decision-authority-mismatched" => ReadyAuthorization(request) with { RequestHash = "mismatched-request-hash" },
            "decision-authority-malformed" => ReadyAuthorization(request) with { ScopeIds = ["scope-b", "scope-a"] },
            "decision-authority-unknown-eligibility" => ReadyAuthorization(request) with { ReviewerRoleId = "unknown-reviewer" },
            _ => ReadyAuthorization(request, cases.Single(item => item.OperationId == request.DecisionOperationId).Status),
        });
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, provider);

        foreach (var item in cases)
        {
            var result = await runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), item.OperationId, HumanReviewDecisionKind.Approve);
            Assert.Equal(item.Expected, result.Status);
            if (item.Expected == HumanReviewDecisionStatus.Denied)
            {
                Assert.Null(result.Evidence);
            }
        }
    }

    [Fact]
    public async Task Public_human_review_decision_provider_exception_is_unavailable_and_does_not_append_a_decision()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-exception", "admission-authority-exception");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var provider = new HumanReviewDelegateAuthorizationProvider(_ => throw new InvalidOperationException("authority unavailable"));
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, provider);

        var result = await runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), "decision-authority-exception", HumanReviewDecisionKind.Approve);
        var detail = await runtime.HumanReview.ReadAsync(blueprint.Id);

        Assert.Equal(HumanReviewDecisionStatus.Unavailable, result.Status);
        Assert.Empty(detail.Detail!.Decisions);
    }

    [Fact]
    public async Task Public_human_review_decision_honors_cancellation_before_authority_and_trusted_expiry_through_outcomes()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync("run-authority-clock", "admission-authority-clock");
        await PersistAuthorityPendingHumanReviewAsync(workspace, blueprint);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var provider = new HumanReviewDelegateAuthorizationProvider(ReadyAuthorization);
        await using var runtime = await CreateRuntimeWithHumanReviewProviderAsync(workspace, executablePath, provider);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.HumanReview.DecideAsync(blueprint.Id, 1, "decision-authority-cancelled", HumanReviewDecisionKind.Approve, cancellationToken: cancellation.Token));

        var result = await runtime.HumanReview.DecideAsync(blueprint.Id, await LifecycleVersionAsync(runtime, blueprint.Id), "decision-authority-expired", HumanReviewDecisionKind.RequestInformation, "late request");

        Assert.Equal(HumanReviewDecisionStatus.Expired, result.Status);
        Assert.Equal("decision-authority-expired", result.Evidence!.OperationId);
    }

    private static HumanReviewDecisionAuthorizationResult ReadyAuthorization(StartupAuthorizationRequest request)
        => ReadyAuthorization(request, HumanReviewDecisionAuthorizationStatus.Ready);

    private static HumanReviewDecisionAuthorizationResult ReadyAuthorization(StartupAuthorizationRequest request, HumanReviewDecisionAuthorizationStatus status)
        => new(status, request.RequestId, request.RequestHash, request.DecisionKind, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, status == HumanReviewDecisionAuthorizationStatus.Ready ? "server-reviewer" : null, status == HumanReviewDecisionAuthorizationStatus.Ready ? request.EligibleReviewers[0].ReviewerRoleId : null, status == HumanReviewDecisionAuthorizationStatus.Ready ? request.EligibleReviewers[0].ScopeIds : [], status == HumanReviewDecisionAuthorizationStatus.Ready ? "server-correlation" : null);

    private static async Task<AgentRuntime> CreateRuntimeWithHumanReviewProviderAsync(TestWorkspace workspace, string executablePath, IHumanReviewDecisionAuthorizationProvider? provider)
    {
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath));
        if (provider is not null)
        {
            factory = factory.WithHumanReviewDecisionAuthorizationProvider(provider);
        }

        return await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
    }

    private static async Task PersistAuthorityPendingHumanReviewAsync(TestWorkspace workspace, CustomLoopRunRecord blueprint)
    {
        var paths = new EmbodySense.Core.Common.Workspace.WorkspacePaths(workspace.RootPath);
        var finalFrontier = blueprint.Frontier ?? throw new InvalidOperationException("The canonical authority test run did not retain a frontier.");
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

    private static async Task<int> LifecycleVersionAsync(AgentRuntime runtime, string runId)
    {
        var read = await runtime.HumanReview.ReadAsync(runId);
        return checked((int)Assert.IsType<HumanReviewDetail>(read.Detail).Summary.LifecycleVersion);
    }
}
