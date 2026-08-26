using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsSchedules
{
    [Fact]
    public Task Public_schedule_queues_and_executes_the_exact_canonical_graph_once_across_restart() => GovernedLoopRuntimeTests.Public_schedule_queues_and_executes_the_exact_canonical_graph_once_across_restart();

    [Fact]
    public Task Public_background_dispose_parks_a_hostile_local_provider_after_peer_handoff() => GovernedLoopRuntimeTests.Public_background_dispose_parks_a_hostile_local_provider_after_peer_handoff();

    [Theory]
    [InlineData(ScheduleOverlapPolicy.Skip, ScheduleRunAdmissionDisposition.OverlapSkipped, ScheduleRunAdmissionDisposition.OverlapSkipped)]
    [InlineData(ScheduleOverlapPolicy.DeferOne, ScheduleRunAdmissionDisposition.OverlapDeferred, ScheduleRunAdmissionDisposition.DeferredOneSuppressed)]
    [InlineData(ScheduleOverlapPolicy.Allow, ScheduleRunAdmissionDisposition.OverlapSerialized, ScheduleRunAdmissionDisposition.OverlapSerialized)]
    public Task Atomic_schedule_run_admission_closes_the_post_observation_race_for_every_overlap_policy(
        ScheduleOverlapPolicy overlap,
        ScheduleRunAdmissionDisposition secondDisposition,
        ScheduleRunAdmissionDisposition thirdDisposition) => GovernedLoopRuntimeTests.Atomic_schedule_run_admission_closes_the_post_observation_race_for_every_overlap_policy(overlap, secondDisposition, thirdDisposition);

    [Fact]
    public Task Durable_schedule_overlap_retry_runs_through_canonical_local_background_runtime() => GovernedLoopRuntimeTests.Durable_schedule_overlap_retry_runs_through_canonical_local_background_runtime();

    [Fact]
    public Task Concurrent_cross_schedule_defer_one_observations_retain_one_atomic_deferral_across_restart() => GovernedLoopRuntimeTests.Concurrent_cross_schedule_defer_one_observations_retain_one_atomic_deferral_across_restart();

    [Fact]
    public Task Worker_defers_pending_schedule_finalization_then_restart_dispatches_and_replays_exactly_once() => GovernedLoopRuntimeTests.Worker_defers_pending_schedule_finalization_then_restart_dispatches_and_replays_exactly_once();

    [Fact]
    public Task Queue_commit_before_result_persistence_cannot_be_lost_by_terminal_worker_replay_after_restart() => GovernedLoopRuntimeTests.Queue_commit_before_result_persistence_cannot_be_lost_by_terminal_worker_replay_after_restart();

    [Theory]
    [InlineData("workspace")]
    [InlineData("role")]
    public Task Substituted_trigger_context_fails_before_canonical_provider_dispatch(string mismatch) => GovernedLoopRuntimeTests.Substituted_trigger_context_fails_before_canonical_provider_dispatch(mismatch);

    [Theory]
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
