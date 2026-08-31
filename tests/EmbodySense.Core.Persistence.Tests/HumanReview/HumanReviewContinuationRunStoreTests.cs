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
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewContinuationRunStoreTests
{
    [Fact]
    public async Task Accepted_reservation_publication_replays_after_response_loss_restart_and_concurrent_calls_without_duplicate_wakes()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-publisher");

        using (var store = new CustomLoopRunStore(paths))
        {
            var canonical = new HumanReviewContinuationRunStore(store);
            var responseLost = new ResponseLostHumanReviewContinuationPublicationStore(canonical);
            var publisher = new HumanReviewContinuationPublicationService(store, responseLost);

            var recovered = await publisher.PublishAsync(approved.Id);

            Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, recovered.Status);
            Assert.Equal(1, responseLost.CommitCount);
            var concurrently = await Task.WhenAll(
                publisher.PublishAsync(approved.Id),
                new HumanReviewContinuationPublicationService(store, canonical).PublishAsync(approved.Id));
            Assert.All(concurrently, result => Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, result.Status));
        }

        using var restarted = new CustomLoopRunStore(paths);
        var replay = await new HumanReviewContinuationPublicationService(restarted, new HumanReviewContinuationRunStore(restarted)).PublishAsync(approved.Id);
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, replay.Status);
        Assert.NotNull(durable.HumanReview?.Continuation);
        Assert.Empty(durable.HumanReview!.Continuation!.Claims);
        Assert.Null(durable.HumanReview.Continuation.Completion);
        Assert.Null(durable.HumanReview.Continuation.Retirement);
    }

    [Fact]
    public async Task Concurrent_accepted_reservation_publishers_converge_on_one_wake_only()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-publisher-race");
        using var store = new CustomLoopRunStore(paths);
        var continuations = new HumanReviewContinuationRunStore(store);
        var first = new HumanReviewContinuationPublicationService(store, continuations);
        var second = new HumanReviewContinuationPublicationService(store, continuations);

        var results = await Task.WhenAll(first.PublishAsync(approved.Id), second.PublishAsync(approved.Id));
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));

        Assert.Contains(results, result => result.Status == HumanReviewContinuationStoreMutationStatus.Committed);
        Assert.Contains(results, result => result.Status == HumanReviewContinuationStoreMutationStatus.Replayed);
        Assert.NotNull(durable.HumanReview?.Continuation);
        Assert.Empty(durable.HumanReview!.Continuation!.Claims);
    }

    [Fact]
    public async Task Archived_completion_reservation_replays_before_current_review_eligibility_and_rejects_substitution()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var current = await CreateArchivedCurrentReviewAsync(paths, "continuation-archived-completion", retire: false);
        var archived = Assert.Single(Assert.IsType<HumanReviewRunState>(current.HumanReview).CompletedReviews);
        var archivedContinuation = Assert.IsType<HumanReviewContinuationState>(archived.Continuation);
        var archivedReservation = Assert.IsType<HumanReviewContinuationReservation>(archived.ContinuationReservation);
        var archivedClaim = Assert.Single(archivedContinuation.Claims);
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, archivedContinuation.Wake, [], null, null, string.Empty));
        var continuations = new HumanReviewContinuationRunStore(new CustomLoopRunStore(paths));

        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.PublishAsync(current.Id, current.LifecycleVersion, initial)).Status);
        var divergentWake = HumanReviewContinuationContractHash.ApplyWake(archivedContinuation.Wake with { WakeId = "archived-completion-divergent-wake", WakeHash = string.Empty });
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.PublishAsync(current.Id, current.LifecycleVersion, HumanReviewContinuationContractHash.ApplyState(initial with { Wake = divergentWake, StateHash = string.Empty }))).Status);

        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.ClaimAsync(current.Id, current.LifecycleVersion, archivedClaim)).Status);
        var divergentClaim = HumanReviewContinuationContractHash.ApplyClaim(archivedClaim with { WorkerId = "archived-completion-divergent-worker", ClaimHash = string.Empty });
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.ClaimAsync(current.Id, current.LifecycleVersion, divergentClaim)).Status);

        var archivedCompletion = Assert.IsType<HumanReviewContinuationCompletion>(archivedContinuation.Completion);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.CompleteAsync(current.Id, current.LifecycleVersion, archivedCompletion)).Status);
        var divergentCompletion = HumanReviewContinuationContractHash.ApplyCompletion(archivedCompletion with { CompletionId = "archived-completion-divergent", CompletionHash = string.Empty });
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.CompleteAsync(current.Id, current.LifecycleVersion, divergentCompletion)).Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.RetireAsync(current.Id, current.LifecycleVersion, new HumanReviewContinuationClaimReference(archivedClaim.ClaimId, archivedClaim.ClaimHash), Retirement(archivedContinuation.Wake, archivedReservation, current.UpdatedAtUtc.AddMinutes(1), "archived-completion-retirement"))).Status);
    }

    [Fact]
    public async Task Archived_retirement_reservation_replays_before_current_review_eligibility_and_rejects_substitution()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var current = await CreateArchivedCurrentReviewAsync(paths, "continuation-archived-retirement", retire: true);
        var archived = Assert.Single(Assert.IsType<HumanReviewRunState>(current.HumanReview).CompletedReviews);
        var archivedContinuation = Assert.IsType<HumanReviewContinuationState>(archived.Continuation);
        var archivedReservation = Assert.IsType<HumanReviewContinuationReservation>(archived.ContinuationReservation);
        var archivedClaim = Assert.Single(archivedContinuation.Claims);
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, archivedContinuation.Wake, [], null, null, string.Empty));
        var continuations = new HumanReviewContinuationRunStore(new CustomLoopRunStore(paths));

        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.PublishAsync(current.Id, current.LifecycleVersion, initial)).Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.ClaimAsync(current.Id, current.LifecycleVersion, archivedClaim)).Status);
        var archivedRetirement = Assert.IsType<HumanReviewContinuationRetirement>(archivedContinuation.Retirement);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.RetireAsync(current.Id, current.LifecycleVersion, new HumanReviewContinuationClaimReference(archivedClaim.ClaimId, archivedClaim.ClaimHash), archivedRetirement)).Status);
        var divergentRetirement = HumanReviewContinuationContractHash.ApplyRetirement(archivedRetirement with { RetirementId = "archived-retirement-divergent", RetirementHash = string.Empty });
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.RetireAsync(current.Id, current.LifecycleVersion, new HumanReviewContinuationClaimReference(archivedClaim.ClaimId, archivedClaim.ClaimHash), divergentRetirement)).Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.CompleteAsync(current.Id, current.LifecycleVersion, Completion(archived.Request, archivedContinuation.Wake, archivedReservation, archivedClaim, current.UpdatedAtUtc.AddMinutes(1), "archived-retirement-completion"))).Status);
    }

    [Fact]
    public async Task Cancellation_after_a_durable_publication_returns_the_exact_canonical_replay()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-cancel-after-commit");
        using var cancellation = new CancellationTokenSource();
        using var store = new CustomLoopRunStore(paths);
        var canonical = new HumanReviewContinuationRunStore(store);
        var cancelled = new CancellationHumanReviewContinuationPublicationStore(canonical, cancellation);
        var publisher = new HumanReviewContinuationPublicationService(store, cancelled);

        var result = await publisher.PublishAsync(approved.Id, cancellation.Token);
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, result.Status);
        Assert.Equal(1, cancelled.CommitCount);
        Assert.NotNull(durable.HumanReview?.Continuation);
    }

    [Fact]
    public async Task Cancellation_before_publication_preserves_caller_cancellation_without_a_wake()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-cancel-before-commit");
        using var cancellation = new CancellationTokenSource();
        using var store = new CustomLoopRunStore(paths);
        var canonical = new HumanReviewContinuationRunStore(store);
        var cancelled = new CancellationHumanReviewContinuationPublicationStore(canonical, cancellation, cancelBeforeCommit: true);
        var publisher = new HumanReviewContinuationPublicationService(store, cancelled);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublishAsync(approved.Id, cancellation.Token));
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));

        Assert.Equal(0, cancelled.CommitCount);
        Assert.Null(durable.HumanReview?.Continuation);
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Unknown, "status-zero")]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Unavailable, "status-six")]
    public async Task Null_unknown_and_unavailable_post_commit_responses_reconcile_the_exact_canonical_wake_before_cancellation(HumanReviewContinuationStoreMutationStatus? uncertainStatus, string identity)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-cancel-uncertain-" + identity);
        using var cancellation = new CancellationTokenSource();
        using var store = new CustomLoopRunStore(paths);
        var canonical = new HumanReviewContinuationRunStore(store);
        var cancelled = new CancellationHumanReviewContinuationPublicationStore(
            canonical,
            cancellation,
            afterCommit: _ => uncertainStatus is { } status ? new HumanReviewContinuationStoreMutationResult(status) : null);
        var publisher = new HumanReviewContinuationPublicationService(store, cancelled);

        var result = await publisher.PublishAsync(approved.Id, cancellation.Token);
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, result.Status);
        Assert.Equal(1, cancelled.CommitCount);
        Assert.NotNull(durable.HumanReview?.Continuation);
    }

    [Fact]
    public async Task Non_cancellation_exception_after_a_durable_publication_reconciles_the_exact_canonical_wake_before_cancellation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-cancel-exception");
        using var cancellation = new CancellationTokenSource();
        using var store = new CustomLoopRunStore(paths);
        var canonical = new HumanReviewContinuationRunStore(store);
        var cancelled = new CancellationHumanReviewContinuationPublicationStore(canonical, cancellation, afterCommit: _ => throw new IOException("The canonical mutation response was lost."));
        var publisher = new HumanReviewContinuationPublicationService(store, cancelled);

        var result = await publisher.PublishAsync(approved.Id, cancellation.Token);
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, result.Status);
        Assert.Equal(1, cancelled.CommitCount);
        Assert.NotNull(durable.HumanReview?.Continuation);
    }

    [Fact]
    public async Task Publication_after_a_later_conflict_receipt_keeps_the_deterministic_wake_and_exactly_replays()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-publisher-conflict");
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(approved.HumanReview?.ContinuationReservation);
        using var store = new CustomLoopRunStore(paths);
        var conflict = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionStoreTestAuthorizer(),
            new HumanReviewDecisionStoreTestClock(approved.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(approved.Id, approved.LifecycleVersion, "post-approval-conflict", HumanReviewDecisionKind.Reject, null));
        var afterConflict = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));
        var publisher = new HumanReviewContinuationPublicationService(store, new HumanReviewContinuationRunStore(store));

        var published = await publisher.PublishAsync(approved.Id);
        var durable = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(approved.Id));
        var replay = await new HumanReviewContinuationPublicationService(store, new HumanReviewContinuationRunStore(store)).PublishAsync(approved.Id);

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, conflict.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, conflict.Receipt?.Disposition);
        Assert.True(afterConflict.UpdatedAtUtc > reservation.ReservedAtUtc);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Committed, published.Status);
        Assert.Equal(reservation.ReservedAtUtc, durable.HumanReview?.Continuation?.Wake.PublishedAtUtc);
        Assert.Equal(afterConflict.UpdatedAtUtc, durable.UpdatedAtUtc);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Replayed, replay.Status);
    }

    [Fact]
    public async Task Canonical_store_publishes_claims_takes_over_and_completes_one_approval_across_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-flow");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-flow");
        var publishedState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using (var store = new CustomLoopRunStore(paths))
        {
            var continuations = new HumanReviewContinuationRunStore(store);
            var publication = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, publishedState);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, publication.Status);
            Assert.Equal(publishedState.StateHash, publication.Run?.HumanReview?.Continuation?.StateHash);

            var published = Assert.IsType<CustomLoopRunRecord>(publication.Run);
            var first = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-flow-one");
            var firstClaim = await continuations.ClaimAsync(published.Id, published.LifecycleVersion, first);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, firstClaim.Status);

            var claimed = Assert.IsType<CustomLoopRunRecord>(firstClaim.Run);
            Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.ClaimAsync(claimed.Id, claimed.LifecycleVersion, first)).Status);
            var divergentFirst = HumanReviewContinuationContractHash.ApplyClaim(first with { WorkerId = "worker-claim-flow-divergent", ClaimHash = string.Empty });
            Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.ClaimAsync(claimed.Id, claimed.LifecycleVersion, divergentFirst)).Status);
            var second = Claim(wake, reservation, first.LeaseExpiresAtUtc.AddTicks(1), "claim-flow-two");
            var takeover = await continuations.ClaimAsync(claimed.Id, claimed.LifecycleVersion, second);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, takeover.Status);

            var takenOver = Assert.IsType<CustomLoopRunRecord>(takeover.Run);
            Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.ClaimAsync(takenOver.Id, takenOver.LifecycleVersion, first)).Status);
            var staleCompletion = Completion(review.Request, wake, reservation, first, first.ClaimedAtUtc.AddSeconds(1), "completion-stale");
            var stale = await continuations.CompleteAsync(takenOver.Id, takenOver.LifecycleVersion, staleCompletion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, stale.Status);

            var completion = Completion(review.Request, wake, reservation, second, second.ClaimedAtUtc.AddSeconds(1), "completion-flow");
            var completed = await continuations.CompleteAsync(takenOver.Id, takenOver.LifecycleVersion, completion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, completed.Status);
            Assert.Equal(completion.CompletionHash, completed.Run?.HumanReview?.Continuation?.Completion?.CompletionHash);
            Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.ClaimAsync(completed.Run!.Id, completed.Run.LifecycleVersion, second)).Status);

            var replay = await continuations.CompleteAsync(takenOver.Id, takenOver.LifecycleVersion, completion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, replay.Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        var continuation = Assert.IsType<HumanReviewContinuationState>(persisted.HumanReview?.Continuation);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Equal(2, continuation.Claims.Length);
        Assert.Equal("claim-flow-two", continuation.Claims[^1].ClaimId);
        Assert.NotNull(continuation.Completion);
        Assert.Null(continuation.Retirement);
    }

    [Fact]
    public async Task Publication_and_terminal_retirement_require_exact_replay_and_never_cross_terminal_boundaries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-retirement");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-retirement");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using var store = new CustomLoopRunStore(paths);
        var continuations = new HumanReviewContinuationRunStore(store);
        var publication = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, publication.Status);
        var replay = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, replay.Status);
        var divergentWake = HumanReviewContinuationContractHash.ApplyWake(wake with { WakeId = "wake-retirement-divergent", WakeHash = string.Empty });
        var divergent = HumanReviewContinuationContractHash.ApplyState(initial with { Wake = divergentWake, StateHash = string.Empty });
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, divergent)).Status);

        var published = Assert.IsType<CustomLoopRunRecord>(publication.Run);
        var claim = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-retirement");
        var claimed = await continuations.ClaimAsync(published.Id, published.LifecycleVersion, claim);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, claimed.Status);
        var retirement = Retirement(wake, reservation, claim.LeaseExpiresAtUtc.AddTicks(1), "retirement-flow");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.RetireAsync(
            claimed.Run!.Id,
            claimed.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference("claim-retirement-unknown", Hash('d')),
            retirement)).Status);
        var retired = await continuations.RetireAsync(
            claimed.Run!.Id,
            claimed.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            retirement);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, retired.Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.RetireAsync(
            claimed.Run.Id,
            claimed.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            retirement)).Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.RetireAsync(
            retired.Run!.Id,
            retired.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference("claim-retirement-unknown", Hash('d')),
            retirement)).Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.ClaimAsync(retired.Run!.Id, retired.Run.LifecycleVersion, claim)).Status);

        var completion = Completion(review.Request, wake, reservation, claim, claim.ClaimedAtUtc.AddSeconds(1), "completion-after-retirement");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.CompleteAsync(claimed.Run.Id, claimed.Run.LifecycleVersion, completion)).Status);
    }

    [Fact]
    public async Task Separate_process_claimers_share_one_compare_exchange_winner_and_one_append_only_claim()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-claim-race");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-claim-race");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        using (var store = new CustomLoopRunStore(paths))
        {
            var published = await new HumanReviewContinuationRunStore(store).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, published.Status);
        }

        var firstReadyPath = Path.Combine(workspace.RootPath, "claim-race-first.ready");
        var secondReadyPath = Path.Combine(workspace.RootPath, "claim-race-second.ready");
        var releasePath = Path.Combine(workspace.RootPath, "claim-race.release");
        var firstResultPath = Path.Combine(workspace.RootPath, "claim-race-first.result");
        var secondResultPath = Path.Combine(workspace.RootPath, "claim-race-second.result");
        using var first = CancellationHostProcess.Start("human-review-continuation-claim-race", workspace.RootPath, approved.Id, "first", firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.Start("human-review-continuation-claim-race", workspace.RootPath, approved.Id, "second", secondReadyPath, releasePath, secondResultPath);
        await WaitForFileAsync(firstReadyPath, TimeSpan.FromSeconds(30));
        await WaitForFileAsync(secondReadyPath, TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(releasePath, "release");
        await first.WaitForExitAsync();
        await second.WaitForExitAsync();

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var outcomes = new[] { await File.ReadAllTextAsync(firstResultPath), await File.ReadAllTextAsync(secondResultPath) };
        Assert.Equal(1, outcomes.Count(item => item == HumanReviewContinuationMutationStatus.Committed.ToString()));
        Assert.Equal(1, outcomes.Count(item => item == HumanReviewContinuationMutationStatus.Conflict.ToString()));

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        var continuation = Assert.IsType<HumanReviewContinuationState>(persisted.HumanReview?.Continuation);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Single(continuation.Claims);
        Assert.Contains(continuation.Claims[0].ClaimId, new[] { "claim-race-first", "claim-race-second" });
    }

    [Fact]
    public async Task Response_unknown_after_canonical_rename_rereads_the_exact_published_wake_without_republishing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-response-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-response-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        using (var uncertain = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed
            ? ValueTask.FromException(new IOException("response lost after canonical publication"))
            : ValueTask.CompletedTask))
        {
            var result = await new HumanReviewContinuationRunStore(uncertain).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, result.Status);
            Assert.Equal(initial.StateHash, result.Run?.HumanReview?.Continuation?.StateHash);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        Assert.Equal(initial.StateHash, persisted.HumanReview?.Continuation?.StateHash);
        Assert.Equal(approved.LifecycleVersion + 1, persisted.LifecycleVersion);
    }

    [Fact]
    public async Task Response_unknown_publication_replay_accepts_the_published_wake_after_a_recovery_worker_claims_it()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-response-loss-descendant");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-response-loss-descendant");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        var claim = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-response-loss-descendant");
        using var canonical = new CustomLoopRunStore(paths);
        HumanReviewContinuationMutationResult? claimed = null;
        var uncertain = new ResponseLossAfterClaimingCustomLoopRunStore(canonical, async published =>
        {
            claimed = await new HumanReviewContinuationRunStore(canonical).ClaimAsync(published.Id, published.LifecycleVersion, claim);
        });

        var replay = await new HumanReviewContinuationRunStore(uncertain).PublishAsync(approved.Id, approved.LifecycleVersion, initial);

        Assert.NotNull(claimed);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, claimed.Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, replay.Status);
        Assert.Equal(claimed.Run!.LifecycleVersion, replay.Run?.LifecycleVersion);
        Assert.Equal(wake.WakeHash, replay.Run?.HumanReview?.Continuation?.Wake.WakeHash);
        Assert.Equal(claim.ClaimHash, replay.Run?.HumanReview?.Continuation?.Claims[^1].ClaimHash);
    }

    [Fact]
    public async Task Corrupt_canonical_run_fails_closed_without_rewriting_the_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-corruption");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, approved.LoopId, approved.Id + ".json");
        await File.WriteAllTextAsync(artifactPath, "{");
        var corruptedBytes = await File.ReadAllBytesAsync(artifactPath);
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-corruption");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using var store = new CustomLoopRunStore(paths);
        var result = await new HumanReviewContinuationRunStore(store).PublishAsync(approved.Id, approved.LifecycleVersion, initial);

        Assert.Equal(HumanReviewContinuationMutationStatus.Invalid, result.Status);
        Assert.Equal(corruptedBytes, await File.ReadAllBytesAsync(artifactPath));
    }

    [Fact]
    public async Task Canonical_read_io_failure_and_explicit_quota_rejection_are_closed_mutation_results_without_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-unavailable-quota");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-unavailable-quota");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using (var unavailable = new CustomLoopRunStore(paths, null, (_, _, _) => ValueTask.FromException(new IOException("test read unavailable"))))
        {
            var unavailableResult = await new HumanReviewContinuationRunStore(unavailable).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.Unavailable, unavailableResult.Status);
        }

        using (var canonical = new CustomLoopRunStore(paths))
        {
            var quotaResult = await new HumanReviewContinuationRunStore(new QuotaRejectingCustomLoopRunStore(canonical)).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.LimitExceeded, quotaResult.Status);
            Assert.Null((await canonical.GetAsync(approved.Id))?.HumanReview?.Continuation);
        }
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
        var approved = await CreateApprovedRunAsync(paths, "continuation-claim-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-claim-process-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        CustomLoopRunRecord published;
        using (var store = new CustomLoopRunStore(paths))
        {
            published = Assert.IsType<CustomLoopRunRecord>((await new HumanReviewContinuationRunStore(store).PublishAsync(approved.Id, approved.LifecycleVersion, initial)).Run);
        }

        var expected = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(published);
        await RunTransitionProcessLossAsync(workspace, published.Id, "claim", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(published.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation?.Claims.IsEmpty == true)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.ClaimHash, recovered.HumanReview?.Continuation?.Claims.Single().ClaimHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).ClaimAsync(recovered.Id, recovered.LifecycleVersion, expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        var claimed = Assert.IsType<CustomLoopRunRecord>(reconciliation.Run);
        var takeover = Claim(wake, reservation, expected.LeaseExpiresAtUtc.AddTicks(1), "claim-process-loss-takeover");
        var takenOver = await new HumanReviewContinuationRunStore(restarted).ClaimAsync(claimed.Id, claimed.LifecycleVersion, takeover);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, takenOver.Status);
        var staleCompletion = Completion(review.Request, wake, reservation, expected, expected.ClaimedAtUtc.AddSeconds(1), "completion-process-loss-stale");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await new HumanReviewContinuationRunStore(restarted).CompleteAsync(takenOver.Run!.Id, takenOver.Run.LifecycleVersion, staleCompletion)).Status);
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
        var approved = await CreateApprovedRunAsync(paths, "continuation-completion-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-completion-process-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var claim = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-completion-process-loss");
        CustomLoopRunRecord claimed;
        using (var store = new CustomLoopRunStore(paths))
        {
            var continuations = new HumanReviewContinuationRunStore(store);
            var published = Assert.IsType<CustomLoopRunRecord>((await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial)).Run);
            claimed = Assert.IsType<CustomLoopRunRecord>((await continuations.ClaimAsync(published.Id, published.LifecycleVersion, claim)).Run);
        }

        var expected = Completion(review.Request, wake, reservation, claim, claim.ClaimedAtUtc.AddSeconds(1), "completion-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(claimed);
        await RunTransitionProcessLossAsync(workspace, claimed.Id, "completion", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation?.Completion is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.CompletionHash, recovered.HumanReview.Continuation.Completion.CompletionHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).CompleteAsync(recovered.Id, recovered.LifecycleVersion, expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        var retirement = Retirement(wake, reservation, expected.CompletedAtUtc.AddSeconds(1), "retirement-after-completion-process-loss");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await new HumanReviewContinuationRunStore(restarted).RetireAsync(
            reconciliation.Run!.Id,
            reconciliation.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            retirement)).Status);
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
        var approved = await CreateApprovedRunAsync(paths, "continuation-retirement-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-retirement-process-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var claim = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-retirement-process-loss");
        CustomLoopRunRecord claimed;
        using (var store = new CustomLoopRunStore(paths))
        {
            var continuations = new HumanReviewContinuationRunStore(store);
            var published = Assert.IsType<CustomLoopRunRecord>((await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial)).Run);
            claimed = Assert.IsType<CustomLoopRunRecord>((await continuations.ClaimAsync(published.Id, published.LifecycleVersion, claim)).Run);
        }

        var expected = Retirement(wake, reservation, claim.ClaimedAtUtc.AddSeconds(1), "retirement-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(claimed);
        await RunTransitionProcessLossAsync(workspace, claimed.Id, "retirement", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation?.Retirement is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.RetirementHash, recovered.HumanReview.Continuation.Retirement.RetirementHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).RetireAsync(
            recovered.Id,
            recovered.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        var completion = Completion(review.Request, wake, reservation, claim, claim.ClaimedAtUtc.AddSeconds(1), "completion-after-retirement-process-loss");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await new HumanReviewContinuationRunStore(restarted).CompleteAsync(reconciliation.Run!.Id, reconciliation.Run.LifecycleVersion, completion)).Status);
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_publication_boundary_converges_on_the_one_exact_wake_or_its_untouched_predecessor(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-publication-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var expectedWake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-process-loss");
        var expected = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, expectedWake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(approved);
        using var process = CancellationHostProcess.Start("human-review-continuation-publication-process-loss", workspace.RootPath, approved.Id, boundary.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
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

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("test host process crashed", await errorTask, StringComparison.OrdinalIgnoreCase);
        _ = await outputTask;
        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.StateHash, recovered.HumanReview.Continuation.StateHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).PublishAsync(recovered.Id, recovered.LifecycleVersion, expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        Assert.Equal(expected.StateHash, reconciliation.Run?.HumanReview?.Continuation?.StateHash);
    }

    private static async Task<CustomLoopRunRecord> CreateArchivedCurrentReviewAsync(WorkspacePaths paths, string identity, bool retire)
    {
        var admitted = await CustomLoopFrontierStoreTests.PersistStrictHumanReviewAdmissionAsync(paths, identity);
        using var store = new CustomLoopRunStore(paths);
        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionStoreTestAuthorizer(),
            new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(admitted.Id, admitted.LifecycleVersion, "approve-" + identity, HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        var approved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var approvedReview = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(approvedReview.ContinuationReservation);
        var wakeAtUtc = approved.UpdatedAtUtc.AddSeconds(1);
        var wake = Wake(approvedReview, reservation, wakeAtUtc, "wake-" + identity);
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [], null, null, string.Empty));
        var continuations = new HumanReviewContinuationRunStore(store);
        var published = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, published.Status);
        var publishedRun = Assert.IsType<CustomLoopRunRecord>(published.Run);
        var claim = Claim(wake, reservation, wakeAtUtc.AddMinutes(1), "claim-" + identity);
        var claimed = await continuations.ClaimAsync(publishedRun.Id, publishedRun.LifecycleVersion, claim);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, claimed.Status);
        var claimedRun = Assert.IsType<CustomLoopRunRecord>(claimed.Run);
        CustomLoopRunRecord terminal;
        if (retire)
        {
            var retirement = Retirement(wake, reservation, claim.ClaimedAtUtc.AddSeconds(1), "retirement-" + identity);
            var retired = await continuations.RetireAsync(claimedRun.Id, claimedRun.LifecycleVersion, new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash), retirement);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, retired.Status);
            terminal = Assert.IsType<CustomLoopRunRecord>(retired.Run);
        }
        else
        {
            var completion = Completion(approvedReview.Request, wake, reservation, claim, claim.ClaimedAtUtc.AddSeconds(1), "completion-" + identity);
            var completed = await continuations.CompleteAsync(claimedRun.Id, claimedRun.LifecycleVersion, completion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, completed.Status);
            terminal = Assert.IsType<CustomLoopRunRecord>(completed.Run);
        }

        var archivedState = Assert.IsType<HumanReviewRunState>(terminal.HumanReview);
        var nextRequest = HumanReviewContractHash.ApplyRequest(archivedState.Request with
        {
            RequestId = "review-request-next-" + identity,
            RequestOperationId = "review-request-operation-next-" + identity,
            Provenance = archivedState.Request.Provenance with { CorrelationId = "review-request-operation-next-" + identity, ProvenanceHash = string.Empty },
            RequestHash = string.Empty,
        });
        var nextAtUtc = terminal.UpdatedAtUtc.AddTicks(1);
        var requestReference = new HumanReviewRequestReference(nextRequest.RequestId, nextRequest.RequestHash);
        var lifecycle = HumanReviewContractHash.ApplyLifecycle(new HumanReviewLifecycle(1, requestReference, HumanReviewLifecycleStatus.Pending, 1, nextAtUtc, null, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-store", nextRequest.RequestOperationId, nextAtUtc, string.Empty)), null, string.Empty));
        var evidence = HumanReviewContractHash.ApplyEvidence(new HumanReviewEvidence(1, "evidence-next-" + identity, requestReference, HumanReviewEvidenceKind.RequestAdmitted, null, nextAtUtc, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-store", nextRequest.RequestOperationId, nextAtUtc, string.Empty)), ImmutableArray<HumanReviewRedactedPreview>.Empty, null, string.Empty));
        var nextAdmissionEvent = new CustomLoopRunEvent(
            terminal.Events[^1].Sequence + 1,
            "event-next-admitted-" + identity,
            nextAtUtc,
            CustomLoopRunEventKind.HumanReviewRequestAdmitted,
            null,
            null,
            null,
            "The follow-on Human Review request was admitted for archived replay qualification.",
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
            null)
        { HumanReviewEvidence = evidence };
        var nextState = new HumanReviewRunState(nextRequest, lifecycle, [evidence])
        {
            CompletedReviews = [archivedState with { CompletedReviews = [] }],
        };
        var candidate = terminal with
        {
            LifecycleVersion = terminal.LifecycleVersion + 1,
            UpdatedAtUtc = nextAtUtc,
            HumanReview = nextState,
            Events = [.. terminal.Events, nextAdmissionEvent],
        };
        var validation = CustomLoopRunValidator.ValidateUpdate(terminal, candidate);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var persisted = await store.UpdateAsync(candidate, terminal.LifecycleVersion);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, persisted.Status);
        return Assert.IsType<CustomLoopRunRecord>(persisted.Run);
    }

    private static async Task<CustomLoopRunRecord> CreateApprovedRunAsync(WorkspacePaths paths, string identity)
    {
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, identity);
        using var store = new CustomLoopRunStore(paths);
        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionStoreTestAuthorizer(),
            new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(admitted.Id, admitted.LifecycleVersion, "approve-" + identity, HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        return Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
    }

    private static HumanReviewContinuationWake Wake(HumanReviewRunState review, HumanReviewContinuationReservation reservation, DateTimeOffset publishedAtUtc, string wakeId)
        => HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            1,
            wakeId,
            new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
            reservation.Decision,
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            review.Request.Binding.BindingHash,
            1,
            publishedAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            Provenance(wakeId, publishedAtUtc),
            string.Empty));

    private static HumanReviewContinuationClaim Claim(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, DateTimeOffset claimedAtUtc, string claimId)
        => HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            claimId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "worker-" + claimId,
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            Provenance(claimId, claimedAtUtc),
            string.Empty));

    private static HumanReviewContinuationCompletion Completion(HumanReviewRequest request, HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim, DateTimeOffset completedAtUtc, string completionId)
    {
        var receipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(new HumanReviewContinuationReleaseReceipt(
            1,
            "release-" + completionId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            HumanReviewContinuationReleaseKind.Continuation,
            HumanReviewContinuationReleaseDisposition.Released,
            Hash('a'),
            Hash('b'),
            null,
            string.Empty));
        return HumanReviewContinuationContractHash.ApplyCompletion(new HumanReviewContinuationCompletion(
            1,
            completionId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            receipt,
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance(completionId, completedAtUtc),
            string.Empty));
    }

    private static HumanReviewContinuationRetirement Retirement(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, DateTimeOffset retiredAtUtc, string retirementId)
        => HumanReviewContinuationContractHash.ApplyRetirement(new HumanReviewContinuationRetirement(
            1,
            retirementId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            HumanReviewContinuationOutcome.Blocked,
            retiredAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance(retirementId, retiredAtUtc),
            string.Empty));

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-continuation-store", correlationId, observedAtUtc, string.Empty));

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    private static async Task RunTransitionProcessLossAsync(TestWorkspace workspace, string runId, string transition, CustomLoopRunPublicationBoundary boundary)
    {
        using var process = CancellationHostProcess.Start("human-review-continuation-transition-process-loss", workspace.RootPath, runId, transition, boundary.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
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

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("test host process crashed", await errorTask, StringComparison.OrdinalIgnoreCase);
        _ = await outputTask;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }

}
