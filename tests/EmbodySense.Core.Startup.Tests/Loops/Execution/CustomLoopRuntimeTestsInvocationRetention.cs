namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(IndependentLoopRuntimeIntegrationCollection.Name)]
public sealed class CustomLoopRuntimeTestsInvocationRetention
{
    [Fact]
    public Task Invocation_quota_pressure_prunes_expired_completed_receipts_before_accepting_a_new_operation() => CustomLoopRuntimeTests.Invocation_quota_pressure_prunes_expired_completed_receipts_before_accepting_a_new_operation();
}
