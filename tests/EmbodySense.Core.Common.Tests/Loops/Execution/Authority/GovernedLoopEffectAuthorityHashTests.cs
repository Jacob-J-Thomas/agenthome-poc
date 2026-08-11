using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Execution.Authority;

public sealed class GovernedLoopEffectAuthorityHashTests
{
    [Fact]
    public void Canonical_hash_is_deterministic_lowercase_and_matches_in_fixed_time()
    {
        var decision = GovernedLoopEffectAuthorityTestFixture.Decision();
        var computed = GovernedLoopEffectAuthorityContractHash.Compute(decision);

        Assert.Equal(computed, GovernedLoopEffectAuthorityContractHash.ComputeDecisionHash(decision));
        Assert.Equal(computed, GovernedLoopEffectAuthorityContractHash.Compute(decision with { ContentHash = GovernedLoopEffectAuthorityTestFixture.Hash('f') }));
        Assert.Equal(GovernedLoopEffectAuthorityContractLimits.Sha256HexCharacters, computed.Length);
        Assert.All(computed, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.True(GovernedLoopEffectAuthorityContractHash.Matches(decision));
        Assert.False(GovernedLoopEffectAuthorityContractHash.Matches(null));
        Assert.False(GovernedLoopEffectAuthorityContractHash.Matches(decision with { ContentHash = GovernedLoopEffectAuthorityTestFixture.Hash('F') }));
        Assert.Contains(
            GovernedLoopEffectAuthorityContractValidator.Validate(decision with { ContentHash = GovernedLoopEffectAuthorityTestFixture.Hash('f') }).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.HashMismatch);
    }

    [Fact]
    public void Hash_binds_identity_boundary_proofs_required_scope_disposition_reason_and_time()
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision();
        var variants = new[]
        {
            valid with { RunId = "run-2" },
            valid with { ExecutionGeneration = 2 },
            valid with { NodeId = "inference-2" },
            valid with { NodeAttempt = 2 },
            valid with { EffectOperationId = "effect-operation-2" },
            valid with { CorrelationId = "provider-request-2" },
            valid with { BoundaryKind = GovernedLoopEffectBoundaryKind.ConversationPublication },
            valid with { AdmissionReceiptHash = GovernedLoopEffectAuthorityTestFixture.Hash('b') },
            valid with { EvaluatedAtUtc = valid.EvaluatedAtUtc.AddTicks(1) }
        };

        Assert.All(
            variants,
            variant => Assert.NotEqual(
                GovernedLoopEffectAuthorityContractHash.Compute(valid),
                GovernedLoopEffectAuthorityContractHash.Compute(variant)));

        var paused = GovernedLoopEffectAuthorityTestFixture.Decision(
            omitCurrent: true,
            disposition: GovernedLoopEffectAuthorityDisposition.Pause,
            reason: GovernedLoopEffectAuthorityReason.GrantUnavailable);
        var denied = GovernedLoopEffectAuthorityTestFixture.Decision(
            omitCurrent: true,
            disposition: GovernedLoopEffectAuthorityDisposition.Deny,
            reason: GovernedLoopEffectAuthorityReason.GrantMissing);
        Assert.NotEqual(GovernedLoopEffectAuthorityContractHash.Compute(valid), GovernedLoopEffectAuthorityContractHash.Compute(paused));
        Assert.NotEqual(GovernedLoopEffectAuthorityContractHash.Compute(paused), GovernedLoopEffectAuthorityContractHash.Compute(denied));
    }

    [Fact]
    public void Hash_is_order_independent_for_semantic_capability_sets_and_rejects_tampering()
    {
        var valid = GovernedLoopEffectAuthorityTestFixture.Decision();
        var secondPin = valid.RequiredCapabilityPins[0] with { SafeDescription = "A second exact description." };
        var duplicatedIdProof = GovernedLoopEffectAuthorityTestFixture.CopyProof(
            valid.AdmittedAuthority,
            pins: [valid.RequiredCapabilityPins[0], secondPin]);

        Assert.False(GovernedLoopEffectAuthorityContractValidator.Validate(duplicatedIdProof).IsValid);
        Assert.False(GovernedLoopEffectAuthorityContractHash.Matches(valid with { CorrelationId = "provider-request-2" }));
        Assert.False(GovernedLoopEffectAuthorityContractHash.Matches(valid with { Reason = GovernedLoopEffectAuthorityReason.GrantMissing }));
        Assert.Throws<ArgumentException>(() => GovernedLoopEffectAuthorityContractHash.Compute(valid with { Reason = GovernedLoopEffectAuthorityReason.GrantMissing }));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAuthorityContractHash.Compute(null!));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAuthorityContractHash.Apply(null!));
    }

    [Fact]
    public void Validation_errors_are_bounded_value_free_and_have_value_semantics()
    {
        var invalid = GovernedLoopEffectAuthorityTestFixture.Decision() with { RunId = "Run-1" };
        var first = Assert.Single(
            GovernedLoopEffectAuthorityContractValidator.Validate(invalid).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.InvalidIdentity);
        var second = Assert.Single(
            GovernedLoopEffectAuthorityContractValidator.Validate(invalid).Errors,
            error => error.Code == GovernedLoopEffectAuthorityValidationErrorCode.InvalidIdentity);

        Assert.Equal(first, second);
        Assert.True(first.Equals(second));
        Assert.False(first.Equals(null));
        Assert.False(first.Equals(new object()));
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal("InvalidIdentity at $.runId", first.ToString());
        Assert.DoesNotContain("Run-1", first.ToString(), StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ((IList<GovernedLoopEffectAuthorityValidationError>)GovernedLoopEffectAuthorityContractValidator.Validate(invalid).Errors).Clear());
    }
}
