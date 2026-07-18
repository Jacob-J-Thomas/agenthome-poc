using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed class CustomLoopLifecycleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Restart_recovery_applies_the_exact_nonterminal_matrix_without_automatic_execution()
    {
        var runs = new[]
        {
            Run("run-admitted", CustomLoopRunStatus.Admitted),
            Run("run-running-safe", CustomLoopRunStatus.Running),
            Run("run-running-open", CustomLoopRunStatus.Running, openAttempt: true),
            Run("run-pause-safe", CustomLoopRunStatus.PauseRequested),
            Run("run-cancel-safe", CustomLoopRunStatus.CancelRequested),
            Run("run-cancel-open", CustomLoopRunStatus.CancelRequested, openAttempt: true),
            Run("run-paused", CustomLoopRunStatus.Paused)
        };
        var store = new MultiRunStore(runs);
        var audit = new RecordingAuditLog();
        var recovery = new CustomLoopRecoveryService(store, audit, new FixedTimeProvider(Now.AddDays(1)));

        var results = await recovery.RecoverAsync(AuditSchema.Actors.Web);

        Assert.Equal(CustomLoopRunStatus.Paused, store["run-admitted"].Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store["run-running-safe"].Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store["run-running-open"].Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store["run-pause-safe"].Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, store["run-cancel-safe"].Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store["run-cancel-open"].Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store["run-paused"].Status);
        Assert.Equal(CustomLoopRecoveryStatus.Unchanged, results.Single(item => item.Run.Id == "run-paused").Status);
        Assert.Equal(12, audit.Events.Count(item => item.Action == AuditSchema.Actions.LoopRunLifecycle));
        Assert.Equal(6, audit.Events.Count(item => item.Outcome == AuditSchema.Outcomes.Requested));
        Assert.All(audit.Events, item => Assert.Equal(false, item.Metadata["automaticExecution"]));
        Assert.All(store.Runs.Values, item => Assert.True(CustomLoopRunValidator.Validate(item).IsValid));
    }

    [Fact]
    public async Task Restart_recovery_never_mutates_before_its_durable_intent_audit_and_a_later_retry_can_recover()
    {
        var run = Run("run-recovery-intent-audit", CustomLoopRunStatus.Running);
        var store = new MultiRunStore([run]);
        var unavailableAudit = new RecordingAuditLog { ThrowOnAppend = true };
        var firstRecovery = new CustomLoopRecoveryService(store, unavailableAudit, new FixedTimeProvider(Now.AddMinutes(1)));

        var failed = Assert.Single(await firstRecovery.RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, failed.Status);
        Assert.Equal(run, store[run.Id]);
        Assert.Contains("intent audit failed before lifecycle mutation", failed.Detail, StringComparison.Ordinal);

        var durableAudit = new RecordingAuditLog();
        var retry = Assert.Single(await new CustomLoopRecoveryService(store, durableAudit, new FixedTimeProvider(Now.AddMinutes(2))).RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Paused, retry.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store[run.Id].Status);
        Assert.Collection(
            durableAudit.Events,
            intent => Assert.Equal(AuditSchema.Outcomes.Requested, intent.Outcome),
            outcome => Assert.Equal(AuditSchema.Outcomes.Succeeded, outcome.Outcome));
    }

    [Fact]
    public async Task Restart_recovery_retains_a_durable_intent_audit_if_the_post_transition_outcome_audit_fails()
    {
        var run = Run("run-recovery-outcome-audit", CustomLoopRunStatus.Running);
        var store = new MultiRunStore([run]);
        var audit = new RecordingAuditLog { ThrowOnAppendNumber = 2 };
        var recovery = new CustomLoopRecoveryService(store, audit, new FixedTimeProvider(Now.AddMinutes(1)));

        var result = Assert.Single(await recovery.RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.Failed, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, store[run.Id].Status);
        var intent = Assert.Single(audit.Events);
        Assert.Equal(AuditSchema.Outcomes.Requested, intent.Outcome);
        Assert.Equal(run.LifecycleVersion, intent.Metadata["previousLifecycleVersion"]);
        Assert.Equal(run.LifecycleVersion + 1, intent.Metadata["lifecycleVersion"]);
        Assert.Equal(store[run.Id].Events[^1].EventId, intent.Metadata["recoveryEventId"]);
    }

    [Fact]
    public async Task Restart_recovery_excludes_downtime_from_the_execution_clock()
    {
        var run = Run("run-clock", CustomLoopRunStatus.Running, updatedAt: Now.AddSeconds(2));
        var store = new MultiRunStore([run]);
        var recovery = new CustomLoopRecoveryService(store, new RecordingAuditLog(), new FixedTimeProvider(Now.AddDays(2)));

        await recovery.RecoverAsync(AuditSchema.Actors.Web);

        Assert.Equal(CustomLoopRunStatus.Paused, store[run.Id].Status);
        Assert.Equal(2000, store[run.Id].ExecutionClock.AccumulatedRunningMilliseconds);
        Assert.Null(store[run.Id].ExecutionClock.ActiveSinceUtc);
    }

    [Fact]
    public async Task Restart_recovery_quarantines_an_unmarked_admission_and_it_can_never_resume()
    {
        var marked = Run("run-incomplete-admission", CustomLoopRunStatus.Admitted);
        var incomplete = marked with { LifecycleVersion = 1, Events = [marked.Events[0]] };
        var store = new MultiRunStore([incomplete]);
        var audit = new RecordingAuditLog();
        var recovery = new CustomLoopRecoveryService(store, audit, new FixedTimeProvider(Now.AddMinutes(1)));

        var recovered = Assert.Single(await recovery.RecoverAsync(AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopRecoveryStatus.NeedsReview, recovered.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store[incomplete.Id].Status);
        Assert.Equal("recovery_incomplete_admission_audit", store[incomplete.Id].FailureCode);
        Assert.False(CustomLoopRunValidator.HasCompleteAdmissionAudit(store[incomplete.Id]));
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, item => Assert.Equal(false, item.Metadata["admissionAuditComplete"]));

        var service = new CustomLoopLifecycleService(store, new InMemoryOperationStore(), new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), audit, new TestExecutionGate(), new FixedTimeProvider(Now.AddMinutes(2)));
        var resume = await service.ResumeAsync(new CustomLoopResumeRequest(incomplete.Id, store[incomplete.Id].LifecycleVersion, "resume-incomplete", AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopControlStatus.InvalidState, resume.Status);
    }

    [Fact]
    public async Task Explicit_resume_quarantines_a_legacy_unmarked_paused_artifact_before_gate_or_dispatch()
    {
        var marked = Run("run-unmarked-paused", CustomLoopRunStatus.Paused);
        var priorLifecycle = marked.Events[2] with { Sequence = 2 };
        var incomplete = marked with { LifecycleVersion = 2, Events = [marked.Events[0], priorLifecycle] };
        Assert.True(CustomLoopRunValidator.Validate(incomplete).IsValid);
        var store = new MultiRunStore([incomplete]);
        var executor = new NoopResumeExecutor(CustomLoopOrderedRunStatus.Completed);
        var service = new CustomLoopLifecycleService(store, new InMemoryOperationStore(), executor, new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate(), new FixedTimeProvider(Now.AddMinutes(2)));

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(incomplete.Id, incomplete.LifecycleVersion, "resume-unmarked", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store[incomplete.Id].Status);
        Assert.Contains("integrity-incomplete", store[incomplete.Id].FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_pause_and_cancel_compare_and_swap_has_exactly_one_winner()
    {
        var run = Run("run-cas", CustomLoopRunStatus.Running);
        var store = new MultiRunStore([run]);
        var operationStore = new InMemoryOperationStore();
        var executor = new NoopResumeExecutor();
        var cancellation = new RecordingCancellationSignal();
        var service = new CustomLoopLifecycleService(store, operationStore, executor, new RecordingModelAvailability(), cancellation, new RecordingAuditLog(), new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));

        var results = await Task.WhenAll(
            service.PauseAsync(new CustomLoopPauseRequest(run.Id, run.LifecycleVersion, "pause-race", AuditSchema.Actors.Web)),
            service.CancelAsync(new CustomLoopCancelRequest(run.Id, run.LifecycleVersion, "cancel-race", AuditSchema.Actors.Web)));

        Assert.Single(results, item => item.Status == CustomLoopControlStatus.Conflict);
        Assert.Single(results, item => item.Status is CustomLoopControlStatus.PauseRequested or CustomLoopControlStatus.CancelRequested);
        Assert.Equal(run.LifecycleVersion + 1, store[run.Id].LifecycleVersion);
        Assert.Contains(store[run.Id].Status, new[] { CustomLoopRunStatus.PauseRequested, CustomLoopRunStatus.CancelRequested });
    }

    [Fact]
    public async Task Control_state_matrix_handles_safe_noops_direct_cancellation_invalid_states_not_found_and_version_conflict()
    {
        var runs = new[]
        {
            Run("run-paused-control", CustomLoopRunStatus.Paused),
            Run("run-pause-requested-control", CustomLoopRunStatus.PauseRequested),
            Run("run-admitted-pause", CustomLoopRunStatus.Admitted),
            Run("run-admitted-cancel", CustomLoopRunStatus.Admitted),
            Run("run-paused-cancel", CustomLoopRunStatus.Paused),
            Run("run-cancel-requested", CustomLoopRunStatus.CancelRequested),
            Run("run-running-resume", CustomLoopRunStatus.Running),
            Run("run-version-conflict", CustomLoopRunStatus.Running)
        };
        var store = new MultiRunStore(runs);
        var operations = new InMemoryOperationStore();
        var cancellation = new RecordingCancellationSignal();
        var service = new CustomLoopLifecycleService(store, operations, new NoopResumeExecutor(), new RecordingModelAvailability(), cancellation, new RecordingAuditLog(), new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));

        var paused = await service.PauseAsync(Pause(runs[0], "pause-paused"));
        var requested = await service.PauseAsync(Pause(runs[1], "pause-requested"));
        var invalidPause = await service.PauseAsync(Pause(runs[2], "pause-admitted"));
        var admittedCancel = await service.CancelAsync(Cancel(runs[3], "cancel-admitted"));
        var pausedCancel = await service.CancelAsync(Cancel(runs[4], "cancel-paused"));
        var repeatedCancel = await service.CancelAsync(Cancel(runs[5], "cancel-repeated"));
        var invalidResume = await service.ResumeAsync(new CustomLoopResumeRequest(runs[6].Id, runs[6].LifecycleVersion, "resume-running", AuditSchema.Actors.Web));
        var conflict = await service.PauseAsync(new CustomLoopPauseRequest(runs[7].Id, runs[7].LifecycleVersion + 1, "pause-stale", AuditSchema.Actors.Web));
        var notFound = await service.PauseAsync(new CustomLoopPauseRequest("run-missing", 1, "pause-missing", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.Paused, paused.Status);
        Assert.Equal(CustomLoopControlStatus.PauseRequested, requested.Status);
        Assert.Equal(CustomLoopControlStatus.InvalidState, invalidPause.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, admittedCancel.Status);
        Assert.Equal(CustomLoopControlStatus.Cancelled, pausedCancel.Status);
        Assert.Equal(CustomLoopControlStatus.CancelRequested, repeatedCancel.Status);
        Assert.Equal(CustomLoopControlStatus.InvalidState, invalidResume.Status);
        Assert.Equal(CustomLoopControlStatus.Conflict, conflict.Status);
        Assert.Equal(CustomLoopControlStatus.NotFound, notFound.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, store[runs[3].Id].Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, store[runs[4].Id].Status);
        Assert.Contains(runs[5].Id, cancellation.RunIds);
    }

    [Fact]
    public async Task Paused_cancel_commits_terminal_state_in_one_compare_and_swap()
    {
        var run = Run("run-paused-atomic-cancel", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var cancellation = new RecordingCancellationSignal();
        var service = new CustomLoopLifecycleService(store, operations, new NoopResumeExecutor(), new RecordingModelAvailability(), cancellation, new RecordingAuditLog(), new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.CancelAsync(Cancel(run, "cancel-paused-atomic"));

        Assert.Equal(CustomLoopControlStatus.Cancelled, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, store[run.Id].Status);
        Assert.Equal(run.LifecycleVersion + 1, store[run.Id].LifecycleVersion);
        Assert.Equal(1, store.UpdateCallCount);
        Assert.Equal("cancel-paused-atomic", store[run.Id].Events[^1].EventId);
        Assert.Empty(cancellation.RunIds);
        var receipt = await operations.GetAsync("cancel-paused-atomic");
        Assert.Equal(CustomLoopControlOperationState.Complete, receipt!.State);
        Assert.Equal(CustomLoopControlStatus.Cancelled, receipt.Outcome);
    }

    [Fact]
    public async Task Cancellation_signal_failure_leaves_pending_receipt_and_retry_signals_again()
    {
        var run = Run("run-cancel-signal-retry", CustomLoopRunStatus.Running);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var cancellation = new RecordingCancellationSignal(failuresBeforeSuccess: 1);
        var service = new CustomLoopLifecycleService(store, operations, new NoopResumeExecutor(), new RecordingModelAvailability(), cancellation, new RecordingAuditLog(), new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));
        var request = Cancel(run, "cancel-signal-retry");

        var first = await service.CancelAsync(request);
        var pending = await operations.GetAsync(request.OperationId);
        var replay = await service.CancelAsync(request);

        Assert.Equal(CustomLoopControlStatus.Failed, first.Status);
        Assert.Contains(nameof(IOException), first.Detail, StringComparison.Ordinal);
        Assert.Equal(CustomLoopRunStatus.CancelRequested, store[run.Id].Status);
        Assert.Equal(CustomLoopControlOperationState.Pending, pending!.State);
        Assert.Equal(CustomLoopControlStatus.Replayed, replay.Status);
        Assert.Equal(2, cancellation.AttemptCount);
        Assert.Equal([run.Id], cancellation.RunIds);
        var completed = await operations.GetAsync(request.OperationId);
        Assert.Equal(CustomLoopControlOperationState.Complete, completed!.State);
        Assert.Equal(CustomLoopControlStatus.CancelRequested, completed.Outcome);
    }

    [Fact]
    public async Task Pending_receipt_recovers_by_operation_event_id_without_repeating_the_transition()
    {
        const string operationId = "pause-receipt-recovery";
        var seed = Run("run-receipt", CustomLoopRunStatus.PauseRequested);
        var run = seed with { Events = [seed.Events[0], seed.Events[1] with { EventId = operationId }] };
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var pending = Pending(CustomLoopControlKind.Pause, run.Id, run.LifecycleVersion - 1, operationId, AuditSchema.Actors.Web);
        await operations.BeginAsync(pending);
        var audit = new RecordingAuditLog();
        var service = new CustomLoopLifecycleService(store, operations, new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), audit, new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.PauseAsync(new CustomLoopPauseRequest(run.Id, pending.ExpectedLifecycleVersion, operationId, pending.Actor));

        Assert.Equal(CustomLoopControlStatus.Replayed, result.Status);
        Assert.Equal(run.LifecycleVersion, store[run.Id].LifecycleVersion);
        Assert.True((await operations.GetAsync(operationId))!.OutcomeAuditRecorded);
        Assert.Contains(audit.Events, item => Equals(item.Metadata["recoveredReceipt"], true));
    }

    [Theory]
    [InlineData(CustomLoopOrderedRunStatus.Completed, CustomLoopControlStatus.Completed)]
    [InlineData(CustomLoopOrderedRunStatus.Cancelled, CustomLoopControlStatus.Cancelled)]
    [InlineData(CustomLoopOrderedRunStatus.Paused, CustomLoopControlStatus.Paused)]
    [InlineData(CustomLoopOrderedRunStatus.NeedsReview, CustomLoopControlStatus.NeedsReview)]
    [InlineData(CustomLoopOrderedRunStatus.Conflict, CustomLoopControlStatus.Conflict)]
    [InlineData(CustomLoopOrderedRunStatus.NotFound, CustomLoopControlStatus.NotFound)]
    [InlineData(CustomLoopOrderedRunStatus.InvalidState, CustomLoopControlStatus.InvalidState)]
    [InlineData(CustomLoopOrderedRunStatus.Failed, CustomLoopControlStatus.Failed)]
    public async Task Resume_maps_ordered_runner_outcomes(CustomLoopOrderedRunStatus orderedStatus, CustomLoopControlStatus expected)
    {
        var run = Run($"run-resume-{orderedStatus.ToString().ToLowerInvariant()}", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var service = new CustomLoopLifecycleService(store, new InMemoryOperationStore(), new NoopResumeExecutor(orderedStatus), new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, $"resume-{orderedStatus.ToString().ToLowerInvariant()}", AuditSchema.Actors.Web));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Resume_executor_failure_quarantines_the_durable_running_run()
    {
        var run = Run("run-resume-executor-failure", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var executor = new NoopResumeExecutor(exception: new IOException("Ordered runner failed."));
        var gate = new TestExecutionGate();
        var service = new CustomLoopLifecycleService(store, operations, executor, new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), gate, new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, "resume-executor-failure", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store[run.Id].Status);
        Assert.Equal("lifecycle_control_failed", store[run.Id].FailureCode);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
        Assert.Single(executor.Requests);
        Assert.Equal(1, gate.ReleasedLeaseCount);
        var receipt = await operations.GetAsync("resume-executor-failure");
        Assert.Equal(CustomLoopControlOperationState.Complete, receipt!.State);
        Assert.Equal(CustomLoopControlStatus.Resumed, receipt.Outcome);
    }

    [Theory]
    [InlineData("different-provider", "model")]
    [InlineData("provider", "different-model")]
    public async Task Resume_rejects_an_unavailable_admitted_provider_or_model_without_lease_transition_or_dispatch(string currentProvider, string currentModel)
    {
        var run = Run("run-resume-model-unavailable", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var executor = new NoopResumeExecutor();
        var availability = new ExactModelAvailability(new CustomLoopModelSnapshot(currentProvider, currentModel));
        var gate = new TestExecutionGate();
        var service = new CustomLoopLifecycleService(store, operations, executor, availability, new RecordingCancellationSignal(), new RecordingAuditLog(), gate, new FixedTimeProvider(Now.AddSeconds(3)));
        var request = new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, $"resume-unavailable-{currentProvider}", AuditSchema.Actors.Web);

        var result = await service.ResumeAsync(request);

        Assert.Equal(CustomLoopControlStatus.InvalidState, result.Status);
        Assert.Equal(run, result.Run);
        Assert.Equal(run, store[run.Id]);
        Assert.Equal(0, store.UpdateCallCount);
        Assert.Contains("provider/model is not available", result.Detail, StringComparison.Ordinal);
        Assert.Contains("remains Paused", result.Detail, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
        Assert.Equal(0, gate.AcquisitionCount);
        Assert.Equal([run.ModelSnapshot], availability.Requests);
        var receipt = await operations.GetAsync(request.OperationId);
        Assert.Equal(CustomLoopControlOperationState.Complete, receipt!.State);
        Assert.Equal(CustomLoopControlStatus.InvalidState, receipt.Outcome);
        Assert.Equal(run.LifecycleVersion, receipt.ResultLifecycleVersion);
        Assert.Equal(CustomLoopRunStatus.Paused, receipt.ResultRunStatus);
    }

    [Fact]
    public async Task Resume_fails_closed_when_model_availability_cannot_be_verified()
    {
        var run = Run("run-resume-model-check-failed", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var executor = new NoopResumeExecutor();
        var availability = new RecordingModelAvailability(exception: new IOException("Provider catalog unavailable."));
        var audit = new RecordingAuditLog();
        var gate = new TestExecutionGate();
        var service = new CustomLoopLifecycleService(store, new InMemoryOperationStore(), executor, availability, new RecordingCancellationSignal(), audit, gate, new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, "resume-model-check-failed", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.Failed, result.Status);
        Assert.Equal(run, result.Run);
        Assert.Equal(run, store[run.Id]);
        Assert.Equal(0, store.UpdateCallCount);
        Assert.Contains(nameof(IOException), result.Detail, StringComparison.Ordinal);
        Assert.Contains("remains Paused", result.Detail, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
        Assert.Empty(audit.Events);
        Assert.Equal(0, gate.AcquisitionCount);
    }

    [Fact]
    public async Task Resume_busy_outcome_is_durable_replayable_and_leaves_paused_run_exactly_unchanged()
    {
        var run = Run("run-resume-busy", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var executor = new NoopResumeExecutor();
        var gate = new TestExecutionGate(CustomLoopExecutionLeaseStatus.WorkspaceBusy);
        var service = new CustomLoopLifecycleService(store, operations, executor, new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), gate, new FixedTimeProvider(Now.AddSeconds(3)));
        var request = new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, "resume-busy", AuditSchema.Actors.Web);

        var first = await service.ResumeAsync(request);
        var replay = await service.ResumeAsync(request);
        var receipt = await operations.GetAsync(request.OperationId);

        Assert.Equal(CustomLoopControlStatus.WorkspaceExecutionBusy, first.Status);
        Assert.Equal(CustomLoopControlStatus.WorkspaceExecutionBusy, replay.Status);
        Assert.Equal(run, store[run.Id]);
        Assert.Equal(CustomLoopControlOperationState.Complete, receipt!.State);
        Assert.Equal(CustomLoopControlStatus.WorkspaceExecutionBusy, receipt.Outcome);
        Assert.Empty(executor.Requests);
        Assert.Equal(1, gate.AcquisitionCount);
        Assert.Equal(0, gate.ReleasedLeaseCount);
    }

    [Fact]
    public async Task Concurrent_same_resume_operation_reports_in_progress_without_completing_or_dispatching()
    {
        var run = Run("run-resume-in-progress", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var executor = new NoopResumeExecutor();
        var gate = new TestExecutionGate(CustomLoopExecutionLeaseStatus.OperationInProgress);
        var service = new CustomLoopLifecycleService(store, operations, executor, new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), gate);
        var request = new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, "resume-in-progress", AuditSchema.Actors.Web);

        var result = await service.ResumeAsync(request);

        Assert.Equal(CustomLoopControlStatus.OperationInProgress, result.Status);
        Assert.Equal(run, store[run.Id]);
        Assert.Equal(CustomLoopControlOperationState.Pending, (await operations.GetAsync(request.OperationId))!.State);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Resume_audit_failure_parks_the_undispatched_running_transition_before_releasing_ownership()
    {
        var run = Run("run-resume-audit-failure", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var executor = new NoopResumeExecutor();
        var gate = new TestExecutionGate();
        var audit = new RecordingAuditLog { ThrowOnAppend = true };
        var service = new CustomLoopLifecycleService(store, new InMemoryOperationStore(), executor, new RecordingModelAvailability(), new RecordingCancellationSignal(), audit, gate, new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, "resume-audit-failure", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, result.Run!.Status);
        Assert.Equal(run.LifecycleVersion + 2, result.Run.LifecycleVersion);
        Assert.Equal(result.Run, store[run.Id]);
        Assert.Empty(executor.Requests);
        Assert.Equal(1, gate.AcquisitionCount);
        Assert.Equal(1, gate.ReleasedLeaseCount);
    }

    [Fact]
    public async Task Resume_receipt_failure_parks_the_undispatched_running_transition_and_never_calls_the_executor()
    {
        var run = Run("run-resume-receipt-failure", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var executor = new NoopResumeExecutor();
        var gate = new TestExecutionGate();
        var operations = new InMemoryOperationStore { ThrowOnComplete = true };
        var service = new CustomLoopLifecycleService(store, operations, executor, new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), gate, new FixedTimeProvider(Now.AddSeconds(3)));

        var result = await service.ResumeAsync(new CustomLoopResumeRequest(run.Id, run.LifecycleVersion, "resume-receipt-failure", AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopControlStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.Paused, result.Run!.Status);
        Assert.Equal(run.LifecycleVersion + 2, result.Run.LifecycleVersion);
        Assert.Contains("safe checkpoint boundary", result.Detail, StringComparison.Ordinal);
        Assert.Equal(result.Run, store[run.Id]);
        Assert.Empty(executor.Requests);
        Assert.Equal(1, gate.AcquisitionCount);
        Assert.Equal(1, gate.ReleasedLeaseCount);
        Assert.Equal(CustomLoopControlOperationState.Pending, (await operations.GetAsync("resume-receipt-failure"))!.State);
    }

    [Fact]
    public async Task Audit_failure_is_visible_without_undoing_the_durable_pause_request_and_invalid_inputs_fail_before_receipts()
    {
        var run = Run("run-audit-warning", CustomLoopRunStatus.Running);
        var store = new MultiRunStore([run]);
        var audit = new RecordingAuditLog { ThrowOnAppend = true };
        var operations = new InMemoryOperationStore();
        var service = new CustomLoopLifecycleService(store, operations, new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), audit, new TestExecutionGate(), new FixedTimeProvider(Now.AddSeconds(3)));

        var warning = await service.PauseAsync(Pause(run, "pause-audit-warning"));

        Assert.Equal(CustomLoopControlStatus.AuditWarning, warning.Status);
        Assert.Equal(CustomLoopRunStatus.PauseRequested, store[run.Id].Status);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PauseAsync(new CustomLoopPauseRequest(run.Id, 0, "invalid-version", AuditSchema.Actors.Web)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PauseAsync(new CustomLoopPauseRequest(run.Id, 1, "invalid-actor", "\u0001")));
        await Assert.ThrowsAsync<ArgumentNullException>(() => service.CancelAsync(null!));
    }

    [Fact]
    public void Constructor_rejects_missing_lifecycle_dependencies()
    {
        var run = Run("run-constructor", CustomLoopRunStatus.Paused);
        var store = new MultiRunStore([run]);
        var operations = new InMemoryOperationStore();
        var resume = new NoopResumeExecutor();
        var availability = new RecordingModelAvailability();
        var cancellation = new RecordingCancellationSignal();
        var audit = new RecordingAuditLog();

        var gate = new TestExecutionGate();
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(null!, operations, resume, availability, cancellation, audit, gate));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(store, null!, resume, availability, cancellation, audit, gate));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(store, operations, null!, availability, cancellation, audit, gate));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(store, operations, resume, null!, cancellation, audit, gate));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(store, operations, resume, availability, null!, audit, gate));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(store, operations, resume, availability, cancellation, null!, gate));
        Assert.Throws<ArgumentNullException>(() => new CustomLoopLifecycleService(store, operations, resume, availability, cancellation, audit, null!));
    }

    [Fact]
    public async Task Receipt_load_update_and_completion_failures_are_reported_without_unsafe_retries()
    {
        var run = Run("run-faults", CustomLoopRunStatus.Running);

        var beginFailure = new InMemoryOperationStore { ThrowOnBegin = true };
        var beginService = new CustomLoopLifecycleService(new MultiRunStore([run]), beginFailure, new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate());
        Assert.Equal(CustomLoopControlStatus.Failed, (await beginService.PauseAsync(Pause(run, "pause-begin-fault"))).Status);

        var loadStore = new MultiRunStore([run]) { ThrowOnGet = true };
        var loadService = new CustomLoopLifecycleService(loadStore, new InMemoryOperationStore(), new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate());
        Assert.Equal(CustomLoopControlStatus.Failed, (await loadService.PauseAsync(Pause(run, "pause-load-fault"))).Status);

        var updateStore = new MultiRunStore([run]) { ThrowOnUpdate = true };
        var updateService = new CustomLoopLifecycleService(updateStore, new InMemoryOperationStore(), new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate());
        Assert.Equal(CustomLoopControlStatus.Failed, (await updateService.PauseAsync(Pause(run, "pause-update-fault"))).Status);

        var completeStore = new MultiRunStore([run]);
        var completionFailure = new InMemoryOperationStore { ThrowOnComplete = true };
        var completeService = new CustomLoopLifecycleService(completeStore, completionFailure, new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate());
        Assert.Equal(CustomLoopControlStatus.NeedsReview, (await completeService.PauseAsync(Pause(run, "pause-complete-fault"))).Status);
        Assert.Equal(CustomLoopRunStatus.PauseRequested, completeStore[run.Id].Status);

        var invalid = run with { ExecutionClock = CustomLoopExecutionClock.NotStarted() };
        var invalidService = new CustomLoopLifecycleService(new MultiRunStore([invalid]), new InMemoryOperationStore(), new NoopResumeExecutor(), new RecordingModelAvailability(), new RecordingCancellationSignal(), new RecordingAuditLog(), new TestExecutionGate());
        Assert.Equal(CustomLoopControlStatus.InvalidState, (await invalidService.PauseAsync(Pause(invalid, "pause-invalid-run"))).Status);
    }

    private static CustomLoopPauseRequest Pause(CustomLoopRunRecord run, string operationId) => new(run.Id, run.LifecycleVersion, operationId, AuditSchema.Actors.Web);

    private static CustomLoopCancelRequest Cancel(CustomLoopRunRecord run, string operationId) => new(run.Id, run.LifecycleVersion, operationId, AuditSchema.Actors.Web);

    private static CustomLoopControlOperation Pending(CustomLoopControlKind kind, string runId, int expectedVersion, string operationId, string actor)
    {
        return new CustomLoopControlOperation(CustomLoopControlOperation.CurrentSchemaVersion, operationId, CustomLoopControlRequestHash.Compute(kind, runId, expectedVersion, operationId, actor), kind, runId, expectedVersion, actor, Now, Now, CustomLoopControlOperationState.Pending, CustomLoopControlStatus.Unknown, null, null, false, "Operation pending.");
    }

    private static CustomLoopRunRecord Run(string id, CustomLoopRunStatus status, bool openAttempt = false, DateTimeOffset? updatedAt = null)
    {
        var updated = updatedAt ?? Now.AddSeconds(2);
        var definition = Definition();
        var events = new List<CustomLoopRunEvent>
        {
            new(1, $"admitted-{id}", Now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null),
            new(2, $"admission-audit-{id}", Now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null)
        };
        if (status != CustomLoopRunStatus.Admitted)
        {
            events.Add(new CustomLoopRunEvent(3, $"lifecycle-{id}", Now.AddSeconds(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, $"Run entered {status}.", [], null, null, null, null, null, null, null, null, null, null));
        }

        if (openAttempt)
        {
            events.Add(new CustomLoopRunEvent(events.Count + 1, $"attempt-{id}", updated, CustomLoopRunEventKind.NodeAttemptStarted, 1, "step-only", 1, "Attempt started.", [], null, null, null, null, null, null, "provider", "model", "attempt-correlation", null, null, null, CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes));
        }

        var context = CustomLoopContextSnapshot.CreateEmpty(Now);
        DateTimeOffset? active = status is CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested ? Now : status == CustomLoopRunStatus.CancelRequested ? Now : null;
        var run = new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            id,
            definition.Id,
            events.Count,
            status,
            Now,
            updated,
            null,
            "web",
            new CustomLoopModelSnapshot("provider", "model"),
            $"admit-{id}",
            string.Empty,
            definition,
            "prompt",
            null,
            context,
            new CustomLoopExecutionClock(0, active),
            CustomLoopRunCheckpoint.Start(),
            events.ToArray(),
            null,
            null,
            null);
        run = CustomLoopAdmissionRequestHash.Apply(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(run).Errors));
        return run;
    }

    private static CustomLoopDefinition Definition()
    {
        var seed = CustomLoopDefinition.CreateSeed("loop-lifecycle", "role-workspace", "step-only", "create-loop", Now);
        return CustomLoopDefinitionContentHash.Apply(seed with { ContentHash = string.Empty });
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MultiRunStore(IEnumerable<CustomLoopRunRecord> runs) : ICustomLoopRunStore
    {
        private readonly object _gate = new();

        public Dictionary<string, CustomLoopRunRecord> Runs { get; } = runs.ToDictionary(item => item.Id, StringComparer.Ordinal);

        public bool ThrowOnGet { get; init; }

        public bool ThrowOnUpdate { get; init; }

        public int UpdateCallCount { get; private set; }

        public CustomLoopRunRecord this[string id] => Runs[id];

        public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
        {
            if (ThrowOnGet)
            {
                throw new IOException("Run store unavailable.");
            }

            lock (_gate)
            {
                Runs.TryGetValue(runId, out var run);
                return Task.FromResult(run);
            }
        }

        public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default) => Task.FromResult<CustomLoopRunRecord?>(null);

        public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult(Runs.Values.FirstOrDefault(item => item.LoopId == loopId && !item.IsTerminal));

        public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);

        public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>(Runs.Values.Where(item => !item.IsTerminal).ToArray());
            }
        }

        public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
        {
            UpdateCallCount++;
            if (ThrowOnUpdate)
            {
                throw new IOException("Run store unavailable.");
            }

            lock (_gate)
            {
                var current = Runs[run.Id];
                if (current.LifecycleVersion != expectedLifecycleVersion)
                {
                    return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(current, expectedLifecycleVersion));
                }

                var validation = CustomLoopRunValidator.ValidateUpdate(current, run);
                Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
                Runs[run.Id] = run;
                return Task.FromResult(CustomLoopRunStoreResult.Updated(run));
            }
        }
    }

    private sealed class InMemoryOperationStore : ICustomLoopControlOperationStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CustomLoopControlOperation> _operations = new(StringComparer.Ordinal);

        public bool ThrowOnBegin { get; init; }

        public bool ThrowOnComplete { get; init; }

        public Task<CustomLoopControlOperationStoreResult> BeginAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
        {
            if (ThrowOnBegin)
            {
                throw new IOException("Operation store unavailable.");
            }

            lock (_gate)
            {
                if (_operations.TryGetValue(operation.OperationId, out var existing))
                {
                    var status = existing.RequestHash == operation.RequestHash ? CustomLoopControlOperationStoreStatus.Replayed : CustomLoopControlOperationStoreStatus.Conflict;
                    return Task.FromResult(new CustomLoopControlOperationStoreResult(status, existing));
                }

                _operations.Add(operation.OperationId, operation);
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Created, operation));
            }
        }

        public Task<CustomLoopControlOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _operations.TryGetValue(operationId, out var operation);
                return Task.FromResult(operation);
            }
        }

        public Task<CustomLoopControlOperationStoreResult> CompleteAsync(CustomLoopControlOperation operation, CancellationToken cancellationToken = default)
        {
            if (ThrowOnComplete)
            {
                throw new IOException("Operation store unavailable.");
            }

            lock (_gate)
            {
                if (!_operations.TryGetValue(operation.OperationId, out var existing))
                {
                    return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.NotFound, null));
                }

                if (existing.RequestHash != operation.RequestHash)
                {
                    return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Conflict, existing));
                }

                _operations[operation.OperationId] = operation;
                return Task.FromResult(new CustomLoopControlOperationStoreResult(CustomLoopControlOperationStoreStatus.Completed, operation));
            }
        }
    }

    private sealed class NoopResumeExecutor(CustomLoopOrderedRunStatus status = CustomLoopOrderedRunStatus.Completed, Exception? exception = null) : ICustomLoopResumeExecutor
    {
        public List<CustomLoopResumeExecutionRequest> Requests { get; } = [];

        public Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(new CustomLoopOrderedRunResult(status, null, "Mapped ordered result."));
        }
    }

    private sealed class RecordingModelAvailability(bool available = true, Exception? exception = null) : ICustomLoopModelAvailability
    {
        public List<CustomLoopModelSnapshot> Requests { get; } = [];

        public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default)
        {
            Requests.Add(modelSnapshot);
            if (exception is not null)
            {
                throw exception;
            }

            return Task.FromResult(available);
        }
    }

    private sealed class ExactModelAvailability(CustomLoopModelSnapshot currentModel) : ICustomLoopModelAvailability
    {
        public List<CustomLoopModelSnapshot> Requests { get; } = [];

        public Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default)
        {
            Requests.Add(modelSnapshot);
            return Task.FromResult(string.Equals(currentModel.Provider, modelSnapshot.Provider, StringComparison.Ordinal)
                && string.Equals(currentModel.Model, modelSnapshot.Model, StringComparison.Ordinal));
        }
    }

    private sealed class RecordingCancellationSignal(int failuresBeforeSuccess = 0) : ICustomLoopExecutionCancellationSignal
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public List<string> RunIds { get; } = [];

        public int AttemptCount { get; private set; }

        public void CancelActiveAttempt(string runId)
        {
            AttemptCount++;
            if (_remainingFailures > 0)
            {
                _remainingFailures--;
                throw new IOException("Cancellation signal unavailable.");
            }

            RunIds.Add(runId);
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private int appendCount;

        public List<AuditEvent> Events { get; } = [];

        public bool ThrowOnAppend { get; init; }

        public int? ThrowOnAppendNumber { get; init; }

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            appendCount++;
            if (ThrowOnAppend || ThrowOnAppendNumber == appendCount)
            {
                throw new IOException("Audit unavailable.");
            }

            Events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
    }
}
