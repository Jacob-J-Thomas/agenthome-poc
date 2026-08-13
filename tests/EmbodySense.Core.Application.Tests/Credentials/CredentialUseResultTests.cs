using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

public sealed class CredentialUseResultTests
{
    [Fact]
    public void Result_exposes_exactly_one_value_free_success_or_failure_contract()
    {
        using var fixture = new LifecycleFixture();
        var evidence = new CredentialUseEvidence(
            CredentialUseEvidence.CurrentSchemaVersion,
            Id("evidence-1"),
            fixture.Reference.Id,
            CredentialContractHash.Compute("binding-v1"),
            Id("proof-1"),
            Id("run-1"),
            fixture.Binding.Scope,
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
            CredentialUseOutcome.Succeeded,
            true);
        var failure = CredentialFailure.FromCode(CredentialFailureCode.Unauthorized);

        var succeeded = CredentialUseResult.Success(evidence);
        var failed = CredentialUseResult.Failed(failure);

        Assert.True(succeeded.Succeeded);
        Assert.Same(evidence, succeeded.Evidence);
        Assert.Null(succeeded.Failure);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.Evidence);
        Assert.Same(failure, failed.Failure);
    }

    [Fact]
    public void Result_rejects_missing_evidence_or_failure()
    {
        Assert.Throws<ArgumentNullException>(() => CredentialUseResult.Success(null!));
        Assert.Throws<ArgumentNullException>(() => CredentialUseResult.Failed(null!));
    }

    private static CredentialContractId Id(string value)
    {
        Assert.True(CredentialContractId.TryParse(value, out var id, out _));
        return id!;
    }
}
