using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Governance.Authority.Grants;

public sealed class AuthorityGrantMutationContractTests
{
    [Fact]
    public void Canonical_hash_is_deterministic_order_independent_and_binds_exact_intent()
    {
        var firstCapability = AuthorityGrantApplicationTestFixture.Capability("org.embodysense/workspace/read", hash: '8');
        var secondCapability = AuthorityGrantApplicationTestFixture.Capability("org.embodysense/workspace/list", hash: '9');
        var first = AuthorityGrantApplicationTestFixture.Request(
            ceiling: AuthorityGrantApplicationTestFixture.Ceiling(
                [firstCapability, secondCapability],
                [AuthorityGrantApplicationTestFixture.DataClass("workspace-content"), AuthorityGrantApplicationTestFixture.DataClass("workspace-metadata")]));
        var reordered = first with
        {
            CandidateCeiling = AuthorityGrantApplicationTestFixture.Ceiling(
                [secondCapability, firstCapability],
                [AuthorityGrantApplicationTestFixture.DataClass("workspace-metadata"), AuthorityGrantApplicationTestFixture.DataClass("workspace-content")]),
            RequestHash = string.Empty,
        };
        reordered = AuthorityGrantMutationRequestHash.Apply(reordered);

        Assert.Equal(first.RequestHash, reordered.RequestHash);
        Assert.True(AuthorityGrantMutationRequestHash.Matches(first));
        Assert.NotEqual(first.RequestHash, AuthorityGrantMutationRequestHash.Compute(first with { OperationId = "grant-operation-2" }));
        Assert.NotEqual(first.RequestHash, AuthorityGrantMutationRequestHash.Compute(first with { ExpectedRevision = 1 }));
    }

