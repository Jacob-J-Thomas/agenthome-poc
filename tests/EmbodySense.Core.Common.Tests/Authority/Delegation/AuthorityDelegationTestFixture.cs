using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Tests.Authority.Grants;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

internal static class AuthorityDelegationTestFixture
{
    internal const string WorkspaceId = "workspace-sha256:1111111111111111111111111111111111111111111111111111111111111111";

    internal static readonly DateTimeOffset EvaluatedAtUtc = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset IssuedAtUtc = EvaluatedAtUtc.AddMinutes(1);

    internal static CapabilityAdmissionPin Pin()
        => TestCapabilityAdmissionFactory.Create(LoopCapabilityRequirements.CreateDefaultConversationManifest()).Pins
            .OrderBy(value => value.DescriptorIdentity.Id.Value, StringComparer.Ordinal)
            .First();

    internal static CapabilityAdmissionPin ForeignPin()
    {
        var source = Pin();
        Assert.True(CapabilityId.TryParse("org.embodysense/zz-foreign", out var id, out _));
        return source with
        {
            DescriptorIdentity = source.DescriptorIdentity with { Id = id! },
        };
    }

    internal static CapabilityDataClass DataClass(string value)
        => AuthorityGrantTestFixture.DataClass(value);

    internal static AuthorityCeiling Ceiling(
        IReadOnlyList<CapabilityDescriptorIdentity>? capabilities = null,
        IReadOnlyList<CapabilityDataClass>? dataClasses = null,
        int maxTargetCount = 5,
        CapabilitySideEffectClass sideEffectClass = CapabilitySideEffectClass.ReadOnly,
        bool recurrence = true,
        bool externalPublication = true,
        bool irreversible = true)
    {
        var pin = Pin();
        return AuthorityGrantTestFixture.Ceiling(
            capabilities ?? [pin.DescriptorIdentity],
            dataClasses ?? [AuthorityGrantTestFixture.DataClass("workspace-content")],
            maxTargetCount,
            sideEffectClass,
            recurrence,
            externalPublication,
            irreversible);
    }

    internal static AuthorityDelegationParentEvidenceReference ParentEvidence(string? originEvidenceHash = null)
    {
        var grant = AuthorityGrantTestFixture.Grant(ceiling: Ceiling());
        var candidate = new AuthorityDelegationParentEvidenceReference(
            WorkspaceId,
            GovernedLoopExecutionBinding.Create(1, "parent-run", grant.Binding.Loop.Revision, 1),
            "origin-node",
            2,
            Hash('1'),
            AuthorityGrantTestFixture.Actor("user-owner"),
            new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            grant.Binding,
            originEvidenceHash ?? Hash('2'),
            Hash('3'),
            EvaluatedAtUtc,
            string.Empty);
        return AuthorityDelegationContractHash.Apply(candidate);
    }

    internal static AuthorityDelegationTargetBinding Target(
        AuthorityDelegationTargetKind kind = AuthorityDelegationTargetKind.Role,
        string? nodeId = null,
        string? bindingEvidenceHash = null)
    {
        var parent = ParentEvidence();
        return kind switch
        {
            AuthorityDelegationTargetKind.Role => new(kind, parent.GrantBinding.Role, null, null, bindingEvidenceHash ?? Hash('4')),
            AuthorityDelegationTargetKind.Loop => new(kind, parent.GrantBinding.Role, parent.GrantBinding.Loop, null, bindingEvidenceHash ?? Hash('4')),
            AuthorityDelegationTargetKind.Node => new(kind, parent.GrantBinding.Role, parent.GrantBinding.Loop, nodeId ?? "delegated-node", bindingEvidenceHash ?? Hash('4')),
            _ => new(kind, parent.GrantBinding.Role, null, nodeId, bindingEvidenceHash ?? Hash('4')),
        };
    }

    internal static AuthorityDelegationBoundary Boundary(
        DateTimeOffset? effectiveAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        AuthorityDelegationCompletionConstraintKind completion = AuthorityDelegationCompletionConstraintKind.None)
        => new(effectiveAtUtc ?? IssuedAtUtc, expiresAtUtc ?? IssuedAtUtc.AddMinutes(30), completion);

    internal static AuthorityDelegationEnvelope Envelope(
        AuthorityDelegationParentEvidenceReference? parentEvidence = null,
        AuthorityDelegationTargetBinding? target = null,
        AuthorityCeiling? parentCeiling = null,
        AuthorityCeiling? delegatedCeiling = null,
        IReadOnlyList<CapabilityAdmissionPin>? parentPins = null,
        IReadOnlyList<CapabilityAdmissionPin>? delegatedPins = null,
        AuthorityDelegationBoundary? boundary = null,
        AuthorityDelegationSubsetProof? proof = null,
        string? targetClass = null,
        string? operationClass = null,
        AuthorityPurpose? purpose = null,
        DateTimeOffset? issuedAtUtc = null,
        string? targetMaximumEvidenceHash = null,
        bool applyHash = true)
    {
        var exactParent = parentEvidence ?? ParentEvidence();
        var exactTarget = target ?? Target();
        var exactParentCeiling = parentCeiling ?? Ceiling();
        var exactDelegatedCeiling = delegatedCeiling ?? Ceiling(maxTargetCount: 2, recurrence: false, externalPublication: false, irreversible: false);
        var exactParentPins = parentPins ?? [Pin()];
        var exactDelegatedPins = delegatedPins ?? [Pin()];
        var ids = exactParentCeiling.Capabilities.Select(value => value.Id.Value).Order(StringComparer.Ordinal).ToArray();
        var exactProof = proof ?? AuthorityDelegationSubsetEvaluator.Evaluate(
            exactParentCeiling,
            exactParentPins,
            ids,
            ids,
            ids,
            exactDelegatedCeiling,
            exactDelegatedPins,
            exactParent.ContentHash,
            targetMaximumEvidenceHash ?? Hash('5'))!;
        var revocation = AuthorityDelegationContractHash.Apply(new AuthorityDelegationRevocationLink(
            exactParent.GrantReference,
            exactParent.ParentAdmissionReceiptHash,
            exactParent.WorkspaceId,
            exactParent.ParentExecution.RunId,
            exactParent.ParentExecution.ExecutionGeneration,
            string.Empty));
        var candidate = new AuthorityDelegationEnvelope(
            AuthorityDelegationEnvelope.CurrentSchemaVersion,
            "delegation-operation-1",
            exactParent,
            exactTarget,
            exactDelegatedCeiling,
            exactDelegatedPins,
            targetClass ?? "role-execution",
            operationClass ?? "bounded-operation",
            purpose ?? AuthorityGrantTestFixture.Purpose("Delegate one exact bounded operation."),
            boundary ?? Boundary(),
            revocation,
            exactProof,
            issuedAtUtc ?? IssuedAtUtc,
            string.Empty);
        return applyHash ? AuthorityDelegationContractHash.Apply(candidate) : candidate;
    }

    internal static string Hash(char value) => new(value, AuthorityDelegationContractLimits.Sha256HexCharacters);
}
