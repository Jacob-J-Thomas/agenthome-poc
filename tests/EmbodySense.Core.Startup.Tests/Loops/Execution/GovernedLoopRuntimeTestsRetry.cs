namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsRetry
{
    [Fact]
    public Task Production_runtime_parks_recovers_and_retries_one_exact_pretransport_failure()
        => GovernedLoopRuntimeTests.Production_runtime_parks_recovers_and_retries_one_exact_pretransport_failure();
}
