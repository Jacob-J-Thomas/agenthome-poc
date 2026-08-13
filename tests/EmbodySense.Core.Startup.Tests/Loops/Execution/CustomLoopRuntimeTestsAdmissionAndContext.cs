namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class CustomLoopRuntimeTestsAdmissionAndContext
{
    [Fact]
    public Task Context_capture_truncates_role_and_conversation_sources_only_at_valid_utf16_boundaries() => CustomLoopRuntimeTests.Context_capture_truncates_role_and_conversation_sources_only_at_valid_utf16_boundaries();

    [Fact]
    public Task Public_runtime_rejects_malformed_invocations_and_durably_replays_a_missing_loop_outcome() => CustomLoopRuntimeTests.Public_runtime_rejects_malformed_invocations_and_durably_replays_a_missing_loop_outcome();

    [Fact]
    public Task Public_runtime_preserves_unsupported_discovery_index_cleanup_guidance_during_admission() => CustomLoopRuntimeTests.Public_runtime_preserves_unsupported_discovery_index_cleanup_guidance_during_admission();

    [Fact]
    public Task Public_runtime_translates_unsupported_discovery_index_schema_for_run_list_reads() => CustomLoopRuntimeTests.Public_runtime_translates_unsupported_discovery_index_schema_for_run_list_reads();

    [Fact]
    public Task Invocation_quota_pressure_prunes_expired_completed_receipts_before_accepting_a_new_operation() => CustomLoopRuntimeTests.Invocation_quota_pressure_prunes_expired_completed_receipts_before_accepting_a_new_operation();

    [Fact]
    public Task Context_capture_bounds_selected_conversation_entries_and_aggregates_all_omissions_once() => CustomLoopRuntimeTests.Context_capture_bounds_selected_conversation_entries_and_aggregates_all_omissions_once();

    [Fact]
    public Task Public_runtime_refreshes_durable_conversation_before_custom_loop_context_capture() => CustomLoopRuntimeTests.Public_runtime_refreshes_durable_conversation_before_custom_loop_context_capture();

    [Fact]
    public Task Context_capture_rejects_local_and_durable_conversation_divergence_without_overwriting_local_state() => CustomLoopRuntimeTests.Context_capture_rejects_local_and_durable_conversation_divergence_without_overwriting_local_state();

    [Fact]
    public Task Admission_captures_bounded_labeled_role_sources_and_a_versioned_newest_conversation_snapshot() => CustomLoopRuntimeTests.Admission_captures_bounded_labeled_role_sources_and_a_versioned_newest_conversation_snapshot();

    [Fact]
    public Task Replay_of_a_valid_historical_run_without_an_invoking_conversation_reaches_admission_without_throwing_or_dispatching() => CustomLoopRuntimeTests.Replay_of_a_valid_historical_run_without_an_invoking_conversation_reaches_admission_without_throwing_or_dispatching();
}
