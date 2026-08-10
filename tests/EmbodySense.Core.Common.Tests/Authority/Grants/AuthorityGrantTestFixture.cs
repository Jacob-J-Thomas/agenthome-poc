using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Authority.Grants;

internal static class AuthorityGrantTestFixture
{
    internal static readonly DateTimeOffset RecordedAtUtc = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static AuthorityGrant Grant(
        AuthorityGrantLifecycleStatus status = AuthorityGrantLifecycleStatus.Active,
        AuthorityGrantBinding? binding = null,
        AuthorityCeiling? ceiling = null,
        AuthorityGrantBoundary? boundary = null,
        string grantId = "workspace-helper",
        string actorId = "user-owner",
        string reason = "Delegate bounded work for one governed loop revision.",
        DateTimeOffset? recordedAtUtc = null)
    {
        return Rehash(new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            Id(grantId),
            Revision(1),
            null,
            null,
            status,
            binding ?? Binding(),
            ceiling ?? Ceiling(),
            boundary ?? Boundary(),
            Actor(actorId),
            Purpose(reason),
            recordedAtUtc ?? RecordedAtUtc,
            string.Empty));
    }

    internal static AuthorityGrant Successor(
        AuthorityGrant current,
        AuthorityGrantLifecycleStatus? status = null,
        AuthorityGrantBinding? binding = null,
        AuthorityCeiling? ceiling = null,
        AuthorityGrantBoundary? boundary = null,
        DateTimeOffset? recordedAtUtc = null)
    {
        return Rehash(new AuthorityGrant(
            current.SchemaVersion,
            current.GrantId,
            Revision(current.Revision.Value + 1),
            current.Revision,
            current.ContentHash,
            status ?? current.Status,
            binding ?? current.Binding,
            ceiling ?? current.RequestedCeiling,
            boundary ?? current.Boundary,
            current.ChangedByActorId,
            current.Reason,
            recordedAtUtc ?? current.RecordedAtUtc.AddMinutes(1),
            string.Empty));
    }

    internal static AuthorityGrant Rehash(AuthorityGrant grant) => AuthorityGrantHash.Apply(grant with { ContentHash = string.Empty });

    internal static AuthorityGrantBinding Binding(
        string profileId = "default-profile",
        int profileRevision = 3,
        char profileHash = 'a',
        string roleId = "bounded-helper",
        int roleRevision = 4,
        char roleHash = 'b',
        string graphId = "governed-loop",
        string loopRevisionId = "revision-7",
        char executableHash = 'c',
        string publicationOperationId = "publish-7",
        char validationHash = 'd')
    {
        var profileReference = new AuthorityProfileReference(ProfileId(profileId), ProfileRevision(profileRevision));
        var profile = new AuthorityGrantProfilePin(profileReference, ProfileHash(profileHash));
        var role = new AuthorityGrantRolePin(new ContextualRoleRevisionIdentity(roleId, roleRevision), new string(roleHash, 64));
        var loopReference = GovernedLoopRevisionReference.Create(1, graphId, loopRevisionId, new string(executableHash, 64));
        var loop = GovernedLoopRevisionPublicationPinFactory.Create(1, loopReference, publicationOperationId, new string(validationHash, 64));
        return new AuthorityGrantBinding(profile, role, loop);
    }

    internal static AuthorityCeiling Ceiling(
        IReadOnlyList<CapabilityDescriptorIdentity>? capabilities = null,
        IReadOnlyList<CapabilityDataClass>? dataClasses = null,
        int maxTargetCount = 5,
        CapabilitySideEffectClass maxSideEffectClass = CapabilitySideEffectClass.ReadOnly,
        bool allowsRecurrence = false,
        bool allowsExternalPublication = false,
        bool allowsIrreversibleAction = false)
    {
        return new AuthorityCeiling(
            capabilities ?? [],
            dataClasses ?? [DataClass("workspace-content")],
            maxTargetCount,
            maxSideEffectClass,
            allowsRecurrence,
            allowsExternalPublication,
            allowsIrreversibleAction);
    }

    internal static AuthorityGrantBoundary Boundary(
        DateTimeOffset? effectiveAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        AuthorityGrantCompletionConstraintKind completionConstraint = AuthorityGrantCompletionConstraintKind.None)
        => new(effectiveAtUtc ?? RecordedAtUtc.AddMinutes(-5), expiresAtUtc ?? RecordedAtUtc.AddHours(1), completionConstraint);

    internal static CapabilityDescriptorIdentity Capability(string id = "org.embodysense/workspace/read-file", string version = "1.2.3", char hash = 'e')
    {
        Assert.True(CapabilityId.TryParse(id, out var capabilityId, out var idError), idError?.Message);
        Assert.True(CapabilityVersion.TryParse(version, out var capabilityVersion, out var versionError), versionError?.Message);
        Assert.True(CapabilityDescriptorHash.TryParse($"sha256:{new string(hash, 64)}", out var capabilityHash, out var hashError), hashError?.Message);
        return new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, capabilityHash!);
    }

    internal static CapabilityDataClass DataClass(string value)
    {
        Assert.True(CapabilityDataClass.TryParse(value, out var dataClass, out var error), error?.Message);
        return dataClass!;
    }

    internal static AuthorityGrantId Id(string value)
    {
        Assert.True(AuthorityGrantId.TryParse(value, out var id, out var error), error?.ToString());
        return id!;
    }

    internal static AuthorityGrantRevision Revision(int value)
    {
        Assert.True(AuthorityGrantRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out var error), error?.ToString());
        return revision!;
    }

    internal static AuthorityActorId Actor(string value)
    {
        Assert.True(AuthorityActorId.TryParse(value, out var actor, out var error), error?.ToString());
        return actor!;
    }

    internal static AuthorityPurpose Purpose(string value)
    {
        Assert.True(AuthorityPurpose.TryParse(value, out var purpose, out var error), error?.ToString());
        return purpose!;
    }

    private static AuthorityProfileId ProfileId(string value)
    {
        Assert.True(AuthorityProfileId.TryParse(value, out var id, out var error), error?.ToString());
        return id!;
    }

    private static AuthorityProfileRevision ProfileRevision(int value)
    {
        Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out var error), error?.ToString());
        return revision!;
    }

    private static AuthorityProfileHash ProfileHash(char value)
    {
        Assert.True(AuthorityProfileHash.TryParse($"sha256:{new string(value, 64)}", out var hash, out var error), error?.ToString());
        return hash!;
    }
}
