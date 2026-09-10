namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsModels
{
    [Fact]
    public Task Model_attempt_crash_windows_are_durable_and_never_redispatch_across_external_restart()
        => GovernedLoopRuntimeTests.Model_attempt_crash_windows_are_durable_and_never_redispatch_across_external_restart();

    [Fact]
    public void Model_crash_snapshot_selection_is_bound_to_the_exact_fixture_probe()
        => GovernedLoopRuntimeTests.Model_crash_snapshot_selection_is_bound_to_the_exact_fixture_probe();
}
