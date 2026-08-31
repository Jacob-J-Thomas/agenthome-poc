using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.HumanReview;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Sequential_human_review_boundaries_archive_the_first_release_and_admit_the_second()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()), artifactFactory: role =>
        {
            var artifact = TwoHumanReviewArtifact(role);
            var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
            Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
            return artifact;
        });
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var request = new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web);

        var parked = await runtime.RunAsync(request);
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, parked.Status);
        var firstRequest = Assert.IsType<HumanReviewRunState>(store.Current.HumanReview).Request;

        var decisionAtUtc = store.Current.UpdatedAtUtc.AddMinutes(1);
        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionTestAuthorizer { ReviewerRoleId = "governed-reviewer", ScopeIds = ["review-scope-one"] },
            new HumanReviewDecisionTestClock(decisionAtUtc)).DecideAsync(
                new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-first-review", HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);

        var approved = store.Current;
        var firstFrontier = Assert.IsType<GovernedLoopFrontierPosture>(approved.Frontier);
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var accepted = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        var wakeAtUtc = approved.UpdatedAtUtc.AddTicks(1);
        var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            1,
            "first-review-wake",
            new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
            new HumanReviewDecisionReference(accepted.DecisionId, accepted.DecisionOperationId, accepted.Kind, accepted.DecisionHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            review.Request.Binding.BindingHash,
            approved.SequentialAdapterBinding!.ExecutionBinding.ExecutionGeneration,
            wakeAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "multiple-review-test", "first-review-wake", wakeAtUtc, string.Empty)),
            string.Empty));
        var claimAtUtc = wakeAtUtc.AddTicks(1);
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            "first-review-claim",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "multiple-review-worker",
            claimAtUtc,
            claimAtUtc.AddMinutes(1),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "multiple-review-test", "first-review-claim", claimAtUtc, string.Empty)),
            string.Empty));
        var published = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [], null, null, string.Empty));
        var publishedRun = approved with
        {
            LifecycleVersion = approved.LifecycleVersion + 1,
            UpdatedAtUtc = wakeAtUtc,
            HumanReview = review with { Continuation = published },
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(publishedRun, approved.LifecycleVersion)).Status);
        var claimed = HumanReviewContinuationContractHash.ApplyState(published with { Claims = [claim], StateHash = string.Empty });
        var claimedRun = publishedRun with
        {
            LifecycleVersion = publishedRun.LifecycleVersion + 1,
            UpdatedAtUtc = claimAtUtc,
            HumanReview = publishedRun.HumanReview! with { Continuation = claimed },
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(claimedRun, publishedRun.LifecycleVersion)).Status);

        var requestReference = new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash);
        var decisionReference = new HumanReviewDecisionReference(accepted.DecisionId, accepted.DecisionOperationId, accepted.Kind, accepted.DecisionHash);
        var wakeReference = new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash);
        var claimReference = new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash);
        var reservationReference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
        var releaseOperationId = Assert.IsType<string>(HumanReviewContinuationReleaseOperationId.Create(requestReference, wakeReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.Continuation));
        var receipt = new HumanReviewContinuationReleaseReceiptIntent(releaseOperationId, requestReference, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.Continuation, null);
        var action = new HumanReviewContinuationActionIntent(HumanReviewContinuationAction.ReleaseContinuation, store.Current.Id, store.Current.LifecycleVersion, requestReference, decisionReference, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, null, receipt);
        var completion = new HumanReviewContinuationCompletionIntent(store.Current.Id, store.Current.LifecycleVersion, requestReference, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, receipt);
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            runtime,
            new FixedTimeProvider(claimAtUtc.AddTicks(1)),
            new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current));

        var released = await release.ReleaseAsync(action, completion);

        Assert.Equal(HumanReviewContinuationReleaseStatus.Completed, released.Status);
        var second = Assert.IsType<HumanReviewRunState>(store.Current.HumanReview);
        Assert.NotEqual(firstRequest.RequestHash, second.Request.RequestHash);
        Assert.Single(second.CompletedReviews);
        Assert.Equal(firstRequest.RequestHash, second.CompletedReviews[0].Request.RequestHash);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, second.Lifecycle.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));

        var admission = new HumanReviewAdmissionService(store);
        var archivedAdmissionReplay = await admission.AdmitAsync(new HumanReviewAdmissionCommand(store.Current.Id, store.Current.LifecycleVersion, firstRequest, firstFrontier));
        Assert.Equal(CustomLoopRunStoreStatus.AlreadyCreated, archivedAdmissionReplay.Status);
        var archivedAdmissionReuse = await admission.AdmitAsync(new HumanReviewAdmissionCommand(store.Current.Id, store.Current.LifecycleVersion, Reissue(firstRequest, "first-review-reused-request", firstRequest.RequestOperationId), firstFrontier));
        Assert.Equal(CustomLoopRunStoreStatus.Conflict, archivedAdmissionReuse.Status);

        var duplicateArchivedRequest = Reissue(second.CompletedReviews[0].Request, "first-review-duplicate-request", second.Request.RequestOperationId);
        var duplicateArchived = second.CompletedReviews[0] with { Request = duplicateArchivedRequest };
        var duplicateAdmissionIdentity = store.Current with { HumanReview = second with { CompletedReviews = [duplicateArchived] } };
        var duplicateAdmissionValidation = CustomLoopRunValidator.Validate(duplicateAdmissionIdentity);
        Assert.Contains(duplicateAdmissionValidation.Errors, error => error.Code == "duplicate_human_review_request_operation_identity");

        var archivedReplay = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionTestAuthorizer { ReviewerRoleId = "governed-reviewer", ScopeIds = ["review-scope-one"] },
            new HumanReviewDecisionTestClock(store.Current.UpdatedAtUtc.AddMinutes(1))).DecideAsync(
                new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-first-review", HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, archivedReplay.Status);
        Assert.Equal(firstRequest.RequestHash, archivedReplay.Receipt?.Request.RequestHash);

        var archivedReuse = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionTestAuthorizer { ReviewerRoleId = "governed-reviewer", ScopeIds = ["review-scope-one"] },
            new HumanReviewDecisionTestClock(store.Current.UpdatedAtUtc.AddMinutes(1))).DecideAsync(
                new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-first-review", HumanReviewDecisionKind.Reject, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, archivedReuse.Status);
        Assert.Equal(HumanReviewDecisionKind.Approve, Assert.Single(Assert.IsType<HumanReviewRunState>(store.Current.HumanReview).CompletedReviews).AcceptedTerminalDecision?.Kind);

        var replayedRelease = await release.ReleaseAsync(action, completion);
        Assert.Equal(HumanReviewContinuationReleaseStatus.Completed, replayedRelease.Status);
        Assert.Equal(completion.ReleaseReceipt.ReleaseOperationId, replayedRelease.Completion?.ReleaseReceipt.ReleaseOperationId);
        Assert.Equal(second.Request.RequestHash, Assert.IsType<HumanReviewRunState>(store.Current.HumanReview).Request.RequestHash);

        var secondDecision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionTestAuthorizer { ReviewerRoleId = "governed-reviewer", ScopeIds = ["review-scope-two"] },
            new HumanReviewDecisionTestClock(store.Current.UpdatedAtUtc.AddMinutes(1))).DecideAsync(
                new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-second-review", HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, secondDecision.Status);
        Assert.Equal(firstRequest.RequestHash, Assert.Single(Assert.IsType<HumanReviewRunState>(store.Current.HumanReview).CompletedReviews).Request.RequestHash);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));

        var archivedParkingIndex = Array.FindIndex(store.Current.Events.ToArray(), item => item.SequentialNodeEvidence?.Kind == CustomLoopSequentialNodeEvidenceKind.ReviewRequested && string.Equals(item.SequentialNodeEvidence.NodeId, "human-review-one", StringComparison.Ordinal));
        Assert.True(archivedParkingIndex >= 0);
        var archivedParking = store.Current.Events[archivedParkingIndex];
        var substitutedParking = archivedParking with { StepId = "human-review-two", SequentialNodeEvidence = null };
        substitutedParking = substitutedParking with
        {
            SequentialNodeEvidence = CustomLoopSequentialNodeEvidenceHash.Apply(archivedParking.SequentialNodeEvidence! with
            {
                NodeId = "human-review-two",
                OutcomeArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(substitutedParking),
                EvidenceHash = string.Empty,
            }),
        };
        var malformedEvents = store.Current.Events.ToArray();
        malformedEvents[archivedParkingIndex] = substitutedParking;
        var malformedArchive = store.Current with { Events = malformedEvents };
        var malformedValidation = CustomLoopRunValidator.Validate(malformedArchive);
        Assert.False(malformedValidation.IsValid);
        Assert.Contains(malformedValidation.Errors, error => error.Code == "invalid_human_review_completed_admission_binding");
    }

    private static GovernedLoopGraphRevisionArtifact TwoHumanReviewArtifact(ContextualRoleRevisionPin role)
        => GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                HumanReviewNode("human-review-one", "review-scope-one"),
                HumanReviewNode("human-review-two", "review-scope-two"),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
                new GovernedLoopNodeDefinition("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>(StringComparer.Ordinal), null, null, null, null),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-review-one", "trigger", "human-review-one", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-review-one-to-human-review-two", "human-review-one", "human-review-two", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-review-two-to-exit", "human-review-two", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-review-two-to-fail", "human-review-two", "fail", GovernedLoopControlCondition.Failure),
            ],
            ["exit", "fail"],
            role,
            bindings: [new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));

    private static GovernedLoopNodeDefinition HumanReviewNode(string id, string scope)
        => new(
            id,
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, GovernedLoopHumanReviewVocabulary.TypeId, GovernedLoopHumanReviewVocabulary.DescriptorVersion),
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerPolicyId,
                [GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                [GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter] = scope,
            },
            null,
            null,
            null,
            null);

    private static HumanReviewRequest Reissue(HumanReviewRequest request, string requestId, string operationId)
        => HumanReviewContractHash.ApplyRequest(request with
        {
            RequestId = requestId,
            RequestOperationId = operationId,
            Provenance = request.Provenance with { CorrelationId = operationId, ProvenanceHash = string.Empty },
            RequestHash = string.Empty,
        });
}
