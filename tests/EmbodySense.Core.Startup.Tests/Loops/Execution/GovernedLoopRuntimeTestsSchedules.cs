namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsSchedules
{
    [Fact]
    public Task Public_schedule_queues_and_executes_the_exact_canonical_graph_once_across_restart() => GovernedLoopRuntimeTests.Public_schedule_queues_and_executes_the_exact_canonical_graph_once_across_restart();

    [Fact]
    public Task Worker_defers_pending_schedule_finalization_then_restart_dispatches_and_replays_exactly_once() => GovernedLoopRuntimeTests.Worker_defers_pending_schedule_finalization_then_restart_dispatches_and_replays_exactly_once();

    [Theory]
    [InlineData("workspace")]
    [InlineData("role")]
    public Task Substituted_trigger_context_fails_before_canonical_provider_dispatch(string mismatch) => GovernedLoopRuntimeTests.Substituted_trigger_context_fails_before_canonical_provider_dispatch(mismatch);

    [Theory]
    [InlineData("forged-identity")]
    [InlineData("schedule")]
    [InlineData("occurrence")]
    [InlineData("payload")]
    [InlineData("target")]
    public Task Forged_or_swapped_schedule_provenance_fails_before_admission_across_replay_and_restart(string mismatch) => GovernedLoopRuntimeTests.Forged_or_swapped_schedule_provenance_fails_before_admission_across_replay_and_restart(mismatch);

    [Fact]
    public Task Manual_governed_invocation_rejects_the_reserved_trigger_namespace_before_admission() => GovernedLoopRuntimeTests.Manual_governed_invocation_rejects_the_reserved_trigger_namespace_before_admission();

    [Fact]
    public Task Manual_invocation_of_a_schedule_trigger_graph_fails_before_admission() => GovernedLoopRuntimeTests.Manual_invocation_of_a_schedule_trigger_graph_fails_before_admission();

    [Fact]
    public Task Schedule_delivery_to_a_manual_trigger_graph_fails_before_admission() => GovernedLoopRuntimeTests.Schedule_delivery_to_a_manual_trigger_graph_fails_before_admission();
}
