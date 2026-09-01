namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

[Collection(LoopRuntimeIntegrationCollection.Name)]
public sealed class GovernedLoopRuntimeTestsPublicFacadeCoverage
{
    [Fact]
    public Task Public_governed_invocation_maps_invalid_and_missing_revision_reads_without_dispatch() => GovernedLoopRuntimeTests.Public_governed_invocation_maps_invalid_and_missing_revision_reads_without_dispatch();

    [Fact]
    public Task Public_governed_invocation_maps_corrupt_revision_artifacts_to_invalid_without_dispatch() => GovernedLoopRuntimeTests.Public_governed_invocation_maps_corrupt_revision_artifacts_to_invalid_without_dispatch();

    [Fact]
    public Task Public_governed_invocation_honors_cancellation_before_graph_read() => GovernedLoopRuntimeTests.Public_governed_invocation_honors_cancellation_before_graph_read();

    [Fact]
    public Task Public_governed_invocation_reports_host_unavailable_when_startup_cannot_acquire_execution_ownership() => GovernedLoopRuntimeTests.Public_governed_invocation_reports_host_unavailable_when_startup_cannot_acquire_execution_ownership();

    [Fact]
    public Task Public_scheduled_governed_invocation_maps_host_unavailable_after_preparing_the_exact_delivery() => GovernedLoopRuntimeTests.Public_scheduled_governed_invocation_maps_host_unavailable_after_preparing_the_exact_delivery();

    [Fact]
    public Task Public_scheduled_governed_invocation_rejects_a_trigger_role_that_does_not_own_the_graph() => GovernedLoopRuntimeTests.Public_scheduled_governed_invocation_rejects_a_trigger_role_that_does_not_own_the_graph();

    [Fact]
    public Task Public_restarted_runtime_fails_closed_when_a_retained_running_run_is_unreadable() => GovernedLoopRuntimeTests.Public_restarted_runtime_fails_closed_when_a_retained_running_run_is_unreadable();

    [Fact]
    public Task Public_governed_invocation_rejects_a_reused_operation_with_a_different_grant() => GovernedLoopRuntimeTests.Public_governed_invocation_rejects_a_reused_operation_with_a_different_grant();

    [Fact]
    public Task Public_governed_invocation_fails_closed_when_the_receipt_artifact_is_corrupt() => GovernedLoopRuntimeTests.Public_governed_invocation_fails_closed_when_the_receipt_artifact_is_corrupt();

    [Fact]
    public Task Public_governed_invocation_maps_a_receipt_write_lock_failure_to_unavailable() => GovernedLoopRuntimeTests.Public_governed_invocation_maps_a_receipt_write_lock_failure_to_unavailable();

    [Fact]
    public Task Public_governed_invocation_keeps_a_running_run_busy_when_its_durable_artifact_is_temporarily_unreadable() => GovernedLoopRuntimeTests.Public_governed_invocation_keeps_a_running_run_busy_when_its_durable_artifact_is_temporarily_unreadable();
}