    [Fact]
    public void Canonical_hash_rejects_null_and_hostile_nested_values()
    {
        var valid = AuthorityGrantApplicationTestFixture.Request();
        var malformedText = valid with { OperationId = "bad\ud800", RequestHash = AuthorityGrantApplicationTestFixture.Hash64('a') };
        var ceiling = valid.CandidateCeiling!;
        var nullCapabilities = valid with { CandidateCeiling = Ceiling(null!, ceiling.DataClasses, ceiling) };
        var nullDataClasses = valid with { CandidateCeiling = Ceiling(ceiling.Capabilities, null!, ceiling) };
        var nullCapability = valid with { CandidateCeiling = Ceiling([null!], ceiling.DataClasses, ceiling) };
        var nullDataClass = valid with { CandidateCeiling = Ceiling(ceiling.Capabilities, [null!], ceiling) };

        Assert.Throws<ArgumentNullException>(() => AuthorityGrantMutationRequestHash.Compute(null!));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(malformedText));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(nullCapabilities));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(nullDataClasses));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(nullCapability));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(nullDataClass));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(malformedText));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(null));
    }

    [Fact]
    public void Canonical_hash_rejects_oversized_text_and_collections_before_canonicalization()
    {
        var valid = AuthorityGrantApplicationTestFixture.Request();
        var oversizedOperation = valid with { OperationId = new string('a', 129) };
        var oversizedRole = valid with
        {
            CandidateBinding = valid.CandidateBinding! with
            {
                Role = valid.CandidateBinding.Role with
                {
                    Identity = new ContextualRoleRevisionIdentity(new string('r', 121), 1),
                },
            },
        };
        var oversizedLoopOperation = valid with
        {
            CandidateBinding = valid.CandidateBinding! with
            {
                Loop = new GovernedLoopRevisionPublicationPin(
                    1,
                    valid.CandidateBinding.Loop.Revision,
                    new string('p', 121),
                    valid.CandidateBinding.Loop.ValidationEvidenceHash),
            },
        };
        var thirtyThreeCapabilities = Enumerable.Range(0, 33)
            .Select(index => AuthorityGrantApplicationTestFixture.Capability($"org.embodysense/workspace/read-{index}"))
            .ToArray();
        var oversizedCeiling = valid with
        {
            CandidateCeiling = Ceiling(thirtyThreeCapabilities, valid.CandidateCeiling!.DataClasses, valid.CandidateCeiling),
        };
        var largeCeiling = valid with
        {
            CandidateCeiling = Ceiling(
                Enumerable.Repeat(AuthorityGrantApplicationTestFixture.Capability(), 4_096).ToArray(),
                valid.CandidateCeiling.DataClasses,
                valid.CandidateCeiling),
        };

        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(oversizedOperation));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(oversizedRole));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(oversizedLoopOperation));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(oversizedCeiling));
        Assert.Throws<ArgumentException>(() => AuthorityGrantMutationRequestHash.Compute(largeCeiling));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(oversizedOperation with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('a') }));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(oversizedRole with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('a') }));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(oversizedLoopOperation with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('a') }));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(oversizedCeiling with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('a') }));
        Assert.False(AuthorityGrantMutationRequestHash.Matches(largeCeiling with { RequestHash = AuthorityGrantApplicationTestFixture.Hash64('a') }));
    }

    [Theory]
    [InlineData(AuthorityGrantOperationKind.Create)]
    [InlineData(AuthorityGrantOperationKind.Narrow)]
    [InlineData(AuthorityGrantOperationKind.Suspend)]
    [InlineData(AuthorityGrantOperationKind.Replace)]
    [InlineData(AuthorityGrantOperationKind.Revoke)]
    [InlineData(AuthorityGrantOperationKind.Expire)]
    public void Validator_accepts_every_canonical_operation_shape(AuthorityGrantOperationKind kind)
    {
        var current = kind == AuthorityGrantOperationKind.Create ? null : AuthorityGrantApplicationTestFixture.Grant();
        var request = AuthorityGrantApplicationTestFixture.Request(kind, current);

        Assert.Empty(AuthorityGrantMutationRequestValidator.Validate(request));
    }

    [Fact]
    public void Validator_returns_bounded_value_free_errors_for_malformed_shape()
    {
        var valid = AuthorityGrantApplicationTestFixture.Request();
        var malformed = valid with
        {
            SchemaVersion = 99,
            OperationId = ".unsafe.",
            Kind = AuthorityGrantOperationKind.Unknown,
            GrantId = null!,
            ExpectedRevision = -1,
            ExpectedStatus = AuthorityGrantLifecycleStatus.Active,
            CandidateBinding = null,
            ActorId = null!,
            Reason = null!,
            RequestHash = "secret-material",
        };

        var errors = AuthorityGrantMutationRequestValidator.Validate(malformed);

        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.UnsupportedSchemaVersion);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidOperationId);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidOperationKind);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidGrantId);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidExpectedRevision);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidCandidate);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidActor);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidReason);
        Assert.Contains(errors, error => error.Code == AuthorityGrantMutationValidationErrorCode.RequestHashMismatch);
        Assert.All(errors, error => Assert.DoesNotContain("secret", error.Path, StringComparison.OrdinalIgnoreCase));
        Assert.InRange(errors.Count, 1, 32);
        Assert.Single(AuthorityGrantMutationRequestValidator.Validate(null));
    }

    [Fact]
    public void Validator_rejects_candidate_fields_on_non_candidate_operations_and_changed_hashes()
    {
        var current = AuthorityGrantApplicationTestFixture.Grant();
        var suspend = AuthorityGrantApplicationTestFixture.Request(AuthorityGrantOperationKind.Suspend, current);
        var withCandidate = suspend with { CandidateBinding = current.Binding };
        withCandidate = AuthorityGrantMutationRequestHash.Apply(withCandidate);
        var changedIntent = suspend with { Reason = AuthorityGrantApplicationTestFixture.Purpose("A different bounded reason.") };

        Assert.Contains(AuthorityGrantMutationRequestValidator.Validate(withCandidate), error => error.Code == AuthorityGrantMutationValidationErrorCode.InvalidCandidate);
        Assert.Contains(AuthorityGrantMutationRequestValidator.Validate(changedIntent), error => error.Code == AuthorityGrantMutationValidationErrorCode.RequestHashMismatch);
    }

    private static AuthorityCeiling Ceiling(
        IReadOnlyList<CapabilityDescriptorIdentity> capabilities,
        IReadOnlyList<CapabilityDataClass> dataClasses,
        AuthorityCeiling source)
        => new(
            capabilities,
            dataClasses,
            source.MaxTargetCount,
            source.MaxSideEffectClass,
            source.AllowsRecurrence,
            source.AllowsExternalPublication,
            source.AllowsIrreversibleAction);
}
