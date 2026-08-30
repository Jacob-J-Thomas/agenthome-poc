using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewDecisionActionContractTests
{
    [Theory]
    [InlineData(HumanReviewDecisionKind.Reject, HumanReviewDecisionActionDisposition.Rejected)]
    [InlineData(HumanReviewDecisionKind.Cancel, HumanReviewDecisionActionDisposition.Cancelled)]
    [InlineData(HumanReviewDecisionKind.RequestInformation, HumanReviewDecisionActionDisposition.InformationParked)]
    public void Nonapproval_action_state_binds_exact_decision_generation_claim_and_disposition(HumanReviewDecisionKind kind, HumanReviewDecisionActionDisposition disposition)
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request, kind);
        var initial = HumanReviewDecisionActionContractHash.ApplyState(new(1, reservation, request.Binding.BindingHash, 1, 2, null, ImmutableArray<HumanReviewDecisionActionClaim>.Empty, null, null, string.Empty));
        var wake = HumanReviewDecisionActionContractHash.ApplyWake(new(1, "action-wake-one", new(request.RequestId, request.RequestHash), reservation.Decision, new(reservation.ReservationId, reservation.ReservationHash), request.Binding.BindingHash, 1, reservation.ReservedAtUtc, request.Timing.ExpiresAtUtc, Provenance("action-wake-one", reservation.ReservedAtUtc), string.Empty));
        var published = HumanReviewDecisionActionContractHash.ApplyState(initial with { Wake = wake, StateHash = string.Empty });
        var claimAtUtc = wake.PublishedAtUtc.AddMinutes(1);
        var claim = HumanReviewDecisionActionContractHash.ApplyClaim(new(1, "action-claim-one", new(wake.WakeId, wake.WakeHash), new(reservation.ReservationId, reservation.ReservationHash), 1, "action-worker-one", claimAtUtc, claimAtUtc.AddMinutes(5), Provenance("action-claim-one", claimAtUtc), string.Empty));
        var claimed = HumanReviewDecisionActionContractHash.ApplyState(published with { Claims = [claim], StateHash = string.Empty });
        var completion = HumanReviewDecisionActionContractHash.ApplyCompletion(new(1, "action-completion-one", new(wake.WakeId, wake.WakeHash), new(claim.ClaimId, claim.ClaimHash), new(reservation.ReservationId, reservation.ReservationHash), 1, disposition, HumanReviewTestData.Hash('b'), HumanReviewTestData.Hash('c'), claimAtUtc.AddMinutes(1), ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("action-completion-one", claimAtUtc.AddMinutes(1)), string.Empty));
        var completed = HumanReviewDecisionActionContractHash.ApplyState(claimed with { Completion = completion, StateHash = string.Empty });

        Assert.True(HumanReviewDecisionActionContractValidator.ValidateState(request, initial).IsValid);
        Assert.True(HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, null, initial).IsValid);
        Assert.True(HumanReviewDecisionActionContractValidator.ValidateState(request, published).IsValid);
        Assert.True(HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, initial, published).IsValid);
        Assert.True(HumanReviewDecisionActionContractValidator.ValidateState(request, claimed).IsValid);
        Assert.True(HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, published, claimed).IsValid);
        Assert.True(HumanReviewDecisionActionContractValidator.ValidateState(request, completed).IsValid);
        Assert.True(HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, claimed, completed).IsValid);
        Assert.True(HumanReviewDecisionActionContractHash.MatchesState(completed));
    }

    [Fact]
    public void Action_state_fails_closed_for_wrong_disposition_generation_and_nonexpired_takeover()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request, HumanReviewDecisionKind.Reject);
        var initial = HumanReviewDecisionActionContractHash.ApplyState(new(1, reservation, request.Binding.BindingHash, 1, 2, null, ImmutableArray<HumanReviewDecisionActionClaim>.Empty, null, null, string.Empty));
        var wake = HumanReviewDecisionActionContractHash.ApplyWake(new(1, "action-wake-two", new(request.RequestId, request.RequestHash), reservation.Decision, new(reservation.ReservationId, reservation.ReservationHash), request.Binding.BindingHash, 1, reservation.ReservedAtUtc, request.Timing.ExpiresAtUtc, Provenance("action-wake-two", reservation.ReservedAtUtc), string.Empty));
        var published = HumanReviewDecisionActionContractHash.ApplyState(initial with { Wake = wake, StateHash = string.Empty });
        var firstAtUtc = wake.PublishedAtUtc.AddMinutes(1);
        var first = HumanReviewDecisionActionContractHash.ApplyClaim(new(1, "action-claim-two", new(wake.WakeId, wake.WakeHash), new(reservation.ReservationId, reservation.ReservationHash), 1, "action-worker-two", firstAtUtc, firstAtUtc.AddMinutes(5), Provenance("action-claim-two", firstAtUtc), string.Empty));
        var early = HumanReviewDecisionActionContractHash.ApplyClaim(first with { ClaimId = "action-claim-three", ClaimedAtUtc = first.LeaseExpiresAtUtc, LeaseExpiresAtUtc = first.LeaseExpiresAtUtc.AddMinutes(1), Provenance = Provenance("action-claim-three", first.LeaseExpiresAtUtc), ClaimHash = string.Empty });
        var invalidTakeover = HumanReviewDecisionActionContractHash.ApplyState(published with { Claims = [first, early], StateHash = string.Empty });
        var wrongGeneration = HumanReviewDecisionActionContractHash.ApplyState(initial with { ExpectedGeneration = 2, StateHash = string.Empty });

        Assert.False(HumanReviewDecisionActionContractValidator.ValidateState(request, invalidTakeover).IsValid);
        Assert.False(HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, initial, wrongGeneration).IsValid);
    }

    private static HumanReviewDecisionActionReservation Reservation(HumanReviewRequest request, HumanReviewDecisionKind kind)
    {
        var reservedAtUtc = request.Timing.CreatedAtUtc.AddMinutes(1);
        return HumanReviewDecisionActionContractHash.ApplyReservation(new(1, "action-reservation-one", new(request.RequestId, request.RequestHash), new("action-decision-one", "action-operation-one", kind, HumanReviewTestData.Hash('a')), reservedAtUtc, HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Server, "action-server", "action-reservation-one", reservedAtUtc, string.Empty)), string.Empty));
    }

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc) => HumanReviewContractHash.ApplyProvenance(new(HumanReviewProvenanceKind.Coordinator, "action-coordinator", correlationId, observedAtUtc, string.Empty));
}
