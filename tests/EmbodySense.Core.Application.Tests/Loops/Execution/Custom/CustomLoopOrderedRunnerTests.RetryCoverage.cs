using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Retry;
using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

public sealed partial class CustomLoopOrderedRunnerTests
{
    [Fact]
    public async Task Retry_public_entry_points_reject_malformed_requests_without_reading_or_dispatching()
    {
        var scenario = await CreateScheduledRetryAsync();

        var schedule = await scenario.Retry.ScheduleAsync(null);
        var continuation = await scenario.Retry.ContinueAsync(new GovernedLoopWakeContinuationRequest(
            scenario.Checkpoint,
            WakeIdentity(scenario.Checkpoint),
            "retry-continuation",
            null,
            null));
        var reconciliation = await scenario.Retry.ReconcileAsync(new GovernedLoopWakeContinuationRequest(
            scenario.Checkpoint,
            WakeIdentity(scenario.Checkpoint),
            "retry-reconciliation",
            ContinuationRequest(scenario.Checkpoint).PreparedWakeEvidence,
            null));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Conflict, schedule.Status);
        Assert.Equal("retry-request-invalid", schedule.Detail);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, continuation?.Status);
        Assert.Equal("retry-continuation-invalid", continuation?.EvidenceReference);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, reconciliation?.Status);
        Assert.Equal("retry-continuation-invalid", reconciliation?.EvidenceReference);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_schedule_replays_the_exact_retained_series_and_fails_closed_when_the_run_read_is_not_authoritative()
    {
        var scenario = await CreateScheduledRetryAsync();
        var request = RetryRequest(scenario);
        var writesBeforeReplay = scenario.Store.Writes.Count;

        var replay = await scenario.Retry.ScheduleAsync(request);

        Assert.Equal(GovernedLoopRetryExecutionStatus.Replayed, replay.Status);
        Assert.Equal("retry-schedule-replayed", replay.Detail);
        Assert.Equal(scenario.Store.Current, replay.Run);
        Assert.Equal(writesBeforeReplay, scenario.Store.Writes.Count);

        scenario.Store.ReturnMissing = true;
        var missing = await scenario.Retry.ScheduleAsync(request);

        Assert.Equal(GovernedLoopRetryExecutionStatus.Conflict, missing.Status);
        Assert.Equal("retry-run-unavailable", missing.Detail);

        scenario.Store.ReturnMissing = false;
        scenario.Store.GetException = new IOException("simulated retry read outage");
        var unavailable = await scenario.Retry.ScheduleAsync(request);

        Assert.Equal(GovernedLoopRetryExecutionStatus.Unavailable, unavailable.Status);
        Assert.Equal("retry-run-unavailable", unavailable.Detail);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_schedule_replays_an_exact_terminal_decision_without_appending_new_evidence()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.RetryPosture.AuthorityEligible = false;
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var wake = await scenario.Sleep.WakeAsync(new GovernedLoopWakeRequest(
            scenario.Scheduled.WakeCheckpointId!,
            scenario.Scheduled.WakeCheckpointHash!,
            null));
        var request = RetryRequest(scenario);
        var beforeReplay = scenario.Store.Current;
        var writesBeforeReplay = scenario.Store.Writes.Count;

        var replay = await scenario.Retry.ScheduleAsync(request);

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, wake.Status);
        Assert.Equal(GovernedLoopRetryExecutionStatus.Ineligible, replay.Status);
        Assert.Equal("retry-terminal-decision-replayed", replay.Detail);
        Assert.Equal(beforeReplay, replay.Run);
        Assert.Equal(writesBeforeReplay, scenario.Store.Writes.Count);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_terminalizes_a_recovered_failure_when_current_authority_is_no_longer_eligible()
    {
        var scenario = await CreateScheduledRetryAsync();
        var failureSnapshot = Assert.Single(scenario.Store.Writes, candidate => candidate.Events.Any(item => item.FailureEvidence is not null)
            && candidate.Events.All(item => item.RetryState is null));
        scenario.Store.ReplaceCurrent(failureSnapshot);
        scenario.RetryPosture.AuthorityEligible = false;
        var writesBeforeDecision = scenario.Store.Writes.Count;

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Ineligible, result.Status);
        Assert.Equal("current-posture-ineligible", result.Detail);
        Assert.Equal(CustomLoopRunStatus.Running, scenario.Store.Current.Status);
        var terminal = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Stopped, terminal.Disposition);
        Assert.Equal(writesBeforeDecision + 1, scenario.Store.Writes.Count);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_replays_a_durably_parked_series_when_the_store_loses_each_success_reply()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        scenario.Store.RawConflictSuccessorFactory = (_, candidate) => candidate;

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));
        var scheduled = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);

        Assert.Equal(GovernedLoopRetryExecutionStatus.Replayed, result.Status);
        Assert.Equal("retry-checkpoint-attached", result.Detail);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.NotNull(scheduled.WakeCheckpointId);
        Assert.NotNull(scheduled.WakeCheckpointHash);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_fails_closed_when_a_concurrent_writer_keeps_the_exact_failure_current()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        scenario.Store.RawConflictSuccessorFactory = (current, _) => current;

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Conflict, result.Status);
        Assert.Equal("retry-park-cas-incomplete", result.Detail);
        Assert.DoesNotContain(scenario.Store.Current.Events, item => item.RetryState is not null);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_reports_a_lost_run_when_the_park_mutation_is_not_found()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        scenario.Store.UpdateResultOverride = CustomLoopRunStoreResult.NotFound();

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Conflict, result.Status);
        Assert.Equal("retry-park-cas-incomplete", result.Detail);
        Assert.DoesNotContain(scenario.Store.Current.Events, item => item.RetryState is not null);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_reports_unavailable_when_the_park_write_and_its_reconciliation_read_are_both_unavailable()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        scenario.Store.BeforeUpdate = (_, _) =>
        {
            scenario.Store.GetException = new IOException("simulated retry reconciliation outage");
            throw new IOException("simulated retry park write outage");
        };

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Unavailable, result.Status);
        Assert.Equal("retry-park-cas-incomplete", result.Detail);
        Assert.DoesNotContain(scenario.Store.Current.Events, item => item.RetryState is not null);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_schedule_preserves_the_parked_retry_for_recovery_when_checkpoint_publication_throws()
    {
        var scenario = await CreateUnpublishedRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario.Store));
        scenario.SleepStore.PublishOverride = null;
        scenario.SleepStore.ThrowBeforePublish = true;

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario.Context, scenario.Store));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Scheduled, result.Status);
        Assert.Equal("retry-checkpoint-recovery-pending", result.Detail);
        var scheduled = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.Null(scheduled.WakeCheckpointId);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Store.Current.Status);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_keeps_the_durable_checkpoint_unattached_when_its_run_update_cannot_be_proven()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        scenario.Store.BeforeUpdate = (candidate, _) =>
        {
            var latest = candidate.Events.LastOrDefault(item => item.RetryState is not null)?.RetryState;
            if (latest is { Disposition: GovernedLoopRetryStateDisposition.Scheduled, WakeCheckpointId: not null })
            {
                throw new IOException("simulated retry checkpoint attachment outage");
            }
        };

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Scheduled, result.Status);
        Assert.Equal("retry-checkpoint-attachment-recovery-pending", result.Detail);
        var scheduled = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.Null(scheduled.WakeCheckpointId);
        Assert.Null(scheduled.WakeCheckpointHash);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_preserves_the_exact_failure_without_parking_when_current_posture_or_the_cas_boundary_is_unavailable()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        var failureVersion = scenario.Store.Current.LifecycleVersion;

        scenario.RetryPosture.Exception = new IOException("simulated retry posture outage");
        var unavailable = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));
        Assert.Equal(GovernedLoopRetryExecutionStatus.Unavailable, unavailable.Status);
        Assert.Equal("retry-posture-unavailable", unavailable.Detail);

        scenario.RetryPosture.Exception = null;
        scenario.RetryPosture.ReturnNull = true;
        var invalid = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));
        Assert.Equal(GovernedLoopRetryExecutionStatus.Unavailable, invalid.Status);
        Assert.Equal("retry-posture-invalid", invalid.Detail);

        scenario.RetryPosture.ReturnNull = false;
        scenario.Store.BeforeUpdate = (_, _) => throw new IOException("simulated retry park persistence outage");
        var ambiguous = await scenario.Retry.ScheduleAsync(RetryRequest(scenario));

        Assert.Equal(GovernedLoopRetryExecutionStatus.NeedsReview, ambiguous.Status);
        Assert.Equal("retry-park-cas-incomplete", ambiguous.Detail);
        Assert.Equal(failureVersion, scenario.Store.Current.LifecycleVersion);
        Assert.DoesNotContain(scenario.Store.Current.Events, item => item.RetryState is not null);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_rejects_an_unpublished_retained_series_without_rewriting_its_failure_evidence()
    {
        var scenario = await CreateUnpublishedRetryAsync();
        var writesBeforeReplay = scenario.Store.Writes.Count;

        var result = await scenario.Retry.ScheduleAsync(RetryRequest(scenario.Context, scenario.Store));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Conflict, result.Status);
        Assert.Equal("retry-series-already-started", result.Detail);
        Assert.Equal(writesBeforeReplay, scenario.Store.Writes.Count);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_rejects_a_hash_valid_but_unretained_failure_before_it_can_park_an_attempt()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        var retained = Assert.IsType<GovernedLoopFailureEvidence>(scenario.Store.Current.Events.Single(item => item.FailureEvidence is not null).FailureEvidence);
        var substitutedWithoutHash = retained with { Attempt = retained.Attempt + 1, ContentHash = string.Empty };
        var substituted = substitutedWithoutHash with { ContentHash = GovernedLoopFailureEvidenceContract.ComputeHash(substitutedWithoutHash) };
        var node = scenario.Context.Plan.Nodes.Single(candidate => string.Equals(candidate.NodeId, substituted.NodeId, StringComparison.Ordinal));

        var result = await scenario.Retry.ScheduleAsync(new GovernedLoopRetryExecutionRequest(
            scenario.Context.Anchor,
            scenario.Context.Plan,
            node,
            substituted,
            AuditSchema.Actors.Web));

        Assert.Equal(GovernedLoopRetryExecutionStatus.Conflict, result.Status);
        Assert.Equal("retry-failure-or-frontier-conflict", result.Detail);
        Assert.Single(scenario.Executor.Requests);
        Assert.DoesNotContain(scenario.Store.Current.Events, item => item.RetryState is not null);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_propagates_caller_cancellation_while_reading_current_posture()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        using var cancellation = new CancellationTokenSource();
        scenario.RetryPosture.BeforeRead = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Retry.ScheduleAsync(RetryRequest(scenario), cancellation.Token));

        Assert.DoesNotContain(scenario.Store.Current.Events, item => item.RetryState is not null);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_schedule_propagates_caller_cancellation_during_checkpoint_publication_after_parking_the_exact_recovery_state()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReplaceCurrent(FailureSnapshot(scenario));
        using var cancellation = new CancellationTokenSource();
        scenario.SleepStore.BeforePublish = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Retry.ScheduleAsync(RetryRequest(scenario), cancellation.Token));

        var scheduled = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.Null(scheduled.WakeCheckpointId);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Store.Current.Status);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_scheduler_exception_is_terminalized_as_review_without_advancing_the_failed_attempt()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()), artifactFactory: RetryArtifact);
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var executor = new QueueExecutor(Result("must not execute"))
        {
            BeforeProviderRequestStarted = _ => throw new InvalidOperationException("provider unavailable before transport"),
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, timeProvider: time, retryNodeExecutor: new ThrowingRetryNodeExecutor()),
            evidence,
            evidence);

        var result = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal("canonical_retry_scheduling_unproven", store.Current.FailureCode);
        Assert.Equal("retry-scheduler-unavailable", store.Current.FailureDetail);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_reentry_rejects_the_second_provider_dispatch_when_the_immutable_retry_deadline_has_elapsed()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        scenario.Store.BeforeUpdate = (candidate, _) =>
        {
            var latest = candidate.Events.LastOrDefault(item => item.RetryState is not null)?.RetryState;
            if (latest?.Disposition == GovernedLoopRetryStateDisposition.Dispatched)
            {
                scenario.Time.UtcNow = scenario.Scheduled.Identity.DeadlineUtc;
            }
        };

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, result?.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, scenario.Store.Current.Status);
        Assert.Equal("retry_deadline_exceeded", scenario.Store.Current.FailureCode);
        Assert.Equal(0, scenario.Executor.ProviderRequestStartedCount);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_replays_the_exact_dispatched_reservation_without_rewriting_durable_state()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.OrderedResume.ResumeException = new IOException("simulated ordered reentry outage");
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        var first = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var writesBeforeReplay = scenario.Store.Writes.Count;

        var replay = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, first?.Status);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, replay?.Status);
        Assert.Equal("ordered-retry-reentry-incomplete", replay?.EvidenceReference);
        Assert.Equal(writesBeforeReplay, scenario.Store.Writes.Count);
        Assert.Equal(GovernedLoopRetryStateDisposition.Dispatched, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_reconciliation_rejects_a_checkpoint_after_the_same_series_has_durably_terminalized_without_dispatch()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        scenario.RetryPosture.Budget = scenario.Scheduled.Budget with { Attempts = scenario.Scheduled.CurrentAttempt + 1 };

        var terminal = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var reconciliation = await scenario.Retry.ReconcileAsync(ReconciliationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, terminal?.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, scenario.Store.Current.Status);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, reconciliation?.Status);
        Assert.Equal("retry-checkpoint-substituted", reconciliation?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.NeedsReview, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_reports_ambiguous_when_an_exhausted_retry_cannot_commit_its_terminal_frontier()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        scenario.RetryPosture.Budget = scenario.Scheduled.Budget with { ResourceUnits = 2 };
        scenario.Store.BeforeUpdate = (_, _) => throw new IOException("simulated exhausted retry persistence outage");

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal("retry-exhaustion-cas-incomplete", result?.EvidenceReference);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Store.Current.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_exhaustion_retains_the_canonical_cycle_start_when_a_cycle_attempt_reaches_its_hard_resource_bound()
    {
        var scenario = await CreateScheduledRetryAsync(RetryConditionCycleArtifact);
        var activation = scenario.Store.Current.Frontier!.Payload.Nodes[scenario.Scheduled.Identity.ActivationOrdinal];
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        scenario.RetryPosture.Budget = scenario.Scheduled.Budget with { ResourceUnits = 2 };

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.NotNull(activation.CycleId);
        Assert.Equal(1, activation.CycleIteration);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, result?.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, scenario.Store.Current.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Exhausted, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.Equal(0, scenario.Executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_propagates_cancellation_from_each_pre_dispatch_governance_boundary()
    {
        var context = await CreateScheduledRetryAsync();
        context.Time.UtcNow = context.Scheduled.NextRetryAtUtc!.Value;
        using var resolveCancellation = new CancellationTokenSource();
        context.OrderedResume.BeforeResolve = _ => resolveCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Retry.ContinueAsync(ContinuationRequest(context.Checkpoint), resolveCancellation.Token));

        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, context.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(context.Executor.Requests);

        var posture = await CreateScheduledRetryAsync();
        posture.Time.UtcNow = posture.Scheduled.NextRetryAtUtc!.Value;
        using var postureCancellation = new CancellationTokenSource();
        posture.RetryPosture.BeforeRead = _ => postureCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => posture.Retry.ContinueAsync(ContinuationRequest(posture.Checkpoint), postureCancellation.Token));

        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, posture.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(posture.Executor.Requests);
    }

    [Fact]
    public async Task Retry_continuation_reconciles_cancellation_after_the_dispatch_cas_boundary_without_provider_dispatch()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        using var cancellation = new CancellationTokenSource();
        scenario.Store.BeforeUpdate = (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        };

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint), cancellation.Token);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal("retry-dispatch-cas-incomplete", result?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.Equal(0, scenario.Executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_fails_closed_when_the_exact_run_read_is_missing_or_unavailable()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Store.ReturnMissing = true;

        var missing = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, missing?.Status);
        Assert.Equal("retry-run-unavailable", missing?.EvidenceReference);

        scenario.Store.ReturnMissing = false;
        scenario.Store.GetException = new IOException("simulated retry continuation read outage");
        var unavailable = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, unavailable?.Status);
        Assert.Equal("retry-run-unavailable", unavailable?.EvidenceReference);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_ordered_reentry_replays_the_exact_completed_canonical_result_without_a_second_provider_dispatch()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var continuation = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var dispatched = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        var context = new GovernedLoopWaitOrderedContext(scenario.Context.Anchor, scenario.Context.Plan, scenario.Context.Artifact);
        var writesBeforeReplay = scenario.Store.Writes.Count;
        var requestsBeforeReplay = scenario.Executor.Requests.Count;

        var replay = await scenario.OrderedResume.ResumeRetryAsync(new GovernedLoopRetryOrderedResumeRequest(context, dispatched, AuditSchema.Actors.Web));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, continuation?.Status);
        Assert.Equal(CustomLoopRunStatus.Completed, scenario.Store.Current.Status);
        Assert.Equal(CustomLoopOrderedRunStatus.Completed, replay.Status);
        Assert.Equal(writesBeforeReplay, scenario.Store.Writes.Count);
        Assert.Equal(requestsBeforeReplay, scenario.Executor.Requests.Count);
        Assert.Equal(2, scenario.Executor.Requests.Count);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_parks_one_new_exact_attempt_after_the_second_retryable_failure()
    {
        var scenario = await CreateScheduledRetryAsync(role => RetryArtifact(role, 3), failedAttempts: 2);
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var latest = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, result?.Status);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Store.Current.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, latest.Disposition);
        Assert.Equal(3, latest.NextAttempt);
        Assert.Equal(2, scenario.Executor.Requests.Count);
        Assert.Equal(0, scenario.Executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_terminalizes_the_second_failed_attempt_from_its_exact_dispatched_reservation()
    {
        var scenario = await CreateScheduledRetryAsync(failedAttempts: 2);
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var terminal = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, result?.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, scenario.Store.Current.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Exhausted, terminal.Disposition);
        Assert.Equal(2, scenario.Executor.Requests.Count);
        Assert.Equal(0, scenario.Executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);

        var reconciliation = await scenario.Retry.ReconcileAsync(ReconciliationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, reconciliation?.Status);
        Assert.Equal(terminal.ContentHash, reconciliation?.ContinuationEvidenceHash);
    }

    [Fact]
    public async Task Retry_schedule_retains_and_replays_needs_review_when_a_required_usage_measurement_is_missing()
    {
        var context = await SequentialContextAsync(
            Run(SequentialDefinition()),
            artifactFactory: role => RetryArtifact(role, 2, 10_000, maximumTokens: 1));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time),
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time)
        {
            Budget = new GovernedLoopRetryBudgetSnapshot(1, null, 0, null, null, 1),
        };
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, retryPosture, orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var executor = new QueueExecutor(Result("must not dispatch"))
        {
            BeforeProviderRequestStarted = _ => throw new InvalidOperationException("provider unavailable before transport"),
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, timeProvider: time, retryNodeExecutor: retryRelay),
            evidence,
            evidence);

        var run = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        var terminal = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        var replay = await retry.ScheduleAsync(RetryRequest(context, store));

        Assert.Equal(CustomLoopOrderedRunStatus.NeedsReview, run.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, store.Current.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.NeedsReview, terminal.Disposition);
        Assert.Equal(GovernedLoopRetryExecutionStatus.NeedsReview, replay.Status);
        Assert.Equal("retry-terminal-decision-replayed", replay.Detail);
        Assert.Single(executor.Requests);
        Assert.Equal(0, executor.ProviderRequestStartedCount);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_ordered_reentry_rejects_invalid_or_unavailable_handoffs_without_dispatching_a_provider()
    {
        var scenario = await CreateScheduledRetryAsync();
        var context = new GovernedLoopWaitOrderedContext(scenario.Context.Anchor, scenario.Context.Plan, scenario.Context.Artifact);
        var invalid = await scenario.OrderedResume.ResumeRetryAsync(new GovernedLoopRetryOrderedResumeRequest(
            context,
            scenario.Scheduled,
            AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, invalid.Status);

        scenario.OrderedResume.ResumeException = new IOException("hold ordered resume while the dispatch reservation is retained");
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        _ = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var dispatched = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        scenario.OrderedResume.ResumeException = null;

        scenario.Store.GetException = new IOException("simulated resumed run outage");
        var unavailable = await scenario.OrderedResume.ResumeRetryAsync(new GovernedLoopRetryOrderedResumeRequest(context, dispatched, AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.Failed, unavailable.Status);

        scenario.Store.GetException = null;
        scenario.Store.ReturnMissing = true;
        var missing = await scenario.OrderedResume.ResumeRetryAsync(new GovernedLoopRetryOrderedResumeRequest(context, dispatched, AuditSchema.Actors.Web));
        Assert.Equal(CustomLoopOrderedRunStatus.NotFound, missing.Status);

        scenario.Store.ReturnMissing = false;
        var substituted = GovernedLoopRetryContract.CreateState(
            dispatched.Identity,
            dispatched.StateVersion + 1,
            GovernedLoopRetryStateDisposition.Dispatched,
            dispatched.CurrentAttempt,
            dispatched.CurrentAttemptOperationId,
            dispatched.NextAttempt,
            dispatched.AttemptOperationId,
            dispatched.Budget,
            null,
            dispatched.WakeCheckpointId,
            dispatched.WakeCheckpointHash,
            dispatched.FailureEvidenceId,
            dispatched.FailureEvidenceHash,
            scenario.Time.UtcNow);
        var substitutedResult = await scenario.OrderedResume.ResumeRetryAsync(new GovernedLoopRetryOrderedResumeRequest(context, substituted, AuditSchema.Actors.Web));

        Assert.Equal(CustomLoopOrderedRunStatus.InvalidState, substitutedResult.Status);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_recovery_fails_closed_for_invalid_discovery_results_without_attempting_a_new_dispatch()
    {
        var scenario = await CreateScheduledRetryAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scenario.Retry.RecoverAsync(0));

        scenario.Store.ListNonterminalException = new IOException("simulated discovery outage");
        var unavailable = await scenario.Retry.RecoverAsync(1);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(0, 1), unavailable);

        scenario.Store.ListNonterminalException = null;
        scenario.Store.ReturnNullNonterminalList = true;
        var nullList = await scenario.Retry.RecoverAsync(1);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(0, 1), nullList);

        scenario.Store.ReturnNullNonterminalList = false;
        scenario.Store.ReturnDuplicateNonterminalList = true;
        var duplicateList = await scenario.Retry.RecoverAsync(1);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(0, 1), duplicateList);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_recovery_propagates_caller_cancellation_before_enumerating_nonterminal_runs()
    {
        var scenario = await CreateScheduledRetryAsync();
        using var cancellation = new CancellationTokenSource();
        scenario.Store.BeforeListNonterminal = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Retry.RecoverAsync(1, cancellation.Token));

        Assert.Single(scenario.Executor.Requests);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_recovery_reconciles_an_unpublished_checkpoint_after_each_exact_read_and_publication_failure()
    {
        var scenario = await CreateUnpublishedRetryAsync();
        Assert.Null(scenario.Scheduled.WakeCheckpointId);
        Assert.Equal(CustomLoopRunStatus.Waiting, scenario.Store.Current.Status);

        scenario.Store.ReturnMissing = true;
        var missing = await scenario.Retry.RecoverAsync(1);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(0, 1), missing);

        scenario.Store.ReturnMissing = false;
        scenario.SleepStore.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(GovernedLoopSleepCheckpointMutationStatus.Ambiguous);
        scenario.SleepStore.CheckpointReadOverride = new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.Unavailable);
        var ambiguous = await scenario.Retry.RecoverAsync(1);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(1, 0), ambiguous);
        Assert.Null(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.WakeCheckpointId);

        scenario.SleepStore.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(GovernedLoopSleepCheckpointMutationStatus.Conflict);
        scenario.SleepStore.CheckpointReadOverride = null;
        var conflict = await scenario.Retry.RecoverAsync(1);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(0, 1), conflict);

        scenario.SleepStore.PublishOverride = null;
        var recovered = await scenario.Retry.RecoverAsync(1);
        var attached = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);

        Assert.Equal(new GovernedLoopRetryRecoveryResult(1, 0), recovered);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, attached.Disposition);
        Assert.NotNull(attached.WakeCheckpointId);
        Assert.NotNull(attached.WakeCheckpointHash);
        Assert.Equal(1, scenario.SleepStore.CheckpointCount);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_recovery_counts_an_exact_checkpoint_that_another_worker_attaches_after_discovery()
    {
        var scenario = await CreateUnpublishedRetryAsync();
        var unbound = scenario.Store.Current;
        scenario.SleepStore.PublishOverride = null;

        var firstWorker = await scenario.Retry.RecoverAsync(1);
        var attached = scenario.Store.Current;
        scenario.Store.ReplaceCurrent(unbound);
        scenario.Store.ReadSubstitutionFactory = _ =>
        {
            scenario.Store.ReplaceCurrent(attached);
            return attached;
        };

        var recovery = await scenario.Retry.RecoverAsync(1);

        Assert.Equal(new GovernedLoopRetryRecoveryResult(1, 0), firstWorker);
        Assert.Equal(new GovernedLoopRetryRecoveryResult(1, 0), recovery);
        Assert.NotNull(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.WakeCheckpointId);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_reconciliation_reports_not_committed_for_an_exact_scheduled_checkpoint_without_dispatching_it()
    {
        var scenario = await CreateScheduledRetryAsync();

        var result = await scenario.Retry.ReconcileAsync(ReconciliationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.NotCommitted, result?.Status);
        Assert.Equal("retry-dispatch-not-retained", result?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_reconciliation_reports_not_committed_for_a_valid_but_unbound_checkpoint()
    {
        var scenario = await CreateScheduledRetryAsync();
        var substituted = GovernedLoopSleepContractHash.Apply(scenario.Checkpoint with
        {
            CheckpointId = string.Empty,
            ContentHash = string.Empty,
            WakeDeadlineUtc = scenario.Checkpoint.WakeDeadlineUtc!.Value.AddMilliseconds(1),
        });

        var result = await scenario.Retry.ReconcileAsync(ReconciliationRequest(substituted));

        Assert.Equal(GovernedLoopWakeContinuationStatus.NotCommitted, result?.Status);
        Assert.Equal("retry-state-not-found", result?.EvidenceReference);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_continuation_fails_closed_before_dispatch_when_the_exact_ordered_context_cannot_be_resolved()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.OrderedResume.ReturnNullContext = true;
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, result?.Status);
        Assert.Equal("retry-context-substituted", result?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_continuation_preserves_the_committed_dispatch_when_ordered_reentry_throws()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.OrderedResume.ResumeException = new IOException("simulated ordered reentry outage");
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal("ordered-retry-reentry-incomplete", result?.EvidenceReference);
        var dispatched = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        Assert.Equal(GovernedLoopRetryStateDisposition.Dispatched, dispatched.Disposition);
        Assert.Equal(2, dispatched.NextAttempt);
        Assert.Single(scenario.Executor.Requests);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
    }

    [Fact]
    public async Task Retry_continuation_fails_closed_when_context_resolution_throws_before_the_retry_is_dispatched()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.OrderedResume.ResolveException = new IOException("simulated ordered context outage");
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, result?.Status);
        Assert.Equal("retry-context-substituted", result?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
    }

    [Theory]
    [InlineData("unavailable", GovernedLoopRetryStateDisposition.Scheduled, CustomLoopRunStatus.Waiting, "retry-current-posture-unavailable")]
    [InlineData("budget-conflict", GovernedLoopRetryStateDisposition.NeedsReview, CustomLoopRunStatus.NeedsReview, "retry-budget-evidence-conflict")]
    [InlineData("deadline", GovernedLoopRetryStateDisposition.Exhausted, CustomLoopRunStatus.Failed, "retry-deadline-exhausted")]
    public async Task Retry_continuation_preserves_each_fail_closed_current_posture_outcome_before_provider_dispatch(
        string posture,
        GovernedLoopRetryStateDisposition expectedDisposition,
        CustomLoopRunStatus expectedRunStatus,
        string expectedDetail)
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        switch (posture)
        {
            case "unavailable":
                scenario.RetryPosture.ReturnNull = true;
                break;
            case "budget-conflict":
                scenario.RetryPosture.Budget = scenario.Scheduled.Budget with { Attempts = scenario.Scheduled.CurrentAttempt + 1 };
                break;
            case "deadline":
                scenario.Time.UtcNow = scenario.Scheduled.Identity.DeadlineUtc;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(posture));
        }

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        if (posture == "unavailable")
        {
            Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, result?.Status);
            Assert.Equal(expectedDetail, result?.EvidenceReference);
        }
        else
        {
            Assert.Equal(GovernedLoopWakeContinuationStatus.Committed, result?.Status);
            Assert.Equal(expectedRunStatus, scenario.Store.Current.Status);
            var terminal = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);
            Assert.Equal(expectedDisposition, terminal.Disposition);
            Assert.Equal(expectedDetail, scenario.Store.Current.FailureDetail);
            Assert.True(CustomLoopRunValidator.Validate(scenario.Store.Current).IsValid);
        }

        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_continuation_fails_closed_when_current_posture_read_throws_after_wake_validation()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        scenario.RetryPosture.Exception = new IOException("simulated continuation posture outage");

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));

        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, result?.Status);
        Assert.Equal("retry-current-posture-unavailable", result?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState?.Disposition);
        Assert.Single(scenario.Executor.Requests);
    }

    [Fact]
    public async Task Retry_continuation_marks_the_dispatch_ambiguous_when_ordered_reentry_and_durable_reconciliation_are_both_unavailable()
    {
        var scenario = await CreateScheduledRetryAsync();
        scenario.Time.UtcNow = scenario.Scheduled.NextRetryAtUtc!.Value;
        scenario.OrderedResume.ResumeException = new IOException("simulated ordered retry reentry outage");
        scenario.Store.BeforeUpdate = (_, _) => scenario.Store.GetException = new IOException("simulated retry reconciliation outage");

        var result = await scenario.Retry.ContinueAsync(ContinuationRequest(scenario.Checkpoint));
        var dispatched = Assert.IsType<GovernedLoopRetryState>(scenario.Store.Current.Events.Last(item => item.RetryState is not null).RetryState);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal("retry-dispatch-evidence-unavailable", result?.EvidenceReference);
        Assert.Equal(GovernedLoopRetryStateDisposition.Dispatched, dispatched.Disposition);
        Assert.Single(scenario.Executor.Requests);
        Assert.Equal(0, scenario.Executor.ProviderRequestStartedCount);
    }

    private async Task<(SequentialTestContext Context, FakeRunStore Store, StubGovernedLoopSleepTimeProvider Time, InMemoryGovernedLoopSleepStore SleepStore, GovernedLoopSleepService Sleep, CanonicalRetryPosturePort RetryPosture, BoundRetryOrderedResumePort OrderedResume, GovernedLoopRetryExecutionService Retry, QueueExecutor Executor, GovernedLoopRetryState Scheduled, GovernedLoopSleepCheckpoint Checkpoint)> CreateScheduledRetryAsync(
        Func<ContextualRoleRevisionPin, GovernedLoopGraphRevisionArtifact>? artifactFactory = null,
        int failedAttempts = 1)
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()), artifactFactory: artifactFactory ?? (role => RetryArtifact(role)));
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time),
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, retryPosture, orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var remainingFailures = failedAttempts;
        var executor = new QueueExecutor(Result("retry completed"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (remainingFailures > 0)
                {
                    remainingFailures--;
                    throw new InvalidOperationException("provider unavailable before transport");
                }

                return Task.CompletedTask;
            },
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, timeProvider: time, retryNodeExecutor: retryRelay),
            evidence,
            evidence);
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        var scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);
        var checkpoint = Assert.IsType<GovernedLoopSleepCheckpoint>((await sleepStore.ReadCheckpointAsync(scheduled.WakeCheckpointId!))?.Checkpoint);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        return (context, store, time, sleepStore, sleep, retryPosture, orderedResume, retry, executor, scheduled, checkpoint);
    }

    private async Task<(SequentialTestContext Context, FakeRunStore Store, StubGovernedLoopSleepTimeProvider Time, InMemoryGovernedLoopSleepStore SleepStore, GovernedLoopSleepService Sleep, CanonicalRetryPosturePort RetryPosture, BoundRetryOrderedResumePort OrderedResume, GovernedLoopRetryExecutionService Retry, QueueExecutor Executor, GovernedLoopRetryState Scheduled)> CreateUnpublishedRetryAsync()
    {
        var context = await SequentialContextAsync(Run(SequentialDefinition()), artifactFactory: RetryArtifact);
        var store = new FakeRunStore(context.Run);
        var time = new StubGovernedLoopSleepTimeProvider(_now);
        var sleepStore = new InMemoryGovernedLoopSleepStore
        {
            PublishOverride = new GovernedLoopSleepCheckpointMutationResult(GovernedLoopSleepCheckpointMutationStatus.Unavailable),
        };
        var continuationRelay = new GovernedLoopWaitContinuationRelay();
        var sleep = new GovernedLoopSleepService(
            sleepStore,
            new CanonicalWaitPosturePort(store, context.Anchor.AdapterBinding.AdmissionReceipt.Intent.Publication, time),
            continuationRelay,
            new StubGovernedLoopAuthenticatedWakeVerificationPort(),
            time);
        var retryPosture = new CanonicalRetryPosturePort(time);
        var orderedResume = new BoundRetryOrderedResumePort();
        var retry = new GovernedLoopRetryExecutionService(store, sleep, retryPosture, orderedResume);
        var retryRelay = new GovernedLoopRetryNodeExecutionRelay();
        retryRelay.Bind(retry);
        continuationRelay.BindRetry(retry);
        var failed = false;
        var executor = new QueueExecutor(Result("retry completed"))
        {
            BeforeProviderRequestStarted = _ =>
            {
                if (!failed)
                {
                    failed = true;
                    throw new InvalidOperationException("provider unavailable before transport");
                }

                return Task.CompletedTask;
            },
        };
        var evidence = new SequentialEvidenceHarness(store, context.Evidence);
        var runtime = new GovernedLoopSequentialOrderedRuntimeAdapter(
            Runner(store, executor, timeProvider: time, retryNodeExecutor: retryRelay),
            evidence,
            evidence);
        orderedResume.Bind(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact), runtime);

        var parked = await runtime.RunAsync(new GovernedLoopSequentialOrderedRunRequest(
            GovernedLoopSequentialOrderedRunRequest.CurrentSchemaVersion,
            context.Anchor,
            context.Plan,
            context.Artifact,
            AuditSchema.Actors.Web));
        var scheduled = Assert.IsType<GovernedLoopRetryState>(store.Current.Events.Last(item => item.RetryState is not null).RetryState);

        Assert.Equal(CustomLoopOrderedRunStatus.Waiting, parked.Status);
        Assert.Equal(GovernedLoopRetryStateDisposition.Scheduled, scheduled.Disposition);
        Assert.Null(scheduled.WakeCheckpointId);
        Assert.True(CustomLoopRunValidator.Validate(store.Current).IsValid);
        return (context, store, time, sleepStore, sleep, retryPosture, orderedResume, retry, executor, scheduled);
    }

    private static GovernedLoopRetryExecutionRequest RetryRequest(
        (SequentialTestContext Context, FakeRunStore Store, StubGovernedLoopSleepTimeProvider Time, InMemoryGovernedLoopSleepStore SleepStore, GovernedLoopSleepService Sleep, CanonicalRetryPosturePort RetryPosture, BoundRetryOrderedResumePort OrderedResume, GovernedLoopRetryExecutionService Retry, QueueExecutor Executor, GovernedLoopRetryState Scheduled, GovernedLoopSleepCheckpoint Checkpoint) scenario)
    {
        return RetryRequest(scenario.Context, scenario.Store);
    }

    private static GovernedLoopRetryExecutionRequest RetryRequest(SequentialTestContext context, FakeRunStore store)
    {
        var failure = Assert.IsType<GovernedLoopFailureEvidence>(store.Current.Events.Single(item => item.FailureEvidence is not null).FailureEvidence);
        var node = context.Plan.Nodes.Single(candidate => string.Equals(candidate.NodeId, failure.NodeId, StringComparison.Ordinal));
        return new GovernedLoopRetryExecutionRequest(context.Anchor, context.Plan, node, failure, AuditSchema.Actors.Web);
    }

    private static CustomLoopRunRecord FailureSnapshot(
        (SequentialTestContext Context, FakeRunStore Store, StubGovernedLoopSleepTimeProvider Time, InMemoryGovernedLoopSleepStore SleepStore, GovernedLoopSleepService Sleep, CanonicalRetryPosturePort RetryPosture, BoundRetryOrderedResumePort OrderedResume, GovernedLoopRetryExecutionService Retry, QueueExecutor Executor, GovernedLoopRetryState Scheduled, GovernedLoopSleepCheckpoint Checkpoint) scenario)
        => FailureSnapshot(scenario.Store);

    private static CustomLoopRunRecord FailureSnapshot(FakeRunStore store)
        => Assert.Single(store.Writes, candidate => candidate.Events.Any(item => item.FailureEvidence is not null)
            && candidate.Events.All(item => item.RetryState is null));

    private static GovernedLoopGraphRevisionArtifact RetryConditionCycleArtifact(ContextualRoleRevisionPin owningRole)
    {
        var source = InferenceConditionCycleArtifact(owningRole).Graph;
        var inference = source.Nodes.Single(node => node.Descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference);
        var policy = GovernedLoopRetryContract.CreatePolicy(
            "retry-cycle-policy",
            inference.Id,
            [GovernedLoopFailureClass.DispatchProvedNotStarted],
            ["provider-dispatch-not-started"],
            2,
            10_000,
            30_000,
            GovernedLoopRetryBackoffStrategy.Fixed,
            1_000,
            1_000,
            GovernedLoopRetryJitterStrategy.None,
            0,
            maximumResourceUnits: 2);
        var retryInference = new GovernedLoopNodeDefinition(
            inference.Id,
            inference.Descriptor,
            inference.Ports,
            inference.AuthorityCeiling,
            inference.Parameters,
            inference.ModelRoutingPolicy,
            inference.AuthoredInputDataClasses,
            policy);
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            source.Nodes.Select(node => string.Equals(node.Id, inference.Id, StringComparison.Ordinal) ? retryInference : node).ToArray(),
            source.ControlEdges,
            source.TerminalNodeIds,
            owningRole,
            source.Bindings,
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private static GovernedLoopWakeContinuationRequest ReconciliationRequest(GovernedLoopSleepCheckpoint checkpoint)
    {
        var identity = WakeIdentity(checkpoint);
        return new GovernedLoopWakeContinuationRequest(checkpoint, identity, "retry-reconciliation", null, null);
    }

    private static GovernedLoopWakeContinuationRequest ContinuationRequest(GovernedLoopSleepCheckpoint checkpoint)
    {
        var identity = WakeIdentity(checkpoint);
        const string OperationId = "retry-continuation";
        var prepared = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            GovernedLoopWakeEvidence.CurrentSchemaVersion,
            1,
            identity,
            GovernedLoopWakeDisposition.Prepared,
            OperationId,
            null,
            null,
            checkpoint.WakeDeadlineUtc!.Value,
            string.Empty));
        Assert.True(GovernedLoopSleepContractValidator.Validate(prepared).IsValid);
        Assert.True(GovernedLoopSleepContractValidator.ValidateComposition(checkpoint, prepared).IsValid);
        return new GovernedLoopWakeContinuationRequest(checkpoint, identity, OperationId, prepared, new string('a', 64));
    }

    private static GovernedLoopWakeIdentity WakeIdentity(GovernedLoopSleepCheckpoint checkpoint)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            GovernedLoopWakeIdentity.CurrentSchemaVersion,
            string.Empty,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            checkpoint.WakeMode,
            null,
            null,
            string.Empty));

}
