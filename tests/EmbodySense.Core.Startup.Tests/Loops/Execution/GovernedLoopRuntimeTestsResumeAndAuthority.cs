using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsResumeAndAuthority
{
    [Fact]
    public Task Public_pause_and_resume_reconstructs_the_canonical_plan_from_durable_evidence() => GovernedLoopRuntimeTests.Public_pause_and_resume_reconstructs_the_canonical_plan_from_durable_evidence();

    [Theory]
    [InlineData(AuthorityGrantOperationKind.Narrow)]
    [InlineData(AuthorityGrantOperationKind.Suspend)]
    [InlineData(AuthorityGrantOperationKind.Replace)]
    [InlineData(AuthorityGrantOperationKind.Revoke)]
    [InlineData(AuthorityGrantOperationKind.Expire)]
    public Task Public_resume_revalidates_the_exact_grant_before_the_next_provider_effect(AuthorityGrantOperationKind transition) => GovernedLoopRuntimeTests.Public_resume_revalidates_the_exact_grant_before_the_next_provider_effect(transition);
}
