using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Tests.Authority.Grants;
using EmbodySense.Core.Common.Tests.Loops.Admission;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Authority;

internal static class GovernedLoopEffectAuthorityTestFixture
{
    internal static readonly DateTimeOffset EvaluatedAtUtc = new(2026, 8, 10, 12, 1, 0, TimeSpan.Zero);

    internal static CapabilityAdmissionPin Pin(int index = 0)
        => GovernedLoopAdmissionTestFixture.CapabilityAdmission().Pins[index];

    internal static AuthorityCeiling AdmittedCeiling(CapabilityAdmissionPin? pin = null, int maxTargetCount = 2)
    {
        var exactPin = pin ?? Pin();
        return AuthorityGrantTestFixture.Ceiling(
            capabilities: [exactPin.DescriptorIdentity],
            maxTargetCount: maxTargetCount);
    }

    internal static AuthorityCeiling RequiredCeiling(CapabilityAdmissionPin? pin = null)
    {
        var exactPin = pin ?? Pin();
        return AuthorityGrantTestFixture.Ceiling(
            capabilities: [exactPin.DescriptorIdentity],
            maxTargetCount: 1);
    }

    internal static GovernedLoopEffectAuthorityProof Proof(
        AuthorityGrantLifecycleStatus status = AuthorityGrantLifecycleStatus.Active,
        GovernedLoopEffectAuthorityGrantPosture grantPosture = GovernedLoopEffectAuthorityGrantPosture.Active,
        AuthorityGrantReference? grant = null,
        AuthorityGrantBinding? binding = null,
        AuthorityGrantBoundary? boundary = null,
        AuthorityCeiling? ceiling = null,
        IReadOnlyList<CapabilityAdmissionPin>? pins = null,
        IReadOnlyList<CapabilityAdmissionPin>? observedPins = null,
        string? dependencyEvidenceHash = null,
        bool omitDependencyEvidenceHash = false,
        int schemaVersion = GovernedLoopEffectAuthorityContractLimits.CurrentSchemaVersion)
    {
        var exactPin = Pin();
        var exactCeiling = ceiling ?? AdmittedCeiling(exactPin);
        var exactGrant = AuthorityGrantTestFixture.Grant(binding: binding, ceiling: exactCeiling, boundary: boundary);
        return new GovernedLoopEffectAuthorityProof(
            schemaVersion,
            grant ?? new AuthorityGrantReference(exactGrant.GrantId, exactGrant.Revision, exactGrant.ContentHash),
            binding ?? exactGrant.Binding,
            status,
            grantPosture,
            boundary ?? exactGrant.Boundary,
            exactCeiling,
            pins ?? [exactPin],
            observedPins ?? [],
            omitDependencyEvidenceHash ? null : dependencyEvidenceHash ?? Hash('d'));
    }

    internal static GovernedLoopEffectAuthorityDecision Decision(
        GovernedLoopEffectAuthorityProof? admitted = null,
        GovernedLoopEffectAuthorityProof? current = null,
        bool omitCurrent = false,
        AuthorityCeiling? requiredAuthority = null,
        AuthorityCeiling? effectiveAuthority = null,
        IReadOnlyList<CapabilityAdmissionPin>? requiredPins = null,
        GovernedLoopEffectAuthorityDisposition disposition = GovernedLoopEffectAuthorityDisposition.Direct,
        GovernedLoopEffectAuthorityReason reason = GovernedLoopEffectAuthorityReason.ActiveExact,
        DateTimeOffset? evaluatedAtUtc = null,
        bool applyHash = true)
    {
        var exactAdmitted = admitted ?? Proof();
        var exactCurrent = omitCurrent ? null : current ?? exactAdmitted;
        var exactRequired = requiredAuthority ?? RequiredCeiling(exactAdmitted.CapabilityPins[0]);
        var candidate = new GovernedLoopEffectAuthorityDecision(
            GovernedLoopEffectAuthorityContractLimits.CurrentSchemaVersion,
            "run-1",
            1,
            "inference-1",
            1,
            "effect-operation-1",
            "provider-request-1",
            GovernedLoopEffectBoundaryKind.ProviderTransport,
            GovernedLoopAdmissionTestFixture.Hash('a'),
            exactAdmitted,
            exactCurrent,
            exactRequired,
            effectiveAuthority ?? (disposition == GovernedLoopEffectAuthorityDisposition.Direct ? exactRequired : AuthorityCeilingIntersection.EmptyCeiling()),
            requiredPins ?? [exactAdmitted.CapabilityPins[0]],
            disposition,
            reason,
            evaluatedAtUtc ?? EvaluatedAtUtc,
            string.Empty);
        return applyHash ? GovernedLoopEffectAuthorityContractHash.Apply(candidate) : candidate;
    }

    internal static GovernedLoopEffectAuthorityProof CopyProof(
        GovernedLoopEffectAuthorityProof value,
        AuthorityGrantBinding? binding = null,
        bool omitBinding = false,
        AuthorityGrantBoundary? boundary = null,
        bool omitBoundary = false,
        AuthorityCeiling? ceiling = null,
        bool omitCeiling = false,
        IReadOnlyList<CapabilityAdmissionPin>? pins = null,
        bool omitPins = false,
        IReadOnlyList<CapabilityAdmissionPin>? observedPins = null,
        bool omitObservedPins = false)
        => new(
            value.SchemaVersion,
            value.Grant,
            omitBinding ? null! : binding ?? value.Binding,
            value.GrantStatus,
            value.GrantPosture,
            omitBoundary ? null! : boundary ?? value.Boundary,
            omitCeiling ? null! : ceiling ?? value.Ceiling,
            omitPins ? null! : pins ?? value.CapabilityPins,
            omitObservedPins ? null! : observedPins ?? value.ObservedCapabilityPins,
            value.DependencyEvidenceHash);

    internal static GovernedLoopEffectAuthorityDecision CopyDecision(
        GovernedLoopEffectAuthorityDecision value,
        GovernedLoopEffectAuthorityProof? admitted = null,
        GovernedLoopEffectAuthorityProof? current = null,
        bool omitCurrent = false,
        AuthorityCeiling? requiredAuthority = null,
        AuthorityCeiling? effectiveAuthority = null,
        IReadOnlyList<CapabilityAdmissionPin>? requiredPins = null)
        => new(
            value.SchemaVersion,
            value.RunId,
            value.ExecutionGeneration,
            value.NodeId,
            value.NodeAttempt,
            value.EffectOperationId,
            value.CorrelationId,
            value.BoundaryKind,
            value.AdmissionReceiptHash,
            admitted ?? value.AdmittedAuthority,
            omitCurrent ? null : current ?? value.CurrentAuthority,
            requiredAuthority ?? value.RequiredAuthority,
            effectiveAuthority ?? value.EffectiveAuthority,
            requiredPins ?? value.RequiredCapabilityPins,
            value.Disposition,
            value.Reason,
            value.EvaluatedAtUtc,
            value.ContentHash);

    internal static string Hash(char value) => new(value, GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters);
}
