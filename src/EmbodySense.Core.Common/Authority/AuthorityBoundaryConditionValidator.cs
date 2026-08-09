using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority;

internal static class AuthorityBoundaryConditionValidator
{
    internal static AuthorityContractError? Validate(AuthorityBoundaryCondition? condition)
    {
        if (condition is null)
        {
            return new AuthorityContractError(AuthorityContractErrorCode.CollectionItemRequired, AuthorityContractField.BoundaryConditions);
        }

        if (!Enum.IsDefined(condition.Decision) || condition.Decision == AuthorityBoundaryDecision.Unknown)
        {
            return new AuthorityContractError(AuthorityContractErrorCode.UnsupportedBoundaryDecision, AuthorityContractField.BoundaryDecision);
        }

        if (!Enum.IsDefined(condition.Reason) || condition.Reason == AuthorityBoundaryReason.Unknown)
        {
            return new AuthorityContractError(AuthorityContractErrorCode.UnsupportedBoundaryReason, AuthorityContractField.BoundaryReason);
        }

        return IsValidPair(condition.Decision, condition.Reason)
            ? null
            : new AuthorityContractError(AuthorityContractErrorCode.InvalidBoundaryCondition, AuthorityContractField.BoundaryConditions);
    }

    private static bool IsValidPair(AuthorityBoundaryDecision decision, AuthorityBoundaryReason reason)
    {
        return decision switch
        {
            AuthorityBoundaryDecision.Direct => reason == AuthorityBoundaryReason.NoBoundary,
            AuthorityBoundaryDecision.Review => reason is AuthorityBoundaryReason.MandatoryReview or AuthorityBoundaryReason.HumanApprovalRequired,
            AuthorityBoundaryDecision.Pause => reason is AuthorityBoundaryReason.ProfileDraft or AuthorityBoundaryReason.ProfileSuspended or AuthorityBoundaryReason.StaleEvidence or AuthorityBoundaryReason.ConflictingState or AuthorityBoundaryReason.UncertainUserIntent,
            AuthorityBoundaryDecision.Deny => reason is AuthorityBoundaryReason.ProfileRetired or AuthorityBoundaryReason.ProfileExpired or AuthorityBoundaryReason.InvalidContract or AuthorityBoundaryReason.TargetLimitExceeded or AuthorityBoundaryReason.DataClassExceeded or AuthorityBoundaryReason.SideEffectExceeded or AuthorityBoundaryReason.ExternalPublication or AuthorityBoundaryReason.IrreversibleAction or AuthorityBoundaryReason.Recurrence,
            _ => false
        };
    }
}
