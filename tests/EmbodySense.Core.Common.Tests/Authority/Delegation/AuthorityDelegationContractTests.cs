using EmbodySense.Core.Common.Authority.Delegation;
using EmbodySense.Core.Common.Authority.Delegation.Models;
using System.Globalization;

namespace EmbodySense.Core.Common.Tests.Authority.Delegation;

public sealed class AuthorityDelegationContractTests
{
    [Fact]
    public void Validate_AcceptsCompleteHashBoundEnvelope()
    {
        var result = AuthorityDelegationContractValidator.Validate(AuthorityDelegationTestFixture.Envelope());

        Assert.True(result.IsValid, string.Join(',', result.Errors));
    }

    [Theory]
    [InlineData(AuthorityDelegationTargetKind.Role)]
    [InlineData(AuthorityDelegationTargetKind.Loop)]
    [InlineData(AuthorityDelegationTargetKind.Node)]
    public void Validate_AcceptsEachExactTargetMatrix(AuthorityDelegationTargetKind kind)
    {
        var result = AuthorityDelegationContractValidator.Validate(AuthorityDelegationTestFixture.Target(kind));

        Assert.True(result.IsValid, string.Join(',', result.Errors));
    }

    [Fact]
    public void Validate_RejectsMalformedTargetMatrix()
    {
        var valid = AuthorityDelegationTestFixture.Target(AuthorityDelegationTargetKind.Node);
        var malformed = valid with { NodeId = null };

        var result = AuthorityDelegationContractValidator.Validate(malformed);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == AuthorityDelegationContractValidationErrorCode.InvalidTargetBinding);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("role.*")]
    [InlineData("Role")]
    [InlineData("role target")]
    public void Validate_RejectsWildcardOrNonCanonicalClassTokens(string invalidClass)
    {
        var envelope = AuthorityDelegationTestFixture.Envelope(targetClass: invalidClass, applyHash: false);

        var result = AuthorityDelegationContractValidator.Validate(envelope);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "$.targetClass");
    }

    [Fact]
    public void Validate_RequiresFiniteExpiryOrTargetCompletion()
    {
        var boundary = AuthorityDelegationTestFixture.Boundary(expiresAtUtc: null, completion: AuthorityDelegationCompletionConstraintKind.None);
        boundary = boundary with { ExpiresAtUtc = null };

        var result = AuthorityDelegationContractValidator.Validate(boundary);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == AuthorityDelegationContractValidationErrorCode.InvalidBoundary);
    }

    [Fact]
    public void Validate_AcceptsOrdinalCanonicalAuthorityCollectionsUnderCultureWithDifferentPunctuationOrdering()
    {
        var dataClasses = new[]
        {
            AuthorityDelegationTestFixture.DataClass("a_i"),
            AuthorityDelegationTestFixture.DataClass("a_x"),
        }.OrderBy(value => value.Value, StringComparer.Ordinal).ToArray();
        var parent = AuthorityDelegationTestFixture.Ceiling(dataClasses: dataClasses);
        var delegated = AuthorityDelegationTestFixture.Ceiling(dataClasses: dataClasses, maxTargetCount: 2, recurrence: false, externalPublication: false, irreversible: false);
        var priorCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("az");
            var result = AuthorityDelegationContractValidator.Validate(AuthorityDelegationTestFixture.Envelope(parentCeiling: parent, delegatedCeiling: delegated));

            Assert.True(result.IsValid, string.Join(',', result.Errors));
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public void Validate_RejectsBoundPlusOneEnvelopeIdentity()
    {
        var valid = AuthorityDelegationTestFixture.Envelope(applyHash: false);
        var oversized = new AuthorityDelegationEnvelope(
            valid.SchemaVersion,
            new string('a', AuthorityDelegationContractLimits.MaxIdentifierCharacters + 1),
            valid.ParentEvidence,
            valid.Target,
            valid.DelegatedCeiling,
            valid.DelegatedCapabilityPins,
            valid.TargetClass,
            valid.OperationClass,
            valid.Purpose,
            valid.Boundary,
            valid.RevocationLink,
            valid.SubsetProof,
            valid.IssuedAtUtc,
            string.Empty);

        var result = AuthorityDelegationContractValidator.Validate(oversized);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Path == "$.envelopeId");
    }

    [Fact]
    public void Validate_RejectsChangedRevocationParentLink()
    {
        var valid = AuthorityDelegationTestFixture.Envelope();
        var changedLink = AuthorityDelegationContractHash.Apply(valid.RevocationLink with { ParentRunId = "other-run", LinkageHash = string.Empty });
        var changed = new AuthorityDelegationEnvelope(
            valid.SchemaVersion,
            valid.EnvelopeId,
            valid.ParentEvidence,
            valid.Target,
            valid.DelegatedCeiling,
            valid.DelegatedCapabilityPins,
            valid.TargetClass,
            valid.OperationClass,
            valid.Purpose,
            valid.Boundary,
            changedLink,
            valid.SubsetProof,
            valid.IssuedAtUtc,
            string.Empty);

        var result = AuthorityDelegationContractValidator.Validate(changed);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == AuthorityDelegationContractValidationErrorCode.ParentLinkMismatch);
    }
}
