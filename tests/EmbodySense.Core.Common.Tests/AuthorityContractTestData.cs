using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests;

internal static class AuthorityContractTestData
{
    internal static readonly DateTimeOffset IssuedAtUtc = new(2026, 7, 31, 18, 30, 0, TimeSpan.Zero);

    internal static AuthorityProfile Profile(
        string profileId = "workspace-observer",
        string revision = "1",
        AuthorityProfileStatus status = AuthorityProfileStatus.Active,
        IReadOnlyList<CapabilityDescriptorIdentity>? capabilities = null,
        IReadOnlyList<CapabilityDataClass>? dataClasses = null,
        int maxTargetCount = 5,
        CapabilitySideEffectClass maxSideEffectClass = CapabilitySideEffectClass.ReadOnly,
        bool allowsRecurrence = false,
        bool allowsExternalPublication = false,
        bool allowsIrreversibleAction = false,
        IReadOnlyList<AuthorityBoundaryCondition>? conditions = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        return new AuthorityProfile(
            AuthorityProfile.CurrentSchemaVersion,
            ProfileId(profileId),
            Revision(revision),
            status,
            Purpose("Inspect bounded workspace state for a user-directed support task."),
            new AuthorityProvenance(ActorId("user-owner"), AuthorityProvenanceKind.UserDeclaration),
            IssuedAtUtc,
            expiresAtUtc,
            new AuthorityCeiling(capabilities ?? [Identity()], dataClasses ?? [DataClass("workspace-content")], maxTargetCount, maxSideEffectClass, allowsRecurrence, allowsExternalPublication, allowsIrreversibleAction),
            conditions ?? []);
    }

    internal static AuthorityProfileId ProfileId(string value)
    {
        Assert.True(AuthorityProfileId.TryParse(value, out var parsed, out var error), error?.ToString());
        return parsed!;
    }

    internal static AuthorityProfileRevision Revision(string value)
    {
        Assert.True(AuthorityProfileRevision.TryParse(value, out var parsed, out var error), error?.ToString());
        return parsed!;
    }

    internal static AuthorityActorId ActorId(string value)
    {
        Assert.True(AuthorityActorId.TryParse(value, out var parsed, out var error), error?.ToString());
        return parsed!;
    }

    internal static AuthorityPurpose Purpose(string value)
    {
        Assert.True(AuthorityPurpose.TryParse(value, out var parsed, out var error), error?.ToString());
        return parsed!;
    }

    internal static CapabilityDataClass DataClass(string value)
    {
        return CapabilityContractTestData.DataClass(value);
    }

    internal static CapabilityDescriptorIdentity Identity(string version = "1.2.3-beta.2+build.7")
    {
        var descriptor = CapabilityContractTestData.ValidDescriptor() with { Version = CapabilityContractTestData.Version(version) };
        Assert.True(CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out var validation), string.Join(',', validation.Errors));
        return identity!;
    }
}
