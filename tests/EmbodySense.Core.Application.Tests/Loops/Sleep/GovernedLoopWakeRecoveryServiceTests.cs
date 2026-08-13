using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sleep;

public sealed class GovernedLoopWakeRecoveryServiceTests
{
    [Fact]
    public async Task Crash_after_prepared_commit_reconciles_noncommit_then_retries_same_operation()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Store.ThrowAfterCreateCommit = true;

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
        Assert.True(result.ContinuationInvoked);
        Assert.Equal(1, harness.Continuation.ReconcileCount);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(1, harness.Continuation.CommittedOperationCount);
    }

    [Fact]
    public async Task Crash_after_continuation_commit_stays_ambiguous_until_restart_reconciliation()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Continuation.ThrowAfterCommit = true;

        var first = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));
        harness.Continuation.ThrowAfterCommit = false;
        var reconciled = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            first.Evidence!.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, first.Status);
        Assert.Equal(GovernedLoopWakeDisposition.AmbiguousAttempt, first.Evidence.Disposition);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, reconciled.Status);
        Assert.False(reconciled.ContinuationInvoked);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(1, harness.Continuation.CommittedOperationCount);
    }

    [Fact]
    public async Task Crash_after_committed_evidence_write_reloads_conclusive_commit()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Store.ThrowAfterAdvanceCommit = true;

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Committed, result.Evidence!.Disposition);
        Assert.Equal(1, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Restart_prepared_reconciliation_retries_only_after_conclusive_noncommit()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(checkpoint);
        harness.Store.SeedWake(prepared);

        var result = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
        Assert.Equal(1, harness.Continuation.ReconcileCount);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(prepared.ContinuationOperationId, result.Evidence!.ContinuationOperationId);
    }

    [Fact]
    public async Task Restart_prepared_reconciliation_commits_observed_outcome_without_redispatch()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(checkpoint);
        harness.Store.SeedWake(prepared);
        harness.Continuation.ReconcileResult = new GovernedLoopWakeContinuationResult(
            GovernedLoopWakeContinuationStatus.Committed,
            GovernedLoopSleepApplicationTestFixture.Hash('6'));

        var result = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
        Assert.Equal(0, harness.Continuation.ContinueCount);
        Assert.Equal(1, harness.Continuation.ReconcileCount);
    }

    [Theory]
    [InlineData(GovernedLoopWakeContinuationStatus.Ambiguous, GovernedLoopWakeResultStatus.AmbiguousAttempt)]
    [InlineData(GovernedLoopWakeContinuationStatus.Unavailable, GovernedLoopWakeResultStatus.AmbiguousAttempt)]
    [InlineData(GovernedLoopWakeContinuationStatus.Conflict, GovernedLoopWakeResultStatus.Conflict)]
    public async Task Restart_reconciliation_never_guesses_ambiguous_unavailable_or_conflicting_outcome(
        GovernedLoopWakeContinuationStatus continuationStatus,
        GovernedLoopWakeResultStatus expected)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(checkpoint);
        harness.Store.SeedWake(prepared);
        harness.Continuation.ReconcileResult = new GovernedLoopWakeContinuationResult(
            continuationStatus,
            EvidenceReference: "reconciliation-evidence");

        var result = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Restart_reconciliation_treats_throwing_null_and_malformed_adapter_outputs_as_ambiguous()
    {
        var throwing = new GovernedLoopSleepApplicationHarness();
        var throwingCheckpoint = await throwing.PublishAsync();
        var throwingPrepared = GovernedLoopSleepApplicationTestFixture.Prepared(throwingCheckpoint);
        throwing.Store.SeedWake(throwingPrepared);
        throwing.Continuation.ReconcileException = new InvalidOperationException("reconciliation unavailable");

        var nullOutput = new GovernedLoopSleepApplicationHarness();
        var nullCheckpoint = await nullOutput.PublishAsync();
        var nullPrepared = GovernedLoopSleepApplicationTestFixture.Prepared(nullCheckpoint);
        nullOutput.Store.SeedWake(nullPrepared);
        nullOutput.Continuation.ReturnNullReconcile = true;

        var throwingResult = await throwing.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            throwingCheckpoint.CheckpointId,
            throwingPrepared.Identity.WakeId));
        var nullResult = await nullOutput.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            nullCheckpoint.CheckpointId,
            nullPrepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, throwingResult.Status);
        Assert.Equal(GovernedLoopWakeDisposition.AmbiguousAttempt, throwingResult.Evidence!.Disposition);
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, nullResult.Status);
        Assert.Equal(GovernedLoopWakeDisposition.AmbiguousAttempt, nullResult.Evidence!.Disposition);
        Assert.Equal(0, throwing.Continuation.ContinueCount);
        Assert.Equal(0, nullOutput.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Ambiguous_restart_reconciliation_does_not_append_an_illegal_ambiguous_self_transition()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(checkpoint);
        harness.Store.SeedWake(prepared);
        harness.Continuation.ReconcileResult = new GovernedLoopWakeContinuationResult(
            GovernedLoopWakeContinuationStatus.Ambiguous,
            EvidenceReference: "first-ambiguous-outcome");
        var first = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        harness.Continuation.ReconcileResult = new GovernedLoopWakeContinuationResult(
            GovernedLoopWakeContinuationStatus.Ambiguous,
            EvidenceReference: "still-ambiguous-outcome");
        var second = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, first.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, second.Status);
        Assert.Equal(first.Evidence!.ContentHash, second.Evidence!.ContentHash);
        Assert.Equal(2, second.Evidence.EvidenceVersion);
    }

    [Fact]
    public async Task Conclusive_noncommit_revalidates_exact_posture_before_retry()
    {
        var stale = new GovernedLoopSleepApplicationHarness();
        var staleCheckpoint = await stale.PublishAsync();
        var stalePrepared = GovernedLoopSleepApplicationTestFixture.Prepared(staleCheckpoint);
        stale.Store.SeedWake(stalePrepared);
        stale.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            GovernedLoopSleepApplicationTestFixture.Posture(binding: GovernedLoopSleepApplicationTestFixture.Binding(2)));

        var futureDeadline = GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(1);
        var early = new GovernedLoopSleepApplicationHarness(deadlineUtc: futureDeadline);
        var earlyCheckpoint = await early.PublishAsync();
        var earlyPrepared = GovernedLoopSleepApplicationTestFixture.Prepared(
            earlyCheckpoint,
            recordedAtUtc: futureDeadline);
        early.Store.SeedWake(earlyPrepared);

        var staleResult = await stale.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            staleCheckpoint.CheckpointId,
            stalePrepared.Identity.WakeId));
        var earlyResult = await early.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            earlyCheckpoint.CheckpointId,
            earlyPrepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Stale, staleResult.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Failed, staleResult.Evidence!.Disposition);
        Assert.Equal(GovernedLoopWakeResultStatus.NotEligible, earlyResult.Status);
        Assert.Same(earlyPrepared, earlyResult.Evidence);
        Assert.Equal(0, stale.Continuation.ContinueCount);
        Assert.Equal(0, early.Continuation.ContinueCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conclusive_noncommit_waits_for_time_to_catch_up_with_current_wake_evidence_before_retry(
        bool ambiguous)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var evidenceAtUtc = GovernedLoopSleepApplicationTestFixture.Now.AddHours(1);
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(
            checkpoint,
            recordedAtUtc: ambiguous ? evidenceAtUtc.AddTicks(-1) : evidenceAtUtc);
        var current = ambiguous
            ? GovernedLoopSleepApplicationTestFixture.Ambiguous(prepared, evidenceAtUtc)
            : prepared;
        harness.Store.SeedWake(current);

        var rolledBack = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            current.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.NotEligible, rolledBack.Status);
        Assert.Same(current, rolledBack.Evidence);
        Assert.Equal(0, harness.Continuation.ContinueCount);

        harness.TimeProvider.UtcNow = evidenceAtUtc;
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            harness.Posture with { ObservedAtUtc = evidenceAtUtc });
        var caughtUp = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            current.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, caughtUp.Status);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(2, harness.Continuation.ReconcileCount);
    }

    [Fact]
    public async Task Clock_rollback_after_prepared_commit_blocks_initial_continuation_until_reconciliation_catches_up()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Store.OnCreate = (_, _) =>
            harness.TimeProvider.UtcNow = GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(-1);

        var rolledBack = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.NotEligible, rolledBack.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Prepared, rolledBack.Evidence!.Disposition);
        Assert.Equal(0, harness.Continuation.ContinueCount);

        harness.Store.OnCreate = null;
        harness.TimeProvider.UtcNow = GovernedLoopSleepApplicationTestFixture.Now;
        var caughtUp = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            rolledBack.Evidence.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Committed, caughtUp.Status);
        Assert.Equal(1, harness.Continuation.ContinueCount);
    }

    [Fact]
    public async Task Prepared_wake_remains_reconcilable_across_temporary_pause()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(checkpoint);
        harness.Store.SeedWake(prepared);
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            GovernedLoopSleepApplicationTestFixture.Posture(lifecycleStatus: GovernedLoopRunStatus.Paused));

        var paused = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));
        harness.CurrentPosture.Result = new GovernedLoopSleepCurrentPostureReadResult(
            GovernedLoopSleepCurrentPostureReadStatus.Found,
            harness.Posture);
        var resumed = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Paused, paused.Status);
        Assert.Same(prepared, paused.Evidence);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, resumed.Status);
        Assert.Equal(1, harness.Continuation.ContinueCount);
        Assert.Equal(2, harness.Continuation.ReconcileCount);
    }

    [Fact]
    public async Task Conclusive_noncommit_fails_closed_when_retry_clock_or_posture_is_unavailable()
    {
        var firstClock = new GovernedLoopSleepApplicationHarness();
        var firstCheckpoint = await firstClock.PublishAsync();
        var firstPrepared = GovernedLoopSleepApplicationTestFixture.Prepared(firstCheckpoint);
        firstClock.Store.SeedWake(firstPrepared);
        firstClock.TimeProvider.ThrowOnCall = firstClock.TimeProvider.CallCount + 1;

        var secondClock = new GovernedLoopSleepApplicationHarness();
        var secondCheckpoint = await secondClock.PublishAsync();
        var secondPrepared = GovernedLoopSleepApplicationTestFixture.Prepared(secondCheckpoint);
        secondClock.Store.SeedWake(secondPrepared);
        secondClock.TimeProvider.ThrowOnCall = secondClock.TimeProvider.CallCount + 2;

        var malformedPosture = new GovernedLoopSleepApplicationHarness();
        var malformedCheckpoint = await malformedPosture.PublishAsync();
        var malformedPrepared = GovernedLoopSleepApplicationTestFixture.Prepared(malformedCheckpoint);
        malformedPosture.Store.SeedWake(malformedPrepared);
        malformedPosture.CurrentPosture.Result = null;

        var first = await firstClock.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            firstCheckpoint.CheckpointId,
            firstPrepared.Identity.WakeId));
        var second = await secondClock.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            secondCheckpoint.CheckpointId,
            secondPrepared.Identity.WakeId));
        var malformed = await malformedPosture.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            malformedCheckpoint.CheckpointId,
            malformedPrepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Unavailable, first.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Unavailable, second.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Invalid, malformed.Status);
        Assert.Same(firstPrepared, first.Evidence);
        Assert.Same(secondPrepared, second.Evidence);
        Assert.Same(malformedPrepared, malformed.Evidence);
    }

    [Fact]
    public async Task Malformed_or_throwing_continuation_result_is_durably_ambiguous()
    {
        var malformedHarness = new GovernedLoopSleepApplicationHarness();
        var malformedCheckpoint = await malformedHarness.PublishAsync();
        malformedHarness.Continuation.ContinueResult = null;
        malformedHarness.Continuation.ContinueException = new InvalidOperationException("provider unavailable");
        var thrown = await malformedHarness.Service.WakeAsync(new GovernedLoopWakeRequest(
            malformedCheckpoint.CheckpointId,
            malformedCheckpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, thrown.Status);
        Assert.Equal(GovernedLoopWakeDisposition.AmbiguousAttempt, thrown.Evidence!.Disposition);

        var nullHarness = new GovernedLoopSleepApplicationHarness();
        var nullCheckpoint = await nullHarness.PublishAsync();
        nullHarness.Continuation.ContinueResult = new GovernedLoopWakeContinuationResult(
            GovernedLoopWakeContinuationStatus.Committed,
            null);
        var malformed = await nullHarness.Service.WakeAsync(new GovernedLoopWakeRequest(
            nullCheckpoint.CheckpointId,
            nullCheckpoint.ContentHash));
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, malformed.Status);
    }

    [Fact]
    public async Task Null_continuation_output_is_durably_ambiguous_and_conclusive_noncommit_is_failed()
    {
        var nullOutput = new GovernedLoopSleepApplicationHarness();
        var nullCheckpoint = await nullOutput.PublishAsync();
        nullOutput.Continuation.ReturnNullContinue = true;

        var noncommit = new GovernedLoopSleepApplicationHarness();
        var noncommitCheckpoint = await noncommit.PublishAsync();
        noncommit.Continuation.ContinueResult = new GovernedLoopWakeContinuationResult(
            GovernedLoopWakeContinuationStatus.NotCommitted,
            EvidenceReference: "provider-declined-continuation");

        var ambiguous = await nullOutput.Service.WakeAsync(new GovernedLoopWakeRequest(
            nullCheckpoint.CheckpointId,
            nullCheckpoint.ContentHash));
        var failed = await noncommit.Service.WakeAsync(new GovernedLoopWakeRequest(
            noncommitCheckpoint.CheckpointId,
            noncommitCheckpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, ambiguous.Status);
        Assert.Equal(GovernedLoopWakeDisposition.AmbiguousAttempt, ambiguous.Evidence!.Disposition);
        Assert.Equal(GovernedLoopWakeResultStatus.Failed, failed.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Failed, failed.Evidence!.Disposition);
    }

    [Theory]
    [InlineData(GovernedLoopWakeEvidenceMutationStatus.Conflict)]
    [InlineData(GovernedLoopWakeEvidenceMutationStatus.Unavailable)]
    [InlineData(GovernedLoopWakeEvidenceMutationStatus.Ambiguous)]
    public async Task Ambiguous_successor_store_outcomes_reload_before_remaining_ambiguous(
        GovernedLoopWakeEvidenceMutationStatus storeStatus)
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        harness.Store.AdvanceOverride = new GovernedLoopWakeEvidenceMutationResult(storeStatus);

        var result = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, result.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Prepared, result.Evidence!.Disposition);
        Assert.True(result.ContinuationInvoked);
    }

    [Fact]
    public async Task Successor_write_exception_and_clock_rollback_preserve_ambiguous_prepared_evidence()
    {
        var writeFailure = new GovernedLoopSleepApplicationHarness();
        var writeCheckpoint = await writeFailure.PublishAsync();
        writeFailure.Store.ThrowBeforeAdvance = true;

        var rollback = new GovernedLoopSleepApplicationHarness();
        var rollbackCheckpoint = await rollback.PublishAsync();
        rollback.Continuation.OnContinue = (_, _) =>
            rollback.TimeProvider.UtcNow = GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(-1);

        var writeResult = await writeFailure.Service.WakeAsync(new GovernedLoopWakeRequest(
            writeCheckpoint.CheckpointId,
            writeCheckpoint.ContentHash));
        var rollbackResult = await rollback.Service.WakeAsync(new GovernedLoopWakeRequest(
            rollbackCheckpoint.CheckpointId,
            rollbackCheckpoint.ContentHash));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, writeResult.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Prepared, writeResult.Evidence!.Disposition);
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, rollbackResult.Status);
        Assert.Equal(GovernedLoopWakeDisposition.Prepared, rollbackResult.Evidence!.Disposition);
    }

    [Fact]
    public async Task Maximum_evidence_version_cannot_overflow_during_reconciliation()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        var prepared = GovernedLoopSleepApplicationTestFixture.Prepared(
            checkpoint,
            evidenceVersion: GovernedLoopSleepContractLimits.MaxVersion);
        harness.Store.SeedWake(prepared);
        harness.Continuation.ReconcileResult = new GovernedLoopWakeContinuationResult(
            GovernedLoopWakeContinuationStatus.Committed,
            GovernedLoopSleepApplicationTestFixture.Hash('6'));

        var result = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            prepared.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, result.Status);
        Assert.Same(prepared, result.Evidence);
        Assert.Equal(GovernedLoopSleepContractLimits.MaxVersion, result.Evidence!.EvidenceVersion);
    }

    [Fact]
    public async Task Cancellation_before_claim_writes_nothing_but_after_prepared_does_not_cancel_exact_continuation()
    {
        var before = new GovernedLoopSleepApplicationHarness();
        var beforeCheckpoint = await before.PublishAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.Service.WakeAsync(
            new GovernedLoopWakeRequest(beforeCheckpoint.CheckpointId, beforeCheckpoint.ContentHash),
            cancelled.Token));
        Assert.Equal(0, before.Store.WakeCount);

        var after = new GovernedLoopSleepApplicationHarness();
        var afterCheckpoint = await after.PublishAsync();
        using var boundary = new CancellationTokenSource();
        after.Store.OnCreate = (_, _) => boundary.Cancel();
        after.Continuation.OnContinue = (_, continuationToken) => Assert.False(continuationToken.CanBeCanceled);
        var result = await after.Service.WakeAsync(
            new GovernedLoopWakeRequest(afterCheckpoint.CheckpointId, afterCheckpoint.ContentHash),
            boundary.Token);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Status);
    }

    [Fact]
    public async Task Two_owners_share_one_checkpoint_claim_and_one_committed_operation()
    {
        var harness = new GovernedLoopSleepApplicationHarness(mode: GovernedLoopWakeMode.AuthenticatedEvent);
        var checkpoint = await harness.PublishAsync();
        var request = new GovernedLoopWakeRequest(
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            GovernedLoopSleepApplicationTestFixture.Hash('7'));

        var results = await Task.WhenAll(
            harness.Service.WakeAsync(request),
            harness.Service.WakeAsync(request));

        Assert.Contains(results, result => result.Status == GovernedLoopWakeResultStatus.Committed);
        Assert.All(
            results,
            result => Assert.True(result.Status is GovernedLoopWakeResultStatus.Committed or GovernedLoopWakeResultStatus.Duplicate));
        Assert.Equal(1, harness.Store.WakeCount);
        Assert.Equal(1, harness.Continuation.CommittedOperationCount);
    }

    [Fact]
    public async Task Reconcile_validates_identifiers_missing_artifacts_and_terminal_replay()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        Assert.Equal(GovernedLoopWakeResultStatus.Invalid, (await harness.Service.ReconcileAsync(null)).Status);
        Assert.Equal(
            GovernedLoopWakeResultStatus.NotFound,
            (await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
                GovernedLoopSleepApplicationTestFixture.Hash('1'),
                GovernedLoopSleepApplicationTestFixture.Hash('2')))).Status);

        var checkpoint = await harness.PublishAsync();
        var wake = await harness.Service.WakeAsync(new GovernedLoopWakeRequest(checkpoint.CheckpointId, checkpoint.ContentHash));
        var replay = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            wake.Evidence!.Identity.WakeId));
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, replay.Status);
        Assert.False(replay.ContinuationInvoked);
    }

    [Fact]
    public async Task Reconcile_rejects_missing_or_cross_checkpoint_wake_evidence()
    {
        var harness = new GovernedLoopSleepApplicationHarness();
        var checkpoint = await harness.PublishAsync();
        Assert.Equal(
            GovernedLoopWakeResultStatus.NotFound,
            (await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
                checkpoint.CheckpointId,
                GovernedLoopSleepApplicationTestFixture.Hash('2')))).Status);

        var other = new GovernedLoopSleepApplicationHarness(
            posture: GovernedLoopSleepApplicationTestFixture.Posture(
                binding: GovernedLoopSleepApplicationTestFixture.Binding(2)));
        var otherCheckpoint = await other.PublishAsync();
        var otherEvidence = GovernedLoopSleepApplicationTestFixture.Terminal(
            otherCheckpoint,
            GovernedLoopWakeDisposition.Late);
        harness.Store.WakeReadOverride = new GovernedLoopWakeEvidenceReadResult(
            GovernedLoopSleepStoreReadStatus.Found,
            otherEvidence);

        var malformed = await harness.Service.ReconcileAsync(new GovernedLoopWakeReconciliationRequest(
            checkpoint.CheckpointId,
            otherEvidence.Identity.WakeId));

        Assert.Equal(GovernedLoopWakeResultStatus.Invalid, malformed.Status);
    }
}
