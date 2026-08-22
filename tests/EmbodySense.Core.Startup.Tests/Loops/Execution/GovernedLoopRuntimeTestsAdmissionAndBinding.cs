namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsAdmissionAndBinding
{
    [Fact]
    public void Public_frontier_node_contract_preserves_legacy_json_and_round_trips_topology_evidence() => GovernedLoopRuntimeTests.Public_frontier_node_contract_preserves_legacy_json_and_round_trips_topology_evidence();

    [Fact]
    public Task Public_runtime_executes_exact_canonical_inputs_and_terminal_replay_precedes_workspace_busy_and_restart() => GovernedLoopRuntimeTests.Public_runtime_executes_exact_canonical_inputs_and_terminal_replay_precedes_workspace_busy_and_restart();

    [Fact]
    public Task Public_runtime_marks_the_unforwardable_configured_profile_unavailable_before_provider_dispatch() => GovernedLoopRuntimeTests.Public_runtime_marks_the_unforwardable_configured_profile_unavailable_before_provider_dispatch();

    [Fact]
    public Task Definitive_authority_rejection_replays_after_restart_without_materialization_or_provider_work() => GovernedLoopRuntimeTests.Definitive_authority_rejection_replays_after_restart_without_materialization_or_provider_work();

    [Fact]
    public Task Begin_before_snapshot_bind_recovers_only_when_the_exact_snapshot_can_be_reproduced() => GovernedLoopRuntimeTests.Begin_before_snapshot_bind_recovers_only_when_the_exact_snapshot_can_be_reproduced();

    [Fact]
    public Task Begin_before_snapshot_bind_conflicts_when_context_changes_and_does_zero_provider_work() => GovernedLoopRuntimeTests.Begin_before_snapshot_bind_conflicts_when_context_changes_and_does_zero_provider_work();

    [Fact]
    public Task Public_absent_exact_grant_is_rejected_without_provider_or_tool_effects() => GovernedLoopRuntimeTests.Public_absent_exact_grant_is_rejected_without_provider_or_tool_effects();
}
