using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessEffectAuthority(ICapabilityAuthorityTransaction transaction, TimeProvider timeProvider) : IGovernedLoopEffectAuthorityDecisionBoundary
{
    public ICapabilityAuthorityTransaction AuthorityTransaction { get; } = transaction;

    public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(GovernedLoopEffectAuthorityRequest request, Func<CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
        => ExecuteWithDecisionAsync(request, (_, token) => commit(token), cancellationToken);

    public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(GovernedLoopEffectAuthorityRequest request, Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit, CancellationToken cancellationToken = default)
    {
        var decision = Decision(request);
        var result = await commit(decision, cancellationToken);
        return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
            GovernedLoopEffectAuthorityExecutionStatus.Decided,
            decision,
            GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
            true,
            result,
            "The process verifier retained one exact direct effect-authority decision.",
            decision.ContentHash);
    }

    private GovernedLoopEffectAuthorityDecision Decision(GovernedLoopEffectAuthorityRequest request)
    {
        var receipt = request.AdmissionReceipt;
        var proof = new GovernedLoopEffectAuthorityProof(
            GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
            receipt.Intent.AuthorityGrant,
            new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication),
            AuthorityGrantLifecycleStatus.Active,
            GovernedLoopEffectAuthorityGrantPosture.Active,
            receipt.Evidence.GrantBoundary,
            receipt.Evidence.EffectiveAuthority,
            receipt.Evidence.CapabilityAdmission.Pins,
            [],
            receipt.Evidence.GrantDependencyEvidenceHash);
        return GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
            GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
            request.ExecutionBinding.RunId,
            request.ExecutionBinding.ExecutionGeneration,
            request.NodeId,
            request.NodeAttempt,
            request.EffectOperationId,
            request.CorrelationId,
            request.BoundaryKind,
            receipt.ContentHash,
            proof,
            proof,
            request.RequiredAuthority,
            request.RequiredAuthority,
            request.RequiredCapabilityPins,
            GovernedLoopEffectAuthorityDisposition.Direct,
            GovernedLoopEffectAuthorityReason.ActiveExact,
            timeProvider.GetUtcNow(),
            string.Empty));
    }
}
