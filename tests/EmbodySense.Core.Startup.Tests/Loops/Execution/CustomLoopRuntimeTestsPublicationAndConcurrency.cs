namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public sealed class CustomLoopRuntimeTestsPublicationAndConcurrency
{
    [Fact]
    public Task Public_runtime_admits_executes_publishes_and_exposes_inspectable_artifacts_without_changing_default_turns() => CustomLoopRuntimeTests.Public_runtime_admits_executes_publishes_and_exposes_inspectable_artifacts_without_changing_default_turns();

    [Fact]
    public Task Conversation_publication_rejects_a_replaced_durable_conversation_with_the_same_transcript() => CustomLoopRuntimeTests.Conversation_publication_rejects_a_replaced_durable_conversation_with_the_same_transcript();

    [Fact]
    public Task Runtime_publishes_multiple_node_and_Exit_outputs_against_the_admission_prefix_plus_the_exact_durable_run_suffix() => CustomLoopRuntimeTests.Runtime_publishes_multiple_node_and_Exit_outputs_against_the_admission_prefix_plus_the_exact_durable_run_suffix();

    [Fact]
    public Task Runtime_notifies_each_verified_conversation_publication_in_durable_order() => CustomLoopRuntimeTests.Runtime_notifies_each_verified_conversation_publication_in_durable_order();

    [Fact]
    public Task Conversation_publication_rejects_matching_content_without_the_exact_publication_identity() => CustomLoopRuntimeTests.Conversation_publication_rejects_matching_content_without_the_exact_publication_identity();

    [Fact]
    public Task Conversation_publication_definitely_fails_when_the_logical_conversation_changes_after_admission() => CustomLoopRuntimeTests.Conversation_publication_definitely_fails_when_the_logical_conversation_changes_after_admission();

    [Fact]
    public Task Conversation_append_exception_is_reconciled_as_definitely_failed_when_no_append_occurred() => CustomLoopRuntimeTests.Conversation_append_exception_is_reconciled_as_definitely_failed_when_no_append_occurred();

    [Fact]
    public Task Concurrent_different_loop_is_durably_rejected_as_workspace_busy_without_context_capture_or_hidden_queueing() => CustomLoopRuntimeTests.Concurrent_different_loop_is_durably_rejected_as_workspace_busy_without_context_capture_or_hidden_queueing();

    [Fact]
    public Task Paused_run_releases_workspace_ownership_and_resume_busy_is_replayed_without_mutation_or_dispatch() => CustomLoopRuntimeTests.Paused_run_releases_workspace_ownership_and_resume_busy_is_replayed_without_mutation_or_dispatch();
}
