using System.Collections.Immutable;
using System.Text.Json;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewContinuationContractTests
{
    [Fact]
    public void Canonical_wake_claim_completion_and_state_bind_one_exact_approval_reservation_and_generation()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var completion = Completion(wake, reservation, claim);
        var state = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim], completion, null, string.Empty));

        Assert.True(HumanReviewContinuationContractValidator.ValidateWake(request, reservation, wake).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateClaim(wake, reservation, claim).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateCompletion(wake, reservation, claim, completion).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, state).IsValid);
        Assert.True(HumanReviewContinuationContractHash.MatchesState(state));
    }

    [Fact]
    public void Validators_fail_closed_for_unknown_values_malformed_identifiers_rebound_hashes_and_exact_bound_plus_one_values()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var retirement = Retirement(wake, reservation);

        Assert.False(HumanReviewContinuationContractValidator.ValidateWake(request, reservation, HumanReviewContinuationContractHash.ApplyWake(wake with { ExpectedGeneration = HumanReviewContractLimits.MaxVersion + 1, WakeHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateWake(request, reservation, HumanReviewContinuationContractHash.ApplyWake(wake with { BindingHash = HumanReviewTestData.Hash('f'), WakeHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateClaim(wake, reservation, HumanReviewContinuationContractHash.ApplyClaim(claim with { ClaimId = "Invalid", ClaimHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateClaim(wake, reservation, HumanReviewContinuationContractHash.ApplyClaim(claim with { LeaseExpiresAtUtc = claim.ClaimedAtUtc.Add(HumanReviewContractLimits.MaxContinuationClaimLease).AddTicks(1), ClaimHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateRetirement(wake, reservation, HumanReviewContinuationContractHash.ApplyRetirement(retirement with { Outcome = (HumanReviewContinuationOutcome)99, RetirementHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateRetirement(wake, reservation, HumanReviewContinuationContractHash.ApplyRetirement(retirement with { Outcome = HumanReviewContinuationOutcome.Completed, RetirementHash = string.Empty })).IsValid);
    }

    [Fact]
    public void State_machine_allows_only_expired_lease_takeover_and_exact_active_claim_completion()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var first = Claim(wake, reservation);
        var early = HumanReviewContinuationContractHash.ApplyClaim(first with { ClaimId = "claim-two", ClaimedAtUtc = first.LeaseExpiresAtUtc.AddTicks(-1), LeaseExpiresAtUtc = first.LeaseExpiresAtUtc.AddMinutes(1), Provenance = Provenance("claim-two", first.LeaseExpiresAtUtc.AddTicks(-1)), ClaimHash = string.Empty });
        var takeover = HumanReviewContinuationContractHash.ApplyClaim(first with { ClaimId = "claim-two", ClaimedAtUtc = first.LeaseExpiresAtUtc, LeaseExpiresAtUtc = first.LeaseExpiresAtUtc.AddMinutes(1), Provenance = Provenance("claim-two", first.LeaseExpiresAtUtc), ClaimHash = string.Empty });
        var invalid = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [first, early], null, null, string.Empty));
        var valid = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [first, takeover], Completion(wake, reservation, takeover), null, string.Empty));

        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(request, reservation, invalid).Errors, error => error.Code == "claim_takeover_before_expiry");
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, valid).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateCompletion(wake, reservation, first, Completion(wake, reservation, takeover)).IsValid);
    }

    [Fact]
    public void Terminal_retirement_is_exclusive_append_only_and_expiry_cannot_predate_wake_expiry()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var retirement = Retirement(wake, reservation);
        var completed = Completion(wake, reservation, claim);
        var prior = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim], null, retirement, string.Empty));
        var rewritten = HumanReviewContinuationContractHash.ApplyState(prior with { Retirement = null, Completion = completed, StateHash = string.Empty });
        var expiryEarly = HumanReviewContinuationContractHash.ApplyRetirement(retirement with { Outcome = HumanReviewContinuationOutcome.Expired, RetiredAtUtc = wake.ExpiresAtUtc.AddTicks(-1), Provenance = Provenance("retire-early", wake.ExpiresAtUtc.AddTicks(-1)), RetirementHash = string.Empty });

        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, prior).IsValid);
        Assert.Contains(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, prior, rewritten).Errors, error => error.Code == "retirement_rewritten");
        Assert.False(HumanReviewContinuationContractValidator.ValidateRetirement(wake, reservation, expiryEarly).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateState(request, reservation, HumanReviewContinuationContractHash.ApplyState(prior with { Completion = completed, StateHash = string.Empty })).IsValid);
    }

    [Fact]
    public void Hashes_are_order_sensitive_and_replays_are_distinguished_from_divergent_identity_reuse()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var altered = HumanReviewContinuationContractHash.ApplyWake(wake with { ExpiresAtUtc = wake.ExpiresAtUtc.AddMinutes(1), Provenance = Provenance("wake-altered", wake.PublishedAtUtc), WakeHash = string.Empty });
        var claim = Claim(wake, reservation);
        var second = HumanReviewContinuationContractHash.ApplyClaim(claim with { ClaimId = "claim-two", ClaimedAtUtc = claim.LeaseExpiresAtUtc, LeaseExpiresAtUtc = claim.LeaseExpiresAtUtc.AddMinutes(1), Provenance = Provenance("claim-two", claim.LeaseExpiresAtUtc), ClaimHash = string.Empty });
        var firstState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim, second], null, null, string.Empty));
        var reversed = HumanReviewContinuationContractHash.ApplyState(firstState with { Claims = [second, claim], StateHash = string.Empty });

        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyWake(wake, wake with { }));
        Assert.Equal(HumanReviewContinuationReplayDisposition.DivergentReuse, HumanReviewContinuationReplayClassifier.ClassifyWake(wake, altered));
        Assert.NotEqual(firstState.StateHash, reversed.StateHash);
        Assert.False(HumanReviewContinuationContractValidator.ValidateState(request, reservation, reversed).IsValid);
    }

    [Fact]
    public void Snapshots_are_defensive_and_malformed_or_default_collections_fail_without_throwing()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var completion = Completion(wake, reservation, claim);
        var state = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim], completion, null, string.Empty));
        var retired = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, Retirement(wake, reservation), string.Empty));
        var malformed = state with { Claims = default };
        var roundTrip = JsonSerializer.Deserialize<HumanReviewContinuationState>(JsonSerializer.Serialize(state));

        Assert.True(HumanReviewContinuationContractSnapshot.TryCaptureState(request, reservation, state, out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotSame(state.Wake, snapshot!.Wake);
        Assert.NotSame(state.Claims[0], snapshot.Claims[0]);
        Assert.NotSame(state.Completion, snapshot.Completion);
        Assert.True(HumanReviewContinuationContractSnapshot.TryCaptureState(request, reservation, retired, out var retirementSnapshot, out var retirementValidation));
        Assert.True(retirementValidation.IsValid);
        Assert.NotSame(retired.Retirement, retirementSnapshot!.Retirement);
        Assert.NotNull(roundTrip);
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, roundTrip).IsValid);
        Assert.False(HumanReviewContinuationContractSnapshot.TryCaptureState(request, reservation, malformed, out var rejected, out var rejectedValidation));
        Assert.Null(rejected);
        Assert.False(rejectedValidation.IsValid);
    }

    private static HumanReviewContinuationReservation Reservation(HumanReviewRequest request)
    {
        var decision = HumanReviewTestData.Decision(request);
        var time = HumanReviewTestData.CreatedAtUtc.AddMinutes(2);
        return HumanReviewContractHash.ApplyContinuationReservation(new HumanReviewContinuationReservation(1, "reservation-one", new HumanReviewRequestReference(request.RequestId, request.RequestHash), new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash), time, Provenance("reservation", time), string.Empty));
    }

    private static HumanReviewContinuationWake Wake(HumanReviewRequest request, HumanReviewContinuationReservation reservation)
    {
        var time = HumanReviewTestData.CreatedAtUtc.AddMinutes(3);
        return HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(1, "wake-one", new HumanReviewRequestReference(request.RequestId, request.RequestHash), reservation.Decision, new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), request.Binding.BindingHash, 1, time, time.AddHours(1), Provenance("wake", time), string.Empty));
    }

    private static HumanReviewContinuationClaim Claim(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation)
    {
        var time = wake.PublishedAtUtc.AddMinutes(1);
        return HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(1, "claim-one", new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, "worker-one", time, time.AddMinutes(10), Provenance("claim", time), string.Empty));
    }

    private static HumanReviewContinuationCompletion Completion(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim)
    {
        var time = claim.ClaimedAtUtc.AddMinutes(1);
        return HumanReviewContinuationContractHash.ApplyCompletion(new HumanReviewContinuationCompletion(1, "completion-one", new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash), new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, time, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("completion", time), string.Empty));
    }

    private static HumanReviewContinuationRetirement Retirement(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation)
    {
        var time = wake.PublishedAtUtc.AddMinutes(1);
        return HumanReviewContinuationContractHash.ApplyRetirement(new HumanReviewContinuationRetirement(1, "retirement-one", new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, HumanReviewContinuationOutcome.Blocked, time, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("retirement", time), string.Empty));
    }

    private static HumanReviewProvenance Provenance(string correlation, DateTimeOffset time) => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "coordinator-one", correlation, time, string.Empty));
}
