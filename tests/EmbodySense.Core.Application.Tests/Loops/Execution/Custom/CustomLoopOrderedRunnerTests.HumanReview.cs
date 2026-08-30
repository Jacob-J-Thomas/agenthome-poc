using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.HumanReview;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Human_review_node_atomically_parks_the_exact_frontier_before_its_request_is_observable()
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var nodeEvidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(
                store,
                new QueueExecutor(),
                new RecordingPublisher(),
                humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            nodeEvidence,
            nodeEvidence);

        var result = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Paused, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, store.Current.Frontier!.Payload.Status);
        var activation = Assert.Single(store.Current.Frontier.Payload.Nodes, item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        Assert.Equal(GovernedLoopNodeExecutionStatus.ReviewBlocked, activation.Status);
        var parked = Assert.Single(store.Current.Events, item => string.Equals(item.EventId, activation.OutcomeEvidenceId, StringComparison.Ordinal));
        Assert.Equal(CustomLoopRunEventKind.NodeOutcomeObserved, parked.Kind);
        Assert.Equal(CustomLoopSequentialNodeEvidenceKind.ReviewRequested, parked.SequentialNodeEvidence?.Kind);
        Assert.Equal(CustomLoopSequentialNodeDisposition.ReviewPending, parked.SequentialNodeEvidence?.Disposition);
        Assert.True(CustomLoopSequentialOutcomeArtifactHash.Matches(parked));
        Assert.Equal(parked.SequentialNodeEvidence!.OutcomeArtifactHash, activation.OutcomeEvidenceHash);
        Assert.Equal(1, store.Current.Events.Count(item => item.Kind == CustomLoopRunEventKind.HumanReviewRequestAdmitted));
        Assert.Equal(store.Current.Frontier.Payload.ContentHash, store.Current.HumanReview!.Request.Binding.FrontierHash);
        Assert.Equal(["review-scope-one"], Assert.Single(store.Current.HumanReview.Request.EligibleReviewers).ScopeIds.ToArray());
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
    }

    [Fact]
    public async Task Authenticated_evidence_allows_one_historical_review_request_before_one_terminal_outcome_and_rejects_duplicates()
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.Paused, parked.Status);
        var original = Assert.Single(store.Current.Events, item => item.SequentialNodeEvidence?.Kind == CustomLoopSequentialNodeEvidenceKind.ReviewRequested);
        var activation = Assert.Single(store.Current.Frontier!.Payload.Nodes, item => item.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var dispatch = new GovernedLoopSequentialNodeDispatchRequest(
            GovernedLoopSequentialNodeDispatchRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Plan.Nodes.Single(node => node.NodeId == activation.NodeId),
            activation,
            activation.Attempt!.Value);
        var retention = new GovernedLoopSequentialOrderedNodeEvidenceRequest(
            GovernedLoopSequentialOrderedNodeEvidenceRequest.CurrentSchemaVersion,
            dispatch,
            GovernedLoopSequentialNodeHandlerResultStatus.ReviewPending,
            store.Current.LifecycleVersion,
            original.Sequence,
            original.EventId);
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.ReviewPending, (await evidence.RetainAsync(retention)).Status);

        var duplicateParking = RehashReviewEvidence(original, CustomLoopSequentialNodeEvidenceKind.ReviewRequested, CustomLoopSequentialNodeDisposition.ReviewPending, original.Sequence + 1, "duplicate-review-request");
        var parkedWithDuplicate = store.Current with
        {
            LifecycleVersion = store.Current.LifecycleVersion + 1,
            UpdatedAtUtc = duplicateParking.TimestampUtc,
            Events = [.. store.Current.Events, duplicateParking],
        };
        store.ReplaceCurrent(parkedWithDuplicate, validate: false);
        var duplicateParkingRequest = retention with { OrderedLifecycleVersion = parkedWithDuplicate.LifecycleVersion, OrderedEventSequence = duplicateParking.Sequence, OrderedEventId = duplicateParking.EventId };
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, (await evidence.RetainAsync(duplicateParkingRequest)).Status);

        var parkedRun = store.Current;
        store.ReplaceCurrent(parkedRun with { Events = parkedRun.Events[..^1], LifecycleVersion = parkedRun.LifecycleVersion - 1, UpdatedAtUtc = original.TimestampUtc }, validate: false);
        var terminal = RehashReviewEvidence(original, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed, original.Sequence + 1, "review-terminal-outcome");
        var terminalRun = store.Current with
        {
            LifecycleVersion = store.Current.LifecycleVersion + 1,
            UpdatedAtUtc = terminal.TimestampUtc,
            Events = [.. store.Current.Events, terminal],
        };
        store.ReplaceCurrent(terminalRun, validate: false);
        var terminalRequest = retention with
        {
            Disposition = GovernedLoopSequentialNodeHandlerResultStatus.Completed,
            OrderedLifecycleVersion = terminalRun.LifecycleVersion,
            OrderedEventSequence = terminal.Sequence,
            OrderedEventId = terminal.EventId,
        };
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Completed, (await evidence.RetainAsync(terminalRequest)).Status);

        var duplicateTerminal = RehashReviewEvidence(terminal, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, CustomLoopSequentialNodeDisposition.Completed, terminal.Sequence + 1, "duplicate-review-terminal");
        var terminalRunWithDuplicate = store.Current with
        {
            LifecycleVersion = store.Current.LifecycleVersion + 1,
            UpdatedAtUtc = duplicateTerminal.TimestampUtc,
            Events = [.. store.Current.Events, duplicateTerminal],
        };
        store.ReplaceCurrent(terminalRunWithDuplicate, validate: false);
        var duplicateTerminalRequest = terminalRequest with { OrderedLifecycleVersion = terminalRunWithDuplicate.LifecycleVersion, OrderedEventSequence = duplicateTerminal.Sequence, OrderedEventId = duplicateTerminal.EventId };
        Assert.Equal(GovernedLoopSequentialNodeHandlerResultStatus.Unknown, (await evidence.RetainAsync(duplicateTerminalRequest)).Status);
    }

    [Fact]
    public async Task Approved_human_review_race_and_replay_release_one_exact_frontier_without_a_second_runtime_reentry()
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var initialRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await initialRuntime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.True(parked.Status == CustomLoopOrderedRunStatus.Paused, parked.Detail);

        var approvalAtUtc = store.Current.UpdatedAtUtc.AddMinutes(1);
        var authorizer = new HumanReviewDecisionTestAuthorizer
        {
            ReviewerRoleId = "governed-reviewer",
            ScopeIds = ["review-scope-one"],
        };
        var decision = await new HumanReviewDecisionService(store, authorizer, new HumanReviewDecisionTestClock(approvalAtUtc)).DecideAsync(
            new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-human-review-one", HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);

        var approved = store.Current;
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var terminalDecision = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        var wakeAtUtc = approved.UpdatedAtUtc.AddTicks(1);
        var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            HumanReviewContinuationWake.CurrentSchemaVersion,
            "human-review-wake-one",
            new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
            new HumanReviewDecisionReference(terminalDecision.DecisionId, terminalDecision.DecisionOperationId, terminalDecision.Kind, terminalDecision.DecisionHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            review.Request.Binding.BindingHash,
            approved.SequentialAdapterBinding!.ExecutionBinding.ExecutionGeneration,
            wakeAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "human-review-wake-one", wakeAtUtc, string.Empty)),
            string.Empty));
        var claimAtUtc = wakeAtUtc.AddTicks(1);
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            HumanReviewContinuationClaim.CurrentSchemaVersion,
            "human-review-claim-one",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "human-review-worker",
            claimAtUtc,
            claimAtUtc.AddMinutes(1),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "human-review-claim-one", claimAtUtc, string.Empty)),
            string.Empty));
        var publishedContinuation = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(
            HumanReviewContinuationState.CurrentSchemaVersion,
            wake,
            ImmutableArray<HumanReviewContinuationClaim>.Empty,
            null,
            null,
            string.Empty));
        var published = approved with
        {
            LifecycleVersion = approved.LifecycleVersion + 1,
            UpdatedAtUtc = wakeAtUtc,
            HumanReview = review with { Continuation = publishedContinuation },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(approved, published).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(approved, published).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(published, approved.LifecycleVersion)).Status);
        var continuation = HumanReviewContinuationContractHash.ApplyState(publishedContinuation with { Claims = ImmutableArray.Create(claim), StateHash = string.Empty });
        var claimed = published with
        {
            LifecycleVersion = published.LifecycleVersion + 1,
            UpdatedAtUtc = claimAtUtc,
            HumanReview = published.HumanReview! with { Continuation = continuation },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(published, claimed).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(published, claimed).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(claimed, published.LifecycleVersion)).Status);

        var request = new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash);
        var decisionReference = new HumanReviewDecisionReference(terminalDecision.DecisionId, terminalDecision.DecisionOperationId, terminalDecision.Kind, terminalDecision.DecisionHash);
        var wakeReference = new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash);
        var claimReference = new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash);
        var reservationReference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
        var releaseOperationId = HumanReviewContinuationReleaseOperationId.Create(request, wakeReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.Continuation);
        Assert.NotNull(releaseOperationId);
        var releaseReceipt = new HumanReviewContinuationReleaseReceiptIntent(releaseOperationId!, request, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.Continuation, null);
        var action = new HumanReviewContinuationActionIntent(
            HumanReviewContinuationAction.ReleaseContinuation,
            store.Current.Id,
            store.Current.LifecycleVersion,
            request,
            decisionReference,
            wakeReference,
            claimReference,
            reservationReference,
            wake.ExpectedGeneration,
            null,
            releaseReceipt);
        var completionIntent = new HumanReviewContinuationCompletionIntent(store.Current.Id, store.Current.LifecycleVersion, request, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, releaseReceipt);
        var reentry = new HumanReviewOrderedReleaseTestRuntime((request, cancellationToken) => ConfirmHumanReviewHandoffAsync(store, request, cancellationToken));
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            reentry,
            new FixedTimeProvider(claimAtUtc.AddTicks(1)),
            new RecordingAuthoritySource(
                HumanReviewContinuationAuthorityReadStatus.Current,
                HumanReviewContinuationAuthorityReadStatus.Current,
                HumanReviewContinuationAuthorityReadStatus.Current,
                HumanReviewContinuationAuthorityReadStatus.Current));
        var bypassReceipt = releaseReceipt with { ReleaseOperationId = "human-review-release-bypass" };
        var bypass = await release.ReleaseAsync(action with { ReleaseReceipt = bypassReceipt }, completionIntent with { ReleaseReceipt = bypassReceipt });
        Assert.Equal(HumanReviewContinuationReleaseStatus.Invalid, bypass.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store.Current.Status);

        var firstWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedWrites = 0;
        store.BeforeUpdateAsync = async (_, _) =>
        {
            if (Interlocked.CompareExchange(ref delayedWrites, 1, 0) == 0)
            {
                firstWriteEntered.TrySetResult();
                await releaseFirstWrite.Task;
            }
        };

        var delayed = release.ReleaseAsync(action, completionIntent);
        await firstWriteEntered.Task;
        var winner = await release.ReleaseAsync(action, completionIntent);
        releaseFirstWrite.TrySetResult();
        var loser = await delayed;
        var replay = await release.ReleaseAsync(action, completionIntent);

        Assert.Equal(HumanReviewContinuationReleaseStatus.Completed, winner.Status);
        Assert.Equal(winner.Completion?.CompletionHash, loser.Completion?.CompletionHash);
        Assert.Equal(winner.Completion?.CompletionHash, replay.Completion?.CompletionHash);
        Assert.Equal(1, reentry.ResumeCount);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Active, store.Current.Frontier?.Payload.Status);
        var releasedNode = Assert.Single(store.Current.Frontier!.Payload.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Completed, releasedNode.Status);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.RequestInformation, CustomLoopRunStatus.Paused, GovernedLoopFrontierStatus.ReviewBlocked, GovernedLoopNodeExecutionStatus.ReviewBlocked)]
    [InlineData(HumanReviewDecisionKind.Reject, CustomLoopRunStatus.Running, GovernedLoopFrontierStatus.Active, GovernedLoopNodeExecutionStatus.Failed)]
    [InlineData(HumanReviewDecisionKind.Cancel, CustomLoopRunStatus.Cancelled, GovernedLoopFrontierStatus.Cancelled, GovernedLoopNodeExecutionStatus.ReviewBlocked)]
    public async Task Nonapproval_human_review_decision_uses_the_same_release_service_without_runtime_reentry(
        HumanReviewDecisionKind kind,
        CustomLoopRunStatus expectedRunStatus,
        GovernedLoopFrontierStatus expectedFrontierStatus,
        GovernedLoopNodeExecutionStatus expectedNodeStatus)
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var initialRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await initialRuntime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.True(parked.Status == CustomLoopOrderedRunStatus.Paused, $"{parked.Status}: {parked.Detail}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Message))}");

        var intent = await PrepareClaimedDecisionActionAsync(store, kind);
        var reentry = new HumanReviewOrderedReleaseTestRuntime((request, cancellationToken) => ConfirmHumanReviewHandoffAsync(store, request, cancellationToken));
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            reentry,
            new FixedTimeProvider(store.Current.UpdatedAtUtc.AddTicks(1)),
            authority);
        var bypass = await release.ReleaseAsync(intent with { ActionOperationId = "action-release-bypass" });
        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Invalid, bypass.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store.Current.Status);

        var result = await release.ReleaseAsync(intent);
        var replay = await release.ReleaseAsync(intent);
        var bypassReplay = await release.ReleaseAsync(intent with { ActionOperationId = "action-release-bypass" });

        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Completed, result.Status);
        Assert.Equal(result.Completion?.CompletionHash, replay.Completion?.CompletionHash);
        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Invalid, bypassReplay.Status);
        Assert.Equal(expectedRunStatus, store.Current.Status);
        Assert.Equal(expectedFrontierStatus, store.Current.Frontier?.Payload.Status);
        Assert.Equal(expectedNodeStatus, Assert.Single(store.Current.Frontier!.Payload.Nodes, node => node.Descriptor.Kind == GovernedLoopNodeKind.HumanReview).Status);
        Assert.Equal(kind == HumanReviewDecisionKind.Reject ? 1 : 0, reentry.ResumeCount);
        Assert.Equal(2, authority.ReadCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.RequestInformation)]
    [InlineData(HumanReviewDecisionKind.Reject)]
    [InlineData(HumanReviewDecisionKind.Cancel)]
    public async Task Nonapproval_release_authority_drift_in_the_final_window_leaves_the_run_unchanged(HumanReviewDecisionKind kind)
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var initialRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await initialRuntime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, parked.Status);

        var intent = await PrepareClaimedDecisionActionAsync(store, kind);
        var before = store.Current;
        var beforeLifecycle = before.LifecycleVersion;
        var beforeEvents = before.Events;
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Unavailable);
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            new HumanReviewOrderedReleaseTestRuntime(),
            new FixedTimeProvider(store.Current.UpdatedAtUtc.AddTicks(1)),
            authority);

        var result = await release.ReleaseAsync(intent);

        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Unavailable, result.Status);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(beforeLifecycle, store.Current.LifecycleVersion);
        Assert.Equal(beforeEvents, store.Current.Events);
        Assert.Equal(before.Status, store.Current.Status);
        Assert.Equal(before.Frontier, store.Current.Frontier);
        Assert.Null(store.Current.HumanReview?.DecisionActions.Single(action => action.Reservation.ReservationHash == intent.Reservation.ReservationHash).Completion);
    }

    [Fact]
    public async Task Routed_reject_release_returns_unavailable_until_its_durable_ordered_handoff_is_observed_and_replay_retries_the_same_operation()
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var initialRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await initialRuntime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.True(parked.Status == CustomLoopOrderedRunStatus.Paused, $"{parked.Status}: {parked.Detail}; validation={string.Join(" | ", store.ValidationFailures.Select(item => item.Code + ":" + item.Message))}");

        var intent = await PrepareClaimedDecisionActionAsync(store, HumanReviewDecisionKind.Reject);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var unresolved = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            new HumanReviewOrderedReleaseTestRuntime(),
            new FixedTimeProvider(store.Current.UpdatedAtUtc.AddTicks(1)),
            authority);

        var first = await unresolved.ReleaseAsync(intent);

        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Unavailable, first.Status);
        Assert.Equal(CustomLoopRunStatus.Running, store.Current.Status);
        Assert.Equal(intent.ActionOperationId, store.Current.Events[^1].EventId);
        Assert.Single(store.Current.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);

        var confirmedRuntime = new HumanReviewOrderedReleaseTestRuntime((request, cancellationToken) => ConfirmHumanReviewHandoffAsync(store, request, cancellationToken));
        var replaying = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            confirmedRuntime,
            new FixedTimeProvider(store.Current.UpdatedAtUtc.AddTicks(1)),
            authority);

        var replay = await replaying.ReleaseAsync(intent);

        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Completed, replay.Status);
        Assert.Equal(1, confirmedRuntime.ResumeCount);
        Assert.NotEqual(intent.ActionOperationId, store.Current.Events[^1].EventId);
        Assert.Single(store.Current.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
    }

    [Fact]
    public async Task Pre_dispatch_effect_certainty_drift_in_the_final_window_leaves_the_run_unchanged_and_never_actuates()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var actuator = new PreDispatchApprovalWorkspaceActionExecutor();
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("bounded provider output")), workspaceActionExecutor: actuator, humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, parked.Status);
        var preparedEffect = Assert.IsType<GovernedLoopEffectAttempt>(actuator.PreparedEffectAttempt);
        var claimed = await PrepareClaimedPreDispatchEffectApprovalAsync(context, store);
        var review = Assert.IsType<HumanReviewRunState>(store.Current.HumanReview);
        var snapshot = HumanReviewEffectReleaseContract.Create(review.Request.Binding, preparedEffect, preparedEffect.Payload.UpdatedAtUtc);
        var receipt = new HumanReviewContinuationReleaseReceiptIntent(
            Assert.IsType<string>(HumanReviewContinuationReleaseOperationId.Create(claimed.Request, claimed.Wake, claimed.Reservation, claimed.ExpectedGeneration, HumanReviewContinuationReleaseKind.PreDispatchEffect)),
            claimed.Request,
            claimed.Wake,
            claimed.Claim,
            claimed.Reservation,
            claimed.ExpectedGeneration,
            HumanReviewContinuationReleaseKind.PreDispatchEffect,
            snapshot.SnapshotHash);
        var action = new HumanReviewContinuationActionIntent(
            HumanReviewContinuationAction.ReleaseEffect,
            store.Current.Id,
            store.Current.LifecycleVersion,
            claimed.Request,
            claimed.Decision,
            claimed.Wake,
            claimed.Claim,
            claimed.Reservation,
            claimed.ExpectedGeneration,
            new GovernedLoopEffectCertaintySnapshotQuery(
                HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, preparedEffect),
                HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, preparedEffect)),
            receipt);
        var completion = new HumanReviewContinuationCompletionIntent(store.Current.Id, store.Current.LifecycleVersion, claimed.Request, claimed.Wake, claimed.Claim, claimed.Reservation, claimed.ExpectedGeneration, receipt);
        var activation = Assert.Single(store.Current.Frontier!.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var node = Assert.Single(context.Plan.Nodes, candidate => string.Equals(candidate.NodeId, activation.NodeId, StringComparison.Ordinal));
        var transition = GovernedLoopSequentialFrontierMachine.ReleaseReviewBlockedRecoverableAction(store.Current.Frontier, store.Current.SequentialAdapterBinding, context.Plan, node, activation, activation.Attempt!.Value, activation.AttemptOperationId, claimed.ClaimedAtUtc.AddTicks(1));
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Status);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(
            new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, new HumanReviewCurrentEffectAttemptEvidence(HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, preparedEffect), HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, preparedEffect))),
            new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, new HumanReviewCurrentEffectAttemptEvidence(HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, preparedEffect), HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, preparedEffect))));
        var effectCertainty = new RecordingEffectCertaintySource(
            new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot),
            new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Unavailable));
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            runtime,
            new FixedTimeProvider(claimed.ClaimedAtUtc.AddTicks(1)),
            authority,
            effectEvidence,
            effectCertainty);
        var before = store.Current;

        var result = await release.ReleaseAsync(action, completion);

        Assert.Equal(HumanReviewContinuationReleaseStatus.Unavailable, result.Status);
        Assert.Equal(2, authority.ReadCount);
        Assert.Equal(2, effectEvidence.ReadCount);
        Assert.Equal(2, effectCertainty.ReadCount);
        Assert.Equal(before.LifecycleVersion, store.Current.LifecycleVersion);
        Assert.Equal(before.Events, store.Current.Events);
        Assert.Equal(before.Frontier, store.Current.Frontier);
        Assert.Single(actuator.Requests);
        Assert.Equal(0, actuator.ActuationCount);
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.RequestInformation)]
    [InlineData(HumanReviewDecisionKind.Reject)]
    [InlineData(HumanReviewDecisionKind.Cancel)]
    public async Task Pre_dispatch_nonapproval_final_authority_drift_leaves_the_run_unchanged(HumanReviewDecisionKind kind)
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var actuator = new PreDispatchApprovalWorkspaceActionExecutor();
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("bounded provider output")), workspaceActionExecutor: actuator, humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, parked.Status);
        Assert.Equal(HumanReviewPurpose.PreDispatchEffect, store.Current.HumanReview?.Request.Purpose);
        var intent = await PrepareClaimedDecisionActionAsync(store, kind);
        var before = store.Current;
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Unavailable);
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            new HumanReviewOrderedReleaseTestRuntime(),
            new FixedTimeProvider(store.Current.UpdatedAtUtc.AddTicks(1)),
            authority);

        var result = await release.ReleaseAsync(intent);

        Assert.Equal(kind == HumanReviewDecisionKind.Reject ? HumanReviewDecisionActionReleaseStatus.Invalid : HumanReviewDecisionActionReleaseStatus.Unavailable, result.Status);
        Assert.Equal(kind == HumanReviewDecisionKind.Reject ? 1 : 2, authority.ReadCount);
        Assert.Equal(before.LifecycleVersion, store.Current.LifecycleVersion);
        Assert.Equal(before.Events, store.Current.Events);
        Assert.Equal(before.Frontier, store.Current.Frontier);
        Assert.Equal(before.Status, store.Current.Status);
        Assert.Equal(0, actuator.ActuationCount);
    }

    [Fact]
    public async Task Pre_dispatch_workspace_effect_approval_releases_the_exact_prepared_effect_once_and_replay_never_redispatches()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => GovernedLoopSequentialApplicationTestFixture.WorkspaceActionArtifact(owningRole: role));
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var actuator = new PreDispatchApprovalWorkspaceActionExecutor();
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(Result("bounded provider output")), workspaceActionExecutor: actuator, humanReviewAdmissionService: new HumanReviewAdmissionService(store)),
            evidence,
            evidence);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.True(parked.Status == CustomLoopOrderedRunStatus.Paused, $"{parked.Status}: {parked.Detail}; failure={store.Current.FailureCode}; writes={string.Join(" | ", store.Writes.Select(run => run.Status + "/" + run.Frontier?.Payload.Status + "/" + run.HumanReview?.Request.Purpose + "/" + run.Events[^1].Kind))}; validation={string.Join(" | ", store.ValidationFailures.Select(error => error.Code + ":" + error.Message))}");
        Assert.Equal(HumanReviewPurpose.PreDispatchEffect, store.Current.HumanReview?.Request.Purpose);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, store.Current.Frontier?.Payload.Status);
        Assert.Single(actuator.Requests);
        Assert.Equal(0, actuator.ActuationCount);
        var preparedEffect = Assert.IsType<GovernedLoopEffectAttempt>(actuator.PreparedEffectAttempt);

        var claimed = await PrepareClaimedPreDispatchEffectApprovalAsync(context, store);
        var review = Assert.IsType<HumanReviewRunState>(store.Current.HumanReview);
        var snapshot = HumanReviewEffectReleaseContract.Create(review.Request.Binding, preparedEffect, preparedEffect.Payload.UpdatedAtUtc);
        var receipt = new HumanReviewContinuationReleaseReceiptIntent(
            Assert.IsType<string>(HumanReviewContinuationReleaseOperationId.Create(claimed.Request, claimed.Wake, claimed.Reservation, claimed.ExpectedGeneration, HumanReviewContinuationReleaseKind.PreDispatchEffect)),
            claimed.Request,
            claimed.Wake,
            claimed.Claim,
            claimed.Reservation,
            claimed.ExpectedGeneration,
            HumanReviewContinuationReleaseKind.PreDispatchEffect,
            snapshot.SnapshotHash);
        var action = new HumanReviewContinuationActionIntent(
            HumanReviewContinuationAction.ReleaseEffect,
            store.Current.Id,
            store.Current.LifecycleVersion,
            claimed.Request,
            claimed.Decision,
            claimed.Wake,
            claimed.Claim,
            claimed.Reservation,
            claimed.ExpectedGeneration,
            new GovernedLoopEffectCertaintySnapshotQuery(
                HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, preparedEffect),
                HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, preparedEffect)),
            receipt);
        var completion = new HumanReviewContinuationCompletionIntent(store.Current.Id, store.Current.LifecycleVersion, claimed.Request, claimed.Wake, claimed.Claim, claimed.Reservation, claimed.ExpectedGeneration, receipt);
        var activation = Assert.Single(store.Current.Frontier!.Payload.Nodes, node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var node = Assert.Single(context.Plan.Nodes, candidate => string.Equals(candidate.NodeId, activation.NodeId, StringComparison.Ordinal));
        var transition = GovernedLoopSequentialFrontierMachine.ReleaseReviewBlockedRecoverableAction(store.Current.Frontier, store.Current.SequentialAdapterBinding, context.Plan, node, activation, activation.Attempt!.Value, activation.AttemptOperationId, claimed.ClaimedAtUtc.AddTicks(1));
        Assert.True(transition.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied, transition.Detail);
        var authority = new RecordingAuthoritySource(HumanReviewContinuationAuthorityReadStatus.Current, HumanReviewContinuationAuthorityReadStatus.Current);
        var effectEvidence = new RecordingEffectEvidenceSource(
            new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, new HumanReviewCurrentEffectAttemptEvidence(HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, preparedEffect), HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, preparedEffect))),
            new HumanReviewCurrentEffectAttemptEvidenceReadResult(HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current, new HumanReviewCurrentEffectAttemptEvidence(HumanReviewEffectReleaseContract.CreateIdentity(review.Request.Binding, preparedEffect), HumanReviewEffectReleaseContract.CreatePreparation(review.Request.Binding, preparedEffect))));
        var effectCertainty = new RecordingEffectCertaintySource(
            new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot),
            new GovernedLoopEffectCertaintySnapshotResult(GovernedLoopEffectCertaintySnapshotStatus.Current, snapshot));
        var release = new HumanReviewOrderedReleaseService(
            store,
            new HumanReviewOrderedReleaseTestContextResolver(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact)),
            runtime,
            new FixedTimeProvider(claimed.ClaimedAtUtc.AddTicks(1)),
            authority,
            effectEvidence,
            effectCertainty);

        var first = await release.ReleaseAsync(action, completion);
        Assert.True(actuator.Requests.Count == 2, $"first={first.Status}; actuator={actuator.ActuationCount}; requests={string.Join(",", actuator.Requests.Select(request => request.HumanReviewRelease is null ? "none" : "release"))}; status={store.Current.Status}; failure={store.Current.FailureCode}:{store.Current.FailureDetail}; frontier={store.Current.Frontier?.Payload.Status}; events={string.Join(",", store.Current.Events.Select(item => item.Kind + ":" + item.EventId))}; validation={string.Join(" | ", CustomLoopRunValidator.Validate(store.Current).Errors)}");
        var replay = await release.ReleaseAsync(action, completion);
        var directResume = await runtime.ResumeAsync(new GovernedLoopSequentialOrderedResumeRequest(
            GovernedLoopSequentialOrderedResumeRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            store.Current.LifecycleVersion,
            action.ReleaseReceipt!.ReleaseOperationId,
            AuditSchema.Actors.Web));

        Assert.Equal(2, authority.ReadCount);
        Assert.True(first.Status == HumanReviewContinuationReleaseStatus.Completed, $"{first.Status}; authority={authority.ReadCount}; effect={effectEvidence.ReadCount}; certainty={effectCertainty.ReadCount}; lifecycle={store.Current.LifecycleVersion}; frontier={store.Current.Frontier?.Payload.Status}; actuator={actuator.ActuationCount}; requests={string.Join(",", actuator.Requests.Select(request => request.HumanReviewRelease is null ? "none" : "release"))}; events={string.Join(",", store.Current.Events.Select(item => item.Kind + ":" + item.EventId))}; resume={directResume.Status}:{directResume.Detail}; validation={string.Join(" | ", CustomLoopRunValidator.Validate(store.Current).Errors)}");
        Assert.Equal(first.Completion?.CompletionHash, replay.Completion?.CompletionHash);
        Assert.Equal(2, actuator.Requests.Count);
        Assert.Equal(1, actuator.ActuationCount);
        Assert.NotNull(actuator.Requests[1].HumanReviewRelease);
        var completedEvidence = store.Current.Events.LastOrDefault(item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted)?.SequentialNodeEvidence;
        var finalActivation = completedEvidence is null ? null : store.Current.Frontier?.Payload.Nodes.ElementAtOrDefault(completedEvidence.ActivationOrdinal);
        var activationMatches = finalActivation is not null
            && completedEvidence is not null
            && finalActivation.ActivationOrdinal == completedEvidence.ActivationOrdinal
            && finalActivation.VisitOrdinal == completedEvidence.VisitOrdinal
            && string.Equals(finalActivation.NodeId, completedEvidence.NodeId, StringComparison.Ordinal)
            && string.Equals(finalActivation.CycleId, completedEvidence.CycleId, StringComparison.Ordinal)
            && finalActivation.CycleIteration == completedEvidence.CycleIteration
            && finalActivation.Attempt == completedEvidence.Attempt;
        var routeMatches = finalActivation is not null
            && completedEvidence is not null
            && completedEvidence.SelectedControlEdgeIds.Concat(completedEvidence.SkippedControlEdgeIds).Order(StringComparer.Ordinal)
                .SequenceEqual(finalActivation.OutgoingControlEdgeIds, StringComparer.Ordinal);
        Assert.True(store.Current.IsTerminal, $"status={store.Current.Status}; frontier={store.Current.Frontier?.Payload.Status}; failure={store.Current.FailureCode}:{store.Current.FailureDetail}; activationMatches={activationMatches}; routeMatches={routeMatches}; activation={finalActivation?.ActivationOrdinal}/{finalActivation?.PlanOrdinal}/{finalActivation?.NodeId}/[{finalActivation?.CycleId ?? "<null>"}]/{finalActivation?.CycleIteration?.ToString() ?? "<null>"}/{finalActivation?.VisitOrdinal}/{finalActivation?.Status}/{finalActivation?.Attempt}/{finalActivation?.AttemptOperationId}/{finalActivation?.OutcomeEvidenceId}/{finalActivation?.ControlOutcome}/{string.Join(",", finalActivation?.OutgoingControlEdgeIds ?? [])}/{string.Join(",", finalActivation?.SelectedControlEdgeIds ?? [])}/{string.Join(",", finalActivation?.SkippedControlEdgeIds ?? [])}; completed={completedEvidence?.Kind}/{completedEvidence?.Disposition}/{completedEvidence?.ActivationOrdinal}/{completedEvidence?.NodeId}/[{completedEvidence?.CycleId ?? "<null>"}]/{completedEvidence?.CycleIteration?.ToString() ?? "<null>"}/{completedEvidence?.VisitOrdinal}/{completedEvidence?.Attempt}/{completedEvidence?.OutcomeArtifactHash}/{completedEvidence?.ControlOutcome}/{string.Join(",", completedEvidence?.SelectedControlEdgeIds ?? [])}/{string.Join(",", completedEvidence?.SkippedControlEdgeIds ?? [])}; nodes={string.Join(";", store.Current.Frontier?.Payload.Nodes.Select(item => item.ActivationOrdinal + "/" + item.PlanOrdinal + "/" + item.NodeId + "/" + item.Status + "/" + item.Attempt + "/" + item.OutcomeEvidenceId + "/" + item.ControlOutcome + "/" + string.Join(",", item.OutgoingControlEdgeIds) + "/" + string.Join(",", item.SelectedControlEdgeIds) + "/" + string.Join(",", item.SkippedControlEdgeIds)) ?? [])}; events={string.Join(",", store.Current.Events.Select(item => item.Kind + ":" + item.EventId))}; validation={string.Join(" | ", store.ValidationFailures.Select(error => error.Code + ":" + error.Field + ":" + error.Message))}");
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(store.Current).Errors));
    }

    private static async Task<HumanReviewDecisionActionIntent> PrepareClaimedDecisionActionAsync(FakeRunStore store, HumanReviewDecisionKind kind)
    {
        var approvedAtUtc = store.Current.UpdatedAtUtc.AddMinutes(1);
        var isPreDispatchEffect = store.Current.HumanReview?.Request.Purpose == HumanReviewPurpose.PreDispatchEffect;
        var authorizer = new HumanReviewDecisionTestAuthorizer
        {
            ReviewerRoleId = isPreDispatchEffect ? GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId : "governed-reviewer",
            ScopeIds = [isPreDispatchEffect ? "pre-dispatch-effect" : "review-scope-one"],
        };
        var decision = await new HumanReviewDecisionService(store, authorizer, new HumanReviewDecisionTestClock(approvedAtUtc)).DecideAsync(
            new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "decision-" + kind.ToString().ToLowerInvariant(), kind, kind == HumanReviewDecisionKind.RequestInformation ? "Need a redacted clarification." : null));
        Assert.Equal(kind == HumanReviewDecisionKind.RequestInformation ? HumanReviewDecisionServiceStatus.InformationRequested : HumanReviewDecisionServiceStatus.Accepted, decision.Status);

        var decided = store.Current;
        var review = Assert.IsType<HumanReviewRunState>(decided.HumanReview);
        var action = Assert.Single(review.DecisionActions);
        var reservation = new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash);
        var request = new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash);
        var wakeAtUtc = decided.UpdatedAtUtc.AddTicks(1);
        var wake = new HumanReviewDecisionActionWake(
            HumanReviewDecisionActionWake.CurrentSchemaVersion,
            "action-wake-" + kind.ToString().ToLowerInvariant(),
            request,
            action.Reservation.Decision,
            reservation,
            action.BindingHash,
            action.ExpectedGeneration,
            wakeAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "action-wake-" + kind.ToString().ToLowerInvariant(), wakeAtUtc, string.Empty)),
            string.Empty);
        var publishedAction = HumanReviewDecisionActionContractHash.ApplyState(action with { Wake = wake, StateHash = string.Empty });
        var published = decided with
        {
            LifecycleVersion = decided.LifecycleVersion + 1,
            UpdatedAtUtc = wakeAtUtc,
            HumanReview = review with { DecisionActions = review.DecisionActions.SetItem(0, publishedAction) },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(decided, published).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(decided, published).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(published, decided.LifecycleVersion)).Status);

        var claimAtUtc = wakeAtUtc.AddTicks(1);
        var claim = new HumanReviewDecisionActionClaim(
            HumanReviewDecisionActionClaim.CurrentSchemaVersion,
            "action-claim-" + kind.ToString().ToLowerInvariant(),
            new HumanReviewDecisionActionWakeReference(publishedAction.Wake!.WakeId, publishedAction.Wake.WakeHash),
            reservation,
            publishedAction.ExpectedGeneration,
            "action-worker",
            claimAtUtc,
            claimAtUtc.AddMinutes(1),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "action-claim-" + kind.ToString().ToLowerInvariant(), claimAtUtc, string.Empty)),
            string.Empty);
        var claimedAction = HumanReviewDecisionActionContractHash.ApplyState(publishedAction with { Claims = ImmutableArray.Create(claim), StateHash = string.Empty });
        var claimed = published with
        {
            LifecycleVersion = published.LifecycleVersion + 1,
            UpdatedAtUtc = claimAtUtc,
            HumanReview = published.HumanReview! with { DecisionActions = published.HumanReview.DecisionActions.SetItem(0, claimedAction) },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(published, claimed).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(published, claimed).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(claimed, published.LifecycleVersion)).Status);

        return new HumanReviewDecisionActionIntent(
            claimed.Id,
            claimed.LifecycleVersion,
            request,
            claimedAction.Reservation.Decision,
            new HumanReviewDecisionActionWakeReference(claimedAction.Wake!.WakeId, claimedAction.Wake.WakeHash),
            new HumanReviewDecisionActionClaimReference(claimedAction.Claims[^1].ClaimId, claimedAction.Claims[^1].ClaimHash),
            reservation,
            claimedAction.ExpectedGeneration,
            ActionReleaseOperationId(claimedAction.Reservation.ReservationHash));
    }

    private static async Task<ClaimedPreDispatchApproval> PrepareClaimedPreDispatchEffectApprovalAsync(SequentialTestContext context, FakeRunStore store)
    {
        var decidedAtUtc = store.Current.UpdatedAtUtc.AddTicks(1);
        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionTestAuthorizer
            {
                ReviewerRoleId = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                ScopeIds = ["pre-dispatch-effect"],
            },
            new HumanReviewDecisionTestClock(decidedAtUtc)).DecideAsync(
                new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-pre-dispatch-effect", HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);

        var approved = store.Current;
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var accepted = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        var request = new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash);
        var decisionReference = new HumanReviewDecisionReference(accepted.DecisionId, accepted.DecisionOperationId, accepted.Kind, accepted.DecisionHash);
        var reservationReference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
        var wakeAtUtc = approved.UpdatedAtUtc.AddTicks(1);
        var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            HumanReviewContinuationWake.CurrentSchemaVersion,
            "pre-dispatch-effect-wake",
            request,
            decisionReference,
            reservationReference,
            review.Request.Binding.BindingHash,
            approved.SequentialAdapterBinding!.ExecutionBinding.ExecutionGeneration,
            wakeAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "pre-dispatch-effect-wake", wakeAtUtc, string.Empty)),
            string.Empty));
        var publishedState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(
            HumanReviewContinuationState.CurrentSchemaVersion,
            wake,
            ImmutableArray<HumanReviewContinuationClaim>.Empty,
            null,
            null,
            string.Empty));
        var published = approved with
        {
            LifecycleVersion = approved.LifecycleVersion + 1,
            UpdatedAtUtc = wakeAtUtc,
            HumanReview = review with { Continuation = publishedState },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(approved, published).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(approved, published).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(published, approved.LifecycleVersion)).Status);

        var claimedAtUtc = wakeAtUtc.AddTicks(1);
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            HumanReviewContinuationClaim.CurrentSchemaVersion,
            "pre-dispatch-effect-claim",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            reservationReference,
            wake.ExpectedGeneration,
            "pre-dispatch-effect-worker",
            claimedAtUtc,
            claimedAtUtc.AddMinutes(1),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "pre-dispatch-effect-claim", claimedAtUtc, string.Empty)),
            string.Empty));
        var claimedState = HumanReviewContinuationContractHash.ApplyState(publishedState with { Claims = ImmutableArray.Create(claim), StateHash = string.Empty });
        var claimed = published with
        {
            LifecycleVersion = published.LifecycleVersion + 1,
            UpdatedAtUtc = claimedAtUtc,
            HumanReview = published.HumanReview! with { Continuation = claimedState },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(published, claimed).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(published, claimed).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(claimed, published.LifecycleVersion)).Status);
        return new ClaimedPreDispatchApproval(request, decisionReference, new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash), reservationReference, wake.ExpectedGeneration, claimedAtUtc);
    }

    private static async Task<ClaimedHumanReviewApproval> PrepareClaimedHumanReviewApprovalAsync(IHumanReviewAdmissionService? admissionService = null, DateTimeOffset? wakeExpiresAtUtc = null, DateTimeOffset? claimLeaseExpiresAtUtc = null)
    {
        var context = await HumanReviewContextAsync();
        var store = new FakeRunStore(context.Run);
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var initialRuntime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, new QueueExecutor(), new RecordingPublisher(), humanReviewAdmissionService: admissionService ?? new HumanReviewAdmissionService(store)),
            evidence,
            evidence);
        var parked = await initialRuntime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.Paused, parked.Status);

        var approvalAtUtc = store.Current.UpdatedAtUtc.AddMinutes(1);
        var authorizer = new HumanReviewDecisionTestAuthorizer
        {
            ReviewerRoleId = "governed-reviewer",
            ScopeIds = ["review-scope-one"],
        };
        var decision = await new HumanReviewDecisionService(store, authorizer, new HumanReviewDecisionTestClock(approvalAtUtc)).DecideAsync(
            new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "approve-human-review-one", HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);

        var approved = store.Current;
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var terminalDecision = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        var wakeAtUtc = approved.UpdatedAtUtc.AddTicks(1);
        var wake = HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            HumanReviewContinuationWake.CurrentSchemaVersion,
            "human-review-wake-one",
            new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
            new HumanReviewDecisionReference(terminalDecision.DecisionId, terminalDecision.DecisionOperationId, terminalDecision.Kind, terminalDecision.DecisionHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            review.Request.Binding.BindingHash,
            approved.SequentialAdapterBinding!.ExecutionBinding.ExecutionGeneration,
            wakeAtUtc,
            wakeExpiresAtUtc ?? review.Request.Timing.ExpiresAtUtc,
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "human-review-wake-one", wakeAtUtc, string.Empty)),
            string.Empty));
        var claimAtUtc = wakeAtUtc.AddTicks(1);
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            HumanReviewContinuationClaim.CurrentSchemaVersion,
            "human-review-claim-one",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "human-review-worker",
            claimAtUtc,
            claimLeaseExpiresAtUtc ?? claimAtUtc.AddMinutes(1),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "test-coordinator", "human-review-claim-one", claimAtUtc, string.Empty)),
            string.Empty));
        var publishedContinuation = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(
            HumanReviewContinuationState.CurrentSchemaVersion,
            wake,
            ImmutableArray<HumanReviewContinuationClaim>.Empty,
            null,
            null,
            string.Empty));
        var published = approved with
        {
            LifecycleVersion = approved.LifecycleVersion + 1,
            UpdatedAtUtc = wakeAtUtc,
            HumanReview = review with { Continuation = publishedContinuation },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(approved, published).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(approved, published).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(published, approved.LifecycleVersion)).Status);
        var continuation = HumanReviewContinuationContractHash.ApplyState(publishedContinuation with { Claims = ImmutableArray.Create(claim), StateHash = string.Empty });
        var claimed = published with
        {
            LifecycleVersion = published.LifecycleVersion + 1,
            UpdatedAtUtc = claimAtUtc,
            HumanReview = published.HumanReview! with { Continuation = continuation },
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(published, claimed).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.ValidateUpdate(published, claimed).Errors));
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(claimed, published.LifecycleVersion)).Status);

        var request = new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash);
        var decisionReference = new HumanReviewDecisionReference(terminalDecision.DecisionId, terminalDecision.DecisionOperationId, terminalDecision.Kind, terminalDecision.DecisionHash);
        var wakeReference = new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash);
        var claimReference = new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash);
        var reservationReference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
        var releaseOperationId = Assert.IsType<string>(HumanReviewContinuationReleaseOperationId.Create(request, wakeReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.Continuation));
        var releaseReceipt = new HumanReviewContinuationReleaseReceiptIntent(releaseOperationId, request, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, HumanReviewContinuationReleaseKind.Continuation, null);
        var action = new HumanReviewContinuationActionIntent(
            HumanReviewContinuationAction.ReleaseContinuation,
            store.Current.Id,
            store.Current.LifecycleVersion,
            request,
            decisionReference,
            wakeReference,
            claimReference,
            reservationReference,
            wake.ExpectedGeneration,
            null,
            releaseReceipt);
        var completion = new HumanReviewContinuationCompletionIntent(store.Current.Id, store.Current.LifecycleVersion, request, wakeReference, claimReference, reservationReference, wake.ExpectedGeneration, releaseReceipt);
        return new ClaimedHumanReviewApproval(context, store, evidence, action, completion, claimAtUtc);
    }

    private static async Task ConfirmHumanReviewHandoffAsync(FakeRunStore store, GovernedLoopSequentialOrderedResumeRequest request, CancellationToken cancellationToken)
    {
        var current = store.Current;
        var now = current.UpdatedAtUtc.AddTicks(1);
        var confirmation = new CustomLoopRunEvent(
            current.Events.Length + 1,
            "human-review-reentry-confirmed-" + current.LifecycleVersion,
            now,
            CustomLoopRunEventKind.LifecycleChanged,
            null,
            null,
            null,
            "The ordered runtime accepted the exact durable Human Review release handoff.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var candidate = current with
        {
            LifecycleVersion = current.LifecycleVersion + 1,
            UpdatedAtUtc = now,
            Events = [.. current.Events, confirmation],
        };
        var validation = CustomLoopRunValidator.ValidateUpdate(current, candidate);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var updated = await store.UpdateAsync(candidate, current.LifecycleVersion, cancellationToken);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, updated.Status);
    }

    private static CustomLoopRunEvent RehashReviewEvidence(
        CustomLoopRunEvent source,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition,
        long sequence,
        string eventId)
    {
        var candidate = source with
        {
            Sequence = sequence,
            EventId = eventId,
            TimestampUtc = source.TimestampUtc.AddTicks(sequence - source.Sequence),
            Kind = kind == CustomLoopSequentialNodeEvidenceKind.ReviewRequested ? CustomLoopRunEventKind.NodeOutcomeObserved : CustomLoopRunEventKind.NodeAttemptCompleted,
            SequentialNodeEvidence = null,
        };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(source.SequentialNodeEvidence! with
        {
            Kind = kind,
            Disposition = disposition,
            OutcomeArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(candidate),
            EvidenceHash = string.Empty,
        });
        return candidate with { SequentialNodeEvidence = evidence };
    }

    private static string ActionReleaseOperationId(string reservationHash)
        => "action-operation-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reservationHash))).ToLowerInvariant()[..24];

    private sealed record ClaimedHumanReviewApproval(
        SequentialTestContext Context,
        FakeRunStore Store,
        SequentialEvidenceHarness Evidence,
        HumanReviewContinuationActionIntent Action,
        HumanReviewContinuationCompletionIntent Completion,
        DateTimeOffset ClaimedAtUtc);

    private sealed record ClaimedPreDispatchApproval(
        HumanReviewRequestReference Request,
        HumanReviewDecisionReference Decision,
        HumanReviewContinuationWakeReference Wake,
        HumanReviewContinuationClaimReference Claim,
        HumanReviewContinuationReservationReference Reservation,
        long ExpectedGeneration,
        DateTimeOffset ClaimedAtUtc);

    private static async Task<SequentialTestContext> HumanReviewContextAsync()
        => await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role =>
            {
                var artifact = HumanReviewArtifact(role);
                var plan = GovernedLoopSequentialPlanBuilder.Build(artifact);
                Assert.True(plan.Plan is not null, $"{plan.Status}: {plan.FailurePath}");
                return artifact;
            });

    private static GovernedLoopGraphRevisionArtifact HumanReviewArtifact(ContextualRoleRevisionPin role)
        => GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-review",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, GovernedLoopHumanReviewVocabulary.TypeId, GovernedLoopHumanReviewVocabulary.DescriptorVersion),
                    [],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerPolicyId,
                        [GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                        [GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter] = "review-scope-one",
                    },
                    null,
                    null,
                    null,
                    null),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
                new GovernedLoopNodeDefinition(
                    "fail",
                    GovernedLoopSequentialNodeDescriptors.FailTerminal,
                    [],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    null,
                    null,
                    null,
                    null),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-review", "trigger", "human-review", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-review-to-exit", "human-review", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-review-to-fail", "human-review", "fail", GovernedLoopControlCondition.Failure),
            ],
            ["exit", "fail"],
            role,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
}
