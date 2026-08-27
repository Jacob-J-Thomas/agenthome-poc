using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewContinuationRecoveryCoordinatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Ambiguous_release_is_parked_without_completion_retirement_or_redispatch()
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], "cursor-next", true),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)));
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate));
        var release = new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Ambiguous));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, release, new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        var item = Assert.Single(result.Items);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, item.Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetireCount);
        Assert.Equal(1, consumer.Count);
        Assert.Equal(1, release.Count);
    }

    [Fact]
    public async Task Conclusive_release_completion_is_recorded_once_after_claim_and_reread()
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], "cursor-next", true),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)));
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate));
        var release = new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed, Completion()));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, release, new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Completed, Assert.Single(result.Items).Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.CompleteCount);
        Assert.Equal(0, store.RetireCount);
    }

    [Fact]
    public async Task Expired_wake_is_retained_without_synthetic_claim_or_consumer_invocation()
    {
        var candidate = Candidate(expiresAtUtc: _now);
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], "cursor-next", true),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)));
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate));
        var release = new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed, Completion()));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, release, new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.ExpiredWakeRetained, Assert.Single(result.Items).Status);
        Assert.Equal(0, store.ClaimCount);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, consumer.Count);
        Assert.Equal(0, release.Count);
        Assert.Equal(0, store.RetireCount);
    }

    [Fact]
    public async Task Exact_replayed_claim_continues_through_reread_and_completion_after_response_loss()
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], "cursor-next", true),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)))
        {
            ClaimResult = new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Replayed),
        };
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate));
        var release = new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed, Completion()));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, release, new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Completed, Assert.Single(result.Items).Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(new HumanReviewContinuationClaimReference(store.LastClaim!.Claim.ClaimId, store.LastClaim.Claim.ClaimHash), store.LastRead!.Claim);
        Assert.Equal(1, consumer.Count);
        Assert.Equal(1, release.Count);
        Assert.Equal(1, store.CompleteCount);
    }

    [Fact]
    public async Task Conclusive_consumer_retirement_is_recorded_through_the_canonical_claim_fence()
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)));
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(Retirement(candidate));
        var release = new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed, Completion()));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, release, new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Retired, Assert.Single(result.Items).Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(1, store.RetireCount);
        Assert.Equal(0, release.Count);
    }

    [Theory]
    [InlineData(HumanReviewContinuationRecoveryPageStatus.Invalid, HumanReviewContinuationRecoveryStatus.Invalid)]
    [InlineData(HumanReviewContinuationRecoveryPageStatus.Unavailable, HumanReviewContinuationRecoveryStatus.Unavailable)]
    public async Task Noncurrent_discovery_page_is_returned_without_claiming(HumanReviewContinuationRecoveryPageStatus pageStatus, HumanReviewContinuationRecoveryStatus expected)
    {
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(pageStatus, [], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(0, store.ClaimCount);
    }

    [Fact]
    public async Task Invalid_request_or_unavailable_clock_is_rejected_without_claiming()
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!));
        var invalidRequestCoordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));
        var unavailableClockCoordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now, new InvalidOperationException()));

        var invalidRequest = await invalidRequestCoordinator.RecoverAsync(Request() with { MaximumCount = 0 });
        var unavailableClock = await unavailableClockCoordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryStatus.Invalid, invalidRequest.Status);
        Assert.Equal(HumanReviewContinuationRecoveryStatus.Invalid, unavailableClock.Status);
        Assert.Equal(0, store.ClaimCount);
    }

    [Fact]
    public async Task Unavailable_discovery_is_parked_at_the_original_cursor()
    {
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!))
        {
            ListException = new IOException(),
        };
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request() with { ScanCursor = "retry-cursor" });

        Assert.Equal(HumanReviewContinuationRecoveryStatus.Unavailable, result.Status);
        Assert.Equal("retry-cursor", result.NextScanCursor);
        Assert.False(result.SourceTruncated);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Conflict, HumanReviewContinuationRecoveryItemStatus.ClaimConflict)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.NotFound, HumanReviewContinuationRecoveryItemStatus.ClaimConflict)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Invalid, HumanReviewContinuationRecoveryItemStatus.Invalid)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Unavailable, HumanReviewContinuationRecoveryItemStatus.Parked)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.LimitExceeded, HumanReviewContinuationRecoveryItemStatus.Parked)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Unknown, HumanReviewContinuationRecoveryItemStatus.Parked)]
    public async Task Noncommitted_claim_does_not_reread_or_consume(HumanReviewContinuationStoreMutationStatus mutationStatus, HumanReviewContinuationRecoveryItemStatus expected)
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!))
        {
            ClaimResult = new HumanReviewContinuationStoreMutationResult(mutationStatus),
        };
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(null!);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(expected, Assert.Single(result.Items).Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, consumer.Count);
    }

    [Theory]
    [InlineData(HumanReviewContinuationCandidateReadStatus.Corrupt, HumanReviewContinuationRecoveryItemStatus.Invalid)]
    [InlineData(HumanReviewContinuationCandidateReadStatus.Missing, HumanReviewContinuationRecoveryItemStatus.StaleAfterClaim)]
    [InlineData(HumanReviewContinuationCandidateReadStatus.Stale, HumanReviewContinuationRecoveryItemStatus.StaleAfterClaim)]
    [InlineData(HumanReviewContinuationCandidateReadStatus.Unavailable, HumanReviewContinuationRecoveryItemStatus.Parked)]
    public async Task Noncurrent_exact_reread_never_consumes(HumanReviewContinuationCandidateReadStatus rereadStatus, HumanReviewContinuationRecoveryItemStatus expected)
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(rereadStatus));
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(null!);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(expected, Assert.Single(result.Items).Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(1, store.ReadCount);
        Assert.Equal(0, consumer.Count);
    }

    [Fact]
    public async Task Missing_current_reread_candidate_is_invalid()
    {
        var candidate = Candidate();
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Invalid, Assert.Single(result.Items).Status);
    }

    [Theory]
    [InlineData(HumanReviewContinuationConsumptionStatus.Invalid, HumanReviewContinuationRecoveryItemStatus.Invalid)]
    [InlineData(HumanReviewContinuationConsumptionStatus.Unavailable, HumanReviewContinuationRecoveryItemStatus.Parked)]
    [InlineData(HumanReviewContinuationConsumptionStatus.StaleClaim, HumanReviewContinuationRecoveryItemStatus.Parked)]
    public async Task Nonprepared_consumer_result_does_not_release_or_terminalize(HumanReviewContinuationConsumptionStatus consumptionStatus, HumanReviewContinuationRecoveryItemStatus expected)
    {
        var candidate = Candidate();
        var store = StoreFor(candidate);
        var consumer = new HumanReviewContinuationRecoveryTestConsumer(new HumanReviewContinuationConsumptionResult(consumptionStatus));
        var release = new HumanReviewContinuationRecoveryTestReleasePort(null!);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, consumer, release, new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(expected, Assert.Single(result.Items).Status);
        Assert.Equal(0, release.Count);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetireCount);
    }

    [Fact]
    public async Task Conclusively_invalid_release_is_retired_through_claim_fence()
    {
        var candidate = Candidate();
        var store = StoreFor(candidate);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(
            store,
            new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate)),
            new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Invalid)),
            new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Retired, Assert.Single(result.Items).Status);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(1, store.RetireCount);
    }

    [Fact]
    public async Task Completed_release_without_conclusive_completion_is_parked_without_retirement()
    {
        var candidate = Candidate();
        var store = StoreFor(candidate);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(
            store,
            new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate)),
            new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed)),
            new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(result.Items).Status);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetireCount);
    }

    [Theory]
    [InlineData((int)HumanReviewContinuationReleaseStatus.Unknown)]
    [InlineData(99)]
    public async Task Unknown_or_future_release_status_is_parked_without_retirement(int rawReleaseStatus)
    {
        var candidate = Candidate();
        var store = StoreFor(candidate);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(
            store,
            new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate)),
            new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult((HumanReviewContinuationReleaseStatus)rawReleaseStatus)),
            new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(result.Items).Status);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(0, store.RetireCount);
    }

    [Theory]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Conflict, HumanReviewContinuationRecoveryItemStatus.Parked)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Invalid, HumanReviewContinuationRecoveryItemStatus.Invalid)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Unavailable, HumanReviewContinuationRecoveryItemStatus.Parked)]
    public async Task Terminal_mutation_failure_does_not_report_completion_or_retirement(HumanReviewContinuationStoreMutationStatus mutationStatus, HumanReviewContinuationRecoveryItemStatus expected)
    {
        var candidate = Candidate();
        var completionStore = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)))
        {
            CompleteResult = new HumanReviewContinuationStoreMutationResult(mutationStatus),
            RetireResult = new HumanReviewContinuationStoreMutationResult(mutationStatus),
        };
        var completionCoordinator = new HumanReviewContinuationRecoveryCoordinator(completionStore, new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate)), new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed, Completion())), new HumanReviewContinuationRecoveryTestClock(_now));

        var completion = await completionCoordinator.RecoverAsync(Request());

        Assert.Equal(expected, Assert.Single(completion.Items).Status);
        Assert.Equal(1, completionStore.CompleteCount);
    }

    [Fact]
    public async Task Unavailable_intermediate_operations_are_parked_without_claimed_work_release()
    {
        var candidate = Candidate();

        var claimFailureStore = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!))
        {
            ClaimException = new IOException(),
        };
        var claimFailure = await new HumanReviewContinuationRecoveryCoordinator(
            claimFailureStore,
            new HumanReviewContinuationRecoveryTestConsumer(null!),
            new HumanReviewContinuationRecoveryTestReleasePort(null!),
            new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        var readFailure = await new HumanReviewContinuationRecoveryCoordinator(
            new HumanReviewContinuationRecoveryTestStore(
                new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
                new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!))
            {
                ReadException = new IOException(),
            },
            new HumanReviewContinuationRecoveryTestConsumer(null!),
            new HumanReviewContinuationRecoveryTestReleasePort(null!),
            new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        var consumerFailure = await new HumanReviewContinuationRecoveryCoordinator(
            StoreFor(candidate),
            new HumanReviewContinuationRecoveryTestConsumer(null!, new IOException()),
            new HumanReviewContinuationRecoveryTestReleasePort(null!),
            new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        var releaseFailure = await new HumanReviewContinuationRecoveryCoordinator(
            StoreFor(candidate),
            new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate)),
            new HumanReviewContinuationRecoveryTestReleasePort(null!, new IOException()),
            new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        var completionFailureStore = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)))
        {
            CompleteException = new IOException(),
        };
        var completionFailure = await new HumanReviewContinuationRecoveryCoordinator(
            completionFailureStore,
            new HumanReviewContinuationRecoveryTestConsumer(Prepared(candidate)),
            new HumanReviewContinuationRecoveryTestReleasePort(new HumanReviewContinuationReleaseResult(HumanReviewContinuationReleaseStatus.Completed, Completion())),
            new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        var retirementFailureStore = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)))
        {
            RetireException = new IOException(),
        };
        var retirementFailure = await new HumanReviewContinuationRecoveryCoordinator(
            retirementFailureStore,
            new HumanReviewContinuationRecoveryTestConsumer(Retirement(candidate)),
            new HumanReviewContinuationRecoveryTestReleasePort(null!),
            new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(claimFailure.Items).Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(readFailure.Items).Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(consumerFailure.Items).Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(releaseFailure.Items).Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(completionFailure.Items).Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(retirementFailure.Items).Status);
    }

    [Fact]
    public async Task Null_port_results_are_closed_without_continuation_release()
    {
        var candidate = Candidate();
        var nullPageStore = new HumanReviewContinuationRecoveryTestStore(
            null!,
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!));
        var nullClaimStore = StoreFor(candidate);
        nullClaimStore = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!))
        {
            ClaimResult = null!,
        };
        var nullConsumerStore = StoreFor(candidate);

        var nullPage = await new HumanReviewContinuationRecoveryCoordinator(nullPageStore, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());
        var nullClaim = await new HumanReviewContinuationRecoveryCoordinator(nullClaimStore, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());
        var nullConsumer = await new HumanReviewContinuationRecoveryCoordinator(nullConsumerStore, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now)).RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryStatus.Unavailable, nullPage.Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(nullClaim.Items).Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Invalid, Assert.Single(nullConsumer.Items).Status);
    }

    [Fact]
    public async Task Claim_lease_is_never_extended_beyond_the_live_wake()
    {
        var candidate = Candidate(_now.AddMinutes(1));
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Unavailable))
        {
            ClaimResult = new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Replayed),
        };
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(result.Items).Status);
        Assert.NotNull(store.LastClaim);
        Assert.Equal(candidate.WakeExpiresAtUtc, store.LastClaim.Claim.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Malformed_discovery_candidate_is_never_claimed()
    {
        var valid = Candidate();
        var malformed = valid with { ExpectedGeneration = 0 };
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [malformed], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, null!));
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(store, new HumanReviewContinuationRecoveryTestConsumer(null!), new HumanReviewContinuationRecoveryTestReleasePort(null!), new HumanReviewContinuationRecoveryTestClock(_now));

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Invalid, Assert.Single(result.Items).Status);
        Assert.Equal(0, store.ClaimCount);
    }

    [Fact]
    public async Task Fresh_claim_time_retains_a_wake_that_expires_while_an_earlier_candidate_is_processed()
    {
        var first = Candidate(_now.AddMinutes(30));
        var expiredBeforeClaim = Candidate(_now.AddMinutes(5)) with { RunId = "run-two" };
        var store = new HumanReviewContinuationRecoveryTestStore(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [first, expiredBeforeClaim], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)));
        var clock = new HumanReviewContinuationRecoveryTestClock([_now, _now, _now.AddMinutes(5)]);
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(
            store,
            new HumanReviewContinuationRecoveryTestConsumer(new HumanReviewContinuationConsumptionResult(HumanReviewContinuationConsumptionStatus.Unavailable)),
            new HumanReviewContinuationRecoveryTestReleasePort(null!),
            clock);

        var result = await coordinator.RecoverAsync(Request() with { MaximumCount = 2 });

        Assert.Collection(
            result.Items,
            item => Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, item.Status),
            item => Assert.Equal(HumanReviewContinuationRecoveryItemStatus.ExpiredWakeRetained, item.Status));
        Assert.Equal(3, clock.ReadCount);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(_now, store.LastClaim!.Claim.ClaimedAtUtc);
    }

    [Fact]
    public async Task Unavailable_fresh_claim_time_parks_without_creating_a_claim()
    {
        var candidate = Candidate();
        var store = StoreFor(candidate);
        var clock = new HumanReviewContinuationRecoveryTestClock([_now], 2, new IOException());
        var coordinator = new HumanReviewContinuationRecoveryCoordinator(
            store,
            new HumanReviewContinuationRecoveryTestConsumer(null!),
            new HumanReviewContinuationRecoveryTestReleasePort(null!),
            clock);

        var result = await coordinator.RecoverAsync(Request());

        Assert.Equal(HumanReviewContinuationRecoveryStatus.Current, result.Status);
        Assert.Equal(HumanReviewContinuationRecoveryItemStatus.Parked, Assert.Single(result.Items).Status);
        Assert.Equal(2, clock.ReadCount);
        Assert.Equal(0, store.ClaimCount);
    }

    private static HumanReviewContinuationRecoveryTestStore StoreFor(HumanReviewContinuationRecoveryCandidate candidate)
        => new(
            new HumanReviewContinuationRecoveryPage(HumanReviewContinuationRecoveryPageStatus.Current, [candidate], null, false),
            new HumanReviewContinuationCandidateReadResult(HumanReviewContinuationCandidateReadStatus.Current, new HumanReviewContinuationCandidate(null!, null, null, null)));

    private static HumanReviewContinuationRecoveryRequest Request()
        => new(1, null, "recovery-worker", "recovery-coordinator", TimeSpan.FromMinutes(5));

    private static HumanReviewContinuationRecoveryCandidate Candidate(DateTimeOffset? expiresAtUtc = null)
        => new(
            "run-one",
            7,
            new HumanReviewRequestReference("request-one", Hash),
            new HumanReviewDecisionReference("decision-one", "decision-operation-one", HumanReviewDecisionKind.Approve, Hash),
            new HumanReviewContinuationWakeReference("wake-one", Hash),
            1,
            expiresAtUtc ?? _now.AddMinutes(30),
            new HumanReviewContinuationReservationReference("reservation-one", Hash),
            null);

    private static HumanReviewContinuationConsumptionResult Prepared(HumanReviewContinuationRecoveryCandidate candidate)
    {
        var action = new HumanReviewContinuationActionIntent(
            HumanReviewContinuationAction.ReleaseContinuation,
            candidate.RunId,
            candidate.ExpectedLifecycleVersion + 1,
            candidate.Request,
            candidate.Decision,
            candidate.Wake,
            new HumanReviewContinuationClaimReference("claim-one", Hash),
            candidate.Reservation,
            candidate.ExpectedGeneration,
            null,
            null!);
        var completion = new HumanReviewContinuationCompletionIntent(
            candidate.RunId,
            candidate.ExpectedLifecycleVersion + 1,
            candidate.Request,
            candidate.Wake,
            new HumanReviewContinuationClaimReference("claim-one", Hash),
            candidate.Reservation,
            candidate.ExpectedGeneration,
            null!);
        return new HumanReviewContinuationConsumptionResult(HumanReviewContinuationConsumptionStatus.ContinuationReleasePrepared, action, completion);
    }

    private static HumanReviewContinuationCompletion Completion()
        => new(1, "completion-one", null!, null!, null!, 1, null!, _now, [], null!, Hash);

    private static HumanReviewContinuationConsumptionResult Retirement(HumanReviewContinuationRecoveryCandidate candidate)
        => new(
            HumanReviewContinuationConsumptionStatus.RetirementRequired,
            Retirement: new HumanReviewContinuationRetirementIntent(
                candidate.RunId,
                candidate.ExpectedLifecycleVersion + 1,
                candidate.Wake,
                new HumanReviewContinuationClaimReference("claim-one", Hash),
                candidate.Reservation,
                candidate.ExpectedGeneration,
                HumanReviewContinuationOutcome.Blocked,
                HumanReviewContinuationRetirementReason.Blocked));
}
