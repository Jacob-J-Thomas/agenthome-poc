namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsWait
{
    [Fact]
    public Task Production_runtime_parks_and_wakes_a_canonical_wait_after_restart() => GovernedLoopRuntimeTests.Production_runtime_parks_and_wakes_a_canonical_wait_after_restart();

    [Fact]
    public Task Primary_host_wakes_one_due_canonical_wait_without_external_restart() => GovernedLoopRuntimeTests.Primary_host_wakes_one_due_canonical_wait_without_external_restart();

    [Fact]
    public Task Explicit_background_request_activates_once_after_late_workspace_host_reacquisition() => GovernedLoopRuntimeTests.Explicit_background_request_activates_once_after_late_workspace_host_reacquisition();
}
