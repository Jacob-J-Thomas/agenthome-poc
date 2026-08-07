using EmbodySense.Core.Application.Governance.Authority;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority;

public sealed class AuthorityPortContractTests
{
    [Fact]
    public async Task Application_ports_project_an_already_evaluated_boundary_without_granting_or_executing_authority()
    {
        var profile = Profile();
        var requestProfiles = new List<AuthorityProfile> { profile };
        Assert.True(AuthorityEvaluationRequestFactory.TryCreate(requestProfiles, _issuedAtUtc.AddSeconds(1), out var request, out var requestValidation));
        Assert.True(requestValidation.IsValid);
        requestProfiles.Clear();
        Assert.Single(request!.Profiles);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityProfile>)request.Profiles).Clear());

        var intersection = AuthorityCeilingIntersection.Evaluate(request.Profiles, request.EvaluatedAtUtc);
        Assert.True(AuthorityBoundaryProjectionFactory.TryCreate(intersection.Receipt, out var projection, out var projectionValidation));
        Assert.True(projectionValidation.IsValid);
        var evaluator = new FixedEvaluator(intersection, projection!);
        var projector = new FixedProjector(projection!);

        var result = await evaluator.EvaluateAsync(request);
        Assert.Equal(AuthorityBoundaryDecision.Direct, result.Intersection.Receipt.Decision);
        Assert.Equal(AuthorityBoundaryDecision.Direct, result.Projection.Decision);
        Assert.Equal(projection, projector.Project(intersection.Receipt));
        Assert.DoesNotContain(typeof(AuthorityEvaluationRequest).GetProperties(), property => property.Name.Contains("Grant", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Trust", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(AuthorityBoundaryProjection).GetProperties(), property => property.Name.Contains("Profile", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Purpose", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Provenance", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Capability", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Raw", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Boundary_projection_snapshots_closed_reasons()
    {
        var original = Profile();
        var profile = new AuthorityProfile(original.SchemaVersion, original.ProfileId, original.Revision, original.Status, original.Purpose, original.Provenance, original.IssuedAtUtc, original.ExpiresAtUtc, original.Ceiling, [new AuthorityBoundaryCondition(AuthorityBoundaryDecision.Review, AuthorityBoundaryReason.MandatoryReview)]);
        var intersection = AuthorityCeilingIntersection.Evaluate([profile], _issuedAtUtc.AddSeconds(1));
        Assert.True(AuthorityBoundaryProjectionFactory.TryCreate(intersection.Receipt, out var projection, out var validation));

        Assert.True(validation.IsValid);
        Assert.Single(projection!.Reasons);
        Assert.Equal(AuthorityBoundaryReason.MandatoryReview, projection.Reasons[0]);
        Assert.Equal(_issuedAtUtc.AddSeconds(1), projection.EvaluatedAtUtc);
        Assert.Throws<NotSupportedException>(() => ((IList<AuthorityBoundaryReason>)projection.Reasons).Clear());
    }

    [Fact]
    public void Public_port_models_reject_forged_or_unbounded_inputs_before_crossing_the_boundary()
    {
        var profile = Profile();
        AssertRequestRejected(null, _issuedAtUtc, AuthorityContractErrorCode.InvalidIntersectionProfiles);
        AssertRequestRejected([], _issuedAtUtc, AuthorityContractErrorCode.InvalidIntersectionProfiles);
        AssertRequestRejected(Enumerable.Repeat(profile, AuthorityContractLimits.MaxProfilesPerIntersection + 1).ToArray(), _issuedAtUtc, AuthorityContractErrorCode.InvalidIntersectionProfiles);
        AssertRequestRejected([profile, profile], _issuedAtUtc, AuthorityContractErrorCode.DuplicateProfileRevision);
        AssertRequestRejected([profile with { SchemaVersion = 2 }], _issuedAtUtc, AuthorityContractErrorCode.UnsupportedSchemaVersion);
        AssertRequestRejected([profile], _issuedAtUtc.ToOffset(TimeSpan.FromHours(1)), AuthorityContractErrorCode.InvalidEvaluationTime);

        Assert.False(AuthorityBoundaryProjectionFactory.TryCreate(null, out var projection, out var projectionValidation));
        Assert.Null(projection);
        Assert.Contains(projectionValidation.Errors, error => error.Code == AuthorityContractErrorCode.Required && error.Field == AuthorityContractField.Contract);
    }

    [Fact]
    public void Persistence_port_keeps_authority_evidence_bounded_and_application_independent_of_persistence()
    {
        var persistencePortType = typeof(IAuthorityProfileStore);
        var forbiddenReferences = persistencePortType.Assembly.GetReferencedAssemblies().Where(reference => reference.Name?.Contains("Persistence", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        IAuthorityProfileStore persistencePort = new CompileTimeAuthorityProfileStore();
        Func<string, CancellationToken, Task<AuthorityProfileReadResult>> read = persistencePort.ReadAsync;
        Func<AuthorityProfileMutation, CancellationToken, Task<AuthorityProfileMutationResult>> mutate = persistencePort.MutateAsync;

        Assert.Empty(forbiddenReferences);
        Assert.NotNull(read);
        Assert.NotNull(mutate);
        Assert.DoesNotContain(typeof(AuthorityProfileMutation).GetProperties(), property => property.Name.Contains("Grant", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Binding", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Delegat", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Target", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(AuthorityProfileOperationReceipt).GetProperties(), property => property.Name.Contains("Target", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly DateTimeOffset _issuedAtUtc = new(2026, 7, 31, 18, 30, 0, TimeSpan.Zero);

    private static AuthorityProfile Profile()
    {
        Assert.True(AuthorityProfileId.TryParse("workspace-observer", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var revision, out _));
        Assert.True(AuthorityPurpose.TryParse("Inspect bounded workspace state.", out var purpose, out _));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actorId, out _));
        Assert.True(CapabilityDescriptorIdentity.TryCreate(Descriptor(), out var identity, out _));
        Assert.True(CapabilityDataClass.TryParse("workspace-content", out var dataClass, out _));
        return new AuthorityProfile(1, profileId!, revision!, AuthorityProfileStatus.Active, purpose!, new AuthorityProvenance(actorId!, AuthorityProvenanceKind.UserDeclaration), _issuedAtUtc, null, new AuthorityCeiling([identity!], [dataClass!], 1, CapabilitySideEffectClass.ReadOnly, false, false, false), []);
    }

    private static CapabilityDescriptor Descriptor()
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/workspace/read-file", out var id, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityProviderId.TryParse("org.embodysense", out var providerId, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\"}}", out var schema, out _));
        Assert.True(CapabilityVersionRange.TryParse("[1.0.0,2.0.0)", out var range, out _));
        Assert.True(CapabilityPlatform.TryParse("windows/x64", out var platform, out _));
        return new CapabilityDescriptor(1, id!, CapabilityKind.Actuator, version!, new CapabilityImplementationIdentity(providerId!, "workspace/read-file"), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, "file:///workspace/read-file", null, null), new CapabilityCompatibility(range!, [platform!]), "Read a bounded file.", schema!, schema!, new CapabilityResourceLimits(1, 1, 1, 1), CapabilitySideEffectClass.ReadOnly, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
    }

    private static void AssertRequestRejected(IReadOnlyList<AuthorityProfile>? profiles, DateTimeOffset evaluatedAtUtc, AuthorityContractErrorCode expectedCode)
    {
        Assert.False(AuthorityEvaluationRequestFactory.TryCreate(profiles, evaluatedAtUtc, out var request, out var validation));
        Assert.Null(request);
        Assert.Contains(validation.Errors, error => error.Code == expectedCode);
    }

    private sealed class FixedEvaluator(AuthorityIntersectionResult intersection, AuthorityBoundaryProjection projection) : IAuthorityProfileEvaluator
    {
        public Task<AuthorityEvaluationResult> EvaluateAsync(AuthorityEvaluationRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AuthorityEvaluationResult(intersection, projection));
        }
    }

    private sealed class FixedProjector(AuthorityBoundaryProjection projection) : IAuthorityBoundaryDecisionProjector
    {
        public AuthorityBoundaryProjection Project(AuthorityBoundaryReceipt receipt)
        {
            return projection;
        }
    }

    private sealed class CompileTimeAuthorityProfileStore : IAuthorityProfileStore
    {
        public Task<AuthorityProfileReadResult> ReadAsync(string profileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AuthorityProfileMutationResult> MutateAsync(AuthorityProfileMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
