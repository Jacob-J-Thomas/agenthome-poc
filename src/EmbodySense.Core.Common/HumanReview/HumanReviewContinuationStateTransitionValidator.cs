using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates one closed, single-boundary successor transition without performing compare-exchange, lease acquisition, recovery, or loop release.</summary>
public static class HumanReviewContinuationStateTransitionValidator
{
    /// <summary>Validates the only legal boundaries: null to wake-only, exact replay, one claim, completion alone, or retirement alone; terminal states permit exact replay only.</summary>
    public static HumanReviewContractValidationResult ValidateTransition(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, HumanReviewContinuationState? previous, HumanReviewContinuationState? candidate)
    {
        var errors = new List<HumanReviewContractValidationError>();
        var previousValidation = previous is null ? new HumanReviewContractValidationResult([]) : HumanReviewContinuationContractValidator.ValidateState(request, reservation, previous);
        var candidateValidation = HumanReviewContinuationContractValidator.ValidateState(request, reservation, candidate);
        errors.AddRange(previousValidation.Errors);
        errors.AddRange(candidateValidation.Errors);
        if (candidate is null || !candidateValidation.IsValid || previous is not null && !previousValidation.IsValid) return new HumanReviewContractValidationResult(errors);

        if (previous is null)
        {
            if (!candidate.Claims.IsEmpty || candidate.Completion is not null || candidate.Retirement is not null) Add(errors, "initial_transition_must_be_wake_only", "$", "The first continuation state may publish only its immutable wake.");
            return new HumanReviewContractValidationResult(errors);
        }

        if (HumanReviewContinuationReplayClassifier.ClassifyState(previous, candidate) == HumanReviewContinuationReplayDisposition.ExactReplay) return new HumanReviewContractValidationResult(errors);

        if (previous.Completion is not null || previous.Retirement is not null)
        {
            Add(errors, "terminal_exact_replay_required", "$", "A terminal continuation state permits only an exact canonical replay.");
            return new HumanReviewContractValidationResult(errors);
        }

        if (HumanReviewContinuationReplayClassifier.ClassifyWake(previous.Wake, candidate.Wake) != HumanReviewContinuationReplayDisposition.ExactReplay)
        {
            Add(errors, "wake_rebound", "$.wake", "An existing continuation state cannot rebind its immutable wake.");
            return new HumanReviewContractValidationResult(errors);
        }

        if (candidate.Claims.Length == previous.Claims.Length + 1)
        {
            if (!ClaimsMatch(previous, candidate)) Add(errors, "claim_history_rewritten", "$.claims", "A claim transition must preserve every prior canonical claim exactly.");
            if (candidate.Completion is not null || candidate.Retirement is not null) Add(errors, "claim_transition_must_not_terminalize", "$", "One transition may append exactly one claim or one terminal artifact, not both.");
            return new HumanReviewContractValidationResult(errors);
        }

        if (candidate.Claims.Length != previous.Claims.Length)
        {
            Add(errors, "invalid_claim_delta", "$.claims", "A successor may append exactly one claim or preserve the exact claim history.");
            return new HumanReviewContractValidationResult(errors);
        }

        if (!ClaimsMatch(previous, candidate))
        {
            Add(errors, "claim_history_rewritten", "$.claims", "A terminal transition must preserve every prior canonical claim exactly.");
            return new HumanReviewContractValidationResult(errors);
        }

        if (candidate.Completion is not null && candidate.Retirement is null) return new HumanReviewContractValidationResult(errors);
        if (candidate.Completion is null && candidate.Retirement is not null) return new HumanReviewContractValidationResult(errors);

        Add(errors, "transition_requires_one_boundary", "$", "A nonterminal successor must be an exact replay, append one claim, complete alone, or retire alone.");
        return new HumanReviewContractValidationResult(errors);
    }

    private static bool ClaimsMatch(HumanReviewContinuationState previous, HumanReviewContinuationState candidate)
    {
        for (var index = 0; index < previous.Claims.Length; index++)
        {
            if (HumanReviewContinuationReplayClassifier.ClassifyClaim(previous.Claims[index], candidate.Claims[index]) != HumanReviewContinuationReplayDisposition.ExactReplay) return false;
        }
        return true;
    }

    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message) => errors.Add(new HumanReviewContractValidationError(code, path, message));
}
