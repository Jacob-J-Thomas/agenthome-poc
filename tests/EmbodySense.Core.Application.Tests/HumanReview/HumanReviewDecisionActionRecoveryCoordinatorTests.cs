using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewDecisionActionRecoveryCoordinatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Expired_unclaimed_wake_is_retired_without_a_synthetic_claim_consumer_or_release()
    {
        var candidate = Candidate(_now);
        var store = new HumanReviewDecisionActionRecoveryTestStore(new(HumanReviewDecisionActionRecoveryPageStatus.Current, [candidate], null, false), new(HumanReviewDecisionActionCandidateReadStatus.Current));
        var consumer = new HumanReviewDecisionActionRecoveryTestConsumer(new(HumanReviewContinuationConsumptionStatus.Invalid));
        var release = new HumanReviewDecisionActionRecoveryTestReleasePort(new(HumanReviewDecisionActionReleaseStatus.Unavailable));
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(store, consumer, release, new HumanReviewDecisionTestClock(_now));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-one", TimeSpan.FromMinutes(5)));

        Assert.Equal(HumanReviewDecisionActionRecoveryStatus.Current, result.Status);
        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.Retired, Assert.Single(result.Items).Status);
        Assert.Equal(0, store.ClaimCount);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(1, store.RetireCount);
        Assert.Equal(0, consumer.Count);
        Assert.Equal(0, release.Count);
        Assert.Equal(HumanReviewContinuationOutcome.Expired, store.LastRetirement?.Outcome);
        Assert.Null(store.LastRetirement?.Claim);
    }

    [Fact]
    public async Task Retained_wake_less_reservation_is_deterministically_published_before_the_refreshed_recovery_scan()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var decisionStore = new HumanReviewDecisionTestStore(fixture.Run);
        _ = await new HumanReviewDecisionService(decisionStore, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(fixture.Run.UpdatedAtUtc.AddMinutes(1))).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "action-recovery-publish", HumanReviewDecisionKind.Reject));
        var reservedRun = Assert.IsType<CustomLoopRunRecord>(decisionStore.Run);
        var reservedAction = Assert.Single(Assert.IsType<HumanReviewRunState>(reservedRun.HumanReview).DecisionActions);
        var publication = new HumanReviewDecisionActionPublicationCandidate(reservedRun.Id, reservedRun.LifecycleVersion, fixture.Request, reservedAction);
        var page = new HumanReviewDecisionActionRecoveryPage(HumanReviewDecisionActionRecoveryPageStatus.Current, [], null, false)
        {
            PublicationCandidates = [publication],
        };
        var store = new HumanReviewDecisionActionRecoveryTestStore(page, new(HumanReviewDecisionActionCandidateReadStatus.Current));
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(store, new HumanReviewDecisionActionRecoveryTestConsumer(new(HumanReviewContinuationConsumptionStatus.Invalid)), new HumanReviewDecisionActionRecoveryTestReleasePort(new(HumanReviewDecisionActionReleaseStatus.Unavailable)), new HumanReviewDecisionTestClock(fixture.Run.UpdatedAtUtc.AddMinutes(2)));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-one", TimeSpan.FromMinutes(5)));

        var published = Assert.Single(result.PublicationItems);
        Assert.Equal(HumanReviewDecisionActionPublicationRecoveryItemStatus.Published, published.Status);
        Assert.Equal(publication, published.Candidate);
        Assert.Equal(1, store.PublishCount);
        Assert.Equal(2, store.ListCount);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Exact_claim_reread_invokes_only_the_existing_nonapproval_decision_path_then_records_completion()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var decisionStore = new HumanReviewDecisionTestStore(fixture.Run);
        var accepted = await new HumanReviewDecisionService(decisionStore, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(fixture.Run.UpdatedAtUtc.AddMinutes(1))).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "action-recovery-reject", HumanReviewDecisionKind.Reject));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, accepted.Status);
        var reservedRun = Assert.IsType<CustomLoopRunRecord>(decisionStore.Run);
        var reservedAction = Assert.Single(Assert.IsType<HumanReviewRunState>(reservedRun.HumanReview).DecisionActions);
        var wake = Wake(reservedAction, fixture.Request);
        var claim = Claim(reservedAction, wake);
        var action = HumanReviewDecisionActionContractHash.ApplyState(reservedAction with { Wake = wake, Claims = [claim], StateHash = string.Empty });
        var currentRun = reservedRun with { LifecycleVersion = reservedRun.LifecycleVersion + 1 };
        var consumerCandidate = new HumanReviewContinuationCandidate(currentRun, null, null, null);
        var current = new HumanReviewDecisionActionCandidate(consumerCandidate, action, claim);
        var recoveryCandidate = Candidate(wake.ExpiresAtUtc, currentRun, action, null);
        var sourceAction = new HumanReviewContinuationActionIntent(HumanReviewContinuationAction.FailRejected, currentRun.Id, currentRun.LifecycleVersion, action.Reservation.Request, action.Reservation.Decision, null, null, null, null, null, null);
        var consumer = new HumanReviewDecisionActionRecoveryTestConsumer(new(HumanReviewContinuationConsumptionStatus.DecisionPathPrepared, sourceAction));
        var completion = Completion(action, claim);
        var release = new HumanReviewDecisionActionRecoveryTestReleasePort(new(HumanReviewDecisionActionReleaseStatus.Completed, completion));
        var store = new HumanReviewDecisionActionRecoveryTestStore(new(HumanReviewDecisionActionRecoveryPageStatus.Current, [recoveryCandidate], null, false), new(HumanReviewDecisionActionCandidateReadStatus.Current, current));
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(store, consumer, release, new HumanReviewDecisionTestClock(wake.PublishedAtUtc.AddMinutes(1)));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-two", TimeSpan.FromMinutes(5)));

        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.Completed, Assert.Single(result.Items).Status);
        Assert.Equal(1, store.ClaimCount);
        Assert.Equal(2, store.ReadCount);
        Assert.Equal(1, consumer.Count);
        Assert.Equal(currentRun, consumer.LastCandidate?.Run);
        Assert.Equal(action.Reservation.Decision, consumer.LastDecision);
        Assert.Equal(1, release.Count);
        Assert.Equal(1, store.CompleteCount);
        Assert.Equal(0, store.RetireCount);
        Assert.Equal(action.ExpectedGeneration, store.LastClaim?.Claim.ExpectedGeneration);
        Assert.Equal(new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash), store.LastClaim?.Claim.Reservation);
        Assert.Equal(currentRun.LifecycleVersion, store.LastCompletion?.ExpectedLifecycleVersion);
        Assert.Equal(action.ExpectedGeneration, store.LastCompletion?.ExpectedGeneration);
        var replay = await coordinator.RecoverAsync(new(1, null, "action-worker-two", TimeSpan.FromMinutes(5)));
        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.Completed, Assert.Single(replay.Items).Status);
        Assert.Equal(2, release.Count);
        Assert.Equal(1, release.IdempotentOperationCount);
        Assert.Single(release.ActionOperationIds);
    }

    [Fact]
    public async Task Claim_lease_is_clipped_to_the_exact_wake_expiry()
    {
        var candidate = Candidate(_now.AddMinutes(3));
        var store = new HumanReviewDecisionActionRecoveryTestStore(new(HumanReviewDecisionActionRecoveryPageStatus.Current, [candidate], null, false), new(HumanReviewDecisionActionCandidateReadStatus.Stale))
        {
            ClaimResult = new(HumanReviewDecisionActionStoreMutationStatus.Conflict)
        };
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(store, new HumanReviewDecisionActionRecoveryTestConsumer(new(HumanReviewContinuationConsumptionStatus.Invalid)), new HumanReviewDecisionActionRecoveryTestReleasePort(new(HumanReviewDecisionActionReleaseStatus.Unavailable)), new HumanReviewDecisionTestClock(_now, _now));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-one", TimeSpan.FromMinutes(5)));

        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.ClaimConflict, Assert.Single(result.Items).Status);
        Assert.Equal(candidate.WakeExpiresAtUtc, store.LastClaim?.Claim.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Claim_limit_result_requires_fail_closed_blocked_retirement_with_the_exact_prior_claim()
    {
        var prior = new HumanReviewDecisionActionClaimReference("action-prior-claim", Hash('e'));
        var candidate = Candidate(_now.AddMinutes(10)) with { PriorClaim = prior };
        var store = new HumanReviewDecisionActionRecoveryTestStore(new(HumanReviewDecisionActionRecoveryPageStatus.Current, [candidate], null, false), new(HumanReviewDecisionActionCandidateReadStatus.Stale))
        {
            ClaimResult = new(HumanReviewDecisionActionStoreMutationStatus.LimitExceeded)
        };
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(store, new HumanReviewDecisionActionRecoveryTestConsumer(new(HumanReviewContinuationConsumptionStatus.Invalid)), new HumanReviewDecisionActionRecoveryTestReleasePort(new(HumanReviewDecisionActionReleaseStatus.Unavailable)), new HumanReviewDecisionTestClock(_now, _now, _now));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-one", TimeSpan.FromMinutes(5)));

        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.Retired, Assert.Single(result.Items).Status);
        Assert.Equal(HumanReviewContinuationOutcome.Blocked, store.LastRetirement?.Outcome);
        Assert.Equal(HumanReviewDecisionActionRetirementReason.ClaimLimitExceeded, store.LastRetirement?.Reason);
        Assert.Equal(prior, store.LastRetirement?.Claim);
    }

    [Fact]
    public async Task Trusted_clock_fence_prevents_release_after_the_exact_claim_expires()
    {
        var fixture = await CreateClaimedRecoveryFixtureAsync();
        var store = fixture.Store;
        var release = fixture.Release;
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(store, fixture.Consumer, release, new HumanReviewDecisionTestClock(fixture.Wake.PublishedAtUtc.AddMinutes(1), fixture.Wake.PublishedAtUtc.AddMinutes(1), fixture.Claim.LeaseExpiresAtUtc));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-two", TimeSpan.FromMinutes(5)));

        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim, Assert.Single(result.Items).Status);
        Assert.Equal(0, release.Count);
        Assert.Equal(0, store.CompleteCount);
    }

    [Fact]
    public async Task Decision_action_head_advanced_during_consumption_blocks_release_after_the_second_canonical_reread()
    {
        var fixture = await CreateClaimedRecoveryFixtureAsync();
        fixture.Consumer.AfterConsume = fixture.Store.AdvanceActionHead;
        var coordinator = new HumanReviewDecisionActionRecoveryCoordinator(fixture.Store, fixture.Consumer, fixture.Release, new HumanReviewDecisionTestClock(fixture.Wake.PublishedAtUtc.AddMinutes(1)));

        var result = await coordinator.RecoverAsync(new(1, null, "action-worker-two", TimeSpan.FromMinutes(5)));

        Assert.Equal(HumanReviewDecisionActionRecoveryItemStatus.StaleAfterClaim, Assert.Single(result.Items).Status);
        Assert.Equal(2, fixture.Store.ReadCount);
        Assert.Equal(fixture.Store.ReadQueries[0], fixture.Store.ReadQueries[1]);
        Assert.Equal(1, fixture.Consumer.Count);
        Assert.Equal(0, fixture.Release.Count);
        Assert.Equal(0, fixture.Store.CompleteCount);
        Assert.Equal(0, fixture.Store.RetireCount);
    }

    private static HumanReviewDecisionActionRecoveryCandidate Candidate(DateTimeOffset expiresAtUtc)
        => new("action-run-one", 1, new("action-request-one", Hash('a')), new("action-decision-one", "action-operation-one", HumanReviewDecisionKind.Reject, Hash('b')), new("action-wake-one", Hash('c')), 1, expiresAtUtc, new("action-reservation-one", Hash('d')), null);

    private static HumanReviewDecisionActionRecoveryCandidate Candidate(DateTimeOffset expiresAtUtc, CustomLoopRunRecord run, HumanReviewDecisionActionState action, HumanReviewDecisionActionClaimReference? priorClaim)
        => new(run.Id, run.LifecycleVersion - 1, action.Reservation.Request, action.Reservation.Decision, new(action.Wake!.WakeId, action.Wake.WakeHash), action.ExpectedGeneration, expiresAtUtc, new(action.Reservation.ReservationId, action.Reservation.ReservationHash), priorClaim);

    private static async Task<ClaimedRecoveryFixture> CreateClaimedRecoveryFixtureAsync()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var decisionStore = new HumanReviewDecisionTestStore(fixture.Run);
        _ = await new HumanReviewDecisionService(decisionStore, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(fixture.Run.UpdatedAtUtc.AddMinutes(1))).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "action-fence-reject", HumanReviewDecisionKind.Reject));
        var reservedRun = Assert.IsType<CustomLoopRunRecord>(decisionStore.Run);
        var reservedAction = Assert.Single(Assert.IsType<HumanReviewRunState>(reservedRun.HumanReview).DecisionActions);
        var wake = Wake(reservedAction, fixture.Request);
        var claim = Claim(reservedAction, wake);
        var action = HumanReviewDecisionActionContractHash.ApplyState(reservedAction with { Wake = wake, Claims = [claim], StateHash = string.Empty });
        var currentRun = reservedRun with { LifecycleVersion = reservedRun.LifecycleVersion + 1 };
        var current = new HumanReviewDecisionActionCandidate(new HumanReviewContinuationCandidate(currentRun, null, null, null), action, claim);
        var candidate = Candidate(wake.ExpiresAtUtc, currentRun, action, null);
        var sourceAction = new HumanReviewContinuationActionIntent(HumanReviewContinuationAction.FailRejected, currentRun.Id, currentRun.LifecycleVersion, action.Reservation.Request, action.Reservation.Decision, null, null, null, null, null, null);
        var consumer = new HumanReviewDecisionActionRecoveryTestConsumer(new(HumanReviewContinuationConsumptionStatus.DecisionPathPrepared, sourceAction));
        var completion = Completion(action, claim);
        var release = new HumanReviewDecisionActionRecoveryTestReleasePort(new(HumanReviewDecisionActionReleaseStatus.Completed, completion));
        var store = new HumanReviewDecisionActionRecoveryTestStore(new(HumanReviewDecisionActionRecoveryPageStatus.Current, [candidate], null, false), new(HumanReviewDecisionActionCandidateReadStatus.Current, current));
        return new ClaimedRecoveryFixture(store, consumer, release, wake, claim);
    }

    private static HumanReviewDecisionActionWake Wake(HumanReviewDecisionActionState action, HumanReviewRequest request)
        => HumanReviewDecisionActionContractHash.ApplyWake(new(1, "action-recovery-wake", action.Reservation.Request, action.Reservation.Decision, new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.BindingHash, action.ExpectedGeneration, action.Reservation.ReservedAtUtc, request.Timing.ExpiresAtUtc, Provenance("action-recovery-wake", action.Reservation.ReservedAtUtc), string.Empty));

    private static HumanReviewDecisionActionClaim Claim(HumanReviewDecisionActionState action, HumanReviewDecisionActionWake wake)
    {
        var claimedAtUtc = wake.PublishedAtUtc.AddMinutes(1);
        return HumanReviewDecisionActionContractHash.ApplyClaim(new(1, "action-recovery-claim", new(wake.WakeId, wake.WakeHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, "action-worker-two", claimedAtUtc, claimedAtUtc.AddMinutes(5), Provenance("action-recovery-claim", claimedAtUtc), string.Empty));
    }

    private static HumanReviewDecisionActionCompletion Completion(HumanReviewDecisionActionState action, HumanReviewDecisionActionClaim claim)
    {
        var completedAtUtc = claim.ClaimedAtUtc.AddMinutes(1);
        return HumanReviewDecisionActionContractHash.ApplyCompletion(new(1, "action-recovery-completion", new(action.Wake!.WakeId, action.Wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(action.Reservation.ReservationId, action.Reservation.ReservationHash), action.ExpectedGeneration, HumanReviewDecisionActionDisposition.Rejected, Hash('e'), Hash('f'), completedAtUtc, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("action-recovery-completion", completedAtUtc), string.Empty));
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc) => HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Coordinator, "action-recovery", correlationId, observedAtUtc, string.Empty));
    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    private sealed record ClaimedRecoveryFixture(HumanReviewDecisionActionRecoveryTestStore Store, HumanReviewDecisionActionRecoveryTestConsumer Consumer, HumanReviewDecisionActionRecoveryTestReleasePort Release, HumanReviewDecisionActionWake Wake, HumanReviewDecisionActionClaim Claim);
}
