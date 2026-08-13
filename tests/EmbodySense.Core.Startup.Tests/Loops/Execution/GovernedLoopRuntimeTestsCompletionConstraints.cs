namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class GovernedLoopRuntimeTestsCompletionConstraints
{
    [Fact]
    public Task First_bound_completion_replays_after_restart_and_rejects_a_second_run_without_provider_dispatch() => GovernedLoopRuntimeTests.First_bound_completion_replays_after_restart_and_rejects_a_second_run_without_provider_dispatch();

    [Fact]
    public Task Failed_provider_attempt_does_not_consume_first_bound_completion() => GovernedLoopRuntimeTests.Failed_provider_attempt_does_not_consume_first_bound_completion();

    [Fact]
    public Task Paused_then_cancelled_run_does_not_consume_first_bound_completion() => GovernedLoopRuntimeTests.Paused_then_cancelled_run_does_not_consume_first_bound_completion();

    [Fact]
    public Task Publication_conflict_does_not_consume_first_bound_completion() => GovernedLoopRuntimeTests.Publication_conflict_does_not_consume_first_bound_completion();
}
