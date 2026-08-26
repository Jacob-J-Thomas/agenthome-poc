using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates append-only successor states without performing compare-exchange, lease acquisition, recovery, or loop release.</summary>
public static class HumanReviewContinuationStateTransitionValidator
{
    /// <summary>Validates that a candidate state preserves every prior artifact exactly and adds only a legal claim or first terminal outcome.</summary>
    public static HumanReviewContractValidationResult ValidateTransition(HumanReviewRequest? request, HumanReviewContinuationReservation? reservation, HumanReviewContinuationState? previous, HumanReviewContinuationState? candidate)
    {
        var errors = new List<HumanReviewContractValidationError>();
        errors.AddRange(HumanReviewContinuationContractValidator.ValidateState(request, reservation, previous).Errors);
        errors.AddRange(HumanReviewContinuationContractValidator.ValidateState(request, reservation, candidate).Errors);
        if (previous is null || candidate is null) return new(errors);
        if (!Equals(previous.Wake, candidate.Wake)) Add(errors, "wake_rebound", "$.wake", "An existing continuation state cannot rebind its immutable wake.");
        if (candidate.Claims.Length < previous.Claims.Length) Add(errors, "claim_history_truncated", "$.claims", "Claim history is append-only and cannot be truncated.");
        else for (var index = 0; index < previous.Claims.Length; index++) if (!Equals(previous.Claims[index], candidate.Claims[index])) Add(errors, "claim_history_rewritten", $"$.claims[{index}]", "Claim history is append-only and prior entries cannot change.");
        if (previous.Completion is not null && !Equals(previous.Completion, candidate.Completion)) Add(errors, "completion_rewritten", "$.completion", "A terminal completion cannot change or be removed.");
        if (previous.Retirement is not null && !Equals(previous.Retirement, candidate.Retirement)) Add(errors, "retirement_rewritten", "$.retirement", "A terminal retirement cannot change or be removed.");
        if ((previous.Completion is not null || previous.Retirement is not null) && candidate.Claims.Length != previous.Claims.Length) Add(errors, "claim_after_terminal", "$.claims", "No claim can be appended after a terminal outcome.");
        return new(errors);
    }

    private static void Add(List<HumanReviewContractValidationError> errors, string code, string path, string message) => errors.Add(new HumanReviewContractValidationError(code, path, message));
}
