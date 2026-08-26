using System.Collections.Immutable;
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
        Assert.True(HumanReviewContinuationContractValidator.ValidateCompletion(request, wake, reservation, claim, completion).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, state).IsValid);
        Assert.True(HumanReviewContinuationContractHash.MatchesState(state));
    }

    [Fact]
    public void Completion_release_receipt_kind_is_bound_to_the_exact_reviewed_purpose()
    {
        var continuationRequest = HumanReviewTestData.Request();
        var continuationReservation = Reservation(continuationRequest);
        var continuationWake = Wake(continuationRequest, continuationReservation);
        var continuationClaim = Claim(continuationWake, continuationReservation);
        var continuationCompletion = Completion(continuationRequest, continuationWake, continuationReservation, continuationClaim);
        var continuationState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, continuationWake, [continuationClaim], continuationCompletion, null, string.Empty));
        var continuationMismatchedReceipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(continuationCompletion.ReleaseReceipt with { Kind = HumanReviewContinuationReleaseKind.PreDispatchEffect, EffectReceiptHash = HumanReviewTestData.Hash('c'), ReleaseReceiptHash = string.Empty });
        var continuationMismatchedCompletion = HumanReviewContinuationContractHash.ApplyCompletion(continuationCompletion with { ReleaseReceipt = continuationMismatchedReceipt, CompletionHash = string.Empty });
        var continuationMismatchedState = HumanReviewContinuationContractHash.ApplyState(continuationState with { Completion = continuationMismatchedCompletion, StateHash = string.Empty });

        Assert.True(HumanReviewContinuationContractValidator.ValidateCompletion(continuationRequest, continuationWake, continuationReservation, continuationClaim, continuationCompletion).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(continuationRequest, continuationReservation, continuationState).IsValid);
        Assert.True(HumanReviewContinuationContractHash.MatchesReleaseReceipt(continuationCompletion.ReleaseReceipt));
        Assert.True(HumanReviewContinuationContractHash.MatchesCompletion(continuationCompletion));
        Assert.True(HumanReviewContinuationContractSnapshot.TryCaptureState(continuationRequest, continuationReservation, continuationState, out var continuationSnapshot, out _));
        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyState(continuationState, continuationSnapshot));
        Assert.True(HumanReviewContinuationContractHash.MatchesReleaseReceipt(continuationMismatchedReceipt));
        Assert.True(HumanReviewContinuationContractHash.MatchesCompletion(continuationMismatchedCompletion));
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateCompletion(continuationRequest, continuationWake, continuationReservation, continuationClaim, continuationMismatchedCompletion).Errors, error => error.Code == "release_kind_purpose_mismatch");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(continuationRequest, continuationReservation, continuationMismatchedState).Errors, error => error.Code == "release_kind_purpose_mismatch");

        var effectRequest = HumanReviewTestData.Request(HumanReviewPurpose.PreDispatchEffect);
        var effectReservation = Reservation(effectRequest);
        var effectWake = Wake(effectRequest, effectReservation);
        var effectClaim = Claim(effectWake, effectReservation);
        var effectCompletion = Completion(effectRequest, effectWake, effectReservation, effectClaim);
        var effectState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, effectWake, [effectClaim], effectCompletion, null, string.Empty));
        var effectMismatchedReceipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(effectCompletion.ReleaseReceipt with { Kind = HumanReviewContinuationReleaseKind.Continuation, EffectReceiptHash = null, ReleaseReceiptHash = string.Empty });
        var effectMismatchedCompletion = HumanReviewContinuationContractHash.ApplyCompletion(effectCompletion with { ReleaseReceipt = effectMismatchedReceipt, CompletionHash = string.Empty });
        var effectMismatchedState = HumanReviewContinuationContractHash.ApplyState(effectState with { Completion = effectMismatchedCompletion, StateHash = string.Empty });

        Assert.True(HumanReviewContinuationContractValidator.ValidateCompletion(effectRequest, effectWake, effectReservation, effectClaim, effectCompletion).IsValid);
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(effectRequest, effectReservation, effectState).IsValid);
        Assert.True(HumanReviewContinuationContractHash.MatchesReleaseReceipt(effectCompletion.ReleaseReceipt));
        Assert.True(HumanReviewContinuationContractHash.MatchesCompletion(effectCompletion));
        Assert.True(HumanReviewContinuationContractSnapshot.TryCaptureState(effectRequest, effectReservation, effectState, out var effectSnapshot, out _));
        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyState(effectState, effectSnapshot));
        Assert.True(HumanReviewContinuationContractHash.MatchesReleaseReceipt(effectMismatchedReceipt));
        Assert.True(HumanReviewContinuationContractHash.MatchesCompletion(effectMismatchedCompletion));
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateCompletion(effectRequest, effectWake, effectReservation, effectClaim, effectMismatchedCompletion).Errors, error => error.Code == "release_kind_purpose_mismatch");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(effectRequest, effectReservation, effectMismatchedState).Errors, error => error.Code == "release_kind_purpose_mismatch");
    }

    [Fact]
    public void Malformed_request_without_binding_fails_closed_across_continuation_boundaries()
    {
        var request = HumanReviewTestData.Request();
        var malformedRequest = request with { Binding = null! };
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var completion = Completion(wake, reservation, claim);
        var state = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim], completion, null, string.Empty));
        Assert.True(HumanReviewContinuationContractJson.TrySerializeState(request, reservation, state, out var json, out _));

        var wakeValidation = HumanReviewContinuationContractValidator.ValidateWake(malformedRequest, reservation, wake);
        var stateValidation = HumanReviewContinuationContractValidator.ValidateState(malformedRequest, reservation, state);
        var completionValidation = HumanReviewContinuationContractValidator.ValidateCompletion(malformedRequest, wake, reservation, claim, completion);
        var receiptValidation = HumanReviewContinuationContractValidator.ValidateReleaseReceipt(malformedRequest, wake, reservation, claim, completion.ReleaseReceipt);
        Assert.False(HumanReviewContinuationContractSnapshot.TryCaptureState(malformedRequest, reservation, state, out var snapshot, out var snapshotValidation));
        Assert.False(HumanReviewContinuationContractJson.TrySerializeState(malformedRequest, reservation, state, out var rejectedJson, out var serializationValidation));
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(malformedRequest, reservation, json, out var restored, out var restorationValidation));

        Assert.Null(snapshot);
        Assert.Null(rejectedJson);
        Assert.Null(restored);
        Assert.Contains(wakeValidation.Errors, error => error.Code == "binding_required");
        Assert.Contains(stateValidation.Errors, error => error.Code == "binding_required");
        Assert.Contains(completionValidation.Errors, error => error.Code == "binding_required");
        Assert.Contains(receiptValidation.Errors, error => error.Code == "binding_required");
        Assert.Contains(snapshotValidation.Errors, error => error.Code == "binding_required");
        Assert.Contains(serializationValidation.Errors, error => error.Code == "binding_required");
        Assert.Contains(restorationValidation.Errors, error => error.Code == "binding_required");
    }

    [Fact]
    public void Validators_fail_closed_for_unknown_values_malformed_identifiers_rebound_hashes_and_exact_bound_plus_one_values()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var retirement = Retirement(wake, reservation);
        var completion = Completion(wake, reservation, claim);
        var extendedWake = HumanReviewContinuationContractHash.ApplyWake(wake with { ExpiresAtUtc = wake.ExpiresAtUtc.AddDays(2), WakeHash = string.Empty });
        var claims = new List<HumanReviewContinuationClaim> { Claim(extendedWake, reservation) };
        for (var index = 1; index <= HumanReviewContractLimits.MaxContinuationClaims; index++) claims.Add(Takeover(claims[^1], $"claim-{index}"));
        var tooManyClaims = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, extendedWake, claims.ToImmutableArray(), null, null, string.Empty));

        Assert.False(HumanReviewContinuationContractValidator.ValidateWake(request, reservation, HumanReviewContinuationContractHash.ApplyWake(wake with { ExpectedGeneration = HumanReviewContractLimits.MaxVersion + 1, WakeHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateWake(request, reservation, HumanReviewContinuationContractHash.ApplyWake(wake with { BindingHash = HumanReviewTestData.Hash('f'), WakeHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateClaim(wake, reservation, HumanReviewContinuationContractHash.ApplyClaim(claim with { ClaimId = "Invalid", ClaimHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateClaim(wake, reservation, HumanReviewContinuationContractHash.ApplyClaim(claim with { LeaseExpiresAtUtc = claim.ClaimedAtUtc.Add(HumanReviewContractLimits.MaxContinuationClaimLease).AddTicks(1), ClaimHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateRetirement(wake, reservation, HumanReviewContinuationContractHash.ApplyRetirement(retirement with { Outcome = (HumanReviewContinuationOutcome)99, RetirementHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContinuationContractValidator.ValidateRetirement(wake, reservation, HumanReviewContinuationContractHash.ApplyRetirement(retirement with { Outcome = HumanReviewContinuationOutcome.Completed, RetirementHash = string.Empty })).IsValid);
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateCompletion(request, wake, reservation, claim, completion with { ReleaseReceipt = null! }).Errors, error => error.Code == "release_receipt_required");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateCompletion(request, wake, reservation, claim, HumanReviewContinuationContractHash.ApplyCompletion(completion with { ReleaseReceipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(completion.ReleaseReceipt with { Disposition = HumanReviewContinuationReleaseDisposition.Ambiguous, ReleaseReceiptHash = string.Empty }), CompletionHash = string.Empty })).Errors, error => error.Code == "unsupported_release_disposition");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateCompletion(request, wake, reservation, claim, HumanReviewContinuationContractHash.ApplyCompletion(completion with { ReleaseReceipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(completion.ReleaseReceipt with { Kind = HumanReviewContinuationReleaseKind.PreDispatchEffect, EffectReceiptHash = null, ReleaseReceiptHash = string.Empty }), CompletionHash = string.Empty })).Errors, error => error.Code == "effect_receipt_required");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(request, reservation, tooManyClaims).Errors, error => error.Code == "invalid_claim_count");
    }

    [Fact]
    public void State_machine_allows_only_expired_lease_takeover_and_exact_active_claim_completion()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var first = Claim(wake, reservation);
        var early = HumanReviewContinuationContractHash.ApplyClaim(first with { ClaimId = "claim-two", ClaimedAtUtc = first.LeaseExpiresAtUtc.AddTicks(-1), LeaseExpiresAtUtc = first.LeaseExpiresAtUtc.AddMinutes(1), Provenance = Provenance("claim-two", first.LeaseExpiresAtUtc.AddTicks(-1)), ClaimHash = string.Empty });
        var atExpiry = HumanReviewContinuationContractHash.ApplyClaim(first with { ClaimId = "claim-two", ClaimedAtUtc = first.LeaseExpiresAtUtc, LeaseExpiresAtUtc = first.LeaseExpiresAtUtc.AddMinutes(1), Provenance = Provenance("claim-two", first.LeaseExpiresAtUtc), ClaimHash = string.Empty });
        var takeover = HumanReviewContinuationContractHash.ApplyClaim(first with { ClaimId = "claim-two", ClaimedAtUtc = first.LeaseExpiresAtUtc.AddTicks(1), LeaseExpiresAtUtc = first.LeaseExpiresAtUtc.AddMinutes(1).AddTicks(1), Provenance = Provenance("claim-two", first.LeaseExpiresAtUtc.AddTicks(1)), ClaimHash = string.Empty });
        var predecessor = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var invalid = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [first, early], null, null, string.Empty));
        var equalBoundary = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [first, atExpiry], null, null, string.Empty));
        var valid = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [first, takeover], Completion(wake, reservation, takeover), null, string.Empty));

        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, predecessor).IsValid);
        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, predecessor, HumanReviewContinuationContractHash.ApplyState(predecessor with { Claims = [first], StateHash = string.Empty })).IsValid);
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(request, reservation, invalid).Errors, error => error.Code == "claim_takeover_before_expiry");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(request, reservation, equalBoundary).Errors, error => error.Code == "claim_takeover_before_expiry");
        var validValidation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, valid);
        Assert.True(validValidation.IsValid, string.Join("; ", validValidation.Errors.Select(error => error.Code)));
        Assert.False(HumanReviewContinuationContractValidator.ValidateCompletion(request, wake, reservation, first, Completion(wake, reservation, takeover)).IsValid);
    }

    [Fact]
    public void Claim_history_rejects_nonadjacent_duplicate_id_or_hash_without_throwing()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var first = Claim(wake, reservation);
        var second = Takeover(first, "claim-two");
        var duplicateId = Takeover(second, "claim-one");
        var duplicateHash = Takeover(second, "claim-three") with { ClaimHash = second.ClaimHash };
        var duplicateIdState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [first, second, duplicateId], null, null, string.Empty));
        var duplicateHashState = new HumanReviewContinuationState(1, wake, [first, second, duplicateHash], null, null, string.Empty);

        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(request, reservation, duplicateIdState).Errors, error => error.Code == "duplicate_claim_id");
        Assert.Contains(HumanReviewContinuationContractValidator.ValidateState(request, reservation, duplicateHashState).Errors, error => error.Code == "duplicate_claim_hash");
    }

    [Fact]
    public void Null_claim_elements_at_any_history_position_fail_closed_without_dereferencing_neighbors()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var firstNull = new HumanReviewContinuationState(1, wake, [null!, claim], null, null, string.Empty);
        var laterNull = new HumanReviewContinuationState(1, wake, [claim, null!], null, null, string.Empty);

        var firstNullValidation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, firstNull);
        var laterNullValidation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, laterNull);

        Assert.False(firstNullValidation.IsValid);
        Assert.False(laterNullValidation.IsValid);
        Assert.Contains(firstNullValidation.Errors, error => error.Code == "claim_required");
        Assert.Contains(laterNullValidation.Errors, error => error.Code == "claim_required");
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
        Assert.Contains(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, prior, rewritten).Errors, error => error.Code == "terminal_exact_replay_required");
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
        var secondClaimedAtUtc = claim.LeaseExpiresAtUtc.AddTicks(1);
        var second = HumanReviewContinuationContractHash.ApplyClaim(claim with { ClaimId = "claim-two", ClaimedAtUtc = secondClaimedAtUtc, LeaseExpiresAtUtc = secondClaimedAtUtc.AddMinutes(1), Provenance = Provenance("claim-two", secondClaimedAtUtc), ClaimHash = string.Empty });
        var firstState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim, second], null, null, string.Empty));
        var reversed = HumanReviewContinuationContractHash.ApplyState(firstState with { Claims = [second, claim], StateHash = string.Empty });
        var reversedValidation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, reversed);

        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyWake(wake, wake with { }));
        Assert.Equal(HumanReviewContinuationReplayDisposition.DivergentReuse, HumanReviewContinuationReplayClassifier.ClassifyWake(wake, altered));
        Assert.True(HumanReviewContinuationContractValidator.ValidateState(request, reservation, firstState).IsValid);
        Assert.NotEqual(firstState.StateHash, reversed.StateHash);
        Assert.False(reversedValidation.IsValid);
        Assert.Contains(reversedValidation.Errors, error => error.Code == "claim_takeover_before_expiry");
    }

    [Fact]
    public void Single_boundary_transition_table_allows_only_the_closed_wake_claim_and_terminal_paths()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var wakeOnly = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var claim = Claim(wake, reservation);
        var claimed = HumanReviewContinuationContractHash.ApplyState(wakeOnly with { Claims = [claim], StateHash = string.Empty });
        var completed = HumanReviewContinuationContractHash.ApplyState(claimed with { Completion = Completion(wake, reservation, claim), StateHash = string.Empty });
        var retired = HumanReviewContinuationContractHash.ApplyState(wakeOnly with { Retirement = Retirement(wake, reservation), StateHash = string.Empty });
        var claimAndCompletion = HumanReviewContinuationContractHash.ApplyState(wakeOnly with { Claims = [claim], Completion = Completion(wake, reservation, claim), StateHash = string.Empty });
        var divergentCompletion = HumanReviewContinuationContractHash.ApplyState(completed with { Completion = HumanReviewContinuationContractHash.ApplyCompletion(completed.Completion! with { CompletionId = "completion-two", CompletionHash = string.Empty }), StateHash = string.Empty });

        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, null, wakeOnly).IsValid);
        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, wakeOnly, wakeOnly with { }).IsValid);
        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, wakeOnly, claimed).IsValid);
        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, claimed, completed).IsValid);
        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, wakeOnly, retired).IsValid);
        Assert.True(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, completed, completed with { }).IsValid);
        Assert.Contains(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, null, claimed).Errors, error => error.Code == "initial_transition_must_be_wake_only");
        Assert.Contains(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, wakeOnly, claimAndCompletion).Errors, error => error.Code == "claim_transition_must_not_terminalize");
        Assert.Contains(HumanReviewContinuationStateTransitionValidator.ValidateTransition(request, reservation, completed, divergentCompletion).Errors, error => error.Code == "terminal_exact_replay_required");
    }

    [Fact]
    public void Canonical_replay_classification_survives_restart_and_never_uses_immutable_array_backing_identity()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var completion = Completion(wake, reservation, claim);
        var retirement = Retirement(wake, reservation);
        var completionCopy = HumanReviewContinuationContractHash.ApplyCompletion(completion with { Evidence = completion.Evidence.Select(item => item with { }).ToImmutableArray(), CompletionHash = string.Empty });
        var retirementCopy = HumanReviewContinuationContractHash.ApplyRetirement(retirement with { Evidence = retirement.Evidence.Select(item => item with { }).ToImmutableArray(), RetirementHash = string.Empty });
        var state = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim], completion, null, string.Empty));
        var stateCopy = HumanReviewContinuationContractHash.ApplyState(state with { Claims = state.Claims.Select(item => item with { }).ToImmutableArray(), Completion = completionCopy, StateHash = string.Empty });
        var divergedCompletionTime = completionCopy.CompletedAtUtc.AddTicks(1);
        var divergedRetirementTime = retirementCopy.RetiredAtUtc.AddTicks(1);
        var divergentCompletion = HumanReviewContinuationContractHash.ApplyCompletion(completionCopy with { CompletedAtUtc = divergedCompletionTime, Provenance = Provenance("completion-divergent", divergedCompletionTime), CompletionHash = string.Empty });
        var divergentRetirement = HumanReviewContinuationContractHash.ApplyRetirement(retirementCopy with { RetiredAtUtc = divergedRetirementTime, Provenance = Provenance("retirement-divergent", divergedRetirementTime), RetirementHash = string.Empty });
        var divergent = HumanReviewContinuationContractHash.ApplyState(stateCopy with { Completion = divergentCompletion, StateHash = string.Empty });

        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyCompletion(completion, completionCopy));
        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyRetirement(retirement, retirementCopy));
        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyState(state, stateCopy));
        Assert.Equal(HumanReviewContinuationReplayDisposition.DivergentReuse, HumanReviewContinuationReplayClassifier.ClassifyCompletion(completion, divergentCompletion));
        Assert.Equal(HumanReviewContinuationReplayDisposition.DivergentReuse, HumanReviewContinuationReplayClassifier.ClassifyRetirement(retirement, divergentRetirement));
        Assert.Equal(HumanReviewContinuationReplayDisposition.DivergentReuse, HumanReviewContinuationReplayClassifier.ClassifyState(state, divergent));
    }

    [Fact]
    public void Strict_schema_one_json_round_trips_only_in_its_exact_canonical_representation_and_rejects_contract_mutations()
    {
        var request = HumanReviewTestData.Request();
        var reservation = Reservation(request);
        var wake = Wake(request, reservation);
        var claim = Claim(wake, reservation);
        var state = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, [claim], Completion(wake, reservation, claim), null, string.Empty));
        var retirementState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, Retirement(wake, reservation), string.Empty));

        Assert.True(HumanReviewContinuationContractJson.TrySerializeState(request, reservation, state, out var json, out var serialization));
        Assert.True(serialization.IsValid);
        Assert.True(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, json, out var restored, out var roundTrip));
        Assert.True(roundTrip.IsValid);
        Assert.NotSame(state, restored);
        Assert.Equal(HumanReviewContinuationReplayDisposition.ExactReplay, HumanReviewContinuationReplayClassifier.ClassifyState(state, restored));
        var reordered = json!.Replace($",\"schemaVersion\":1,\"stateHash\":\"{state.StateHash}\"", $",\"stateHash\":\"{state.StateHash}\",\"schemaVersion\":1", StringComparison.Ordinal);
        var whitespace = json.Replace("{", "{ ", StringComparison.Ordinal);
        var alternateEscape = json.Replace("wake-one", "wake\\u002done", StringComparison.Ordinal);
        var alternateNumber = json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1.0", StringComparison.Ordinal);
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, reordered, out var reorderedState, out var reorderedValidation));
        Assert.Null(reorderedState);
        Assert.Contains(reorderedValidation.Errors, error => error.Code == "noncanonical_continuation_json");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, whitespace, out var whitespaceState, out var whitespaceValidation));
        Assert.Null(whitespaceState);
        Assert.Contains(whitespaceValidation.Errors, error => error.Code == "noncanonical_continuation_json");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, alternateEscape, out var alternateEscapeState, out var alternateEscapeValidation));
        Assert.Null(alternateEscapeState);
        Assert.Contains(alternateEscapeValidation.Errors, error => error.Code == "noncanonical_continuation_json");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, alternateNumber, out var alternateNumberState, out var alternateNumberValidation));
        Assert.Null(alternateNumberState);
        Assert.Contains(alternateNumberValidation.Errors, error => error.Code == "noncanonical_continuation_json");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, json![..^1] + ",\"unknown\":true}", out _, out var unknown));
        Assert.Contains(unknown.Errors, error => error.Code == "unknown_json_property");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, json[..^1] + ",\"schemaVersion\":1}", out _, out var duplicate));
        Assert.Contains(duplicate.Errors, error => error.Code == "duplicate_json_property");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, json.Replace($",\"stateHash\":\"{state.StateHash}\"", string.Empty, StringComparison.Ordinal), out _, out var missing));
        Assert.Contains(missing.Errors, error => error.Code == "required_json_property_missing");
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal), out _, out var forward));
        Assert.Contains(forward.Errors, error => error.Code == "unsupported_schema_version");
        Assert.True(HumanReviewContinuationContractJson.TrySerializeState(request, reservation, retirementState, out var retirementJson, out _));
        Assert.False(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, retirementJson!.Replace("\"outcome\":5", "\"outcome\":99", StringComparison.Ordinal), out _, out var unknownEnum));
        Assert.Contains(unknownEnum.Errors, error => error.Code == "unsupported_json_enum");
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
        Assert.True(HumanReviewContinuationContractJson.TrySerializeState(request, reservation, state, out var json, out _));
        Assert.True(HumanReviewContinuationContractJson.TryDeserializeState(request, reservation, json, out var roundTrip, out _));

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

    private static HumanReviewContinuationClaim Takeover(HumanReviewContinuationClaim prior, string claimId)
    {
        var claimedAtUtc = prior.LeaseExpiresAtUtc.AddTicks(1);
        return HumanReviewContinuationContractHash.ApplyClaim(prior with { ClaimId = claimId, ClaimedAtUtc = claimedAtUtc, LeaseExpiresAtUtc = claimedAtUtc.AddMinutes(10), Provenance = Provenance(claimId, claimedAtUtc), ClaimHash = string.Empty });
    }

    private static HumanReviewContinuationCompletion Completion(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim)
    {
        return Completion(HumanReviewPurpose.Continuation, wake, reservation, claim);
    }

    private static HumanReviewContinuationCompletion Completion(HumanReviewRequest request, HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim)
    {
        return Completion(request.Purpose, wake, reservation, claim);
    }

    private static HumanReviewContinuationCompletion Completion(HumanReviewPurpose purpose, HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim)
    {
        var time = claim.ClaimedAtUtc.AddMinutes(1);
        var kind = purpose == HumanReviewPurpose.PreDispatchEffect ? HumanReviewContinuationReleaseKind.PreDispatchEffect : HumanReviewContinuationReleaseKind.Continuation;
        var effectReceiptHash = kind == HumanReviewContinuationReleaseKind.PreDispatchEffect ? HumanReviewTestData.Hash('c') : null;
        var receipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(new HumanReviewContinuationReleaseReceipt(1, "release-one", new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash), new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, kind, HumanReviewContinuationReleaseDisposition.Released, HumanReviewTestData.Hash('a'), HumanReviewTestData.Hash('b'), effectReceiptHash, string.Empty));
        return HumanReviewContinuationContractHash.ApplyCompletion(new HumanReviewContinuationCompletion(1, "completion-one", new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash), new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, receipt, time, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("completion", time), string.Empty));
    }

    private static HumanReviewContinuationRetirement Retirement(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation)
    {
        var time = wake.PublishedAtUtc.AddMinutes(1);
        return HumanReviewContinuationContractHash.ApplyRetirement(new HumanReviewContinuationRetirement(1, "retirement-one", new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash), new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash), wake.ExpectedGeneration, HumanReviewContinuationOutcome.Blocked, time, ImmutableArray<HumanReviewRedactedPreview>.Empty, Provenance("retirement", time), string.Empty));
    }

    private static HumanReviewProvenance Provenance(string correlation, DateTimeOffset time) => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "coordinator-one", correlation, time, string.Empty));
}
