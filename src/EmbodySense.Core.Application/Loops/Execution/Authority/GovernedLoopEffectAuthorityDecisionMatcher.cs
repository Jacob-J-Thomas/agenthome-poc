using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Authority;

/// <summary>Authenticates one returned effect-authority decision against the complete exact request and retained admission proof.</summary>
public static class GovernedLoopEffectAuthorityDecisionMatcher
{
    /// <summary>Determines whether a decision is the complete exact result for one validated effect-authority request.</summary>
    /// <param name="decision">The decision returned by the authority boundary.</param>
    /// <param name="request">The exact request supplied to that boundary.</param>
    /// <returns><see langword="true"/> only when every operation identity and immutable admitted-authority field matches.</returns>
    public static bool IsExactMatch(
        GovernedLoopEffectAuthorityDecision? decision,
        GovernedLoopEffectAuthorityRequest? request)
    {
        if (decision is null || request is null)
        {
            return false;
        }

        try
        {
            if (!GovernedLoopEffectAuthorityRequestValidator.IsValid(request)
                || !GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid)
            {
                return false;
            }

            var receipt = request.AdmissionReceipt;
            var admitted = decision.AdmittedAuthority;
            var admittedBinding = new AuthorityGrantBinding(
                receipt.Evidence.GrantProfile,
                receipt.Intent.Role,
                receipt.Intent.Publication);
            return string.Equals(decision.RunId, request.ExecutionBinding.RunId, StringComparison.Ordinal)
                && decision.ExecutionGeneration == request.ExecutionBinding.ExecutionGeneration
                && string.Equals(decision.NodeId, request.NodeId, StringComparison.Ordinal)
                && decision.NodeAttempt == request.NodeAttempt
                && string.Equals(decision.EffectOperationId, request.EffectOperationId, StringComparison.Ordinal)
                && string.Equals(decision.CorrelationId, request.CorrelationId, StringComparison.Ordinal)
                && decision.BoundaryKind == request.BoundaryKind
                && string.Equals(decision.AdmissionReceiptHash, receipt.ContentHash, StringComparison.Ordinal)
                && AuthorityCeilingSubset.IsEqual(decision.RequiredAuthority, request.RequiredAuthority)
                && decision.RequiredCapabilityPins.SequenceEqual(request.RequiredCapabilityPins)
                && Equals(admitted.Grant, receipt.Intent.AuthorityGrant)
                && Equals(admitted.Binding, admittedBinding)
                && Equals(admitted.Boundary, receipt.Evidence.GrantBoundary)
                && AuthorityCeilingSubset.IsEqual(admitted.Ceiling, receipt.Evidence.EffectiveAuthority)
                && admitted.CapabilityPins.SequenceEqual(receipt.Evidence.CapabilityAdmission.Pins)
                && admitted.ObservedCapabilityPins.Count == 0
                && string.Equals(admitted.DependencyEvidenceHash, receipt.Evidence.GrantDependencyEvidenceHash, StringComparison.Ordinal);
        }
        catch (Exception malformed) when (malformed is not OutOfMemoryException)
        {
            return false;
        }
    }
}
