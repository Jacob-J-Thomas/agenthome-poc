using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.Execution.Authority;
using EmbodySense.Core.Persistence.Loops.Execution.Authority.Models;

namespace EmbodySense.CancellationHost.Persistence;

internal static class GovernedLoopEffectAuthorityCrashHost
{
    private static readonly DateTimeOffset _recordedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _evaluatedAtUtc = _recordedAtUtc.AddMinutes(1);

    internal static async Task<int> RunAsync(
        string mode,
        string workspaceRoot,
        string trustRoot,
        string releaseMarker,
        string readyMarker,
        string operationId)
    {
        var target = mode switch
        {
            "crash-proof" => GovernedLoopEffectAuthorityPersistenceBoundary.ProofPublished,
            "crash-primary" => GovernedLoopEffectAuthorityPersistenceBoundary.PrimaryPublished,
            "crash-trust" => GovernedLoopEffectAuthorityPersistenceBoundary.TrustAdvanced,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        var options = new GovernedLoopEffectAuthorityEvidenceStoreOptions
        {
            DurableBoundaryObserver = (boundary, _) =>
            {
                if (boundary == target)
                {
                    CrossProcessMarkerProtocol.TerminateAbruptly();
                }

                return ValueTask.CompletedTask;
            },
        };

        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyMarker, releaseMarker);
        var store = new GovernedLoopEffectAuthorityEvidenceStore(
            new WorkspacePaths(workspaceRoot),
            new FileCapabilityCatalogTrustProvider(trustRoot),
            options);
        _ = await store.AppendAsync(CreateDecision(operationId));
        return 0;
    }

    private static GovernedLoopEffectAuthorityDecision CreateDecision(string operationId)
    {
        var pin = CreateCapabilityPin();
        var admittedCeiling = CreateCeiling(pin, maxTargetCount: 2);
        var grant = CreateGrant(admittedCeiling);
        var proof = new GovernedLoopEffectAuthorityProof(
            GovernedLoopEffectAuthorityContractLimits.CurrentSchemaVersion,
            new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            grant.Binding,
            AuthorityGrantLifecycleStatus.Active,
            GovernedLoopEffectAuthorityGrantPosture.Active,
            grant.Boundary,
            admittedCeiling,
            [pin],
            [],
            new string('d', GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters));
        var required = CreateCeiling(pin, maxTargetCount: 1);
        return GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
            GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
            "run-1",
            1,
            "inference-1",
            1,
            operationId,
            "provider-request-1",
            GovernedLoopEffectBoundaryKind.ProviderTransport,
            new string('a', GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters),
            proof,
            proof,
            required,
            required,
            [pin],
            GovernedLoopEffectAuthorityDisposition.Direct,
            GovernedLoopEffectAuthorityReason.ActiveExact,
            _evaluatedAtUtc,
            string.Empty));
    }

    private static CapabilityAdmissionPin CreateCapabilityPin()
    {
        var capabilityId = ParseCapabilityId("org.embodysense/conversation-turn");
        var version = ParseCapabilityVersion("1.0.0");
        var descriptorDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(capabilityId.Value))).ToLowerInvariant();
        var descriptorHash = ParseCapabilityHash("sha256:" + descriptorDigest);
        var provider = ParseCapabilityProvider("org.embodysense");
        return new CapabilityAdmissionPin(
            new CapabilityDescriptorIdentity(capabilityId, version, descriptorHash),
            CapabilityKind.GraphNode,
            new CapabilityImplementationIdentity(provider, "conversation-turn"),
            new CapabilityProvenance(
                CapabilityProvenanceKind.BuiltIn,
                "https://embodysense.dev/builtins/conversation-turn",
                "1",
                null),
            new CapabilityDependencyArtifactMetadata(null, null),
            "Test-safe description for conversation-turn.");
    }

    private static AuthorityGrant CreateGrant(AuthorityCeiling ceiling)
    {
        var binding = CreateBinding();
        var boundary = new AuthorityGrantBoundary(
            _recordedAtUtc.AddMinutes(-5),
            _recordedAtUtc.AddHours(1),
            AuthorityGrantCompletionConstraintKind.None);
        var candidate = new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            ParseGrantId("workspace-helper"),
            ParseGrantRevision("1"),
            null,
            null,
            AuthorityGrantLifecycleStatus.Active,
            binding,
            ceiling,
            boundary,
            ParseActorId("user-owner"),
            ParsePurpose("Delegate bounded work for one governed loop revision."),
            _recordedAtUtc,
            string.Empty);
        return AuthorityGrantHash.Apply(candidate);
    }

    private static AuthorityGrantBinding CreateBinding()
    {
        var profileReference = new AuthorityProfileReference(
            ParseProfileId("default-profile"),
            ParseProfileRevision("3"));
        var profile = new AuthorityGrantProfilePin(
            profileReference,
            ParseProfileHash("sha256:" + new string('a', 64)));
        var role = new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity("bounded-helper", 4),
            new string('b', 64));
        var revision = GovernedLoopRevisionReference.Create(
            1,
            "governed-loop",
            "revision-7",
            new string('c', 64));
        var loop = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            revision,
            "publish-7",
            new string('d', 64));
        return new AuthorityGrantBinding(profile, role, loop);
    }

    private static AuthorityCeiling CreateCeiling(CapabilityAdmissionPin pin, int maxTargetCount)
        => new(
            [pin.DescriptorIdentity],
            [ParseDataClass("workspace-content")],
            maxTargetCount,
            CapabilitySideEffectClass.ReadOnly,
            AllowsRecurrence: false,
            AllowsExternalPublication: false,
            AllowsIrreversibleAction: false);

    private static CapabilityId ParseCapabilityId(string value)
    {
        _ = CapabilityId.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("capability id", error?.Message);
    }

    private static CapabilityVersion ParseCapabilityVersion(string value)
    {
        _ = CapabilityVersion.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("capability version", error?.Message);
    }

    private static CapabilityDescriptorHash ParseCapabilityHash(string value)
    {
        _ = CapabilityDescriptorHash.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("capability hash", error?.Message);
    }

    private static CapabilityProviderId ParseCapabilityProvider(string value)
    {
        _ = CapabilityProviderId.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("capability provider", error?.Message);
    }

    private static CapabilityDataClass ParseDataClass(string value)
    {
        _ = CapabilityDataClass.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("data class", error?.Message);
    }

    private static AuthorityGrantId ParseGrantId(string value)
    {
        _ = AuthorityGrantId.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("grant id", error?.ToString());
    }

    private static AuthorityGrantRevision ParseGrantRevision(string value)
    {
        _ = AuthorityGrantRevision.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("grant revision", error?.ToString());
    }

    private static AuthorityActorId ParseActorId(string value)
    {
        _ = AuthorityActorId.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("actor id", error?.ToString());
    }

    private static AuthorityPurpose ParsePurpose(string value)
    {
        _ = AuthorityPurpose.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("authority purpose", error?.ToString());
    }

    private static AuthorityProfileId ParseProfileId(string value)
    {
        _ = AuthorityProfileId.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("profile id", error?.ToString());
    }

    private static AuthorityProfileRevision ParseProfileRevision(string value)
    {
        _ = AuthorityProfileRevision.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("profile revision", error?.ToString());
    }

    private static AuthorityProfileHash ParseProfileHash(string value)
    {
        _ = AuthorityProfileHash.TryParse(value, out var parsed, out var error);
        return parsed ?? throw Invalid("profile hash", error?.ToString());
    }

    private static InvalidOperationException Invalid(string description, string? detail)
        => new($"The effect-authority crash host {description} is invalid: {detail ?? "unknown validation error"}.");
}
