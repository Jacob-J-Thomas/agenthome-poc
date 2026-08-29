using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

/// <summary>Exercises the public Human Input response bridge against an exact canonical waiting checkpoint.</summary>
public sealed class HumanInputResponseContinuationServiceTests
{
    [Fact]
    public async Task Accepted_selection_terminalizes_before_a_non_mutating_ordered_reentry_fails_closed()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        var checkpoint = Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, checkpoint.Posture);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, checkpoint.Evidence[1].Kind);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized, checkpoint.Evidence[2].Kind);
        Assert.Equal(GovernedLoopWakeResultStatus.AmbiguousAttempt, result.Wake?.Status);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(1, scenario.SleepStore.WakeCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Runs.Current).IsValid);
    }

    [Fact]
    public async Task Submitted_and_replayed_are_returned_only_after_terminal_checkpoint_frontier_and_ordered_advancement_are_durable()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(advanceOrderedReentry: true);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);
        var replay = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Submitted, result.Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Replayed, replay.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, result.Wake?.Status);
        Assert.Equal(GovernedLoopWakeResultStatus.Committed, replay.Wake?.Status);
        Assert.Equal(CustomLoopRunStatus.Running, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopNodeExecutionStatus.Running, scenario.Runs.Current.Frontier?.Payload.Nodes.Single(node => node.NodeId == "exit").Status);
        var checkpoint = Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, checkpoint.Posture);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized, checkpoint.Evidence[^1].Kind);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(1, scenario.SleepStore.WakeCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Runs.Current).IsValid);
    }

    [Fact]
    public async Task Exact_duplicate_response_retains_ambiguous_evidence_without_a_second_terminal_checkpoint()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();

        var first = await scenario.Service.WakeAsync(scenario.Candidate);
        var writes = scenario.Runs.UpdateCount;
        var replay = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, first.Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, replay.Status);
        Assert.Equal(writes, scenario.Runs.UpdateCount);
        Assert.Equal(3, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Evidence.Length);
        Assert.Equal(1, scenario.SleepStore.WakeCount);
        Assert.DoesNotContain(scenario.Runs.Current.Events, item => item.SequentialNodeEvidence is { Kind: CustomLoopSequentialNodeEvidenceKind.DispatchStarted, NodeId: "exit" });
    }

    [Fact]
    public async Task Pending_lifecycle_without_a_selection_returns_no_work_before_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(includeSelection: false);

        var pending = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.NoWork, pending.Status);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, GovernedLoopHumanInputWaitingCheckpointPosture.Expired)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, GovernedLoopHumanInputWaitingCheckpointPosture.Rejected)]
    public async Task Terminal_no_response_without_a_failure_route_fails_the_run_without_a_generic_wake(
        HumanInputRequestLifecycleOperationKind lifecycleOperation,
        GovernedLoopHumanInputWaitingCheckpointPosture checkpointPosture)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(includeSelection: false, noResponseTerminalOperation: lifecycleOperation);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Retired, result.Status);
        Assert.Equal(CustomLoopRunStatus.Failed, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, scenario.Runs.Current.Frontier?.Payload.Status);
        Assert.Equal(checkpointPosture, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Runs.Current).IsValid);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, GovernedLoopHumanInputWaitingCheckpointPosture.Expired)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, GovernedLoopHumanInputWaitingCheckpointPosture.Rejected)]
    public async Task Terminal_no_response_with_a_failure_route_reenters_exactly_once_without_a_generic_wake(
        HumanInputRequestLifecycleOperationKind lifecycleOperation,
        GovernedLoopHumanInputWaitingCheckpointPosture checkpointPosture)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: lifecycleOperation,
            includeFailureRoute: true,
            advanceOrderedReentry: true);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Retired, result.Status);
        Assert.Equal(CustomLoopRunStatus.Running, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Active, scenario.Runs.Current.Frontier?.Payload.Status);
        Assert.Equal(checkpointPosture, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Runs.Current).IsValid);
    }

    [Theory]
    [InlineData(HumanInputRequestLifecycleOperationKind.Expire, GovernedLoopHumanInputWaitingCheckpointPosture.Expired)]
    [InlineData(HumanInputRequestLifecycleOperationKind.Reject, GovernedLoopHumanInputWaitingCheckpointPosture.Rejected)]
    public async Task Terminal_no_response_failure_route_without_a_canonical_advance_remains_unavailable(
        HumanInputRequestLifecycleOperationKind lifecycleOperation,
        GovernedLoopHumanInputWaitingCheckpointPosture checkpointPosture)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: lifecycleOperation,
            includeFailureRoute: true);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(CustomLoopRunStatus.Running, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Active, scenario.Runs.Current.Frontier?.Payload.Status);
        Assert.Equal(checkpointPosture, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputFailureCount);
    }

    [Fact]
    public async Task Routed_no_response_reentry_remains_retryable_without_a_generic_wake_until_the_canonical_run_advances()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire,
            includeFailureRoute: true);

        var first = await scenario.Service.WakeAsync(scenario.Candidate);
        var retry = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, first.Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, retry.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Expired, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(2, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(CustomLoopRunStatus.Running, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Active, scenario.Runs.Current.Frontier?.Payload.Status);
    }

    [Fact]
    public async Task Cancelled_lifecycle_cancels_the_exact_checkpoint_and_run_without_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Cancel);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Retired, result.Status);
        Assert.Equal(CustomLoopRunStatus.Cancelled, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Cancelled, scenario.Runs.Current.Frontier?.Payload.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputFailureCount);
    }

    [Fact]
    public async Task Superseded_lifecycle_moves_the_exact_checkpoint_to_review_without_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Supersede);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Retired, result.Status);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, scenario.Runs.Current.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, scenario.Runs.Current.Frontier?.Payload.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.NeedsReview, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputFailureCount);
    }

    [Theory]
    [InlineData(false, false, HumanInputResponseContinuationWakeStatus.Unavailable)]
    [InlineData(true, true, HumanInputResponseContinuationWakeStatus.Invalid)]
    public async Task Missing_or_corrupt_response_evidence_fails_closed_before_generic_wake(
        bool responseAvailable,
        bool corruptResponse,
        HumanInputResponseContinuationWakeStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(responseAvailable: responseAvailable, corruptResponse: corruptResponse);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(expected, result.Status);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
    }

    [Fact]
    public async Task Invalid_stale_and_cancelled_calls_do_not_create_a_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(includeSelection: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Invalid, (await scenario.Service.WakeAsync(null)).Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Invalid, (await scenario.Service.WakeAsync(new HumanInputResponseContinuationCandidate("invalid id", scenario.Candidate.CheckpointId, scenario.Candidate.CheckpointHash))).Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Stale, (await scenario.Service.WakeAsync(new HumanInputResponseContinuationCandidate("missing-run", scenario.Candidate.CheckpointId, scenario.Candidate.CheckpointHash))).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Service.WakeAsync(scenario.Candidate, cancellation.Token));
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Different_valid_scanned_checkpoint_hash_is_stale_before_response_or_run_mutation()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var mismatched = new HumanInputResponseContinuationCandidate(
            scenario.Candidate.RunId,
            scenario.Candidate.CheckpointId,
            new string('b', 64));

        var result = await scenario.Service.WakeAsync(mismatched);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Stale, result.Status);
        Assert.Equal(0, scenario.Runs.UpdateAttemptCount);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
    }

    [Theory]
    [InlineData(GovernedLoopSleepCheckpointMutationStatus.Conflict, HumanInputResponseContinuationWakeStatus.Invalid)]
    [InlineData(GovernedLoopSleepCheckpointMutationStatus.Unavailable, HumanInputResponseContinuationWakeStatus.Unavailable)]
    public async Task Publication_store_outcomes_map_to_closed_response_wake_statuses(
        GovernedLoopSleepCheckpointMutationStatus publicationStatus,
        HumanInputResponseContinuationWakeStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.SleepStore.PublishOverride = new GovernedLoopSleepCheckpointMutationResult(publicationStatus);

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(expected, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Unavailable_canonical_run_read_fails_closed_before_response_or_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.Runs.GetException = new IOException("simulated canonical run read failure");

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Selection_compare_exchange_conflict_does_not_publish_a_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.Runs.ConflictNextUpdate = true;

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Stale, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Generic_sleep_binding_is_required_exactly_once()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(includeSelection: false);

        Assert.Throws<ArgumentNullException>(() => scenario.Service.BindSleep(null!));
        Assert.Throws<InvalidOperationException>(() => scenario.Service.BindSleep(scenario.Sleep));
    }

    [Fact]
    public void Constructor_rejects_a_missing_canonical_run_port()
    {
        Assert.Throws<ArgumentNullException>(() => new HumanInputResponseContinuationService(null!, null!, null!, null!, null!, null!));
    }

    [Fact]
    public async Task Verify_exact_answered_selection_reports_verified_then_rejects_substitution_and_not_found()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        var verifiedRequest = new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            prepared.Checkpoint.ContentHash,
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc);

        var verified = await scenario.Service.VerifyAsync(verifiedRequest);
        var substituted = await scenario.Service.VerifyAsync(verifiedRequest with { AuthenticationEvidenceHash = GovernedLoopSleepApplicationTestFixture.Hash('d') });
        var missing = await scenario.Service.VerifyAsync(verifiedRequest with { CheckpointId = GovernedLoopSleepApplicationTestFixture.Hash('e') });

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Verified, verified?.Status);
        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected, substituted?.Status);
        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.NotFound, missing?.Status);
    }

    [Fact]
    public async Task Verify_unavailable_checkpoint_source_fails_closed()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.SleepStore.CheckpointReadOverride = new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.Unavailable);

        var result = await scenario.Service.VerifyAsync(new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            prepared.Checkpoint.ContentHash,
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc));

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable, result?.Status);
    }

    [Fact]
    public async Task Reconcile_does_not_terminalize_an_answered_checkpoint_but_continue_does()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);

        var reconciled = await scenario.Service.ReconcileAsync(prepared.Request with { PreparedWakeEvidence = null, ExpectedPostureHash = null });
        Assert.Equal(GovernedLoopWakeContinuationStatus.NotCommitted, reconciled?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);

        var continued = await scenario.Service.ContinueAsync(prepared.Request);
        var replay = await scenario.Service.ReconcileAsync(prepared.Request with { ExpectedPostureHash = null });

        Assert.True(
            continued?.Status == GovernedLoopWakeContinuationStatus.Ambiguous,
            $"Actual {continued?.Status}/{continued?.EvidenceReference}; checkpoint {Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture}; run {scenario.Runs.Current.Status}/{scenario.Runs.Current.Frontier?.Payload.Status}.");
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, replay?.Status);
        Assert.Equal(2, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Continue_rejects_a_substituted_selection_without_terminalizing()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);

        var substitutedIdentity = CreateWakeIdentity(prepared.Checkpoint, GovernedLoopSleepApplicationTestFixture.Hash('d'));
        var result = await scenario.Service.ContinueAsync(prepared.Request with
        {
            Identity = substitutedIdentity,
            PreparedWakeEvidence = CreatePreparedEvidence(substitutedIdentity, prepared.Request.ContinuationOperationId),
        });

        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Theory]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.Unavailable, GovernedLoopWakeContinuationStatus.Unavailable)]
    [InlineData(GovernedLoopSleepCurrentPostureReadStatus.Found, GovernedLoopWakeContinuationStatus.Conflict)]
    public async Task Continue_fails_closed_for_unavailable_or_stale_current_posture(
        GovernedLoopSleepCurrentPostureReadStatus postureStatus,
        GovernedLoopWakeContinuationStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.CurrentPosture.Result = postureStatus == GovernedLoopSleepCurrentPostureReadStatus.Unavailable
            ? new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.Unavailable)
            : scenario.CurrentPosture.Result! with
            {
                Posture = scenario.CurrentPosture.Result!.Posture! with { PostureHash = GovernedLoopSleepApplicationTestFixture.Hash('d') },
            };

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(expected, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Wake_stops_after_the_selected_checkpoint_reread_becomes_unavailable()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.Runs.ThrowOnGetCall = 3;

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(3, scenario.Runs.GetCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Wake_maps_generic_create_and_successor_persistence_failures_to_closed_statuses()
    {
        var invalid = await HumanInputResponseContinuationScenario.CreateAsync();
        invalid.SleepStore.ReturnNullCreate = true;
        var unavailable = await HumanInputResponseContinuationScenario.CreateAsync();
        unavailable.SleepStore.ReturnNullAdvance = true;

        var invalidResult = await invalid.Service.WakeAsync(invalid.Candidate);
        var unavailableResult = await unavailable.Service.WakeAsync(unavailable.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Invalid, invalidResult.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(invalid.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, unavailableResult.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(unavailable.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, unavailable.Ordered.ResumeHumanInputCount);
    }

    [Theory]
    [InlineData(false, HumanInputResponseContinuationWakeStatus.Invalid)]
    [InlineData(true, HumanInputResponseContinuationWakeStatus.Unavailable)]
    public async Task Terminal_wake_replay_maps_generic_checkpoint_resolution_failures(
        bool throwsDuringCheckpointRead,
        HumanInputResponseContinuationWakeStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(advanceOrderedReentry: true);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Submitted, (await scenario.Service.WakeAsync(scenario.Candidate)).Status);
        if (throwsDuringCheckpointRead)
        {
            scenario.SleepStore.CheckpointReadException = new IOException("simulated generic checkpoint outage");
        }
        else
        {
            scenario.SleepStore.CheckpointReadOverride = new GovernedLoopSleepCheckpointReadResult(GovernedLoopSleepStoreReadStatus.NotFound);
        }

        var replay = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(expected, replay.Status);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_fails_closed_when_the_ordered_context_is_missing_or_unavailable(bool throwsDuringContextResolution)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        if (throwsDuringContextResolution)
        {
            scenario.Contexts.ResolveException = new IOException("simulated ordered-context outage");
        }
        else
        {
            scenario.Contexts.Context = null;
        }

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, scenario.Contexts.ResolveCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Continue_reports_a_terminal_compare_exchange_conflict_without_ordered_reentry()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.Runs.ConflictNextUpdate = true;

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Verify_rejects_corrupt_response_state_and_selection_before_checkpoint_publication()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        var verifiedRequest = new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            prepared.Checkpoint.ContentHash,
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc);
        var corrupt = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Ready);
        var corruptService = new HumanInputResponseContinuationService(
            scenario.Runs,
            corrupt,
            scenario.SleepStore,
            scenario.CurrentPosture,
            scenario.Contexts,
            scenario.Ordered,
            new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now));

        var corruptResult = await corruptService.VerifyAsync(verifiedRequest);
        var response = await scenario.Responses.ReadAsync(new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Request.RequestId,
            Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Request.RequestVersionId,
            Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Request.RequestHash));
        var selection = Assert.IsType<HumanInputResponseSelection>(response.Snapshot?.Selection);
        var chronologyResult = await scenario.Service.VerifyAsync(verifiedRequest with
        {
            CheckpointPublishedAtUtc = selection.SelectedAtUtc.AddTicks(1),
        });

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected, corruptResult?.Status);
        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected, chronologyResult?.Status);
        Assert.Equal(1, corrupt.ReadCount);
    }

    [Fact]
    public async Task Wake_stops_after_selection_attachment_when_the_second_canonical_run_read_is_not_found()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.Runs.ReturnNullOnGetCall = 3;

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Stale, result.Status);
        Assert.Equal(3, scenario.Runs.GetCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Wake_maps_generic_publication_and_wake_transport_failures_without_losing_the_exact_selection()
    {
        var publicationFailure = await HumanInputResponseContinuationScenario.CreateAsync();
        publicationFailure.SleepStore.ThrowBeforePublish = true;
        var wakeFailure = await HumanInputResponseContinuationScenario.CreateAsync();
        wakeFailure.SleepStore.ThrowBeforeCreate = true;

        var publication = await publicationFailure.Service.WakeAsync(publicationFailure.Candidate);
        var wake = await wakeFailure.Service.WakeAsync(wakeFailure.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, publication.Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, wake.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(publicationFailure.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(wakeFailure.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, publicationFailure.SleepStore.WakeCount);
        Assert.Equal(0, wakeFailure.SleepStore.WakeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wake_propagates_publication_cancellation_and_fails_closed_when_generic_wake_cancellation_is_observed(bool duringWake)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        if (duringWake)
        {
            scenario.SleepStore.OnCreate = (_, _) => cancellation.Cancel();
        }
        else
        {
            scenario.SleepStore.BeforePublish = _ => cancellation.Cancel();
        }

        if (duringWake)
        {
            var result = await scenario.Service.WakeAsync(scenario.Candidate, cancellation.Token);
            Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Service.WakeAsync(scenario.Candidate, cancellation.Token));
        }

        Assert.Equal(
            duringWake ? GovernedLoopHumanInputWaitingCheckpointPosture.Terminal : GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed,
            Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(duringWake ? 1 : 0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Wake_reuses_the_durable_selection_after_a_prepublication_interruption()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.SleepStore.ThrowBeforePublish = true;

        var interrupted = await scenario.Service.WakeAsync(scenario.Candidate);
        scenario.SleepStore.ThrowBeforePublish = false;
        var recovered = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, interrupted.Status);
        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, recovered.Status);
        Assert.Equal(2, scenario.Runs.UpdateCount);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, scenario.SleepStore.WakeCount);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Verify_rejects_invalid_requests_and_closes_null_or_throwing_checkpoint_reads()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        var request = new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            prepared.Checkpoint.ContentHash,
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc);

        var malformed = await scenario.Service.VerifyAsync(null!);
        scenario.SleepStore.ReturnNullCheckpointRead = true;
        var nullRead = await scenario.Service.VerifyAsync(request);
        scenario.SleepStore.ReturnNullCheckpointRead = false;
        scenario.SleepStore.CheckpointReadException = new IOException("simulated checkpoint resolution outage");
        var throwingRead = await scenario.Service.VerifyAsync(request);

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected, malformed?.Status);
        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected, nullRead?.Status);
        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable, throwingRead?.Status);
    }

    [Fact]
    public async Task Continue_closes_invalid_requests_and_unavailable_canonical_run_reads_before_terminalization()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var invalid = await scenario.Service.ContinueAsync(null!);
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.Runs.GetException = new IOException("simulated canonical run outage");

        var unavailable = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Conflict, invalid?.Status);
        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, unavailable?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Continue_leaves_terminal_evidence_retryable_when_the_second_ordered_context_resolution_fails()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.Contexts.ResolveExceptionOnCall = scenario.Contexts.ResolveCount + 2;

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(2, scenario.Contexts.ResolveCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_reports_an_ambiguous_terminal_reentry_when_ordered_execution_returns_no_result_or_throws(bool throwsFromOrderedRuntime)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        if (throwsFromOrderedRuntime)
        {
            scenario.Ordered.HumanInputResumeException = new IOException("simulated ordered reentry outage");
        }
        else
        {
            scenario.Ordered.ReturnNullHumanInputResume = true;
        }

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Continue_reports_not_committed_when_the_terminal_compare_exchange_reports_not_found()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.Runs.UpdateOverride = CustomLoopRunStoreResult.NotFound();
        scenario.Runs.UpdateOverrideCall = scenario.Runs.UpdateAttemptCount + 1;

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.NotCommitted, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Routed_no_response_reentry_stays_unavailable_when_ordered_context_or_execution_cannot_prove_an_advance(bool throwsFromOrderedRuntime)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire,
            includeFailureRoute: true);
        if (throwsFromOrderedRuntime)
        {
            scenario.Ordered.HumanInputFailureResumeException = new IOException("simulated ordered failure reentry outage");
        }
        else
        {
            scenario.Contexts.NullOnResolveCall = 2;
        }

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Expired, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(CustomLoopRunStatus.Running, scenario.Runs.Current.Status);
        Assert.Equal(throwsFromOrderedRuntime ? 1 : 0, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Fact]
    public async Task Wake_returns_stale_without_touching_response_or_sleep_state_when_the_exact_checkpoint_is_absent()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var absent = new HumanInputResponseContinuationCandidate(scenario.Candidate.RunId, "human-input-continuation-absent", scenario.Candidate.CheckpointHash);

        var result = await scenario.Service.WakeAsync(absent);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Stale, result.Status);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Wake_maps_response_store_transport_failures_before_generic_publication()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var store = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Ready)
        {
            ReadException = new IOException("simulated response store outage"),
        };
        var service = new HumanInputResponseContinuationService(
            scenario.Runs,
            store,
            scenario.SleepStore,
            scenario.CurrentPosture,
            scenario.Contexts,
            scenario.Ordered,
            new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now));
        service.BindSleep(new GovernedLoopSleepService(
            scenario.SleepStore,
            scenario.CurrentPosture,
            service,
            service,
            new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now)));

        var result = await service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Theory]
    [InlineData(false, HumanInputResponseContinuationWakeStatus.Unavailable)]
    [InlineData(true, HumanInputResponseContinuationWakeStatus.Invalid)]
    public async Task Wake_maps_selection_compare_exchange_transport_and_format_failures_to_closed_statuses(
        bool formatFailure,
        HumanInputResponseContinuationWakeStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        scenario.Runs.UpdateException = formatFailure
            ? new FormatException("simulated malformed compare exchange")
            : new IOException("simulated compare exchange outage");

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(expected, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Fact]
    public async Task Continue_reconciles_an_exact_concurrent_terminal_compare_exchange_before_ordered_reentry()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.Runs.CommitCandidateBeforeConflict = true;
        scenario.Runs.ConflictNextUpdate = true;

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Ambiguous, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Runs.Current).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Continue_fails_closed_when_current_posture_returns_null_or_throws(bool throwsDuringPostureRead)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        if (throwsDuringPostureRead)
        {
            scenario.CurrentPosture.Exception = new IOException("simulated posture read outage");
        }
        else
        {
            scenario.CurrentPosture.Result = null;
        }

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(GovernedLoopWakeContinuationStatus.Unavailable, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Verify_returns_not_found_for_a_checkpoint_hash_that_does_not_match_the_resolved_exact_checkpoint()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);

        var result = await scenario.Service.VerifyAsync(new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            GovernedLoopSleepApplicationTestFixture.Hash('d'),
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc));

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.NotFound, result?.Status);
    }

    [Fact]
    public async Task Verify_marks_an_answered_checkpoint_unavailable_when_its_response_store_cannot_return_ready_state()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        var unavailableResponses = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.NotFound);
        var service = new HumanInputResponseContinuationService(
            scenario.Runs,
            unavailableResponses,
            scenario.SleepStore,
            scenario.CurrentPosture,
            scenario.Contexts,
            scenario.Ordered,
            new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now));

        var result = await service.VerifyAsync(new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            prepared.Checkpoint.ContentHash,
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc));

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable, result?.Status);
        Assert.Equal(1, unavailableResponses.ReadCount);
    }

    [Fact]
    public async Task Routed_no_response_context_resolution_failure_preserves_the_pending_checkpoint_without_a_generic_wake()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire,
            includeFailureRoute: true);
        scenario.Contexts.ResolveException = new IOException("simulated ordered-context outage");

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Theory]
    [InlineData(false, GovernedLoopWakeContinuationStatus.Unavailable)]
    [InlineData(true, GovernedLoopWakeContinuationStatus.Conflict)]
    public async Task Continue_maps_terminal_compare_exchange_transport_and_format_failures_without_ordered_reentry(
        bool formatFailure,
        GovernedLoopWakeContinuationStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        scenario.Runs.UpdateException = formatFailure
            ? new FormatException("simulated malformed terminal compare exchange")
            : new IOException("simulated terminal compare exchange outage");

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(expected, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task No_response_retirement_fails_closed_when_the_exact_ordered_context_is_missing()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire);
        scenario.Contexts.Context = null;

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Invalid, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Theory]
    [InlineData(false, HumanInputResponseContinuationWakeStatus.Unavailable)]
    [InlineData(true, HumanInputResponseContinuationWakeStatus.Unavailable)]
    public async Task No_response_retirement_maps_transport_and_not_found_compare_exchange_results_without_generic_wake(
        bool notFound,
        HumanInputResponseContinuationWakeStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire);
        if (notFound)
        {
            scenario.Runs.UpdateOverride = CustomLoopRunStoreResult.NotFound();
        }
        else
        {
            scenario.Runs.UpdateException = new IOException("simulated retirement compare exchange outage");
        }

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(expected, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputFailureCount);
    }

    [Fact]
    public async Task No_response_retirement_reconciles_an_exact_concurrent_compare_exchange_once()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Reject);
        scenario.Runs.CommitCandidateBeforeConflict = true;
        scenario.Runs.ConflictNextUpdate = true;

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Retired, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Rejected, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(CustomLoopRunStatus.Failed, scenario.Runs.Current.Status);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.True(CustomLoopRunValidator.Validate(scenario.Runs.Current).IsValid);
    }

    [Theory]
    [InlineData(false, GovernedLoopWakeContinuationStatus.NotCommitted)]
    [InlineData(true, GovernedLoopWakeContinuationStatus.Conflict)]
    public async Task Continue_maps_not_found_and_malformed_canonical_run_reads_before_terminalization(
        bool malformedRead,
        GovernedLoopWakeContinuationStatus expected)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        if (malformedRead)
        {
            scenario.Runs.GetException = new FormatException("simulated malformed canonical run read");
        }
        else
        {
            scenario.Runs.ReturnNullOnGetCall = scenario.Runs.GetCount + 1;
        }

        var result = await scenario.Service.ContinueAsync(prepared.Request);

        Assert.Equal(expected, result?.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Theory]
    [InlineData(1, GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed)]
    [InlineData(2, GovernedLoopHumanInputWaitingCheckpointPosture.Terminal)]
    public async Task Continue_propagates_caller_cancellation_from_the_initial_or_terminal_ordered_context_resolution(
        int cancellationResolveCall,
        GovernedLoopHumanInputWaitingCheckpointPosture expectedPosture)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        using var cancellation = new CancellationTokenSource();
        scenario.Contexts.ResolveException = new OperationCanceledException("simulated caller cancellation", cancellation.Token);
        scenario.Contexts.ResolveExceptionOnCall = cancellationResolveCall;
        scenario.Contexts.BeforeResolve = count =>
        {
            if (count == cancellationResolveCall)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Service.ContinueAsync(prepared.Request, cancellation.Token));

        Assert.Equal(expectedPosture, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.Ordered.ResumeHumanInputCount);
    }

    [Fact]
    public async Task Continue_propagates_caller_cancellation_from_ordered_reentry_after_terminalization()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        using var cancellation = new CancellationTokenSource();
        scenario.Ordered.BeforeHumanInputResume = cancellation.Cancel;
        scenario.Ordered.HumanInputResumeException = new OperationCanceledException("simulated caller cancellation", cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Service.ContinueAsync(prepared.Request, cancellation.Token));

        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputCount);
    }

    [Theory]
    [InlineData(1, GovernedLoopHumanInputWaitingCheckpointPosture.Pending)]
    [InlineData(2, GovernedLoopHumanInputWaitingCheckpointPosture.Expired)]
    public async Task Routed_no_response_reentry_propagates_caller_cancellation_from_terminal_or_failure_reentry_context(
        int cancellationResolveCall,
        GovernedLoopHumanInputWaitingCheckpointPosture expectedPosture)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire,
            includeFailureRoute: true);
        using var cancellation = new CancellationTokenSource();
        scenario.Contexts.ResolveException = new OperationCanceledException("simulated caller cancellation", cancellation.Token);
        scenario.Contexts.ResolveExceptionOnCall = cancellationResolveCall;
        scenario.Contexts.BeforeResolve = count =>
        {
            if (count == cancellationResolveCall)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.Service.WakeAsync(scenario.Candidate, cancellation.Token));

        Assert.Equal(expectedPosture, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Fact]
    public async Task Routed_no_response_reentry_remains_unavailable_when_ordered_failure_reentry_returns_no_result()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            includeSelection: false,
            noResponseTerminalOperation: HumanInputRequestLifecycleOperationKind.Expire,
            includeFailureRoute: true);
        scenario.Ordered.ReturnNullHumanInputFailureResume = true;

        var result = await scenario.Service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Unavailable, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Expired, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(1, scenario.Ordered.ResumeHumanInputFailureCount);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    [Fact]
    public async Task Verify_rejects_an_invalid_event_reference_and_fails_closed_when_response_read_throws()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var prepared = await PrepareAnsweredContinuationAsync(scenario);
        var request = new GovernedLoopAuthenticatedWakeVerificationRequest(
            prepared.Checkpoint.CheckpointId,
            prepared.Checkpoint.ContentHash,
            prepared.Checkpoint.AuthenticatedEventReference!,
            prepared.Identity.AuthenticationEvidenceHash!,
            prepared.Checkpoint.PublishedAtUtc);
        var invalidReference = await scenario.Service.VerifyAsync(request with { AuthenticatedEventReference = "invalid-event-reference" });
        var unavailableResponses = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Ready)
        {
            ReadException = new IOException("simulated response read outage"),
        };
        var unavailableService = new HumanInputResponseContinuationService(
            scenario.Runs,
            unavailableResponses,
            scenario.SleepStore,
            scenario.CurrentPosture,
            scenario.Contexts,
            scenario.Ordered,
            new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now));

        var unavailable = await unavailableService.VerifyAsync(request);

        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Rejected, invalidReference?.Status);
        Assert.Equal(GovernedLoopAuthenticatedWakeVerificationStatus.Unavailable, unavailable?.Status);
        Assert.Equal(1, unavailableResponses.ReadCount);
    }

    [Fact]
    public async Task Wake_fails_closed_without_publication_when_its_authoritative_clock_is_unavailable()
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync();
        var time = new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now)
        {
            GetUtcNowException = new IOException("simulated authoritative clock outage"),
        };
        var service = new HumanInputResponseContinuationService(
            scenario.Runs,
            scenario.Responses,
            scenario.SleepStore,
            scenario.CurrentPosture,
            scenario.Contexts,
            scenario.Ordered,
            time);
        service.BindSleep(new GovernedLoopSleepService(
            scenario.SleepStore,
            scenario.CurrentPosture,
            service,
            service,
            time));

        var result = await service.WakeAsync(scenario.Candidate);

        Assert.Equal(HumanInputResponseContinuationWakeStatus.Invalid, result.Status);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints).Posture);
        Assert.Equal(0, scenario.SleepStore.WakeCount);
    }

    private static async Task<(GovernedLoopSleepCheckpoint Checkpoint, GovernedLoopWakeIdentity Identity, GovernedLoopWakeContinuationRequest Request)> PrepareAnsweredContinuationAsync(
        HumanInputResponseContinuationScenario scenario)
    {
        var current = scenario.Runs.Current;
        var checkpoint = Assert.Single(current.HumanInputWaitingCheckpoints);
        var response = await scenario.Responses.ReadAsync(new HumanInputRequestReference(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash));
        var selection = Assert.IsType<HumanInputResponseSelection>(response.Snapshot?.Selection);
        var answeredEvidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered,
            selection.SelectedAtUtc,
            HumanInputResponseSelectionReference.Create(selection),
            null,
            null,
            null,
            null,
            checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var answered = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            checkpoint.SchemaVersion,
            checkpoint.Binding,
            checkpoint.NodeConfiguration,
            checkpoint.ResolvedPolicy,
            checkpoint.Request,
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed,
            [.. checkpoint.Evidence, answeredEvidence],
            string.Empty));
        var answeredRun = current with
        {
            LifecycleVersion = checked(current.LifecycleVersion + 1),
            UpdatedAtUtc = selection.SelectedAtUtc,
            HumanInputWaitingCheckpoints = [answered],
        };
        Assert.True(CustomLoopRunValidator.ValidateUpdate(current, answeredRun).IsValid);
        await scenario.Runs.UpdateAsync(answeredRun, current.LifecycleVersion);

        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(answeredRun.Frontier?.Payload.Nodes.ElementAtOrDefault(answered.Binding.ActivationOrdinal));
        var binding = Assert.IsType<GovernedLoopSequentialAdapterBinding>(answeredRun.SequentialAdapterBinding);
        var sleepCheckpoint = GovernedLoopSleepContractHash.Apply(new GovernedLoopSleepCheckpoint(
            GovernedLoopSleepCheckpoint.CurrentSchemaVersion,
            string.Empty,
            new GovernedLoopSleepBinding(
                binding.ExecutionBinding,
                binding.AdmissionReceipt.Intent.Publication,
                answered.Binding.FrontierVersion,
                answered.Binding.FrontierHash,
                answered.Binding.ActivationOrdinal,
                answered.Binding.CycleId,
                answered.Binding.CycleIteration,
                answered.Binding.NodeId,
                answered.Binding.NodeVisitOrdinal,
                activation.Attempt!.Value,
                activation.AttemptOperationId!),
            GovernedLoopWakeMode.AuthenticatedEvent,
            null,
            GovernedLoopHumanInputContinuationVocabulary.AuthenticatedEventReferencePrefix + answered.Binding.CheckpointId,
            answered.Request.Timing.RequestedAtUtc,
            string.Empty));
        var published = await scenario.SleepStore.PublishAndReleaseAsync(sleepCheckpoint, GovernedLoopSleepApplicationTestFixture.Hash('f'));
        Assert.Equal(GovernedLoopSleepCheckpointMutationStatus.Committed, published?.Status);

        var identity = GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            GovernedLoopWakeIdentity.CurrentSchemaVersion,
            string.Empty,
            sleepCheckpoint.CheckpointId,
            sleepCheckpoint.ContentHash,
            sleepCheckpoint.WakeMode,
            sleepCheckpoint.AuthenticatedEventReference,
            HumanInputResponseSelectionReference.Create(selection).SelectionHash,
            string.Empty));
        var operationId = GovernedLoopSleepApplicationTestFixture.Hash('c');
        var prepared = CreatePreparedEvidence(identity, operationId);
        return (sleepCheckpoint, identity, new GovernedLoopWakeContinuationRequest(
            sleepCheckpoint,
            identity,
            operationId,
            prepared,
            GovernedLoopSleepApplicationTestFixture.Hash('9')));
    }

    private static GovernedLoopWakeIdentity CreateWakeIdentity(GovernedLoopSleepCheckpoint checkpoint, string authenticationEvidenceHash)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeIdentity(
            GovernedLoopWakeIdentity.CurrentSchemaVersion,
            string.Empty,
            checkpoint.CheckpointId,
            checkpoint.ContentHash,
            checkpoint.WakeMode,
            checkpoint.AuthenticatedEventReference,
            authenticationEvidenceHash,
            string.Empty));

    private static GovernedLoopWakeEvidence CreatePreparedEvidence(GovernedLoopWakeIdentity identity, string operationId)
        => GovernedLoopSleepContractHash.Apply(new GovernedLoopWakeEvidence(
            GovernedLoopWakeEvidence.CurrentSchemaVersion,
            1,
            identity,
            GovernedLoopWakeDisposition.Prepared,
            operationId,
            null,
            null,
            GovernedLoopSleepApplicationTestFixture.Now,
            string.Empty));
}
