using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewDecisionActionRunStoreTests
{
    [Theory]
    [InlineData(HumanReviewDecisionKind.Reject, HumanReviewDecisionActionDisposition.Rejected)]
    [InlineData(HumanReviewDecisionKind.Cancel, HumanReviewDecisionActionDisposition.Cancelled)]
    public async Task Accepted_nonapproval_decision_publishes_claims_completes_and_replays_one_exact_canonical_action(HumanReviewDecisionKind kind, HumanReviewDecisionActionDisposition disposition)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-" + kind.ToString().ToLowerInvariant());
        using var store = new CustomLoopRunStore(paths);
        var accepted = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-decision-" + kind.ToString().ToLowerInvariant(), kind, null));
        Assert.Contains(accepted.Status, new[] { HumanReviewDecisionServiceStatus.Accepted, HumanReviewDecisionServiceStatus.InformationRequested });
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var initial = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var actions = new HumanReviewDecisionActionRunStore(store);
        var publisher = new HumanReviewDecisionActionPublicationService(store, actions);

        var publication = await publisher.PublishAsync(new(reserved.Id, new(initial.Reservation.ReservationId, initial.Reservation.ReservationHash)));
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, publication.Status);
        var published = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));
        var publishedAction = Assert.Single(Assert.IsType<HumanReviewRunState>(published.HumanReview).DecisionActions);
        var wake = Assert.IsType<HumanReviewDecisionActionWake>(publishedAction.Wake);
        var claim = Claim(publishedAction, wake.PublishedAtUtc.AddMinutes(1), "action-claim-" + kind.ToString().ToLowerInvariant());
        var candidate = new HumanReviewDecisionActionRecoveryCandidate(published.Id, published.LifecycleVersion, new(publishedAction.Reservation.Request.RequestId, publishedAction.Reservation.Request.RequestHash), publishedAction.Reservation.Decision, new(wake.WakeId, wake.WakeHash), publishedAction.ExpectedGeneration, wake.ExpiresAtUtc, new(publishedAction.Reservation.ReservationId, publishedAction.Reservation.ReservationHash), null);

        var claimed = await actions.ClaimAsync(new(candidate, claim));
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, claimed.Status);
        var afterClaim = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(published.Id));
        var claimedAction = Assert.Single(Assert.IsType<HumanReviewRunState>(afterClaim.HumanReview).DecisionActions);
        Assert.Empty((await actions.ListCandidatesAsync(1, null, claim.LeaseExpiresAtUtc)).Candidates);
        var takeoverPage = await actions.ListCandidatesAsync(1, null, claim.LeaseExpiresAtUtc.AddTicks(1));
        Assert.Equal(new HumanReviewDecisionActionClaimReference(claim.ClaimId, claim.ClaimHash), Assert.Single(takeoverPage.Candidates).PriorClaim);
        var prior = new HumanReviewDecisionActionClaimReference(claim.ClaimId, claim.ClaimHash);
        var early = Claim(claimedAction, claim.LeaseExpiresAtUtc, "action-early-" + kind.ToString().ToLowerInvariant());
        var takeoverCandidate = candidate with { ExpectedLifecycleVersion = afterClaim.LifecycleVersion, PriorClaim = prior };
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Invalid, (await actions.ClaimAsync(new(takeoverCandidate, early))).Status);
        var successor = Claim(claimedAction, claim.LeaseExpiresAtUtc.AddTicks(1), "action-takeover-" + kind.ToString().ToLowerInvariant());
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await actions.ClaimAsync(new(takeoverCandidate, successor))).Status);
        var afterTakeover = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(published.Id));
        var takenOverAction = Assert.Single(Assert.IsType<HumanReviewRunState>(afterTakeover.HumanReview).DecisionActions);
        var staleCompletion = Completion(takenOverAction, claim, disposition, claim.ClaimedAtUtc.AddMinutes(1));
        var staleIntent = new HumanReviewDecisionActionCompletionIntent(afterTakeover.Id, afterTakeover.LifecycleVersion, new(wake.WakeId, wake.WakeHash), prior, new(takenOverAction.Reservation.ReservationId, takenOverAction.Reservation.ReservationHash), takenOverAction.ExpectedGeneration);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Conflict, (await actions.CompleteAsync(staleIntent, staleCompletion)).Status);
        var completion = Completion(takenOverAction, successor, disposition, successor.ClaimedAtUtc.AddMinutes(1));
        var intent = new HumanReviewDecisionActionCompletionIntent(afterTakeover.Id, afterTakeover.LifecycleVersion, new(wake.WakeId, wake.WakeHash), new(successor.ClaimId, successor.ClaimHash), new(takenOverAction.Reservation.ReservationId, takenOverAction.Reservation.ReservationHash), takenOverAction.ExpectedGeneration);

        var completed = await actions.CompleteAsync(intent, completion);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, completed.Status);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Replayed, (await actions.CompleteAsync(intent, completion)).Status);
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(afterTakeover.Id));
        Assert.Equal(completion.CompletionHash, Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions).Completion?.CompletionHash);
    }

    [Fact]
    public async Task Concurrent_claims_and_stale_generation_fail_closed_without_duplicate_action_execution()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-race");
        using var store = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-race-decision", HumanReviewDecisionKind.Reject, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var actions = new HumanReviewDecisionActionRunStore(store);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionPublicationService(store, actions).PublishAsync(new(reserved.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash)))).Status);
        var published = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));
        var retained = Assert.Single(Assert.IsType<HumanReviewRunState>(published.HumanReview).DecisionActions);
        var wake = Assert.IsType<HumanReviewDecisionActionWake>(retained.Wake);
        var candidate = new HumanReviewDecisionActionRecoveryCandidate(published.Id, published.LifecycleVersion, new(retained.Reservation.Request.RequestId, retained.Reservation.Request.RequestHash), retained.Reservation.Decision, new(wake.WakeId, wake.WakeHash), retained.ExpectedGeneration, wake.ExpiresAtUtc, new(retained.Reservation.ReservationId, retained.Reservation.ReservationHash), null);
        var first = Claim(retained, wake.PublishedAtUtc.AddMinutes(1), "action-race-claim-one");
        var second = Claim(retained, wake.PublishedAtUtc.AddMinutes(1), "action-race-claim-two");

        var results = await Task.WhenAll(actions.ClaimAsync(new(candidate, first)), actions.ClaimAsync(new(candidate, second)));
        Assert.Equal(1, results.Count(result => result.Status == HumanReviewDecisionActionStoreMutationStatus.Committed));
        Assert.Equal(1, results.Count(result => result.Status == HumanReviewDecisionActionStoreMutationStatus.Conflict));
        var stale = candidate with { ExpectedGeneration = candidate.ExpectedGeneration + 1 };
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Invalid, (await actions.ClaimAsync(new(stale, Claim(retained, wake.PublishedAtUtc.AddMinutes(2), "action-race-claim-three")))).Status);
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_during_publication_preserves_one_replayable_action_wake_or_its_predecessor(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-publication-loss-" + boundary.ToString().ToLowerInvariant());
        using (var seed = new CustomLoopRunStore(paths))
        {
            _ = await new HumanReviewDecisionService(seed, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-publication-loss-decision-" + boundary.ToString().ToLowerInvariant(), HumanReviewDecisionKind.Reject, null));
        }

        await RunActionPublicationLossHostAsync(workspace, admitted.Id, boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(admitted.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        var publisher = new HumanReviewDecisionActionPublicationService(restarted, new HumanReviewDecisionActionRunStore(restarted));
        var reconciled = await publisher.PublishAsync(new(recovered.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash)));
        Assert.Contains(reconciled.Status, new[] { HumanReviewDecisionActionStoreMutationStatus.Committed, HumanReviewDecisionActionStoreMutationStatus.Replayed });
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(admitted.Id));
        Assert.NotNull(Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions).Wake);
    }

    [Fact]
    public async Task Concurrent_nonapproval_action_publishers_converge_on_one_deterministic_wake_only()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-publication-race");
        using var store = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-publication-race-decision", HumanReviewDecisionKind.Reject, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var reservation = new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash);
        var canonical = new HumanReviewDecisionActionRunStore(store);
        var first = new HumanReviewDecisionActionPublicationService(store, canonical);
        var second = new HumanReviewDecisionActionPublicationService(store, canonical);

        var results = await Task.WhenAll(first.PublishAsync(new(reserved.Id, reservation)), second.PublishAsync(new(reserved.Id, reservation)));
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));
        var published = Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions);

        Assert.Contains(results, result => result.Status == HumanReviewDecisionActionStoreMutationStatus.Committed);
        Assert.Contains(results, result => result.Status == HumanReviewDecisionActionStoreMutationStatus.Replayed);
        Assert.NotNull(published.Wake);
        Assert.Empty(published.Claims);
    }

    [Fact]
    public async Task Response_loss_after_canonical_rename_rereads_and_replays_the_exact_action_wake_without_duplicate_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-response-loss");
        HumanReviewDecisionActionReservationReference reservation;
        using (var seed = new CustomLoopRunStore(paths))
        {
            _ = await new HumanReviewDecisionService(seed, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-response-loss-decision", HumanReviewDecisionKind.Reject, null));
            var reserved = Assert.IsType<CustomLoopRunRecord>(await seed.GetAsync(admitted.Id));
            var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
            reservation = new(action.Reservation.ReservationId, action.Reservation.ReservationHash);
        }

        using (var uncertain = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed ? ValueTask.FromException(new IOException("response lost after canonical action wake publication")) : ValueTask.CompletedTask))
        {
            var result = await new HumanReviewDecisionActionPublicationService(uncertain, new HumanReviewDecisionActionRunStore(uncertain)).PublishAsync(new(admitted.Id, reservation));
            Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Replayed, result.Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(admitted.Id));
        var actionAfterRestart = Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions);
        Assert.NotNull(actionAfterRestart.Wake);
        Assert.Empty(actionAfterRestart.Claims);
        Assert.True(CustomLoopRunValidator.Validate(durable).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(durable).Errors));
    }

    [Fact]
    public async Task Cancellation_after_durable_action_wake_publication_reconciles_the_exact_canonical_replay()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-cancel-after-commit");
        using var cancellation = new CancellationTokenSource();
        using var store = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-cancel-after-commit-decision", HumanReviewDecisionKind.Reject, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var canonical = new HumanReviewDecisionActionRunStore(store);
        var cancelled = new CancellationHumanReviewDecisionActionPublicationStore(canonical, cancellation);
        var publisher = new HumanReviewDecisionActionPublicationService(store, cancelled);

        var result = await publisher.PublishAsync(new(reserved.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash)), cancellation.Token);
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Replayed, result.Status);
        Assert.Equal(1, cancelled.CommitCount);
        Assert.NotNull(Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions).Wake);
    }

    [Fact]
    public async Task Cancellation_before_action_wake_publication_preserves_caller_cancellation_without_a_wake()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-cancel-before-commit");
        using var cancellation = new CancellationTokenSource();
        using var store = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-cancel-before-commit-decision", HumanReviewDecisionKind.Reject, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var cancelled = new CancellationHumanReviewDecisionActionPublicationStore(new HumanReviewDecisionActionRunStore(store), cancellation, cancelBeforeCommit: true);
        var publisher = new HumanReviewDecisionActionPublicationService(store, cancelled);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(new(reserved.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash)), cancellation.Token));
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));

        Assert.Equal(0, cancelled.CommitCount);
        Assert.Null(Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions).Wake);
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_claim_boundary_preserves_one_replayable_predecessor_or_successor_and_rejects_a_stale_worker(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await CreatePublishedActionAsync(paths, "action-claim-process-loss", HumanReviewDecisionKind.Reject);
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(published.HumanReview).DecisionActions);
        var expected = Claim(action, action.Wake!.PublishedAtUtc.AddMinutes(1), "claim-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(published);

        await RunActionTransitionProcessLossAsync(workspace, published.Id, "claim", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(published.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        var recoveredAction = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        if (recoveredAction.Claims.IsEmpty)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
            var candidate = Candidate(recovered, recoveredAction, null);
            Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionRunStore(restarted).ClaimAsync(new(candidate, expected))).Status);
            recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(recovered.Id));
            recoveredAction = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        }
        else
        {
            Assert.Equal(expected.ClaimHash, Assert.Single(recoveredAction.Claims).ClaimHash);
        }

        var prior = new HumanReviewDecisionActionClaimReference(expected.ClaimId, expected.ClaimHash);
        var takeover = Claim(recoveredAction, expected.LeaseExpiresAtUtc.AddTicks(1), "claim-process-loss-takeover");
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionRunStore(restarted).ClaimAsync(new(Candidate(recovered, recoveredAction, prior), takeover))).Status);
        var takenOver = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(recovered.Id));
        var takenOverAction = Assert.Single(Assert.IsType<HumanReviewRunState>(takenOver.HumanReview).DecisionActions);
        var staleCompletion = Completion(takenOverAction, expected, HumanReviewDecisionActionDisposition.Rejected, expected.ClaimedAtUtc.AddSeconds(1));
        var staleIntent = new HumanReviewDecisionActionCompletionIntent(takenOver.Id, takenOver.LifecycleVersion, new(takenOverAction.Wake!.WakeId, takenOverAction.Wake.WakeHash), prior, new(takenOverAction.Reservation.ReservationId, takenOverAction.Reservation.ReservationHash), takenOverAction.ExpectedGeneration);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Conflict, (await new HumanReviewDecisionActionRunStore(restarted).CompleteAsync(staleIntent, staleCompletion)).Status);
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_completion_boundary_preserves_one_replayable_predecessor_or_successor_and_excludes_retirement(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var claimed = await CreateClaimedActionAsync(paths, "action-completion-process-loss", HumanReviewDecisionKind.Cancel, "claim-process-loss");
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(claimed.HumanReview).DecisionActions);
        var claim = Assert.Single(action.Claims);
        var expected = Completion(action, claim, HumanReviewDecisionActionDisposition.Cancelled, claim.ClaimedAtUtc.AddSeconds(1), "completion-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(claimed);

        await RunActionTransitionProcessLossAsync(workspace, claimed.Id, "completion", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        var recoveredAction = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        if (recoveredAction.Completion is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
            var intent = new HumanReviewDecisionActionCompletionIntent(recovered.Id, recovered.LifecycleVersion, new(recoveredAction.Wake!.WakeId, recoveredAction.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(recoveredAction.Reservation.ReservationId, recoveredAction.Reservation.ReservationHash), recoveredAction.ExpectedGeneration);
            Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionRunStore(restarted).CompleteAsync(intent, expected)).Status);
            recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(recovered.Id));
            recoveredAction = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        }
        else
        {
            Assert.Equal(expected.CompletionHash, recoveredAction.Completion.CompletionHash);
        }

        var retirement = Retirement(recoveredAction, claim, HumanReviewContinuationOutcome.Blocked, claim.ClaimedAtUtc.AddSeconds(2), "retirement-after-completion-process-loss");
        var retirementIntent = new HumanReviewDecisionActionRetirementIntent(recovered.Id, recovered.LifecycleVersion, new(recoveredAction.Wake!.WakeId, recoveredAction.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(recoveredAction.Reservation.ReservationId, recoveredAction.Reservation.ReservationHash), recoveredAction.ExpectedGeneration, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Conflict, (await new HumanReviewDecisionActionRunStore(restarted).RetireAsync(retirementIntent, retirement)).Status);
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_retirement_boundary_preserves_one_replayable_predecessor_or_successor_and_excludes_completion(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var claimed = await CreateClaimedActionAsync(paths, "action-retirement-process-loss", HumanReviewDecisionKind.RequestInformation, "claim-process-loss");
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(claimed.HumanReview).DecisionActions);
        var claim = Assert.Single(action.Claims);
        var expected = Retirement(action, claim, HumanReviewContinuationOutcome.Blocked, claim.ClaimedAtUtc.AddSeconds(1), "retirement-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(claimed);

        await RunActionTransitionProcessLossAsync(workspace, claimed.Id, "retirement", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        var recoveredAction = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        if (recoveredAction.Retirement is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
            var intent = new HumanReviewDecisionActionRetirementIntent(recovered.Id, recovered.LifecycleVersion, new(recoveredAction.Wake!.WakeId, recoveredAction.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(recoveredAction.Reservation.ReservationId, recoveredAction.Reservation.ReservationHash), recoveredAction.ExpectedGeneration, HumanReviewContinuationOutcome.Blocked, HumanReviewDecisionActionRetirementReason.Invalid);
            Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionRunStore(restarted).RetireAsync(intent, expected)).Status);
            recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(recovered.Id));
            recoveredAction = Assert.Single(Assert.IsType<HumanReviewRunState>(recovered.HumanReview).DecisionActions);
        }
        else
        {
            Assert.Equal(expected.RetirementHash, recoveredAction.Retirement.RetirementHash);
        }

        var completion = Completion(recoveredAction, claim, HumanReviewDecisionActionDisposition.InformationParked, claim.ClaimedAtUtc.AddSeconds(2), "completion-after-retirement-process-loss");
        var completionIntent = new HumanReviewDecisionActionCompletionIntent(recovered.Id, recovered.LifecycleVersion, new(recoveredAction.Wake!.WakeId, recoveredAction.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(recoveredAction.Reservation.ReservationId, recoveredAction.Reservation.ReservationHash), recoveredAction.ExpectedGeneration);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Conflict, (await new HumanReviewDecisionActionRunStore(restarted).CompleteAsync(completionIntent, completion)).Status);
    }

    [Fact]
    public async Task Publication_returns_quota_or_unavailable_without_synthesizing_a_wake()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-quota-unavailable");
        using var canonical = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(canonical, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-quota-unavailable-decision", HumanReviewDecisionKind.Reject, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await canonical.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var command = new HumanReviewDecisionActionPublicationCommand(reserved.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash));
        var quota = await new HumanReviewDecisionActionPublicationService(canonical, new HumanReviewDecisionActionRunStore(new QuotaRejectingCustomLoopRunStore(canonical))).PublishAsync(command);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.LimitExceeded, quota.Status);
        Assert.Null(Assert.Single(Assert.IsType<HumanReviewRunState>((await canonical.GetAsync(reserved.Id))!.HumanReview).DecisionActions).Wake);

        using var unavailable = new CustomLoopRunStore(paths, null, (_, _, _) => ValueTask.FromException(new IOException("action read unavailable")));
        var unavailableResult = await new HumanReviewDecisionActionPublicationService(unavailable, new HumanReviewDecisionActionRunStore(unavailable)).PublishAsync(command);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Unavailable, unavailableResult.Status);
        Assert.Null(Assert.Single(Assert.IsType<HumanReviewRunState>((await canonical.GetAsync(reserved.Id))!.HumanReview).DecisionActions).Wake);
    }

    [Fact]
    public async Task Expired_unclaimed_action_retires_through_the_same_whole_run_compare_exchange_boundary()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-expired-retirement");
        using var store = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-expired-retirement-decision", HumanReviewDecisionKind.Cancel, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var initial = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var reservation = new HumanReviewDecisionActionReservationReference(initial.Reservation.ReservationId, initial.Reservation.ReservationHash);
        var actions = new HumanReviewDecisionActionRunStore(store);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionPublicationService(store, actions).PublishAsync(new(reserved.Id, reservation))).Status);
        var published = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(published.HumanReview).DecisionActions);
        var wake = Assert.IsType<HumanReviewDecisionActionWake>(action.Wake);
        var retirement = HumanReviewDecisionActionContractHash.ApplyRetirement(new(1, "action-expired-retirement-one", new(wake.WakeId, wake.WakeHash), reservation, action.ExpectedGeneration, HumanReviewContinuationOutcome.Expired, wake.ExpiresAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("action-expired-retirement-one", wake.ExpiresAtUtc), string.Empty));
        var intent = new HumanReviewDecisionActionRetirementIntent(published.Id, published.LifecycleVersion, new(wake.WakeId, wake.WakeHash), null, reservation, action.ExpectedGeneration, HumanReviewContinuationOutcome.Expired, HumanReviewDecisionActionRetirementReason.Expired);

        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await actions.RetireAsync(intent, retirement)).Status);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Replayed, (await actions.RetireAsync(intent, retirement)).Status);
        var retired = Assert.Single(Assert.IsType<HumanReviewRunState>(Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(published.Id)).HumanReview).DecisionActions);
        Assert.Equal(retirement.RetirementHash, retired.Retirement?.RetirementHash);
        Assert.Null(retired.Completion);
    }

    [Fact]
    public async Task Whole_run_validation_rejects_corrupt_duplicate_action_reservations_for_one_accepted_decision()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, "action-corrupt-duplicate");
        using var store = new CustomLoopRunStore(paths);
        _ = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "action-corrupt-duplicate-decision", HumanReviewDecisionKind.Reject, null));
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var review = Assert.IsType<HumanReviewRunState>(reserved.HumanReview);
        var action = Assert.Single(review.DecisionActions);
        var corrupt = reserved with { HumanReview = review with { DecisionActions = [action, action] } };

        Assert.Contains(CustomLoopRunValidator.Validate(corrupt).Errors, error => error.Code == "invalid_human_review_decision_action");
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.Reject)]
    [InlineData(HumanReviewDecisionKind.Cancel)]
    [InlineData(HumanReviewDecisionKind.RequestInformation)]
    public async Task Separate_process_claimers_share_one_compare_exchange_winner_for_each_nonapproval_action(HumanReviewDecisionKind kind)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var identity = "action-claim-race-" + kind.ToString().ToLowerInvariant();
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, identity);
        CustomLoopRunRecord reserved;
        using (var store = new CustomLoopRunStore(paths))
        {
            var decision = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "reserve-" + identity, kind, kind == HumanReviewDecisionKind.RequestInformation ? "Need a redacted clarification." : null));
            Assert.Equal(kind == HumanReviewDecisionKind.RequestInformation ? HumanReviewDecisionServiceStatus.InformationRequested : HumanReviewDecisionServiceStatus.Accepted, decision.Status);
            reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
            var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
            Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionPublicationService(store, new HumanReviewDecisionActionRunStore(store)).PublishAsync(new(reserved.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash)))).Status);
        }

        var firstReadyPath = Path.Combine(workspace.RootPath, "action-claim-race-first.ready");
        var secondReadyPath = Path.Combine(workspace.RootPath, "action-claim-race-second.ready");
        var releasePath = Path.Combine(workspace.RootPath, "action-claim-race.release");
        var firstResultPath = Path.Combine(workspace.RootPath, "action-claim-race-first.result");
        var secondResultPath = Path.Combine(workspace.RootPath, "action-claim-race-second.result");
        using var first = CancellationHostProcess.Start("human-review-decision-action-claim-race", workspace.RootPath, reserved.Id, "first", firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.Start("human-review-decision-action-claim-race", workspace.RootPath, reserved.Id, "second", secondReadyPath, releasePath, secondResultPath);
        await WaitForFileAsync(firstReadyPath, TimeSpan.FromSeconds(30));
        await WaitForFileAsync(secondReadyPath, TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(releasePath, "release");
        await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var outcomes = new[] { await File.ReadAllTextAsync(firstResultPath), await File.ReadAllTextAsync(secondResultPath) };
        Assert.Equal(1, outcomes.Count(value => value == HumanReviewDecisionActionStoreMutationStatus.Committed.ToString()));
        Assert.Equal(1, outcomes.Count(value => value == HumanReviewDecisionActionStoreMutationStatus.Conflict.ToString()));
        using var restarted = new CustomLoopRunStore(paths);
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(reserved.Id));
        Assert.True(CustomLoopRunValidator.Validate(durable).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(durable).Errors));
        Assert.Single(Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions).Claims);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path)) await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
    }

    private static async Task RunActionPublicationLossHostAsync(TestWorkspace workspace, string runId, CustomLoopRunPublicationBoundary boundary)
    {
        using var process = CancellationHostProcess.Start("human-review-decision-action-publication-process-loss", workspace.RootPath, runId, boundary.ToString());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var errorText = await error;
        var outputText = await output;
        Assert.True(process.ExitCode != 0 && errorText.Contains("test host process crashed", StringComparison.OrdinalIgnoreCase), $"Expected the process-loss boundary crash; exit={process.ExitCode}; stdout={outputText}; stderr={errorText}");
    }

    private static async Task RunActionTransitionProcessLossAsync(TestWorkspace workspace, string runId, string transition, CustomLoopRunPublicationBoundary boundary)
    {
        using var process = CancellationHostProcess.Start("human-review-decision-action-transition-process-loss", workspace.RootPath, runId, transition, boundary.ToString());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var errorText = await error;
        var outputText = await output;
        Assert.True(process.ExitCode != 0 && errorText.Contains("test host process crashed", StringComparison.OrdinalIgnoreCase), $"Expected the process-loss boundary crash; exit={process.ExitCode}; stdout={outputText}; stderr={errorText}");
    }

    private static async Task<CustomLoopRunRecord> CreatePublishedActionAsync(WorkspacePaths paths, string identity, HumanReviewDecisionKind kind)
    {
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, identity);
        using var store = new CustomLoopRunStore(paths);
        var decision = await new HumanReviewDecisionService(store, new HumanReviewDecisionStoreTestAuthorizer(), new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new(admitted.Id, admitted.LifecycleVersion, "decision-" + identity, kind, kind == HumanReviewDecisionKind.RequestInformation ? "Need a redacted clarification." : null));
        Assert.Equal(kind == HumanReviewDecisionKind.RequestInformation ? HumanReviewDecisionServiceStatus.InformationRequested : HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionPublicationService(store, new HumanReviewDecisionActionRunStore(store)).PublishAsync(new(reserved.Id, new(action.Reservation.ReservationId, action.Reservation.ReservationHash)))).Status);
        return Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));
    }

    private static async Task<CustomLoopRunRecord> CreateClaimedActionAsync(WorkspacePaths paths, string identity, HumanReviewDecisionKind kind, string claimId)
    {
        var published = await CreatePublishedActionAsync(paths, identity, kind);
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(published.HumanReview).DecisionActions);
        var claim = Claim(action, action.Wake!.PublishedAtUtc.AddMinutes(1), claimId);
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionRunStore(store).ClaimAsync(new(Candidate(published, action, null), claim))).Status);
        return Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(published.Id));
    }

    private static HumanReviewDecisionActionRecoveryCandidate Candidate(CustomLoopRunRecord run, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaimReference? priorClaim)
        => new(run.Id, run.LifecycleVersion, new(run.HumanReview!.Request.RequestId, run.HumanReview.Request.RequestHash), action.Reservation.Decision, new(action.Wake!.WakeId, action.Wake.WakeHash), action.ExpectedGeneration, action.Wake.ExpiresAtUtc, new(action.Reservation.ReservationId, action.Reservation.ReservationHash), priorClaim);

    private static HumanReviewDecisionActionClaim Claim(HumanReviewDecisionActionState action, DateTimeOffset claimedAtUtc, string claimId)
        => HumanReviewDecisionActionContractHash.ApplyClaim(new(1, claimId, new(action.Wake!.WakeId, action.Wake.WakeHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, "worker-" + claimId, claimedAtUtc, claimedAtUtc.AddMinutes(5), Provenance(claimId, claimedAtUtc), string.Empty));

    private static HumanReviewDecisionActionCompletion Completion(HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim, HumanReviewDecisionActionDisposition disposition, DateTimeOffset completedAtUtc, string? completionId = null)
    {
        var id = completionId ?? "completion-" + claim.ClaimId;
        return HumanReviewDecisionActionContractHash.ApplyCompletion(new(1, id, new(action.Wake!.WakeId, action.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, disposition, Hash('a'), Hash('b'), completedAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance(id, completedAtUtc), string.Empty));
    }

    private static HumanReviewDecisionActionRetirement Retirement(HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim, HumanReviewContinuationOutcome outcome, DateTimeOffset retiredAtUtc, string retirementId)
        => HumanReviewDecisionActionContractHash.ApplyRetirement(new(1, retirementId, new(action.Wake!.WakeId, action.Wake.WakeHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, outcome, retiredAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance(retirementId, retiredAtUtc), string.Empty));

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc) => HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Coordinator, "human-review-action-store", correlationId, observedAtUtc, string.Empty));
    private static string Hash(char value) => new(value, HumanReviewContractLimits.Sha256HexCharacters);
}
