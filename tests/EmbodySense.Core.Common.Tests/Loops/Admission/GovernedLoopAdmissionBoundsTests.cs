using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Admission;

public sealed class GovernedLoopAdmissionBoundsTests
{
    [Fact]
    public void Exact_identifier_and_surface_limits_are_accepted_and_limit_plus_one_is_rejected()
    {
        var maximumIdentifier = new string('a', GovernedLoopAdmissionLimits.MaxIdentifierCharacters);
        var maximumSurface = new string('b', GovernedLoopAdmissionLimits.MaxSurfaceCharacters);
        var exact = GovernedLoopAdmissionTestFixture.Intent(
            operationId: maximumIdentifier,
            surface: maximumSurface);
        var identifierOverflow = GovernedLoopAdmissionTestFixture.Intent(
            operationId: maximumIdentifier + "a");
        var surfaceOverflow = GovernedLoopAdmissionTestFixture.Intent(
            surface: maximumSurface + "b");

        Assert.True(GovernedLoopAdmissionValidator.Validate(exact).IsValid);
        Assert.Contains(
            GovernedLoopAdmissionValidator.Validate(identifierOverflow).Errors,
            error => error.Code == GovernedLoopAdmissionValidationErrorCode.InvalidIdentity);
        Assert.Contains(
            GovernedLoopAdmissionValidator.Validate(surfaceOverflow).Errors,
            error => error.Code == GovernedLoopAdmissionValidationErrorCode.InvalidIdentity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("UPPER")]
    [InlineData("space separated")]
    public void Operation_and_surface_tokens_reject_noncanonical_values(string value)
    {
        Assert.False(GovernedLoopAdmissionValidator.Validate(
            GovernedLoopAdmissionTestFixture.Intent(operationId: value)).IsValid);
        Assert.False(GovernedLoopAdmissionValidator.Validate(
            GovernedLoopAdmissionTestFixture.Intent(surface: value)).IsValid);
    }

    [Fact]
    public void Evidence_reference_limit_matches_the_closed_unique_kind_domain()
    {
        var supportedKinds = Enum.GetValues<GovernedLoopAdmissionEvidenceKind>()
            .Where(value => value != GovernedLoopAdmissionEvidenceKind.Unknown)
            .ToArray();

        Assert.Equal(GovernedLoopAdmissionLimits.MaxEvidenceReferences, supportedKinds.Length);

        var intent = GovernedLoopAdmissionTestFixture.Intent();
        var exact = GovernedLoopAdmissionTestFixture.Evidence(intent);
        var overflow = new GovernedLoopAdmissionEvidence(
            exact.SchemaVersion,
            exact.IntentHash,
            exact.Binding,
            exact.GrantProfile,
            exact.GrantBoundary,
            exact.GrantDependencyEvidenceHash,
            exact.EffectiveAuthority,
            exact.CapabilityAdmission,
            [.. exact.References, GovernedLoopAdmissionTestFixture.Reference(supportedKinds[^1], 'f')],
            exact.EvaluatedAtUtc,
            string.Empty);

        Assert.Equal(supportedKinds.Length, exact.References.Count);
        Assert.True(GovernedLoopAdmissionValidator.Validate(exact, intent).IsValid);
        Assert.False(GovernedLoopAdmissionValidator.Validate(overflow, intent).IsValid);
    }

    [Fact]
    public void Validation_results_are_bounded_read_only_and_do_not_retain_hostile_values()
    {
        const string SecretCanary = "secret-canary-value-that-must-not-survive";
        var hostile = GovernedLoopAdmissionTestFixture.Intent(
            operationId: SecretCanary,
            requestHash: SecretCanary,
            surface: SecretCanary,
            graphArtifactHash: SecretCanary,
            graphLayoutHash: SecretCanary);
        var result = GovernedLoopAdmissionValidator.Validate(hostile);
        var errors = result.Errors;

        Assert.NotEmpty(errors);
        Assert.True(errors.Count <= GovernedLoopAdmissionLimits.MaxValidationErrors);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopAdmissionValidationError>)errors).Clear());
        Assert.DoesNotContain(SecretCanary, string.Join('|', errors.Select(error => error.Path)), StringComparison.Ordinal);
        Assert.All(errors, error => Assert.InRange(error.Path.Length, 1, GovernedLoopAdmissionLimits.MaxErrorPathCharacters));
    }

    [Fact]
    public void Canonical_hash_fields_use_exact_plain_lowercase_sha256_shape()
    {
        var valid = GovernedLoopAdmissionTestFixture.Intent();
        var malformedHashes = new[]
        {
            string.Empty,
            GovernedLoopAdmissionTestFixture.Hash('a')[..^1],
            GovernedLoopAdmissionTestFixture.Hash('a') + "a",
            GovernedLoopAdmissionTestFixture.Hash('A'),
            "sha256:" + GovernedLoopAdmissionTestFixture.Hash('a')
        };

        Assert.All(
            malformedHashes,
            hash =>
            {
                Assert.False(GovernedLoopAdmissionValidator.Validate(valid with { RequestHash = hash }).IsValid);
                Assert.False(GovernedLoopAdmissionValidator.Validate(valid with { GraphArtifactHash = hash }).IsValid);
                Assert.False(GovernedLoopAdmissionValidator.Validate(valid with { GraphLayoutHash = hash }).IsValid);
            });
    }
}
