using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal static class GovernedLoopEffectAuthorityTestFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 19, 0, 0, TimeSpan.Zero);

    internal static (
        GovernedLoopEffectAuthorityRequest Request,
        AuthorityGrant Grant,
        AuthorityGrantResolution Resolution,
        CapabilityAdmissionPin RequiredPin,
        CapabilityAdmissionPin? UnrelatedPin) Create(bool includeUnrelatedWorkspaceCapability = false)
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.LinearArtifact(allowWorkspaceTools: includeUnrelatedWorkspaceCapability);
        var requiredIdentity = AuthorityGrantApplicationTestFixture.Capability(GovernedLoopSequentialApplicationTestFixture.ModelInferenceCapabilityId);
        var admittedIdentities = new List<CapabilityDescriptorIdentity> { requiredIdentity };
        if (includeUnrelatedWorkspaceCapability)
        {
            admittedIdentities.Add(AuthorityGrantApplicationTestFixture.Capability(GovernedLoopSequentialApplicationTestFixture.WorkspaceCommandCapabilityId));
        }

        var admittedCeiling = AuthorityGrantApplicationTestFixture.Ceiling(
            capabilities: admittedIdentities,
            maxTargets: 1,
            sideEffect: includeUnrelatedWorkspaceCapability ? CapabilitySideEffectClass.LocalReversible : CapabilitySideEffectClass.ReadOnly);
        var requiredCeiling = AuthorityGrantApplicationTestFixture.Ceiling(
            capabilities: [requiredIdentity],
            maxTargets: 1,
            sideEffect: CapabilitySideEffectClass.ReadOnly);
        var profile = AuthorityGrantApplicationTestFixture.Profile(ceiling: admittedCeiling);
        var profilePin = new AuthorityGrantProfilePin(
            new AuthorityProfileReference(profile.ProfileId, profile.Revision),
            AuthorityGrantApplicationTestFixture.ProfileHash(profile));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            artifact.RevisionArtifact.Revision,
            "publish-effect-loop",
            AuthorityGrantApplicationTestFixture.Hash64('7'));
        var binding = new AuthorityGrantBinding(profilePin, artifact.Graph.OwningRole, publication);
        var grant = AuthorityGrantApplicationTestFixture.Grant(
            binding: binding,
            ceiling: admittedCeiling,
            boundary: AuthorityGrantApplicationTestFixture.Boundary(Now.AddHours(-1), Now.AddHours(1)),
            recordedAtUtc: Now.AddMinutes(-5));
        var grantReference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            "admit-effect-run",
            AuthorityGrantApplicationTestFixture.Hash64('1'),
            publication,
            grantReference,
            artifact.Graph.OwningRole,
            AuthorityGrantApplicationTestFixture.Actor(),
            "web",
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var manifest = Manifest(admittedIdentities.Select(item => item.Id).ToArray());
        Assert.True(CapabilityDependencyManifestHash.TryCompute(manifest, out var requirementsHash, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var provider, out _));
        var pins = admittedIdentities.Select((identity, index) => new CapabilityAdmissionPin(
            identity,
            CapabilityKind.GraphNode,
            new CapabilityImplementationIdentity(provider!, $"effect-authority-test-{index + 1}"),
            new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, $"https://example.test/effect-authority-{index + 1}", "1", null),
            new CapabilityDependencyArtifactMetadata(null, null),
            $"A safe effect-authority test capability {index + 1}.")).ToArray();
        var capabilityEvidence = manifest.Required.Select((requirement, index) => new CapabilityAdmissionEvidence(
            manifest.SubjectId,
            requirement.CapabilityId,
            requirement.CompatibleVersionRange,
            false,
            "Selected",
            admittedIdentities[index],
            "The exact built-in effect-authority capability was selected.")).ToArray();
        var snapshot = new CapabilityAdmissionSnapshot(
            CapabilityAdmissionSnapshot.CurrentSchemaVersion,
            AuthorityGrantApplicationTestFixture.WorkspaceId,
            manifest,
            requirementsHash!.Value,
            pins,
            capabilityEvidence,
            Now);
        var executionBinding = GovernedLoopExecutionBinding.Create(1, "run-effect-authority", artifact.RevisionArtifact.Revision, 1);
        var dependencyHash = AuthorityGrantApplicationTestFixture.Hash64('4');
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            executionBinding,
            profilePin,
            grant.Boundary,
            dependencyHash,
            admittedCeiling,
            snapshot,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, admittedCeiling, snapshot),
            Now,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            Now,
            string.Empty));
        Assert.True(GovernedLoopAdmissionValidator.Validate(receipt).IsValid);
        var request = new GovernedLoopEffectAuthorityRequest(
            receipt,
            executionBinding,
            artifact,
            "infer-01",
            1,
            "provider-effect-1",
            "provider-correlation-1",
            GovernedLoopEffectBoundaryKind.ProviderTransport,
            requiredCeiling,
            [pins[0]]);
        var resolution = new AuthorityGrantResolution(
            AuthorityGrantResolutionStatus.Active,
            grantReference,
            grant,
            admittedCeiling,
            dependencyHash,
            Now,
            grant);
        return (request, grant, resolution, pins[0], includeUnrelatedWorkspaceCapability ? pins[1] : null);
    }

    internal static AuthorityGrantReference Reference(AuthorityGrant grant) => new(grant.GrantId, grant.Revision, grant.ContentHash);

    private static CapabilityDependencyManifest Manifest(IReadOnlyList<CapabilityId> capabilityIds)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/effect-authority-test", out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        return new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            capabilityIds.Select(capabilityId => new CapabilityDependency(capabilityId, range!)).ToArray(),
            [],
            new CapabilityDependencyArtifactMetadata(null, null));
    }
}
