using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewContinuationPublicationServiceTests
{
    [Fact]
    public async Task Publisher_uses_the_immutable_reservation_timestamp_for_each_exact_wake_without_a_clock()
    {
        var approved = await ApprovedRunAsync();
        var publisherStore = new HumanReviewContinuationPublicationTestStore();
        var publisher = new HumanReviewContinuationPublicationService(new HumanReviewDecisionTestStore(approved), publisherStore);

        var result = await publisher.PublishAsync(approved.Id);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Committed, result.Status);
        var publication = Assert.Single(publisherStore.Publications);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(approved.HumanReview?.ContinuationReservation);
        Assert.Equal(reservation.ReservedAtUtc, publication.Continuation.Wake.PublishedAtUtc);
        Assert.Equal(reservation.ReservedAtUtc, publication.Continuation.Wake.Provenance.ObservedAtUtc);
        Assert.Equal(reservation.ReservationHash, publication.Continuation.Wake.Reservation.ReservationHash);
    }

    [Theory]
    [InlineData(HumanReviewContinuationStoreMutationStatus.NotFound)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.Invalid)]
    [InlineData(HumanReviewContinuationStoreMutationStatus.LimitExceeded)]
    public async Task Publisher_preserves_closed_canonical_store_postures(HumanReviewContinuationStoreMutationStatus storedStatus)
    {
        var approved = await ApprovedRunAsync();
        var publisherStore = new HumanReviewContinuationPublicationTestStore { Result = new HumanReviewContinuationStoreMutationResult(storedStatus) };
        var publisher = new HumanReviewContinuationPublicationService(new HumanReviewDecisionTestStore(approved), publisherStore);

        var result = await publisher.PublishAsync(approved.Id);

        Assert.Equal(storedStatus, result.Status);
        Assert.Single(publisherStore.Publications);
    }

    [Fact]
    public async Task Publisher_rejects_an_expired_reservation_before_calling_the_store()
    {
        var expired = ExpiredReservation(await ApprovedRunAsync());
        var publisherStore = new HumanReviewContinuationPublicationTestStore();
        var publisher = new HumanReviewContinuationPublicationService(new HumanReviewDecisionTestStore(expired), publisherStore);

        var result = await publisher.PublishAsync(expired.Id);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, result.Status);
        Assert.Empty(publisherStore.Publications);
    }

    [Fact]
    public async Task Publisher_maps_invalid_not_found_and_unavailable_run_reads_without_mutating()
    {
        var approved = await ApprovedRunAsync();
        var invalidStore = new HumanReviewDecisionTestStore(approved);
        var missingStore = new HumanReviewDecisionTestStore(approved) { GetOverrideAsync = (_, _) => Task.FromResult<CustomLoopRunRecord?>(null) };
        var unavailableStore = new HumanReviewDecisionTestStore(approved) { GetOverrideAsync = (_, _) => throw new IOException("Run source unavailable.") };

        var invalid = await new HumanReviewContinuationPublicationService(invalidStore, new HumanReviewContinuationPublicationTestStore()).PublishAsync("../invalid");
        var missing = await new HumanReviewContinuationPublicationService(missingStore, new HumanReviewContinuationPublicationTestStore()).PublishAsync(approved.Id);
        var unavailable = await new HumanReviewContinuationPublicationService(unavailableStore, new HumanReviewContinuationPublicationTestStore()).PublishAsync(approved.Id);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Invalid, invalid.Status);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.NotFound, missing.Status);
        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Unavailable, unavailable.Status);
        Assert.Equal(0, invalidStore.ReadCount);
    }

    [Fact]
    public async Task Divergent_publisher_results_remain_a_conflict_after_the_bounded_reconciliation_reads()
    {
        var approved = await ApprovedRunAsync();
        var publisherStore = new HumanReviewContinuationPublicationTestStore { Result = new HumanReviewContinuationStoreMutationResult(HumanReviewContinuationStoreMutationStatus.Conflict) };
        var publisher = new HumanReviewContinuationPublicationService(new HumanReviewDecisionTestStore(approved), publisherStore);

        var result = await publisher.PublishAsync(approved.Id);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Conflict, result.Status);
        Assert.Equal(3, publisherStore.Publications.Count);
    }

    [Fact]
    public async Task Lost_publisher_responses_remain_unavailable_when_the_canonical_run_did_not_change()
    {
        var approved = await ApprovedRunAsync();
        var publisherStore = new HumanReviewContinuationPublicationTestStore { Result = null };
        var publisher = new HumanReviewContinuationPublicationService(new HumanReviewDecisionTestStore(approved), publisherStore);

        var result = await publisher.PublishAsync(approved.Id);

        Assert.Equal(HumanReviewContinuationStoreMutationStatus.Unavailable, result.Status);
        Assert.Equal(3, publisherStore.Publications.Count);
    }

    private static async Task<CustomLoopRunRecord> ApprovedRunAsync()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var decision = new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionTestAuthorizer(),
            new HumanReviewDecisionTestClock(fixture.Run.UpdatedAtUtc.AddMinutes(1)));
        var result = await decision.DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "approve-publication-one", HumanReviewDecisionKind.Approve));

        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, result.Status);
        return Assert.IsType<CustomLoopRunRecord>(store.Run);
    }

    private static CustomLoopRunRecord ExpiredReservation(CustomLoopRunRecord approved)
    {
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var existing = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var reservedAtUtc = review.Request.Timing.ExpiresAtUtc.AddSeconds(1);
        var reservation = HumanReviewContractHash.ApplyContinuationReservation(existing with
        {
            ReservedAtUtc = reservedAtUtc,
            Provenance = existing.Provenance with { ObservedAtUtc = reservedAtUtc, ProvenanceHash = string.Empty },
            ReservationHash = string.Empty,
        });
        var reference = new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash);
        var reservationEvidence = HumanReviewContractHash.ApplyEvidence(review.Evidence[^1] with
        {
            RecordedAtUtc = reservedAtUtc,
            Provenance = review.Evidence[^1].Provenance with { ObservedAtUtc = reservedAtUtc, ProvenanceHash = string.Empty },
            ContinuationReservation = reference,
            EvidenceHash = string.Empty,
        });
        var reservationEvent = approved.Events[^1] with
        {
            TimestampUtc = reservedAtUtc,
            HumanReviewEvidence = reservationEvidence,
            HumanReviewContinuationReservation = reference,
        };
        var expired = approved with
        {
            UpdatedAtUtc = reservedAtUtc,
            Events = [.. approved.Events[..^1], reservationEvent],
            HumanReview = review with
            {
                ContinuationReservation = reservation,
                Evidence = review.Evidence.SetItem(review.Evidence.Length - 1, reservationEvidence),
            },
        };

        Assert.True(CustomLoopRunValidator.Validate(expired).IsValid);
        return expired;
    }
}
