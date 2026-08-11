using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using System.Collections.Immutable;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

internal static class AuthorityGrantApplicationTestFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

    internal static AuthorityGrantMutationRequest Request(
        AuthorityGrantOperationKind kind = AuthorityGrantOperationKind.Create,
        AuthorityGrant? current = null,
        string operationId = "grant-operation-1",
        AuthorityGrantBinding? binding = null,
        AuthorityCeiling? ceiling = null,
        AuthorityGrantBoundary? boundary = null,
        long? expectedRevision = null,
        AuthorityGrantLifecycleStatus? expectedStatus = null)
    {
        var usesCandidate = kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace;
        var request = new AuthorityGrantMutationRequest(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            operationId,
            kind,
            current?.GrantId ?? GrantId(),
            expectedRevision ?? current?.Revision.Value ?? 0,
            expectedStatus ?? current?.Status ?? AuthorityGrantLifecycleStatus.Unknown,
            usesCandidate ? binding ?? current?.Binding ?? Binding() : null,
            usesCandidate ? ceiling ?? current?.RequestedCeiling ?? Ceiling() : null,
            usesCandidate ? boundary ?? current?.Boundary ?? Boundary() : null,
            Actor(),
            Purpose(),
            string.Empty);
        return AuthorityGrantMutationRequestHash.Apply(request);
    }

    internal static AuthorityGrant Grant(
        AuthorityGrantLifecycleStatus status = AuthorityGrantLifecycleStatus.Active,
        int revision = 1,
        AuthorityGrant? predecessor = null,
        AuthorityGrantBinding? binding = null,
        AuthorityCeiling? ceiling = null,
        AuthorityGrantBoundary? boundary = null,
        DateTimeOffset? recordedAtUtc = null)
    {
        var grant = new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            predecessor?.GrantId ?? GrantId(),
            GrantRevision(revision),
            predecessor?.Revision,
            predecessor?.ContentHash,
            status,
            binding ?? predecessor?.Binding ?? Binding(),
            ceiling ?? predecessor?.RequestedCeiling ?? Ceiling(),
            boundary ?? predecessor?.Boundary ?? Boundary(),
            Actor(),
            Purpose(),
            recordedAtUtc ?? Now,
            string.Empty);
        return AuthorityGrantHash.Apply(grant);
    }

    internal static AuthorityGrantOperationEvidence CommittedEvidence(AuthorityGrant grant, string operationId = "grant-operation-1", string? requestHash = null)
        => new(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            operationId,
            requestHash ?? Hash64('1'),
            grant.Revision.Value == 1 ? AuthorityGrantOperationKind.Create : AuthorityGrantOperationKind.Narrow,
            AuthorityGrantOperationOutcome.Committed,
            AuthorityGrantOperationFailureCode.None,
            grant.GrantId,
            grant.Revision.Value - 1L,
            new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            grant.ChangedByActorId,
            grant.Reason,
            Hash64('2'),
            Hash64('3'),
            grant.RecordedAtUtc);

    internal static AuthorityGrantStoreSnapshot Snapshot(AuthorityGrant grant, params AuthorityGrantOperationEvidence[] additionalOperations)
    {
        var revisions = new List<AuthorityGrant>();
        var operations = new List<AuthorityGrantOperationEvidence>();
        if (grant.Revision.Value == 1)
        {
            revisions.Add(grant);
            operations.Add(CommittedEvidence(grant));
        }
        else
        {
            throw new ArgumentException("Use Snapshot(IEnumerable) for multi-revision fixtures.", nameof(grant));
        }

        operations.AddRange(additionalOperations);
        return new AuthorityGrantStoreSnapshot(grant, revisions, operations);
    }

    internal static AuthorityGrantStoreSnapshot Snapshot(IReadOnlyList<AuthorityGrant> revisions, IReadOnlyList<AuthorityGrantOperationEvidence> operations)
        => new(revisions[^1], revisions, operations);

    internal static AuthorityGrantBinding Binding()
    {
        var profile = Profile();
        var profileHash = ProfileHash(profile);
        var role = Role();
        var loop = LoopPin();
        return new AuthorityGrantBinding(
            new AuthorityGrantProfilePin(new AuthorityProfileReference(profile.ProfileId, profile.Revision), profileHash),
            new AuthorityGrantRolePin(role.Identity, role.ContentHash),
            loop);
    }

    internal static AuthorityCeiling Ceiling(
        IReadOnlyList<CapabilityDescriptorIdentity>? capabilities = null,
        IReadOnlyList<CapabilityDataClass>? dataClasses = null,
        int maxTargets = 2,
        CapabilitySideEffectClass sideEffect = CapabilitySideEffectClass.ReadOnly,
        bool recurrence = false,
        bool publication = false,
        bool irreversible = false)
        => new(
            capabilities ?? [Capability()],
            dataClasses ?? [DataClass()],
            maxTargets,
            sideEffect,
            recurrence,
            publication,
            irreversible);

    internal static AuthorityGrantBoundary Boundary(DateTimeOffset? effective = null, DateTimeOffset? expires = null)
        => new(effective ?? Now.AddMinutes(-1), expires ?? Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None);

    internal static AuthorityProfile Profile(
        AuthorityProfileStatus status = AuthorityProfileStatus.Active,
        int revision = 1,
        DateTimeOffset? issuedAt = null,
        DateTimeOffset? expiresAt = null,
        AuthorityCeiling? ceiling = null)
        => new(
            1,
            ProfileId(),
            ProfileRevision(revision),
            status,
            Purpose(),
            new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration),
            issuedAt ?? Now.AddHours(-1),
            expiresAt,
            ceiling ?? Ceiling(),
            []);

    internal static AuthorityProfileHash ProfileHash(AuthorityProfile profile)
    {
        Assert.True(AuthorityProfileHash.TryCompute(profile, out var hash, out var validation));
        Assert.True(validation.IsValid);
        return hash!;
    }

    internal static AuthorityProfileRecord ProfileRecord(AuthorityProfile? profile = null, bool tombstoned = false)
    {
        profile ??= Profile();
        var hash = ProfileHash(profile);
        var operationId = profile.Revision.Value == 1 ? "create-profile" : "revise-profile";
        var revision = new AuthorityProfileRevisionEvidence(profile, hash, operationId, Now.AddMinutes(-30));
        var receipt = new AuthorityProfileOperationReceipt(
            operationId,
            Hash64('4'),
            profile.Revision.Value == 1 ? AuthorityProfileMutationKind.Create : AuthorityProfileMutationKind.Revise,
            AuthorityProfileMutationStatus.Applied,
            profile.ProfileId,
            profile.Revision.Value,
            Actor(),
            Purpose(),
            revision.RecordedAtUtc);
        AuthorityProfileTombstone? tombstone = null;
        var operations = new List<AuthorityProfileOperationReceipt> { receipt };
        if (tombstoned)
        {
            tombstone = new AuthorityProfileTombstone("tombstone-profile", Actor(), Purpose(), Now.AddMinutes(-20));
            operations.Add(new AuthorityProfileOperationReceipt(
                tombstone.OperationId,
                Hash64('5'),
                AuthorityProfileMutationKind.Tombstone,
                AuthorityProfileMutationStatus.Applied,
                profile.ProfileId,
                null,
                tombstone.ActorId,
                tombstone.Reason,
                tombstone.RecordedAtUtc));
        }

        return new AuthorityProfileRecord(profile.ProfileId, profile, hash, [revision], tombstone, operations);
    }

    internal static ContextualRoleRevision Role(
        ContextualRoleStatus status = ContextualRoleStatus.Published,
        IReadOnlyList<string>? capabilityIds = null,
        string roleId = "bounded-helper",
        int revision = 1)
    {
        var role = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity(roleId, revision),
            string.Empty,
            "Bounded helper",
            "Performs bounded governed-loop work.",
            status,
            new ContextualRoleProvenance("user-owner", Now.AddHours(-2), Now.AddHours(-1)),
            new ContextualRoleWorkspaceApplicability(["workspace-one"]),
            new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, "bounded-helper-source", ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima((capabilityIds ?? [Capability().Id.Value]).ToImmutableArray()));
        return ContextualRoleRevisionContentHash.Apply(role);
    }

    internal static ContextualRoleLifecycleSnapshot RoleLifecycle(ContextualRoleRevision? role = null, ContextualRoleLifecycleState state = ContextualRoleLifecycleState.Active)
    {
        role ??= Role();
        return new ContextualRoleLifecycleSnapshot(1, role.Identity.RoleId, role.Identity, state, "publish-role", ContextualRoleRevisionMutationKind.Create, Now.AddMinutes(-10));
    }

    internal static GovernedLoopRevisionPublicationPin LoopPin()
    {
        var reference = GovernedLoopRevisionReference.Create(1, "governed-loop", "revision-1", Hash64('6'));
        return GovernedLoopRevisionPublicationPinFactory.Create(1, reference, "publish-loop", Hash64('7'));
    }

    internal static GovernedLoopRevisionArtifact LoopArtifact(GovernedLoopRevisionPublicationPin? pin = null)
    {
        pin ??= LoopPin();
        return GovernedLoopRevisionArtifactFactory.Create(1, pin.Revision, null, null, "create-loop", "user-owner", Now.AddHours(-1));
    }

    internal static GovernedLoopPublishedRevisionResolution PublishedLoop(GovernedLoopRevisionPublicationPin? pin = null)
    {
        pin ??= LoopPin();
        return new GovernedLoopPublishedRevisionResolution(
            GovernedLoopPublishedRevisionResolutionStatus.Active,
            pin,
            LoopArtifact(pin),
            GovernedLoopRevisionLifecycleStatus.Published,
            2,
            "publish-loop");
    }

    internal static AuthorityGrantId GrantId(string value = "workspace-helper")
    {
        Assert.True(AuthorityGrantId.TryParse(value, out var result, out var error), error?.ToString());
        return result!;
    }

    internal static AuthorityGrantRevision GrantRevision(int value)
    {
        Assert.True(AuthorityGrantRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var result, out var error), error?.ToString());
        return result!;
    }

    internal static AuthorityActorId Actor(string value = "user-owner")
    {
        Assert.True(AuthorityActorId.TryParse(value, out var result, out var error), error?.ToString());
        return result!;
    }

    internal static AuthorityPurpose Purpose(string value = "Delegate bounded work for one governed loop revision.")
    {
        Assert.True(AuthorityPurpose.TryParse(value, out var result, out var error), error?.ToString());
        return result!;
    }

    internal static AuthorityProfileId ProfileId(string value = "default-profile")
    {
        Assert.True(AuthorityProfileId.TryParse(value, out var result, out var error), error?.ToString());
        return result!;
    }

    internal static AuthorityProfileRevision ProfileRevision(int value)
    {
        Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var result, out var error), error?.ToString());
        return result!;
    }

    internal static CapabilityDescriptorIdentity Capability(string id = "org.embodysense/workspace/read", string version = "1.0.0", char hash = '8')
    {
        Assert.True(CapabilityId.TryParse(id, out var capabilityId, out var idError), idError?.Message);
        Assert.True(CapabilityVersion.TryParse(version, out var capabilityVersion, out var versionError), versionError?.Message);
        Assert.True(CapabilityDescriptorHash.TryParse($"sha256:{new string(hash, 64)}", out var descriptorHash, out var hashError), hashError?.Message);
        return new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!);
    }

    internal static CapabilityDataClass DataClass(string value = "workspace-content")
    {
        Assert.True(CapabilityDataClass.TryParse(value, out var result, out var error), error?.Message);
        return result!;
    }

    internal static string Hash64(char value) => new(value, 64);
}
