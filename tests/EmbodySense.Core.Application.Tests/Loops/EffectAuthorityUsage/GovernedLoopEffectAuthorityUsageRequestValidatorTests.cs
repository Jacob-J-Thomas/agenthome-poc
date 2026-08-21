using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;

namespace EmbodySense.Core.Application.Tests.Loops.EffectAuthorityUsage;

public sealed class GovernedLoopEffectAuthorityUsageRequestValidatorTests
{
    [Fact]
    public void Actuator_dispatch_requires_and_accepts_one_exact_server_owned_target_fingerprint()
    {
        Assert.True(AuthorityGrantId.TryParse("grant-1", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var revision, out _));
        var request = new GovernedLoopEffectAuthorityUsageRequest(
            GovernedLoopEffectAuthorityUsageRequest.CurrentSchemaVersion,
            new AuthorityGrantReference(grantId!, revision!, "sha256:" + new string('a', 64)),
            AuthorityGrantCompletionConstraintKind.None,
            new string('b', 64),
            "run-1",
            1,
            "action-1",
            1,
            "effect-operation-1",
            GovernedLoopEffectBoundaryKind.ActuatorDispatch,
            1,
            new string('c', 64),
            new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));

        Assert.True(GovernedLoopEffectAuthorityUsageRequestValidator.IsValid(request));
        Assert.False(GovernedLoopEffectAuthorityUsageRequestValidator.IsValid(request with { TargetFingerprint = null }));
    }
}
