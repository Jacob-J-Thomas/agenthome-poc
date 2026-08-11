using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal static class HostileEffectAuthorityDecisionFactory
{
    private static readonly string _forgedDependencyEvidenceHash = new('f', 64);

    internal static GovernedLoopEffectAuthorityDecision ForDifferentOperation(
        GovernedLoopEffectAuthorityDecision exact)
    {
        ArgumentNullException.ThrowIfNull(exact);
        return RequireCanonical(GovernedLoopEffectAuthorityContractHash.Apply(exact with
        {
            EffectOperationId = "hostile-substituted-operation",
            ContentHash = string.Empty,
        }));
    }

    internal static GovernedLoopEffectAuthorityDecision WithForgedAdmittedProof(
        GovernedLoopEffectAuthorityDecision exact)
    {
        ArgumentNullException.ThrowIfNull(exact);
        var admitted = CopyProof(exact.AdmittedAuthority, _forgedDependencyEvidenceHash);
        var current = exact.CurrentAuthority is null
            ? null
            : CopyProof(exact.CurrentAuthority, _forgedDependencyEvidenceHash);
        return RequireCanonical(GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
            exact.SchemaVersion,
            exact.RunId,
            exact.ExecutionGeneration,
            exact.NodeId,
            exact.NodeAttempt,
            exact.EffectOperationId,
            exact.CorrelationId,
            exact.BoundaryKind,
            exact.AdmissionReceiptHash,
            admitted,
            current,
            exact.RequiredAuthority,
            exact.EffectiveAuthority,
            exact.RequiredCapabilityPins,
            exact.Disposition,
            exact.Reason,
            exact.EvaluatedAtUtc,
            string.Empty)));
    }

    private static GovernedLoopEffectAuthorityProof CopyProof(
        GovernedLoopEffectAuthorityProof proof,
        string dependencyEvidenceHash)
        => new(
            proof.SchemaVersion,
            proof.Grant,
            proof.Binding,
            proof.GrantStatus,
            proof.GrantPosture,
            proof.Boundary,
            proof.Ceiling,
            proof.CapabilityPins,
            proof.ObservedCapabilityPins,
            dependencyEvidenceHash);

    private static GovernedLoopEffectAuthorityDecision RequireCanonical(
        GovernedLoopEffectAuthorityDecision decision)
    {
        Assert.True(
            GovernedLoopEffectAuthorityContractValidator.Validate(decision).IsValid,
            "The hostile decision must remain structurally canonical so only exact request matching rejects it.");
        return decision;
    }
}
