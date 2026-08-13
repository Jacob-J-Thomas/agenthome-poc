namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopRuntimeTestsDurabilityAndRecovery
{
    [Fact]
    public Task Completed_invocation_receipt_cannot_replay_after_the_logical_conversation_is_replaced() => CustomLoopRuntimeTests.Completed_invocation_receipt_cannot_replay_after_the_logical_conversation_is_replaced();

    [Fact]
    public Task Bound_invocation_replay_returns_a_structured_failure_when_conversation_identity_cannot_be_read() => CustomLoopRuntimeTests.Bound_invocation_replay_returns_a_structured_failure_when_conversation_identity_cannot_be_read();

    [Fact]
    public Task New_terminal_binding_returns_a_structured_failure_when_conversation_identity_cannot_be_read() => CustomLoopRuntimeTests.New_terminal_binding_returns_a_structured_failure_when_conversation_identity_cannot_be_read();

    [Fact]
    public Task Rejected_invocation_replay_preserves_structured_validation_errors() => CustomLoopRuntimeTests.Rejected_invocation_replay_preserves_structured_validation_errors();

    [Fact]
    public Task Audit_unavailable_receipt_replays_a_valid_nonterminal_run_relationship() => CustomLoopRuntimeTests.Audit_unavailable_receipt_replays_a_valid_nonterminal_run_relationship();

    [Fact]
    public Task Audit_unavailable_receipt_replays_a_valid_operation_conflict_run_relationship() => CustomLoopRuntimeTests.Audit_unavailable_receipt_replays_a_valid_operation_conflict_run_relationship();

    [Fact]
    public Task Rejected_receipt_replays_against_its_intentionally_deleted_run_tombstone() => CustomLoopRuntimeTests.Rejected_receipt_replays_against_its_intentionally_deleted_run_tombstone();

    [Fact]
    public Task Definition_read_failure_is_bound_and_replayed_without_repeating_the_failed_read() => CustomLoopRuntimeTests.Definition_read_failure_is_bound_and_replayed_without_repeating_the_failed_read();

    [Fact]
    public Task Captured_receipt_retains_its_context_binding_when_a_retried_definition_read_fails() => CustomLoopRuntimeTests.Captured_receipt_retains_its_context_binding_when_a_retried_definition_read_fails();

    [Fact]
    public Task Pending_workspace_busy_binding_completes_its_selected_outcome_after_the_workspace_becomes_free() => CustomLoopRuntimeTests.Pending_workspace_busy_binding_completes_its_selected_outcome_after_the_workspace_becomes_free();

    [Fact]
    public Task Production_factory_keeps_saved_custom_loop_invocation_on_the_legacy_runtime_without_canonical_proof() => CustomLoopRuntimeTests.Production_factory_keeps_saved_custom_loop_invocation_on_the_legacy_runtime_without_canonical_proof();

    [Fact]
    public Task Concurrent_same_operation_has_one_owner_and_replays_its_admitted_run_without_redispatch() => CustomLoopRuntimeTests.Concurrent_same_operation_has_one_owner_and_replays_its_admitted_run_without_redispatch();

    [Fact]
    public Task Restart_preserves_the_current_conversation_bound_to_a_paused_run_before_explicit_resume() => CustomLoopRuntimeTests.Restart_preserves_the_current_conversation_bound_to_a_paused_run_before_explicit_resume();
}
