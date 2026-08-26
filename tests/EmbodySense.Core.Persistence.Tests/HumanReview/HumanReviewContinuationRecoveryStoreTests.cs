using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewContinuationRecoveryStoreTests
{
    [Fact]
    public async Task Terminal_nonempty_source_page_emits_a_tail_probe_before_clean_empty_tail_resets_scan()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-tail-probe");
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var now = published.Continuation.Wake.PublishedAtUtc.AddSeconds(1);

        var first = await recovery.ListCandidatesAsync(1, null, now);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Current, first.Status);
        Assert.Single(first.Candidates);
        Assert.False(first.SourceTruncated);
        Assert.False(string.IsNullOrWhiteSpace(first.NextScanCursor));

        var tail = await recovery.ListCandidatesAsync(1, first.NextScanCursor, now);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Current, tail.Status);
        Assert.Empty(tail.Candidates);
        Assert.False(tail.SourceTruncated);
        Assert.Null(tail.NextScanCursor);
    }

    [Fact]
    public async Task Unclaimed_expired_wake_is_retained_but_never_returned_as_claimable_work()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-expired-unclaimed");
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(10, null, published.Continuation.Wake.ExpiresAtUtc);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Current, page.Status);
        Assert.Empty(page.Candidates);
        Assert.False(page.SourceTruncated);
        Assert.False(string.IsNullOrWhiteSpace(page.NextScanCursor));
        Assert.Null((await runs.GetAsync(published.Run.Id))?.HumanReview?.Continuation?.Claims.FirstOrDefault());
    }

    [Fact]
    public async Task Strictly_expired_claim_is_rediscovered_only_while_its_wake_remains_valid()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-expired-claim");
        using var runs = new CustomLoopRunStore(paths);
        var claim = Claim(published.Continuation.Wake, published.Reservation, published.Continuation.Wake.PublishedAtUtc.AddSeconds(1));
        var claimed = await new HumanReviewContinuationRunStore(runs).ClaimAsync(published.Run.Id, published.Run.LifecycleVersion, claim);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, claimed.Status);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());

        var rediscovered = await recovery.ListCandidatesAsync(10, null, claim.LeaseExpiresAtUtc.AddTicks(1));
        var candidate = Assert.Single(rediscovered.Candidates);
        Assert.Equal(new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash), candidate.PriorClaim);

        var expiredWake = await recovery.ListCandidatesAsync(10, null, published.Continuation.Wake.ExpiresAtUtc);
        Assert.Empty(expiredWake.Candidates);
        Assert.Single((await runs.GetAsync(published.Run.Id))!.HumanReview!.Continuation!.Claims);
    }

    [Fact]
    public async Task Exact_claim_is_reread_with_its_pinned_immutable_graph_before_consumption_can_begin()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-exact-reread");
        var graph = GraphArtifact();
        Assert.Equal(published.Run.SequentialAdapterBinding?.GraphArtifactHash, graph.ArtifactHash);
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore(graph));
        var now = published.Continuation.Wake.PublishedAtUtc.AddSeconds(1);
        var candidate = Assert.Single((await recovery.ListCandidatesAsync(10, null, now)).Candidates);
        var claim = Claim(published.Continuation.Wake, published.Reservation, now);

        var claimed = await recovery.ClaimAsync(new HumanReviewContinuationClaimIntent(candidate, claim));
        var reread = await recovery.ReadAsync(new HumanReviewContinuationCandidateQuery(
            candidate.RunId,
            candidate.Request,
            candidate.Decision,
            candidate.Wake,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            candidate.Reservation,
            candidate.ExpectedGeneration));

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Committed, claimed.Status);
        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Current, reread.Status);
        Assert.Equal(claim.ClaimHash, reread.Candidate?.Claim?.ClaimHash);
        Assert.Equal(graph.ArtifactHash, reread.Candidate?.GraphArtifact?.ArtifactHash);
    }

    [Fact]
    public async Task Filtered_underfilled_source_page_retains_the_source_cursor_when_more_summaries_exist()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var summary = new CustomLoopRunSummary("run-raced-away", "loop-one", "admit-one", 1, 1, CustomLoopRunStatus.Paused, now, now, null, 0, 0, null, false);
        var runs = new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([summary], "source-next"));
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(1, "source-before", now);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Current, page.Status);
        Assert.Empty(page.Candidates);
        Assert.True(page.SourceTruncated);
        Assert.Equal("source-next", page.NextScanCursor);
        Assert.Equal("source-before", runs.ReceivedCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Discovery_rejects_invalid_bounds_without_reading_canonical_runs(int maximumCount)
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([], null)),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(maximumCount, null, now);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Invalid, page.Status);
        Assert.Empty(page.Candidates);
        Assert.Null(page.NextScanCursor);
    }

    [Theory]
    [InlineData(typeof(ArgumentException), HumanReviewContinuationRecoveryPageStatus.Invalid)]
    [InlineData(typeof(FormatException), HumanReviewContinuationRecoveryPageStatus.Invalid)]
    [InlineData(typeof(IOException), HumanReviewContinuationRecoveryPageStatus.Unavailable)]
    public async Task Discovery_classifies_source_page_failures_without_retaining_a_cursor(Type exceptionType, HumanReviewContinuationRecoveryPageStatus expected)
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var exception = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([], null), listException: exception),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(1, "before", now);

        Assert.Equal(expected, page.Status);
        Assert.Null(page.NextScanCursor);
        Assert.False(page.SourceTruncated);
    }

    [Fact]
    public async Task Discovery_propagates_requested_cancellation_from_the_source_page()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([], null), listException: new OperationCanceledException(cancellation.Token)),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ListCandidatesAsync(1, null, new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), cancellation.Token));
    }

    [Theory]
    [InlineData(typeof(FormatException), HumanReviewContinuationRecoveryPageStatus.Invalid)]
    [InlineData(typeof(IOException), HumanReviewContinuationRecoveryPageStatus.Unavailable)]
    public async Task Discovery_classifies_exact_summary_reread_failures(Type exceptionType, HumanReviewContinuationRecoveryPageStatus expected)
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var summary = new CustomLoopRunSummary("run-raced-away", "loop-one", "admit-one", 1, 1, CustomLoopRunStatus.Paused, now, now, null, 0, 0, null, false);
        var exception = Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType));
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([summary], null), getException: exception),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(1, null, now);

        Assert.Equal(expected, page.Status);
        Assert.Empty(page.Candidates);
        Assert.Null(page.NextScanCursor);
    }

    [Fact]
    public async Task Discovery_propagates_requested_cancellation_from_an_exact_summary_reread()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var summary = new CustomLoopRunSummary("run-raced-away", "loop-one", "admit-one", 1, 1, CustomLoopRunStatus.Paused, now, now, null, 0, 0, null, false);
        using var cancellation = new CancellationTokenSource();
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(
                new CustomLoopRunPage([summary], null),
                getException: new OperationCanceledException(cancellation.Token),
                onGet: cancellation.Cancel),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ListCandidatesAsync(1, null, now, cancellation.Token));
    }

    [Fact]
    public async Task Discovery_rejects_null_or_overfilled_source_pages()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var summary = new CustomLoopRunSummary("run-raced-away", "loop-one", "admit-one", 1, 1, CustomLoopRunStatus.Paused, now, now, null, 0, 0, null, false);
        var nullSource = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(null!),
            new HumanReviewContinuationRecoveryUnusedGraphStore());
        var overfilled = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([summary, summary], null)),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        var nullPage = await nullSource.ListCandidatesAsync(1, null, now);
        var overfilledPage = await overfilled.ListCandidatesAsync(1, null, now);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Invalid, nullPage.Status);
        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Invalid, overfilledPage.Status);
    }

    [Fact]
    public async Task Discovery_rejects_malformed_summary_before_exact_reread()
    {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        var malformed = new CustomLoopRunSummary("", "loop-one", "admit-one", 1, 1, CustomLoopRunStatus.Paused, now, now, null, 0, 0, null, false);
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([malformed], null)),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(1, null, now);

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Invalid, page.Status);
        Assert.Empty(page.Candidates);
    }

    [Fact]
    public async Task Exact_reread_classifies_missing_stale_and_unavailable_artifact_evidence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-reread-statuses");
        using var runs = new CustomLoopRunStore(paths);
        var discovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var candidate = Assert.Single((await discovery.ListCandidatesAsync(1, null, published.Continuation.Wake.PublishedAtUtc.AddSeconds(1))).Candidates);
        var exact = new HumanReviewContinuationCandidateQuery(candidate.RunId, candidate.Request, candidate.Decision, candidate.Wake, null, candidate.Reservation, candidate.ExpectedGeneration);
        var unavailable = await discovery.ReadAsync(exact);
        var missing = await discovery.ReadAsync(exact with { RunId = "run-missing" });
        var stale = await discovery.ReadAsync(exact with { Request = new HumanReviewRequestReference("request-other", candidate.Request.RequestHash) });

        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Unavailable, unavailable.Status);
        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Missing, missing.Status);
        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Stale, stale.Status);
    }

    [Theory]
    [InlineData(typeof(FormatException), HumanReviewContinuationCandidateReadStatus.Corrupt)]
    [InlineData(typeof(IOException), HumanReviewContinuationCandidateReadStatus.Unavailable)]
    public async Task Exact_reread_classifies_invalid_query_and_canonical_read_failures(Type exceptionType, HumanReviewContinuationCandidateReadStatus expected)
    {
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([], null), getException: Assert.IsAssignableFrom<Exception>(Activator.CreateInstance(exceptionType))),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        var invalid = await recovery.ReadAsync(null!);
        var failure = await recovery.ReadAsync(new HumanReviewContinuationCandidateQuery("run-one", null!, null!, null, null, null, null));

        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Corrupt, invalid.Status);
        Assert.Equal(expected, failure.Status);
    }

    [Fact]
    public async Task Exact_reread_propagates_requested_cancellation_from_the_canonical_run_source()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var recovery = new HumanReviewContinuationRecoveryStore(
            new HumanReviewContinuationRecoveryPagingTestStore(new CustomLoopRunPage([], null), getException: new OperationCanceledException(cancellation.Token)),
            new HumanReviewContinuationRecoveryUnusedGraphStore());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ReadAsync(new HumanReviewContinuationCandidateQuery("run-one", null!, null!, null, null, null, null), cancellation.Token));
    }

    [Fact]
    public async Task Exact_reread_rejects_any_changed_continuation_fence()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-reread-fences");
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var candidate = Assert.Single((await recovery.ListCandidatesAsync(1, null, published.Continuation.Wake.PublishedAtUtc.AddSeconds(1))).Candidates);
        var exact = new HumanReviewContinuationCandidateQuery(candidate.RunId, candidate.Request, candidate.Decision, candidate.Wake, null, candidate.Reservation, candidate.ExpectedGeneration);

        var reservation = await recovery.ReadAsync(exact with { Reservation = new HumanReviewContinuationReservationReference("reservation-other", candidate.Reservation.ReservationHash) });
        var wake = await recovery.ReadAsync(exact with { Wake = new HumanReviewContinuationWakeReference("wake-other", candidate.Wake.WakeHash) });
        var claim = await recovery.ReadAsync(exact with { Claim = new HumanReviewContinuationClaimReference("claim-other", candidate.Wake.WakeHash) });
        var generation = await recovery.ReadAsync(exact with { ExpectedGeneration = candidate.ExpectedGeneration + 1 });

        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Stale, reservation.Status);
        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Stale, wake.Status);
        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Stale, claim.Status);
        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Stale, generation.Status);
    }

    [Fact]
    public async Task Active_claim_is_not_returned_until_strictly_after_its_lease_expiry()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-active-claim");
        using var runs = new CustomLoopRunStore(paths);
        var claim = Claim(published.Continuation.Wake, published.Reservation, published.Continuation.Wake.PublishedAtUtc.AddSeconds(1));
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, (await new HumanReviewContinuationRunStore(runs).ClaimAsync(published.Run.Id, published.Run.LifecycleVersion, claim)).Status);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());

        var page = await recovery.ListCandidatesAsync(1, null, claim.ClaimedAtUtc.AddSeconds(1));

        Assert.Equal(HumanReviewContinuationRecoveryPageStatus.Current, page.Status);
        Assert.Empty(page.Candidates);
    }

    [Fact]
    public async Task Exact_reread_never_treats_a_missing_immutable_graph_as_current()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-reread-artifact");
        using var runs = new CustomLoopRunStore(paths);
        var discovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var candidate = Assert.Single((await discovery.ListCandidatesAsync(1, null, published.Continuation.Wake.PublishedAtUtc.AddSeconds(1))).Candidates);
        var exact = new HumanReviewContinuationCandidateQuery(candidate.RunId, candidate.Request, candidate.Decision, candidate.Wake, null, candidate.Reservation, candidate.ExpectedGeneration);
        var missing = await new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore(null, GovernedLoopRevisionStoreReadStatus.NotFound)).ReadAsync(exact);

        Assert.Equal(HumanReviewContinuationCandidateReadStatus.Missing, missing.Status);
    }

    [Fact]
    public async Task Exact_reread_propagates_requested_cancellation_from_the_immutable_graph_source()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var published = await PublishApprovedWakeAsync(paths, "recovery-reread-cancellation");
        using var runs = new CustomLoopRunStore(paths);
        var discovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var candidate = Assert.Single((await discovery.ListCandidatesAsync(1, null, published.Continuation.Wake.PublishedAtUtc.AddSeconds(1))).Candidates);
        var query = new HumanReviewContinuationCandidateQuery(candidate.RunId, candidate.Request, candidate.Decision, candidate.Wake, null, candidate.Reservation, candidate.ExpectedGeneration);
        using var cancellation = new CancellationTokenSource();
        var recovery = new HumanReviewContinuationRecoveryStore(
            runs,
            new HumanReviewContinuationRecoveryUnusedGraphStore(
                exception: new OperationCanceledException(cancellation.Token),
                onRead: cancellation.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.ReadAsync(query, cancellation.Token));
    }

    [Fact]
    public async Task Inconsistent_claim_completion_and_retirement_intents_are_rejected_before_mutation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());

        var claim = await recovery.ClaimAsync(new HumanReviewContinuationClaimIntent(null!, null!));
        var completion = await recovery.CompleteAsync(null!, null!);
        var retirement = await recovery.RetireAsync(null!, null!);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, claim.Status);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, completion.Status);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, retirement.Status);
    }

    [Fact]
    public async Task Completion_cannot_substitute_a_different_prepared_release_operation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var hash = new string('a', 64);
        var request = new HumanReviewRequestReference("request-one", hash);
        var wake = new HumanReviewContinuationWakeReference("wake-one", hash);
        var claim = new HumanReviewContinuationClaimReference("claim-one", hash);
        var reservation = new HumanReviewContinuationReservationReference("reservation-one", hash);
        var releaseIntent = new HumanReviewContinuationReleaseReceiptIntent("release-expected", request, wake, claim, reservation, 1, HumanReviewContinuationReleaseKind.Continuation, null);
        var intent = new HumanReviewContinuationCompletionIntent("run-one", 2, request, wake, claim, reservation, 1, releaseIntent);
        var substitutedReceipt = new HumanReviewContinuationReleaseReceipt(1, "release-substituted", wake, claim, reservation, 1, HumanReviewContinuationReleaseKind.Continuation, HumanReviewContinuationReleaseDisposition.Released, hash, hash, null, hash);
        var completion = new HumanReviewContinuationCompletion(1, "completion-one", wake, claim, reservation, 1, substitutedReceipt, new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), [], null!, hash);

        var result = await recovery.CompleteAsync(intent, completion);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Malformed_claim_intent_is_translated_to_a_closed_invalid_result()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        using var runs = new CustomLoopRunStore(paths);
        var recovery = new HumanReviewContinuationRecoveryStore(runs, new HumanReviewContinuationRecoveryUnusedGraphStore());
        var hash = new string('a', 64);
        var wake = new HumanReviewContinuationWakeReference("wake-one", hash);
        var reservation = new HumanReviewContinuationReservationReference("reservation-one", hash);
        var candidate = new HumanReviewContinuationRecoveryCandidate("", 1, null!, null!, wake, 1, new DateTimeOffset(2026, 8, 26, 13, 0, 0, TimeSpan.Zero), reservation, null);
        var claim = new HumanReviewContinuationClaim(1, "claim-one", wake, reservation, 1, "worker-one", new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 26, 12, 1, 0, TimeSpan.Zero), null!, hash);

        var result = await recovery.ClaimAsync(new HumanReviewContinuationClaimIntent(candidate, claim));

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, result.Status);
    }

    private static async Task<HumanReviewContinuationRecoveryPublishedWake> PublishApprovedWakeAsync(WorkspacePaths paths, string identity)
    {
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, identity);
        using var runs = new CustomLoopRunStore(paths);
        var accepted = await new HumanReviewDecisionService(
            runs,
            new HumanReviewDecisionStoreTestAuthorizer(),
            new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(admitted.Id, admitted.LifecycleVersion, "approve-" + identity, HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, accepted.Status);
        var approved = Assert.IsType<CustomLoopRunRecord>(await runs.GetAsync(admitted.Id));
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-" + identity);
        var continuation = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var publication = await new HumanReviewContinuationRunStore(runs).PublishAsync(approved.Id, approved.LifecycleVersion, continuation);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, publication.Status);
        return new HumanReviewContinuationRecoveryPublishedWake(Assert.IsType<CustomLoopRunRecord>(publication.Run), reservation, continuation);
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

    private static HumanReviewContinuationClaim Claim(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, DateTimeOffset claimedAtUtc)
        => HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            "claim-recovery",
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "recovery-worker",
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            Provenance("claim-recovery", claimedAtUtc),
            string.Empty));

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "recovery-coordinator", correlationId, observedAtUtc, string.Empty));

    private static GovernedLoopGraphRevisionArtifact GraphArtifact()
    {
        var graph = CustomLoopSequentialEvidenceStoreTests.LinearGraph();
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-sequential", "user-owner", new DateTimeOffset(2026, 8, 10, 11, 0, 0, TimeSpan.Zero));
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }
}
