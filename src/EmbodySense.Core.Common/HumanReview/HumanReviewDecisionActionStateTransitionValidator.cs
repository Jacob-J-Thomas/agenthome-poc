using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates exact append-only transitions for one non-approval Human Review decision action state.</summary>
public static class HumanReviewDecisionActionStateTransitionValidator
{
    /// <summary>Validates a candidate reservation, wake publication, claim append, completion, retirement, or exact replay.</summary>
    public static HumanReviewContractValidationResult ValidateTransition(HumanReviewRequest? request, HumanReviewDecisionActionState? previous, HumanReviewDecisionActionState? candidate)
    {
        var errors = new List<HumanReviewContractValidationError>();
        var candidateValidation = HumanReviewDecisionActionContractValidator.ValidateState(request, candidate);
        errors.AddRange(candidateValidation.Errors);
        if (previous is null)
        {
            if (candidate is not null && candidate.Wake is not null) Add(errors, "action_initial_wake_forbidden", "$.wake", "An accepted decision must reserve before publication adds its wake.");
            return new HumanReviewContractValidationResult(errors);
        }
        var previousValidation = HumanReviewDecisionActionContractValidator.ValidateState(request, previous);
        errors.AddRange(previousValidation.Errors);
        if (!candidateValidation.IsValid || !previousValidation.IsValid || candidate is null) return new HumanReviewContractValidationResult(errors);
        if (SameState(previous, candidate)) return new HumanReviewContractValidationResult(errors);
        if (previous.Completion is not null || previous.Retirement is not null) { Add(errors, "action_terminal_exact_replay_required", "$", "A terminal action state permits only exact replay."); return new HumanReviewContractValidationResult(errors); }
        if (previous.Reservation.ReservationHash != candidate.Reservation.ReservationHash || previous.BindingHash != candidate.BindingHash || previous.ExpectedGeneration != candidate.ExpectedGeneration || previous.ReservedLifecycleVersion != candidate.ReservedLifecycleVersion) { Add(errors, "action_reservation_rebound", "$", "An action state cannot rebind reservation, binding, generation, or lifecycle evidence."); return new HumanReviewContractValidationResult(errors); }
        if (previous.Wake is null)
        {
            if (candidate.Wake is null || !candidate.Claims.IsDefaultOrEmpty || candidate.Completion is not null || candidate.Retirement is not null) Add(errors, "action_wake_publication_required", "$", "The first successor must add only one deterministic wake.");
            return new HumanReviewContractValidationResult(errors);
        }
        if (candidate.Wake is null || previous.Wake.WakeHash != candidate.Wake.WakeHash) { Add(errors, "action_wake_rebound", "$.wake", "A published action wake is immutable."); return new HumanReviewContractValidationResult(errors); }
        if (candidate.Claims.Length == previous.Claims.Length && ClaimsMatch(previous, candidate))
        {
            if (previous.Completion is null && candidate.Completion is not null && candidate.Retirement is null || previous.Retirement is null && candidate.Retirement is not null && candidate.Completion is null) return new HumanReviewContractValidationResult(errors);
            Add(errors, "action_transition_required", "$", "A successor must append a claim, complete, retire, or replay exactly.");
            return new HumanReviewContractValidationResult(errors);
        }
        if (candidate.Claims.Length == previous.Claims.Length + 1 && ClaimsMatch(previous, candidate) && candidate.Completion is null && candidate.Retirement is null) return new HumanReviewContractValidationResult(errors);
        Add(errors, "action_claim_history_rewritten", "$.claims", "Claims are append-only and one transition may append only one claim.");
        return new HumanReviewContractValidationResult(errors);
    }

    private static bool SameState(HumanReviewDecisionActionState left, HumanReviewDecisionActionState right) => left.StateHash == right.StateHash;
    private static bool ClaimsMatch(HumanReviewDecisionActionState previous, HumanReviewDecisionActionState candidate) => !previous.Claims.Any(static claim => claim is null) && !candidate.Claims.Take(previous.Claims.Length).Any(static claim => claim is null) && previous.Claims.Select(claim => claim.ClaimHash).SequenceEqual(candidate.Claims.Take(previous.Claims.Length).Select(claim => claim.ClaimHash), StringComparer.Ordinal);
    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message) => errors.Add(new HumanReviewContractValidationError(code, path, message));
}
